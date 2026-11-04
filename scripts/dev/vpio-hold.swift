import AVFoundation
// vpio-hold [秒數，預設 12]：模擬 Safari 的 WebRTC（Google Meet），對預設麥克風開 macOS 的語音處理（VoiceProcessingIO），
// 開著的時候其他 app 從同一支麥克風幾乎收不到聲音（requirements 第 39 節）。只量音量，不存聲音。
// swiftc -O scripts/dev/vpio-hold.swift -o ~/Library/Caches/ShiyinBuild/vpio-hold
let seconds = Double(CommandLine.arguments.dropFirst().first ?? "12") ?? 12
let engine = AVAudioEngine()
let input = engine.inputNode
do { try input.setVoiceProcessingEnabled(true) } catch { print("vpio: enable failed: \(error)"); exit(1) }
let format = input.outputFormat(forBus: 0)
print("vpio: voice processing on, input \(format.channelCount)ch \(Int(format.sampleRate))Hz"); fflush(stdout)
let lock = NSLock(); var sumSq = 0.0, count = 0
input.installTap(onBus: 0, bufferSize: 4096, format: format) { buffer, _ in
    guard let d = buffer.floatChannelData else { return }
    lock.lock(); for i in 0..<Int(buffer.frameLength) { sumSq += Double(d[0][i] * d[0][i]); count += 1 }; lock.unlock()
}
engine.mainMixerNode.outputVolume = 0
engine.connect(input, to: engine.mainMixerNode, format: format)
do { try engine.start() } catch { print("vpio: start failed: \(error)"); exit(1) }
RunLoop.main.run(until: Date().addingTimeInterval(seconds))
lock.lock(); let rms = count > 0 ? sqrt(sumSq / Double(count)) : 0; lock.unlock()
print(String(format: "vpio: own input rms=%.1f dBFS over %.0fs, stopping", rms > 0 ? 20 * log10(rms) : -999, seconds)); fflush(stdout)
engine.stop()
