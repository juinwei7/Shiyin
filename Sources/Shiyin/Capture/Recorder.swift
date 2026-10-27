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
        /// 沒人說話太久而自動停止（R48.2）：最後有人說話的秒數
        var idleSince: Double? = nil
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
    /// 精確的錄音秒數（筆記的時間點、標記重點用）。每 0.05 秒變一次，畫面不要讀這個，讀 elapsedSeconds（design §25）
    @ObservationIgnored private(set) var elapsed: Double = 0
    /// 畫面顯示的錄音秒數：整秒變了才更新
    private(set) var elapsedSeconds = 0
    /// 音量（0～1）。不讓 SwiftUI 觀察：音量條（LevelMeter）自己定時來讀，
    /// 不然說話時每秒變十幾次，每次整個視窗都要重新排版（design §25）
    @ObservationIgnored private(set) var micLevel: Float = 0
    @ObservationIgnored private(set) var systemLevel: Float = 0
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
    /// 有音源超過 3 秒沒送資料、正在重新連接（design §20.2）：錄音畫面的提示列顯示
    private(set) var deviceWarning: String?

    /// 收音提醒（design §26.1、R24.1、R24.3）
    enum LevelHint: String {
        case silent   // 麥克風的聲音完全是 0
        case quiet    // 有人在說話，但聲音很小
        case noisy    // 背景一直很大聲

        /// 錄音畫面、選單列、通知都用同一句（R42）
        var text: String {
            switch self {
            case .silent: return String(localized: "麥克風沒有收到聲音：可能被靜音、被其他 app 佔用，或選錯了麥克風。可以在設定的「錄音」換一個。")
            case .quiet: return String(localized: "聲音有點小：手機或筆電放近說話的人一點。")
            case .noisy: return String(localized: "背景有點吵，說話者可能分不準。錄完可以用「重新辨識說話者」告訴拾音大約幾個人。")
            }
        }
    }
    private(set) var levelHint: LevelHint?
    @ObservationIgnored private var dismissedLevelHints: Set<LevelHint> = []
    /// 這場錄音已經通知過的提示（R42.1：每種每場最多一次）
    @ObservationIgnored private var notifiedLevelHints: Set<LevelHint> = []
    /// 提示第一次出現（design §31.5）：AppModel 決定要不要送通知
    @ObservationIgnored var onLevelHint: ((LevelHint) -> Void)?
    @ObservationIgnored private var noisySeconds = 0
    @ObservationIgnored private var quietSeconds = 0

    /// 忘了停止錄音（design §35、R48）：最後有人說話的錄音秒數；即時逐字稿有沒有接上音訊
    @ObservationIgnored private var lastSpeechAt: Double = 0
    @ObservationIgnored private var liveActive = false
    /// 好像沒人在說話了，要停止錄音嗎？（錄音畫面、選單列顯示）
    private(set) var idlePrompt = false
    /// 提醒出現（AppModel 送通知）
    @ObservationIgnored var onIdlePrompt: (() -> Void)?
    /// 15 分鐘提醒、30 分鐘自動停止；DEBUG 可以用啟動參數縮短（design §35.2）
    static var idleRemindSeconds: Double { debugSeconds("idleRemindSeconds") ?? 900 }
    static var idleStopSeconds: Double { debugSeconds("idleStopSeconds") ?? 1800 }
    private static func debugSeconds(_ key: String) -> Double? {
        #if DEBUG
        let value = UserDefaults.standard.double(forKey: key)
        return value > 0 ? value : nil
        #else
        return nil
        #endif
    }
    #if DEBUG
    @ObservationIgnored private(set) var levelCheck: [String: Any] = [:]
    /// 忘了停止錄音的狀態（design §35.2）：shiyinctl state 的 recorder.idle
    var idleInfo: [String: Any] {
        ["idleSeconds": isRecording ? elapsed - lastSpeechAt : 0, "lastSpeechAt": lastSpeechAt, "prompt": idlePrompt,
         "liveActive": liveActive, "remindAfter": Self.idleRemindSeconds, "stopAfter": Self.idleStopSeconds]
    }
    #endif

    @ObservationIgnored var onFinished: ((Result) -> Void)?
    /// 錄音開始了（全域快捷鍵標記重點這時才註冊，design §21.10）
    @ObservationIgnored var onStarted: (() -> Void)?
    /// 停止後即時辨識收尾完（最後還沒定稿的文字已經存進去），給草稿紀錄用（design §21.3）：會議 id、錄到的秒數
    @ObservationIgnored var onLiveFinished: ((Int64, Double) -> Void)?
    @ObservationIgnored var onLiveFinal: ((Int64, Double, Double, String) -> Void)?
    /// 錄音中換了會議語言（自動偵測或改回），要更新資料庫
    @ObservationIgnored var onLanguageChange: ((Int64, MeetingLanguage) -> Void)?
    @ObservationIgnored private let tap = MixTap()
    @ObservationIgnored private var writer: RecordingWriter?
    @ObservationIgnored private var sources: [Int: AudioSource] = [:]   // 聲道 → 音源
    /// 看門狗：沒送資料的聲道 → 上次試著重新連接的時間；重新連接失敗的原因
    @ObservationIgnored private var reconnecting: [Int: Date] = [:]
    @ObservationIgnored private var reconnectErrors: [Int: String] = [:]
    @ObservationIgnored private var pendingRestarts: [Int: DispatchWorkItem] = [:]
    /// 磁碟空間（design §20.3）：上次檢查的時間；錄音中不到 300 MB 只提醒一次
    @ObservationIgnored private var lastDiskCheck = Date.distantPast
    @ObservationIgnored private var warnedLowDisk = false
    #if DEBUG
    /// 模擬錄音的檔案送完了，之後沒有資料是正常的
    @ObservationIgnored private var simulationEnded = false
    #endif
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
        simulationEnded = false
        begin(meetingId: meetingId) {
            let source = FileSource(url: file, speed: speed)
            source.onEnded = { [weak self] in Task { @MainActor in self?.simulationEnded = true } }
            var list: [(Int, AudioSource)] = [(0, source)]
            if both {
                let mirror = MirroredSource()
                source.mirror = mirror
                list.append((1, mirror))
            }
            return list
        }
    }

    /// 只在 DEBUG：recordstall，某個聲道停止送資料幾秒，nil 是馬上恢復（design §20.2）
    func simulateStall(channel: Int, seconds: Double?) {
        (sources[0] as? FileSource)?.stall(channel: channel, seconds: seconds)
    }

    /// 只在 DEBUG：recordinterrupt，等於音源發現裝置變了。連續送幾次，驗證只會重新開始一次
    func simulateInterruption(channel: Int, times: Int = 3) {
        for _ in 0..<times { sources[channel]?.onInterruption?() }
    }
    #endif

    func pause() {
        guard isRecording, !isPaused else { return }
        writer?.setPaused(true)
        isPaused = true
    }

    func resume() {
        guard isRecording, isPaused else { return }
        writer?.resetRecentLevels()
        (noisySeconds, quietSeconds) = (0, 0)
        markSpeech()   // 按了繼續表示人還在，重新計時（design §35.1）
        writer?.setPaused(false)
        isPaused = false
    }

    /// 「繼續錄」：人還在，重新計時（R48.1）
    func continueAfterIdle() {
        guard isRecording else { return }
        markSpeech()
    }

    /// 有人在說話：重新計時，提醒收掉
    private func markSpeech() {
        lastSpeechAt = elapsed
        if idlePrompt { idlePrompt = false }
    }

    /// 每秒檢查沒人說話多久（design §35）：15 分鐘提醒、30 分鐘自動停止
    private func checkIdle(_ writer: RecordingWriter) {
        guard AppSettings.idleStop, !isPaused else { return }
        // 即時逐字稿沒在跑時才看聲音（打字、關門也會算，只當備用，D115）
        if !liveActive && soundsLikeSpeech(writer) { markSpeech() }
        let idle = elapsed - lastSpeechAt
        if idle >= Self.idleStopSeconds {
            log.info("recording auto-stopped after \(Int(idle))s without speech")
            stop(idleSince: lastSpeechAt)
            return
        }
        let prompt = idle >= Self.idleRemindSeconds
        if prompt != idlePrompt {
            idlePrompt = prompt
            if prompt { onIdlePrompt?() }
        }
    }

    /// 最近 1 秒有 30% 以上的音框像說話（design §35.1）：比背景（最近 30 秒的第 10 百分位）大 12 dB 而且高於 -50 dBFS，
    /// 或本身就高於 -40 dBFS（一開始就一直有人說話時，背景也是說話的音量）
    private func soundsLikeSpeech(_ writer: RecordingWriter) -> Bool {
        for channel in 0..<(source == "both" ? 2 : 1) {
            let levels = writer.recentLevels(channel)
            guard levels.count >= 20 else { continue }
            let background = levels.sorted()[Int(Double(levels.count - 1) * 0.1)]
            let threshold = min(max(background + 12, -50), -40)
            let last = levels.suffix(10)
            if Double(last.filter { $0 > threshold }.count) >= Double(last.count) * 0.3 { return true }
        }
        return false
    }

    /// 「知道了」：這一種提示這場錄音不再出現
    func dismissLevelHint() {
        guard let levelHint else { return }
        dismissedLevelHints.insert(levelHint)
        self.levelHint = nil
    }

    func stop(idleSince: Double? = nil) {
        guard isRecording, let meetingId else { return }
        sources.values.forEach { $0.stop() }
        sources = [:]
        let duration = writer?.finish() ?? 0
        let file = writer?.url
        if writer?.writeFailed == true {
            // 寫不進去時關檔可能也沒更新 WAV 標頭的大小，照實際的檔案大小補上（復原流程也是這樣修）
            if let file { try? WAVRepair.repair(file) }
            failureMessage = String(localized: "錄音寫不進磁碟（可能是空間不足），已經停止錄音。停止前錄到的部分有保留，會照常轉錄。")
        }
        let live = live
        Task {
            await live?.finish()
            self.onLiveFinished?(meetingId, duration)
        }
        cleanup()
        log.info("recording stopped, \(Int(duration))s")
        onFinished?(Result(meetingId: meetingId, file: file, duration: duration, error: nil, idleSince: idleSince))
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
            var started: [Int: AudioSource] = [:]
            var warnings: [String] = []
            for (channel, audioSource) in makeSources() {
                audioSource.onSamples = { [weak writer] samples in writer?.append(channel: channel, samples) }
                audioSource.onInterruption = { [weak self] in
                    Task { @MainActor in self?.scheduleRestart(channel) }
                }
                do {
                    try audioSource.start()
                    started[channel] = audioSource
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
            warnedLowDisk = false
            checkDiskSpace(writer, starting: true)
            self.meetingId = meetingId
            startedAt = Date()
            elapsed = 0
            elapsedSeconds = 0
            levelHint = nil
            dismissedLevelHints = []
            notifiedLevelHints = []
            (noisySeconds, quietSeconds) = (0, 0)
            lastSpeechAt = 0
            liveActive = false
            idlePrompt = false
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
            onStarted?()
        } catch {
            abort(meetingId, message: String(localized: "沒辦法開始錄音：\(error.localizedDescription)"))
        }
    }

    private func startLive(writer: RecordingWriter, meetingId: Int64, restarted: Bool = false) {
        let live = LiveTranscriber(language: language)
        self.live = live
        liveStatus = String(localized: "正在準備即時逐字稿…")
        liveActive = false
        live.onVolatile = { [weak self] text in
            #if DEBUG
            DebugMetrics.bump("volatileUpdate")
            #endif
            guard let self else { return }
            // 有新的字就是有人在說話（design §35.1）
            if !text.isEmpty && text != self.volatileText { self.markSpeech() }
            self.volatileText = text
        }
        // 中途重新開始時，新的辨識從 0 秒算，要加上接上音訊時已經錄了多久
        let offset = LiveOffset()
        live.onFinal = { [weak self] start, end, text in
            guard let self else { return }
            let (start, end) = (start + offset.seconds, end + offset.seconds)
            self.liveLines.append(LiveLine(start: start, end: end, text: text))
            if !text.isEmpty { self.markSpeech() }
            self.onLiveFinal?(meetingId, start, end, text)
        }
        Task {
            do {
                try await live.start()
                guard self.live === live else { return }   // 準備期間又換了語言
                if restarted { offset.seconds = writer.recordedSeconds }
                tap.setLive(live.feedHandler)
                liveStatus = ""
                liveActive = true
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
        if writer.writeFailed {
            stop()
            return
        }
        // 觀察的屬性只在值真的變了才設（@Observable 設成同樣的值也會讓畫面重算，design §25）
        elapsed = writer.recordedSeconds
        let seconds = Int(elapsed)
        if seconds != elapsedSeconds {
            elapsedSeconds = seconds
            checkLevels(writer)
            checkIdle(writer)
            guard isRecording else { return }   // 沒人說話太久，剛自動停止
        }
        var silent = systemSilent
        if source == "both" {
            micLevel = writer.level(0)
            systemLevel = writer.level(1)
            silent = writer.silence(1) > 15
        } else if source == "system" {
            systemLevel = writer.level(0)
            silent = writer.silence(0) > 15
        } else {
            micLevel = writer.level(0)
        }
        if silent != systemSilent { systemSilent = silent }
        watchdog(writer)
        if Date().timeIntervalSince(lastDiskCheck) >= 30 { checkDiskSpace(writer, starting: false) }
    }

    /// 收音提醒（design §26.1）：每秒看麥克風最近 30 秒的音量。第 90 百分位是說話的音量、第 10 百分位是背景
    private func checkLevels(_ writer: RecordingWriter) {
        guard hasMic, !isPaused else { return }
        let silent = writer.silence(0) > 10
        let levels = writer.recentLevels(0).sorted()
        var speech: Float = 0, background: Float = 0
        if levels.count >= 300 {
            speech = levels[Int(Double(levels.count - 1) * 0.9)]
            background = levels[Int(Double(levels.count - 1) * 0.1)]
            noisySeconds = background > -36 ? noisySeconds + 1 : 0
            // 說話比背景大 4 dB 以上才算有人在說話（完全安靜時聲音很平），不是安靜的停頓
            quietSeconds = speech < -50 && speech - background >= 4 ? quietSeconds + 1 : 0
        }
        let hint: LevelHint? = silent ? .silent : quietSeconds >= 60 ? .quiet : noisySeconds >= 45 ? .noisy : nil
        let shown = hint.flatMap { dismissedLevelHints.contains($0) ? nil : $0 }
        if shown != levelHint {
            levelHint = shown
            // 第一次出現才通知（R42.1）；提示消失又出現不再通知
            if let shown, notifiedLevelHints.insert(shown).inserted { onLevelHint?(shown) }
        }
        #if DEBUG
        levelCheck = ["speech": speech, "background": background, "frames": levels.count, "silentSeconds": writer.silence(0),
                      "noisySeconds": noisySeconds, "quietSeconds": quietSeconds, "hint": levelHint?.rawValue ?? ""]
        #endif
    }

    /// 開始錄音時剩不到 1 GB、錄音中剩不到 300 MB 就提醒（錄音照常進行；寫不進去時 tick 會停止錄音）
    private func checkDiskSpace(_ writer: RecordingWriter, starting: Bool) {
        lastDiskCheck = Date()
        // ForImportantUsage 含可以清掉的空間，但磁碟映像、外接磁碟會回傳 0，所以跟一般的剩餘空間取大的
        guard let values = try? Paths.recordingDir.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey, .volumeAvailableCapacityKey])
        else { return }
        let available = max(values.volumeAvailableCapacityForImportantUsage ?? 0, Int64(values.volumeAvailableCapacity ?? 0))
        guard available < (starting ? 1_000_000_000 : 300_000_000), starting || !warnedLowDisk else { return }
        if !starting { warnedLowDisk = true }
        // 16 kHz、16 位元：單聲道每小時約 115 MB，雙聲道約 230 MB
        let bytesPerMinute = CaptureFormat.sampleRate * 2 * Double(writer.channels) * 60
        let minutes = Int(Double(available) / bytesPerMinute)
        let size = ByteCountFormatter.string(fromByteCount: available, countStyle: .file)
        log.info("low disk space: \(available / 1_000_000) MB")
        let message = String(localized: "磁碟只剩 \(size)，大約只能錄 \(minutes) 分鐘。空間用完時會自動停止錄音，已經錄到的部分會保留。")
        failureMessage = failureMessage.map { $0 + "\n\n" + message } ?? message
    }

    // MARK: 換裝置、看門狗（design §20.2、R18.2、D25）

    /// 裝置切換時通知會連續來好幾次，等 0.3 秒沒有新的通知再重新開始
    private func scheduleRestart(_ channel: Int) {
        guard isRecording else { return }
        pendingRestarts[channel]?.cancel()
        let item = DispatchWorkItem { [weak self] in
            MainActor.assumeIsolated {
                self?.pendingRestarts[channel] = nil
                self?.restartSource(channel)
            }
        }
        pendingRestarts[channel] = item
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.3, execute: item)
    }

    private func restartSource(_ channel: Int) {
        guard isRecording, let audioSource = sources[channel] else { return }
        do {
            try audioSource.restart()
            reconnectErrors[channel] = nil
            log.info("audio source \(channel) restarted")
        } catch {
            // 錄音不停：另一個音源照常錄，雙聲道時這段會補靜音；看門狗每 5 秒再試
            reconnectErrors[channel] = error.localizedDescription
            log.error("audio source \(channel) restart failed: \(error.localizedDescription, privacy: .public)")
        }
        updateDeviceWarning()
    }

    /// 音源超過 3 秒沒送資料：提示並每 5 秒試一次重新開始；收到資料後提示消失
    private func watchdog(_ writer: RecordingWriter) {
        #if DEBUG
        if simulationEnded { return }
        #endif
        let now = Date()
        for channel in sources.keys.sorted() {
            if writer.secondsSinceSamples(channel) > 3 {
                if let last = reconnecting[channel], now.timeIntervalSince(last) < 5 { continue }
                if reconnecting[channel] == nil { log.info("audio source \(channel) stalled") }
                reconnecting[channel] = now
                restartSource(channel)
            } else if reconnecting[channel] != nil {
                reconnecting[channel] = nil
                reconnectErrors[channel] = nil
                log.info("audio source \(channel) recovered")
                updateDeviceWarning()
            }
        }
    }

    private func updateDeviceWarning() {
        let warning = reconnecting.keys.sorted().first.map { channel -> String in
            let mic = source != "system" && channel == 0
            if let reason = reconnectErrors[channel] {
                return mic ? String(localized: "麥克風沒有送出聲音，重新連接失敗：\(reason)。會繼續重試，錄音沒有停")
                    : String(localized: "電腦聲音的擷取中斷了，重新連接失敗：\(reason)。會繼續重試，錄音沒有停")
            }
            return mic ? String(localized: "麥克風沒有送出聲音，正在重新連接…") : String(localized: "電腦聲音的擷取中斷了，正在重新連接…")
        }
        if warning != deviceWarning { deviceWarning = warning }
    }

    private func abort(_ meetingId: Int64, permission: Bool = false, message: String? = nil) {
        if permission { permissionDenied = true }
        if let message { failureMessage = message }
        onFinished?(Result(meetingId: meetingId, file: nil, duration: 0, error: nil))
    }

    private func cleanup() {
        meterTimer?.invalidate()
        meterTimer = nil
        pendingRestarts.values.forEach { $0.cancel() }
        pendingRestarts = [:]
        reconnecting = [:]
        reconnectErrors = [:]
        deviceWarning = nil
        if let activity { ProcessInfo.processInfo.endActivity(activity) }
        activity = nil
        writer = nil
        live = nil
        tap.setLive(nil)
        tap.setDetector(nil)
        languageSwitch = nil
        levelHint = nil
        idlePrompt = false
        liveActive = false
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
