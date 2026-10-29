using System.Text.Json;

namespace Shiyin.LLM;

/// <summary>
/// Codex、Claude 的桌面版（R55、R56，M§37、design-windows §10）：訊息、畫面要知道 codex 是不是桌面版附的、有沒有 Claude 桌面版。
/// App 啟動時接上 WindowsToolLocator；測試、沒接時都當作沒有
/// </summary>
public static class DesktopApps
{
    /// <summary>用 Codex 桌面版附的 codex 時是 app 的名稱，其他是 null</summary>
    public static Func<string?> CodexAppName { get; set; } = () => null;
    /// <summary>這台電腦有沒有 Claude 桌面版</summary>
    public static Func<bool> HasClaude { get; set; } = () => false;

    /// <summary>Claude Code 官方的安裝指令（PowerShell）和說明（R56.2）</summary>
    public const string ClaudeInstallCommand = "irm https://claude.ai/install.ps1 | iex";
    public const string ClaudeInstallGuide = "https://code.claude.com/docs/en/setup";
}

/// <summary>Codex 桌面版附的 codex（M§37.1、design-windows §10）：執行檔和要接在子程序 PATH 前面的資料夾（有 rg）</summary>
public sealed record CodexPackage(string Executable, string? PathDir)
{
    /// <summary>從桌面版的 resources 資料夾找 codex：先照說明檔 codex-cli\codex-package.json，沒有說明檔時找 resources\codex.exe。
    /// 找不到、說明檔不對時回傳 null，problem 是原因（只記 log）</summary>
    public static CodexPackage? Find(string resources, out string? problem)
    {
        var root = Path.Combine(resources, "codex-cli");
        var manifest = Path.Combine(root, "codex-package.json");
        if (File.Exists(manifest)) return Read(root, manifest, out problem);
        var exe = Path.Combine(resources, "codex.exe");
        if (File.Exists(exe))
        {
            problem = null;
            return new CodexPackage(exe, null);
        }
        problem = "no codex-package.json or codex.exe";
        return null;
    }

    static CodexPackage? Read(string root, string manifest, out string? problem)
    {
        JsonElement obj;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            obj = doc.RootElement.Clone();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            problem = "unreadable codex-package.json";
            return null;
        }
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty("layoutVersion", out var version)
            || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var v) || v != 1)
        {
            problem = "layoutVersion is not 1";
            return null;
        }
        if (!obj.TryGetProperty("entrypoint", out var entry) || entry.ValueKind != JsonValueKind.String || Inside(root, entry.GetString()!) is not { } exe)
        {
            problem = "bad entrypoint";
            return null;
        }
        // 只執行 exe：.cmd、.bat、.ps1 要經過 cmd.exe 或 PowerShell，參數會被截斷、改寫（design-windows §10）
        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(exe))
        {
            problem = "entrypoint is not an exe";
            return null;
        }
        // pathDir 只是補 rg，不對就不加，不影響整理
        string? pathDir = null;
        if (obj.TryGetProperty("pathDir", out var p) && p.ValueKind == JsonValueKind.String && Inside(root, p.GetString()!) is { } dir && Directory.Exists(dir))
            pathDir = dir;
        problem = null;
        return new CodexPackage(exe, pathDir);
    }

    /// <summary>說明檔裡的相對路徑：不接受絕對路徑、磁碟代號、..，展開後也要在 codex-cli 裡面</summary>
    static string? Inside(string root, string relative)
    {
        if (relative.Length == 0 || Path.IsPathRooted(relative) || relative.StartsWith('~') || (relative.Length > 1 && relative[1] == ':'))
            return null;
        var parts = relative.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Contains("..")) return null;
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine([root, .. parts]));
        return full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) ? full : null;
    }
}
