using Microsoft.Win32;
using Shiyin.Services;

namespace Shiyin.Platform;

/// <summary>
/// 通知用的登錄（design-windows §12.2、D62）：AppUserModelID（通知上的名稱、圖示）和 shiyin: 網址（點通知、按按鈕時開拾音）。
/// 只寫使用者自己的登錄；每次啟動都寫一次，exe 的路徑變了也跟著改
/// </summary>
public static class ToastRegistration
{
    public const string AppId = "Weiwei.Shiyin";
    public const string Scheme = "shiyin";
    const string AppIdKey = @"Software\Classes\AppUserModelId\" + AppId;
    const string ProtocolKey = @"Software\Classes\" + Scheme;

    public static void Register(string displayName, string iconPath)
    {
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(AppIdKey))
            {
                key.SetValue("DisplayName", displayName);
                key.SetValue("IconUri", iconPath);
            }
            using (var key = Registry.CurrentUser.CreateSubKey(ProtocolKey))
            {
                key.SetValue("", "URL:" + displayName);
                key.SetValue("URL Protocol", "");
                using var icon = key.CreateSubKey("DefaultIcon");
                icon.SetValue("", $"\"{Environment.ProcessPath}\",0");
                using var command = key.CreateSubKey(@"shell\open\command");
                command.SetValue("", $"\"{Environment.ProcessPath}\" \"%1\"");
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Log.Error($"toast registration failed: {e.GetType().Name}");
        }
    }

    /// <summary>使用者在「設定 → 系統 → 通知」的選擇：全部的通知、或拾音的通知被關掉時是 false</summary>
    public static bool EnabledInSettings()
    {
        try
        {
            using (var all = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\PushNotifications"))
                if (all?.GetValue("ToastEnabled") is int on && on == 0) return false;
            using (var app = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\" + AppId))
                if (app?.GetValue("Enabled") is int on && on == 0) return false;
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException) { return true; }
    }

    /// <summary>解除安裝時（Velopack 的 hook）</summary>
    public static void Unregister()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(AppIdKey, false);
            Registry.CurrentUser.DeleteSubKeyTree(ProtocolKey, false);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Log.Error($"toast registration removal failed: {e.GetType().Name}");
        }
    }
}
