import Foundation

/// 整理工具還沒準備好的原因（R45.1、design §34.1、D112）。判斷不送請求、不花額度、不讀鑰匙圈的內容
enum SummarizerProblem: Equatable {
    /// 沒選過、也沒有 claude、codex（R35）
    case notChosen
    /// 使用者選了「不整理」
    case off
    case notInstalled(LLMProvider)
    case notLoggedIn(LLMProvider)
    case missingKey(LLMProvider)
    /// OpenAI 相容 API 沒填網址或模型
    case incomplete
    /// 整理工具選「公司」，但還沒登入企業伺服器或登入失效（R51.3）
    case enterpriseSignedOut
    /// 整理工具選「公司」，已登入但伺服器還沒有整理用的模型（R51.3）
    case enterpriseNoModels
    /// 設定問題的失敗之後暫停自動整理（R46.3、§34.4）；String 是那次失敗的說明
    case paused(LLMProvider, String)

    enum Action: Equatable { case openSettings, login(LLMProvider) }

    /// 原因和怎麼修，一句
    var message: String {
        switch self {
        case .notChosen: return String(localized: "還沒設定整理工具，請到設定選擇 Claude、Codex 或 API")
        case .off: return String(localized: "整理工具設定為「不整理」，請到設定改成 Claude、Codex 或 API")
        case .notInstalled(let provider): return LLMError.notInstalled(provider == .codex ? "codex" : "claude").localizedDescription
        case .notLoggedIn(let provider):
            return provider == .codex
                ? String(localized: "Codex 還沒登入，登入之後才能整理（終端機指令：codex login）。")
                : String(localized: "Claude Code 還沒登入，登入之後才能整理（終端機指令：claude auth login）。")
        case .missingKey(let provider): return String(localized: "還沒有填 \(provider.shortLabel) 的 API key，請到設定 → 整理工具填寫")
        case .incomplete: return String(localized: "還沒有填 OpenAI 相容 API 的網址或模型，請到設定 → 整理工具填寫")
        case .enterpriseSignedOut: return String(localized: "公司的伺服器還沒登入或登入已失效，請到設定 → 整理工具登入")
        case .enterpriseNoModels: return String(localized: "公司的伺服器還沒有提供整理用的模型，請通知管理者")
        case .paused(_, let detail): return detail
        }
    }

    var action: Action {
        switch self {
        case .notLoggedIn(let provider): return .login(provider)
        case .paused(let provider, let detail) where (provider == .claude || provider == .codex) && LLMError.isLoginProblem(detail):
            return .login(provider)
        default: return .openSettings
        }
    }

    var actionTitle: String {
        switch action {
        case .openSettings: return String(localized: "打開設定")
        case .login(let provider): return String(localized: "登入 \(provider == .codex ? "Codex" : "Claude Code")")
        }
    }
}

extension AppSettings {
    /// 整理工具準備好了沒；nil 是準備好了（§34.1）
    @MainActor static var summarizerProblem: SummarizerProblem? {
        if needsProviderSetup { return .notChosen }
        let provider = self.provider
        switch provider {
        case .none: return .off
        case .claude, .codex:
            if Paths.findTool(provider == .claude ? "claude" : "codex") == nil { return .notInstalled(provider) }
            if CLILoginCache.loggedIn(provider) == false { return .notLoggedIn(provider) }
        case .anthropic, .openai, .gemini:
            if !Keychain.exists(provider.keychainAccount) { return .missingKey(provider) }
        case .openaiCompatible:
            if OpenAICompatibleAPI.normalized(OpenAICompatibleAPI.savedBaseURL) == nil || model.isEmpty { return .incomplete }
        case .enterprise:
            if !EnterpriseServer.shared.isSignedIn { return .enterpriseSignedOut }
            if EnterpriseServer.shared.account?.llm != true { return .enterpriseNoModels }
        }
        if let pause = SummarizerPause.current(for: provider) { return .paused(provider, pause.message) }
        return nil
    }

    /// 自動排程（整理、校正、猜說話者、草稿）只在準備好時排（R45.4）
    @MainActor static var summarizerReady: Bool { summarizerProblem == nil }
}

/// claude、codex 登入狀態的快取（§34.1）。只有明確查到「沒有登入」才算沒準備好；還沒查或看不出來是 nil
@MainActor
enum CLILoginCache {
    private static var values: [LLMProvider: Bool] = [:]
    private static var checkedAt: [LLMProvider: Date] = [:]

    static func loggedIn(_ provider: LLMProvider) -> Bool? {
        #if DEBUG
        // -debugCLILogin no：當成沒登入（在登入了的電腦上測，design §34.6）
        if UserDefaults.standard.string(forKey: "debugCLILogin") == "no" { return false }
        #endif
        return values[provider]
    }

    static func store(_ provider: LLMProvider, _ loggedIn: Bool?) {
        values[provider] = loggedIn
        checkedAt[provider] = Date()
        if loggedIn == true { SummarizerPause.clear() }
        NotificationCenter.default.post(name: .summarizerStateChanged, object: nil)
    }

    static func isStale(_ provider: LLMProvider, after seconds: TimeInterval) -> Bool {
        checkedAt[provider].map { Date().timeIntervalSince($0) > seconds } ?? true
    }
}

/// 設定問題的失敗之後暫停自動整理（R46.3、§34.4、D113）：只在記憶體，整理工具設定的指紋變了就不算
@MainActor
enum SummarizerPause {
    struct Value { var provider: LLMProvider; var message: String; var signature: String }
    private static var value: Value?

    static func current(for provider: LLMProvider) -> Value? {
        guard let value, value.provider == provider, value.signature == signature(provider) else { return nil }
        return value
    }

    static func pause(_ provider: LLMProvider, message: String) {
        let value = Value(provider: provider, message: message, signature: signature(provider))
        self.value = value
        NotificationCenter.default.post(name: .summarizerStateChanged, object: nil)
    }

    static func clear() {
        guard value != nil else { return }
        value = nil
        NotificationCenter.default.post(name: .summarizerStateChanged, object: nil)
    }

    /// 整理工具設定的指紋：換工具、改模型或網址、存或刪 key 都會變
    static func signature(_ provider: LLMProvider) -> String {
        let url = provider == .openaiCompatible ? OpenAICompatibleAPI.savedBaseURL : ""
        return [provider.rawValue, AppSettings.model, url, String(Keychain.generation)].joined(separator: "|")
    }
}

/// 設定好整理工具時送一次的測試（design §34.3）：引導和設定頁的「測試整理工具」共用
@MainActor
enum SummarizerTest {
    /// 成功回傳花了幾秒，失敗丟出整理工具的錯誤；成功時解除暫停（R46.3）
    static func run(provider: LLMProvider, model: String) async throws -> Int {
        let started = Date()
        let language = AppSettings.defaultMeetingLanguage
        let prompt = language.text(zh: "這是連線測試。逐字稿：\n[00:00] 說話者 1：大家好，今天只是測試一下。",
                                   en: "This is a connection test. Transcript:\n[00:00] Speaker 1: Hi everyone, this is just a test.",
                                   ja: "これは接続テストです。文字起こし：\n[00:00] 話者 1：皆さん、今日はテストだけです。")  // l10n-skip
        _ = try await LLMClient.generate(provider: provider, model: model, system: NotesPrompt.system(language),
                                         prompt: prompt, onStart: { _ in })
        SummarizerPause.clear()
        return Int(Date().timeIntervalSince(started).rounded())
    }

    /// 這個整理工具在設定裡的模型（引導裡選的不一定是目前的整理工具）
    static func model(for provider: LLMProvider) -> String {
        provider == AppSettings.provider ? AppSettings.model : UserDefaults.standard.string(forKey: provider.modelKey) ?? ""
    }
}
