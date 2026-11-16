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
    /// ContentDialog 內建的標題、按鈕不用（按鈕撐滿一整排、主要按鈕在左邊、底下一條灰色的按鈕區），整個內容自己排、按鈕區收起來（design-windows §11.35）。
    /// 會議、資料夾、新資料夾、定期會議、人物、標記都用這一個；同時只開一個
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
        var dialog = new ContentDialog { XamlRoot = window.XamlRoot };
        var saved = false;

        var title = Ui.DialogTitle(RenameTitle(target));
        title.FontSize = 22;
        var hint = Ui.Text("", 14, color: Ui.Secondary);
        var close = SidebarLook.HeaderIcon(new Button(), "");
        close.Margin = new Thickness(0, -2, -6, 0);
        close.VerticalAlignment = VerticalAlignment.Top;
        ToolTipService.SetToolTip(close, L.T("取消"));
        AutomationProperties.SetName(close, L.T("取消"));
        close.Click += (_, _) => dialog.Hide();
        var header = Ui.Bar(null, Ui.Column(6, title, hint), close, 12);

        var box = RenameBox(original, placeholder);
        var cancel = PillButton(L.T("取消"), prominent: false);
        var primary = PillButton(emptyFolder ? L.T("建立") : newFolder ? L.T("建立並移入") : L.T("儲存"), prominent: true);
        cancel.Click += (_, _) => dialog.Hide();
        var buttons = Ui.Row(10, cancel, primary);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        var form = Ui.Column(18, header, box, buttons);
        form.Width = 392;
        dialog.Content = form;

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
            dialog.Hide();
        }
        primary.Click += (_, _) => Save();
        box.TextChanged += (_, _) => Update();
        // Enter 儲存（主要按鈕不是 ContentDialog 的預設按鈕了，自己接）、Esc 取消
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            e.Handled = true;
            Save();
        };
        form.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Escape) return;
            e.Handled = true;
            dialog.Hide();
        };
        // 內建的按鈕區（沒有按鈕時照樣是底下一條灰色的空白）收起來，整個對話框同一個底色。
        // 打開時輸入框拿到焦點、名字整個選好，直接打新的名字：ContentDialog 打開後會把焦點給第一個能按的元件（右上角的 ✕），排在它後面
        dialog.Opened += (_, _) => Ui.Guard(() =>
        {
            if (Descendant(dialog, "CommandSpace") is FrameworkElement commands) commands.Visibility = Visibility.Collapsed;
            box.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Ui.Guard(() =>
            {
                box.Focus(FocusState.Programmatic);
                box.SelectAll();
            }));
        });
        Update();
        RenamingTarget = target;
        try { await Show(dialog); }
        finally { RenamingTarget = null; }
        if (!saved) return;
        var name = box.Text.Trim();
        // 正在看的資料夾改名：先換篩選，重讀時才不會因為舊名稱不見而回到所有會議（同 Mac 的 renameFolder）
        if (target is RenameTarget.Folder folder && state.FolderFilter == folder.Name) state.SetFolder(name);
        host.Controller.CommitRename(target, name);
        // 人物模式：選改完的那個人（改成已經有的名字時合併過去，是那個人）
        if (target is RenameTarget.Person && state.Sidebar == SidebarMode.People) state.SelectPersonNamed(name);
    }

    /// <summary>範本裡有名字的元件（ContentDialog 的按鈕區）</summary>
    static DependencyObject? Descendant(DependencyObject root, string name)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement { Name: var n } && n == name) return child;
            if (Descendant(child, name) is { } found) return found;
        }
        return null;
    }

    /// <summary>
    /// 細框的輸入框（ChatGPT、同 Mac：圓角 10，沒焦點時字色 25% 的框、有焦點時 75%，沒有強調色的焦點線；字 15）。對比主題時照平台
    /// </summary>
    static TextBox RenameBox(string text, string placeholder)
    {
        var box = new TextBox { Text = text, PlaceholderText = placeholder, FontSize = 15, CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8, 12, 8) };
        AutomationProperties.SetName(box, placeholder);
        if (Ui.HighContrast) return box;
        var fill = SidebarLook.Gray(0.04);
        box.Resources["TextControlBackground"] = fill;
        box.Resources["TextControlBackgroundPointerOver"] = fill;
        box.Resources["TextControlBackgroundFocused"] = fill;
        box.Resources["TextControlBorderBrush"] = SidebarLook.Gray(0.25);
        box.Resources["TextControlBorderBrushPointerOver"] = SidebarLook.Gray(0.4);
        box.Resources["TextControlBorderBrushFocused"] = SidebarLook.Gray(0.75);
        box.Resources["TextControlBorderThemeThickness"] = new Thickness(1);
        box.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(1.5);
        return box;
    }

    /// <summary>
    /// 對話框的圓角按鈕（同 Mac 的 SheetPillButtonStyle）：主要按鈕是字的顏色當底、視窗底色的字（淺色黑底白字、深色白底黑字），停用時 35%；
    /// 次要按鈕淡淡的底加細框。圓角 10、高 34。對比主題時照平台（主要按鈕是強調色的按鈕）
    /// </summary>
    static Button PillButton(string text, bool prominent)
    {
        var button = new Button { Content = text, MinWidth = 68, MinHeight = 34, Padding = new Thickness(18, 0, 18, 0), CornerRadius = new CornerRadius(10) };
        if (Ui.HighContrast)
        {
            if (prominent && Application.Current.Resources.TryGetValue("AccentButtonStyle", out var style) && style is Style accent) button.Style = accent;
            return button;
        }
        Brush Ink(double opacity) => SidebarLook.Gray(opacity);
        var paper = new SolidColorBrush(Ui.IsDark ? Ui.HexColor("#202020") : Microsoft.UI.Colors.White);
        var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var r = button.Resources;
        if (prominent)
        {
            r["ButtonBackground"] = Ink(1);
            r["ButtonBackgroundPointerOver"] = Ink(0.85);
            r["ButtonBackgroundPressed"] = Ink(0.75);
            r["ButtonBackgroundDisabled"] = Ink(0.35);
            foreach (var key in new[] { "ButtonForeground", "ButtonForegroundPointerOver", "ButtonForegroundPressed", "ButtonForegroundDisabled" }) r[key] = paper;
            foreach (var key in new[] { "ButtonBorderBrush", "ButtonBorderBrushPointerOver", "ButtonBorderBrushPressed", "ButtonBorderBrushDisabled" }) r[key] = clear;
            button.Background = Ink(1);
            button.Foreground = paper;
            button.BorderBrush = clear;
        }
        else
        {
            r["ButtonBackground"] = Ink(0.06);
            r["ButtonBackgroundPointerOver"] = Ink(0.10);
            r["ButtonBackgroundPressed"] = Ink(0.14);
            foreach (var key in new[] { "ButtonForeground", "ButtonForegroundPointerOver", "ButtonForegroundPressed" }) r[key] = Ui.Primary;
            foreach (var key in new[] { "ButtonBorderBrush", "ButtonBorderBrushPointerOver", "ButtonBorderBrushPressed" }) r[key] = Ink(0.18);
            button.Background = Ink(0.06);
            button.Foreground = Ui.Primary;
            button.BorderBrush = Ink(0.18);
        }
        return button;
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
