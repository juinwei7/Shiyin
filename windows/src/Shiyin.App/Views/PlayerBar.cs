using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Shiyin.Localization;
using Shiyin.Model;
using Shiyin.Notes;
using Shiyin.Services;
using Shiyin.Services.Capture;
using Shiyin.Text;

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
    /// <summary>聽哪一邊、麥克風只播建議收錄（R119.2、R119.5、M§93.5）：只有「麥克風＋電腦聲音」的會議</summary>
    readonly DropDownButton channels = new();
    readonly Timeline timeline;
    readonly TopicBand topics;
    /// <summary>麥克風時間軸（R119.4、M§93.5）：跟錄音時間軸同一欄，時間對齊；只有「麥克風＋電腦聲音」的會議</summary>
    readonly MicTrackView mic;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    MeetingDetail? detail;
    double topicsDuration = -1;

    public event Action<double>? Tick;

    public PlayerBar(AppHost host, MainWindow window)
    {
        this.host = host;
        mic = new MicTrackView(host, window);
        mic.ZoomChanged += zoom => timeline.SetZoom(zoom);
        timeline = new Timeline((seconds, playNow) => host.Player.Seek(seconds, playNow));
        topics = new TopicBand(seconds => host.Player.Seek(seconds, true));
        foreach (var w in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto })
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        // 麥克風時間軸在第一列、議題帶在第二列（沒有時收起來，高度是 0），其他元件在第三列，跟時間軸對齊
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        // 麥克風時間軸在第一列（錄音時間軸那一欄）；收合鈕、段數放在議題帶那一列的左邊、右邊，不比議題帶高
        // （使用者：「放在議題帶的左邊跟右邊」）：時間軸收、開時那一列不動，收合鈕一直在同一個位置
        Grid.SetColumnSpan(mic.Toggle, 2);
        Grid.SetRow(mic.Toggle, 1);
        Grid.SetColumn(mic.Content, 2);
        Grid.SetColumn(mic.Info, 3);
        Grid.SetColumnSpan(mic.Info, 3);
        Grid.SetRow(mic.Info, 1);
        Grid.SetColumn(mic.ThresholdLabel, 3);
        Grid.SetColumnSpan(mic.ThresholdLabel, 3);
        foreach (var part in mic.Parts) root.Children.Add(part);
        Grid.SetColumn(topics.Root, 2);
        Grid.SetRow(topics.Root, 1);
        root.Children.Add(topics.Root);
        play.Click += (_, _) => host.Player.Toggle();
        current.Width = 52;
        current.TextAlignment = TextAlignment.Right;
        var menu = new MenuFlyout();
        foreach (var rate in new[] { 0.75, 1, 1.25, 1.5, 2 })
            menu.Items.Add(Ui.Item(Label(rate), () => { host.Player.Rate = rate; speed.Content = Label(rate); }));
        speed.Flyout = menu;
        ToolTipService.SetToolTip(speed, L.T("播放速度"));
        ToolTipService.SetToolTip(channels, L.T("聽哪一邊：兩邊、只麥克風、只電腦聲音。勾了「麥克風只播建議收錄」時，麥克風在建議收錄以外是靜音"));
        UIElement[] items = [play, current, timeline.Root, total, speed, channels];
        for (var i = 0; i < items.Length; i++)
        {
            Grid.SetColumn((FrameworkElement)items[i], i);
            Grid.SetRow((FrameworkElement)items[i], 2);
            root.Children.Add(items[i]);
        }
        root.BorderThickness = new Thickness(0, 1, 0, 0);
        timer.Tick += (_, _) => Ui.Guard(Update);
        host.Player.StateChanged += () => host.Post(Update);
    }

    static string ChannelsLabel(PlayChannels c) => c switch
    {
        PlayChannels.Mic => L.T("只麥克風"),
        PlayChannels.System => L.T("只電腦聲音"),
        _ => L.T("兩邊"),
    };

    void ShowChannels(MeetingDetail value)
    {
        var meeting = value.Meeting;
        channels.Visibility = meeting.Channels == "both" ? Visibility.Visible : Visibility.Collapsed;
        if (meeting.Channels != "both" || meeting.Id is not long id) return;
        var current = PlayChannelsText.Parse(meeting.PlayChannels);
        var gating = meeting.PlayMicRegions && current != PlayChannels.System;
        var menu = new MenuFlyout();
        foreach (var c in new[] { PlayChannels.Both, PlayChannels.Mic, PlayChannels.System })
            menu.Items.Add(Ui.Check(ChannelsLabel(c), c == current, () => host.Controller.SetPlayChannels(id, c)));
        menu.Items.Add(new MenuFlyoutSeparator());
        var only = Ui.Check(L.T("麥克風只播建議收錄"), meeting.PlayMicRegions, () => host.Controller.SetPlayMicRegions(id, !meeting.PlayMicRegions));
        // 只電腦聲音、還沒有區塊時停用；已經勾了的照樣能取消（同 Mac）
        only.IsEnabled = current != PlayChannels.System && (value.MicRegions.Count > 0 || meeting.PlayMicRegions);
        menu.Items.Add(only);
        channels.Flyout = menu;
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        if (gating) content.Children.Add(new FontIcon { Glyph = "\uE71C", FontSize = 12 });   // 篩選
        content.Children.Add(new TextBlock { Text = ChannelsLabel(current) });
        channels.Content = content;
    }

    static string Label(double rate) => rate == 1 ? "1×" : $"{rate:0.##}×";

    /// <summary>議題帶滑過的卡片（M§87、R113.3）：那個議題的重點、這段時間說話的人（說得多的在前），卡片打開時才算</summary>
    static Func<MindMap.TopicRange, (List<string> Points, List<string> Speakers)> CardInfo(MeetingDetail detail) => range =>
    {
        var points = (detail.Notes is { } notes ? MindMap.ShownTopic(notes, range.Branch)?.Points : null)?
            .Select(p => p.Text.Trim()).Where(t => t.Length > 0).ToList() ?? [];
        var talk = new Dictionary<int, double>();
        foreach (var segment in detail.Segments)
        {
            if (segment.Speaker is not int speaker) continue;
            var overlap = Math.Min(segment.End, range.End) - Math.Max(segment.Start, range.Start);
            if (overlap > 0) talk[speaker] = talk.GetValueOrDefault(speaker) + overlap;
        }
        var speakers = talk.OrderByDescending(p => p.Value).ThenBy(p => p.Key).Select(p => detail.SpeakerName(p.Key) ?? "").ToList();
        return (points, speakers);
    };

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
        ShowChannels(value);
        var both = value.Meeting.Channels == "both" && host.Paths.HasAudio(value.Meeting);
        foreach (var part in mic.Parts) part.Visibility = both ? Visibility.Visible : Visibility.Collapsed;
        if (both) mic.Show(value, Math.Max(host.Player.Duration, value.Meeting.DurationSeconds ?? 0));
        timeline.SetZoom(both ? mic.VisibleZoom : null);
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
        if (mic.Content.Visibility == Visibility.Visible) mic.Update(player.CurrentTime, duration);
        if (duration != topicsDuration)
        {
            topicsDuration = duration;
            topics.Show(detail.Notes is { } notes ? MindMap.TopicRanges(notes, duration, L.Ui(MindMap.TopicLabel(MeetingLanguages.Interface))) : [], duration,
                CardInfo(detail));
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
    /// <summary>麥克風時間軸放大看的範圍（M§93.5）：框出來</summary>
    MicZoom? zoom;
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

    public void SetZoom(MicZoom? value)
    {
        if (value == zoom) return;
        zoom = value;
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
        canvas.Children.Add(new Rectangle { Width = Math.Max(width, 0), Height = 1, Fill = Ui.Track, Margin = new Thickness(0, height / 2, 0, 0) });
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
        if (zoom is { } z)
        {
            var frame = new Rectangle
            {
                Width = Math.Max(2, z.Span * scale), Height = height - 1, RadiusX = 3, RadiusY = 3, Stroke = Ui.Celadon, StrokeThickness = 1,
                Fill = new SolidColorBrush(Ui.Celadon.Color) { Opacity = 0.08 },
            };
            Canvas.SetLeft(frame, z.Start * scale);
            Canvas.SetTop(frame, 0.5);
            canvas.Children.Add(frame);
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
    Func<MindMap.TopicRange, (List<string> Points, List<string> Speakers)>? card;
    double duration, time;
    int current = -2;

    public TopicBand(Action<double> seek)
    {
        this.seek = seek;
        Root.SizeChanged += (_, _) => Draw();
    }

    public void Show(List<MindMap.TopicRange> value, double total, Func<MindMap.TopicRange, (List<string> Points, List<string> Speakers)>? cardInfo = null)
    {
        ranges = value;
        duration = total;
        card = cardInfo;
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
            // 滑過的卡片（M§87、R113.3）：工具提示放卡片的內容，打開時才算
            var range = r;
            var tip = new ToolTip { MaxWidth = 320 };
            tip.Opened += (_, _) => tip.Content ??= Card(range);
            ToolTipService.SetToolTip(box, tip);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, label);
            Canvas.SetLeft(box, r.Start * scale);
            Root.Children.Add(box);
            boxes.Add((text, fill, hover, color));
        }
        Update(time);
    }

    /// <summary>列出幾條重點（同 Mac：原本 3 條，卡片點不到「還有 N 條」，2026-11-25 #19 改成直接列出來）</summary>
    const int MaxPoints = 8;

    /// <summary>卡片：議題名稱、時間（開始–結束・多久）、說話者、最多 8 條重點、「還有 N 條，在「紀錄」分頁的這個議題裡」、「點一下從這裡播放」</summary>
    UIElement Card(MindMap.TopicRange r)
    {
        var (points, speakers) = card?.Invoke(r) ?? ([], []);
        var column = Ui.Column(6, Ui.Text(r.Title, 13, bold: true, maxLines: 3),
            Ui.Text($"{TimeFormat.String(r.Start)}–{TimeFormat.String(r.End)}・{L.Ui(TimeFormat.Duration((int)SwiftText.Round(r.End - r.Start), MeetingLanguages.Interface))}", 12, color: Ui.Secondary));  // l10n-skip：時間
        if (speakers.Count > 0) column.Children.Add(Ui.Text(L.F("說話者：%@", MeetingLanguages.UiList(speakers)), 12, color: Ui.Secondary, maxLines: 2));
        if (points.Count > 0)
        {
            var list = Ui.Column(3);
            foreach (var point in points.Take(MaxPoints)) list.Children.Add(Ui.Text("・" + point, 12, maxLines: 2));  // l10n-skip：條列的點
            // 卡片不能點、移開就收起：多的指到紀錄分頁（#19）
            if (points.Count > MaxPoints) list.Children.Add(Ui.Text(L.F("還有 %lld 條，在「紀錄」分頁的這個議題裡", points.Count - MaxPoints), 12, color: Ui.Secondary));
            list.Margin = new Thickness(0, 2, 0, 0);
            column.Children.Add(list);
        }
        column.Children.Add(Ui.Text(L.T("點一下從這裡播放"), 11, color: Ui.Tertiary));
        return column;
    }
}
