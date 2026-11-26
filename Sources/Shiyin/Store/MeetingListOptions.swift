import Foundation

/// 會議列表的排序（R97.2、design §71）：記住
enum MeetingSort: String, CaseIterable, Identifiable {
    case dateDesc, dateAsc, title, duration
    var id: String { rawValue }

    var label: String {
        switch self {
        case .dateDesc: return String(localized: "日期（新到舊）")
        case .dateAsc: return String(localized: "日期（舊到新）")
        case .title: return String(localized: "標題")
        case .duration: return String(localized: "長度（長的在前）")
        }
    }
}

/// 會議列表的分組（R97.2）：記住（以前存的 series 讀成不分組）
enum MeetingGrouping: String, CaseIterable, Identifiable {
    case none, month, folder
    var id: String { rawValue }

    var label: String {
        switch self {
        case .none: return String(localized: "不分組")
        case .month: return String(localized: "依月份")
        case .folder: return String(localized: "依資料夾")
        }
    }
}

/// 會議列表的篩選（R97.3）：不記住，都要符合
struct MeetingFilter: Equatable {
    var needsReview = false
    var myActionItems = false
    var personId: Int64?
    var templateId: String?

    var isActive: Bool { needsReview || myActionItems || personId != nil || templateId != nil }
}

/// 分組後的一段：標題空的是不分組
struct MeetingGroup: Identifiable {
    var id: String
    var title: String
    var rows: [MeetingRow]
}

extension AppModel {
    /// 「我」的名字（R80）：「有我的待辦的」用；沒有設定「我」是 nil
    var myName: String? { peopleOverview.first(where: \.isMe)?.name }

    /// 實際用的分組：看某個資料夾時不依資料夾分組（design §71）
    var effectiveGrouping: MeetingGrouping {
        switch meetingGrouping {
        case .folder where folderFilter != nil: return .none
        default: return meetingGrouping
        }
    }

    /// 選單裡可以選的分組
    var availableGroupings: [MeetingGrouping] {
        MeetingGrouping.allCases.filter { $0 != .folder || folderFilter == nil }
    }

    func matches(_ row: MeetingRow, _ filter: MeetingFilter) -> Bool {
        if filter.needsReview && row.reviewCount == 0 { return false }
        if let id = filter.templateId, row.meeting.templateId != id { return false }
        if let person = filter.personId, meetingPeople[row.id]?.contains(person) != true { return false }
        if filter.myActionItems {
            // 負責人跟名字一樣或包含名字（不分大小寫，同人物頁 R85.3）
            guard let name = myName?.trimmingCharacters(in: .whitespacesAndNewlines), !name.isEmpty,
                  row.meeting.decodedNotes?.actionItems.contains(where: { $0.owner.range(of: name, options: .caseInsensitive) != nil }) == true
            else { return false }
        }
        return true
    }

    func sorted(_ rows: [MeetingRow]) -> [MeetingRow] {
        switch meetingSort {
        case .dateDesc: return rows   // 資料庫照 meeting_date DESC, id DESC 讀出來
        case .dateAsc: return rows.reversed()
        case .title:
            return rows.sorted { $0.meeting.displayTitle.localizedStandardCompare($1.meeting.displayTitle) == .orderedAscending }
        case .duration:
            return rows.sorted { ($0.meeting.durationSeconds ?? -1) > ($1.meeting.durationSeconds ?? -1) }
        }
    }

    /// 列表的分組（R97.2）：月份照日期新到舊（舊到新排序時反過來）；資料夾照名稱，最後是「不在資料夾」；每一段裡照排序
    var meetingGroups: [MeetingGroup] {
        let rows = filteredMeetings
        guard !showTrash else { return [MeetingGroup(id: "all", title: "", rows: rows)] }
        switch effectiveGrouping {
        case .none:
            return [MeetingGroup(id: "all", title: "", rows: rows)]
        case .month:
            let keys = Set(rows.map { String($0.meeting.meetingDate.prefix(7)) }).sorted(by: meetingSort == .dateAsc ? (<) : (>))
            return keys.map { key in
                MeetingGroup(id: "month-" + key, title: Self.monthTitle(key), rows: rows.filter { $0.meeting.meetingDate.hasPrefix(key) })
            }
        case .folder:
            // 不在資料夾的放最後，其他照名字；組的 id 用資料夾 id（R118，改名不會讓收合的狀態跑掉）
            let folders = Set(rows.map(\.meeting.folderId)).sorted { a, b in
                (a == nil) != (b == nil) ? a != nil : folderName(a).localizedStandardCompare(folderName(b)) == .orderedAscending
            }
            return folders.map { folder in
                MeetingGroup(id: folder.map { "folder-\($0)" } ?? "folder-none", title: folder == nil ? String(localized: "不在資料夾") : folderName(folder),
                             rows: rows.filter { $0.meeting.folderId == folder })
            }
        }
    }

    /// 「2026-11」→「2026 年 11 月」（介面語言）
    static func monthTitle(_ key: String) -> String {
        let parts = key.split(separator: "-").compactMap { Int($0) }
        guard parts.count == 2, let date = Calendar(identifier: .gregorian).date(from: DateComponents(year: parts[0], month: parts[1], day: 1))
        else { return key }
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: MeetingLanguage.interface == .en ? "en_US" : "zh_TW")
        formatter.setLocalizedDateFormatFromTemplate("yMMMM")
        let text = formatter.string(from: date)
        return MeetingLanguage.simplifiedInterface ? ChineseScript.toSimplifiedUI(text) : text
    }

    /// 篩選的說明（列表上方那一列）
    var filterSummary: String {
        var parts: [String] = []
        if meetingFilter.needsReview { parts.append(String(localized: "有待確認")) }
        if meetingFilter.myActionItems { parts.append(String(localized: "有我的待辦")) }
        if let id = meetingFilter.personId, let name = peopleOverview.first(where: { $0.id == id })?.name {
            parts.append(String(localized: "\(name) 有參加"))
        }
        if let id = meetingFilter.templateId, let template = templates.first(where: { $0.id == id }) {
            parts.append(String(localized: "範本：\(template.displayName)"))
        }
        return parts.joined(separator: MeetingLanguage.ui(zh: "、", en: ", "))
    }
}
