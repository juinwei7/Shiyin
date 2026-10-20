using System.Text.Json;
using Shiyin.LLM;
using Shiyin.Localization;
using Shiyin.Platform;

namespace Shiyin.App;

/// <summary>Claude Code、Codex 的登入狀態與重新登入（同 Mac 的 CLILogin，design-windows §10）</summary>
public static class CliLogin
{
    public sealed record Status(bool LoggedIn, string Detail);

    static ToolCommand? Tool(LLMProvider provider) =>
        new WindowsToolLocator().Find(provider == LLMProvider.Codex ? "codex" : "claude");

    /// <summary>查登入狀態，不會送出請求、不消耗額度。找不到指令或看不懂輸出時回傳 null</summary>
    public static async Task<Status?> Check(LLMProvider provider)
    {
        if (provider is not (LLMProvider.Claude or LLMProvider.Codex) || Tool(provider) is not { } tool) return null;
        var runner = new ProcessRunner();
        try
        {
            if (provider == LLMProvider.Claude)
            {
                var r = await runner.Run(tool, ["auth", "status", "--json"], null, Path.GetTempPath(), TimeSpan.FromSeconds(20), CancellationToken.None);
                using var doc = JsonDocument.Parse(r.Stdout);
                var root = doc.RootElement;
                if (!root.TryGetProperty("loggedIn", out var loggedIn)) return null;
                var detail = root.TryGetProperty("email", out var email) ? email.GetString() ?? "" : root.TryGetProperty("authMethod", out var m) ? m.GetString() ?? "" : "";
                if (root.TryGetProperty("subscriptionType", out var plan) && plan.GetString() is { Length: > 0 } p)
                    detail += L.F("（%@ 方案）", char.ToUpperInvariant(p[0]) + p[1..]);
                return new Status(loggedIn.GetBoolean(), detail);
            }
            var result = await runner.Run(tool, ["login", "status"], null, Path.GetTempPath(), TimeSpan.FromSeconds(20), CancellationToken.None);
            // codex 把狀態印在 stdout 或 stderr，兩個都看
            var line = (result.Stdout + "\n" + result.Stderr).Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
            var ok = line.Contains("logged in", StringComparison.OrdinalIgnoreCase) && !line.Contains("not logged in", StringComparison.OrdinalIgnoreCase);
            return new Status(ok, line);
        }
        catch (Exception e) when (e is JsonException or IOException or TimeoutException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>開終端機執行登入指令（會開瀏覽器登入頁）</summary>
    public static void Open(AppHost host, LLMProvider provider)
    {
        if (Tool(provider) is not { } tool)
        {
            host.Notifier.Notice(L.F("找不到 %@ 指令，請先安裝並登入，或到設定 → 整理工具改用 API key", provider == LLMProvider.Codex ? "codex" : "claude"));
            return;
        }
        Shell.OpenLoginTerminal(tool, provider == LLMProvider.Codex ? ["login"] : ["auth", "login"]);
        host.Notifier.Notice(L.F("拾音：正在打開瀏覽器登入 %@。登入完成後，回到拾音按「重試」或「測試整理工具」。", provider.ShortLabel()));
    }
}
