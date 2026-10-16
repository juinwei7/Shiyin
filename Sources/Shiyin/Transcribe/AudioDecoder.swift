import AVFoundation

enum AudioDecoderError: LocalizedError {
    case noAudioTrack
    case unreadable(String)

    var errorDescription: String? {
        switch self {
        case .noAudioTrack: return String(localized: "這個檔案裡沒有聲音")
        case .unreadable(let reason): return String(localized: "讀不到這個錄音檔，可能檔案已損壞或不是支援的格式（\(reason)）")
        }
    }
}

/// 把任何 AVFoundation 讀得到的音訊或影片檔，解碼成 16 kHz 單聲道 Float32（design §6.1）。取代第二版的 ffmpeg。
enum AudioDecoder {
    static let sampleRate: Double = 16_000

    static func decode(_ url: URL) async throws -> [Float] {
        let asset = AVURLAsset(url: url)
        guard let track = try await asset.loadTracks(withMediaType: .audio).first else { throw AudioDecoderError.noAudioTrack }
        let reader: AVAssetReader
        do { reader = try AVAssetReader(asset: asset) } catch { throw AudioDecoderError.unreadable(error.localizedDescription) }
        let output = AVAssetReaderTrackOutput(track: track, outputSettings: [
            AVFormatIDKey: kAudioFormatLinearPCM,
            AVSampleRateKey: sampleRate,
            AVNumberOfChannelsKey: 1,
            AVLinearPCMBitDepthKey: 32,
            AVLinearPCMIsFloatKey: true,
            AVLinearPCMIsBigEndianKey: false,
            AVLinearPCMIsNonInterleaved: false,
        ])
        output.alwaysCopiesSampleData = false
        reader.add(output)
        guard reader.startReading() else {
            throw AudioDecoderError.unreadable(reader.error?.localizedDescription ?? String(localized: "無法開始讀取"))
        }
        var samples: [Float] = []
        if let duration = try? await asset.load(.duration).seconds, duration.isFinite {
            samples.reserveCapacity(Int(duration * sampleRate) + 16_000)
        }
        while let buffer = output.copyNextSampleBuffer() {
            guard let block = CMSampleBufferGetDataBuffer(buffer) else { continue }
            let length = CMBlockBufferGetDataLength(block)
            guard length >= 4 else { continue }
            // block buffer 不一定連續，用複製的方式最保險
            var chunk = [Float](repeating: 0, count: length / 4)
            let status = chunk.withUnsafeMutableBytes {
                CMBlockBufferCopyDataBytes(block, atOffset: 0, dataLength: (length / 4) * 4, destination: $0.baseAddress!)
            }
            if status == kCMBlockBufferNoErr { samples.append(contentsOf: chunk) }
        }
        if reader.status == .failed {
            throw AudioDecoderError.unreadable(reader.error?.localizedDescription ?? String(localized: "讀取中斷"))
        }
        return samples
    }

    /// 寫成 16 kHz 單聲道 16-bit WAV（匯出 wav、macOS 內建辨識的暫存檔都用這個）
    static func writeWAV(_ samples: [Float], to url: URL) throws {
        let file = try AVAudioFile(forWriting: url, settings: [
            AVFormatIDKey: kAudioFormatLinearPCM, AVSampleRateKey: sampleRate, AVNumberOfChannelsKey: 1,
            AVLinearPCMBitDepthKey: 16, AVLinearPCMIsFloatKey: false, AVLinearPCMIsBigEndianKey: false,
        ], commonFormat: .pcmFormatFloat32, interleaved: false)
        var index = 0
        while index < samples.count {
            let count = min(160_000, samples.count - index)
            let buffer = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: AVAudioFrameCount(count))!
            buffer.frameLength = AVAudioFrameCount(count)
            samples[index..<(index + count)].withUnsafeBufferPointer {
                buffer.floatChannelData![0].update(from: $0.baseAddress!, count: count)
            }
            try file.write(from: buffer)
            index += count
        }
        file.close()
    }
}
