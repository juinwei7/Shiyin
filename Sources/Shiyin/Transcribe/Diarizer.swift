import FluidAudio
import Foundation

/// 說話者辨識（design §6.3）：FluidAudio 的離線辨識（pyannote + VBx，在 ANE 上跑）
@MainActor
enum Diarizer {
    struct Turn: Equatable {
        var speaker: Int      // 1 開始，依第一次出現的順序編號
        var start: Double
        var end: Double
    }

    /// 說話者辨識的結果：每段誰在講，加上每位說話者的聲紋（256 維）和說話的總秒數（design §21.4）
    struct Output {
        var turns: [Turn]
        var voiceprints: [Int: [Float]]
        var speechSeconds: [Int: Double]
        /// 分得準不準的線索（design §24.3）
        var quality: Quality? = nil
        #if DEBUG
        /// 評估用（shiyinctl voiceprint-eval 的 dump）：每個片段裡每位說話者的聲紋
        var chunks: [ChunkEmbedding] = []
        #endif
    }

    /// 分得準不準的自我檢查（design §24.5）。
    /// 碰撞率：一個片段裡模型聽到兩個以上的人時，不同的人被分到同一群的比例。分群時同一片段的人會被強制分開，
    /// 所以只有分出 1 群時才會碰撞。凝聚度：每個片段的聲紋跟它那一群平均聲紋的相似度（依片段數平均），
    /// 不同的人被併在同一群時會變低
    struct Quality: Equatable {
        var collisionRate: Double
        var multiSpeakerChunks: Int
        var cohesion: Double
        var clusters: Int
        var chunks: Int

        /// 超過這個分數就提示「說話者可能分得不夠」：凝聚度低於 0.6（評估時一般會議最低 0.71，麻將錄音 0.48）
        static let hintThreshold = 0.4

        /// 越大越可能把不同的人併在一起：只分出 1 群、但至少 10 個片段聽得出兩個人時是 1；其他看凝聚度。
        /// 片段太少（不到 30 個，大約 1 分鐘的說話）時不判斷
        var mergeRisk: Double {
            guard chunks >= 30 else { return 0 }
            if clusters <= 1 && multiSpeakerChunks >= 10 && collisionRate >= 0.5 { return 1 }
            return max(0, 1 - cohesion)
        }
    }

    static var modelsDirectory: URL { Paths.modelsDir.appendingPathComponent("fluidaudio", isDirectory: true) }

    private static var manager: OfflineDiarizerManager?

    static var isInstalled: Bool {
        let items = (try? FileManager.default.contentsOfDirectory(atPath: modelsDirectory.path)) ?? []
        return !items.isEmpty
    }

    /// 拾音用的設定（design §6.3.2、R34、D92）：
    /// - 每個片段的聲紋也拿回來，用來算分得準不準（design §24.3），不影響分群的結果
    /// - 一個人在一個視窗裡說 0.3 秒以上就算聲紋（預設 1 秒），沒人認領的片段重新算聲紋、判給最像的人：
    ///   很短的一句不會因為沒有聲紋，被併進旁邊那個人（AMI 2 秒內的句子標對 24% → 52%）
    static func config(clustering: OfflineDiarizerConfig.Clustering = .community) -> OfflineDiarizerConfig {
        var config = OfflineDiarizerConfig(clustering: clustering, exposeChunkEmbeddings: true)
        #if DEBUG
        // -debugDiarizerLegacy YES：改之前的設定（FluidAudio 的預設），用同一段錄音比較
        if UserDefaults.standard.bool(forKey: "debugDiarizerLegacy") { return config }
        #endif
        config.embedding.minSegmentDurationSeconds = 0.3
        config.zeroVoteReembed = .init(enabled: true, minDurationSeconds: 0.4)
        return config
    }

    /// 模型不在就下載並編譯（第一次約數十 MB）
    static func prepare() async throws -> OfflineDiarizerManager {
        if let manager { return manager }
        try FileManager.default.createDirectory(at: modelsDirectory, withIntermediateDirectories: true)
        let manager = OfflineDiarizerManager(config: config())
        try await manager.prepareModels(directory: modelsDirectory)
        // 短句再確認的聲紋模型（R34）一起準備好；下載不了不擋說話者辨識
        _ = try? await SpeakerRecheck.prepare()
        self.manager = manager
        return manager
    }

    /// minSpeakers：至少分出幾個人（design §23.2），nil 自動判斷；clustering：評估時換分群的設定（門檻、人數）
    /// config：評估時整份換掉（DEBUG 的 voiceprint-eval，短句的實驗 design §6.3.2）
    static func diarize(samples: [Float], minSpeakers: Int? = nil, clustering: OfflineDiarizerConfig.Clustering? = nil,
                        config: OfflineDiarizerConfig? = nil,
                        onProgress: @escaping @Sendable (Double) -> Void) async throws -> Output {
        var clustering = clustering
        if clustering == nil, let minSpeakers {
            clustering = .community
            clustering?.minSpeakers = minSpeakers
        }
        let manager: OfflineDiarizerManager
        if let config {
            manager = OfflineDiarizerManager(config: config)
            try await manager.prepareModels(directory: modelsDirectory)
        } else if let clustering {
            manager = OfflineDiarizerManager(config: Self.config(clustering: clustering))
            try await manager.prepareModels(directory: modelsDirectory)
        } else {
            manager = try await prepare()
        }
        let result = try await manager.process(audio: samples) { done, total in
            if total > 0 { onProgress(Double(done) / Double(total)) }
        }
        var numbering: [String: Int] = [:]
        let turns = result.segments
            .sorted { $0.startTimeSeconds < $1.startTimeSeconds }
            .map { segment in
                let number = numbering[segment.speakerId] ?? {
                    let next = numbering.count + 1
                    numbering[segment.speakerId] = next
                    return next
                }()
                return Turn(speaker: number, start: Double(segment.startTimeSeconds), end: Double(segment.endTimeSeconds))
            }
        // 聲紋：離線辨識的 speakerDatabase（每位說話者一個），跟著上面的編號
        var voiceprints: [Int: [Float]] = [:]
        for (id, embedding) in result.speakerDatabase ?? [:] {
            if let number = numbering[id], !embedding.isEmpty { voiceprints[number] = embedding }
        }
        var seconds: [Int: Double] = [:]
        for turn in turns { seconds[turn.speaker, default: 0] += max(0, turn.end - turn.start) }
        var output = Output(turns: turns, voiceprints: voiceprints, speechSeconds: seconds, quality: result.chunkEmbeddings.map(quality))
        #if DEBUG
        output.chunks = result.chunkEmbeddings ?? []
        #endif
        return output
    }

    nonisolated static func quality(_ chunks: [ChunkEmbedding]) -> Quality {
        var multi = 0, collided = 0
        for (_, items) in Dictionary(grouping: chunks, by: \.chunkIndex) {
            let locals = Set(items.map(\.speakerIndex))
            guard locals.count >= 2 else { continue }
            multi += 1
            if Set(items.map(\.speakerId)).count < locals.count { collided += 1 }
        }
        func normalized(_ v: [Float]) -> [Float] {
            let norm = sqrt(v.reduce(0) { $0 + $1 * $1 })
            return norm > 0 ? v.map { $0 / norm } : v
        }
        var total = 0.0, count = 0
        let groups = Dictionary(grouping: chunks, by: \.speakerId)
        for (_, items) in groups {
            let vectors = items.map { normalized($0.embedding256) }
            guard let width = vectors.first?.count else { continue }
            var sum = [Float](repeating: 0, count: width)
            for v in vectors { for i in 0..<width { sum[i] += v[i] } }
            let centroid = normalized(sum)
            for v in vectors { total += Double(zip(v, centroid).reduce(0) { $0 + $1.0 * $1.1 }); count += 1 }
        }
        return Quality(collisionRate: multi > 0 ? Double(collided) / Double(multi) : 0, multiSpeakerChunks: multi,
                       cohesion: count > 0 ? total / Double(count) : 1, clusters: groups.count, chunks: chunks.count)
    }

    /// 每段逐字稿標上跟它重疊最多的說話者；完全沒重疊就用時間最接近的。
    /// 最後依逐字稿裡出現的順序重新編號，沒分到任何段落的說話者就不會留下空的編號
    nonisolated static func assign(_ turns: [Turn], to segments: [TranscribedSegment]) -> [Int?] {
        assignNumbering(turns, to: segments).speakers
    }

    /// 同 assign，另外回傳說話者辨識的編號 → 逐字稿上的編號（聲紋跟著換編號，design §21.4）
    nonisolated static func assignNumbering(_ turns: [Turn], to segments: [TranscribedSegment]) -> (speakers: [Int?], numbering: [Int: Int]) {
        guard !turns.isEmpty else { return (segments.map { _ in nil }, [:]) }
        let raw: [Int?] = segments.map { segment in
            var overlap: [Int: Double] = [:]
            for turn in turns where turn.end > segment.start && turn.start < segment.end {
                overlap[turn.speaker, default: 0] += min(turn.end, segment.end) - max(turn.start, segment.start)
            }
            if let best = overlap.max(by: { $0.value < $1.value }) { return best.key }
            let middle = (segment.start + segment.end) / 2
            return turns.min(by: { abs(($0.start + $0.end) / 2 - middle) < abs(($1.start + $1.end) / 2 - middle) })?.speaker
        }
        var numbering: [Int: Int] = [:]
        let speakers: [Int?] = raw.map { speaker in
            guard let speaker else { return nil }
            if let number = numbering[speaker] { return number }
            numbering[speaker] = numbering.count + 1
            return numbering.count
        }
        return (speakers, numbering)
    }
}
