import SwiftUI

/// 閱讀用的欄位：靠左、寬度上限約 680 pt
struct ReadingColumn<Content: View>: View {
    @Environment(\.snapshotMode) private var snapshotMode
    @ViewBuilder var content: Content

    var body: some View {
        if snapshotMode {
            column
        } else {
            ScrollView { column }
        }
    }

    private var column: some View {
        VStack(alignment: .leading, spacing: 26) {
            content
        }
        .frame(maxWidth: 680, alignment: .leading)
        .padding(.horizontal, 32)
        .padding(.vertical, 24)
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

struct NotesSection<Content: View>: View {
    let title: String
    @ViewBuilder var content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(title).font(.songti(18))
            content
        }
    }
}

/// 一條紀錄：內容加上時間點（R6.4：點時間點就跳到錄音那裡）
struct ItemList: View {
    let items: [MeetingNotes.Item]

    var body: some View {
        VStack(alignment: .leading, spacing: 7) {
            ForEach(Array(items.enumerated()), id: \.offset) { _, item in
                HStack(alignment: .firstTextBaseline, spacing: 8) {
                    Text("•").foregroundStyle(.secondary)
                    (Text(item.text))
                        .textSelection(.enabled)
                        .fixedSize(horizontal: false, vertical: true)
                    if !item.time.isEmpty { TimeButton(time: item.time).font(.caption.monospacedDigit()) }
                }
            }
        }
        .lineSpacing(3)
    }
}

/// 沒有會議紀錄時的說明與動作
struct NotesPlaceholder: View {
    @Environment(AppModel.self) private var model
    let detail: MeetingDetail

    var body: some View {
        let status = model.detailStatus
        VStack(alignment: .leading, spacing: 12) {
            if status == .recording {
                Text("錄音結束、轉錄完成後，會議紀錄會出現在這裡。").foregroundStyle(.secondary)
            } else if status?.isActive == true {
                Text("轉錄和整理完成後，會議紀錄會出現在這裡。").foregroundStyle(.secondary)
            } else if detail.segments.isEmpty && detail.meeting.userNotes.isEmpty {
                Text("還沒有逐字稿。").foregroundStyle(.secondary)
            } else {
                Text(AppSettings.provider == .none
                     ? "整理工具目前設定為「不整理」。要產生會議紀錄，請到設定選擇 Claude、Codex 或 API。"
                     : "這場會議還沒整理成會議紀錄。")
                    .foregroundStyle(.secondary)
                Button("整理成會議紀錄") { model.resummarize() }
                    .disabled(AppSettings.provider == .none)
            }
        }
        .padding(32)
    }
}

/// 摘要分頁：摘要、決議、待辦、未決問題、自訂段落（R6.3）。可以切換成編輯模式（R6.6）。
struct SummaryTab: View {
    @Environment(AppModel.self) private var model
    let detail: MeetingDetail
    @State private var editing: MeetingNotes?

    var body: some View {
        if let draft = Binding($editing) {
            NotesEditor(notes: draft, showTopics: false) { save in
                if save, let editing { model.saveEditedNotes(editing) }
                editing = nil
            }
        } else if let notes = detail.notes, !notes.isEmpty {
            ReadingColumn {
                EditBar { editing = notes }
                if !notes.summary.isEmpty { NotesSection(title: String(localized: "摘要")) { ItemList(items: notes.summary) } }
                if !notes.decisions.isEmpty { NotesSection(title: String(localized: "決議")) { ItemList(items: notes.decisions) } }
                if !notes.actionItems.isEmpty { NotesSection(title: String(localized: "待辦")) { ActionItemsTable(items: notes.actionItems) } }
                if !notes.openQuestions.isEmpty { NotesSection(title: String(localized: "未決問題")) { ItemList(items: notes.openQuestions) } }
                ForEach(Array(notes.custom.enumerated()), id: \.offset) { _, section in
                    if !section.items.isEmpty { NotesSection(title: section.title) { ItemList(items: section.items) } }
                }
                if let by = detail.meeting.notesBy {
                    Text("由 \(LLMProvider(rawValue: by)?.shortLabel ?? by) 整理．\(templateName)")
                        .font(.caption)
                        .foregroundStyle(.tertiary)
                }
            }
        } else {
            NotesPlaceholder(detail: detail)
        }
    }

    private var templateName: String {
        String(localized: "範本：") + (model.templates.first { $0.id == detail.meeting.templateId }?.displayName ?? String(localized: "一般會議"))
    }
}

/// 右上角的「編輯」按鈕
struct EditBar: View {
    let onEdit: () -> Void

    var body: some View {
        HStack {
            Spacer()
            Button("編輯", systemImage: "pencil", action: onEdit)
                .buttonStyle(.borderless)
                .foregroundStyle(Color.celadon)
                .help("直接修改會議紀錄，存檔時會留一個版本")
        }
        .padding(.bottom, -18)
    }
}

struct ActionItemsTable: View {
    let items: [MeetingNotes.ActionItem]

    var body: some View {
        Grid(alignment: .leadingFirstTextBaseline, horizontalSpacing: 18, verticalSpacing: 9) {
            GridRow {
                Text("負責人")
                Text("事項")
                Text("期限")
            }
            .font(.caption)
            .foregroundStyle(.secondary)
            Divider().gridCellUnsizedAxes(.horizontal)
            ForEach(Array(items.enumerated()), id: \.offset) { _, item in
                GridRow {
                    Text(item.owner.isEmpty ? String(localized: "未指定") : item.owner)
                        .foregroundStyle(item.owner.isEmpty ? Color.signal : Color.primary)
                    HStack(alignment: .firstTextBaseline, spacing: 8) {
                        Text(item.task)
                            .textSelection(.enabled)
                            .fixedSize(horizontal: false, vertical: true)
                        if !item.time.isEmpty { TimeButton(time: item.time).font(.caption.monospacedDigit()) }
                    }
                    .gridColumnAlignment(.leading)
                    Text(item.due.isEmpty ? "—" : item.due)
                        .monospacedDigit()
                        .foregroundStyle(item.due.isEmpty ? .secondary : .primary)
                }
            }
        }
    }
}

/// 詳細分頁：依議題分段（R6.3）
struct TopicsTab: View {
    @Environment(AppModel.self) private var model
    let detail: MeetingDetail
    @State private var editing: MeetingNotes?

    var body: some View {
        if let draft = Binding($editing) {
            NotesEditor(notes: draft, showTopics: true) { save in
                if save, let editing { model.saveEditedNotes(editing) }
                editing = nil
            }
        } else if let notes = detail.notes, !notes.isEmpty {
            ReadingColumn {
                EditBar { editing = notes }
                if notes.topics.isEmpty {
                    Text("這場會議沒有分出議題（可能是範本沒有包含「議題詳細」）。").foregroundStyle(.secondary)
                }
                ForEach(Array(notes.topics.enumerated()), id: \.offset) { _, topic in
                    VStack(alignment: .leading, spacing: 10) {
                        HStack(alignment: .firstTextBaseline, spacing: 10) {
                            TimeButton(time: topic.start)
                            Text(topic.title).font(.songti(18))
                        }
                        ItemList(items: topic.points)
                    }
                }
            }
        } else {
            NotesPlaceholder(detail: detail)
        }
    }
}

/// 手動編輯會議紀錄（R6.6）：每一條都是可以改的欄位，可以新增、刪除、上下移動；按「完成」才存成新版本
struct NotesEditor: View {
    @Binding var notes: MeetingNotes
    let showTopics: Bool
    let onFinish: (Bool) -> Void

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Text(showTopics ? "編輯議題" : "編輯會議紀錄").font(.headline)
                Spacer()
                Button("取消") { onFinish(false) }
                    .keyboardShortcut(.cancelAction)
                Button("完成") { onFinish(true) }
                    .keyboardShortcut(.defaultAction)
                    .buttonStyle(.borderedProminent)
            }
            .padding(.horizontal, 32)
            .padding(.vertical, 10)
            Divider()
            ScrollView {
                VStack(alignment: .leading, spacing: 22) {
                    if showTopics {
                        ForEach($notes.topics.indices, id: \.self) { i in
                            VStack(alignment: .leading, spacing: 8) {
                                HStack {
                                    TextField("時間", text: $notes.topics[i].start).frame(width: 70).monospacedDigit()
                                    TextField("議題", text: $notes.topics[i].title).font(.songti(16))
                                    Button { notes.topics.remove(at: i) } label: { Image(systemName: "trash") }
                                        .buttonStyle(.borderless).help("刪除這個議題")
                                }
                                ItemsEditor(items: $notes.topics[i].points)
                            }
                        }
                        Button("新增議題", systemImage: "plus") {
                            notes.topics.append(.init(title: "", start: "", points: []))
                        }
                    } else {
                        TextField("標題", text: $notes.title).font(.songti(18))
                        EditorSection(title: String(localized: "摘要")) { ItemsEditor(items: $notes.summary) }
                        EditorSection(title: String(localized: "決議")) { ItemsEditor(items: $notes.decisions) }
                        EditorSection(title: String(localized: "待辦")) { ActionItemsEditor(items: $notes.actionItems) }
                        EditorSection(title: String(localized: "未決問題")) { ItemsEditor(items: $notes.openQuestions) }
                        ForEach($notes.custom.indices, id: \.self) { i in
                            EditorSection(title: notes.custom[i].title) { ItemsEditor(items: $notes.custom[i].items) }
                        }
                        if !notes.reviewItems.isEmpty {
                            EditorSection(title: String(localized: "待確認")) {
                                ForEach(Array(notes.reviewItems.enumerated()), id: \.offset) { index, item in
                                    HStack(alignment: .firstTextBaseline) {
                                        Text(item.time).monospacedDigit().foregroundStyle(.secondary)
                                        Text(item.question)
                                        Spacer()
                                        Button { notes.reviewItems.remove(at: index) } label: { Image(systemName: "trash") }
                                            .buttonStyle(.borderless).help("刪除這一題")
                                    }
                                }
                            }
                        }
                    }
                }
                .textFieldStyle(.roundedBorder)
                .frame(maxWidth: 720, alignment: .leading)
                .padding(.horizontal, 32)
                .padding(.vertical, 20)
                .frame(maxWidth: .infinity, alignment: .leading)
            }
        }
    }
}

private struct EditorSection<Content: View>: View {
    let title: String
    @ViewBuilder var content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(title).font(.songti(16))
            content
        }
    }
}

private struct ItemsEditor: View {
    @Binding var items: [MeetingNotes.Item]

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            ForEach(items.indices, id: \.self) { i in
                HStack(spacing: 6) {
                    TextField("內容", text: $items[i].text, axis: .vertical)
                    TextField("時間", text: $items[i].time).frame(width: 64).monospacedDigit()
                    RowButtons(index: i, count: items.count, move: { items.swapAt(i, $0) }, delete: { items.remove(at: i) })
                }
            }
            Button("新增一條", systemImage: "plus") { items.append(.init(text: "")) }
                .buttonStyle(.borderless)
                .font(.callout)
        }
    }
}

private struct ActionItemsEditor: View {
    @Binding var items: [MeetingNotes.ActionItem]

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            ForEach(items.indices, id: \.self) { i in
                HStack(spacing: 6) {
                    TextField("負責人", text: $items[i].owner).frame(width: 90)
                    TextField("事項", text: $items[i].task, axis: .vertical)
                    TextField("yyyy-MM-dd", text: $items[i].due).frame(width: 100).monospacedDigit()
                    TextField("時間", text: $items[i].time).frame(width: 64).monospacedDigit()
                    RowButtons(index: i, count: items.count, move: { items.swapAt(i, $0) }, delete: { items.remove(at: i) })
                }
            }
            Button("新增待辦", systemImage: "plus") { items.append(.init(owner: "", task: "", due: "")) }
                .buttonStyle(.borderless)
                .font(.callout)
        }
    }
}

private struct RowButtons: View {
    let index: Int
    let count: Int
    let move: (Int) -> Void
    let delete: () -> Void

    var body: some View {
        HStack(spacing: 2) {
            Button { move(index - 1) } label: { Image(systemName: "chevron.up") }.disabled(index == 0).help("往上移")
            Button { move(index + 1) } label: { Image(systemName: "chevron.down") }.disabled(index >= count - 1).help("往下移")
            Button(action: delete) { Image(systemName: "trash") }.help("刪除")
        }
        .buttonStyle(.borderless)
    }
}
