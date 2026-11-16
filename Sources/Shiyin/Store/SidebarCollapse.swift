import Foundation

// MARK: - 側欄的收合（R101、design §75）

extension AppModel {
    static func folderKey(_ name: String) -> String { "folder:" + name }
    static func seriesKey(_ id: Int64) -> String { "series:\(id)" }
    static func groupKey(_ group: MeetingGroup) -> String { "group:" + group.id }

    /// 段落（series、folders、meetings、people、group:…）展開了沒；預設展開
    func isSectionExpanded(_ key: String) -> Bool { !sidebarCollapsed.contains(key) }

    func setSection(_ key: String, expanded: Bool) {
        // 值真的變了才設：觀察的屬性一設就重排整個側欄（§21.12）
        if expanded, sidebarCollapsed.contains(key) { sidebarCollapsed.remove(key) }
        if !expanded, !sidebarCollapsed.contains(key) { sidebarCollapsed.insert(key) }
    }

    /// 資料夾、定期會議在原地展開了沒；預設收起
    func isRowExpanded(_ key: String) -> Bool { sidebarExpanded.contains(key) }

    func setRow(_ key: String, expanded: Bool) {
        if expanded, !sidebarExpanded.contains(key) { sidebarExpanded.insert(key) }
        if !expanded, sidebarExpanded.contains(key) { sidebarExpanded.remove(key) }
    }

    /// 點資料夾、定期會議：在原地展開或收起（R101.2）
    func toggleRow(_ key: String) { setRow(key, expanded: !isRowExpanded(key)) }

    /// 下面的列表只看這個資料夾（「顯示全部 N 場」、右鍵「只看這個資料夾」）
    func showFolder(_ name: String) {
        sidebarMode = .meetings
        folderFilter = name
    }

    /// 資料夾、定期會議裡的會議（不含垃圾桶），日期新到舊（資料庫讀出來的順序）
    func peekRows(folder: String) -> [MeetingRow] { meetings.filter { $0.meeting.folder == folder } }
    func peekRows(series id: Int64) -> [MeetingRow] { meetings.filter { $0.meeting.seriesId == id } }

    /// 點了展開的那一場：人物模式換回會議、選那場；下面的列表不換
    func openFromSidebar(_ id: Int64) {
        sidebarMode = .meetings
        selection = id
    }

    /// 程式帶你去某場會議時（reveal），展開會議列表和那場所在的那一組，看得到它在哪裡
    func expandToShow(_ id: Int64) {
        setSection("meetings", expanded: true)
        if let group = meetingGroups.first(where: { group in group.rows.contains { $0.id == id } }) {
            setSection(Self.groupKey(group), expanded: true)
        }
    }
}
