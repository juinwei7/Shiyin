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
    static let liveTranscriptionKey = "liveTranscription"
    static let recordSourceKey = "recordSource"          // mic / system / both
    static let inputDeviceKey = "inputDeviceUID"
    static let defaultTemplateKey = "defaultTemplate"
    static let globalHotKeyKey = "globalHotKey"
    static let transcriptionEngineKey = "transcriptionEngine"   // breeze / system
    static let defaultMeetingLanguageKey = "defaultMeetingLanguage"   // zh / en

    private static var d: UserDefaults { .standard }

    static var provider: LLMProvider { LLMProvider(rawValue: d.string(forKey: providerKey) ?? "") ?? .claude }
    static var model: String { d.string(forKey: provider.modelKey) ?? "" }
    static var autoSummarize: Bool { d.object(forKey: autoSummarizeKey) as? Bool ?? true }
    static var compressAudio: Bool { d.object(forKey: compressAudioKey) as? Bool ?? true }
    static var diarize: Bool { d.object(forKey: diarizeKey) as? Bool ?? true }
    static var liveTranscription: Bool { d.object(forKey: liveTranscriptionKey) as? Bool ?? true }
    static var recordSource: String { d.string(forKey: recordSourceKey) ?? "mic" }
    static var inputDeviceUID: String { d.string(forKey: inputDeviceKey) ?? "" }
    static var defaultTemplate: String { d.string(forKey: defaultTemplateKey) ?? "general" }
    static var globalHotKey: Bool { d.object(forKey: globalHotKeyKey) as? Bool ?? true }
    /// 正式轉錄用 macOS 內建辨識（design §6.2.1）
    static var useSystemTranscriber: Bool { d.string(forKey: transcriptionEngineKey) == "system" }
    /// 新會議的會議語言（design §16.3）；沒設定時跟著介面語言
    static var defaultMeetingLanguage: MeetingLanguage {
        MeetingLanguage(rawValue: d.string(forKey: defaultMeetingLanguageKey) ?? "") ?? .interface
    }
}

struct RevisePayload: Codable {
    var answers: [ReviewAnswer]
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

    init(db: AppDatabase) {
        self.db = db
    }

    func start() {
        do { try db.requeueRunning() } catch { log.error("requeue failed: \(error.localizedDescription)") }
        for lane in [JobLane.transcribe, .llm] {
            Task { await loop(lane) }
        }
    }

    func wake() {
        sleepers.values.forEach { $0.cancel() }
    }

    var isBusy: Bool { !running.isEmpty }

    func runningJob(for meetingId: Int64) -> Running? {
        running.values.first { $0.job.meetingId == meetingId && $0.job.kind != .compress }
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

    /// 結束 app 時：只停止執行中的工作，不改資料庫狀態，下次開啟會從頭重跑
    func terminateAll() {
        running.values.forEach { $0.cancelAction?() }
    }

    private func loop(_ lane: JobLane) async {
        while !Task.isCancelled {
            let job: Job?
            let modelReady = AppSettings.useSystemTranscriber || FileManager.default.fileExists(atPath: ModelStore.breezePath.path)
            do { job = try db.claimNext(lane: lane, includeTranscribe: modelReady) } catch {
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
        do {
            switch job.kind {
            case .transcribe: try await transcribe(job, lane: lane)
            case .summarize: try await summarize(job, lane: lane, answers: nil)
            case .revise:
                let payload = try JSONDecoder().decode(RevisePayload.self, from: Data((job.payload ?? "{}").utf8))
                try await summarize(job, lane: lane, answers: payload.answers)
            case .compress: try await compress(job)
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
                    try db.finish(jobId, status: .failed, error: failure.localizedDescription)
                    log.error("job \(jobId) failed: \(failure.localizedDescription, privacy: .public)")
                } else {
                    try db.finish(jobId, status: .done)
                    log.info("job \(jobId) done")
                }
            } catch {
                log.error("finish failed: \(error.localizedDescription)")
            }
            if job.kind != .compress { onFinished?(job, failure) }
        }
        onChange?()
    }

    private func isStillRunning(_ job: Job) -> Bool { (try? db.jobStatus(job.id!)) == .running }

    // MARK: 轉錄（design §6）

    private func transcribe(_ job: Job, lane: JobLane) async throws {
        guard let meeting = try db.meeting(job.meetingId) else { return }
        let useSystem = AppSettings.useSystemTranscriber
        let language = meeting.meetingLanguage
        guard useSystem || FileManager.default.fileExists(atPath: ModelStore.breezePath.path) else { throw WhisperError.modelMissing }
        running[lane]?.stage = String(localized: "讀取錄音")
        let samples = try await AudioDecoder.decode(meeting.audioURL)
        guard isStillRunning(job) else { return }
        let duration = Double(samples.count) / AudioDecoder.sampleRate
        let diarize = AppSettings.diarize && duration > 5
        let whisperShare = diarize ? 0.85 : 1.0

        running[lane]?.stage = useSystem ? String(localized: "轉錄（macOS 內建）") : String(localized: "轉錄")
        running[lane]?.progress = 0
        let transcribed: [TranscribedSegment]
        if useSystem {
            let task = Task { [weak self] in
                try await SystemTranscriber.transcribe(samples: samples, language: language) { value in
                    Task { @MainActor in self?.running[lane]?.progress = value * whisperShare }
                }
            }
            running[lane]?.cancelAction = { task.cancel() }
            transcribed = try await task.value
        } else {
            let control = WhisperEngine.Control { [weak self] value in
                Task { @MainActor in self?.running[lane]?.progress = value * whisperShare }
            }
            running[lane]?.cancelAction = { control.cancel() }
            let prompt = WhisperEngine.prompt(glossary: try db.glossary().map(\.term), language: language)
            let modelPath = ModelStore.breezePath.path
            transcribed = try await Task.detached(priority: .userInitiated) {
                try WhisperEngine.transcribe(samples: samples, modelPath: modelPath, prompt: prompt,
                                             language: language.whisperCode, control: control)
            }.value
        }
        guard isStillRunning(job) else { return }

        var speakers: [Int?] = transcribed.map { _ in nil }
        if diarize {
            running[lane]?.stage = String(localized: "辨識說話者")
            do {
                let turns = try await Diarizer.diarize(samples: samples) { [weak self] value in
                    Task { @MainActor in self?.running[lane]?.progress = whisperShare + value * (1 - whisperShare) }
                }
                speakers = Diarizer.assign(turns, to: transcribed)
            } catch {
                // 說話者辨識失敗不影響逐字稿
                log.error("diarization failed: \(error.localizedDescription, privacy: .public)")
            }
        }
        guard isStillRunning(job) else { return }
        let segments = transcribed.enumerated().map { i, s in
            Segment(meetingId: job.meetingId, seq: i, start: s.start, end: s.end, text: s.text,
                    lowConfidence: s.lowConfidence, speaker: speakers[i], kind: "final")
        }
        try db.saveTranscript(job.meetingId, segments: segments, duration: Int(duration.rounded()),
                              speakerCount: speakers.compactMap { $0 }.max() ?? 0)
        if AppSettings.compressAudio && meeting.audioURL.pathExtension.lowercased() == "wav" {
            try db.enqueue(job.meetingId, kind: .compress)
        }
        if AppSettings.autoSummarize && AppSettings.provider != .none {
            try db.enqueue(job.meetingId, kind: .summarize)
        }
        wake()
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

    private func summarize(_ job: Job, lane: JobLane, answers: [ReviewAnswer]?) async throws {
        guard let meeting = try db.meeting(job.meetingId) else { return }
        let provider = AppSettings.provider
        guard provider != .none else { throw LLMError.failed(String(localized: "整理工具設定為「不整理」，請到設定改成 Claude、Codex 或 API")) }
        running[lane]?.provider = provider
        running[lane]?.stage = answers == nil ? String(localized: "整理") : String(localized: "校稿")
        let segments = try db.segments(job.meetingId)
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
            guard let notes = meeting.decodedNotes else { return }
            prompt = NotesPrompt.revise(context, notes: notes, answers: answers)
        } else {
            prompt = NotesPrompt.summarize(context)
        }
        let notes = try await LLMClient.generate(
            provider: provider, model: AppSettings.model, system: NotesPrompt.system(meeting.meetingLanguage), prompt: prompt,
            onStart: { [weak self] process in self?.running[lane]?.cancelAction = { process.terminate() } })
        guard isStillRunning(job) else { return }
        try db.saveNotes(job.meetingId, notes: notes, by: provider.rawValue, reason: answers == nil ? String(localized: "重新整理") : String(localized: "校稿"))
    }
}
