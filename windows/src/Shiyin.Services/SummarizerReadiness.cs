using Shiyin.LLM;
using Shiyin.Localization;

namespace Shiyin.Services;

/// <summary>整理工具還沒準備好的原因（R45.1、M§34.1、D112）：判斷不送請求、不花額度、不讀認證管理員的內容</summary>
/// <summary>EnterpriseSignedOut：整理工具選「公司」，但還沒登入企業伺服器或登入失效；EnterpriseNoModels：已登入但伺服器還沒有整理用的模型（R51.3）</summary>
public enum SummarizerProblemKind { NotChosen, Off, NotInstalled, NotLoggedIn, MissingKey, Incomplete, EnterpriseSignedOut, EnterpriseNoModels, Paused }

/// <summary>Detail：暫停時是那次失敗的說明</summary>
public sealed record SummarizerProblem(SummarizerProblemKind Kind, LLMProvider Provider, string Detail = "")
{
    /// <summary>原因和怎麼修，一句</summary>
    public string Message => Kind switch
    {
        SummarizerProblemKind.NotChosen => L.T("還沒設定整理工具，請到設定選擇 Claude、Codex 或 API"),
        SummarizerProblemKind.Off => L.T("整理工具設定為「不整理」，請到設定改成 Claude、Codex 或 API"),
        SummarizerProblemKind.NotInstalled => LLMException.NotInstalled(Provider == LLMProvider.Codex ? "codex" : "claude").Message,
        // 用桌面版附的 codex：到那個 app 登入（R55.3）
        SummarizerProblemKind.NotLoggedIn when DesktopApp is { } app => L.F("Codex 還沒登入，請打開 %@ 登入之後再整理。", app),
        SummarizerProblemKind.NotLoggedIn => Provider == LLMProvider.Codex
            ? L.T("Codex 還沒登入，登入之後才能整理（終端機指令：codex login）。")
            : L.T("Claude Code 還沒登入，登入之後才能整理（終端機指令：claude auth login）。"),
        SummarizerProblemKind.MissingKey => L.F("還沒有填 %@ 的 API key，請到設定 → 整理工具填寫", Provider.ShortLabel()),
        SummarizerProblemKind.Incomplete => L.T("還沒有填 OpenAI 相容 API 的網址或模型，請到設定 → 整理工具填寫"),
        SummarizerProblemKind.EnterpriseSignedOut => L.T("公司的伺服器還沒登入或登入已失效，請到設定 → 進階 → 企業伺服器登入"),
        SummarizerProblemKind.EnterpriseNoModels => L.T("公司的伺服器還沒有提供整理用的模型，請通知管理者"),
        _ => Detail,
    };

    /// <summary>修的按鈕是「登入」（不是「打開設定」）</summary>
    public bool OffersLogin => Kind == SummarizerProblemKind.NotLoggedIn
        || (Kind == SummarizerProblemKind.Paused && Provider is LLMProvider.Claude or LLMProvider.Codex && LLMException.IsLoginMessage(Detail));

    public string ActionTitle => !OffersLogin ? L.T("打開設定")
        : DesktopApp is { } app ? L.F("打開 %@ 登入", app)
        : L.F("登入 %@", Provider == LLMProvider.Codex ? "Codex" : "Claude Code");

    /// <summary>用 Codex 桌面版附的 codex 時是那個 app 的名稱（R55.3）</summary>
    string? DesktopApp => Provider == LLMProvider.Codex ? DesktopApps.CodexAppName() : null;
}
