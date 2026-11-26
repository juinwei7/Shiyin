@preconcurrency import AVFoundation
import Foundation
import Speech

/// 錄音中的即時逐字稿（design §5.3）：SpeechAnalyzer，語言依會議語言（§16.4），暫定結果約 0 秒、定稿約 2 秒
@MainActor
final class LiveTranscriber {
    let language: MeetingLanguage
    var onVolatile: ((String) -> Void)?
    var onFinal: ((Double, Double, String) -> Void)?

    private var analyzer: SpeechAnalyzer?
    private var resultsTask: Task<Void, Never>?
    private let feeder = Feeder()

    init(language: MeetingLanguage) {
        self.language = language
    }

    /// 定稿結果依句子、停頓切成幾行（design-ios §7、D88）：iPhone 畫面窄，內建辨識又常常不加句號，一個結果 15～40 秒。
    /// Mac 照舊一個結果一行（SystemTranscriber.phoneStyle）
    static var splitLines: Bool { SystemTranscriber.phoneStyle }

    /// 送音訊的部分，可以從寫檔的執行緒呼叫
    final class Feeder: @unchecked Sendable {
        private let lock = NSLock()
        private var builder: AsyncStream<AnalyzerInput>.Continuation?
        private var converter: AVAudioConverter?
        private var format: AVAudioFormat?
        /// 送進去的聲音的音量，時間跟定稿結果的 audioTimeRange 同一個起點（切行時找停頓用）
        private var loudness = Segmenting.Loudness()
        private var tracksLoudness = false

        func configure(builder: AsyncStream<AnalyzerInput>.Continuation, format: AVAudioFormat, tracksLoudness: Bool = false) {
            lock.withLock {
                self.builder = builder
                self.format = format
                self.tracksLoudness = tracksLoudness
                converter = format == CaptureFormat.mono ? nil : AVAudioConverter(from: CaptureFormat.mono, to: format)
            }
        }

        func feed(_ samples: [Float]) {
            lock.withLock {
                guard let builder, let format, !samples.isEmpty,
                      let input = AVAudioPCMBuffer(pcmFormat: CaptureFormat.mono, frameCapacity: AVAudioFrameCount(samples.count))
                else { return }
                if tracksLoudness { loudness.append(samples) }
                input.frameLength = AVAudioFrameCount(samples.count)
                samples.withUnsafeBufferPointer { input.floatChannelData![0].update(from: $0.baseAddress!, count: samples.count) }
                guard let converter else {
                    builder.yield(AnalyzerInput(buffer: input))
                    return
                }
                let capacity = AVAudioFrameCount(Double(samples.count) * format.sampleRate / CaptureFormat.sampleRate + 64)
                guard let output = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: capacity) else { return }
                var fed = false
                var error: NSError?
                converter.convert(to: output, error: &error) { _, status in
                    if fed { status.pointee = .noDataNow; return nil }
                    fed = true
                    status.pointee = .haveData
                    return input
                }
                if error == nil, output.frameLength > 0 { builder.yield(AnalyzerInput(buffer: output)) }
            }
        }

        func finish() {
            lock.withLock {
                builder?.finish()
                builder = nil
            }
        }

        func splitLong(_ segments: [TranscribedSegment]) -> [TranscribedSegment] {
            lock.withLock { Segmenting.splitLong(segments, loudness: loudness) }
        }

        func quietest(from: Double, to: Double) -> Double? {
            lock.withLock { loudness.quietest(from: from, to: to) }
        }
    }

    nonisolated var feedHandler: ([Float]) -> Void {
        let feeder = feeder
        return { feeder.feed($0) }
    }


    /// 準備 SpeechAnalyzer；系統還沒有 zh_TW 模型時先下載（併入首次使用引導，design §11）
    func start() async throws {
        let transcriber = SpeechTranscriber(locale: language.speechLocale, transcriptionOptions: [],
                                            reportingOptions: [.volatileResults], attributeOptions: [.audioTimeRange])
        try await Self.ensureAssets(language, for: transcriber)
        let analyzer = SpeechAnalyzer(modules: [transcriber])
        guard let format = await SpeechAnalyzer.bestAvailableAudioFormat(compatibleWith: [transcriber]) else {
            throw CaptureError.unsupportedFormat
        }
        let (stream, builder) = AsyncStream<AnalyzerInput>.makeStream()
        let split = Self.splitLines
        feeder.configure(builder: builder, format: format, tracksLoudness: split)
        try await analyzer.start(inputSequence: stream)
        self.analyzer = analyzer
        let feeder = feeder
        let profile = language.profile
        let traditional = profile.transcription.traditional
        resultsTask = Task { [weak self] in
            do {
                for try await result in transcriber.results {
                    // 暫定和定稿結果都先依詞改正繁體字、套語言檔的 scriptFixes（R14.8、design §6.2.2、D201）
                    let content = traditional ? SystemTranscriber.fixVariants(result.text, profile: profile) : result.text
                    let text = String(content.characters).trimmingCharacters(in: .whitespaces)
                    if result.isFinal {
                        let lines = split ? feeder.splitLong(SystemTranscriber.sentences(of: content)) : []
                        if !lines.isEmpty {
                            for line in lines { self?.onFinal?(line.start, line.end, line.text) }
                        } else if !text.isEmpty {
                            self?.onFinal?(result.range.start.seconds, result.range.end.seconds, text)
                        }
                        self?.onVolatile?("")
                        self?.finalizing = nil
                    } else {
                        self?.onVolatile?(text)
                        if split { self?.finalizeEarly(result.range, analyzer: analyzer) }
                    }
                }
            } catch {
                log.error("live transcription ended: \(error.localizedDescription, privacy: .public)")
            }
        }
    }

    /// 請辨識先定稿到哪裡（秒）；定稿的結果回來之前不再請
    private var finalizing: Double?

    /// 還沒定稿的文字超過 8 秒（連續講話時會長到 30 秒、一百多字）：請辨識先定稿到這 8 秒裡最安靜的地方，
    /// 定稿的部分就照上面切成短的幾行（design-ios §7）。還沒定稿的結果只有一整段的時間，沒有每個字的時間，不能自己切
    private func finalizeEarly(_ range: CMTimeRange, analyzer: SpeechAnalyzer) {
        let (start, end) = (range.start.seconds, range.end.seconds)
        guard finalizing == nil, end - start > Segmenting.maxSeconds else { return }
        let limit = start + Segmenting.maxSeconds
        let through = feeder.quietest(from: start + Segmenting.minPart, to: limit) ?? limit
        finalizing = through
        Task { try? await analyzer.finalize(through: CMTime(seconds: through, preferredTimescale: 1000)) }
    }

    func finish() async {
        feeder.finish()
        try? await analyzer?.finalizeAndFinishThroughEndOfInput()
        _ = await resultsTask?.value
        analyzer = nil
        resultsTask = nil
    }

    static func isLocaleInstalled(_ language: MeetingLanguage) async -> Bool {
        await SpeechTranscriber.installedLocales.contains { $0.identifier == language.speechLocale.identifier }
    }

    /// 系統還沒有這個語言的辨識模型時先下載（即時逐字稿和 macOS 內建辨識共用）
    static func ensureAssets(_ language: MeetingLanguage, for transcriber: SpeechTranscriber? = nil) async throws {
        let module = transcriber ?? SpeechTranscriber(locale: language.speechLocale, preset: .transcription)
        if await isLocaleInstalled(language) { return }
        if let request = try await AssetInventory.assetInstallationRequest(supporting: [module]) {
            try await request.downloadAndInstall()
        }
    }
}
