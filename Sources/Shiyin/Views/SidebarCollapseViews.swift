import SwiftUI

// MARK: - 側欄的收合（R101、design §75）

/// 側欄的列滑鼠移上去時，整列一塊淡淡的圓角底（R101，參考 ChatGPT 桌面版）：跟系統選取的那一塊一樣大，
/// 選到的列被系統的選取蓋過去
struct SidebarHoverModifier: ViewModifier {
    /// 選到的列（側欄自己畫選取的列：資料夾、展開的會議）一直有一塊比較深的灰底，同 ChatGPT 選到的對話
    var selected = false
    @State private var hovering = false

    func body(content: Content) -> some View {
        content
            .background {
                RoundedRectangle(cornerRadius: SidebarHoverModifier.radius, style: .continuous)
                    .fill(Color.primary.opacity(selected ? 0.11 : hovering ? 0.06 : 0))
                    .padding(.horizontal, -SidebarHoverModifier.inset.width)
                    .padding(.vertical, -SidebarHoverModifier.inset.height)
            }
            .onHover { hovering = $0 }
    }

    static let radius: CGFloat = 8

    /// 內容到系統選取那一塊的邊的距離
    static let inset = CGSize(width: 6, height: 3)
}

extension View {
    func sidebarHover(selected: Bool = false) -> some View { modifier(SidebarHoverModifier(selected: selected)) }

    /// 側欄 List 裡有選取的列（下面的會議、人物）：不畫系統的選取（青瓷綠的一整塊），選到的列改成灰底（ChatGPT）；
    /// 選取、多選、鍵盤照舊。選到時字照常的顏色（不然系統把字換成白的，淺色模式在灰底上看不見）
    func sidebarSelectableRow(selected: Bool) -> some View {
        environment(\.backgroundProminence, .standard)
            .sidebarHover(selected: selected)
            .background(NoNativeSelection())
    }
}

/// 段落標題右邊的小按鈕（「+」、顯示方式）：細線、跟字差不多高、灰色，滑過一塊淡淡的圓角底，跟列的「⋯」對齊（ChatGPT）
struct SidebarHeaderIcon: View {
    let systemName: String
    var active = false
    @State private var hovering = false

    var body: some View {
        Image(systemName: systemName)
            .font(.system(size: 15, weight: .light))
            .foregroundStyle(active ? Color.celadon : hovering ? Color.primary : Color.secondary)
            .frame(width: 26, height: 24)
            .background(RoundedRectangle(cornerRadius: 6, style: .continuous).fill(Color.primary.opacity(hovering ? 0.08 : 0)))
            .contentShape(Rectangle())
            .onHover { hovering = $0 }
    }

    /// 跟列右邊的「⋯」對齊：段落標題比列寬一點
    static let trailing: CGFloat = 6
}

/// 側欄的圖示、名字：自己排，不用系統側欄的排法（系統會用強調色畫圖示）；圖示跟字同色（ChatGPT）
struct SidebarLabelStyle: LabelStyle {
    func makeBody(configuration: Configuration) -> some View {
        HStack(spacing: 9) {
            configuration.icon
                .font(.system(size: 15))
                .frame(width: 22, alignment: .center)
            configuration.title
        }
    }
}

/// 找到側欄 List 背後的 NSTableView，關掉系統畫的選取（選取的狀態照舊，由列自己畫灰底）
private struct NoNativeSelection: NSViewRepresentable {
    func makeNSView(context: Context) -> Finder { Finder() }
    func updateNSView(_ view: Finder, context: Context) {}

    final class Finder: NSView {
        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            DispatchQueue.main.async { [weak self] in
                var view = self?.superview
                while let current = view, !(current is NSTableView) { view = current.superview }
                if let table = view as? NSTableView, table.selectionHighlightStyle != .none { table.selectionHighlightStyle = .none }
            }
        }
    }
}

/// 可以收合的段落標題：標題後面一個小箭頭（滑過標題或收起來時才出現，參考 ChatGPT 桌面版），標題和箭頭是同一顆按鈕
struct SidebarSectionHeader: View {
    @Environment(AppModel.self) private var model
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    let title: String
    let key: String
    /// 旁邊還有選單（會議列表的「顯示方式」）：段落標題在輔助使用裡會跟選單併成一個元件，按下去要照舊打開選單，
    /// 所以這時不用按鈕，收合改成有名字的動作（2026-11-16 用 AX 按的時候發現選單打不開）
    var besideMenu = false
    @State private var hovering = false

    var body: some View {
        let expanded = model.isSectionExpanded(key)
        let toggle = {
            withAnimation(reduceMotion ? nil : .easeInOut(duration: 0.18)) { model.setSection(key, expanded: !expanded) }
        }
        let label = HStack(spacing: 4) {
            // 跟列一樣大的灰字、不加粗（ChatGPT 的「專案」「最近項目」），不用 macOS 側欄預設的小粗體
            Text(title).lineLimit(1).font(.body).fontWeight(.regular).foregroundStyle(.secondary)
            Image(systemName: expanded ? "chevron.down" : "chevron.right")
                .font(.caption.weight(.medium))
                .foregroundStyle(.tertiary)
                .opacity(hovering || !expanded ? 1 : 0)
                .accessibilityHidden(true)
        }
        .contentShape(Rectangle())
        .onHover { hovering = $0 }
        let help = expanded ? String(localized: "收起這一段") : String(localized: "展開這一段")
        if besideMenu {
            label
                .sidebarClick(toggle)
                .help(help)
                .accessibilityAction(named: Text(help), toggle)
        } else {
            // 不用 Button：List 裡的 SwiftUI 按鈕會讓整個表格的點擊卡住（見 sidebarClick）
            label
                .sidebarClick(toggle)
                .help(help)
                .accessibilityElement(children: .combine)
                .accessibilityAddTraits([.isHeader, .isButton])
                .accessibilityAction(.default, toggle)
                .accessibilityValue(Text(expanded ? "已展開" : "已收起"))
        }
    }
}

/// 側欄的資料夾圖示（R101）：關著、打開兩種表示有沒有展開（參考 ChatGPT 桌面版的專案列表）；系統圖示沒有打開的資料夾，
/// 自己畫（`OpenFolderShape`），大小、粗細照系統的 `folder`。標記在右上角：齒輪是有自動加入條件
struct FolderGlyph: View {
    var open: Bool
    var badge: String? = nil

    var body: some View {
        // 看不見的系統資料夾圖示決定大小、基線，畫的東西放在它上面
        Image(systemName: "folder").hidden()
            .overlay {
                GeometryReader { g in
                    let badgeSize = g.size.height * 0.62
                    let center = CGPoint(x: g.size.width * 0.88, y: g.size.height * 0.2)
                    ZStack {
                        Group {
                            if open {
                                OpenFolderShape()
                                    .stroke(style: StrokeStyle(lineWidth: g.size.height * 0.085, lineCap: .round, lineJoin: .round))
                            } else {
                                Image(systemName: "folder").resizable()
                            }
                        }
                        // 標記的位置挖空，線才不會穿過標記。遮罩一定要給明確的顏色：側欄的預設前景色是半透明的，
                        // 拿來當遮罩整個圖示會不見（2026-11-16 在側欄上才看到）
                        .mask {
                            BadgeHole(center: center, radius: badge == nil ? 0 : badgeSize * 0.62)
                                .fill(Color.black, style: FillStyle(eoFill: true))
                        }
                        if let badge {
                            Image(systemName: badge)
                                .resizable().scaledToFit()
                                .fontWeight(.bold)
                                .frame(width: badgeSize, height: badgeSize)
                                .position(center)
                        }
                    }
                }
            }
            .accessibilityHidden(true)
    }
}

/// 整個框，挖掉標記的圓（even-odd）
private struct BadgeHole: Shape {
    var center: CGPoint
    var radius: CGFloat

    func path(in rect: CGRect) -> Path {
        var path = Path(rect)
        if radius > 0 { path.addEllipse(in: CGRect(x: center.x - radius, y: center.y - radius, width: radius * 2, height: radius * 2)) }
        return path
    }
}

/// 打開的資料夾：後面一片帶標籤的背板，前面一片斜的蓋子（座標是系統 `folder` 圖示的框裡的比例）
struct OpenFolderShape: Shape {
    func path(in r: CGRect) -> Path {
        func p(_ x: CGFloat, _ y: CGFloat) -> CGPoint { CGPoint(x: r.minX + x * r.width, y: r.minY + y * r.height) }
        var path = Path()
        path.move(to: p(0.10, 0.80))
        path.addLine(to: p(0.10, 0.22))
        path.addQuadCurve(to: p(0.17, 0.14), control: p(0.10, 0.14))
        path.addLine(to: p(0.36, 0.14))
        path.addQuadCurve(to: p(0.42, 0.17), control: p(0.40, 0.14))
        path.addLine(to: p(0.47, 0.24))
        path.addLine(to: p(0.76, 0.24))
        path.addQuadCurve(to: p(0.83, 0.31), control: p(0.83, 0.24))
        path.addLine(to: p(0.83, 0.42))
        path.move(to: p(0.10, 0.80))
        path.addQuadCurve(to: p(0.17, 0.86), control: p(0.11, 0.86))
        path.addLine(to: p(0.76, 0.86))
        path.addQuadCurve(to: p(0.83, 0.81), control: p(0.81, 0.86))
        path.addLine(to: p(0.95, 0.50))
        path.addQuadCurve(to: p(0.90, 0.42), control: p(0.97, 0.42))
        path.addLine(to: p(0.30, 0.42))
        path.addQuadCurve(to: p(0.24, 0.47), control: p(0.26, 0.42))
        path.addLine(to: p(0.10, 0.80))
        return path
    }
}

/// 資料夾在原地展開的最近 5 場；超過時最後一列「顯示全部 N 場」（下面的列表只看這個資料夾）；
/// 沒有會議時一列「沒有會議」
struct SidebarPeek: View {
    let rows: [MeetingRow]
    let showAll: () -> Void

    var body: some View {
        if rows.isEmpty {
            SidebarPeekLine { Text("沒有會議").foregroundStyle(.secondary) }
                .selectionDisabled()
        }
        // id 用字串：ForEach 會把 id 當成列的 tag，用會議 id 的話 List 會把這幾列當成選取（跟下面的列表一起亮成一整條）
        ForEach(rows.prefix(5).map { (key: "peek:\($0.id)", row: $0) }, id: \.key) { item in
            SidebarPeekRow(row: item.row)
        }
        if rows.count > 5 {
            SidebarPeekLine {
                Text("顯示全部 \(rows.count) 場").foregroundStyle(.secondary)
                    .accessibilityAddTraits(.isButton)
                    .accessibilityAction(.default, showAll)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .contentShape(Rectangle())
            .sidebarClick(showAll)
            .sidebarHover()
            .selectionDisabled()
        }
    }
}

/// 展開的一場：點了打開（下面的列表不換）；選到時青瓷綠、粗體，同資料夾列（不用 List 的選取，下面的列表也有這場）
private struct SidebarPeekRow: View {
    @Environment(AppModel.self) private var model
    let row: MeetingRow

    var body: some View {
        let selected = model.sidebarMode == .meetings && !model.showTrash && model.selection == row.id
        let open = { model.openFromSidebar(row.id) }
        SidebarPeekLine {
            Text(row.meeting.displayTitle).lineLimit(1)
                .accessibilityAddTraits(selected ? [.isButton, .isSelected] : .isButton)
                .accessibilityAction(.default, open)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .contentShape(Rectangle())
        // 拖到別的資料夾、其他會議的標題、垃圾桶（R103.1）：不在 List 的選取裡，蓋拖曳把手；沒拖就是點一下
        .sidebarDragHandle(type: .shiyinMeetingIDs, label: row.meeting.displayTitle,
                           data: { Data(model.dragIDs(for: row.id).map(String.init).joined(separator: ",").utf8) }, onClick: open)
        .floatingRowMenu(selected: selected) { model.meetingMenu([row.id]) }
        // 選到時一塊灰底（同 ChatGPT），不用 List 的選取（下面的列表也有這場，兩邊都亮會像多選）
        .sidebarHover(selected: selected)
        .help("\(row.meeting.meetingDate)・\(row.meeting.displayTitle)")
        .selectionDisabled()
    }
}

/// 縮排到跟資料夾名稱對齊：看不見的資料夾圖示
private struct SidebarPeekLine<Content: View>: View {
    @ViewBuilder var content: Content

    var body: some View {
        Label { content } icon: { Image(systemName: "folder").hidden() }
            .labelStyle(SidebarLabelStyle())
    }
}
