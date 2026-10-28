using Shiyin.Localization;
using Shiyin.Model;
using Shiyin.Notes;

namespace Shiyin.LLM;

/// <summary>Enterprise：公司的企業伺服器（R51、M§36.3），選單一直有（D124），模型由公司的後台設定</summary>
public enum LLMProvider { Claude, Codex, Anthropic, OpenAI, Gemini, OpenAICompatible, Enterprise, None }

public static class LLMProviders
{
    public static readonly LLMProvider[] All = Enum.GetValues<LLMProvider>();

    /// <summary>存在設定裡的代號（跟 Mac 的 rawValue 相同）</summary>
    public static string Code(this LLMProvider p) => p switch
    {
        LLMProvider.Claude => "claude",
        LLMProvider.Codex => "codex",
        LLMProvider.Anthropic => "anthropic",
        LLMProvider.OpenAI => "openai",
        LLMProvider.Gemini => "gemini",
        LLMProvider.OpenAICompatible => "openaiCompatible",
        LLMProvider.Enterprise => "enterprise",
        _ => "none",
    };

    public static LLMProvider? Parse(string? code) => All.Cast<LLMProvider?>().FirstOrDefault(p => p!.Value.Code() == code);

    public static string Label(this LLMProvider p) => p switch
    {
        LLMProvider.Claude => L.T("Claude（claude CLI）"),
        LLMProvider.Codex => L.T("Codex（codex CLI）"),
        LLMProvider.Anthropic => "Anthropic API",
        LLMProvider.OpenAI => "OpenAI API",
        LLMProvider.Gemini => "Gemini API",
        LLMProvider.OpenAICompatible => L.T("OpenAI 相容 API"),
        LLMProvider.Enterprise => L.T("公司"),
        _ => L.T("不整理"),
    };

    /// <summary>「由 xx 整理」用的名稱</summary>
    public static string ShortLabel(this LLMProvider p) => p switch
    {
        LLMProvider.Claude => "Claude",
        LLMProvider.Codex => "Codex",
        LLMProvider.OpenAICompatible or LLMProvider.None => p.Label(),
        LLMProvider.Enterprise => L.T("公司的模型"),
        _ => p.Label(),
    };

    public static bool UsesApiKey(this LLMProvider p) => p is LLMProvider.Anthropic or LLMProvider.OpenAI or LLMProvider.Gemini or LLMProvider.OpenAICompatible;
    /// <summary>「取得 API key」的網頁（R35.3）；OpenAI 相容 API 各家不同，沒有</summary>
    public static string? ApiKeyUrl(this LLMProvider p) => p switch
    {
        LLMProvider.Anthropic => "https://console.anthropic.com/settings/keys",
        LLMProvider.OpenAI => "https://platform.openai.com/api-keys",
        LLMProvider.Gemini => "https://aistudio.google.com/apikey",
        _ => null,
    };
    /// <summary>OpenAI 相容 API 的 key 可以不填：本機的 Ollama、LM Studio 不需要（M§19.3）</summary>
    public static bool KeyOptional(this LLMProvider p) => p == LLMProvider.OpenAICompatible;
    /// <summary>認證管理員裡的帳號名稱（Mac 是鑰匙圈的 account）</summary>
    public static string KeyAccount(this LLMProvider p) => p.Code();
    /// <summary>模型設定的鍵；API 的預設模型</summary>
    public static string ModelKey(this LLMProvider p) => p.Code() + "Model";

    public static string DefaultModel(this LLMProvider p) => p switch
    {
        LLMProvider.Anthropic => "claude-sonnet-5-5",
        LLMProvider.OpenAI => "gpt-5",
        LLMProvider.Gemini => "gemini-3.8-flash",
        _ => "",
    };

    /// <summary>結果格式不對時附加的說明：OpenAI 相容 API 的模型不一定支援 JSON 輸出</summary>
    public static string BadOutputHint(this LLMProvider p) =>
        p is LLMProvider.OpenAICompatible or LLMProvider.Enterprise ? " " + L.T("這個模型可能不支援 JSON 輸出，可以換一個模型試試。") : "";
}

/// <summary>Rejected：key 錯誤或沒權限（401、403、key 不對的 400）；Limited：429、CLI 的 usage limit；
/// Unreachable：連不到；NotConfigured：OpenAI 相容 API 沒填網址或模型、設定為不整理（M§34.4）</summary>
public enum LLMErrorKind { NotInstalled, NotLoggedIn, MissingKey, Rejected, Limited, Unreachable, NotConfigured, Failed, BadOutput }

/// <summary>整理工具的錯誤（M§19、§21.9）：訊息給使用者看；log 只寫 <see cref="LogSummary"/>，不寫內容（R18.8）</summary>
public sealed class LLMException(LLMErrorKind kind, string detail) : Exception(Describe(kind, detail))
{
    public LLMErrorKind Kind { get; } = kind;
    public string Detail { get; } = detail;

    static string Describe(LLMErrorKind kind, string detail) => kind switch
    {
        // 一般使用者不知道什麼是指令：說清楚是給已經在用 Claude Code（Codex）的人，沒有的話改用 API（R35.4）
        LLMErrorKind.NotInstalled => detail == "codex"
            ? L.T("這台電腦找不到 codex。沒有在用 Codex 的話，請到設定 → 整理工具改用 API（填 API key）或選「不整理」")
            : L.T("這台電腦找不到 claude。沒有在用 Claude Code 的話，請到設定 → 整理工具改用 API（填 API key）或選「不整理」"),
        LLMErrorKind.NotLoggedIn => detail == "codex"
            ? L.T("Codex 還沒登入或登入已過期，請重新登入後再試一次（終端機指令：codex login）。")
            : L.T("Claude Code 還沒登入或登入已過期，請重新登入後再試一次（終端機指令：claude auth login）。"),
        LLMErrorKind.MissingKey => L.F("還沒有填 %@ 的 API key，請到設定 → 整理工具填寫", detail),
        LLMErrorKind.BadOutput => L.F("回傳的會議紀錄格式不正確：%@", detail),
        _ => detail,
    };

    public static LLMException NotInstalled(string name) => new(LLMErrorKind.NotInstalled, name);
    public static LLMException NotLoggedIn(string name) => new(LLMErrorKind.NotLoggedIn, name);
    public static LLMException MissingKey(string name) => new(LLMErrorKind.MissingKey, name);
    public static LLMException Failed(string detail) => new(LLMErrorKind.Failed, detail);
    public static LLMException Rejected(string detail) => new(LLMErrorKind.Rejected, detail);
    public static LLMException Limited(string detail) => new(LLMErrorKind.Limited, detail);
    public static LLMException Unreachable(string detail) => new(LLMErrorKind.Unreachable, detail);
    public static LLMException NotConfigured(string detail) => new(LLMErrorKind.NotConfigured, detail);

    /// <summary>設定問題（R46.1）：重試也不會好，要改設定或登入</summary>
    public bool IsSetupProblem => Kind is LLMErrorKind.NotInstalled or LLMErrorKind.NotLoggedIn or LLMErrorKind.MissingKey
        or LLMErrorKind.Rejected or LLMErrorKind.NotConfigured;

    /// <summary>工作紀錄的 error_kind（LogSummary）是不是設定問題（M§34.4）</summary>
    public static bool IsSetupProblemKind(string? errorKind) =>
        errorKind is not null && new[] { "LLMError.notInstalled", "LLMError.notLoggedIn", "LLMError.missingKey", "LLMError.rejected", "LLMError.notConfigured" }
            .Any(errorKind.StartsWith);

    /// <summary>claude、codex 的輸出是不是用量上限（M§34.4）</summary>
    public static bool IsLimitProblem(string text)
    {
        var t = text.ToLowerInvariant();
        string[] markers = ["usage limit", "rate limit", "limit reached", "rate_limit"];
        return markers.Any(t.Contains);
    }
    public static LLMException BadOutput(string detail) => new(LLMErrorKind.BadOutput, detail);

    /// <summary>寫進 log 的錯誤摘要：只有種類，不含訊息（整理工具回傳的錯誤訊息可能帶有逐字稿）</summary>
    public string LogSummary => Kind switch
    {
        LLMErrorKind.NotInstalled => $"LLMError.notInstalled({Detail})",
        LLMErrorKind.NotLoggedIn => $"LLMError.notLoggedIn({Detail})",
        LLMErrorKind.MissingKey => $"LLMError.missingKey({Detail})",
        LLMErrorKind.Rejected => "LLMError.rejected",
        LLMErrorKind.Limited => "LLMError.limited",
        LLMErrorKind.Unreachable => "LLMError.unreachable",
        LLMErrorKind.NotConfigured => "LLMError.notConfigured",
        LLMErrorKind.Failed => "LLMError.failed",
        _ => "LLMError.badOutput",
    };

    /// <summary>CLI 的錯誤訊息是不是登入問題</summary>
    public static bool IsLoginProblem(string text)
    {
        var t = text.ToLowerInvariant();
        string[] markers = ["not logged in", "/login", "auth login", "codex login", "failed to authenticate", "oauth", "login required",
            "invalid api key", "unauthorized", "401"];
        return markers.Any(t.Contains);
    }
}

public static class ErrorSummary
{
    /// <summary>任何錯誤的摘要：LLM 的錯誤寫種類，其他寫型別名稱</summary>
    public static string LogSummary(this Exception e) => e is LLMException l ? l.LogSummary : e.GetType().Name;
}

/// <summary>API key 存放（Windows 是認證管理員，design-windows §12.5）</summary>
public interface IKeyStore
{
    /// <summary>讀 key；沒有回傳 null。讀不到（系統拒絕）丟例外</summary>
    string? Read(string account);
    bool Exists(string account);
}

/// <summary>企業伺服器的登入（M§36.3）：Services 的 EnterpriseServer 實作，整理工具用它拿 access token</summary>
public interface IEnterpriseAccess
{
    /// <summary>伺服器的網址；沒有登入過是 null</summary>
    string? BaseUrl { get; }
    /// <summary>登入了、伺服器有提供整理</summary>
    bool LlmReady { get; }
    /// <summary>還有效的 access token；登入失效時丟 <see cref="LLMException"/>（NotConfigured）</summary>
    Task<string> AccessToken(CancellationToken cancel);
    /// <summary>伺服器說 access token 不能用了：下次用 refresh token 換新的</summary>
    void InvalidateAccess();
    /// <summary>伺服器給的訊息用 app 的介面語言（Accept-Language）</summary>
    string Language { get; }
    /// <summary>企業伺服器專用的 HttpClient：不跟著轉址（M§36.2、D126）</summary>
    HttpClient Http { get; }
}

/// <summary>找外部程式（claude、codex），找不到回傳 null（design-windows §10）</summary>
public interface IToolLocator
{
    ToolCommand? Find(string name);
}

/// <summary>要執行的程式：真正的執行檔，加上要放在最前面的參數（例如用 node 執行 js 時是 js 的路徑）</summary>
public sealed record ToolCommand(string Executable, IReadOnlyList<string> PrefixArguments, IReadOnlyDictionary<string, string>? Environment = null);

/// <summary>一種整理工具的呼叫方式（M§19.1、D22）</summary>
public interface ILLMBackend
{
    /// <summary>回傳符合 format.Schema 的 JSON</summary>
    Task<string> GenerateJson(string system, string prompt, OutputFormat format, CancellationToken cancel);

    /// <summary>可用的模型，給設定頁選；不支援或失敗回空陣列</summary>
    Task<List<string>> ListModels(CancellationToken cancel);
}

/// <summary>整理工具的入口（M§8.4、§19）：輸出一律用 JSON schema 限制</summary>
public sealed class LLMClient(IKeyStore keys, IToolLocator tools, ProcessRunner runner, HttpClient http)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(20);

    /// <summary>API 的網址改到別的地方（DEBUG 的 llmurl：假伺服器，M§19.5）</summary>
    public Dictionary<LLMProvider, string> BaseUrlOverrides { get; } = [];

    /// <summary>OpenAI 相容 API 的網址（設定裡的 openaiCompatibleBaseURL）</summary>
    public Func<string> CompatibleBaseUrl { get; set; } = () => "";

    /// <summary>企業伺服器（M§36.3）；沒有就是 null</summary>
    public IEnterpriseAccess? Enterprise { get; set; }

    internal IKeyStore Keys => keys;
    internal IToolLocator Tools => tools;
    internal ProcessRunner Runner => runner;
    internal HttpClient Http => http;

    internal string Base(LLMProvider provider, string standard) => BaseUrlOverrides.TryGetValue(provider, out var url) ? url : standard;

    public ILLMBackend? Backend(LLMProvider provider, string model) => provider switch
    {
        LLMProvider.Claude => new ClaudeCli(this, model),
        LLMProvider.Codex => new CodexCli(this, model),
        LLMProvider.Anthropic => new AnthropicApi(this, model.Length == 0 ? provider.DefaultModel() : model),
        LLMProvider.OpenAI => new OpenAIApi(this, model.Length == 0 ? provider.DefaultModel() : model),
        LLMProvider.Gemini => new GeminiApi(this, model.Length == 0 ? provider.DefaultModel() : model),
        LLMProvider.OpenAICompatible => new OpenAICompatibleApi(this, model, Base(provider, CompatibleBaseUrl())),
        LLMProvider.Enterprise => new EnterpriseApi(this, model),
        _ => null,
    };

    public Task<string> GenerateJson(LLMProvider provider, string model, string system, string prompt, OutputFormat format, CancellationToken cancel)
    {
        var backend = Backend(provider, model) ?? throw LLMException.Failed(L.T("整理工具設定為「不整理」"));
        return backend.GenerateJson(system, prompt, format, cancel);
    }

    public async Task<MeetingNotes> Generate(LLMProvider provider, string model, string system, string prompt, CancellationToken cancel)
    {
        var json = await GenerateJson(provider, model, system, prompt, OutputFormat.MeetingNotes, cancel);
        return MeetingNotes.Decode(json) ?? throw LLMException.BadOutput("JSON" + provider.BadOutputHint());
    }

    public Task<List<string>> ListModels(LLMProvider provider, CancellationToken cancel) =>
        Backend(provider, "")?.ListModels(cancel) ?? Task.FromResult(new List<string>());

    /// <summary>公司在後台設定的模型（R51.1）；取不到是 null</summary>
    public Task<List<EnterpriseModel>?> EnterpriseModels(CancellationToken cancel) => new EnterpriseApi(this, "").Models(cancel);
}
