import AppKit
import UniformTypeIdentifiers

// MARK: - 拖曳（R103、design §77）

extension UTType {
    /// 拖的會議（id 用逗號分開），只在拾音裡面（Info.plist 宣告）
    static let shiyinMeetingIDs = UTType(exportedAs: "com.weiwei.shiyin.meeting-ids")
    /// 拖的資料夾（鍵：folder:名稱）
    static let shiyinSidebarItem = UTType(exportedAs: "com.weiwei.shiyin.sidebar-item")
}

/// 側欄「資料夾」那一段的一列
struct SidebarFolderItem: Identifiable {
    var name: String
    var count: Int

    var key: String { AppModel.folderKey(name) }
    var id: String { key }
}

extension AppModel {
    /// 「資料夾」那一段的順序：拖曳排過的照 sidebarOrder，沒排過的（新的）在後面，照名稱
    var sidebarFolderItems: [SidebarFolderItem] {
        let defaults = folders.map { SidebarFolderItem(name: $0.name, count: $0.count) }
        let rank = Dictionary(sidebarOrder.enumerated().map { ($1, $0) }, uniquingKeysWith: { a, _ in a })
        return defaults.enumerated().sorted { a, b in
            switch (rank[a.element.key], rank[b.element.key]) {
            case let (x?, y?): return x < y
            case (_?, nil): return true
            case (nil, _?): return false
            case (nil, nil): return a.offset < b.offset
            }
        }.map(\.element)
    }

    /// 把一個資料夾拖到另一個的前面或後面
    func moveSidebarItem(_ key: String, to target: String, before: Bool) {
        var keys = sidebarFolderItems.map(\.key)
        guard key != target, let from = keys.firstIndex(of: key) else { return }
        keys.remove(at: from)
        guard let to = keys.firstIndex(of: target) else { return }
        keys.insert(key, at: before ? to : to + 1)
        sidebarOrder = keys
    }

    /// 往上、往下移一格（VoiceOver 的動作）
    func moveSidebarItem(_ key: String, by offset: Int) {
        let keys = sidebarFolderItems.map(\.key)
        guard let i = keys.firstIndex(of: key), keys.indices.contains(i + offset) else { return }
        moveSidebarItem(key, to: keys[i + offset], before: offset < 0)
    }

    /// 拖一場會議時帶的會議：在多選裡就是整個多選（列表的順序）
    func dragIDs(for id: Int64) -> [Int64] {
        let multi = multiSelected
        return multi.count > 1 && multi.contains(id) ? multi : [id]
    }

    /// 點下面列表的會議（design §77）：一般是只選這場；⌘ 加入或拿掉；⇧ 從上一次點的那場選到這場（照列表看到的順序）
    func clickMeetingRow(_ id: Int64, modifiers: NSEvent.ModifierFlags) {
        if modifiers.contains(.command) {
            var ids = listSelection
            if ids.contains(id) { ids.remove(id) } else { ids.insert(id) }
            listSelection = ids
            selectionAnchor = id
        } else if modifiers.contains(.shift), let anchor = selectionAnchor ?? selection {
            let order = meetingGroups.flatMap(\.rows).map(\.id)
            if let a = order.firstIndex(of: anchor), let b = order.firstIndex(of: id) {
                listSelection = Set(order[min(a, b)...max(a, b)])
            } else {
                listSelection = [id]
            }
        } else {
            listSelection = [id]
            selectionAnchor = id
        }
    }

    /// 會議拖到下面列表的標題：移進現在看的資料夾；「其他會議」是移出資料夾
    func dropOnListHeader(_ ids: [Int64]) {
        if let folder = folderFilter {
            moveToFolder(ids, folder: folder)
        } else if listShowsOthersOnly {
            let inFolder = ids.filter { id in !(meetings.first { $0.id == id }?.meeting.folder.isEmpty ?? true) }
            if inFolder.isEmpty {
                noticeMessage = ids.count == 1 ? String(localized: "這場會議沒有在任何資料夾裡") : String(localized: "這些會議都沒有在資料夾裡")
            } else {
                moveToFolder(inFolder, folder: "")
            }
        }
    }

    /// 下面列表的標題能不能收會議（看所有會議、有篩選時不行）
    var listHeaderAcceptsMeetings: Bool {
        !showTrash && (folderFilter != nil || listShowsOthersOnly)
    }

    /// 從 Finder 拖到資料夾上的檔案：匯入到那個資料夾（R103.3）；參考文件（PDF 這類）要拖到會議頁
    func importDropped(_ urls: [URL], into folder: String) {
        let files = urls.filter { !DocumentReader.isDroppedDocument($0) }
        if files.isEmpty {
            errorMessage = String(localized: "參考文件請拖到會議頁，會加進那場會議")
            return
        }
        importFolderOverride = folder
        requestImport(files)
        importFolderOverride = nil
        if showImportSheet { pendingImportFolder = folder }
    }
}
