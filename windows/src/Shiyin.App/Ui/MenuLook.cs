using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Shiyin.App;

/// <summary>
/// 右鍵和「⋯」的選單（R105、M§79、design-windows §11.38）：平台的 MenuFlyout 加輕量的樣式覆寫（不換 ControlTemplate）。
/// 面板圓角 14、不透明的底（深色 #2B2B2B、淺色白）、細框；每一項比平台高一點（滑過的底 32）、圓角 8、滑過一塊字色 9% 的底；
/// 快捷鍵提示、子選單的「›」灰色；分隔線字色 10%。圖示（Segoe Fluent 的細線圖示）、紅字（Ui.DangerItem）、子選單在右邊、
/// 鍵盤（↑↓→←、Enter、Esc）、點外面就關、UI Automation 都照平台。對比主題時不覆寫（照平台的系統色，§11.3）
/// </summary>
public static class MenuLook
{
    static Style? presenter;
    static string presenterKey = "";

    /// <summary>面板的樣子（照深淺色快取：Style 用過之後不能再改）。子選單的面板 WinUI 會套同一個（MenuFlyout 的 MenuFlyoutPresenterStyle）</summary>
    static Style Presenter
    {
        get
        {
            if (presenter is not null && presenterKey == Ui.ThemeKey) return presenter;
            var style = new Style(typeof(MenuFlyoutPresenter));
            if (Application.Current.Resources.TryGetValue("DefaultMenuFlyoutPresenterStyle", out var basedOn) && basedOn is Style b) style.BasedOn = b;
            style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(14)));
            // 不透明的底（平台是壓克力）：淺色白；深色 #323232（Mac 是 white 0.17＝#2B2B2B，但 Windows 的側欄就是 #2B2B2B，
            // 選單開在側欄上只剩一圈細框分得出來，2026-11-17 虛擬機），比側欄、內容（#202020）都亮一點
            style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Ui.IsDark ? Ui.HexColor("#323232") : Microsoft.UI.Colors.White)));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, SidebarLook.Gray(0.10)));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            // 每一項的外距是 4,2（平台），加上面板的內距 1,3：四邊各 5，同 Mac 的 padding 5
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(1, 3, 1, 3)));
            style.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 200d));
            presenter = style;
            presenterKey = Ui.ThemeKey;
            return style;
        }
    }

    /// <summary>選單的內容填好之後（打開時才建，Opening）套上樣子</summary>
    public static MenuFlyout Apply(MenuFlyout menu)
    {
        if (Ui.HighContrast) return menu;
        var style = Presenter;
        if (menu.MenuFlyoutPresenterStyle != style) menu.MenuFlyoutPresenterStyle = style;
        foreach (var item in menu.Items) Item(item);
        return menu;
    }

    static void Item(MenuFlyoutItemBase item)
    {
        switch (item)
        {
            case MenuFlyoutSeparator separator:
                // 平台是 -4,1（伸到項目的外距外面）；同 Mac 上下各 4、左右縮進一點
                separator.Padding = new Thickness(8, 4, 8, 4);
                separator.Background = SidebarLook.Gray(0.10);
                break;
            case MenuFlyoutSubItem sub:
                Row(sub);
                foreach (var child in sub.Items) Item(child);
                break;
            case MenuFlyoutItem row:
                Row(row);
                break;
        }
    }

    /// <summary>
    /// 一項：滑過的底 32 高（MinHeight 36 減掉平台上下各 2 的外距；平台約 29）、圓角 8、字色 9% 的底（按下 12%）。
    /// 快捷鍵提示、「›」是次要文字的顏色（紅色那一項的快捷鍵也是灰的，同 Mac）
    /// </summary>
    static void Row(Control item)
    {
        item.MinHeight = 36;
        item.CornerRadius = new CornerRadius(8);
        var r = item.Resources;
        var hover = SidebarLook.Gray(0.09);
        var pressed = SidebarLook.Gray(0.12);
        r["MenuFlyoutItemBackgroundPointerOver"] = hover;
        r["MenuFlyoutItemBackgroundPressed"] = pressed;
        r["MenuFlyoutSubItemBackgroundPointerOver"] = hover;
        r["MenuFlyoutSubItemBackgroundPressed"] = pressed;
        r["MenuFlyoutSubItemBackgroundSubMenuOpened"] = hover;
        foreach (var key in new[]
        {
            "MenuFlyoutItemKeyboardAcceleratorTextForeground", "MenuFlyoutItemKeyboardAcceleratorTextForegroundPointerOver",
            "MenuFlyoutItemKeyboardAcceleratorTextForegroundPressed", "MenuFlyoutSubItemChevron", "MenuFlyoutSubItemChevronPointerOver",
            "MenuFlyoutSubItemChevronPressed", "MenuFlyoutSubItemChevronSubMenuOpened",
        }) r[key] = Ui.Secondary;
    }

    /// <summary>
    /// 右邊灰色的快捷鍵提示（KeyboardAcceleratorTextOverride）：只是提示，不註冊快捷鍵（ContextFlyout 裡的 KeyboardAccelerator 在選單關著時也會生效，
    /// 每一列都註冊一次會搶同一個鍵；同 Mac 的理由）。Windows 的鍵：重新命名「F2」、刪除這類「Delete」（§11.33 的側欄鍵盤）
    /// </summary>
    public static T Hint<T>(T item, string keys) where T : MenuFlyoutItem
    {
        item.KeyboardAcceleratorTextOverride = keys;
        return item;
    }

    public const string Rename = "F2";
    public const string Delete = "Delete";
}
