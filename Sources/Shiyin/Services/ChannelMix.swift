import AVFoundation
import MediaToolbox
import os

/// 播放時聽哪一邊（R119.2、design §93.4）
enum PlayChannels: String, CaseIterable, Sendable {
    case both, mic, system

    var label: String {
        switch self {
        case .both: String(localized: "兩邊")
        case .mic: String(localized: "只麥克風")
        case .system: String(localized: "只電腦聲音")
        }
    }
}

/// 混音的設定（design §93.4）：左右聲道的音量、麥克風只留建議收錄的區塊（照開始排好、不重疊）
struct ChannelSettings: Sendable, Equatable {
    var micGain: Float = 1
    var systemGain: Float = 1
    var gateMic = false
    var regions: [MicRegions.Range] = []

    init() {}

    init(_ channels: PlayChannels, gateMic: Bool, regions: [MicRegions.Range]) {
        micGain = channels == .system ? 0 : 1
        systemGain = channels == .mic ? 0 : 1
        self.gateMic = gateMic && channels != .system
        self.regions = regions.sorted { $0.start < $1.start }
    }
}

/// 主執行緒改、tap 讀（播放中切換不用重建 AVPlayerItem）
final class ChannelControl: Sendable {
    let settings = OSAllocatedUnfairLock(initialState: ChannelSettings())
}

/// 雙聲道的錄音（左聲道麥克風、右聲道電腦聲音）播放、匯出 m4a 時混在一起，兩耳聽到的一樣（design §49、R73、D158）；
/// 播放時可以只聽一邊、麥克風只播建議收錄（design §93.4）。錄音檔本身不變
enum ChannelMix {
    /// 跟 AVAssetReader 混成單聲道一樣（−3 dB）：只有一邊有聲音時不會小一半
    static let gain: Float = 0.7071
    /// 區塊邊界的淡入淡出（避免爆音）
    nonisolated static let fade = 0.01

    /// 麥克風在 t 秒的音量（0～1）：區塊裡是 1、外面是 0，區塊的頭尾 10 ms 線性淡入淡出。regions 照開始排好、不重疊。
    /// 純邏輯，Windows 同一套（標準答案 util/mic-gate.json）
    nonisolated static func gate(_ t: Double, _ regions: [MicRegions.Range]) -> Double {
        var low = 0, high = regions.count
        while low < high {   // 第一個開始 > t 的區塊
            let mid = (low + high) / 2
            if regions[mid].start <= t { low = mid + 1 } else { high = mid }
        }
        guard low > 0 else { return 0 }
        let r = regions[low - 1]
        guard t < r.end else { return 0 }
        return min(1, max(0, min(t - r.start, r.end - t) / fade))
    }

    /// 匯出 wav 用（design §93.6）：交錯排列的雙聲道（左右左右…）混成單聲道，m = (L × gate(t) + R) × 0.7071，超過 ±1 截掉。
    /// 區塊照時間依序往後找（不是每個取樣都搜尋一次）；progress 有給時每約 10 秒回報一次、看取消（design §94）
    nonisolated static func mixGated(interleaved: [Float], regions: [MicRegions.Range], sampleRate: Double,
                                     progress: ((Double) -> Void)? = nil) throws -> [Float] {
        let sorted = regions.sorted { $0.start < $1.start }
        let count = interleaved.count / 2
        var out = [Float](repeating: 0, count: count)
        let chunk = Int(sampleRate) * 10
        var next = 0   // 第一個還沒結束的區塊
        try interleaved.withUnsafeBufferPointer { source in
            try out.withUnsafeMutableBufferPointer { output in
                var i = 0
                while i < count {
                    let end = min(count, i + chunk)
                    for f in i..<end {
                        let t = Double(f) / sampleRate
                        while next < sorted.count && sorted[next].end <= t { next += 1 }
                        var g: Float = 0
                        if next < sorted.count, t >= sorted[next].start {
                            g = Float(min(1, max(0, min(t - sorted[next].start, sorted[next].end - t) / fade)))
                        }
                        output[f] = min(1, max(-1, (source[2 * f] * g + source[2 * f + 1]) * gain))
                    }
                    i = end
                    if let progress {
                        try Task.checkCancellation()
                        progress(Double(i) / Double(max(1, count)))
                    }
                }
            }
        }
        return out
    }

    /// 音軌是 2 個聲道時，回傳裝了混音 tap 的 AVAudioMix；單聲道、讀不到音軌時是 nil（照原樣播）。
    /// control 沒給時是兩邊都聽、不篩（匯出用）
    static func audioMix(for asset: AVAsset, control: ChannelControl = ChannelControl()) async -> AVAudioMix? {
        guard let track = try? await asset.loadTracks(withMediaType: .audio).first,
              let formats = try? await track.load(.formatDescriptions),
              formats.first.flatMap({ CMAudioFormatDescriptionGetStreamBasicDescription($0)?.pointee.mChannelsPerFrame }) == 2,
              let tap = makeTap(control)
        else { return nil }
        let parameters = AVMutableAudioMixInputParameters(track: track)
        parameters.audioTapProcessor = tap
        let mix = AVMutableAudioMix()
        mix.inputParameters = [parameters]
        return mix
    }

    /// tap 的狀態：prepare 時記下處理的格式，process 只在 2 個聲道的 Float32 時混音
    private final class State {
        var interleaved = false
        var supported = false
        var sampleRate = 0.0
        let control: ChannelControl
        init(_ control: ChannelControl) { self.control = control }
    }

    private static func makeTap(_ control: ChannelControl) -> MTAudioProcessingTap? {
        #if DEBUG
        stats.withLock { $0 = Stats() }
        #endif
        var callbacks = MTAudioProcessingTapCallbacks(
            version: kMTAudioProcessingTapCallbacksVersion_0,
            clientInfo: Unmanaged.passRetained(State(control)).toOpaque(),
            init: { _, clientInfo, storage in storage.pointee = clientInfo },
            finalize: { tap in Unmanaged<State>.fromOpaque(MTAudioProcessingTapGetStorage(tap)).release() },
            prepare: { tap, _, format in
                let state = Unmanaged<State>.fromOpaque(MTAudioProcessingTapGetStorage(tap)).takeUnretainedValue()
                let f = format.pointee
                state.supported = f.mFormatID == kAudioFormatLinearPCM && f.mFormatFlags & kAudioFormatFlagIsFloat != 0
                    && f.mBitsPerChannel == 32 && f.mChannelsPerFrame == 2
                state.interleaved = f.mFormatFlags & kAudioFormatFlagIsNonInterleaved == 0
                state.sampleRate = f.mSampleRate
            },
            unprepare: nil,
            process: { tap, frames, _, bufferList, framesOut, flagsOut in
                var range = CMTimeRange()
                guard MTAudioProcessingTapGetSourceAudio(tap, frames, bufferList, flagsOut, &range, framesOut) == noErr else { return }
                let state = Unmanaged<State>.fromOpaque(MTAudioProcessingTapGetStorage(tap)).takeUnretainedValue()
                guard state.supported else { return }
                let start = range.start.isValid ? range.start.seconds : 0
                ChannelMix.mix(UnsafeMutableAudioBufferListPointer(bufferList), frames: Int(framesOut.pointee), interleaved: state.interleaved,
                               settings: state.control.settings.withLock { $0 }, start: start.isFinite ? start : 0,
                               sampleRate: state.sampleRate > 0 ? state.sampleRate : 16_000)
            })
        var tap: MTAudioProcessingTap?
        guard MTAudioProcessingTapCreate(kCFAllocatorDefault, &callbacks, kMTAudioProcessingTapCreationFlag_PreEffects, &tap) == noErr
        else {
            Unmanaged<State>.fromOpaque(callbacks.clientInfo!).release()
            return nil
        }
        return tap
    }

    /// m = (L × 麥克風音量 × gate(t) + R × 電腦聲音音量) × 0.7071，超過 ±1 截掉，寫回兩個聲道
    private static func mix(_ buffers: UnsafeMutableAudioBufferListPointer, frames: Int, interleaved: Bool,
                            settings: ChannelSettings, start: Double, sampleRate: Double) {
        let left: UnsafeMutablePointer<Float>, right: UnsafeMutablePointer<Float>, stride: Int
        if interleaved {
            guard buffers.count == 1, let data = buffers[0].mData else { return }
            left = data.assumingMemoryBound(to: Float.self)
            right = left + 1
            stride = 2
        } else {
            guard buffers.count == 2, let l = buffers[0].mData, let r = buffers[1].mData else { return }
            left = l.assumingMemoryBound(to: Float.self)
            right = r.assumingMemoryBound(to: Float.self)
            stride = 1
        }
        #if DEBUG
        var peaks: (Float, Float, Float, Float, Float) = (0, 0, 0, 0, 0)
        #endif
        let gating = settings.gateMic && settings.micGain > 0
        for i in 0..<frames {
            let l = left[i * stride], r = right[i * stride]
            let g: Float = gating ? Float(gate(start + Double(i) / sampleRate, settings.regions)) : 1
            let mic = l * settings.micGain * g, system = r * settings.systemGain
            let m = min(1, max(-1, (mic + system) * gain))
            #if DEBUG
            peaks = (max(peaks.0, abs(l)), max(peaks.1, abs(r)), max(peaks.2, abs(m)), max(peaks.3, abs(mic)), max(peaks.4, abs(system)))
            #endif
            left[i * stride] = m
            right[i * stride] = m
        }
        #if DEBUG
        let found = peaks
        stats.withLock {
            $0.frames += frames
            $0.sourcePeaks = [max($0.sourcePeaks[0], found.0), max($0.sourcePeaks[1], found.1)]
            $0.mixedPeak = max($0.mixedPeak, found.2)
            $0.micPeak = max($0.micPeak, found.3)
            $0.systemPeak = max($0.systemPeak, found.4)
            $0.gateMic = gating
            $0.lastTime = start
        }
        #endif
    }

    #if DEBUG
    /// 驗證用（design §49.3、§93.7）：最近建立的 tap 處理了多少取樣、混音前兩個聲道和混音後的峰值、
    /// 篩過之後麥克風和電腦聲音的峰值（resetStats 歸零後重新算）
    struct Stats {
        var frames = 0
        var sourcePeaks: [Float] = [0, 0]
        var mixedPeak: Float = 0
        var micPeak: Float = 0
        var systemPeak: Float = 0
        var gateMic = false
        var lastTime = 0.0
    }
    static let stats = OSAllocatedUnfairLock(initialState: Stats())
    #endif
}
