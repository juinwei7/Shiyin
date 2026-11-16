import AppKit

/// 「⋯」和右鍵選單的內容（R105、design §79）：每一種東西一份，自己畫的選單（FloatingMenu）用。
/// 工具列的「更多」照舊是系統選單（ObjectActions 的 SwiftUI 版本）
extension AppModel {
    /// 側欄的會議（一場或多選的好幾場）
    func meetingMenu(_ ids: [Int64]) -> [MenuEntry] {
        if showTrash {
            return [.action(String(localized: "還原"), icon: "arrow.uturn.backward") { self.restore(ids) },
                    .action(String(localized: "永久刪除…"), icon: "trash", destructive: true) { self.confirmPurge = ids }]
        }
        guard !ids.isEmpty else { return [] }
        var entries: [MenuEntry] = []
        if ids.count == 1, let id = ids.first {
            entries += [.action(String(localized: "重新命名…"), icon: "pencil", shortcut: "↩") { self.requestRenameMeeting(id) }, .divider]
        }
        entries.append(.submenu(String(localized: "移到資料夾"), icon: "folder", folderMoveEntries(ids)))
        entries.append(.divider)
        if ids.count == 1 {
            entries.append(.action(String(localized: "移到垃圾桶"), icon: "trash", shortcut: "⌘⌫", destructive: true) { self.moveToTrash(ids) })
        } else {
            entries.append(.action(String(localized: "把 \(ids.count) 場會議移到垃圾桶…"), icon: "trash", destructive: true) {
                self.deleteTargets = ids
                self.confirmDelete = true
            })
        }
        return entries
    }

    /// 「移到資料夾 ›」：別的資料夾、新資料夾、移出
    private func folderMoveEntries(_ ids: [Int64]) -> [MenuEntry] {
        let current = commonFolder(ids)
        let targets = folders.filter { current == nil || $0.name != current }
        var entries: [MenuEntry] = targets.map { f in .action(f.name, icon: "folder") { self.moveToFolder(ids, folder: f.name) } }
        if !entries.isEmpty { entries.append(.divider) }
        entries.append(.action(String(localized: "新資料夾…"), icon: "folder.badge.plus") { self.renameTarget = .newFolder(ids) })
        if current == nil {
            entries.append(.action(String(localized: "移出資料夾"), icon: "minus.circle") { self.moveToFolder(ids, folder: "") })
        } else if let current, !current.isEmpty {
            entries.append(.action(String(localized: "移出「\(current)」"), icon: "minus.circle") { self.moveToFolder(ids, folder: "") })
        }
        return entries
    }

    func folderMenu(_ folder: String) -> [MenuEntry] {
        [.action(String(localized: "重新命名…"), icon: "pencil") { self.renameTarget = .folder(folder) },
         .action(folderRule(folder) == nil ? String(localized: "自動加入的條件…") : String(localized: "編輯自動加入的條件…"), icon: "gearshape") {
             self.editingFolderRule = folder
         },
         .divider,
         .action(String(localized: "刪除資料夾…"), icon: "trash", destructive: true) { self.confirmDeleteFolder = folder }]
    }

    func personMenu(_ person: PersonOverview) -> [MenuEntry] {
        let others = peopleOverview.filter { $0.id != person.id }
        var entries: [MenuEntry] = [.action(String(localized: "重新命名…"), icon: "pencil", shortcut: "↩") {
            self.selectedPerson = person.id
            self.renameTarget = .person(person.id)
        }]
        if !others.isEmpty {
            entries.append(.submenu(String(localized: "合併到"), icon: "person.2", others.map { other in
                .action(other.name) {
                    self.renamePerson(person.id, to: other.name)
                    self.selectedPerson = other.id
                }
            }))
        }
        entries += [.divider,
                    .action(String(localized: "移除這個人…"), icon: "person.crop.circle.badge.minus", destructive: true) { self.confirmRemovePerson = person.id }]
        return entries
    }

    /// 人物頁裡的一場會議：打開
    func meetingLinkMenu(_ meeting: Meeting) -> [MenuEntry] {
        guard let id = meeting.id else { return [] }
        return [.action(String(localized: "打開這場會議"), icon: "arrow.up.forward.app") { self.openFromPersonPage(id) }]
    }

    /// 會議紀錄的一條：拷貝、從這裡播放、編輯、刪除
    func noteItemMenu(_ ref: NoteItemRef, text: String, time: String) -> [MenuEntry] {
        var entries: [MenuEntry] = [.action(String(localized: "拷貝"), icon: "doc.on.doc") {
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(text, forType: .string)
        }]
        if let seconds = TimeFormat.seconds(time), player.isLoaded {
            entries.append(.action(String(localized: "從這裡播放"), icon: "play") { self.player.seek(to: seconds) })
        }
        entries += [.divider,
                    .action(String(localized: "編輯這一條"), icon: "pencil", enabled: canEditNotes) { self.editingNoteItem = ref },
                    .action(String(localized: "刪除這一條"), icon: "trash", destructive: true, enabled: canEditNotes) { self.deleteNoteItem(ref) }]
        return entries
    }
}
