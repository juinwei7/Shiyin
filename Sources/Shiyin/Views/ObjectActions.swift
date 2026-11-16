import SwiftUI

// 每一種東西都能直接改、刪（R100、design §74）：畫面上的「⋯」、右鍵、「更多」用同一份選單內容

/// 列右邊的「⋯」：滑鼠在列上或那一列選到時才顯示，點了跟右鍵同一份選單
struct RowMenuButton<Items: View>: View {
    @ViewBuilder var items: () -> Items

    var body: some View {
        Menu { items() } label: {
            Image(systemName: "ellipsis.circle")
        }
        .menuStyle(.borderlessButton)
        .menuIndicator(.hidden)
        .fixedSize()
        .tint(.secondary)
        .help("更多動作")
        .accessibilityLabel(Text("更多動作"))
    }
}

/// 給列加上「⋯」：右邊留出位置（列的寬度不跳），滑過或選到時顯示
private struct RowMenuModifier<Items: View>: ViewModifier {
    var selected: Bool
    @ViewBuilder var items: () -> Items
    @State private var hovering = false

    func body(content: Content) -> some View {
        content
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.trailing, 22)
            .overlay(alignment: .trailing) {
                RowMenuButton(items: items)
                    .opacity(hovering || selected ? 1 : 0)
                    .allowsHitTesting(hovering || selected)
            }
            .onHover { hovering = $0 }
    }
}

extension View {
    func rowMenu<Items: View>(selected: Bool = false, @ViewBuilder _ items: @escaping () -> Items) -> some View {
        modifier(RowMenuModifier(selected: selected, items: items))
    }
}

/// 側欄的會議（一場或多選的好幾場）：右鍵、「⋯」
struct MeetingMenuItems: View {
    @Environment(AppModel.self) private var model
    let ids: [Int64]

    var body: some View {
        if model.showTrash {
            Button("還原") { model.restore(ids) }
            Button("永久刪除…", role: .destructive) { model.confirmPurge = ids }
        } else if !ids.isEmpty {
            if ids.count == 1, let id = ids.first {
                Button("重新命名…") { model.requestRenameMeeting(id) }
                Divider()
            }
            FolderMenu(meetingIds: ids, current: model.commonFolder(ids))
            SeriesMenu(meetingIds: ids)
            Divider()
            if ids.count == 1 {
                Button("移到垃圾桶", role: .destructive) { model.moveToTrash(ids) }
            } else {
                Button("把 \(ids.count) 場會議移到垃圾桶…", role: .destructive) {
                    model.deleteTargets = ids
                    model.confirmDelete = true
                }
            }
        }
    }
}

/// 資料夾：右鍵、「⋯」
struct FolderMenuItems: View {
    @Environment(AppModel.self) private var model
    let folder: String

    var body: some View {
        Button(model.folderRule(folder) == nil ? "自動加入的條件…" : "編輯自動加入的條件…") { model.editingFolderRule = folder }
        Button("重新命名…") { model.folderPrompt = .rename(folder) }
        Divider()
        Button("刪除資料夾…", role: .destructive) { model.confirmDeleteFolder = folder }
    }
}

/// 定期會議：側欄的右鍵、「⋯」、系列頁的「更多」
struct SeriesMenuItems: View {
    @Environment(AppModel.self) private var model
    let series: MeetingSeries

    var body: some View {
        Button("重新命名…") { model.renamingSeries = series.id }
        Divider()
        Button("刪除定期會議…", role: .destructive) { model.confirmDeleteSeries = series.id }
    }
}

/// 人物頁、系列頁裡的一場會議：打開；在定期會議裡的可以移出
struct MeetingLinkMenuItems: View {
    @Environment(AppModel.self) private var model
    let meeting: Meeting
    var series: MeetingSeries? = nil

    var body: some View {
        Button("打開這場會議") {
            if series != nil { model.selection = meeting.id } else { model.openFromPersonPage(meeting.id ?? 0) }
        }
        if let series, let id = meeting.id {
            Divider()
            Button("移出「\(series.name)」") { model.setSeries([id], series: nil) }
        }
    }
}

/// 刪除資料夾、定期會議的確認（R100）：側欄的右鍵、「⋯」、Delete 都走這裡
struct OrganizeConfirmations: ViewModifier {
    @Environment(AppModel.self) private var model

    func body(content: Content) -> some View {
        let folder = model.confirmDeleteFolder ?? ""
        let count = model.meetings.filter { $0.meeting.folder == folder }.count
        let series = model.seriesById(model.confirmDeleteSeries)
        content
            .confirmationDialog(String(localized: "刪除資料夾「\(folder)」？"),
                                isPresented: Binding(get: { model.confirmDeleteFolder != nil }, set: { if !$0 { model.confirmDeleteFolder = nil } })) {
                Button("刪除資料夾", role: .destructive) {
                    if let name = model.confirmDeleteFolder { model.deleteFolder(name) }
                }
            } message: {
                Text(count > 0 ? String(localized: "裡面的 \(count) 場會議會留著，移到「不在資料夾」。自動加入的條件也會刪掉。")
                               : String(localized: "自動加入的條件也會刪掉。"))
            }
            .confirmationDialog(String(localized: "刪除定期會議「\(series?.name ?? "")」？"),
                                isPresented: Binding(get: { model.confirmDeleteSeries != nil }, set: { if !$0 { model.confirmDeleteSeries = nil } })) {
                Button("刪除定期會議", role: .destructive) {
                    if let id = model.confirmDeleteSeries { model.deleteSeries(id) }
                }
            } message: {
                Text("會議會保留，之後的會議不會再自動加進來，也不會再建議這一組。")
            }
    }
}

/// 重新命名一個重點標記（R100）：標記選單的「重新命名…」
struct BookmarkRenameModifier: ViewModifier {
    @Environment(AppModel.self) private var model
    @State private var label = ""

    func body(content: Content) -> some View {
        content
            .alert(String(localized: "重新命名標記"), isPresented: Binding(get: { model.renamingBookmark != nil },
                                                                     set: { if !$0 { model.renamingBookmark = nil } })) {
                TextField("名稱", text: $label)
                Button("取消", role: .cancel) { model.renamingBookmark = nil }
                Button("重新命名") {
                    if let id = model.renamingBookmark?.id { model.renameBookmark(id, label: label) }
                    model.renamingBookmark = nil
                }
            } message: {
                Text("\(TimeFormat.string(model.renamingBookmark?.time ?? 0)) 的標記")
            }
            .onChange(of: model.renamingBookmark?.id) { _, _ in label = model.renamingBookmark?.label ?? "" }
    }
}

// MARK: - 紀錄的條目（R100、design §74）

/// 一條紀錄的動作：雙擊就地改、右鍵、滑過時行尾的「⋯」。ref 是 nil（不是這場會議的紀錄）時什麼都不加
private struct NoteItemActions: ViewModifier {
    @Environment(AppModel.self) private var model
    let ref: NoteItemRef?
    let text: String
    let time: String

    func body(content: Content) -> some View {
        if let ref {
            content
                // 整行（含行尾空白）都可以右鍵、雙擊；文字不當成可選取（selectableUnlessEditable），右鍵才是這一份
                .contentShape(Rectangle())
                .onTapGesture(count: 2) { if model.canEditNotes { model.editingNoteItem = ref } }
                .contextMenu { NoteItemMenuItems(ref: ref, text: text, time: time) }
                .rowMenu { NoteItemMenuItems(ref: ref, text: text, time: time) }
        } else {
            content
        }
    }
}

extension View {
    func noteItemActions(ref: NoteItemRef?, text: String, time: String) -> some View {
        modifier(NoteItemActions(ref: ref, text: text, time: time))
    }
}

struct NoteItemMenuItems: View {
    @Environment(AppModel.self) private var model
    let ref: NoteItemRef
    let text: String
    let time: String

    var body: some View {
        Button("拷貝") {
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(text, forType: .string)
        }
        if let seconds = TimeFormat.seconds(time), model.player.isLoaded {
            Button("從這裡播放") { model.player.seek(to: seconds) }
        }
        Divider()
        Button("編輯這一條") { model.editingNoteItem = ref }
            .disabled(!model.canEditNotes)
        Button("刪除這一條", role: .destructive) { model.deleteNoteItem(ref) }
            .disabled(!model.canEditNotes)
    }
}

/// 就地改一條紀錄：Return 儲存、Esc 取消；待辦只改事項
struct NoteItemEditor: View {
    @Environment(AppModel.self) private var model
    let ref: NoteItemRef
    @State private var text = ""
    @FocusState private var focused: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            TextField("", text: $text, axis: .vertical)
                .textFieldStyle(.roundedBorder)
                .focused($focused)
                .onSubmit { model.editNoteItem(ref, text: text) }
                .onExitCommand { model.editingNoteItem = nil }
            HStack(spacing: 8) {
                Text("Return 儲存、Esc 取消").font(.caption).foregroundStyle(.secondary)
                Spacer()
                Button("取消") { model.editingNoteItem = nil }
                Button("儲存") { model.editNoteItem(ref, text: text) }
                    .buttonStyle(.borderedProminent)
                    .disabled(text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
            .font(.callout)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .onAppear {
            text = model.detail?.notes?.text(at: ref) ?? ""
            focused = true
        }
    }
}

extension View {
    /// 能直接改的紀錄（R100）不開文字選取：開了的話右鍵是系統的文字選單、雙擊只選到一個詞。
    /// 要拷貝用右鍵的「拷貝」，要改一部分雙擊進去改
    @ViewBuilder
    func selectableUnlessEditable(_ editable: Bool) -> some View {
        if editable { self } else { textSelection(.enabled) }
    }
}
