import Foundation

// MARK: - 側欄的收合（R101、design §75）

extension AppModel {
    /// 資料夾那一列的鍵（R118、design §92.1）：用 id，改名不用搬
    nonisolated static func folderKey(_ id: Int64) -> String { "folderid:\(id)" }
    nonisolated static func groupKey(_ group: MeetingGroup) -> String { "group:" + group.id }

    /// 段落（folders、meetings、people、group:…）展開了沒；預設展開
    func isSectionExpanded(_ key: String) -> Bool { !sidebarCollapsed.contains(key) }

    func setSection(_ key: String, expanded: Bool) {
        // 值真的變了才設：觀察的屬性一設就重排整個側欄（§21.12）
        if expanded, sidebarCollapsed.contains(key) { sidebarCollapsed.remove(key) }
        if !expanded, !sidebarCollapsed.contains(key) { sidebarCollapsed.insert(key) }
    }

    /// 資料夾在原地展開了沒；預設收起
    func isRowExpanded(_ key: String) -> Bool { sidebarExpanded.contains(key) }

    func setRow(_ key: String, expanded: Bool) {
        if expanded, !sidebarExpanded.contains(key) { sidebarExpanded.insert(key) }
        if !expanded, sidebarExpanded.contains(key) { sidebarExpanded.remove(key) }
    }

    /// 點資料夾：在原地展開或收起（R101.2）
    func toggleRow(_ key: String) { setRow(key, expanded: !isRowExpanded(key)) }

    /// 下面的列表只看這個資料夾（展開後的「顯示全部 N 場」）
    func showFolder(_ id: Int64) {
        sidebarMode = .meetings
        folderFilter = id
    }

    /// 資料夾裡的會議（不含垃圾桶），日期新到舊（資料庫讀出來的順序）
    func peekRows(folderId: Int64) -> [MeetingRow] { meetings.filter { $0.meeting.folderId == folderId } }

    /// 點了展開的那一場：人物模式換回會議、選那場；下面的列表不換
    func openFromSidebar(_ id: Int64) {
        sidebarMode = .meetings
        selection = id
    }

    /// 下面的列表只放資料夾以外的會議（R101.6）。
    /// 沒有看某個資料夾、沒有篩選、不是依資料夾分組時才這樣：篩選、依資料夾分組是要跨資料夾找東西。
    /// 還沒有資料夾時就是所有會議
    var listShowsOthersOnly: Bool {
        !showTrash && folderFilter == nil && !meetingFilter.isActive
            && meetingGrouping != .folder && (meetings.contains { $0.meeting.folderId != nil })
    }

    /// 程式帶你去某場會議時（reveal），看得到它在哪裡：在下面的列表就展開列表和那一組；
    /// 在資料夾裡（下面的列表沒有）就展開那個資料夾
    func expandToShow(_ id: Int64) {
        if let group = meetingGroups.first(where: { group in group.rows.contains { $0.id == id } }) {
            setSection("meetings", expanded: true)
            setSection(Self.groupKey(group), expanded: true)
        } else if let meeting = meetings.first(where: { $0.id == id })?.meeting {
            setSection("folders", expanded: true)
            if let folderId = meeting.folderId {
                setRow(Self.folderKey(folderId), expanded: true)
            }
        }
    }
}
