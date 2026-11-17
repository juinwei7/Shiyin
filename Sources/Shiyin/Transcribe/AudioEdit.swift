import AVFoundation

/// 編輯會議的錄音（R107、design §81.2）：照片段接成新的錄音；編輯畫面的波形
enum AudioEdit {
    enum EditError: LocalizedError {
        case unreadable
        var errorDescription: String? { String(localized: "讀不到錄音檔") }
    }

    /// 照 plan 的片段接起來，寫成暫存檔（新的暫存資料夾裡）。sources 是每場會議的錄音，沒有錄音的那幾段補成安靜；
    /// 全部都沒有錄音時回傳 nil。只有一場、而且是 WAV：照原本的格式逐個取樣接（同 AudioTrim.splitWAV）；
    /// 其他：解碼成 32-bit float，取樣率取高的、聲道取多的（單聲道放到兩邊），寫成 m4a（同壓縮工作，D172）
    static func compose(_ plan: EditPlan, sources: [Int64: URL]) async throws -> URL? {
        let used = plan.pieces.map(\.meetingId).reduce(into: [Int64]()) { if !$0.contains($1) { $0.append($1) } }
        let urls = used.compactMap { sources[$0] }
        guard !urls.isEmpty, plan.duration > 0 else { return nil }
        let dir = try Paths.makeTempDirectory()
        do {
            if used.count == 1, let url = urls.first, url.pathExtension.lowercased() == "wav" {
                return try composeWAV(plan, from: url, in: dir)
            }
            var rate = 0.0
            var channels: AVAudioChannelCount = 1
            for url in urls {
                let info = try await trackInfo(AVURLAsset(url: url))
                rate = max(rate, info.rate)
                channels = max(channels, info.channels)
            }
            guard rate > 0, let format = AVAudioFormat(standardFormatWithSampleRate: rate, channels: min(channels, 2)) else {
                throw EditError.unreadable
            }
            let wav = dir.appendingPathComponent("edit.wav")
            let settings: [String: Any] = [AVFormatIDKey: kAudioFormatLinearPCM, AVSampleRateKey: rate, AVNumberOfChannelsKey: format.channelCount,
                                           AVLinearPCMBitDepthKey: 32, AVLinearPCMIsFloatKey: true, AVLinearPCMIsNonInterleaved: false]
            do {
                // 寫完離開這一圈就關檔
                let file = try AVAudioFile(forWriting: wav, settings: settings, commonFormat: .pcmFormatFloat32, interleaved: false)
                for piece in plan.pieces {
                    if let url = sources[piece.meetingId] {
                        try await append(piece, from: url, to: file, format: format)
                    } else {
                        try writeSilence(frames: Int((piece.length * rate).rounded()), to: file, format: format)
                    }
                }
            }
            let output = dir.appendingPathComponent("edit.m4a")
            guard let export = AVAssetExportSession(asset: AVURLAsset(url: wav), presetName: AVAssetExportPresetAppleM4A) else {
                throw CocoaError(.fileWriteUnknown)
            }
            try await export.export(to: output, as: .m4a)
            try? FileManager.default.removeItem(at: wav)
            return output
        } catch {
            try? FileManager.default.removeItem(at: dir)
            throw error
        }
    }

    private static func composeWAV(_ plan: EditPlan, from url: URL, in dir: URL) throws -> URL {
        let input = try AVAudioFile(forReading: url)
        let format = input.processingFormat
        let output = dir.appendingPathComponent("edit.wav")
        guard let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: 65_536) else { throw CocoaError(.fileWriteUnknown) }
        let file = try AVAudioFile(forWriting: output, settings: input.fileFormat.settings, commonFormat: format.commonFormat,
                                   interleaved: format.isInterleaved)
        for piece in plan.pieces {
            let start = AVAudioFramePosition(piece.start * format.sampleRate)
            var remaining = min(AVAudioFramePosition(piece.length * format.sampleRate), input.length - start)
            guard start < input.length, remaining > 0 else { continue }
            input.framePosition = start
            while remaining > 0 {
                try input.read(into: buffer, frameCount: AVAudioFrameCount(min(Int64(65_536), remaining)))
                if buffer.frameLength == 0 { break }
                try file.write(from: buffer)
                remaining -= AVAudioFramePosition(buffer.frameLength)
            }
        }
        return output
    }

    /// 音軌要跟讀它的 AVAssetReader 是同一個 asset（不同的 asset 加 output 會丟 Objective-C 例外、整個 app 當掉）
    private static func trackInfo(_ asset: AVURLAsset) async throws -> (track: AVAssetTrack, rate: Double, channels: AVAudioChannelCount) {
        guard let track = try await asset.loadTracks(withMediaType: .audio).first else { throw EditError.unreadable }
        let descriptions = try await track.load(.formatDescriptions)
        guard let asbd = descriptions.first.flatMap({ CMAudioFormatDescriptionGetStreamBasicDescription($0)?.pointee }) else {
            throw EditError.unreadable
        }
        return (track, asbd.mSampleRate, max(1, asbd.mChannelsPerFrame))
    }

    /// 讀一段（解碼、換取樣率），聲道少的複製到其他聲道；錄音比片段短時補安靜，新的錄音長度才會跟時間軸一致
    private static func append(_ piece: EditPlan.Piece, from url: URL, to file: AVAudioFile, format: AVAudioFormat) async throws {
        let expected = Int((piece.length * format.sampleRate).rounded())
        let asset = AVURLAsset(url: url)
        let info = try await trackInfo(asset)
        let duration = try await asset.load(.duration).seconds
        var written = 0
        if piece.start < duration {
            let reader = try AVAssetReader(asset: asset)
            reader.timeRange = CMTimeRange(start: CMTime(seconds: piece.start, preferredTimescale: 48_000),
                                           end: CMTime(seconds: min(piece.end, duration), preferredTimescale: 48_000))
            let source = Int(info.channels)
            let output = AVAssetReaderTrackOutput(track: info.track, outputSettings: [
                AVFormatIDKey: kAudioFormatLinearPCM, AVSampleRateKey: format.sampleRate, AVNumberOfChannelsKey: source,
                AVLinearPCMBitDepthKey: 32, AVLinearPCMIsFloatKey: true, AVLinearPCMIsNonInterleaved: false, AVLinearPCMIsBigEndianKey: false,
            ])
            reader.add(output)
            guard reader.startReading() else { throw reader.error ?? EditError.unreadable }
            while written < expected, let sample = output.copyNextSampleBuffer() {
                let frames = min(CMSampleBufferGetNumSamples(sample), expected - written)
                guard frames > 0, let block = CMSampleBufferGetDataBuffer(sample),
                      let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(frames)),
                      let channels = buffer.floatChannelData else { continue }
                var samples = [Float](repeating: 0, count: frames * source)
                guard CMBlockBufferCopyDataBytes(block, atOffset: 0, dataLength: samples.count * 4, destination: &samples) == kCMBlockBufferNoErr
                else { throw EditError.unreadable }
                buffer.frameLength = AVAudioFrameCount(frames)
                for c in 0..<Int(format.channelCount) {
                    let from = min(c, source - 1), destination = channels[c]
                    for f in 0..<frames { destination[f] = samples[f * source + from] }
                }
                try file.write(from: buffer)
                written += frames
            }
            reader.cancelReading()
            if reader.status == .failed { throw reader.error ?? EditError.unreadable }
        }
        if written < expected { try writeSilence(frames: expected - written, to: file, format: format) }
    }

    private static func writeSilence(frames: Int, to file: AVAudioFile, format: AVAudioFormat) throws {
        var remaining = frames
        guard let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: 65_536) else { throw CocoaError(.fileWriteUnknown) }
        while remaining > 0 {
            let n = min(65_536, remaining)
            buffer.frameLength = AVAudioFrameCount(n)
            if let channels = buffer.floatChannelData {
                for c in 0..<Int(format.channelCount) { channels[c].update(repeating: 0, count: n) }
            }
            try file.write(from: buffer)
            remaining -= n
        }
    }

    /// 編輯畫面的波形（§81.5）：每 step 秒一個峰值（0～1，取絕對值的最大），單聲道、8 kHz 解碼就夠
    static func peaks(_ url: URL, step: Double = 0.05) async throws -> [Float] {
        let asset = AVURLAsset(url: url)
        guard let track = try await asset.loadTracks(withMediaType: .audio).first else { throw EditError.unreadable }
        let rate = 8_000.0
        let reader = try AVAssetReader(asset: asset)
        let output = AVAssetReaderTrackOutput(track: track, outputSettings: [
            AVFormatIDKey: kAudioFormatLinearPCM, AVSampleRateKey: rate, AVNumberOfChannelsKey: 1,
            AVLinearPCMBitDepthKey: 32, AVLinearPCMIsFloatKey: true, AVLinearPCMIsNonInterleaved: false, AVLinearPCMIsBigEndianKey: false,
        ])
        reader.add(output)
        guard reader.startReading() else { throw reader.error ?? EditError.unreadable }
        let bucket = max(1, Int(rate * step))
        var peaks: [Float] = []
        var current: Float = 0, count = 0
        while let sample = output.copyNextSampleBuffer() {
            try Task.checkCancellation()
            let frames = CMSampleBufferGetNumSamples(sample)
            guard frames > 0, let block = CMSampleBufferGetDataBuffer(sample) else { continue }
            var samples = [Float](repeating: 0, count: frames)
            guard CMBlockBufferCopyDataBytes(block, atOffset: 0, dataLength: frames * 4, destination: &samples) == kCMBlockBufferNoErr else { continue }
            for value in samples {
                current = max(current, abs(value))
                count += 1
                if count == bucket { peaks.append(min(1, current)); current = 0; count = 0 }
            }
        }
        if count > 0 { peaks.append(min(1, current)) }
        if reader.status == .failed { throw reader.error ?? EditError.unreadable }
        return peaks
    }
}
