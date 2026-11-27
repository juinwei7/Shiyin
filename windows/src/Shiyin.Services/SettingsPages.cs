using Shiyin.Localization;
using Shiyin.Model;

namespace Shiyin.Services;

/// <summary>
/// 設定視窗的每一頁（R108、M§82、design-windows §11.41，同 Mac 的 SettingsPage）：代號、順序、分組、名稱、搜尋用的關鍵字。
/// 代號跟 Mac 相同，記住的頁面存在 settings.json 的 settingsPage
/// </summary>
public static class SettingsPages
{
    public const string Provider = "provider";

    /// <summary>側欄的四組（組和組之間有分隔線）</summary>
    public static readonly IReadOnlyList<IReadOnlyList<string>> Groups =
    [
        ["provider", "templates"], ["recording", "transcription", "models"], ["speakers", "glossary"], ["general", "ai", "enterprise", "status"],
    ];

    /// <summary>十一頁照側欄的順序（控制通道的 0～10 是這個順序）</summary>
    public static readonly IReadOnlyList<string> All = [.. Groups.SelectMany(g => g)];

    public static string Title(string id) => id switch
    {
        "templates" => MeetingLanguages.Ui("範本", "Templates"),
        "recording" => MeetingLanguages.Ui("錄音", "Recording"),
        "transcription" => MeetingLanguages.Ui("轉錄", "Transcription"),   // 「轉錄」在別處是動詞（Transcribing）
        "models" => L.T("模型與空間"),
        "speakers" => MeetingLanguages.Ui("說話者", "Speakers"),
        "glossary" => L.T("詞彙表"),
        "general" => L.T("一般"),
        "ai" => L.T("AI 工具"),
        "enterprise" => L.T("企業伺服器"),
        "status" => L.T("檢查與更新"),
        _ => L.T("整理工具"),
    };

    /// <summary>
    /// 搜尋用（R108.4）：那一頁項目的名稱（用介面語言），加上常用的說法（API key、MCP、快捷鍵這類）。
    /// 同 Mac 的 keywords，只列 Windows 有的項目（Mac 才有的系統錄音權限、說話者辨識的模型、環境檢查不列；Windows 才有的介面語言、顯示卡加速（模型清單上的名稱）、回報問題、關於拾音加上）
    /// </summary>
    public static IReadOnlyList<string> Keywords(string id) => id switch
    {
        "provider" =>
        [
            L.T("轉錄完成後自動整理成會議紀錄"), L.T("轉錄完成後檢查可能聽錯的字"), L.T("模型"), L.T("測試整理工具"),
            "Claude", "Codex", "Antigravity", "ChatGPT", "OpenAI", "Gemini", "Anthropic", "API key",
        ],
        "templates" => [L.T("要產生的段落"), L.T("自訂段落"), L.T("設為預設範本")],
        "recording" =>
        [
            L.T("錄音來源"), L.T("麥克風"), L.T("麥克風權限設定"), L.T("偵測到線上會議時提醒錄音"), L.T("會議結束時自動停止錄音"),
            L.T("在螢幕上方顯示錄音島"), L.T("提醒與快捷鍵"), "Zoom", "Teams", "Google Meet",
        ],
        "transcription" =>
        [
            L.T("正式轉錄"), L.T("轉錄時辨識說話者"), L.T("轉錄前消除喇叭的回音"), L.T("錄音中顯示即時逐字稿"), L.T("預設會議語言"),
            L.T("中文的字"), L.T("自動偵測會議語言"), "Breeze", "Qwen", "Whisper",
        ],
        "models" =>
        [
            L.T("模型"), L.T("儲存空間"), L.T("資料位置"), L.T("轉錄完成後把錄音壓縮成 m4a（約每小時 43 MB）"), L.T("NVIDIA 顯示卡加速（CUDA）"), L.T("AMD、Intel 顯示卡加速（Vulkan）"),
            "Breeze", "Qwen", "Whisper",
        ],
        "speakers" => [L.T("記住說話者的聲音，之後的會議自動帶入名字"), L.T("我的聲音"), L.T("認識的人")],
        "glossary" => [L.T("新增詞彙")],
        "general" =>
        [
            L.T("介面語言"), L.T("會議內容的文字大小"), L.T("分享畫面時藏起拾音的視窗"), L.T("重新顯示首次使用引導"), L.T("重建搜尋索引"), L.T("匿名使用統計"),
        ],
        "ai" => [L.T("讓 AI 工具讀取會議"), "MCP", "Claude Code", "Claude Desktop", "Codex"],
        "enterprise" => [L.T("網址"), L.T("登入")],
        "status" => [L.T("更新"), L.T("檢查更新"), L.T("目前版本"), L.T("回報問題…"), L.T("關於拾音")],
        _ => [],
    };

    /// <summary>打的字在名稱或關鍵字裡（不分大小寫；空白的時候每一頁都算）</summary>
    public static bool Matches(string id, string query)
    {
        var q = query.Trim();
        return q.Length == 0 || Keywords(id).Prepend(Title(id)).Any(k => k.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>搜尋後留下的頁面（照側欄的順序）</summary>
    public static List<string> Find(string query) => [.. All.Where(id => Matches(id, query))];

    /// <summary>
    /// 控制通道、別的地方指定的頁面：代號、0～10（側欄的順序），以及 4.8 以前四頁的代號
    ///（names → 說話者、advanced → 一般、storage → 模型與空間）；認不得是 null
    /// </summary>
    public static string? Resolve(string? name)
    {
        var key = name?.Trim().ToLowerInvariant() ?? "";
        if (All.Contains(key)) return key;
        if (int.TryParse(key, out var index)) return index >= 0 && index < All.Count ? All[index] : null;
        return key switch { "names" => "speakers", "advanced" => "general", "storage" => "models", _ => null };
    }

    /// <summary>上次停在哪一頁（R108.5）：存的不是十一頁的代號（沒存過、壞掉）時是整理工具</summary>
    public static string Stored(AppSettings settings) => settings.String(AppSettings.SettingsPageKey) is { } id && All.Contains(id) ? id : Provider;

    /// <summary>記住這一頁：值真的變了才寫</summary>
    public static void Remember(AppSettings settings, string id)
    {
        if (All.Contains(id) && settings.String(AppSettings.SettingsPageKey) != id) settings.Set(AppSettings.SettingsPageKey, id);
    }
}
