using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace Shiyin.App;

/// <summary>
/// 分享畫面時藏起拾音的視窗（R77、design-windows §11.19）：SetWindowDisplayAffinity 設 WDA_EXCLUDEFROMCAPTURE，
/// 分享整個螢幕、截圖、錄影都拍不到，自己的螢幕照常看得到。每個視窗在 WindowPlacement.Center（主視窗、設定、
/// 其他視窗）或偵測會議的小視窗 Show 時記下來；設定改了對所有開著的視窗重新套用
/// </summary>
public static partial class WindowCapture
{
    const uint WdaNone = 0, WdaExcludeFromCapture = 0x11;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    static readonly List<WeakReference<Window>> windows = [];

    /// <summary>AppHost 啟動時、設定改了時設</summary>
    public static bool Hide { get; private set; }

    public static void Track(Window window)
    {
        lock (windows)
        {
            if (!windows.Any(w => w.TryGetTarget(out var x) && ReferenceEquals(x, window)))
            {
                windows.Add(new WeakReference<Window>(window));
                window.Closed += (_, _) => { lock (windows) windows.RemoveAll(w => !w.TryGetTarget(out var x) || ReferenceEquals(x, window)); };
            }
        }
        Apply(window);
    }

    public static void Set(bool hide)
    {
        Hide = hide;
        List<Window> open;
        lock (windows) open = [.. windows.Select(w => w.TryGetTarget(out var x) ? x : null).OfType<Window>()];
        foreach (var window in open) Apply(window);
    }

    static void Apply(Window window)
    {
        try { SetWindowDisplayAffinity(WinRT.Interop.WindowNative.GetWindowHandle(window), Hide ? WdaExcludeFromCapture : WdaNone); }
        catch (Exception e) when (e is COMException or InvalidOperationException) { }
    }
}
