using Shiyin.Data;
using Shiyin.Localization;
using Shiyin.Model;
using Shiyin.Notes;
using Shiyin.Text;

namespace Shiyin.Services;

/// <summary>會議列表的排序（R97.2、M§71）：記住（settings.json 的 meetingSort）</summary>
public enum MeetingSort { DateDesc, DateAsc, Title, Duration }

/// <summary>會議列表的分組（R97.2）：記住；依定期會議同 Mac 還沒有（選單不出現，存的值當成不分組）</summary>
public enum MeetingGrouping { None, Month, Folder, Series }

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
    public static readonly MeetingGrouping[] All = [MeetingGrouping.None, MeetingGrouping.Month, MeetingGrouping.Folder, MeetingGrouping.Series];

    public static string Code(this MeetingGrouping g) => g switch
    {
        MeetingGrouping.Month => "month",
        MeetingGrouping.Folder => "folder",
        MeetingGrouping.Series => "series",
        _ => "none",
    };

    public static MeetingGrouping? Parse(string? code) => All.Select(g => (MeetingGrouping?)g).FirstOrDefault(g => g!.Value.Code() == code);

    public static string Label(this MeetingGrouping g) => g switch
    {
        MeetingGrouping.Month => L.T("依月份"),
        MeetingGrouping.Folder => L.T("依資料夾"),
        MeetingGrouping.Series => L.T("依定期會議"),
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
    /// <summary>實際用的分組：看某個資料夾時不依資料夾分組；依定期會議同 Mac 還沒有（M§71）</summary>
    public static MeetingGrouping Effective(MeetingGrouping grouping, string? folderFilter) => grouping switch
    {
        MeetingGrouping.Folder when folderFilter is not null => MeetingGrouping.None,
        MeetingGrouping.Series => MeetingGrouping.None,
        _ => grouping,
    };

    /// <summary>選單裡可以選的分組</summary>
    public static List<MeetingGrouping> Available(string? folderFilter) =>
        MeetingGroupings.All.Where(g => g != MeetingGrouping.Series && (g != MeetingGrouping.Folder || folderFilter is null)).ToList();

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
    /// 分組（R97.2）：月份照日期新到舊（舊到新排序時反過來）；資料夾照名稱，最後是「不在資料夾」；每一段裡照排序。
    /// rows 是已經篩過、排好的列表；垃圾桶不分組
    /// </summary>
    public static List<MeetingGroup> Groups(List<MeetingRow> rows, MeetingGrouping effective, MeetingSort sort, bool trash)
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
                var folders = rows.Select(r => r.Meeting.Folder).Distinct().Where(f => f.Length > 0).Order(NaturalComparer.Instance).ToList();
                if (rows.Any(r => r.Meeting.Folder.Length == 0)) folders.Add("");
                return folders.Select(folder => new MeetingGroup("folder-" + folder, folder.Length == 0 ? L.T("不在資料夾") : folder,
                    rows.Where(r => r.Meeting.Folder == folder).ToList())).ToList();
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
    /// 列表上的會議（R97、M§71）：資料夾或定期會議、篩選（垃圾桶不篩選）、排序
    /// </summary>
    public static List<MeetingRow> Filtered(IEnumerable<MeetingRow> source, bool trash, string? folderFilter, long? seriesFilter, MeetingFilter filter,
        MeetingSort sort, IReadOnlyDictionary<long, HashSet<long>> meetingPeople, string? myName)
    {
        var rows = source;
        if (!trash && folderFilter is { } folder) rows = rows.Where(r => r.Meeting.Folder == folder);
        if (!trash && seriesFilter is long series) rows = rows.Where(r => r.Meeting.SeriesId == series);
        if (!trash && filter.IsActive) rows = rows.Where(r => Matches(r, filter, meetingPeople, myName));
        return Sorted(rows, sort);
    }
}

/// <summary>資料夾自動歸類、定期會議的畫面用的計算（R98、R99、M§72、§73，同 Mac 的 Store/OrganizeActions.swift）</summary>
public static class Organizing
{
    /// <summary>現有的資料夾和各自的場數（不含垃圾桶），依名稱排序；有自動加入條件的資料夾沒有會議也列出來（M§72）</summary>
    public static List<(string Name, int Count)> Folders(IEnumerable<MeetingRow> meetings, IEnumerable<FolderRule> rules)
    {
        var counts = meetings.Where(m => m.Meeting.Folder.Length > 0).GroupBy(m => m.Meeting.Folder).ToDictionary(g => g.Key, g => g.Count());
        foreach (var rule in rules) counts.TryAdd(rule.Folder, 0);
        return counts.Select(p => (p.Key, p.Value)).OrderBy(f => f.Key, NaturalComparer.Instance).ToList();
    }

    /// <summary>找可能的定期會議的輸入：不在垃圾桶（呼叫的人給列表）、不在定期會議裡的會議</summary>
    public static List<SeriesDetector.Input> SeriesInputs(IEnumerable<MeetingRow> meetings, IReadOnlyDictionary<long, HashSet<long>> meetingPeople,
        TimeZoneInfo? timeZone = null) =>
        meetings.Where(r => r.Meeting.SeriesId is null)
            .Select(r => MeetingTime.Of(r.Meeting, timeZone) is { } time
                ? new SeriesDetector.Input(r.Id, time, meetingPeople.GetValueOrDefault(r.Id) ?? [], r.Meeting.DisplayTitle) : null)
            .OfType<SeriesDetector.Input>().ToList();

    /// <summary>這幾場共同的定期會議；不同或都沒有時 null</summary>
    public static long? CommonSeries(IEnumerable<MeetingRow> rows, IReadOnlyCollection<long> ids)
    {
        var values = rows.Where(r => ids.Contains(r.Id)).Select(r => r.Meeting.SeriesId).Distinct().ToList();
        return values.Count == 1 ? values[0] : null;
    }

    /// <summary>常參加的人：過半數場次都有的人物</summary>
    public static List<PersonOverview> Regulars(long seriesId, IEnumerable<MeetingRow> meetings, IReadOnlyDictionary<long, HashSet<long>> meetingPeople,
        IEnumerable<PersonOverview> people)
    {
        var core = SeriesDetector.Core(meetings.Where(r => r.Meeting.SeriesId == seriesId).Select(r => (IReadOnlySet<long>)(meetingPeople.GetValueOrDefault(r.Id) ?? [])));
        return people.Where(p => core.Contains(p.Id)).ToList();
    }

    /// <summary>
    /// 建議放進資料夾（R98.3）：會議不在資料夾、還沒決定過、已經有會議紀錄、不在錄音中、不在垃圾桶時，跟各個資料夾比
    /// </summary>
    public static FolderSuggestion? Suggestion(Meeting meeting, IEnumerable<MeetingRow> meetings, IReadOnlyDictionary<long, HashSet<long>> meetingPeople,
        long? recordingId)
    {
        if (meeting.Id is not long id || meeting.Folder.Length > 0 || meeting.FolderDecided || meeting.DeletedAt is not null
            || meeting.Notes is null || recordingId == id) return null;
        var input = new FolderSuggester.Input(id, "", meeting.DisplayTitle, meetingPeople.GetValueOrDefault(id) ?? []);
        var others = meetings.Select(r => new FolderSuggester.Input(r.Id, r.Meeting.Folder, r.Meeting.DisplayTitle, meetingPeople.GetValueOrDefault(r.Id) ?? []));
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

    /// <summary>側欄滑過去看的條件：「自動加入：標題包含 週會、sync；陳家豪、王小明 都參加；範本：一對一；定期會議：產品週會」</summary>
    public static string RuleSummary(FolderRule rule, IEnumerable<PersonOverview> people, IEnumerable<TemplateRecord> templates, IEnumerable<MeetingSeries> series)
    {
        var separator = MeetingLanguages.Ui("、", ", ");
        var parts = new List<string>();
        if (rule.Keywords.Count > 0) parts.Add(L.F("標題包含 %@", string.Join(separator, rule.Keywords)));
        var list = people.ToList();
        var names = rule.People.Select(id => list.FirstOrDefault(p => p.Id == id)?.Name).OfType<string>().ToList();
        if (names.Count > 0) parts.Add(L.F("%@ 都參加", string.Join(separator, names)));
        if (rule.TemplateId is { } t && templates.FirstOrDefault(x => x.Id == t) is { } template) parts.Add(L.F("範本：%@", template.DisplayName()));
        if (rule.SeriesId is long sid && series.FirstOrDefault(x => x.Id == sid) is { } s) parts.Add(L.F("定期會議：%@", s.Name));
        return L.F("自動加入：%@", string.Join(MeetingLanguages.Ui("；", "; "), parts));
    }

    /// <summary>系列頁的「每週一 10:00・6 場・最近 2026-11-10」（rows 是這個系列的會議，新到舊）</summary>
    public static string SeriesSummaryLine(MeetingSeries series, IReadOnlyList<MeetingRow> rows)
    {
        var parts = new List<string> { series.FrequencyText, L.F("%lld 場", rows.Count) };
        if (rows.Count > 0) parts.Add(L.F("最近 %@", rows[0].Meeting.MeetingDate));
        return string.Join("・", parts);
    }
}
