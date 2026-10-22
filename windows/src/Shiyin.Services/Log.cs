namespace Shiyin.Services;

/// <summary>
/// log（design-windows §12.9）：logs\shiyin-&lt;日期&gt;.log，保留 7 天。不寫逐字稿、會議紀錄的內容和 key（CLAUDE.md）
/// </summary>
public static class Log
{
    static readonly object gate = new();
    static string? directory;

    /// <summary>測試、評估工具不設定：只寫到 Debug 輸出</summary>
    public static void Configure(string logsDirectory)
    {
        lock (gate)
        {
            directory = logsDirectory;
            Directory.CreateDirectory(logsDirectory);
            foreach (var file in Directory.GetFiles(logsDirectory, "shiyin-*.log"))
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-7))
                    try { File.Delete(file); } catch (IOException) { }
        }
    }

    public static void Info(string message) => Write("info", message);
    public static void Error(string message) => Write("error", message);

    static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        System.Diagnostics.Debug.WriteLine(line);
        lock (gate)
        {
            if (directory is null) return;
            try { File.AppendAllText(Path.Combine(directory, $"shiyin-{DateTime.Now:yyyy-MM-dd}.log"), line + Environment.NewLine); }
            catch (IOException) { }
        }
    }
}
