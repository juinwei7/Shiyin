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
        /// 說話的時段用哪個模型分的（R87）
        var model: Model = .standard
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

    /// 說話者辨識的模型（design §61.2、R87）：標準（pyannote＋VBx）、Nemotron-3（NVIDIA，最多 8 人）
    enum Model: String {
        case standard, nemotron
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
    /// model：評估時指定模型；nil 照設定（換了分群設定、整份設定時用標準的）
    static func diarize(samples: [Float], minSpeakers: Int? = nil, clustering: OfflineDiarizerConfig.Clustering? = nil,
                        config: OfflineDiarizerConfig? = nil, model requested: Model? = nil,
                        onProgress: @escaping @Sendable (Double) -> Void) async throws -> Output {
        var model = requested ?? (clustering == nil && config == nil ? AppSettings.diarizationModel : .standard)
        // Nemotron-3 最多分 8 人：先填人數選了 9 人以上時用標準的（R87.3）；模型還沒下載好時也先用標準的
        if model == .nemotron && ((minSpeakers ?? 0) > 8 || !isNemotronInstalled) {
            log.info("nemotron skipped: minSpeakers \(minSpeakers ?? 0, privacy: .public), installed \(isNemotronInstalled, privacy: .public)")
            model = .standard
        }
        // Nemotron-3 很快（32 分鐘的會議約 2 秒），標準流程佔進度的 90%
        let share = model == .nemotron ? 0.9 : 1
        let standard = try await standardDiarize(samples: samples, minSpeakers: minSpeakers, clustering: clustering, config: config) {
            onProgress($0 * share)
        }
        guard model == .nemotron else { return standard }
        try Task.checkCancellation()
        let started = Date()
        let turns: [Turn]
        do {
            // 背景的工作不會跟著取消（32 分鐘的會議約 2 秒），做完再看要不要停
            turns = try await nemotronTurns(samples)
        } catch {
            log.error("nemotron failed: \(error.localizedDescription, privacy: .public)")
            return standard
        }
        try Task.checkCancellation()
        onProgress(1)
        guard let combined = combine(standard: standard, nemotron: turns) else {
            log.info("nemotron saturated: \(Set(turns.map(\.speaker)).count, privacy: .public) speakers, standard \(standard.speechSeconds.count, privacy: .public)")
            return standard
        }
        log.info("nemotron: \(combined.speechSeconds.count, privacy: .public) speakers in \(Date().timeIntervalSince(started), format: .fixed(precision: 1), privacy: .public)s")
        if requested == nil { UsageStats.shared.diarizedWithNemotron() }
        return combined
    }

    /// 標準的離線流程（pyannote＋VBx）
    private static func standardDiarize(samples: [Float], minSpeakers: Int?, clustering: OfflineDiarizerConfig.Clustering?,
                                        config: OfflineDiarizerConfig?,
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

    // MARK: Nemotron-3（design §61.2、R87）

    static var nemotronConfig: Nemotron3Config { .offline }
    /// FluidAudio 下載到 models/fluidaudio/nemotron-3-diarization（大約 190 MB）
    static var nemotronDirectory: URL { modelsDirectory.appendingPathComponent(Repo.nemotron3Diarization.folderName, isDirectory: true) }

    /// 下載完整時 FluidAudio 才寫這個標記（權重版本），下載到一半的不算
    static var isNemotronInstalled: Bool {
        let marker = nemotronDirectory.appendingPathComponent(ModelNames.Nemotron3.weightsVersionFile)
        let version = (try? String(contentsOf: marker, encoding: .utf8))?.trimmingCharacters(in: .whitespacesAndNewlines)
        return version == ModelNames.Nemotron3.weightsVersion
    }

    /// 下載並載入一次（確認模型可以用）；設定選了 Nemotron-3 時由 ModelStore 呼叫
    nonisolated static func prepareNemotron(progress: @escaping @Sendable (Double) -> Void) async throws {
        let directory = await modelsDirectory
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        _ = try await Nemotron3Models.loadFromHuggingFace(config: .offline, cacheDirectory: directory) { progress($0.fractionCompleted) }
    }

    static func deleteNemotron() {
        try? FileManager.default.removeItem(at: nemotronDirectory)
    }

    /// Nemotron-3 的說話時段：每 10 毫秒每個人的機率 > 0.5 算在說話、短於 0.2 秒的不要（FluidAudio 的 segments，
    /// 同 CLI 和 §54 的評估）；依第一次出現的順序編號。在背景執行緒跑，不卡主執行緒；用完就放掉模型
    nonisolated static func nemotronTurns(_ samples: [Float]) async throws -> [Turn] {
        let directory = await modelsDirectory
        return try await Task.detached(priority: .userInitiated) {
            let config = Nemotron3Config.offline
            let models = try await Nemotron3Models.loadFromHuggingFace(config: config, cacheDirectory: directory)
            let diarizer = Nemotron3Diarizer(config: config, models: models)
            let (probabilities, frames) = try diarizer.processComplete(samples)
            let segments = Nemotron3Diarizer.segments(probabilities: probabilities, frameCount: frames, numSpeakers: config.numSpeakers)
            var numbering: [Int: Int] = [:]
            return segments.map { segment in
                let number = numbering[segment.speakerIndex] ?? {
                    numbering[segment.speakerIndex] = numbering.count + 1
                    return numbering.count
                }()
                return Turn(speaker: number, start: Double(segment.startSeconds), end: Double(segment.endSeconds))
            }
        }.value
    }

    /// 用 Nemotron-3 的時段、標準流程的聲紋（design §61.2）：每位 Nemotron 的說話者借跟他重疊最多的那位標準說話者的聲紋，
    /// 那一位有一半以上的時間跟他重疊才借；兩位對到同一位時給重疊比較多的。品質檢查照標準流程的。
    /// Nemotron 分滿 8 人、標準流程分出更多人時回傳 nil（用標準的，R87.3）
    nonisolated static func combine(standard: Output, nemotron turns: [Turn], maxSpeakers: Int = 8) -> Output? {
        let speakers = Set(turns.map(\.speaker))
        let standardSpeakers = Set(standard.turns.map(\.speaker))
        guard !speakers.isEmpty else { return nil }
        if speakers.count >= maxSpeakers && standardSpeakers.count > speakers.count { return nil }
        var seconds: [Int: Double] = [:]
        for turn in turns { seconds[turn.speaker, default: 0] += max(0, turn.end - turn.start) }
        var standardSeconds: [Int: Double] = [:]
        for turn in standard.turns { standardSeconds[turn.speaker, default: 0] += max(0, turn.end - turn.start) }
        // 重疊的秒數 [Nemotron 的說話者: [標準的說話者: 秒數]]
        var overlap: [Int: [Int: Double]] = [:]
        for a in turns {
            for b in standard.turns where b.end > a.start && b.start < a.end {
                overlap[a.speaker, default: [:]][b.speaker, default: 0] += min(a.end, b.end) - max(a.start, b.start)
            }
        }
        // 每位 Nemotron 的說話者：重疊最多的標準說話者，那一位一半以上的時間跟他重疊才算
        var claims: [Int: (speaker: Int, overlap: Double)] = [:]
        for speaker in speakers.sorted() {
            guard let best = overlap[speaker]?.max(by: { $0.value < $1.value || ($0.value == $1.value && $0.key > $1.key) }),
                  let total = standardSeconds[best.key], total > 0, best.value >= total * 0.5,
                  standard.voiceprints[best.key] != nil else { continue }
            if let other = claims[best.key], other.overlap >= best.value { continue }
            claims[best.key] = (speaker, best.value)
        }
        var voiceprints: [Int: [Float]] = [:]
        for (standardSpeaker, claim) in claims { voiceprints[claim.speaker] = standard.voiceprints[standardSpeaker] }
        var output = Output(turns: turns.sorted { ($0.start, $0.speaker) < ($1.start, $1.speaker) }, voiceprints: voiceprints,
                            speechSeconds: seconds, quality: standard.quality, model: .nemotron)
        #if DEBUG
        output.chunks = standard.chunks
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
