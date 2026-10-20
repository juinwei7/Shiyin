using System.Diagnostics;
using System.Text;
using Shiyin.Localization;

namespace Shiyin.LLM;

public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>錯誤訊息只取 stderr 的結尾，不帶逐字稿內容</summary>
    public string StderrTail(int limit)
    {
        var trimmed = Stderr.Trim();
        return trimmed.Length > limit ? "…" + trimmed[^limit..] : trimmed;
    }
}

/// <summary>
/// 執行外部程式（同 Mac 的 Subprocess）：一律用參數陣列，不經過 shell 或 cmd.exe（CLAUDE.md、design-windows §10）。
/// 取消、逾時時結束整棵程序樹。Windows 另外用 <see cref="OnStarted"/> 把程序放進 Job Object，拾音當掉時子程序一起結束
/// </summary>
public sealed class ProcessRunner
{
    /// <summary>程序啟動後呼叫（Windows：放進 Job Object）</summary>
    public Action<Process>? OnStarted { get; set; }

    /// <summary>子程序的環境變數（補上找到 CLI 的資料夾，Mac 是 Paths.subprocessEnvironment）</summary>
    public Func<IDictionary<string, string?>, IDictionary<string, string?>>? Environment { get; set; }

    public async Task<ProcessResult> Run(ToolCommand command, IReadOnlyList<string> arguments, string? stdin, string? directory,
                                         TimeSpan timeout, CancellationToken cancel)
    {
        var info = new ProcessStartInfo(command.Executable)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdin is not null,
            UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        if (stdin is not null) info.StandardInputEncoding = new UTF8Encoding(false);
        foreach (var a in command.PrefixArguments) info.ArgumentList.Add(a);
        foreach (var a in arguments) info.ArgumentList.Add(a);
        if (directory is not null) info.WorkingDirectory = directory;
        if (Environment is not null)
        {
            var env = Environment(info.Environment);
            foreach (var (k, v) in env) info.Environment[k] = v;
        }
        if (command.Environment is not null) foreach (var (k, v) in command.Environment) info.Environment[k] = v;

        using var process = new Process { StartInfo = info };
        process.Start();
        OnStarted?.Invoke(process);
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        if (stdin is not null)
        {
            // 子程序先結束時寫入會失敗，不影響結果（M§20.1）
            try
            {
                await process.StandardInput.WriteAsync(stdin);
                process.StandardInput.Close();
            }
            catch (IOException) { }
        }
        using var timer = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, timer.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            if (cancel.IsCancellationRequested) throw;
            throw new TimeoutException(L.F("執行超過 %lld 分鐘，已停止", (long)timeout.TotalMinutes));
        }
        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }
}
