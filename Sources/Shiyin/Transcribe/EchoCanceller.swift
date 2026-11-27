import Accelerate
import AVFoundation

/// 喇叭回音消除（R123、design §97）：用喇叭開線上會議時，麥克風會收到電腦放出來的對方聲音。轉錄前拿電腦聲音那一聲道當參考，
/// 把麥克風聲道裡的回音扣掉，寫成暫存的雙聲道檔給後面的轉錄、說話者辨識用；錄音檔本身不動。
///
/// 1. 找時間差：每 1 秒取 4 秒，用 GCC-PHAT 找回音比電腦聲音晚多少（可能是負的：兩個聲道各自收到多少就寫多少），
///    連成分段的直線（時鐘漂移是斜率、中途跳一下就換一段）。找不到穩定的時間差就是沒有回音（戴耳機），不處理
/// 2. 電腦聲音照時間差重新取樣對齊，再用分塊頻域 Kalman 濾波器估計回音路徑、從麥克風扣掉
/// 3. 每個頻帶不讓輸出比原本的麥克風大聲；離回音太遠的地方照原本的麥克風
///
/// 依序讀檔（找時間差一次、濾波一次，加上預習的 30 秒），記憶體不隨錄音長度增加。Windows 是同一套算法（design-windows §11.56）
enum EchoCanceller {
    static let rate = 16_000

    /// 時間差是固定斜率的一段：時間差（取樣點）＝ slope × 位置 ＋ intercept；位置、時間差都是 16 kHz 的取樣點
    struct LagSegment: Codable, Equatable {
        var start: Int
        var end: Int
        var slope: Double
        var intercept: Double
        func lag(at n: Int) -> Double { slope * Double(n) + intercept }
    }

    struct Report: Codable {
        /// 電腦聲音、麥克風都有聲音的 4 秒視窗數，和其中找得到回音的
        var windows = 0
        var confident = 0
        var segments: [LagSegment] = []
        /// 有回音、有處理的時間（秒，[開始, 結束]）
        var zones: [[Double]] = []
        var seconds = 0.0
        var echoFound: Bool { !segments.isEmpty }
    }

    /// 有回音時寫出 output（聲道 0 扣掉回音的麥克風、聲道 1 原本的電腦聲音）；沒有回音、不是雙聲道時不寫檔。
    /// 很花 CPU、會卡住呼叫的執行緒（轉錄流程在 Task.detached 裡叫）
    static func process(_ input: URL, to output: URL, isCancelled: @escaping () -> Bool = { false }) async throws -> Report {
        let started = Date()
        guard try await channelCount(input) == 2 else { return Report() }
        var (report, plan) = try analyze(try await FileStereo(input), isCancelled: isCancelled)
        if let plan {
            let writer = try Writer(output)
            defer { writer.close() }
            try FilterPass.run(prime: try await FileStereo(input), main: try await FileStereo(input), plan: plan, isCancelled: isCancelled) {
                try writer.write(mic: $0, ref: $1, count: $2)
            }
        }
        report.seconds = Date().timeIntervalSince(started)
        return report
    }

    /// 記憶體裡的雙聲道（標準答案、測試用）：回傳扣掉回音的麥克風，沒有回音時是 nil
    static func process(mic: [Float], ref: [Float]) throws -> (Report, [Float]?) {
        let (report, plan) = try analyze(MemoryStereo(mic, ref), isCancelled: { false })
        guard let plan else { return (report, nil) }
        var cleaned: [Float] = []
        try FilterPass.run(prime: MemoryStereo(mic, ref), main: MemoryStereo(mic, ref), plan: plan, isCancelled: { false }) {
            cleaned.append(contentsOf: $0.prefix($2))
        }
        return (report, cleaned)
    }

    private static func analyze(_ source: StereoSource, isCancelled: () -> Bool) throws -> (Report, Plan?) {
        var report = Report()
        let found = try LagFinder.run(source, isCancelled: isCancelled)
        report.windows = found.windows
        report.confident = found.confident
        guard let plan = Plan(found) else { return (report, nil) }
        report.segments = plan.segments
        report.zones = plan.zones.map { [Double($0.lowerBound) / Double(rate), Double($0.upperBound) / Double(rate)] }
        return (report, plan)
    }

    private static func channelCount(_ url: URL) async throws -> Int {
        let asset = AVURLAsset(url: url)
        guard let track = try await asset.loadTracks(withMediaType: .audio).first else { throw AudioDecoderError.noAudioTrack }
        let formats = try await track.load(.formatDescriptions)
        return formats.first.flatMap { CMAudioFormatDescriptionGetStreamBasicDescription($0)?.pointee.mChannelsPerFrame }.map(Int.init) ?? 1
    }

    // MARK: 讀檔

    /// 依序讀出 16 kHz 的雙聲道（聲道 0 麥克風、1 電腦聲音），每次接在 mic、ref 後面加一段；讀完就不再加
    protocol StereoSource: AnyObject {
        var finished: Bool { get }
        func read(mic: inout Window, ref: inout Window) throws
    }

    private final class FileStereo: StereoSource {
        private let stream: AudioDecoder.Stream
        private var interleaved: [Float] = []
        var finished: Bool { stream.finished }

        init(_ url: URL) async throws { stream = try await AudioDecoder.Stream(url, channels: 2) }

        func read(mic: inout Window, ref: inout Window) throws {
            interleaved.removeAll(keepingCapacity: true)
            try stream.fill(&interleaved, upTo: rate * 2)
            let frames = interleaved.count / 2
            interleaved.withUnsafeBufferPointer { p in
                mic.append(frames) { p[2 * $0] }
                ref.append(frames) { p[2 * $0 + 1] }
            }
        }
    }

    private final class MemoryStereo: StereoSource {
        private let mic: [Float], ref: [Float]
        private var position = 0
        var finished: Bool { position >= mic.count }

        init(_ mic: [Float], _ ref: [Float]) { (self.mic, self.ref) = (mic, ref) }

        func read(mic out: inout Window, ref outRef: inout Window) throws {
            let end = min(mic.count, position + rate)
            let (mic, ref, start) = (self.mic, self.ref, position)
            out.append(end - start) { mic[start + $0] }
            outRef.append(end - start) { start + $0 < ref.count ? ref[start + $0] : 0 }
            position = end
        }
    }

    /// 依序讀雙聲道，留著還要用的那一段（絕對位置存取，超出範圍是 0）
    private final class Reader {
        private let source: StereoSource
        private(set) var mic = Window()
        private(set) var ref = Window()

        init(_ source: StereoSource) { self.source = source }

        /// 讀到兩個聲道都至少有 end 個取樣點（或讀完）
        func read(upTo end: Int) throws {
            while mic.end < end, !source.finished { try source.read(mic: &mic, ref: &ref) }
        }

        func drop(before n: Int) { mic.drop(before: n); ref.drop(before: n) }
    }

    struct Window {
        private(set) var base = 0
        private(set) var data: [Float] = []
        var end: Int { base + data.count }

        subscript(n: Int) -> Float { n >= base && n < end ? data[n - base] : 0 }

        mutating func append(_ count: Int, _ value: (Int) -> Float) {
            data.reserveCapacity(data.count + count)
            for i in 0..<count { data.append(value(i)) }
        }

        /// 丟掉 n 之前的（累積夠多才真的搬，避免每次都搬整個陣列）
        mutating func drop(before n: Int) {
            let k = min(max(0, n - base), data.count)
            guard k >= rate * 4 else { return }
            data.removeFirst(k)
            base += k
        }

        /// Σ weights[j] × self[first + j]（都在範圍內時用 vDSP）
        func dot(_ weights: UnsafePointer<Float>, from first: Int, count: Int) -> Float {
            var sum: Float = 0
            if first >= base, first + count <= end {
                data.withUnsafeBufferPointer { vDSP_dotpr(weights, 1, $0.baseAddress! + (first - base), 1, &sum, vDSP_Length(count)) }
            } else {
                for j in 0..<count { sum += weights[j] * self[first + j] }
            }
            return sum
        }

        /// 從絕對位置 start 起複製 count 個到 out（超出範圍補 0）
        func copy(from start: Int, count: Int, into out: UnsafeMutablePointer<Float>) {
            for i in 0..<count { out[i] = self[start + i] }
        }
    }

    // MARK: 1. 找時間差

    /// GCC-PHAT 在 8 kHz 做（相鄰兩點平均降頻）：4 秒的電腦聲音對前後各多 2 秒的麥克風，看 ±2 秒內哪裡最像
    enum LagFinder {
        static let rate = 8_000
        static let window = 4 * rate
        static let hop = rate
        static let maxLag = 2 * rate
        static let log2n = 16   // 65536 ≥ window + 2 × maxLag
        /// PHAT 只看這段頻率：低頻有冷氣、電源的嗡嗡聲，筆電喇叭也放不出來
        static let band = 150.0...3800.0
        /// 峰值比平均高幾個標準差才算找到；沒有回音時最高的峰大約 4～5
        static let zMin: Float = 9

        struct Point { var time: Int; var lag: Double; var z: Float }
        struct Found {
            var points: [Point] = []   // 找到的（16 kHz 的取樣點）
            var windows = 0
            var confident = 0
            var micEnergy = 0.0, refEnergy = 0.0
            var total = 0              // 錄音長度（16 kHz 的取樣點）
        }

        static func run(_ source: StereoSource, isCancelled: () -> Bool) throws -> Found {
            let reader = Reader(source)
            let fft = RealFFT(log2n: log2n)
            let n = 1 << log2n
            let bins = n / 2 + 1
            var found = Found()
            var mic8 = Window(), ref8 = Window()
            var seg = [Float](repeating: 0, count: n), refBuf = [Float](repeating: 0, count: n)
            var sr = [Float](repeating: 0, count: bins), si = sr, rr = sr, ri = sr
            var corr = [Float](repeating: 0, count: n)
            let low = Int((band.lowerBound * Double(n) / Double(rate)).rounded(.up))
            let high = Int((band.upperBound * Double(n) / Double(rate)).rounded(.down))
            var start = 0
            while true {
                if isCancelled() { throw CancellationError() }
                // 降頻：16 kHz 的第 2i、2i+1 點平均成 8 kHz 的第 i 點
                let need = 2 * (start + window + maxLag)
                try reader.read(upTo: need)
                let available = reader.mic.end / 2
                if mic8.end < available {
                    let from = mic8.end
                    mic8.append(available - from) { (reader.mic[2 * (from + $0)] + reader.mic[2 * (from + $0) + 1]) * 0.5 }
                    ref8.append(available - from) { (reader.ref[2 * (from + $0)] + reader.ref[2 * (from + $0) + 1]) * 0.5 }
                }
                reader.drop(before: 2 * (start - maxLag))
                if start + window > available { break }
                // 這個視窗：電腦聲音 [start, start+window)，麥克風 [start-maxLag, start+window+maxLag)
                for i in 0..<n { seg[i] = 0; refBuf[i] = 0 }
                mic8.copy(from: start - maxLag, count: window + 2 * maxLag, into: &seg)
                ref8.copy(from: start, count: window, into: &refBuf)
                let refPower = meanSquare(refBuf, window)
                let micPower = meanSquare(Array(seg[maxLag..<(maxLag + window)]), window)
                let segPower = meanSquare(seg, window + 2 * maxLag)
                if refPower > 3.2e-6, segPower > 1e-7 {   // -55 dBFS、-70 dBFS
                    found.windows += 1
                    found.micEnergy += Double(micPower)
                    found.refEnergy += Double(refPower)
                    fft.forward(seg, re: &sr, im: &si)
                    fft.forward(refBuf, re: &rr, im: &ri)
                    for k in 0..<bins {
                        guard k >= low, k <= high else { sr[k] = 0; si[k] = 0; continue }
                        // seg × conj(ref)，再除以大小（PHAT）
                        let re = sr[k] * rr[k] + si[k] * ri[k]
                        let im = si[k] * rr[k] - sr[k] * ri[k]
                        let mag = (re * re + im * im).squareRoot() + 1e-12
                        sr[k] = re / mag
                        si[k] = im / mag
                    }
                    fft.inverse(re: sr, im: si, out: &corr)
                    let count = 2 * maxLag + 1
                    var best = 0
                    var mean: Float = 0, sq: Float = 0
                    for i in 0..<count {
                        mean += corr[i]
                        sq += corr[i] * corr[i]
                        if corr[i] > corr[best] { best = i }
                    }
                    mean /= Float(count)
                    let std = max(0, sq / Float(count) - mean * mean).squareRoot()
                    let z = (corr[best] - mean) / (std + 1e-12)
                    if z >= zMin {
                        var frac = 0.0
                        if best > 0, best < count - 1 {
                            let (a, b, c) = (Double(corr[best - 1]), Double(corr[best]), Double(corr[best + 1]))
                            let den = a - 2 * b + c
                            if den < 0 { frac = 0.5 * (a - c) / den }
                        }
                        found.confident += 1
                        found.points.append(Point(time: 2 * (start + window / 2), lag: 2 * (Double(best - maxLag) + frac), z: z))
                    }
                }
                mic8.drop(before: start + hop - maxLag)
                ref8.drop(before: start + hop - maxLag)
                start += hop
            }
            found.total = reader.mic.end
            return found
        }

        private static func meanSquare(_ x: [Float], _ count: Int) -> Float {
            var value: Float = 0
            x.withUnsafeBufferPointer { vDSP_measqv($0.baseAddress!, 1, &value, vDSP_Length(min(count, x.count))) }
            return value
        }
    }

    // MARK: 時間差 → 分段直線、要處理的區間

    struct Plan {
        static let tolerance = 0.003 * Double(rate)   // 離直線 3 ms 以內算同一段
        static let minPoints = 5                       // 一段至少幾個點才算數（沒有回音時偶爾有零星的假峰）
        static let zonePadding = 10 * rate             // 找到回音的地方前後 10 秒內才處理
        var segments: [LagSegment]
        var zones: [Range<Int>]
        /// 參考訊號（電腦聲音）的音量調到跟麥克風差不多，濾波器的初始值才不受音量影響
        var gain: Float
        var total: Int

        init?(_ found: LagFinder.Found) {
            let points = found.points
            var groups: [[LagFinder.Point]] = []
            var current: [LagFinder.Point] = []
            var pending: [LagFinder.Point] = []
            for point in points {
                if current.isEmpty { current = [point]; continue }
                let (k, b) = Self.fit(current.suffix(30))
                if abs(point.lag - (k * Double(point.time) + b)) <= Self.tolerance {
                    current += pending
                    pending = []
                    current.append(point)
                } else {
                    pending.append(point)
                    guard pending.count >= 3 else { continue }
                    // 連續三個點都不在線上、彼此一致：從這裡換一段
                    let lags = pending.map(\.lag)
                    if lags.max()! - lags.min()! <= 2 * Self.tolerance {
                        groups.append(current)
                        current = pending
                        pending = []
                    } else {
                        pending.removeFirst()
                    }
                }
            }
            if !current.isEmpty { groups.append(current) }
            groups = groups.filter { $0.count >= Self.minPoints }
            let used = groups.reduce(0) { $0 + $1.count }
            // 有回音：夠多個點、佔有聲音的視窗至少一成
            guard used >= 8, Double(used) >= 0.1 * Double(found.windows), found.refEnergy > 0 else { return nil }
            total = found.total
            var segments = groups.map { group -> LagSegment in
                let (k, b) = Self.fit(group[...])
                return LagSegment(start: group.first!.time, end: group.last!.time, slope: k, intercept: b)
            }
            // 段跟段之間在中點換；頭尾延伸到整段錄音
            for i in 0..<(segments.count - 1) {
                let mid = (segments[i].end + segments[i + 1].start) / 2
                segments[i].end = mid
                segments[i + 1].start = mid
            }
            segments[0].start = 0
            segments[segments.count - 1].end = max(found.total, segments[segments.count - 1].end)
            self.segments = segments
            // 處理的區間：找到回音的點前後 10 秒，重疊的接起來
            var zones: [Range<Int>] = []
            for point in groups.joined().sorted(by: { $0.time < $1.time }) {
                let range = max(0, point.time - Self.zonePadding)..<min(found.total, point.time + Self.zonePadding)
                if let last = zones.last, range.lowerBound <= last.upperBound {
                    zones[zones.count - 1] = last.lowerBound..<max(last.upperBound, range.upperBound)
                } else {
                    zones.append(range)
                }
            }
            self.zones = zones
            gain = Float((found.micEnergy / found.refEnergy).squareRoot())
        }

        /// 最小平方法；離群的（超過殘差中位數 3 倍、也超過 2 個取樣點）去掉再算一次
        static func fit(_ points: ArraySlice<LagFinder.Point>) -> (Double, Double) {
            let lags = points.map(\.lag)
            guard points.count >= 3 else { return (0, lags.sorted()[lags.count / 2]) }
            func solve(_ ps: [LagFinder.Point]) -> (Double, Double) {
                let n = Double(ps.count)
                let t0 = Double(ps[0].time)   // 時間從第一個點算起，數值比較穩
                var st = 0.0, sl = 0.0, stt = 0.0, stl = 0.0
                for p in ps {
                    let t = Double(p.time) - t0
                    st += t; sl += p.lag; stt += t * t; stl += t * p.lag
                }
                let den = n * stt - st * st
                let k = abs(den) < 1e-9 ? 0 : (n * stl - st * sl) / den
                let b = (sl - k * st) / n
                return (k, b - k * t0)
            }
            let all = Array(points)
            let (k, b) = solve(all)
            let residuals = all.map { abs($0.lag - (k * Double($0.time) + b)) }
            let limit = max(2, 3 * residuals.sorted()[residuals.count / 2])
            let kept = zip(all, residuals).filter { $0.1 <= limit }.map(\.0)
            return kept.count >= 3 ? solve(kept) : (k, b)
        }
    }

    // MARK: 2、3. 對齊、濾波、輸出

    enum FilterPass {
        static let block = KalmanFilter.block
        /// 回音路徑從濾波器的第 2 塊開始（32 ms），時間差估得不準、時鐘漂移都還在範圍內
        static let margin = 2 * block
        /// 先用前 30 秒學一遍再從頭處理，開頭的回音才扣得掉
        static let primeSeconds = 30
        /// 換段（時間差跳動）的前後 2 秒不學：這時對齊可能是錯的，學了會把濾波器帶歪
        static let freezeSamples = 2 * rate
        /// 進出處理區間時跟原本的麥克風交叉淡入淡出
        static let fadeSamples = rate / 4

        /// sink 收到（扣掉回音的麥克風、原本的電腦聲音、這一塊有幾個是有效的）
        static func run(prime: StereoSource, main: StereoSource, plan: Plan, isCancelled: () -> Bool,
                        sink: ([Float], [Float], Int) throws -> Void) throws {
            let filter = KalmanFilter()
            let resampler = Resampler()
            let frozen: [Range<Int>] = plan.segments.dropFirst().map { max(0, $0.start - freezeSamples)..<($0.start + freezeSamples) }
            // 預習：前 30 秒跑一遍，只留下濾波器學到的東西
            let primeBlocks = min(plan.total, primeSeconds * rate) / block
            try pass(prime, plan: plan, filter: filter, resampler: resampler, frozen: frozen, blocks: primeBlocks, isCancelled: isCancelled, emit: false) { _, _, _, _ in }
            filter.resetHistory()
            let clamp = Clamp()
            var pendingRef = Window()
            var outMic = [Float](repeating: 0, count: block)
            try pass(main, plan: plan, filter: filter, resampler: resampler, frozen: frozen, blocks: (plan.total + block - 1) / block,
                     isCancelled: isCancelled, emit: true) { k, mic, cleaned, ref in
                // 每個頻帶不比原本的麥克風大聲；輸出晚一塊
                clamp.process(mic: mic, echoFree: cleaned, into: &outMic)
                pendingRef.append(block) { ref[$0] }
                if k > 0 {
                    let start = (k - 1) * block
                    try sink(outMic, (0..<block).map { pendingRef[start + $0] }, min(block, plan.total - start))
                }
                pendingRef.drop(before: (k - 1) * block)
            }
            // 最後一塊
            let last = ((plan.total + block - 1) / block - 1) * block
            if last >= 0, last < plan.total {
                clamp.process(mic: [Float](repeating: 0, count: block), echoFree: [Float](repeating: 0, count: block), into: &outMic)
                try sink(outMic, (0..<block).map { pendingRef[last + $0] }, min(block, plan.total - last))
            }
        }

        /// 從頭讀 blocks 塊：對齊參考訊號、濾波；emit 時 sink 收到（第幾塊、麥克風、扣掉回音的麥克風、原本的電腦聲音）
        private static func pass(_ source: StereoSource, plan: Plan, filter: KalmanFilter, resampler: Resampler, frozen: [Range<Int>], blocks: Int,
                                 isCancelled: () -> Bool, emit: Bool, sink: (Int, [Float], [Float], [Float]) throws -> Void) throws {
            let reader = Reader(source)
            let maxShift = plan.segments.map { max(abs($0.lag(at: $0.start)), abs($0.lag(at: $0.end))) }.max() ?? 0
            let lookahead = Int(maxShift) + margin + Resampler.taps + block
            var aligned = [Float](repeating: 0, count: block)
            var mic = [Float](repeating: 0, count: block)
            var ref = [Float](repeating: 0, count: block)
            var cleaned = [Float](repeating: 0, count: block)
            var seg = 0, zone = 0
            for k in 0..<blocks {
                if k % 64 == 0, isCancelled() { throw CancellationError() }
                let start = k * block
                try reader.read(upTo: start + block + lookahead)
                reader.drop(before: start - lookahead)
                for i in 0..<block {
                    let n = start + i
                    while seg < plan.segments.count - 1, n >= plan.segments[seg].end { seg += 1 }
                    let position = Double(n) - (plan.segments[seg].lag(at: n) - Double(margin))
                    aligned[i] = resampler.read(reader.ref, at: position) * plan.gain
                    mic[i] = reader.mic[n]
                    ref[i] = reader.ref[n]
                }
                let adapt = !frozen.contains { $0.contains(start) }
                filter.step(reference: aligned, mic: mic, adapt: adapt, error: &cleaned)
                guard emit else { continue }
                // 處理區間外照原本的麥克風，邊界交叉淡入淡出（區間照時間排、不重疊：只看還沒過去的前兩個）
                for i in 0..<block {
                    let n = start + i
                    while zone < plan.zones.count, n >= plan.zones[zone].upperBound + fadeSamples { zone += 1 }
                    let w = zoneWeight(n, plan.zones[zone..<min(plan.zones.count, zone + 2)])
                    cleaned[i] = w * cleaned[i] + (1 - w) * mic[i]
                }
                try sink(k, mic, cleaned, ref)
            }
        }

        static func zoneWeight(_ n: Int, _ zones: ArraySlice<Range<Int>>) -> Float {
            var best: Float = 0
            for zone in zones where n >= zone.lowerBound - fadeSamples && n < zone.upperBound + fadeSamples {
                let into = min(n - zone.lowerBound, zone.upperBound - 1 - n)
                best = max(best, min(1, max(0, Float(into + fadeSamples) / Float(2 * fadeSamples))))
            }
            return best
        }
    }

    /// 寫成 16 kHz 雙聲道 16-bit WAV
    private final class Writer {
        private let file: AVAudioFile
        private let buffer: AVAudioPCMBuffer
        private var filled = 0
        private let capacity = rate

        init(_ url: URL) throws {
            file = try AVAudioFile(forWriting: url, settings: [
                AVFormatIDKey: kAudioFormatLinearPCM, AVSampleRateKey: Double(rate), AVNumberOfChannelsKey: 2,
                AVLinearPCMBitDepthKey: 16, AVLinearPCMIsFloatKey: false, AVLinearPCMIsBigEndianKey: false,
            ], commonFormat: .pcmFormatFloat32, interleaved: false)
            buffer = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: AVAudioFrameCount(rate))!
        }

        func write(mic: [Float], ref: [Float], count: Int) throws {
            guard count > 0 else { return }
            let channels = buffer.floatChannelData!
            for i in 0..<count {
                channels[0][filled] = max(-1, min(1, mic[i]))
                channels[1][filled] = ref[i]
                filled += 1
                if filled == capacity { try flush() }
            }
        }

        private func flush() throws {
            guard filled > 0 else { return }
            buffer.frameLength = AVAudioFrameCount(filled)
            try file.write(from: buffer)
            filled = 0
        }

        func close() {
            try? flush()
            file.close()
        }
    }

    // MARK: 元件

    /// 小數位置讀取：Kaiser 窗的 sinc（32 點、512 個相位查表）
    final class Resampler {
        static let taps = 32
        static let phases = 512
        private let table: [Float]

        init() {
            let half = Self.taps / 2
            let beta = 8.0, cutoff = 0.95
            func bessel0(_ x: Double) -> Double {
                var sum = 1.0, term = 1.0
                for k in 1..<30 {
                    term *= (x / (2 * Double(k))) * (x / (2 * Double(k)))
                    sum += term
                }
                return sum
            }
            var table = [Float](repeating: 0, count: (Self.phases + 1) * Self.taps)
            for p in 0...Self.phases {
                let frac = Double(p) / Double(Self.phases)
                for j in 0..<Self.taps {
                    let u = Double(j - half + 1) - frac
                    let sinc = u == 0 ? 1 : sin(.pi * u * cutoff) / (.pi * u * cutoff)
                    let r = u / Double(half)
                    let window = r * r < 1 ? bessel0(beta * (1 - r * r).squareRoot()) / bessel0(beta) : 0
                    table[p * Self.taps + j] = Float(cutoff * sinc * window)
                }
            }
            self.table = table
        }

        func read(_ x: Window, at position: Double) -> Float {
            let base = Int(position.rounded(.down))
            let phase = Int(((position - Double(base)) * Double(Self.phases)).rounded())
            return table.withUnsafeBufferPointer { x.dot($0.baseAddress! + phase * Self.taps, from: base - Self.taps / 2 + 1, count: Self.taps) }
        }
    }

    /// 分塊頻域 Kalman 濾波器（每塊 256 點、16 塊 = 256 ms 的回音路徑；FFT 512 點）。
    /// 觀測雜訊用誤差的功率估計：本地的人在說話時誤差變大，自動學得慢，不會被本地人聲帶歪
    final class KalmanFilter {
        static let block = 256
        static let partitions = 16
        static let a2: Float = 0.9995 * 0.9995
        static let initialP: Float = 0.1
        private let bins = KalmanFilter.block + 1
        private let fft = RealFFT(log2n: 9)
        private var round = 0
        // 狀態放在一塊連續的記憶體，迴圈裡不做陣列的邊界檢查
        private let storage: UnsafeMutablePointer<Float>
        /// 分區 × 頻帶：回音路徑 H、狀態的變異數 P、參考訊號最近 16 塊的頻譜 X（第 0 塊是最新的）
        private let hr, hi, p, xr, xi: UnsafeMutablePointer<Float>
        /// 頻帶：誤差功率、分母、暫存
        private let psi, total, yr, yi, er, ei: UnsafeMutablePointer<Float>
        private let previous, frame, time: UnsafeMutablePointer<Float>

        init() {
            let (block, bins) = (Self.block, KalmanFilter.block + 1)
            let state = Self.partitions * bins
            let size = 5 * state + 6 * bins + 5 * block
            storage = .allocate(capacity: size)
            storage.initialize(repeating: 0, count: size)
            var next = storage
            func take(_ count: Int) -> UnsafeMutablePointer<Float> {
                defer { next += count }
                return next
            }
            (hr, hi, p, xr, xi) = (take(state), take(state), take(state), take(state), take(state))
            (psi, total, yr, yi, er, ei) = (take(bins), take(bins), take(bins), take(bins), take(bins), take(bins))
            (previous, frame, time) = (take(block), take(2 * block), take(2 * block))
            p.update(repeating: Self.initialP, count: state)
        }

        deinit { storage.deallocate() }

        /// 預習完從頭再來：回音路徑留著，參考訊號的歷史清掉
        func resetHistory() {
            xr.update(repeating: 0, count: Self.partitions * bins)
            xi.update(repeating: 0, count: Self.partitions * bins)
            previous.update(repeating: 0, count: Self.block)
        }

        func step(reference: [Float], mic: [Float], adapt: Bool, error: inout [Float]) {
            let block = Self.block, bins = self.bins, parts = Self.partitions
            let (hr, hi, p, xr, xi, psi, total, yr, yi, er, ei, frame, time) = (hr, hi, p, xr, xi, psi, total, yr, yi, er, ei, frame, time)
            // 參考訊號：前一塊＋這一塊做 FFT，放進歷史的最前面
            // 範圍重疊，用 memmove
            memmove(xr + bins, xr, (parts - 1) * bins * MemoryLayout<Float>.size)
            memmove(xi + bins, xi, (parts - 1) * bins * MemoryLayout<Float>.size)
            frame.update(from: previous, count: block)
            reference.withUnsafeBufferPointer { (frame + block).update(from: $0.baseAddress!, count: block) }
            previous.update(from: frame + block, count: block)
            fft.forward(frame, re: xr, im: xi)
            // 估計的回音 Y = Σ H·X，取後半塊
            yr.update(repeating: 0, count: bins)
            yi.update(repeating: 0, count: bins)
            for q in 0..<parts {
                let o = q * bins
                for k in 0..<bins {
                    yr[k] += hr[o + k] * xr[o + k] - hi[o + k] * xi[o + k]
                    yi[k] += hr[o + k] * xi[o + k] + hi[o + k] * xr[o + k]
                }
            }
            fft.inverse(re: yr, im: yi, out: time)
            for i in 0..<block { error[i] = mic[i] - time[block + i] }
            guard adapt else { return }
            // 誤差的頻譜（前半補 0）
            frame.update(repeating: 0, count: block)
            error.withUnsafeBufferPointer { (frame + block).update(from: $0.baseAddress!, count: block) }
            fft.forward(frame, re: er, im: ei)
            // Kalman 增益：μ = P / (Σ|X|²P + 誤差功率)
            for k in 0..<bins {
                psi[k] = 0.5 * psi[k] + 0.5 * (er[k] * er[k] + ei[k] * ei[k])
                total[k] = psi[k]
            }
            for q in 0..<parts {
                let o = q * bins
                for k in 0..<bins { total[k] += (xr[o + k] * xr[o + k] + xi[o + k] * xi[o + k]) * p[o + k] }
            }
            for q in 0..<parts {
                let o = q * bins
                for k in 0..<bins {
                    let x2 = xr[o + k] * xr[o + k] + xi[o + k] * xi[o + k]
                    let mu = p[o + k] / (total[k] + 1e-10)
                    // H += μ · conj(X) · E
                    hr[o + k] += mu * (xr[o + k] * er[k] + xi[o + k] * ei[k])
                    hi[o + k] += mu * (xr[o + k] * ei[k] - xi[o + k] * er[k])
                    let h2 = hr[o + k] * hr[o + k] + hi[o + k] * hi[o + k]
                    p[o + k] = Self.a2 * (1 - 0.5 * mu * x2) * p[o + k] + (1 - Self.a2) * h2
                }
            }
            // 每塊輪流把一個分區的時域後半清掉（線性卷積的限制）
            let o = round * bins
            fft.inverse(re: hr + o, im: hi + o, out: time)
            (time + block).update(repeating: 0, count: block)
            fft.forward(time, re: hr + o, im: hi + o)
            round = (round + 1) % parts
        }
    }

    /// 每個頻帶不讓輸出比原本的麥克風大聲（STFT 512 點、跳 256 點、開根號的 Hann 窗）：濾波器沒學好、
    /// 回音路徑突然變了（換成耳機）時，不會把電腦聲音反過來加進麥克風
    final class Clamp {
        private let n = 512, hop = 256
        private let fft = RealFFT(log2n: 9)
        private let window: [Float]
        private var micFrame: [Float], outFrame: [Float], accumulator: [Float]
        private var dr: [Float], di: [Float], er: [Float], ei: [Float]
        private var time: [Float]

        init() {
            window = (0..<512).map { Float(sin(Double.pi * Double($0) / 512)) }   // sqrt(periodic hann)
            micFrame = [Float](repeating: 0, count: 512); outFrame = micFrame; accumulator = micFrame; time = micFrame
            dr = [Float](repeating: 0, count: 257); di = dr; er = dr; ei = dr
        }

        /// 收一塊（256 點），吐出前一塊處理好的
        func process(mic: [Float], echoFree: [Float], into out: inout [Float]) {
            micFrame.withUnsafeMutableBufferPointer { $0.baseAddress!.update(from: $0.baseAddress! + hop, count: hop) }
            outFrame.withUnsafeMutableBufferPointer { $0.baseAddress!.update(from: $0.baseAddress! + hop, count: hop) }
            for i in 0..<hop { micFrame[hop + i] = mic[i]; outFrame[hop + i] = echoFree[i] }
            for i in 0..<n { time[i] = micFrame[i] * window[i] }
            fft.forward(time, re: &dr, im: &di)
            for i in 0..<n { time[i] = outFrame[i] * window[i] }
            fft.forward(time, re: &er, im: &ei)
            for k in 0...hop {
                let d = (dr[k] * dr[k] + di[k] * di[k]).squareRoot()
                let e = (er[k] * er[k] + ei[k] * ei[k]).squareRoot()
                if e > d {
                    let ratio = d / (e + 1e-12)
                    er[k] *= ratio
                    ei[k] *= ratio
                }
            }
            fft.inverse(re: er, im: ei, out: &time)
            for i in 0..<n { accumulator[i] += time[i] * window[i] }
            for i in 0..<hop { out[i] = accumulator[i] }
            accumulator.withUnsafeMutableBufferPointer { $0.baseAddress!.update(from: $0.baseAddress! + hop, count: hop) }
            for i in hop..<n { accumulator[i] = 0 }
        }
    }
}

/// 實數 FFT（vDSP），頻譜的格式、大小跟 numpy 的 rfft／irfft 一樣：n/2+1 個複數，正轉不縮放，反轉除以 n
final class RealFFT {
    let n: Int
    private let log2n: vDSP_Length
    private let setup: FFTSetup
    private var real: [Float], imag: [Float]

    init(log2n: Int) {
        n = 1 << log2n
        self.log2n = vDSP_Length(log2n)
        setup = vDSP_create_fftsetup(self.log2n, FFTRadix(kFFTRadix2))!
        real = [Float](repeating: 0, count: n / 2)
        imag = real
    }

    deinit { vDSP_destroy_fftsetup(setup) }

    func forward(_ x: UnsafePointer<Float>, re: UnsafeMutablePointer<Float>, im: UnsafeMutablePointer<Float>) {
        let half = n / 2
        real.withUnsafeMutableBufferPointer { r in
            imag.withUnsafeMutableBufferPointer { i in
                var split = DSPSplitComplex(realp: r.baseAddress!, imagp: i.baseAddress!)
                x.withMemoryRebound(to: DSPComplex.self, capacity: half) { vDSP_ctoz($0, 2, &split, 1, vDSP_Length(half)) }
                vDSP_fft_zrip(setup, &split, 1, log2n, FFTDirection(FFT_FORWARD))
            }
        }
        // vDSP 的正轉是兩倍；直流、奈奎斯特頻率擠在第 0 個的實部、虛部
        re[0] = real[0] * 0.5; im[0] = 0
        re[half] = imag[0] * 0.5; im[half] = 0
        for k in 1..<half { re[k] = real[k] * 0.5; im[k] = imag[k] * 0.5 }
    }

    func inverse(re: UnsafePointer<Float>, im: UnsafePointer<Float>, out: UnsafeMutablePointer<Float>) {
        let half = n / 2
        real[0] = re[0]; imag[0] = re[half]
        for k in 1..<half { real[k] = re[k]; imag[k] = im[k] }
        real.withUnsafeMutableBufferPointer { r in
            imag.withUnsafeMutableBufferPointer { i in
                var split = DSPSplitComplex(realp: r.baseAddress!, imagp: i.baseAddress!)
                vDSP_fft_zrip(setup, &split, 1, log2n, FFTDirection(FFT_INVERSE))
                out.withMemoryRebound(to: DSPComplex.self, capacity: half) { vDSP_ztoc(&split, 1, $0, 2, vDSP_Length(half)) }
            }
        }
        var scale = 1 / Float(n)
        vDSP_vsmul(out, 1, &scale, out, 1, vDSP_Length(n))
    }
}
