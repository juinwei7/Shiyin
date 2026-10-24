import Foundation

/// 轉錄的設定（design §6、§17）。JobRunner 用目前的 app 設定產生；評估工具（shiyinctl transcribe-eval）從 JSON 讀，
/// 同一段錄音可以換設定重跑比較（§17.1）
struct TranscriptionOptions: Codable {
    /// breeze：whisper.cpp，模型依會議語言（中文、英文 Breeze，日文 large-v3-turbo，design §18.2）；system：macOS 內建辨識；
    /// qwen：Qwen3-ASR（llama.cpp，中文、英文，design §32）
    enum Engine: String, Codable { case breeze, system, qwen }
    /// 分聲道時怎麼處理麥克風收到的回音（design §17.3）
    enum Echo: String, Codable {
        case none
        case dedupe   // 轉錄後刪掉跟電腦聲音重複的麥克風段落
    }

    var engine: Engine = .breeze
    var language: MeetingLanguage = .zh
    var glossary: [String] = []
    var diarize = true
    /// 雙聲道錄音（麥克風、電腦聲音）分開轉錄（design §17.3）；還在評估，設定裡沒有開關
    var splitChannels = false
    var echo: Echo = .dedupe
    /// 評估用：換一個 whisper 模型檔（design §18.2 比較日文引擎）；nil 依會議語言選
    var model: String?
    /// 沒分聲道時聲紋記成哪種收音（design §21.4）：mic、system，雙聲道混在一起是 mix
    var audioSource = "mix"
    /// whisper 每段幾秒（design §21.16、D34）；評估時設 0 不分段
    var chunkSeconds: Double = 600
    /// 說話者辨識至少分出幾個人（design §23.2），nil 自動判斷
    var minSpeakers: Int?
    /// 評估用：whisper 解碼參數和提示詞的寫法（design §17.1）；nil 用 app 的預設值。正式轉錄不設
    var tuning: DecoderTuning?

    /// whisper 解碼的實驗開關（只有 transcribe-eval 會設）：每一項 nil 就用 whisper.cpp 或 app 的預設值
    struct DecoderTuning: Codable {
        /// whisper.cpp 的 no_context（預設 true：每個 30 秒的窗只用基本提示詞，不接前一個窗的文字）
        var noContext: Bool?
        var beamSize: Int?
        /// 預設 0.2；-1 關掉信心不夠時升溫重試
        var temperatureInc: Float?
        var entropyThold: Float?
        var logprobThold: Float?
        var noSpeechThold: Float?
        /// 壓掉非語音的 token（音樂、掌聲這類標記）
        var suppressNst: Bool?
        /// 不做 app 的幻覺過濾，看過濾丟掉了什麼
        var keepAll: Bool?
        /// 提示詞的寫法：nil 是現在的「以下是…詞彙：…」；natural 是寫成像逐字稿的一句話（design §17.1）
        var promptStyle: String?
        /// 段落最長幾個字（搭配 split_on_word），0 或 nil 不限制
        var maxLen: Int?
        /// Qwen3-ASR（design §32.4）：每段最長幾秒（預設 15）、跳針時要不要重試（預設 true）、要不要先辨識說話者再分段（預設 true）
        var qwenChunkSeconds: Double?
        var qwenRetry: Bool?
        var qwenTurns: Bool?
        var qwenGuard: Bool?
        var qwenCutsAny: Bool?
        var qwenSplitMixed: Bool?
    }

    init() {}

    /// 寫進 meetings.transcribed_with（design §21.17）
    var transcribedWith: String { engine == .system ? "system" : engine == .qwen ? "qwen" : language == .ja ? "japanese" : "breeze" }

    @MainActor
    static func current(language: MeetingLanguage, glossary: [String], channels: String, minSpeakers: Int? = nil) -> TranscriptionOptions {
        var options = TranscriptionOptions()
        options.minSpeakers = minSpeakers
        // 這個語言的模型還沒下載好時，這一場先用 macOS 內建辨識（design §21.17、D35）；
        // 中文、英文預設 Qwen3-ASR（design §32.3），還沒下載好而已經有 Breeze 時先用 Breeze（R43.7）
        let wanted = ModelStore.transcription(for: language)
        if AppSettings.useSystemTranscriber { options.engine = .system }
        else if ModelStore.isDownloaded(wanted) { options.engine = wanted == .qwen ? .qwen : .breeze }
        else if wanted == .qwen && ModelStore.isDownloaded(.breeze) { options.engine = .breeze }
        else { options.engine = .system }
        options.language = language
        options.glossary = glossary
        options.diarize = AppSettings.diarize
        // 只有錄音時選了「麥克風＋電腦聲音」的會議才分聲道；匯入的雙聲道檔案（音樂、影片）照舊混成一軌
        options.splitChannels = AppSettings.splitChannels && channels == "both"
        options.audioSource = channels == "both" ? "mix" : channels
        return options
    }

    // 評估用的 JSON 只寫要改的欄位，其他用預設值
    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        engine = try c.decodeIfPresent(Engine.self, forKey: .engine) ?? .breeze
        language = try c.decodeIfPresent(MeetingLanguage.self, forKey: .language) ?? .zh
        glossary = try c.decodeIfPresent([String].self, forKey: .glossary) ?? []
        diarize = try c.decodeIfPresent(Bool.self, forKey: .diarize) ?? true
        splitChannels = try c.decodeIfPresent(Bool.self, forKey: .splitChannels) ?? false
        echo = try c.decodeIfPresent(Echo.self, forKey: .echo) ?? .dedupe
        model = try c.decodeIfPresent(String.self, forKey: .model)
        audioSource = try c.decodeIfPresent(String.self, forKey: .audioSource) ?? "mix"
        chunkSeconds = try c.decodeIfPresent(Double.self, forKey: .chunkSeconds) ?? 600
        minSpeakers = try c.decodeIfPresent(Int.self, forKey: .minSpeakers)
        tuning = try c.decodeIfPresent(DecoderTuning.self, forKey: .tuning)
    }
}

struct TranscriptionResult {
    var segments: [TranscribedSegment]
    var speakers: [Int?]
    /// 分聲道時每段來自哪個聲道（0 麥克風、1 電腦聲音）；沒分聲道時都是 nil
    var channels: [Int?]
    var duration: Double
    /// 每位說話者（逐字稿上的編號）的聲紋、說話秒數、收音（design §21.4）
    var voices: [Int: SpeakerVoice] = [:]
    /// whisper 分段的位置（秒）；評估工具用來檢查接縫
    var seams: [Double] = []
    /// 說話者辨識分得準不準的線索（design §24.3）；分聲道、沒辨識時是 nil
    var diarizationQuality: Diarizer.Quality? = nil
}

struct SpeakerVoice {
    var embedding: [Float]
    var seconds: Double
    var source: String     // mic / system / mix
}

/// 轉錄流程：解碼 → 辨識 → 說話者辨識（design §6）；雙聲道錄音可以分開轉錄再合併（§17.3）
@MainActor
struct TranscriptionPipeline {
    var options: TranscriptionOptions
    /// whisper 模型檔（engine 是 breeze 時用）：評估時可以指定，平常依會議語言選。不看引擎設定：設定是 Qwen 時 transcription(for:) 回 qwen，
    /// 但 whisper 這條路只認 Breeze 和 large-v3-turbo（2026-10-24 評估時 Breeze 跑去載 GGUF 而失敗）
    var modelPath: String { options.model ?? ModelStore.path(options.language == .ja ? .japanese : .breeze).path }
    /// 目前在做哪一步（顯示在會議列表）
    var stage: (String) -> Void = { _ in }
    /// 整體進度 0～1，會從背景執行緒呼叫
    var progress: @Sendable (Double) -> Void = { _ in }
    /// 這一步要怎麼取消（whisper 用 Control，macOS 內建用 Task）
    var setCancel: (@escaping () -> Void) -> Void = { _ in }
    /// 每一步之間檢查工作是否還要繼續
    var shouldContinue: () -> Bool = { true }

    /// 回傳 nil 表示中途被取消
    func run(_ audio: URL) async throws -> TranscriptionResult? {
        if options.engine == .qwen {
            guard ModelStore.isDownloaded(.qwen) else { throw WhisperError.modelMissing }
            return try await runQwen(audio)
        }
        guard options.engine == .system || FileManager.default.fileExists(atPath: modelPath) else { throw WhisperError.modelMissing }
        stage(String(localized: "讀取錄音"))
        if options.splitChannels {
            let channels = try await AudioDecoder.decodeChannels(audio)
            if channels.count == 2 { return try await runSplit(microphone: channels[0], system: channels[1]) }
        }
        if options.engine == .breeze { return try await runStreaming(audio) }
        let samples = try await AudioDecoder.decode(audio)
        guard shouldContinue() else { return nil }
        let duration = Double(samples.count) / AudioDecoder.sampleRate
        let diarize = options.diarize && duration > 5
        let share = diarize ? 0.85 : 1.0
        let report = progress
        progress(0)
        guard let (transcribed, seams) = try await transcribe(samples, from: 0, span: share) else { return nil }
        var speakers: [Int?] = transcribed.map { _ in nil }
        var voices: [Int: SpeakerVoice] = [:]
        var quality: Diarizer.Quality?
        if diarize {
            stage(String(localized: "辨識說話者"))
            (speakers, voices, quality) = await self.speakers(of: transcribed, in: samples, source: options.audioSource) { report(share + $0 * (1 - share)) }
        }
        guard shouldContinue() else { return nil }
        return TranscriptionResult(segments: transcribed, speakers: speakers, channels: transcribed.map { _ in nil }, duration: duration,
                                   voices: voices, seams: seams, diarizationQuality: quality)
    }

    // MARK: whisper 從檔案依序讀（design §21.16）

    /// whisper 一段一段從檔案讀，說話者辨識時才解碼整段：轉錄時記憶體裡不用留整段錄音
    private func runStreaming(_ audio: URL) async throws -> TranscriptionResult? {
        guard shouldContinue() else { return nil }
        stage(String(localized: "轉錄"))
        // 還不知道長度（太短不辨識說話者），先照要辨識說話者算進度
        let share = options.diarize ? 0.85 : 1.0
        let report = progress
        progress(0)
        let control = WhisperEngine.Control { report($0 * share) }
        setCancel { control.cancel() }
        let prompt = WhisperEngine.prompt(glossary: options.glossary, language: options.language, style: options.tuning?.promptStyle)
        let (path, code, chunkSeconds, tuning) = (modelPath, options.language.whisperCode, options.chunkSeconds, options.tuning)
        let result = try await Task.detached(priority: .userInitiated) {
            try await WhisperEngine.transcribe(file: audio, chunkSeconds: chunkSeconds, modelPath: path, prompt: prompt, language: code,
                                               control: control, tuning: tuning)
        }.value
        guard shouldContinue() else { return nil }
        let duration = Double(result.sampleCount) / AudioDecoder.sampleRate
        var speakers: [Int?] = result.segments.map { _ in nil }
        var voices: [Int: SpeakerVoice] = [:]
        var quality: Diarizer.Quality?
        if options.diarize && duration > 5 {
            stage(String(localized: "辨識說話者"))
            let samples = try await AudioDecoder.decode(audio)
            guard shouldContinue() else { return nil }
            (speakers, voices, quality) = await self.speakers(of: result.segments, in: samples, source: options.audioSource) {
                report(share + $0 * (1 - share))
            }
        }
        guard shouldContinue() else { return nil }
        return TranscriptionResult(segments: result.segments, speakers: speakers, channels: result.segments.map { _ in nil },
                                   duration: duration, voices: voices, seams: result.seams.map { Double($0) / AudioDecoder.sampleRate },
                                   diarizationQuality: quality)
    }

    // MARK: Qwen3-ASR（design §32.4）

    /// 先解碼整段、先辨識說話者，再依說話者片段和安靜處分段轉錄；段落的時間就是分段的時間，說話者是那一段重疊最多的人
    private func runQwen(_ audio: URL) async throws -> TranscriptionResult? {
        stage(String(localized: "讀取錄音"))
        let samples = try await AudioDecoder.decode(audio)
        guard shouldContinue() else { return nil }
        let duration = Double(samples.count) / AudioDecoder.sampleRate
        let diarize = options.diarize && duration > 5 && options.tuning?.qwenTurns != false
        let report = progress
        progress(0)
        var output: Diarizer.Output?
        let share = diarize ? 0.15 : 0.0
        if diarize {
            stage(String(localized: "辨識說話者"))
            do {
                output = try await Diarizer.diarize(samples: samples, minSpeakers: options.minSpeakers) { report($0 * share) }
            } catch {
                log.error("diarization failed: \(error.localizedDescription, privacy: .public)")
            }
        }
        guard shouldContinue() else { return nil }
        stage(String(localized: "轉錄"))
        let control = WhisperEngine.Control { report(share + $0 * (1 - share)) }
        setCancel { control.cancel() }
        let chunks = QwenEngine.chunks(samples, turns: output?.turns ?? [], maxSeconds: options.tuning?.qwenChunkSeconds ?? QwenEngine.maxChunkSeconds,
                                       anyChange: options.tuning?.qwenCutsAny ?? false, splitMixed: options.tuning?.qwenSplitMixed ?? true)
        let (language, glossary, retry, useGuard) = (options.language, options.glossary, options.tuning?.qwenRetry ?? true, options.tuning?.qwenGuard ?? true)
        let (modelPath, encoderPath) = (ModelStore.path(.qwen).path, ModelStore.path(.qwenEncoder).path)
        let segments = try await Task.detached(priority: .userInitiated) {
            try QwenEngine.transcribe(samples: samples, chunks: chunks, modelPath: modelPath, encoderPath: encoderPath, language: language,
                                      glossary: glossary, control: control, retry: retry, guard: useGuard)
        }.value
        guard shouldContinue() else { return nil }
        var speakers: [Int?] = segments.map { _ in nil }
        var voices: [Int: SpeakerVoice] = [:]
        if let output {
            (speakers, voices) = await assignSpeakers(output, to: segments, in: samples, source: options.audioSource)
        }
        return TranscriptionResult(segments: segments, speakers: speakers, channels: segments.map { _ in nil }, duration: duration,
                                   voices: voices, seams: chunks.dropFirst().map { Double($0.range.lowerBound) / AudioDecoder.sampleRate },
                                   diarizationQuality: output?.quality)
    }

    /// 已經辨識好說話者：段落依重疊量標說話者、短句再確認（R34）、每位說話者的聲紋（跟 speakers(of:in:source:) 後半一樣）
    private func assignSpeakers(_ output: Diarizer.Output, to segments: [TranscribedSegment], in samples: [Float],
                                source: String) async -> ([Int?], [Int: SpeakerVoice]) {
        var (speakers, numbering) = Diarizer.assignNumbering(output.turns, to: segments)
        do {
            speakers = try await SpeakerRecheck.refine(segments, speakers: speakers, samples: samples)
        } catch {
            log.error("speaker recheck failed: \(error.localizedDescription, privacy: .public)")
        }
        var voices: [Int: SpeakerVoice] = [:]
        for (raw, number) in numbering {
            if let embedding = output.voiceprints[raw] {
                voices[number] = SpeakerVoice(embedding: embedding, seconds: output.speechSeconds[raw] ?? 0, source: source)
            }
        }
        return (speakers, voices)
    }

    // MARK: 分聲道（design §17.3）

    private func runSplit(microphone: [Float], system: [Float]) async throws -> TranscriptionResult? {
        guard shouldContinue() else { return nil }
        let duration = Double(max(microphone.count, system.count)) / AudioDecoder.sampleRate
        let diarize = options.diarize && duration > 5
        let report = progress
        progress(0)

        // 兩個聲道各自轉錄、各自辨識說話者；沒有聲音的聲道（例如沒有播放任何聲音）直接跳過
        var parts: [(channel: Int, segments: [TranscribedSegment], speakers: [Int?], voices: [Int: SpeakerVoice])] = []
        let inputs = [(0, microphone), (1, system)].filter { Self.hasSound($0.1) }
        for (index, (channel, samples)) in inputs.enumerated() {
            let base = Double(index) / Double(inputs.count), width = 1 / Double(inputs.count)
            let share = diarize ? 0.85 : 1.0
            guard let (segments, _) = try await transcribe(samples, from: base, span: width * share) else { return nil }
            var speakers: [Int?] = segments.map { _ in nil }
            var voices: [Int: SpeakerVoice] = [:]
            if diarize && !segments.isEmpty {
                stage(String(localized: "辨識說話者"))
                (speakers, voices, _) = await self.speakers(of: segments, in: samples, source: channel == 0 ? "mic" : "system") {
                    report(base + width * (share + $0 * (1 - share)))
                }
            }
            guard shouldContinue() else { return nil }
            parts.append((channel, segments, speakers, voices))
        }

        // 說話者編號：麥克風那一邊的人先編，電腦聲音那一邊接著編
        var merged: [(segment: TranscribedSegment, speaker: Int?, channel: Int)] = []
        var offsetVoices: [Int: SpeakerVoice] = [:]
        var offset = 0
        for part in parts {
            merged += zip(part.segments, part.speakers).map { ($0, $1.map { $0 + offset }, part.channel) }
            for (number, voice) in part.voices { offsetVoices[number + offset] = voice }
            offset += part.speakers.compactMap { $0 }.max() ?? 0
        }
        if options.echo == .dedupe {
            let systemSegments = merged.filter { $0.channel == 1 }.map(\.segment)
            merged.removeAll { $0.channel == 0 && Self.isEcho($0.segment, of: systemSegments) }
        }
        merged.sort { $0.segment.start < $1.segment.start }
        let segments = merged.map(\.segment)
        // 刪掉回音段落後，說話者編號可能有空號，依出現順序重新編
        var numbering: [Int: Int] = [:]
        let speakers: [Int?] = merged.map { item in
            guard let speaker = item.speaker else { return nil }
            if let number = numbering[speaker] { return number }
            numbering[speaker] = numbering.count + 1
            return numbering.count
        }
        if segments.isEmpty { throw WhisperError.empty }
        var voices: [Int: SpeakerVoice] = [:]
        for (old, new) in numbering { if let voice = offsetVoices[old] { voices[new] = voice } }
        return TranscriptionResult(segments: segments, speakers: speakers, channels: merged.map(\.channel), duration: duration,
                                   voices: voices)
    }

    /// 方均根低於 -70 dBFS 就當作沒有聲音
    private static func hasSound(_ samples: [Float]) -> Bool {
        guard !samples.isEmpty else { return false }
        var sum: Double = 0
        for s in samples { sum += Double(s * s) }
        return (sum / Double(samples.count)).squareRoot() > 3e-4
    }

    /// 麥克風段落跟電腦聲音的某一段時間重疊一半以上、文字也很像，就是喇叭的回音
    nonisolated static func isEcho(_ segment: TranscribedSegment, of system: [TranscribedSegment]) -> Bool {
        let length = max(segment.end - segment.start, 0.01)
        let text = comparable(segment.text)
        guard !text.isEmpty else { return false }
        return system.contains { other in
            let overlap = min(segment.end, other.end) - max(segment.start, other.start)
            guard overlap / length > 0.5 else { return false }
            return similarity(text, comparable(other.text)) > 0.6
        }
    }

    /// 兩句幾乎一樣（最長共同子序列 ÷ 較長那句的長度 > 0.8）；whisper 分段的接縫去掉重複用
    nonisolated static func similar(_ a: String, _ b: String) -> Bool {
        let x = comparable(a), y = comparable(b)
        guard min(x.count, y.count) >= 4 else { return false }
        return similarity(x, y) * Double(min(x.count, y.count)) / Double(max(x.count, y.count)) > 0.8
    }

    private nonisolated static func comparable(_ text: String) -> [Character] {
        text.lowercased().filter { $0.isLetter || $0.isNumber }.map { $0 }
    }

    /// 最長共同子序列 ÷ 較短那段的長度
    private nonisolated static func similarity(_ a: [Character], _ b: [Character]) -> Double {
        guard !a.isEmpty, !b.isEmpty else { return 0 }
        var prev = [Int](repeating: 0, count: b.count + 1)
        for x in a {
            var cur = [Int](repeating: 0, count: b.count + 1)
            for (j, y) in b.enumerated() { cur[j + 1] = x == y ? prev[j] + 1 : max(prev[j + 1], cur[j]) }
            prev = cur
        }
        return Double(prev[b.count]) / Double(min(a.count, b.count))
    }

    // MARK: 共用步驟

    /// 辨識一段聲音；進度換算到整體的 from…from+span。回傳 nil 表示被取消；沒有辨識出任何文字時回傳空陣列。
    /// 另外回傳 whisper 分段的位置（秒）
    private func transcribe(_ samples: [Float], from base: Double, span: Double) async throws -> ([TranscribedSegment], [Double])? {
        let report = progress
        let useSystem = options.engine == .system
        stage(useSystem ? String(localized: "轉錄（macOS 內建）") : String(localized: "轉錄"))
        let segments: [TranscribedSegment]
        var seams: [Double] = []
        do {
            if useSystem {
                let language = options.language
                let task = Task {
                    try await SystemTranscriber.transcribe(samples: samples, language: language) { report(base + $0 * span) }
                }
                setCancel { task.cancel() }
                segments = try await task.value
            } else {
                let control = WhisperEngine.Control { report(base + $0 * span) }
                setCancel { control.cancel() }
                let prompt = WhisperEngine.prompt(glossary: options.glossary, language: options.language, style: options.tuning?.promptStyle)
                let (path, code, chunkSeconds, tuning) = (modelPath, options.language.whisperCode, options.chunkSeconds, options.tuning)
                let chunks: [Range<Int>]
                (segments, chunks) = try await Task.detached(priority: .userInitiated) {
                    let chunks = WhisperEngine.chunks(samples, seconds: chunkSeconds)
                    return (try WhisperEngine.transcribe(samples: samples, chunks: chunks, modelPath: path, prompt: prompt, language: code,
                                                         control: control, tuning: tuning), chunks)
                }.value
                seams = chunks.dropFirst().map { Double($0.lowerBound) / AudioDecoder.sampleRate }
            }
        } catch WhisperError.empty where options.splitChannels {
            // 分聲道時某一邊沒人說話是正常的
            segments = []
        }
        return shouldContinue() ? (segments, seams) : nil
    }

    /// 說話者辨識；失敗不影響逐字稿，回傳全部沒有說話者
    /// 每段的說話者，以及每位說話者的聲紋（換成逐字稿上的編號）
    private func speakers(of segments: [TranscribedSegment], in samples: [Float], source: String,
                          onProgress: @escaping @Sendable (Double) -> Void) async -> ([Int?], [Int: SpeakerVoice], Diarizer.Quality?) {
        do {
            let output = try await Diarizer.diarize(samples: samples, minSpeakers: options.minSpeakers, onProgress: onProgress)
            var (speakers, numbering) = Diarizer.assignNumbering(output.turns, to: segments)
            // 短句再跟每位說話者比一次（R34、design §6.3.2）；模型下載不了或算不出來就照說話者辨識的結果
            do {
                speakers = try await SpeakerRecheck.refine(segments, speakers: speakers, samples: samples)
            } catch {
                log.error("speaker recheck failed: \(error.localizedDescription, privacy: .public)")
            }
            var voices: [Int: SpeakerVoice] = [:]
            for (raw, number) in numbering {
                if let embedding = output.voiceprints[raw] {
                    voices[number] = SpeakerVoice(embedding: embedding, seconds: output.speechSeconds[raw] ?? 0, source: source)
                }
            }
            return (speakers, voices, output.quality)
        } catch {
            log.error("diarization failed: \(error.localizedDescription, privacy: .public)")
            return (segments.map { _ in nil }, [:], nil)
        }
    }
}
