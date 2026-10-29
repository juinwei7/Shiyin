using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace Shiyin.Platform;

/// <summary>
/// Microsoft Store（MSIX）套件的安裝資料夾（design-windows §10）：C:\Program Files\WindowsApps 一般使用者列不出內容，
/// 用 package family 問系統（GetPackagesByPackageFamily、GetPackagePathByFullName）
/// </summary>
public static partial class Packages
{
    public sealed record Installed(string FullName, string Path);

    /// <summary>這個 package family 裝了哪些套件（通常一個）；沒有、查不到是空的</summary>
    public static IReadOnlyList<Installed> ByFamily(string family)
    {
        try
        {
            uint count = 0, length = 0;
            var rc = GetPackagesByPackageFamily(family, ref count, null, ref length, IntPtr.Zero);
            if (count == 0 || (rc != 0 && rc != ErrorInsufficientBuffer)) return [];
            var names = new IntPtr[count];
            var buffer = Marshal.AllocHGlobal((int)length * sizeof(char));
            try
            {
                if (GetPackagesByPackageFamily(family, ref count, names, ref length, buffer) != 0) return [];
                var list = new List<Installed>();
                foreach (var name in names.Take((int)count))
                    if (Marshal.PtrToStringUni(name) is { } fullName && PathOf(fullName) is { } path) list.Add(new Installed(fullName, path));
                return list;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return []; }
    }

    /// <summary>套件的 AppxManifest.xml：顯示名稱（是 ms-resource: 時用 fallback）和第一個 Application 的 Id</summary>
    public static (string Name, string? AppId) Manifest(string packagePath, string fallback)
    {
        try
        {
            var doc = XDocument.Load(System.IO.Path.Combine(packagePath, "AppxManifest.xml"));
            var name = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "DisplayName" && e.Parent?.Name.LocalName == "Properties")?.Value.Trim();
            var id = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Application")?.Attribute("Id")?.Value;
            return (string.IsNullOrEmpty(name) || name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) ? fallback : name, id);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return (fallback, null);
        }
    }

    static string? PathOf(string fullName)
    {
        uint length = 0;
        if (GetPackagePathByFullName(fullName, ref length, null) != ErrorInsufficientBuffer || length == 0) return null;
        var buffer = new char[length];
        if (GetPackagePathByFullName(fullName, ref length, buffer) != 0) return null;
        return new string(buffer).TrimEnd('\0');
    }

    const int ErrorInsufficientBuffer = 122;

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetPackagesByPackageFamily(string packageFamilyName, ref uint count, [Out] IntPtr[]? packageFullNames,
        ref uint bufferLength, IntPtr buffer);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetPackagePathByFullName(string packageFullName, ref uint pathLength, [Out] char[]? path);
}
