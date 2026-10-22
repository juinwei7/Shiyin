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
        var engines = ["breeze": 0, "macos": 0, "whisperJa": 0]
        var meetingLanguages = ["zh": 0, "en": 0, "ja": 0]
        var diarizationRuns = 0
        var summarizer = ["claudeCode": 0, "codex": 0, "api": 0, "local": 0]
        var questionsAsked = 0
        var errors = ["recording": 0, "transcribe": 0, "diarize": 0, "summarize": 0]
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

    /// 轉錄完成。engine 是 Meeting.transcribedWith（breeze、system、japanese）
    func transcribed(seconds: Double, wallSeconds: Double, engine: String, language: MeetingLanguage,
                     imported: Bool, diarized: Bool) {
        let audio = Self.whole(seconds)
        let key = ["system": "macos", "japanese": "whisperJa"][engine] ?? "breeze"
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
        let key: String
        switch provider {
        case .claude: key = "claudeCode"
        case .codex: key = "codex"
        case .openaiCompatible:
            let local = URL(string: OpenAICompatibleAPI.savedBaseURL).map(OpenAICompatibleAPI.isLocal) ?? false
            key = local ? "local" : "api"
        case .anthropic, .openai, .gemini: key = "api"
        case .none: return
        }
        add { $0.summarizer[key, default: 0] += 1 }
    }

    func asked() {
        add { $0.questionsAsked += 1 }
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

    // MARK: 設定頁

    /// 「查看會送出的內容」：下一份要送的；還沒有已過完的日子就顯示今天到目前為止的
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
        struct App: Encodable { var version: String; var os: String; var chip: String; var uiLanguage: String }
        var schema = 1
        var installId: String
        var date: String
        var app: App
        var usage: Usage
        var errors: [String: Int]
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
                         summarizer: c.summarizer, questionsAsked: c.questionsAsked),
            errors: c.errors)
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
