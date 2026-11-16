import SwiftUI

/// 會議列表的「顯示方式」：排序、分組、篩選（R97、design §71）。列表上方的按鈕和選單列的「顯示方式」共用；
/// 選單列放成三個子選單，列表上方的按鈕放成三段
struct MeetingListMenu: View {
    let model: AppModel
    var submenus = false

    var body: some View {
        if submenus {
            // 選單列：Picker 的選單樣式就是有打勾的子選單（inline 會在子選單頭尾多出分隔線）
            sortPicker.pickerStyle(.menu)
            groupingPicker.pickerStyle(.menu)
                .disabled(model.showTrash)
            Menu("篩選") { filters }
                .disabled(model.showTrash)
        } else {
            // inline 的 Picker 自己會帶段落標題和分隔線；再包一層 Section 會多出連續好幾條分隔線（2026-11-16 自己驗收時發現）
            sortPicker.pickerStyle(.inline)
            if !model.showTrash {
                groupingPicker.pickerStyle(.inline)
                Section("篩選") { filters }
            }
        }
    }

    private var sortPicker: some View {
        @Bindable var model = model
        return Picker("排序方式", selection: $model.meetingSort) {
            ForEach(MeetingSort.allCases) { Text($0.label).tag($0) }
        }
    }

    /// 看某個資料夾時不能依資料夾分組，打勾的是實際用的
    private var groupingPicker: some View {
        Picker("分組方式", selection: Binding(get: { model.effectiveGrouping }, set: { model.meetingGrouping = $0 })) {
            ForEach(model.availableGroupings) { Text($0.label).tag($0) }
        }
    }

    @ViewBuilder
    private var filters: some View {
        @Bindable var model = model
        Toggle("有待確認的", isOn: $model.meetingFilter.needsReview)
        // 沒有設定「我」時不出現（R97.3）
        if model.myName != nil {
            Toggle("有我的待辦的", isOn: $model.meetingFilter.myActionItems)
        }
        if !model.peopleOverview.isEmpty {
            Picker("有誰參加", selection: $model.meetingFilter.personId) {
                Text("不限").tag(Int64?.none)
                Divider()
                ForEach(model.peopleOverview) { person in
                    Text(person.isMe ? String(localized: "\(person.name)（我）") : person.name).tag(Optional(person.id))
                }
            }
            .pickerStyle(.menu)
        }
        Picker("範本", selection: $model.meetingFilter.templateId) {
            Text("不限").tag(String?.none)
            Divider()
            ForEach(model.templates) { template in
                Text(template.displayName).tag(Optional(template.id))
            }
        }
        .pickerStyle(.menu)
        if model.meetingFilter.isActive {
            Divider()
            Button("清除篩選") { model.meetingFilter = MeetingFilter() }
        }
    }
}

/// 會議那一段的標題：「所有會議」或資料夾名稱、分組方式，右邊「顯示方式」按鈕；篩選中按鈕變實心（design §71）
struct MeetingListHeader: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        HStack(spacing: 6) {
            // 名稱是收合的按鈕（R101、design §75）；右邊的「顯示方式」不動
            SidebarSectionHeader(title: title, key: "meetings", besideMenu: true)
            Spacer(minLength: 4)
            Menu {
                MeetingListMenu(model: model)
            } label: {
                Image(systemName: model.meetingFilter.isActive ? "line.3.horizontal.decrease.circle.fill" : "line.3.horizontal.decrease.circle")
                    .foregroundStyle(model.meetingFilter.isActive ? Color.celadon : .secondary)
            }
            .menuStyle(.borderlessButton)
            .menuIndicator(.hidden)
            // 無邊框的選單按鈕用強調色畫圖示：沒篩選時是灰的，篩選中才是青瓷綠
            .tint(model.meetingFilter.isActive ? Color.celadon : Color.secondary)
            .fixedSize()
            .help("排序、分組、篩選會議")
            .accessibilityLabel(Text("顯示方式"))
        }
    }

    private var title: String {
        // 下面只放資料夾以外的會議時叫「其他會議」（R101.6）
        let scope = model.seriesById(model.seriesFilter)?.name ?? model.folderFilter
            ?? (model.listShowsOthersOnly ? String(localized: "其他會議") : String(localized: "所有會議"))
        let grouping = model.effectiveGrouping
        return grouping == .none ? scope : scope + "・" + grouping.label
    }
}

/// 篩選中的那一列（列表最上面）：「篩選：有待確認、王小明 有參加」，✕ 清掉（R97.3）
struct MeetingFilterBar: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 6) {
            Image(systemName: "line.3.horizontal.decrease").foregroundStyle(Color.celadon)
            Text("篩選：\(model.filterSummary)")
                .lineLimit(2)
                .frame(maxWidth: .infinity, alignment: .leading)
            Button { model.meetingFilter = MeetingFilter() } label: {
                Image(systemName: "xmark.circle.fill").foregroundStyle(.secondary)
            }
            .buttonStyle(.borderless)
            .help("清除篩選")
            .accessibilityLabel(Text("清除篩選"))
        }
        .font(.callout)
        .selectionDisabled()
    }
}

/// 篩選後沒有會議（R97.3）：說明、清除篩選
struct MeetingFilterEmpty: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("沒有符合篩選的會議").foregroundStyle(.secondary)
            Button("清除篩選") { model.meetingFilter = MeetingFilter() }
                .buttonStyle(.borderless)
                .foregroundStyle(Color.celadon)
        }
        .font(.callout)
        .padding(.vertical, 4)
        .selectionDisabled()
    }
}
