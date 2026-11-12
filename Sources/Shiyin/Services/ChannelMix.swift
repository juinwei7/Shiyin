import AVFoundation
import MediaToolbox
import os

/// 雙聲道的錄音（左聲道麥克風、右聲道電腦聲音）播放、匯出 m4a 時混在一起，兩耳聽到的一樣（design §49、R73、D158）。
/// 錄音檔本身不變
enum ChannelMix {
    /// 跟 AVAssetReader 混成單聲道一樣（−3 dB）：只有一邊有聲音時不會小一半
    static let gain: Float = 0.7071

    /// 音軌是 2 個聲道時，回傳裝了混音 tap 的 AVAudioMix；單聲道、讀不到音軌時是 nil（照原樣播）
    static func audioMix(for asset: AVAsset) async -> AVAudioMix? {
        guard let track = try? await asset.loadTracks(withMediaType: .audio).first,
              let formats = try? await track.load(.formatDescriptions),
              formats.first.flatMap({ CMAudioFormatDescriptionGetStreamBasicDescription($0)?.pointee.mChannelsPerFrame }) == 2,
              let tap = makeTap()
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
    }

    private static func makeTap() -> MTAudioProcessingTap? {
        #if DEBUG
        stats.withLock { $0 = Stats() }
        #endif
        var callbacks = MTAudioProcessingTapCallbacks(
            version: kMTAudioProcessingTapCallbacksVersion_0,
            clientInfo: Unmanaged.passRetained(State()).toOpaque(),
            init: { _, clientInfo, storage in storage.pointee = clientInfo },
            finalize: { tap in Unmanaged<State>.fromOpaque(MTAudioProcessingTapGetStorage(tap)).release() },
            prepare: { tap, _, format in
                let state = Unmanaged<State>.fromOpaque(MTAudioProcessingTapGetStorage(tap)).takeUnretainedValue()
                let f = format.pointee
                state.supported = f.mFormatID == kAudioFormatLinearPCM && f.mFormatFlags & kAudioFormatFlagIsFloat != 0
                    && f.mBitsPerChannel == 32 && f.mChannelsPerFrame == 2
                state.interleaved = f.mFormatFlags & kAudioFormatFlagIsNonInterleaved == 0
            },
            unprepare: nil,
            process: { tap, frames, _, bufferList, framesOut, flagsOut in
                guard MTAudioProcessingTapGetSourceAudio(tap, frames, bufferList, flagsOut, nil, framesOut) == noErr else { return }
                let state = Unmanaged<State>.fromOpaque(MTAudioProcessingTapGetStorage(tap)).takeUnretainedValue()
                guard state.supported else { return }
                ChannelMix.mix(UnsafeMutableAudioBufferListPointer(bufferList), frames: Int(framesOut.pointee), interleaved: state.interleaved)
            })
        var tap: MTAudioProcessingTap?
        guard MTAudioProcessingTapCreate(kCFAllocatorDefault, &callbacks, kMTAudioProcessingTapCreationFlag_PreEffects, &tap) == noErr
        else {
            Unmanaged<State>.fromOpaque(callbacks.clientInfo!).release()
            return nil
        }
        return tap
    }

    /// m = (L + R) × 0.7071，超過 ±1 截掉，寫回兩個聲道
    private static func mix(_ buffers: UnsafeMutableAudioBufferListPointer, frames: Int, interleaved: Bool) {
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
        var peaks: (Float, Float, Float) = (0, 0, 0)
        #endif
        for i in 0..<frames {
            let l = left[i * stride], r = right[i * stride]
            let m = min(1, max(-1, (l + r) * gain))
            #if DEBUG
            peaks = (max(peaks.0, abs(l)), max(peaks.1, abs(r)), max(peaks.2, abs(m)))
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
        }
        #endif
    }

    #if DEBUG
    /// 驗證用（design §49.3）：最近建立的 tap 處理了多少取樣、混音前兩個聲道和混音後的峰值
    struct Stats {
        var frames = 0
        var sourcePeaks: [Float] = [0, 0]
        var mixedPeak: Float = 0
    }
    static let stats = OSAllocatedUnfairLock(initialState: Stats())
    #endif
}
