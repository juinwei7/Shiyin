using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Shiyin.Localization;
using Shiyin.Model;
using Windows.UI;

namespace Shiyin.App;

/// <summary>
/// 畫面的小工具：全部用 C# 建（不用 XAML 檔，design-windows §11），這裡集中顏色、字型、常用的元件寫法。
/// 顏色同 Mac 的 Theme.swift（M§9.3）：青瓷綠是主要動作、目前播放的段落；琥珀是低信心、待確認、進行中
/// </summary>
public static class Ui
{
    static bool dark;

    /// <summary>深色、淺色模式換了：重新決定顏色（畫面會整個重畫）</summary>
    public static void SetTheme(ElementTheme theme) => dark = theme == ElementTheme.Dark;

    static Color Hex(uint rgb) => Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    static SolidColorBrush Brush(uint light, uint darkColor) => new(Hex(dark ? darkColor : light));

    public static SolidColorBrush Celadon => Brush(0x2D6A5D, 0x6DB2A1);
    public static SolidColorBrush Signal => Brush(0xB97F06, 0xE2AD3B);
    public static SolidColorBrush Danger => Brush(0xC0392B, 0xEE8E8E);
    public static Brush Primary => Resource("TextFillColorPrimaryBrush", 0x1A, 0x1A, 0x1A, 0xFF, 0xFF, 0xFF);
    public static Brush Secondary => Resource("TextFillColorSecondaryBrush", 0x5F, 0x5F, 0x5F, 0xC5, 0xC5, 0xC5);
    public static Brush Tertiary => Resource("TextFillColorTertiaryBrush", 0x8A, 0x8A, 0x8A, 0x9A, 0x9A, 0x9A);
    public static Brush CardBackground => Resource("CardBackgroundFillColorDefaultBrush", 0xFB, 0xFB, 0xFB, 0x2B, 0x2B, 0x2B);
    public static Brush Subtle => Resource("SubtleFillColorSecondaryBrush", 0xEE, 0xEE, 0xEE, 0x33, 0x33, 0x33);
    public static Brush Divider => Resource("DividerStrokeColorDefaultBrush", 0xE5, 0xE5, 0xE5, 0x3A, 0x3A, 0x3A);

    static object? Lookup(string key)
    {
        try { return Application.Current.Resources[key]; }
        catch (Exception) { return null; }
    }

    /// <summary>WinUI 的主題資源（在 XamlControlsResources 的 ThemeDictionaries 裡）；找不到時用差不多的顏色，不要因為資源名稱改了就閃退</summary>
    static Brush Resource(string key, byte lr, byte lg, byte lb, byte dr, byte dg, byte db)
    {
        var resources = Application.Current.Resources;
        if (Lookup(key) is Brush b) return b;
        var theme = dark ? "Dark" : "Light";
        foreach (var merged in resources.MergedDictionaries)
        {
            if (merged.ThemeDictionaries.TryGetValue(theme, out var t) && t is ResourceDictionary dictionary && dictionary.TryGetValue(key, out var value) && value is Brush brush)
                return brush;
            if (merged.TryGetValue(key, out var plain) && plain is Brush p) return p;
        }
        return new SolidColorBrush(dark ? Color.FromArgb(255, dr, dg, db) : Color.FromArgb(255, lr, lg, lb));
    }
    public static Color CeladonColor => Hex(0x2D6A5D);

    /// <summary>
    /// 視窗的底色：淺色白、深色 #202020（Windows 11 的 SolidBackgroundFillColorBase）。WinUI 的視窗沒設底色時深色模式是純黑
    ///（2026-10-21 朋友的電腦上看到）。每個視窗的最外層都要設，跟著深淺色換
    /// </summary>
    public static T WindowBackground<T>(T root) where T : FrameworkElement => Surface(root, 0xFFFFFF, 0x202020);

    /// <summary>主視窗的側欄：淺色 #F7F7F7、深色 #2B2B2B，跟內容區分得出來（同 Mac 的側欄）</summary>
    public static T SidebarBackground<T>(T root) where T : FrameworkElement => Surface(root, 0xF7F7F7, 0x2B2B2B);

    /// <summary>不透明的底色，跟著元件的深淺色換（建好之後不會重建的元件用）</summary>
    static T Surface<T>(T root, uint light, uint darkColor) where T : FrameworkElement
    {
        void Apply()
        {
            var brush = new SolidColorBrush(root.ActualTheme == ElementTheme.Dark ? Hex(darkColor) : Hex(light));
            switch (root)
            {
                case Panel p: p.Background = brush; break;
                case Border b: b.Background = brush; break;
                case Control c: c.Background = brush; break;
            }
        }
        Apply();
        root.ActualThemeChanged += (_, _) => Apply();
        return root;
    }

    /// <summary>
    /// 計時器、UI 執行緒回呼裡的例外：WinUI 不會走 UnhandledException，直接結束程式（CoreMessagingXP 0xc000027b）。
    /// 接住、寫進 log（同 AppHost.Post）
    /// </summary>
    public static void Guard(Action action)
    {
        try { action(); }
        catch (Exception e) { Services.Log.Error($"ui callback failed: {e.GetType().Name} 0x{e.HResult:X8}"); }
    }

    /// <summary>
    /// 強調色換成拾音的青瓷色（同 Mac 的 AccentColor：淺色 #2D6A5D、深色 #6DB2A1）。WinUI 的強調色筆刷在淺色主題用 Dark1、
    /// 深色主題用 Light2，hover、按下用相鄰的，所以七個都要給；要在 XamlControlsResources 之前加進 app 的資源
    /// </summary>
    public static void ApplyAccent(ResourceDictionary resources)
    {
        resources["SystemAccentColor"] = Hex(0x2D6A5D);
        resources["SystemAccentColorDark1"] = Hex(0x2D6A5D);
        resources["SystemAccentColorDark2"] = Hex(0x245549);
        resources["SystemAccentColorDark3"] = Hex(0x1B4037);
        resources["SystemAccentColorLight1"] = Hex(0x4E8E7F);
        resources["SystemAccentColorLight2"] = Hex(0x6DB2A1);
        resources["SystemAccentColorLight3"] = Hex(0x8FC6B8);
    }

    /// <summary>說話者的顏色（8 色循環；青瓷綠、琥珀排在最後，同 Mac）</summary>
    public static SolidColorBrush Speaker(int idx)
    {
        (uint, uint)[] palette =
        [
            (0x3F5BA9, 0x8FA8EE), (0x8E3B6B, 0xE08BBE), (0x4F7A28, 0x9BCB6E), (0x7A4FB5, 0xBFA0EE),
            (0xA23B3B, 0xEE8E8E), (0x2F7486, 0x7CC4D6), (0x9A5B13, 0xE2A65E), (0x2D6A5D, 0x6DB2A1),
        ];
        var (light, d) = palette[(Math.Max(idx, 1) - 1) % palette.Length];
        return Brush(light, d);
    }

    /// <summary>會議內容的標題字型：中文、英文新細明體，日文 Yu Mincho（design-windows §11.3；W2 截圖比較後定）</summary>
    public static FontFamily TitleFont(MeetingLanguage language) =>
        new(language == MeetingLanguage.Ja ? "Yu Mincho" : "PMingLiU, Microsoft JhengHei UI");

    /// <summary>會議內容的語言（ja-JP 時用日文字形，D20）</summary>
    public static string ContentLanguage(MeetingLanguage language) => language.Text("zh-TW", "en-US", "ja-JP");

    public static TextBlock Text(string text, double size = 14, bool bold = false, Brush? color = null, bool wrap = true, int maxLines = 0,
                                 bool select = false)
    {
        var t = new TextBlock
        {
            Text = text, FontSize = size, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis, IsTextSelectionEnabled = select,
        };
        // 用 Bold 不用 SemiBold：SemiBold 的中文、日文會被 Windows 換成明體的假粗體（2026-10-22 虛擬機的介面巡檢看到）
        if (bold) t.FontWeight = FontWeights.Bold;
        if (color is not null) t.Foreground = color;
        if (maxLines > 0) { t.MaxLines = maxLines; t.TextTrimming = TextTrimming.CharacterEllipsis; }
        return t;
    }

    public static TextBlock Caption(string text) => Text(text, 12, color: Secondary);

    /// <summary>ContentDialog 的標題：內建的標題是 SemiBold，中文會變成明體，自己給一個 Bold 的</summary>
    public static TextBlock DialogTitle(string text) => Text(text, 20, bold: true);

    public static TextBlock Heading(string text, MeetingLanguage language, double size = 18)
    {
        var t = Text(text, size, bold: true);
        t.FontFamily = TitleFont(language);
        t.Language = ContentLanguage(language);
        return t;
    }

    public static Button Button(string text, Action click, bool accent = false, string? tooltip = null)
    {
        var b = new Button { Content = text };
        if (accent && Lookup("AccentButtonStyle") is Style style) b.Style = style;
        b.Click += (_, _) => click();
        if (tooltip is not null) ToolTipService.SetToolTip(b, tooltip);
        return b;
    }

    public static Button IconButton(Symbol symbol, string tooltip, Action click)
    {
        var b = new Button { Content = new SymbolIcon(symbol), Padding = new Thickness(8, 6, 8, 6) };
        ToolTipService.SetToolTip(b, tooltip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, tooltip);
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>Segoe Fluent Icons 的圖示按鈕（Symbol 沒有的圖示，例如往下的箭頭 E74B）</summary>
    public static Button GlyphButton(string glyph, string tooltip, Action click)
    {
        var b = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 16 }, Padding = new Thickness(8, 6, 8, 6) };
        ToolTipService.SetToolTip(b, tooltip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, tooltip);
        b.Click += (_, _) => click();
        return b;
    }

    public static HyperlinkButton Link(string text, Action click)
    {
        var b = new HyperlinkButton { Content = text, Padding = new Thickness(0) };
        b.Click += (_, _) => click();
        return b;
    }

    public static StackPanel Row(double spacing, params UIElement?[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
        foreach (var c in children) if (c is not null) p.Children.Add(c);
        return p;
    }

    public static StackPanel Column(double spacing, params UIElement?[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Vertical, Spacing = spacing };
        foreach (var c in children) if (c is not null) p.Children.Add(c);
        return p;
    }

    /// <summary>兩欄：左邊照內容的寬度，右邊拿剩下的</summary>
    public static Grid Split(FrameworkElement left, FrameworkElement right, double spacing = 8, GridLength? leftWidth = null)
    {
        var g = new Grid { ColumnSpacing = spacing };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = leftWidth ?? GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        g.Children.Add(left);
        g.Children.Add(right);
        return g;
    }

    /// <summary>
    /// 文字後面接著時間點（同 Mac 的 HStack）：短的時候時間緊接在文字後面，長的時候文字換行、時間在欄位的右邊。
    /// 不能用兩個比例欄（Split 的左欄給 Star）：兩欄會各分一半
    /// </summary>
    public static FrameworkElement Inline(FrameworkElement text, UIElement? time, double spacing = 8, VerticalAlignment trailing = VerticalAlignment.Top)
    {
        if (time is not FrameworkElement t) return text;
        t.VerticalAlignment = trailing;   // 預設對齊第一行：時間是按鈕時預設上下置中，換行的條目會跑到中間
        var g = new Grid { ColumnSpacing = spacing, HorizontalAlignment = HorizontalAlignment.Left };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(t, 1);
        g.Children.Add(text);
        g.Children.Add(t);
        return g;
    }

    /// <summary>左、中（拿剩下的）、右。左欄的寬度跟著內容、不會換行：會換行的長文字放中間</summary>
    public static Grid Bar(FrameworkElement? left, FrameworkElement? middle, FrameworkElement? right, double spacing = 8)
    {
        var g = new Grid { ColumnSpacing = spacing };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (left is not null) { Grid.SetColumn(left, 0); g.Children.Add(left); }
        if (middle is not null) { Grid.SetColumn(middle, 1); g.Children.Add(middle); }
        if (right is not null) { Grid.SetColumn(right, 2); g.Children.Add(right); }
        return g;
    }

    public static ScrollViewer Scroll(UIElement content, double padding = 24) => new()
    {
        Content = new Border { Padding = new Thickness(padding), Child = content },
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };

    public static Border Card(UIElement child, double padding = 16) => new()
    {
        Child = child, Padding = new Thickness(padding), CornerRadius = new CornerRadius(8), Background = CardBackground,
        BorderBrush = Divider, BorderThickness = new Thickness(1),
    };

    public static MenuFlyoutItem Item(string text, Action click, bool enabled = true, Symbol? icon = null)
    {
        var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
        if (icon is Symbol s) item.Icon = new SymbolIcon(s);
        item.Click += (_, _) => click();
        return item;
    }

    public static MenuFlyoutSubItem Sub(string text, params MenuFlyoutItemBase[] items)
    {
        var sub = new MenuFlyoutSubItem { Text = text };
        foreach (var i in items) sub.Items.Add(i);
        return sub;
    }

    public static ToggleMenuFlyoutItem Check(string text, bool on, Action click)
    {
        var item = new ToggleMenuFlyoutItem { Text = text, IsChecked = on };
        item.Click += (_, _) => click();
        return item;
    }

    /// <summary>時間點：可以點的時候跳到錄音的那個位置播放（R4.2、R5.2）</summary>
    public static UIElement Time(string time, Action<double>? seek)
    {
        if (time.Length == 0) return new TextBlock();
        if (seek is not null && TimeFormat.Seconds(time) is double seconds)
        {
            var b = new HyperlinkButton { Content = time, Padding = new Thickness(0), FontSize = 13, Foreground = Celadon };
            ToolTipService.SetToolTip(b, Localization.L.F("從 %@ 開始播放", time));
            b.Click += (_, _) => seek(seconds);
            return b;
        }
        return Text(time, 13, color: Secondary, wrap: false);
    }

    public static ComboBox Combo<T>(IEnumerable<(T Value, string Label)> options, T selected, Action<T> changed, string? header = null)
    {
        var list = options.ToList();
        var box = new ComboBox { Header = header, MinWidth = 200 };
        foreach (var (_, label) in list) box.Items.Add(label);
        box.SelectedIndex = Math.Max(0, list.FindIndex(o => EqualityComparer<T>.Default.Equals(o.Value, selected)));
        box.SelectionChanged += (_, _) => { if (box.SelectedIndex >= 0) changed(list[box.SelectedIndex].Value); };
        return box;
    }

    public static ToggleSwitch Toggle(string header, bool on, Action<bool> changed, string? description = null)
    {
        // 開關旁的「開啟／關閉」是 WinUI 內建的文字，跟著 Windows 的顯示語言，不跟拾音的介面語言（PrimaryLanguageOverride 也改不到）：自己給
        var t = new ToggleSwitch { Header = header, IsOn = on, OnContent = MeetingLanguages.Ui("開啟", "On"), OffContent = MeetingLanguages.Ui("關閉", "Off") };
        t.Toggled += (_, _) => changed(t.IsOn);
        if (description is not null) ToolTipService.SetToolTip(t, description);
        return t;
    }
}

/// <summary>
/// 橫排，放不下的元件整個不顯示（不會被切一半）：錄音列的兩個音量條，視窗窄時只留麥克風的（2026-10-22 介面巡檢）
/// </summary>
public sealed partial class FitRow : Panel
{
    public double Spacing { get; set; }

    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size available)
    {
        double width = 0, height = 0;
        var first = true;
        foreach (var child in Children)
        {
            child.Measure(new Windows.Foundation.Size(double.PositiveInfinity, available.Height));
            if (child.Visibility == Visibility.Collapsed) continue;
            var add = child.DesiredSize.Width + (first ? 0 : Spacing);
            if (width + add > available.Width) break;
            width += add;
            height = Math.Max(height, child.DesiredSize.Height);
            first = false;
        }
        return new Windows.Foundation.Size(width, height);
    }

    protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size final)
    {
        double x = 0;
        var first = true;
        var full = false;
        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            if (child.Visibility == Visibility.Collapsed) continue;
            var add = size.Width + (first ? 0 : Spacing);
            // 放不下的放到看不見的地方（下面的 Clip 切掉），不要壓成 0：內容還是會畫出來
            if (full || x + add > final.Width) { full = true; child.Arrange(new Windows.Foundation.Rect(final.Width + 10_000, 0, size.Width, size.Height)); continue; }
            x += first ? 0 : Spacing;
            child.Arrange(new Windows.Foundation.Rect(x, (final.Height - size.Height) / 2, size.Width, size.Height));
            x += size.Width;
            first = false;
        }
        Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, final.Width, final.Height) };
        return final;
    }
}

/// <summary>
/// 閱讀的欄位（同 Mac 的 ReadingColumn）：填滿可用的寬度、最寬 maxWidth，靠左。不能用 StackPanel 設 MaxWidth 加靠左：
/// 靠左時寬度是內容要的寬度，會換行的文字算出來很窄，摘要整欄擠成一半（2026-10-21 朋友的電腦上看到）
/// </summary>
public sealed partial class ReadingColumn : Panel
{
    readonly double maxWidth;

    public ReadingColumn(UIElement child, double maxWidth)
    {
        this.maxWidth = maxWidth;
        Children.Add(child);
    }

    double ColumnWidth(double available) => double.IsInfinity(available) ? maxWidth : Math.Min(available, maxWidth);

    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size available)
    {
        var width = ColumnWidth(available.Width);
        double height = 0;
        foreach (var child in Children)
        {
            child.Measure(new Windows.Foundation.Size(width, available.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }
        return new Windows.Foundation.Size(width, height);
    }

    protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size final)
    {
        var width = ColumnWidth(final.Width);
        foreach (var child in Children) child.Arrange(new Windows.Foundation.Rect(0, 0, width, final.Height));
        return final;
    }
}
