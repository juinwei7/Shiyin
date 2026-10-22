import Foundation

enum LLMProvider: String, CaseIterable, Identifiable {
    case claude, codex, anthropic, openai, gemini, openaiCompatible, none
    var id: String { rawValue }

    var label: String {
        switch self {
        case .claude: return String(localized: "Claude（claude CLI）")
        case .codex: return String(localized: "Codex（codex CLI）")
        case .anthropic: return "Anthropic API"
        case .openai: return "OpenAI API"
        case .gemini: return "Gemini API"
        case .openaiCompatible: return String(localized: "OpenAI 相容 API")
        case .none: return String(localized: "不整理")
        }
    }

    /// 「由 xx 整理」用的名稱
    var shortLabel: String {
        switch self {
        case .claude: return "Claude"
        case .codex: return "Codex"
        case .anthropic: return "Anthropic API"
        case .openai: return "OpenAI API"
        case .gemini: return "Gemini API"
        case .openaiCompatible: return String(localized: "OpenAI 相容 API")
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
        self == .openaiCompatible ? " " + String(localized: "這個模型可能不支援 JSON 輸出，可以換一個模型試試。") : ""
    }
}

enum LLMError: LocalizedError {
    case notInstalled(String)
    /// claude / codex 沒登入或登入過期（使用者回報：只看到英文的 OAuth 錯誤，不知道要做什麼）
    case notLoggedIn(String)
    case missingKey(String)
    case failed(String)
    case badOutput(String)

    var errorDescription: String? {
        switch self {
        case .notInstalled(let name):
            // 一般使用者不知道什麼是指令：說清楚是給已經在用 Claude Code（Codex）的人，沒有的話改用 API（R35.4）
            return name == "codex"
                ? String(localized: "這台 Mac 找不到 codex。沒有在用 Codex 的話，請到設定 → 整理工具改用 API（填 API key）或選「不整理」")
                : String(localized: "這台 Mac 找不到 claude。沒有在用 Claude Code 的話，請到設定 → 整理工具改用 API（填 API key）或選「不整理」")
        case .notLoggedIn(let name):
            return name == "codex"
                ? String(localized: "Codex 還沒登入或登入已過期，請重新登入後再試一次（終端機指令：codex login）。")
                : String(localized: "Claude Code 還沒登入或登入已過期，請重新登入後再試一次（終端機指令：claude auth login）。")
        case .missingKey(let name): return String(localized: "還沒有填 \(name) 的 API key，請到設定 → 整理工具填寫")
        case .failed(let detail): return detail
        case .badOutput(let detail): return String(localized: "回傳的會議紀錄格式不正確：\(detail)")
        }
    }

    /// CLI 的錯誤訊息是不是登入問題，例如「Not logged in · Please run /login」「Failed to authenticate: OAuth session expired」
    static func isLoginProblem(_ text: String) -> Bool {
        let t = text.lowercased()
        // 自己的說明文字裡有「claude auth login」「codex login」，存在工作紀錄的失敗訊息也要認得
        return ["not logged in", "/login", "auth login", "codex login", "failed to authenticate", "oauth", "login required",
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
            case .failed: return "LLMError.failed"
            case .badOutput: return "LLMError.badOutput"
            }
        }
        let error = self as NSError
        return "\(error.domain) \(error.code)"
    }
}

/// 一種整理工具的呼叫方式（design §19.1、D22）。每種整理工具一個實作，放在 Notes/Backends/
@MainActor
protocol LLMBackend {
    /// 回傳符合 format.schema 的 JSON
    func generateJSON(system: String, prompt: String, format: LLMClient.OutputFormat,
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
        case .anthropic: return AnthropicAPI(model: model.isEmpty ? defaultModel : model)
        case .openai: return OpenAIAPI(model: model.isEmpty ? defaultModel : model)
        case .gemini: return GeminiAPI(model: model.isEmpty ? defaultModel : model)
        case .openaiCompatible: return OpenAICompatibleAPI(model: model, baseURL: OpenAICompatibleAPI.savedBaseURL)
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

    static func generate(provider: LLMProvider, model: String, system: String, prompt: String,
                         onStart: (Subprocess) -> Void) async throws -> MeetingNotes {
        let data = try await generateJSON(provider: provider, model: model, system: system, prompt: prompt,
                                          format: .meetingNotes, onStart: onStart)
        do { return try JSONDecoder().decode(MeetingNotes.self, from: data) }
        catch { throw LLMError.badOutput(error.localizedDescription + provider.badOutputHint) }
    }

    /// 回傳符合 format.schema 的 JSON
    static func generateJSON(provider: LLMProvider, model: String, system: String, prompt: String,
                             format: OutputFormat, onStart: (Subprocess) -> Void) async throws -> Data {
        guard let backend = provider.backend(model: model) else {
            throw LLMError.failed(String(localized: "整理工具設定為「不整理」"))
        }
        return try await backend.generateJSON(system: system, prompt: prompt, format: format, onStart: onStart)
    }

    /// 列出可用的模型，給設定頁選（失敗就回空陣列）
    static func listModels(_ provider: LLMProvider) async -> [String] {
        await provider.backend(model: "")?.listModels() ?? []
    }
}
