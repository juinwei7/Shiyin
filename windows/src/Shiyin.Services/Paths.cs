using Shiyin.Model;

namespace Shiyin.Services;

/// <summary>
/// 拾音所有檔案的位置（design-windows §3）。資料一律放 SHIYIN_HOME，不放專案資料夾
/// </summary>
public sealed class ShiyinPaths
{
    public string Home { get; }

    public ShiyinPaths(string home) => Home = Path.GetFullPath(home);

    /// <summary>
    /// SHIYIN_HOME；沒設定時 Windows 用 %LOCALAPPDATA%\Shiyin。其他系統（在 Mac 上跑測試、評估）一定要設 SHIYIN_HOME：
    /// .NET 在 Mac 的 LocalApplicationData 是 ~/Library/Application Support，會跟 Mac 版拾音的資料夾撞在一起
    /// </summary>
    public static ShiyinPaths FromEnvironment()
    {
        var custom = Environment.GetEnvironmentVariable("SHIYIN_HOME");
        if (!string.IsNullOrEmpty(custom))
        {
            if (custom.StartsWith('~')) custom = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + custom[1..];
            return new ShiyinPaths(custom);
        }
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("SHIYIN_HOME is required outside Windows");  // l10n-skip
        return new ShiyinPaths(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Shiyin"));
    }

    public string Models => Path.Combine(Home, "models");
    public string Backups => Path.Combine(Home, "backups");
    public string Audio => Path.Combine(Home, "audio");
    /// <summary>錄音中的檔案：&lt;會議 id&gt;-&lt;uuid&gt;.wav，當掉後由復原流程接回（M§20.6）</summary>
    public string Recording => Path.Combine(Audio, "recording");
    public string Database => Path.Combine(Home, "shiyin.sqlite");
    public string Tmp => Path.Combine(Home, "tmp");
    public string Logs => Path.Combine(Home, "logs");
    public string Settings => Path.Combine(Home, "settings.json");
    /// <summary>匿名使用統計的匯總（design-windows §12.10、M§30.2）</summary>
    public string Usage => Path.Combine(Home, "usage.json");

    public string AudioPath(Meeting meeting) => Path.Combine(Audio, meeting.AudioFile);

    /// <summary>有錄音檔（同 Mac 的 hasAudio：欄位有值而且檔案在）</summary>
    public bool HasAudio(Meeting meeting) => meeting.AudioFile.Length > 0 && File.Exists(AudioPath(meeting));

    public void EnsureDirectories()
    {
        foreach (var dir in new[] { Home, Audio, Recording, Tmp, Logs, Models, Backups }) Directory.CreateDirectory(dir);
    }

    /// <summary>在 tmp 底下建立一個用完即刪的資料夾</summary>
    public string MakeTempDirectory()
    {
        var dir = Path.Combine(Tmp, Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        return dir;
    }
}
