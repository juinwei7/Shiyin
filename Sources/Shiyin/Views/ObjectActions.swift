import SwiftUI

// 每一種東西都能直接改、刪（R100、design §74）：畫面上的「⋯」、右鍵、「更多」用同一份選單內容

/// 選單裡會刪掉、移除東西的項目：紅色的字和圖示（R101，參考 ChatGPT 桌面版）。macOS 的選單不會照 role 變色，
/// 要自己上色；title 已經查過字串表（呼叫的地方用 `String(localized:)`）
struct DestructiveMenuButton: View {
    let title: String
    var systemImage = "trash"
    let action: () -> Void

    init(_ title: String, systemImage: String = "trash", action: @escaping () -> Void) {
        self.title = title
        self.systemImage = systemImage
        self.action = action
    }

    var body: some View {
        Button(role: .destructive, action: action) {
            Label { Text(verbatim: title).foregroundStyle(.red) } icon: { Image(systemName: systemImage).foregroundStyle(.red) }
        }
        // 選單的圖示用主色畫（字用前景色）
        .tint(.red)
    }
}

extension View {
    /// 選單項目顯示圖示（macOS 的選單預設只顯示文字；R101，參考 ChatGPT 桌面版的選單）；圖示跟字同色，不用 app 的主色
    func menuIcons() -> some View { labelStyle(.titleAndIcon).tint(.primary) }
}

/// 定期會議：系列頁工具列的「更多」（系統選單；側欄的「⋯」、右鍵是 AppModel.seriesMenu）
struct SeriesMenuItems: View {
    @Environment(AppModel.self) private var model
    let series: MeetingSeries

    var body: some View {
        Group {
            Button("重新命名…", systemImage: "pencil") { if let id = series.id { model.renameTarget = .series(id) } }
            Divider()
            DestructiveMenuButton(String(localized: "刪除定期會議…")) { model.confirmDeleteSeries = series.id }
        }
        .menuIcons()
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
                // 整行（含行尾空白）都可以右鍵、雙擊：先撐滿整行再設可點的範圍（反過來的話只有字的範圍收得到；
                // 2026-11-16 自己驗收時發現）。文字不當成可選取（selectableUnlessEditable），右鍵才是這一份
                .frame(maxWidth: .infinity, alignment: .leading)
                .contentShape(Rectangle())
                .onTapGesture(count: 2) { if model.canEditNotes { model.editingNoteItem = ref } }
                .floatingRowMenu { model.noteItemMenu(ref, text: text, time: time) }
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
