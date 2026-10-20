using System.Diagnostics;

namespace Shiyin.Platform;

/// <summary>打開網址、系統設定、終端機（參數一律用陣列，不組命令列字串）</summary>
public static class Shell
{
    /// <summary>瀏覽器打開網址（只接受 https，回報問題、說明）</summary>
    public static void OpenUrl(string url)
    {
        if (!url.StartsWith("https://", StringComparison.Ordinal)) return;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    /// <summary>用預設的 app 打開檔案（授權條款）</summary>
    public static void OpenFile(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    /// <summary>隱私設定的麥克風頁（R27 R1.8）</summary>
    public static void OpenMicrophonePrivacy() => Process.Start(new ProcessStartInfo("ms-settings:privacy-microphone") { UseShellExecute = true });

    /// <summary>檔案總管選取這個檔案</summary>
    public static void Reveal(string path)
    {
        var info = new ProcessStartInfo("explorer.exe");
        info.ArgumentList.Add("/select," + path);
        Process.Start(info);
    }

    /// <summary>
    /// 開終端機登入 claude 或 codex（design-windows §10）：Windows Terminal，沒有就用 cmd.exe /k。
    /// 參數是固定的，不放任何使用者的內容
    /// </summary>
    public static void OpenLoginTerminal(Shiyin.LLM.ToolCommand tool, IReadOnlyList<string> arguments)
    {
        var wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
        ProcessStartInfo info;
        if (File.Exists(wt))
        {
            info = new ProcessStartInfo(wt);
            info.ArgumentList.Add("--");
        }
        else
        {
            info = new ProcessStartInfo("cmd.exe");
            info.ArgumentList.Add("/k");
        }
        info.ArgumentList.Add(tool.Executable);
        foreach (var a in tool.PrefixArguments) info.ArgumentList.Add(a);
        foreach (var a in arguments) info.ArgumentList.Add(a);
        if (tool.Environment is not null) foreach (var (k, v) in tool.Environment) info.Environment[k] = v;
        info.UseShellExecute = false;
        Process.Start(info);
    }

    /// <summary>剩餘空間（錄音的提醒）</summary>
    public static long? AvailableBytes(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace; }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { return null; }
    }
}
