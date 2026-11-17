using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
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

/// <summary>
/// 對話框（ContentDialog）：匯入、匯出、重新辨識說話者、編輯會議資訊、用詞修正、確認、錯誤（重新命名在 RenameDialog.cs）。
/// 錯誤、錄音的提醒（Alert）照舊是系統的樣子（同 Mac 的 alert），其他照 Sheet 的樣子（R106、design-windows §11.39）
/// </summary>
public static partial class Dialogs
{
    /// <summary>DEBUG 的 state（sheet）：開著的、照 Sheet 樣子的對話框的標題（沒開是 null）</summary>
    public static string? OpenSheet { get; internal set; }

    public static async Task Alert(MainWindow window, string title, string message)
    {
        if (window.XamlRoot is null) return;
        var dialog = new ContentDialog
        {
            XamlRoot = window.XamlRoot, Title = Ui.DialogTitle(title), Content = Ui.Text(message, select: true), CloseButtonText = L.T("好"), DefaultButton = ContentDialogButton.Close,
        };
        await Show(dialog);
    }

    /// <summary>
    /// 確認（R106.2，同 Mac 的 ConfirmSheet）：標題、說明、右上角 ✕；右下「取消」、確認的按鈕（destructive：刪除、移除這類，紅底白字；其他是主要按鈕）。
    /// Enter 是確認（打開時焦點在確認的按鈕）、Esc／✕／取消是取消
    /// </summary>
    public static async Task<bool> Confirm(MainWindow window, string title, string message, string action, bool destructive = false)
    {
        if (window.XamlRoot is null) return false;
        var sheet = new SheetDialog(window, title, spacing: 22);
        var confirmed = false;
        var ok = Sheet.Pill(action, destructive ? Sheet.Kind.Destructive : Sheet.Kind.Primary);
        void Accept()
        {
            confirmed = true;
            sheet.Close();
        }
        ok.Click += (_, _) => Accept();
        sheet.Form.Children.Add(Bullets(message) is { } list
            ? Ui.Column(10, Sheet.Header(title, sheet.Cancel), list)
            : Sheet.Header(title, sheet.Cancel, Sheet.Subtitle(message)));
        sheet.Form.Children.Add(Sheet.Buttons(null, Sheet.Pill(L.T("取消"), click: sheet.Cancel), ok));
        sheet.Submit = Accept;
        sheet.Primary = ok;
        sheet.Focus = ok;
        await sheet.Show();
        return confirmed;
    }

    /// <summary>
    /// 說明是「・」開頭的好幾行時畫成條列，換行對齊在點的後面（編輯會議的套用、還原，M§81.5、同 Mac 的 ConfirmSheet）；不是條列回傳 null
    /// </summary>
    static StackPanel? Bullets(string message)
    {
        var lines = message.Split('\n');
        if (lines.Length < 2 || !lines.All(l => l.StartsWith('・'))) return null;  // l10n-skip
        var list = new StackPanel { Spacing = 6 };
        foreach (var line in lines)
        {
            var dot = Ui.Text("・", 14, color: Ui.Secondary, wrap: false);  // l10n-skip
            var text = Ui.Text(line[1..], 14, color: Ui.Secondary);
            list.Children.Add(Ui.Split(dot, text, 2, GridLength.Auto));
        }
        AutomationProperties.SetName(list, message);
        return list;
    }

    /// <summary>同時只能開一個 ContentDialog：前一個還開著時等它關掉</summary>
    static readonly SemaphoreSlim Gate = new(1, 1);

    internal static async Task<ContentDialogResult> Show(ContentDialog dialog)
    {
        await Gate.WaitAsync();
        Ui.InterfaceLanguage(dialog);
        try { return await dialog.ShowAsync(); }
        finally { Gate.Release(); }
    }

    /// <summary>「大約幾個人」的選項：自動判斷、2～12 人</summary>
    static IEnumerable<(int, string)> SpeakerCounts() => new[] { (0, L.T("自動判斷")) }.Concat(Enumerable.Range(2, 11).Select(n => (n, L.F("%lld 人", n))));

    // MARK: 匯入（R1.2）

    /// <summary>DEBUG 的 state（showImportSheet，同 Mac）：匯入的對話框開著沒</summary>
    public static bool ImportOpen { get; private set; }

    /// <param name="folder">匯入到哪個資料夾：從檔案總管拖到側欄的資料夾上時是那個資料夾（R103.3），不然是目前看的資料夾</param>
    public static async Task Import(MainWindow window, IReadOnlyList<string> files, string? folder = null)
    {
        if (window.XamlRoot is null) return;
        var host = AppHost.Current;
        var single = files.Count == 1;
        var heading = single ? L.T("匯入錄音") : L.F("匯入 %lld 個錄音檔", files.Count);
        var sheet = new SheetDialog(window, heading, Sheet.WideWidth, 16);
        var title = Sheet.TextField("", L.T("選填，沒填就由整理時命名"));
        var inferred = DateText.Date(ImportRules.InferDate(Path.GetFileName(files[0]), Importer.ModificationDate(files[0]), Importer.Today)) ?? Importer.Today;
        var date = Sheet.Date(new CalendarDatePicker { Date = new DateTimeOffset(inferred.ToDateTime(TimeOnly.MinValue)) });
        var participants = Sheet.TextField("", L.T("選填，用逗號分隔"));
        var templates = window.State.Templates;
        var template = Sheet.Combo(Ui.Combo(templates.Select(t => (t.Id, t.DisplayName())), host.Settings.DefaultTemplate, _ => { }));
        var language = Sheet.Combo(Ui.Combo(MeetingLanguages.All.Select(l => (l, l.Name())), host.Settings.DefaultMeetingLanguage, _ => { }));
        ToolTipService.SetToolTip(language, L.T("決定轉錄和會議紀錄用的語言"));
        var count = Sheet.Combo(Ui.Combo(SpeakerCounts(), 0, _ => { }));
        ToolTipService.SetToolTip(count, L.T("人多、吵雜或手機放在桌上錄音時，先告訴拾音大約幾個人，說話者會分得比較準"));
        // 檔名：灰字、前面一個聲音的圖示（同 Mac 的 Label(_, systemImage: "waveform")）
        var names = Ui.Column(4, files.Select(f =>
        {
            var icon = new FontIcon { Glyph = "", FontSize = 13, Foreground = Ui.Secondary, VerticalAlignment = VerticalAlignment.Center };
            var name = Ui.Text(Path.GetFileName(f), 14, color: Ui.Secondary, wrap: false);
            return (UIElement)Ui.Split(icon, name, 8);
        }).ToArray());
        var imported = false;
        var primary = Sheet.Pill(L.T("匯入並開始轉錄"), Sheet.Kind.Primary);
        void Accept()
        {
            imported = true;
            sheet.Close();
        }
        primary.Click += (_, _) => Accept();
        var form = sheet.Form;
        form.Children.Add(Sheet.Header(heading, sheet.Cancel));
        form.Children.Add(names);
        if (single) form.Children.Add(Sheet.Labeled(L.T("標題"), title));
        // 好幾個檔：日期依各檔案（同 Mac：同一種框裡一行灰字）
        var dateField = single ? (FrameworkElement)date : Sheet.Box(Ui.Text(L.T("依各檔案的檔名或修改日期"), 15, color: Ui.Secondary, wrap: false));
        form.Children.Add(Sheet.Pair(Sheet.Labeled(L.T("會議日期"), dateField), Sheet.Labeled(L.T("大約幾個人"), count)));
        form.Children.Add(Sheet.Labeled(L.T("與會者"), participants));
        form.Children.Add(Sheet.Pair(Sheet.Labeled(L.T("範本"), template), Sheet.Labeled(L.T("會議語言"), language)));
        form.Children.Add(Sheet.Buttons(null, Sheet.Pill(L.T("取消"), click: sheet.Cancel), primary));
        sheet.Submit = Accept;
        sheet.Primary = primary;
        sheet.Focus = single ? title : participants;
        ImportOpen = true;
        try { await sheet.Show(); }
        finally { ImportOpen = false; }
        if (!imported) return;
        var options = new ImportOptions
        {
            Title = title.Text,
            Date = single && date.Date is { } d ? DateText.String(DateOnly.FromDateTime(d.Date)) : null,
            Participants = participants.Text,
            TemplateId = templates.ElementAtOrDefault(template.SelectedIndex)?.Id,
            Language = MeetingLanguages.All[Math.Max(0, language.SelectedIndex)],
            MinSpeakers = count.SelectedIndex <= 0 ? null : count.SelectedIndex + 1,
        };
        await host.Controller.ConfirmImport(files, options, folder ?? window.State.CurrentFolder);
    }

    // MARK: 匯出（R7）

    static string FormatLabel(string format) => format switch
    {
        "md" => "Markdown",
        "pdf" => "PDF",
        "png" => L.T("PNG 圖片"),
        "svg" => "SVG",
        "docx" => L.T("Word（docx）"),
        "txt" => L.T("純文字（txt）"),
        "srt" => L.T("字幕（srt）"),
        "vtt" => L.T("字幕（vtt）"),
        "shiyin" => L.T(".shiyin 封包"),
        _ => format,
    };

    /// <summary>
    /// 匯出對話框：打開時就決定是哪一場會議（M§20.9）；存檔或分享（R7.5）。同 Mac 的 ExportSheet：項目、格式兩欄，左下「分享…」（次要），
    /// 右下「取消」「存檔…」（主要、Enter）
    /// </summary>
    public static async Task Export(MainWindow window, long meetingId)
    {
        if (window.XamlRoot is null) return;
        var host = AppHost.Current;
        ExportBundle bundle;
        try { bundle = host.Controller.Bundle(meetingId); }
        catch (AppController.ExportException e) { window.ShowError(e.Message); return; }
        var hasNotes = bundle.Notes is not null;
        var hasAudio = host.Paths.HasAudio(bundle.Meeting);
        // 還沒有會議紀錄（整理中或整理失敗）時，預設改匯出逐字稿
        var item = hasNotes ? ExportItem.Notes : ExportItem.Transcript;
        var format = item.Formats()[0];
        var hasMindMap = bundle.Notes is { IsEmpty: false };
        var items = Enum.GetValues<ExportItem>().Where(i => (i != ExportItem.Audio || hasAudio) && (i != ExportItem.MindMap || hasMindMap)).ToList();
        var itemBox = Sheet.Combo(Ui.Combo(items.Select(i => (i, i.Label())), item, _ => { }));
        var formatBox = Sheet.Combo(new ComboBox());
        var times = new CheckBox { Content = L.T("包含時間點"), IsChecked = true };
        var speakers = new CheckBox { Content = L.T("包含說話者"), IsChecked = true };
        var checks = Ui.Row(18, times, speakers);
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
            checks.Visibility = item == ExportItem.Transcript ? Visibility.Visible : Visibility.Collapsed;
            times.IsEnabled = format is not ("srt" or "vtt");
            hint.Text = item switch
            {
                ExportItem.Notes when !hasNotes => L.T("這場會議還沒有會議紀錄（還在整理或整理失敗），匯出的會議紀錄只會有標題和基本資料。要留下內容，請改匯出逐字稿。"),
                ExportItem.Notes => L.T("PDF 和 docx 的段落標題使用明體；Markdown 適合貼到 Notion、GitHub。"),
                ExportItem.MindMap => L.T("依會議紀錄畫成心智圖。PNG 適合貼到聊天軟體、簡報；SVG 可以再用其他軟體編輯。"),
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
        var heading = L.F("匯出「%@」", bundle.Meeting.DisplayTitle);
        var sheet = new SheetDialog(window, heading, Sheet.WideWidth, 16);
        var action = 0;   // 1 存檔、2 分享
        var save = Sheet.Pill(L.T("存檔…"), Sheet.Kind.Primary, click: () => { action = 1; sheet.Close(); });
        var share = Sheet.Pill(L.T("分享…"), click: () => { action = 2; sheet.Close(); });
        sheet.Form.Children.Add(Sheet.Header(heading, sheet.Cancel));
        sheet.Form.Children.Add(Sheet.Pair(Sheet.Labeled(L.T("項目"), itemBox), Sheet.Labeled(L.T("格式"), formatBox)));
        sheet.Form.Children.Add(checks);
        sheet.Form.Children.Add(hint);
        sheet.Form.Children.Add(Sheet.Buttons(share, Sheet.Pill(L.T("取消"), click: sheet.Cancel), save));
        sheet.Submit = () => { action = 1; sheet.Close(); };
        sheet.Primary = save;
        // 焦點在「存檔…」：Enter 直接存檔（同 Mac 的 .defaultAction）；選單式的欄位有焦點時 Enter 是打開它
        sheet.Focus = save;
        await sheet.Show();
        if (action == 0) return;
        var filename = AppController.SuggestedFilename(item, format, bundle.Meeting);
        var withTimes = times.IsChecked == true;
        var withSpeakers = speakers.IsChecked == true;
        try
        {
            if (action == 1)
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
        if (window.XamlRoot is null) return;
        var host = AppHost.Current;
        var current = host.Db.Meeting(meetingId)?.MinSpeakers ?? 0;
        var count = Sheet.Combo(Ui.Combo(SpeakerCounts(), current, _ => { }));
        count.HorizontalAlignment = HorizontalAlignment.Left;
        count.Width = 220;
        var heading = L.T("重新辨識說話者");
        var sheet = new SheetDialog(window, heading);
        var start = false;
        var primary = Sheet.Pill(L.T("開始"), Sheet.Kind.Primary, click: () => { start = true; sheet.Close(); });
        sheet.Form.Children.Add(Sheet.Header(heading, sheet.Cancel,
            Sheet.Subtitle(L.T("只重新分辨誰在說話，逐字稿的文字不會變。人多、吵雜或手機遠距收音時，告訴拾音大約幾個人會分得比較準。"))));
        sheet.Form.Children.Add(Sheet.Labeled(L.T("大約幾個人"), count));
        sheet.Form.Children.Add(Ui.Text(L.T("原本取過的名字會盡量保留，其他說話者會用聲紋比對、再用整理工具猜名字。會議紀錄不會自動更新。"), 13, color: Ui.Secondary));
        sheet.Form.Children.Add(Sheet.Buttons(null, Sheet.Pill(L.T("取消"), click: sheet.Cancel), primary));
        sheet.Submit = () => { start = true; sheet.Close(); };
        sheet.Primary = primary;
        // 焦點在「開始」：Enter 直接開始；選單式的欄位有焦點時 Enter 是打開它
        sheet.Focus = primary;
        await sheet.Show();
        if (start) host.Controller.Rediarize(meetingId, count.SelectedIndex <= 0 ? null : count.SelectedIndex + 1);
    }

    // MARK: 拆成兩句（R70、M§46.3）

    /// <summary>DEBUG 的 splitcaret 用：開著的拆句對話框的文字框</summary>
    internal static TextBox? SplitBox;

    /// <summary>前半句、後半句（去掉頭尾的空白）；任一邊沒有字是 null（R70.2，同 AppDatabase.SplitSegment）</summary>
    static (string Head, string Tail)? SplitParts(string text, int at)
    {
        if (at <= 0 || at >= text.Length) return null;
        char[] blanks = [' ', '\t', '\n', '\r', '　'];
        var head = text[..at].Trim(blanks);
        var tail = text[at..].Trim(blanks);
        return head.Length == 0 || tail.Length == 0 ? null : (head, tail);
    }

    /// <summary>右鍵「拆成兩句…」：唯讀的文字框放游標（SelectionStart 是 UTF-16 的位置，同 Mac），選後半句的說話者。
    /// 預設是下一句的說話者；下一句是同一位時用上一句的；都是同一位時新的說話者。
    /// Windows 才有的對話框（Mac 是就地拆），樣子照 Sheet（R106.4）：文字放進同一種框（有焦點時框變亮）</summary>
    public static async Task SplitSegment(MainWindow window, MeetingDetail detail, Segment segment)
    {
        if (window.XamlRoot is null || segment.Id is not long segmentId || detail.Meeting.Id is not long meetingId) return;
        var index = detail.Segments.FindIndex(s => s.Id == segmentId);
        var next = index >= 0 && index + 1 < detail.Segments.Count ? detail.Segments[index + 1].Speaker : null;
        var previous = index > 0 ? detail.Segments[index - 1].Speaker : null;
        var target = new[] { next, previous }.FirstOrDefault(s => s is not null && s != segment.Speaker);
        var options = detail.Speakers.Keys.Order()
            .Select(idx => ((int?)idx, idx == segment.Speaker ? L.F("%@（同一位）", detail.SpeakerName(idx) ?? "") : detail.SpeakerName(idx) ?? ""))
            .Append(((int?)null, L.T("新的說話者"))).ToList();
        var speaker = Sheet.Combo(Ui.Combo(options, target, v => target = v));
        var box = Sheet.Field(new TextBox { Text = segment.Text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap });
        AutomationProperties.SetName(box, L.T("要拆開的句子"));
        var preview = Ui.Text("", 12, color: Ui.Secondary);
        preview.Visibility = Visibility.Collapsed;
        var heading = L.T("拆成兩句");
        var sheet = new SheetDialog(window, heading, Sheet.WideWidth, 14);
        var split = false;
        var primary = Sheet.Pill(L.T("拆開"), Sheet.Kind.Primary, click: () => { split = true; sheet.Close(); });
        primary.IsEnabled = false;
        box.SelectionChanged += (_, _) =>
        {
            var parts = SplitParts(segment.Text, box.SelectionStart);
            preview.Text = parts is { } p ? L.F("前半句：…%@｜後半句：%@…", p.Head[^Math.Min(8, p.Head.Length)..], p.Tail[..Math.Min(8, p.Tail.Length)]) : "";
            preview.Visibility = preview.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            primary.IsEnabled = parts is not null;
        };
        sheet.Form.Children.Add(Sheet.Header(heading, sheet.Cancel, Sheet.Subtitle(L.T("在文字裡點一下要拆開的地方"))));
        sheet.Form.Children.Add(box);
        sheet.Form.Children.Add(preview);
        sheet.Form.Children.Add(Sheet.Labeled(L.T("後半句是"), speaker));
        sheet.Form.Children.Add(Sheet.Buttons(null, Sheet.Pill(L.T("取消"), click: sheet.Cancel), primary));
        sheet.Submit = () => { split = true; sheet.Close(); };
        sheet.Primary = primary;
        sheet.Focus = box;
        sheet.SelectOnFocus = false;
        SplitBox = box;
        try { await sheet.Show(); }
        finally { SplitBox = null; }
        if (split) AppHost.Current.Controller.SplitSegment(meetingId, segmentId, box.SelectionStart, target);
    }

    // MARK: 會議資訊

    /// <summary>會議頁標題旁的「編輯」（同 Mac 的 EditInfoForm，R106.3）：標題（打開時全選）、會議日期、與會者</summary>
    public static async Task EditInfo(MainWindow window, Meeting meeting)
    {
        if (window.XamlRoot is null) return;
        var title = Sheet.TextField(meeting.DisplayTitle, L.T("標題"));
        var date = Sheet.Date(new CalendarDatePicker());
        if (DateText.Date(meeting.MeetingDate) is { } d) date.Date = new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue));
        var participants = Sheet.TextField(meeting.Participants, L.T("用逗號分隔"));
        var heading = L.T("編輯會議資訊");
        var sheet = new SheetDialog(window, heading);
        var saved = false;
        var primary = Sheet.Pill(L.T("儲存"), Sheet.Kind.Primary, click: () => { saved = true; sheet.Close(); });
        sheet.Form.Children.Add(Sheet.Header(heading, sheet.Cancel, Sheet.Subtitle(L.T("標題、日期、與會者。"))));
        sheet.Form.Children.Add(Sheet.Labeled(L.T("標題"), title));
        sheet.Form.Children.Add(Sheet.Labeled(L.T("會議日期"), date));
        sheet.Form.Children.Add(Sheet.Labeled(L.T("與會者"), participants));
        sheet.Form.Children.Add(Sheet.Buttons(null, Sheet.Pill(L.T("取消"), click: sheet.Cancel), primary));
        sheet.Submit = () => { saved = true; sheet.Close(); };
        sheet.Primary = primary;
        sheet.Focus = title;
        await sheet.Show();
        if (!saved) return;
        // 標題欄預先填的是顯示用的標題（還沒命名時是檔名）；沒動過就照原樣存，不然會被當成自訂標題鎖住，整理時不會再取名
        var edited = title.Text.Trim();
        var dateText = date.Date is { } picked ? DateText.String(DateOnly.FromDateTime(picked.Date)) : meeting.MeetingDate;
        AppHost.Current.Controller.UpdateInfo(meeting.Id!.Value, edited == meeting.DisplayTitle ? meeting.Title : edited, dateText, participants.Text);
    }

    // MARK: 用詞修正（R4.5）

    /// <summary>同 Mac 的 ReplaceSheet：錯誤寫法、正確寫法兩欄；「全部取代」（Enter）留在對話框裡顯示結果，之後「取消」換成「完成」</summary>
    public static async Task Replace(MainWindow window, long meetingId, string initial)
    {
        if (window.XamlRoot is null) return;
        var host = AppHost.Current;
        var find = Sheet.TextField(initial, L.T("錯誤寫法"));
        var replacement = Sheet.TextField("", L.T("正確寫法"));
        var glossary = new CheckBox { Content = L.T("把正確寫法加進詞彙表，之後轉錄比較容易一次轉對"), IsChecked = true };
        var result = Ui.Text("", 13, color: Ui.Celadon);
        result.Visibility = Visibility.Collapsed;
        var heading = L.T("用詞修正");
        var sheet = new SheetDialog(window, heading, Sheet.WideWidth, 16);
        var close = Sheet.Pill(L.T("取消"), click: sheet.Cancel);
        var primary = Sheet.Pill(L.T("全部取代"), Sheet.Kind.Primary);
        void Update() => primary.IsEnabled = find.Text.Length > 0 && find.Text != replacement.Text;
        void Run()
        {
            if (!primary.IsEnabled) return;
            var r = host.Controller.Replace(meetingId, find.Text, replacement.Text, glossary.IsChecked == true);
            if (r is null) return;
            close.Content = L.T("完成");
            result.Visibility = Visibility.Visible;
            if (r.SegmentsChanged == 0 && !r.NotesChanged) { result.Text = L.F("沒有找到「%@」。", find.Text); return; }
            var parts = new List<string>();
            if (r.SegmentsChanged > 0) parts.Add(L.F("逐字稿改了 %lld 段", r.SegmentsChanged));
            if (r.NotesChanged) parts.Add(L.T("會議紀錄也一起改了（按「編輯」旁的「復原上一版」可以還原）"));
            result.Text = string.Join(L.T("，"), parts) + L.T("。");
        }
        primary.Click += (_, _) => Run();
        find.TextChanged += (_, _) => Update();
        replacement.TextChanged += (_, _) => Update();
        Update();
        sheet.Form.Children.Add(Sheet.Header(heading, sheet.Cancel,
            Sheet.Subtitle(L.T("把逐字稿裡的錯字換成正確寫法，會議紀錄裡同樣的字也會一起改。區分大小寫。"))));
        sheet.Form.Children.Add(Sheet.Pair(Sheet.Labeled(L.T("錯誤寫法"), find), Sheet.Labeled(L.T("正確寫法"), replacement)));
        sheet.Form.Children.Add(glossary);
        sheet.Form.Children.Add(result);
        sheet.Form.Children.Add(Sheet.Buttons(null, close, primary));
        sheet.Submit = Run;
        sheet.Primary = primary;
        sheet.Focus = find;
        await sheet.Show();
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
