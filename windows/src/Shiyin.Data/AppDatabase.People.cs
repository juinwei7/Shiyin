using System.Globalization;
using Shiyin.Model;

namespace Shiyin.Data;

/// <summary>側欄「人物」的一列（M§60.1、R85.1）：一起開過幾場（不在垃圾桶的）、最近一場的日期、是不是我（錄過「我的聲音」，R80）</summary>
public sealed record PersonOverview(long Id, string Name, int MeetingCount, string? LastMeetingDate, bool IsMe);

/// <summary>人物頁的一場會議：他說了多久（秒）</summary>
public sealed record PersonMeeting(Meeting Meeting, double Seconds)
{
    public long Id => Meeting.Id ?? 0;
}

/// <summary>人物頁的一條待辦：負責人是他（R85.3）；Index 是在那場會議紀錄的待辦裡的位置</summary>
public sealed record PersonActionItem(Meeting Meeting, int Index, MeetingNotes.ActionItem Item);

/// <summary>人物頁的三個查詢（同 Mac 的 Store/People.swift，M§60.1；標準答案 db/43～48）</summary>
public sealed partial class AppDatabase
{
    /// <summary>認識的人：我排第一，其他照最近一場由新到舊，再照名字</summary>
    public List<PersonOverview> PeopleOverview() => Read(db => db.Query("""
        SELECT p.id, p.name,
          (SELECT count(DISTINCT s.meeting_id) FROM speakers s JOIN meetings m ON m.id = s.meeting_id
             WHERE s.person_id = p.id AND m.deleted_at IS NULL) AS meetings,
          (SELECT max(m.meeting_date) FROM speakers s JOIN meetings m ON m.id = s.meeting_id
             WHERE s.person_id = p.id AND m.deleted_at IS NULL) AS last_date,
          EXISTS (SELECT 1 FROM voiceprints v WHERE v.person_id = p.id AND v.audio_source = 'enroll') AS me
        FROM people p
        """).Select(r => new PersonOverview(r.Long("id"), r.Str("name"), r.Int("meetings"), r.StrOrNull("last_date"), r.Bool("me"))).ToList())
        .OrderBy(p => p.IsMe ? 0 : 1)
        .ThenByDescending(p => p.LastMeetingDate ?? "", StringComparer.Ordinal)
        .ThenBy(p => p.Name, NameComparer)
        .ToList();

    /// <summary>
    /// 同一天的人照名字排（Mac 的 localizedStandardCompare：不分大小寫、數字照大小）。依介面語言的排序規則，
    /// 標準答案避開同一天的情況（兩邊的語系不一定一樣）
    /// </summary>
    static readonly IComparer<string> NameComparer = Comparer<string>.Create((a, b) =>
        string.Compare(a, b, CultureInfo.CurrentCulture, CompareOptions.IgnoreCase | CompareOptions.IgnoreWidth | CompareOptions.NumericOrdering));

    /// <summary>他出現過的會議（不在垃圾桶），說了多久是那場他的說話者的正式逐字稿加總；照日期由新到舊</summary>
    public List<PersonMeeting> PersonMeetings(long personId) => Read(db =>
    {
        var rows = db.Query("""
            SELECT s.meeting_id,
              (SELECT COALESCE(SUM(g."end" - g.start), 0) FROM segments g
                 WHERE g.meeting_id = s.meeting_id AND g.kind = 'final' AND g.speaker = s.idx) AS seconds
            FROM speakers s JOIN meetings m ON m.id = s.meeting_id
            WHERE s.person_id = ? AND m.deleted_at IS NULL
            """, personId);
        var seconds = new Dictionary<long, double>();
        foreach (var row in rows) seconds[row.Long("meeting_id")] = seconds.GetValueOrDefault(row.Long("meeting_id")) + row.Double("seconds");
        return seconds.Select(p => FetchMeeting(db, p.Key) is { } m ? new PersonMeeting(m, p.Value) : null).OfType<PersonMeeting>()
            .OrderByDescending(p => p.Meeting.MeetingDate, StringComparer.Ordinal).ThenByDescending(p => p.Id).ToList();
    });

    /// <summary>
    /// 負責人跟名字一樣或包含名字（不分大小寫，R85.3；同本地 MCP 的 list_action_items）的待辦，所有不在垃圾桶的會議紀錄；
    /// 照會議日期由新到舊、會議裡的順序
    /// </summary>
    public List<PersonActionItem> PersonActionItems(string name)
    {
        var target = name.Trim();
        if (target.Length == 0) return [];
        var meetings = Read(db => db.Query("SELECT * FROM meetings WHERE deleted_at IS NULL AND notes IS NOT NULL AND notes <> '' ORDER BY meeting_date DESC, id DESC")
            .Select(ToMeeting).ToList());
        return meetings.SelectMany(m => m.DecodedNotes is { } notes
            ? notes.ActionItems.Select((item, index) => (item, index))
                .Where(p => p.item.Owner.Contains(target, StringComparison.OrdinalIgnoreCase))
                .Select(p => new PersonActionItem(m, p.index, p.item))
            : []).ToList();
    }
}
