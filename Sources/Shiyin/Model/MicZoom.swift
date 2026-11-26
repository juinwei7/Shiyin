import Foundation

/// 麥克風時間軸放大後看的範圍（R119.10、design §93.5、D202）：start～start + span 秒。整場是 nil（不是這個型別）。
/// 純邏輯，Windows 同一套（標準答案 util/mic-zoom.json）
struct MicZoom: Equatable, Sendable {
    var start: Double
    var span: Double

    var end: Double { start + span }

    /// 最大放大到看 10 秒
    static let minSpan = 10.0
    /// 每按一次 ⌃+／⌃− 的倍數、Ctrl＋滾輪每一格的倍數
    static let keyFactor = 2.0
    static let wheelFactor = 1.25

    func contains(_ t: Double) -> Bool { t >= start && t <= end }

    /// 放大 factor 倍（小於 1 是縮小），anchor（秒）在畫面上的位置不變；長度到整場時回傳 nil
    static func zoomed(_ current: MicZoom?, duration: Double, factor: Double, anchor: Double) -> MicZoom? {
        guard duration > 0, factor > 0 else { return current }
        let view = current ?? MicZoom(start: 0, span: duration)
        let span = min(duration, max(min(minSpan, duration), view.span / factor))
        guard span < duration else { return nil }
        let a = min(max(anchor, 0), duration)
        let ratio = view.span > 0 ? (a - view.start) / view.span : 0
        return MicZoom(start: clampStart(a - ratio * span, span: span, duration: duration), span: span)
    }

    /// 左右移動 seconds 秒（負的往前）
    func panned(duration: Double, by seconds: Double) -> MicZoom {
        MicZoom(start: Self.clampStart(start + seconds, span: span, duration: duration), span: span)
    }

    /// 時間在範圍外時，移到讓它在左邊 10% 的位置；在範圍裡不動
    func following(duration: Double, time: Double) -> MicZoom {
        guard !contains(time) else { return self }
        return MicZoom(start: Self.clampStart(time - span * 0.1, span: span, duration: duration), span: span)
    }

    private static func clampStart(_ start: Double, span: Double, duration: Double) -> Double {
        min(max(0, start), max(0, duration - span))
    }
}
