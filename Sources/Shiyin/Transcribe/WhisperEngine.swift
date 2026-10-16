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
    ]

    /// 基本提示詞＋詞彙表（R4.1）：依會議語言（design §16.4），中文 150 字、英文 400 個字元以內
    static func prompt(glossary: [String], language: MeetingLanguage = .zh) -> String {
        let base = language.text(zh: "以下是繁體中文的工作會議錄音，會夾雜英文專有名詞。", en: "The following is a recording of a work meeting.")
        let limit = language == .en ? 400 : 150
        func build(_ terms: [String]) -> String {
            terms.isEmpty ? base : base + language.text(zh: "詞彙：" + terms.joined(separator: "、") + "。",
                                                        en: " Glossary: " + terms.joined(separator: ", ") + ".")
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

    static func transcribe(samples: [Float], modelPath: String, prompt: String, language: String = "zh",
                           control: Control) throws -> [TranscribedSegment] {
        _ = silenceLogs
        guard FileManager.default.fileExists(atPath: modelPath) else { throw WhisperError.modelMissing }
        var contextParams = whisper_context_default_params()
        contextParams.use_gpu = true
        contextParams.flash_attn = true
        guard let ctx = whisper_init_from_file_with_params(modelPath, contextParams) else { throw WhisperError.loadFailed }
        defer { whisper_free(ctx) }

        var params = whisper_full_default_params(WHISPER_SAMPLING_BEAM_SEARCH)
        params.beam_search.beam_size = 5
        params.greedy.best_of = 5
        params.n_threads = Int32(max(2, min(8, ProcessInfo.processInfo.activeProcessorCount - 2)))
        params.print_progress = false
        params.print_realtime = false
        params.print_timestamps = false
        params.print_special = false
        params.suppress_blank = true

        let controlPointer = Unmanaged.passUnretained(control).toOpaque()
        params.progress_callback = { _, _, progress, userData in
            guard let userData else { return }
            Unmanaged<Control>.fromOpaque(userData).takeUnretainedValue().onProgress(Double(progress) / 100)
        }
        params.progress_callback_user_data = controlPointer
        params.abort_callback = { userData in
            guard let userData else { return false }
            return Unmanaged<Control>.fromOpaque(userData).takeUnretainedValue().isCancelled
        }
        params.abort_callback_user_data = controlPointer

        let result: Int32 = language.withCString { lang in
            prompt.withCString { promptPointer in
                params.language = lang
                params.initial_prompt = promptPointer
                return samples.withUnsafeBufferPointer { buffer in
                    whisper_full(ctx, params, buffer.baseAddress, Int32(buffer.count))
                }
            }
        }
        if control.isCancelled { throw WhisperError.cancelled }
        guard result == 0 else { throw WhisperError.failed(result) }

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
            if noSpeech > 0.6 && avgLogProb < -1.0 { continue }
            if text.count < 40 && hallucinationPhrases.contains(where: { text.contains($0) }) { continue }
            segments.append(TranscribedSegment(
                start: Double(whisper_full_get_segment_t0(ctx, i)) / 100,
                end: Double(whisper_full_get_segment_t1(ctx, i)) / 100,
                text: text,
                lowConfidence: avgLogProb < -1.0 || compressionRatio(text) > 2.4))
        }
        if segments.isEmpty { throw WhisperError.empty }
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
