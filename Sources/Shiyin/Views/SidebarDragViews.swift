import SwiftUI
import UniformTypeIdentifiers

// MARK: - 拖曳（R103、design §77）

/// 拖到側欄一列上的位置：放進去（會議、檔案）、放在前面或後面（排序）
enum SidebarDropState: Equatable { case into, before, after }

/// 一列收什麼：會議、資料夾（排序）、Finder 的檔案
struct SidebarRowDrop {
    var meetings: (([Int64]) -> Void)? = nil
    /// 拖過來的鍵、放在前面（true）或後面
    var item: ((String, Bool) -> Void)? = nil
    var files: (([URL]) -> Void)? = nil
}

struct SidebarRowDropDelegate: DropDelegate {
    let drop: SidebarRowDrop
    let height: CGFloat
    @Binding var state: SidebarDropState?

    private func kind(_ info: DropInfo) -> SidebarDropState? {
        if drop.item != nil, info.hasItemsConforming(to: [.shiyinSidebarItem]) { return info.location.y < height / 2 ? .before : .after }
        if drop.meetings != nil, info.hasItemsConforming(to: [.shiyinMeetingIDs]) { return .into }
        if drop.files != nil, info.hasItemsConforming(to: [.fileURL]) { return .into }
        return nil
    }

    func validateDrop(info: DropInfo) -> Bool { kind(info) != nil }
    func dropEntered(info: DropInfo) { state = kind(info) }
    func dropExited(info: DropInfo) { state = nil }

    func dropUpdated(info: DropInfo) -> DropProposal? {
        let current = kind(info)
        if state != current { state = current }
        guard current != nil else { return DropProposal(operation: .forbidden) }
        return DropProposal(operation: info.hasItemsConforming(to: [.fileURL]) ? .copy : .move)
    }

    func performDrop(info: DropInfo) -> Bool {
        let current = kind(info)
        #if DEBUG
        DebugControl.note("drop: \(String(describing: current))")
        #endif
        state = nil
        switch current {
        case .before?, .after?:
            guard let provider = info.itemProviders(for: [.shiyinSidebarItem]).first, let item = drop.item else { return false }
            let before = current == .before
            provider.loadDataRepresentation(forTypeIdentifier: UTType.shiyinSidebarItem.identifier) { data, _ in
                guard let data, let key = String(data: data, encoding: .utf8) else { return }
                DispatchQueue.main.async { item(key, before) }
            }
            return true
        case .into?:
            if let meetings = drop.meetings, let provider = info.itemProviders(for: [.shiyinMeetingIDs]).first {
                provider.loadDataRepresentation(forTypeIdentifier: UTType.shiyinMeetingIDs.identifier) { data, _ in
                    let ids = data.flatMap { String(data: $0, encoding: .utf8) }?.split(separator: ",").compactMap { Int64($0) } ?? []
                    DispatchQueue.main.async { if !ids.isEmpty { meetings(ids) } }
                }
                return true
            }
            if let files = drop.files {
                let providers = info.itemProviders(for: [.fileURL])
                guard !providers.isEmpty else { return false }
                SidebarRowDropDelegate.loadURLs(providers) { files($0) }
                return true
            }
            return false
        case nil:
            return false
        }
    }

    /// 檔案網址都讀到了才回主執行緒
    static func loadURLs(_ providers: [NSItemProvider], done: @escaping ([URL]) -> Void) {
        let group = DispatchGroup()
        var urls: [URL] = []
        let lock = NSLock()
        for provider in providers {
            group.enter()
            _ = provider.loadObject(ofClass: URL.self) { url, _ in
                if let url { lock.lock(); urls.append(url); lock.unlock() }
                group.leave()
            }
        }
        group.notify(queue: .main) { if !urls.isEmpty { done(urls) } }
    }
}

/// 只收會議的地方（下面列表的標題、垃圾桶）
struct MeetingDropModifier: ViewModifier {
    let enabled: Bool
    @Binding var targeted: Bool
    let action: ([Int64]) -> Void

    func body(content: Content) -> some View {
        if enabled {
            content.onDrop(of: [.shiyinMeetingIDs], isTargeted: $targeted) { providers in
                guard let provider = providers.first else { return false }
                provider.loadDataRepresentation(forTypeIdentifier: UTType.shiyinMeetingIDs.identifier) { data, _ in
                    let ids = data.flatMap { String(data: $0, encoding: .utf8) }?.split(separator: ",").compactMap { Int64($0) } ?? []
                    DispatchQueue.main.async { if !ids.isEmpty { action(ids) } }
                }
                return true
            }
        } else {
            content
        }
    }
}

extension View {
    func meetingDrop(enabled: Bool = true, targeted: Binding<Bool>, perform action: @escaping ([Int64]) -> Void) -> some View {
        modifier(MeetingDropModifier(enabled: enabled, targeted: targeted, action: action))
    }
}

/// 「資料夾」那一段（R101.3、R103）：資料夾（照拖曳排好的順序）
struct SidebarFolderRows: View {
    @Environment(AppModel.self) private var model
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        let people = model.sidebarMode == .people
        ForEach(model.sidebarFolderItems) { item in
            let name = item.name
            let key = item.key
            let expanded = model.isRowExpanded(key)
            let toggle = { withAnimation(reduceMotion ? nil : .easeInOut(duration: 0.18)) { model.toggleRow(key) } }
            let reorder: (String, Bool) -> Void = { dragged, before in
                withAnimation(reduceMotion ? nil : .easeInOut(duration: 0.18)) { model.moveSidebarItem(dragged, to: key, before: before) }
            }
            // 有自動加入條件的資料夾加齒輪標記，滑過去看條件（design §72）
            let rule = model.folderRule(name)
            FolderRow(name: name, count: item.count, symbol: "folder",
                      selected: !people && model.folderFilter == name,
                      folder: .init(expanded: expanded, badge: rule == nil ? nil : "gearshape.fill"),
                      dragKey: key,
                      drop: SidebarRowDrop(meetings: { model.moveToFolder($0, folder: name) }, item: reorder,
                                           files: { model.importDropped($0, into: name) }),
                      menu: { model.folderMenu(name) }, action: toggle)
            .help(rule.map { model.folderRuleSummary($0) } ?? "")
            if expanded {
                SidebarPeek(rows: model.peekRows(folder: name)) { model.showFolder(name) }
            }
        }
    }
}

// MARK: - 側欄的拖曳把手

/// 側欄裡不在 List 選取範圍的列（資料夾、展開的會議）：List 只讓有 tag 的列開始拖曳，列上的 DragGesture
/// 也收不到拖曳（滑鼠拖動被 List 吃掉；2026-11-16 自己驗收時發現）。在列上蓋一層只接左鍵的 AppKit view：
/// 拖動超過 4 點開始系統的拖曳（放的地方跟下面列表、Finder 的拖曳同一套 onDrop），沒拖就當成點一下；
/// 右鍵、滑過照舊交給下面的 SwiftUI（右鍵選單、滑過的底）
struct SidebarDragHandle: NSViewRepresentable {
    /// nil：只接點一下（不能拖的列、段落標題）
    let type: UTType?
    let data: () -> Data
    let label: String
    /// 點一下（放開時）：帶著修飾鍵（⌘、⇧ 多選）
    let onClick: (NSEvent.ModifierFlags) -> Void

    func makeNSView(context: Context) -> HandleView { HandleView() }

    func updateNSView(_ view: HandleView, context: Context) {
        view.type = type
        view.data = data
        view.label = label
        view.onClick = onClick
    }

    final class HandleView: NSView, NSDraggingSource {
        var type: UTType?
        var data: (() -> Data)?
        var label = ""
        var onClick: ((NSEvent.ModifierFlags) -> Void)?
        private var downPoint: NSPoint?
        private var dragging = false

        /// 視窗不在前面時第一下也算（同表格的列：點一下就選到，不是只把視窗叫到前面）
        override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

        /// 只接左鍵；右鍵、滑過、捲動交給下面
        override func hitTest(_ point: NSPoint) -> NSView? {
            guard let type = NSApp.currentEvent?.type, [.leftMouseDown, .leftMouseDragged, .leftMouseUp].contains(type) else { return nil }
            return super.hitTest(point)
        }

        override func mouseDown(with event: NSEvent) {
            downPoint = event.locationInWindow
            dragging = false
        }

        override func mouseDragged(with event: NSEvent) {
            guard let type, let down = downPoint, !dragging, let data = data?(),
                  hypot(event.locationInWindow.x - down.x, event.locationInWindow.y - down.y) > 4 else { return }
            dragging = true
            let item = NSPasteboardItem()
            item.setData(data, forType: NSPasteboard.PasteboardType(type.identifier))
            let dragItem = NSDraggingItem(pasteboardWriter: item)
            let image = Self.labelImage(label)
            let at = convert(event.locationInWindow, from: nil)
            dragItem.setDraggingFrame(NSRect(x: at.x - 12, y: at.y - image.size.height / 2, width: image.size.width, height: image.size.height),
                                      contents: image)
            #if DEBUG
            DebugControl.note("drag handle: begin dragging \(label)")
            #endif
            beginDraggingSession(with: [dragItem], event: event, source: self)
        }

        override func mouseUp(with event: NSEvent) {
            if !dragging {
                onClick?(event.modifierFlags)
                // 按鍵（↑↓、Return、Delete）照舊給側欄的表格：這一下沒經過表格，它不會自己變成第一回應者
                var view = superview
                while let current = view, !(current is NSTableView) { view = current.superview }
                if let table = view, window?.firstResponder !== table { window?.makeFirstResponder(table) }
            }
            downPoint = nil
            dragging = false
        }

        func draggingSession(_ session: NSDraggingSession, sourceOperationMaskFor context: NSDraggingContext) -> NSDragOperation {
            context == .withinApplication ? [.move, .generic] : []
        }

        func draggingSession(_ session: NSDraggingSession, endedAt screenPoint: NSPoint, operation: NSDragOperation) {
            #if DEBUG
            DebugControl.note("drag handle: ended operation=\(operation.rawValue)")
            #endif
            dragging = false
            downPoint = nil
        }

        /// 拖的時候跟著游標的標籤：名稱放在圓角的底上
        static func labelImage(_ text: String) -> NSImage {
            let font = NSFont.systemFont(ofSize: NSFont.systemFontSize)
            let attributes: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: NSColor.labelColor]
            let size = (text as NSString).size(withAttributes: attributes)
            let box = NSSize(width: min(size.width, 260) + 20, height: size.height + 10)
            return NSImage(size: box, flipped: false) { rect in
                NSColor.windowBackgroundColor.withAlphaComponent(0.92).setFill()
                NSBezierPath(roundedRect: rect.insetBy(dx: 0.5, dy: 0.5), xRadius: box.height / 2, yRadius: box.height / 2).fill()
                NSColor(Color.celadon).withAlphaComponent(0.7).setStroke()
                NSBezierPath(roundedRect: rect.insetBy(dx: 0.5, dy: 0.5), xRadius: box.height / 2, yRadius: box.height / 2).stroke()
                (text as NSString).draw(with: NSRect(x: 10, y: 5, width: box.width - 20, height: size.height),
                                        options: [.usesLineFragmentOrigin, .truncatesLastVisibleLine], attributes: attributes)
                return true
            }
        }
    }
}

extension View {
    /// 蓋上拖曳把手（只接左鍵；沒拖就是點一下）
    func sidebarDragHandle(type: UTType, label: String, data: @escaping () -> Data, onClick: @escaping () -> Void) -> some View {
        overlay { SidebarDragHandle(type: type, data: data, label: label, onClick: { _ in onClick() }) }
    }

    /// 下面列表的會議列（List 有選取的列）也蓋把手，不用 List 自己的 onDrag：沒選到的列直接拖時，拖得起來但放不進資料夾
    /// （2026-11-17 使用者用三指拖移：「一定要先點到才能拖嗎？」）；點一下、⌘／⇧ 多選改成自己做（type 是 nil 時不能拖）
    func sidebarDragHandle(type: UTType?, label: String, data: @escaping () -> Data,
                           onClick: @escaping (NSEvent.ModifierFlags) -> Void) -> some View {
        overlay { SidebarDragHandle(type: type, data: data, label: label, onClick: onClick) }
    }

    /// 側欄 List 裡「點一下」一律用這個，不要用 `onTapGesture`：List 裡只要有一個 onTapGesture，SwiftUI 就在整個表格加上
    /// 會延後滑鼠事件的單擊、雙擊辨識器，點下面列表的會議時，按下被延後送進表格、放開被扣在辨識器那邊，表格一直等放開，
    /// 整個 app 卡住到下一次按滑鼠（2026-11-17 使用者：「點進去就會當機約 1～5 秒」，用 sample 抓到）
    func sidebarClick(_ action: @escaping () -> Void) -> some View {
        overlay { SidebarDragHandle(type: nil, data: { Data() }, label: "", onClick: { _ in action() }) }
    }
}
