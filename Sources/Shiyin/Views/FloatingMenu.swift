import AppKit
import SwiftUI

// MARK: - 自己畫的選單（R105、design §79）

/// 「⋯」和右鍵選單的一項（自己畫的選單：圓角深色面板、細線圖示、右邊灰色的快捷鍵、子選單「›」）
struct MenuEntry: Identifiable {
    enum Kind {
        case action(() -> Void)
        case submenu([MenuEntry])
        case divider
    }

    let id = UUID()
    var title = ""
    var icon: String?
    /// 只是提示（例如 ↩、⌘⌫），不註冊快捷鍵
    var shortcut: String?
    var destructive = false
    var enabled = true
    var kind: Kind

    static func action(_ title: String, icon: String? = nil, shortcut: String? = nil, destructive: Bool = false,
                       enabled: Bool = true, _ action: @escaping () -> Void) -> MenuEntry {
        MenuEntry(title: title, icon: icon, shortcut: shortcut, destructive: destructive, enabled: enabled, kind: .action(action))
    }

    static func submenu(_ title: String, icon: String? = nil, _ items: [MenuEntry]) -> MenuEntry {
        MenuEntry(title: title, icon: icon, enabled: !items.isEmpty, kind: .submenu(items))
    }

    static var divider: MenuEntry { MenuEntry(kind: .divider) }

    var isDivider: Bool { if case .divider = kind { true } else { false } }
}

/// 開著的選單：一個透明的面板（不搶焦點，跟系統選單一樣主視窗照樣是作用中的），子選單畫在右邊、面板跟著變寬。
/// 點選單外面、按 Esc、選了一項、切到別的 app 就關
@MainActor
final class FloatingMenuController {
    static let shared = FloatingMenuController()

    private var panel: NSPanel?
    private var monitors: [Any] = []
    /// 選單開著時讓作用中的視窗送滑鼠移動事件（滑過靠它，見 installMonitors），關掉時改回原本的
    private var movedWindow: (window: NSWindow, accepted: Bool)?
    private var state: FloatingMenuState?
    /// 選單左上角（螢幕座標），面板變寬時固定這一點
    private var topLeft: NSPoint = .zero

    var isOpen: Bool { panel != nil }

    #if DEBUG
    /// DEBUG 驗收時使用者在用別的 app，點別的 app 不關選單（floatothers off）
    var ignoreOtherApps = false

    /// DEBUG：開著的選單的項目（子選單寫「移到資料夾 ▸ 客戶」）、選某一項
    var debugTitles: [String] {
        func titles(_ entries: [MenuEntry], _ prefix: String) -> [String] {
            entries.flatMap { e -> [String] in
                if e.isDivider { return [prefix + "——"] }
                let line = prefix + e.title + (e.shortcut.map { "（\($0)）" } ?? "") + (e.destructive ? "（紅）" : "") + (e.enabled ? "" : "（停用）")   // l10n-skip
                if case .submenu(let items) = e.kind { return [line] + titles(items, prefix + e.title + " ▸ ") }
                return [line]
            }
        }
        return state.map { titles($0.entries, "") } ?? []
    }

    func debugPick(_ path: String) {
        guard let state else { return }
        var entries = state.entries
        let parts = path.components(separatedBy: " ▸ ")
        for (i, part) in parts.enumerated() {
            guard let entry = entries.first(where: { $0.title == part }) else { return }
            if i == parts.count - 1 { state.activate(entry) } else if case .submenu(let items) = entry.kind { entries = items }
        }
    }

    var debugFrame: NSRect? { panel?.frame }
    var debugWindowNumber: Int { panel?.windowNumber ?? -1 }

    var debugState: String {
        guard let state else { return "" }
        let mouse = state.lastPoint.map { "\(Int($0.x)),\(Int($0.y))" } ?? "-"
        return "open=\(state.openSubmenu ?? -1) hl=\(state.highlighted ?? -1) sub=\(state.subHighlighted ?? -1) frame=\(state.submenuFrame) mouse=\(mouse)"
    }
    #endif

    /// 在螢幕上的一點打開（左上角對齊；超出螢幕時往上、往左挪）
    func show(_ entries: [MenuEntry], at point: NSPoint, from parent: NSWindow? = nil) {
        close()
        guard !entries.isEmpty else { return }
        let state = FloatingMenuState(entries: entries) { [weak self] in self?.close() }
        self.state = state
        let host = NSHostingView(rootView: FloatingMenuView(state: state) { [weak self] size in self?.resize(to: size) })
        host.sizingOptions = []
        let size = host.fittingSize
        let panel = NSPanel(contentRect: NSRect(origin: .zero, size: size), styleMask: [.borderless, .nonactivatingPanel],
                            backing: .buffered, defer: false)
        panel.isFloatingPanel = true
        // 主視窗在桌面層（DEBUG 驗收時放在所有視窗後面）時選單也跟著放低，不出現在使用者的畫面上
        if let parent, parent.level < .normal {
            panel.level = NSWindow.Level(rawValue: parent.level.rawValue + 1)
        } else {
            panel.level = .popUpMenu
        }
        panel.backgroundColor = .clear
        panel.isOpaque = false
        panel.hasShadow = false   // 陰影畫在內容裡（圓角）
        panel.hidesOnDeactivate = true
        panel.contentView = host

        // 內容外面留了 12 點畫陰影，看得到的選單左上角對齊這一點
        let inset = FloatingMenuView.shadowInset
        var origin = NSPoint(x: point.x - inset, y: point.y + inset - size.height)
        if let screen = NSScreen.screens.first(where: { $0.frame.contains(point) })?.visibleFrame {
            if origin.y < screen.minY { origin.y = min(point.y + 4, screen.maxY - size.height) }
            if origin.x + size.width > screen.maxX { origin.x = screen.maxX - size.width }
        }
        topLeft = NSPoint(x: origin.x, y: origin.y + size.height)
        panel.setFrame(NSRect(origin: origin, size: size), display: true)
        panel.orderFront(nil)
        self.panel = panel
        installMonitors()
    }

    /// 在一個 view 下面打開（「⋯」按鈕）
    func show(_ entries: [MenuEntry], below view: NSView) {
        guard let window = view.window else { return }
        let rect = window.convertToScreen(view.convert(view.bounds, to: nil))
        show(entries, at: NSPoint(x: rect.minX, y: rect.minY - 4), from: window)
    }

    func close() {
        monitors.forEach(NSEvent.removeMonitor)
        monitors = []
        if let movedWindow { movedWindow.window.acceptsMouseMovedEvents = movedWindow.accepted }
        movedWindow = nil
        panel?.orderOut(nil)
        panel = nil
        state = nil
    }

    /// 子選單打開、關上時面板跟著變寬變窄，左上角不動
    private func resize(to size: CGSize) {
        guard let panel, size.width > 0, size.height > 0 else { return }
        panel.setFrame(NSRect(x: topLeft.x, y: topLeft.y - size.height, width: size.width, height: size.height), display: true)
    }

    private func installMonitors() {
        // 點在選單外面：關掉，這一下不往下傳（同系統選單）
        if let m = NSEvent.addLocalMonitorForEvents(matching: [.leftMouseDown, .rightMouseDown, .otherMouseDown], handler: { [weak self] event in
            guard let self, let panel = self.panel else { return event }
            if event.window === panel { return event }
            self.close()
            return nil
        }) { monitors.append(m) }
        if let m = NSEvent.addGlobalMonitorForEvents(matching: [.leftMouseDown, .rightMouseDown], handler: { [weak self] _ in
            Task { @MainActor in
                #if DEBUG
                if self?.ignoreOtherApps == true { return }
                #endif
                self?.close()
            }
        }) { monitors.append(m) }
        // 滑過：面板不是作用中的視窗，SwiftUI 的 onHover 有時慢半拍（快速斜著移到子選單時經過的列來不及算），
        // app 收到滑鼠移動就自己算游標在哪一列
        if let key = NSApp.keyWindow {
            movedWindow = (key, key.acceptsMouseMovedEvents)
            key.acceptsMouseMovedEvents = true
        }
        if let m = NSEvent.addLocalMonitorForEvents(matching: [.mouseMoved, .leftMouseDragged], handler: { [weak self] event in
            guard let self, let panel = self.panel, let content = panel.contentView else { return event }
            // 事件的位置換成面板裡的位置（左上原點）
            let screen = event.window.map { $0.convertPoint(toScreen: event.locationInWindow) } ?? event.locationInWindow
            let point = panel.convertPoint(fromScreen: screen)
            self.state?.trackMouse(CGPoint(x: point.x, y: content.bounds.height - point.y))
            return event
        }) { monitors.append(m) }
        // 鍵盤：上下移、Return 選、→ 打開子選單、← 回上一層、Esc 關
        if let m = NSEvent.addLocalMonitorForEvents(matching: .keyDown, handler: { [weak self] event in
            guard let self, let state = self.state else { return event }
            return state.handleKey(event) ? nil : event
        }) { monitors.append(m) }
    }
}

/// 選單的狀態：滑鼠或鍵盤移到哪一項、哪個子選單開著
@Observable
@MainActor
final class FloatingMenuState {
    let entries: [MenuEntry]
    let dismiss: () -> Void
    /// 第一層亮著的那一項、開著的子選單（第一層的位置）、子選單裡亮著的那一項
    var highlighted: Int?
    var openSubmenu: Int?
    var subHighlighted: Int?

    init(entries: [MenuEntry], dismiss: @escaping () -> Void) {
        self.entries = entries
        self.dismiss = dismiss
    }

    var submenuEntries: [MenuEntry] {
        guard let i = openSubmenu, case .submenu(let items) = entries[i].kind else { return [] }
        return items
    }

    func activate(_ entry: MenuEntry) {
        guard entry.enabled, case .action(let action) = entry.kind else { return }
        dismiss()
        action()
    }

    /// 子選單開著時滑到別的列，等一下才換（同系統選單）：斜著往子選單移的時候會經過別的列，
    /// 一經過就換掉的話子選單點不到（2026-11-17 自己驗收時發現）；這段時間滑進子選單就不換
    @ObservationIgnored private var pendingSwitch: Task<Void, Never>?
    /// 子選單在面板裡的框、最後一次滑鼠移動的位置（都是左上原點）：換子選單之前看游標是不是已經在子選單裡
    @ObservationIgnored var submenuFrame: CGRect = .zero
    @ObservationIgnored var lastPoint: CGPoint?

    func hover(_ index: Int) {
        highlighted = index
        pendingSwitch?.cancel()
        let apply = { [weak self] in
            guard let self else { return }
            if case .submenu = self.entries[index].kind, self.entries[index].enabled {
                if self.openSubmenu != index { self.openSubmenu = index; self.subHighlighted = nil; self.subRowFrames = [:] }
            } else {
                self.openSubmenu = nil
                self.subRowFrames = [:]
            }
        }
        guard openSubmenu != nil, openSubmenu != index else { apply(); return }
        pendingSwitch = Task { @MainActor [weak self] in
            try? await Task.sleep(for: .milliseconds(450))
            guard let self, !Task.isCancelled else { return }
            if let point = self.lastPoint, self.submenuFrame.insetBy(dx: -4, dy: -4).contains(point) {
                self.highlighted = self.openSubmenu
                return
            }
            apply()
        }
    }

    /// 每一列在面板裡的框（左上原點）：第一層、子選單分開放
    @ObservationIgnored var rowFrames: [Int: CGRect] = [:]
    @ObservationIgnored var subRowFrames: [Int: CGRect] = [:]

    /// 游標移動：照現在的位置亮那一列（第一層換列會開、換子選單；離開選單時只留子選單那一列亮著）

    func trackMouse(_ point: CGPoint) {
        lastPoint = point
        if openSubmenu != nil, let j = subRowFrames.first(where: { $0.value.contains(point) })?.key {
            if subHighlighted != j { hoverSubmenu(j) }
        } else if let i = rowFrames.first(where: { $0.value.contains(point) })?.key {
            if highlighted != i || (openSubmenu == nil && isSubmenu(i)) { hover(i) }
        } else if openSubmenu == nil || !submenuFrame.contains(point) {
            if highlighted != openSubmenu { highlighted = openSubmenu }
            if subHighlighted != nil { subHighlighted = nil }
        }
    }

    private func isSubmenu(_ i: Int) -> Bool { if case .submenu = entries[i].kind { true } else { false } }

    /// 滑進子選單：取消還沒換的，亮回子選單那一列
    func hoverSubmenu(_ index: Int) {
        pendingSwitch?.cancel()
        subHighlighted = index
        highlighted = openSubmenu
    }

    /// 回傳有沒有處理
    func handleKey(_ event: NSEvent) -> Bool {
        let inSub = openSubmenu != nil && subHighlighted != nil
        let list = inSub ? submenuEntries : entries
        func step(_ from: Int?, _ delta: Int) -> Int? {
            let usable = list.indices.filter { !list[$0].isDivider && list[$0].enabled }
            guard !usable.isEmpty else { return nil }
            guard let from, let i = usable.firstIndex(of: from) else { return delta > 0 ? usable.first : usable.last }
            return usable[(i + delta + usable.count) % usable.count]
        }
        switch event.keyCode {
        case 53:   // Esc
            if inSub { subHighlighted = nil; openSubmenu = nil } else { dismiss() }
        case 125, 126:   // ↓ ↑
            let delta = event.keyCode == 125 ? 1 : -1
            if inSub { subHighlighted = step(subHighlighted, delta) } else { highlighted = step(highlighted, delta); openSubmenu = nil }
        case 124:   // →
            if !inSub, let i = highlighted, case .submenu = entries[i].kind, entries[i].enabled {
                openSubmenu = i
                subHighlighted = step(nil, 1)
            }
        case 123:   // ←
            if inSub { subHighlighted = nil; openSubmenu = nil }
        case 36, 76:   // Return、Enter
            if inSub, let j = subHighlighted { activate(submenuEntries[j]) }
            else if let i = highlighted {
                if case .submenu = entries[i].kind { openSubmenu = i; subHighlighted = step(nil, 1) } else { activate(entries[i]) }
            }
        default:
            return false
        }
        return true
    }
}

/// 選單的樣子：圓角 14 的面板（深色模式深灰、淺色白），每一項高 30：細線圖示、字、右邊灰色的快捷鍵或「›」
struct FloatingMenuView: View {
    let state: FloatingMenuState
    let onSize: (CGSize) -> Void

    var body: some View {
        HStack(alignment: .top, spacing: 4) {
            panel(state.entries, sub: false)
            if state.openSubmenu != nil {
                // 用 padding 不用 offset：面板的大小要算到子選單往下挪的部分，不然下面被切掉
                panel(state.submenuEntries, sub: true)
                    .onGeometryChange(for: CGRect.self) { $0.frame(in: .global) } action: { state.submenuFrame = $0 }
                    .padding(.top, submenuOffset)
            }
        }
        .padding(FloatingMenuView.shadowInset)   // 陰影的空間
        .fixedSize()
        .onGeometryChange(for: CGSize.self) { $0.size } action: { onSize($0) }
    }

    /// 子選單跟它那一項同高
    private var submenuOffset: CGFloat {
        guard let i = state.openSubmenu else { return 0 }
        let before = state.entries.prefix(i)
        return CGFloat(before.filter { !$0.isDivider }.count) * FloatingMenuView.rowHeight
            + CGFloat(before.filter(\.isDivider).count) * FloatingMenuView.dividerHeight
    }

    static let rowHeight: CGFloat = 30
    static let shadowInset: CGFloat = 12
    static let dividerHeight: CGFloat = 9

    private func panel(_ entries: [MenuEntry], sub: Bool) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            ForEach(Array(entries.enumerated()), id: \.element.id) { index, entry in
                if entry.isDivider {
                    Rectangle().fill(Color.primary.opacity(0.1)).frame(height: 1)
                        .padding(.horizontal, 8).padding(.vertical, 4)
                } else {
                    row(entry, highlighted: sub ? state.subHighlighted == index : (state.highlighted == index || state.openSubmenu == index))
                        .onGeometryChange(for: CGRect.self) { $0.frame(in: .global) } action: { frame in
                            if sub { state.subRowFrames[index] = frame } else { state.rowFrames[index] = frame }
                        }
                        .onTapGesture {
                            if case .submenu = entry.kind { if !sub { state.hover(index) } } else { state.activate(entry) }
                        }
                }
            }
        }
        .padding(5)
        .frame(minWidth: 200, alignment: .leading)
        .fixedSize()
        .floatingPanelChrome()
    }

    private func row(_ entry: MenuEntry, highlighted: Bool) -> some View {
        let color: Color = entry.destructive ? .red : .primary
        return HStack(spacing: 10) {
            Group {
                if let icon = entry.icon { Image(systemName: icon) } else { Color.clear }
            }
            .font(.system(size: 14, weight: .light))
            .frame(width: 18)
            Text(entry.title).font(.system(size: 14)).lineLimit(1)
            Spacer(minLength: 16)
            // 快捷鍵、「›」一律灰色（刪除那一項也是；.secondary 會跟著紅色變淡紅）
            if let shortcut = entry.shortcut {
                Text(shortcut).font(.system(size: 13)).foregroundStyle(Color(nsColor: .secondaryLabelColor))
            } else if case .submenu = entry.kind {
                Image(systemName: "chevron.right").font(.system(size: 11, weight: .medium)).foregroundStyle(Color(nsColor: .secondaryLabelColor))
            }
        }
        .foregroundStyle(color)
        .padding(.horizontal, 9)
        .frame(height: FloatingMenuView.rowHeight)
        .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(Color.primary.opacity(highlighted && entry.enabled ? 0.09 : 0)))
        .contentShape(Rectangle())
        .opacity(entry.enabled ? 1 : 0.35)
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(.isButton)
    }
}

// MARK: - 「⋯」和右鍵

/// 抓一個 NSView 當選單的位置（匯出的「分享…」也用它放分享選單）
final class MenuAnchorBox {
    weak var view: NSView?
}

struct MenuAnchor: NSViewRepresentable {
    let box: MenuAnchorBox
    func makeNSView(context: Context) -> NSView {
        let view = NSView()
        box.view = view
        return view
    }
    func updateNSView(_ view: NSView, context: Context) { box.view = view }
}

/// 列右邊的「⋯」：打開自己畫的選單，掛在按鈕下面
struct FloatingMenuButton: View {
    let entries: () -> [MenuEntry]
    @State private var box = MenuAnchorBox()

    var body: some View {
        Button {
            if let view = box.view { FloatingMenuController.shared.show(entries(), below: view) }
        } label: {
            Image(systemName: "ellipsis")
                .frame(width: 22, height: 20)
                .contentShape(Rectangle())
                .background(MenuAnchor(box: box))
        }
        .buttonStyle(.borderless)
        .tint(.secondary)
        .help("更多動作")
        .accessibilityLabel(Text("更多動作"))
    }
}

/// 右鍵（或 Control-點一下）：在游標的地方打開自己畫的選單。列上蓋一個不接任何事件的透明 view 只是記下位置，
/// app 層級的事件監看收到右鍵時找游標下面最小的那一個（展開的會議在資料夾列裡面這種情況取裡面的）
private struct RightClickCatcher: NSViewRepresentable {
    let entries: () -> [MenuEntry]

    func makeNSView(context: Context) -> CatcherView { CatcherView() }
    func updateNSView(_ view: CatcherView, context: Context) { view.entries = entries }

    final class CatcherView: NSView {
        var entries: (() -> [MenuEntry])?

        override func hitTest(_ point: NSPoint) -> NSView? { nil }

        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            if window != nil { RightClickRegistry.shared.add(self) }
        }
    }
}

@MainActor
private final class RightClickRegistry {
    static let shared = RightClickRegistry()
    private let views = NSHashTable<RightClickCatcher.CatcherView>.weakObjects()
    private var monitor: Any?

    func add(_ view: RightClickCatcher.CatcherView) {
        views.add(view)
        guard monitor == nil else { return }
        monitor = NSEvent.addLocalMonitorForEvents(matching: [.rightMouseDown, .leftMouseDown]) { [weak self] event in
            guard event.type == .rightMouseDown || event.modifierFlags.contains(.control), let self, let window = event.window else { return event }
            let hits = self.views.allObjects.filter { view in
                view.window === window && !view.isHiddenOrHasHiddenAncestor
                    && view.bounds.contains(view.convert(event.locationInWindow, from: nil))
            }
            guard let view = hits.min(by: { $0.bounds.width * $0.bounds.height < $1.bounds.width * $1.bounds.height }),
                  let entries = view.entries else { return event }
            FloatingMenuController.shared.show(entries(), at: window.convertPoint(toScreen: event.locationInWindow), from: window)
            return nil
        }
    }
}

extension View {
    /// 右鍵打開自己畫的選單（取代系統的 contextMenu）
    func floatingContextMenu(_ entries: @escaping () -> [MenuEntry]) -> some View {
        overlay(RightClickCatcher(entries: entries))
    }

    /// 列右邊的「⋯」（滑過或選到時顯示）＋右鍵，同一份選單
    func floatingRowMenu(selected: Bool = false, _ entries: @escaping () -> [MenuEntry]) -> some View {
        modifier(FloatingRowMenuModifier(selected: selected, entries: entries))
    }
}

private struct FloatingRowMenuModifier: ViewModifier {
    var selected: Bool
    let entries: () -> [MenuEntry]
    @State private var hovering = false

    func body(content: Content) -> some View {
        content
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.trailing, 22)
            .floatingContextMenu(entries)
            // 「⋯」滑過或選到時才建：每一列都放一顆（含定位用的 NSView）時，長會議的紀錄一次幾百顆，
            // 換會議時多花約 0.08 秒（2026-11-17 用使用者資料的複本量到；使用者覺得「卡卡的」）
            .overlay(alignment: .trailing) {
                if hovering || selected { FloatingMenuButton(entries: entries) }
            }
            .onHover { hovering = $0 }
            // VoiceOver 的「顯示選單」（VO-Shift-M）：同右鍵，在游標的位置打開
            .accessibilityAction(.showMenu) {
                guard let window = NSApp.keyWindow else { return }
                FloatingMenuController.shared.show(entries(), at: NSEvent.mouseLocation, from: window)
            }
    }
}

extension View {
    /// 浮出的面板的外框（自己畫的選單、議題帶的卡片，design §79、§87）：圓角 14、深色 white 0.17／淺色白色、細框、陰影
    func floatingPanelChrome() -> some View {
        background {
            RoundedRectangle(cornerRadius: 14, style: .continuous)
                .fill(Color(nsColor: NSColor(name: nil) { appearance in
                    appearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua ? NSColor(white: 0.17, alpha: 1) : NSColor(white: 1, alpha: 1)
                }))
                .shadow(color: .black.opacity(0.28), radius: 10, y: 4)
        }
        .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous).strokeBorder(Color.primary.opacity(0.1)))
    }
}
