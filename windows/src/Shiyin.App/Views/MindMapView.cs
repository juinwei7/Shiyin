using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Shiyin.Localization;
using Shiyin.Model;
using Shiyin.Notes;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Shiyin.App;

/// <summary>
/// 心智圖（R67、design-windows §11.10）：照 MindMap.LayoutOf 的座標畫（同 Mac 的 MindMapView）。
/// 匯出（exporting）時白底、淺色、時間是一般的字；畫面上跟著深色模式，時間點了播放
/// </summary>
public static class MindMapView
{
    static SolidColorBrush Brush(string hex) => new(Ui.HexColor(hex));

    public static Canvas Build(MindMap.Layout layout, MeetingLanguage language, bool exporting, Action<double>? seek)
    {
        var dark = !exporting && Ui.IsDark;
        var canvas = new Canvas { Width = layout.Width, Height = layout.Height, Background = exporting ? new SolidColorBrush(Colors.White) : new SolidColorBrush(Colors.Transparent) };
        foreach (var e in layout.Edges)
        {
            var mid = (e.X0 + e.X1) / 2;
            var figure = new PathFigure { StartPoint = new Windows.Foundation.Point(e.X0, e.Y0) };
            figure.Segments.Add(new BezierSegment
            {
                Point1 = new Windows.Foundation.Point(mid, e.Y0), Point2 = new Windows.Foundation.Point(mid, e.Y1), Point3 = new Windows.Foundation.Point(e.X1, e.Y1),
            });
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            canvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = geometry, Stroke = Brush(MindMap.Color(e.Branch, dark)), StrokeThickness = e.From == 0 ? 2 : 1.5 });
        }
        foreach (var b in layout.Boxes)
        {
            var node = Node(b, language, dark, exporting, seek);
            Canvas.SetLeft(node, b.X);
            Canvas.SetTop(node, b.Y);
            canvas.Children.Add(node);
        }
        return canvas;
    }

    static FrameworkElement Node(MindMap.Box b, MeetingLanguage language, bool dark, bool exporting, Action<double>? seek)
    {
        var s = b.Style;
        var text = new TextBlock
        {
            FontSize = s.Size, LineHeight = s.LineHeight, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextAlignment = b.Side == 0 ? TextAlignment.Center : b.Side > 0 ? TextAlignment.Left : TextAlignment.Right,
            Foreground = b.Level == 0 ? new SolidColorBrush(Colors.White) : exporting ? Brush(MindMap.TextColor) : Ui.Primary,
            Language = language.Profile.Fonts.ContentWindows, TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Top,
        };
        // 根用標題字型（同 Ui.Heading）；第一層用內文字型：新細明體、Batang 沒有粗體，縮小到 9 px 左右時系統把點陣字加粗，筆畫糊成一團
        // （2026-11-06 虛擬機 100%）。用 Bold 不用 SemiBold（同 Ui.Text）：正黑體沒有 SemiBold，會被換成新細明體的假粗體
        if (b.Level < 2)
        {
            text.FontFamily = b.Level == 0 ? Ui.TitleFont(language) : Ui.BodyFont(language);
            text.FontWeight = FontWeights.Bold;
        }
        for (var i = 0; i < b.Lines.Count; i++)
        {
            if (i > 0) text.Inlines.Add(new LineBreak());
            text.Inlines.Add(new Run { Text = b.Lines[i] });
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        FrameworkElement? time = null;
        if (b.Time.Length > 0)
        {
            time = !exporting && seek is not null && TimeFormat.Seconds(b.Time) is double seconds
                ? new HyperlinkButton
                {
                    Content = b.Time, Padding = new Thickness(0), MinWidth = 0, MinHeight = 0, BorderThickness = new Thickness(0),
                    FontSize = MindMap.TimeSize, Foreground = Ui.Celadon,
                }
                : new TextBlock { Text = b.Time, FontSize = MindMap.TimeSize, Foreground = exporting ? Brush(MindMap.SecondaryColor) : Ui.Secondary };
            if (time is HyperlinkButton link && TimeFormat.Seconds(b.Time) is double at) link.Click += (_, _) => seek!(at);
            time.VerticalAlignment = VerticalAlignment.Top;
            time.Height = s.LineHeight;
            time.Margin = b.Side < 0 ? new Thickness(MindMap.TimeGap, 0, 0, 0) : new Thickness(0, 0, MindMap.TimeGap, 0);
        }
        if (b.Side < 0) { row.Children.Add(text); if (time is not null) row.Children.Add(time); }
        else { if (time is not null) row.Children.Add(time); row.Children.Add(text); }
        row.HorizontalAlignment = b.Side == 0 ? HorizontalAlignment.Center : b.Side > 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        var border = new Border
        {
            Width = b.W, Height = b.H, Padding = new Thickness(s.PadH, s.PadV, s.PadH, s.PadV), Child = row,
        };
        if (b.Level == 0)
        {
            border.Background = Brush(MindMap.RootColor);
            border.CornerRadius = new CornerRadius(12);
        }
        else if (b.Level == 1)
        {
            border.Background = exporting ? new SolidColorBrush(Colors.White) : Ui.CardBackground;
            border.BorderBrush = Brush(MindMap.Color(b.Branch, dark));
            // Border 的框線畫在內距裡面、會佔掉文字的空間（Mac 的框線疊在上面不佔）：內距扣掉框線，文字才放得下（2026-11-06 粗體標題的左右被切掉）
            border.BorderThickness = new Thickness(1.5);
            border.Padding = new Thickness(s.PadH - 1.5, s.PadV - 1.5, s.PadH - 1.5, s.PadV - 1.5);
            border.CornerRadius = new CornerRadius(8);
        }
        return border;
    }

    /// <summary>
    /// 畫面上：預設比畫面寬時縮小到放得下（最小 0.6 倍）；使用者縮放過（R91）就用他選的。再乘上文字大小（R59）；
    /// 比畫面寬時左右捲，上下跟著整頁捲（同 Mac）。上方靠右是縮放的按鈕，Ctrl＋滾輪也能縮放（design-windows §11.24）。
    /// 縮放用 Viewbox（不用 ScrollViewer 的 ZoomFactor：放在上下捲的頁面裡，高度會照沒縮放的算，下面空一大塊）；
    /// 縮放時只改 Viewbox 的大小和按鈕，不整頁重畫。page：整個心智圖頁的 ScrollViewer（Ctrl＋滾輪在整頁都有作用；滑鼠在圖上時一般的滾輪捲整頁）
    /// </summary>
    public static FrameworkElement Scrolling(MindMap.Layout layout, MeetingLanguage language, Action<double>? seek, MindMapZoom zoom,
                                             ScrollViewer? page = null)
    {
        var box = new Viewbox
        {
            Child = Build(layout, language, exporting: false, seek), Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Width = layout.Width, Height = layout.Height,
        };
        var scroll = new ScrollViewer
        {
            Content = box, HorizontalScrollMode = ScrollMode.Enabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        // 縮小、目前的百分比（按了回到符合寬度）、放大（同 Mac 的 MindMapZoomControl）：沒有框，字是次要文字的顏色
        var minus = ZoomButton(new FontIcon { Glyph = "\uE71F", FontSize = 14 }, L.T("縮小"), L.T("縮小"));
        var percent = ZoomButton("100%", L.T("符合寬度"), L.T("回到符合寬度"));
        percent.MinWidth = 44;
        percent.FontSize = 13;
        var plus = ZoomButton(new FontIcon { Glyph = "\uE8A3", FontSize = 14 }, L.T("放大"), L.T("放大"));
        void Apply()
        {
            var total = zoom.Scale * Ui.ContentScale;
            box.Width = layout.Width * total;
            box.Height = layout.Height * total;
            var text = $"{(int)Math.Round(zoom.Scale * 100, MidpointRounding.AwayFromZero)}%";
            if (percent.Content as string != text) percent.Content = text;
            minus.IsEnabled = zoom.Scale > MindMapZoom.Min + 0.001;
            plus.IsEnabled = zoom.Scale < MindMapZoom.Max - 0.001;
        }
        void Step(int steps)
        {
            zoom.Value = MindMapZoom.Snap(zoom.Scale + steps * MindMapZoom.StepSize);
            Apply();
        }
        minus.Click += (_, _) => Step(-1);
        plus.Click += (_, _) => Step(1);
        percent.Click += (_, _) => { zoom.Value = null; Apply(); };
        scroll.SizeChanged += (_, e) =>
        {
            if (e.NewSize.Width <= 0) return;
            zoom.Fit = Math.Max(0.6, Math.Min(1, (e.NewSize.Width - 16) / layout.Width));
            Apply();
        };
        // Ctrl＋滾輪（Mac 是觸控板兩指開合，R91.2）：滑鼠滾輪一格（120）縮放一次；精確式觸控板的捏合送的量比較小，累積到一格才動。
        // 接在整個心智圖頁上：縮小後心智圖只有一小塊，滑鼠不在圖上也要能放大（2026-11-16 虛擬機上只接在圖上時，25% 之後很難捲到）。
        // ScrollViewer 可能已經把滾輪事件標成處理過，所以 handledEventsToo；標成處理過，整頁的 ScrollViewer 才不會跟著上下捲
        var wheel = 0;
        UIElement area = page is not null ? page : scroll;
        area.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, e) =>
        {
            if ((e.KeyModifiers & Windows.System.VirtualKeyModifiers.Control) == 0) return;
            e.Handled = true;
            wheel += e.GetCurrentPoint(area).Properties.MouseWheelDelta;
            var steps = wheel / 120;
            if (steps == 0) return;
            wheel -= steps * 120;
            Step(steps);
        }), true);
        // 滑鼠在圖上時（R91.3：上下跟著整頁捲）：左右捲的 ScrollViewer 不能上下捲，會把一般的滾輪拿去左右捲（放大之後圖比畫面寬，
        // 滑鼠在圖上就捲不動整頁，2026-11-16 虛擬機）。在圖上先接住：一般的滾輪自己捲整頁（一格 100，同瀏覽器），
        // Shift＋滾輪、橫向的滾輪照舊左右捲；Ctrl 的交給上面的縮放（handledEventsToo 收得到）
        box.PointerWheelChanged += (_, e) =>
        {
            var wheelPoint = e.GetCurrentPoint(box).Properties;
            if ((e.KeyModifiers & Windows.System.VirtualKeyModifiers.Control) != 0) { e.Handled = true; return; }
            if (page is null || (e.KeyModifiers & Windows.System.VirtualKeyModifiers.Shift) != 0 || wheelPoint.IsHorizontalMouseWheel) return;
            e.Handled = true;
            page.ChangeView(null, Math.Max(0, page.VerticalOffset - wheelPoint.MouseWheelDelta * 100.0 / 120), null);
        };
        Apply();
        var bar = Ui.Row(2, minus, percent, plus);
        bar.HorizontalAlignment = HorizontalAlignment.Right;
        bar.Margin = new Thickness(24, 0, 24, 4);
        var column = new StackPanel();
        column.Children.Add(bar);
        column.Children.Add(scroll);
        return column;
    }

    static Button ZoomButton(object content, string name, string tooltip)
    {
        var b = Ui.Borderless(new Button { Content = content, MinHeight = 0 }, Ui.Secondary);
        b.Padding = new Thickness(8, 4, 8, 4);
        ToolTipService.SetToolTip(b, tooltip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, name);
        return b;
    }
}

/// <summary>
/// 心智圖的縮放（R91、design-windows §11.24，同 Mac 的 MindMapView.zoom）：Value 是使用者選的倍數（null 是預設＝符合寬度），
/// Fit 是畫面上算出來的符合寬度。放在 NotesTabs（主視窗整頁重畫時留著）；換會議、換分頁、切回條列、編輯時回到預設
/// </summary>
public sealed class MindMapZoom
{
    public const double Min = 0.25, Max = 3, StepSize = 0.25;

    public double? Value { get; set; }
    public double Fit { get; set; } = 1;
    public double Scale => Value ?? Fit;

    /// <summary>對齊到 25% 的格子、夾在 25%～300%（預設的倍數不在格子上時，第一下就對齊；同 Mac 的 rounded()，0.5 進位）</summary>
    public static double Snap(double value) => Math.Clamp(Math.Round(value * 4, MidpointRounding.AwayFromZero) / 4, Min, Max);
}

/// <summary>匯出、複製心智圖的 PNG（design-windows §11.10）：匯出模式的 Canvas 放進看不到的 Popup，RenderTargetBitmap 畫成 2 倍</summary>
public static class MindMapImage
{
    /// <summary>主視窗的 XamlRoot（Program 建好主視窗時設，同 Pdf.Window）</summary>
    public static Func<XamlRoot?> Root { get; set; } = () => null;

    public static async Task<byte[]> Png(MindMap.Layout layout, MeetingLanguage language)
    {
        var root = Root() ?? throw new Services.AppController.ExportException(L.T("匯出失敗"));
        var canvas = MindMapView.Build(layout, language, exporting: true, seek: null);
        var popup = new Popup { Child = canvas, XamlRoot = root, HorizontalOffset = -layout.Width - 100000, IsHitTestVisible = false };
        popup.IsOpen = true;
        try
        {
            canvas.UpdateLayout();
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(canvas, (int)Math.Ceiling(layout.Width * 2), (int)Math.Ceiling(layout.Height * 2));
            var pixels = await bitmap.GetPixelsAsync();
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 192, 192,
                System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(pixels));
            await encoder.FlushAsync();
            var bytes = new byte[stream.Size];
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(bytes);
            return bytes;
        }
        finally
        {
            popup.IsOpen = false;
            popup.Child = null;
        }
    }
}
