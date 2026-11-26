using Shiyin.Data;

namespace Shiyin.Services;

/// <summary>
/// 側欄的收合（R101、M§75、design-windows §11.34，同 Mac 的 Store/SidebarCollapse.swift）：收起來的段落（folders、meetings、people、
/// group:&lt;分組 id&gt;）、在原地展開的列（folderid:&lt;資料夾 id&gt;，R118、M§92.1），存在 settings.json（字串陣列，鍵同 Mac）；值真的變了才寫
/// </summary>
public static class SidebarCollapse
{
    /// <summary>資料夾在原地展開幾場；超過時最後一列「顯示全部 N 場」</summary>
    public const int PeekCount = 5;

    /// <summary>資料夾那一列的鍵（R118、M§92.1）：用 id，改名不用搬；前綴跟以前的 folder:&lt;名稱&gt; 不同，名字剛好是數字時不會混在一起</summary>
    public static string FolderKey(long id) => "folderid:" + id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>鍵是哪個資料夾（folderid:&lt;id&gt;）；不是資料夾的鍵是 null</summary>
    public static long? FolderId(string key) =>
        key.StartsWith("folderid:", StringComparison.Ordinal) && long.TryParse(key["folderid:".Length..], System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var id) ? id : null;
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

    /// <summary>資料夾在原地展開了沒；預設收起</summary>
    public static bool IsRowExpanded(AppSettings settings, string key) => Expanded(settings).Contains(key);

    public static void SetRow(AppSettings settings, string key, bool expanded)
    {
        var set = Expanded(settings);
        if (expanded ? set.Add(key) : set.Remove(key)) Save(settings, AppSettings.SidebarExpandedKey, set);
    }

    /// <summary>資料夾裡的會議（不含垃圾桶），日期新到舊：meetings 是資料庫讀出來的順序（meeting_date DESC, id DESC）</summary>
    public static List<MeetingRow> PeekRows(IEnumerable<MeetingRow> meetings, long folderId) => meetings.Where(m => m.Meeting.FolderId == folderId).ToList();

    /// <summary>
    /// 下面的列表只放資料夾以外的會議，標題「其他會議」（R101.6，同 Mac 的 listShowsOthersOnly）：不在垃圾桶、沒看某個資料夾、
    /// 沒篩選、不是依資料夾分組，而且至少有一場會議在資料夾裡。篩選、依資料夾分組是要跨資料夾找東西，照舊是所有會議
    /// </summary>
    public static bool ShowsOthersOnly(IEnumerable<MeetingRow> meetings, bool trash, long? folderFilter, MeetingFilter filter, MeetingGrouping grouping) =>
        !trash && folderFilter is null && !filter.IsActive && grouping != MeetingGrouping.Folder && meetings.Any(InFolder);

    public static bool InFolder(MeetingRow row) => row.Meeting.FolderId is not null;

    /// <summary>
    /// 程式帶你去某場會議時（reveal）：看得到它在哪裡。在下面的列表（groups）就展開會議列表和那一組；在資料夾裡（下面的列表沒有）
    /// 就展開「資料夾」那一段和那個資料夾（最新的會議一定在展開的最近 5 場裡）
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
            if (meeting.FolderId is long folderId) SetRow(settings, FolderKey(folderId), true);
        }
    }

    static void Save(AppSettings settings, string key, HashSet<string> set) => settings.Set(key, set.Order(StringComparer.Ordinal));

    /// <summary>以前用名字當鍵的資料夾（v14 換定期會議、v18 換成 id 時用）</summary>
    const string NameKeyPrefix = "folder:";

    /// <summary>
    /// 拿掉定期會議（D177、資料庫的 migration v14）之後：設定檔裡展開、排序的 series:&lt;id&gt; 換成 folder:&lt;名稱&gt;（names 是 v14 換掉的定期會議），
    /// 換完跟原本的資料夾重複時留前面的；names 裡沒有的 series 鍵拿掉。同 Mac 的 AppDatabase.replaceSeriesKeys。
    /// 展開的鍵照舊照 Ordinal 排序存，排序的鍵照原本的順序。v18 起資料夾的鍵是 id：同一次升級時接著 ReplaceFolderKeys 換成 folderid:&lt;id&gt;
    /// </summary>
    public static void ReplaceSeriesKeys(AppSettings settings, IReadOnlyDictionary<long, string> names)
    {
        foreach (var key in new[] { AppSettings.SidebarExpandedKey, AppSettings.SidebarOrderKey })
        {
            var list = settings.StringList(key);
            if (!list.Any(k => k.StartsWith("series:", StringComparison.Ordinal))) continue;
            var seen = new HashSet<string>();
            var replaced = list.Select(k =>
            {
                if (!k.StartsWith("series:", StringComparison.Ordinal)) return k;
                return long.TryParse(k["series:".Length..], out var id) && names.TryGetValue(id, out var name) ? NameKeyPrefix + name : null;
            }).OfType<string>().Where(seen.Add).ToList();
            if (key == AppSettings.SidebarExpandedKey) settings.Set(key, replaced.Order(StringComparer.Ordinal));
            else settings.Set(key, replaced);
        }
    }

    /// <summary>
    /// 資料夾改用 id（R118、資料庫的 migration v18）之後：設定檔裡展開、排序的 folder:&lt;名稱&gt; 換成 folderid:&lt;id&gt;（ids 是 v18 之後的資料夾，
    /// 名稱 → id）；收合的段落（依資料夾分組的每一組）group:folder-&lt;名稱&gt; 換成 group:folder-&lt;id&gt;，不在資料夾那一組 group:folder- 換成
    /// group:folder-none。找不到的名字留著不動（可能是別的 SHIYIN_HOME 的資料夾），換完重複的留前面的；ids 是空的不呼叫。
    /// 同 Mac 的 AppDatabase.replaceFolderKeys（M§92.1）。展開、收合的鍵照舊照 Ordinal 排序存，排序的鍵照原本的順序；值沒變不寫
    /// </summary>
    public static void ReplaceFolderKeys(AppSettings settings, IReadOnlyDictionary<string, long> ids)
    {
        string Row(string k) => k.StartsWith(NameKeyPrefix, StringComparison.Ordinal) && ids.TryGetValue(k[NameKeyPrefix.Length..], out var id) ? FolderKey(id) : k;
        string Group(string k)
        {
            const string prefix = "group:folder-";
            if (!k.StartsWith(prefix, StringComparison.Ordinal)) return k;
            var name = k[prefix.Length..];
            if (name.Length == 0) return prefix + "none";
            return ids.TryGetValue(name, out var id) ? prefix + id.ToString(System.Globalization.CultureInfo.InvariantCulture) : k;
        }
        foreach (var (key, map, sorted) in new[]
        {
            (AppSettings.SidebarExpandedKey, (Func<string, string>)Row, true), (AppSettings.SidebarOrderKey, Row, false), (AppSettings.SidebarCollapsedKey, Group, true),
        })
        {
            var list = settings.StringList(key);
            var seen = new HashSet<string>();
            var replaced = list.Select(map).Where(seen.Add).ToList();
            if (replaced.SequenceEqual(list)) continue;
            settings.Set(key, sorted ? replaced.Order(StringComparer.Ordinal).ToList() : replaced);
        }
    }
}
