using Shiyin.Data;
using Shiyin.Localization;
using Shiyin.Model;
using Shiyin.Notes;
using Shiyin.Text;

namespace Shiyin.Services;

/// <summary>會議列表的排序（R97.2、M§71）：記住（settings.json 的 meetingSort）</summary>
public enum MeetingSort { DateDesc, DateAsc, Title, Duration }

/// <summary>會議列表的分組（R97.2）：記住（以前存的 series 讀成不分組；定期會議 D177 拿掉）</summary>
public enum MeetingGrouping { None, Month, Folder }

public static class MeetingSorts
{
    public static readonly MeetingSort[] All = [MeetingSort.DateDesc, MeetingSort.DateAsc, MeetingSort.Title, MeetingSort.Duration];

    public static string Code(this MeetingSort s) => s switch
    {
        MeetingSort.DateAsc => "dateAsc",
        MeetingSort.Title => "title",
        MeetingSort.Duration => "duration",
        _ => "dateDesc",
    };

    public static MeetingSort? Parse(string? code) => All.Select(s => (MeetingSort?)s).FirstOrDefault(s => s!.Value.Code() == code);

    public static string Label(this MeetingSort s) => s switch
    {
        MeetingSort.DateAsc => L.T("日期（舊到新）"),
        MeetingSort.Title => L.T("標題"),
        MeetingSort.Duration => L.T("長度（長的在前）"),
        _ => L.T("日期（新到舊）"),
    };
}

public static class MeetingGroupings
{
    public static readonly MeetingGrouping[] All = [MeetingGrouping.None, MeetingGrouping.Month, MeetingGrouping.Folder];

    public static string Code(this MeetingGrouping g) => g switch
    {
        MeetingGrouping.Month => "month",
        MeetingGrouping.Folder => "folder",
        _ => "none",
    };

    public static MeetingGrouping? Parse(string? code) => All.Select(g => (MeetingGrouping?)g).FirstOrDefault(g => g!.Value.Code() == code);

    public static string Label(this MeetingGrouping g) => g switch
    {
        MeetingGrouping.Month => L.T("依月份"),
        MeetingGrouping.Folder => L.T("依資料夾"),
        _ => L.T("不分組"),
    };
}

/// <summary>會議列表的篩選（R97.3）：不記住，都要符合</summary>
public sealed record MeetingFilter(bool NeedsReview = false, bool MyActionItems = false, long? PersonId = null, string? TemplateId = null)
{
    public bool IsActive => NeedsReview || MyActionItems || PersonId is not null || TemplateId is not null;
}

/// <summary>分組後的一段：標題空的是不分組</summary>
public sealed record MeetingGroup(string Id, string Title, List<MeetingRow> Rows);

/// <summary>會議列表的排序、分組、篩選（R97、M§71，同 Mac 的 Store/MeetingListOptions.swift）</summary>
public static class MeetingList
{
    /// <summary>實際用的分組：看某個資料夾時不依資料夾分組（M§71）</summary>
    public static MeetingGrouping Effective(MeetingGrouping grouping, long? folderFilter) => grouping switch
    {
        MeetingGrouping.Folder when folderFilter is not null => MeetingGrouping.None,
        _ => grouping,
    };

    /// <summary>選單裡可以選的分組</summary>
    public static List<MeetingGrouping> Available(long? folderFilter) =>
        MeetingGroupings.All.Where(g => g != MeetingGrouping.Folder || folderFilter is null).ToList();

    /// <summary>「我」的名字（R80）：錄過自己聲音的人物；沒有設定「我」是 null</summary>
    public static string? MyName(IEnumerable<PersonOverview> people) => people.FirstOrDefault(p => p.IsMe)?.Name;

    public static bool Matches(MeetingRow row, MeetingFilter filter, IReadOnlyDictionary<long, HashSet<long>> meetingPeople, string? myName)
    {
        if (filter.NeedsReview && row.ReviewCount == 0) return false;
        if (filter.TemplateId is { } template && row.Meeting.TemplateId != template) return false;
        if (filter.PersonId is long person && !(meetingPeople.GetValueOrDefault(row.Id)?.Contains(person) ?? false)) return false;
        if (filter.MyActionItems)
        {
            // 負責人跟名字一樣或包含名字（不分大小寫，同人物頁 R85.3）
            var name = myName?.Trim() ?? "";
            if (name.Length == 0 || row.Meeting.DecodedNotes?.ActionItems.Any(a => a.Owner.Contains(name, StringComparison.OrdinalIgnoreCase)) != true)
                return false;
        }
        return true;
    }

    /// <summary>
    /// 排序：資料庫照 meeting_date DESC, id DESC 讀出來；舊到新是反過來；標題照 Finder 的排法（同 Mac 的 localizedStandardCompare）；
    /// 長度沒有的排最後（一樣長時照原本的順序）
    /// </summary>
    public static List<MeetingRow> Sorted(IEnumerable<MeetingRow> rows, MeetingSort sort) => sort switch
    {
        MeetingSort.DateAsc => rows.Reverse().ToList(),
        MeetingSort.Title => rows.OrderBy(r => r.Meeting.DisplayTitle, NaturalComparer.Instance).ToList(),
        MeetingSort.Duration => rows.OrderByDescending(r => r.Meeting.DurationSeconds ?? -1).ToList(),
        _ => rows.ToList(),
    };

    /// <summary>
    /// 分組（R97.2）：月份照日期新到舊（舊到新排序時反過來）；資料夾照名稱（folderName：id → 名字），最後是「不在資料夾」；每一段裡照排序。
    /// 資料夾那一組的 id 是 folder-&lt;資料夾 id&gt;（不在資料夾是 folder-none），改名時收合的狀態不會跑掉（R118，同 Mac）。
    /// rows 是已經篩過、排好的列表；垃圾桶不分組
    /// </summary>
    public static List<MeetingGroup> Groups(List<MeetingRow> rows, MeetingGrouping effective, MeetingSort sort, bool trash, Func<long?, string>? folderName = null)
    {
        if (trash) return [new MeetingGroup("all", "", rows)];
        switch (effective)
        {
            case MeetingGrouping.Month:
                var months = rows.Select(r => Month(r.Meeting.MeetingDate)).Distinct().ToList();
                months.Sort(StringComparer.Ordinal);
                if (sort != MeetingSort.DateAsc) months.Reverse();
                return months.Select(key => new MeetingGroup("month-" + key, MonthTitle(key), rows.Where(r => Month(r.Meeting.MeetingDate) == key).ToList())).ToList();
            case MeetingGrouping.Folder:
                var name = folderName ?? (_ => "");
                var folders = rows.Select(r => r.Meeting.FolderId).OfType<long>().Distinct().OrderBy(id => name(id), NaturalComparer.Instance).Select(id => (long?)id).ToList();
                if (rows.Any(r => r.Meeting.FolderId is null)) folders.Add(null);
                return folders.Select(folder => new MeetingGroup(folder is long id ? $"folder-{id}" : "folder-none", folder is null ? L.T("不在資料夾") : name(folder),
                    rows.Where(r => r.Meeting.FolderId == folder).ToList())).ToList();
            default:
                return [new MeetingGroup("all", "", rows)];
        }
    }

    static string Month(string date) => date.Length >= 7 ? date[..7] : date;

    static readonly string[] EnglishMonths = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    /// <summary>「2026-11」→「2026年11月」「November 2026」（介面語言，同 Mac 的 DateFormatter yMMMM）</summary>
    public static string MonthTitle(string key)
    {
        var parts = key.Split('-');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var year) || !int.TryParse(parts[1], out var month) || month is < 1 or > 12) return key;
        return MeetingLanguages.Ui($"{year}年{month}月", $"{EnglishMonths[month - 1]} {year}");  // l10n-skip
    }

    /// <summary>篩選的說明（列表上方那一列）：「有待確認、王小明 有參加、範本：一對一」</summary>
    public static string FilterSummary(MeetingFilter filter, IEnumerable<PersonOverview> people, IEnumerable<TemplateRecord> templates)
    {
        var parts = new List<string>();
        if (filter.NeedsReview) parts.Add(L.T("有待確認"));
        if (filter.MyActionItems) parts.Add(L.T("有我的待辦"));
        if (filter.PersonId is long id && people.FirstOrDefault(p => p.Id == id) is { } person) parts.Add(L.F("%@ 有參加", person.Name));
        if (filter.TemplateId is { } t && templates.FirstOrDefault(x => x.Id == t) is { } template) parts.Add(L.F("範本：%@", template.DisplayName()));
        return string.Join(MeetingLanguages.Ui("、", ", "), parts);
    }

    /// <summary>
    /// 列表上的會議（R97、M§71）：資料夾、篩選（垃圾桶不篩選）、排序
    /// </summary>
    public static List<MeetingRow> Filtered(IEnumerable<MeetingRow> source, bool trash, long? folderFilter, MeetingFilter filter,
        MeetingSort sort, IReadOnlyDictionary<long, HashSet<long>> meetingPeople, string? myName)
    {
        var rows = source;
        if (!trash && folderFilter is long folder) rows = rows.Where(r => r.Meeting.FolderId == folder);
        if (!trash && filter.IsActive) rows = rows.Where(r => Matches(r, filter, meetingPeople, myName));
        return Sorted(rows, sort);
    }
}

/// <summary>資料夾自動歸類的畫面用的計算（R98、M§72，同 Mac 的 Store/OrganizeActions.swift）</summary>
public static class Organizing
{
    /// <summary>
    /// 現有的資料夾和各自的場數（不含垃圾桶），依名稱排序：資料夾表裡的每一個（自己建的空資料夾、會議都移走了的、只有自動加入條件的都在，
    /// R104、M§78、§92.3；v18 起每個資料夾一定在表裡，不用再跟會議、條件取聯集），沒有會議的是 0 場
    /// </summary>
    public static List<(long Id, string Name, int Count)> Folders(IEnumerable<MeetingRow> meetings, IEnumerable<Folder> folders)
    {
        var counts = meetings.Select(m => m.Meeting.FolderId).OfType<long>().GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
        return folders.Select(f => (f.Id, f.Name, counts.GetValueOrDefault(f.Id))).OrderBy(f => f.Name, NaturalComparer.Instance).ToList();
    }

    /// <summary>
    /// 建議放進資料夾（R98.3）：會議不在資料夾、還沒決定過、已經有會議紀錄、不在錄音中、不在垃圾桶時，跟各個資料夾比（folderName：id → 名字）
    /// </summary>
    public static FolderSuggestion? Suggestion(Meeting meeting, IEnumerable<MeetingRow> meetings, IReadOnlyDictionary<long, HashSet<long>> meetingPeople,
        long? recordingId, Func<long?, string> folderName)
    {
        if (meeting.Id is not long id || meeting.FolderId is not null || meeting.FolderDecided || meeting.DeletedAt is not null
            || meeting.Notes is null || recordingId == id) return null;
        var input = new FolderSuggester.Input(id, null, "", meeting.DisplayTitle, meetingPeople.GetValueOrDefault(id) ?? []);
        var others = meetings.Select(r => new FolderSuggester.Input(r.Id, r.Meeting.FolderId, folderName(r.Meeting.FolderId), r.Meeting.DisplayTitle,
            meetingPeople.GetValueOrDefault(r.Id) ?? []));
        return FolderSuggester.Best(input, others, NaturalComparer.Instance);
    }

    /// <summary>「這場很像「產品」裡的會議：陳家豪、王小明 都參加（3 場）」或「…：標題都有「產品週會」（4 場）」</summary>
    public static string SuggestionText(FolderSuggestion s, IEnumerable<PersonOverview> people)
    {
        if (s.Title is { } word) return L.F("這場很像「%@」裡的會議：標題都有「%@」（%lld 場）", s.Folder, word, s.Count);
        var list = people.ToList();
        var names = (s.People ?? []).Select(id => list.FirstOrDefault(p => p.Id == id)?.Name).OfType<string>().Take(3);
        return L.F("這場很像「%@」裡的會議：%@ 都參加（%lld 場）", s.Folder, string.Join(MeetingLanguages.Ui("、", ", "), names), s.Count);
    }

    /// <summary>側欄滑過去看的條件：「自動加入：標題包含 週會、sync；陳家豪、王小明 都參加；範本：一對一」</summary>
    public static string RuleSummary(FolderRule rule, IEnumerable<PersonOverview> people, IEnumerable<TemplateRecord> templates)
    {
        var separator = MeetingLanguages.Ui("、", ", ");
        var parts = new List<string>();
        if (rule.Keywords.Count > 0) parts.Add(L.F("標題包含 %@", string.Join(separator, rule.Keywords)));
        var list = people.ToList();
        var names = rule.People.Select(id => list.FirstOrDefault(p => p.Id == id)?.Name).OfType<string>().ToList();
        if (names.Count > 0) parts.Add(L.F("%@ 都參加", string.Join(separator, names)));
        if (rule.TemplateId is { } t && templates.FirstOrDefault(x => x.Id == t) is { } template) parts.Add(L.F("範本：%@", template.DisplayName()));
        return L.F("自動加入：%@", string.Join(MeetingLanguages.Ui("；", "; "), parts));
    }
}
