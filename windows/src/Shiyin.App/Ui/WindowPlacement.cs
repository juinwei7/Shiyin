using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace Shiyin.App;

/// <summary>
/// 視窗的位置和大小（design-windows §11.1）：大小用邏輯像素（同 Mac 的 pt），依這個螢幕的縮放換算成實際像素；
/// 比工作區（扣掉工作列）大時縮到放得下，放在工作區中間。1366×768 的筆電扣掉工作列只有約 720 高
/// </summary>
public static partial class WindowPlacement
{
    public static void Center(Window window, int width, int height)
    {
        SetIcon(window);
        FollowTheme(window);
        var area = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var scale = Scale(window);
        var w = Math.Min((int)Math.Round(width * scale), area.Width - 16);
        var h = Math.Min((int)Math.Round(height * scale), area.Height - 16);
        window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
    }

    /// <summary>
    /// 視窗的最小尺寸（邏輯像素，同 Mac 的 minWidth、minHeight）：沒設時可以拉到很窄，錄音的按鈕、筆記欄會被切掉（2026-10-22 介面巡檢）。
    /// 不超過這個螢幕的工作區：1366×768 放大 125% 時工作區只有約 614 高
    /// </summary>
    public static void MinimumSize(Window window, double width, double height)
    {
        var area = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var scale = Scale(window);
        var manager = WinUIEx.WindowManager.Get(window);
        manager.MinWidth = Math.Min(width, area.Width / scale - 16);
        manager.MinHeight = Math.Min(height, area.Height / scale - 16);
    }

    /// <summary>標題列、工作列用拾音的圖示（沒設時是空白的預設圖示）</summary>
    public static void SetIcon(Window window)
    {
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "Shiyin.ico");
        if (File.Exists(icon)) window.AppWindow.SetIcon(icon);
    }

    /// <summary>
    /// 標題列跟著 Windows 的淺色、深色模式（R26.3）。沒設時標題列的顏色不一定跟內容一致：
    /// 2026-10-21 在虛擬機看到深色模式下主視窗的內容是深色、標題列還是淺色
    /// </summary>
    public static void FollowTheme(Window window) => window.AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;

    /// <summary>這個視窗所在螢幕的縮放（100% 是 1）</summary>
    public static double Scale(Window window)
    {
        var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(window));
        return dpi > 0 ? dpi / 96.0 : 1;
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(IntPtr hwnd);
}
