using Shiyin.Model;

namespace Shiyin.Services;

/// <summary>
/// 重新命名的對話框要改的東西（R102、M§76、design-windows §11.35，同 Mac 的 Store/RenameTarget.swift）：
/// 會議、資料夾、新資料夾（建立後把這幾場會議移進去）、人物、重點標記（定期會議 D177 拿掉）
/// </summary>
public abstract record RenameTarget
{
    public sealed record Meeting(long Id) : RenameTarget;
    /// <summary>資料夾（id，R118）</summary>
    public sealed record Folder(long Id) : RenameTarget;
    public sealed record NewFolder(IReadOnlyList<long> Ids) : RenameTarget;
    public sealed record Person(long Id) : RenameTarget;
    public sealed record Bookmark(long Id) : RenameTarget;

    /// <summary>DEBUG 的 state（renameTarget）：同 Mac 的 "\(self)"，例如 meeting(42)、folder(3)、newFolder([1, 2])</summary>
    public string Key => this switch
    {
        Meeting m => $"meeting({m.Id})",
        Folder f => $"folder({f.Id})",
        NewFolder n => $"newFolder([{string.Join(", ", n.Ids)}])",
        Person p => $"person({p.Id})",
        Bookmark b => $"bookmark({b.Id})",
        _ => "",
    };
}

/// <summary>重新命名的規則（同 Mac 的 renameConflict、RenameSheet 的 canSave）：純函式，畫面和測試共用</summary>
public static class Renaming
{
    /// <summary>
    /// 名字空白不能存；沒改也不能存（新資料夾沒有原本的名字，有字就能建立）。「+」建空的資料夾時（沒有會議）同名的已經有就不能建（R104，conflict 是 Conflict 的結果）
    /// </summary>
    public static bool CanSave(RenameTarget target, string original, string name, string? conflict = null)
    {
        var trimmed = name.Trim();
        return trimmed.Length > 0 && (target is RenameTarget.NewFolder || trimmed != original) && !(IsEmptyNewFolder(target) && conflict is not null);
    }

    /// <summary>「資料夾」標題的「+」建的空資料夾（R104）；從會議的選單建的新資料夾會把會議移進去</summary>
    public static bool IsEmptyNewFolder(RenameTarget target) => target is RenameTarget.NewFolder { Ids.Count: 0 };

    /// <summary>
    /// 改成這個名字會跟別的東西合併時，那個東西的名字（說明換成合併的提醒）：別的資料夾同名（區分大小寫，同資料庫）、
    /// 人物同名（大小寫不分，同 AppDatabase.RenamePerson）。其他是 null
    /// </summary>
    public static string? Conflict(RenameTarget target, string name, IEnumerable<(long Id, string Name)> folders, IEnumerable<(long Id, string Name)> people)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) return null;
        return target switch
        {
            RenameTarget.Folder f => folders.Any(x => x.Name == trimmed && x.Id != f.Id) ? trimmed : null,
            RenameTarget.NewFolder => folders.Any(x => x.Name == trimmed) ? trimmed : null,
            RenameTarget.Person p => people.Where(x => x.Id != p.Id && string.Equals(x.Name, trimmed, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Name).FirstOrDefault(),
            _ => null,
        };
    }
}

public sealed partial class AppController
{
    /// <summary>
    /// 對話框打開時輸入框裡的字：從資料庫讀（人物、標記不靠畫面上的列表；Mac 驗收時人物列表沒打開過，輸入框是空的）。
    /// 會議沒取過標題時是顯示的標題（檔名）
    /// </summary>
    public string CurrentName(RenameTarget target) => target switch
    {
        RenameTarget.Meeting m => Db.Meeting(m.Id) is { } meeting ? (meeting.Title.Length == 0 ? meeting.DisplayTitle : meeting.Title) : "",
        RenameTarget.Folder f => Db.FolderName(f.Id),
        RenameTarget.Person p => Db.PeopleOverview().FirstOrDefault(x => x.Id == p.Id)?.Name ?? "",
        RenameTarget.Bookmark b => Db.Bookmark(b.Id)?.Label ?? "",
        _ => "",
    };

    /// <summary>
    /// 對話框按「儲存」（新資料夾是「建立並移入」）：走原本的動作。會議只改標題（日期、與會者照舊；改了會鎖住標題，整理時不再取名）；
    /// 資料夾改成已經有的名字時合併進那一個（M§92.2）；人物改成已經有的名字時合併過去（AppDatabase.RenamePerson）。空白、沒改的不存。
    /// 回傳資料夾改名後留下的資料夾 id（合併時不是原本的，正在看原本那個時畫面換過去）；其他是 null
    /// </summary>
    public long? CommitRename(RenameTarget target, string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) return null;
        switch (target)
        {
            case RenameTarget.Meeting m:
                if (Db.Meeting(m.Id) is not { } meeting || trimmed == CurrentName(target)) return null;
                UpdateInfo(m.Id, trimmed, meeting.MeetingDate, meeting.Participants);
                break;
            case RenameTarget.Folder f: return RenameFolder(f.Id, trimmed);
            // 「+」建的是空的資料夾（R104），從會議的選單建的會把會議移進去
            case RenameTarget.NewFolder n when n.Ids.Count == 0: CreateFolder(trimmed); break;
            case RenameTarget.NewFolder n: MoveToNewFolder(n.Ids, trimmed); break;
            case RenameTarget.Person p: RenamePerson(p.Id, trimmed); break;
            case RenameTarget.Bookmark b:
                if (Db.Bookmark(b.Id) is { } bookmark) RenameBookmark(bookmark.MeetingId, b.Id, trimmed);
                break;
        }
        return null;
    }
}
