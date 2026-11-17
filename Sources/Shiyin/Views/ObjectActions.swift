import SwiftUI

// 每一種東西都能直接改、刪（R100、design §74）：畫面上的「⋯」、右鍵、「更多」用同一份選單內容

/// 選單裡會刪掉、移除東西的項目：紅色的字和圖示（R101）。macOS 的選單不會照 role 變色，
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
    /// 選單項目顯示圖示（macOS 的選單預設只顯示文字；R101）；圖示跟字同色，不用 app 的主色
    func menuIcons() -> some View { labelStyle(.titleAndIcon).tint(.primary) }
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
                .textFieldStyle(.sheet)
                .focused($focused)
                .onSubmit { model.editNoteItem(ref, text: text) }
                .onExitCommand { model.editingNoteItem = nil }
            HStack(spacing: 8) {
                Text("Return 儲存、Esc 取消").font(.caption).foregroundStyle(.secondary)
                Spacer()
                Button("取消") { model.editingNoteItem = nil }
                    .buttonStyle(SheetPillButtonStyle(.secondary, compact: true))
                Button("儲存") { model.editNoteItem(ref, text: text) }
                    .buttonStyle(SheetPillButtonStyle(.primary, compact: true))
                    .disabled(text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
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
