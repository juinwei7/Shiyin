using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Shiyin.App.Views;
using Shiyin.Localization;
using Shiyin.Model;
using Shiyin.Services;
using Shiyin.Services.Capture;
using Windows.UI;

namespace Shiyin.App;

/// <summary>
/// 錄音島（R81.9、M§57，design-windows §11.21）：錄音中、偵測到會議時，前景視窗那個螢幕的上緣正中間的小視窗。
/// 收起是 160×32 的膠囊（紅點、時間）；提醒、偵測會議、滑鼠移上去時往下展開（寬 440）。
/// 一律置頂、不出現在工作列、按了不搶焦點（WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE）、分享畫面拍不到（WDA_EXCLUDEFROMCAPTURE）
/// </summary>
public sealed partial class IslandWindow : Window
{
    readonly AppHost host;
    /// <summary>內容放在 Canvas 裡量出自然的大小，視窗跟著內容（收起時只佔一小塊，不擋下面視窗的點擊，R81.6）</summary>
    readonly Canvas canvas = new();
    readonly Border card = new() { Background = new SolidColorBrush(Colors.Black) };
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly DispatcherTimer leave = new() { Interval = TimeSpan.FromMilliseconds(300) };
    readonly IntPtr hwnd;
    bool styled;

    public const double CollapsedWidth = 160, CollapsedHeight = 32, ExpandedWidth = 440;

    // 狀態（M§57.2）
    public bool Hover { get; private set; }
    public sealed record PromptAction(string Id, string Title, Action Run);
    public sealed record PromptState(MeetingDetector.Prompt Prompt, string Message, List<PromptAction> Actions, DateTime Expires);
    public PromptState? Prompt { get; private set; }
    /// <summary>目前的提醒是哪一個；換了就展開 10 秒（R81.2），之後收回成琥珀色的點</summary>
    public string? HintKey { get; private set; }
    DateTime expandedUntil = DateTime.MinValue;
    bool hintExpanded;
    bool bookmarked;
    string? shownKey;
    TextBlock? clock;
    /// <summary>滑過去時最新一句即時逐字稿：一直在變，只改文字不重建（重建時按到一半的按鈕會按不到）</summary>
    TextBlock? latest;
    (int X, int Y, int W, int H) placed;

    public IslandWindow(AppHost host)
    {
        this.host = host;
        Title = L.T("拾音錄音島");
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        // 黑底白字，深色、淺色模式都一樣（同 Mac，M§57.1）
        canvas.RequestedTheme = ElementTheme.Dark;
        canvas.Background = new SolidColorBrush(Colors.Black);
        canvas.Children.Add(card);
        Content = Ui.InterfaceLanguage(canvas);
        card.SizeChanged += (_, _) => Ui.Guard(Place);
        canvas.PointerEntered += (_, _) => Ui.Guard(() =>
        {
            leave.Stop();
            if (Hover) return;
            Hover = true;
            Update();
        });
        // 滑出去 0.3 秒後才收回（滑過邊界不會閃）
        canvas.PointerExited += (_, _) => { leave.Stop(); leave.Start(); };
        leave.Tick += (_, _) => Ui.Guard(() =>
        {
            leave.Stop();
            if (!Hover) return;
            Hover = false;
            Update();
        });
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }
        AppWindow.IsShownInSwitchers = false;
        // 每 0.5 秒看要不要出現、提醒換了沒、偵測會議的提醒過期沒、前景視窗換到別的螢幕沒（便宜：只比對幾個值）
        timer.Tick += (_, _) => Ui.Guard(Update);
        timer.Start();
        host.Recorder.Changed += () => host.Post(Update);
        // 開始錄音（不管從哪裡）時，偵測會議的「要開始錄音嗎？」收起來
        host.Recorder.Started += () => host.Post(() => { if (Prompt?.Prompt is MeetingDetector.Prompt.Start) ClosePrompt(); });
        host.Settings.Changed += key => { if (key == AppSettings.RecordingIslandKey) host.Post(Update); };
    }

    /// <summary>設定「在螢幕上方顯示錄音島」（R81.8）</summary>
    public bool Enabled => host.Settings.RecordingIsland;

    // MARK: 偵測會議的提醒（R81.3）

    /// <summary>「&lt;app&gt; 正在開會，要開始錄音嗎？」、「會議好像結束了，要停止錄音嗎？」：按鈕同右下角的小視窗，30 秒沒按就收起來</summary>
    public void ShowPrompt(MeetingDetector.Prompt prompt)
    {
        List<PromptAction> actions;
        string message;
        if (prompt is MeetingDetector.Prompt.Start)
        {
            message = L.F("%@ 正在開會，要開始錄音嗎？", prompt.App.Name);
            actions =
            [
                new("later", L.T("這次不用"), () => { }),
                new("mute", L.T("不要再提醒這個 app"), () => host.Detector.Mute(prompt.App.Id)),
                new("start", L.T("開始錄音"), () => host.Controller.StartRecording("")),
            ];
        }
        else
        {
            message = L.T("會議好像結束了，要停止錄音嗎？");
            actions = [new("continue", L.T("繼續錄"), () => { }), new("stop", L.T("停止並轉錄"), host.Recorder.Stop)];
        }
        Prompt = new PromptState(prompt, message, actions, DateTime.UtcNow.AddSeconds(30));
        Update();
    }

    /// <summary>按島上偵測會議的按鈕（DEBUG 的 meetingaction 也走這裡）</summary>
    public void Perform(string id)
    {
        if (Prompt?.Actions.FirstOrDefault(a => a.Id == id) is not { } action) return;
        ClosePrompt();
        action.Run();
    }

    public void ClosePrompt()
    {
        Prompt = null;
        Update();
    }

    /// <summary>DEBUG 的 island hover on|off：等於滑鼠移上去、移開</summary>
    public void SetHover(bool on)
    {
        leave.Stop();
        Hover = on;
        Update();
    }

    // MARK: 出現、內容

    public string Mode
    {
        get
        {
            if (!AppWindow.IsVisible) return "hidden";
            if (Prompt is not null) return "prompt";
            if (Hover && host.Recorder.IsRecording) return "hover";
            return hintExpanded ? "hint" : "collapsed";
        }
    }

    public void Update()
    {
        var recorder = host.Recorder;
        var now = DateTime.UtcNow;
        if (Prompt is { } p && p.Expires < now) Prompt = null;
        var key = HintKeyOf(recorder);
        if (key != HintKey)
        {
            HintKey = key;
            if (key is not null) expandedUntil = now.AddSeconds(10);
        }
        hintExpanded = key is not null && now < expandedUntil;
        var show = Enabled && (recorder.IsRecording || Prompt is not null);
        if (!show)
        {
            if (AppWindow.IsVisible) AppWindow.Hide();
            Hover = false;
            shownKey = null;
            return;
        }
        Render();
        if (!AppWindow.IsVisible)
        {
            ApplyStyles();
            Place();
            AppWindow.Show(activateWindow: false);
        }
        else Place();
    }

    /// <summary>目前的提醒是哪一個（順序同錄音畫面的提示列）：沒人說話 → 藍牙 → 收音 → 重新連接</summary>
    static string? HintKeyOf(Recorder recorder)
    {
        if (!recorder.IsRecording) return null;
        if (recorder.IdlePrompt) return "idle";
        if (recorder.MicHint is { } mic) return "mic:" + mic.Name;
        if (recorder.Hint is { } hint) return "level:" + hint;
        if (recorder.DeviceWarning is { } warning) return "device:" + warning;
        return null;
    }

    /// <summary>內容換了才重建（計時只改那個 TextBlock）</summary>
    void Render()
    {
        var recorder = host.Recorder;
        var mode = Prompt is not null ? "prompt" : Hover && recorder.IsRecording ? "hover" : hintExpanded ? "hint" : "collapsed";
        var line = mode == "hover" ? LatestLine(recorder) : null;
        var key = string.Join('\u001F', mode, HintKey, Prompt?.Message, recorder.IsPaused, bookmarked, recorder.HasMic, recorder.HasSystem,
            recorder.MicHint?.BuiltIn?.Name, line is not null);
        if (key == shownKey)
        {
            if (clock is not null) clock.Text = TimeFormat.String(recorder.ElapsedSeconds);
            if (latest is not null && line is not null && latest.Text != line) latest.Text = line;
            return;
        }
        shownKey = key;
        latest = null;
        UIElement content = mode switch
        {
            "prompt" => Expanded(PromptHeader(), PromptBody(Prompt!)),
            "hover" => Expanded(RecordingHeader(), Controls()),
            "hint" when CurrentHint() is { } hint => Expanded(RecordingHeader(), HintBody(hint)),
            _ => Collapsed(),
        };
        card.Child = content;
    }

    UIElement Collapsed()
    {
        card.Width = CollapsedWidth;
        card.Height = CollapsedHeight;
        card.Padding = new Thickness(0);
        var row = Ui.Row(8, StatusDot(), Elapsed());
        row.HorizontalAlignment = HorizontalAlignment.Center;
        row.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(card, host.Recorder.IsPaused ? L.T("已暫停") : L.T("錄音中"));
        return row;
    }

    UIElement Expanded(FrameworkElement header, FrameworkElement body)
    {
        card.Width = ExpandedWidth;
        card.Height = double.NaN;
        card.Padding = new Thickness(18, 4, 18, 16);
        AutomationProperties.SetName(card, L.T("拾音錄音島"));
        return Ui.Column(8, header, body);
    }

    /// <summary>紅點、時間（展開時的第一列）</summary>
    FrameworkElement RecordingHeader()
    {
        var row = Ui.Row(8, StatusDot(), Elapsed());
        row.Height = 30;
        return row;
    }

    FrameworkElement PromptHeader()
    {
        clock = null;
        var row = Ui.Row(8, new FontIcon { Glyph = "\uE720", FontSize = 14, Foreground = Brush(0x6DB2A1) });
        row.Height = 30;
        return row;
    }

    /// <summary>紅點；暫停時是暫停的圖示；有提醒（收回之後）是琥珀色的點</summary>
    FrameworkElement StatusDot()
    {
        FrameworkElement dot = host.Recorder.IsPaused
            ? new FontIcon { Glyph = "\uE769", FontSize = 10, Foreground = new SolidColorBrush(Color.FromArgb(204, 255, 255, 255)) }
            : new Ellipse { Width = 8, Height = 8, Fill = HintKey is not null ? Brush(0xE2AD3B) : Brush(0xFF453A) };
        dot.VerticalAlignment = VerticalAlignment.Center;
        return dot;
    }

    TextBlock Elapsed()
    {
        clock = Ui.Text(TimeFormat.String(host.Recorder.ElapsedSeconds), 12, bold: true, color: new SolidColorBrush(Colors.White), wrap: false);
        clock.VerticalAlignment = VerticalAlignment.Center;
        return clock;
    }

    /// <summary>滑過去（R81.5）：音量、最新一句即時逐字稿、有提醒時那一句，按鈕</summary>
    FrameworkElement Controls()
    {
        var recorder = host.Recorder;
        var column = Ui.Column(12);
        var colors = ((Brush)new SolidColorBrush(Color.FromArgb(46, 255, 255, 255)), (Brush)Brush(0x6DB2A1), (Brush)Brush(0xE2AD3B));
        var meters = Ui.Row(14);
        if (recorder.HasMic) meters.Children.Add(Meter("\uE720", () => recorder.MicLevel, colors, L.T("麥克風")));
        if (recorder.HasSystem) meters.Children.Add(Meter("\uE767", () => recorder.SystemLevel, colors, L.T("電腦聲音")));
        column.Children.Add(meters);
        if (LatestLine(recorder) is { } line)
        {
            latest = Ui.Text(line, 13, color: Secondary, maxLines: 2);
            latest.Language = Ui.ContentLanguage(recorder.Language);
            column.Children.Add(latest);
        }
        if (CurrentHint() is { } hint)
        {
            column.Children.Add(Ui.Split(Icon(hint.Glyph, 0xE2AD3B), Ui.Text(hint.Text, 13, color: Brush(0xE2AD3B), maxLines: 3), 8));
            // 提醒的按鈕也放在這裡：滑鼠移到展開的提醒上就換成這個畫面，不放的話「知道了」「繼續錄」永遠按不到
            if (hint.Buttons.Count > 0) column.Children.Add(HintButtons(hint));
        }
        var bookmark = IslandButton(bookmarked ? L.T("已標記") : L.T("標記重點"), bookmarked ? "\uE735" : "\uE734", Role.Normal, () =>
        {
            if (!host.Controller.BookmarkRecording()) return;
            bookmarked = true;
            Update();
            var reset = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            reset.Tick += (_, _) => Ui.Guard(() => { reset.Stop(); bookmarked = false; Update(); });
            reset.Start();
        });
        var left = Ui.Row(8,
            IslandButton(recorder.IsPaused ? L.T("繼續") : L.T("暫停"), recorder.IsPaused ? "\uE768" : "\uE769", Role.Normal,
                () => { if (recorder.IsPaused) recorder.Resume(); else recorder.Pause(); }),
            bookmark,
            IslandButton(L.T("打開拾音"), "\uE8A7", Role.Normal, () => OpenShiyin(recorder.MeetingId)));
        var stop = IslandButton(L.T("停止並轉錄"), "\uE71A", Role.Destructive, recorder.Stop);
        column.Children.Add(Ui.Bar(left, null, stop));
        return column;
    }

    /// <summary>
    /// 「打開拾音」（R81.5）：打開錄音中的會議、主視窗拉到最前面。島按了不搶焦點，Activate 只會讓工作列閃：
    /// 剛收到點擊的是拾音，可以自己 SetForegroundWindow
    /// </summary>
    void OpenShiyin(long? meetingId)
    {
        host.ShowMain?.Invoke(meetingId);
        if (App.Current.MainHandle is { } main && main != IntPtr.Zero) SetForegroundWindow(main);
    }

    FrameworkElement Meter(string glyph, Func<float> read, (Brush, Brush, Brush) colors, string name)
    {
        var meter = new LevelMeter(12, colors) { Read = read, Running = true };
        AutomationProperties.SetName(meter.Root, name);
        // 島收起來、重建時舊的音量條不再讀（DispatcherTimer 不會自己停）
        meter.Root.Unloaded += (_, _) => meter.Running = false;
        return Ui.Row(6, Icon(glyph, null), meter.Root);
    }

    sealed record Hint(string Text, string Glyph, List<(string Title, bool Primary, Action Run)> Buttons);

    /// <summary>目前的提醒和它的按鈕，跟錄音畫面的提示列同一句、同樣的按鈕（R81.2）</summary>
    Hint? CurrentHint()
    {
        var r = host.Recorder;
        if (!r.IsRecording) return null;
        if (r.IdlePrompt)
            return new Hint(L.T("好像沒人在說話了，要停止錄音嗎？再 15 分鐘沒人說話會自動停止。"), "\uE708",
                [(L.T("繼續錄"), false, r.ContinueAfterIdle), (L.T("停止錄音"), true, r.Stop)]);
        if (r.MicHint is { } mic)
        {
            var buttons = new List<(string, bool, Action)>();
            if (mic.BuiltIn is { } builtIn) buttons.Add((L.F("改用 %@", builtIn.Name), true, () => r.SwitchMicrophone(builtIn.Id)));
            buttons.Add((L.T("知道了"), false, r.DismissMicHint));
            return new Hint(mic.Text, "\uE7F6", buttons);
        }
        if (r.Hint is { } hint) return new Hint(r.HintText(hint), "\uE7BA", [(L.T("知道了"), false, r.DismissLevelHint)]);
        if (r.DeviceWarning is { } warning) return new Hint(warning, "\uE72C", []);
        return null;
    }

    FrameworkElement HintBody(Hint hint)
    {
        var column = Ui.Column(12, Ui.Split(Icon(hint.Glyph, null), Ui.Text(hint.Text, 13, color: new SolidColorBrush(Colors.White)), 8));
        if (hint.Buttons.Count > 0) column.Children.Add(HintButtons(hint));
        return column;
    }

    StackPanel HintButtons(Hint hint)
    {
        var row = Ui.Row(8, [.. hint.Buttons.Select(b => (UIElement)IslandButton(b.Title, null, b.Primary ? Role.Primary : Role.Normal, b.Run))]);
        row.HorizontalAlignment = HorizontalAlignment.Right;
        return row;
    }

    FrameworkElement PromptBody(PromptState prompt)
    {
        var message = Ui.Text(prompt.Message, 14, color: new SolidColorBrush(Colors.White));
        var row = Ui.Row(8, [.. prompt.Actions.Select((a, i) => (UIElement)IslandButton(a.Title, null, i == prompt.Actions.Count - 1 ? Role.Primary : Role.Normal, () => Perform(a.Id)))]);
        row.HorizontalAlignment = HorizontalAlignment.Right;
        return Ui.Column(12, message, row);
    }

    /// <summary>最新一句即時逐字稿（還沒定稿的優先）</summary>
    static string? LatestLine(Recorder recorder)
    {
        var text = recorder.VolatileText.Length > 0 ? recorder.VolatileText : recorder.LiveLines.LastOrDefault()?.Text ?? "";
        return text.Length == 0 ? null : text;
    }

    enum Role { Normal, Primary, Destructive }

    /// <summary>島上的按鈕：白色半透明的膠囊；主要的是青瓷綠，停止是紅色（同 Mac 的 IslandButton）</summary>
    static Button IslandButton(string title, string? glyph, Role role, Action action)
    {
        var text = Ui.Text(title, 12, color: new SolidColorBrush(Colors.White), wrap: false);
        var button = new Button
        {
            Content = glyph is null ? text : Ui.Row(5, new FontIcon { Glyph = glyph, FontSize = 11, Foreground = new SolidColorBrush(Colors.White) }, text),
            CornerRadius = new CornerRadius(14), Padding = new Thickness(11, 5, 11, 6), BorderThickness = new Thickness(0),
            Background = role switch
            {
                Role.Primary => Brush(0x2D6A5D),
                Role.Destructive => Brush(0xBF3333),
                _ => new SolidColorBrush(Color.FromArgb(36, 255, 255, 255)),
            },
        };
        AutomationProperties.SetName(button, title);
        button.Click += (_, _) => Ui.Guard(action);
        return button;
    }

    static FontIcon Icon(string glyph, uint? color) => new()
    {
        Glyph = glyph, FontSize = 13, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0),
        Foreground = color is uint c ? Brush(c) : Secondary,
    };

    static SolidColorBrush Secondary => new(Color.FromArgb(178, 255, 255, 255));

    static SolidColorBrush Brush(uint rgb) => new(Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));

    // MARK: 視窗（Win32）

    /// <summary>
    /// 第一次出現前：不出現在工作列、按了不搶焦點；一律拍不到（R81.7，不看 R77 的設定；DEBUG 的 --set debugIslandCapture=YES 才拍得到）；
    /// Windows 11 的圓角
    /// </summary>
    void ApplyStyles()
    {
        if (styled) return;
        styled = true;
        try
        {
            var ex = GetWindowLongPtr(hwnd, GwlExStyle);
            SetWindowLongPtr(hwnd, GwlExStyle, (IntPtr)((long)ex | WsExToolWindow | WsExNoActivate));
            // SetBorderAndTitleBar(false, false) 之後還留著 WS_DLGFRAME（四周 3 px 的框，2026-11-15 虛擬機看到）：改成沒有框的 popup
            var style = (long)GetWindowLongPtr(hwnd, GwlStyle);
            SetWindowLongPtr(hwnd, GwlStyle, (IntPtr)((style & ~(WsCaption | WsThickFrame | WsSysMenu | WsMinimizeBox | WsMaximizeBox)) | WsPopup));
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
            var capture = host.Debug && host.Settings.Flag("debugIslandCapture", false);
            SetWindowDisplayAffinity(hwnd, capture ? WdaNone : WdaExcludeFromCapture);
            var round = DwmwcpRound;
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int));
            // Windows 11 在視窗外圍畫的那一圈淺色框線拿掉（黑底上很顯眼）
            var none = DwmwaColorNone;
            DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref none, sizeof(int));
        }
        catch (Exception e) when (e is COMException or EntryPointNotFoundException or DllNotFoundException)
        {
            Services.Log.Error($"island style failed: {e.GetType().Name}");
        }
    }

    /// <summary>
    /// 前景視窗所在的螢幕（沒有前景視窗時是主螢幕）的工作區上緣正中間往下 6 px；大小是內容量出來的大小（邏輯像素乘上縮放）
    /// </summary>
    void Place()
    {
        if (!AppWindow.IsVisible && !Enabled) return;
        var display = ForegroundDisplay();
        var area = display.WorkArea;
        var scale = WindowPlacement.Scale(this);
        var width = card.ActualWidth > 0 ? card.ActualWidth : double.IsNaN(card.Width) ? CollapsedWidth : card.Width;
        var height = card.ActualHeight > 0 ? card.ActualHeight : double.IsNaN(card.Height) ? CollapsedHeight : card.Height;
        var w = (int)Math.Ceiling(width * scale);
        var h = (int)Math.Ceiling(height * scale);
        var x = area.X + (area.Width - w) / 2;
        var y = area.Y + (int)Math.Round(6 * scale);
        if (placed == (x, y, w, h)) return;
        placed = (x, y, w, h);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, w, h));
    }

    /// <summary>前景視窗（拾音自己的島不算）的中心點所在的螢幕</summary>
    DisplayArea ForegroundDisplay()
    {
        var foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero && foreground != hwnd && GetWindowRect(foreground, out var r))
            return DisplayArea.GetFromPoint(new Windows.Graphics.PointInt32((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2), DisplayAreaFallback.Primary);
        return DisplayArea.Primary;
    }

    /// <summary>DEBUG 的 state：island</summary>
    public (int X, int Y, int W, int H) Frame => placed;

    const int GwlExStyle = -20, GwlStyle = -16;
    const long WsExToolWindow = 0x80, WsExNoActivate = 0x08000000;
    const long WsPopup = 0x80000000, WsCaption = 0x00C00000, WsThickFrame = 0x00040000, WsSysMenu = 0x00080000, WsMinimizeBox = 0x00020000, WsMaximizeBox = 0x00010000;
    const uint SwpNoSize = 0x1, SwpNoMove = 0x2, SwpNoZOrder = 0x4, SwpNoActivate = 0x10, SwpFrameChanged = 0x20;
    const uint WdaNone = 0, WdaExcludeFromCapture = 0x11;
    const int DwmwaWindowCornerPreference = 33, DwmwcpRound = 2, DwmwaBorderColor = 34, DwmwaColorNone = unchecked((int)0xFFFFFFFE);

    [StructLayout(LayoutKind.Sequential)]
    struct Rect { public int Left, Top, Right, Bottom; }

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
