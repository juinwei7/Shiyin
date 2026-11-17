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
    public static bool IsDark => dark;

    /// <summary>"#RRGGBB"</summary>
    public static Color HexColor(string hex) => Hex(Convert.ToUInt32(hex.TrimStart('#'), 16));

    static Color Hex(uint rgb) => Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    /// <summary>
    /// 自己的顏色（青瓷綠、琥珀、紅、說話者 8 色）：對比主題（高對比）時換成系統色（design-windows §11.3）。
    /// 對比主題只有幾個系統色（視窗底、文字、反白、反白的字、超連結、停用的字、按鈕），使用者選的主題就是要這幾個顏色，
    /// 自己的顏色在「夜空」的黑底上看不見（深的青瓷綠），在「沙漠」的米色底上對比不夠
    /// </summary>
    static SolidColorBrush Brush(uint light, uint darkColor, Windows.UI.ViewManagement.UIElementType contrast) =>
        HighContrast ? SystemBrush(contrast) : new(Hex(dark ? darkColor : light));

    /// <summary>青瓷綠：主要的動作、完成、目前的段落。對比主題時是反白的顏色（同 WinUI 的強調色在對比主題的做法）</summary>
    public static SolidColorBrush Celadon => Brush(0x2D6A5D, 0x6DB2A1, Windows.UI.ViewManagement.UIElementType.Highlight);
    /// <summary>琥珀：低信心、待確認、進行中。對比主題時是文字的顏色（靠圖形、文字分辨，不靠顏色）</summary>
    public static SolidColorBrush Signal => Brush(0xB97F06, 0xE2AD3B, Windows.UI.ViewManagement.UIElementType.WindowText);
    /// <summary>紅：失敗、刪除、錄音中。對比主題時是文字的顏色</summary>
    public static SolidColorBrush Danger => Brush(0xC0392B, 0xEE8E8E, Windows.UI.ViewManagement.UIElementType.WindowText);
    public static Brush Primary => HighContrast ? SystemBrush(Windows.UI.ViewManagement.UIElementType.WindowText) : Resource("TextFillColorPrimaryBrush", 0x1A, 0x1A, 0x1A, 0xFF, 0xFF, 0xFF);
    public static Brush Secondary => HighContrast ? SystemBrush(Windows.UI.ViewManagement.UIElementType.WindowText) : Resource("TextFillColorSecondaryBrush", 0x5F, 0x5F, 0x5F, 0xC5, 0xC5, 0xC5);
    public static Brush Tertiary => HighContrast ? SystemBrush(Windows.UI.ViewManagement.UIElementType.WindowText) : Resource("TextFillColorTertiaryBrush", 0x8A, 0x8A, 0x8A, 0x9A, 0x9A, 0x9A);
    public static Brush CardBackground => HighContrast ? SystemBrush(Windows.UI.ViewManagement.UIElementType.Window) : Resource("CardBackgroundFillColorDefaultBrush", 0xFB, 0xFB, 0xFB, 0x2B, 0x2B, 0x2B);
    /// <summary>淡淡的底色（滑過的按鈕、狀態條、「自動」的膠囊）。對比主題時透明（看得到視窗的底色）：系統色的底上再疊一層，字就不是系統色配系統色了</summary>
    public static Brush Subtle => HighContrast ? new SolidColorBrush(Microsoft.UI.Colors.Transparent) : Resource("SubtleFillColorSecondaryBrush", 0xEE, 0xEE, 0xEE, 0x33, 0x33, 0x33);
    /// <summary>圖形沒亮的部分（音量條沒亮的格子、引導還沒到的步驟、播放列的底線）：平常是 Subtle，對比主題時是停用的字的顏色（透明的話看不見）</summary>
    public static Brush Track => HighContrast ? SystemBrush(Windows.UI.ViewManagement.UIElementType.GrayText) : Subtle;
    public static Brush Divider => HighContrast ? SystemBrush(Windows.UI.ViewManagement.UIElementType.WindowText) : Resource("DividerStrokeColorDefaultBrush", 0xE5, 0xE5, 0xE5, 0x3A, 0x3A, 0x3A);
    /// <summary>強調色的文字（強調色＝青瓷綠，ApplyAccent）。對比主題時是反白的顏色</summary>
    public static Brush AccentText => HighContrast ? SystemBrush(Windows.UI.ViewManagement.UIElementType.Highlight) : Resource("AccentTextFillColorPrimaryBrush", 0x24, 0x55, 0x49, 0x8F, 0xC6, 0xB8);
    /// <summary>強調色的填色（「我」的外圈）</summary>
    public static Brush AccentFill => HighContrast ? SystemBrush(Windows.UI.ViewManagement.UIElementType.Highlight) : Resource("AccentFillColorDefaultBrush", 0x2D, 0x6A, 0x5D, 0x6D, 0xB2, 0xA1);
    /// <summary>畫在青瓷綠實心上面的字、打勾：內容的底色（淺色白、深色 #202020，同 Mac 鏤空的 checkmark.circle.fill）；對比主題時是反白的字的顏色</summary>
    public static Brush OnCeladon => HighContrast ? SystemBrush(Windows.UI.ViewManagement.UIElementType.HighlightText) : new SolidColorBrush(dark ? Hex(0x202020) : Hex(0xFFFFFF));

    /// <summary>系統色（對比主題的那幾個顏色；UISettings 跟 GetSysColor 同一份）</summary>
    public static Color SystemColor(Windows.UI.ViewManagement.UIElementType type)
    {
        try { return ColorSettings.UIElementColor(type); }
        catch (System.Runtime.InteropServices.COMException) { return type == Windows.UI.ViewManagement.UIElementType.Window ? Hex(0xFFFFFF) : Hex(0x000000); }
    }

    public static SolidColorBrush SystemBrush(Windows.UI.ViewManagement.UIElementType type) => new(SystemColor(type));

    /// <summary>
    /// 一層淡淡的顏色（建議條、狀態條、系列頁圖示的圓底、待確認的標記）。對比主題時透明：字、圖示是系統色，底也要是系統色（視窗的底色）
    /// </summary>
    public static Brush Tint(Color color, double opacity) =>
        HighContrast ? new SolidColorBrush(Microsoft.UI.Colors.Transparent) : new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * opacity), color.R, color.G, color.B));

    /// <summary>
    /// 有底色的一塊（建議條、狀態條、提問的回答）：平常是 background；對比主題時沒有底色，改成一圈文字顏色的細框，
    /// 才分得出是一塊（同 Windows 設定的卡片在對比主題的樣子）
    /// </summary>
    public static Border Tinted(Border border, Brush background)
    {
        if (HighContrast)
        {
            border.Background = null;
            border.BorderBrush = SystemBrush(Windows.UI.ViewManagement.UIElementType.WindowText);
            border.BorderThickness = new Thickness(1);
        }
        else border.Background = background;
        return border;
    }

    /// <summary>
    /// 對比主題時，element 裡的字和圖示不用自己給的顏色，跟著外面的元件：清單的列（選到、滑過時是反白的字，其他是文字的顏色）、
    /// 超連結按鈕（超連結的顏色）。自己給的系統色（文字的顏色）在選到的列（反白的底）上對比不夠。
    /// 按鈕這類控制項自己會處理對比主題，有自己底色的一小塊（頭像、資料夾齒輪的小圓）留著自己的顏色。不是對比主題時什麼都不做
    /// </summary>
    public static T FollowForeground<T>(T element) where T : UIElement
    {
        if (HighContrast) ClearForeground(element);
        return element;
    }

    static void ClearForeground(UIElement element)
    {
        switch (element)
        {
            case TextBlock t: t.ClearValue(TextBlock.ForegroundProperty); break;
            case IconElement i: i.ClearValue(IconElement.ForegroundProperty); break;
            case Control: break;
            case Border { Background: not null }: break;
            case Border b: if (b.Child is not null) ClearForeground(b.Child); break;
            case Panel p: foreach (var child in p.Children) ClearForeground(child); break;
        }
    }

    /// <summary>
    /// 對比主題開關、換了一個對比主題時（系統色換了）：在 UI 執行緒做 action（重畫、重新給顏色）。一般的深淺色換了看 ActualThemeChanged，這裡不管
    ///（ContrastKey 沒變時不做）。AccessibilitySettings.HighContrastChanged 在沒打包的桌面 app 訂閱時丟 0x80070490（2026-11-15 虛擬機啟動就當掉），
    /// 改聽 UISettings.ColorValuesChanged（不在 UI 執行緒）。訂閱失敗就算了，重新開啟拾音時照樣對；元件從畫面拿掉就不再聽
    /// </summary>
    public static void OnContrastChanged(FrameworkElement root, Action action)
    {
        var last = ContrastKey;
        var queue = root.DispatcherQueue;
        Windows.Foundation.TypedEventHandler<Windows.UI.ViewManagement.UISettings, object> changed = (_, _) => queue?.TryEnqueue(() => Guard(() =>
        {
            var key = ContrastKey;
            if (key == last) return;
            last = key;
            action();
        }));
        try
        {
            ColorSettings.ColorValuesChanged += changed;
            root.Unloaded += (_, _) => { try { ColorSettings.ColorValuesChanged -= changed; } catch (System.Runtime.InteropServices.COMException) { } };
        }
        catch (System.Runtime.InteropServices.COMException e) { Services.Log.Error($"color settings subscribe failed: 0x{e.HResult:X8}"); }
    }

    /// <summary>對比主題的樣子：沒開時空字串，開著時是用到的系統色（換了一個對比主題也要重畫）</summary>
    public static string ContrastKey
    {
        get
        {
            highContrast = ReadHighContrast();
            if (!HighContrast) return "";
            var types = new[]
            {
                Windows.UI.ViewManagement.UIElementType.Window, Windows.UI.ViewManagement.UIElementType.WindowText, Windows.UI.ViewManagement.UIElementType.Highlight,
                Windows.UI.ViewManagement.UIElementType.HighlightText, Windows.UI.ViewManagement.UIElementType.Hotlight, Windows.UI.ViewManagement.UIElementType.GrayText,
                Windows.UI.ViewManagement.UIElementType.ButtonText,
            };
            return "hc" + string.Join(',', types.Select(t => SystemColor(t).ToString()));
        }
    }

    /// <summary>畫面的顏色跟著什麼：深淺色和對比主題（列表、人物頁、系列頁的「內容一樣時不重建」要算進去）</summary>
    public static string ThemeKey => (dark ? "dark" : "light") + ContrastKey;

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
    public static Color SignalColor => Hex(0xB97F06);

    /// <summary>
    /// 視窗的底色：淺色白、深色 #202020（Windows 11 的 SolidBackgroundFillColorBase）。WinUI 的視窗沒設底色時深色模式是純黑
    ///（2026-10-21 朋友的電腦上看到）。每個視窗的最外層都要設，跟著深淺色換
    /// </summary>
    public static T WindowBackground<T>(T root) where T : FrameworkElement => Surface(InterfaceLanguage(root), 0xFFFFFF, 0x202020);

    /// <summary>主視窗的側欄：淺色 #F7F7F7、深色 #2B2B2B，跟內容區分得出來（同 Mac 的側欄）</summary>
    public static T SidebarBackground<T>(T root) where T : FrameworkElement => Surface(root, 0xF7F7F7, 0x2B2B2B);

    /// <summary>側欄的底色（建好之後會重建的小元件用，例如資料夾齒輪的小圓）：對比主題時是系統的視窗底色</summary>
    public static SolidColorBrush SidebarColor =>
        HighContrast ? SystemBrush(Windows.UI.ViewManagement.UIElementType.Window) : new(Hex(dark ? 0x2B2B2Bu : 0xF7F7F7u));

    static readonly Windows.UI.ViewManagement.AccessibilitySettings Accessibility = new();
    /// <summary>要一直留著同一個實例，ColorValuesChanged 才會一直收到</summary>
    static readonly Windows.UI.ViewManagement.UISettings ColorSettings = new();

    /// <summary>
    /// Windows 的對比主題（高對比）開著。每個顏色都會問（建一頁要問上千次），記住讀到的值；ContrastKey 每次重讀
    ///（OnContrastChanged 先算 ContrastKey 再重畫，重畫時就是新的值）
    /// </summary>
    public static bool HighContrast => highContrast ??= ReadHighContrast();
    static bool? highContrast;

    static bool ReadHighContrast()
    {
        try { return Accessibility.HighContrast; }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidCastException) { return false; }
    }

    /// <summary>
    /// 不透明的底色，跟著元件的深淺色換（建好之後不會重建的元件用）。對比主題（高對比）時用系統的視窗底色
    ///（UISettings 的 Window 顏色，同 SystemColorWindowColor）：不然平台的控制項是對比主題的顏色、底色還是淺色，文字都被加上底板
    /// </summary>
    static T Surface<T>(T root, uint light, uint darkColor) where T : FrameworkElement
    {
        void Apply()
        {
            var color = HighContrast ? SystemColor(Windows.UI.ViewManagement.UIElementType.Window)
                : root.ActualTheme == ElementTheme.Dark ? Hex(darkColor) : Hex(light);
            var brush = new SolidColorBrush(color);
            switch (root)
            {
                case Panel p: p.Background = brush; break;
                case Border b: b.Background = brush; break;
                case Control c: c.Background = brush; break;
            }
        }
        Apply();
        root.ActualThemeChanged += (_, _) => Apply();
        // 對比主題開、關，換了一個對比主題
        OnContrastChanged(root, Apply);
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

    /// <summary>說話者的顏色（8 色循環；青瓷綠、琥珀排在最後，同 Mac），也是頭像的「釉色」（M§58.1，Core 的 Avatars.Palette）</summary>
    public static SolidColorBrush Speaker(int idx)
    {
        var (light, d) = Avatars.Speaker(idx);
        // 對比主題：說話者都是文字的顏色（分辨靠名字）
        return Brush(light, d, Windows.UI.ViewManagement.UIElementType.WindowText);
    }

    /// <summary>人物頁、側欄的頭像顏色（M§58.1）：照名字算，同一個名字每次都一樣</summary>
    public static SolidColorBrush Person(string name) => Speaker(Avatars.PersonIndex(name));

    /// <summary>
    /// 頭像（M§58.2、R83，design-windows §11.22）：圓形，底色是釉色 18% 不透明，字是釉色（粗體，字級是直徑的 0.46）；
    /// 「我」（R80）外圈一圈青瓷綠。尺寸：逐字稿、待辦 18、會議頁上方 20、側欄 26、人物頁 56（會議內容裡的用 Ui.C 跟著文字大小）
    /// </summary>
    public static Border Avatar(string name, SolidColorBrush color, double size, bool isMe = false)
    {
        var text = new TextBlock
        {
            Text = Avatars.Initials(name), FontSize = Math.Round(size * 0.46, 1), FontWeight = FontWeights.Bold, Foreground = color,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextLineBounds = TextLineBounds.Tight,
            TextTrimming = TextTrimming.Clip, MaxLines = 1,
        };
        var c = color.Color;
        // Border 的框線畫在裡面（不佔外面的寬度）：外圈用 BorderThickness，大小照樣是直徑
        var ring = isMe ? Math.Min(2, Math.Max(1.5, size / 14)) : 0;   // 細的一圈，最多 2（同 Mac）
        var avatar = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Child = text,
            Background = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * 0.18), c.R, c.G, c.B)),
            BorderBrush = isMe ? AccentFill : null, BorderThickness = new Thickness(ring),
        };
        if (HighContrast)
        {
            // 對比主題：視窗底色的圓、文字顏色的字和外圈（「我」的外圈粗一點）。釉色 18% 的底在黑底上看不見；
            // 底色不透明，放在選到的列（反白的底）上也看得清楚（FollowForeground 跳過有底色的一塊）
            text.Foreground = SystemBrush(Windows.UI.ViewManagement.UIElementType.WindowText);
            avatar.Background = SystemBrush(Windows.UI.ViewManagement.UIElementType.Window);
            avatar.BorderBrush = text.Foreground;
            avatar.BorderThickness = new Thickness(isMe ? 2 : 1);
        }
        // 名字就在旁邊，螢幕閱讀器不用再唸一次頭像
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(avatar, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        return avatar;
    }

    /// <summary>
    /// 會議內容的文字大小（R59、design-windows §11.7）：設定的倍率（85%～200%）。會議內容（紀錄、逐字稿、提問、即時逐字稿、我的筆記）
    /// 建元件時用 <see cref="C"/> 乘上去；側欄、按鈕、提示列不用。設定改了之後主視窗重畫
    /// </summary>
    public static double ContentScale { get; set; } = 1;

    /// <summary>會議內容的字級、欄寬乘上文字大小的倍率</summary>
    public static double C(double size) => Math.Round(size * ContentScale, 1);

    /// <summary>會議內容的標題字型：中文、英文新細明體，日文 Yu Mincho（design-windows §11.3；W2 截圖比較後定），語言檔的 fonts.titleWindows</summary>
    public static FontFamily TitleFont(MeetingLanguage language) => new(language.Profile.Fonts.TitleWindows);

    /// <summary>內文的字型（語言檔 fonts.htmlWindows.body 去掉引號和 sans-serif）：要粗體又不能用明體時用（明體沒有粗體，系統加粗後會糊）</summary>
    public static FontFamily BodyFont(MeetingLanguage language) =>
        new(string.Join(", ", language.Profile.Fonts.HtmlWindows.Body.Split(',').Select(n => n.Trim().Trim('"')).Where(n => n.Length > 0 && n is not ("serif" or "sans-serif"))));

    /// <summary>會議內容的語言（ja-JP 時用日文字形，D20），語言檔的 fonts.contentWindows</summary>
    /// <summary>會議內容的語言標記（挑字型）：中文會議跟著「中文的字」（R72.5、M§48.3），簡體是 zh-CN</summary>
    public static string ContentLanguage(MeetingLanguage language) =>
        language == MeetingLanguage.Zh && AppHost.Current.Settings.SimplifiedChinese(language) ? "zh-CN" : language.Profile.Fonts.ContentWindows;  // l10n-skip

    /// <summary>簡體介面時視窗、對話框的語言標記設成 zh-CN：WinUI 依語言挑中文字型（微軟雅黑），不然簡體字用繁體的字型（M§48.3）</summary>
    public static T InterfaceLanguage<T>(T element) where T : FrameworkElement
    {
        if (L.Simplified) element.Language = "zh-CN";  // l10n-skip
        return element;
    }

    /// <summary>
    /// SplitButton 放開 Enter、空白鍵就執行（不管有沒有在它上面按下）。別的地方按 Enter 存檔、那個輸入框被拿掉時，焦點落到視窗的第一個元件
    /// （錄音按鈕），放開的 Enter 就開始錄音（2026-11-16 虛擬機上就地改紀錄時發生）。只有按下也在這個按鈕上時才讓放開生效
    /// </summary>
    public static T IgnoreStrayKeyUp<T>(T button) where T : Control
    {
        var down = Windows.System.VirtualKey.None;
        button.PreviewKeyDown += (_, e) => down = e.Key;
        button.PreviewKeyUp += (_, e) =>
        {
            if (e.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space && down != e.Key) e.Handled = true;
            down = Windows.System.VirtualKey.None;
        };
        button.LostFocus += (_, _) => down = Windows.System.VirtualKey.None;
        return button;
    }

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

    /// <summary>
    /// 沒有框和底色的按鈕（同 Mac 的 .menuStyle(.borderlessButton)，M§59）：文字、圖示青瓷綠，滑過去才有淡淡的底色。
    /// 會議頁上方的「會議設定」、文件選單用：跟資訊列同一層，不比標題搶眼。
    /// foreground：字的顏色換掉（心智圖的縮放用次要文字的顏色，同 Mac 的 .secondary，design-windows §11.24）
    /// </summary>
    public static T Borderless<T>(T button, Brush? foreground = null) where T : Button
    {
        if (HighContrast)
        {
            // 對比主題：照平台的按鈕（按鈕的底色、字、框，滑過去反白）。沒有框的按鈕在對比主題裡看起來跟一般的字一樣，
            // 滑過去的底（Subtle）和字（反白的顏色）也不是成對的系統色。開著 app 打開對比主題時拿掉原本的覆寫
            foreach (var key in BorderlessKeys) button.Resources.Remove(key);
            button.ClearValue(Control.BackgroundProperty);
            button.ClearValue(Control.ForegroundProperty);
            button.ClearValue(Control.BorderThicknessProperty);
            button.Padding = new Thickness(6, 3, 6, 3);
            return button;
        }
        // 平台的按鈕加輕量的樣式覆寫（不換 ControlTemplate）：範本各狀態讀的主題資源換成「subtle」那一組
        //（跟 ListView 的列、NavigationView 的項目同樣的透明底，滑過去、按下才有底色），文字是強調色的文字
        var clear = Resource("SubtleFillColorTransparentBrush", 0, 0, 0, 0, 0, 0);
        foreach (var key in new[] { "ButtonBackground", "ButtonBackgroundDisabled", "ButtonBorderBrush", "ButtonBorderBrushPointerOver", "ButtonBorderBrushPressed", "ButtonBorderBrushDisabled" })
            button.Resources[key] = clear;
        button.Resources["ButtonBackgroundPointerOver"] = Subtle;
        button.Resources["ButtonBackgroundPressed"] = Resource("SubtleFillColorTertiaryBrush", 0xE8, 0xE8, 0xE8, 0x2E, 0x2E, 0x2E);
        foreach (var key in new[] { "ButtonForeground", "ButtonForegroundPointerOver", "ButtonForegroundPressed" }) button.Resources[key] = foreground ?? AccentText;
        button.Resources["ButtonForegroundDisabled"] = Resource("TextFillColorDisabledBrush", 0xA0, 0xA0, 0xA0, 0x78, 0x78, 0x78);
        button.Background = clear;
        button.BorderThickness = new Thickness(0);
        button.Foreground = foreground ?? AccentText;
        button.Padding = new Thickness(6, 3, 6, 3);
        return button;
    }

    static readonly string[] BorderlessKeys =
    [
        "ButtonBackground", "ButtonBackgroundDisabled", "ButtonBorderBrush", "ButtonBorderBrushPointerOver", "ButtonBorderBrushPressed", "ButtonBorderBrushDisabled",
        "ButtonBackgroundPointerOver", "ButtonBackgroundPressed", "ButtonForeground", "ButtonForegroundPointerOver", "ButtonForegroundPressed", "ButtonForegroundDisabled",
    ];

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

    /// <summary>選單的一項。icon／glyph：圖示（R101.4：側欄和物件的選單每一項都有，跟字同色；Symbol 沒有的用 Segoe Fluent 的 glyph）</summary>
    public static MenuFlyoutItem Item(string text, Action click, bool enabled = true, Symbol? icon = null, string? glyph = null)
    {
        var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
        if (icon is Symbol s) item.Icon = new SymbolIcon(s);
        else if (glyph is not null) item.Icon = new FontIcon { Glyph = glyph };
        item.Click += (_, _) => click();
        return item;
    }

    /// <summary>Segoe Fluent 的 OpenInNewWindow（「打開」：打開定期會議、打開這場會議）、BlockContact（移除這個人）</summary>
    public const string OpenGlyph = "\uE8A7";
    public const string RemovePersonGlyph = "\uE8F8";

    /// <summary>
    /// 會刪掉、移除東西的選單項目（R101.4）：紅字、紅圖示，滑過、按下也是紅的（範本各狀態讀的主題資源換掉）。
    /// 對比主題時照平台的顏色（§11.3：滑過去是反白的底，自己的顏色會看不見）。icon 是 null 時只有紅字（本來沒有圖示的選單）
    /// </summary>
    public static MenuFlyoutItem DangerItem(string text, Action click, bool enabled = true, Symbol? icon = Symbol.Delete, string? glyph = null)
    {
        var item = Item(text, click, enabled, icon, glyph);
        if (HighContrast) return item;
        var red = Danger;
        item.Foreground = red;
        foreach (var key in new[] { "MenuFlyoutItemForeground", "MenuFlyoutItemForegroundPointerOver", "MenuFlyoutItemForegroundPressed" }) item.Resources[key] = red;
        if (enabled && item.Icon is { } i) i.Foreground = red;
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

    /// <summary>
    /// 時間點：可以點的時候跳到錄音的那個位置播放（R4.2、R5.2）。at：實際的秒數（逐字稿的段落），
    /// 不給時照顯示的時間跳（顯示的時間去掉了小數，24.4 秒的段落顯示 00:24，跳到 24.0 會落在上一段）
    /// </summary>
    public static UIElement Time(string time, Action<double>? seek, double? at = null, double size = 13)
    {
        if (time.Length == 0) return new TextBlock();
        if (seek is not null && (at ?? TimeFormat.Seconds(time)) is double seconds)
        {
            // 對比主題時照平台的超連結顏色（青瓷綠換成的反白顏色不是給超連結用的）
            var b = new HyperlinkButton { Content = time, Padding = new Thickness(0), FontSize = size };
            if (!HighContrast) b.Foreground = Celadon;
            ToolTipService.SetToolTip(b, Localization.L.F("從 %@ 開始播放", time));
            b.Click += (_, _) => seek(seconds);
            return b;
        }
        return Text(time, size, color: Secondary, wrap: false);
    }

    /// <summary>enabled：哪些選項能選（例如沒安裝的 CLI 不能選，R45.3）；沒給就全部能選</summary>
    public static ComboBox Combo<T>(IEnumerable<(T Value, string Label)> options, T selected, Action<T> changed, string? header = null, Func<T, bool>? enabled = null)
    {
        var list = options.ToList();
        var box = new ComboBox { Header = header, MinWidth = 200 };
        foreach (var (value, label) in list)
            box.Items.Add(enabled is null ? label : new ComboBoxItem { Content = label, IsEnabled = enabled(value) });
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

/// <summary>
/// 左右兩塊（同 Mac 的 ViewThatFits）：放得下時同一列，右邊那塊靠右；放不下時右邊那塊換到下一行、靠左。
/// 會議頁上方的第二列（日期、與會者｜會議設定、文件，M§59）
/// </summary>
public sealed partial class LeadingTrailing : Panel
{
    public double Spacing { get; set; } = 16;
    public double LineSpacing { get; set; } = 8;
    bool fits = true;

    public LeadingTrailing(FrameworkElement leading, FrameworkElement trailing)
    {
        Children.Add(leading);
        Children.Add(trailing);
    }

    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size available)
    {
        var infinite = new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity);
        foreach (var child in Children) child.Measure(infinite);
        var (a, b) = (Children[0].DesiredSize, Children[1].DesiredSize);
        fits = a.Width + Spacing + b.Width <= available.Width;
        if (fits) return new Windows.Foundation.Size(double.IsInfinity(available.Width) ? a.Width + Spacing + b.Width : available.Width, Math.Max(a.Height, b.Height));
        // 放不下：左邊那塊照可用的寬度量（裡面是橫排，超出的會被切掉）
        Children[0].Measure(new Windows.Foundation.Size(available.Width, double.PositiveInfinity));
        a = Children[0].DesiredSize;
        return new Windows.Foundation.Size(available.Width, a.Height + LineSpacing + b.Height);
    }

    protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size final)
    {
        var (a, b) = (Children[0].DesiredSize, Children[1].DesiredSize);
        if (fits)
        {
            var height = Math.Max(a.Height, b.Height);
            Children[0].Arrange(new Windows.Foundation.Rect(0, (height - a.Height) / 2, a.Width, a.Height));
            Children[1].Arrange(new Windows.Foundation.Rect(Math.Max(0, final.Width - b.Width), (height - b.Height) / 2, b.Width, b.Height));
        }
        else
        {
            Children[0].Arrange(new Windows.Foundation.Rect(0, 0, Math.Min(a.Width, final.Width), a.Height));
            Children[1].Arrange(new Windows.Foundation.Rect(0, a.Height + LineSpacing, Math.Min(b.Width, final.Width), b.Height));
        }
        Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, final.Width, final.Height) };
        return final;
    }
}

/// <summary>
/// 一排會換行的小元件（同 Mac 的 FlowLayout；會議頁上方的資訊列；原本在系列頁的檔案裡，定期會議 D177 拿掉後搬過來）。同一行裡高度不同的元件上下置中
/// （資訊列的名字是按鈕，比一般的文字高一點）
/// </summary>
public sealed partial class FlowPanel : Panel
{
    public double Spacing { get; set; } = 8;
    public double LineSpacing { get; set; } = 8;
    // 外面常照量出來的寬度（widest）排；150% 這種縮放下寬度是浮點數，加總的誤差會讓最後一個「差一點點」放不下而換行，
    // 高度卻只留一行，跟下一排疊在一起（會議頁的資訊列）
    const double WidthTolerance = 0.5;

    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size available)
    {
        double x = 0, y = 0, line = 0, widest = 0;
        foreach (var child in Children)
        {
            child.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > available.Width + WidthTolerance) { x = 0; y += line + LineSpacing; line = 0; }
            x += size.Width + Spacing;
            line = Math.Max(line, size.Height);
            widest = Math.Max(widest, x - Spacing);
        }
        return new Windows.Foundation.Size(double.IsInfinity(available.Width) ? widest : Math.Min(widest, available.Width), y + line);
    }

    protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size final)
    {
        // 先分好行、算每一行的高度，再排（同一行裡置中）
        var lines = new List<(int Start, int Count, double Height)>();
        double x = 0, line = 0;
        int start = 0;
        for (var i = 0; i < Children.Count; i++)
        {
            var size = Children[i].DesiredSize;
            if (x > 0 && x + size.Width > final.Width + WidthTolerance) { lines.Add((start, i - start, line)); start = i; x = 0; line = 0; }
            x += size.Width + Spacing;
            line = Math.Max(line, size.Height);
        }
        if (Children.Count > start) lines.Add((start, Children.Count - start, line));
        double y = 0;
        foreach (var (first, count, height) in lines)
        {
            x = 0;
            for (var i = first; i < first + count; i++)
            {
                var size = Children[i].DesiredSize;
                Children[i].Arrange(new Windows.Foundation.Rect(x, y + (height - size.Height) / 2, size.Width, size.Height));
                x += size.Width + Spacing;
            }
            y += height + LineSpacing;
        }
        return final;
    }
}
