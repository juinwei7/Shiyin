using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Shiyin.Localization;
using Shiyin.Model;
using Shiyin.Notes;
using Shiyin.Services;

namespace Shiyin.App.Views;

/// <summary>底部播放列（同 Mac 的 PlayerBar）：播放鍵、時間、錄音時間軸（R5.3）、播放速度；有議題時時間軸上面加議題帶（R68）</summary>
public sealed class PlayerBar
{
    readonly AppHost host;
    readonly Grid root = new() { ColumnSpacing = 14, Padding = new Thickness(18, 10, 18, 10) };
    // 不固定寬度：固定 40 時按鈕預設的左右內距（各 11）只剩 18 放 20 寬的圖示，三角形右邊被切掉（2026-10-22 使用者看到）
    readonly Button play = new() { Padding = new Thickness(10, 6, 10, 6) };
    readonly TextBlock current = Ui.Text("0:00", 13, wrap: false);
    readonly TextBlock total = Ui.Text("0:00", 13, wrap: false);
    readonly DropDownButton speed = new();
    readonly Timeline timeline;
    readonly TopicBand topics;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    MeetingDetail? detail;
    double topicsDuration = -1;

    public event Action<double>? Tick;

    public PlayerBar(AppHost host)
    {
        this.host = host;
        timeline = new Timeline((seconds, playNow) => host.Player.Seek(seconds, playNow));
        topics = new TopicBand(seconds => host.Player.Seek(seconds, true));
        foreach (var w in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        // 議題帶在第一列（沒有議題時收起來，高度是 0），其他元件在第二列，跟時間軸對齊
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(topics.Root, 2);
        root.Children.Add(topics.Root);
        play.Click += (_, _) => host.Player.Toggle();
        current.Width = 52;
        current.TextAlignment = TextAlignment.Right;
        var menu = new MenuFlyout();
        foreach (var rate in new[] { 0.75, 1, 1.25, 1.5, 2 })
            menu.Items.Add(Ui.Item(Label(rate), () => { host.Player.Rate = rate; speed.Content = Label(rate); }));
        speed.Flyout = menu;
        ToolTipService.SetToolTip(speed, L.T("播放速度"));
        UIElement[] items = [play, current, timeline.Root, total, speed];
        for (var i = 0; i < items.Length; i++)
        {
            Grid.SetColumn((FrameworkElement)items[i], i);
            Grid.SetRow((FrameworkElement)items[i], 1);
            root.Children.Add(items[i]);
        }
        root.BorderThickness = new Thickness(0, 1, 0, 0);
        timer.Tick += (_, _) => Ui.Guard(Update);
        host.Player.StateChanged += () => host.Post(Update);
    }

    static string Label(double rate) => rate == 1 ? "1×" : $"{rate:0.##}×";

    public FrameworkElement Show(MeetingDetail value)
    {
        detail = value;
        // 主題的顏色每次顯示時重設：播放列只建一次，換深淺色時（主視窗會重畫）不重設的話還是舊主題的顏色，深色時總長看不見
        total.Foreground = Ui.Secondary;
        root.BorderBrush = Ui.Divider;
        timeline.Show(value.Segments, (value.Notes?.ReviewItems ?? []).Select(r => TimeFormat.Seconds(r.Time)).OfType<double>().ToList(),
            value.Bookmarks.Select(b => b.Time).ToList());
        topicsDuration = -1;   // 紀錄可能改了（編輯、回答待確認、重新整理）：下一次 Update 重算議題帶
        speed.Content = Label(host.Player.Rate <= 0 ? 1 : host.Player.Rate);
        timer.Start();
        Update();
        return root;
    }

    void Update()
    {
        if (detail is null) return;
        var player = host.Player;
        var duration = Math.Max(player.Duration, detail.Meeting.DurationSeconds ?? 0);
        play.Content = new SymbolIcon(player.IsPlaying ? Symbol.Pause : Symbol.Play);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(play, player.IsPlaying ? L.T("暫停") : L.T("播放"));
        ToolTipService.SetToolTip(play, player.IsPlaying ? L.T("暫停（空白鍵）") : L.T("播放（空白鍵）"));
        current.Text = TimeFormat.String(player.CurrentTime);
        total.Text = TimeFormat.String(duration);
        timeline.Update(player.CurrentTime, duration);
        if (duration != topicsDuration)
        {
            topicsDuration = duration;
            topics.Show(detail.Notes is { } notes ? MindMap.TopicRanges(notes, duration, L.Ui(MindMap.TopicLabel(MeetingLanguages.Interface))) : [], duration);
        }
        topics.Update(player.CurrentTime);
        root.Opacity = player.IsLoaded ? 1 : 0.5;
        Tick?.Invoke(player.CurrentTime);
    }
}

/// <summary>
/// 錄音時間軸（M§9.3）：整場會議畫成一條帶子，每段逐字稿一格；格子高度依說話密度變化，低信心段落是琥珀色，
/// 待確認題目在上方、重點在下方有標記。點擊或拖曳就跳播
/// </summary>
public sealed class Timeline
{
    public Grid Root { get; } = new() { Height = 30, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    readonly Canvas canvas = new();
    readonly Rectangle cursor = new() { Width = 2, Height = 30 };
    readonly Action<double, bool> seek;
    List<Segment> segments = [];
    List<double> reviews = [], bookmarks = [];
    double duration;
    bool dragging;

    public Timeline(Action<double, bool> seek)
    {
        this.seek = seek;
        Root.Children.Add(canvas);
        Root.SizeChanged += (_, _) => Draw();
        Root.PointerPressed += (_, e) => { dragging = true; Root.CapturePointer(e.Pointer); seek(TimeAt(e.GetCurrentPoint(Root).Position.X), false); };
        Root.PointerMoved += (_, e) => { if (dragging) seek(TimeAt(e.GetCurrentPoint(Root).Position.X), false); };
        Root.PointerReleased += (_, e) => { if (!dragging) return; dragging = false; Root.ReleasePointerCapture(e.Pointer); seek(TimeAt(e.GetCurrentPoint(Root).Position.X), true); };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(Root, L.T("錄音時間軸"));
    }

    double TimeAt(double x) => Root.ActualWidth <= 0 || duration <= 0 ? 0 : Math.Clamp(x / Root.ActualWidth, 0, 1) * duration;

    public void Show(List<Segment> value, List<double> reviewTimes, List<double> bookmarkTimes)
    {
        segments = value;
        reviews = reviewTimes;
        bookmarks = bookmarkTimes;
        Draw();
    }

    public void Update(double time, double total)
    {
        if (Math.Abs(total - duration) > 0.5) { duration = total; Draw(); }
        cursor.Fill = Ui.Celadon;
        Canvas.SetLeft(cursor, duration > 0 ? time / duration * Root.ActualWidth - 1 : 0);
    }

    void Draw()
    {
        canvas.Children.Clear();
        var (width, height) = (Root.ActualWidth, Root.ActualHeight);
        canvas.Children.Add(new Rectangle { Width = Math.Max(width, 0), Height = 1, Fill = Ui.Subtle, Margin = new Thickness(0, height / 2, 0, 0) });
        if (duration <= 0 || width <= 0) return;
        var scale = width / duration;
        // 說話密度（每秒字數）決定格子高度，看起來像波形；上方留給待確認的三角形、下方留給重點的圓點
        var densities = segments.Select(s => Shiyin.Text.SwiftText.Count(s.Text) / Math.Max(0.5, s.End - s.Start)).ToList();
        var max = Math.Max(densities.DefaultIfEmpty(1).Max(), 1);
        const double marker = 6;
        var bar = height - marker * 2;
        var dim = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(115, 128, 128, 128));
        for (var i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            var h = bar * (0.35 + 0.65 * densities[i] / max);
            var r = new Rectangle { Width = Math.Max(1.5, (s.End - s.Start) * scale - 1), Height = h, RadiusX = 1, RadiusY = 1, Fill = s.LowConfidence ? Ui.Signal : dim };
            Canvas.SetLeft(r, s.Start * scale);
            Canvas.SetTop(r, marker + (bar - h) / 2);
            canvas.Children.Add(r);
        }
        foreach (var t in reviews)
        {
            var p = new Polygon { Fill = Ui.Signal, Points = { new(t * scale - 3.5, 0), new(t * scale + 3.5, 0), new(t * scale, 5) } };
            canvas.Children.Add(p);
        }
        foreach (var t in bookmarks)
        {
            var dot = new Ellipse { Width = 5, Height = 5, Fill = Ui.Signal };
            Canvas.SetLeft(dot, t * scale - 2.5);
            Canvas.SetTop(dot, height - 5);
            canvas.Children.Add(dot);
        }
        canvas.Children.Add(cursor);
    }
}

/// <summary>
/// 議題帶（M§43.2、R68、design-windows §11.11）：時間軸上面每個議題一段，寬度、時間比例同時間軸，顏色同心智圖的那一支；
/// 正在播放的那段底色深、字粗，點了從那個議題開始播放，滑過去是「名稱　開始–結束」
/// </summary>
public sealed class TopicBand
{
    public Canvas Root { get; } = new() { Height = 18, Margin = new Thickness(0, 0, 0, 4), Visibility = Visibility.Collapsed };
    readonly Action<double> seek;
    readonly List<(TextBlock Text, SolidColorBrush Fill, SolidColorBrush Hover, Windows.UI.Color Color)> boxes = [];
    List<MindMap.TopicRange> ranges = [];
    double duration, time;
    int current = -2;

    public TopicBand(Action<double> seek)
    {
        this.seek = seek;
        Root.SizeChanged += (_, _) => Draw();
    }

    public void Show(List<MindMap.TopicRange> value, double total)
    {
        ranges = value;
        duration = total;
        Root.Visibility = value.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Draw();
    }

    public void Update(double now)
    {
        time = now;
        var index = ranges.FindIndex(r => now >= r.Start && now < r.End);
        if (index == current) return;
        current = index;
        for (var i = 0; i < boxes.Count; i++)
        {
            var (text, fill, hover, color) = boxes[i];
            var on = i == current;
            fill.Color = Windows.UI.Color.FromArgb((byte)(on ? 115 : 46), color.R, color.G, color.B);
            hover.Color = Windows.UI.Color.FromArgb((byte)(on ? 140 : 80), color.R, color.G, color.B);
            // 用 Bold 不用 SemiBold（同 Ui.Text）：SemiBold 的中文會被換成新細明體的假粗體
            text.FontWeight = on ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;
        }
    }

    void Draw()
    {
        Root.Children.Clear();
        boxes.Clear();
        current = -2;
        var width = Root.ActualWidth;
        if (duration <= 0 || width <= 0) return;
        var scale = width / duration;
        foreach (var r in ranges)
        {
            var w = Math.Max(2, (r.End - r.Start) * scale - 2);
            var label = $"{r.Title}　{TimeFormat.String(r.Start)}–{TimeFormat.String(r.End)}";  // l10n-skip：名稱和時間，不用翻譯
            // 整個標題放得下，或至少放得下一兩個字加「…」才寫字，不然只留色塊（同 Mac）
            var text = new TextBlock
            {
                Text = r.Title, FontSize = 11, Foreground = Ui.Primary, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 5, 0),
                Visibility = MindMap.Width(r.Title, 11) + 10 <= w || w >= 40 ? Visibility.Visible : Visibility.Collapsed,
            };
            var color = Ui.HexColor(MindMap.Color(r.Branch, Ui.IsDark));
            SolidColorBrush fill = new(color), hover = new(color);
            // 按鈕預設滑過、按下會換成系統的灰底：換成這一段自己的顏色
            var box = new Button
            {
                Width = w, Height = 18, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(4), Background = fill, Content = text,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
            };
            box.Resources["ButtonBackgroundPointerOver"] = hover;
            box.Resources["ButtonBackgroundPressed"] = hover;
            box.Resources["ButtonBorderBrushPointerOver"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            box.Resources["ButtonBorderBrushPressed"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            var start = r.Start;
            box.Click += (_, _) => seek(start);
            ToolTipService.SetToolTip(box, label);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, label);
            Canvas.SetLeft(box, r.Start * scale);
            Root.Children.Add(box);
            boxes.Add((text, fill, hover, color));
        }
        Update(time);
    }
}
