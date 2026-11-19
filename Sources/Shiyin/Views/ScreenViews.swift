import AppKit
import QuickLook
import SwiftUI

// MARK: - 會議中的畫面（design §83.4、R109.4）

/// 縮圖：背景讀檔、縮小（長會議的逐字稿一次幾十張，不在主執行緒解 2560 px 的 JPEG），讀過的記在記憶體
struct ScreenThumbnail: View {
    let url: URL
    let width: CGFloat
    @State private var image: NSImage?

    var body: some View {
        Group {
            if let image {
                Image(nsImage: image)
                    .resizable()
                    .aspectRatio(contentMode: .fit)
            } else {
                Rectangle().fill(Color.primary.opacity(0.06)).aspectRatio(16 / 10, contentMode: .fit)
            }
        }
        .frame(width: width)
        .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 8, style: .continuous).strokeBorder(Color.primary.opacity(0.12)))
        .task(id: url) { image = await ScreenThumbnailCache.shared.image(for: url) }
    }
}

@MainActor
final class ScreenThumbnailCache {
    static let shared = ScreenThumbnailCache()
    private let cache = NSCache<NSURL, NSImage>()

    func image(for url: URL) async -> NSImage? {
        if let cached = cache.object(forKey: url as NSURL) { return cached }
        let box = await Task.detached(priority: .userInitiated) { () -> ImageBox? in
            guard let source = CGImageSourceCreateWithURL(url as CFURL, nil) else { return nil }
            let options: [CFString: Any] = [kCGImageSourceCreateThumbnailFromImageAlways: true,
                                            kCGImageSourceCreateThumbnailWithTransform: true,
                                            kCGImageSourceThumbnailMaxPixelSize: 720]
            return CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary).map(ImageBox.init)
        }.value
        guard let box else { return nil }
        let image = NSImage(cgImage: box.image, size: .zero)
        cache.setObject(image, forKey: url as NSURL)
        return image
    }
}

/// 逐字稿裡的一列（選了「夾在逐字稿裡一列」）：時間、縮圖、辨識出的字。
/// 三條路：縮圖點一下放大；滑過或選到時右邊「⋯」、右鍵同一份選單；選到時 Return 放大、Delete 刪除、⌘C 複製圖片
struct ScreenRow: View {
    @Environment(AppModel.self) private var model
    @Environment(\.textScale) private var scale
    let screen: MeetingDocument
    var highlighted = false
    /// 錄音中的即時逐字稿：小一點，沒有選單（錄完再整理）
    var compact = false
    @FocusState private var focused: Bool

    var body: some View {
        let at = screen.at ?? 0
        let reading = screen.id.map { model.readingScreens.contains($0) } ?? false
        let firstLine = screen.screenText.split(separator: "\n").first.map(String.init)
        HStack(alignment: .top, spacing: compact ? 12 : 14) {
            Group {
                if compact {
                    Text(TimeFormat.string(at)).contentFont(.callout, monospacedDigit: true).foregroundStyle(Color.celadon)
                } else {
                    TimeButton(time: TimeFormat.string(at), seek: at)
                }
            }
            .frame(width: (compact ? 52 : 58) * scale, alignment: .trailing)
            .padding(.top, 2)
            Button { if !compact { model.previewScreen(screen) } } label: {
                ScreenThumbnail(url: screen.fileURL, width: (compact ? 168 : 220) * scale)
            }
            .buttonStyle(.plain)
            .help(compact ? "" : String(localized: "放大"))
            .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 4) {
                if compact {
                    // 截圖、貼上、拖進來的都是「畫面」（不一定是截的）
                    Label("畫面", systemImage: "photo").contentFont(.callout, weight: .medium)
                }
                Group {
                    if reading {
                        Text("正在讀取畫面上的字…")
                    } else if let firstLine {
                        Text(firstLine)
                    } else {
                        Text("畫面上沒有辨識出文字")
                    }
                }
                .foregroundStyle(.secondary)
                .lineLimit(compact ? 1 : 2)
                if !compact {
                    Text("畫面 \(TimeFormat.string(at))").contentFont(.caption).foregroundStyle(.tertiary)
                }
            }
            .padding(.top, 2)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .padding(.vertical, compact ? 2 : 6)
        // 即時逐字稿的行沒有左右內距，時間要對齊；會後的逐字稿跟段落一樣留 10
        .padding(.horizontal, compact ? 0 : 10)
        .background(background, in: RoundedRectangle(cornerRadius: 8, style: .continuous))
        .modifier(ScreenRowActions(screen: screen, enabled: !compact, focused: $focused))
        .accessibilityElement(children: .combine)
        .accessibilityLabel(Text("畫面 \(TimeFormat.string(at))"))
        .accessibilityValue(Text(firstLine ?? ""))
    }

    private var background: Color {
        if focused { return Color.primary.opacity(0.09) }
        if highlighted { return Color.signal.opacity(0.18) }
        return .clear
    }
}

/// 逐字稿裡那一列的動作：選取（點一下）、鍵盤、右鍵和「⋯」
private struct ScreenRowActions: ViewModifier {
    @Environment(AppModel.self) private var model
    let screen: MeetingDocument
    let enabled: Bool
    var focused: FocusState<Bool>.Binding

    func body(content: Content) -> some View {
        if enabled {
            content
                .contentShape(Rectangle())
                .focusable()
                .focused(focused)
                .focusEffectDisabled()
                .onTapGesture(count: 2) { model.previewScreen(screen) }
                .onTapGesture { focused.wrappedValue = true }
                .floatingRowMenu(selected: focused.wrappedValue) { model.screenMenu(screen) }
                .onKeyPress(.return) {
                    model.previewScreen(screen)
                    return .handled
                }
                .onDeleteCommand { model.confirmDeleteScreen = screen.id }
                .onCopyCommand {
                    guard let image = NSImage(contentsOf: screen.fileURL) else { return [] }
                    return [NSItemProvider(object: image)]
                }
                .accessibilityAction(named: Text("放大")) { model.previewScreen(screen) }
                .accessibilityAction(named: Text("刪除")) { model.confirmDeleteScreen = screen.id }
        } else {
            content
        }
    }
}

/// 逐字稿和畫面依時間排在一起：畫面放在第一個「開始時間不早於它」的段落前面，比最後一段晚的放在最後
enum ScreenPlacement {
    /// 回傳每一段前面要放哪些畫面，和放在最後的
    static func place(_ screens: [MeetingDocument], starts: [Double]) -> (before: [Int: [MeetingDocument]], trailing: [MeetingDocument]) {
        var before: [Int: [MeetingDocument]] = [:]
        var trailing: [MeetingDocument] = []
        for screen in screens {
            let at = screen.at ?? 0
            if let index = starts.firstIndex(where: { $0 >= at }) { before[index, default: []].append(screen) }
            else { trailing.append(screen) }
        }
        return (before, trailing)
    }
}

// MARK: - 改時間（design §83.4，共用的彈窗 §80）

struct MovingScreen: Identifiable { let id: Int64 }

struct MoveScreenSheet: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    let id: Int64
    @State private var text = ""
    @State private var original = ""

    var body: some View {
        let seconds = TimeFormat.seconds(text.trimmingCharacters(in: .whitespaces))
        let duration = Double(model.detail?.meeting.durationSeconds ?? 0)
        let tooLate = seconds.map { duration > 0 && $0 > duration } ?? false
        let canSave = seconds != nil && !tooLate && text != original
        VStack(alignment: .leading, spacing: 18) {
            SheetHeader(title: String(localized: "改這張畫面的時間"), subtitle: hint(seconds: seconds, tooLate: tooLate, duration: duration))
            VStack(alignment: .leading, spacing: 6) {
                SheetFieldLabel(String(localized: "錄音時間"))
                SheetTextField(placeholder: "12:34", text: $text, focusOnAppear: true) { if canSave { save(seconds!) } }
            }
            HStack(spacing: 10) {
                if model.player.isLoaded {
                    Button("用目前播放的位置") { text = TimeFormat.string(model.player.currentTime) }
                        .buttonStyle(SheetPillButtonStyle(.secondary))
                }
                Spacer()
                Button("取消") { dismiss() }
                    .buttonStyle(SheetPillButtonStyle(.secondary))
                    .keyboardShortcut(.cancelAction)
                Button("儲存") { if let seconds { save(seconds) } }
                    .buttonStyle(SheetPillButtonStyle(.primary))
                    .keyboardShortcut(.defaultAction)
                    .disabled(!canSave)
            }
        }
        .sheetChrome()
        .onAppear {
            original = TimeFormat.string(model.detail?.screens.first { $0.id == id }?.at ?? 0)
            text = original
        }
    }

    /// 說明行：寫會影響什麼；時間打錯、超過會議長度時改成說為什麼不能存
    private func hint(seconds: Double?, tooLate: Bool, duration: Double) -> String {
        if seconds == nil && !text.trimmingCharacters(in: .whitespaces).isEmpty {
            return String(localized: "請輸入像 12:34 或 1:02:03 的時間。")
        }
        if tooLate { return String(localized: "超過會議的長度（\(TimeFormat.string(duration))）。") }
        return String(localized: "畫面會移到這個時間，整理會議紀錄時照新的時間對上逐字稿。")
    }

    private func save(_ seconds: Double) {
        model.moveScreen(id, to: seconds)
        dismiss()
    }
}

/// 主視窗上的改時間彈窗、放大（Quick Look，← → 看同一場的其他張）（design §83.4）
struct ScreenSheets: ViewModifier {
    @Environment(AppModel.self) private var model

    func body(content: Content) -> some View {
        content
            .sheet(item: Binding(get: { model.movingScreen.map(MovingScreen.init) }, set: { model.movingScreen = $0?.id })) {
                MoveScreenSheet(id: $0.id)
            }
            .quickLookPreview(Binding(get: { model.screenPreview }, set: { model.screenPreview = $0 }), in: model.screenPreviewItems)
    }
}
