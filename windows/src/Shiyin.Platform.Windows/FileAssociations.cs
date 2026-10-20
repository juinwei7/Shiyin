using System.Runtime.InteropServices;
using Microsoft.Win32;
using Shiyin.Services;

namespace Shiyin.Platform;

/// <summary>
/// 檔案關聯（design-windows §12.1）：安裝時在 HKCU\Software\Classes 註冊 .shiyin 的預設程式，
/// 錄音、影片、逐字稿出現在「開啟檔案」的清單裡；解除安裝時拿掉。只寫使用者自己的登錄，不用系統管理員
/// </summary>
public static partial class FileAssociations
{
    const string ProgId = "ShiyinApp.Package";
    const string AppKey = @"Software\Classes\Applications\Shiyin.exe";

    static string Command => $"\"{Environment.ProcessPath}\" \"%1\"";

    public static void Register()
    {
        try
        {
            using (var ext = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.shiyin"))
                ext.SetValue("", ProgId);
            using (var prog = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
            {
                prog.SetValue("", Localization.L.T("拾音會議封包"));
                using var icon = prog.CreateSubKey("DefaultIcon");
                icon.SetValue("", $"\"{Environment.ProcessPath}\",0");
                using var command = prog.CreateSubKey(@"shell\open\command");
                command.SetValue("", Command);
            }
            using (var app = Registry.CurrentUser.CreateSubKey(AppKey))
            {
                app.SetValue("FriendlyAppName", Localization.L.T("拾音"));
                using var command = app.CreateSubKey(@"shell\open\command");
                command.SetValue("", Command);
                using var types = app.CreateSubKey("SupportedTypes");
                foreach (var ext in Importer.MediaExtensions.Concat(Importer.TranscriptExtensions).Append("shiyin")) types.SetValue("." + ext, "");
            }
            foreach (var ext in Importer.MediaExtensions)
            {
                using var open = Registry.CurrentUser.CreateSubKey($@"Software\Classes\.{ext}\OpenWithProgids");
                open.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
            }
            Notify();
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Log.Error($"file association failed: {e.GetType().Name}");
        }
    }

    public static void Unregister()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", false);
            Registry.CurrentUser.DeleteSubKeyTree(AppKey, false);
            using (var ext = Registry.CurrentUser.OpenSubKey(@"Software\Classes\.shiyin", true))
                if (ext?.GetValue("") as string == ProgId) Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\.shiyin", false);
            foreach (var ext in Importer.MediaExtensions)
                using (var open = Registry.CurrentUser.OpenSubKey($@"Software\Classes\.{ext}\OpenWithProgids", true))
                    open?.DeleteValue(ProgId, false);
            Notify();
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Log.Error($"file association removal failed: {e.GetType().Name}");
        }
    }

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    /// <summary>SHCNE_ASSOCCHANGED：讓檔案總管重新讀檔案關聯</summary>
    static void Notify() => SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
}
