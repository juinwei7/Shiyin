import Foundation
import Observation

/// 錄一段自己的聲音（design §56.2、R80）：用錄音同一支麥克風收 16 kHz 單聲道，最多 40 秒；
/// 有聲音的部分到 10 秒才能存。錄音只在記憶體裡，算完聲紋就丟（R80.5）
@MainActor
@Observable
final class VoiceEnrollment {
    enum Phase: Equatable {
        case idle, recording, computing
        case failed(String)
    }

    static let maxSeconds = 40.0
    static let minVoicedSeconds = 10.0
    /// 高於這個音量（dBFS）的 100 毫秒算有聲音
    static let voicedLevel: Float = -50

    private(set) var phase: Phase = .idle
    private(set) var seconds = 0.0
    private(set) var voicedSeconds = 0.0
    /// 麥克風完全沒有聲音超過 3 秒（同收音提醒 R24.3）
    private(set) var micSilent = false
    /// 音量（0～1）：音量條自己定時讀，不讓 SwiftUI 觀察（design §25）
    @ObservationIgnored private(set) var level: Float = 0

    @ObservationIgnored private var source: MicrophoneSource?
    @ObservationIgnored private var timer: Timer?
    @ObservationIgnored private let buffer = SampleBuffer()
    @ObservationIgnored private var processed = 0
    @ObservationIgnored private var zeroSeconds = 0.0

    var canFinish: Bool { phase == .recording && voicedSeconds >= Self.minVoicedSeconds }

    func start() {
        stopCapture()
        buffer.reset()
        (processed, seconds, voicedSeconds, zeroSeconds, level, micSilent) = (0, 0, 0, 0, 0, false)
        let source = MicrophoneSource(deviceUID: AppSettings.inputDeviceUID)
        let buffer = buffer
        source.onSamples = { buffer.append($0) }
        do {
            try source.start()
        } catch {
            phase = .failed(String(localized: "沒辦法開始錄音：\(error.localizedDescription)"))
            return
        }
        self.source = source
        phase = .recording
        timer = Timer.scheduledTimer(withTimeInterval: 0.1, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.tick() }
        }
    }

    /// 每 100 毫秒處理新進來的音訊：算音量、有聲音的秒數；到 40 秒自動停（R80.3）
    private func tick() {
        let frame = Int(CaptureFormat.sampleRate / 10)
        let samples = buffer.snapshot()
        while processed + frame <= samples.count {
            let slice = samples[processed..<processed + frame]
            let rms = sqrt(slice.reduce(0) { $0 + $1 * $1 } / Float(frame))
            let db = rms > 0 ? 20 * log10(rms) : -120
            if db > Self.voicedLevel { voicedSeconds += 0.1 }
            zeroSeconds = rms == 0 ? zeroSeconds + 0.1 : 0
            level = max(0, min(1, (db + 60) / 60))
            processed += frame
        }
        seconds = Double(samples.count) / CaptureFormat.sampleRate
        let silent = zeroSeconds >= 3
        if silent != micSilent { micSilent = silent }
        if seconds >= Self.maxSeconds { stopCapture() }
    }

    func cancel() {
        stopCapture()
        buffer.reset()
        phase = .idle
    }

    /// 按「完成」：停止收音，算聲紋、存成「我」。回傳是否存好了
    func finish(name: String, model: AppModel) async -> Bool {
        stopCapture()
        guard voicedSeconds >= Self.minVoicedSeconds else {
            phase = .failed(String(localized: "再多唸一點：有聲音的部分要 10 秒以上。"))
            return false
        }
        phase = .computing
        let samples = buffer.snapshot()
        buffer.reset()
        do {
            try await model.enrollVoice(name: name, samples: samples)
            phase = .idle
            return true
        } catch {
            phase = .failed(error.localizedDescription)
            return false
        }
    }

    private func stopCapture() {
        timer?.invalidate()
        timer = nil
        source?.stop()
        source = nil
        if phase == .recording { phase = .idle }
    }

    /// 從錄好的音訊算聲紋（DEBUG 的 enrollvoice 也走這裡）：說話秒數最多的那一位，要 ≥ 8 秒（同比對的下限）。
    /// 用另外一個辨識器，不跟轉錄線共用（轉錄線可能正在辨識說話者，design §56.2）
    nonisolated static func voiceprint(_ samples: [Float]) async throws -> [Float] {
        let output = try await Diarizer.diarize(samples: samples, config: Diarizer.config()) { _ in }
        guard let best = output.speechSeconds.max(by: { $0.value < $1.value }),
              best.value >= AppDatabase.voiceprintMinimumSeconds, let embedding = output.voiceprints[best.key] else {
            throw EnrollmentError.tooShort
        }
        return embedding
    }

    /// 要唸的文字（R80.1）：跟著介面語言
    static var passage: String {
        String(localized: "拾音會在這台 Mac 上把會議錄下來，轉成逐字稿，再整理成會議紀錄。我現在唸這一段，是要讓拾音記住我的聲音。之後開會的時候，拾音聽到這個聲音，就會在逐字稿上標出我的名字。聲音特徵只存在這台電腦裡，不會上傳。下午三點，我們要討論下一季的計畫，請大家先看一下上週的數字。")
    }
}

enum EnrollmentError: LocalizedError {
    case tooShort

    var errorDescription: String? {
        String(localized: "沒聽清楚你的聲音：請在安靜的地方再錄一次，照著畫面上的文字唸完。")
    }
}

/// 音訊執行緒送進來的樣本（上鎖）
final class SampleBuffer: @unchecked Sendable {
    private let lock = NSLock()
    private var samples: [Float] = []

    func append(_ chunk: [Float]) { lock.withLock { samples += chunk } }
    func snapshot() -> [Float] { lock.withLock { samples } }
    func reset() { lock.withLock { samples = [] } }
}
