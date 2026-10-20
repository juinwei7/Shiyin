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
    }

    static var modelsDirectory: URL { Paths.modelsDir.appendingPathComponent("fluidaudio", isDirectory: true) }

    private static var manager: OfflineDiarizerManager?

    static var isInstalled: Bool {
        let items = (try? FileManager.default.contentsOfDirectory(atPath: modelsDirectory.path)) ?? []
        return !items.isEmpty
    }

    /// 模型不在就下載並編譯（第一次約數十 MB）
    static func prepare() async throws -> OfflineDiarizerManager {
        if let manager { return manager }
        try FileManager.default.createDirectory(at: modelsDirectory, withIntermediateDirectories: true)
        let manager = OfflineDiarizerManager()
        try await manager.prepareModels(directory: modelsDirectory)
        self.manager = manager
        return manager
    }

    /// minSpeakers：至少分出幾個人（design §23.2），nil 自動判斷；clustering：評估時換分群的設定（門檻、人數）
    static func diarize(samples: [Float], minSpeakers: Int? = nil, clustering: OfflineDiarizerConfig.Clustering? = nil,
                        onProgress: @escaping @Sendable (Double) -> Void) async throws -> Output {
        var clustering = clustering
        if clustering == nil, let minSpeakers {
            clustering = .community
            clustering?.minSpeakers = minSpeakers
        }
        let manager: OfflineDiarizerManager
        if let clustering {
            manager = OfflineDiarizerManager(config: OfflineDiarizerConfig(clustering: clustering))
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
        return Output(turns: turns, voiceprints: voiceprints, speechSeconds: seconds)
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
