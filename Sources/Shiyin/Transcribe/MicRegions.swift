import AVFoundation
import Foundation

/// 麥克風的「建議收錄」（R119.3、design §93.2、D201）：用麥克風聲道前 1 分鐘有聲音部分的平均音量當基準，
/// 明顯超過的段落標成區塊。detect 是純邏輯，Windows 同一套（標準答案 pipeline/mic-regions.json）
enum MicRegions {
    /// 規則都用「格」（100 ms）算，最後才換成秒，兩個平台算出來的數字一樣
    enum Rules {
        static let frameSeconds = 0.1
        static let baselineFrames = 600       // 前 1 分鐘（有聲音的格子）
        static let minVoicedFrames = 100      // 有聲音的不到 10 秒就不偵測
        static let marginDb = 6.0
        static let minThresholdDb = -50.0
        static let silentDb = -90.0           // 每格音量的下限
        /// 這以下當成麥克風靜音（硬體、系統靜音、m4a 裡暫停那段的雜訊）：不算進基準、不算有聲音（design §93.2）
        static let mutedDb = -80.0
        static let joinGapFrames = 50         // 間隔不到 5 秒的接起來
        static let minLengthFrames = 4        // 接起來後（補之前）短於 0.4 秒的丟掉
        static let padBeforeFrames = 3
        static let padAfterFrames = 5
        /// 整格的取樣都比這個小就當成全是 0（暫停麥克風、硬體靜音）
        static let zeroSample: Float = 1e-6
    }

    struct Result: Equatable {
        /// 基準（dBFS）；全部沒有聲音時是 silentDb
        var floorDb: Double
        /// 門檻；nil 是有聲音的太少、沒偵測（tooLittle）
        var thresholdDb: Double?
        var regions: [Range]

        var tooLittle: Bool { thresholdDb == nil }
    }

    struct Range: Equatable, Hashable { var start: Double; var end: Double }

    /// 每 100 ms 一格的音量（dBFS，下限 silentDb）；整格是 0 的是 nil。只讀左聲道（麥克風）
    nonisolated static func levels(_ url: URL) throws -> [Double?] {
        let file = try AVAudioFile(forReading: url)
        let format = file.processingFormat
        let frameLength = max(1, Int((format.sampleRate * Rules.frameSeconds).rounded()))
        guard let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(frameLength * 100)) else { return [] }
        var out: [Double?] = []
        var sum = 0.0, count = 0, loud = false
        func flush() {
            guard count > 0 else { return }
            out.append(loud ? max(Rules.silentDb, 10 * log10(max(sum / Double(count), 1e-12))) : nil)
            sum = 0; count = 0; loud = false
        }
        // 讀到結尾時 read 會丟錯（不是回傳 0 個），用位置判斷讀完了沒
        while file.framePosition < file.length {
            try file.read(into: buffer)
            let n = Int(buffer.frameLength)
            guard n > 0, let channel = buffer.floatChannelData?[0] else { break }
            for i in 0..<n {
                let x = channel[i]
                if abs(x) >= Rules.zeroSample { loud = true }
                sum += Double(x) * Double(x)
                count += 1
                if count == frameLength { flush() }
            }
        }
        flush()
        return out
    }

    /// threshold：使用者自己設的門檻（R119.12），給了就用它、不套 −50 的下限；基準照算
    nonisolated static func detect(_ levels: [Double?], threshold userThreshold: Double? = nil) -> Result {
        let voiced = levels.compactMap { $0 }.filter { $0 > Rules.mutedDb }
        guard voiced.count >= Rules.minVoicedFrames else {
            return Result(floorDb: voiced.isEmpty ? Rules.silentDb : mean(voiced), thresholdDb: nil, regions: [])
        }
        let floor = mean(Array(voiced.prefix(Rules.baselineFrames)))
        let threshold = userThreshold ?? max(floor + Rules.marginDb, Rules.minThresholdDb)

        // 超過門檻的格子連成一段（[開始, 結束)，單位是格）
        var runs: [(Int, Int)] = []
        for (i, level) in levels.enumerated() {
            guard let level, level > threshold else { continue }
            if let last = runs.last, last.1 == i { runs[runs.count - 1].1 = i + 1 } else { runs.append((i, i + 1)) }
        }
        var joined: [(Int, Int)] = []
        for run in runs {
            if let last = joined.last, run.0 - last.1 < Rules.joinGapFrames { joined[joined.count - 1].1 = run.1 } else { joined.append(run) }
        }
        var padded: [(Int, Int)] = []
        for run in joined where run.1 - run.0 >= Rules.minLengthFrames {
            let s = max(0, run.0 - Rules.padBeforeFrames), e = min(levels.count, run.1 + Rules.padAfterFrames)
            if let last = padded.last, s <= last.1 { padded[padded.count - 1].1 = max(last.1, e) } else { padded.append((s, e)) }
        }
        return Result(floorDb: floor, thresholdDb: threshold,
                      regions: padded.map { Range(start: Double($0.0) / 10, end: Double($0.1) / 10) })
    }

    nonisolated private static func mean(_ values: [Double]) -> Double {
        values.reduce(0, +) / Double(values.count)
    }

    /// 區塊照開始排好，重疊或間隔不到 gap 的接起來；接起來的有一塊是使用者的就算使用者的
    nonisolated static func normalized(_ regions: [MicRegion], gap: Double = 0) -> [MicRegion] {
        var out: [MicRegion] = []
        for r in regions.sorted(by: { ($0.start, $0.end) < ($1.start, $1.end) }) where r.end > r.start {
            if var last = out.last, r.start - last.end <= gap {
                last.end = max(last.end, r.end)
                if r.origin == MicRegion.user { last.origin = MicRegion.user }
                out[out.count - 1] = last
            } else {
                out.append(r)
            }
        }
        return out
    }

    /// 讀錄音、偵測、寫進資料庫（錄音停止後轉錄線的第一步、匯入沒有區塊的封包、時間軸的「重新偵測」）。
    /// 不是「麥克風＋電腦聲音」、沒有錄音時不做
    nonisolated static func run(_ meetingId: Int64, db: AppDatabase) throws {
        guard let meeting = try db.meeting(meetingId), meeting.channels == "both", meeting.hasAudio else { return }
        let result = detect(try levels(meeting.audioURL), threshold: meeting.micThresholdUser)
        try db.saveMicDetection(meetingId, result)
    }
}

extension EditPlan {
    /// 一段時間（start～end）在新時間軸上變成哪幾段：跟每個保留的片段取交集再對應（design §93.3）
    func mapRange(_ meetingId: Int64, start: Double, end: Double) -> [(start: Double, end: Double)] {
        var out: [(start: Double, end: Double)] = []
        var base = 0.0
        for piece in pieces {
            defer { base += piece.length }
            guard piece.meetingId == meetingId else { continue }
            let s = max(start, piece.start), e = min(end, piece.end)
            if e > s { out.append((base + s - piece.start, base + e - piece.start)) }
        }
        return out
    }
}
