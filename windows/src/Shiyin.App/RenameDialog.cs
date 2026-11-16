using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Shiyin.Localization;
using Shiyin.Model;
using Shiyin.Services;

namespace Shiyin.App;

/// <summary>重新命名的對話框（R102、M§76、design-windows §11.35，同 Mac 的 Views/RenameSheet.swift，參考 ChatGPT 的「重新命名對話」）</summary>
public static partial class Dialogs
{
    /// <summary>DEBUG 的 state（renameTarget）：開著的重新命名對話框要改的東西</summary>
    public static RenameTarget? RenamingTarget { get; private set; }

    /// <summary>
    /// 樣子照 ChatGPT 的「重新命名對話」（R102.1、M§76）：大標題、右上角 ✕、一行灰字說明、細框的輸入框（帶目前的名字並全選）、
    /// 右下兩顆圓角按鈕「取消」（淡底細框）「儲存」（新資料夾「建立並移入」、「+」是「建立」；字的顏色當底：淺色黑底白字、深色白底黑字）。
    /// 名字空白、沒改時「儲存」停用（Enter 也不會存）；改成已經有的資料夾、人物名字時說明換成合併的提醒。Enter 儲存、Esc 取消。
    /// R106 起元件是共用的 Sheet、SheetDialog（design-windows §11.39），樣子不變。會議、資料夾、新資料夾、定期會議、人物、標記都用這一個；同時只開一個
    /// </summary>
    public static async Task Rename(MainWindow window, RenameTarget target)
    {
        if (window.XamlRoot is null || RenamingTarget is not null) return;
        var host = AppHost.Current;
        var state = window.State;
        var original = host.Controller.CurrentName(target);
        var newFolder = target is RenameTarget.NewFolder;
        // 「資料夾」標題的「+」：建一個空的資料夾（R104），主要按鈕「建立」
        var emptyFolder = Renaming.IsEmptyNewFolder(target);
        var placeholder = target switch
        {
            RenameTarget.Folder or RenameTarget.NewFolder => L.T("資料夾名稱"),
            RenameTarget.Person => L.T("名字"),
            _ => L.T("名稱"),
        };
        var heading = RenameTitle(target);
        var sheet = new SheetDialog(window, heading);
        var saved = false;
        var hint = Sheet.Subtitle("");
        var box = Sheet.TextField(original, placeholder);
        var primary = Sheet.Pill(emptyFolder ? L.T("建立") : newFolder ? L.T("建立並移入") : L.T("儲存"), Sheet.Kind.Primary);
        sheet.Form.Children.Add(Sheet.Header(heading, sheet.Cancel, hint));
        sheet.Form.Children.Add(box);
        sheet.Form.Children.Add(Sheet.Buttons(null, Sheet.Pill(L.T("取消"), click: sheet.Cancel), primary));

        // 資料夾、人物（合併的提醒）：打開時的名單
        var folders = state.Folders.Select(f => f.Name).ToList();
        var people = host.Db.PeopleOverview().Select(p => (p.Id, p.Name)).ToList();
        var time = target is RenameTarget.Bookmark b ? host.Db.Bookmark(b.Id)?.Time ?? 0 : 0;
        bool CanSave() => Renaming.CanSave(target, original, box.Text, Renaming.Conflict(target, box.Text, folders, people));
        void Update()
        {
            hint.Text = RenameHint(target, Renaming.Conflict(target, box.Text, folders, people), time);
            primary.IsEnabled = CanSave();
        }
        void Save()
        {
            if (!CanSave()) return;
            saved = true;
            sheet.Close();
        }
        primary.Click += (_, _) => Save();
        box.TextChanged += (_, _) => Update();
        // Enter 儲存（主要按鈕停用時不存）、Esc 取消；打開時輸入框拿到焦點、名字整個選好，直接打新的名字（SheetDialog）
        sheet.Submit = Save;
        sheet.Primary = primary;
        sheet.Focus = box;
        Update();
        RenamingTarget = target;
        try { await sheet.Show(); }
        finally { RenamingTarget = null; }
        if (!saved) return;
        var name = box.Text.Trim();
        // 正在看的資料夾改名：先換篩選，重讀時才不會因為舊名稱不見而回到所有會議（同 Mac 的 renameFolder）
        if (target is RenameTarget.Folder folder && state.FolderFilter == folder.Name) state.SetFolder(name);
        host.Controller.CommitRename(target, name);
        // 人物模式：選改完的那個人（改成已經有的名字時合併過去，是那個人）
        if (target is RenameTarget.Person && state.Sidebar == SidebarMode.People) state.SelectPersonNamed(name);
    }

    static string RenameTitle(RenameTarget target) => target switch
    {
        RenameTarget.Meeting => L.T("重新命名會議"),
        RenameTarget.Folder => L.T("重新命名資料夾"),
        RenameTarget.NewFolder => L.T("新資料夾"),
        RenameTarget.Series => L.T("重新命名定期會議"),
        RenameTarget.Person => L.T("重新命名這個人"),
        _ => L.T("重新命名標記"),
    };

    /// <summary>說明（R102.2，同 Mac）；改成會合併的名字時換成提醒</summary>
    static string RenameHint(RenameTarget target, string? conflict, double bookmarkTime)
    {
        if (conflict is not null)
            return Renaming.IsEmptyNewFolder(target) ? L.F("已經有「%@」。", conflict)
                : target is RenameTarget.NewFolder ? L.F("已經有「%@」，會議會移進那個資料夾。", conflict) : L.F("已經有「%@」，儲存後會合併成一個。", conflict);
        return target switch
        {
            RenameTarget.Meeting => L.T("保持簡短、容易辨識。日期和與會者在會議頁的「編輯」改。"),
            RenameTarget.Folder => L.T("資料夾裡的會議都會跟著改。"),
            RenameTarget.NewFolder { Ids.Count: 0 } => L.T("例如專案名稱或客戶名稱。"),
            RenameTarget.NewFolder n => n.Ids.Count > 1 ? L.F("例如專案名稱或客戶名稱。這 %lld 場會議會移進新資料夾。", n.Ids.Count)
                : L.T("例如專案名稱或客戶名稱。這場會議會移進新資料夾。"),
            RenameTarget.Series => L.T("保持簡短、容易辨識。"),
            RenameTarget.Person => L.T("所有會議裡的這個人都會跟著改。"),
            _ => L.F("%@ 的標記", TimeFormat.String(bookmarkTime)),
        };
    }
}
