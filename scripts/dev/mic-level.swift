import AVFoundation
import CoreAudio
// mic-level [秒數，預設 30]：模擬拾音的 MicrophoneSource：AVAudioEngine 收預設麥克風、沒有語音處理；設定變了就重新開始（同拾音 design §20.2）。
// 每秒印一行：秒數、RMS(dBFS)、最大值、完全是 0 的樣本比例、聲道數、取樣率、各聲道的 RMS。只量音量，不存聲音（requirements 第 39 節）。
// swiftc -O scripts/dev/mic-level.swift -o ~/Library/Caches/ShiyinBuild/mic-level
let seconds = Double(CommandLine.arguments.dropFirst().first ?? "30") ?? 30
let lock = NSLock()
var sumSq = 0.0, count = 0, zeros = 0, peak: Float = 0
var chSq = [Double](repeating: 0, count: 16), chN = 0
var engine = AVAudioEngine()
var observer: NSObjectProtocol?
var fmtDesc = ""
func start() {
    engine = AVAudioEngine()
    let input = engine.inputNode
    let format = input.outputFormat(forBus: 0)
    fmtDesc = "\(format.channelCount)ch \(Int(format.sampleRate))Hz"
    input.installTap(onBus: 0, bufferSize: 4096, format: format) { buffer, _ in
        guard let data = buffer.floatChannelData else { return }
        lock.lock()
        for c in 0..<Int(buffer.format.channelCount) {
            for i in 0..<Int(buffer.frameLength) {
                let v = data[c][i]
                sumSq += Double(v * v); count += 1; chSq[c] += Double(v * v)
                if v == 0 { zeros += 1 }
                if abs(v) > peak { peak = abs(v) }
            }
        }
        chN += Int(buffer.frameLength)
        lock.unlock()
    }
    observer = NotificationCenter.default.addObserver(forName: .AVAudioEngineConfigurationChange, object: engine, queue: .main) { _ in
        print(String(format: "  [config change] restart (was %@)", fmtDesc)); fflush(stdout)
        if let o = observer { NotificationCenter.default.removeObserver(o) }
        engine.inputNode.removeTap(onBus: 0); engine.stop()
        start()
    }
    engine.prepare()
    do { try engine.start() } catch { print("  start failed: \(error)"); fflush(stdout) }
}
print("mic permission:", AVCaptureDevice.authorizationStatus(for: .audio).rawValue, "(3 = authorized)")
if let dev = AVCaptureDevice.default(for: .audio) { print("default input:", dev.localizedName) }
fflush(stdout)
start()
let t0 = Date()
var next = 1.0
while Date().timeIntervalSince(t0) < seconds {
    RunLoop.main.run(until: Date().addingTimeInterval(0.05))
    let t = Date().timeIntervalSince(t0)
    if t >= next {
        lock.lock()
        let rms = count > 0 ? sqrt(sumSq / Double(count)) : 0
        let db = rms > 0 ? 20 * log10(rms) : -999
        let zf = count > 0 ? Double(zeros) / Double(count) : 1
        let chs = chSq.prefix(4).map { chN > 0 && $0 > 0 ? String(format: "%6.1f", 20 * log10(sqrt($0 / Double(chN)))) : "     -" }.joined(separator: " ")
        print(String(format: "t=%2.0f  rms=%7.1f dBFS  peak=%.5f  zeros=%5.1f%%  %@  per-ch: %@", next, db, peak, zf * 100, fmtDesc, chs))
        fflush(stdout)
        sumSq = 0; count = 0; zeros = 0; peak = 0; chSq = [Double](repeating: 0, count: 16); chN = 0
        lock.unlock()
        next += 1
    }
}
