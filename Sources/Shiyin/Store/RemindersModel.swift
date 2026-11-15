import AppKit
import Foundation

/// 待辦加進「提醒事項」（design §52、R76）
extension AppModel {
    /// 紀錄分頁「待辦」旁的按鈕：把這場會議的待辦加進「拾音」列表，加過的不重複加（R76.3）
    func addActionItemsToReminders() {
        guard let detail, let notes = detail.notes, !notes.actionItems.isEmpty, !addingReminders else { return }
        let meeting = detail.meeting
        addingReminders = true
        Task {
            defer { addingReminders = false }
            let store = Reminders.store
            if store.access == .notDetermined { _ = await store.requestAccess() }
            guard store.access == .granted else {
                showRemindersDenied()
                return
            }
            do {
                let list = try store.list()
                let existing = try await store.existing(meetingId: meeting.id ?? 0)
                let items = Reminders.items(meeting: meeting, notes: notes).filter { existing[$0.key] == nil }
                if items.isEmpty {
                    noticeMessage = String(localized: "這場的待辦都已經在提醒事項了")
                } else {
                    try store.add(items, to: list)
                    noticeMessage = String(localized: "已加到提醒事項的「\(Reminders.listTitle)」列表：\(items.count) 項")
                }
                if self.detail?.meeting.id == meeting.id { reminderStatus = try await store.existing(meetingId: meeting.id ?? 0) }
            } catch {
                errorMessage = String(localized: "沒辦法加到提醒事項：\(error.localizedDescription)")
            }
        }
    }

    /// 打開會議、回到前景時讀一次（R76.4）。還沒允許過就不讀，不會因此跳出權限詢問
    func refreshReminderStatus() {
        guard let id = detail?.meeting.id else {
            reminderStatus = [:]
            return
        }
        let store = Reminders.store
        guard store.access == .granted else {
            if !reminderStatus.isEmpty { reminderStatus = [:] }
            return
        }
        Task {
            let status = (try? await store.existing(meetingId: id)) ?? [:]
            if detail?.meeting.id == id, status != reminderStatus { reminderStatus = status }
        }
    }

    /// 拒絕或之前拒絕過（R76.5）
    private func showRemindersDenied() {
        let alert = NSAlert()
        alert.messageText = String(localized: "拾音沒有使用提醒事項的權限")
        alert.informativeText = String(localized: "請到「系統設定 → 隱私權與安全性 → 提醒事項」允許拾音，再按一次「加到提醒事項」。")
        alert.addButton(withTitle: String(localized: "打開系統設定"))
        alert.addButton(withTitle: String(localized: "取消"))
        if alert.runModal() == .alertFirstButtonReturn { Recorder.openPrivacySettings("Privacy_Reminders") }
    }
}
