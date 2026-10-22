using System.Runtime.InteropServices;
using Microsoft.Win32;
using Shiyin.Localization;
using Shiyin.Services;

namespace Shiyin.Platform;

/// <summary>回報問題的環境資訊（design-windows §12.7）：Windows 版本和 build、CPU、記憶體、顯示卡、whisper 用哪張顯示卡（或 CPU）</summary>
public static class SystemInfo
{
    /// <summary>whisperGpu：whisper 會用的顯示卡（WhisperEngine.GpuDevice），空的是只用 CPU</summary>
    public static ProblemReport.Environment Collect(string appVersion, string build, string whisperGpu) => new(
        appVersion, build, Windows(), Cpu(), Memory(), Gpu(), whisperGpu.Length > 0 ? whisperGpu : "CPU");

    static string Windows()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var build = key?.GetValue("CurrentBuild") as string ?? Environment.OSVersion.Version.Build.ToString();
        var ubr = key?.GetValue("UBR") is int u ? $".{u}" : "";
        // ProductName 在 Windows 11 還是寫 Windows 10：build 22000 以上就是 Windows 11
        var name = int.TryParse(build, out var b) && b >= 22000 ? "Windows 11" : "Windows 10";
        var edition = key?.GetValue("EditionID") as string ?? "";
        var display = key?.GetValue("DisplayVersion") as string ?? "";
        var detail = $"{build}{ubr}{L.T("，")}{RuntimeInformation.OSArchitecture}";
        // 括號跟著介面語言：英文介面用半形（2026-10-22 介面巡檢看到全形的括號）
        return $"{name} {edition} {display}" + (L.English ? $" ({detail})" : $"（{detail}）");  // l10n-skip
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

    /// <summary>有 NVIDIA 顯示卡（R27 R4.6）：顯示卡驅動的登錄裡，硬體 ID 的廠商是 10DE</summary>
    public static bool HasNvidiaGpu() => Adapters().Any(a => a.DeviceId.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase));

    /// <summary>顯示卡驅動的登錄：名稱、硬體 ID（PCI\VEN_xxxx&amp;DEV_xxxx…）</summary>
    static List<(string Name, string DeviceId)> Adapters()
    {
        var list = new List<(string, string)>();
        try
        {
            using var adapters = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (adapters is null) return list;
            foreach (var sub in adapters.GetSubKeyNames().Where(n => n.All(char.IsAsciiDigit)))
            {
                try
                {
                    using var adapter = adapters.OpenSubKey(sub);
                    if (adapter?.GetValue("DriverDesc") is string desc) list.Add((desc, adapter.GetValue("MatchingDeviceId") as string ?? ""));
                }
                catch (System.Security.SecurityException) { }
            }
        }
        catch (System.Security.SecurityException) { }
        return list;
    }

    /// <summary>顯示卡的名稱：從驅動程式的登錄讀（不用 DXGI 的 COM）</summary>
    static string Gpu()
    {
        var names = Adapters().Select(a => a.Name).Distinct().ToList();
        return names.Count == 0 ? "?" : string.Join(L.T("、"), names);
    }
}
