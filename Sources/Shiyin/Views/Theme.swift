import AppKit
import SwiftUI

extension EnvironmentValues {
    /// DEBUG 截圖（ImageRenderer）時為 true：不包 ScrollView，內容才畫得出來（design §13）
    @Entry var snapshotMode = false
    /// 正在看的會議的語言：決定會議內容的字形和標題字型（design §18.6）
    @Entry var meetingLanguage: MeetingLanguage = .zh
}

/// 視覺 token（design §9.3）
extension Color {
    /// 青瓷綠：主要動作、目前播放的段落
    static let celadon = Color(nsColor: .dynamic(light: 0x2D6A5D, dark: 0x6DB2A1))
    /// VU 琥珀：低信心、待確認、進行中
    static let signal = Color(nsColor: .dynamic(light: 0xB97F06, dark: 0xE2AD3B))
}

extension Color {
    /// 說話者的顏色（8 色循環，深淺色模式都看得清楚）。
    /// 青瓷綠是時間點按鈕的顏色、琥珀色代表低信心，所以這兩種排在最後，人少的會議不會用到
    static func speaker(_ idx: Int) -> Color {
        let palette: [(UInt32, UInt32)] = [
            (0x3F5BA9, 0x8FA8EE), (0x8E3B6B, 0xE08BBE), (0x4F7A28, 0x9BCB6E), (0x7A4FB5, 0xBFA0EE),
            (0xA23B3B, 0xEE8E8E), (0x2F7486, 0x7CC4D6), (0x9A5B13, 0xE2A65E), (0x2D6A5D, 0x6DB2A1),
        ]
        let pair = palette[(max(idx, 1) - 1) % palette.count]
        return Color(nsColor: .dynamic(light: pair.0, dark: pair.1))
    }
}

extension NSColor {
    static func dynamic(light: UInt32, dark: UInt32) -> NSColor {
        NSColor(name: nil) { appearance in
            let hex = appearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua ? dark : light
            return NSColor(srgbRed: CGFloat((hex >> 16) & 0xFF) / 255, green: CGFloat((hex >> 8) & 0xFF) / 255,
                           blue: CGFloat(hex & 0xFF) / 255, alpha: 1)
        }
    }
}

extension Font {
    /// 會議標題、會議紀錄段落標題：宋體-繁，讓紀錄讀起來像一份文件
    static func songti(_ size: CGFloat, weight: Font.Weight = .semibold) -> Font {
        .custom("Songti TC", size: size).weight(weight)
    }
}

/// 會議內容的文字（逐字稿、會議紀錄的條目、筆記）：字形跟著會議語言，日文會議用日文字形（design §18.6）。
/// 只套在內容上，介面的說明文字、按鈕維持繁中字形
private struct MeetingText: ViewModifier {
    @Environment(\.meetingLanguage) private var language

    func body(content: Content) -> some View {
        content.typesettingLanguage(Locale.Language(identifier: language == .ja ? "ja" : "zh-Hant"))
    }
}

/// 會議內容的標題（會議標題、段落、議題）：宋體-繁；日文會議改用ヒラギノ明朝，宋體的漢字是中文寫法（design §18.6）
private struct ContentTitleFont: ViewModifier {
    @Environment(\.meetingLanguage) private var language
    let size: CGFloat

    func body(content: Content) -> some View {
        content.font(language == .ja ? .custom("Hiragino Mincho ProN", size: size).weight(.semibold) : .songti(size))
            .meetingText()
    }
}

extension View {
    func meetingText() -> some View { modifier(MeetingText()) }
    func contentTitleFont(_ size: CGFloat) -> some View { modifier(ContentTitleFont(size: size)) }
}

/// 可以點的時間點：跳到錄音的那個位置播放（R4.2、R5.2）
struct TimeButton: View {
    @Environment(AppModel.self) private var model
    let time: String
    /// 要跳到的秒數；沒給就照顯示的時間。逐字稿的段落給真正的開始時間（24.4 秒的段落顯示 00:24，跳到 24.0 高亮會落在上一段）
    var seek: Double? = nil

    var body: some View {
        // 沒有錄音的會議（例如匯入逐字稿）點了也沒辦法播，顯示成一般文字
        if let seconds = seek ?? TimeFormat.seconds(time), model.player.isLoaded {
            Button(time) { model.player.seek(to: seconds) }
                .buttonStyle(.plain)
                .font(.callout.monospacedDigit())
                .foregroundStyle(Color.celadon)
                .help("從 \(time) 開始播放")
        } else if !time.isEmpty {
            Text(time).font(.callout.monospacedDigit()).foregroundStyle(.secondary)
        }
    }
}

/// 錄音音量表：模仿 VU 表的分段 LED，滿格的地方是琥珀色（design §9.3）
struct VUMeter: View {
    var level: Float
    var segments = 14

    var body: some View {
        HStack(spacing: 2) {
            ForEach(0..<segments, id: \.self) { i in
                RoundedRectangle(cornerRadius: 1)
                    .fill(color(i))
                    .frame(width: 4, height: 12)
            }
        }
        .accessibilityElement()
        .accessibilityLabel("音量")
        .accessibilityValue("\(Int(level * 100))%")
    }

    private func color(_ i: Int) -> Color {
        let lit = Float(i) < level * Float(segments)
        guard lit else { return Color.secondary.opacity(0.18) }
        return i >= segments - 3 ? .signal : .celadon
    }
}

/// 跟 VUMeter 長得一樣的音量條，但用 AppKit 的圖層畫、自己每 0.08 秒讀一次音量（design §25）：
/// SwiftUI 的 view 每變一次，整個視窗都要重新排版、重算最小尺寸，錄音時音量一直在變，CPU 會多用三成
struct LevelMeter: NSViewRepresentable {
    var segments = 14
    let level: @MainActor () -> Float

    func makeNSView(context: Context) -> LevelMeterView { LevelMeterView(segments: segments, level: level) }

    func updateNSView(_ view: LevelMeterView, context: Context) { view.level = level }

    func sizeThatFits(_ proposal: ProposedViewSize, nsView: LevelMeterView, context: Context) -> CGSize? {
        nsView.intrinsicContentSize
    }
}

final class LevelMeterView: NSView {
    var level: @MainActor () -> Float
    private let segments: Int
    private var bars: [CALayer] = []
    private var lit = -1
    private var timer: Timer?

    init(segments: Int, level: @escaping @MainActor () -> Float) {
        self.segments = segments
        self.level = level
        super.init(frame: NSRect(origin: .zero, size: CGSize(width: segments * 6 - 2, height: 12)))
        wantsLayer = true
        for i in 0..<segments {
            let bar = CALayer()
            bar.frame = CGRect(x: i * 6, y: 0, width: 4, height: 12)
            bar.cornerRadius = 1
            layer?.addSublayer(bar)
            bars.append(bar)
        }
        setAccessibilityElement(true)
        setAccessibilityRole(.levelIndicator)
        setAccessibilityLabel(String(localized: "音量"))
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override var intrinsicContentSize: NSSize { NSSize(width: segments * 6 - 2, height: 12) }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        timer?.invalidate()
        timer = nil
        guard window != nil else { return }
        let timer = Timer(timeInterval: 0.08, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.refresh() }
        }
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
        refresh()
    }

    override func viewDidChangeEffectiveAppearance() {
        super.viewDidChangeEffectiveAppearance()
        lit = -1
        refresh()
    }

    /// 亮的格數變了才改顏色（跟 VUMeter 同樣的規則：最後三格是琥珀色）
    private func refresh() {
        let value = level()
        let count = (0..<segments).filter { Float($0) < value * Float(segments) }.count
        guard count != lit else { return }
        lit = count
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        effectiveAppearance.performAsCurrentDrawingAppearance {
            let off = NSColor.secondaryLabelColor.withAlphaComponent(0.18).cgColor
            let green = NSColor.dynamic(light: 0x2D6A5D, dark: 0x6DB2A1).cgColor
            let amber = NSColor.dynamic(light: 0xB97F06, dark: 0xE2AD3B).cgColor
            for (i, bar) in bars.enumerated() {
                bar.backgroundColor = i < count ? (i >= segments - 3 ? amber : green) : off
            }
        }
        CATransaction.commit()
        setAccessibilityValue("\(Int(value * 100))%")
    }
}

/// 錄音經過時間，每秒更新
struct ElapsedText: View {
    let since: Date

    var body: some View {
        TimelineView(.periodic(from: .now, by: 1)) { context in
            Text(TimeFormat.string(context.date.timeIntervalSince(since)))
                .monospacedDigit()
        }
    }
}
