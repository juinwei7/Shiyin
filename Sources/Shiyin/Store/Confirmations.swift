import Foundation

/// 要確認的事（R106、design §80）：原本各自的狀態（confirmDeleteFolder 這些）照舊，主視窗依目前設了哪一個顯示同一種確認彈窗
extension AppModel {
    var activeConfirmation: ConfirmRequest? {
        if let folder = confirmDeleteFolder {
            let count = meetings.filter { $0.meeting.folderId == folder }.count
            return ConfirmRequest(id: "folder:\(folder)", title: String(localized: "刪除資料夾「\(folderName(folder))」？"),
                                  message: count > 0 ? String(localized: "裡面的 \(count) 場會議會留著，移到「不在資料夾」。自動加入的條件也會刪掉。")
                                                     : String(localized: "自動加入的條件也會刪掉。"),
                                  confirmTitle: String(localized: "刪除資料夾")) { self.deleteFolder(folder) }
        }
        if let id = confirmRemovePerson {
            let name = peopleOverview.first { $0.id == id }?.name ?? ""
            return ConfirmRequest(id: "person:\(id)", title: String(localized: "移除「\(name)」？"),
                                  message: String(localized: "會從「人物」拿掉，也會刪掉他的聲音特徵。會議裡已經帶入的名字會留著，之後的會議不會再自動認出他。"),
                                  confirmTitle: String(localized: "移除")) {
                if self.selectedPerson == id { self.selectedPerson = nil }
                self.deletePerson(id)
            }
        }
        if let ids = confirmPurge {
            return ConfirmRequest(id: "purge:\(ids)", title: ids.count > 1 ? String(localized: "永久刪除 \(ids.count) 場會議？")
                                                                         : String(localized: "永久刪除這場會議？"),
                                  message: String(localized: "會議紀錄、逐字稿和錄音都會刪掉，沒辦法還原。"),
                                  confirmTitle: String(localized: "永久刪除")) { self.deletePermanently(ids) }
        }
        if confirmEmptyTrash {
            return ConfirmRequest(id: "emptyTrash", title: String(localized: "永久刪除垃圾桶裡的 \(trash.count) 場會議？"),
                                  message: String(localized: "會議紀錄、逐字稿和錄音都會刪掉，沒辦法還原。"),
                                  confirmTitle: String(localized: "清空垃圾桶")) { self.emptyTrash() }
        }
        if let segment = splitMeetingRequest {
            let time = detail?.segments.first { $0.id == segment }.map { TimeFormat.string($0.start) } ?? ""
            return ConfirmRequest(id: "split:\(segment)", title: String(localized: "從 \(time) 這句開始分成另一場會議？"),
                                  message: String(localized: "這句和之後的逐字稿、錄音、標記重點、畫面變成新的一場，前面的留在這場；我的筆記、參考文件、提問留在這場。兩場都要重新整理會議紀錄。30 天內可以從「更多」還原。"),
                                  confirmTitle: String(localized: "分成兩場"), destructive: false) { self.splitMeeting(at: segment) }
        }
        if let id = confirmRestoreEdit {
            return ConfirmRequest(id: "restoreEdit:\(id)", title: String(localized: "還原成編輯前的樣子？"), message: restoreEditMessage(id),
                                  confirmTitle: String(localized: "還原"), destructive: false) { self.restoreMeetingEdit(id) }
        }
        if let id = confirmDeleteScreen, let at = detail?.screens.first(where: { $0.id == id })?.at {
            return ConfirmRequest(id: "screen:\(id)", title: String(localized: "刪除 \(TimeFormat.string(at)) 的畫面？"),
                                  message: String(localized: "圖片和辨識出的文字都會刪掉。已經整理好的會議紀錄要重新整理才會拿掉這張的內容。"),
                                  confirmTitle: String(localized: "刪除")) { self.deleteScreen(id) }
        }
        if confirmDelete {
            let ids = deleteTargets
            return ConfirmRequest(id: "trash:\(ids)", title: ids.count > 1 ? String(localized: "把 \(ids.count) 場會議移到垃圾桶？")
                                                                         : String(localized: "把這場會議移到垃圾桶？"),
                                  message: String(localized: "30 天內都可以從垃圾桶還原。"),
                                  confirmTitle: String(localized: "移到垃圾桶")) { self.moveToTrash(ids) }
        }
        return nil
    }

    /// 確認彈窗關掉（取消或做完）
    func clearConfirmations() {
        if confirmDeleteFolder != nil { confirmDeleteFolder = nil }
        if confirmRemovePerson != nil { confirmRemovePerson = nil }
        if confirmPurge != nil { confirmPurge = nil }
        if confirmEmptyTrash { confirmEmptyTrash = false }
        if splitMeetingRequest != nil { splitMeetingRequest = nil }
        if confirmRestoreEdit != nil { confirmRestoreEdit = nil }
        if confirmDelete { confirmDelete = false }
        if confirmDeleteScreen != nil { confirmDeleteScreen = nil }
    }
}
