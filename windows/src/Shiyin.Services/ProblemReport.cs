using Shiyin.Data;
using Shiyin.LLM;
using Shiyin.Localization;
using Shiyin.Model;

namespace Shiyin.Services;

/// <summary>
/// 回報問題（M§21.9、R19.7、D33；design-windows §12.7）：組出要附上的環境資訊，打開瀏覽器的 GitHub 新 issue 頁面讓使用者自己送出。
/// 不帶會議內容、檔名、API key、模型名稱、網址；錯誤只寫種類（jobs.error_kind），不讀 jobs.error（可能含逐字稿）
/// </summary>
public static class ProblemReport
{
    public const string RepositoryUrl = "https://github.com/juinwei7/Shiyin";
    public const string DefaultIssuesUrl = RepositoryUrl + "/issues";
    /// <summary>贊助（R13.3）：「關於拾音」最上面的連結，用預設瀏覽器打開</summary>
    public const string SponsorUrl = "https://ko-fi.com/weiwei_chang";

    /// <summary>Windows 的環境（Platform 填）：版本和 build、CPU、記憶體、顯示卡、whisper 用哪張顯示卡（或 CPU）</summary>
    public sealed record Environment(string AppVersion, string Build, string Windows, string Cpu, long MemoryBytes, string Gpu, string WhisperBackend);

    public static string Text(AppDatabase db, AppSettings settings, Environment env)
    {
        var (on, off) = (L.T("開"), L.T("關"));
        var lines = new List<string>
        {
            L.T("## 問題描述"),
            L.T("（請寫發生了什麼事、怎麼重現。不要貼會議內容）"),
            "",
            L.T("## 環境"),
            L.F("- 拾音 %@（build %@）", env.AppVersion, env.Build),
            "- " + env.Windows,
            L.F("- %@，記憶體 %@", env.Cpu, Memory(env.MemoryBytes)),
            L.F("- 顯示卡：%@；whisper：%@", env.Gpu, env.WhisperBackend),
            L.F("- 介面語言：%@", MeetingLanguages.Interface.UiName()),
            L.F("- 轉錄：%@；說話者辨識：%@；聲紋：%@", settings.UseFastTranscriber ? L.T("快速辨識") : settings.UseQwen ? "Qwen3-ASR 1.7B" : "Breeze ASR 25",
                settings.Diarize ? on : off, settings.Voiceprints ? on : off),
            L.F("- 整理工具：%@", settings.Provider.Label()),
            "",
            L.T("## 最近失敗的工作"),
        };
        var failures = db.RecentFailures(5);
        if (failures.Count == 0) lines.Add(L.T("- 沒有"));
        foreach (var f in failures) lines.Add(L.F("- %@ %@：%@", f.Date, f.Kind, f.ErrorKind ?? L.T("（沒有記錄種類）")));
        return string.Join("\n", lines);
    }

    /// <summary>記憶體的寫法（同 Mac 的 ByteCountFormatter .memory：二進位）</summary>
    public static string Memory(long bytes) => (bytes / (double)(1L << 30)).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " GB";

    public static string Url(string body, string issuesUrl = DefaultIssuesUrl) =>
        $"{issuesUrl.TrimEnd('/')}/new?title=&body={Uri.EscapeDataString(body)}";
}
