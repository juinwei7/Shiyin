using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
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
    /// 畫面上：比畫面寬時縮小到放得下（最小 0.6 倍），再乘上文字大小（R59）；還是比畫面寬時左右捲，上下跟著整頁捲（同 Mac）。
    /// 縮放用 Viewbox（不用 ScrollViewer 的 ZoomFactor：放在上下捲的頁面裡，高度會照沒縮放的算，下面空一大塊）
    /// </summary>
    public static FrameworkElement Scrolling(MindMap.Layout layout, MeetingLanguage language, Action<double>? seek)
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
        scroll.SizeChanged += (_, e) =>
        {
            if (e.NewSize.Width <= 0) return;
            var zoom = Math.Max(0.6, Math.Min(1, (e.NewSize.Width - 16) / layout.Width)) * Ui.ContentScale;
            box.Width = layout.Width * zoom;
            box.Height = layout.Height * zoom;
        };
        return scroll;
    }
}

/// <summary>匯出、複製心智圖的 PNG（design-windows §11.10）：匯出模式的 Canvas 放進看不到的 Popup，RenderTargetBitmap 畫成 2 倍</summary>
public static class MindMapImage
{
    /// <summary>主視窗的 XamlRoot（Program 建好主視窗時設，同 Pdf.Window）</summary>
    public static Func<XamlRoot?> Root { get; set; } = () => null;

    public static async Task<byte[]> Png(MindMap.Layout layout, MeetingLanguage language)
    {
        var root = Root() ?? throw new Services.AppController.ExportException(Localization.L.T("匯出失敗"));
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
