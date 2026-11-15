import AVFoundation

/// 錄音的前段複製成暫存檔（design §35.3）：沒人說話太久而自動停止的錄音，只轉錄到最後說話的地方；分割會議切成兩個檔（§64）
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

    /// 從 seconds 切成前段、後段兩個暫存檔（design §64，分割會議）：格式照原本的檔（WAV 還是 WAV，m4a 重新編碼成同樣設定的 AAC），
    /// 聲道數不變。切點不在錄音裡面時回傳 nil
    static func split(_ url: URL, at seconds: Double) throws -> (head: URL, tail: URL)? {
        let input = try AVAudioFile(forReading: url)
        let format = input.processingFormat
        let cut = AVAudioFramePosition(seconds * format.sampleRate)
        guard cut > 0, cut < input.length else { return nil }
        let dir = try Paths.makeTempDirectory()
        let ext = url.pathExtension.lowercased()
        let head = dir.appendingPathComponent("head.\(ext)"), tail = dir.appendingPathComponent("tail.\(ext)")
        do {
            guard let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: 65_536) else { throw CocoaError(.fileWriteUnknown) }
            for (output, frames) in [(head, cut), (tail, input.length - cut)] {
                // 每段一個 AVAudioFile，離開這一圈就關檔（m4a 在關檔時才寫完）
                let file = try AVAudioFile(forWriting: output, settings: input.fileFormat.settings, commonFormat: format.commonFormat,
                                           interleaved: format.isInterleaved)
                var remaining = frames
                while remaining > 0 {
                    try input.read(into: buffer, frameCount: AVAudioFrameCount(min(Int64(65_536), remaining)))
                    if buffer.frameLength == 0 { break }
                    try file.write(from: buffer)
                    remaining -= AVAudioFramePosition(buffer.frameLength)
                }
            }
        } catch {
            try? FileManager.default.removeItem(at: dir)
            throw error
        }
        return (head, tail)
    }
}
