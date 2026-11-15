using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using Shiyin.Services;
using Shiyin.Services.Capture;

namespace Shiyin.Platform;

/// <summary>
/// 正在出聲的 app（R75.3、design-windows §11.18）：每個啟用中的輸出裝置上、狀態是 Active、目前音量大於 0 的音訊工作階段。
/// 拾音自己、系統音效不算。錄音用的是 Console 角色的預設輸出裝置（§6），app 的聲音在別的裝置上就錄不到
/// </summary>
public static class AudioOutputs
{
    public static IReadOnlyList<Recorder.PlayingApp> Playing()
    {
        var result = new List<Recorder.PlayingApp>();
        var own = Environment.ProcessId;
        using var devices = new MMDeviceEnumerator();
        string? defaultId = null, defaultName = null;
        if (devices.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Console, out var output))
        {
            defaultId = output.ID;
            defaultName = Name(output);
        }
        foreach (var device in devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            try
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (var i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    if (session.IsSystemSoundsSession || session.State != NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive) continue;
                    if (session.AudioMeterInformation.MasterPeakValue <= 0) continue;
                    var pid = (int)session.GetProcessID;
                    if (pid == own || pid == 0) continue;
                    var (name, kind) = Describe(pid);
                    result.Add(new Recorder.PlayingApp(name, kind, Name(device), device.ID == defaultId, defaultName ?? ""));
                }
            }
            // 讀不到某個裝置的工作階段：跳過那個裝置（不提示，比誤報好）
            catch (Exception e) when (e is COMException or InvalidCastException or ArgumentException) { }
        }
        return result;
    }

    static string Name(MMDevice device)
    {
        try { return device.FriendlyName; }
        catch (COMException) { return device.ID; }
    }

    /// <summary>名稱：已知的會議 app、瀏覽器用固定的名稱（§12.4），其他用程式的說明（讀不到用程序名稱）</summary>
    static (string Name, string Kind) Describe(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            var exe = process.ProcessName.ToLowerInvariant() + ".exe";
            if (MeetingDetector.MeetingApps.TryGetValue(exe, out var meeting)) return (meeting, "meeting");
            if (MeetingDetector.Browsers.TryGetValue(exe, out var browser)) return (browser, "browser");
            string? description = null;
            try { description = process.MainModule?.FileVersionInfo.FileDescription; }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            return (string.IsNullOrWhiteSpace(description) ? process.ProcessName : description, "other");
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return ($"PID {pid}", "other"); }
    }
}
