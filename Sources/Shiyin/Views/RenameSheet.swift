import SwiftUI

/// 重新命名的彈窗（R102、design §76，參考 ChatGPT 的「重新命名對話」）：標題、一行說明、帶目前名字的輸入框、取消／儲存。
/// 會議、資料夾、新資料夾、定期會議、人物、標記都用這一個
struct RenameSheet: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    let target: RenameTarget
    @State private var name = ""
    @State private var original = ""
    @FocusState private var focused: Bool

    var body: some View {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        // 「+」建空的資料夾時，同名的已經有就不能建（R104）
        let canSave = !trimmed.isEmpty && (isNewFolder || trimmed != original) && !(isEmptyNewFolder && model.renameConflict(for: target, name: trimmed) != nil)
        // 樣子照 ChatGPT 的「重新命名對話」：大標題、右上角 ✕、細框的輸入框、白底黑字的主要按鈕（深色模式；淺色反過來）
        VStack(alignment: .leading, spacing: 18) {
            HStack(alignment: .top) {
                VStack(alignment: .leading, spacing: 6) {
                    Text(title).font(.title2).fontWeight(.bold)
                    Text(hint(trimmed)).font(.body).foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                Spacer(minLength: 12)
                Button { dismiss() } label: { Image(systemName: "xmark").font(.system(size: 14, weight: .medium)) }
                    .buttonStyle(.plain)
                    .foregroundStyle(.secondary)
                    .help("取消")
                    .accessibilityLabel(Text("取消"))
            }
            TextField(placeholder, text: $name)
                .textFieldStyle(.plain)
                .font(.system(size: 15))
                .focused($focused)
                .focusEffectDisabled()
                .padding(.horizontal, 12)
                .padding(.vertical, 10)
                .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(Color.primary.opacity(0.04)))
                .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous)
                    .strokeBorder(Color.primary.opacity(focused ? 0.75 : 0.25), lineWidth: focused ? 1.5 : 1))
                .onSubmit { if canSave { save() } }
            HStack(spacing: 10) {
                Spacer()
                Button("取消") { dismiss() }
                    .buttonStyle(SheetPillButtonStyle(prominent: false))
                    .keyboardShortcut(.cancelAction)
                Button(isEmptyNewFolder ? String(localized: "建立") : isNewFolder ? String(localized: "建立並移入") : String(localized: "儲存")) { save() }
                    .buttonStyle(SheetPillButtonStyle(prominent: true))
                    .keyboardShortcut(.defaultAction)
                    .disabled(!canSave)
            }
        }
        .padding(24)
        .frame(width: 440)
        .onAppear {
            original = model.currentName(for: target)
            name = original
            focused = true
        }
    }

    private var isNewFolder: Bool { if case .newFolder = target { true } else { false } }
    private var isEmptyNewFolder: Bool { if case .newFolder(let ids) = target { ids.isEmpty } else { false } }

    private func save() {
        model.commitRename(target, to: name)
        dismiss()
    }

    private var title: String {
        switch target {
        case .meeting: String(localized: "重新命名會議")
        case .folder: String(localized: "重新命名資料夾")
        case .newFolder: String(localized: "新資料夾")
        case .series: String(localized: "重新命名定期會議")
        case .person: String(localized: "重新命名這個人")
        case .bookmark: String(localized: "重新命名標記")
        }
    }

    private var placeholder: String {
        switch target {
        case .folder, .newFolder: String(localized: "資料夾名稱")
        case .person: String(localized: "名字")
        default: String(localized: "名稱")
        }
    }

    /// 說明；改成會合併的名字時換成提醒
    private func hint(_ name: String) -> String {
        if let other = model.renameConflict(for: target, name: name) {
            switch target {
            case .newFolder(let ids) where ids.isEmpty: return String(localized: "已經有「\(other)」。")
            case .newFolder: return String(localized: "已經有「\(other)」，會議會移進那個資料夾。")
            default: return String(localized: "已經有「\(other)」，儲存後會合併成一個。")
            }
        }
        switch target {
        case .meeting: return String(localized: "保持簡短、容易辨識。日期和與會者在會議頁的「編輯」改。")
        case .folder: return String(localized: "資料夾裡的會議都會跟著改。")
        case .newFolder(let ids) where ids.isEmpty: return String(localized: "例如專案名稱或客戶名稱。")
        case .newFolder(let ids):
            return ids.count > 1 ? String(localized: "例如專案名稱或客戶名稱。這 \(ids.count) 場會議會移進新資料夾。")
                                 : String(localized: "例如專案名稱或客戶名稱。這場會議會移進新資料夾。")
        case .series: return String(localized: "保持簡短、容易辨識。")
        case .person: return String(localized: "所有會議裡的這個人都會跟著改。")
        case .bookmark(let id):
            let time = model.detail?.bookmarks.first { $0.id == id }?.time ?? 0
            return String(localized: "\(TimeFormat.string(time)) 的標記")
        }
    }
}

/// 彈窗的按鈕（ChatGPT 的樣子）：圓角、主要按鈕是字的顏色當底（深色模式白底黑字），次要按鈕淡淡的底加細框
struct SheetPillButtonStyle: ButtonStyle {
    var prominent: Bool
    @Environment(\.isEnabled) private var isEnabled

    func makeBody(configuration: Configuration) -> some View {
        let shape = RoundedRectangle(cornerRadius: 10, style: .continuous)
        configuration.label
            .font(.body.weight(.medium))
            .padding(.horizontal, 18)
            .frame(minWidth: 68, minHeight: 34)
            .foregroundStyle(prominent ? Color(nsColor: .windowBackgroundColor) : Color.primary)
            .background(shape.fill(prominent ? Color.primary : Color.primary.opacity(0.06)))
            .overlay(shape.strokeBorder(prominent ? Color.clear : Color.primary.opacity(0.18)))
            .contentShape(shape)
            .opacity(isEnabled ? (configuration.isPressed ? 0.75 : 1) : 0.35)
    }
}
