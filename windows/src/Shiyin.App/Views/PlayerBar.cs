using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Shiyin.Localization;
using Shiyin.Model;
using Shiyin.Services;

namespace Shiyin.App.Views;

/// <summary>底部播放列（同 Mac 的 PlayerBar）：播放鍵、時間、錄音時間軸（R5.3）、播放速度</summary>
public sealed class PlayerBar
{
    readonly AppHost host;
    readonly Grid root = new() { ColumnSpacing = 14, Padding = new Thickness(18, 10, 18, 10) };
    readonly Button play = new() { Width = 40 };
    readonly TextBlock current = Ui.Text("0:00", 13, wrap: false);
    readonly TextBlock total = Ui.Text("0:00", 13, wrap: false);
    readonly DropDownButton speed = new();
    readonly Timeline timeline;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    MeetingDetail? detail;

    public event Action<double>? Tick;

    public PlayerBar(AppHost host)
    {
        this.host = host;
        timeline = new Timeline((seconds, playNow) => host.Player.Seek(seconds, playNow));
        foreach (var w in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        play.Click += (_, _) => host.Player.Toggle();
        current.Width = 52;
        current.TextAlignment = TextAlignment.Right;
        total.Foreground = Ui.Secondary;
        var menu = new MenuFlyout();
        foreach (var rate in new[] { 0.75, 1, 1.25, 1.5, 2 })
            menu.Items.Add(Ui.Item(Label(rate), () => { host.Player.Rate = rate; speed.Content = Label(rate); }));
        speed.Flyout = menu;
        ToolTipService.SetToolTip(speed, L.T("播放速度"));
        UIElement[] items = [play, current, timeline.Root, total, speed];
        for (var i = 0; i < items.Length; i++)
        {
            Grid.SetColumn((FrameworkElement)items[i], i);
            root.Children.Add(items[i]);
        }
        root.BorderBrush = Ui.Divider;
        root.BorderThickness = new Thickness(0, 1, 0, 0);
        timer.Tick += (_, _) => Update();
        host.Player.StateChanged += () => host.Post(Update);
    }

    static string Label(double rate) => rate == 1 ? "1×" : $"{rate:0.##}×";

    public FrameworkElement Show(MeetingDetail value)
    {
        detail = value;
        timeline.Show(value.Segments, (value.Notes?.ReviewItems ?? []).Select(r => TimeFormat.Seconds(r.Time)).OfType<double>().ToList(),
            value.Bookmarks.Select(b => b.Time).ToList());
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
        ToolTipService.SetToolTip(play, player.IsPlaying ? L.T("暫停（空白鍵）") : L.T("播放（空白鍵）"));
        current.Text = TimeFormat.String(player.CurrentTime);
        total.Text = TimeFormat.String(duration);
        timeline.Update(player.CurrentTime, duration);
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
