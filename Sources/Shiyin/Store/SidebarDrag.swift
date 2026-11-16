import AppKit
import UniformTypeIdentifiers

// MARK: - 拖曳（R103、design §77）

extension UTType {
    /// 拖的會議（id 用逗號分開），只在拾音裡面（Info.plist 宣告）
    static let shiyinMeetingIDs = UTType(exportedAs: "com.weiwei.shiyin.meeting-ids")
    /// 拖的資料夾、定期會議（鍵：folder:名稱、series:id）
    static let shiyinSidebarItem = UTType(exportedAs: "com.weiwei.shiyin.sidebar-item")
}

/// 側欄「資料夾」那一段的一列：定期會議或資料夾
enum SidebarFolderItem: Identifiable {
    case series(MeetingSeries)
    case folder(name: String, count: Int)

    var key: String {
        switch self {
        case .series(let series): AppModel.seriesKey(series.id ?? 0)
        case .folder(let name, _): AppModel.folderKey(name)
        }
    }
    var id: String { key }
}

extension AppModel {
    /// 「資料夾」那一段的順序：拖曳排過的照 sidebarOrder，沒排過的（新的）在後面，照預設（定期會議在前、資料夾照名稱）
    var sidebarFolderItems: [SidebarFolderItem] {
        let defaults = series.map(SidebarFolderItem.series) + folders.map { .folder(name: $0.name, count: $0.count) }
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

    /// 把一個資料夾或定期會議拖到另一個的前面或後面
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

    func meetingDragProvider(for id: Int64) -> NSItemProvider {
        let data = Data(dragIDs(for: id).map(String.init).joined(separator: ",").utf8)
        let provider = NSItemProvider()
        provider.registerDataRepresentation(forTypeIdentifier: UTType.shiyinMeetingIDs.identifier, visibility: .ownProcess) { done in
            done(data, nil)
            return nil
        }
        return provider
    }

    /// 會議拖到下面列表的標題：移進現在看的地方（資料夾、定期會議）；「其他會議」是移出資料夾和定期會議
    func dropOnListHeader(_ ids: [Int64]) {
        if let folder = folderFilter {
            moveToFolder(ids, folder: folder)
        } else if let id = seriesFilter {
            setSeries(ids, series: id)
        } else if listShowsOthersOnly {
            let inSeries = ids.filter { id in meetings.first { $0.id == id }?.meeting.seriesId != nil }
            let inFolder = ids.filter { id in !(meetings.first { $0.id == id }?.meeting.folder.isEmpty ?? true) }
            if !inSeries.isEmpty { setSeries(inSeries, series: nil) }
            if !inFolder.isEmpty { moveToFolder(inFolder, folder: "") }
            if inSeries.isEmpty && inFolder.isEmpty {
                noticeMessage = ids.count == 1 ? String(localized: "這場會議沒有在任何資料夾裡") : String(localized: "這些會議都沒有在資料夾裡")
            } else if inFolder.isEmpty {
                noticeMessage = String(localized: "已移出定期會議")
            }
        }
    }

    /// 下面列表的標題能不能收會議（看所有會議、有篩選時不行）
    var listHeaderAcceptsMeetings: Bool {
        !showTrash && (folderFilter != nil || seriesFilter != nil || listShowsOthersOnly)
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
