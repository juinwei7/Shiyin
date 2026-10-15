import AVFoundation
import Foundation
import Speech

/// 正式轉錄改用 macOS 內建辨識（design §6.2.1、D11）：不需要 Breeze 模型，速度快很多，但沒有詞彙提示
enum SystemTranscriber {
    static func transcribe(samples: [Float], onProgress: @escaping @Sendable (Double) -> Void) async throws -> [TranscribedSegment] {
        let duration = Double(samples.count) / AudioDecoder.sampleRate
        // 解碼後的 16 kHz 單聲道寫成暫存 WAV，格式轉換交給 SpeechAnalyzer 處理
        let dir = try Paths.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        let url = dir.appendingPathComponent("audio.wav")
        try AudioDecoder.writeWAV(samples, to: url)

        // 跟即時逐字稿共用系統的 zh_TW 模型，還沒有就先下載
        try await LiveTranscriber.ensureAssets()
        let transcriber = SpeechTranscriber(locale: Locale(identifier: "zh_TW"), transcriptionOptions: [],
                                            reportingOptions: [], attributeOptions: [.audioTimeRange])
        let analyzer = SpeechAnalyzer(modules: [transcriber])
        let collector = Task { () -> [TranscribedSegment] in
            var segments: [TranscribedSegment] = []
            for try await result in transcriber.results where result.isFinal {
                let text = String(result.text.characters).trimmingCharacters(in: .whitespacesAndNewlines)
                guard !text.isEmpty else { continue }
                segments.append(TranscribedSegment(start: result.range.start.seconds, end: result.range.end.seconds,
                                                   text: text, lowConfidence: false))
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
}
