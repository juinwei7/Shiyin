import Foundation

/// 資料夾自動歸類、定期會議（R98、R99、design §72、§73）
extension AppModel {
    /// 找可能的定期會議：不在垃圾桶、不在定期會議裡的會議
    var seriesInputs: [SeriesDetector.Input] {
        meetings.compactMap { row in
            guard row.meeting.seriesId == nil, let time = MeetingTime(row.meeting) else { return nil }
            return SeriesDetector.Input(id: row.id, time: time, people: meetingPeople[row.id] ?? [], title: row.meeting.displayTitle)
        }
    }

    func seriesById(_ id: Int64?) -> MeetingSeries? {
        guard let id else { return nil }
        return series.first { $0.id == id }
    }

    func seriesCount(_ id: Int64) -> Int { meetings.filter { $0.meeting.seriesId == id }.count }

    /// 側欄點了定期會議：只看它的會議，右邊是系列頁
    func openSeries(_ id: Int64) {
        sidebarMode = .meetings
        showTrash = false
        seriesFilter = id
        selection = nil
    }

    // MARK: 定期會議

    func createSeries(_ candidate: SeriesCandidate, name: String, meetingIds: [Int64]) {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        let series = MeetingSeries(name: trimmed.isEmpty ? candidate.name : trimmed, weekday: candidate.weekday, minute: candidate.minute,
                                   intervalWeeks: candidate.intervalWeeks, people: candidate.people)
        perform { try db.createSeries(series, meetingIds: meetingIds) }
    }

    func rejectSeriesCandidate(_ candidate: SeriesCandidate) {
        perform { try db.rejectSeries(candidate.key) }
    }

    func renameSeries(_ id: Int64, to name: String) {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        perform { try db.renameSeries(id, to: trimmed) }
    }

    /// 會議留著；之後不再建議同一組
    func deleteSeries(_ id: Int64) {
        if seriesFilter == id { seriesFilter = nil }
        perform { try db.deleteSeries(id) }
    }

    /// 加到定期會議、移出（series 是 nil）；多選時一起（手動加入不檢查條件）
    func setSeries(_ ids: [Int64], series: Int64?) {
        guard !ids.isEmpty else { return }
        perform { try db.setSeries(ids, series: series) }
    }

    /// 這幾場共同的定期會議；不同或都沒有時 nil
    func commonSeries(_ ids: [Int64]) -> Int64? {
        let values = Set(ids.map { id in meetings.first { $0.id == id }?.meeting.seriesId })
        return values.count == 1 ? values.first ?? nil : nil
    }

    /// 系列頁：還沒完成的待辦、常參加的人
    func seriesOpenItems(_ id: Int64) -> [PersonActionItem] {
        SeriesOpenItems.compute((try? db.seriesMeetings(id)) ?? [])
    }

    func seriesRegulars(_ id: Int64) -> [PersonOverview] {
        let ids = meetings.filter { $0.meeting.seriesId == id }.map(\.id)
        let core = SeriesDetector.core(ids.map { meetingPeople[$0] ?? [] })
        return peopleOverview.filter { core.contains($0.id) }
    }

    // MARK: 資料夾的條件

    func folderRule(_ folder: String) -> FolderRule? { folderRules.first { $0.folder == folder } }

    /// 儲存條件（都空時移除）；applyNow：現在不在資料夾裡的會議也套用
    func saveFolderRule(_ rule: FolderRule, applyNow: Bool) {
        perform {
            try db.saveFolderRule(rule)
            guard applyNow && !rule.isEmpty else { return }
            let ids = try db.meetingsMatching(rule)
            guard !ids.isEmpty else { return }
            try db.setFolder(ids, folder: rule.folder)
            noticeMessage = String(localized: "已把 \(ids.count) 場會議放進「\(rule.folder)」")
        }
    }

    /// 側欄滑過去看的條件：「標題包含 週會、sync；陳家豪、王小明都參加；範本：一對一；定期會議：產品週會」
    func folderRuleSummary(_ rule: FolderRule) -> String {
        let separator = MeetingLanguage.ui(zh: "、", en: ", ")
        var parts: [String] = []
        if !rule.keywords.isEmpty { parts.append(String(localized: "標題包含 \(rule.keywords.joined(separator: separator))")) }
        let names = rule.people.compactMap { id in peopleOverview.first { $0.id == id }?.name }
        if !names.isEmpty { parts.append(String(localized: "\(names.joined(separator: separator)) 都參加")) }
        if let id = rule.templateId, let template = templates.first(where: { $0.id == id }) {
            parts.append(String(localized: "範本：\(template.displayName)"))
        }
        if let series = seriesById(rule.seriesId) { parts.append(String(localized: "定期會議：\(series.name)")) }
        let joined = parts.joined(separator: MeetingLanguage.ui(zh: "；", en: "; "))
        return String(localized: "自動加入：\(joined)")
    }

    // MARK: 建議放進資料夾（R98.3）

    /// 會議不在資料夾、還沒決定過、已經有會議紀錄、不在錄音中、不在垃圾桶時，跟各個資料夾比
    func folderSuggestion(for meeting: Meeting) -> FolderSuggestion? {
        guard let id = meeting.id, meeting.folder.isEmpty, !meeting.folderDecided, meeting.deletedAt == nil,
              meeting.notes != nil, recorder.meetingId != id else { return nil }
        let input = FolderSuggester.Input(id: id, folder: "", title: meeting.displayTitle, people: meetingPeople[id] ?? [])
        let others = meetings.map { FolderSuggester.Input(id: $0.id, folder: $0.meeting.folder, title: $0.meeting.displayTitle,
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

    func acceptFolderSuggestion(_ meetingId: Int64, folder: String) {
        moveToFolder([meetingId], folder: folder)
    }

    /// 「不用」：之後不再建議這場
    func dismissFolderSuggestion(_ meetingId: Int64) {
        perform { try db.setFolderDecided(meetingId) }
    }

    // MARK: 自動（轉錄完、整理完）

    func organized(_ meetingId: Int64, folder: String?, series: MeetingSeries?) {
        reload()
        let title = (try? db.meeting(meetingId))?.displayTitle ?? ""
        if let folder {
            noticeMessage = String(localized: "「\(title)」已放進「\(folder)」")
        } else if let series {
            noticeMessage = String(localized: "「\(title)」已加到定期會議「\(series.name)」")
        }
    }
}
