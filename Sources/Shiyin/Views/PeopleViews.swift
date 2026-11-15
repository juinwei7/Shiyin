import SwiftUI

/// 側欄的人物列表（design §60.2、R85.1）
struct PeopleList: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        @Bindable var model = model
        List(selection: $model.selectedPerson) {
            ForEach(model.peopleOverview) { person in
                HStack(spacing: 10) {
                    PersonAvatar(name: person.name, color: Color.person(person.name), size: 26, isMe: person.isMe, scales: false)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(person.isMe ? String(localized: "\(person.name)（我）") : person.name).lineLimit(1)
                        Text(PersonPage.summaryLine(person)).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                    }
                }
                .padding(.vertical, 2)
                .tag(person.id)
            }
        }
        .overlay {
            if model.peopleOverview.isEmpty {
                Text("在逐字稿點「說話者 1」改成名字，或到設定錄一段你自己的聲音，拾音就會記住這個人。")
                    .font(.callout).foregroundStyle(.secondary).multilineTextAlignment(.center)
                    .padding(24)
            }
        }
        .onAppear { model.reloadPeopleOverview() }
    }
}

/// 人物頁（design §60.2、R85.2）：一起開過的會議、他負責的待辦，在這裡改名、合併、刪除聲音特徵
struct PersonPage: View {
    @Environment(AppModel.self) private var model
    let person: PersonOverview
    @State private var meetings: [PersonMeeting] = []
    @State private var items: [PersonActionItem] = []
    @State private var renaming = false
    @State private var renameText = ""
    @State private var confirmDelete = false

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
            NotesSection(title: String(localized: "會議")) {
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
                    }
                }
            }
        }
        .task(id: person) { load() }
    }

    private var header: some View {
        HStack(alignment: .center, spacing: 18) {
            PersonAvatar(name: person.name, color: Color.person(person.name), size: 56, isMe: person.isMe, scales: false)
            VStack(alignment: .leading, spacing: 6) {
                HStack(alignment: .firstTextBaseline, spacing: 10) {
                    Text(person.name).contentTitleFont(26).textSelection(.enabled)
                    if person.isMe {
                        Text("我").font(.caption.weight(.semibold)).foregroundStyle(Color.celadon)
                            .padding(.horizontal, 6).padding(.vertical, 2)
                            .background(Color.celadon.opacity(0.15), in: Capsule())
                    }
                }
                Text(Self.summaryLine(person, long: true)).foregroundStyle(.secondary)
                HStack(spacing: 14) {
                    Button("改名") { renameText = person.name; renaming = true }
                        .popover(isPresented: $renaming, arrowEdge: .bottom) { renameForm }
                    let others = model.peopleOverview.filter { $0.id != person.id }
                    if !others.isEmpty {
                        Menu("合併到…") {
                            ForEach(others) { other in
                                Button(other.name) {
                                    model.renamePerson(person.id, to: other.name)
                                    model.selectedPerson = other.id
                                }
                            }
                        }
                        .menuStyle(.borderlessButton)
                        .menuIndicator(.hidden)
                        .fixedSize()
                        .help("同一個人被記成兩個名字時，合併成一個（聲音特徵、會議裡的名字一起改）")
                    }
                    Button("刪除聲音特徵…", role: .destructive) { confirmDelete = true }
                }
                .buttonStyle(.link)
                .font(.callout)
            }
        }
        .padding(.bottom, 8)
        .confirmationDialog("刪除「\(person.name)」的聲音特徵？", isPresented: $confirmDelete) {
            Button("刪除", role: .destructive) {
                model.deletePerson(person.id)
                model.selectedPerson = nil
            }
        } message: {
            Text("已經帶入的名字會留著，之後的會議不會再自動帶入。")
        }
    }

    private var renameForm: some View {
        VStack(alignment: .leading, spacing: 12) {
            TextField("名字", text: $renameText).textFieldStyle(.roundedBorder).frame(width: 220)
                .onSubmit(saveRename)
            HStack {
                Spacer()
                Button("取消") { renaming = false }.keyboardShortcut(.cancelAction)
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
        renaming = false
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
