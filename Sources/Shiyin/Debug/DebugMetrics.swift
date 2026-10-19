#if DEBUG
import Foundation

/// 只在 DEBUG：驗證效能用的計數（design §21.12、§21.13）。`shiyinctl metrics <輸出.json> [reset]` 讀出或歸零
@MainActor
enum DebugMetrics {
    /// 各種事件發生的次數，例如逐字稿列表重算、會議列表重讀
    static var counts: [String: Int] = [:]
    /// 各種工作最近一次花的毫秒數，例如 reloadDetail
    static var timings: [String: Double] = [:]
    /// 最近一次 AppModel.perform（含重讀）花的毫秒數，以及最慢的一次
    static var lastPerformMs = 0.0
    static var maxPerformMs = 0.0
    /// 主執行緒最長卡住多久：每 10 毫秒一次的計時器，兩次之間隔了多久
    static var maxMainGapMs = 0.0
    private static var lastTick = Date()
    private static var timer: Timer?

    static func bump(_ name: String) {
        counts[name, default: 0] += 1
    }

    static func recordPerform(_ ms: Double) {
        lastPerformMs = ms
        maxPerformMs = max(maxPerformMs, ms)
    }

    static func time(_ name: String, since start: Date) {
        timings[name] = Date().timeIntervalSince(start) * 1000
    }

    static func reset() {
        counts = [:]
        timings = [:]
        lastPerformMs = 0
        maxPerformMs = 0
        maxMainGapMs = 0
        lastTick = Date()
    }

    /// 啟動時開始量主執行緒的間隔（common modes，選單、捲動時也量得到）
    static func startMainThreadWatch() {
        guard timer == nil else { return }
        let timer = Timer(timeInterval: 0.01, repeats: true) { _ in
            MainActor.assumeIsolated {
                let now = Date()
                maxMainGapMs = max(maxMainGapMs, now.timeIntervalSince(lastTick) * 1000)
                lastTick = now
            }
        }
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
    }

    static var snapshot: [String: Any] {
        ["counts": counts, "timings": timings, "lastPerformMs": lastPerformMs, "maxPerformMs": maxPerformMs, "maxMainGapMs": maxMainGapMs]
    }
}

/// 只在 DEBUG：量轉錄各階段的記憶體（design §21.16）。背景執行緒每 50 毫秒讀一次 phys_footprint，記下每個階段的最大值
final class MemoryMeter: @unchecked Sendable {
    private let lock = NSLock()
    private var stage = "開始"   // l10n-skip
    private var order: [String] = []
    private var peaks: [String: UInt64] = [:]
    private var running = true
    let baseline = MemoryMeter.footprint()

    init() {
        Thread.detachNewThread { [self] in
            while lock.withLock({ running }) {
                let value = Self.footprint()
                lock.withLock {
                    if peaks[stage] == nil { order.append(stage) }
                    peaks[stage] = max(peaks[stage] ?? 0, value)
                }
                Thread.sleep(forTimeInterval: 0.05)
            }
        }
    }

    func set(stage: String) { lock.withLock { self.stage = stage } }

    /// 停止量測，回傳各階段的峰值（MB，依出現順序）和整個程序到目前為止的峰值
    func stop() -> [String: Any] {
        lock.withLock { running = false }
        let mb = { (value: UInt64) in Double(value) / 1_048_576 }
        return lock.withLock {
            ["baselineMB": mb(baseline), "stages": order.map { ["stage": $0, "peakMB": mb(peaks[$0] ?? 0)] },
             "peakMB": mb(peaks.values.max() ?? 0), "processPeakMB": mb(Self.lifetimePeak())]
        }
    }

    static func footprint() -> UInt64 { vmInfo()?.phys_footprint ?? 0 }
    static func lifetimePeak() -> UInt64 { UInt64(vmInfo()?.ledger_phys_footprint_peak ?? 0) }

    private static func vmInfo() -> task_vm_info_data_t? {
        var info = task_vm_info_data_t()
        var count = mach_msg_type_number_t(MemoryLayout<task_vm_info_data_t>.size / MemoryLayout<integer_t>.size)
        let result = withUnsafeMutablePointer(to: &info) {
            $0.withMemoryRebound(to: integer_t.self, capacity: Int(count)) { task_info(mach_task_self_, task_flavor_t(TASK_VM_INFO), $0, &count) }
        }
        return result == KERN_SUCCESS ? info : nil
    }
}
#endif
