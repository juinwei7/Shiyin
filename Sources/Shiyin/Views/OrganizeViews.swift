import SwiftUI

// MARK: - 資料夾自動歸類（R98、design §72）

/// 資料夾右鍵「自動加入的條件…」
struct FolderRuleSheet: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    let folderId: Int64
    @State private var keywords = ""
    @State private var people: Set<Int64> = []
    @State private var templateId: String?
    @State private var applyNow = false
    @State private var existing = false

    private var rule: FolderRule {
        FolderRule(folderId: folderId, keywords: GlossaryInput.split(keywords), people: people.sorted(), templateId: templateId)
    }

    var body: some View {
        let matching = (try? model.db.meetingsMatching(rule).count) ?? 0
        VStack(alignment: .leading, spacing: 16) {
            // 同其他彈窗的樣子（R106、design §80）
            SheetHeader(title: String(localized: "「\(model.folderName(folderId))」自動加入的條件"),
                        subtitle: String(localized: "符合任一個條件的新會議，會自動放進這個資料夾。已經在別的資料夾的會議不會移動。"))
            VStack(alignment: .leading, spacing: 6) {
                SheetFieldLabel(String(localized: "標題包含"))
                SheetTextField(placeholder: String(localized: "例如：週會、sync（任一個詞）"), text: $keywords)
            }
            if !model.peopleOverview.isEmpty {
                VStack(alignment: .leading, spacing: 6) {
                    SheetFieldLabel(String(localized: "這些人都參加"))
                    ScrollView {
                        VStack(alignment: .leading, spacing: 6) {
                            ForEach(model.peopleOverview) { person in
                                Toggle(person.isMe ? String(localized: "\(person.name)（我）") : person.name, isOn: Binding(
                                    get: { people.contains(person.id) },
                                    set: { if $0 { people.insert(person.id) } else { people.remove(person.id) } }))
                                .toggleStyle(.checkbox)
                            }
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                    }
                    .frame(maxHeight: 140)
                    .fixedSize(horizontal: false, vertical: true)
                    .sheetFieldBox()
                }
            }
            VStack(alignment: .leading, spacing: 6) {
                SheetFieldLabel(String(localized: "用這個範本"))
                SheetMenuField(options: [(String?.none, String(localized: "不限"))] + model.templates.map { (Optional($0.id), $0.displayName) },
                               selection: $templateId)
            }
            Toggle(String(localized: "現在不在資料夾裡的會議也套用（\(matching) 場）"), isOn: $applyNow)
                .toggleStyle(.checkbox)
                .disabled(matching == 0 || rule.isEmpty)
            HStack(spacing: 10) {
                if existing {
                    Button("移除條件") {
                        model.saveFolderRule(FolderRule(folderId: folderId), applyNow: false)
                        dismiss()
                    }
                    .buttonStyle(SheetPillButtonStyle(.secondary))
                    .foregroundStyle(.red)
                }
                Spacer()
                Button("取消") { dismiss() }
                    .buttonStyle(SheetPillButtonStyle(.secondary))
                    .keyboardShortcut(.cancelAction)
                Button("儲存") {
                    model.saveFolderRule(rule, applyNow: applyNow && matching > 0)
                    dismiss()
                }
                .buttonStyle(SheetPillButtonStyle(.primary))
                .keyboardShortcut(.defaultAction)
            }
        }
        .sheetChrome(width: 480)
        .onAppear {
            guard let rule = model.folderRule(folderId) else { return }
            existing = true
            keywords = rule.keywords.joined(separator: MeetingLanguage.ui(zh: "、", en: ", "))
            people = Set(rule.people)
            templateId = rule.templateId
        }
    }
}

/// 紀錄分頁頂端：這場很像某個資料夾裡的會議（R98.3）
struct FolderSuggestionBanner: View {
    @Environment(AppModel.self) private var model
    let meetingId: Int64
    let suggestion: FolderSuggestion

    var body: some View {
        HStack(spacing: 10) {
            Label(model.folderSuggestionText(suggestion), systemImage: "folder.badge.plus")
                .lineLimit(2)
            Spacer(minLength: 8)
            Button("不用") { model.dismissFolderSuggestion(meetingId) }
            Button("放進去") { model.acceptFolderSuggestion(meetingId, folderId: suggestion.folderId) }
                .buttonStyle(.borderedProminent)
        }
        .font(.callout)
        .padding(.horizontal, 14)
        .padding(.vertical, 8)
        .background(Color.celadon.opacity(0.08), in: RoundedRectangle(cornerRadius: 8))
    }
}
