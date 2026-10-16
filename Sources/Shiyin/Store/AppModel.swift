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

enum DetailTab: String, CaseIterable, Identifiable {
    case summary, topics, transcript, review, notes
    var id: String { rawValue }
    var label: String {
        switch self {
        case .summary: return String(localized: "摘要")
        case .topics: return String(localized: "詳細")
        case .transcript: return String(localized: "逐字稿")
        case .review: return String(localized: "待確認")
        case .notes: return String(localized: "筆記")
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

    func speakerName(_ idx: Int?) -> String? {
        guard let idx else { return nil }
        return speakers[idx] ?? String(localized: "說話者 \(idx)")
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
    var meetings: [MeetingRow] = []
    var trash: [MeetingRow] = []
    var showTrash = false
    var selection: Int64? { didSet { if oldValue != selection { reloadDetail() } } }
    var detail: MeetingDetail?
    var tab: DetailTab = .summary
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
    var confirmDelete = false
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
            alert.informativeText = String(localized: "原因：\(reason)\n\n你的資料沒有被改動。升級前的備份在：\(Paths.backupsDir.path)\n請把這個訊息截圖給維護拾音的人。")
            alert.addButton(withTitle: String(localized: "結束拾音"))
            alert.runModal()
            exit(1)
        }
        runner = JobRunner(db: db)
        environment = EnvironmentStatus(items: [])
        runner.onChange = { [weak self] in self?.reload() }
        runner.onFinished = { [weak self] job, error in self?.jobFinished(job, error: error) }
        runner.onAudioReplaced = { [weak self] id in
            guard let self, self.selection == id else { return }
            self.player.unload()
            self.reloadDetail(forceAudio: true)
        }
        recorder.onFinished = { [weak self] result in self?.recordingFinished(result) }
        recorder.onLiveFinal = { [weak self] id, start, end, text in
            try? self?.db.appendLiveSegment(id, start: start, end: end, text: text)
        }
        recorder.onLanguageChange = { [weak self] id, language in
            guard let self, var meeting = try? self.db.meeting(id) else { return }
            meeting.language = language.rawValue
            self.perform { try self.db.updateMeeting(meeting) }
        }
        notifier.onOpen = { [weak self] id in self?.reveal(id) }
    }

    private var started = false

    func start() {
        guard !started else { return }
        started = true
        notifier.activate()
        BuiltinTemplates.seed(db)
        cleanUpPreviousRun()
        purgeExpiredTrash()
        recoverRecordings()
        runner.start()
        // 語言偵測模型很小（60 MB），開著自動偵測就先下載好
        if AppSettings.autoDetectLanguage { models.autoStart(.languageDetection) }
        GlobalHotKey.shared.onPress = { [weak self] in self?.toggleRecording() }
        GlobalHotKey.shared.setEnabled(AppSettings.globalHotKey)
        updater.scheduleAutomaticChecks()
        refreshEnvironment()
        legacyBytes = Paths.legacyItems.reduce(0) { $0 + Self.directorySize($1) }
        reload()
    }

    // MARK: 讀取

    func reload() {
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

    /// 能不能正式轉錄這個語言的會議：對應的模型已下載（中英文 Breeze、日文 large-v3-turbo），或改用 macOS 內建辨識（design §6.2.1、§18.2）
    func canTranscribe(_ language: MeetingLanguage) -> Bool {
        AppSettings.useSystemTranscriber || models.isInstalled(ModelStore.transcription(for: language))
    }

    /// 改用 macOS 內建辨識，排隊中的轉錄馬上開始
    func useSystemTranscriber() {
        UserDefaults.standard.set("system", forKey: AppSettings.transcriptionEngineKey)
        runner.wake()
        refreshEnvironment()
        reload()
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
        guard let id = selection, let meeting = try? db.meeting(id) else {
            detail = nil
            player.unload()
            return
        }
        let previous = detail?.meeting
        detail = MeetingDetail(
            meeting: meeting, segments: (try? db.segments(id)) ?? [], notes: meeting.decodedNotes,
            versionCount: (try? db.versionCount(id)) ?? 0, latestJob: try? db.latestJob(id),
            speakers: Dictionary(uniqueKeysWithValues: ((try? db.speakers(id)) ?? []).map { ($0.idx, $0.name) }),
            bookmarks: (try? db.bookmarks(id)) ?? [], corrections: (try? db.corrections(id)) ?? [])
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
        return status(meetingId: detail.meeting.id!, latestJob: detail.latestJob,
                      reviewCount: detail.notes?.reviewItems.count ?? 0)
    }

    var menuBarSymbol: String {
        if recorder.isRecording { return "record.circle.fill" }
        if runner.isBusy || queuedCount > 0 { return "waveform.circle.fill" }
        return "waveform"
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

    private func importFiles(_ urls: [URL], options: ImportOptions) {
        var options = options
        if options.folder.isEmpty { options.folder = currentFolder }
        var last: Meeting?
        var failures: [String] = []
        for url in urls {
            do { last = try Importer.importFile(url, options: options, db: db) }
            catch { failures.append(error.localizedDescription) }
        }
        if !failures.isEmpty { errorMessage = failures.joined(separator: "\n") }
        runner.wake()
        reload()
        if let last { showTrash = false; selection = last.id }
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
        do {
            if let error = result.error {
                errorMessage = error
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
            meeting.audioFile = destination.lastPathComponent
            if result.duration > 0 { meeting.durationSeconds = Int(result.duration.rounded()) }
            try db.updateMeeting(meeting)
            try db.enqueue(result.meetingId, kind: .transcribe)
            runner.wake()
            reload()
            selection = result.meetingId
        } catch {
            errorMessage = String(localized: "錄音存檔失敗：\(error.localizedDescription)（檔案還在 \(result.file?.path ?? "")）")
        }
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
                    meeting.audioFile = destination.lastPathComponent
                    if meeting.title.isEmpty { meeting.title = String(localized: "復原的錄音") }
                    try db.updateMeeting(meeting)
                    try db.enqueue(id, kind: .transcribe)
                } else {
                    try Importer.importFile(file, options: ImportOptions(title: String(localized: "復原的錄音"), sourceName: String(localized: "復原的錄音.wav")),
                                            db: db, move: true)
                }
                noticeMessage = String(localized: "找到上次沒有正常結束的錄音，已經復原並開始轉錄。")
            } catch {
                log.error("recover recording failed: \(error.localizedDescription)")
            }
        }
        // 錄音中途當掉、連錄音檔都沒有的空會議
        for row in meetings where row.meeting.source == "recording" && row.meeting.audioFile.isEmpty && recorder.meetingId != row.id {
            try? db.deleteMeeting(row.id)
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
        guard var meeting = detail?.meeting, meeting.meetingLanguage != language else { return }
        meeting.language = language.rawValue
        perform { try db.updateMeeting(meeting) }
        noticeMessage = String(localized: "會議語言改成 \(language.name)，要重新轉錄或整理的話，從「更多」選")
    }

    func setTemplate(_ templateId: String) {
        guard var meeting = detail?.meeting else { return }
        meeting.templateId = templateId
        perform { try db.updateMeeting(meeting) }
    }

    /// 移到垃圾桶（R9.3）
    func moveToTrash(_ id: Int64) {
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
        guard let meeting = try? db.meeting(id) else { return }
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
        perform { try db.enqueue(id, kind: job.kind, payload: job.payload) }
        runner.wake()
    }

    func retranscribe() {
        guard let id = selection, detail?.meeting.hasAudio == true else { return }
        perform { try db.enqueue(id, kind: .transcribe) }
        runner.wake()
    }

    func resummarize() {
        guard let id = selection else { return }
        guard AppSettings.provider != .none else {
            errorMessage = String(localized: "整理工具設定為「不整理」，請先到設定改成 Claude、Codex 或 API")
            return
        }
        perform { try db.enqueue(id, kind: .summarize) }
        runner.wake()
    }

    func undoNotes() {
        guard let id = selection else { return }
        perform { _ = try db.undoNotes(id) }
    }

    /// 手動編輯會議紀錄（R6.6）
    func saveEditedNotes(_ notes: MeetingNotes) {
        guard let id = selection, notes != detail?.notes else { return }
        perform { try db.saveNotes(id, notes: notes, by: nil, reason: String(localized: "手動編輯")) }
    }

    func submitReview(answers: [Int: String], dismissed: Set<Int>) {
        guard let id = selection, let notes = detail?.notes else { return }
        let answered = answers.compactMap { index, text -> (Int, ReviewAnswer)? in
            let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !trimmed.isEmpty, !dismissed.contains(index), notes.reviewItems.indices.contains(index) else { return nil }
            let item = notes.reviewItems[index]
            return (index, ReviewAnswer(time: item.time, question: item.question, answer: trimmed))
        }.sorted { $0.0 < $1.0 }.map(\.1)
        if !answered.isEmpty && AppSettings.provider == .none {
            errorMessage = String(localized: "整理工具設定為「不整理」，沒辦法依回答修正。請先到設定改成 Claude、Codex 或 API")
            return
        }
        perform {
            if !dismissed.isEmpty { try db.dismissReviewItems(id, indexes: dismissed) }
            if !answered.isEmpty {
                let payload = String(decoding: try JSONEncoder().encode(RevisePayload(answers: answered)), as: UTF8.self)
                try db.enqueue(id, kind: .revise, payload: payload)
            }
        }
        runner.wake()
    }

    func updateSegment(_ segmentId: Int64, text: String) {
        perform { try db.updateSegmentText(segmentId, text: text) }
    }

    // MARK: 逐字稿校正（design §17.4）

    /// 手動檢查可能聽錯的字（「更多」選單）
    func checkTranscript() {
        guard let id = selection else { return }
        guard AppSettings.provider != .none else {
            errorMessage = String(localized: "整理工具設定為「不整理」，請先到設定改成 Claude、Codex 或 API")
            return
        }
        perform { try db.enqueue(id, kind: .correct) }
        runner.wake()
    }

    func acceptCorrection(_ id: Int64) {
        perform {
            if try !db.acceptCorrection(id) {
                noticeMessage = String(localized: "這一段已經改過，找不到建議要改的文字")
            }
        }
    }

    func rejectCorrection(_ id: Int64) {
        perform { try db.rejectCorrection(id) }
    }

    func revertCorrection(_ id: Int64) {
        perform {
            if try !db.revertCorrection(id) {
                noticeMessage = String(localized: "這一段已經改過，沒辦法還原")
            }
        }
    }

    func acceptAllCorrections() {
        guard let pending = detail?.corrections.filter({ $0.status == "pending" }) else { return }
        perform {
            let failed = try pending.compactMap(\.id).filter { try !db.acceptCorrection($0) }.count
            if failed > 0 { noticeMessage = String(localized: "有 \(failed) 則建議的段落已經改過，沒有套用") }
        }
    }

    func rejectAllCorrections() {
        guard let pending = detail?.corrections.filter({ $0.status == "pending" }) else { return }
        perform { for id in pending.compactMap(\.id) { try db.rejectCorrection(id) } }
    }

    func renameSpeaker(_ idx: Int, to name: String) {
        guard let id = selection else { return }
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        perform { try db.renameSpeaker(id, idx: idx, name: trimmed.isEmpty ? String(localized: "說話者 \(idx)") : trimmed) }
    }

    func updateUserNotes(_ text: String) {
        guard let id = selection ?? recorder.meetingId else { return }
        perform { try db.updateUserNotes(id, text) }
    }

    func addBookmark(at time: Double, meetingId: Int64? = nil, label: String = "") {
        guard let id = meetingId ?? selection else { return }
        perform { try db.addBookmark(id, time: time, label: label) }
    }

    func deleteBookmark(_ id: Int64) {
        perform { try db.deleteBookmark(id) }
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

    func importPackage(_ url: URL) {
        do {
            let meeting = try MeetingPackage.importPackage(url, db: db, folder: currentFolder)
            reload()
            showTrash = false
            selection = meeting.id
            noticeMessage = String(localized: "已匯入「\(meeting.displayTitle)」")
        } catch {
            errorMessage = error.localizedDescription
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
            if AppSettings.autoSummarize && AppSettings.provider != .none { try db.enqueue(id, kind: .summarize) }
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
                tab = .notes
            } else if let meeting = try? db.insertMeeting(Meeting(
                meetingDate: DateText.string(Date()), sourceFilename: url.lastPathComponent, userNotes: text, source: "transcript",
                folder: currentFolder, language: AppSettings.defaultMeetingLanguage.rawValue),
                audioExtension: nil) {
                perform { try db.updateUserNotes(meeting.id!, text) }
                selection = meeting.id
                tab = .notes
            }
        }
    }

    // MARK: 匯出（R7）

    func exportBundle() -> ExportBundle? {
        guard let d = detail else { return nil }
        return ExportBundle(meeting: d.meeting, segments: d.segments, speakers: d.speakers, bookmarks: d.bookmarks, notes: d.notes)
    }

    /// 寫出檔案；成功回傳檔案位置
    func export(_ item: ExportItem, format: String, times: Bool, speakers: Bool, to url: URL) async throws {
        guard let b = exportBundle() else { return }
        switch item {
        case .notes:
            switch format {
            case "pdf": try HTML.pdf(NotesExporter.html(b), to: url)
            case "docx": try HTML.docx(NotesExporter.html(b)).write(to: url)
            default: try NotesExporter.markdown(b).write(to: url, atomically: true, encoding: .utf8)
            }
        case .transcript:
            if format == "docx" { try HTML.docx(TranscriptExporter.html(b, times: times, speakers: speakers)).write(to: url) }
            else { try TranscriptExporter.text(b, format: format, times: times, speakers: speakers).write(to: url, atomically: true, encoding: .utf8) }
        case .audio:
            try await AudioExporter.export(b, format: format, to: url)
        case .package:
            try MeetingPackage.export(b, to: url)
        }
    }

    func suggestedFilename(_ item: ExportItem, format: String) -> String {
        guard let m = detail?.meeting else { return String(localized: "會議.\(format)") }
        let base = "\(m.meetingDate) \(m.displayTitle)".replacingOccurrences(of: "/", with: "／").replacingOccurrences(of: ":", with: "：")  // l10n-skip
        switch item {
        case .transcript: return String(localized: "\(base)（逐字稿）.\(format)")
        case .audio: return "\(base).\(format)"
        default: return "\(base).\(format)"
        }
    }

    enum CopyStyle { case plain, markdown, rich }

    func copyNotes(_ style: CopyStyle) {
        guard let b = exportBundle() else { return }
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
            highlightSegment = hit.ref
            if let segment = detail?.segments.first(where: { $0.id == hit.ref }) { player.seek(to: segment.start, play: false) }
        case .notes, .title: tab = .summary
        case .userNotes: tab = .notes
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

    func perform(_ work: () throws -> Void) {
        do { try work() } catch { errorMessage = error.localizedDescription }
        reload()
    }

    private func jobFinished(_ job: Job, error: Error?) {
        guard let meeting = try? db.meeting(job.meetingId) else { return }
        notifier.jobFinished(job, meeting: meeting, error: error)
    }
}
