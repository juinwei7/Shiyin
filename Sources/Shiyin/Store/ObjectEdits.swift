import Foundation

/// 每一種東西都能直接改、刪（R100、design §74）
extension AppModel {
    /// 重新命名一場會議：選那場、打開會議頁「編輯」的輸入框
    func requestRenameMeeting(_ id: Int64) {
        sidebarMode = .meetings
        if selection != id { selection = id }
        renameMeetingRequest = id
    }

    /// 紀錄能不能就地改：有紀錄、不在垃圾桶、沒在錄這場、沒在整理（同「編輯」按鈕）
    var canEditNotes: Bool {
        guard let detail, detail.notes != nil, detail.meeting.deletedAt == nil else { return false }
        return detailStatus?.isActive != true && recorder.meetingId != detail.meeting.id
    }

    /// 就地改一條紀錄（雙擊、右鍵「編輯這一條」）：存成一個版本，同手動編輯
    func editNoteItem(_ ref: NoteItemRef, text: String) {
        editingNoteItem = nil
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard canEditNotes, let id = detail?.meeting.id, var notes = detail?.notes, !trimmed.isEmpty,
              notes.text(at: ref) != trimmed else { return }
        notes.setText(trimmed, at: ref)
        saveEditedNotes(notes, meetingId: id)
    }

    /// 刪除一條紀錄：不確認，可以用「復原上一版會議紀錄」還原
    func deleteNoteItem(_ ref: NoteItemRef) {
        guard canEditNotes, let id = detail?.meeting.id, var notes = detail?.notes, notes.text(at: ref) != nil else { return }
        if editingNoteItem == ref { editingNoteItem = nil }
        notes.remove(at: ref)
        saveEditedNotes(notes, meetingId: id)
        noticeMessage = String(localized: "已刪除一條紀錄，按「編輯」旁的「復原上一版」可以還原")
    }

    func renameBookmark(_ id: Int64, label: String) {
        perform(.detail([.bookmarks])) { try db.renameBookmark(id, label: label.trimmingCharacters(in: .whitespacesAndNewlines)) }
    }

    func renameTerm(_ id: Int64, to term: String) { perform { try db.renameTerm(id, to: term) } }

    // MARK: 側欄的鍵盤：照現在選到、亮起來的是什麼

    /// Return：重新命名。回傳有沒有處理
    func sidebarReturn() -> Bool {
        guard !showTrash else { return false }
        if sidebarMode == .people {
            guard let id = selectedPerson else { return false }
            personRenameRequest = id
            return true
        }
        if selectedMeetings.count > 1 { return false }
        if let id = selection {
            requestRenameMeeting(id)
        } else if let id = seriesFilter {
            renamingSeries = id
        } else if let folder = folderFilter {
            folderPrompt = .rename(folder)
        } else {
            return false
        }
        return true
    }

    /// Delete：刪除或移除（都先確認）
    func sidebarDelete() {
        if sidebarMode == .people && !showTrash {
            if let id = selectedPerson { confirmRemovePerson = id }
            return
        }
        let ids = multiSelected.isEmpty ? [selection].compactMap { $0 } : multiSelected
        if !ids.isEmpty {
            if showTrash {
                confirmPurge = ids
            } else {
                deleteTargets = ids
                confirmDelete = true
            }
        } else if !showTrash, let id = seriesFilter {
            confirmDeleteSeries = id
        } else if !showTrash, let folder = folderFilter {
            confirmDeleteFolder = folder
        }
    }
}
