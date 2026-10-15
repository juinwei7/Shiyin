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

    @ObservationIgnored var onFinished: ((Result) -> Void)?
    @ObservationIgnored var onLiveFinal: ((Int64, Double, Double, String) -> Void)?
    @ObservationIgnored private var writer: RecordingWriter?
    @ObservationIgnored private var sources: [AudioSource] = []
    @ObservationIgnored private var live: LiveTranscriber?
    @ObservationIgnored private var meterTimer: Timer?
    @ObservationIgnored private var activity: NSObjectProtocol?

    var hasMic: Bool { source != "system" }
    var hasSystem: Bool { source != "mic" }

    func start(meetingId: Int64, source: String = AppSettings.recordSource) {
        guard !isRecording else { return }
        self.source = source
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
    func startSimulated(meetingId: Int64, file: URL, speed: Double, both: Bool) {
        guard !isRecording else { return }
        source = both ? "both" : "mic"
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
        do { duration = try writer?.finish() ?? 0 } catch { errorText = "錄音寫檔發生錯誤：\(error.localizedDescription)" }
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
                throw CaptureError.coreAudio(warnings.first ?? "開始錄音", -1)
            }
            if !warnings.isEmpty { failureMessage = "有一個音源沒辦法啟動，只錄到另一個：\(warnings.joined(separator: "；"))" }
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
                                                             reason: "拾音正在錄音")
            let timer = Timer(timeInterval: 0.05, repeats: true) { [weak self] _ in
                MainActor.assumeIsolated { self?.tick() }
            }
            RunLoop.main.add(timer, forMode: .common)
            meterTimer = timer
            if AppSettings.liveTranscription { startLive(writer: writer, meetingId: meetingId) }
            log.info("recording started (\(self.source, privacy: .public))")
        } catch {
            abort(meetingId, message: "沒辦法開始錄音：\(error.localizedDescription)")
        }
    }

    private func startLive(writer: RecordingWriter, meetingId: Int64) {
        let live = LiveTranscriber()
        self.live = live
        liveStatus = "正在準備即時逐字稿…"
        live.onVolatile = { [weak self] text in self?.volatileText = text }
        live.onFinal = { [weak self] start, end, text in
            guard let self else { return }
            self.liveLines.append(LiveLine(start: start, end: end, text: text))
            self.onLiveFinal?(meetingId, start, end, text)
        }
        Task {
            do {
                try await live.start()
                writer.onMix = live.feedHandler
                liveStatus = ""
            } catch {
                liveStatus = "即時逐字稿沒辦法啟動（\(error.localizedDescription)），錄音照常進行，錄完會正式轉錄"
            }
        }
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
