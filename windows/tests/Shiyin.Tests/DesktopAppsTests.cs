using Shiyin.LLM;
using Shiyin.Services;
using Xunit;

namespace Shiyin.Tests;

/// <summary>Codex、Claude 的桌面版（R55、R56，M§37、design-windows §10）：說明檔的規則、桌面版的訊息和按鈕</summary>
public sealed class DesktopAppsTests : IDisposable
{
    readonly TempHome home = new();

    public void Dispose()
    {
        DesktopApps.CodexAppName = () => null;
        DesktopApps.HasClaude = () => false;
        home.Dispose();
    }

    /// <summary>假的桌面版 resources 資料夾：codex-cli\codex-package.json、bin\codex.exe、codex-path</summary>
    string Resources(string? manifest, params string[] files)
    {
        var resources = Path.Combine(home.Path, Guid.NewGuid().ToString("N"), "app", "resources");
        var root = Path.Combine(resources, "codex-cli");
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        Directory.CreateDirectory(Path.Combine(root, "codex-path"));
        if (manifest is not null) File.WriteAllText(Path.Combine(root, "codex-package.json"), manifest);
        foreach (var file in files)
        {
            var path = Path.Combine(resources, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "");
        }
        return resources;
    }

    [Fact]
    public void ManifestFindsTheExeAndPathDir()
    {
        var resources = Resources("""{"layoutVersion":1,"entrypoint":"bin/codex.exe","pathDir":"codex-path"}""", @"codex-cli/bin/codex.exe");
        var package = CodexPackage.Find(resources, out var problem);
        Assert.NotNull(package);
        Assert.Null(problem);
        Assert.Equal(Path.GetFullPath(Path.Combine(resources, "codex-cli", "bin", "codex.exe")), package.Executable);
        Assert.Equal(Path.GetFullPath(Path.Combine(resources, "codex-cli", "codex-path")), package.PathDir);
    }

    [Theory]
    [InlineData("""{"layoutVersion":2,"entrypoint":"bin/codex.exe","pathDir":"codex-path"}""", "layoutVersion is not 1")]
    [InlineData("""{"layoutVersion":1,"entrypoint":"../codex-cli/bin/codex.exe"}""", "bad entrypoint")]
    [InlineData("""{"layoutVersion":1,"entrypoint":"/bin/codex.exe"}""", "bad entrypoint")]
    [InlineData("""{"layoutVersion":1,"entrypoint":"C:\\Windows\\System32\\cmd.exe"}""", "bad entrypoint")]
    [InlineData("""{"layoutVersion":1,"entrypoint":"bin/codex.cmd"}""", "entrypoint is not an exe")]
    [InlineData("""{"layoutVersion":1,"entrypoint":"bin/missing.exe"}""", "entrypoint is not an exe")]
    [InlineData("""{"layoutVersion":1}""", "bad entrypoint")]
    [InlineData("not json", "unreadable codex-package.json")]
    public void BadManifestIsSkipped(string manifest, string reason)
    {
        // 說明檔不對時不退回 resources\codex.exe：照說明檔走，認不得就當作沒有（M§37.1）
        var resources = Resources(manifest, @"codex-cli/bin/codex.exe", @"codex-cli/bin/codex.cmd", "codex.exe");
        Assert.Null(CodexPackage.Find(resources, out var problem));
        Assert.Equal(reason, problem);
    }

    [Fact]
    public void WithoutManifestUsesResourcesCodexExe()
    {
        var resources = Resources(null, "codex.exe");
        var package = CodexPackage.Find(resources, out _);
        Assert.NotNull(package);
        Assert.Equal(Path.Combine(resources, "codex.exe"), package.Executable);
        Assert.Null(package.PathDir);
        Assert.Null(CodexPackage.Find(Resources(null), out var problem));
        Assert.Equal("no codex-package.json or codex.exe", problem);
    }

    [Fact]
    public void BadPathDirIsDropped()
    {
        var resources = Resources("""{"layoutVersion":1,"entrypoint":"bin/codex.exe","pathDir":"../.."}""", @"codex-cli/bin/codex.exe");
        var package = CodexPackage.Find(resources, out _);
        Assert.NotNull(package);
        Assert.Null(package.PathDir);
    }

    [Fact]
    public void DesktopCodexMessagesOpenTheApp()
    {
        var installed = LLMException.NotLoggedIn("codex").Message;
        Assert.Contains("codex login", installed);
        Assert.Equal("登入 Codex", new SummarizerProblem(SummarizerProblemKind.NotLoggedIn, LLMProvider.Codex).ActionTitle);

        DesktopApps.CodexAppName = () => "Codex";
        var desktop = LLMException.NotLoggedIn("codex").Message;
        Assert.Contains("打開 Codex", desktop);
        Assert.DoesNotContain("codex login", desktop);
        // 失敗紀錄裡的這句也要認得是登入問題（橫幅的「打開 Codex 登入」、暫停時的按鈕）；標準答案用的 IsLoginProblem 不變
        Assert.True(LLMException.IsLoginMessage(desktop));
        Assert.False(LLMException.IsLoginProblem(desktop));
        var problem = new SummarizerProblem(SummarizerProblemKind.NotLoggedIn, LLMProvider.Codex);
        Assert.Contains("打開 Codex", problem.Message);
        Assert.Equal("打開 Codex 登入", problem.ActionTitle);
        var paused = new SummarizerProblem(SummarizerProblemKind.Paused, LLMProvider.Codex, desktop);
        Assert.True(paused.OffersLogin);
        // Claude 不受影響
        Assert.Equal("登入 Claude Code", new SummarizerProblem(SummarizerProblemKind.NotLoggedIn, LLMProvider.Claude).ActionTitle);
    }

    [Fact]
    public void ClaudeDesktopExplainsInstallingClaudeCode()
    {
        Assert.Contains("找不到 claude", LLMException.NotInstalled("claude").Message);
        DesktopApps.HasClaude = () => true;
        var message = LLMException.NotInstalled("claude").Message;
        Assert.Contains("Claude Code（命令列版）", message);
        Assert.Contains("Claude 桌面版不能直接用", message);
        Assert.Equal(message, new SummarizerProblem(SummarizerProblemKind.NotInstalled, LLMProvider.Claude).Message);
        // codex 的訊息不受影響
        Assert.Contains("找不到 codex", LLMException.NotInstalled("codex").Message);
    }
}
