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
            Text(title).contentTitleFont(18)
            content
        }
    }
}

/// 一條紀錄：內容加上時間點（R6.4：點時間點就跳到錄音那裡）。
/// `ref` 給每一條在會議紀錄裡的位置（R41.1）：有對到待確認的題目時，時間點後面顯示標記，點了在下面展開題目就地回答
struct ItemList: View {
    @Environment(AppModel.self) private var model
    let items: [MeetingNotes.Item]
    var detail: MeetingDetail? = nil
    var ref: (Int) -> NoteItemRef? = { _ in nil }

    var body: some View {
        VStack(alignment: .leading, spacing: 7) {
            ForEach(Array(items.enumerated()), id: \.offset) { index, item in
                let ref = ref(index)
                VStack(alignment: .leading, spacing: 8) {
                    HStack(alignment: .firstTextBaseline, spacing: 8) {
                        Text("•").foregroundStyle(.secondary)
                        (Text(item.text)).meetingText()
                            .textSelection(.enabled)
                            .fixedSize(horizontal: false, vertical: true)
                        if !item.time.isEmpty { TimeButton(time: item.time).font(.caption.monospacedDigit()) }
                        if let ref, let detail { ReviewMarker(ref: ref, count: detail.reviewCount(ref)) }
                    }
                    if let ref, let detail, model.reviewDraft?.expanded == ref {
                        InlineReviewCard(detail: detail, indexes: detail.reviewLinks[ref] ?? [])
                    }
                }
            }
        }
        .lineSpacing(3)
    }
}

/// 待確認的標記（R41.1，design §31.2）：這一條紀錄有幾題整理時不確定的地方，琥珀色跟側欄的「待確認 N 題」一樣。
/// 點了在那一條下面展開題目（R41.2）；同時只展開一條，再點一次收合
struct ReviewMarker: View {
    @Environment(AppModel.self) private var model
    let ref: NoteItemRef
    let count: Int

    var body: some View {
        if count > 0 {
            Button {
                if let draft = model.reviewDraft {
                    draft.expanded = draft.expanded == ref ? nil : ref
                } else {
                    model.tab = .summary
                    model.reviewExpanded = true
                }
            } label: {
                Text(count > 1 ? "\(MeetingLanguage.ui(zh: "？", en: "?"))\(count)" : MeetingLanguage.ui(zh: "？", en: "?"))
                    .font(.caption2.weight(.semibold).monospacedDigit())
                    .foregroundStyle(Color.signal)
                    .padding(.horizontal, count > 1 ? 5 : 0)
                    .frame(minWidth: 17, minHeight: 17)
                    .background(Color.signal.opacity(0.16), in: Capsule())
            }
            .buttonStyle(.plain)
            .help(count > 1 ? String(localized: "整理時有 \(count) 題不確定的地方，點了就能回答") : String(localized: "整理時有一題不確定的地方，點了就能回答"))
            .accessibilityLabel(String(localized: "待確認 \(count) 題"))
        }
    }
}

/// 就地回答（R41.2，design §31.2）：展開在那一條紀錄下面，列出對應的題目；回答跟待確認分頁是同一份草稿，
/// 「送出」的規則也一樣（送目前所有已回答和略過的題目）
struct InlineReviewCard: View {
    @Environment(AppModel.self) private var model
    let detail: MeetingDetail
    let indexes: [Int]

    var body: some View {
        if let draft = model.reviewDraft, draft.meetingId == detail.meeting.id, let items = detail.notes?.reviewItems {
            let shown = Set(indexes.filter { items.indices.contains($0) })
            VStack(alignment: .leading, spacing: 12) {
                ForEach(shown.sorted(), id: \.self) { index in
                    ReviewQuestionRow(index: index, item: items[index], language: detail.meeting.meetingLanguage, draft: draft)
                }
                HStack(spacing: 12) {
                    Text("只送這一條的題目；有回答的交給整理工具依答案修正，略過的直接移除。")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                    Spacer()
                    Button("收合") { draft.expanded = nil }
                    // 只送這一條對到的題目（R41.2），題數也只算這幾題
                    Button(draft.submitTitle(only: shown)) { model.submitReviewDraft(only: shown) }
                        .buttonStyle(.borderedProminent)
                        .disabled(!draft.hasSomethingToSubmit(only: shown) || model.detailStatus?.isActive == true)
                }
            }
            .padding(14)
            .background(Color.signal.opacity(0.07), in: RoundedRectangle(cornerRadius: 8))
            .padding(.leading, 14)
        }
    }
}

/// 沒有會議紀錄時的說明與動作
struct NotesPlaceholder: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openSettings) private var openSettings
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
            } else if AppSettings.needsProviderSetup {
                // 沒選過整理工具、也沒有 claude、codex（R35.2）：說還沒設定，給打開設定的按鈕（整理工具是第一個分頁）
                Text("還沒設定整理工具。選好之後，就能把逐字稿整理成摘要、決議和待辦；之後的會議轉錄完會自動整理。")
                    .foregroundStyle(.secondary)
                Button("設定整理工具") { openSettings() }
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

/// 紀錄分頁（R44.1，design §33.1）：待確認的條、摘要、決議、待辦、未決問題、議題、自訂段落，一頁捲到底（順序同匯出）。
/// 可以切換成編輯模式（R6.6），一份編輯器編全部
struct NotesPage: View {
    @Environment(AppModel.self) private var model
    let detail: MeetingDetail
    @State private var editing: MeetingNotes?
    @State private var base: MeetingNotes?

    var body: some View {
        if let current = editing {
            NotesEditor(notes: Binding(draft: $editing, fallback: current)) { finish($0) }
                .onDisappear { finish(true, automatically: true) }   // 切到別的分頁、別場會議：自動存，不丟掉打的字
        } else if let notes = detail.notes, !notes.isEmpty {
            ReadingColumn {
                if detail.reviewCount > 0 { ReviewBanner(detail: detail) }
                EditBar(disabled: model.detailStatus?.isActive == true, onUndo: detail.canUndoNotes ? { model.undoNotes() } : nil) {
                    base = notes; editing = notes
                }
                if !notes.summary.isEmpty {
                    NotesSection(title: String(localized: "摘要")) { ItemList(items: notes.summary, detail: detail) { .summary($0) } }
                }
                if !notes.decisions.isEmpty {
                    NotesSection(title: String(localized: "決議")) { ItemList(items: notes.decisions, detail: detail) { .decisions($0) } }
                }
                if !notes.actionItems.isEmpty {
                    NotesSection(title: String(localized: "待辦")) { ActionItemsTable(items: notes.actionItems, detail: detail) { .actionItems($0) } }
                }
                if !notes.openQuestions.isEmpty {
                    NotesSection(title: String(localized: "未決問題")) { ItemList(items: notes.openQuestions, detail: detail) { .openQuestions($0) } }
                }
                if !notes.topics.isEmpty {
                    NotesSection(title: String(localized: "議題")) {
                        VStack(alignment: .leading, spacing: 22) {
                            ForEach(Array(notes.topics.enumerated()), id: \.offset) { t, topic in
                                VStack(alignment: .leading, spacing: 10) {
                                    HStack(alignment: .firstTextBaseline, spacing: 10) {
                                        TimeButton(time: topic.start)
                                        Text(topic.title).contentTitleFont(16)
                                    }
                                    ItemList(items: topic.points, detail: detail) { .topicPoint(t, $0) }
                                }
                            }
                        }
                    }
                }
                ForEach(Array(notes.custom.enumerated()), id: \.offset) { c, section in
                    if !section.items.isEmpty {
                        NotesSection(title: section.title) { ItemList(items: section.items, detail: detail) { .custom(c, $0) } }
                    }
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

    private func finish(_ save: Bool, automatically: Bool = false) {
        guard let draft = editing, let id = detail.meeting.id else { return }
        editing = nil
        if save { model.finishNotesEdit(draft, base: base, meetingId: id, automatically: automatically) }
    }

    private var templateName: String {
        String(localized: "範本：") + (model.templates.first { $0.id == detail.meeting.templateId }?.displayName ?? String(localized: "一般會議"))
    }
}

/// 紀錄頂部「還有 N 題待確認」（R44.2，design §33.1）：點了在原地展開待確認的內容（同一份 ReviewTab），回答跟就地回答共用草稿；
/// 展開狀態只記這次選取期間（AppModel.reviewExpanded，換會議收起）
struct ReviewBanner: View {
    @Environment(AppModel.self) private var model
    let detail: MeetingDetail

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            Button {
                withAnimation(.easeInOut(duration: 0.15)) { model.reviewExpanded.toggle() }
            } label: {
                HStack(spacing: 10) {
                    Image(systemName: "questionmark.circle.fill").foregroundStyle(Color.signal)
                    Text("還有 \(detail.reviewCount) 題待確認").font(.callout.weight(.medium))
                    Spacer()
                    Text(model.reviewExpanded ? "收起" : "展開").font(.callout).foregroundStyle(.secondary)
                    Image(systemName: model.reviewExpanded ? "chevron.up" : "chevron.down").font(.caption).foregroundStyle(.secondary)
                }
                .padding(.horizontal, 14)
                .padding(.vertical, 10)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .accessibilityLabel(String(localized: "待確認 \(detail.reviewCount) 題"))
            if model.reviewExpanded {
                Divider()
                ReviewTab(detail: detail, embedded: true)
                    .padding(.horizontal, 14)
                    .padding(.vertical, 12)
            }
        }
        .background(Color.signal.opacity(0.08), in: RoundedRectangle(cornerRadius: 10))
    }
}

/// 右上角的「編輯」按鈕；有上一版時旁邊多一顆「復原上一版」（R44.4，原本在「更多」）
struct EditBar: View {
    /// 轉錄或整理中不能編輯：工作完成時會整份寫回，手動改的會被蓋掉
    var disabled = false
    /// nil：沒有上一版，不顯示
    var onUndo: (() -> Void)? = nil
    let onEdit: () -> Void

    var body: some View {
        HStack(spacing: 14) {
            Spacer()
            if let onUndo {
                Button("復原上一版", systemImage: "arrow.uturn.backward", action: onUndo)
                    .buttonStyle(.borderless)
                    .foregroundStyle(disabled ? Color.secondary : Color.celadon)
                    .disabled(disabled)
                    .help("換回上一版會議紀錄（再按一次換回更早的）")
            }
            Button("編輯", systemImage: "pencil", action: onEdit)
                .buttonStyle(.borderless)
                .foregroundStyle(disabled ? Color.secondary : Color.celadon)
                .disabled(disabled)
                .help(disabled ? "正在處理這場會議，完成後才能編輯" : "直接修改會議紀錄，存檔時會留一個版本")
        }
        .padding(.bottom, -18)
    }
}

struct ActionItemsTable: View {
    @Environment(AppModel.self) private var model
    let items: [MeetingNotes.ActionItem]
    var detail: MeetingDetail? = nil
    var ref: (Int) -> NoteItemRef? = { _ in nil }

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
            ForEach(Array(items.enumerated()), id: \.offset) { index, item in
                let ref = ref(index)
                GridRow {
                    Text(item.owner.isEmpty ? String(localized: "未指定") : item.owner).meetingText()
                        .foregroundStyle(item.owner.isEmpty ? Color.signal : Color.primary)
                    HStack(alignment: .firstTextBaseline, spacing: 8) {
                        Text(item.task).meetingText()
                            .textSelection(.enabled)
                            .fixedSize(horizontal: false, vertical: true)
                        if !item.time.isEmpty { TimeButton(time: item.time).font(.caption.monospacedDigit()) }
                        if let ref, let detail { ReviewMarker(ref: ref, count: detail.reviewCount(ref)) }
                    }
                    .gridColumnAlignment(.leading)
                    Text(item.due.isEmpty ? "—" : item.due)
                        .monospacedDigit()
                        .foregroundStyle(item.due.isEmpty ? .secondary : .primary)
                }
                if let ref, let detail, model.reviewDraft?.expanded == ref {
                    GridRow {
                        InlineReviewCard(detail: detail, indexes: detail.reviewLinks[ref] ?? [])
                            .gridCellColumns(3)
                    }
                }
            }
        }
    }
}

extension Binding {
    /// 編輯中的草稿（可能是 nil）給編輯器用：草稿已經是 nil 時讀到 fallback，寫入不理會。
    /// 不能用 `Binding($editing)`：按「完成」「取消」把草稿設成 nil 時，同一次更新裡編輯器的欄位還會再讀一次，
    /// 它會強制解開 nil 而閃退（3.2.0 的錯誤，3.2.1 修正）
    init(draft base: Binding<Value?>, fallback: Value) {
        self.init(get: { base.wrappedValue ?? fallback },
                  set: { if base.wrappedValue != nil { base.wrappedValue = $0 } })
    }
}

/// 手動編輯會議紀錄（R6.6）：每一條都是可以改的欄位，可以新增、刪除、上下移動；按「完成」才存成新版本
struct NotesEditor: View {
    @Binding var notes: MeetingNotes
    let onFinish: (Bool) -> Void

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Text("編輯會議紀錄").font(.headline)
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
                    TextField("標題", text: $notes.title).contentTitleFont(18)
                    EditorSection(title: String(localized: "摘要")) { ItemsEditor(items: $notes.summary) }
                    EditorSection(title: String(localized: "決議")) { ItemsEditor(items: $notes.decisions) }
                    EditorSection(title: String(localized: "待辦")) { ActionItemsEditor(items: $notes.actionItems) }
                    EditorSection(title: String(localized: "未決問題")) { ItemsEditor(items: $notes.openQuestions) }
                    // 議題（原「詳細」分頁的編輯器，R44.1 合併成一份）
                    EditorSection(title: String(localized: "議題")) {
                        ForEach(notes.topics.indices, id: \.self) { i in
                            let topic = $notes.topics.safe(i, fallback: .init(title: "", start: "", points: []))
                            VStack(alignment: .leading, spacing: 8) {
                                HStack {
                                    TextField("時間", text: topic.start).frame(width: 70).monospacedDigit()
                                    TextField("議題", text: topic.title).contentTitleFont(16)
                                    Button { if notes.topics.indices.contains(i) { notes.topics.remove(at: i) } } label: { Image(systemName: "trash") }
                                        .buttonStyle(.borderless).help("刪除這個議題")
                                }
                                ItemsEditor(items: topic.points)
                            }
                        }
                        Button("新增議題", systemImage: "plus") {
                            notes.topics.append(.init(title: "", start: "", points: []))
                        }
                        .buttonStyle(.borderless)
                        .font(.callout)
                    }
                    ForEach(notes.custom.indices, id: \.self) { i in
                        let section = $notes.custom.safe(i, fallback: .init(title: "", items: []))
                        EditorSection(title: section.wrappedValue.title) { ItemsEditor(items: section.items) }
                    }
                    if !notes.reviewItems.isEmpty {
                        EditorSection(title: String(localized: "待確認")) {
                            ForEach(Array(notes.reviewItems.enumerated()), id: \.offset) { index, item in
                                HStack(alignment: .firstTextBaseline) {
                                    Text(item.time).monospacedDigit().foregroundStyle(.secondary)
                                    Text(item.question).meetingText()
                                    Spacer()
                                    Button { if notes.reviewItems.indices.contains(index) { notes.reviewItems.remove(at: index) } } label: { Image(systemName: "trash") }
                                        .buttonStyle(.borderless).help("刪除這一題")
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
            Text(title).contentTitleFont(16)
            content
        }
    }
}

private struct ItemsEditor: View {
    @Binding var items: [MeetingNotes.Item]

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            ForEach(items.indices, id: \.self) { i in
                let item = $items.safe(i, fallback: .init(text: ""))
                HStack(spacing: 6) {
                    TextField("內容", text: item.text, axis: .vertical).meetingText()
                    TextField("時間", text: item.time).frame(width: 64).monospacedDigit()
                    RowButtons(index: i, count: items.count, move: { $items.move(i, to: $0) }, delete: { $items.remove(i) })
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
                let item = $items.safe(i, fallback: .init(owner: "", task: "", due: ""))
                HStack(spacing: 6) {
                    TextField("負責人", text: item.owner).frame(width: 90)
                    TextField("事項", text: item.task, axis: .vertical).meetingText()
                    TextField("yyyy-MM-dd", text: item.due).frame(width: 100).monospacedDigit()
                    TextField("時間", text: item.time).frame(width: 64).monospacedDigit()
                    RowButtons(index: i, count: items.count, move: { $items.move(i, to: $0) }, delete: { $items.remove(i) })
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

/// 編輯清單用的陣列元素綁定：刪掉一條之後 SwiftUI 可能還會用舊的位置讀寫一次，越界時讀到空值、不寫入，不會閃退
extension Binding where Value: MutableCollection & RangeReplaceableCollection, Value.Index == Int {
    func safe(_ index: Int, fallback: Value.Element) -> Binding<Value.Element> {
        Binding<Value.Element>(
            get: { wrappedValue.indices.contains(index) ? wrappedValue[index] : fallback },
            set: { if wrappedValue.indices.contains(index) { wrappedValue[index] = $0 } })
    }

    func remove(_ index: Int) {
        if wrappedValue.indices.contains(index) { wrappedValue.remove(at: index) }
    }

    func move(_ index: Int, to target: Int) {
        if wrappedValue.indices.contains(index) && wrappedValue.indices.contains(target) { wrappedValue.swapAt(index, target) }
    }
}
