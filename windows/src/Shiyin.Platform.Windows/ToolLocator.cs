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
        // npm install -g @anthropic-ai/claude-code：2.x 的套件直接帶原生的 claude.exe（2026-10 在虛擬機看到 2.1.306 已經沒有 cli.js）；舊版用 node 執行 cli.js
        foreach (var prefix in NpmPrefixes("claude.cmd"))
        {
            var package = Path.Combine(prefix, "node_modules", "@anthropic-ai", "claude-code");
            var exe = Path.Combine(package, "bin", "claude.exe");
            if (File.Exists(exe)) return With(exe, []);
            var cli = Path.Combine(package, "cli.js");
            if (File.Exists(cli) && Node() is { } node) return With(node, [cli], prefix);
        }
        return null;
    }

    static ToolCommand? Codex()
    {
        if (Exe("codex.exe") is { } onPath) return With(onPath, []);
        // npm install -g @openai/codex：執行檔在各平台的子套件裡（@openai/codex-win32-arm64/vendor/<平台>/bin/codex.exe），
        // 旁邊的 codex-path 有它要用的 rg.exe，codex.js 會把它加進 PATH，這裡也加
        var arm = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64;
        var arch = arm ? "aarch64-pc-windows-msvc" : "x86_64-pc-windows-msvc";
        var platform = arm ? "codex-win32-arm64" : "codex-win32-x64";
        foreach (var prefix in NpmPrefixes("codex.cmd"))
        {
            var package = Path.Combine(prefix, "node_modules", "@openai", "codex");
            foreach (var vendor in new[] { Path.Combine(package, "node_modules", "@openai", platform, "vendor", arch), Path.Combine(package, "vendor", arch) })
                foreach (var candidate in new[] { Path.Combine(vendor, "bin", "codex.exe"), Path.Combine(vendor, "codex", "codex.exe") })
                    if (File.Exists(candidate)) return With(candidate, [], Path.Combine(vendor, "codex-path"));
            var legacy = Path.Combine(package, "bin", $"codex-{arch}.exe");
            if (File.Exists(legacy)) return With(legacy, []);
            var js = Path.Combine(package, "bin", "codex.js");
            if (File.Exists(js) && Node() is { } node) return With(node, [js], Path.GetDirectoryName(js)!);
        }
        return null;
    }

    /// <summary>
    /// npm 的全域資料夾：官方安裝檔是 %APPDATA%\npm；zip 版的 Node、nvm、scoop、Volta 在別的地方，
    /// 找 PATH 裡有那個指令的 .cmd 的資料夾（只拿來定位套件，不執行 .cmd）
    /// </summary>
    static IEnumerable<string> NpmPrefixes(string shim) =>
        new[] { Path.Combine(Roaming, "npm") }
            .Concat(PathDirs().Where(dir => File.Exists(Path.Combine(dir, shim))))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    static IEnumerable<string> PathDirs()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string full;
            try { full = Path.GetFullPath(dir); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            yield return full;
        }
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
