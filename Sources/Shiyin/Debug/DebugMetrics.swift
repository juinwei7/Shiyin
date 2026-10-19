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
#endif
