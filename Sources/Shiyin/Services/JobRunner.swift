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
    static let transcriptionEngineKey = "transcriptionEngine"   // breeze / qwen / system（沒有值 = breeze，design §32.6）
    static let defaultMeetingLanguageKey = "defaultMeetingLanguage"   // zh / en / ja
    static let voiceprintsKey = "voiceprints"
    static let meetingDetectionKey = "meetingDetection"
    static let bookmarkHotKeyKey = "bookmarkHotKey"

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

    /// 使用者選過的整理工具；沒選過（或不是認得的值）是 nil（R35）
    static var chosenProvider: LLMProvider? { LLMProvider(rawValue: d.string(forKey: providerKey) ?? "") }

    /// 沒選過時自動用這台 Mac 上有的 CLI（先 claude 再 codex），都沒有就先不整理（R35.1、design §19.6）。
    /// 每次都重新找：之後裝了 CLI 就自動用上
    static var detectedProvider: LLMProvider {
        if Paths.findTool("claude") != nil { return .claude }
        if Paths.findTool("codex") != nil { return .codex }
        return .none
    }

    static var provider: LLMProvider {
        #if DEBUG
        if let debugProvider { return debugProvider }
        #endif
        return chosenProvider ?? detectedProvider
    }

    /// 還沒選過整理工具、也找不到 CLI：介面說「還沒設定整理工具」，不說「不整理」（R35.2）
    static var needsProviderSetup: Bool {
        #if DEBUG
        if debugProvider != nil { return false }
        #endif
        return chosenProvider == nil && provider == .none
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
    /// 記住說話者的聲音，之後的會議自動帶入名字（design §21.4、R19.3）
    static var voiceprints: Bool { flag(voiceprintsKey, default: true) }
    /// 錄音中按 ⌃⌥⌘B 標記重點（design §21.10、R19.5）
    static var bookmarkHotKey: Bool { flag(bookmarkHotKeyKey, default: true) }
    /// 正式轉錄用 macOS 內建辨識（design §6.2.1）
    static var useSystemTranscriber: Bool { d.string(forKey: transcriptionEngineKey) == "system" }
    /// 正式轉錄引擎（design §32.6）：breeze（預設，聯發科）、qwen（阿里巴巴，可選）、system；不認得的值當 breeze
    static var transcriptionEngine: String {
        let value = d.string(forKey: transcriptionEngineKey) ?? ""
        return ["breeze", "qwen", "system"].contains(value) ? value : "breeze"
    }
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
    /// 重新辨識說話者做完（分出幾位）或失敗（design §23.3）
    @ObservationIgnored var onRediarized: ((Int64, Result<Int, Error>) -> Void)?

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

    /// 畫面上顯示的執行中工作：轉錄優先，其次是整理（使用者在等的）；背景的輔助工作（壓縮、校正、猜說話者）不算
    func runningJob(for meetingId: Int64) -> Running? {
        JobLane.allCases.lazy.compactMap { self.running[$0] }
            .first { $0.job.meetingId == meetingId && !$0.job.kind.isAuxiliary }
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
            // 模型還沒下載好的語言先用 macOS 內建辨識轉錄（design §21.17），模型在背景下載；
            // 日文模型在日文會議第一次要轉錄時才下載（design §18.2）
            if lane == .transcribe && !AppSettings.useSystemTranscriber {
                for language in MeetingLanguage.allCases where !ModelStore.hasTranscriptionModel(for: language)
                    && (try? db.hasQueuedTranscribe(language: language)) == true {
                    AppModel.shared.models.autoStartTranscription(for: language)
                }
            }
            do { job = try db.claimNext(lane: lane) } catch {
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
            case .nameSpeakers: await nameSpeakers(job, lane: lane)
            case .rediarize: try await rediarize(job, lane: lane)
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
                    // 設定問題（沒登入、key 錯誤…）：暫停自動整理，設定改了、登入了、測試成功才再排（R46.3、design §34.4）
                    if job.kind == .summarize || job.kind == .revise, (failure as? LLMError)?.isSetupProblem == true,
                       AppSettings.provider != .none {
                        SummarizerPause.pause(AppSettings.provider, message: failure.localizedDescription)
                        log.info("summarizer paused after setup problem")
                    }
                } else {
                    try db.finish(jobId, status: .done)
                    log.info("job \(jobId) done")
                    if job.kind == .summarize || job.kind == .revise { SummarizerPause.clear() }
                }
                countUsage(job, failure: failure)
            } catch {
                log.error("finish failed: \(error.localizedDescription)")
            }
            // 壓縮、逐字稿校正是背景的輔助工作，不發通知；有草稿時正式版不再發（design §21.3）
            if !job.kind.isAuxiliary && (notify || failure != nil) { onFinished?(job, failure) }
        }
        onChange?()
    }

    private func isStillRunning(_ job: Job) -> Bool { (try? db.jobStatus(job.id!)) == .running }

    /// 匿名使用統計（design §30.1）：只算真的跑完的工作（取消的不算）；轉錄成功在 transcribe 裡算（要用到長度）
    private func countUsage(_ job: Job, failure: Error?) {
        switch (job.kind, failure) {
        case (.transcribe, .some): UsageStats.shared.failed(.transcribe)
        case (.rediarize, .none): UsageStats.shared.rediarized()
        case (.rediarize, .some): UsageStats.shared.failed(.diarize)
        case (.summarize, .none), (.revise, .none): UsageStats.shared.summarized(with: AppSettings.provider)
        case (.summarize, .some(let error)), (.revise, .some(let error)):
            // 種類、整理工具、是不是草稿另外記（R47、design §34.5）
            let draft = job.kind == .summarize
                && ((try? JSONDecoder().decode(SummarizePayload.self, from: Data((job.payload ?? "{}").utf8)))?.draft ?? false)
            UsageStats.shared.summarizeFailed(error, provider: AppSettings.provider, draft: draft)
        default: break
        }
    }

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
        let startedAt = Date()
        var pipeline = TranscriptionPipeline(options: .current(language: meeting.meetingLanguage,
                                                                glossary: try db.glossary().map(\.term),
                                                                channels: meeting.channels, minSpeakers: meeting.minSpeakers))
        pipeline.stage = { [weak self] in self?.running[lane]?.stage = $0 }
        pipeline.progress = { [weak self] value in Task { @MainActor in self?.running[lane]?.progress = value } }
        pipeline.setCancel = { [weak self] in self?.running[lane]?.cancelAction = $0 }
        pipeline.shouldContinue = { [weak self] in self?.isStillRunning(job) ?? false }
        if pipeline.options.engine == .system && !AppSettings.useSystemTranscriber {
            log.info("transcription model not ready, using the system recognizer")
        }
        guard let result = try await pipeline.run(meeting.audioURL) else { return }
        let (transcribed, speakers, duration) = (result.segments, result.speakers, result.duration)
        let segments = transcribed.enumerated().map { i, s in
            Segment(meetingId: job.meetingId, seq: i, start: s.start, end: s.end, text: s.text,
                    lowConfidence: s.lowConfidence, speaker: speakers[i], kind: "final", channel: result.channels[i])
        }
        try db.saveTranscript(job.meetingId, segments: segments, duration: Int(duration.rounded()),
                              speakerCount: speakers.compactMap { $0 }.max() ?? 0, voices: result.voices,
                              transcribedWith: pipeline.options.transcribedWith)
        // 匿名使用統計（design §30.1）：語音長度、實際花的時間、引擎；說話者辨識跟 pipeline 的條件一樣
        UsageStats.shared.transcribed(seconds: duration, wallSeconds: Date().timeIntervalSince(startedAt),
                                      engine: pipeline.options.transcribedWith, language: meeting.meetingLanguage,
                                      imported: meeting.source == "import", diarized: pipeline.options.diarize && duration > 5)
        // 說話者辨識的碰撞率（design §24.3）：可能把人併在一起時，逐字稿分頁會提示
        try db.setSpeakerCheck(job.meetingId, result.diarizationQuality?.mergeRisk)
        // 聲紋比對（design §21.4）：在整理之前，會議紀錄裡就會是名字
        if AppSettings.voiceprints {
            let matched = try db.matchVoiceprints(job.meetingId)
            if matched > 0 { log.info("voiceprints matched \(matched) speakers") }
        }
        if AppSettings.compressAudio && meeting.audioURL.pathExtension.lowercased() == "wav" {
            try db.enqueue(job.meetingId, kind: .compress)
        }
        // 校正在輔助線、整理在整理線，兩個同時開始（design §21.2、R20.1）。
        // 整理工具還沒準備好（沒登入、沒有 key…）就都不排，不會「整理失敗」（R45.4、§34.2）
        let summarizerReady = AppSettings.summarizerReady
        if !summarizerReady { log.info("summarizer not ready, skip auto jobs") }
        if AppSettings.correctTranscript && summarizerReady {
            try db.enqueue(job.meetingId, kind: .correct)
        }
        if AppSettings.autoSummarize && summarizerReady {
            try db.enqueue(job.meetingId, kind: .summarize)
            // 還有預設名稱的說話者就猜名字（design §21.5）：在輔助線、校正後面，不擋整理
            if try db.speakers(job.meetingId).contains(where: { $0.nameSource == "default" }) {
                try db.enqueue(job.meetingId, kind: .nameSpeakers)
            }
        }
        wake()
    }

    // MARK: 重新辨識說話者（design §23.3、R22）

    /// 只重跑說話者辨識，用這場記住的人數；逐字稿文字、校正、重點、會議紀錄都不動
    private func rediarize(_ job: Job, lane: JobLane) async throws {
        guard let meeting = try db.meeting(job.meetingId) else { return }
        do {
            running[lane]?.stage = String(localized: "重新辨識說話者")
            let samples = try await AudioDecoder.decode(meeting.audioURL)
            guard isStillRunning(job) else { return }
            let report: @Sendable (Double) -> Void = { [weak self] value in Task { @MainActor in self?.running[lane]?.progress = value } }
            let minSpeakers = meeting.minSpeakers
            let task = Task { try await Diarizer.diarize(samples: samples, minSpeakers: minSpeakers, onProgress: report) }
            running[lane]?.cancelAction = { task.cancel() }
            let output = try await task.value
            guard isStillRunning(job) else { return }

            let segments = try db.segments(job.meetingId)
            let transcribed = segments.map { TranscribedSegment(start: $0.start, end: $0.end, text: $0.text, lowConfidence: $0.lowConfidence) }
            var (speakers, numbering) = Diarizer.assignNumbering(output.turns, to: transcribed)
            // 短句再跟每位說話者比一次（R34、design §6.3.2）；使用者改過的句子下面另外處理，不用查
            let locked = Set(segments.indices.filter { segments[$0].speakerManual })
            if let refined = try? await SpeakerRecheck.refine(transcribed, speakers: speakers, samples: samples, locked: locked) { speakers = refined }
            let source = meeting.channels == "both" ? "mix" : meeting.channels
            var voices: [Int: SpeakerVoice] = [:]
            for (raw, number) in numbering {
                if let embedding = output.voiceprints[raw] {
                    voices[number] = SpeakerVoice(embedding: embedding, seconds: output.speechSeconds[raw] ?? 0, source: source)
                }
            }
            let old = try db.speakers(job.meetingId)
            // 使用者改過說話者的句子（design §24.2）不照新的結果，跟著原本那位說話者對應到的新說話者
            var assigned = speakers
            var inherited = Self.inheritNames(old: old, segments: segments.filter { !$0.speakerManual },
                                              newSpeakers: zip(segments, speakers).filter { !$0.0.speakerManual }.map(\.1))
            let mapping = Self.oldToNew(segments: segments, newSpeakers: speakers)
            var extra: [Int: Int] = [:]
            var next = (speakers.compactMap { $0 }.max() ?? 0) + 1
            for (i, segment) in segments.enumerated() where segment.speakerManual {
                guard let o = segment.speaker else { continue }
                if let n = mapping[o] {
                    assigned[i] = n
                } else {
                    if extra[o] == nil {
                        extra[o] = next
                        // 預設名稱不沿用（編號變了，用新編號的預設名稱）
                        if let speaker = old.first(where: { $0.idx == o }), speaker.nameSource != "default" {
                            inherited[next] = (speaker.name, speaker.nameSource, speaker.personId)
                        }
                        next += 1
                    }
                    assigned[i] = extra[o]
                }
            }
            try db.reassignSpeakers(job.meetingId, segmentSpeakers: zip(segments, assigned).compactMap { s, n in s.id.map { ($0, n) } },
                                    voices: voices, inherited: inherited)
            try db.setSpeakerCheck(job.meetingId, output.quality?.mergeRisk)
            // 沿用使用者取的名字：用新的、比較乾淨的分群重新學聲紋（自動帶入的名字本來就不學）
            for (idx, item) in inherited where item.source != "voiceprint" {
                try db.learnVoiceprint(job.meetingId, idx: idx, name: item.name, source: item.source)
            }
            if AppSettings.voiceprints { try db.matchVoiceprints(job.meetingId) }
            if AppSettings.summarizerReady, try db.speakers(job.meetingId).contains(where: { $0.nameSource == "default" }) {
                try db.enqueue(job.meetingId, kind: .nameSpeakers)
            }
            let count = Set(assigned.compactMap { $0 }).count
            log.info("rediarized meeting \(job.meetingId): \(count) speakers, \(inherited.count) names kept")
            onRediarized?(job.meetingId, .success(count))
            wake()
        } catch {
            onRediarized?(job.meetingId, .failure(error))
            throw error
        }
    }

    /// 舊的說話者 → 新的說話者：舊的那位（沒被使用者改過的）段落分到最多的新說話者（design §24.2）
    nonisolated static func oldToNew(segments: [Segment], newSpeakers: [Int?]) -> [Int: Int] {
        var byOld: [Int: [Int: Double]] = [:]
        for (segment, new) in zip(segments, newSpeakers) where !segment.speakerManual {
            guard let new, let o = segment.speaker else { continue }
            byOld[o, default: [:]][new, default: 0] += max(0, segment.end - segment.start)
        }
        return byOld.compactMapValues { $0.max(by: { $0.value < $1.value })?.key }
    }

    /// 名字沿用（design §23.3）：新的說話者 n 的段落（依秒數）有 60% 以上原本是同一位取過名字的 o，
    /// 而且 o 原本的段落分到 n 最多，n 就沿用 o 的名字、名字來源、連到的人
    nonisolated static func inheritNames(old: [SpeakerName], segments: [Segment], newSpeakers: [Int?])
        -> [Int: (name: String, source: String, personId: Int64?)] {
        let named = Dictionary(old.filter { $0.nameSource != "default" }.map { ($0.idx, $0) }, uniquingKeysWith: { a, _ in a })
        var byNew: [Int: [Int: Double]] = [:]   // 新 → 舊 → 秒數
        var byOld: [Int: [Int: Double]] = [:]   // 舊 → 新 → 秒數
        for (segment, new) in zip(segments, newSpeakers) {
            guard let new, let oldSpeaker = segment.speaker else { continue }
            let seconds = max(0, segment.end - segment.start)
            byNew[new, default: [:]][oldSpeaker, default: 0] += seconds
            byOld[oldSpeaker, default: [:]][new, default: 0] += seconds
        }
        var result: [Int: (name: String, source: String, personId: Int64?)] = [:]
        for (new, shares) in byNew {
            let total = shares.values.reduce(0, +)
            guard total > 0, let top = shares.max(by: { $0.value < $1.value }), top.value / total >= 0.6,
                  let speaker = named[top.key],
                  byOld[top.key]?.max(by: { $0.value < $1.value })?.key == new else { continue }
            result[new] = (speaker.name, speaker.nameSource, speaker.personId)
        }
        return result
    }

    // MARK: 猜說話者（design §21.5、R19.2）

    /// 從對話內容猜還沒有名字的說話者，存成建議。輔助功能：失敗只記 log
    private func nameSpeakers(_ job: Job, lane: JobLane) async {
        do {
            guard let meeting = try db.meeting(job.meetingId) else { return }
            let provider = AppSettings.provider
            guard provider != .none else { return }
            let speakers = try db.speakers(job.meetingId)
            let unnamed = speakers.filter { $0.nameSource == "default" }.map(\.idx)
            let segments = try db.segments(job.meetingId)
            guard !unnamed.isEmpty, !segments.isEmpty else { return }
            running[lane]?.provider = provider
            running[lane]?.stage = String(localized: "猜說話者")
            let names = Dictionary(uniqueKeysWithValues: speakers.map { ($0.idx, $0.name) })
            let language = meeting.meetingLanguage
            let context = SpeakerNaming.Context(
                language: language, meetingDate: meeting.meetingDate, title: meeting.title, participants: meeting.participantList,
                knownPeople: try db.peopleNames(), glossary: try db.glossary().map(\.term), unnamed: unnamed,
                speakerName: { names[$0] ?? language.text(zh: "說話者 \($0)", en: "Speaker \($0)", ja: "話者 \($0)") },
                segments: segments.map { ($0.start, $0.speaker, $0.text) })
            let suggestions = try await callLLM(lane) { onStart in
                try await SpeakerNaming.suggest(context, provider: provider, model: AppSettings.model, onStart: onStart)
            }
            guard isStillRunning(job) else { return }
            try db.saveSpeakerSuggestions(job.meetingId, suggestions)
            log.info("speaker names suggested: \(suggestions.count)")
        } catch {
            log.error("speaker naming failed: \(error.logSummary, privacy: .public)")
        }
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
        guard provider != .none else {
            throw LLMError.notConfigured(AppSettings.needsProviderSetup
                ? String(localized: "還沒設定整理工具，請到設定選擇 Claude、Codex 或 API")
                : String(localized: "整理工具設定為「不整理」，請到設定改成 Claude、Codex 或 API"))
        }
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
