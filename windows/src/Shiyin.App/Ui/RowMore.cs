using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Shiyin.Localization;

namespace Shiyin.App;

/// <summary>
/// 列右邊的「⋯」（R100、M§74 的 RowMenuButton）：滑鼠在列上、或那一列選到時才看得到（沒顯示時透明、照樣佔位，列的寬度不跳），
/// 點了打開跟右鍵同一份選單（fill 建選單的內容，打開時才建）。無障礙名稱、ToolTip 是「更多動作」。
/// 兩種選單都照 ChatGPT 的樣子（MenuLook，R105、design-windows §11.38）；「⋯」的選單左上角對齊「⋯」的左下（同 Mac）
/// </summary>
public static class RowMore
{
    public static Button Button(Action<MenuFlyout> fill)
    {
        var menu = MenuLook.Apply(new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft });
        menu.Opening += (_, _) => Ui.Guard(() => { menu.Items.Clear(); fill(menu); MenuLook.Apply(menu); });
        var button = Ui.Borderless(new Button { Content = new FontIcon { Glyph = "", FontSize = 14 }, Flyout = menu }, Ui.Secondary);
        button.Padding = new Thickness(5, 2, 5, 2);
        button.MinWidth = 0;
        button.VerticalAlignment = VerticalAlignment.Center;
        ToolTipService.SetToolTip(button, L.T("更多動作"));
        AutomationProperties.SetName(button, L.T("更多動作"));
        Show(button, false);
        return button;
    }

    /// <summary>右鍵的選單：同一份內容（打開時才建）</summary>
    public static MenuFlyout Context(Action<MenuFlyout> fill)
    {
        var menu = new MenuFlyout();
        fill(menu);
        MenuLook.Apply(menu);
        menu.Opening += (_, _) => Ui.Guard(() => { menu.Items.Clear(); fill(menu); MenuLook.Apply(menu); });
        return menu;
    }

    /// <summary>
    /// 滑鼠進出 row 時顯示、收起「⋯」；selected 是 true 時一直顯示。replaced（資料夾、定期會議的數量）在「⋯」顯示時藏起來，
    /// 兩個放在同一個位置。回傳重新判斷的動作（選取改了時呼叫）
    /// </summary>
    public static Action Attach(UIElement row, Button more, Func<bool>? selected = null, UIElement? replaced = null)
    {
        var hovering = false;
        void Update()
        {
            var show = hovering || selected?.Invoke() == true || more.Flyout?.IsOpen == true;
            Show(more, show);
            if (replaced is not null) replaced.Opacity = show ? 0 : 1;
        }
        row.PointerEntered += (_, _) => { hovering = true; Update(); };
        row.PointerExited += (_, _) => { hovering = false; Update(); };
        if (more.Flyout is { } flyout) flyout.Closed += (_, _) => Update();
        if (row is SelectorItem item) item.RegisterPropertyChangedCallback(SelectorItem.IsSelectedProperty, (_, _) => Update());
        Update();
        return Update;
    }

    static void Show(Button more, bool show)
    {
        more.Opacity = show ? 1 : 0;
        more.IsHitTestVisible = show;
        more.IsTabStop = show;
    }
}
