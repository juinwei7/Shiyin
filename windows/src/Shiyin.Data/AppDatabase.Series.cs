using System.Text.Json;
using Shiyin.Model;
using Shiyin.Text;

namespace Shiyin.Data;

/// <summary>資料夾自動歸類、定期會議（R98、R99、M§72、§73；同 Mac 的 Store/Series.swift 的資料庫部分）</summary>
public sealed partial class AppDatabase
{
    // MARK: JSON 陣列（同 GRDB 的 JSONEncoder：斜線跳脫）

    static string JsonStrings(IEnumerable<string> list) => SwiftJsonWriter.Write(JNode.Of(list), sortKeys: true, escapeSlashes: true);

    static string JsonIds(IEnumerable<long> list) => SwiftJsonWriter.Write(new JNode.Arr(list.Select(i => (JNode)new JNode.Int(i))), sortKeys: true, escapeSlashes: true);

    static List<string> ParseStrings(string json) =>
        JsonRead.Parse(json) is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : [];

    static List<long> ParseIds(string json) =>
        JsonRead.Parse(json) is { ValueKind: JsonValueKind.Array } a
            ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number && x.TryGetInt64(out _)).Select(x => x.GetInt64()).ToList() : [];

    static FolderRule ToFolderRule(Row r) => new()
    {
        Folder = r.Str("folder"), Keywords = ParseStrings(r.Str("keywords")), People = ParseIds(r.Str("people")),
        TemplateId = r.StrOrNull("template_id"), SeriesId = r.LongOrNull("series_id"), CreatedAt = r.Str("created_at"),
    };

    static MeetingSeries ToSeries(Row r) => new()
    {
        Id = r.Long("id"), Name = r.Str("name"), Weekday = r.Int("weekday"), Minute = r.IntOrNull("minute"), IntervalWeeks = r.Int("interval_weeks"),
        People = ParseIds(r.Str("people")), CreatedAt = r.Str("created_at"),
    };

    // MARK: 資料夾的條件

    public List<FolderRule> FolderRules() => Read(db => db.Query("SELECT * FROM folder_rules ORDER BY folder").Select(ToFolderRule).ToList());

    /// <summary>儲存條件；條件都空時當成移除</summary>
    public void SaveFolderRule(FolderRule rule) => Write(db =>
    {
        if (rule.IsEmpty)
        {
            db.Execute("DELETE FROM folder_rules WHERE folder = ?", rule.Folder);
            return;
        }
        db.Execute("INSERT INTO folder_rules (folder, keywords, people, template_id, series_id, created_at) VALUES (?, ?, ?, ?, ?, ?) "
            + "ON CONFLICT(folder) DO UPDATE SET keywords = excluded.keywords, people = excluded.people, template_id = excluded.template_id, "
            + "series_id = excluded.series_id, created_at = excluded.created_at",
            rule.Folder, JsonStrings(rule.Keywords), JsonIds(rule.People), rule.TemplateId, rule.SeriesId, rule.CreatedAt);
    });

    /// <summary>「現在不在資料夾裡的會議也套用」：符合條件的會議（不在資料夾、不在垃圾桶）</summary>
    public List<long> MeetingsMatching(FolderRule rule)
    {
        var people = MeetingPeople();
        var meetings = Read(db => db.Query("SELECT * FROM meetings WHERE deleted_at IS NULL AND folder = '' ORDER BY rowid").Select(ToMeeting).ToList());
        return meetings.Where(m => rule.Matches(m.DisplayTitle, people.GetValueOrDefault(m.Id ?? 0) ?? [], m.TemplateId, m.SeriesId))
            .Select(m => m.Id ?? 0).ToList();
    }

    /// <summary>建議按了「不用」：之後不再建議這場</summary>
    public void SetFolderDecided(long id) => Write(db => db.Execute("UPDATE meetings SET folder_decided = 1 WHERE id = ?", id));

    /// <summary>
    /// 自動放進資料夾（M§72）：不在資料夾、還沒決定過、不在垃圾桶；符合好幾個時放名稱排第一的（同 Mac 的 localizedStandardCompare）。
    /// 回傳資料夾名稱
    /// </summary>
    public string? AutoFile(long meetingId)
    {
        if (Meeting(meetingId) is not { } meeting || meeting.Folder.Length > 0 || meeting.FolderDecided || meeting.DeletedAt is not null) return null;
        var attended = MeetingPeople().GetValueOrDefault(meetingId) ?? [];
        var rule = FolderRules().OrderBy(r => r.Folder, NaturalComparer.Instance)
            .FirstOrDefault(r => r.Matches(meeting.DisplayTitle, attended, meeting.TemplateId, meeting.SeriesId));
        if (rule is null) return null;
        Write(db => db.Execute("UPDATE meetings SET folder = ?, folder_decided = 1, updated_at = ? WHERE id = ?", rule.Folder, Clock.Now(), meetingId));
        return rule.Folder;
    }

    // MARK: 定期會議

    public List<MeetingSeries> Series() => Read(db => db.Query("SELECT * FROM series ORDER BY name").Select(ToSeries).ToList());

    public HashSet<string> RejectedSeriesKeys() => Read(db => db.Query("SELECT key FROM series_rejected").Select(r => r.Str("key")).ToHashSet());

    public void RejectSeries(string key) =>
        Write(db => db.Execute("INSERT OR IGNORE INTO series_rejected (key, created_at) VALUES (?, ?)", key, Clock.Now()));

    /// <summary>建立定期會議，勾的會議 series_id 設成它；回傳有 id 的</summary>
    public MeetingSeries CreateSeries(MeetingSeries series, IReadOnlyList<long> meetingIds) => Write(db =>
    {
        db.Execute("INSERT INTO series (name, weekday, minute, interval_weeks, people, created_at) VALUES (?, ?, ?, ?, ?, ?)",
            series.Name, series.Weekday, series.Minute, series.IntervalWeeks, JsonIds(series.People), series.CreatedAt);
        var id = db.LastInsertRowId;
        foreach (var m in meetingIds) db.Execute("UPDATE meetings SET series_id = ?, updated_at = ? WHERE id = ?", id, Clock.Now(), m);
        return new MeetingSeries
        {
            Id = id, Name = series.Name, Weekday = series.Weekday, Minute = series.Minute, IntervalWeeks = series.IntervalWeeks,
            People = [.. series.People], CreatedAt = series.CreatedAt,
        };
    });

    public void RenameSeries(long id, string name) => Write(db => db.Execute("UPDATE series SET name = ? WHERE id = ?", name, id));

    /// <summary>會議留著（series_id 變 null）；記進「不是定期會議」，之後不再建議同一組</summary>
    public void DeleteSeries(long id) => Write(db =>
    {
        if (db.QueryOne("SELECT * FROM series WHERE id = ?", id) is { } row)
            db.Execute("INSERT OR IGNORE INTO series_rejected (key, created_at) VALUES (?, ?)", ToSeries(row).Key, Clock.Now());
        db.Execute("UPDATE meetings SET series_id = NULL WHERE series_id = ?", id);
        db.Execute("DELETE FROM series WHERE id = ?", id);
    });

    /// <summary>加到定期會議、移出（series 是 null）</summary>
    public void SetSeries(IReadOnlyList<long> ids, long? series) => Write(db =>
    {
        foreach (var id in ids) db.Execute("UPDATE meetings SET series_id = ?, updated_at = ? WHERE id = ?", series, Clock.Now(), id);
    });

    /// <summary>系列裡的會議（不在垃圾桶），日期由新到舊</summary>
    public List<Meeting> SeriesMeetings(long seriesId) => Read(db =>
        db.Query("SELECT * FROM meetings WHERE series_id = ? AND deleted_at IS NULL ORDER BY meeting_date DESC, id DESC", seriesId).Select(ToMeeting).ToList());

    /// <summary>上一場（R99.5）：同系列、日期比這場早（同一天時 id 比較小）、有會議紀錄的最近一場</summary>
    public Meeting? PreviousSeriesMeeting(Meeting meeting)
    {
        if (meeting.SeriesId is not long series || meeting.Id is not long id) return null;
        return Read(db => db.QueryOne("""
            SELECT * FROM meetings WHERE series_id = ? AND deleted_at IS NULL AND notes IS NOT NULL AND notes != ''
              AND (meeting_date < ? OR (meeting_date = ? AND id < ?))
            ORDER BY meeting_date DESC, id DESC LIMIT 1
            """, series, meeting.MeetingDate, meeting.MeetingDate, id) is { } r ? ToMeeting(r) : null);
    }

    /// <summary>
    /// 轉錄完自動加入定期會議（M§73）：還不在定期會議、星期幾相同、時間接近、同一群人、跟系列最近一場相隔 1～35 天；
    /// 好幾個符合時取最近一場最近的。回傳加入的系列。timeZone 是 null 時用這台電腦的時區
    /// </summary>
    public MeetingSeries? AutoJoinSeries(long meetingId, TimeZoneInfo? timeZone = null)
    {
        if (Meeting(meetingId) is not { } meeting || meeting.SeriesId is not null || meeting.DeletedAt is not null
            || MeetingTime.Of(meeting, timeZone) is not { } time) return null;
        var attended = MeetingPeople().GetValueOrDefault(meetingId) ?? [];
        (MeetingSeries Series, int Gap)? best = null;
        foreach (var series in Series())
        {
            if (series.Weekday != time.Weekday || !SeriesDetector.CloseInTime(series.Minute, time.Minute)
                || !SeriesDetector.SamePeople(attended, series.People.ToHashSet())) continue;
            if (series.Id is not long sid || SeriesMeetings(sid).FirstOrDefault(m => m.Id != meetingId) is not { } last
                || MeetingTime.Of(last, timeZone) is not { } lastTime) continue;
            var gap = Math.Abs(time.Day - lastTime.Day);
            if (gap is >= 1 and <= 35 && (best is null || gap < best.Value.Gap)) best = (series, gap);
        }
        if (best is not { } chosen) return null;
        SetSeries([meetingId], chosen.Series.Id);
        return chosen.Series;
    }

    // MARK: 與會的人物

    /// <summary>每場會議連到哪些人物（說話者對到的人；列表的「有誰參加」篩選、資料夾的條件、定期會議，M§71）</summary>
    public Dictionary<long, HashSet<long>> MeetingPeople() => Read(db =>
    {
        var result = new Dictionary<long, HashSet<long>>();
        foreach (var row in db.Query("SELECT DISTINCT meeting_id, person_id FROM speakers WHERE person_id IS NOT NULL"))
        {
            var meeting = row.Long("meeting_id");
            if (!result.TryGetValue(meeting, out var set)) result[meeting] = set = [];
            set.Add(row.Long("person_id"));
        }
        return result;
    });
}

/// <summary>
/// 定期會議裡還沒完成的待辦（R99.4、M§73）：由舊到新，先用這一場的 followUps 拿掉完成的，再加入這一場的待辦（同一件事以新的為準）；
/// 最後新的會議在前、同一場照紀錄裡的順序
/// </summary>
public static class SeriesOpenItems
{
    public static List<PersonActionItem> Compute(IEnumerable<Meeting> meetings)
    {
        var open = new List<PersonActionItem>();
        foreach (var meeting in meetings.OrderBy(m => m.MeetingDate, SwiftText.Comparer).ThenBy(m => m.Id ?? 0))
        {
            if (meeting.DecodedNotes is not { } notes) continue;
            foreach (var f in notes.FollowUps.Where(f => f.Status == FollowUpStatus.Done))
                open.RemoveAll(o => SeriesTasks.Same(o.Item.Task, f.Task));
            for (var i = 0; i < notes.ActionItems.Count; i++)
            {
                var item = notes.ActionItems[i];
                if (item.Task.Length == 0) continue;
                open.RemoveAll(o => SeriesTasks.Same(o.Item.Task, item.Task));
                open.Add(new PersonActionItem(meeting, i, item));
            }
        }
        return open.OrderByDescending(o => o.Meeting.MeetingDate, SwiftText.Comparer).ThenByDescending(o => o.Meeting.Id ?? 0).ThenBy(o => o.Index).ToList();
    }
}
