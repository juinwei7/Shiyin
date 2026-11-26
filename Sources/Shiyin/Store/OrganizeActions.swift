import Foundation

/// 資料夾自動歸類（R98、design §72）
extension AppModel {
    // MARK: 資料夾的條件

    func folderRule(_ folderId: Int64) -> FolderRule? { folderRules.first { $0.folderId == folderId } }

    /// 儲存條件（都空時移除）；applyNow：現在不在資料夾裡的會議也套用
    func saveFolderRule(_ rule: FolderRule, applyNow: Bool) {
        perform {
            try db.saveFolderRule(rule)
            guard applyNow && !rule.isEmpty else { return }
            let ids = try db.meetingsMatching(rule)
            guard !ids.isEmpty else { return }
            try db.setFolder(ids, folderId: rule.folderId)
            noticeMessage = String(localized: "已把 \(ids.count) 場會議放進「\(folderName(rule.folderId))」")
        }
    }

    /// 側欄滑過去看的條件：「標題包含 週會、sync；陳家豪、王小明都參加；範本：一對一」
    func folderRuleSummary(_ rule: FolderRule) -> String {
        let separator = MeetingLanguage.ui(zh: "、", en: ", ")
        var parts: [String] = []
        if !rule.keywords.isEmpty { parts.append(String(localized: "標題包含 \(rule.keywords.joined(separator: separator))")) }
        let names = rule.people.compactMap { id in peopleOverview.first { $0.id == id }?.name }
        if !names.isEmpty { parts.append(String(localized: "\(names.joined(separator: separator)) 都參加")) }
        if let id = rule.templateId, let template = templates.first(where: { $0.id == id }) {
            parts.append(String(localized: "範本：\(template.displayName)"))
        }
        let joined = parts.joined(separator: MeetingLanguage.ui(zh: "；", en: "; "))
        return String(localized: "自動加入：\(joined)")
    }

    // MARK: 建議放進資料夾（R98.3）

    /// 會議不在資料夾、還沒決定過、已經有會議紀錄、不在錄音中、不在垃圾桶時，跟各個資料夾比
    func folderSuggestion(for meeting: Meeting) -> FolderSuggestion? {
        guard let id = meeting.id, meeting.folderId == nil, !meeting.folderDecided, meeting.deletedAt == nil,
              meeting.notes != nil, recorder.meetingId != id else { return nil }
        let input = FolderSuggester.Input(id: id, folderId: nil, folder: "", title: meeting.displayTitle, people: meetingPeople[id] ?? [])
        let others = meetings.map { FolderSuggester.Input(id: $0.id, folderId: $0.meeting.folderId, folder: folderName($0.meeting.folderId), title: $0.meeting.displayTitle,
                                                          people: meetingPeople[$0.id] ?? []) }
        return FolderSuggester.best(for: input, among: others)
    }

    func folderSuggestionText(_ suggestion: FolderSuggestion) -> String {
        switch suggestion.reason {
        case .people(let ids):
            let names = ids.compactMap { id in peopleOverview.first { $0.id == id }?.name }
            let shown = names.prefix(3).joined(separator: MeetingLanguage.ui(zh: "、", en: ", "))
            return String(localized: "這場很像「\(suggestion.folder)」裡的會議：\(shown) 都參加（\(suggestion.count) 場）")
        case .title(let word):
            return String(localized: "這場很像「\(suggestion.folder)」裡的會議：標題都有「\(word)」（\(suggestion.count) 場）")
        }
    }

    func acceptFolderSuggestion(_ meetingId: Int64, folderId: Int64) {
        moveToFolder([meetingId], folderId: folderId)
    }

    /// 「不用」：之後不再建議這場
    func dismissFolderSuggestion(_ meetingId: Int64) {
        perform { try db.setFolderDecided(meetingId) }
    }

    // MARK: 自動（轉錄完、整理完）

    func organized(_ meetingId: Int64, folderId: Int64?) {
        reload()
        let title = (try? db.meeting(meetingId))?.displayTitle ?? ""
        if let folderId { noticeMessage = String(localized: "「\(title)」已放進「\(folderName(folderId))」") }
    }
}
