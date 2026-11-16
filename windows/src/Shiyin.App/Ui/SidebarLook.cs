using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Shiyin.App;

/// <summary>
/// 側欄的樣子照 ChatGPT（R101.4、M§75「單色」、design-windows §11.34）：滑過一塊淡灰、選到一塊深一點的灰（圓角 8），字照常；
/// 不用平台的強調色選取（左邊那條青瓷綠的線）。段落標題右邊的小按鈕（「+」、顯示方式）是細線的圖示。對比主題時照平台（§11.3）
/// </summary>
public static class SidebarLook
{
    /// <summary>字的顏色的灰：淺色模式黑、深色模式白，透明度 opacity（同 Mac 的 Color.primary.opacity）</summary>
    public static SolidColorBrush Gray(double opacity)
    {
        byte v = Ui.IsDark ? (byte)255 : (byte)0;
        return new SolidColorBrush(Windows.UI.Color.FromArgb((byte)Math.Round(255 * opacity), v, v, v));
    }

    /// <summary>滑過（6%）、選到（11%），同 Mac 的 sidebarHover(selected:)</summary>
    public static Brush Hover => Gray(0.06);
    public static Brush Selected => Gray(0.11);

    static readonly string[] IndicatorKeys =
    [
        "ListViewItemSelectionIndicatorBrush", "ListViewItemSelectionIndicatorPointerOverBrush", "ListViewItemSelectionIndicatorPressedBrush",
        "ListViewItemSelectionIndicatorDisabledBrush",
    ];

    /// <summary>
    /// 側欄的一列：滑過、選到的底換成灰色，拿掉左邊的強調色的線（輕量的樣式覆寫，要在範本套上之前設，所以建列時就呼叫）。
    /// showSelected false：選到也不畫（「所有會議」是回到預設的畫面，同 ChatGPT 的「新對話」）
    /// </summary>
    public static T Row<T>(T item, bool showSelected = true) where T : ListViewItem
    {
        var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        if (Ui.HighContrast)
        {
            // 對比主題：照平台（反白的底、反白的字）；只有「所有會議」選到也不畫，字照常是文字的顏色
            if (!showSelected)
            {
                item.Resources["ListViewItemBackgroundSelected"] = clear;
                item.Resources["ListViewItemForegroundSelected"] = Ui.SystemBrush(Windows.UI.ViewManagement.UIElementType.WindowText);
                foreach (var key in IndicatorKeys) item.Resources[key] = clear;
            }
            return item;
        }
        item.Resources["ListViewItemBackgroundPointerOver"] = Hover;
        item.Resources["ListViewItemBackgroundPressed"] = Hover;
        item.Resources["ListViewItemBackgroundSelected"] = showSelected ? Selected : clear;
        item.Resources["ListViewItemBackgroundSelectedPointerOver"] = showSelected ? Selected : Hover;
        item.Resources["ListViewItemBackgroundSelectedPressed"] = showSelected ? Selected : Hover;
        foreach (var key in IndicatorKeys) item.Resources[key] = clear;
        item.CornerRadius = new CornerRadius(8);
        return item;
    }

    /// <summary>
    /// 段落標題右邊的小按鈕（同 Mac 的 SidebarHeaderIcon，ChatGPT「專案」右邊的「+」）：細線的圖示（15）、灰色、26×24 的點擊範圍，
    /// 滑過變亮加淡淡的圓角底；右邊留 6 跟列的「⋯」對齊。active：篩選中，圖示是強調色的字。rotate：轉 90 度（Equalizer 轉成橫的三條滑桿）
    /// </summary>
    public static Button HeaderIcon(Button button, string glyph, bool active = false, bool rotate = false)
    {
        var icon = new FontIcon { Glyph = glyph, FontSize = 15 };
        if (rotate)
        {
            icon.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
            icon.RenderTransform = new RotateTransform { Angle = 90 };
        }
        button.Content = icon;
        button.Width = 26;
        button.Height = 24;
        button.MinWidth = 0;
        button.Padding = new Thickness(0);
        button.CornerRadius = new CornerRadius(6);
        button.VerticalAlignment = VerticalAlignment.Center;
        button.Margin = new Thickness(0, 0, 6, 0);
        if (Ui.HighContrast)
        {
            // 對比主題：照平台的按鈕（§11.3 的 Borderless）
            Ui.Borderless(button);
            button.Width = 26;
            button.Height = 24;
            button.Padding = new Thickness(0);
            return button;
        }
        var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        foreach (var key in new[] { "ButtonBackground", "ButtonBackgroundDisabled", "ButtonBorderBrush", "ButtonBorderBrushPointerOver", "ButtonBorderBrushPressed", "ButtonBorderBrushDisabled" })
            button.Resources[key] = clear;
        button.Resources["ButtonBackgroundPointerOver"] = Gray(0.08);
        button.Resources["ButtonBackgroundPressed"] = Gray(0.12);
        var normal = active ? Ui.AccentText : Ui.Secondary;
        var bright = active ? Ui.AccentText : Ui.Primary;
        button.Resources["ButtonForeground"] = normal;
        button.Resources["ButtonForegroundPointerOver"] = bright;
        button.Resources["ButtonForegroundPressed"] = bright;
        button.Background = clear;
        button.BorderThickness = new Thickness(0);
        button.Foreground = normal;
        return button;
    }

    /// <summary>無障礙：資料夾、定期會議的值是「已展開、4 場」（右邊不顯示場數，同 Mac 的 accessibilityState）</summary>
    public static void Status(UIElement item, bool? expanded, string count) =>
        AutomationProperties.SetItemStatus(item, expanded is bool e ? Shiyin.Localization.L.F("%@、%@", e ? Shiyin.Localization.L.T("已展開") : Shiyin.Localization.L.T("已收起"), count) : count);
}
