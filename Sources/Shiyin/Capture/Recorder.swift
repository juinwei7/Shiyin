import AVFoundation
import AppKit
import Observation

/// 錄音（design §5、R1、R2）：統籌音源、寫檔、即時逐字稿、暫停、音量
@MainActor
@Observable
final class Recorder {
    struct Result {
        var meetingId: Int64
        var file: URL?
        var duration: Double
        var error: String?
    }

    struct LiveLine: Identifiable, Hashable {
        let id = UUID()
        var start: Double
        var end: Double
        var text: String
    }

    private(set) var isRecording = false
    private(set) var isPaused = false
    private(set) var meetingId: Int64?
    private(set) var startedAt: Date?
    private(set) var elapsed: Double = 0
    private(set) var micLevel: Float = 0
    private(set) var systemLevel: Float = 0
    private(set) var source = "mic"            // mic / system / both
    private(set) var liveLines: [LiveLine] = []
    private(set) var volatileText = ""
    private(set) var liveStatus = ""
    /// 系統聲音一直是靜音：可能沒有允許「錄製系統音訊」或沒有東西在播
    private(set) var systemSilent = false
    var permissionDenied = false
    var failureMessage: String?
    /// 自動偵測換了會議語言（design §18.8）：錄音畫面顯示「改回」
    private(set) var languageSwitch: (from: MeetingLanguage, to: MeetingLanguage)?

    @ObservationIgnored var onFinished: ((Result) -> Void)?
    @ObservationIgnored var onLiveFinal: ((Int64, Double, Double, String) -> Void)?
    /// 錄音中換了會議語言（自動偵測或改回），要更新資料庫
    @ObservationIgnored var onLanguageChange: ((Int64, MeetingLanguage) -> Void)?
    @ObservationIgnored private let tap = MixTap()
    @ObservationIgnored private var writer: RecordingWriter?
    @ObservationIgnored private var sources: [AudioSource] = []
    @ObservationIgnored private var live: LiveTranscriber?
    @ObservationIgnored private var meterTimer: Timer?
    @ObservationIgnored private var activity: NSObjectProtocol?

    /// 這場錄音的會議語言，決定即時逐字稿的辨識語言（design §16.4）
    private(set) var language: MeetingLanguage = .zh

    var hasMic: Bool { source != "system" }
    var hasSystem: Bool { source != "mic" }

    func start(meetingId: Int64, source: String = AppSettings.recordSource, language: MeetingLanguage) {
        guard !isRecording else { return }
        self.source = source
        self.language = language
        if source == "system" {
            begin(meetingId: meetingId, makeSources: makeDeviceSources)
            return
        }
        switch AVCaptureDevice.authorizationStatus(for: .audio) {
        case .authorized:
            begin(meetingId: meetingId, makeSources: makeDeviceSources)
        case .notDetermined:
            AVCaptureDevice.requestAccess(for: .audio) { granted in
                Task { @MainActor in
                    if granted { self.begin(meetingId: meetingId, makeSources: self.makeDeviceSources) }
                    else { self.abort(meetingId, permission: true) }
                }
            }
        default:
            abort(meetingId, permission: true)
        }
    }

    #if DEBUG
    /// 只在 DEBUG：用音訊檔當麥克風，驗證錄音流程（speed 是倍速）
    func startSimulated(meetingId: Int64, file: URL, speed: Double, both: Bool, language: MeetingLanguage) {
        guard !isRecording else { return }
        source = both ? "both" : "mic"
        self.language = language
        begin(meetingId: meetingId) {
            let source = FileSource(url: file, speed: speed)
            var list: [(Int, AudioSource)] = [(0, source)]
            if both {
                let mirror = MirroredSource()
                source.mirror = mirror
                list.append((1, mirror))
            }
            return list
        }
    }
    #endif

    func pause() {
        guard isRecording, !isPaused else { return }
        writer?.setPaused(true)
        isPaused = true
    }

    func resume() {
        guard isRecording, isPaused else { return }
        writer?.setPaused(false)
        isPaused = false
    }

    func stop() {
        guard isRecording, let meetingId else { return }
        sources.forEach { $0.stop() }
        sources = []
        var duration = 0.0
        var errorText: String?
        do { duration = try writer?.finish() ?? 0 } catch { errorText = String(localized: "錄音寫檔發生錯誤：\(error.localizedDescription)") }
        let file = writer?.url
        let live = live
        Task { await live?.finish() }
        cleanup()
        log.info("recording stopped, \(Int(duration))s")
        onFinished?(Result(meetingId: meetingId, file: file, duration: duration, error: errorText))
    }

    static func openPrivacySettings(_ pane: String = "Privacy_Microphone") {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?\(pane)") {
            NSWorkspace.shared.open(url)
        }
    }

    // MARK: 內部

    private func makeDeviceSources() -> [(Int, AudioSource)] {
        switch source {
        case "system": return [(0, SystemAudioSource())]
        case "both": return [(0, MicrophoneSource(deviceUID: AppSettings.inputDeviceUID)), (1, SystemAudioSource())]
        default: return [(0, MicrophoneSource(deviceUID: AppSettings.inputDeviceUID))]
        }
    }

    private func begin(meetingId: Int64, makeSources: () -> [(Int, AudioSource)]) {
        do {
            try Paths.ensureDirectories()
            // 檔名帶會議 id：當掉後可以接回原本的會議（AppModel.recoverRecordings）
            let url = Paths.recordingDir.appendingPathComponent("\(meetingId)-\(UUID().uuidString).wav")
            let channels = source == "both" ? 2 : 1
            let writer = try RecordingWriter(url: url, channels: channels)
            var started: [AudioSource] = []
            var warnings: [String] = []
            for (channel, audioSource) in makeSources() {
                audioSource.onSamples = { [weak writer] samples in writer?.append(channel: channel, samples) }
                do {
                    try audioSource.start()
                    started.append(audioSource)
                } catch {
                    warnings.append(error.localizedDescription)
                }
            }
            guard !started.isEmpty else {
                try? FileManager.default.removeItem(at: url)
                throw CaptureError.coreAudio(warnings.first ?? String(localized: "開始錄音"), -1)
            }
            if !warnings.isEmpty { failureMessage = String(localized: "有一個音源沒辦法啟動，只錄到另一個：\(warnings.joined(separator: String(localized: "；")))") }
            writer.onMix = tap.handler
            writer.start()
            self.writer = writer
            self.sources = started
            self.meetingId = meetingId
            startedAt = Date()
            elapsed = 0
            liveLines = []
            volatileText = ""
            systemSilent = false
            isPaused = false
            isRecording = true
            activity = ProcessInfo.processInfo.beginActivity(options: [.idleSystemSleepDisabled, .userInitiated],
                                                             reason: String(localized: "拾音正在錄音"))
            let timer = Timer(timeInterval: 0.05, repeats: true) { [weak self] _ in
                MainActor.assumeIsolated { self?.tick() }
            }
            RunLoop.main.add(timer, forMode: .common)
            meterTimer = timer
            if AppSettings.liveTranscription { startLive(writer: writer, meetingId: meetingId) }
            startLanguageDetection(meetingId: meetingId)
            log.info("recording started (\(self.source, privacy: .public))")
        } catch {
            abort(meetingId, message: String(localized: "沒辦法開始錄音：\(error.localizedDescription)"))
        }
    }

    private func startLive(writer: RecordingWriter, meetingId: Int64, restarted: Bool = false) {
        let live = LiveTranscriber(language: language)
        self.live = live
        liveStatus = String(localized: "正在準備即時逐字稿…")
        live.onVolatile = { [weak self] text in self?.volatileText = text }
        // 中途重新開始時，新的辨識從 0 秒算，要加上接上音訊時已經錄了多久
        let offset = LiveOffset()
        live.onFinal = { [weak self] start, end, text in
            guard let self else { return }
            let (start, end) = (start + offset.seconds, end + offset.seconds)
            self.liveLines.append(LiveLine(start: start, end: end, text: text))
            self.onLiveFinal?(meetingId, start, end, text)
        }
        Task {
            do {
                try await live.start()
                guard self.live === live else { return }   // 準備期間又換了語言
                if restarted { offset.seconds = writer.recordedSeconds }
                tap.setLive(live.feedHandler)
                liveStatus = ""
            } catch {
                liveStatus = String(localized: "即時逐字稿沒辦法啟動（\(error.localizedDescription)），錄音照常進行，錄完會正式轉錄")
            }
        }
    }

    // MARK: 自動偵測主要語言（design §18.8、R16、D21）

    /// 開始錄音後收集約 10 秒有聲音的片段，判斷是中文、英文還是日文；跟目前不同而且有把握才切換，只判斷一次
    private func startLanguageDetection(meetingId: Int64) {
        languageSwitch = nil
        guard AppSettings.autoDetectLanguage else { return }
        let path = ModelStore.path(.languageDetection).path
        guard FileManager.default.fileExists(atPath: path) else {
            AppModel.shared.models.autoStart(.languageDetection)   // 這次來不及，下次錄音就能用
            return
        }
        tap.setDetector(LanguageDetector.Collector { [weak self] samples in
            let recorder = self
            Task.detached(priority: .utility) {
                let result = Swift.Result { try LanguageDetector.detect(samples, modelPath: path) }
                await recorder?.applyDetection(result, meetingId: meetingId)
            }
        })
    }

    private func applyDetection(_ result: Swift.Result<[MeetingLanguage: Double], Error>, meetingId: Int64) {
        tap.setDetector(nil)
        guard isRecording, self.meetingId == meetingId else { return }
        switch result {
        case .success(let probabilities):
            let summary = probabilities.sorted { $0.key.rawValue < $1.key.rawValue }
                .map { "\($0.key.rawValue) \(String(format: "%.2f", $0.value))" }.joined(separator: ", ")
            log.info("language detection: \(summary, privacy: .public)")
            guard let (best, p) = probabilities.max(by: { $0.value < $1.value }).map({ ($0.key, $0.value) }),
                  best != language, p >= LanguageDetector.threshold else { return }
            let from = language
            switchLanguage(to: best)
            languageSwitch = (from, best)
        case .failure(let error):
            log.error("language detection failed: \(error.localizedDescription, privacy: .public)")
        }
    }

    /// 錄音中換會議語言：即時逐字稿用新的語言重新開始（之前的逐字稿保留，錄完會正式轉錄）
    func switchLanguage(to newLanguage: MeetingLanguage) {
        guard isRecording, let meetingId, let writer, newLanguage != language else { return }
        language = newLanguage
        languageSwitch = nil
        tap.setDetector(nil)
        onLanguageChange?(meetingId, newLanguage)
        guard AppSettings.liveTranscription else { return }
        tap.setLive(nil)
        // 舊的辨識收尾送來的是用錯的語言聽的，不加進逐字稿
        let old = live
        old?.onFinal = nil
        old?.onVolatile = nil
        volatileText = ""
        Task { await old?.finish() }
        startLive(writer: writer, meetingId: meetingId, restarted: true)
    }

    private func tick() {
        guard let writer else { return }
        elapsed = writer.recordedSeconds
        if source == "both" {
            micLevel = writer.level(0)
            systemLevel = writer.level(1)
            systemSilent = writer.silence(1) > 15
        } else if source == "system" {
            systemLevel = writer.level(0)
            systemSilent = writer.silence(0) > 15
        } else {
            micLevel = writer.level(0)
        }
    }

    private func abort(_ meetingId: Int64, permission: Bool = false, message: String? = nil) {
        if permission { permissionDenied = true }
        if let message { failureMessage = message }
        onFinished?(Result(meetingId: meetingId, file: nil, duration: 0, error: nil))
    }

    private func cleanup() {
        meterTimer?.invalidate()
        meterTimer = nil
        if let activity { ProcessInfo.processInfo.endActivity(activity) }
        activity = nil
        writer = nil
        live = nil
        tap.setLive(nil)
        tap.setDetector(nil)
        languageSwitch = nil
        meetingId = nil
        startedAt = nil
        isRecording = false
        isPaused = false
        micLevel = 0
        systemLevel = 0
        volatileText = ""
        liveStatus = ""
    }
}

/// 錄音的單聲道混音要同時送給即時逐字稿和語言偵測；寫檔的執行緒呼叫 handler，主執行緒換掉接收端
final class MixTap: @unchecked Sendable {
    private let lock = NSLock()
    private var live: (([Float]) -> Void)?
    private var detector: LanguageDetector.Collector?

    func setLive(_ handler: (([Float]) -> Void)?) { lock.withLock { live = handler } }
    func setDetector(_ collector: LanguageDetector.Collector?) { lock.withLock { detector = collector } }

    var handler: ([Float]) -> Void {
        { [weak self] samples in
            guard let self else { return }
            let (live, detector) = self.lock.withLock { (self.live, self.detector) }
            live?(samples)
            detector?.feed(samples)
        }
    }
}

/// 一個即時辨識的時間偏移（design §18.8）
@MainActor
private final class LiveOffset {
    var seconds: Double = 0
}
