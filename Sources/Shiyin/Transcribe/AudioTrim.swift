import AVFoundation

/// 錄音的前段複製成暫存檔（design §35.3）：沒人說話太久而自動停止的錄音，只轉錄到最後說話的地方
enum AudioTrim {
    /// 前 seconds 秒寫成 32-bit float 的 WAV（聲道、取樣率不變），放在新的暫存資料夾；錄音本來就沒有比這長時回傳 nil
    static func prefix(of url: URL, seconds: Double) throws -> URL? {
        let input = try AVAudioFile(forReading: url)
        let format = input.processingFormat
        let frames = AVAudioFramePosition(seconds * format.sampleRate)
        guard frames > 0, frames < input.length else { return nil }
        let dir = try Paths.makeTempDirectory()
        let output = dir.appendingPathComponent("until.wav")
        let settings: [String: Any] = [AVFormatIDKey: kAudioFormatLinearPCM, AVSampleRateKey: format.sampleRate,
                                       AVNumberOfChannelsKey: format.channelCount, AVLinearPCMBitDepthKey: 32,
                                       AVLinearPCMIsFloatKey: true, AVLinearPCMIsNonInterleaved: false]
        do {
            let file = try AVAudioFile(forWriting: output, settings: settings, commonFormat: format.commonFormat,
                                       interleaved: format.isInterleaved)
            guard let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: 65_536) else { throw CocoaError(.fileWriteUnknown) }
            var remaining = frames
            while remaining > 0 {
                try input.read(into: buffer, frameCount: AVAudioFrameCount(min(Int64(65_536), remaining)))
                if buffer.frameLength == 0 { break }
                try file.write(from: buffer)
                remaining -= AVAudioFramePosition(buffer.frameLength)
            }
        } catch {
            try? FileManager.default.removeItem(at: dir)
            throw error
        }
        return output
    }
}
