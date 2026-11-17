using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Shiyin.Localization;
using Shiyin.Model;
using Shiyin.Notes;
using Shiyin.Services;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace Shiyin.App.Views;

/// <summary>
/// 可以用鍵盤、輔助工具調整的元件（R107.9）：時間軸的播放位置、開頭和結尾的把手。UI Automation 是 RangeValue（Slider 類型），
/// 螢幕閱讀器唸名稱和說明（「開頭」「00:12，剩下 02:15」）；UIA 的 SetValue 也能直接調（虛擬機驗證用）
/// </summary>
sealed partial class Adjustable : ContentControl
{
    public required Func<double> Value { get; init; }
    public required Action<double> Set { get; init; }
    public required Func<double> Maximum { get; init; }
    public double SmallChange { get; init; } = 1;
    public double LargeChange { get; init; } = 10;
    /// <summary>給螢幕閱讀器的說明（AutomationProperties.HelpText），值改了跟著換</summary>
    public required Func<string> Describe { get; init; }

    public Adjustable(InputSystemCursorShape? cursor = null)
    {
        IsTabStop = true;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        if (cursor is { } shape) ProtectedCursor = InputSystemCursor.Create(shape);
    }

    public void Refresh() => AutomationProperties.SetHelpText(this, Describe());

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    sealed partial class Peer(Adjustable owner) : FrameworkElementAutomationPeer(owner), IRangeValueProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;
        protected override string GetClassNameCore() => nameof(Adjustable);
        protected override object GetPatternCore(PatternInterface pattern) => pattern == PatternInterface.RangeValue ? this : base.GetPatternCore(pattern);

        public bool IsReadOnly => false;
        public double LargeChange => owner.LargeChange;
        public double Maximum => owner.Maximum();
        public double Minimum => 0;
        public double SmallChange => owner.SmallChange;
        public double Value => owner.Value();
        public void SetValue(double value) => owner.Set(value);
    }
}

/// <summary>
/// 編輯畫面的時間軸（M§81.5、design-windows §11.40）：上面一列標籤（開頭、結尾、切點、合併時每場的名字）、中間的時間軸
///（議題帶、依說話者著色的波形、標記重點、裁掉的兩端、刪掉的段落（斜線、虛線邊、「恢復」）、選取（藍色）、合併的分隔線、切點、把手、播放頭）、
/// 下面的刻度。放大時左右捲動，波形只畫看得到的那一段（2 小時的會議放大 40 倍是幾萬點寬）
/// </summary>
sealed class EditTimeline
{
    public const double TrackHeight = 132;
    const double WaveTop = 26, WaveHeight = 92, FullHeight = 120;

    readonly MeetingEditor editor;
    readonly Action<double> seek;
    readonly Action revealPlayhead;
    public Border Root { get; }
    readonly ScrollViewer scroll = new()
    {
        HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, ZoomMode = ZoomMode.Disabled,
    };
    readonly Grid content = new() { Padding = new Thickness(12, 8, 12, 8) };
    readonly Canvas labels = new();
    readonly Grid track = new() { Height = TrackHeight };
    readonly Canvas wave = new() { IsHitTestVisible = false };
    readonly Canvas bars = new() { IsHitTestVisible = false };
    readonly Canvas overlay = new();
    /// <summary>把手、播放頭：一直留著只搬位置（拖曳中重畫時拿下來再放回去會丟掉滑鼠的捕捉和焦點）</summary>
    readonly Canvas fixedLayer = new();
    /// <summary>切點的剪刀：在播放頭上面（切點常常就在播放頭的位置，播放頭的圓點會蓋住剪刀）</summary>
    readonly Canvas topLayer = new();
    readonly Canvas ruler = new() { Height = 16 };
    readonly Canvas playhead = new() { IsHitTestVisible = false };
    readonly Rectangle playheadLine = new() { Width = 2, Height = TrackHeight - 8 };
    readonly Ellipse playheadDot = new() { Width = 10, Height = 10 };
    readonly Border loading;
    /// <summary>波形的表面：拖曳選一段、點一下移播放頭；鍵盤（空白鍵、← →、Delete）；UI Automation 的播放位置</summary>
    public Adjustable Surface { get; }
    public Adjustable StartHandle { get; }
    public Adjustable EndHandle { get; }
    string? signature;
    (double From, double To) drawnBars = (0, -1);
    double width;
    double? dragFrom;
    bool dragging;

    public EditTimeline(MeetingEditor editor, Action<double> seek, Action revealPlayhead)
    {
        this.editor = editor;
        this.seek = seek;
        this.revealPlayhead = revealPlayhead;
        Root = new Border { CornerRadius = new CornerRadius(10), Child = scroll };
        AutomationProperties.SetName(Root, L.T("編輯的時間軸"));
        scroll.Content = content;
        var column = new StackPanel { Spacing = 4 };
        column.Children.Add(labels);
        column.Children.Add(track);
        column.Children.Add(ruler);
        content.Children.Add(column);
        wave.Children.Add(bars);
        Surface = new Adjustable
        {
            Value = () => editor.Playhead, Set = seek, Maximum = () => editor.Total, SmallChange = 1, LargeChange = 10,
            Describe = () => L.F("%@／%@", TimeFormat.String(editor.Playhead), TimeFormat.String(editor.Total)),
            Content = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) }, UseSystemFocusVisuals = false,
        };
        AutomationProperties.SetName(Surface, L.T("播放位置"));
        StartHandle = Handle(true);
        EndHandle = Handle(false);
        playhead.Children.Add(playheadLine);
        playhead.Children.Add(playheadDot);
        Canvas.SetLeft(playheadLine, 4);
        Canvas.SetTop(playheadLine, 2);
        Canvas.SetTop(playheadDot, -2);
        loading = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 4, 10, 4), IsHitTestVisible = false,
            Child = Ui.Row(6, new ProgressRing { IsActive = true, Width = 14, Height = 14 }, Ui.Text(L.T("正在讀取錄音…"), 13, color: Ui.Secondary, wrap: false)),
        };
        track.Children.Add(wave);
        track.Children.Add(Surface);
        track.Children.Add(overlay);
        track.Children.Add(fixedLayer);
        track.Children.Add(topLayer);
        track.Children.Add(loading);
        fixedLayer.Children.Add(StartHandle);
        fixedLayer.Children.Add(EndHandle);
        fixedLayer.Children.Add(playhead);
        scroll.SizeChanged += (_, _) => Ui.Guard(() => Update(force: true));
        scroll.ViewChanged += (_, _) => Ui.Guard(DrawVisibleBars);
        Surface.GotFocus += (_, _) => Root.BorderBrush = SidebarLook.Gray(0.35);
        Surface.LostFocus += (_, _) => Root.BorderBrush = SidebarLook.Gray(0);
        Root.BorderThickness = new Thickness(1);

        // 在波形上拖曳選一段；不是拖曳（4 點以內）就是移播放頭、逐字稿捲到那一句（同 Mac）
        Surface.PointerPressed += (_, e) => Ui.Guard(() =>
        {
            if (!e.GetCurrentPoint(track).Properties.IsLeftButtonPressed) return;
            Surface.Focus(FocusState.Pointer);
            dragFrom = e.GetCurrentPoint(track).Position.X;
            dragging = false;
            Surface.CapturePointer(e.Pointer);
            e.Handled = true;
        });
        Surface.PointerMoved += (_, e) => Ui.Guard(() =>
        {
            if (dragFrom is not double from) return;
            var x = e.GetCurrentPoint(track).Position.X;
            if (!dragging && Math.Abs(x - from) <= 4) return;
            dragging = true;
            editor.SetSelection((TimeAt(from), TimeAt(x)));
        });
        Surface.PointerReleased += (_, e) => Ui.Guard(() =>
        {
            if (dragFrom is null) return;
            var x = e.GetCurrentPoint(track).Position.X;
            // 放掉捕捉時馬上會來 PointerCaptureLost（把拖曳的狀態清掉）：先記下來（2026-11-17 虛擬機：拖曳選取變成點一下）
            var dragged = dragging;
            Surface.ReleasePointerCapture(e.Pointer);
            if (!dragged)
            {
                editor.SetSelection(null);
                seek(TimeAt(x));
                revealPlayhead();
            }
            dragFrom = null;
            dragging = false;
            e.Handled = true;
        });
        Surface.PointerCaptureLost += (_, _) => { dragFrom = null; dragging = false; };
        // Ctrl＋滾輪（觸控板的捏合也是）放大縮小，以游標的位置為中心；不用 Ctrl＋、Ctrl－（那是整個 app 的文字大小，同 Mac）
        content.PointerWheelChanged += (_, e) => Ui.Guard(() =>
        {
            if (!e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control)) return;
            var point = e.GetCurrentPoint(track);
            var at = TimeAt(point.Position.X);
            var offsetInView = point.Position.X + 12 - scroll.HorizontalOffset;
            var delta = point.Properties.MouseWheelDelta;
            editor.SetZoom(editor.Zoom * Math.Pow(1.0015, delta));
            Update();
            scroll.UpdateLayout();
            scroll.ChangeView(Math.Max(0, X(at) + 12 - offsetInView), null, null, disableAnimation: true);
            e.Handled = true;
        });
        Surface.KeyDown += (_, e) => Ui.Guard(() =>
        {
            var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            switch (e.Key)
            {
                case VirtualKey.Left: seek(editor.Playhead - (shift ? 10 : 1)); break;
                case VirtualKey.Right: seek(editor.Playhead + (shift ? 10 : 1)); break;
                case VirtualKey.Delete or VirtualKey.Back: editor.DeleteSelection(); break;
                default: return;
            }
            e.Handled = true;
        });
    }

    /// <summary>開頭、結尾的把手：拖曳、焦點在把手上時 ← → 微調 0.1 秒（Shift 1 秒），螢幕閱讀器唸時間和剩下的長度（R107.9）</summary>
    Adjustable Handle(bool isStart)
    {
        var grip = new Rectangle { Width = 2, Height = 22, RadiusX = 1, RadiusY = 1, Fill = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var body = new Border
        {
            Width = 9, Height = TrackHeight - 4, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(77, 0, 0, 0)), Child = grip, HorizontalAlignment = HorizontalAlignment.Center,
        };
        // 兩邊各多 4 點也抓得到（同 Mac 的 contentShape inset -4）
        var area = new Grid { Width = 17, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Children = { body } };
        Adjustable? handle = null;
        handle = new Adjustable(InputSystemCursorShape.SizeWestEast)
        {
            Value = () => isStart ? editor.Start : editor.End,
            Set = v => { if (isStart) editor.SetStart(v); else editor.SetEnd(v); },
            Maximum = () => editor.Total, SmallChange = 0.1, LargeChange = 1,
            Describe = () => L.F("%@，剩下 %@", TimeFormat.String(isStart ? editor.Start : editor.End), TimeFormat.String(editor.KeptDuration())),
            Content = area, Tag = body, Width = 17,
        };
        AutomationProperties.SetName(handle, isStart ? L.T("開頭") : L.T("結尾"));
        var captured = false;
        handle.PointerPressed += (_, e) => Ui.Guard(() =>
        {
            handle.Focus(FocusState.Pointer);
            captured = handle.CapturePointer(e.Pointer);
            e.Handled = true;
        });
        handle.PointerMoved += (_, e) => Ui.Guard(() =>
        {
            if (!captured) return;
            var t = TimeAt(e.GetCurrentPoint(track).Position.X);
            if (isStart) editor.SetStart(t); else editor.SetEnd(t);
        });
        handle.PointerReleased += (_, e) => { if (captured) handle.ReleasePointerCapture(e.Pointer); captured = false; e.Handled = true; };
        handle.PointerCaptureLost += (_, _) => captured = false;
        handle.KeyDown += (_, e) => Ui.Guard(() =>
        {
            if (e.Key is not (VirtualKey.Left or VirtualKey.Right)) return;
            var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            var step = (shift ? 1 : 0.1) * (e.Key == VirtualKey.Left ? -1 : 1);
            if (isStart) editor.SetStart(editor.Start + step); else editor.SetEnd(editor.End + step);
            e.Handled = true;
        });
        handle.GotFocus += (_, _) => body.BorderBrush = Ui.Primary;
        handle.LostFocus += (_, _) => body.BorderBrush = new SolidColorBrush(Color.FromArgb(77, 0, 0, 0));
        return handle;
    }

    double Total => Math.Max(1, editor.Total);
    double TimeAt(double x) => Math.Clamp(x, 0, width) / Math.Max(1, width) * editor.Total;
    double X(double t) => editor.Total > 0 ? t / editor.Total * width : 0;

    /// <summary>字色 opacity 的灰（淺色黑、深色白）</summary>
    static SolidColorBrush Ink(double opacity) => SidebarLook.Gray(opacity);

    static SolidColorBrush Blue(double opacity) => new(Color.FromArgb((byte)Math.Round(255 * opacity), 0, Ui.IsDark ? (byte)0x84 : (byte)0x7A, 0xFF));

    /// <summary>editor 改了：播放頭只搬位置；其他東西（把手、刪掉的段落、選取、切點、縮放、合併、波形讀好了）變了才整個重畫</summary>
    public void Update(bool force = false)
    {
        var viewport = scroll.ActualWidth;
        if (viewport <= 0) return;
        width = Math.Max(1, viewport - 24) * editor.Zoom;
        var key = string.Join("|", width, editor.Start, editor.End, editor.Cut, editor.Selection, string.Join(",", editor.Deleted),
            string.Join(",", editor.Parts.Select(p => $"{p.Id}@{p.Offset}")), editor.Peaks.Count, editor.LoadingPeaks.Count, editor.Lines.Count, Ui.ThemeKey);
        if (force || key != signature)
        {
            signature = key;
            Redraw();
        }
        MoveHandles();
        MovePlayhead();
        StartHandle.Refresh();
        EndHandle.Refresh();
        Surface.Refresh();
    }

    void MovePlayhead()
    {
        var x = X(editor.Playhead);
        Canvas.SetLeft(playhead, x - 5);
        // 試聽中、放大時播放頭跟著走（同 Mac）
        if (editor.IsPlaying && editor.Zoom > 1.01)
        {
            var visible = x + 12 - scroll.HorizontalOffset;
            if (visible < 24 || visible > scroll.ViewportWidth - 24) scroll.ChangeView(Math.Max(0, x + 12 - scroll.ViewportWidth / 2), null, null, disableAnimation: true);
        }
    }

    /// <summary>縮放改了：播放頭留在畫面中間（同 Mac 的 scrollTo playhead）</summary>
    public void CenterPlayhead()
    {
        scroll.UpdateLayout();
        scroll.ChangeView(Math.Max(0, X(editor.Playhead) + 12 - scroll.ViewportWidth / 2), null, null, disableAnimation: true);
    }

    void Redraw()
    {
        var merged = editor.MergedPart is not null;
        scroll.HorizontalScrollBarVisibility = editor.Zoom > 1.01 ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden;
        Root.Background = Ink(Ui.IsDark ? 0.12 : 0.05);
        content.Width = width + 24;
        labels.Width = width;
        labels.Height = merged ? 38 : 18;
        track.Width = width;
        ruler.Width = width;
        playheadLine.Fill = Ui.Primary;
        playheadDot.Fill = Ui.Primary;
        loading.Background = Ui.IsDark ? new SolidColorBrush(Color.FromArgb(230, 0x33, 0x33, 0x33)) : new SolidColorBrush(Color.FromArgb(230, 0xF4, 0xF4, 0xF4));
        loading.Visibility = editor.IsLoadingPeaks ? Visibility.Visible : Visibility.Collapsed;
        DrawLabels(merged);
        DrawWave();
        drawnBars = (0, -1);
        DrawVisibleBars();
        DrawOverlay();
        DrawRuler();
    }

    // MARK: 標籤、刻度

    /// <summary>文字放在 x：靠近兩端時往內對齊，不然置中（量好寬度後再擺）</summary>
    void Place(Canvas canvas, TextBlock text, double x, double top, bool alignStart = false)
    {
        Canvas.SetTop(text, top);
        Canvas.SetLeft(text, x);
        text.SizeChanged += (_, _) =>
        {
            var w = text.ActualWidth;
            Canvas.SetLeft(text, alignStart || x < 50 ? Math.Max(0, x) : x > width - 50 ? Math.Min(x, width) - w : x - w / 2);
        };
        canvas.Children.Add(text);
    }

    void DrawLabels(bool merged)
    {
        labels.Children.Clear();
        var top = merged ? 20 : 0;
        TextBlock Label(string s, Brush color) => new() { Text = s, FontSize = 11, Foreground = color };
        if (editor.Start > 0.05) Place(labels, Label(L.F("開頭 %@", TimeFormat.String(editor.Start)), Ui.Celadon), X(editor.Start), top);
        if (editor.End < editor.Total - 0.05) Place(labels, Label(L.F("結尾 %@", TimeFormat.String(editor.End)), Ui.Celadon), X(editor.End), top);
        if (editor.Cut is double cut) Place(labels, Label(L.F("切點 %@", TimeFormat.String(cut)), Ui.Signal), X(cut), top);
        if (!merged) return;
        // 合併時多一行：每一場從哪裡開始、叫什麼
        foreach (var part in editor.Parts)
        {
            var box = new Border
            {
                CornerRadius = new CornerRadius(4), Background = Ink(0.12), Padding = new Thickness(6, 0, 6, 0),
                MaxWidth = Math.Max(40, part.Length / Total * width - 8),
                Child = new TextBlock { Text = part.Title, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap },
            };
            Canvas.SetLeft(box, part.Offset / Total * width + 2);
            labels.Children.Add(box);
        }
    }

    void DrawRuler()
    {
        ruler.Children.Clear();
        double[] steps = [5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600];
        var total = editor.Total;
        var step = steps.FirstOrDefault(s => total > 0 && s / total * width >= 70, 3600);
        for (var t = 0.0; t <= Math.Max(0, total - step * 0.4); t += step)
            Place(ruler, new TextBlock { Text = TimeFormat.String(t), FontSize = 11, Foreground = Ui.Secondary }, X(t), 0, alignStart: t == 0);
        var end = new TextBlock { Text = TimeFormat.String(total), FontSize = 11, Foreground = Ui.Secondary };
        end.SizeChanged += (_, _) => Canvas.SetLeft(end, width - end.ActualWidth);
        ruler.Children.Add(end);
    }

    // MARK: 時間軸

    /// <summary>議題帶、標記重點、裁掉的兩端、刪掉的段落、選取、合併的分隔線、切點（波形另外畫：只畫看得到的那一段）</summary>
    void DrawWave()
    {
        // 保留 bars（第一個子元件），其他重建
        while (wave.Children.Count > 1) wave.Children.RemoveAt(1);
        var dark = Ui.IsDark;
        var veil = dark ? new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)) : new SolidColorBrush(Color.FromArgb(153, 255, 255, 255));
        // 議題帶（同 M§43 的顏色）
        foreach (var topic in editor.Topics)
        {
            var w = Math.Max(2, (topic.End - topic.Start) / Total * width - 2);
            var alpha = editor.Kept((topic.Start + topic.End) / 2) ? 1 : 0.45;
            var color = Ui.HexColor(MindMap.Color(topic.Branch, dark));
            var box = new Border
            {
                Width = w, Height = 18, CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * 0.3 * alpha), color.R, color.G, color.B)),
            };
            if (w >= 40)
                box.Child = new TextBlock
                {
                    Text = topic.Title, FontSize = 11, Foreground = Ui.Primary, Opacity = alpha, Margin = new Thickness(5, 0, 5, 0),
                    TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.Clip, VerticalAlignment = VerticalAlignment.Center,
                };
            Canvas.SetLeft(box, topic.Start / Total * width);
            wave.Children.Add(box);
        }
        // 標記重點：下緣的琥珀色菱形
        foreach (var t in editor.Marks)
        {
            var x = X(t);
            wave.Children.Add(new Polygon
            {
                Fill = Ui.Signal, Opacity = editor.Kept(t) ? 1 : 0.35,
                Points = { new Point(x, 120), new Point(x + 4, 124), new Point(x, 128), new Point(x - 4, 124) },
            });
        }
        Rectangle Rect(double x, double w, Brush fill, double top = 0, double height = FullHeight)
        {
            var r = new Rectangle { Width = Math.Max(0, w), Height = height, Fill = fill };
            Canvas.SetLeft(r, x);
            Canvas.SetTop(r, top);
            wave.Children.Add(r);
            return r;
        }
        // 裁掉的兩端
        Rect(0, X(editor.Start), veil);
        Rect(X(editor.End), width - X(editor.End), veil);
        // 刪掉的段落：變暗、斜線、虛線邊
        foreach (var (lower, upper) in editor.Deleted)
        {
            double x0 = X(lower), x1 = X(upper);
            Rect(x0, x1 - x0, veil);
            var stripes = new GeometryGroup();
            for (var sx = x0 - FullHeight; sx < x1; sx += 8)
                stripes.Children.Add(new LineGeometry { StartPoint = new Point(sx, FullHeight), EndPoint = new Point(sx + FullHeight, 0) });
            wave.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
            {
                Data = stripes, Stroke = Ink(0.16), StrokeThickness = 3,
                Clip = new RectangleGeometry { Rect = new Rect(x0, 0, Math.Max(0, x1 - x0), FullHeight) },
            });
            foreach (var x in new[] { x0, x1 })
                wave.Children.Add(new Line { X1 = x, X2 = x, Y1 = 0, Y2 = FullHeight, Stroke = Ink(0.45), StrokeThickness = 1, StrokeDashArray = [3, 3] });
        }
        // 選取：藍色（把手、時間點是青瓷綠，同色分不出來）
        if (editor.Selection is { } sel)
        {
            double x0 = X(sel.Lower), x1 = X(sel.Upper);
            var rect = Rect(x0, Math.Max(1, x1 - x0), Blue(0.22));
            rect.Stroke = Blue(0.9);
            rect.StrokeThickness = 1;
            rect.RadiusX = rect.RadiusY = 3;
        }
        // 合併的分隔線
        if (editor.MergedPart is { } other && editor.OwnPart is { } own)
            Rect(X(Math.Max(own.Offset, other.Offset)) - 1, 2, Ink(0.75), -4, FullHeight + 8);
        // 切點
        if (editor.Cut is double cut)
            wave.Children.Add(new Line { X1 = X(cut), X2 = X(cut), Y1 = 0, Y2 = FullHeight + 8, Stroke = Ui.Signal, StrokeThickness = 2, StrokeDashArray = [2.5, 1.5] });
    }

    /// <summary>波形：每 3 點一條（2 點寬），高度是那段時間的峰值、顏色是那時候在說話的人；只畫看得到的那一段（前後各多一個畫面）</summary>
    void DrawVisibleBars()
    {
        var view = Math.Max(1, scroll.ViewportWidth);
        double from = Math.Max(0, scroll.HorizontalOffset - 12 - view), to = Math.Min(width, scroll.HorizontalOffset - 12 + 2 * view);
        if (drawnBars.To >= drawnBars.From && from >= drawnBars.From && to <= drawnBars.To) return;
        // 多畫一些，捲動一點點時不用重畫
        from = Math.Max(0, from - view);
        to = Math.Min(width, to + view);
        drawnBars = (from, to);
        bars.Children.Clear();
        var total = editor.Total;
        if (total <= 0 || width <= 3) return;
        var lines = editor.Lines;
        var groups = new Dictionary<(int Speaker, bool Kept), GeometryGroup>();
        var first = Math.Max(0, (int)(from / 3));
        var last = Math.Min((int)(width / 3), (int)Math.Ceiling(to / 3));
        // 從看得到的第一條開始找句子（句子照開始的時間排好了）
        var cursor = 0;
        {
            var mid0 = (first * 3 + 1.5) / width * total;
            int lo = 0, hi = lines.Count - 1;
            while (lo < hi)
            {
                var m = (lo + hi + 1) / 2;
                if (lines[m].Start <= mid0) lo = m; else hi = m - 1;
            }
            cursor = Math.Max(0, lo);
        }
        for (var i = first; i < last; i++)
        {
            double t0 = i * 3.0 / width * total, t1 = (i + 1) * 3.0 / width * total, mid = (t0 + t1) / 2;
            while (cursor < lines.Count - 1 && lines[cursor + 1].Start <= mid) cursor++;
            var line = lines.Count == 0 ? null : lines[cursor];
            var speaker = line is not null && line.Start <= mid && line.End >= t0 ? line.Speaker ?? -1 : -1;
            var h = Math.Max(2, Math.Sqrt(editor.Level(t0, t1)) * WaveHeight);
            var key = (speaker, editor.Kept(mid));
            if (!groups.TryGetValue(key, out var group)) groups[key] = group = new GeometryGroup();
            group.Children.Add(new RectangleGeometry { Rect = new Rect(i * 3, WaveTop + (WaveHeight - h) / 2, 2, h) });
        }
        foreach (var ((speaker, kept), group) in groups)
        {
            var color = speaker >= 0 ? Ui.Speaker(speaker).Color : ((SolidColorBrush)Ink(0.5)).Color;
            bars.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = group, Fill = new SolidColorBrush(color) { Opacity = kept ? 0.9 : 0.25 } });
        }
    }

    // MARK: 按得到的東西：刪掉的段落的「恢復」、切點、把手、選取的長度、播放頭

    void DrawOverlay()
    {
        overlay.Children.Clear();
        topLayer.Children.Clear();
        overlay.Width = width;
        overlay.Height = TrackHeight;
        foreach (var range in editor.Deleted) overlay.Children.Add(RestoreChip(range));
        if (editor.Selection is { } sel)
        {
            var chip = new Border
            {
                CornerRadius = new CornerRadius(6), Background = Blue(1), Padding = new Thickness(7, 2, 7, 2), IsHitTestVisible = false,
                Child = new TextBlock { Text = L.F("選取 %@", TimeFormat.String(sel.Upper - sel.Lower)), FontSize = 11, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) },
            };
            var mid = X((sel.Lower + sel.Upper) / 2);
            Canvas.SetTop(chip, 36);
            chip.SizeChanged += (_, _) => Canvas.SetLeft(chip, mid - chip.ActualWidth / 2);
            Canvas.SetLeft(chip, mid);
            overlay.Children.Add(chip);
        }
        if (editor.Cut is double cut)
        {
            var scissors = new Button
            {
                Width = 22, Height = 22, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(0),
                Background = Ui.Signal, Content = new FontIcon { Glyph = "", FontSize = 11, Foreground = new SolidColorBrush(Ui.IsDark ? Ui.HexColor("#202020") : Microsoft.UI.Colors.White) },
            };
            scissors.Resources["ButtonBackgroundPointerOver"] = Ui.Signal;
            scissors.Resources["ButtonBackgroundPressed"] = Ui.Signal;
            scissors.Click += (_, _) => editor.ToggleCut();
            var label = L.F("切點 %@，按一下拿掉", TimeFormat.String(cut));
            ToolTipService.SetToolTip(scissors, L.F("切點 %@：按一下拿掉", TimeFormat.String(cut)));
            AutomationProperties.SetName(scissors, label);
            Canvas.SetLeft(scissors, X(cut) - 11);
            Canvas.SetTop(scissors, -6);
            topLayer.Children.Add(scissors);
        }
        fixedLayer.Width = width;
        fixedLayer.Height = TrackHeight;
    }

    void MoveHandles()
    {
        foreach (var (handle, t) in new[] { (StartHandle, editor.Start), (EndHandle, editor.End) })
        {
            ((Border)handle.Tag).Background = Ui.Celadon;
            Canvas.SetLeft(handle, X(t) - 8.5);
            Canvas.SetTop(handle, -4);
        }
        ToolTipService.SetToolTip(StartHandle, L.F("開頭 %@：拖到會議真正開始的地方", TimeFormat.String(editor.Start)));
        ToolTipService.SetToolTip(EndHandle, L.F("結尾 %@：拖到會議真正結束的地方", TimeFormat.String(editor.End)));
    }

    /// <summary>刪掉的段落中間一顆「刪掉 03:35・恢復」（窄的時候只有「恢復」或圖示）：按一下恢復</summary>
    Button RestoreChip((double Lower, double Upper) range)
    {
        double x0 = X(range.Lower), x1 = X(range.Upper);
        var icon = new FontIcon { Glyph = "", FontSize = 11 };
        object content = x1 - x0 >= 120 ? L.F("刪掉 %@・恢復", TimeFormat.String(range.Upper - range.Lower))
            : x1 - x0 >= 50 ? Ui.Row(4, icon, new TextBlock { Text = L.T("恢復"), FontSize = 11 }) : icon;
        var chip = new Button
        {
            Content = content, FontSize = 11, Padding = new Thickness(7, 2, 7, 2), MinWidth = 0, MinHeight = 0, CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Ui.IsDark ? Color.FromArgb(235, 0x2B, 0x2B, 0x2B) : Color.FromArgb(235, 255, 255, 255)),
            BorderBrush = Ink(0.25), BorderThickness = new Thickness(1),
        };
        chip.Click += (_, _) => editor.Restore(range);
        ToolTipService.SetToolTip(chip, L.F("刪掉的段落 %@–%@，按一下恢復", TimeFormat.String(range.Lower), TimeFormat.String(range.Upper)));
        AutomationProperties.SetName(chip, L.F("刪掉的段落 %@ 到 %@，按一下恢復", TimeFormat.String(range.Lower), TimeFormat.String(range.Upper)));
        var mid = (x0 + x1) / 2;
        Canvas.SetTop(chip, 36);
        Canvas.SetLeft(chip, mid);
        chip.SizeChanged += (_, _) => Canvas.SetLeft(chip, mid - chip.ActualWidth / 2);
        return chip;
    }
}
