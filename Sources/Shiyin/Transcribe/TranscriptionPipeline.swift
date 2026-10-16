import Foundation

/// 轉錄的設定（design §6、§17）。JobRunner 用目前的 app 設定產生；評估工具（shiyinctl transcribe-eval）從 JSON 讀，
/// 同一段錄音可以換設定重跑比較（§17.1）
struct TranscriptionOptions: Codable {
    enum Engine: String, Codable { case breeze, system }
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

    init() {}

    @MainActor
    static func current(language: MeetingLanguage, glossary: [String], channels: String) -> TranscriptionOptions {
        var options = TranscriptionOptions()
        options.engine = AppSettings.useSystemTranscriber ? .system : .breeze
        options.language = language
        options.glossary = glossary
        options.diarize = AppSettings.diarize
        // 只有錄音時選了「麥克風＋電腦聲音」的會議才分聲道；匯入的雙聲道檔案（音樂、影片）照舊混成一軌
        options.splitChannels = AppSettings.splitChannels && channels == "both"
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
    }
}

struct TranscriptionResult {
    var segments: [TranscribedSegment]
    var speakers: [Int?]
    /// 分聲道時每段來自哪個聲道（0 麥克風、1 電腦聲音）；沒分聲道時都是 nil
    var channels: [Int?]
    var duration: Double
}

/// 轉錄流程：解碼 → 辨識 → 說話者辨識（design §6）；雙聲道錄音可以分開轉錄再合併（§17.3）
@MainActor
struct TranscriptionPipeline {
    var options: TranscriptionOptions
    var modelPath = ModelStore.breezePath.path
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
        guard options.engine == .system || FileManager.default.fileExists(atPath: modelPath) else { throw WhisperError.modelMissing }
        stage(String(localized: "讀取錄音"))
        if options.splitChannels {
            let channels = try await AudioDecoder.decodeChannels(audio)
            if channels.count == 2 { return try await runSplit(microphone: channels[0], system: channels[1]) }
        }
        let samples = try await AudioDecoder.decode(audio)
        guard shouldContinue() else { return nil }
        let duration = Double(samples.count) / AudioDecoder.sampleRate
        let diarize = options.diarize && duration > 5
        let share = diarize ? 0.85 : 1.0
        let report = progress
        progress(0)
        guard let transcribed = try await transcribe(samples, from: 0, span: share) else { return nil }
        var speakers: [Int?] = transcribed.map { _ in nil }
        if diarize {
            stage(String(localized: "辨識說話者"))
            speakers = await self.speakers(of: transcribed, in: samples) { report(share + $0 * (1 - share)) }
        }
        guard shouldContinue() else { return nil }
        return TranscriptionResult(segments: transcribed, speakers: speakers, channels: transcribed.map { _ in nil }, duration: duration)
    }

    // MARK: 分聲道（design §17.3）

    private func runSplit(microphone: [Float], system: [Float]) async throws -> TranscriptionResult? {
        guard shouldContinue() else { return nil }
        let duration = Double(max(microphone.count, system.count)) / AudioDecoder.sampleRate
        let diarize = options.diarize && duration > 5
        let report = progress
        progress(0)

        // 兩個聲道各自轉錄、各自辨識說話者；沒有聲音的聲道（例如沒有播放任何聲音）直接跳過
        var parts: [(channel: Int, segments: [TranscribedSegment], speakers: [Int?])] = []
        let inputs = [(0, microphone), (1, system)].filter { Self.hasSound($0.1) }
        for (index, (channel, samples)) in inputs.enumerated() {
            let base = Double(index) / Double(inputs.count), width = 1 / Double(inputs.count)
            let share = diarize ? 0.85 : 1.0
            guard let segments = try await transcribe(samples, from: base, span: width * share) else { return nil }
            var speakers: [Int?] = segments.map { _ in nil }
            if diarize && !segments.isEmpty {
                stage(String(localized: "辨識說話者"))
                speakers = await self.speakers(of: segments, in: samples) { report(base + width * (share + $0 * (1 - share))) }
            }
            guard shouldContinue() else { return nil }
            parts.append((channel, segments, speakers))
        }

        // 說話者編號：麥克風那一邊的人先編，電腦聲音那一邊接著編
        var merged: [(segment: TranscribedSegment, speaker: Int?, channel: Int)] = []
        var offset = 0
        for part in parts {
            merged += zip(part.segments, part.speakers).map { ($0, $1.map { $0 + offset }, part.channel) }
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
        return TranscriptionResult(segments: segments, speakers: speakers, channels: merged.map(\.channel), duration: duration)
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

    /// 辨識一段聲音；進度換算到整體的 from…from+span。回傳 nil 表示被取消；沒有辨識出任何文字時回傳空陣列
    private func transcribe(_ samples: [Float], from base: Double, span: Double) async throws -> [TranscribedSegment]? {
        let report = progress
        let useSystem = options.engine == .system
        stage(useSystem ? String(localized: "轉錄（macOS 內建）") : String(localized: "轉錄"))
        let segments: [TranscribedSegment]
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
                let prompt = WhisperEngine.prompt(glossary: options.glossary, language: options.language)
                let (path, code) = (modelPath, options.language.whisperCode)
                segments = try await Task.detached(priority: .userInitiated) {
                    try WhisperEngine.transcribe(samples: samples, modelPath: path, prompt: prompt, language: code, control: control)
                }.value
            }
        } catch WhisperError.empty where options.splitChannels {
            // 分聲道時某一邊沒人說話是正常的
            segments = []
        }
        return shouldContinue() ? segments : nil
    }

    /// 說話者辨識；失敗不影響逐字稿，回傳全部沒有說話者
    private func speakers(of segments: [TranscribedSegment], in samples: [Float],
                          onProgress: @escaping @Sendable (Double) -> Void) async -> [Int?] {
        do {
            let turns = try await Diarizer.diarize(samples: samples, onProgress: onProgress)
            return Diarizer.assign(turns, to: segments)
        } catch {
            log.error("diarization failed: \(error.localizedDescription, privacy: .public)")
            return segments.map { _ in nil }
        }
    }
}
