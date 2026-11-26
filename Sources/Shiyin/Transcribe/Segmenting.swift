import Accelerate
import Foundation

/// 內建辨識的段落切細（design-ios §8.1、D88）：內建辨識常常不加句號，依句子切不開，一段 15～40 秒、跨過換人。
/// 正式轉錄在換人的地方切開（要說話者辨識的結果），還是太長的在最安靜的地方切開；即時逐字稿只切太長的。
/// 內建辨識的字的時間是連著的（停頓算在字裡面），所以停頓用錄音的音量找。文字不變，只改切法
enum Segmenting {
    /// 超過這麼長就切
    static let maxSeconds = 8.0
    /// 切出來的每一塊至少這麼長
    static let minPart = 2.0
    /// 說話者辨識在換人處常常抖一下：連續同一人不到這麼久的，併進旁邊比較長的那串
    static let minTurnRun = 1.0

    /// 每 10 毫秒的音量（dB）
    struct Loudness {
        static let hop = Int(AudioDecoder.sampleRate / 100)
        private(set) var db: [Float] = []
        /// 不滿 10 毫秒的尾巴，等下一批聲音
        private var pending: [Float] = []

        init() {}
        init(samples: [Float]) { append(samples) }

        /// 接著加 16 kHz 單聲道的聲音（即時逐字稿一批一批送進來）
        mutating func append(_ samples: [Float]) {
            var input = samples
            if !pending.isEmpty {
                input = pending + samples
                pending = []
            }
            let frames = input.count / Self.hop
            db.reserveCapacity(db.count + frames)
            input.withUnsafeBufferPointer { buffer in
                for i in 0..<frames {
                    var meanSquare: Float = 0
                    vDSP_measqv(buffer.baseAddress! + i * Self.hop, 1, &meanSquare, vDSP_Length(Self.hop))
                    db.append(10 * log10(meanSquare + 1e-18))
                }
            }
            pending = Array(input[(frames * Self.hop)...])
        }

        /// from…to 秒之間，0.15 秒平均音量最低的地方（秒）
        func quietest(from: Double, to: Double) -> Double? {
            let half = 7
            let first = max(0, Int(from * 100)), last = min(db.count - 1, Int(to * 100))
            guard last > first else { return nil }
            var best: (frame: Int, average: Float)?
            // 以每一格為中心、前後各 7 格的平均（邊界只算有的格數）
            for frame in first...last {
                let lo = max(0, frame - half), hi = min(db.count - 1, frame + half)
                var sum: Float = 0
                for k in lo...hi { sum += db[k] }
                let average = sum / Float(hi - lo + 1)
                if best == nil || average < best!.average { best = (frame, average) }
            }
            return best.map { (Double($0.frame) + 0.5) / 100 }
        }
    }

    // MARK: 太長的在最安靜的地方切開

    /// 超過 maxSeconds 的段落從前面往後切：每次在接下來 maxSeconds 內（切出的兩邊都至少 minPart）最安靜的地方、
    /// 最近的字的分界切開。前面切好的不會因為後面多了字而改變，還沒定稿、一直變長的即時逐字稿也能用。沒有每個字的時間就不切
    static func splitLong(_ segments: [TranscribedSegment], loudness: Loudness) -> [TranscribedSegment] {
        segments.flatMap { segment -> [TranscribedSegment] in
            guard segment.words.count > 1, segment.end - segment.start > maxSeconds else { return [segment] }
            return lines(segment.words, loudness: loudness).map { piece(of: $0, from: segment) }
        }
    }

    private static func lines(_ words: [TranscribedWord], loudness: Loudness) -> [ArraySlice<TranscribedWord>] {
        var result: [ArraySlice<TranscribedWord>] = []
        var first = 0
        let end = words[words.count - 1].end
        while first < words.count - 1 {
            let start = words[first].start
            guard end - start > maxSeconds else { break }
            let allowed = (first + 1..<words.count).filter { words[$0].start - start >= minPart && end - words[$0].start >= minPart }
            guard let next = allowed.first else { break }
            let limit = min(start + maxSeconds, end - minPart)
            let window = allowed.filter { words[$0].start <= limit }
            let cut: Int
            if window.isEmpty {
                cut = next   // 接下來 maxSeconds 內沒有分界（很長的一個字）：在下一個分界切
            } else {
                let quiet = loudness.quietest(from: start + minPart, to: limit) ?? limit
                cut = window.min { abs(words[$0].start - quiet) < abs(words[$1].start - quiet) }!
            }
            result.append(words[first..<cut])
            first = cut
        }
        result.append(words[first...])
        return result
    }

    // MARK: 換人的地方切開

    /// 每個字依時間標上說話者辨識的結果，在換人的地方切開；連續同一人不到 minTurnRun 的併進旁邊比較長的那串
    static func splitAtTurns(_ segments: [TranscribedSegment], turns: [Diarizer.Turn]) -> [TranscribedSegment] {
        guard !turns.isEmpty else { return segments }
        return segments.flatMap { segment -> [TranscribedSegment] in
            let words = segment.words
            guard words.count > 1 else { return [segment] }
            let nearby = turns.filter { $0.end > segment.start && $0.start < segment.end }
            var runs: [(speaker: Int?, range: Range<Int>)] = []
            for (i, word) in words.enumerated() {
                let speaker = speaker(of: word, nearby.isEmpty ? turns : nearby)
                if let last = runs.last, last.speaker == speaker {
                    runs[runs.count - 1].range = last.range.lowerBound..<(i + 1)
                } else {
                    runs.append((speaker, i..<(i + 1)))
                }
            }
            func duration(_ run: (speaker: Int?, range: Range<Int>)) -> Double {
                words[run.range.upperBound - 1].end - words[run.range.lowerBound].start
            }
            while runs.count > 1 {
                let k = runs.indices.min { duration(runs[$0]) < duration(runs[$1]) }!
                guard duration(runs[k]) < minTurnRun else { break }
                let j = k == 0 ? 1 : k == runs.count - 1 ? k - 1 : duration(runs[k - 1]) >= duration(runs[k + 1]) ? k - 1 : k + 1
                let (lo, hi) = (min(j, k), max(j, k))
                runs.replaceSubrange(lo...hi, with: [(runs[j].speaker, runs[lo].range.lowerBound..<runs[hi].range.upperBound)])
                // 併完後跟前後同一人的接起來
                var merged: [(speaker: Int?, range: Range<Int>)] = []
                for run in runs {
                    if let last = merged.last, last.speaker == run.speaker {
                        merged[merged.count - 1].range = last.range.lowerBound..<run.range.upperBound
                    } else {
                        merged.append(run)
                    }
                }
                runs = merged
            }
            guard runs.count > 1 else { return [segment] }
            return runs.map { piece(of: words[$0.range], from: segment) }
        }
    }

    /// 這個字是誰說的：重疊最多的人；沒有重疊時用中點最近的（同 Diarizer.assign）
    private static func speaker(of word: TranscribedWord, _ turns: [Diarizer.Turn]) -> Int? {
        let (start, end) = (word.start, max(word.end, word.start + 0.01))
        var overlap: [Int: Double] = [:]
        for turn in turns where turn.end > start && turn.start < end {
            overlap[turn.speaker, default: 0] += min(turn.end, end) - max(turn.start, start)
        }
        if let best = overlap.max(by: { $0.value < $1.value }) { return best.key }
        let middle = (start + end) / 2
        return turns.min(by: { abs(($0.start + $0.end) / 2 - middle) < abs(($1.start + $1.end) / 2 - middle) })?.speaker
    }

    private static func piece(of words: ArraySlice<TranscribedWord>, from segment: TranscribedSegment) -> TranscribedSegment {
        TranscribedSegment(start: words.first!.start, end: words.last!.end,
                           text: words.map(\.text).joined().trimmingCharacters(in: .whitespacesAndNewlines),
                           lowConfidence: segment.lowConfidence, words: Array(words))
    }
}
