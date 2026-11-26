import AVFoundation
import Foundation
import Speech

/// 正式轉錄改用 macOS 內建辨識（design §6.2.1、D11）：不需要 Breeze 模型，速度快很多，但沒有詞彙提示
enum SystemTranscriber {
    /// iPhone 的內建辨識做法（design-ios §7、§8）：段落和即時逐字稿切細（D88）。
    /// Mac 照舊；Mac 的 DEBUG 版可以用 -debugPhoneTranscription YES 打開，用同一份程式驗證。
    /// 依句子切段（R14.7）、繁體字依詞改正（R14.8）兩邊都做
    static var phoneStyle: Bool {
        #if os(iOS)
        true
        #elseif DEBUG
        UserDefaults.standard.bool(forKey: "debugPhoneTranscription")
        #else
        false
        #endif
    }

    /// 繁體字依詞改正（R14.8、design §6.2.2）：一個字換一個字，每個字的時間不變
    static func fixVariants(_ text: AttributedString) -> AttributedString {
        ChineseVariants.fix(text)
    }

    static func transcribe(samples: [Float], language: MeetingLanguage,
                           onProgress: @escaping @Sendable (Double) -> Void) async throws -> [TranscribedSegment] {
        let duration = Double(samples.count) / AudioDecoder.sampleRate
        // 解碼後的 16 kHz 單聲道寫成暫存 WAV，格式轉換交給 SpeechAnalyzer 處理
        let dir = try Paths.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        let url = dir.appendingPathComponent("audio.wav")
        try AudioDecoder.writeWAV(samples, to: url)

        // 跟即時逐字稿共用系統的辨識模型，還沒有就先下載
        try await LiveTranscriber.ensureAssets(language)
        let transcriber = SpeechTranscriber(locale: language.speechLocale, transcriptionOptions: [],
                                            reportingOptions: [], attributeOptions: [.audioTimeRange])
        let analyzer = SpeechAnalyzer(modules: [transcriber])
        let traditional = language.profile.transcription.traditional
        let collector = Task { () -> [TranscribedSegment] in
            var segments: [TranscribedSegment] = []
            for try await result in transcriber.results where result.isFinal {
                // 繁體字依詞改正（R14.8），再依句子切段、留每個字的時間（R14.7）
                segments += sentences(of: traditional ? fixVariants(result.text) : result.text)
                if duration > 0 { onProgress(min(1, result.range.end.seconds / duration)) }
            }
            return segments
        }
        let file = try AVAudioFile(forReading: url)
        try await withTaskCancellationHandler {
            try await analyzer.start(inputAudioFile: file, finishAfterFile: true)
            _ = try await collector.value
        } onCancel: {
            collector.cancel()
            Task { await analyzer.cancelAndFinishNow() }
        }
        try Task.checkCancellation()
        return try await collector.value
    }

    /// 一個定稿結果常常有好幾句、跨過換人說話的地方（平均 12 秒）。用每個字的時間在句尾切開，
    /// 說話者辨識才標得準（R14.7、design §6.2.1）。每段留著每個字的時間，iPhone 再切細（Segmenting、design-ios D88）
    static func sentences(of text: AttributedString) -> [TranscribedSegment] {
        var segments: [TranscribedSegment] = []
        var piece = "", start: Double?, end: Double?
        var words: [TranscribedWord] = []
        var untimed = ""   // 還沒有字之前、沒有時間的文字（空白、標點），併進下一個字
        func close() {
            let trimmed = piece.trimmingCharacters(in: .whitespacesAndNewlines)
            if !trimmed.isEmpty, let start, let end {
                segments.append(TranscribedSegment(start: start, end: end, text: trimmed, lowConfidence: false, words: words))
            } else if !trimmed.isEmpty, var last = segments.popLast() {
                // 沒有時間的尾巴（標點）併進前一句
                last.text += trimmed
                if !last.words.isEmpty { last.words[last.words.count - 1].text += trimmed }
                segments.append(last)
            }
            piece = ""; start = nil; end = nil; words = []; untimed = ""
        }
        for run in text.runs {
            let part = String(text[run.range].characters)
            if let range = run.audioTimeRange {
                if start == nil { start = range.start.seconds }
                end = range.end.seconds
                words.append(TranscribedWord(start: range.start.seconds, end: range.end.seconds, text: untimed + part))
                untimed = ""
            } else if !words.isEmpty {
                // 沒有時間的標點、空白跟著前一個字
                words[words.count - 1].text += part
            } else {
                untimed += part
            }
            piece += part
            if let last = part.trimmingCharacters(in: .whitespaces).last, "。？！?!.".contains(last) { close() }  // l10n-skip：句尾標點
        }
        close()
        return segments
    }
}
