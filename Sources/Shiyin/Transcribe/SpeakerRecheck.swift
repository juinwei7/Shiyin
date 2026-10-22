import CoreML
import FluidAudio
import Foundation
import os

/// 短句的說話者再確認（R34、design §6.3.2、D92）：說話者辨識有時把很短的一句併進旁邊那個人（換人點放早或放晚了一兩秒）。
/// 分好說話者之後，4 秒以內的句子各自算一次聲紋（帶著前後文：10 秒的窗、遮罩只標這一句），跟每位說話者的聲音
/// （他最長的 20 句的平均）比；明顯比較像另一位（相似度高出 0.15 以上）就改成那一位。
/// 聲紋模型用 FluidAudio 的 wespeaker_v2（約 8 MB，第一次用時下載）。模型一次有三個欄位，但三個欄位共用第一個欄位的聲音
/// （設計給同一個 10 秒窗裡的三個人），所以一次算一句：17 分鐘的錄音約 8 秒（M3 Pro）
enum SpeakerRecheck {
    static let margin = 0.15
    /// 太短的聲音不夠判斷
    static let minSeconds = 0.5
    /// 長句說話者辨識本來就準，只查短的
    static let maxSeconds = 4.0
    /// 每位說話者的聲音：3 秒以上、最長的 20 句
    static let anchorSeconds = 3.0
    static let anchorCount = 20

    private static let log = Logger(subsystem: "com.weiwei.shiyin", category: "transcribe")
    private static let windowSamples = 160_000
    private static let lock = NSLock()
    nonisolated(unsafe) private static var model: MLModel?

    /// FluidAudio 下載到「這個資料夾的上一層／speaker-diarization」，跟說話者辨識的模型同一個資料夾（檔名不同，不會互相蓋掉）
    static var modelsDirectory: URL { Paths.modelsDir.appendingPathComponent("fluidaudio/speaker-diarization", isDirectory: true) }

    /// 模型不在就下載並編譯
    static func prepare() async throws -> MLModel {
        if let model = lock.withLock({ model }) { return model }
        let models = try await DiarizerModels.downloadIfNeeded(to: modelsDirectory)
        lock.withLock { model = models.embeddingModel }
        return models.embeddingModel
    }

    /// 回傳改過的說話者（跟 segments 對應）。locked：使用者改過說話者的句子，不動
    static func refine(_ segments: [TranscribedSegment], speakers: [Int?], samples: [Float],
                       locked: Set<Int> = []) async throws -> [Int?] {
        #if DEBUG
        // -debugNoRecheck YES：不做這一步，跟改之前比
        if UserDefaults.standard.bool(forKey: "debugNoRecheck") { return speakers }
        #endif
        func seconds(_ i: Int) -> Double { segments[i].end - segments[i].start }
        let check = segments.indices.filter { i in
            speakers[i] != nil && !locked.contains(i) && seconds(i) >= minSeconds && seconds(i) <= maxSeconds
        }
        var anchors: [Int: [Int]] = [:]
        for i in segments.indices where seconds(i) >= anchorSeconds {
            if let speaker = speakers[i] { anchors[speaker, default: []].append(i) }
        }
        anchors = anchors.mapValues { Array($0.sorted { seconds($0) > seconds($1) }.prefix(anchorCount)) }
        // 只有一位說話者有長句：沒得比
        guard !check.isEmpty, anchors.count >= 2 else { return speakers }
        let needed = Array(Set(check).union(anchors.values.joined())).sorted()
        let model = try await prepare()
        let vectors = try embed(needed.map { (segments[$0].start, segments[$0].end) }, samples: samples, model: model)
        var byIndex: [Int: [Float]] = [:]
        for (i, vector) in zip(needed, vectors) { byIndex[i] = vector }
        var centroids: [Int: [Float]] = [:]
        for (speaker, indices) in anchors {
            let members = indices.compactMap { byIndex[$0] }
            guard !members.isEmpty else { continue }
            var sum = [Float](repeating: 0, count: members[0].count)
            for v in members { for d in sum.indices { sum[d] += v[d] } }
            centroids[speaker] = normalized(sum)
        }
        var refined = speakers
        var changed = 0
        for i in check {
            guard let current = speakers[i], let vector = byIndex[i], let mine = centroids[current] else { continue }
            let own = dot(vector, mine)
            guard let (best, score) = centroids.filter({ $0.key != current }).map({ ($0.key, dot(vector, $0.value)) }).max(by: { $0.1 < $1.1 }),
                  score - own >= margin else { continue }
            refined[i] = best
            changed += 1
        }
        log.info("speaker recheck: \(check.count) checked, \(changed) changed")
        return refined
    }

    /// 每一段一個 256 維、長度 1 的聲紋。模型一次吃三個 10 秒的窗（[3, 160000] 的聲音、[3, 589] 的遮罩）
    static func embed(_ spans: [(start: Double, end: Double)], samples: [Float], model: MLModel, perCall: Int = 1) throws -> [[Float]] {
        let frames = model.modelDescription.inputDescriptionsByName["mask"]?.multiArrayConstraint?.shape.last?.intValue ?? 589
        let rate = AudioDecoder.sampleRate
        var result: [[Float]] = []
        for batch in stride(from: 0, to: spans.count, by: perCall) {
            try Task.checkCancellation()
            let waveform = try MLMultiArray(shape: [3, NSNumber(value: windowSamples)], dataType: .float32)
            let mask = try MLMultiArray(shape: [3, NSNumber(value: frames)], dataType: .float32)
            let wave = waveform.dataPointer.assumingMemoryBound(to: Float.self)
            let masks = mask.dataPointer.assumingMemoryBound(to: Float.self)
            wave.initialize(repeating: 0, count: 3 * windowSamples)
            masks.initialize(repeating: 0, count: 3 * frames)
            let count = min(perCall, spans.count - batch)
            for slot in 0..<count {
                let span = spans[batch + slot]
                // 窗的中心對著這一段；錄音不到 10 秒的部分補 0
                let center = (span.start + span.end) / 2
                let start = max(0, min(Int((center - 5) * rate), samples.count - windowSamples))
                let end = min(samples.count, start + windowSamples)
                if end > start {
                    samples.withUnsafeBufferPointer { buffer in
                        (wave + slot * windowSamples).update(from: buffer.baseAddress! + start, count: end - start)
                    }
                }
                let from = Double(start) / rate
                for f in 0..<frames {
                    let t = from + (Double(f) + 0.5) * 10 / Double(frames)
                    if t >= span.start && t <= span.end { masks[slot * frames + f] = 1 }
                }
            }
            let input = try MLDictionaryFeatureProvider(dictionary: ["waveform": waveform, "mask": mask])
            guard let output = try model.prediction(from: input).featureValue(for: "embedding")?.multiArrayValue else {
                throw DiarizerError.embeddingExtractionFailed
            }
            let dimension = output.shape.last?.intValue ?? 256
            for slot in 0..<count {
                result.append(normalized((0..<dimension).map { output[[NSNumber(value: slot), NSNumber(value: $0)]].floatValue }))
            }
        }
        return result
    }

    private static func normalized(_ v: [Float]) -> [Float] {
        let norm = sqrt(v.reduce(0) { $0 + $1 * $1 })
        return norm > 0 ? v.map { $0 / norm } : v
    }

    private static func dot(_ a: [Float], _ b: [Float]) -> Double {
        zip(a, b).reduce(0) { $0 + Double($1.0) * Double($1.1) }
    }
}
