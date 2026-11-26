using System.Text.Json;
using Shiyin.Model;
using Shiyin.Text;

namespace Shiyin.Data;

/// <summary>資料夾自動歸類（R98、M§72；同 Mac 的 Store/FolderRules.swift 的資料庫部分）。定期會議（M§73）D177 拿掉，表、欄位留著不用</summary>
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
        FolderId = r.Long("folder_id"), Keywords = ParseStrings(r.Str("keywords")), People = ParseIds(r.Str("people")),
        TemplateId = r.StrOrNull("template_id"), CreatedAt = r.Str("created_at"),
    };

    // MARK: 資料夾的條件

    public List<FolderRule> FolderRules() => Read(db => db.Query("SELECT * FROM folder_rules ORDER BY folder_id").Select(ToFolderRule).ToList());

    /// <summary>儲存條件；條件都空時當成移除。series_id 欄位留著不用（D177），一律 null</summary>
    public void SaveFolderRule(FolderRule rule) => Write(db =>
    {
        if (rule.IsEmpty)
        {
            db.Execute("DELETE FROM folder_rules WHERE folder_id = ?", rule.FolderId);
            return;
        }
        db.Execute("INSERT INTO folder_rules (folder_id, keywords, people, template_id, series_id, created_at) VALUES (?, ?, ?, ?, NULL, ?) "
            + "ON CONFLICT(folder_id) DO UPDATE SET keywords = excluded.keywords, people = excluded.people, template_id = excluded.template_id, "
            + "series_id = NULL, created_at = excluded.created_at",
            rule.FolderId, JsonStrings(rule.Keywords), JsonIds(rule.People), rule.TemplateId, rule.CreatedAt);
    });

    /// <summary>「現在不在資料夾裡的會議也套用」：符合條件的會議（不在資料夾、不在垃圾桶）</summary>
    public List<long> MeetingsMatching(FolderRule rule)
    {
        var people = MeetingPeople();
        var meetings = Read(db => db.Query("SELECT * FROM meetings WHERE deleted_at IS NULL AND folder_id IS NULL ORDER BY rowid").Select(ToMeeting).ToList());
        return meetings.Where(m => rule.Matches(m.DisplayTitle, people.GetValueOrDefault(m.Id ?? 0) ?? [], m.TemplateId))
            .Select(m => m.Id ?? 0).ToList();
    }

    /// <summary>建議按了「不用」：之後不再建議這場</summary>
    public void SetFolderDecided(long id) => Write(db => db.Execute("UPDATE meetings SET folder_decided = 1 WHERE id = ?", id));

    /// <summary>
    /// 自動放進資料夾（M§72）：不在資料夾、還沒決定過、不在垃圾桶；符合好幾個時放名稱排第一的（同 Mac 的 localizedStandardCompare）。
    /// 回傳資料夾 id（R118、M§92.2）
    /// </summary>
    public long? AutoFile(long meetingId)
    {
        if (Meeting(meetingId) is not { } meeting || meeting.FolderId is not null || meeting.FolderDecided || meeting.DeletedAt is not null) return null;
        var attended = MeetingPeople().GetValueOrDefault(meetingId) ?? [];
        var names = Folders().ToDictionary(f => f.Id, f => f.Name);
        var rule = FolderRules().OrderBy(r => names.GetValueOrDefault(r.FolderId) ?? "", NaturalComparer.Instance)
            .FirstOrDefault(r => r.Matches(meeting.DisplayTitle, attended, meeting.TemplateId));
        if (rule is null) return null;
        Write(db => db.Execute("UPDATE meetings SET folder_id = ?, folder_decided = 1, updated_at = ? WHERE id = ?", rule.FolderId, Clock.Now(), meetingId));
        return rule.FolderId;
    }

    // MARK: 與會的人物

    /// <summary>每場會議連到哪些人物（說話者對到的人；列表的「有誰參加」篩選、資料夾的條件和建議，M§71、§72）</summary>
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
