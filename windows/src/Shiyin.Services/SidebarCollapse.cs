using Shiyin.Data;

namespace Shiyin.Services;

/// <summary>
/// 側欄的收合（R101、M§75、design-windows §11.34，同 Mac 的 Store/SidebarCollapse.swift）：收起來的段落（folders（含定期會議）、meetings、people、
/// group:&lt;分組 id&gt;）、在原地展開的列（folder:&lt;名稱&gt;、series:&lt;id&gt;），存在 settings.json（字串陣列，鍵同 Mac）；值真的變了才寫
/// </summary>
public static class SidebarCollapse
{
    /// <summary>資料夾、定期會議在原地展開幾場；超過時最後一列「顯示全部 N 場」</summary>
    public const int PeekCount = 5;

    public static string FolderKey(string name) => "folder:" + name;
    public static string SeriesKey(long id) => $"series:{id}";
    public static string GroupKey(MeetingGroup group) => "group:" + group.Id;

    public static HashSet<string> Collapsed(AppSettings settings) => [.. settings.StringList(AppSettings.SidebarCollapsedKey)];
    public static HashSet<string> Expanded(AppSettings settings) => [.. settings.StringList(AppSettings.SidebarExpandedKey)];

    /// <summary>段落展開了沒；預設展開</summary>
    public static bool IsSectionExpanded(AppSettings settings, string key) => !Collapsed(settings).Contains(key);

    public static void SetSection(AppSettings settings, string key, bool expanded)
    {
        var set = Collapsed(settings);
        if (expanded ? set.Remove(key) : set.Add(key)) Save(settings, AppSettings.SidebarCollapsedKey, set);
    }

    /// <summary>資料夾、定期會議在原地展開了沒；預設收起</summary>
    public static bool IsRowExpanded(AppSettings settings, string key) => Expanded(settings).Contains(key);

    public static void SetRow(AppSettings settings, string key, bool expanded)
    {
        var set = Expanded(settings);
        if (expanded ? set.Add(key) : set.Remove(key)) Save(settings, AppSettings.SidebarExpandedKey, set);
    }

    /// <summary>資料夾改名：展開的鍵跟著改</summary>
    public static void RenameFolder(AppSettings settings, string old, string name)
    {
        var set = Expanded(settings);
        if (old == name || !set.Remove(FolderKey(old))) return;
        set.Add(FolderKey(name));
        Save(settings, AppSettings.SidebarExpandedKey, set);
    }

    /// <summary>資料夾、定期會議裡的會議（不含垃圾桶），日期新到舊：meetings 是資料庫讀出來的順序（meeting_date DESC, id DESC）</summary>
    public static List<MeetingRow> PeekRows(IEnumerable<MeetingRow> meetings, string folder) => meetings.Where(m => m.Meeting.Folder == folder).ToList();
    public static List<MeetingRow> PeekRows(IEnumerable<MeetingRow> meetings, long series) => meetings.Where(m => m.Meeting.SeriesId == series).ToList();

    /// <summary>
    /// 下面的列表只放資料夾、定期會議以外的會議，標題「其他會議」（R101.6，同 Mac 的 listShowsOthersOnly）：不在垃圾桶、沒看某個資料夾或定期會議、
    /// 沒篩選、不是依資料夾分組，而且至少有一場會議在資料夾或定期會議裡。篩選、依資料夾分組是要跨資料夾找東西，照舊是所有會議
    /// </summary>
    public static bool ShowsOthersOnly(IEnumerable<MeetingRow> meetings, bool trash, string? folderFilter, long? seriesFilter, MeetingFilter filter, MeetingGrouping grouping) =>
        !trash && folderFilter is null && seriesFilter is null && !filter.IsActive && grouping != MeetingGrouping.Folder && meetings.Any(InFolderOrSeries);

    public static bool InFolderOrSeries(MeetingRow row) => row.Meeting.Folder.Length > 0 || row.Meeting.SeriesId is not null;

    /// <summary>
    /// 程式帶你去某場會議時（reveal）：看得到它在哪裡。在下面的列表（groups）就展開會議列表和那一組；在資料夾、定期會議裡（下面的列表沒有）
    /// 就展開「資料夾」那一段和那個定期會議或資料夾（最新的會議一定在展開的最近 5 場裡）
    /// </summary>
    public static void ExpandToShow(AppSettings settings, long id, IEnumerable<MeetingGroup> groups, IEnumerable<MeetingRow> meetings)
    {
        if (groups.FirstOrDefault(g => g.Rows.Any(r => r.Id == id)) is { } group)
        {
            SetSection(settings, "meetings", true);
            SetSection(settings, GroupKey(group), true);
        }
        else if (meetings.FirstOrDefault(m => m.Id == id)?.Meeting is { } meeting)
        {
            SetSection(settings, "folders", true);
            if (meeting.SeriesId is long series) SetRow(settings, SeriesKey(series), true);
            else if (meeting.Folder.Length > 0) SetRow(settings, FolderKey(meeting.Folder), true);
        }
    }

    static void Save(AppSettings settings, string key, HashSet<string> set) => settings.Set(key, set.Order(StringComparer.Ordinal));
}
