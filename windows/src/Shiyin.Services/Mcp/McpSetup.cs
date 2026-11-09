using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shiyin.LLM;
using Shiyin.Localization;

namespace Shiyin.Services.Mcp;

/// <summary>
/// 把本地 MCP 加到 AI 工具（M§44.3、design-windows §11.12）：Claude Code、Codex、Claude Desktop、複製設定。
/// Windows 不用 Mac 的啟動檔：安裝版的 shiyin-mcp.exe 在 Velopack 的 current 資料夾，更新後路徑不變；
/// SHIYIN_HOME 是用環境變數指定的（DEBUG、示範資料）時，登記時一起帶
/// </summary>
public static class McpSetup
{
    public const string ServerName = "shiyin";

    public sealed record Outcome(bool Ok, string Message);

    /// <summary>shiyin-mcp.exe：跟 Shiyin.exe 同一個資料夾</summary>
    public static string ServerPath(string appDirectory) => Path.Combine(appDirectory, "shiyin-mcp.exe");

    /// <summary>SHIYIN_HOME 是用環境變數指定的才回傳（完整路徑）</summary>
    public static string? CustomHome() =>
        System.Environment.GetEnvironmentVariable("SHIYIN_HOME") is { Length: > 0 } ? ShiyinPaths.FromEnvironment().Home : null;

    // MARK: 複製

    public static string ClaudeCodeCommand(string server, string? home) =>
        $"claude mcp add --scope user {ServerName}" + (home is null ? "" : $" -e \"SHIYIN_HOME={home}\"") + $" -- \"{server}\"";

    public static string ConfigJson(string server, string? home) =>
        new JsonObject { ["mcpServers"] = new JsonObject { [ServerName] = Entry(server, home) } }.ToJsonString(Pretty);

    static JsonObject Entry(string server, string? home)
    {
        var entry = new JsonObject { ["command"] = server };
        if (home is not null) entry["env"] = new JsonObject { ["SHIYIN_HOME"] = home };
        return entry;
    }

    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // MARK: Claude Code、Codex

    public static Task<Outcome> AddToClaudeCode(ToolCommand? tool, ProcessRunner runner, string server, string? home)
    {
        if (tool is null) return Task.FromResult(new Outcome(false, L.T("找不到 claude")));
        // -e 可以接好幾個值，要放在名稱後面（放前面會把名稱也當成環境變數）
        var add = new List<string> { "mcp", "add", "--scope", "user", ServerName };
        if (home is not null) add.AddRange(["-e", $"SHIYIN_HOME={home}"]);
        add.AddRange(["--", server]);
        return Register(tool, runner, ["mcp", "remove", "--scope", "user", ServerName], add,
            L.T("已加到 Claude Code，開新的對話就能用。"), e => L.F("沒有加到 Claude Code：%@", e));
    }

    public static Task<Outcome> AddToCodex(ToolCommand? tool, ProcessRunner runner, string server, string? home)
    {
        if (tool is null) return Task.FromResult(new Outcome(false, L.T("找不到 codex")));
        var add = new List<string> { "mcp", "add", ServerName };
        if (home is not null) add.AddRange(["--env", $"SHIYIN_HOME={home}"]);
        add.AddRange(["--", server]);
        return Register(tool, runner, ["mcp", "remove", ServerName], add,
            L.T("已加到 Codex，開新的對話就能用。"), e => L.F("沒有加到 Codex：%@", e));
    }

    /// <summary>先移除同名的（不在也沒關係）再加，按兩次不會重複</summary>
    static async Task<Outcome> Register(ToolCommand tool, ProcessRunner runner, List<string> remove, List<string> add, string done, Func<string, string> failed)
    {
        try
        {
            try { await runner.Run(tool, remove, null, null, TimeSpan.FromSeconds(20), CancellationToken.None); }
            catch (Exception e) when (e is not OutOfMemoryException) { }
            var result = await runner.Run(tool, add, null, null, TimeSpan.FromSeconds(20), CancellationToken.None);
            return result.Succeeded ? new Outcome(true, done) : new Outcome(false, failed(FirstLine(result.Stderr.Length > 0 ? result.Stderr : result.Stdout)));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new Outcome(false, failed(e.Message));
        }
    }

    static string FirstLine(string text)
    {
        var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        return line.Length == 0 ? L.T("沒有錯誤訊息") : line.Length > 200 ? line[..200] : line;
    }

    // MARK: Claude Desktop

    /// <summary>只改 mcpServers.shiyin，其他設定保留；原檔先複製成 .bak。不是 JSON 物件就不動</summary>
    public static Outcome AddToClaudeDesktop(string configPath, string server, string? home)
    {
        try
        {
            var root = new JsonObject();
            if (File.Exists(configPath))
            {
                JsonNode? parsed;
                try { parsed = JsonNode.Parse(File.ReadAllText(configPath)); }
                catch (JsonException) { parsed = null; }
                if (parsed is not JsonObject o || (o["mcpServers"] is { } existing && existing is not JsonObject))
                    return new Outcome(false, L.F("Claude Desktop 的設定檔不是拾音看得懂的格式，沒有改動：%@", configPath));
                root = o;
                File.Copy(configPath, configPath + ".bak", overwrite: true);
            }
            if (root["mcpServers"] is not JsonObject servers) root["mcpServers"] = servers = [];
            servers[ServerName] = Entry(server, home);
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            var temp = configPath + ".tmp";
            File.WriteAllText(temp, root.ToJsonString(Pretty) + "\n");
            File.Move(temp, configPath, overwrite: true);
            return new Outcome(true, L.T("已加到 Claude Desktop，重新開啟 Claude Desktop 後就能用。"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new Outcome(false, L.F("沒有加到 Claude Desktop：%@", e.Message));
        }
    }
}
