import SwiftUI

/// 側欄的一個人（design §60.2、R85.1）：頭像、名字、一起開過幾場和最近一場
struct PersonRow: View {
    let person: PersonOverview

    var body: some View {
        HStack(spacing: 10) {
            PersonAvatar(name: person.name, color: Color.person(person.name), size: 26, isMe: person.isMe, scales: false)
            VStack(alignment: .leading, spacing: 1) {
                Text(person.isMe ? String(localized: "\(person.name)（我）") : person.name).lineLimit(1)
                Text(PersonPage.summaryLine(person)).font(.caption).foregroundStyle(.secondary).lineLimit(1)
            }
        }
        .padding(.vertical, 2)
    }
}

/// 人物頁（design §60.2、R85.2）：一起開過的會議、他負責的待辦，在這裡改名、合併、刪除聲音特徵
struct PersonPage: View {
    @Environment(AppModel.self) private var model
    let person: PersonOverview
    @State private var meetings: [PersonMeeting] = []
    @State private var items: [PersonActionItem] = []
    /// 名字旁的鉛筆打開的改名（R85.6）；「更多 → 改名…」、側欄右鍵的改名也打開這一個（R85.7）
    @State private var editingName = false
    @State private var renameText = ""

    var body: some View {
        ReadingColumn {
            header
            NotesSection(title: String(localized: "負責的待辦")) {
                if !items.isEmpty {
                    Button("加到提醒事項", systemImage: "checklist") { model.addPersonItemsToReminders(items) }
                        .buttonStyle(.borderless)
                        .font(.callout)
                        .disabled(model.addingReminders)
                        .help("把這些待辦加到「提醒事項」app 的「\(Reminders.listTitle)」列表，加過的不會重複加")
                }
            } content: {
                if items.isEmpty {
                    Text("會議紀錄裡還沒有這個人負責的待辦。").foregroundStyle(.secondary)
                } else {
                    itemsGrid
                }
            }
            NotesSection(title: MeetingLanguage.ui(zh: "會議", en: "Meetings")) {
                VStack(alignment: .leading, spacing: 8) {
                    ForEach(meetings) { entry in
                        Button { model.openFromPersonPage(entry.id) } label: {
                            HStack(alignment: .firstTextBaseline, spacing: 14) {
                                Text(entry.meeting.meetingDate).monospacedDigit().foregroundStyle(.secondary)
                                Text(entry.meeting.displayTitle).meetingText().lineLimit(1)
                                Spacer(minLength: 12)
                                Text(Self.spoke(entry.seconds)).font(.callout).foregroundStyle(.secondary)
                            }
                            .contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                        .help("打開這場會議")
                        .contextMenu { MeetingLinkMenuItems(meeting: entry.meeting) }   // 掛在按鈕上，不然按鈕會吃掉右鍵
                        .rowMenu { MeetingLinkMenuItems(meeting: entry.meeting) }
                    }
                }
            }
        }
        .navigationTitle(person.name)
        // 改名、合併、刪除放在工具列右上角的「更多」，跟會議頁同一個位置
        .toolbar { ToolbarItem(placement: .primaryAction) { moreMenu } }
        .task(id: person) { load() }
        // 「改名…」（更多、側欄右鍵）：打開鉛筆下面的輸入框
        .task(id: model.personRenameRequest) {
            guard model.personRenameRequest == person.id else { return }
            model.personRenameRequest = nil
            renameText = person.name
            editingName = true
        }
    }

    /// 頭像、名字、摘要；改名、合併、刪除收在右邊的「更多」（同會議頁的「更多」，破壞性的動作不跟其他並排）
    private var header: some View {
        HStack(alignment: .center, spacing: 16) {
            PersonAvatar(name: person.name, color: Color.person(person.name), size: 52, isMe: person.isMe, scales: false)
            VStack(alignment: .leading, spacing: 4) {
                HStack(alignment: .firstTextBaseline, spacing: 8) {
                    Text(person.name).contentTitleFont(26).textSelection(.enabled).lineLimit(1)
                    Button { renameText = person.name; editingName = true } label: { Image(systemName: "pencil") }
                        .buttonStyle(.borderless)
                        .foregroundStyle(Color.celadon)
                        .help("重新命名")
                        .accessibilityLabel(Text("重新命名"))
                        .popover(isPresented: $editingName, arrowEdge: .bottom) { renameForm }
                    if person.isMe {
                        Text("我").font(.callout).foregroundStyle(.secondary)
                    }
                }
                Text(Self.summaryLine(person, long: true)).font(.callout).foregroundStyle(.secondary)
                actions
            }
            Spacer(minLength: 0)
        }
        .padding(.bottom, 4)
    }

    /// 標題下面看得到的動作（R100）：合併到、移除這個人；重新命名是名字旁的鉛筆，右上角「更多」照舊
    private var actions: some View {
        let others = model.peopleOverview.filter { $0.id != person.id }
        return HStack(spacing: 16) {
            if !others.isEmpty {
                Menu {
                    ForEach(others) { other in
                        Button(other.name) {
                            model.renamePerson(person.id, to: other.name)
                            model.selectedPerson = other.id
                        }
                    }
                } label: {
                    Label("合併到", systemImage: "arrow.triangle.merge")
                }
                .menuStyle(.borderlessButton)
                .fixedSize()
                .help("同一個人被記成兩個名字時，合併成一個")
            }
            Button("移除這個人…", systemImage: "person.badge.minus", role: .destructive) { model.confirmRemovePerson = person.id }
                .foregroundStyle(.red)
        }
        .buttonStyle(.borderless)
        .font(.callout)
        .padding(.top, 4)
    }

    private var moreMenu: some View {
        Menu {
            PersonMenuItems(person: person)
        } label: {
            Label("更多", systemImage: "ellipsis.circle")
        }
        .accessibilityLabel(Text("更多"))
        .help("重新命名、跟另一個名字合併（同一個人被記成兩個名字時）、移除這個人")
    }

    private var renameForm: some View {
        VStack(alignment: .leading, spacing: 12) {
            TextField("名字", text: $renameText).textFieldStyle(.roundedBorder).frame(width: 220)
                .onSubmit(saveRename)
            HStack {
                Spacer()
                Button("取消") { editingName = false }.keyboardShortcut(.cancelAction)
                Button("儲存", action: saveRename).keyboardShortcut(.defaultAction)
                    .disabled(renameText.trimmingCharacters(in: .whitespaces).isEmpty)
            }
        }
        .padding(16)
    }

    private func saveRename() {
        let name = renameText.trimmingCharacters(in: .whitespaces)
        guard !name.isEmpty else { return }
        model.renamePerson(person.id, to: name)
        editingName = false
        // 改成已經有的名字時合併過去，選那個人
        model.selectedPerson = model.peopleOverview.first { $0.name.caseInsensitiveCompare(name) == .orderedSame }?.id
    }

    /// 他負責的待辦：事項、期限、哪一場會議和時間點（點了打開那場、從那裡播放）
    private var itemsGrid: some View {
        Grid(alignment: .leadingFirstTextBaseline, horizontalSpacing: 18, verticalSpacing: 9) {
            GridRow {
                Text("事項")
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
                    Text(entry.item.due.isEmpty ? "—" : entry.item.due).monospacedDigit()
                        .foregroundStyle(entry.item.due.isEmpty ? .secondary : .primary)
                    Button {
                        model.openFromPersonPage(entry.meeting.id ?? 0, seconds: TimeFormat.seconds(entry.item.time))
                    } label: {
                        HStack(spacing: 6) {
                            Text(entry.meeting.displayTitle).lineLimit(1)
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

    private func load() {
        meetings = (try? model.db.personMeetings(person.id)) ?? []
        items = (try? model.db.personActionItems(person.name)) ?? []
    }

    /// 「N 場會議・最近 <日期>」；人物頁上方用長的寫法
    static func summaryLine(_ person: PersonOverview, long: Bool = false) -> String {
        guard let date = person.lastMeetingDate else {
            return person.isMe ? String(localized: "錄過你的聲音，還沒有一起開過會議") : String(localized: "還沒有一起開過會議")
        }
        return long ? String(localized: "一起開過 \(person.meetingCount) 場會議，最近 \(date)")
            : String(localized: "\(person.meetingCount) 場會議・最近 \(date)")
    }

    static func spoke(_ seconds: Double) -> String {
        let minutes = Int((seconds / 60).rounded())
        return minutes < 1 ? String(localized: "說了不到 1 分鐘") : String(localized: "說了 \(minutes) 分鐘")
    }
}

/// 一個人的動作（R85.7）：人物頁的「更多」和側欄人物列的右鍵選單共用
struct PersonMenuItems: View {
    @Environment(AppModel.self) private var model
    let person: PersonOverview

    var body: some View {
        let others = model.peopleOverview.filter { $0.id != person.id }
        Button("重新命名…") {
            model.selectedPerson = person.id
            model.personRenameRequest = person.id
        }
        if !others.isEmpty {
            Menu("合併到") {
                ForEach(others) { other in
                    Button(other.name) {
                        model.renamePerson(person.id, to: other.name)
                        model.selectedPerson = other.id
                    }
                }
            }
        }
        Divider()
        Button("移除這個人…", role: .destructive) { model.confirmRemovePerson = person.id }
    }
}

/// 人物模式、還沒選人時的右邊（design §60.2）
struct PeopleEmptyView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        VStack(spacing: 10) {
            Image(systemName: "person.2").font(.largeTitle).foregroundStyle(.tertiary)
            Text(model.peopleOverview.isEmpty
                 ? String(localized: "還沒有認識的人")
                 : String(localized: "選一個人，看一起開過的會議和負責的待辦"))
                .foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}
