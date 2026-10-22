using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32;
using Shiyin.Services;

namespace Shiyin.Platform;

/// <summary>
/// 開始功能表、桌面的捷徑和「應用程式」清單裡的名稱（design-windows §14.1、R27 R12.3）：中文介面是「拾音」，其他是「Shiyin」。
/// Velopack 只能用打包時固定的名稱，所以打包時關掉它的捷徑（vpk pack --shortcuts None），改在安裝、更新的 hook 和啟動時自己處理
/// </summary>
public static class Shortcuts
{
    /// <summary>Velopack 給的 AppUserModelID（velopack.&lt;packId&gt;）：工作列把捷徑和執行中的拾音歸成同一組</summary>
    const string AppUserModelId = "velopack.ShiyinApp";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ShiyinApp";
    static readonly string[] Names = ["拾音", "Shiyin"];  // l10n-skip

    static string[] Folders =>
    [
        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
    ];

    /// <summary>是不是安裝好的拾音（%LOCALAPPDATA%\ShiyinApp\current\Shiyin.exe），開發版不動使用者的捷徑</summary>
    public static bool IsInstalledApp => Environment.ProcessPath is { } exe
        && Path.GetDirectoryName(exe) is { } dir && Path.GetFileName(dir) == "current"
        && Path.GetFileName(Path.GetDirectoryName(dir)) == "ShiyinApp";

    /// <summary>安裝時：開始功能表和桌面各建一個，指到目前的 Shiyin.exe</summary>
    public static void Create(string name)
    {
        foreach (var folder in Folders)
        {
            try
            {
                foreach (var other in Names.Where(n => n != name)) File.Delete(Path.Combine(folder, other + ".lnk"));
                Write(Path.Combine(folder, name + ".lnk"), Environment.ProcessPath!);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or COMException)
            {
                Log.Error($"shortcut failed: {e.GetType().Name}");
            }
        }
        SetListName(name);
    }

    /// <summary>更新後、每次啟動：換了介面語言就把現有的捷徑改名（只改名，使用者刪掉的不會再建回來）</summary>
    public static void Rename(string name)
    {
        foreach (var folder in Folders)
        {
            var target = Path.Combine(folder, name + ".lnk");
            if (File.Exists(target)) continue;
            if (Names.Select(n => Path.Combine(folder, n + ".lnk")).FirstOrDefault(File.Exists) is not { } existing) continue;
            try { File.Move(existing, target); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Error($"shortcut rename failed: {e.GetType().Name}"); }
        }
        SetListName(name);
    }

    /// <summary>解除安裝時：兩種名稱都刪</summary>
    public static void Remove()
    {
        foreach (var folder in Folders)
            foreach (var name in Names)
            {
                try { File.Delete(Path.Combine(folder, name + ".lnk")); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Error($"shortcut removal failed: {e.GetType().Name}"); }
            }
    }

    /// <summary>「設定 → 應用程式」裡的名稱（Velopack 寫的是打包時的名稱）</summary>
    static void SetListName(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey, writable: true);
            if (key is not null && key.GetValue("DisplayName") as string != name) key.SetValue("DisplayName", name);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Error($"app list name failed: {e.GetType().Name}");
        }
    }

    static void Write(string path, string target)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(target);
            link.SetWorkingDirectory(Path.GetDirectoryName(target)!);
            link.SetIconLocation(target, 0);
            var store = (IPropertyStore)link;
            var key = new PropertyKey { Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 };   // PKEY_AppUserModel_ID
            var value = new PropVariant { Type = 31, Value = Marshal.StringToCoTaskMemUni(AppUserModelId) };   // VT_LPWSTR
            try
            {
                store.SetValue(ref key, ref value);
                store.Commit();
            }
            finally { Marshal.FreeCoTaskMem(value.Value); }
            ((IPersistFile)link).Save(path, true);
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int max, IntPtr findData, uint flags);
        void GetIDList(out IntPtr list);
        void SetIDList(IntPtr list);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int max);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int max);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int max);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int cmd);
        void SetShowCmd(int cmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int max, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PropertyKey
    {
        public Guid Format;
        public uint Id;
    }

    /// <summary>只用到字串（VT_LPWSTR）：型別、三個保留欄位、指標，64 位元時共 24 bytes</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct PropVariant
    {
        public ushort Type;
        ushort reserved1, reserved2, reserved3;
        public IntPtr Value;
        IntPtr padding;
    }
}
