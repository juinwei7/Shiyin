import AppKit
import SwiftUI

/// 心智圖（design §42.4、R67）：用 MindMap.layout 的座標畫；連線畫在 Canvas，節點是一般的文字。
/// 匯出（exporting）時白底、淺色的調色盤、時間是一般的字；畫面上跟著深色模式，時間點了播放
struct MindMapView: View {
    @Environment(\.snapshotMode) private var snapshotMode
    @Environment(\.textScale) private var textScale
    @Environment(\.colorScheme) private var colorScheme
    let layout: MindMap.Layout
    let language: MeetingLanguage
    /// 畫面上可以用的寬度（外面那層量的；放在上下捲的頁面裡，不能自己用 GeometryReader 量高度）
    var availableWidth: Double = 0
    var exporting = false
    /// 點時間點（畫面上、有錄音時才有）
    var onSeek: ((Double) -> Void)? = nil

    var body: some View {
        if exporting || snapshotMode {
            canvas
        } else {
            // 比畫面寬時縮小到放得下（最小 0.6 倍），再乘上文字大小（⌘＋／⌘－）；還是比畫面寬時左右捲，上下跟著整頁捲
            let fit = max(0.6, min(1, (availableWidth - 16) / layout.width))
            let zoom = fit * textScale
            ScrollView(.horizontal) {
                canvas
                    .scaleEffect(zoom, anchor: .topLeading)
                    .frame(width: layout.width * zoom, height: layout.height * zoom, alignment: .topLeading)
            }
            .frame(height: layout.height * zoom)
        }
    }

    private var dark: Bool { !exporting && colorScheme == .dark }

    private var canvas: some View {
        ZStack(alignment: .topLeading) {
            Canvas { context, _ in
                for e in layout.edges {
                    var path = Path()
                    let mid = (e.x0 + e.x1) / 2
                    path.move(to: CGPoint(x: e.x0, y: e.y0))
                    path.addCurve(to: CGPoint(x: e.x1, y: e.y1), control1: CGPoint(x: mid, y: e.y0), control2: CGPoint(x: mid, y: e.y1))
                    context.stroke(path, with: .color(Color(hex: MindMap.color(e.branch, dark: dark))), lineWidth: e.from == 0 ? 2 : 1.5)
                }
            }
            .frame(width: layout.width, height: layout.height)
            ForEach(layout.boxes, id: \.id) { box in
                MindMapNode(box: box, language: language, dark: dark, exporting: exporting, onSeek: onSeek)
                    .frame(width: box.w, height: box.h)
                    .offset(x: box.x, y: box.y)
            }
        }
        .frame(width: layout.width, height: layout.height, alignment: .topLeading)
        .background(exporting ? Color.white : Color.clear)
        .environment(\.colorScheme, exporting ? .light : colorScheme)
    }
}

private struct MindMapNode: View {
    let box: MindMap.Box
    let language: MeetingLanguage
    let dark: Bool
    let exporting: Bool
    let onSeek: ((Double) -> Void)?

    var body: some View {
        let s = box.style
        let color = Color(hex: MindMap.color(box.branch, dark: dark))
        HStack(alignment: .top, spacing: 0) {
            if box.side < 0 { text; time } else { time; text }
        }
        .padding(.horizontal, s.padH)
        .padding(.vertical, s.padV)
        .frame(width: box.w, height: box.h, alignment: box.side == 0 ? .center : box.side > 0 ? .topLeading : .topTrailing)
        .background {
            switch box.level {
            case 0: RoundedRectangle(cornerRadius: 12).fill(Color(hex: MindMap.rootColor))
            case 1:
                RoundedRectangle(cornerRadius: 8).fill(exporting ? Color.white : Color(nsColor: .textBackgroundColor))
                RoundedRectangle(cornerRadius: 8).strokeBorder(color, lineWidth: 1.5)
            default: EmptyView()
            }
        }
        .contentShape(Rectangle())
        .onTapGesture { if let seconds = seconds { onSeek?(seconds) } }
    }

    private var seconds: Double? { box.time.isEmpty ? nil : TimeFormat.seconds(box.time) }

    @ViewBuilder private var time: some View {
        if !box.time.isEmpty {
            // 估的寬度比真的字窄一點時寧可超出到間距裡，不要變成「00:…」
            let label = Text(box.time).font(.system(size: MindMap.timeSize).monospacedDigit()).lineLimit(1).fixedSize()
            Group {
                if let onSeek, let seconds {
                    Button { onSeek(seconds) } label: { label.foregroundStyle(Color.celadon) }
                        .buttonStyle(.plain)
                        .help("從這裡播放")
                } else {
                    label.foregroundStyle(exporting ? Color(hex: MindMap.secondaryColor) : Color.secondary)
                }
            }
            .frame(width: box.timeWidth - MindMap.timeGap, height: box.style.lineHeight, alignment: box.side < 0 ? .trailing : .leading)
            .padding(box.side < 0 ? .leading : .trailing, MindMap.timeGap)
        }
    }

    private var text: some View {
        let s = box.style
        let alignment: HorizontalAlignment = box.side == 0 ? .center : box.side > 0 ? .leading : .trailing
        return VStack(alignment: alignment, spacing: 0) {
            ForEach(Array(box.lines.enumerated()), id: \.offset) { _, line in
                Text(line)
                    .font(font)
                    .foregroundStyle(foreground)
                    .lineLimit(1)
                    .fixedSize()
                    .frame(height: s.lineHeight)
            }
        }
        .typesettingLanguage(Locale.Language(identifier: language.profile.fonts.typesetting))
    }

    private var font: Font {
        let s = box.style
        return box.level == 2 ? .system(size: s.size) : .custom(language.profile.fonts.titleMac, size: s.size).weight(.semibold)
    }

    private var foreground: Color {
        if box.level == 0 { return .white }
        return exporting ? Color(hex: MindMap.textColor) : .primary
    }
}

extension Color {
    /// "#RRGGBB"
    init(hex: String) {
        let value = UInt32(hex.dropFirst(), radix: 16) ?? 0
        self.init(red: Double((value >> 16) & 0xFF) / 255, green: Double((value >> 8) & 0xFF) / 255, blue: Double(value & 0xFF) / 255)
    }
}

/// 匯出、複製心智圖（design §42.4）：跟畫面同一個 MindMapView，匯出模式
@MainActor
enum MindMapExport {
    static func layout(_ b: ExportBundle) -> MindMap.Layout? {
        guard let notes = b.notes, !notes.isEmpty else { return nil }
        let language = b.meeting.meetingLanguage
        return MindMap.layout(MindMap.tree(notes: notes, title: b.meeting.displayTitle, labels: .init(language)))
    }

    private static func renderer(_ layout: MindMap.Layout, language: MeetingLanguage) -> ImageRenderer<some View> {
        let renderer = ImageRenderer(content: MindMapView(layout: layout, language: language, exporting: true))
        renderer.scale = 2
        return renderer
    }

    static func png(_ layout: MindMap.Layout, language: MeetingLanguage) -> Data? {
        guard let image = renderer(layout, language: language).cgImage else { return nil }
        return NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:])
    }

    static func pdf(_ layout: MindMap.Layout, language: MeetingLanguage, to url: URL) throws {
        var failed = true
        renderer(layout, language: language).render { size, render in
            var box = CGRect(origin: .zero, size: size)
            guard let context = CGContext(url as CFURL, mediaBox: &box, nil) else { return }
            context.beginPDFPage(nil)
            render(context)
            context.endPDFPage()
            context.closePDF()
            failed = false
        }
        if failed { throw ExportError.failed(String(localized: "沒辦法建立 PDF")) }
    }
}
