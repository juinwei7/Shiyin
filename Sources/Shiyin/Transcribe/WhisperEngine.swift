import Compression
import Foundation
import whisper

struct TranscribedSegment {
    var start: Double
    var end: Double
    var text: String
    var lowConfidence: Bool
}

enum WhisperError: LocalizedError {
    case modelMissing
    case loadFailed
    case failed(Int32)
    case cancelled
    case empty

    var errorDescription: String? {
        switch self {
        case .modelMissing: return String(localized: "還沒有下載轉錄模型，請到設定 → 模型與儲存空間下載")
        case .loadFailed: return String(localized: "轉錄模型載入失敗，模型檔可能已損壞，請到設定重新下載")
        case .failed(let code): return String(localized: "轉錄失敗（whisper 錯誤代碼 \(code)）")
        case .cancelled: return String(localized: "已取消")
        case .empty: return String(localized: "錄音裡沒有辨識到任何語音")
        }
    }
}

/// whisper.cpp 的包裝（design §6.2）。在呼叫端的背景 Task 執行，blocking。
enum WhisperEngine {

    // Whisper 在靜音段常見的幻覺字串，出現在短句時直接丟掉（沿用第二版）
    static let hallucinationPhrases = [
        "字幕由Amara.org社區提供", "字幕由Amara.org社群提供", "字幕由Amara.org社区提供",  // l10n-skip
        "請不吝點贊", "請不吝點讚", "请不吝点赞", "訂閱轉發打賞", "订阅转发打赏",  // l10n-skip
        "明鏡與點點欄目", "明镜与点点栏目", "優優獨播劇場", "优优独播剧场",  // l10n-skip
        "Subtitles by the Amara.org community",
        "ご視聴ありがとうございました", "ご視聴いただきありがとうございました", "チャンネル登録",  // l10n-skip
    ]

    /// 基本提示詞＋詞彙表（R4.1）：依會議語言（design §16.4、§18.2），中文、日文 150 字、英文 400 個字元以內
    static func prompt(glossary: [String], language: MeetingLanguage = .zh, style: String? = nil) -> String {
        let limit = language == .en ? 400 : 150
        // 評估用的另一種寫法（design §17.1）：Whisper 的提示詞是「前面已經講過的話」，寫成像逐字稿的一句話，
        // 詞彙放在句子裡，順便帶出繁體和標點的風格
        if style == "natural" {
            func natural(_ terms: [String]) -> String {
                let list = terms.joined(separator: language == .en ? ", " : "、")   // l10n-skip
                return terms.isEmpty
                    ? language.text(zh: "好，那我們開始今天的會議。", en: "Okay, let's get started with today's meeting.", ja: "はい、では今日の会議を始めます。")  // l10n-skip
                    : language.text(zh: "好，那我們開始今天的會議，先看一下 \(list) 這幾個部分的進度。",
                                    en: "Okay, let's get started with today's meeting. First, a quick update on \(list).",
                                    ja: "はい、では今日の会議を始めます。まず \(list) の進捗を確認しましょう。")  // l10n-skip
            }
            var picked: [String] = []
            for term in glossary {
                if natural(picked + [term]).count > limit { break }
                picked.append(term)
            }
            return natural(picked)
        }
        let base = language.text(zh: "以下是繁體中文的工作會議錄音，會夾雜英文專有名詞。", en: "The following is a recording of a work meeting.",
                                 ja: "以下は仕事の会議の録音です。")  // l10n-skip
        func build(_ terms: [String]) -> String {
            terms.isEmpty ? base : base + language.text(zh: "詞彙：" + terms.joined(separator: "、") + "。",
                                                        en: " Glossary: " + terms.joined(separator: ", ") + ".",
                                                        ja: "用語：" + terms.joined(separator: "、") + "。")  // l10n-skip
        }
        var picked: [String] = []
        for term in glossary {
            if build(picked + [term]).count > limit { break }
            picked.append(term)
        }
        return build(picked)
    }

    /// 傳給 C callback 的狀態：進度與取消
    final class Control: @unchecked Sendable {
        let onProgress: @Sendable (Double) -> Void
        private let lock = NSLock()
        private var cancelled = false
        init(onProgress: @escaping @Sendable (Double) -> Void) { self.onProgress = onProgress }
        func cancel() { lock.withLock { cancelled = true } }
        var isCancelled: Bool { lock.withLock { cancelled } }
    }

    private static let silenceLogs: Void = {
        whisper_log_set({ _, _, _ in }, nil)
    }()

    /// 長錄音分段（design §21.16、D34）：每段約 chunkSeconds 秒，分段點選在預定位置前後 10 秒內最安靜的 0.5 秒。
    /// 最後一段不會短於 2 分鐘（剩下的併進前一段）；chunkSeconds ≤ 0 不分段
    static func chunks(_ samples: [Float], seconds chunkSeconds: Double) -> [Range<Int>] {
        let rate = Int(AudioDecoder.sampleRate)
        let chunk = Int(chunkSeconds * Double(rate))
        guard chunk > 0 else { return [0..<samples.count] }
        var result: [Range<Int>] = []
        var start = 0
        while samples.count - start > chunk + 120 * rate {
            let cut = samples.withUnsafeBufferPointer { quietestCut($0, start: start, target: start + chunk) }
            result.append(start..<cut)
            start = cut
        }
        result.append(start..<samples.count)
        return result
    }

    /// target 前後 10 秒內最安靜的 0.5 秒的中間：每 0.05 秒一個窗，能量（平方和）最小的
    private static func quietestCut(_ samples: UnsafeBufferPointer<Float>, start: Int, target: Int) -> Int {
        let rate = Int(AudioDecoder.sampleRate)
        let search = 10 * rate, window = rate / 2, hop = rate / 20
        let from = max(start + window, target - search), to = min(samples.count - window, target + search)
        var squares = [Double](repeating: 0, count: to - from + 1)
        for i in from..<to { squares[i - from + 1] = squares[i - from] + Double(samples[i] * samples[i]) }
        var best = target, bestEnergy = Double.infinity
        var position = from
        while position + window <= to {
            let energy = squares[position + window - from] - squares[position - from]
            if energy < bestEnergy { bestEnergy = energy; best = position + window / 2 }
            position += hop
        }
        return best
    }

    /// 傳給 progress callback 的狀態：這一段在整體的哪個位置
    private final class ChunkProgress {
        let control: Control
        let base: Double, span: Double
        init(control: Control, base: Double, span: Double) { (self.control, self.base, self.span) = (control, base, span) }
    }

    /// 一次轉錄：模型只載入一次，一段一段送進去；每段用基本提示詞加上前一段最後的文字
    final class Session {
        private let ctx: OpaquePointer
        private let prompt: String, language: String
        let control: Control
        private let tuning: TranscriptionOptions.DecoderTuning?
        private(set) var segments: [TranscribedSegment] = []

        init(modelPath: String, prompt: String, language: String, control: Control, tuning: TranscriptionOptions.DecoderTuning? = nil) throws {
            self.tuning = tuning
            _ = silenceLogs
            guard FileManager.default.fileExists(atPath: modelPath) else { throw WhisperError.modelMissing }
            var contextParams = whisper_context_default_params()
            contextParams.use_gpu = true
            contextParams.flash_attn = true
            guard let ctx = whisper_init_from_file_with_params(modelPath, contextParams) else { throw WhisperError.loadFailed }
            WhisperContexts.opened()
            (self.ctx, self.prompt, self.language, self.control) = (ctx, prompt, language, control)
        }

        deinit {
            whisper_free(ctx)
            WhisperContexts.closed()
        }

        /// 辨識一段：offset 是這一段在整段錄音的第幾個取樣；進度換算到 progressBase…progressBase+progressSpan
        func feed(_ samples: UnsafeBufferPointer<Float>, offset: Int, progressBase: Double, progressSpan: Double) throws {
            guard !samples.isEmpty else { return }
            // 前一段最後的文字接在提示詞後面，跟 whisper 自己連續轉錄時一樣（whisper 只取最後 224 個 token）
            let chunkPrompt = segments.isEmpty ? prompt : prompt + (language == "en" ? " " : "") + WhisperEngine.tail(of: segments, language: language)
            let progress = ChunkProgress(control: control, base: progressBase, span: progressSpan)
            var part = try WhisperEngine.run(ctx, samples: samples, offset: offset, prompt: chunkPrompt, language: language, progress: progress, tuning: tuning)
            if offset > 0, let last = segments.last {
                // 接縫：開頭幾秒跟前一段最後一句重複的（提示詞被念出來）去掉
                let seam = Double(offset) / AudioDecoder.sampleRate
                while let first = part.first, first.start < seam + 3, TranscriptionPipeline.similar(first.text, last.text) { part.removeFirst() }
            }
            segments += part
        }
    }

    /// 辨識已經解碼的整段錄音（分聲道時用）。chunks 是分段的範圍（`chunks(_:seconds:)`）
    static func transcribe(samples: [Float], chunks: [Range<Int>]? = nil, modelPath: String, prompt: String, language: String = "zh",
                           control: Control, tuning: TranscriptionOptions.DecoderTuning? = nil) throws -> [TranscribedSegment] {
        let session = try Session(modelPath: modelPath, prompt: prompt, language: language, control: control, tuning: tuning)
        let total = Double(max(samples.count, 1))
        try samples.withUnsafeBufferPointer { buffer in
            for range in chunks ?? [0..<samples.count] {
                try session.feed(UnsafeBufferPointer(rebasing: buffer[range]), offset: range.lowerBound,
                                 progressBase: Double(range.lowerBound) / total, progressSpan: Double(range.count) / total)
            }
        }
        if session.segments.isEmpty { throw WhisperError.empty }
        return session.segments
    }

    /// 從檔案依序讀、一段一段轉錄，不先解碼整段（design §21.16）。切的位置跟 `chunks(_:seconds:)` 相同。
    /// 回傳逐字稿、分段的位置（第幾個取樣）、總取樣數
    static func transcribe(file url: URL, chunkSeconds: Double, modelPath: String, prompt: String, language: String,
                           control: Control, tuning: TranscriptionOptions.DecoderTuning? = nil) async throws -> (segments: [TranscribedSegment], seams: [Int], sampleCount: Int) {
        let stream = try await AudioDecoder.Stream(url)
        let session = try Session(modelPath: modelPath, prompt: prompt, language: language, control: control, tuning: tuning)
        let rate = Int(AudioDecoder.sampleRate)
        let chunk = Int(chunkSeconds * Double(rate))
        // 剩下超過「一段＋2 分鐘」才切，所以要讀到這麼多才能決定；不分段時讀完整段
        let need = chunk > 0 ? chunk + 120 * rate + 1 : Int.max
        let total = Double(max(stream.estimatedCount, 1))
        var buffer: [Float] = []
        var offset = 0
        var seams: [Int] = []
        while true {
            if control.isCancelled { throw WhisperError.cancelled }
            try stream.fill(&buffer, upTo: need)
            let cut = chunk > 0 && buffer.count >= need
                ? buffer.withUnsafeBufferPointer { quietestCut($0, start: 0, target: chunk) } : buffer.count
            try buffer.withUnsafeBufferPointer { buffer in
                try session.feed(UnsafeBufferPointer(rebasing: buffer[0..<cut]), offset: offset,
                                 progressBase: min(Double(offset) / total, 1), progressSpan: Double(cut) / total)
            }
            buffer.removeFirst(cut)
            offset += cut
            // 沒切（cut 是整個 buffer）時一定已經讀完
            if buffer.isEmpty && stream.finished { break }
            seams.append(offset)
        }
        if session.segments.isEmpty { throw WhisperError.empty }
        return (session.segments, seams, offset)
    }

    /// 前一段最後的文字：中文、日文 120 字、英文 400 個字元以內
    private static func tail(of segments: [TranscribedSegment], language: String) -> String {
        let limit = language == "en" ? 400 : 120
        var text = ""
        for segment in segments.reversed() {
            text = segment.text + (text.isEmpty ? "" : (language == "en" ? " " : "")) + text
            if text.count >= limit { break }
        }
        return String(text.suffix(limit))
    }

    /// 辨識一段；時間點換成整段錄音的時間
    private static func run(_ ctx: OpaquePointer, samples: UnsafeBufferPointer<Float>, offset start: Int, prompt: String, language: String,
                            progress: ChunkProgress, tuning: TranscriptionOptions.DecoderTuning? = nil) throws -> [TranscribedSegment] {
        let control = progress.control
        var params = whisper_full_default_params(WHISPER_SAMPLING_BEAM_SEARCH)
        params.beam_search.beam_size = 5
        params.greedy.best_of = 5
        params.n_threads = Int32(max(2, min(8, ProcessInfo.processInfo.activeProcessorCount - 2)))
        params.print_progress = false
        params.print_realtime = false
        params.print_timestamps = false
        params.print_special = false
        params.suppress_blank = true
        // 評估用的實驗開關（design §17.1）：正式轉錄都是 nil
        if let tuning {
            if let v = tuning.noContext { params.no_context = v }
            if let v = tuning.beamSize { params.beam_search.beam_size = Int32(v); params.greedy.best_of = Int32(v) }
            if let v = tuning.temperatureInc { params.temperature_inc = v }
            if let v = tuning.entropyThold { params.entropy_thold = v }
            if let v = tuning.logprobThold { params.logprob_thold = v }
            if let v = tuning.noSpeechThold { params.no_speech_thold = v }
            if let v = tuning.suppressNst { params.suppress_nst = v }
            if let v = tuning.maxLen, v > 0 { params.max_len = Int32(v); params.split_on_word = true; params.token_timestamps = true }
        }

        let progressPointer = Unmanaged.passUnretained(progress).toOpaque()
        params.progress_callback = { _, _, value, userData in
            guard let userData else { return }
            let progress = Unmanaged<ChunkProgress>.fromOpaque(userData).takeUnretainedValue()
            progress.control.onProgress(min(1, progress.base + Double(value) / 100 * progress.span))
        }
        params.progress_callback_user_data = progressPointer
        params.abort_callback = { userData in
            guard let userData else { return false }
            return Unmanaged<Control>.fromOpaque(userData).takeUnretainedValue().isCancelled
        }
        params.abort_callback_user_data = Unmanaged.passUnretained(control).toOpaque()

        let result: Int32 = withExtendedLifetime(progress) {
            language.withCString { lang in
                prompt.withCString { promptPointer in
                    params.language = lang
                    params.initial_prompt = promptPointer
                    return whisper_full(ctx, params, samples.baseAddress, Int32(samples.count))
                }
            }
        }
        if control.isCancelled { throw WhisperError.cancelled }
        guard result == 0 else { throw WhisperError.failed(result) }

        let offset = Double(start) / AudioDecoder.sampleRate
        let limit = Double(start + samples.count) / AudioDecoder.sampleRate
        let eot = whisper_token_eot(ctx)
        var segments: [TranscribedSegment] = []
        for i in 0..<whisper_full_n_segments(ctx) {
            let text = String(cString: whisper_full_get_segment_text(ctx, i)).trimmingCharacters(in: .whitespacesAndNewlines)
            guard !text.isEmpty else { continue }
            var logSum = 0.0, count = 0
            for j in 0..<whisper_full_n_tokens(ctx, i) where whisper_full_get_token_id(ctx, i, j) < eot {
                logSum += Foundation.log(max(Double(whisper_full_get_token_p(ctx, i, j)), 1e-6))
                count += 1
            }
            let avgLogProb = count > 0 ? logSum / Double(count) : -10
            let noSpeech = Double(whisper_full_get_segment_no_speech_prob(ctx, i))
            // 幾乎確定沒人講話、信心又低：Whisper 對靜音的幻覺
            let keepAll = tuning?.keepAll == true
            if !keepAll && noSpeech > 0.6 && avgLogProb < -1.0 { continue }
            if !keepAll && text.count < 40 && hallucinationPhrases.contains(where: { text.contains($0) }) { continue }
            // 最後一個 30 秒的窗會補零，時間點可能超過這一段的結尾
            segments.append(TranscribedSegment(
                start: min(offset + Double(whisper_full_get_segment_t0(ctx, i)) / 100, limit),
                end: min(offset + Double(whisper_full_get_segment_t1(ctx, i)) / 100, limit),
                text: text,
                lowConfidence: avgLogProb < -1.0 || compressionRatio(text) > 2.4))
        }
        return segments
    }

    /// Whisper 用來判斷重複幻覺的壓縮比：原始位元組數 ÷ zlib 壓縮後的位元組數
    static func compressionRatio(_ text: String) -> Double {
        let input = Array(text.utf8)
        guard input.count > 16 else { return 1 }
        var output = [UInt8](repeating: 0, count: input.count + 64)
        let size = compression_encode_buffer(&output, output.count, input, input.count, nil, COMPRESSION_ZLIB)
        return size > 0 ? Double(input.count) / Double(size) : 1
    }
}

#if DEBUG
extension WhisperEngine {
    /// whisper.cpp 的預設解碼參數（transcribe-eval 的輸出附上，實驗時對照用）
    static func defaultParameters() -> [String: Any] {
        let d = whisper_full_default_params(WHISPER_SAMPLING_BEAM_SEARCH)
        return ["noContext": d.no_context, "temperatureInc": d.temperature_inc, "entropyThold": d.entropy_thold, "logprobThold": d.logprob_thold,
                "noSpeechThold": d.no_speech_thold, "suppressNst": d.suppress_nst, "maxInitialTs": d.max_initial_ts,
                "lengthPenalty": d.length_penalty, "nMaxTextCtx": d.n_max_text_ctx]
    }

    /// dump-golden 用（design-windows §4.1）：前一段最後的文字、下一段的提示詞
    static func debugChunkPrompt(base: String, previous: [TranscribedSegment], language: String) -> String {
        previous.isEmpty ? base : base + (language == "en" ? " " : "") + tail(of: previous, language: language)
    }
}
#endif
