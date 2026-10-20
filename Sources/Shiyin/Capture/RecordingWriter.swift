import AVFoundation
import Foundation

/// 把各音源的 16 kHz 音訊對齊後寫成 WAV（design §5.2），同時把混音後的單聲道交給即時轉錄。
/// 兩個音源時：左聲道麥克風、右聲道系統聲音。暫停時丟掉收到的音訊，所以檔案裡沒有暫停的那一段。
final class RecordingWriter: @unchecked Sendable {
    let channels: Int
    let url: URL
    var onMix: (([Float]) -> Void)?

    private let file: AVAudioFile
    private let lock = NSLock()
    private var pending: [[Float]]
    private var levels: [Float]
    private var silentSeconds: [Double]
    private var paused = false
    private var acceptedFrames: [Int] // 各聲道累計收到的音框（不含暫停）
    private var lastReceived: [Double]  // 各聲道最後一次收到音訊的時間（systemUptime），看門狗用（design §20.2）
    private var timer: DispatchSourceTimer?
    private var writtenFrames = 0          // 只在 queue 上讀寫
    private let queue = DispatchQueue(label: "com.weiwei.shiyin.writer", qos: .userInitiated)
    private var failed = false            // 寫檔失敗過，用 lock 保護
    /// 收音提醒（design §26.1）：各聲道每 100 毫秒以上一格的音量（dBFS），留最近 300 格（30 秒）
    private var frameEnergy: [Float]
    private var frameSamples: [Int]
    private var recent: [[Float]]

    init(url: URL, channels: Int) throws {
        self.url = url
        self.channels = channels
        pending = Array(repeating: [], count: channels)
        levels = Array(repeating: 0, count: channels)
        silentSeconds = Array(repeating: 0, count: channels)
        acceptedFrames = Array(repeating: 0, count: channels)
        frameEnergy = Array(repeating: 0, count: channels)
        frameSamples = Array(repeating: 0, count: channels)
        recent = Array(repeating: [], count: channels)
        lastReceived = Array(repeating: ProcessInfo.processInfo.systemUptime, count: channels)
        file = try AVAudioFile(forWriting: url, settings: [
            AVFormatIDKey: kAudioFormatLinearPCM,
            AVSampleRateKey: CaptureFormat.sampleRate,
            AVNumberOfChannelsKey: channels,
            AVLinearPCMBitDepthKey: 16,
            AVLinearPCMIsFloatKey: false,
            AVLinearPCMIsBigEndianKey: false,
        ], commonFormat: .pcmFormatFloat32, interleaved: false)
    }

    func start() {
        lock.withLock { lastReceived = Array(repeating: ProcessInfo.processInfo.systemUptime, count: channels) }
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + 0.1, repeating: 0.1)
        timer.setEventHandler { [weak self] in self?.drain(final: false) }
        timer.resume()
        self.timer = timer
    }

    /// 音訊執行緒呼叫
    func append(channel: Int, _ samples: [Float]) {
        guard channel < channels, !samples.isEmpty else { return }
        var sum: Float = 0
        for s in samples { sum += s * s }
        let rms = (sum / Float(samples.count)).squareRoot()
        let now = ProcessInfo.processInfo.systemUptime
        lock.withLock {
            lastReceived[channel] = now
            levels[channel] = rms
            silentSeconds[channel] = rms == 0 ? silentSeconds[channel] + Double(samples.count) / CaptureFormat.sampleRate : 0
            guard !paused else { return }
            pending[channel].append(contentsOf: samples)
            acceptedFrames[channel] += samples.count
            frameEnergy[channel] += sum
            frameSamples[channel] += samples.count
            if frameSamples[channel] >= Int(CaptureFormat.sampleRate / 10) {
                let rms = (frameEnergy[channel] / Float(frameSamples[channel])).squareRoot()
                recent[channel].append(20 * log10(max(rms, 1e-6)))
                if recent[channel].count > 300 { recent[channel].removeFirst(recent[channel].count - 300) }
                frameEnergy[channel] = 0
                frameSamples[channel] = 0
            }
        }
    }

    func setPaused(_ value: Bool) {
        lock.withLock {
            paused = value
            if value { for i in 0..<channels { pending[i].removeAll() } }
        }
    }

    /// 目前錄到的秒數（不含暫停），給計時、筆記和重點標記的時間點用
    var recordedSeconds: Double {
        lock.withLock { Double(acceptedFrames.max() ?? 0) / CaptureFormat.sampleRate }
    }

    /// 0～1 的音量（RMS 換成 dB 再正規化）
    func level(_ channel: Int) -> Float {
        let rms = lock.withLock { channel < channels ? levels[channel] : 0 }
        guard rms > 0 else { return 0 }
        return max(0, min(1, (20 * log10(rms) + 60) / 60))
    }

    /// 寫檔失敗了（例如磁碟滿了）：Recorder 看到就停止錄音（design §20.3）
    var writeFailed: Bool { lock.withLock { failed } }

    func silence(_ channel: Int) -> Double { lock.withLock { channel < channels ? silentSeconds[channel] : 0 } }

    /// 最近 30 秒（不含暫停）每 100 毫秒的音量，dBFS（design §26.1）
    func recentLevels(_ channel: Int) -> [Float] { lock.withLock { channel < channels ? recent[channel] : [] } }

    /// 暫停後重新開始：之前的音量不算（暫停前後可能換了地方）
    func resetRecentLevels() { lock.withLock { for i in 0..<channels { recent[i].removeAll(); frameEnergy[i] = 0; frameSamples[i] = 0 } } }

    /// 這個聲道多久沒有送音訊來（秒）。暫停時音源照樣送資料，所以暫停不影響
    func secondsSinceSamples(_ channel: Int) -> Double {
        let now = ProcessInfo.processInfo.systemUptime
        return lock.withLock { channel < channels ? now - lastReceived[channel] : 0 }
    }

    /// 停止並關檔，回傳寫進檔案的秒數（寫檔失敗時是失敗前寫進去的部分，看 `writeFailed`）。
    /// 注意：AVAudioFile 關檔後 length 會變成 0，所以秒數要用自己累計的音框數算
    func finish() -> Double {
        timer?.cancel()
        timer = nil
        let frames = queue.sync { () -> Int in
            drain(final: true)
            return writtenFrames
        }
        file.close()
        return Double(frames) / CaptureFormat.sampleRate
    }

    private func drain(final: Bool) {
        let frames: [[Float]] = lock.withLock {
            if channels == 1 {
                defer { pending[0].removeAll(keepingCapacity: true) }
                return [pending[0]]
            }
            // 兩個音源：取兩邊都有的長度；一邊落後超過 0.5 秒（或收尾時），補靜音讓時間對齊
            var count = pending.map(\.count).min() ?? 0
            let longest = pending.map(\.count).max() ?? 0
            if final || longest - count > Int(CaptureFormat.sampleRate / 2) {
                for i in 0..<channels where pending[i].count < longest {
                    pending[i].append(contentsOf: [Float](repeating: 0, count: longest - pending[i].count))
                }
                count = longest
            }
            let taken = pending.map { Array($0.prefix(count)) }
            for i in 0..<channels { pending[i].removeFirst(count) }
            return taken
        }
        let count = frames[0].count
        guard count > 0, let buffer = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: AVAudioFrameCount(count))
        else { return }
        buffer.frameLength = AVAudioFrameCount(count)
        for (channel, samples) in frames.enumerated() {
            samples.withUnsafeBufferPointer { buffer.floatChannelData![channel].update(from: $0.baseAddress!, count: count) }
        }
        do {
            try file.write(from: buffer)
            writtenFrames += count
        } catch {
            if !writeFailed { log.error("recording write failed: \(error.localizedDescription, privacy: .public)") }
            lock.withLock { failed = true }
        }
        // 即時轉錄用的單聲道混音
        if channels == 1 {
            onMix?(frames[0])
        } else {
            var mix = [Float](repeating: 0, count: count)
            for i in 0..<count { mix[i] = (frames[0][i] + frames[1][i]) * 0.5 }
            onMix?(mix)
        }
    }
}
