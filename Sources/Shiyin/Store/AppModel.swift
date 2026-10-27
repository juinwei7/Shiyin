import AppKit
import Foundation
import GRDB
import Observation

/// 列表與標題列顯示的狀態
enum MeetingStatus: Equatable {
    case recording
    case queued
    case transcribing(Double?, String)
    case summarizing
    case failed(JobKind, String)
    case cancelled
    case review(Int)
    case done

    var label: String {
        switch self {
        case .recording: return String(localized: "錄音中")
        case .queued: return String(localized: "排隊中")
        case .transcribing(_, let stage) where stage == String(localized: "等企業伺服器"):
            // 在等企業伺服器（R50.5）：不接「中」、不顯示百分比
            return stage
        case .transcribing(let p, let stage):
            let name = stage.isEmpty || stage == String(localized: "轉錄") ? String(localized: "轉錄中") : String(localized: "\(stage)中")
            return p.map { "\(name) \(Int($0 * 100))%" } ?? name
        case .summarizing: return String(localized: "整理中")
        case .failed(let kind, _): return kind == .transcribe ? String(localized: "轉錄失敗") : String(localized: "整理失敗")
        case .cancelled: return String(localized: "已取消")
        case .review(let n): return String(localized: "待確認 \(n) 題")
        case .done: return String(localized: "完成")
        }
    }

    var isActive: Bool {
        switch self {
        case .queued, .transcribing, .summarizing, .recording: return true
        default: return false
        }
    }
}

/// 會議頁的三個分頁（R44.1，design §33.1）：紀錄（摘要、決議、待辦、未決問題、議題、自訂段落，頂部一條待確認）、逐字稿（頂部可收合的我的筆記）、提問
enum DetailTab: String, CaseIterable, Identifiable {
    case summary, transcript, ask
    var id: String { rawValue }
    var label: String {
        switch self {
        case .summary: return String(localized: "紀錄")
        case .transcript: return String(localized: "逐字稿")
        case .ask: return String(localized: "提問")
        }
    }

    /// 4.0 以前的分頁名稱（topics、review、notes）當別名：DEBUG 的 tab 指令、舊腳本。回傳分頁，和要不要順便展開待確認、我的筆記
    static func resolve(_ name: String) -> (tab: DetailTab, expandReview: Bool, expandNotes: Bool)? {
        switch name {
        case "topics": return (.summary, false, false)
        case "review": return (.summary, true, false)
        case "notes": return (.transcript, false, true)
        default: return DetailTab(rawValue: name).map { ($0, false, false) }
        }
    }
}

/// 選取中的會議的完整資料
struct MeetingDetail: Equatable {
    var meeting: Meeting
    var segments: [Segment]
    var notes: MeetingNotes?
    var versionCount: Int
    var latestJob: Job?
    var speakers: [Int: String]
    var bookmarks: [Bookmark]
    /// 逐字稿校正建議（design §17.4）
    var corrections: [Correction] = []
    /// 逐字稿分頁用的索引，載入時算一次，不要每次畫面更新都重算（design §21.12）：
    /// 每段的校正（不含已忽略的）、依開始時間排好的段落
    var correctionsBySegment: [Int64: [Correction]] = [:]
    var timelineStarts: [Double] = []
    var timelineIds: [Int64] = []
    /// 段落 id → 在 segments 裡的位置（找連續的同一位說話者，design §24.3）
    var segmentIndex: [Int64: Int] = [:]
    /// 說話者名字的來源（default / manual / voiceprint / suggestion），聲紋帶入的顯示「自動」（design §21.4）
    var speakerSources: [Int: String] = [:]
    /// AI 猜的名字（design §21.5）、聲紋沒把握的（§21.4、R33）：還是預設名稱的說話者才有
    var speakerSuggestions: [Int: SpeakerNaming.Suggestion] = [:]
    /// 每位說話者最像的幾位認識的人（R33）：「是別人」的選單、待確認的「不是」
    var speakerCandidates: [Int: [String]] = [:]
    /// 待確認的題數：會議紀錄的待確認＋說話者的建議（R33.3）
    var reviewCount: Int { (notes?.reviewItems.count ?? 0) + speakerSuggestions.count }
    /// 可以復原的說話者調整（合併、改句子）的說明，沒有就是 nil；顯示在改名小視窗和逐字稿右鍵選單（R44.4）
    var speakerUndoLabel: String? = nil
    /// 有上一版會議紀錄可以復原（R44.4）：紀錄分頁的編輯列才顯示「復原上一版」
    var canUndoNotes: Bool { versionCount > 0 }
    /// 最後幾分鐘沒人說話、沒有轉錄（design §35.3、R48.3）：自動停止的錄音，錄音比轉錄的範圍長 1 分鐘以上才有
    var untranscribedMinutes: Int? = nil
    /// 待確認的題目對應到哪一條紀錄（R41.1，design §31.1）：載入時算一次，會議紀錄換了就重算（`linkReviewItems`）
    var reviewLinks: [NoteItemRef: [Int]] = [:]

    /// 這一條紀錄對到幾題待確認
    func reviewCount(_ ref: NoteItemRef) -> Int { reviewLinks[ref]?.count ?? 0 }

    mutating func linkReviewItems() {
        reviewLinks = notes?.reviewLinks() ?? [:]
    }

    mutating func loadSpeakers(_ rows: [SpeakerName]) {
        speakers = Dictionary(uniqueKeysWithValues: rows.map { ($0.idx, $0.name) })
        speakerSources = Dictionary(uniqueKeysWithValues: rows.map { ($0.idx, $0.nameSource) })
        speakerSuggestions = Dictionary(uniqueKeysWithValues: rows.compactMap { row in
            guard row.nameSource == "default", let name = row.suggestedName else { return nil }
            return (row.idx, SpeakerNaming.Suggestion(speaker: row.idx, name: name, reason: row.suggestionReason ?? "",
                                                      time: row.suggestionTime ?? ""))
        })
    }

    mutating func buildIndexes() {
        correctionsBySegment = Dictionary(grouping: corrections.filter { $0.status != "rejected" }, by: \.segmentId)
        let ordered = segments.compactMap { s in s.id.map { (start: s.start, id: $0) } }.sorted { $0.start < $1.start }
        timelineStarts = ordered.map(\.start)
        timelineIds = ordered.map(\.id)
        segmentIndex = Dictionary(segments.enumerated().compactMap { i, s in s.id.map { ($0, i) } }, uniquingKeysWith: { a, _ in a })
    }

    /// 跟這段同一位說話者、在逐字稿裡前後連在一起的段落（「這段連續的話是誰說的」，design §24.3）
    func speakerRun(around id: Int64) -> [Int64] {
        guard let i = segmentIndex[id], let speaker = segments[i].speaker else { return [id] }
        var low = i, high = i
        while low > 0, segments[low - 1].speaker == speaker { low -= 1 }
        while high + 1 < segments.count, segments[high + 1].speaker == speaker { high += 1 }
        return segments[low...high].compactMap(\.id)
    }

    /// 說話者可能分得不夠的提示（design §24.5）：自我檢查覺得可能把人併在一起、使用者沒選過人數、沒按過「不用了」
    var showsSpeakerHint: Bool {
        meeting.mayHaveMergedSpeakers && meeting.minSpeakers == nil && !meeting.speakerHintDismissed
            && segments.contains { $0.kind == "final" && $0.channel == nil }
    }

    /// 播放到 time 時的段落：開始時間不晚於 time + 0.05 的最後一段（二分搜尋）
    func segmentId(at time: Double) -> Int64? {
        var low = 0, high = timelineStarts.count
        while low < high {
            let mid = (low + high) / 2
            if timelineStarts[mid] <= time + 0.05 { low = mid + 1 } else { high = mid }
        }
        return low > 0 ? timelineIds[low - 1] : nil
    }

    /// 待確認的校正依「原文 → 建議」分組，處數多的在前；組內依逐字稿的順序（design §21.11、R20.2）
    var correctionGroups: [[Correction]] {
        let pending = corrections.filter { $0.status == "pending" }
        let order = Dictionary(segments.enumerated().compactMap { i, s in s.id.map { ($0, i) } }, uniquingKeysWith: { a, _ in a })
        return Dictionary(grouping: pending) { "\($0.original)\u{1F}\($0.suggestion)" }.values
            .map { $0.sorted { (order[$0.segmentId] ?? 0) < (order[$1.segmentId] ?? 0) } }
            .sorted { $0.count != $1.count ? $0.count > $1.count : (order[$0[0].segmentId] ?? 0) < (order[$1[0].segmentId] ?? 0) }
    }

    func speakerName(_ idx: Int?) -> String? {
        guard let idx else { return nil }
        return speakers[idx] ?? meeting.meetingLanguage.text(zh: "說話者 \(idx)", en: "Speaker \(idx)", ja: "話者 \(idx)")  // l10n-skip
    }
}

@MainActor
@Observable
final class AppModel {
    static let shared = AppModel()

    let db: AppDatabase
    let runner: JobRunner
    let models = ModelStore()
    let player = Player()
    let recorder = Recorder()
    let notifier = Notifier()
    let updater = Updater()

    var environment: EnvironmentStatus
    /// 整理工具準備好了沒（design §34.1）：會議頁的提示、失敗橫幅跟著變。只在值真的變了才設（重排整個視窗很貴，§21.12）
    private(set) var summarizerProblem: SummarizerProblem?
    @ObservationIgnored private var summarizerObservers: [NSObjectProtocol] = []
    var meetings: [MeetingRow] = []
    var trash: [MeetingRow] = []
    var showTrash = false
    var selection: Int64? {
        didSet {
            if oldValue != selection {
                reviewExpanded = false
                userNotesExpanded = false
                reloadDetail()
            }
        }
    }
    var detail: MeetingDetail?
    /// 待確認的回答（design §31.2）：待確認分頁和紀錄旁的就地回答共用；換會議就換一份（`syncReviewDraft`）
    var reviewDraft: ReviewDraft?
    var tab: DetailTab = .summary
    /// 紀錄頂部「還有 N 題待確認」、逐字稿頂部「我的筆記」展開了沒（design §33.1）：只記這次選取期間，換會議就收起
    var reviewExpanded = false
    var userNotesExpanded = false
    var filter = ""
    /// 側欄目前只看的資料夾；nil 是所有會議（R9.2）
    var folderFilter: String?
    /// 正在輸入名稱的資料夾動作：新增（要移入的會議）或改名（舊名稱）
    var folderPrompt: FolderPrompt?
    enum FolderPrompt: Equatable { case create(Int64), rename(String) }
    var glossary: [GlossaryTerm] = []
    var glossarySuggestions: [GlossarySuggestion] = []
    var templates: [TemplateRecord] = []

    var searchQuery = "" { didSet { runSearch() } }
    var searchResults: [SearchHit] = []
    /// 搜尋結果點進來時要高亮的逐字稿段落
    var highlightSegment: Int64?

    var pendingImport: [URL] = []
    var showImportSheet = false
    var showFileImporter = false
    var showExportSheet = false
    /// 重新辨識說話者的小視窗（design §23.4）和它要處理的會議
    var showRediarizeSheet = false
    var rediarizeMeetingId: Int64?
    /// 匯出對話框要匯出的會議：打開時記下，對話框開著時選取的會議換了也不變（design §20.9、R18.9）
    var exportMeetingId: Int64?
    var confirmDelete = false
    /// 垃圾桶裡「永久刪除」要確認的會議
    var confirmPurge: Int64?
    /// 確認對話框要刪的會議：按下 ⌘⌫ 時就決定，不然確認前選取的會議變了（例如錄音停止後跳到那場）會刪到別場
    var deleteTarget: Int64?
    var queuedCount = 0
    var errorMessage: String?
    var noticeMessage: String?
    /// 第二版留下的 Python 環境、mlx 模型（R10.5）
    var legacyBytes: Int64 = 0

    @ObservationIgnored var openMainWindow: (() -> Void)?
    @ObservationIgnored var openWindowById: ((String) -> Void)?

    private init() {
        do {
            try Paths.ensureDirectories()
            db = try AppDatabase(path: Paths.database.path)
        } catch {
            // 遷移在交易裡進行，失敗時原本的資料不會被改到（N3）；說明清楚後結束，不要直接閃退
            let alert = NSAlert()
            alert.alertStyle = .critical
            alert.messageText = String(localized: "拾音沒辦法開啟資料庫")
            let reason = (error as? DatabaseError)?.message ?? error.localizedDescription
            log.error("open database failed: \(reason, privacy: .public)")
            alert.informativeText = String(localized: "原因：\(reason)\n\n你的資料沒有被改動。升級前的備份在：\(Paths.backupsDir.path)\n請把這個訊息截圖給維護拾音的人。")
            alert.addButton(withTitle: String(localized: "結束拾音"))
            alert.runModal()
            exit(1)
        }
        runner = JobRunner(db: db)
        environment = EnvironmentStatus(items: [])
        runner.onChange = { [weak self] in self?.reload() }
        runner.onFinished = { [weak self] job, error in self?.jobFinished(job, error: error) }
        recorder.onLiveFinished = { [weak self] id, duration in self?.enqueueDraftNotes(meetingId: id, duration: duration) }
        runner.onRediarized = { [weak self] id, result in
            guard let self, self.selection == id else { return }
            switch result {
            case .success(let count):
                self.noticeMessage = String(localized: "已重新辨識說話者：分出 \(count) 位。名字請再確認一下；會議紀錄要更新的話，請重新整理會議紀錄。")
            case .failure(let error):
                if !(error is CancellationError) { self.errorMessage = String(localized: "重新辨識說話者失敗：\(error.localizedDescription)") }
            }
        }
        runner.onEditedDraftReplaced = { [weak self] id in
            guard let self, self.selection == id else { return }
            self.noticeMessage = String(localized: "正式版的會議紀錄好了；你改過的草稿留在版本紀錄裡，按紀錄分頁「編輯」旁的「復原上一版」可以找回")
        }
        runner.onAudioReplaced = { [weak self] id in
            guard let self, self.selection == id else { return }
            // 錄音壓縮成 m4a 後換檔案：接著原本的位置，正在播的繼續播
            let (time, playing) = (self.player.currentTime, self.player.isPlaying)
            self.player.unload()
            self.reloadDetail(forceAudio: true)
            if time > 0 || playing { self.player.seek(to: time, play: playing) }
        }
        recorder.onFinished = { [weak self] result in self?.recordingFinished(result) }
        recorder.onLiveFinal = { [weak self] id, start, end, text in
            try? self?.db.appendLiveSegment(id, start: start, end: end, text: text)
        }
        recorder.onLanguageChange = { [weak self] id, language in
            guard let self else { return }
            self.perform { try self.db.setLanguage(id, language) }
        }
        notifier.onOpen = { [weak self] id in self?.reveal(id) }
    }

    private var started = false

    func start() {
        guard !started else { return }
        started = true
        notifier.activate()
        // 偵測線上會議、提醒錄音（design §21.7、R19.4）
        meetingDetector.isRecording = { [weak self] in self?.recorder.isRecording ?? false }
        meetingDetector.onPrompt = { [weak self] in self?.showMeetingPrompt($0) }
        notifier.onMeetingAction = { [weak self] action, bundle in self?.handleMeetingAction(action, bundle: bundle) }
        notifier.onIdleAction = { [weak self] action in
            guard let self else { return }
            if action == "stop" { self.recorder.stop() } else if action == "continue" { self.recorder.continueAfterIdle() }
        }
        meetingDetector.start()
        BuiltinTemplates.seed(db)
        // claude、codex 的登入狀態（design §34.1）：沒登入時不自動整理
        Task { await CLILogin.refreshIfStale() }
        // 企業伺服器：公司在後台加了模型、改了設定，不用重新登入（R51.5）
        Task {
            await EnterpriseServer.shared.refreshInfo()
            refreshSummarizerProblem()
        }
        refreshSummarizerProblem()
        // 設定（整理工具、模型、網址）、key、登入狀態、暫停變了就重算
        for name in [UserDefaults.didChangeNotification, .summarizerStateChanged] {
            summarizerObservers.append(NotificationCenter.default.addObserver(forName: name, object: nil, queue: .main) { _ in
                MainActor.assumeIsolated { AppModel.shared.refreshSummarizerProblem() }
            })
        }
        cleanUpPreviousRun()
        purgeExpiredTrash()
        recoverRecordings()
        runner.start()
        // 語言偵測模型很小（60 MB），開著自動偵測就先下載好
        if AppSettings.autoDetectLanguage { models.autoStart(.languageDetection) }
        GlobalHotKey.shared.onPress = { [weak self] in self?.toggleRecording() }
        GlobalHotKey.shared.onBookmark = { [weak self] in self?.bookmarkFromHotKey() }
        recorder.onStarted = { GlobalHotKey.shared.setBookmarkEnabled(AppSettings.bookmarkHotKey) }
        recorder.onLevelHint = { [weak self] hint in self?.levelHintAppeared(hint) }
        // 忘了停止錄音（design §35、R48）：提醒一出現就送通知（人多半不在電腦前）
        recorder.onIdlePrompt = { [weak self] in
            guard let self, let id = self.recorder.meetingId else { return }
            self.notifier.idlePrompt(meetingId: id)
        }
        GlobalHotKey.shared.setEnabled(AppSettings.globalHotKey)
        updater.scheduleAutomaticChecks()
        UsageStats.shared.touch()
        UsageStats.shared.scheduleAutomaticSends()
        refreshEnvironment()
        legacyBytes = Paths.legacyItems.reduce(0) { $0 + Self.directorySize($1) }
        reload()
    }

    // MARK: 讀取

    func reload() {
        #if DEBUG
        DebugMetrics.bump("reloadList")
        #endif
        do {
            meetings = try db.meetingRows()
            trash = try db.meetingRows(trash: true)
            glossary = try db.glossary()
            glossarySuggestions = try db.glossarySuggestions()
            templates = try db.templates()
            queuedCount = try db.activeJobs().filter { $0.status == .queued }.count
        } catch {
            errorMessage = error.localizedDescription
        }
        if let selection, !meetings.contains(where: { $0.id == selection }), !trash.contains(where: { $0.id == selection }) {
            self.selection = nil
        }
        // 資料夾裡最後一場會議移走後，資料夾就不存在了，回到所有會議
        if let folderFilter, !meetings.contains(where: { $0.meeting.folder == folderFilter }) { self.folderFilter = nil }
        reloadDetail()
    }

    /// 這場會議是用 macOS 內建辨識（模型下載好之前）或 Breeze 轉的，現在目前引擎的模型下載好了，可以重新轉錄（design §21.17、§32.6）
    func canUpgradeTranscript(_ meeting: Meeting) -> Bool {
        guard !AppSettings.useSystemTranscriber, !AppSettings.usesEnterpriseTranscription, meeting.hasAudio, meeting.deletedAt == nil,
              models.isInstalled(transcriptionFor: meeting.meetingLanguage) else { return false }
        let older = ModelStore.transcription(for: meeting.meetingLanguage) == .qwen ? ["system", "breeze"] : ["system"]
        return older.contains(meeting.transcribedWith)
    }

    // MARK: 更新（R10.6）

    /// 從選單檢查：結果一定要讓使用者看到
    func checkForUpdatesFromMenu() async {
        await updater.check(userInitiated: true)
        switch updater.state {
        case .upToDate: noticeMessage = String(localized: "已是最新版本（\(Updater.currentVersion)）")
        case .failed(let reason): errorMessage = String(localized: "檢查更新失敗：\(reason)")
        case .available: openMainWindow?()
        default: break
        }
    }

    // MARK: 資料夾（R9.2）

    /// 現有的資料夾和各自的場數（不含垃圾桶），依名稱排序
    var folders: [(name: String, count: Int)] {
        let counts = Dictionary(grouping: meetings.filter { !$0.meeting.folder.isEmpty }, by: \.meeting.folder).mapValues(\.count)
        return counts.map { (name: $0.key, count: $0.value) }.sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
    }

    /// 新會議要放進的資料夾：正在看某個資料夾時就放那裡
    var currentFolder: String { showTrash ? "" : folderFilter ?? "" }

    func moveToFolder(_ id: Int64, folder: String) {
        let name = folder.trimmingCharacters(in: .whitespacesAndNewlines)
        if let current = try? db.meeting(id)?.folder, current == name {
            noticeMessage = name.isEmpty ? String(localized: "這場會議沒有在任何資料夾裡") : String(localized: "這場會議已經在「\(name)」")
            return
        }
        perform { try db.setFolder([id], folder: name) }
        noticeMessage = name.isEmpty ? String(localized: "已移出資料夾") : String(localized: "已移到「\(name)」")
    }

    func renameFolder(_ old: String, to newName: String) {
        let name = newName.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !name.isEmpty, name != old else { return }
        // 先切換篩選，reload 時才不會因為舊名稱不存在而跳回所有會議
        if folderFilter == old { folderFilter = name }
        perform { try db.renameFolder(old, to: name) }
    }

    /// 刪除資料夾：會議留著，移回「不在資料夾」
    func deleteFolder(_ name: String) {
        perform { try db.renameFolder(name, to: "") }
        if folderFilter == name { folderFilter = nil }
    }

    func reloadDetail(forceAudio: Bool = false) {
        #if DEBUG
        DebugMetrics.bump("reloadDetail")
        let started = Date()
        defer { DebugMetrics.time("reloadDetail", since: started) }
        #endif
        guard let id = selection, let meeting = try? db.meeting(id) else {
            detail = nil
            syncReviewDraft()
            player.unload()
            return
        }
        let previous = detail?.meeting
        let speakerRows = (try? db.speakers(id)) ?? []
        var loaded = MeetingDetail(
            meeting: meeting, segments: (try? db.segments(id)) ?? [], notes: meeting.decodedNotes,
            versionCount: (try? db.versionCount(id)) ?? 0, latestJob: try? db.latestJob(id),
            speakers: Dictionary(uniqueKeysWithValues: speakerRows.map { ($0.idx, $0.name) }),
            bookmarks: (try? db.bookmarks(id)) ?? [], corrections: (try? db.corrections(id)) ?? [])
        loaded.loadSpeakers(speakerRows)
        loaded.speakerCandidates = (try? db.voiceprintCandidates(id)) ?? [:]
        loaded.speakerUndoLabel = try? db.speakerUndoLabel(id)
        if let until = try? db.transcribedUntil(id), let duration = meeting.durationSeconds, Double(duration) - until >= 60 {
            loaded.untranscribedMinutes = Int(((Double(duration) - until) / 60).rounded())
        }
        loaded.buildIndexes()
        loaded.linkReviewItems()
        detail = loaded
        syncReviewDraft()
        if forceAudio || previous?.id != meeting.id || previous?.audioFile != meeting.audioFile {
            if meeting.hasAudio { player.load(meeting.audioURL) } else { player.unload() }
        }
    }

    var filteredMeetings: [MeetingRow] {
        var source = showTrash ? trash : meetings
        if !showTrash, let folderFilter { source = source.filter { $0.meeting.folder == folderFilter } }
        let keyword = filter.trimmingCharacters(in: .whitespaces)
        guard !keyword.isEmpty else { return source }
        return source.filter { $0.meeting.displayTitle.localizedCaseInsensitiveContains(keyword) }
    }

    func status(_ row: MeetingRow) -> MeetingStatus {
        status(meetingId: row.id, latestJob: row.latestJob, reviewCount: row.reviewCount)
    }

    func status(meetingId: Int64, latestJob: Job?, reviewCount: Int) -> MeetingStatus {
        if recorder.meetingId == meetingId { return .recording }
        if let running = runner.runningJob(for: meetingId) {
            return running.job.kind == .transcribe ? .transcribing(running.progress, running.stage) : .summarizing
        }
        if let job = latestJob {
            switch job.status {
            case .queued, .running: return .queued
            case .failed: return .failed(job.kind, job.error ?? "")
            case .cancelled where job.kind == .transcribe: return .cancelled
            default: break
            }
        }
        return reviewCount > 0 ? .review(reviewCount) : .done
    }

    var detailStatus: MeetingStatus? {
        guard let detail else { return nil }
        return status(meetingId: detail.meeting.id!, latestJob: detail.latestJob, reviewCount: detail.reviewCount)
    }

    /// 用全域快捷鍵標記重點後，選單列圖示換成星號 1 秒（design §21.10）
    var bookmarkFlash = false

    /// ⌃⌥⌘B：在其他 app 裡標記重點，有音效和選單列圖示的回饋
    func bookmarkFromHotKey() {
        guard recorder.isRecording, let id = recorder.meetingId else { return }
        addBookmark(at: recorder.elapsed, meetingId: id)
        NSSound(named: "Tink")?.play()
        bookmarkFlash = true
        Task { @MainActor in
            try? await Task.sleep(for: .seconds(1))
            bookmarkFlash = false
        }
    }

    var menuBarSymbol: String {
        if bookmarkFlash { return "star.circle.fill" }
        // 收音提醒還在時換成警示的樣子（R42.2），按「知道了」或提示消失就換回來
        if recorder.isRecording { return recorder.levelHint != nil || recorder.idlePrompt ? "mic.badge.exclamationmark" : "record.circle.fill" }
        if runner.isBusy || queuedCount > 0 { return "waveform.circle.fill" }
        return "waveform"
    }

    /// 收音提醒第一次出現（R42.1、design §31.5）：拾音不是最前面的 app、或主視窗沒開時送通知，點了打開錄音中的會議。
    /// 每場每種最多一次由 Recorder 管；拾音在前面時只有錄音畫面和選單列的提示
    private func levelHintAppeared(_ hint: Recorder.LevelHint) {
        guard let id = recorder.meetingId else { return }
        let mainVisible = NSApp.windows.contains { $0.isVisible && $0.identifier?.rawValue.hasPrefix("main") == true }
        guard !(NSApp.isActive && mainVisible) else { return }
        notifier.levelHint(hint, meetingId: id)
    }

    func refreshEnvironment() {
        models.refresh()
        environment = EnvironmentStatus.check(models: models)
    }

    // MARK: 匯入（R3）

    func requestImport(_ urls: [URL]) {
        let packages = urls.filter { $0.pathExtension.lowercased() == "shiyin" }
        let transcripts = urls.filter { ["srt", "vtt", "txt"].contains($0.pathExtension.lowercased()) }
        let markdown = urls.filter { ["md", "markdown"].contains($0.pathExtension.lowercased()) }
        packages.forEach(importPackage)
        transcripts.forEach { importTranscript($0) }
        if !markdown.isEmpty { importMarkdownNotes(markdown) }
        let media = urls.filter(Self.isMedia)
        guard !media.isEmpty else {
            if packages.isEmpty && transcripts.isEmpty && markdown.isEmpty { errorMessage = String(localized: "只能匯入錄音、影片、逐字稿（srt、vtt、txt）、Markdown 或 .shiyin 檔") }
            return
        }
        pendingImport = media
        showImportSheet = true
    }

    func confirmImport(_ options: ImportOptions) {
        let urls = pendingImport
        pendingImport = []
        showImportSheet = false
        importFiles(urls, options: urls.count == 1 ? options : ImportOptions(participants: options.participants, templateId: options.templateId,
                                                                              language: options.language))
    }

    /// 從 Dock 或 Finder 匯入：不跳對話框，直接用預設值
    func importDirectly(_ urls: [URL]) {
        let other = urls.filter { !Self.isMedia($0) }
        if !other.isEmpty { requestImport(other) }
        importFiles(urls.filter(Self.isMedia), options: ImportOptions())
        openMainWindow?()
    }

    /// 正在匯入的檔案數，側邊欄下方顯示（design §21.13、R20.4）
    var importingCount = 0

    /// 複製檔案在背景做，大檔案（或從外接硬碟複製）時不卡住畫面（design §21.13、R20.4）
    private func importFiles(_ urls: [URL], options: ImportOptions) {
        guard !urls.isEmpty else { return }
        var options = options
        if options.folder.isEmpty { options.folder = currentFolder }
        let db = db
        importingCount += urls.count
        Task {
            var last: Meeting?
            var failures: [String] = []
            for url in urls {
                do {
                    last = try await Task.detached(priority: .userInitiated) { [options] in
                        try Importer.importFile(url, options: options, db: db)
                    }.value
                    runner.wake()
                    reload()
                } catch { failures.append(error.localizedDescription) }
                importingCount -= 1
            }
            if !failures.isEmpty { errorMessage = failures.joined(separator: "\n") }
            if let last { showTrash = false; selection = last.id }
        }
    }

    static func isMedia(_ url: URL) -> Bool {
        guard let type = (try? url.resourceValues(forKeys: [.contentTypeKey]))?.contentType else {
            return ["m4a", "mp3", "wav", "aac", "flac", "mp4", "mov", "caf", "aiff", "aif", "m4v"].contains(url.pathExtension.lowercased())
        }
        return Importer.contentTypes.contains { type.conforms(to: $0) }
    }

    // MARK: 錄音（R1、R2）

    func toggleRecording() {
        if recorder.isRecording { recorder.stop() } else { startRecording() }
    }

    func startRecording() {
        guard !recorder.isRecording else { return }
        MeetingPromptPanel.shared.close()
        #if DEBUG
        // 啟動參數 -debugRecordFile <音檔>：開始錄音改用模擬錄音，驗證時不要真的用使用者的麥克風錄（design §21.7）
        if let file = UserDefaults.standard.string(forKey: "debugRecordFile") {
            startSimulatedRecording(file: URL(fileURLWithPath: file), speed: 1, both: false)
            return
        }
        #endif
        let formatter = DateFormatter()
        formatter.dateFormat = "yyyy-MM-dd HH:mm"
        let now = Date()
        do {
            let meeting = try db.insertMeeting(Meeting(
                meetingDate: DateText.string(now), sourceFilename: String(localized: "錄音 \(formatter.string(from: now))"),
                templateId: AppSettings.defaultTemplate, source: "recording", channels: AppSettings.recordSource,
                folder: currentFolder, language: AppSettings.defaultMeetingLanguage.rawValue),
                audioExtension: nil)
            recorder.start(meetingId: meeting.id!, language: meeting.meetingLanguage)
            reload()
            showTrash = false
            selection = meeting.id
            tab = .transcript
        } catch {
            errorMessage = String(localized: "沒辦法開始錄音：\(error.localizedDescription)")
        }
    }

    #if DEBUG
    /// 只在 DEBUG：用音訊檔模擬錄音（shiyinctl record-sim）
    func startSimulatedRecording(file: URL, speed: Double, both: Bool) {
        guard !recorder.isRecording, let meeting = try? db.insertMeeting(Meeting(
            meetingDate: DateText.string(Date()), sourceFilename: String(localized: "模擬錄音"), source: "recording",
            channels: both ? "both" : "mic", folder: currentFolder, language: AppSettings.defaultMeetingLanguage.rawValue),
            audioExtension: nil) else { return }
        recorder.startSimulated(meetingId: meeting.id!, file: file, speed: speed, both: both, language: meeting.meetingLanguage)
        reload()
        selection = meeting.id
    }
    #endif

    /// 錄音結束：把錄音檔接到會議上並排轉錄（R2.5）
    private func recordingFinished(_ result: Recorder.Result) {
        MeetingPromptPanel.shared.close()   // 還開著的「要停止錄音嗎？」不用了
        GlobalHotKey.shared.setBookmarkEnabled(false)   // 不錄音時不佔用 ⌃⌥⌘B
        do {
            if let error = result.error {
                errorMessage = error
                UsageStats.shared.failed(.recording)
            }
            guard var meeting = try db.meeting(result.meetingId) else { return }
            // 只有檔案本身確實幾乎是空的（不到 0.5 秒的音訊）才當作沒錄到東西；
            // 不能只看 duration，萬一計算出錯會把整場錄音刪掉
            let bytes = (result.file.flatMap { try? FileManager.default.attributesOfItem(atPath: $0.path)[.size] as? Int }) ?? 0
            guard let file = result.file, bytes > 44 + Int(CaptureFormat.sampleRate) else {
                if let file = result.file { try? FileManager.default.removeItem(at: file) }
                try db.deleteMeeting(result.meetingId)
                reload()
                return
            }
            let destination = Paths.audioDir.appendingPathComponent("\(result.meetingId).wav")
            try? FileManager.default.removeItem(at: destination)
            try FileManager.default.moveItem(at: file, to: destination)
            let original = meeting
            do {
                meeting.audioFile = destination.lastPathComponent
                if result.duration > 0 { meeting.durationSeconds = Int(result.duration.rounded()) }
                try db.updateMeeting(meeting)
                // 沒人說話太久而自動停止：只轉錄到最後說話後 15 秒（design §35.3、R48.3）
                let until = result.idleSince.map { $0 + 15 }.flatMap { $0 < result.duration ? $0 : nil }
                let payload = until.flatMap { try? JSONEncoder().encode(TranscribePayload(until: $0)) }.map { String(decoding: $0, as: UTF8.self) }
                try db.enqueue(result.meetingId, kind: .transcribe, payload: payload)
                if result.idleSince != nil { notifier.idleStopped(meetingId: result.meetingId, title: meeting.displayTitle) }
                UsageStats.shared.recorded(seconds: result.duration)
            } catch {
                // 資料庫寫不進去（磁碟滿時常一起發生）：搬回 recording/，下次開啟由復原流程接回（design §20.3）。
                // 會議已經改到一半的話改回去，不要指向搬走的檔案
                try? FileManager.default.moveItem(at: destination, to: file)
                try? db.updateMeeting(original)
                throw error
            }
            runner.wake()
            reload()
            selection = result.meetingId
        } catch {
            errorMessage = String(localized: "錄音存檔失敗：\(error.localizedDescription)（檔案還在 \(result.file?.path ?? "")）")
        }
    }

    /// 草稿紀錄（design §21.3、R19.1）：即時辨識收尾後，錄音夠長、即時逐字稿夠多、會自動整理時，
    /// 轉錄的同時先用即時逐字稿整理一份草稿
    private func enqueueDraftNotes(meetingId: Int64, duration: Double) {
        guard duration >= Self.draftMinimumSeconds, AppSettings.autoSummarize, AppSettings.summarizerReady,
              let meeting = try? db.meeting(meetingId), meeting.hasAudio, meeting.notes == nil,
              let live = try? db.segments(meetingId, kind: "live"),
              live.reduce(0, { $0 + $1.text.count }) >= 100 else { return }
        let payload = String(decoding: (try? JSONEncoder().encode(SummarizePayload(draft: true))) ?? Data(), as: UTF8.self)
        do {
            try db.enqueue(meetingId, kind: .summarize, payload: payload)
            runner.wake()
        } catch { log.error("enqueue draft notes failed: \(error.localizedDescription)") }
    }

    /// 錄音太短就不整理草稿：轉錄很快，等正式版就好
    static var draftMinimumSeconds: Double {
        #if DEBUG
        // 啟動參數 -debugDraftMinimumSeconds 10：驗證時不用真的錄 3 分鐘
        if UserDefaults.standard.object(forKey: "debugDraftMinimumSeconds") != nil {
            return UserDefaults.standard.double(forKey: "debugDraftMinimumSeconds")
        }
        #endif
        return 180
    }

    /// 啟動時處理上次沒正常結束的錄音（R1.5）
    private func recoverRecordings() {
        let files = (try? FileManager.default.contentsOfDirectory(at: Paths.recordingDir, includingPropertiesForKeys: nil)) ?? []
        for file in files where file.pathExtension.lowercased() == "wav" {
            do {
                try WAVRepair.repair(file)
                // 檔名是 <會議id>-<uuid>.wav；找不到會議就建一場新的
                let id = Int64(file.deletingPathExtension().lastPathComponent.split(separator: "-").first ?? "")
                if let id, var meeting = try db.meeting(id) {
                    let destination = Paths.audioDir.appendingPathComponent("\(id).wav")
                    try? FileManager.default.removeItem(at: destination)
                    try FileManager.default.moveItem(at: file, to: destination)
                    let original = meeting
                    do {
                        meeting.audioFile = destination.lastPathComponent
                        if meeting.title.isEmpty { meeting.title = String(localized: "復原的錄音") }
                        try db.updateMeeting(meeting)
                        try db.enqueue(id, kind: .transcribe)
                    } catch {
                        // 搬回 recording/、會議改回去，下次開啟再試（design §20.6）
                        try? FileManager.default.moveItem(at: destination, to: file)
                        try? db.updateMeeting(original)
                        throw error
                    }
                } else {
                    try Importer.importFile(file, options: ImportOptions(title: String(localized: "復原的錄音"), sourceName: String(localized: "復原的錄音.wav")),
                                            db: db, move: true)
                }
                noticeMessage = String(localized: "找到上次沒有正常結束的錄音，已經復原並開始轉錄。")
            } catch {
                log.error("recover recording failed: \(error.localizedDescription)")
            }
        }
        // 錄音中途當掉、連錄音檔都沒有的空會議（design §20.6）。直接查資料庫（這時 meetings 還沒載入）；
        // recording/ 裡還有錄音檔的會議是這次沒復原成功的，留著下次再試
        let remaining = Set(((try? FileManager.default.contentsOfDirectory(at: Paths.recordingDir, includingPropertiesForKeys: nil)) ?? [])
            .compactMap { Int64($0.deletingPathExtension().lastPathComponent.split(separator: "-").first ?? "") })
        for id in (try? db.emptyRecordingMeetings()) ?? [] where !remaining.contains(id) && recorder.meetingId != id {
            try? db.deleteMeeting(id)
        }
    }

    private func cleanUpPreviousRun() {
        if let leftovers = try? FileManager.default.contentsOfDirectory(at: Paths.tmpDir, includingPropertiesForKeys: nil) {
            leftovers.forEach { try? FileManager.default.removeItem(at: $0) }
        }
    }

    // MARK: 會議操作

    func reveal(_ id: Int64) {
        reload()
        showTrash = trash.contains { $0.id == id }
        selection = id
        openMainWindow?()
    }

    func updateInfo(title: String, date: String, participants: String) {
        guard let id = selection else { return }
        perform { try db.updateInfo(id, title: title, date: date, participants: participants) }
    }

    /// 改會議語言（design §16.3）：之後的轉錄、整理、匯出都用新的語言，已經有的逐字稿和紀錄不會自動重做
    func setMeetingLanguage(_ language: MeetingLanguage) {
        guard let meeting = detail?.meeting, let id = meeting.id, meeting.meetingLanguage != language else { return }
        if recorder.isRecording && recorder.meetingId == id {
            // 錄音中：即時逐字稿也要換語言（跟自動偵測換語言同一條路，會一起更新資料庫）
            recorder.switchLanguage(to: language)
            noticeMessage = String(localized: "會議語言改成 \(language.name)，即時逐字稿也跟著換了")
            return
        }
        perform { try db.setLanguage(id, language) }
        noticeMessage = String(localized: "會議語言改成 \(language.name)，要重新轉錄或整理的話，從「更多 → 重新處理」選")
    }

    /// 錄音中選「大約幾個人」（design §26.2）：錄完轉錄時用
    func setMinSpeakers(_ count: Int?) {
        guard let id = selection else { return }
        perform(.detail([.meeting])) { try db.setMinSpeakers(id, count) }
    }

    func setTemplate(_ templateId: String) {
        guard let id = detail?.meeting.id else { return }
        perform { try db.setTemplate(id, templateId: templateId) }
    }

    /// 「會議 → 移到垃圾桶…」：先記住是哪一場，再問要不要刪
    func requestDelete() {
        guard let id = selection else { return }
        deleteTarget = id
        confirmDelete = true
    }

    /// 移到垃圾桶（R9.3）
    func moveToTrash(_ id: Int64) {
        if recorder.isRecording && recorder.meetingId == id {
            errorMessage = String(localized: "這場會議還在錄音，請先停止錄音再移到垃圾桶")
            return
        }
        // 已經在垃圾桶裡就不再處理，不然 30 天的期限會重新計算
        if (try? db.meeting(id))?.deletedAt != nil { return }
        runner.cancel(meetingId: id)
        perform { try db.setTrashed(id, true) }
        noticeMessage = String(localized: "已移到垃圾桶，30 天內都可以還原")
    }

    func restore(_ id: Int64) {
        perform { try db.setTrashed(id, false) }
        showTrash = false
        selection = id
    }

    /// 永久刪除（垃圾桶裡的「立即刪除」）
    func deletePermanently(_ id: Int64) {
        guard let meeting = try? db.meeting(id), !(recorder.isRecording && recorder.meetingId == id) else { return }
        runner.cancel(meetingId: id)
        if selection == id { player.unload() }
        perform {
            try db.deleteMeeting(id)
            if meeting.hasAudio { try? FileManager.default.removeItem(at: meeting.audioURL) }
        }
    }

    private func purgeExpiredTrash() {
        for meeting in (try? db.expiredTrash(olderThan: 30)) ?? [] {
            try? db.deleteMeeting(meeting.id!)
            if meeting.hasAudio { try? FileManager.default.removeItem(at: meeting.audioURL) }
        }
    }

    func cancelJobs() {
        guard let id = selection else { return }
        runner.cancel(meetingId: id)
    }

    func retry() {
        guard let id = selection, let job = detail?.latestJob, job.status == .failed || job.status == .cancelled else { return }
        // 整理工具還沒準備好時重試也一樣會失敗（R46.2）：說原因
        if job.kind.usesSummarizer, let problem = AppSettings.summarizerProblem { errorMessage = problem.message; return }
        perform { try db.enqueue(id, kind: job.kind, payload: job.payload) }
        runner.wake()
    }

    /// 重新轉錄、重新整理、檢查聽錯的字、復原上一版之前再擋一次：工具列和選單的停用狀態可能沒有更新，
    /// 不擋的話會在處理中重複排工作（多花整理工具的額度），或替垃圾桶裡的會議跑
    private func readyForAction(_ id: Int64) -> Bool {
        if detail?.meeting.deletedAt != nil {
            noticeMessage = String(localized: "垃圾桶裡的會議要先還原才能操作")
            return false
        }
        if recorder.isRecording && recorder.meetingId == id {
            noticeMessage = String(localized: "這場會議還在錄音")
            return false
        }
        if detailStatus?.isActive == true {
            noticeMessage = String(localized: "這場會議還在處理中，完成後再試一次")
            return false
        }
        return true
    }

    func retranscribe() {
        guard let id = selection, readyForAction(id) else { return }
        guard detail?.meeting.hasAudio == true else {
            noticeMessage = String(localized: "這場會議沒有錄音，沒辦法重新轉錄")
            return
        }
        perform { try db.enqueue(id, kind: .transcribe) }
        runner.wake()
    }

    /// 「更多 → 重新辨識說話者…」（design §23.4）：按下時檢查能不能做（R22.4），可以才打開選人數的視窗
    func requestRediarize() {
        guard let id = selection, readyForAction(id) else { return }
        if let problem = rediarizeProblem(id) {
            noticeMessage = problem
            return
        }
        rediarizeMeetingId = id
        showRediarizeSheet = true
    }

    private func rediarizeProblem(_ id: Int64) -> String? {
        guard let detail, detail.meeting.id == id else { return nil }
        if !detail.meeting.hasAudio { return String(localized: "這場會議沒有錄音，沒辦法重新辨識說話者") }
        if !detail.segments.contains(where: { $0.kind == "final" }) { return String(localized: "還沒有正式逐字稿，轉錄完成後再試一次") }
        if detail.segments.contains(where: { $0.channel != nil }) { return String(localized: "這場會議是分聲道轉錄的，請用「重新轉錄」") }
        if (try? db.hasActiveJob(id, kind: .rediarize)) == true { return String(localized: "已經在重新辨識說話者了") }
        return nil
    }

    /// 選人數的視窗按「開始」（DEBUG 的 rediarize 也用這個）：記住人數（R22.3），排重新辨識的工作
    func rediarize(meetingId: Int64, minSpeakers: Int?) {
        perform { try db.setMinSpeakers(meetingId, minSpeakers); try db.enqueue(meetingId, kind: .rediarize) }
        runner.wake()
    }

    // MARK: 說話者的修正（design §24、R23）

    /// 轉錄或重新辨識說話者時不能改：完成時會整份寫回段落的說話者
    private func speakerEditProblem(_ id: Int64) -> String? {
        if detail?.meeting.deletedAt != nil { return String(localized: "垃圾桶裡的會議要先還原才能操作") }
        if detailStatus?.isActive == true || (try? db.hasActiveJob(id, kind: .rediarize)) == true {
            return String(localized: "正在處理這場會議，完成後才能改說話者")
        }
        return nil
    }

    /// 「合併到…」（R23.1）：from 說的話全部算 into 的；會議紀錄裡的名字跟著換
    func mergeSpeaker(_ from: Int, into: Int) {
        guard let id = selection else { return }
        if let problem = speakerEditProblem(id) {
            noticeMessage = problem
            return
        }
        guard let fromName = detail?.speakerName(from), let intoName = detail?.speakerName(into) else { return }
        let source = detail?.speakerSources[into]
        perform(.detail([.speakers, .segments, .meeting])) {
            guard try db.mergeSpeakers(id, from: from, into: into,
                                       label: String(localized: "把「\(fromName)」合併到「\(intoName)」")) != nil else { return }
            // 使用者取的名字：用合併後的聲紋重新學（自動帶入的名字不學，design §21.4）
            if AppSettings.voiceprints, source == "manual" || source == "suggestion" {
                try db.learnVoiceprint(id, idx: into, name: intoName, source: source ?? "manual")
            }
            noticeMessage = String(localized: "已把「\(fromName)」合併到「\(intoName)」。要復原的話，點說話者的名字選「復原」。")
        }
    }

    /// 「這句是誰說的」（R23.2）：to 是 nil 時新增一位說話者
    func reassignSegments(_ ids: [Int64], to idx: Int?) {
        guard let id = selection, !ids.isEmpty else { return }
        if let problem = speakerEditProblem(id) {
            noticeMessage = problem
            return
        }
        let target = idx.flatMap { detail?.speakerName($0) } ?? String(localized: "新的說話者")
        let label = ids.count == 1 ? String(localized: "把一句改成「\(target)」說的")
                                   : String(localized: "把 \(ids.count) 句改成「\(target)」說的")
        perform(.detail([.speakers, .segments])) {
            try db.reassignSegments(id, ids: ids, to: idx, label: label)
        }
    }

    /// 「復原說話者的調整」（改名小視窗、逐字稿右鍵選單）：只復原最近一次合併或改句子
    func undoSpeakerEdit() {
        guard let id = selection else { return }
        if let problem = speakerEditProblem(id) {
            noticeMessage = problem
            return
        }
        guard let label = try? db.speakerUndoLabel(id) else {
            noticeMessage = String(localized: "沒有可以復原的說話者調整（改名、重新辨識說話者之後就不能復原之前的調整）")
            return
        }
        perform(.detail([.speakers, .segments, .meeting])) {
            if try db.undoSpeakerEdit(id) { noticeMessage = String(localized: "已復原：\(label)") }
        }
    }

    /// 說話者可能分得不夠的提示（design §24.5）
    func dismissSpeakerHint() {
        guard let id = selection else { return }
        perform(.detail([.meeting])) { try db.dismissSpeakerHint(id) }
    }

    func resummarize() {
        guard let id = selection, readyForAction(id) else { return }
        if let detail, detail.segments.isEmpty && detail.meeting.userNotes.isEmpty {
            noticeMessage = String(localized: "還沒有逐字稿或筆記可以整理")
            return
        }
        guard AppSettings.provider != .none else {
            errorMessage = AppSettings.needsProviderSetup
                ? String(localized: "還沒設定整理工具，請先到設定選擇 Claude、Codex 或 API")
                : String(localized: "整理工具設定為「不整理」，請先到設定改成 Claude、Codex 或 API")
            return
        }
        // 沒登入、沒有 key…：不排，說原因（R45.4、design §34.2）
        if let problem = AppSettings.summarizerProblem { errorMessage = problem.message; return }
        perform { try db.enqueue(id, kind: .summarize) }
        runner.wake()
    }

    func undoNotes() {
        guard let id = selection, readyForAction(id) else { return }
        perform {
            if try !db.undoNotes(id) { noticeMessage = String(localized: "沒有上一版可以復原") }
        }
    }

    /// 手動編輯會議紀錄（R6.6）
    /// 編輯器按「完成」或關掉時存檔。編輯期間會議紀錄被別的工作改過的話提醒一下：舊的那一版還在版本紀錄裡
    func finishNotesEdit(_ draft: MeetingNotes, base: MeetingNotes?, meetingId: Int64, automatically: Bool = false) {
        guard draft != base else { return }
        if let base, let current = try? db.meeting(meetingId)?.decodedNotes, current != base {
            noticeMessage = String(localized: "編輯期間會議紀錄有更新，已用你改的版本存檔；按「編輯」旁的「復原上一版」可以找回更新的內容")
        } else if automatically {
            noticeMessage = String(localized: "已儲存剛才編輯的會議紀錄")
        }
        saveEditedNotes(draft, meetingId: meetingId)
    }

    /// meetingId 要明確給：編輯器關掉時（切到別場會議、錄音停止後自動跳到那場），selection 可能已經是別場
    func saveEditedNotes(_ notes: MeetingNotes, meetingId: Int64) {
        guard let current = try? db.meeting(meetingId)?.decodedNotes, notes != current else { return }
        perform { try db.saveNotes(meetingId, notes: notes, by: nil, reason: String(localized: "手動編輯")) }
    }

    /// 回傳是否送出成功；失敗時畫面上的回答要留著
    @discardableResult
    func submitReview(answers: [Int: String], dismissed: Set<Int>) -> Bool {
        guard let id = selection, let detail, let notes = detail.notes, readyForAction(id) else { return false }
        let language = detail.meeting.meetingLanguage
        // 回答分三類（design §31.3）：確認（是非題回答「是」、沒填答案）和略過直接從紀錄移除，不送整理工具（R41.4、D103）；
        // 只有要改內容的回答（打字的、「不是」）才送整理工具依回答修正
        var confirmed: Set<Int> = []
        var dismissed = dismissed
        var modified: [(Int, ReviewAnswer)] = []
        for (index, text) in answers {
            let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !trimmed.isEmpty, !dismissed.contains(index), notes.reviewItems.indices.contains(index) else { continue }
            let item = notes.reviewItems[index]
            if let choice = YesNoAnswer(trimmed, language: language), choice.isYes == true {
                confirmed.insert(index)
            } else if let choice = YesNoAnswer(trimmed, language: language), choice.isYes == false, choice.correction.isEmpty {
                dismissed.insert(index)   // 「不是」沒填答案：有問題但不知道對的是什麼，跟略過一樣（R41.4）
            } else {
                modified.append((index, ReviewAnswer(time: item.time, question: item.question, answer: trimmed)))
            }
        }
        let answered = modified.sorted { $0.0 < $1.0 }.map(\.1)
        if !answered.isEmpty && AppSettings.provider == .none {
            errorMessage = AppSettings.needsProviderSetup
                ? String(localized: "還沒設定整理工具，沒辦法依回答修正。請先到設定選擇 Claude、Codex 或 API")
                : String(localized: "整理工具設定為「不整理」，沒辦法依回答修正。請先到設定改成 Claude、Codex 或 API")
            return false
        }
        if !answered.isEmpty, let problem = AppSettings.summarizerProblem {
            errorMessage = problem.message
            return false
        }
        perform {
            if !confirmed.isEmpty || !dismissed.isEmpty { try db.resolveReviewItems(id, confirmed: confirmed, dismissed: dismissed) }
            if !answered.isEmpty {
                let payload = String(decoding: try JSONEncoder().encode(RevisePayload(answers: answered)), as: UTF8.self)
                try db.enqueue(id, kind: .revise, payload: payload)
            }
        }
        runner.wake()
        // 匿名統計（R41.7）：只記題數
        UsageStats.shared.reviewed(answered: answered.count, confirmed: confirmed.count,
                                   skipped: dismissed.filter { notes.reviewItems.indices.contains($0) }.count)
        return true
    }

    func updateSegment(_ segmentId: Int64, text: String) {
        perform(.segments([segmentId])) { try db.updateSegmentText(segmentId, text: text) }
    }

    // MARK: 逐字稿校正（design §17.4）

    /// 手動檢查可能聽錯的字（「更多」選單）
    func checkTranscript() {
        guard let id = selection, readyForAction(id) else { return }
        if detail?.segments.isEmpty != false {
            noticeMessage = String(localized: "還沒有逐字稿可以檢查")
            return
        }
        guard AppSettings.provider != .none else {
            errorMessage = AppSettings.needsProviderSetup
                ? String(localized: "還沒設定整理工具，請先到設定選擇 Claude、Codex 或 API")
                : String(localized: "整理工具設定為「不整理」，請先到設定改成 Claude、Codex 或 API")
            return
        }
        if let problem = AppSettings.summarizerProblem { errorMessage = problem.message; return }
        perform { try db.enqueue(id, kind: .correct) }
        runner.wake()
    }

    /// 這幾則校正改到的段落（只重讀這幾段，design §21.12）
    private func segmentIds(ofCorrections ids: [Int64]) -> [Int64] {
        let wanted = Set(ids)
        return Array(Set(detail?.corrections.filter { $0.id.map(wanted.contains) ?? false }.map(\.segmentId) ?? []))
    }

    func acceptCorrection(_ id: Int64) {
        perform(.segments(segmentIds(ofCorrections: [id]), [.corrections])) {
            if try !db.acceptCorrection(id) {
                noticeMessage = String(localized: "這一段已經改過，找不到建議要改的文字")
            }
        }
    }

    func rejectCorrection(_ id: Int64) {
        perform(.detail([.corrections])) { try db.rejectCorrection(id) }
    }

    func revertCorrection(_ id: Int64) {
        perform(.segments(segmentIds(ofCorrections: [id]), [.corrections])) {
            if try !db.revertCorrection(id) {
                noticeMessage = String(localized: "這一段已經改過，沒辦法還原")
            }
        }
    }

    func acceptAllCorrections() {
        guard let pending = detail?.corrections.filter({ $0.status == "pending" }) else { return }
        acceptCorrections(pending.compactMap(\.id))
    }

    func rejectAllCorrections() {
        guard let pending = detail?.corrections.filter({ $0.status == "pending" }) else { return }
        rejectCorrections(pending.compactMap(\.id))
    }

    /// 同一個詞的每一處一起接受或忽略（design §21.11、R20.2）
    func acceptCorrections(_ ids: [Int64]) {
        perform(.segments(segmentIds(ofCorrections: ids), [.corrections])) {
            let failed = try db.acceptCorrections(ids)
            if failed > 0 { noticeMessage = String(localized: "有 \(failed) 則建議的段落已經改過，沒有套用") }
        }
    }

    func rejectCorrections(_ ids: [Int64]) {
        perform(.detail([.corrections])) { try db.rejectCorrections(ids) }
    }

    // MARK: 編輯中的草稿（design §21.15、R20.6）：會議內容，存在資料庫，不放 UserDefaults

    func drafts(_ meetingId: Int64) -> [String: String] { (try? db.drafts(meetingId)) ?? [:] }

    func latestDraft(_ meetingId: Int64, prefix: String) -> (key: String, text: String)? {
        try? db.latestDraft(meetingId, prefix: prefix)
    }

    func saveDraft(_ meetingId: Int64, key: String, text: String) {
        do { try db.setDraft(meetingId, key: key, text: text) } catch { log.error("save draft failed: \(error.localizedDescription)") }
    }

    func deleteDrafts(_ meetingId: Int64, prefix: String) {
        try? db.deleteDrafts(meetingId, prefix: prefix)
    }

    #if DEBUG
    /// shiyinctl editsegment：等於點那一段開始編輯（逐字稿分頁看到就開始）
    var debugEditSegment: Int64?
    /// shiyinctl settingsaction：等於在設定的整理工具頁按「選擇」（pickmodel）或貼上 key 按「儲存 key」（savekey）
    var debugSettingsAction: [String]?
    #endif

    /// 捲到這一段並暫時加底色。先清掉再設，同一段點第二次也會捲過去；幾秒後拿掉底色，不要一直留著
    func flashSegment(_ id: Int64) {
        highlightSegment = nil
        Task { @MainActor in
            highlightSegment = id
            try? await Task.sleep(for: .seconds(6))
            if highlightSegment == id { highlightSegment = nil }
        }
    }

    /// source：manual（使用者改名）或 suggestion（套用 AI 猜的名字，design §21.5）
    func renameSpeaker(_ idx: Int, to name: String, source: String = "manual") {
        guard let id = selection else { return }
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        // 清空就回到預設名稱，用會議語言（會帶進會議紀錄和匯出），不是介面語言
        let language = detail?.meeting.meetingLanguage ?? .zh
        let fallback = language.text(zh: "說話者 \(idx)", en: "Speaker \(idx)", ja: "話者 \(idx)")  // l10n-skip
        let name = trimmed.isEmpty ? fallback : trimmed
        let old = detail?.speakers[idx]
        perform(.detail([.speakers, .meeting])) {
            try db.renameSpeaker(id, idx: idx, name: name)
            // 記住這個人的聲音（design §21.4）；改回預設名稱就忘掉這場會議的這一份
            if AppSettings.voiceprints { try db.learnVoiceprint(id, idx: idx, name: name, source: source) }
            try db.clearSpeakerSuggestion(id, idx: idx)
            // 會議紀錄裡的舊名字換成新名字（design §21.6）
            if let old { try db.replaceSpeakerNameInNotes(id, from: old, to: name) }
        }
    }

    /// 「不是他」：聲紋自動帶入的名字不對，回到預設名稱（design §21.4）；會議紀錄裡的名字也換回去
    func rejectVoiceprintMatch(_ idx: Int) {
        guard let id = selection else { return }
        let language = detail?.meeting.meetingLanguage ?? .zh
        let fallback = language.text(zh: "說話者 \(idx)", en: "Speaker \(idx)", ja: "話者 \(idx)")  // l10n-skip
        let old = detail?.speakers[idx]
        perform(.detail([.speakers, .meeting])) {
            try db.rejectVoiceprintMatch(id, idx: idx, defaultName: fallback)
            if let old { try db.replaceSpeakerNameInNotes(id, from: old, to: fallback) }
        }
    }

    // MARK: 猜說話者的建議（design §21.5、R19.2）

    func applySpeakerSuggestion(_ idx: Int) {
        guard let suggestion = detail?.speakerSuggestions[idx] else { return }
        renameSpeaker(idx, to: suggestion.name, source: "suggestion")
    }

    func applyAllSpeakerSuggestions() {
        for idx in (detail?.speakerSuggestions.keys.sorted() ?? []) { applySpeakerSuggestion(idx) }
    }

    func dismissSpeakerSuggestion(_ idx: Int) {
        guard let id = selection else { return }
        perform(.detail([.speakers])) { try db.clearSpeakerSuggestion(id, idx: idx) }
    }

    /// 待確認的說話者題目送出（R33.2、design §27）：回答是 YesNoAnswer 的文字（依會議語言）。
    /// 是 = 套用建議；不是而且有名字 = 改成那個名字；不是沒填、略過 = 清掉建議；還沒選的不動
    func answerSpeakerQuestions(_ answers: [Int: String], skipped: Set<Int>) {
        guard let detail else { return }
        let language = detail.meeting.meetingLanguage
        for idx in detail.speakerSuggestions.keys.sorted() {
            if skipped.contains(idx) {
                dismissSpeakerSuggestion(idx)
                continue
            }
            guard let choice = YesNoAnswer(answers[idx] ?? "", language: language), let yes = choice.isYes else { continue }
            let name = choice.correction.trimmingCharacters(in: .whitespacesAndNewlines)
            if yes {
                applySpeakerSuggestion(idx)
            } else if !name.isEmpty {
                renameSpeaker(idx, to: name)
            } else {
                dismissSpeakerSuggestion(idx)
            }
        }
    }

    // MARK: 偵測線上會議（design §21.7、R19.4）

    @ObservationIgnored let meetingDetector = MeetingDetector()

    /// 有通知權限就發通知（帶按鈕），沒有就用不搶焦點的浮動視窗
    func showMeetingPrompt(_ prompt: MeetingDetector.Prompt) {
        Task {
            if await notifier.isAuthorized() {
                notifier.meetingPrompt(prompt)
            } else {
                showMeetingPanel(prompt)
            }
        }
    }

    private func showMeetingPanel(_ prompt: MeetingDetector.Prompt) {
        switch prompt {
        case .start(let app):
            MeetingPromptPanel.shared.show(prompt, actions: [
                ("later", String(localized: "這次不用"), {}),
                // 名稱長的 app（Microsoft Teams）放進按鈕會被截掉；訊息裡已經有 app 的名字
                ("mute", String(localized: "不要再提醒這個 app"), { MeetingDetector.mute(app.bundleID) }),
                ("start", String(localized: "開始錄音"), { [weak self] in self?.startRecording() }),
            ], message: String(localized: "\(app.name) 正在開會，要開始錄音嗎？"))
        case .stop:
            MeetingPromptPanel.shared.show(prompt, actions: [
                ("continue", String(localized: "繼續錄"), {}),
                ("stop", String(localized: "停止並轉錄"), { [weak self] in self?.recorder.stop() }),
            ], message: String(localized: "會議好像結束了，要停止錄音嗎？"))
        }
    }

    /// 通知上的按鈕
    func handleMeetingAction(_ action: String, bundle: String) {
        switch action {
        case "start": startRecording()
        case "mute": MeetingDetector.mute(bundle)
        case "stop": recorder.stop()
        default: break
        }
    }

    // MARK: 對會議提問的狀態（design §21.8；流程在 Asking.swift）

    /// 正在問哪裡：一場會議或所有會議
    var asking: AskTarget?
    /// 目前會議的問答紀錄、問所有會議的問答紀錄
    var meetingQuestions: [QuestionRecord] = []
    var allQuestions: [QuestionRecord] = []
    @ObservationIgnored var askTask: Task<Void, Never>?
    @ObservationIgnored var askProcess: Subprocess?

    // MARK: 認識的人（設定頁，design §21.4）

    var people: [PersonSummary] = []

    func reloadPeople() {
        people = (try? db.peopleSummaries()) ?? []
    }

    func renamePerson(_ id: Int64, to name: String) {
        perform { try db.renamePerson(id, to: name) }
        reloadPeople()
    }

    func deletePerson(_ id: Int64) {
        perform { try db.deletePerson(id) }
        reloadPeople()
    }

    func deleteAllPeople() {
        perform { try db.deleteAllPeople() }
        reloadPeople()
    }

    func updateUserNotes(_ text: String) {
        guard let id = selection ?? recorder.meetingId else { return }
        perform { try db.updateUserNotes(id, text) }
    }

    /// 筆記編輯器存檔（design §20.4）：畫面上是同一場會議時只更新它的筆記，不整份重新讀取。
    /// 編輯器切分頁後重新出現時，讀到的就是剛存的內容，不會用舊的蓋回去
    func saveUserNotes(_ id: Int64, _ text: String) {
        do {
            try db.updateUserNotes(id, text)
            if detail?.meeting.id == id { detail?.meeting.userNotes = text }
        } catch {
            log.error("save user notes failed: \(error.localizedDescription)")
        }
    }

    func addBookmark(at time: Double, meetingId: Int64? = nil, label: String = "") {
        guard let id = meetingId ?? selection else { return }
        perform(.detail([.bookmarks])) { try db.addBookmark(id, time: time, label: label) }
    }

    func deleteBookmark(_ id: Int64) {
        perform(.detail([.bookmarks])) { try db.deleteBookmark(id) }
    }

    func replace(find: String, with replacement: String, addToGlossary: Bool) -> ReplaceResult? {
        guard let id = selection, !find.isEmpty, find != replacement else { return nil }
        var result: ReplaceResult?
        perform {
            result = try db.replace(id, find: find, with: replacement)
            if addToGlossary { try db.addTerm(replacement) }
        }
        return result
    }

    func addTerm(_ term: String) { perform { try db.addTerm(term) } }
    func dismissGlossarySuggestion(_ term: String) { perform { try db.dismissGlossarySuggestion(term) } }
    func deleteTerm(_ id: Int64) { perform { try db.deleteTerm(id) } }

    func saveTemplate(_ template: TemplateRecord) { perform { try db.saveTemplate(template) } }
    func deleteTemplate(_ id: String) { perform { try db.deleteTemplate(id) } }

    // MARK: 匯入其他格式（R3.3～R3.5）

    /// 解開封包、複製錄音在背景做（design §21.13、R20.4）
    func importPackage(_ url: URL) {
        let (db, folder) = (db, currentFolder)
        importingCount += 1
        Task {
            defer { importingCount -= 1 }
            do {
                let meeting = try await Task.detached(priority: .userInitiated) {
                    try MeetingPackage.importPackage(url, db: db, folder: folder)
                }.value
                reload()
                showTrash = false
                selection = meeting.id
                noticeMessage = String(localized: "已匯入「\(meeting.displayTitle)」")
            } catch {
                errorMessage = error.localizedDescription
            }
        }
    }

    func importTranscript(_ url: URL) {
        let accessing = url.startAccessingSecurityScopedResource()
        defer { if accessing { url.stopAccessingSecurityScopedResource() } }
        guard let text = (try? String(contentsOf: url, encoding: .utf8)) ?? (try? String(contentsOf: url, encoding: .utf16)) else {
            errorMessage = String(localized: "讀不到「\(url.lastPathComponent)」")
            return
        }
        let parsed = TranscriptImporter.parse(text, ext: url.pathExtension.lowercased())
        guard !parsed.segments.isEmpty else {
            errorMessage = String(localized: "「\(url.lastPathComponent)」裡沒有可以用的逐字稿內容")
            return
        }
        do {
            let meeting = try db.insertMeeting(Meeting(
                meetingDate: Importer.inferDate(filename: url.lastPathComponent, modified: Importer.modificationDate(url)),
                sourceFilename: url.lastPathComponent, templateId: AppSettings.defaultTemplate, source: "transcript",
                folder: currentFolder, language: AppSettings.defaultMeetingLanguage.rawValue),
                audioExtension: nil)
            let id = meeting.id!
            try db.saveTranscript(id, segments: parsed.segments.enumerated().map { i, s in
                Segment(meetingId: id, seq: i, start: s.start, end: s.end, text: s.text, lowConfidence: false, speaker: nil, kind: "final")
            }, duration: Int(parsed.segments.last?.end ?? 0), speakerCount: 0)
            if AppSettings.autoSummarize && AppSettings.summarizerReady { try db.enqueue(id, kind: .summarize) }
            runner.wake()
            reload()
            showTrash = false
            selection = id
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    /// Markdown 當成使用者筆記：有選會議就加到那場，沒有就建一場新的（R3.5）
    func importMarkdownNotes(_ urls: [URL]) {
        for url in urls {
            let accessing = url.startAccessingSecurityScopedResource()
            defer { if accessing { url.stopAccessingSecurityScopedResource() } }
            guard let text = try? String(contentsOf: url, encoding: .utf8) else { continue }
            if let id = selection, let meeting = try? db.meeting(id) {
                let merged = meeting.userNotes.isEmpty ? text : meeting.userNotes + "\n\n" + text
                perform { try db.updateUserNotes(id, merged) }
                tab = .transcript
                userNotesExpanded = true
            } else if let meeting = try? db.insertMeeting(Meeting(
                meetingDate: DateText.string(Date()), sourceFilename: url.lastPathComponent, userNotes: text, source: "transcript",
                folder: currentFolder, language: AppSettings.defaultMeetingLanguage.rawValue),
                audioExtension: nil) {
                perform { try db.updateUserNotes(meeting.id!, text) }
                selection = meeting.id
                tab = .transcript
                userNotesExpanded = true
            }
        }
    }

    // MARK: 匯出（R7）

    func openExportSheet() {
        exportMeetingId = selection
        showExportSheet = true
    }

    /// 依會議 id 從資料庫讀要匯出的資料；會議已經被永久刪除時丟出錯誤（design §20.9）
    func exportBundle(meetingId: Int64) throws -> ExportBundle {
        guard let meeting = try db.meeting(meetingId) else { throw ExportError.meetingGone }
        return ExportBundle(meeting: meeting, segments: try db.segments(meetingId),
                            speakers: Dictionary(uniqueKeysWithValues: try db.speakers(meetingId).map { ($0.idx, $0.name) }),
                            bookmarks: try db.bookmarks(meetingId), notes: meeting.decodedNotes)
    }

    func exportBundle() -> ExportBundle? {
        guard let d = detail else { return nil }
        return ExportBundle(meeting: d.meeting, segments: d.segments, speakers: d.speakers, bookmarks: d.bookmarks, notes: d.notes)
    }

    /// 寫出檔案
    func export(_ item: ExportItem, format: String, times: Bool, speakers: Bool, meetingId: Int64, to url: URL) async throws {
        let b = try exportBundle(meetingId: meetingId)
        switch item {
        case .notes:
            switch format {
            case "pdf": try await HTML.pdf(NotesExporter.html(b), to: url)
            case "docx": try await HTML.docx(NotesExporter.html(b)).write(to: url)
            default: try NotesExporter.markdown(b).write(to: url, atomically: true, encoding: .utf8)
            }
        case .transcript:
            if format == "docx" { try await HTML.docx(TranscriptExporter.html(b, times: times, speakers: speakers)).write(to: url) }
            else {
                // 文字格式、封包在背景寫，長會議不卡住畫面（design §21.13）；Word、PDF 用非同步的 HTML 轉換
                try await Task.detached(priority: .userInitiated) {
                    try TranscriptExporter.text(b, format: format, times: times, speakers: speakers).write(to: url, atomically: true, encoding: .utf8)
                }.value
            }
        case .audio:
            try await AudioExporter.export(b, format: format, to: url)
        case .package:
            try await Task.detached(priority: .userInitiated) { try MeetingPackage.export(b, to: url) }.value
        }
    }

    func suggestedFilename(_ item: ExportItem, format: String, meeting: Meeting?) -> String {
        guard let m = meeting else { return String(localized: "會議.\(format)") }
        let base = "\(m.meetingDate) \(m.displayTitle)".replacingOccurrences(of: "/", with: "／").replacingOccurrences(of: ":", with: "：")  // l10n-skip
        switch item {
        case .transcript: return String(localized: "\(base)（逐字稿）.\(format)")
        case .audio: return "\(base).\(format)"
        default: return "\(base).\(format)"
        }
    }

    enum ExportError: LocalizedError {
        case meetingGone
        var errorDescription: String? { String(localized: "這場會議已經被刪除，沒辦法匯出") }
    }

    enum CopyStyle { case plain, markdown, rich }

    func copyNotes(_ style: CopyStyle) {
        guard let b = exportBundle() else { return }
        guard b.notes != nil else {
            noticeMessage = String(localized: "還沒有會議紀錄可以複製")
            return
        }
        switch style {
        case .plain:
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(NotesExporter.plain(b), forType: .string)
        case .markdown:
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(NotesExporter.markdown(b), forType: .string)
        case .rich:
            HTML.copy(plain: NotesExporter.plain(b), html: NotesExporter.html(b))
        }
        noticeMessage = String(localized: "已複製會議紀錄")
    }

    // MARK: 搜尋（R9.1）

    private func runSearch() {
        searchResults = (try? db.search(searchQuery)) ?? []
    }

    func openSearchHit(_ hit: SearchHit) {
        showTrash = false
        selection = hit.meetingId
        switch hit.kind {
        case .segment:
            tab = .transcript
            flashSegment(hit.ref)
            if let segment = detail?.segments.first(where: { $0.id == hit.ref }) { player.seek(to: segment.start, play: false) }
        case .notes, .title: tab = .summary
        case .userNotes: tab = .transcript; userNotesExpanded = true
        }
    }

    // MARK: 第二版留下的檔案（R10.5）

    func deleteLegacyItems() {
        for url in Paths.legacyItems { try? FileManager.default.removeItem(at: url) }
        legacyBytes = 0
        noticeMessage = String(localized: "已刪除第二版的 Python 環境和舊模型")
    }

    static func directorySize(_ url: URL) -> Int64 {
        guard let enumerator = FileManager.default.enumerator(at: url, includingPropertiesForKeys: [.totalFileAllocatedSizeKey])
        else { return 0 }
        var total: Int64 = 0
        for case let file as URL in enumerator {
            total += Int64((try? file.resourceValues(forKeys: [.totalFileAllocatedSizeKey]))?.totalFileAllocatedSize ?? 0)
        }
        return total
    }

    // MARK: 內部

    /// 操作完要重讀什麼（design §21.12）：全部（會議列表和目前的會議），或只重讀目前這場會議的某幾樣
    enum ReloadScope {
        case all
        case detail(DetailParts)
        /// 只重讀改到的這幾段（長會議整份重讀要幾十毫秒），以及其他幾樣
        case segments([Int64], DetailParts = [])
    }

    struct DetailParts: OptionSet {
        let rawValue: Int
        static let speakers = DetailParts(rawValue: 1 << 0)
        static let bookmarks = DetailParts(rawValue: 1 << 1)
        static let segments = DetailParts(rawValue: 1 << 2)
        static let corrections = DetailParts(rawValue: 1 << 3)
        static let meeting = DetailParts(rawValue: 1 << 4)   // 會議本身、會議紀錄、版本數
    }

    func perform(_ scope: ReloadScope = .all, _ work: () throws -> Void) {
        #if DEBUG
        let started = Date()
        defer { DebugMetrics.recordPerform(Date().timeIntervalSince(started) * 1000) }
        #endif
        do { try work() } catch { errorMessage = error.localizedDescription }
        switch scope {
        case .all: reload()
        case .detail(let parts): reloadDetail(parts)
        case .segments(let ids, let parts): reloadDetail(parts, segmentIds: ids)
        }
    }

    /// 只重讀目前這場會議的某幾樣（segmentIds：只換掉這幾段）；目前沒有顯示這場會議時整個重讀
    func reloadDetail(_ parts: DetailParts, segmentIds: [Int64] = []) {
        guard let id = selection, var updated = detail, updated.meeting.id == id else { return reloadDetail() }
        if !segmentIds.isEmpty, let fresh = try? db.segments(ids: segmentIds) {
            let index = Dictionary(updated.segments.enumerated().compactMap { i, s in s.id.map { ($0, i) } }, uniquingKeysWith: { a, _ in a })
            for segment in fresh { if let id = segment.id, let i = index[id] { updated.segments[i] = segment } }
        }
        #if DEBUG
        DebugMetrics.bump("reloadDetailParts")
        #endif
        if parts.contains(.speakers) {
            updated.loadSpeakers((try? db.speakers(id)) ?? [])
            updated.speakerCandidates = (try? db.voiceprintCandidates(id)) ?? [:]
            updated.speakerUndoLabel = try? db.speakerUndoLabel(id)
        }
        if parts.contains(.bookmarks) { updated.bookmarks = (try? db.bookmarks(id)) ?? [] }
        if parts.contains(.segments) { updated.segments = (try? db.segments(id)) ?? [] }
        if parts.contains(.corrections) { updated.corrections = (try? db.corrections(id)) ?? [] }
        if parts.contains(.meeting), let meeting = try? db.meeting(id) {
            updated.meeting = meeting
            updated.notes = meeting.decodedNotes
            updated.versionCount = (try? db.versionCount(id)) ?? 0
            updated.linkReviewItems()
        }
        if !parts.isDisjoint(with: [.segments, .corrections]) || !segmentIds.isEmpty { updated.buildIndexes() }
        detail = updated
        syncReviewDraft()
    }

    /// 待確認的回答跟著目前的會議（design §31.2）：換會議就換一份（舊的先存），題目或說話者的建議變了就同步
    private func syncReviewDraft() {
        guard let detail, let id = detail.meeting.id else {
            reviewDraft?.flush()
            reviewDraft = nil
            return
        }
        if reviewDraft?.meetingId != id {
            reviewDraft?.flush()
            reviewDraft = ReviewDraft(meetingId: id, model: self)
        }
        reviewDraft?.sync(items: detail.notes?.reviewItems ?? [], suggestions: detail.speakerSuggestions)
    }

    /// 送出待確認的回答（待確認分頁和就地回答共用，R41.2）：先送整理工具要處理的題目（題目文字可能有說話者的名字，
    /// 改名後會變），失敗時全部留著；再處理說話者的題目（design §27）
    func submitReviewDraft(only indexes: Set<Int>? = nil) {
        guard let draft = reviewDraft, detail?.meeting.id == draft.meetingId else { return }
        if let indexes {
            // 卡片只送這一條對到的題目（R41.2），說話者題目不算，其他題目的回答留著
            let answers = draft.answers.filter { indexes.contains($0.key) }
            let dismissed = draft.dismissed.intersection(indexes)
            guard !answers.isEmpty || !dismissed.isEmpty, submitReview(answers: answers, dismissed: dismissed) else { return }
            draft.clear(indexes: indexes)
            return
        }
        if draft.answeredCount > 0 || !draft.dismissed.isEmpty {
            guard submitReview(answers: draft.answers, dismissed: draft.dismissed) else { return }
        }
        answerSpeakerQuestions(draft.speakerAnswers, skipped: draft.speakerSkipped)
        draft.clear()
    }

    private func jobFinished(_ job: Job, error: Error?) {
        guard let meeting = try? db.meeting(job.meetingId) else { return }
        notifier.jobFinished(job, meeting: meeting, error: error)
    }
}

extension AppModel {
    func refreshSummarizerProblem() {
        let problem = AppSettings.summarizerProblem
        if problem != summarizerProblem { summarizerProblem = problem }
    }
}

extension Notification.Name {
    /// 拾音要結束了：畫面上還開著的編輯器馬上存草稿（design §21.15）
    static let flushDrafts = Notification.Name("com.weiwei.shiyin.flushDrafts")
    /// 整理工具的登入狀態、key、暫停變了（design §34.1）
    static let summarizerStateChanged = Notification.Name("com.weiwei.shiyin.summarizerStateChanged")
}
