import SwiftUI

// MARK: - 定期會議（R99、design §73）

/// 側欄的「定期會議」一段：有建議時第一列「發現 N 組可能的定期會議」，再來每個定期會議一列
struct SeriesSidebarSection: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        if !model.series.isEmpty || !model.seriesCandidates.isEmpty {
            Section("定期會議") {
                if !model.seriesCandidates.isEmpty {
                    Button { model.showSeriesSuggestions = true } label: {
                        Label(String(localized: "發現 \(model.seriesCandidates.count) 組可能的定期會議"), systemImage: "sparkles")
                            .foregroundStyle(Color.celadon)
                            .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .help("同一群人、每週同一天同一個時間開的會議。確認之後，下次的會議會自動加進去，整理時接上上次的待辦")
                }
                ForEach(model.series) { series in
                    let id = series.id ?? 0
                    FolderRow(name: series.name, count: model.seriesCount(id), symbol: "arrow.triangle.2.circlepath",
                              selected: model.sidebarMode == .meetings && model.seriesFilter == id,
                              menu: { AnyView(SeriesMenuItems(series: series)) }) {
                        model.openSeries(id)
                    }
                    .help(series.frequencyText)
                    .contextMenu { SeriesMenuItems(series: series) }
                }
            }
        }
    }
}

/// 確認可能的定期會議：一次一組（design §73）
struct SeriesSuggestionSheet: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    @State private var index = 0
    @State private var name = ""
    @State private var chosen: Set<Int64> = []
    /// 這次略過的（下次打開還在）
    @State private var skipped: Set<String> = []
    /// 這次處理過幾組（建立、不是、略過），算「第幾組」
    @State private var handled = 0

    private var candidates: [SeriesCandidate] { model.seriesCandidates.filter { !skipped.contains($0.key) } }

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            if let candidate = candidates.first {
                content(candidate)
            } else {
                Text(skipped.isEmpty ? String(localized: "沒有其他可能的定期會議了。")
                                     : String(localized: "略過的那幾組下次打開還會在。"))
                    .foregroundStyle(.secondary)
                HStack {
                    Spacer()
                    Button("完成") { dismiss() }.keyboardShortcut(.defaultAction)
                }
            }
        }
        .padding(20)
        .frame(width: 460)
        .onAppear { load(candidates.first) }
        .onChange(of: candidates.first?.key) { _, _ in load(candidates.first) }
    }

    @ViewBuilder
    private func content(_ candidate: SeriesCandidate) -> some View {
        let total = handled + candidates.count
        HStack(alignment: .firstTextBaseline) {
            Text("可能的定期會議").font(.title3).fontWeight(.semibold)
            Spacer()
            if total > 1 { Text("第 \(handled + 1) 組，共 \(total) 組").font(.callout).foregroundStyle(.secondary) }
        }
        Text("這幾場會議是同一群人開的（\(candidate.frequencyText)）。確認之後，下次的會議會自動加進來，整理時會接上上次的待辦。")
            .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
        LabeledContent("名稱") {
            TextField("名稱", text: $name).textFieldStyle(.roundedBorder).labelsHidden()
        }
        let people = model.peopleOverview.filter { candidate.people.contains($0.id) }
        if !people.isEmpty {
            HStack(spacing: 10) {
                ForEach(people) { person in
                    HStack(spacing: 5) {
                        PersonAvatar(name: person.name, color: Color.person(person.name), size: 20, isMe: person.isMe, scales: false)
                        Text(person.name).lineLimit(1)
                    }
                }
            }
            .font(.callout)
        }
        ScrollView {
            VStack(alignment: .leading, spacing: 6) {
                ForEach(candidate.meetingIds.reversed(), id: \.self) { id in
                    if let row = model.meetings.first(where: { $0.id == id }) {
                        Toggle(isOn: Binding(get: { chosen.contains(id) }, set: { if $0 { chosen.insert(id) } else { chosen.remove(id) } })) {
                            HStack(spacing: 12) {
                                Text(row.meeting.meetingDate).monospacedDigit().foregroundStyle(.secondary)
                                Text(row.meeting.displayTitle).meetingText().lineLimit(1)
                            }
                        }
                        .toggleStyle(.checkbox)
                    }
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .frame(maxHeight: 200)
        .fixedSize(horizontal: false, vertical: true)
        HStack {
            Button("不是定期會議") { handled += 1; model.rejectSeriesCandidate(candidate) }
                .help("之後不再建議這一組")
            Spacer()
            Button("略過") { handled += 1; skipped.insert(candidate.key) }
                .keyboardShortcut(.cancelAction)
                .help("這次先不處理，下次打開還會在")
            Button("建立") {
                let ids = candidate.meetingIds.filter { chosen.contains($0) }
                handled += 1
                model.createSeries(candidate, name: name, meetingIds: ids)
            }
            .keyboardShortcut(.defaultAction)
            .disabled(chosen.count < 2)
        }
    }

    private func load(_ candidate: SeriesCandidate?) {
        guard let candidate else { return }
        name = candidate.name
        chosen = Set(candidate.meetingIds)
    }
}

/// 系列頁（design §73）：頻率、常參加的人、還沒完成的待辦、每一場會議
struct SeriesPage: View {
    @Environment(AppModel.self) private var model
    let series: MeetingSeries
    @State private var items: [PersonActionItem] = []

    var body: some View {
        let id = series.id ?? 0
        let rows = model.meetings.filter { $0.meeting.seriesId == id }
        ReadingColumn {
            header(rows)
            let regulars = model.seriesRegulars(id)
            if !regulars.isEmpty {
                NotesSection(title: String(localized: "常參加的人")) {
                    FlowLayout(spacing: 12) {
                        ForEach(regulars) { person in
                            Button { model.sidebarMode = .people; model.selectedPerson = person.id } label: {
                                HStack(spacing: 6) {
                                    PersonAvatar(name: person.name, color: Color.person(person.name), size: 22, isMe: person.isMe, scales: false)
                                    Text(person.name).meetingText()
                                }
                            }
                            .buttonStyle(.plain)
                            .help("打開人物頁")
                        }
                    }
                }
            }
            NotesSection(title: String(localized: "還沒完成的待辦")) {
                if !items.isEmpty {
                    Button("加到提醒事項", systemImage: "checklist") { model.addPersonItemsToReminders(items) }
                        .buttonStyle(.borderless)
                        .font(.callout)
                        .disabled(model.addingReminders)
                        .help("把這些待辦加到「提醒事項」app 的「\(Reminders.listTitle)」列表，加過的不會重複加")
                }
            } content: {
                if items.isEmpty {
                    Text("目前沒有還沒完成的待辦。之後每一場整理時，會對照上一場的待辦標出完成了沒。").foregroundStyle(.secondary)
                } else {
                    itemsGrid
                }
            }
            NotesSection(title: MeetingLanguage.ui(zh: "會議", en: "Meetings")) {
                VStack(alignment: .leading, spacing: 8) {
                    ForEach(rows) { row in
                        let rowSeries = series
                        Button { model.selection = row.id } label: {
                            HStack(alignment: .firstTextBaseline, spacing: 14) {
                                Text(row.meeting.meetingDate).monospacedDigit().foregroundStyle(.secondary)
                                Text(row.meeting.displayTitle).meetingText().lineLimit(1)
                                Spacer(minLength: 12)
                                if row.meeting.durationSeconds != nil {
                                    Text(TimeFormat.duration(row.meeting.durationSeconds)).font(.callout).foregroundStyle(.secondary)
                                }
                            }
                            .contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                        .help("打開這場會議")
                        .rowMenu { MeetingLinkMenuItems(meeting: row.meeting, series: rowSeries) }
                        .contextMenu { MeetingLinkMenuItems(meeting: row.meeting, series: rowSeries) }
                    }
                }
            }
        }
        .navigationTitle(series.name)
        .toolbar { ToolbarItem(placement: .primaryAction) { moreMenu } }
        .task(id: rows.map(\.meeting)) { items = model.seriesOpenItems(id) }
    }

    private func header(_ rows: [MeetingRow]) -> some View {
        HStack(alignment: .center, spacing: 16) {
            Image(systemName: "arrow.triangle.2.circlepath")
                .font(.system(size: 24, weight: .medium))
                .foregroundStyle(Color.celadon)
                .frame(width: 52, height: 52)
                .background(Color.celadon.opacity(0.12), in: Circle())
            VStack(alignment: .leading, spacing: 4) {
                HStack(alignment: .firstTextBaseline, spacing: 8) {
                    Text(series.name).contentTitleFont(26).textSelection(.enabled).lineLimit(1)
                    Button { model.renamingSeries = series.id } label: { Image(systemName: "pencil") }
                        .buttonStyle(.borderless)
                        .foregroundStyle(Color.celadon)
                        .help("重新命名")
                        .accessibilityLabel(Text("重新命名"))
                }
                Text(Self.summaryLine(series, rows: rows)).font(.callout).foregroundStyle(.secondary)
                // 看得到的動作（R100）：重新命名是名字旁的鉛筆；右上角「更多」照舊
                HStack(spacing: 16) {
                    Button("刪除定期會議…", systemImage: "trash", role: .destructive) { model.confirmDeleteSeries = series.id }
                        .foregroundStyle(.red)
                }
                .buttonStyle(.borderless)
                .font(.callout)
                .padding(.top, 4)
            }
            Spacer(minLength: 0)
        }
        .padding(.bottom, 4)
    }

    /// 「每週一 10:00・6 場・最近 2026-11-10」
    static func summaryLine(_ series: MeetingSeries, rows: [MeetingRow]) -> String {
        var parts = [series.frequencyText, String(localized: "\(rows.count) 場")]
        if let last = rows.first?.meeting.meetingDate { parts.append(String(localized: "最近 \(last)")) }
        return parts.joined(separator: "・")
    }

    private var moreMenu: some View {
        Menu {
            SeriesMenuItems(series: series)
        } label: {
            Label("更多", systemImage: "ellipsis.circle")
        }
        .accessibilityLabel(Text("更多"))
        .help("重新命名、刪除定期會議")
    }

    /// 事項、負責人、期限、哪一場會議和時間點（同人物頁）
    private var itemsGrid: some View {
        Grid(alignment: .leadingFirstTextBaseline, horizontalSpacing: 18, verticalSpacing: 9) {
            GridRow {
                Text("事項")
                Text("負責人")
                Text("期限")
                Text("會議")
            }
            .contentFont(.caption)
            .foregroundStyle(.secondary)
            Divider().gridCellUnsizedAxes(.horizontal)
            ForEach(items) { entry in
                GridRow {
                    Text(entry.item.task).meetingText().textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
                        .frame(maxWidth: .infinity, alignment: .leading)
                    Text(entry.item.owner.isEmpty ? "—" : entry.item.owner).meetingText()
                        .foregroundStyle(entry.item.owner.isEmpty ? .secondary : .primary)
                    Text(entry.item.due.isEmpty ? "—" : entry.item.due).monospacedDigit()
                        .foregroundStyle(entry.item.due.isEmpty ? .secondary : .primary)
                    Button {
                        model.openMeetingLink(entry.meeting.id ?? 0, seconds: TimeFormat.seconds(entry.item.time), activate: false)
                    } label: {
                        HStack(spacing: 6) {
                            Text(entry.meeting.meetingDate).monospacedDigit().lineLimit(1)
                            if !entry.item.time.isEmpty { Text(entry.item.time).monospacedDigit().foregroundStyle(Color.celadon) }
                        }
                    }
                    .buttonStyle(.plain)
                    .font(.callout)
                    .help("打開這場會議，從這裡開始播放")
                }
            }
        }
    }
}

/// 一排會換行的小元件（常參加的人）
struct FlowLayout: Layout {
    var spacing: CGFloat = 8

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let width = proposal.width ?? .infinity
        var x: CGFloat = 0, y: CGFloat = 0, line: CGFloat = 0, widest: CGFloat = 0
        for view in subviews {
            let size = view.sizeThatFits(.unspecified)
            if x > 0 && x + size.width > width { x = 0; y += line + spacing; line = 0 }
            x += size.width + spacing
            line = max(line, size.height)
            widest = max(widest, x - spacing)
        }
        return CGSize(width: min(widest, width), height: y + line)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        var x = bounds.minX, y = bounds.minY, line: CGFloat = 0
        for view in subviews {
            let size = view.sizeThatFits(.unspecified)
            if x > bounds.minX && x + size.width > bounds.maxX { x = bounds.minX; y += line + spacing; line = 0 }
            view.place(at: CGPoint(x: x, y: y), proposal: ProposedViewSize(size))
            x += size.width + spacing
            line = max(line, size.height)
        }
    }
}

/// 會議的右鍵選單：加到定期會議 ▸、移出（多選時全部）
struct SeriesMenu: View {
    @Environment(AppModel.self) private var model
    let meetingIds: [Int64]

    var body: some View {
        let current = model.commonSeries(meetingIds)
        if !model.series.isEmpty {
            Menu("加到定期會議", systemImage: "arrow.triangle.2.circlepath") {
                ForEach(model.series.filter { $0.id != current }) { series in
                    Button(series.name) { model.setSeries(meetingIds, series: series.id) }
                }
            }
        }
        if let current, let series = model.seriesById(current) {
            Button("移出「\(series.name)」") { model.setSeries(meetingIds, series: nil) }
        }
    }
}

/// 定期會議改名的輸入框（側欄右鍵、系列頁）
struct SeriesRenameModifier: ViewModifier {
    @Environment(AppModel.self) private var model
    @State private var name = ""

    func body(content: Content) -> some View {
        content.alert(String(localized: "重新命名定期會議"), isPresented: Binding(get: { model.renamingSeries != nil },
                                                                     set: { if !$0 { model.renamingSeries = nil } })) {
            TextField("名稱", text: $name)
            Button("取消", role: .cancel) { model.renamingSeries = nil }
            Button("重新命名") {
                if let id = model.renamingSeries { model.renameSeries(id, to: name) }
                model.renamingSeries = nil
            }
        }
        .onChange(of: model.renamingSeries) { _, id in name = model.seriesById(id)?.name ?? "" }
    }
}

// MARK: - 資料夾自動歸類（R98、design §72）

/// 資料夾右鍵「自動加入的條件…」
struct FolderRuleSheet: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    let folder: String
    @State private var keywords = ""
    @State private var people: Set<Int64> = []
    @State private var templateId: String?
    @State private var seriesId: Int64?
    @State private var applyNow = false
    @State private var existing = false

    private var rule: FolderRule {
        FolderRule(folder: folder, keywords: GlossaryInput.split(keywords), people: people.sorted(), templateId: templateId, seriesId: seriesId)
    }

    var body: some View {
        let matching = (try? model.db.meetingsMatching(rule).count) ?? 0
        VStack(alignment: .leading, spacing: 14) {
            Text("「\(folder)」自動加入的條件").font(.title3).fontWeight(.semibold)
            Text("符合任一個條件的新會議，會自動放進這個資料夾。已經在別的資料夾的會議不會移動。")
                .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            Form {
                TextField("標題包含", text: $keywords, prompt: Text("例如：週會、sync（任一個詞）"))
                if !model.peopleOverview.isEmpty {
                    LabeledContent("這些人都參加") {
                        ScrollView {
                            VStack(alignment: .leading, spacing: 4) {
                                ForEach(model.peopleOverview) { person in
                                    Toggle(person.isMe ? String(localized: "\(person.name)（我）") : person.name, isOn: Binding(
                                        get: { people.contains(person.id) },
                                        set: { if $0 { people.insert(person.id) } else { people.remove(person.id) } }))
                                    .toggleStyle(.checkbox)
                                }
                            }
                            .frame(maxWidth: .infinity, alignment: .leading)
                        }
                        .frame(maxHeight: 160)
                        .fixedSize(horizontal: false, vertical: true)
                    }
                }
                Picker("用這個範本", selection: $templateId) {
                    Text("不限").tag(String?.none)
                    Divider()
                    ForEach(model.templates) { Text($0.displayName).tag(Optional($0.id)) }
                }
                if !model.series.isEmpty {
                    Picker("屬於定期會議", selection: $seriesId) {
                        Text("不限").tag(Int64?.none)
                        Divider()
                        ForEach(model.series) { Text($0.name).tag($0.id) }
                    }
                }
            }
            .formStyle(.grouped)
            .scrollDisabled(true)
            .fixedSize(horizontal: false, vertical: true)
            Toggle(String(localized: "現在不在資料夾裡的會議也套用（\(matching) 場）"), isOn: $applyNow)
                .toggleStyle(.checkbox)
                .disabled(matching == 0 || rule.isEmpty)
            HStack {
                if existing {
                    Button("移除條件", role: .destructive) {
                        model.saveFolderRule(FolderRule(folder: folder), applyNow: false)
                        dismiss()
                    }
                }
                Spacer()
                Button("取消") { dismiss() }.keyboardShortcut(.cancelAction)
                Button("儲存") {
                    model.saveFolderRule(rule, applyNow: applyNow && matching > 0)
                    dismiss()
                }
                .keyboardShortcut(.defaultAction)
            }
        }
        .padding(20)
        .frame(width: 480)
        .onAppear {
            guard let rule = model.folderRule(folder) else { return }
            existing = true
            keywords = rule.keywords.joined(separator: MeetingLanguage.ui(zh: "、", en: ", "))
            people = Set(rule.people)
            templateId = rule.templateId
            seriesId = rule.seriesId
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
            Button("放進去") { model.acceptFolderSuggestion(meetingId, folder: suggestion.folder) }
                .buttonStyle(.borderedProminent)
        }
        .font(.callout)
        .padding(.horizontal, 14)
        .padding(.vertical, 8)
        .background(Color.celadon.opacity(0.08), in: RoundedRectangle(cornerRadius: 8))
    }
}

// MARK: - 上次的待辦（R99.5）

/// 紀錄分頁「上次的待辦」：狀態、事項、負責人、時間點
struct FollowUpsList: View {
    let followUps: [MeetingNotes.FollowUp]

    var body: some View {
        VStack(alignment: .leading, spacing: 9) {
            ForEach(Array(followUps.enumerated()), id: \.offset) { _, item in
                HStack(alignment: .firstTextBaseline, spacing: 8) {
                    Image(systemName: Self.symbol(item.status))
                        .foregroundStyle(Self.color(item.status))
                        .help(Self.label(item.status))
                        .accessibilityLabel(Text(Self.label(item.status)))
                    Text(item.task).meetingText().textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
                    if !item.owner.isEmpty { Text(item.owner).meetingText().foregroundStyle(.secondary) }
                    Text(Self.label(item.status)).contentFont(.caption).foregroundStyle(Self.color(item.status))
                    if !item.time.isEmpty { TimeButton(time: item.time) }
                }
            }
        }
    }

    static func symbol(_ status: MeetingNotes.FollowUp.Status) -> String {
        switch status {
        case .done: return "checkmark.circle.fill"
        case .ongoing: return "circle.lefthalf.filled"
        case .notMentioned: return "minus.circle"
        }
    }

    static func color(_ status: MeetingNotes.FollowUp.Status) -> Color {
        switch status {
        case .done: return .celadon
        case .ongoing: return .orange
        case .notMentioned: return .secondary
        }
    }

    static func label(_ status: MeetingNotes.FollowUp.Status) -> String {
        switch status {
        case .done: return String(localized: "完成")
        case .ongoing: return String(localized: "還在進行")
        case .notMentioned: return String(localized: "這次沒提到")
        }
    }
}
