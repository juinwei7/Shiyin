using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Shiyin.Localization;
using Shiyin.Model;
using Shiyin.Services;
using Windows.System;
using Windows.UI.Core;

namespace Shiyin.App;

/// <summary>
/// 播放列上方的麥克風時間軸（R119.4、M§93.5、design-windows §11.52）：波形、門檻線、建議收錄的區塊。
/// 拖曳空白處新增、拖區塊兩邊調整、點選後 Delete 刪除、右鍵選單、雙擊從那裡播放；點空白處跳播
/// </summary>
public sealed class MicTrackView
{
    const double TrackHeight = 34, Edge = 4, MinLength = 0.4, FloorDb = -60;
    /// <summary>收合、展開滑動的時間（同 Mac）</summary>
    static readonly TimeSpan SlideTime = TimeSpan.FromMilliseconds(220);

    readonly AppHost host;
    readonly MainWindow window;
    /// <summary>
    /// 三塊放進播放列的格子（M§93.5）：Toggle 在播放鍵、目前時間那兩欄，Content 在錄音時間軸那一欄，Info 在右邊幾欄；
    /// 都貼齊這一列的底部，收合鈕不管收還是開都在同一個位置（使用者：「讓收合按鈕無論是收還是合都在畫面上同一個位子」）
    /// </summary>
    public Button Toggle => toggle;
    public Grid Content => body;
    public StackPanel Info { get; } = new() { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
    /// <summary>門檻的數值：時間軸右邊那幾欄、跟門檻線同高（使用者：「顯示在門檻線的右邊，現在疊在一起看不清楚」）</summary>
    public Canvas ThresholdLabel { get; } = new() { Height = TrackHeight, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false };
    readonly TextBlock thresholdText = new() { FontSize = 9 };
    public IEnumerable<FrameworkElement> Parts => [toggle, body, Info, ThresholdLabel];
    /// <summary>收合鈕：藥丸形、整顆都能按（使用者：「只有文字可以觸發收合太難按了」）</summary>
    readonly Button toggle = new() { Padding = new Thickness(9, 2, 9, 2), CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(0) };
    readonly TextBlock title = Ui.Text("", 12, wrap: false);
    readonly TextBlock count = Ui.Text("", 12, wrap: false);
    readonly ProgressRing busy = new() { Width = 12, Height = 12, IsActive = false };
    readonly StackPanel placeholder = new() { Orientation = Orientation.Horizontal, Spacing = 8, Height = TrackHeight };
    readonly Grid track = new() { Height = TrackHeight, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), IsTabStop = true };
    readonly Canvas canvas = new();
    /// <summary>放提示或時間軸；收合用滑的：高度在 0 和原本之間動畫、同時淡出（使用者：「用 slide 的，不要突然出現跟消失」）</summary>
    readonly Grid body = new() { Height = TrackHeight };
    readonly RotateTransform chevronTurn = new();
    readonly FontIcon chevron = new() { Glyph = "\uE70E", FontSize = 9 };   // ChevronUp：展開時朝上（時間軸在上面），收起時轉成朝右
    readonly Rectangle cursor = new() { Width = 2, Height = TrackHeight };

    MeetingDetail? detail;
    List<double?> levels = [];
    /// <summary>放大後看的範圍（R119.10、M§93.5）；null 是整場，換會議時清掉</summary>
    public MicZoom? Zoom { get; private set; }
    /// <summary>錄音時間軸要框的範圍：收起來時不框（看不到放大的那條）</summary>
    public MicZoom? VisibleZoom => Collapsed ? null : Zoom;
    public event Action<MicZoom?>? ZoomChanged;
    readonly TextBlock zoomText = Ui.Text("", 12, wrap: false);
    readonly HyperlinkButton whole = new() { Padding = new Thickness(0) };
    bool hovering, keysHooked;
    /// <summary>按住 Ctrl：滑鼠的位置畫提示線和時間（R119.11）</summary>
    bool ctrlDown;
    readonly Canvas guideLayer = new() { IsHitTestVisible = false };
    readonly Rectangle guideLine = new() { Width = 1, Height = TrackHeight };
    readonly TextBlock guideLabel = new() { FontSize = 9 };
    readonly Border guideText = new() { Padding = new Thickness(3, 0, 3, 0), CornerRadius = new CornerRadius(2) };
    double hoverX, lastTime;
    long? levelsMeeting;
    string? levelsFile;
    double duration;
    long? selected;
    // 拖曳中：新增（Id 是 null）或調整（IsStart：拖的是開始那一邊）
    (long? Id, bool IsStart, double Start, double End)? drag;
    double pressX;
    bool pressed;
    /// <summary>拖門檻線（R119.12）：拖的時候的門檻；null 是沒在拖</summary>
    double? thresholdDrag;
    bool pressedOnThreshold;

    public MicTrackView(AppHost host, MainWindow window)
    {
        this.host = host;
        this.window = window;
        chevron.RenderTransform = chevronTurn;
        chevron.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        label.Children.Add(chevron);
        label.Children.Add(title);
        toggle.Content = label;
        toggle.Click += (_, _) =>
        {
            host.Settings.Set(AppSettings.MicTrackCollapsedKey, !Collapsed);
            Layout();
            Slide(Collapsed, animated: true);
            ZoomChanged?.Invoke(VisibleZoom);
        };
        toggle.VerticalAlignment = VerticalAlignment.Center;
        ThresholdLabel.Children.Add(thresholdText);
        toggle.HorizontalAlignment = HorizontalAlignment.Left;
        body.VerticalAlignment = VerticalAlignment.Bottom;
        Info.Children.Add(count);
        Info.Children.Add(busy);
        whole.Content = L.T("整場");
        whole.Visibility = zoomText.Visibility = Visibility.Collapsed;
        ToolTipService.SetToolTip(whole, L.T("回到整場（Ctrl+0）"));
        whole.Click += (_, _) => SetZoom(null);
        Info.Children.Add(zoomText);
        Info.Children.Add(whole);
        // 滑鼠在時間軸上時才收 Ctrl＋＋／−／0、滾輪（M§93.5）
        track.PointerEntered += (_, e) => { hovering = true; hoverX = e.GetCurrentPoint(track).Position.X; };
        track.PointerExited += (_, _) => { hovering = false; ctrlDown = false; UpdateGuide(); };
        track.PointerWheelChanged += Wheel;
        track.Loaded += (_, _) =>
        {
            if (keysHooked || track.XamlRoot?.Content is not UIElement content) return;
            content.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(Key), true);
            content.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler((_, e) =>
            {
                if (e.Key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl && ctrlDown) { ctrlDown = false; UpdateGuide(); }
            }), true);
            keysHooked = true;
        };
        track.Children.Add(canvas);
        // 提示線在上面一層（Draw 清掉的是 canvas，不會拿掉它）
        guideLayer.Children.Add(guideLine);
        guideLayer.Children.Add(guideText);
        guideLayer.Visibility = Visibility.Collapsed;
        track.Children.Add(guideLayer);
        body.Children.Add(placeholder);
        body.Children.Add(track);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(track, L.T("麥克風時間軸"));

        track.SizeChanged += (_, _) => Draw();
        track.PointerPressed += Pressed;
        track.PointerMoved += Moved;
        track.PointerReleased += Released;
        track.DoubleTapped += (_, e) =>
        {
            if (RegionAt(e.GetPosition(track).X) is { } r) host.Player.Seek(r.Start, true);
        };
        track.RightTapped += (_, e) => ShowMenu(e.GetPosition(track));
        track.KeyDown += (_, e) =>
        {
            if (e.Key is VirtualKey.Delete or VirtualKey.Back && selected is long id && detail?.Meeting.Id is long meeting)
            {
                host.Controller.DeleteMicRegion(meeting, id);
                selected = null;
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.Escape && selected is not null)
            {
                selected = null;
                Draw();
                e.Handled = true;
            }
        };
    }

    bool Collapsed => host.Settings.Flag(AppSettings.MicTrackCollapsedKey, false);
    List<MicRegion> Regions => detail?.MicRegions ?? [];

    public void Show(MeetingDetail value, double total)
    {
        detail = value;
        duration = total;
        if (selected is long id && !value.MicRegions.Any(r => r.Id == id)) selected = null;
        // 波形：換會議、換錄音檔（壓縮成 m4a）時重讀
        if (value.Meeting.Id is long changed && levelsMeeting != changed) SetZoom(null);
        if (value.Meeting.Id is long meeting && (levelsMeeting != meeting || levelsFile != value.Meeting.AudioFile))
        {
            levelsMeeting = meeting;
            levelsFile = value.Meeting.AudioFile;
            levels = [];
            _ = LoadLevels(meeting, value.Meeting.AudioFile);
        }
        Layout();
        Slide(Collapsed, animated: false);
    }

    /// <summary>收合、展開：animated 是按了收合鍵（滑動），換會議時直接放到位</summary>
    void Slide(bool collapse, bool animated)
    {
        body.IsHitTestVisible = !collapse;
        double height = collapse ? 0 : TrackHeight, opacity = collapse ? 0 : 1, angle = collapse ? 90 : 0;
        if (!animated)
        {
            body.Height = height;
            body.Opacity = opacity;
            ThresholdLabel.Height = height;
            ThresholdLabel.Opacity = opacity;
            chevronTurn.Angle = angle;
            return;
        }
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var storyboard = new Storyboard();
        void Animate(DependencyObject target, string property, double to, bool dependent)
        {
            var animation = new DoubleAnimation { To = to, Duration = SlideTime, EasingFunction = ease, EnableDependentAnimation = dependent };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, property);
            storyboard.Children.Add(animation);
        }
        Animate(body, "Height", height, true);   // 高度要排版，是 dependent 的動畫
        Animate(body, "Opacity", opacity, false);
        Animate(ThresholdLabel, "Height", height, true);
        Animate(ThresholdLabel, "Opacity", opacity, false);
        Animate(chevronTurn, "Angle", angle, false);
        storyboard.Begin();
    }

    async Task LoadLevels(long meeting, string file)
    {
        List<double?> loaded;
        try { loaded = await host.Controller.MicLevels(meeting); }
        catch (Exception e) when (e is not OutOfMemoryException) { Log.Error($"mic levels failed: {e.Message}"); return; }
        if (levelsMeeting != meeting || levelsFile != file) return;
        levels = loaded;
        Draw();
    }

    void Layout()
    {
        if (detail is null) return;
        var m = detail.Meeting;
        var collapsed = Collapsed;
        title.Text = L.T("麥克風");
        ToolTipService.SetToolTip(toggle, collapsed ? L.T("展開麥克風時間軸") : L.T("收起麥克風時間軸"));
        count.Text = Regions.Count > 0 ? L.F("建議收錄 %lld 段", Regions.Count) : "";
        count.Foreground = Ui.Secondary;
        toggle.Background = Ui.Subtle;
        var detecting = m.Id is long id && host.Controller.IsDetectingMic(id);
        // 放大的倍數、「整場」：收起來時不顯示（同 Mac）
        whole.Visibility = zoomText.Visibility = Zoom is not null && !collapsed ? Visibility.Visible : Visibility.Collapsed;
        busy.IsActive = detecting;
        busy.Visibility = detecting ? Visibility.Visible : Visibility.Collapsed;
        var empty = m.MicFloorDb is null || (m.MicThresholdDb is null && Regions.Count == 0);
        placeholder.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        track.Visibility = !empty ? Visibility.Visible : Visibility.Collapsed;
        placeholder.Children.Clear();
        if (empty)
        {
            var text = Ui.Text(detecting ? L.T("正在偵測建議收錄…") : m.MicFloorDb is null ? L.T("還沒偵測建議收錄") : L.T("麥克風幾乎沒有錄到聲音，沒辦法偵測"), 12, wrap: false);
            text.Foreground = Ui.Secondary;
            text.VerticalAlignment = VerticalAlignment.Center;
            placeholder.Children.Add(text);
            if (!detecting && m.MicFloorDb is null && m.Id is long meeting)
            {
                var detect = new Button { Content = L.T("偵測"), Padding = new Thickness(8, 2, 8, 2), VerticalAlignment = VerticalAlignment.Center };
                detect.Click += (_, _) => _ = host.Controller.DetectMicRegions(meeting);
                placeholder.Children.Add(detect);
            }
        }
        Draw();
    }

    public void Update(double time, double total)
    {
        if (Math.Abs(total - duration) > 0.5) { duration = total; Draw(); }
        // 播放中跑出範圍、跳播到範圍外：畫面跟著移過去（M§93.5）
        if (Zoom is { } zoom && !zoom.Contains(time) && (zoom.Contains(lastTime) || Math.Abs(time - lastTime) > 1)) SetZoom(zoom.Following(duration, time));
        lastTime = time;
        cursor.Fill = Ui.Celadon;
        cursor.Visibility = Zoom is null || Zoom.Contains(time) ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetLeft(cursor, duration > 0 ? Math.Min(track.ActualWidth - 2, Math.Max(0, X(time))) : 0);
    }

    /// <summary>按住 Ctrl 時滑鼠位置的提示線和時間（R119.11）</summary>
    void UpdateGuide()
    {
        var show = ctrlDown && hovering && track.ActualWidth > 0;
        guideLayer.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        var width = track.ActualWidth;
        var x = Math.Min(Math.Max(0, hoverX), width - 1);
        guideLine.Fill = Ui.Primary;
        guideLine.Opacity = 0.55;
        Canvas.SetLeft(guideLine, x);
        guideText.Child ??= guideLabel;
        guideLabel.Text = TimeFormat.String(TimeAt(hoverX));
        guideLabel.Foreground = Ui.Primary;
        guideText.Background = Ui.CardBackground;
        Canvas.SetLeft(guideText, x + 48 > width ? Math.Max(0, x - 46) : x + 3);
        Canvas.SetTop(guideText, 1);
    }

    // MARK: 放大縮小（R119.10、M§93.5）

    void SetZoom(MicZoom? zoom)
    {
        if (zoom == Zoom) return;
        Zoom = zoom;
        zoomText.Text = zoom is { } z && duration > 0 ? "×" + (duration / z.Span >= 10 ? $"{duration / z.Span:0}" : $"{duration / z.Span:0.0}") : "";
        zoomText.Foreground = Ui.Secondary;
        whole.Visibility = zoomText.Visibility = zoom is null || Collapsed ? Visibility.Collapsed : Visibility.Visible;
        ZoomChanged?.Invoke(VisibleZoom);
        Draw();
    }

    /// <summary>Ctrl＋滾輪放大縮小（觸控板的捏合 Windows 也送成這個）；Shift 或水平滾輪左右移動</summary>
    void Wheel(object sender, PointerRoutedEventArgs e)
    {
        if (Collapsed || duration <= 0) return;
        var point = e.GetCurrentPoint(track);
        var delta = point.Properties.MouseWheelDelta;
        if (e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control))
        {
            SetZoom(MicZoom.Zoomed(Zoom, duration, Math.Pow(MicZoom.WheelFactor, delta / 120.0), TimeAt(point.Position.X)));
            e.Handled = true;
        }
        else if (Zoom is { } zoom && (point.Properties.IsHorizontalMouseWheel || e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift)))
        {
            var seconds = (point.Properties.IsHorizontalMouseWheel ? delta : -delta) / 120.0 * zoom.Span * 0.1;
            SetZoom(zoom.Panned(duration, seconds));
            e.Handled = true;
        }
    }

    /// <summary>主視窗的按鍵（含已處理的）：滑鼠在時間軸上、按著 Ctrl 時才算，不搶輸入框的焦點</summary>
    void Key(object sender, KeyRoutedEventArgs e)
    {
        if (!hovering || Collapsed || duration <= 0 || track.Visibility != Visibility.Visible) return;
        if (e.Key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl)
        {
            if (!ctrlDown) { ctrlDown = true; UpdateGuide(); }
            return;
        }
        if (!InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down)) return;
        var anchor = TimeAt(hoverX);
        switch ((int)e.Key)
        {
            case (int)VirtualKey.Add or 187: SetZoom(MicZoom.Zoomed(Zoom, duration, MicZoom.KeyFactor, anchor)); break;      // 187：主鍵盤的 =／+
            case (int)VirtualKey.Subtract or 189: SetZoom(MicZoom.Zoomed(Zoom, duration, 1 / MicZoom.KeyFactor, anchor)); break;   // 189：主鍵盤的 -
            case (int)VirtualKey.Number0 or (int)VirtualKey.NumberPad0: SetZoom(null); break;
            default: return;
        }
        e.Handled = true;
    }

    // MARK: 座標

    // 看的範圍：放大時是 Zoom，不然是整場
    double ViewStart => Zoom?.Start ?? 0;
    double ViewSpan => Zoom?.Span ?? duration;
    double TimeAt(double x) => track.ActualWidth <= 0 || ViewSpan <= 0 ? 0 : ViewStart + Math.Clamp(x / track.ActualWidth, 0, 1) * ViewSpan;
    double X(double t) => ViewSpan <= 0 ? 0 : (t - ViewStart) / ViewSpan * track.ActualWidth;
    double Snapped(double t) => Math.Min(Math.Max(0, Math.Round(t * 10, MidpointRounding.AwayFromZero) / 10), duration);
    MicRegion? RegionAt(double x) { var t = TimeAt(x); return Regions.FirstOrDefault(r => r.Start <= t && t <= r.End); }

    double LineY(double db) => TrackHeight - Math.Clamp((db - FloorDb) / -FloorDb, 0, 1) * TrackHeight;

    /// <summary>滑鼠的高度換成門檻：夾在 −60～−10、取到 0.5 dB（R119.12）</summary>
    static double ThresholdAt(double y)
    {
        var db = FloorDb + (1 - Math.Clamp(y, 0, TrackHeight) / TrackHeight) * -FloorDb;
        return Math.Min(-10, Math.Max(FloorDb, Math.Round(db * 2, MidpointRounding.AwayFromZero) / 2));
    }

    bool OnThreshold(double y) => detail?.Meeting.MicThresholdDb is double t && Math.Abs(y - LineY(t)) <= Edge;

    (MicRegion Region, bool IsStart)? EdgeAt(double x)
    {
        (MicRegion, bool, double)? best = null;
        foreach (var r in Regions)
            foreach (var (t, isStart) in new[] { (r.Start, true), (r.End, false) })
            {
                var d = Math.Abs(X(t) - x);
                if (d <= Edge && d < (best?.Item3 ?? double.MaxValue)) best = (r, isStart, d);
            }
        return best is { } b ? (b.Item1, b.Item2) : null;
    }

    // MARK: 滑鼠

    void Pressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(track);
        if (!point.Properties.IsLeftButtonPressed) return;
        // Ctrl＋點一下打開選單（同 Mac 的 ⌃＋點一下，R119.11）：不開始拖曳、不跳播
        if (e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control))
        {
            ShowMenu(point.Position);
            e.Handled = true;
            return;
        }
        track.Focus(FocusState.Pointer);
        pressed = true;
        pressX = point.Position.X;
        drag = null;
        // 在門檻線上按下去：拖門檻（上下），不新增區塊、不跳播
        pressedOnThreshold = OnThreshold(point.Position.Y);
        thresholdDrag = null;
        track.CapturePointer(e.Pointer);
    }

    void Moved(object sender, PointerRoutedEventArgs e)
    {
        var position = e.GetCurrentPoint(track).Position;
        var x = position.X;
        hoverX = x;
        ctrlDown = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control);
        UpdateGuide();
        // 滑到邊緣：游標變成左右箭頭
        SetCursor(thresholdDrag is not null || (!pressed && OnThreshold(position.Y)) ? InputSystemCursorShape.SizeNorthSouth
            : EdgeAt(x) is not null || drag is { Id: not null } ? InputSystemCursorShape.SizeWestEast : InputSystemCursorShape.Arrow);
        if (!pressed) return;
        if (pressedOnThreshold)
        {
            thresholdDrag = ThresholdAt(position.Y);
            Draw();
            return;
        }
        var t = Snapped(TimeAt(x));
        if (drag is { } d)
        {
            drag = d.Id is null ? d with { End = t }
                : d.IsStart ? d with { Start = Math.Min(t, d.End - 0.1) } : d with { End = Math.Max(t, d.Start + 0.1) };
            Draw();
            return;
        }
        if (Math.Abs(x - pressX) < 3) return;
        if (EdgeAt(pressX) is { Region.Id: long id } hit)
        {
            selected = id;
            drag = hit.IsStart ? (id, true, Math.Min(t, hit.Region.End - 0.1), hit.Region.End) : (id, false, hit.Region.Start, Math.Max(t, hit.Region.Start + 0.1));
        }
        else if (RegionAt(pressX) is null)
        {
            drag = (null, false, Snapped(TimeAt(pressX)), t);
        }
        Draw();
    }

    void Released(object sender, PointerRoutedEventArgs e)
    {
        if (!pressed) return;
        pressed = false;
        track.ReleasePointerCapture(e.Pointer);
        var x = e.GetCurrentPoint(track).Position.X;
        var d = drag;
        drag = null;
        if (detail?.Meeting.Id is not long meeting) return;
        if (pressedOnThreshold)
        {
            pressedOnThreshold = false;
            if (thresholdDrag is double value) _ = host.Controller.SetMicThreshold(meeting, value, levels);
            thresholdDrag = null;
            Draw();
            return;
        }
        switch (d)
        {
            case { Id: null } create:
                double start = Math.Min(create.Start, create.End), end = Math.Max(create.Start, create.End);
                if (end - start >= MinLength) host.Controller.AddMicRegion(meeting, start, end); else Draw();
                break;
            case { Id: long id } resize:
                host.Controller.UpdateMicRegion(meeting, id, resize.Start, resize.End);
                break;
            default:
                // 沒拖：點到區塊是選取，點空白處是跳播
                if (RegionAt(x) is { } r) selected = r.Id;
                else
                {
                    selected = null;
                    host.Player.Seek(TimeAt(x), false);
                }
                Draw();
                break;
        }
    }

    static string DbText(double db) => db % 1 == 0 ? db.ToString("0", System.Globalization.CultureInfo.InvariantCulture) : db.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    void SetCursor(InputSystemCursorShape shape) =>
        typeof(UIElement).GetProperty("ProtectedCursor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?
            .SetValue(track, InputSystemCursor.Create(shape));

    void ShowMenu(Windows.Foundation.Point at)
    {
        if (detail?.Meeting.Id is not long meeting) return;
        var menu = new MenuFlyout();
        if (RegionAt(at.X) is { Id: long id } r)
        {
            selected = id;
            Draw();
            menu.Items.Add(Ui.Item(L.T("從這裡播放"), () => host.Player.Seek(TimeAt(at.X), true), glyph: ""));
            menu.Items.Add(Ui.Item(L.T("從這段開頭播放"), () => host.Player.Seek(r.Start, true), glyph: "\uE892"));   // Previous
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Ui.DangerItem(L.T("刪除這段"), () => { host.Controller.DeleteMicRegion(meeting, id); selected = null; }));
        }
        else
        {
            menu.Items.Add(Ui.Item(L.T("從這裡播放"), () => host.Player.Seek(TimeAt(at.X), true), glyph: ""));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Ui.Item(L.T("重新偵測"), () => _ = host.Controller.DetectMicRegions(meeting), enabled: !host.Controller.IsDetectingMic(meeting), glyph: ""));
            if (detail?.Meeting.MicThresholdUser is not null)
                menu.Items.Add(Ui.Item(L.T("門檻改回自動"), () => _ = host.Controller.SetMicThreshold(meeting, null, levels), enabled: !host.Controller.IsDetectingMic(meeting)));
            menu.Items.Add(Ui.DangerItem(L.T("全部清除…"), async () =>
            {
                if (await Dialogs.Confirm(window, L.T("清除所有建議收錄？"),
                        L.T("麥克風時間軸上的區塊都會拿掉（包括自己新增、調整過的）。之後可以按「重新偵測」再找一次。"), L.T("全部清除"), destructive: true))
                {
                    host.Controller.ClearMicRegions(meeting);
                    selected = null;
                }
            }, enabled: Regions.Count > 0));
        }
        menu.ShowAt(track, at);
    }

    // MARK: 畫

    void Draw()
    {
        canvas.Children.Clear();
        double width = track.ActualWidth, height = TrackHeight;
        if (detail is null || duration <= 0 || width <= 0) { canvas.Children.Add(cursor); return; }
        double Y(double db) => height - Math.Clamp((db - FloorDb) / -FloorDb, 0, 1) * height;

        // 建議收錄的區塊（拖曳中的那塊照拖的位置畫）
        var shown = Regions.Select(r => (Id: r.Id, r.Start, r.End)).ToList();
        if (drag is { Id: long dragged } resize) shown = [.. shown.Select(s => s.Id == dragged ? (s.Id, resize.Start, resize.End) : s)];
        else if (drag is { } create) shown.Add((null, Math.Min(create.Start, create.End), Math.Max(create.Start, create.End)));
        var fill = new SolidColorBrush(Ui.Celadon.Color) { Opacity = 0.18 };
        foreach (var s in shown.Where(s => s.End >= ViewStart && s.Start <= ViewStart + ViewSpan))
        {
            double left = Math.Max(-2, X(s.Start)), right = Math.Min(width + 2, X(s.End));
            var rect = new Rectangle { Width = Math.Max(1, right - left), Height = height, RadiusX = 2, RadiusY = 2, Fill = fill };
            if (s.Id is null || s.Id == selected) { rect.Stroke = Ui.Celadon; rect.StrokeThickness = 1; }
            Canvas.SetLeft(rect, left);
            canvas.Children.Add(rect);
        }

        // 波形：每個像素欄取那段時間裡最大的音量（放大到一欄不到一格時取那一格）
        if (levels.Count > 0 && ViewSpan > 0)
        {
            var columns = Math.Max(1, (int)width);
            var frames = 1 / Transcribe.MicRegions.Rules.FrameSeconds;
            var geometry = new GeometryGroup();
            for (var c = 0; c < columns; c++)
            {
                int from = Math.Max(0, (int)((ViewStart + (double)c / columns * ViewSpan) * frames));
                int to = Math.Max(from + 1, (int)((ViewStart + (double)(c + 1) / columns * ViewSpan) * frames));
                if (from >= levels.Count) break;
                var peak = double.NegativeInfinity;
                for (var i = from; i < Math.Min(to, levels.Count); i++) if (levels[i] is double level) peak = Math.Max(peak, level);
                if (!(peak > FloorDb)) continue;
                var top = Y(peak);
                geometry.Children.Add(new RectangleGeometry { Rect = new Windows.Foundation.Rect(c, top, 1, height - top) });
            }
            canvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = geometry, Fill = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(128, 128, 128, 128)) });
        }

        // 門檻線：拖的時候是實線、右邊寫數值（R119.12）；自己設過的也寫
        if ((thresholdDrag ?? detail.Meeting.MicThresholdDb) is double threshold)
        {
            var dragging = thresholdDrag is not null;
            var line = new Line { X1 = 0, X2 = width, Y1 = Y(threshold), Y2 = Y(threshold), Stroke = Ui.Signal, StrokeThickness = dragging ? 1.5 : 1, Opacity = dragging ? 1 : 0.8 };
            if (!dragging) line.StrokeDashArray = [3, 3];
            canvas.Children.Add(line);
            // 數值寫在時間軸外面、門檻線的右邊；自己設過、拖的時候是強調色，自動的是次要的顏色
            thresholdText.Text = L.F("門檻 %@ dB", DbText(threshold));
            thresholdText.Foreground = dragging || detail.Meeting.MicThresholdUser is not null ? Ui.Signal : Ui.Secondary;
            thresholdText.Measure(new Windows.Foundation.Size(200, 20));
            var h = thresholdText.DesiredSize.Height;
            Canvas.SetLeft(thresholdText, 0);
            Canvas.SetTop(thresholdText, Math.Clamp(Y(threshold) - h / 2, 0, TrackHeight - h));
            thresholdText.Visibility = Visibility.Visible;
        }
        else
        {
            thresholdText.Visibility = Visibility.Collapsed;
        }
        canvas.Children.Add(cursor);
    }
}
