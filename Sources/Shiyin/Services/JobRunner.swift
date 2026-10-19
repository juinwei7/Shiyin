import AVFoundation
import Foundation
import Observation
import os

let log = Logger(subsystem: "com.weiwei.shiyin", category: "app")

/// 設定（UserDefaults）。介面用 @AppStorage 讀寫同樣的 key。
enum AppSettings {
    static let providerKey = "llmProvider"
    static let autoSummarizeKey = "autoSummarize"
    static let compressAudioKey = "compressAudio"
    static let diarizeKey = "diarize"
    static let splitChannelsKey = "splitChannels"
    static let correctTranscriptKey = "correctTranscript"
    static let liveTranscriptionKey = "liveTranscription"
    static let autoDetectLanguageKey = "autoDetectLanguage"
    static let recordSourceKey = "recordSource"          // mic / system / both
    static let inputDeviceKey = "inputDeviceUID"
    static let defaultTemplateKey = "defaultTemplate"
    static let globalHotKeyKey = "globalHotKey"
    static let transcriptionEngineKey = "transcriptionEngine"   // breeze / system
    static let defaultMeetingLanguageKey = "defaultMeetingLanguage"   // zh / en / ja

    private static var d: UserDefaults { .standard }

    /// 開關類的設定。不能用 `object(forKey:) as? Bool`：啟動參數（`-autoSummarize NO`）讀進來是字串 "NO"，
    /// 轉不成 Bool 會落回預設值；`bool(forKey:)` 認得字串的 YES、NO
    private static func flag(_ key: String, default value: Bool) -> Bool {
        d.object(forKey: key) == nil ? value : d.bool(forKey: key)
    }

    #if DEBUG
    /// shiyinctl llmprovider：只在記憶體換整理工具，不改跟使用者共用的設定（design §19.5）
    nonisolated(unsafe) static var debugProvider: LLMProvider?
    nonisolated(unsafe) static var debugModel: String?
    #endif

    static var provider: LLMProvider {
        #if DEBUG
        if let debugProvider { return debugProvider }
        #endif
        return LLMProvider(rawValue: d.string(forKey: providerKey) ?? "") ?? .claude
    }
    static var model: String {
        #if DEBUG
        if let debugModel { return debugModel }
        #endif
        return d.string(forKey: provider.modelKey) ?? ""
    }
    static var autoSummarize: Bool { flag(autoSummarizeKey, default: true) }
    static var compressAudio: Bool { flag(compressAudioKey, default: true) }
    static var diarize: Bool { flag(diarizeKey, default: true) }
    /// 線上會議的麥克風、電腦聲音分開轉錄（design §17.3）；還在評估，設定裡沒有開關，測試時用 defaults 打開
    static var splitChannels: Bool { flag(splitChannelsKey, default: false) }
    /// 轉錄完先找出可能聽錯的字（design §17.4）；評估後預設開啟（§17.6）
    static var correctTranscript: Bool { flag(correctTranscriptKey, default: true) }
    static var liveTranscription: Bool { flag(liveTranscriptionKey, default: true) }
    /// 錄音開始後自動偵測主要語言（design §18.8、R16）
    static var autoDetectLanguage: Bool { flag(autoDetectLanguageKey, default: true) }
    static var recordSource: String { d.string(forKey: recordSourceKey) ?? "mic" }
    static var inputDeviceUID: String { d.string(forKey: inputDeviceKey) ?? "" }
    static var defaultTemplate: String { d.string(forKey: defaultTemplateKey) ?? "general" }
    static var globalHotKey: Bool { flag(globalHotKeyKey, default: true) }
    /// 正式轉錄用 macOS 內建辨識（design §6.2.1）
    static var useSystemTranscriber: Bool { d.string(forKey: transcriptionEngineKey) == "system" }
    /// 新會議的會議語言（design §16.3）；沒設定時跟著介面語言
    static var defaultMeetingLanguage: MeetingLanguage {
        MeetingLanguage(rawValue: d.string(forKey: defaultMeetingLanguageKey) ?? "") ?? .systemDefault
    }
}

struct RevisePayload: Codable {
    var answers: [ReviewAnswer]
}

/// 整理工作的 payload：draft 是用即時逐字稿整理草稿（design §21.3）
struct SummarizePayload: Codable {
    var draft = false
}

/// 工作佇列：轉錄線、整理線各跑一個迴圈，互不等待（沿用 v2 §4）
@MainActor
@Observable
final class JobRunner {
    struct Running {
        var job: Job
        var startedAt = Date()
        var stage = ""                 // 目前階段，顯示用
        var progress: Double?
        var provider: LLMProvider?
        @ObservationIgnored var cancelAction: (() -> Void)?

        /// 預估剩餘秒數：已經過時間 ÷ 目前進度
        var remaining: TimeInterval? {
            guard let progress, progress > 0.03 else { return nil }
            let elapsed = Date().timeIntervalSince(startedAt)
            return elapsed / progress - elapsed
        }
    }

    private(set) var running: [JobLane: Running] = [:]
    @ObservationIgnored private let db: AppDatabase
    @ObservationIgnored private var sleepers: [JobLane: Task<Void, Never>] = [:]
    @ObservationIgnored var onChange: (() -> Void)?
    @ObservationIgnored var onFinished: ((Job, Error?) -> Void)?
    @ObservationIgnored var onAudioReplaced: ((Int64) -> Void)?
    /// 正式版取代了使用者改過的草稿（design §21.3）
    @ObservationIgnored var onEditedDraftReplaced: ((Int64) -> Void)?

    init(db: AppDatabase) {
        self.db = db
    }

    func start() {
        do { try db.requeueRunning() } catch { log.error("requeue failed: \(error.localizedDescription)") }
        for lane in JobLane.allCases {
            Task { await loop(lane) }
        }
    }

    func wake() {
        sleepers.values.forEach { $0.cancel() }
    }

    var isBusy: Bool { !running.isEmpty }

    /// 畫面上顯示的執行中工作：轉錄優先，其次是整理（使用者在等的），最後才是輔助線
    func runningJob(for meetingId: Int64) -> Running? {
        JobLane.allCases.lazy.compactMap { self.running[$0] }
            .first { $0.job.meetingId == meetingId && $0.job.kind != .compress }
    }

    func cancel(meetingId: Int64) {
        do {
            let ids = try db.cancelJobs(meetingId)
            for state in running.values where ids.contains(state.job.id ?? -1) {
                state.cancelAction?()
            }
        } catch {
            log.error("cancel failed: \(error.localizedDescription)")
        }
        onChange?()
    }

    /// 結束 app 時：執行中的工作先改回排隊（不算意外結束，design §20.5），再停止，下次開啟會從頭重跑
    func terminateAll() {
        do { try db.releaseRunning() } catch { log.error("release running jobs failed: \(error.localizedDescription)") }
        running.values.forEach { $0.cancelAction?() }
    }

    private func loop(_ lane: JobLane) async {
        while !Task.isCancelled {
            let job: Job?
            // 轉錄只取模型已經下載的語言；日文會議第一次要轉錄時才自動下載日文模型（design §18.2）
            let ready = Set(MeetingLanguage.allCases.filter {
                AppSettings.useSystemTranscriber
                    || FileManager.default.fileExists(atPath: ModelStore.path(ModelStore.transcription(for: $0)).path)
            })
            if lane == .transcribe && !ready.contains(.ja) && (try? db.hasQueuedTranscribe(language: .ja)) == true {
                AppModel.shared.models.autoStart(.japanese)
            }
            do { job = try db.claimNext(lane: lane, transcribeLanguages: ready) } catch {
                log.error("claim failed: \(error.localizedDescription)")
                job = nil
            }
            if let job {
                await execute(job, lane: lane)
                continue
            }
            let sleeper = Task<Void, Never> { try? await Task.sleep(for: .seconds(2)) }
            sleepers[lane] = sleeper
            await sleeper.value
            sleepers[lane] = nil
        }
    }

    private func execute(_ job: Job, lane: JobLane) async {
        running[lane] = Running(job: job)
        let activity = ProcessInfo.processInfo.beginActivity(
            options: [.idleSystemSleepDisabled, .userInitiated], reason: String(localized: "拾音正在轉錄或整理會議紀錄"))
        onChange?()
        log.info("job \(job.id ?? 0) \(job.kind.rawValue) started")
        var failure: Error?
        var notify = true
        do {
            switch job.kind {
            case .transcribe: try await transcribe(job, lane: lane)
            case .summarize:
                let payload = (try? JSONDecoder().decode(SummarizePayload.self, from: Data((job.payload ?? "{}").utf8))) ?? SummarizePayload()
                notify = try await summarize(job, lane: lane, answers: nil, draft: payload.draft)
            case .revise:
                let payload = try JSONDecoder().decode(RevisePayload.self, from: Data((job.payload ?? "{}").utf8))
                notify = try await summarize(job, lane: lane, answers: payload.answers)
            case .compress: try await compress(job)
            case .correct: await correct(job, lane: lane)
            }
        } catch {
            failure = error
        }
        ProcessInfo.processInfo.endActivity(activity)
        running[lane] = nil

        let jobId = job.id!
        if (try? db.jobStatus(jobId)) == .running {
            do {
                if let failure {
                    try db.finish(jobId, status: .failed, error: failure.localizedDescription, errorKind: failure.logSummary)
                    log.error("job \(jobId) failed: \(failure.logSummary, privacy: .public)")
                } else {
                    try db.finish(jobId, status: .done)
                    log.info("job \(jobId) done")
                }
            } catch {
                log.error("finish failed: \(error.localizedDescription)")
            }
            // 壓縮、逐字稿校正是背景的輔助工作，不發通知；有草稿時正式版不再發（design §21.3）
            if job.kind != .compress && job.kind != .correct && (notify || failure != nil) { onFinished?(job, failure) }
        }
        onChange?()
    }

    private func isStillRunning(_ job: Job) -> Bool { (try? db.jobStatus(job.id!)) == .running }

    /// 呼叫整理工具，按「取消」時 CLI 結束子程序、API 取消請求（design §21.14、R20.5）
    private func callLLM<T>(_ lane: JobLane, _ work: @escaping @MainActor (_ onStart: @escaping (Subprocess) -> Void) async throws -> T) async throws -> T {
        var process: Subprocess?
        let task = Task { @MainActor in try await work { process = $0 } }
        running[lane]?.cancelAction = { process?.terminate(); task.cancel() }
        return try await task.value
    }

    // MARK: 轉錄（design §6）

    private func transcribe(_ job: Job, lane: JobLane) async throws {
        guard let meeting = try db.meeting(job.meetingId) else { return }
        var pipeline = TranscriptionPipeline(options: .current(language: meeting.meetingLanguage,
                                                                glossary: try db.glossary().map(\.term),
                                                                channels: meeting.channels))
        pipeline.stage = { [weak self] in self?.running[lane]?.stage = $0 }
        pipeline.progress = { [weak self] value in Task { @MainActor in self?.running[lane]?.progress = value } }
        pipeline.setCancel = { [weak self] in self?.running[lane]?.cancelAction = $0 }
        pipeline.shouldContinue = { [weak self] in self?.isStillRunning(job) ?? false }
        guard let result = try await pipeline.run(meeting.audioURL) else { return }
        let (transcribed, speakers, duration) = (result.segments, result.speakers, result.duration)
        let segments = transcribed.enumerated().map { i, s in
            Segment(meetingId: job.meetingId, seq: i, start: s.start, end: s.end, text: s.text,
                    lowConfidence: s.lowConfidence, speaker: speakers[i], kind: "final", channel: result.channels[i])
        }
        try db.saveTranscript(job.meetingId, segments: segments, duration: Int(duration.rounded()),
                              speakerCount: speakers.compactMap { $0 }.max() ?? 0)
        if AppSettings.compressAudio && meeting.audioURL.pathExtension.lowercased() == "wav" {
            try db.enqueue(job.meetingId, kind: .compress)
        }
        // 校正在輔助線、整理在整理線，兩個同時開始（design §21.2、R20.1）
        if AppSettings.correctTranscript && AppSettings.provider != .none {
            try db.enqueue(job.meetingId, kind: .correct)
        }
        if AppSettings.autoSummarize && AppSettings.provider != .none {
            try db.enqueue(job.meetingId, kind: .summarize)
        }
        wake()
    }

    // MARK: 逐字稿校正（design §17.4）

    /// 找出可能聽錯的字，存成待確認的建議。這是輔助功能：失敗只記 log，不影響整理會議紀錄
    private func correct(_ job: Job, lane: JobLane) async {
        do {
            guard let meeting = try db.meeting(job.meetingId) else { return }
            let provider = AppSettings.provider
            guard provider != .none else { return }
            let segments = try db.segments(job.meetingId)
            guard !segments.isEmpty else { return }
            running[lane]?.provider = provider
            running[lane]?.stage = String(localized: "檢查可能聽錯的字")
            let language = meeting.meetingLanguage
            let names = Dictionary(uniqueKeysWithValues: try db.speakers(job.meetingId).map { ($0.idx, $0.name) })
            let speakerName = { (idx: Int?) -> String? in
                idx.map { names[$0] ?? language.text(zh: "說話者 \($0)", en: "Speaker \($0)", ja: "話者 \($0)") }
            }
            let context = TranscriptCorrection.Context(
                language: language, title: meeting.title, participants: meeting.participantList,
                speakerNames: Array(Set(names.values)).sorted(), glossary: try db.glossary().map(\.term),
                segments: segments.map { ($0.text, speakerName($0.speaker)) })
            let suggestions = try await callLLM(lane) { onStart in
                try await TranscriptCorrection.suggest(
                    context, provider: provider, model: AppSettings.model, onStart: onStart,
                    onProgress: { [weak self] value in self?.running[lane]?.progress = value })
            }
            guard isStillRunning(job) else { return }
            try db.saveCorrections(job.meetingId, suggestions.compactMap { s in
                segments[s.segment].id.map {
                    Correction(meetingId: job.meetingId, segmentId: $0, original: s.original, suggestion: s.suggestion, reason: s.reason)
                }
            })
        } catch {
            log.error("transcript correction failed: \(error.logSummary, privacy: .public)")
        }
    }

    // MARK: 壓縮（design §6.4）

    private func compress(_ job: Job) async throws {
        guard let meeting = try db.meeting(job.meetingId), meeting.hasAudio,
              meeting.audioURL.pathExtension.lowercased() == "wav" else { return }
        let output = Paths.audioDir.appendingPathComponent("\(job.meetingId).m4a")
        try? FileManager.default.removeItem(at: output)
        guard let export = AVAssetExportSession(asset: AVURLAsset(url: meeting.audioURL), presetName: AVAssetExportPresetAppleM4A)
        else { throw LLMError.failed(String(localized: "無法建立壓縮工作")) }
        try await export.export(to: output, as: .m4a)
        try db.setAudioFile(job.meetingId, output.lastPathComponent)
        try? FileManager.default.removeItem(at: meeting.audioURL)
        onAudioReplaced?(job.meetingId)
    }

    // MARK: 整理（design §8）

    /// 回傳要不要發通知：草稿沒做（正式逐字稿已經好了）、正式版取代了草稿時不發
    private func summarize(_ job: Job, lane: JobLane, answers: [ReviewAnswer]?, draft: Bool = false) async throws -> Bool {
        guard let meeting = try db.meeting(job.meetingId) else { return false }
        // 草稿（design §21.3）：正式逐字稿已經好了就不做（短的會議轉錄比較快）
        if try draft && !db.segments(job.meetingId).isEmpty { return false }
        let provider = AppSettings.provider
        guard provider != .none else { throw LLMError.failed(String(localized: "整理工具設定為「不整理」，請到設定改成 Claude、Codex 或 API")) }
        running[lane]?.provider = provider
        running[lane]?.stage = draft ? String(localized: "整理草稿") : answers == nil ? String(localized: "整理") : String(localized: "校稿")
        let segments = try db.segments(job.meetingId, kind: draft ? "live" : "final")
        guard !segments.isEmpty || !meeting.userNotes.isEmpty else { throw LLMError.failed(String(localized: "還沒有逐字稿，請先完成轉錄")) }
        let templates = try db.templates()
        let template = templates.first { $0.id == meeting.templateId } ?? templates.first { $0.id == "general" }
        let context = NotesContext(
            meeting: meeting, segments: segments,
            speakers: Dictionary(uniqueKeysWithValues: try db.speakers(job.meetingId).map { ($0.idx, $0.name) }),
            bookmarks: try db.bookmarks(job.meetingId), glossary: try db.glossary().map(\.term),
            template: template?.definition(for: meeting.meetingLanguage) ?? TemplateDefinition.decode("{}"))
        let prompt: String
        if let answers {
            guard let notes = meeting.decodedNotes else { return false }
            prompt = NotesPrompt.revise(context, notes: notes, answers: answers)
        } else {
            prompt = NotesPrompt.summarize(context)
        }
        let system = NotesPrompt.system(meeting.meetingLanguage)
        let notes = try await callLLM(lane) { onStart in
            try await LLMClient.generate(provider: provider, model: AppSettings.model, system: system, prompt: prompt, onStart: onStart)
        }
        guard isStillRunning(job) else { return false }
        if draft {
            // 整理草稿的期間正式逐字稿好了、或已經有會議紀錄：丟掉草稿
            guard try db.segments(job.meetingId).isEmpty, try db.meeting(job.meetingId)?.notes == nil else { return false }
            try db.saveNotes(job.meetingId, notes: notes, by: provider.rawValue, reason: String(localized: "草稿"), source: .draft)
            return true
        }
        let hadDraft = try db.meeting(job.meetingId)?.notesDraft ?? 0
        let replacedEdited = try db.saveNotes(job.meetingId, notes: notes, by: provider.rawValue,
                                              reason: answers == nil ? String(localized: "重新整理") : String(localized: "校稿"),
                                              source: answers == nil ? .generated : .revised)
        if replacedEdited { onEditedDraftReplaced?(job.meetingId) }
        return answers != nil || hadDraft == 0
    }
}
