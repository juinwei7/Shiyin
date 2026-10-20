using Shiyin.LLM;

namespace Shiyin.Platform;

/// <summary>
/// 找 claude、codex（design-windows §10、T-W6）：一定要找到真正的執行檔，不能透過 npm 的 .cmd 執行
/// （cmd.exe 的命令列只有 8191 個字元、跳脫規則不同，--system-prompt、--json-schema 會被截斷或解讀錯，還有參數注入的風險）。
/// 找不到 exe 時用 node.exe 加套件裡的 js。子程序的 PATH 補上找到的資料夾
/// </summary>
public sealed class WindowsToolLocator : IToolLocator
{
    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static string NpmModules => Path.Combine(Roaming, "npm", "node_modules");

    public ToolCommand? Find(string name) => name switch
    {
        "claude" => Claude(),
        "codex" => Codex(),
        _ => Exe(name + ".exe") is { } exe ? new ToolCommand(exe, []) : null,
    };

    static ToolCommand? Claude()
    {
        // 原生安裝（claude.ai/install.ps1）放在 %USERPROFILE%\.local\bin
        var native = Path.Combine(Home, ".local", "bin", "claude.exe");
        if (File.Exists(native)) return With(native, []);
        if (Exe("claude.exe") is { } onPath) return With(onPath, []);
        // npm install -g @anthropic-ai/claude-code：用 node 執行 cli.js
        var cli = Path.Combine(NpmModules, "@anthropic-ai", "claude-code", "cli.js");
        return File.Exists(cli) && Node() is { } node ? With(node, [cli], Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(cli)))!) : null;
    }

    static ToolCommand? Codex()
    {
        if (Exe("codex.exe") is { } onPath) return With(onPath, []);
        // npm install -g @openai/codex：套件裡有各平台的執行檔
        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64
            ? "aarch64-pc-windows-msvc" : "x86_64-pc-windows-msvc";
        var package = Path.Combine(NpmModules, "@openai", "codex");
        foreach (var candidate in new[]
                 {
                     Path.Combine(package, "vendor", arch, "codex", "codex.exe"),
                     Path.Combine(package, "bin", $"codex-{arch}.exe"),
                 })
            if (File.Exists(candidate)) return With(candidate, []);
        var js = Path.Combine(package, "bin", "codex.js");
        return File.Exists(js) && Node() is { } node ? With(node, [js], Path.GetDirectoryName(js)!) : null;
    }

    static string? Node()
    {
        if (Exe("node.exe") is { } onPath) return onPath;
        foreach (var dir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Local + @"\Programs" })
        {
            var node = Path.Combine(dir, "nodejs", "node.exe");
            if (File.Exists(node)) return node;
        }
        return null;
    }

    /// <summary>PATH 裡的 exe（只找 .exe，不找 .cmd、.bat、.ps1）</summary>
    static string? Exe(string file)
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Append(Path.Combine(Home, ".local", "bin")).Append(Path.Combine(Local, "Microsoft", "WinGet", "Links"));
        foreach (var dir in paths)
        {
            try
            {
                var path = Path.Combine(dir, file);
                if (File.Exists(path)) return path;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    /// <summary>子程序的 PATH 前面補上執行檔（和 node）所在的資料夾</summary>
    static ToolCommand With(string executable, IReadOnlyList<string> prefix, string? extraDir = null)
    {
        var dirs = new List<string> { Path.GetDirectoryName(executable)! };
        if (extraDir is not null) dirs.Add(extraDir);
        var path = string.Join(';', dirs.Append(Environment.GetEnvironmentVariable("PATH") ?? ""));
        return new ToolCommand(executable, prefix, new Dictionary<string, string> { ["PATH"] = path });
    }
}
