import Foundation

/// 匿名使用統計（design §30、R40、D100）：每天一份匯總，送到建置時寫進 app 的統計網址。
/// 不含任何會議內容（R40.2）。沒有網址、或使用者關掉時，不累計也不送。任何執行緒都能呼叫。
final class UsageStats: @unchecked Sendable {
    static let shared = UsageStats()
    /// 設定 → 進階的開關（預設開）
    static let enabledKey = "usageStats"

    struct Counters: Codable, Equatable {
        var meetingsRecorded = 0, recordedSeconds = 0
        var meetingsImported = 0, importedSeconds = 0
        var transcribedSeconds = 0, transcribeWallSeconds = 0
        var engines = ["breeze": 0, "builtin": 0, "whisperJa": 0, "qwen": 0]
        var meetingLanguages = ["zh": 0, "en": 0, "ja": 0]
        var diarizationRuns = 0
        var summarizer = ["claudeCode": 0, "codex": 0, "api": 0, "local": 0]
        var questionsAsked = 0
        /// 待確認送出的題數（R41.7、§31.3）：回答（打字或「不是」）、確認（「是」）、略過
        var review = ["answered": 0, "confirmed": 0, "skipped": 0]
        var errors = ["recording": 0, "transcribe": 0, "diarize": 0, "summarize": 0]
        /// 整理失敗的種類、當時的整理工具、其中幾次是草稿（R47、§34.5）：只有次數
        var summarizeErrors = Counters.summarizeErrorKinds.reduce(into: [String: Int]()) { $0[$1] = 0 }
        var summarizeErrorProviders = ["claudeCode": 0, "codex": 0, "api": 0, "local": 0]
        var summarizeDraftErrors = 0

        static let summarizeErrorKinds = ["notInstalled", "notLoggedIn", "missingKey", "notConfigured", "keyRejected",
                                          "limited", "timeout", "unreachable", "badOutput", "other"]

        init() {}

        /// 舊版存的檔案沒有後來加的欄位（例如 review）：少的當 0，不要整份重來
        init(from decoder: Decoder) throws {
            let c = try decoder.container(keyedBy: CodingKeys.self)
            let base = Counters()
            meetingsRecorded = try c.decodeIfPresent(Int.self, forKey: .meetingsRecorded) ?? 0
            recordedSeconds = try c.decodeIfPresent(Int.self, forKey: .recordedSeconds) ?? 0
            meetingsImported = try c.decodeIfPresent(Int.self, forKey: .meetingsImported) ?? 0
            importedSeconds = try c.decodeIfPresent(Int.self, forKey: .importedSeconds) ?? 0
            transcribedSeconds = try c.decodeIfPresent(Int.self, forKey: .transcribedSeconds) ?? 0
            transcribeWallSeconds = try c.decodeIfPresent(Int.self, forKey: .transcribeWallSeconds) ?? 0
            engines = try c.decodeIfPresent([String: Int].self, forKey: .engines) ?? base.engines
            meetingLanguages = try c.decodeIfPresent([String: Int].self, forKey: .meetingLanguages) ?? base.meetingLanguages
            diarizationRuns = try c.decodeIfPresent(Int.self, forKey: .diarizationRuns) ?? 0
            summarizer = try c.decodeIfPresent([String: Int].self, forKey: .summarizer) ?? base.summarizer
            questionsAsked = try c.decodeIfPresent(Int.self, forKey: .questionsAsked) ?? 0
            review = try c.decodeIfPresent([String: Int].self, forKey: .review) ?? base.review
            errors = try c.decodeIfPresent([String: Int].self, forKey: .errors) ?? base.errors
            summarizeErrors = try c.decodeIfPresent([String: Int].self, forKey: .summarizeErrors) ?? base.summarizeErrors
            summarizeErrorProviders = try c.decodeIfPresent([String: Int].self, forKey: .summarizeErrorProviders) ?? base.summarizeErrorProviders
            summarizeDraftErrors = try c.decodeIfPresent(Int.self, forKey: .summarizeDraftErrors) ?? 0
        }
    }

    struct Store: Codable {
        var installId = UUID().uuidString.lowercased()
        var days: [String: Counters] = [:]
        var sent: [String] = []
    }

    enum Failure: String { case recording, transcribe, diarize, summarize }

    private let lock = NSLock()
    private var cached: Store?
    private var sending = false

    #if DEBUG
    /// shiyinctl usageurl：只在記憶體設統計網址；DEBUG 版不讀 Info.plist 的網址（design §30.7）
    nonisolated(unsafe) static var debugURL: String?
    #endif

    // MARK: 開關、網址

    /// 建置時寫進 Info.plist 的統計網址（§30.6）。只接受 https；localhost、127.0.0.1 給測試用
    static var endpoint: URL? {
        #if DEBUG
        let raw = debugURL ?? ""
        #else
        let raw = Bundle.main.object(forInfoDictionaryKey: "ShiyinUsageURL") as? String ?? ""
        #endif
        let text = raw.trimmingCharacters(in: .whitespaces)
        guard !text.isEmpty, let url = URL(string: text), let scheme = url.scheme?.lowercased() else { return nil }
        if scheme == "https" { return url }
        if scheme == "http", ["localhost", "127.0.0.1"].contains(url.host?.lowercased() ?? "") { return url }
        return nil
    }

    /// 設定頁、使用說明要不要顯示這個功能：有網址的版本才有（DEBUG 版一律顯示，§30.5）
    static var isOffered: Bool {
        #if DEBUG
        return true
        #else
        return endpoint != nil
        #endif
    }

    static var isEnabled: Bool {
        let d = UserDefaults.standard
        return d.object(forKey: enabledKey) == nil ? true : d.bool(forKey: enabledKey)
    }

    private static var isActive: Bool { endpoint != nil && isEnabled }

    // MARK: 累計

    /// 錄音存檔成功
    func recorded(seconds: Double) {
        add { $0.meetingsRecorded += 1; $0.recordedSeconds += Self.whole(seconds) }
    }

    /// 匯入音訊或影片成功（長度在轉錄完成時才加，見 transcribed）
    func imported() {
        add { $0.meetingsImported += 1 }
    }

    /// 轉錄完成。engine 是 Meeting.transcribedWith（breeze、system、japanese）；system 記成 builtin（跨平台的名稱，§30.1）
    func transcribed(seconds: Double, wallSeconds: Double, engine: String, language: MeetingLanguage,
                     imported: Bool, diarized: Bool) {
        let audio = Self.whole(seconds)
        let key = ["system": "builtin", "japanese": "whisperJa", "qwen": "qwen"][engine] ?? "breeze"
        add {
            $0.transcribedSeconds += audio
            $0.transcribeWallSeconds += Self.whole(wallSeconds)
            $0.engines[key, default: 0] += audio
            $0.meetingLanguages[language.rawValue, default: 0] += 1
            if imported { $0.importedSeconds += audio }
            if diarized { $0.diarizationRuns += 1 }
        }
    }

    /// 重新辨識說話者完成
    func rediarized() {
        add { $0.diarizationRuns += 1 }
    }

    /// 整理（含草稿、修正）成功。只記整理工具的種類，不記網址、模型
    @MainActor
    func summarized(with provider: LLMProvider) {
        guard let key = Self.providerKind(provider) else { return }
        add { $0.summarizer[key, default: 0] += 1 }
    }

    /// 整理（含草稿、修正）失敗（R47、§34.5）：原本的次數照舊，另外記種類、整理工具、是不是草稿；不記錯誤訊息
    @MainActor
    func summarizeFailed(_ error: Error, provider: LLMProvider, draft: Bool) {
        let kind = Self.summarizeFailureKind(error)
        let providerKey = Self.providerKind(provider)
        add {
            $0.errors["summarize", default: 0] += 1
            $0.summarizeErrors[kind, default: 0] += 1
            if let providerKey { $0.summarizeErrorProviders[providerKey, default: 0] += 1 }
            if draft { $0.summarizeDraftErrors += 1 }
        }
    }

    /// 整理工具的種類：claudeCode、codex、api、local（本機的 OpenAI 相容服務）；none 是 nil
    @MainActor
    static func providerKind(_ provider: LLMProvider) -> String? {
        switch provider {
        case .claude: return "claudeCode"
        case .codex: return "codex"
        case .openaiCompatible:
            let local = URL(string: OpenAICompatibleAPI.savedBaseURL).map(OpenAICompatibleAPI.isLocal) ?? false
            return local ? "local" : "api"
        case .anthropic, .openai, .gemini: return "api"
        case .none: return nil
        }
    }

    /// 失敗的種類（§34.5）
    static func summarizeFailureKind(_ error: Error) -> String {
        if let llm = error as? LLMError {
            switch llm {
            case .notInstalled: return "notInstalled"
            case .notLoggedIn: return "notLoggedIn"
            case .missingKey: return "missingKey"
            case .notConfigured: return "notConfigured"
            case .rejected: return "keyRejected"
            case .limited: return "limited"
            case .unreachable: return "unreachable"
            case .badOutput: return "badOutput"
            case .failed: return "other"
            }
        }
        if case SubprocessError.timeout = error { return "timeout" }
        if (error as? URLError)?.code == .timedOut { return "timeout" }
        return "other"
    }

    func asked() {
        add { $0.questionsAsked += 1 }
    }

    /// 待確認送出成功（R41.7）：只記題數，不記題目和答案
    func reviewed(answered: Int, confirmed: Int, skipped: Int) {
        guard answered + confirmed + skipped > 0 else { return }
        add {
            $0.review["answered", default: 0] += answered
            $0.review["confirmed", default: 0] += confirmed
            $0.review["skipped", default: 0] += skipped
        }
    }

    func failed(_ kind: Failure) {
        add { $0.errors[kind.rawValue, default: 0] += 1 }
    }

    /// 打開 app、每小時檢查時建立今天的空白匯總：沒有用量的日子也算活躍（§30.3）
    func touch() {
        add { _ in }
    }

    private func add(_ update: (inout Counters) -> Void) {
        guard Self.isActive else { return }
        lock.lock()
        defer { lock.unlock() }
        var store = loadLocked()
        let today = Self.dateString(Date())
        var counters = store.days[today] ?? Counters()
        update(&counters)
        store.days[today] = counters
        saveLocked(store)
    }

    // MARK: 設定頁、DEBUG

    /// 下一份要送的內容（shiyinctl usage 用；設定頁不顯示）；還沒有已過完的日子就是今天到目前為止的
    func previewJSON() -> String {
        lock.lock()
        let store = loadLocked()
        lock.unlock()
        let today = Self.dateString(Date())
        let date = pendingDates(in: store, today: today).first ?? today
        let payload = Self.payload(installId: store.installId, date: date, counters: store.days[date] ?? Counters())
        return String(decoding: Self.encode(payload, pretty: true), as: UTF8.self)
    }

    func resetInstallId() {
        lock.lock()
        defer { lock.unlock() }
        var store = loadLocked()
        store.installId = UUID().uuidString.lowercased()
        saveLocked(store)
    }

    /// 關掉開關時：清掉還沒送的資料（R40.4）
    func clearPending() {
        lock.lock()
        defer { lock.unlock() }
        var store = loadLocked()
        store.days = [:]
        saveLocked(store)
    }

    // MARK: 送出（§30.3）

    /// 啟動 60 秒後送一次，之後每小時檢查一次
    func scheduleAutomaticSends() {
        Task.detached(priority: .utility) { [self] in
            try? await Task.sleep(for: .seconds(60))
            while true {
                if !Self.isEnabled { clearPending() }
                touch()
                await sendPending()
                try? await Task.sleep(for: .seconds(3600))
            }
        }
    }

    /// 送今天以前、最近 3 天內、還沒送過的日子，一天一個請求
    func sendPending() async {
        guard let url = Self.endpoint, Self.isEnabled else { return }
        let start: (installId: String, dates: [String], days: [String: Counters])? = lock.withLock {
            if sending { return nil }
            sending = true
            let store = loadLocked()
            return (store.installId, pendingDates(in: store, today: Self.dateString(Date())), store.days)
        }
        guard let start else { return }
        defer { lock.withLock { sending = false } }

        for date in start.dates {
            let payload = Self.payload(installId: start.installId, date: date, counters: start.days[date] ?? Counters())
            var request = URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 20)
            request.httpMethod = "POST"
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.setValue("Shiyin/\(Self.appVersion)", forHTTPHeaderField: "User-Agent")
            request.httpBody = Self.encode(payload, pretty: false)
            let code: Int
            do {
                let (_, response) = try await URLSession.shared.data(for: request)
                code = (response as? HTTPURLResponse)?.statusCode ?? 0
            } catch {
                log.info("usage stats not sent: \(error.localizedDescription, privacy: .public)")
                break   // 網路有問題：剩下的下次再送
            }
            let delivered = (200..<300).contains(code)
            let rejected = (400..<500).contains(code) && code != 429
            lock.withLock {
                var store = loadLocked()
                if delivered || rejected { store.days[date] = nil }
                if delivered { store.sent.append(date) }
                saveLocked(store)
            }
            if rejected { log.error("usage stats rejected: HTTP \(code)") }   // 格式被拒，那一天不再送
            if !delivered && !rejected {
                log.info("usage stats not sent: HTTP \(code)")
                break   // 5xx、429：下次再送
            }
        }
    }

    /// 今天以前、最近 3 天內、還沒送過的日子（從舊到新）
    private func pendingDates(in store: Store, today: String) -> [String] {
        let oldest = Self.dateString(Date().addingTimeInterval(-3 * 86_400))
        return store.days.keys.filter { $0 < today && $0 >= oldest && !store.sent.contains($0) }.sorted()
    }

    // MARK: 格式（§30.4）

    struct Payload: Encodable {
        struct App: Encodable { var platform = "mac"; var version: String; var os: String; var chip: String; var uiLanguage: String }
        var schema = 1
        var installId: String
        var date: String
        var app: App
        var usage: Usage
        var errors: [String: Int]
        var errorDetail: ErrorDetail
    }

    /// 2026-10-25 加（R47、§34.5）：舊後台忽略這個鍵，schema 仍是 1
    struct ErrorDetail: Encodable {
        var summarize: [String: Int]
        var summarizeProvider: [String: Int]
        var summarizeDraft: Int
    }

    struct Usage: Encodable {
        var meetingsRecorded: Int, recordedSeconds: Int
        var meetingsImported: Int, importedSeconds: Int
        var transcribedSeconds: Int, transcribeWallSeconds: Int
        var engines: [String: Int]
        var meetingLanguages: [String: Int]
        var diarizationRuns: Int
        var summarizer: [String: Int]
        var questionsAsked: Int
        var review: [String: Int]
    }

    static func payload(installId: String, date: String, counters c: Counters) -> Payload {
        Payload(
            installId: installId, date: date,
            app: .init(version: appVersion, os: osVersion, chip: chip,
                       uiLanguage: Bundle.main.preferredLocalizations.first ?? "en"),
            usage: Usage(meetingsRecorded: c.meetingsRecorded, recordedSeconds: c.recordedSeconds,
                         meetingsImported: c.meetingsImported, importedSeconds: c.importedSeconds,
                         transcribedSeconds: c.transcribedSeconds, transcribeWallSeconds: c.transcribeWallSeconds,
                         engines: c.engines, meetingLanguages: c.meetingLanguages, diarizationRuns: c.diarizationRuns,
                         summarizer: c.summarizer, questionsAsked: c.questionsAsked, review: c.review),
            errors: c.errors,
            errorDetail: ErrorDetail(summarize: c.summarizeErrors, summarizeProvider: c.summarizeErrorProviders,
                                     summarizeDraft: c.summarizeDraftErrors))
    }

    static func encode(_ payload: Payload, pretty: Bool) -> Data {
        let encoder = JSONEncoder()
        encoder.outputFormatting = pretty ? [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes] : [.sortedKeys]
        return (try? encoder.encode(payload)) ?? Data()
    }

    /// Updater.currentVersion 是主執行緒的，這裡直接讀 Info.plist
    static var appVersion: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0"
    }

    static var osVersion: String {
        let v = ProcessInfo.processInfo.operatingSystemVersion
        return "macOS \(v.majorVersion).\(v.minorVersion)" + (v.patchVersion > 0 ? ".\(v.patchVersion)" : "")
    }

    static var chip: String {
        var size = 0
        sysctlbyname("machdep.cpu.brand_string", nil, &size, nil, 0)
        guard size > 0 else { return "" }
        var buffer = [CChar](repeating: 0, count: size)
        sysctlbyname("machdep.cpu.brand_string", &buffer, &size, nil, 0)
        return String(cString: buffer)
    }

    // MARK: 存檔（§30.2）

    static var fileURL: URL { Paths.home.appendingPathComponent("usage.json") }

    private func loadLocked() -> Store {
        if let cached { return cached }
        let store = (try? Data(contentsOf: Self.fileURL)).flatMap { try? JSONDecoder().decode(Store.self, from: $0) } ?? Store()
        cached = store
        return store
    }

    /// 只留最近 4 天的匯總、最近 7 天送過的日期
    private func saveLocked(_ store: Store) {
        var store = store
        let keepDays = Self.dateString(Date().addingTimeInterval(-4 * 86_400))
        let keepSent = Self.dateString(Date().addingTimeInterval(-7 * 86_400))
        store.days = store.days.filter { $0.key >= keepDays }
        store.sent = Array(Set(store.sent.filter { $0 >= keepSent })).sorted()
        cached = store
        do {
            try FileManager.default.createDirectory(at: Paths.home, withIntermediateDirectories: true)
            let encoder = JSONEncoder()
            encoder.outputFormatting = [.sortedKeys]
            try encoder.encode(store).write(to: Self.fileURL, options: .atomic)
        } catch {
            log.error("usage stats save failed: \(error.localizedDescription, privacy: .public)")
        }
    }

    private static let formatter: DateFormatter = {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.timeZone = .current
        f.dateFormat = "yyyy-MM-dd"
        return f
    }()

    static func dateString(_ date: Date) -> String { formatter.string(from: date) }

    private static func whole(_ seconds: Double) -> Int { seconds.isFinite ? max(0, Int(seconds.rounded())) : 0 }

    #if DEBUG
    /// shiyinctl usage：目前的檔案和下一份要送的內容
    func debugSnapshot() -> [String: Any] {
        lock.lock()
        let store = loadLocked()
        lock.unlock()
        let stored = (try? JSONSerialization.jsonObject(with: JSONEncoder().encode(store))) ?? [:]
        let preview = (try? JSONSerialization.jsonObject(with: Data(previewJSON().utf8))) ?? [:]
        return ["store": stored, "next": preview, "endpoint": Self.endpoint?.absoluteString ?? "", "enabled": Self.isEnabled]
    }

    /// shiyinctl usageage：把今天的匯總改成幾天前（模擬時間過去）
    func debugAge(days: Int) {
        lock.lock()
        defer { lock.unlock() }
        var store = loadLocked()
        let today = Self.dateString(Date())
        guard let counters = store.days.removeValue(forKey: today) else { return }
        store.days[Self.dateString(Date().addingTimeInterval(Double(-days) * 86_400))] = counters
        saveLocked(store)
    }
    #endif
}
