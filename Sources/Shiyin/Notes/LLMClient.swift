import Foundation

enum LLMProvider: String, CaseIterable, Identifiable {
    /// enterprise：公司的企業伺服器（R51、design §36.3），選單一直有（D124），模型由公司的後台設定
    case claude, codex, antigravity, anthropic, openai, gemini, openaiCompatible, enterprise, none
    var id: String { rawValue }

    /// 使用者自己安裝、登入的命令列工具（claude、codex、agy）的執行檔名稱；API、公司、不整理是 nil
    var cliName: String? {
        switch self {
        case .claude: return "claude"
        case .codex: return "codex"
        case .antigravity: return "agy"
        default: return nil
        }
    }
    var isCLI: Bool { cliName != nil }
    /// CLI 的產品名稱：登入按鈕、說明用（「登入 Claude Code」）
    var cliProductName: String { self == .claude ? "Claude Code" : shortLabel }

    /// 整理時送會議中的畫面的圖片本身（design §83.5、D188）：OpenAI 相容 API 看設定（很多本機模型不能看圖），企業伺服器先只送文字
    @MainActor
    var sendsScreenImages: Bool {
        switch self {
        case .claude, .codex, .anthropic, .openai, .gemini: true
        case .openaiCompatible: AppSettings.compatibleSendsImages
        // agy 一次跑完的模式不能附圖（R121.5）
        case .antigravity, .enterprise, .none: false
        }
    }

    var label: String {
        switch self {
        case .claude: return String(localized: "Claude（claude CLI）")
        case .codex: return String(localized: "Codex（codex CLI）")
        case .antigravity: return String(localized: "Antigravity（agy CLI）")
        case .anthropic: return "Anthropic API"
        case .openai: return "OpenAI API"
        case .gemini: return "Gemini API"
        case .openaiCompatible: return String(localized: "OpenAI 相容 API")
        case .enterprise: return String(localized: "公司")
        case .none: return String(localized: "不整理")
        }
    }

    /// 「由 xx 整理」用的名稱
    var shortLabel: String {
        switch self {
        case .claude: return "Claude"
        case .codex: return "Codex"
        case .antigravity: return "Antigravity"
        case .anthropic: return "Anthropic API"
        case .openai: return "OpenAI API"
        case .gemini: return "Gemini API"
        case .openaiCompatible: return String(localized: "OpenAI 相容 API")
        case .enterprise: return String(localized: "公司的模型")
        case .none: return String(localized: "不整理")
        }
    }

    var usesAPIKey: Bool { [.anthropic, .openai, .gemini, .openaiCompatible].contains(self) }
    /// 「取得 API key」的網頁（R35.3）；OpenAI 相容 API 各家不同，沒有
    var apiKeyURL: URL? {
        switch self {
        case .anthropic: return URL(string: "https://console.anthropic.com/settings/keys")
        case .openai: return URL(string: "https://platform.openai.com/api-keys")
        case .gemini: return URL(string: "https://aistudio.google.com/apikey")
        default: return nil
        }
    }
    /// OpenAI 相容 API 的 key 可以不填：本機的 Ollama、LM Studio 不需要（design §19.3）
    var keyOptional: Bool { self == .openaiCompatible }
    /// 鑰匙圈的 account（design §8.4）
    var keychainAccount: String { rawValue }

    /// 模型設定存在 UserDefaults 的 key；API 的預設模型
    var modelKey: String { "\(rawValue)Model" }
    var defaultModel: String {
        switch self {
        case .anthropic: return "claude-sonnet-5-5"
        case .openai: return "gpt-5"
        case .gemini: return "gemini-3.8-flash"
        default: return ""
        }
    }

    /// 結果格式不對時附加的說明：OpenAI 相容 API 的模型不一定支援 JSON 輸出（design §19.3）
    var badOutputHint: String {
        self == .openaiCompatible || self == .enterprise ? " " + String(localized: "這個模型可能不支援 JSON 輸出，可以換一個模型試試。") : ""
    }
}

/// 「其他」的失敗是哪一種（design §61.1、R86）：只看 HTTP 狀態碼、CLI 的結束代碼，不看錯誤的文字（D114）
enum FailureCause: String, CaseIterable {
    case http400, http404, http413, http500, http502, http503, http504, http529, httpOther
    case incomplete
    case cliExit1, cliExit2, cliExit127, cliSignal, cliExitOther, cliNoOutput, cliError
    case unknown

    static func http(_ status: Int) -> FailureCause {
        switch status {
        case 400: return .http400
        case 404: return .http404
        case 413: return .http413
        case 500: return .http500
        case 502: return .http502
        case 503: return .http503
        case 504: return .http504
        case 529: return .http529
        default: return .httpOther
        }
    }

    /// CLI 結束了但沒有結果：結束代碼 0 是沒有輸出；被訊號結束（或 128 以上）是被系統結束
    static func cli(_ result: SubprocessResult) -> FailureCause {
        if result.reason == .uncaughtSignal || result.status > 128 { return .cliSignal }
        switch result.status {
        case 0: return .cliNoOutput
        case 1: return .cliExit1
        case 2: return .cliExit2
        case 127: return .cliExit127
        default: return .cliExitOther
        }
    }
}

enum LLMError: LocalizedError {
    case notInstalled(String)
    /// claude / codex 沒登入或登入過期（使用者回報：只看到英文的 OAuth 錯誤，不知道要做什麼）
    case notLoggedIn(String)
    case missingKey(String)
    /// key 錯誤或沒有權限：HTTP 401、403、Gemini 的「API key not valid」400（design §34.4）
    case rejected(String)
    /// 額度或用量上限：HTTP 429、claude／codex 的 usage limit
    case limited(String)
    /// 連不到服務（網路）
    case unreachable(String)
    /// OpenAI 相容 API 沒填網址或模型、網址不對；整理工具是「不整理」
    case notConfigured(String)
    /// 其他的失敗；cause 是統計、回報問題用的種類（design §61.1、R86），不含任何文字
    case failed(String, cause: FailureCause = .unknown)
    case badOutput(String)
    /// 提問的回答格式不對（design §21.8）：不說「會議紀錄」
    case badAnswer(String)

    var errorDescription: String? {
        switch self {
        case .notInstalled(let name):
            // 一般使用者不知道什麼是指令：說清楚是給已經在用 Claude Code（Codex）的人，沒有的話改用 API（R35.4）
            if name == "claude" && DesktopApps.hasClaude {
                // 只有 Claude 桌面版：說明要另外裝 Claude Code（R56.3）
                return String(localized: "這台 Mac 找不到 Claude Code（命令列版）。Claude 桌面版不能直接用，請到設定 → 整理工具照說明安裝 Claude Code；或改用 API（填 API key）、選「不整理」")
            }
            switch name {
            case "codex": return String(localized: "這台 Mac 找不到 codex。沒有在用 Codex 的話，請到設定 → 整理工具改用 API（填 API key）或選「不整理」")
            case "agy": return String(localized: "這台 Mac 找不到 agy。沒有在用 Antigravity 的話，請到設定 → 整理工具改用 API（填 API key）或選「不整理」")
            default: return String(localized: "這台 Mac 找不到 claude。沒有在用 Claude Code 的話，請到設定 → 整理工具改用 API（填 API key）或選「不整理」")
            }
        case .notLoggedIn(let name):
            // 用桌面版附的 codex：到那個 app 登入（R55.3）
            if name == "codex", let app = CLILogin.desktopApp(.codex) {
                return String(localized: "Codex 還沒登入或登入已過期，請打開 \(app) 重新登入後再試一次。")
            }
            switch name {
            case "codex": return String(localized: "Codex 還沒登入或登入已過期，請重新登入後再試一次（終端機指令：codex login）。")
            case "agy": return String(localized: "Antigravity 還沒登入或登入已過期，請重新登入後再試一次（終端機指令：agy）。")
            default: return String(localized: "Claude Code 還沒登入或登入已過期，請重新登入後再試一次（終端機指令：claude auth login）。")
            }
        case .missingKey(let name): return String(localized: "還沒有填 \(name) 的 API key，請到設定 → 整理工具填寫")
        case .rejected(let detail), .limited(let detail), .unreachable(let detail), .notConfigured(let detail): return detail
        case .failed(let detail, _): return detail
        case .badOutput(let detail): return String(localized: "回傳的會議紀錄格式不正確：\(detail)")
        case .badAnswer(let detail): return String(localized: "回傳的回答格式不正確：\(detail)")
        }
    }

    /// 設定問題（R46.1）：重試也不會好，要改設定或登入
    var isSetupProblem: Bool {
        switch self {
        case .notInstalled, .notLoggedIn, .missingKey, .rejected, .notConfigured: return true
        case .limited, .unreachable, .failed, .badOutput, .badAnswer: return false
        }
    }

    /// 工作紀錄的 error_kind（`logSummary`）是不是設定問題（design §34.4）
    static func isSetupProblem(errorKind: String?) -> Bool {
        guard let errorKind else { return false }
        return ["LLMError.notInstalled", "LLMError.notLoggedIn", "LLMError.missingKey", "LLMError.rejected", "LLMError.notConfigured"]
            .contains { errorKind.hasPrefix($0) }
    }

    /// claude、codex 的輸出是不是用量上限（R62.1、design §40.1）：「Claude AI usage limit reached」「You've hit your usage limit」
    /// 「You've hit your weekly limit · resets Nov 2 at 9pm」「Credit balance is too low」「429 Too Many Requests」
    static func isLimitProblem(_ text: String) -> Bool {
        let t = text.lowercased()
        let markers = ["usage limit", "rate limit", "limit reached", "rate_limit",
                       "credit balance is too low", "insufficient_quota", "exceeded your current quota", "too many requests"]
        if markers.contains(where: { t.contains($0) }) { return true }
        // 「hit your limit」「hit your weekly limit」「hit your Opus limit」；「… limit · resets 3pm」
        return t.contains(#/hit your[^.\n]{0,30}limit/#) || t.contains(#/limit\W{1,5}resets/#)
    }

    /// claude、codex 的額度用完（R62.2、design §40.2）：說是哪個工具、怎麼辦，下一行附上 CLI 的原文（裡面有什麼時候恢復，D137）
    static func cliLimited(_ provider: LLMProvider, output: String) -> LLMError {
        let advice = switch provider {
        case .codex: String(localized: "Codex 的用量額度用完了，等恢復後再試，或到設定 → 整理工具先換成 Claude Code 或 API。")
        case .antigravity: String(localized: "Antigravity 的用量額度用完了，等恢復後再試，或到設定 → 整理工具先換成 Gemini API 或其他整理工具。")
        default: String(localized: "Claude Code 的用量額度用完了，等恢復後再試，或到設定 → 整理工具先換成 Codex 或 API。")
        }
        // 最後一行符合的：codex 的 stderr 前面可能有別的 log，錯誤在最後
        guard let line = output.split(whereSeparator: \.isNewline)
            .map({ $0.trimmingCharacters(in: .whitespaces) })
            .last(where: { isLimitProblem($0) }) else { return .limited(advice) }
        let original = String(line.prefix(300))
        let source = switch provider {
        case .codex: String(localized: "Codex 的訊息：\(original)")
        case .antigravity: String(localized: "Antigravity 的訊息：\(original)")
        default: String(localized: "Claude Code 的訊息：\(original)")
        }
        return .limited(advice + "\n" + source)
    }

    /// 工作紀錄的失敗訊息是不是登入問題：CLI 的訊息（isLoginProblem），加上桌面版附的 codex 的「打開 ChatGPT 重新登入」（沒有終端機指令，R55.3）、agy 的說明
    static func isLoginMessage(_ text: String) -> Bool {
        isLoginProblem(text) || (CLILogin.desktopApp(.codex) != nil && text.contains(LLMError.notLoggedIn("codex").localizedDescription))
            // agy 的說明文字裡沒有英文的登入字樣（登入就是直接開 agy）
            || text.contains(LLMError.notLoggedIn("agy").localizedDescription)
    }

    /// CLI 的錯誤訊息是不是登入問題，例如「Not logged in · Please run /login」「Failed to authenticate: OAuth session expired」、
    /// agy 的「authentication required. Run 'agy' to log in」「Please sign in to view available models」
    static func isLoginProblem(_ text: String) -> Bool {
        let t = text.lowercased()
        // 自己的說明文字裡有「claude auth login」「codex login」，存在工作紀錄的失敗訊息也要認得
        return ["not logged in", "/login", "auth login", "codex login", "failed to authenticate", "oauth", "login required",
                "authentication required", "authentication failed", "please sign in",
                "invalid api key", "unauthorized", "401"].contains { t.contains($0) }
    }
}

extension Error {
    /// 寫進 log 用的錯誤摘要：只有種類，不含訊息。整理工具回傳的錯誤訊息可能帶有逐字稿或會議紀錄（design §20.8、R18.8）
    var logSummary: String {
        if let error = self as? LLMError {
            switch error {
            case .notInstalled(let name): return "LLMError.notInstalled(\(name))"
            case .notLoggedIn(let name): return "LLMError.notLoggedIn(\(name))"
            case .missingKey(let name): return "LLMError.missingKey(\(name))"
            case .rejected: return "LLMError.rejected"
            case .limited: return "LLMError.limited"
            case .unreachable: return "LLMError.unreachable"
            case .notConfigured: return "LLMError.notConfigured"
            case .failed(_, let cause): return cause == .unknown ? "LLMError.failed" : "LLMError.failed.\(cause.rawValue)"
            case .badOutput: return "LLMError.badOutput"
            case .badAnswer: return "LLMError.badAnswer"
            }
        }
        let error = self as NSError
        return "\(error.domain) \(error.code)"
    }
}

/// 一種整理工具的呼叫方式（design §19.1、D22）。每種整理工具一個實作，放在 Notes/Backends/
@MainActor
protocol LLMBackend {
    /// 回傳符合 format.schema 的 JSON；images 是會議中的畫面（design §83.5），不支援看圖的整理工具不會收到
    func generateJSON(system: String, prompt: String, format: LLMClient.OutputFormat, images: [LLMImage],
                      onStart: (Subprocess) -> Void) async throws -> Data
    /// 可用的模型，給設定頁選；不支援或失敗回空陣列
    func listModels() async -> [String]
}

extension LLMProvider {
    /// 這個整理工具的實作；「不整理」沒有
    @MainActor
    func backend(model: String) -> (any LLMBackend)? {
        switch self {
        case .claude: return ClaudeCLI(model: model)
        case .codex: return CodexCLI(model: model)
        case .antigravity: return AntigravityCLI(model: model)
        case .anthropic: return AnthropicAPI(model: model.isEmpty ? defaultModel : model)
        case .openai: return OpenAIAPI(model: model.isEmpty ? defaultModel : model)
        case .gemini: return GeminiAPI(model: model.isEmpty ? defaultModel : model)
        case .openaiCompatible: return OpenAICompatibleAPI(model: model, baseURL: OpenAICompatibleAPI.savedBaseURL)
        case .enterprise: return EnterpriseAPI(model: model)
        case .none: return nil
        }
    }
}

/// 產生會議紀錄的入口（design §8.4、§19）。
/// 輸出一律用 JSON schema 限制；逐字稿校正（design §17.4）用同一套呼叫方式，只是格式不同
@MainActor
enum LLMClient {
    static let timeout: TimeInterval = 20 * 60

    #if DEBUG
    /// shiyinctl llmurl：把 API 的網址改到本機的假伺服器（design §19.5）
    static var debugBaseURLs: [LLMProvider: String] = [:]
    #endif

    /// API 的網址（不含路徑）；DEBUG 建置可以用 llmurl 改掉
    static func apiBase(_ provider: LLMProvider, _ standard: String) -> String {
        #if DEBUG
        if let url = debugBaseURLs[provider] { return url }
        #endif
        return standard
    }

    /// 要模型輸出的格式：JSON schema，以及 Anthropic API 用的工具名稱和說明
    struct OutputFormat {
        let schema: String
        let toolName: String
        let toolDescription: String

        static var meetingNotes: OutputFormat {
            OutputFormat(schema: MeetingNotes.jsonSchema, toolName: "save_meeting_notes",
                         toolDescription: String(localized: "儲存整理好的會議紀錄"))
        }
    }

    static func generate(provider: LLMProvider, model: String, system: String, prompt: String, images: [LLMImage] = [],
                         onStart: (Subprocess) -> Void) async throws -> MeetingNotes {
        let data = try await generateJSON(provider: provider, model: model, system: system, prompt: prompt,
                                          format: .meetingNotes, images: images, onStart: onStart)
        do { return try JSONDecoder().decode(MeetingNotes.self, from: data) }
        catch { throw LLMError.badOutput(error.localizedDescription + provider.badOutputHint) }
    }

    /// 回傳符合 format.schema 的 JSON
    static func generateJSON(provider: LLMProvider, model: String, system: String, prompt: String,
                             format: OutputFormat, images: [LLMImage] = [], onStart: (Subprocess) -> Void) async throws -> Data {
        guard let backend = provider.backend(model: model) else {
            throw LLMError.failed(String(localized: "整理工具設定為「不整理」"))
        }
        do {
            return try await backend.generateJSON(system: system, prompt: prompt, format: format, images: images, onStart: onStart)
        } catch let error as LLMError where !images.isEmpty && error.rejectsImages {
            // 模型不能看圖（design §83.5）：同一次整理改成只送文字，畫面辨識出的文字照樣在提示詞裡
            log.info("model rejected images, retrying with text only")
            return try await backend.generateJSON(system: system, prompt: prompt, format: format, images: [], onStart: onStart)
        }
    }

    /// 列出可用的模型，給設定頁選（失敗就回空陣列）
    static func listModels(_ provider: LLMProvider) async -> [String] {
        await provider.backend(model: "")?.listModels() ?? []
    }
}
