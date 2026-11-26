using Shiyin.Data;
using Shiyin.Localization;

namespace Shiyin.Services;

/// <summary>
/// 側欄的拖曳（R103、M§77、design-windows §11.36，同 Mac 的 Store/SidebarDrag.swift）：「資料夾」那一段拖曳排好的順序，
/// 存在 settings.json 的 sidebarOrder（字串陣列，照順序；鍵同 sidebarExpanded：folderid:&lt;id&gt;，R118）；拖一場會議時帶哪幾場
/// </summary>
public static class SidebarDrag
{
    /// <summary>DataPackage 的自訂格式（只在拾音裡面）：拖的會議（id 用逗號分開）、拖的資料夾（鍵）</summary>
    public const string MeetingFormat = "com.weiwei.shiyin.meeting-ids";
    public const string ItemFormat = "com.weiwei.shiyin.sidebar-item";

    public static List<string> Order(AppSettings settings) => settings.StringList(AppSettings.SidebarOrderKey);

    /// <summary>
    /// 「資料夾」那一段的順序：拖曳排過的照 sidebarOrder，沒排過的（新的）在後面，照預設（defaults：資料夾照名稱）
    /// </summary>
    public static List<string> Sorted(IReadOnlyList<string> defaults, IReadOnlyList<string> order)
    {
        var rank = new Dictionary<string, int>();
        for (var i = 0; i < order.Count; i++) rank.TryAdd(order[i], i);
        return defaults.Select((key, index) => (key, index))
            .OrderBy(x => rank.TryGetValue(x.key, out var r) ? 0 : 1)
            .ThenBy(x => rank.TryGetValue(x.key, out var r) ? r : x.index)
            .Select(x => x.key).ToList();
    }

    public static List<string> Sorted(AppSettings settings, IReadOnlyList<string> defaults) => Sorted(defaults, Order(settings));

    /// <summary>
    /// 把一個資料夾拖到另一個的前面或後面（current 是畫面上的順序）；整段的順序寫回 sidebarOrder。回傳有沒有寫（設定改了才重畫）
    /// </summary>
    public static bool Move(AppSettings settings, IReadOnlyList<string> current, string key, string target, bool before)
    {
        var keys = current.ToList();
        var from = keys.IndexOf(key);
        if (key == target || from < 0) return false;
        keys.RemoveAt(from);
        var to = keys.IndexOf(target);
        if (to < 0) return false;
        keys.Insert(before ? to : to + 1, key);
        return Save(settings, keys);
    }

    /// <summary>往上、往下移一格（Alt+↑／Alt+↓）；到頭了不動</summary>
    public static bool MoveBy(AppSettings settings, IReadOnlyList<string> current, string key, int offset)
    {
        var i = current.ToList().IndexOf(key);
        if (i < 0 || i + offset < 0 || i + offset >= current.Count) return false;
        return Move(settings, current, key, current[i + offset], before: offset < 0);
    }

    /// <summary>刪除資料夾、改名時合併進別的資料夾（留下的那個照舊的位置，R118）：拿掉它的鍵。鍵是 id，改名不用搬</summary>
    public static void Remove(AppSettings settings, string key)
    {
        var keys = Order(settings);
        if (keys.RemoveAll(k => k == key) > 0) Save(settings, keys);
    }

    /// <summary>拖一場會議時帶的會議：在多選裡就是整個多選（列表的順序），不然只有它（同 Mac 的 dragIDs）</summary>
    public static List<long> DragIds(long id, IReadOnlyList<long> multi) => multi.Count > 1 && multi.Contains(id) ? [.. multi] : [id];

    public static string Encode(IEnumerable<long> ids) => string.Join(',', ids);

    public static List<long> Decode(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => long.TryParse(x, out var v) ? v : 0).Where(v => v > 0).ToList();

    /// <summary>下面列表的標題能不能收會議（R103.1）：在看資料夾，或是「其他會議」；看所有會議、篩選中、垃圾桶、人物時不行</summary>
    public static bool HeaderAccepts(bool trash, bool people, long? folderFilter, bool othersOnly) =>
        !trash && !people && (folderFilter is not null || othersOnly);

    static bool Save(AppSettings settings, List<string> keys)
    {
        if (keys.SequenceEqual(Order(settings))) return false;
        settings.Set(AppSettings.SidebarOrderKey, keys);
        return true;
    }
}

public sealed partial class AppController
{
    /// <summary>
    /// 會議拖到下面列表的標題（R103.1，同 Mac 的 dropOnListHeader）：在看資料夾＝移進去、「其他會議」＝移出資料夾（提示照 MoveToFolder）
    /// </summary>
    public void DropOnListHeader(IReadOnlyList<long> ids, long? folderFilter, bool othersOnly)
    {
        if (ids.Count == 0) return;
        if (folderFilter is long folder) MoveToFolder(ids, folder);
        else if (othersOnly)
        {
            var inFolder = ids.Select(Db.Meeting).OfType<Model.Meeting>().Where(m => m.FolderId is not null).Select(m => m.Id!.Value).ToList();
            if (inFolder.Count > 0) MoveToFolder(inFolder, null);
            else Notice?.Invoke(ids.Count == 1 ? L.T("這場會議沒有在任何資料夾裡") : L.T("這些會議都沒有在資料夾裡"));
        }
    }
}
