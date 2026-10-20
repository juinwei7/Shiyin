using System.Runtime.InteropServices;
using Microsoft.Win32;
using Shiyin.Services;

namespace Shiyin.Platform;

/// <summary>回報問題的環境資訊（design-windows §12.7）：Windows 版本和 build、CPU、記憶體、顯示卡、whisper 用 CPU 還是 Vulkan</summary>
public static class SystemInfo
{
    public static ProblemReport.Environment Collect(string appVersion, string build, string whisperInfo) => new(
        appVersion, build, Windows(), Cpu(), Memory(), Gpu(), whisperInfo.Contains("VULKAN = 1", StringComparison.Ordinal) || whisperInfo.Contains("Vulkan", StringComparison.Ordinal) ? "Vulkan" : "CPU");

    static string Windows()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var build = key?.GetValue("CurrentBuild") as string ?? Environment.OSVersion.Version.Build.ToString();
        var ubr = key?.GetValue("UBR") is int u ? $".{u}" : "";
        // ProductName 在 Windows 11 還是寫 Windows 10：build 22000 以上就是 Windows 11
        var name = int.TryParse(build, out var b) && b >= 22000 ? "Windows 11" : "Windows 10";
        var edition = key?.GetValue("EditionID") as string ?? "";
        var display = key?.GetValue("DisplayVersion") as string ?? "";
        return $"{name} {edition} {display}（{build}{ubr}，{RuntimeInformation.OSArchitecture}）".Replace("  ", " ");
    }

    static string Cpu()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? RuntimeInformation.ProcessArchitecture.ToString();
    }

    static long Memory()
    {
        var status = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        return Native.GlobalMemoryStatusEx(ref status) ? (long)status.ullTotalPhys : 0;
    }

    /// <summary>顯示卡的名稱：從驅動程式的登錄讀（不用 DXGI 的 COM）</summary>
    static string Gpu()
    {
        using var adapters = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        if (adapters is null) return "?";
        var names = new List<string>();
        foreach (var sub in adapters.GetSubKeyNames().Where(n => n.All(char.IsAsciiDigit)))
        {
            try
            {
                using var adapter = adapters.OpenSubKey(sub);
                if (adapter?.GetValue("DriverDesc") is string desc && !names.Contains(desc)) names.Add(desc);
            }
            catch (System.Security.SecurityException) { }
        }
        return names.Count == 0 ? "?" : string.Join("、", names);
    }
}
