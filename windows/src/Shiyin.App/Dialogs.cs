using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Shiyin.Data;
using Shiyin.IO;
using Shiyin.Localization;
using Shiyin.Model;
using Shiyin.Notes;
using Shiyin.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Shiyin.App;

/// <summary>對話框（ContentDialog）：匯入、匯出、重新辨識說話者、編輯資訊、資料夾、用詞修正、確認、錯誤</summary>
public static class Dialogs
{
    static ContentDialog Dialog(MainWindow window, string title, UIElement content, string primary, string? close = null) => new()
    {
        XamlRoot = window.XamlRoot, Title = title, Content = content, PrimaryButtonText = primary, CloseButtonText = close ?? L.T("取消"),
        DefaultButton = ContentDialogButton.Primary,
    };

    public static async Task Alert(MainWindow window, string title, string message)
    {
        if (window.XamlRoot is null) return;
        var dialog = new ContentDialog
        {
            XamlRoot = window.XamlRoot, Title = title, Content = Ui.Text(message, select: true), CloseButtonText = L.T("好"), DefaultButton = ContentDialogButton.Close,
        };
        await Show(dialog);
    }

    public static async Task<bool> Confirm(MainWindow window, string title, string message, string action)
    {
        if (window.XamlRoot is null) return false;
        return await Show(Dialog(window, title, Ui.Text(message), action)) == ContentDialogResult.Primary;
    }

    /// <summary>同時只能開一個 ContentDialog：前一個還開著時等它關掉</summary>
    static readonly SemaphoreSlim Gate = new(1, 1);

    static async Task<ContentDialogResult> Show(ContentDialog dialog)
    {
        await Gate.WaitAsync();
        try { return await dialog.ShowAsync(); }
        finally { Gate.Release(); }
    }

    // MARK: 匯入（R1.2）

    public static async Task Import(MainWindow window, IReadOnlyList<string> files)
    {
        var host = AppHost.Current;
        var single = files.Count == 1;
        var title = new TextBox { Header = L.T("標題"), PlaceholderText = L.T("選填，沒填就由整理時命名") };
        var inferred = DateText.Date(ImportRules.InferDate(Path.GetFileName(files[0]), Importer.ModificationDate(files[0]), Importer.Today)) ?? Importer.Today;
        var date = new CalendarDatePicker { Header = L.T("會議日期"), Date = new DateTimeOffset(inferred.ToDateTime(TimeOnly.MinValue)) };
        var participants = new TextBox { Header = L.T("與會者"), PlaceholderText = L.T("選填，用逗號分隔") };
        var templates = window.State.Templates;
        var template = Ui.Combo(templates.Select(t => (t.Id, t.DisplayName())), host.Settings.DefaultTemplate, _ => { }, L.T("範本"));
        var language = Ui.Combo(MeetingLanguages.All.Select(l => (l, l.Name())), host.Settings.DefaultMeetingLanguage, _ => { }, L.T("會議語言"));
        ToolTipService.SetToolTip(language, L.T("決定轉錄和會議紀錄用的語言"));
        var count = Ui.Combo(new[] { (0, L.T("自動判斷")) }.Concat(Enumerable.Range(2, 11).Select(n => (n, L.F("%lld 人", n)))), 0, _ => { }, L.T("大約幾個人"));
        ToolTipService.SetToolTip(count, L.T("人多、吵雜或手機放在桌上錄音時，先告訴拾音大約幾個人，說話者會分得比較準"));
        var names = Ui.Column(4, files.Select(f => (UIElement)Ui.Text(Path.GetFileName(f), 13, color: Ui.Secondary, wrap: false)).ToArray());
        var form = Ui.Column(12, names);
        if (single)
        {
            form.Children.Add(title);
            form.Children.Add(date);
        }
        else form.Children.Add(Ui.Text(L.T("會議日期：依各檔案的檔名或修改日期"), 13, color: Ui.Secondary));
        form.Children.Add(participants);
        form.Children.Add(template);
        form.Children.Add(language);
        form.Children.Add(count);
        form.Width = 420;
        var dialog = Dialog(window, single ? L.T("匯入錄音") : L.F("匯入 %lld 個錄音檔", files.Count), new ScrollViewer { Content = form }, L.T("匯入並開始轉錄"));
        if (await Show(dialog) != ContentDialogResult.Primary) return;
        var options = new ImportOptions
        {
            Title = title.Text,
            Date = single && date.Date is { } d ? DateText.String(DateOnly.FromDateTime(d.Date)) : null,
            Participants = participants.Text,
            TemplateId = templates.ElementAtOrDefault(template.SelectedIndex)?.Id,
            Language = MeetingLanguages.All[Math.Max(0, language.SelectedIndex)],
            MinSpeakers = count.SelectedIndex <= 0 ? null : count.SelectedIndex + 1,
        };
        await host.Controller.ConfirmImport(files, options, window.State.CurrentFolder);
    }

    // MARK: 匯出（R7）

    static string FormatLabel(string format) => format switch
    {
        "md" => "Markdown",
        "pdf" => "PDF",
        "docx" => L.T("Word（docx）"),
        "txt" => L.T("純文字（txt）"),
        "srt" => L.T("字幕（srt）"),
        "vtt" => L.T("字幕（vtt）"),
        "shiyin" => L.T(".shiyin 封包"),
        _ => format,
    };

    /// <summary>匯出對話框：打開時就決定是哪一場會議（M§20.9）；存檔或分享（R7.5）</summary>
    public static async Task Export(MainWindow window, long meetingId)
    {
        var host = AppHost.Current;
        ExportBundle bundle;
        try { bundle = host.Controller.Bundle(meetingId); }
        catch (AppController.ExportException e) { window.ShowError(e.Message); return; }
        var hasNotes = bundle.Notes is not null;
        var hasAudio = host.Paths.HasAudio(bundle.Meeting);
        // 還沒有會議紀錄（整理中或整理失敗）時，預設改匯出逐字稿
        var item = hasNotes ? ExportItem.Notes : ExportItem.Transcript;
        var format = item.Formats()[0];
        var items = Enum.GetValues<ExportItem>().Where(i => i != ExportItem.Audio || hasAudio).ToList();
        var itemBox = Ui.Combo(items.Select(i => (i, i.Label())), item, _ => { }, L.T("項目"));
        var formatBox = new ComboBox { Header = L.T("格式"), MinWidth = 200 };
        var times = new CheckBox { Content = L.T("包含時間點"), IsChecked = true };
        var speakers = new CheckBox { Content = L.T("包含說話者"), IsChecked = true };
        var hint = Ui.Text("", 13, color: Ui.Secondary);
        void Sync()
        {
            item = items[Math.Max(0, itemBox.SelectedIndex)];
            var formats = item.Formats();
            if (!formats.Contains(format)) format = formats[0];
            formatBox.Items.Clear();
            foreach (var f in formats) formatBox.Items.Add(FormatLabel(f));
            formatBox.SelectedIndex = Array.IndexOf(formats, format);
            formatBox.IsEnabled = formats.Length > 1;
            times.Visibility = speakers.Visibility = item == ExportItem.Transcript ? Visibility.Visible : Visibility.Collapsed;
            times.IsEnabled = format is not ("srt" or "vtt");
            hint.Text = item switch
            {
                ExportItem.Notes when !hasNotes => L.T("這場會議還沒有會議紀錄（還在整理或整理失敗），匯出的會議紀錄只會有標題和基本資料。要留下內容，請改匯出逐字稿。"),
                ExportItem.Notes => L.T("PDF 和 docx 的段落標題使用明體；Markdown 適合貼到 Notion、GitHub。"),
                ExportItem.Transcript => L.T("srt、vtt 是字幕格式，可以搭配錄音或影片在播放器裡顯示。"),
                ExportItem.Audio => L.T("m4a 檔案小、適合分享；wav 是 16 kHz 單聲道，適合給其他轉錄工具使用。"),
                _ => L.T("包含錄音、逐字稿、說話者、會議紀錄、筆記和重點標記。傳給同事後，對方雙擊就能匯入拾音。"),
            };
            hint.Foreground = item == ExportItem.Notes && !hasNotes ? Ui.Signal : Ui.Secondary;
        }
        itemBox.SelectionChanged += (_, _) => Sync();
        formatBox.SelectionChanged += (_, _) =>
        {
            if (formatBox.SelectedIndex >= 0) format = item.Formats()[formatBox.SelectedIndex];
            times.IsEnabled = format is not ("srt" or "vtt");
        };
        Sync();
        var form = Ui.Column(12, itemBox, formatBox, times, speakers, hint);
        form.Width = 420;
        var dialog = Dialog(window, L.F("匯出「%@」", bundle.Meeting.DisplayTitle), form, L.T("存檔…"));
        dialog.SecondaryButtonText = L.T("分享…");
        var result = await Show(dialog);
        if (result == ContentDialogResult.None) return;
        var filename = AppController.SuggestedFilename(item, format, bundle.Meeting);
        var withTimes = times.IsChecked == true;
        var withSpeakers = speakers.IsChecked == true;
        try
        {
            if (result == ContentDialogResult.Primary)
            {
                var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = Path.GetFileNameWithoutExtension(filename) };
                picker.FileTypeChoices.Add(FormatLabel(format), ["." + format]);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, window.Handle);
                if (await picker.PickSaveFileAsync() is not { } file) return;
                window.ShowNotice(L.T("正在匯出…"));
                await host.Controller.Export(item, format, withTimes, withSpeakers, meetingId, file.Path);
                window.ShowNotice(L.F("已匯出 %@", file.Name));
            }
            else
            {
                var dir = host.Paths.MakeTempDirectory();
                var path = Path.Combine(dir, filename);
                await host.Controller.Export(item, format, withTimes, withSpeakers, meetingId, path);
                await Share.File(window, path, bundle.Meeting.DisplayTitle);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or AppController.ExportException or COMException or InvalidOperationException)
        {
            window.ShowError(e.Message);
        }
    }

    // MARK: 重新辨識說話者（M§23.4、R22）

    public static async Task Rediarize(MainWindow window, long meetingId)
    {
        var host = AppHost.Current;
        var current = host.Db.Meeting(meetingId)?.MinSpeakers ?? 0;
        var count = Ui.Combo(new[] { (0, L.T("自動判斷")) }.Concat(Enumerable.Range(2, 11).Select(n => (n, L.F("%lld 人", n)))), current, _ => { }, L.T("大約幾個人"));
        var form = Ui.Column(14,
            Ui.Text(L.T("只重新分辨誰在說話，逐字稿的文字不會變。人多、吵雜或手機遠距收音時，告訴拾音大約幾個人會分得比較準。"), color: Ui.Secondary),
            count,
            Ui.Text(L.T("原本取過的名字會盡量保留，其他說話者會用聲紋比對、再用整理工具猜名字。會議紀錄不會自動更新。"), 13, color: Ui.Secondary));
        form.Width = 400;
        if (await Show(Dialog(window, L.T("重新辨識說話者"), form, L.T("開始"))) != ContentDialogResult.Primary) return;
        host.Controller.Rediarize(meetingId, count.SelectedIndex <= 0 ? null : count.SelectedIndex + 1);
    }

    // MARK: 會議資訊

    public static async Task EditInfo(MainWindow window, Meeting meeting)
    {
        var title = new TextBox { Header = L.T("標題"), Text = meeting.DisplayTitle };
        var date = new CalendarDatePicker { Header = L.T("會議日期") };
        if (DateText.Date(meeting.MeetingDate) is { } d) date.Date = new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue));
        var participants = new TextBox { Header = L.T("與會者"), Text = meeting.Participants, PlaceholderText = L.T("用逗號分隔") };
        var form = Ui.Column(12, title, date, participants);
        form.Width = 360;
        if (await Show(Dialog(window, L.T("編輯"), form, L.T("儲存"))) != ContentDialogResult.Primary) return;
        // 標題欄預先填的是顯示用的標題（還沒命名時是檔名）；沒動過就照原樣存，不然會被當成自訂標題鎖住，整理時不會再取名
        var edited = title.Text.Trim();
        var dateText = date.Date is { } picked ? DateText.String(DateOnly.FromDateTime(picked.Date)) : meeting.MeetingDate;
        AppHost.Current.Controller.UpdateInfo(meeting.Id!.Value, edited == meeting.DisplayTitle ? meeting.Title : edited, dateText, participants.Text);
    }

    // MARK: 資料夾（R9.2）

    public static async Task NewFolder(MainWindow window, long meetingId)
    {
        var name = new TextBox { PlaceholderText = L.T("資料夾名稱") };
        var form = Ui.Column(10, Ui.Text(L.T("例如專案名稱或客戶名稱。這場會議會移進新資料夾。"), color: Ui.Secondary), name);
        if (await Show(Dialog(window, L.T("新資料夾"), form, L.T("建立並移入"))) != ContentDialogResult.Primary) return;
        if (name.Text.Trim().Length > 0) AppHost.Current.Controller.MoveToFolder(meetingId, name.Text);
    }

    public static async Task RenameFolder(MainWindow window, string old)
    {
        var name = new TextBox { Text = old };
        var form = Ui.Column(10, Ui.Text(L.T("資料夾裡的會議都會跟著改。"), color: Ui.Secondary), name);
        if (await Show(Dialog(window, L.T("重新命名資料夾"), form, L.T("重新命名"))) != ContentDialogResult.Primary) return;
        if (window.State.FolderFilter == old) window.State.SetFolder(name.Text.Trim());
        AppHost.Current.Controller.RenameFolder(old, name.Text);
    }

    // MARK: 用詞修正（R4.5）

    public static async Task Replace(MainWindow window, long meetingId, string initial)
    {
        var host = AppHost.Current;
        var find = new TextBox { Header = L.T("錯誤寫法"), Text = initial };
        var replacement = new TextBox { Header = L.T("正確寫法") };
        var glossary = new CheckBox { Content = L.T("把正確寫法加進詞彙表，之後轉錄比較容易一次轉對"), IsChecked = true };
        var result = Ui.Text("", 13, color: Ui.Celadon);
        var form = Ui.Column(12, Ui.Text(L.T("把逐字稿裡的錯字換成正確寫法，會議紀錄裡同樣的字也會一起改。區分大小寫。"), 13, color: Ui.Secondary), find, replacement, glossary, result);
        form.Width = 400;
        var dialog = Dialog(window, L.T("用詞修正"), form, L.T("全部取代"), L.T("完成"));
        dialog.PrimaryButtonClick += (_, e) =>
        {
            // 留在對話框裡顯示結果
            e.Cancel = true;
            if (find.Text.Length == 0 || find.Text == replacement.Text) return;
            var r = host.Controller.Replace(meetingId, find.Text, replacement.Text, glossary.IsChecked == true);
            if (r is null) return;
            if (r.SegmentsChanged == 0 && !r.NotesChanged) { result.Text = L.F("沒有找到「%@」。", find.Text); return; }
            var parts = new List<string>();
            if (r.SegmentsChanged > 0) parts.Add(L.F("逐字稿改了 %lld 段", r.SegmentsChanged));
            if (r.NotesChanged) parts.Add(L.T("會議紀錄也一起改了（可以從「更多」復原上一版）"));
            result.Text = string.Join(L.T("，"), parts) + L.T("。");
        };
        await Show(dialog);
    }
}

/// <summary>
/// Windows 的分享視窗（R7.5、design-windows §12.6）：DataTransferManager 要用視窗代號取得（IDataTransferManagerInterop）
/// </summary>
public static class Share
{
    [ComImport, Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDataTransferManagerInterop
    {
        IntPtr GetForWindow([In] IntPtr appWindow, [In] ref Guid riid);
        void ShowShareUIForWindow(IntPtr appWindow);
    }

    static readonly Guid DataTransferManagerId = new("a5caee9b-8708-49d1-8d36-67d25a8da00c");

    public static async Task File(MainWindow window, string path, string title)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        var interop = DataTransferManager.As<IDataTransferManagerInterop>();
        var id = DataTransferManagerId;
        var manager = WinRT.MarshalInterface<DataTransferManager>.FromAbi(interop.GetForWindow(window.Handle, ref id));
        void Requested(DataTransferManager sender, DataRequestedEventArgs args)
        {
            args.Request.Data.Properties.Title = title;
            args.Request.Data.SetStorageItems([file]);
            sender.DataRequested -= Requested;
        }
        manager.DataRequested += Requested;
        interop.ShowShareUIForWindow(window.Handle);
    }
}
