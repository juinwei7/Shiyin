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
        let canSave = !trimmed.isEmpty && (isNewFolder || trimmed != original)
        VStack(alignment: .leading, spacing: 16) {
            VStack(alignment: .leading, spacing: 4) {
                Text(title).font(.title3).fontWeight(.semibold)
                Text(hint(trimmed)).font(.callout).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            TextField(placeholder, text: $name)
                .textFieldStyle(.roundedBorder)
                .focused($focused)
                .onSubmit { if canSave { save() } }
            HStack {
                Spacer()
                Button("取消") { dismiss() }.keyboardShortcut(.cancelAction)
                Button(isNewFolder ? String(localized: "建立並移入") : String(localized: "儲存")) { save() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(!canSave)
            }
        }
        .padding(20)
        .frame(width: 420)
        .onAppear {
            original = model.currentName(for: target)
            name = original
            focused = true
        }
    }

    private var isNewFolder: Bool { if case .newFolder = target { true } else { false } }

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
            case .newFolder: return String(localized: "已經有「\(other)」，會議會移進那個資料夾。")
            default: return String(localized: "已經有「\(other)」，儲存後會合併成一個。")
            }
        }
        switch target {
        case .meeting: return String(localized: "保持簡短、容易辨識。日期和與會者在會議頁的「編輯」改。")
        case .folder: return String(localized: "資料夾裡的會議都會跟著改。")
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
