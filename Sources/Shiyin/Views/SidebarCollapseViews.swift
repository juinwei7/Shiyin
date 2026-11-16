import SwiftUI

// MARK: - 側欄的收合（R101、design §75）

/// 箭頭的寬度：資料夾、定期會議列最前面的箭頭，沒有箭頭的列（所有會議、人物、展開的會議）留同樣的空位對齊
enum SidebarMetrics {
    static let chevron: CGFloat = 12
}

/// 可以收合的段落標題：標題後面一個小箭頭，標題和箭頭是同一顆按鈕
struct SidebarSectionHeader: View {
    @Environment(AppModel.self) private var model
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    let title: String
    let key: String
    /// 旁邊還有選單（會議列表的「顯示方式」）：段落標題在輔助使用裡會跟選單併成一個元件，按下去要照舊打開選單，
    /// 所以這時不用按鈕，收合改成有名字的動作（2026-11-16 用 AX 按的時候發現選單打不開）
    var besideMenu = false

    var body: some View {
        let expanded = model.isSectionExpanded(key)
        let toggle = {
            withAnimation(reduceMotion ? nil : .easeInOut(duration: 0.18)) { model.setSection(key, expanded: !expanded) }
        }
        let label = HStack(spacing: 4) {
            Text(title).lineLimit(1)
            Image(systemName: expanded ? "chevron.down" : "chevron.right")
                .font(.caption2.weight(.semibold))
                .foregroundStyle(.tertiary)
                .accessibilityHidden(true)
        }
        .contentShape(Rectangle())
        let help = expanded ? String(localized: "收起這一段") : String(localized: "展開這一段")
        if besideMenu {
            label
                .onTapGesture(perform: toggle)
                .help(help)
                .accessibilityAction(named: Text(help), toggle)
        } else {
            Button(action: toggle) { label }
                .buttonStyle(.plain)
                .help(help)
                .accessibilityAddTraits(.isHeader)
                .accessibilityValue(Text(expanded ? "已展開" : "已收起"))
        }
    }
}

/// 資料夾、定期會議列最前面的箭頭：點了在原地展開或收起（不觸發整列的「只看這個資料夾」）
struct SidebarDisclosure {
    var key: String
    /// 沒有會議時不顯示箭頭（位置留著）
    var enabled: Bool
}

struct SidebarChevron: View {
    @Environment(AppModel.self) private var model
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    let disclosure: SidebarDisclosure?
    let name: String

    var body: some View {
        if let disclosure, disclosure.enabled {
            let expanded = model.isRowExpanded(disclosure.key)
            let toggle = {
                withAnimation(reduceMotion ? nil : .easeInOut(duration: 0.18)) { model.setRow(disclosure.key, expanded: !expanded) }
            }
            Image(systemName: expanded ? "chevron.down" : "chevron.right")
                .font(.caption2.weight(.semibold))
                .foregroundStyle(.secondary)
                .frame(width: SidebarMetrics.chevron, height: 16)
                .contentShape(Rectangle())
                .onTapGesture(perform: toggle)
                .help(expanded ? String(localized: "收起最近的會議") : String(localized: "展開最近的會議"))
                .accessibilityElement()
                .accessibilityLabel(Text(expanded ? "收起「\(name)」" : "展開「\(name)」"))
                .accessibilityAddTraits(.isButton)
                .accessibilityAction(.default, toggle)
        } else {
            Color.clear.frame(width: SidebarMetrics.chevron, height: 1)
        }
    }
}

/// 資料夾、定期會議在原地展開的最近 5 場；超過時最後一列「顯示全部 N 場」（等於點資料夾名稱）
struct SidebarPeek: View {
    let rows: [MeetingRow]
    let showAll: () -> Void

    var body: some View {
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
            .onTapGesture(perform: showAll)
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
                .foregroundStyle(selected ? Color.celadon : .primary)
                .fontWeight(selected ? .semibold : .regular)
                .accessibilityAddTraits(selected ? [.isButton, .isSelected] : .isButton)
                .accessibilityAction(.default, open)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .contentShape(Rectangle())
        .onTapGesture(perform: open)
        .contextMenu { MeetingMenuItems(ids: [row.id]) }
        .rowMenu(selected: selected) { MeetingMenuItems(ids: [row.id]) }
        .help("\(row.meeting.meetingDate)・\(row.meeting.displayTitle)")
        .selectionDisabled()
    }
}

/// 縮排到跟資料夾名稱對齊：箭頭的空位＋看不見的資料夾圖示
private struct SidebarPeekLine<Content: View>: View {
    @ViewBuilder var content: Content

    var body: some View {
        HStack(spacing: 2) {
            Color.clear.frame(width: SidebarMetrics.chevron, height: 1)
            Label { content } icon: { Image(systemName: "folder").hidden() }
        }
    }
}
