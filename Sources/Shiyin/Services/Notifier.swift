import Foundation
import UserNotifications

/// 系統通知（design §10、R12）。沒有授權就不發，其他功能不受影響。
@MainActor
final class Notifier: NSObject, UNUserNotificationCenterDelegate {
    var onOpen: ((Int64) -> Void)?
    #if DEBUG
    /// 驗證用：最近要發的通知內容（沒有通知權限時也記錄）
    private(set) var recent: [[String: Any]] = []
    #endif

    func activate() {
        UNUserNotificationCenter.current().delegate = self
    }

    func jobFinished(_ job: Job, meeting: Meeting, error: Error?) {
        let content = UNMutableNotificationContent()
        let title = meeting.displayTitle
        if let error {
            content.title = job.kind == .transcribe ? String(localized: "轉錄失敗") : String(localized: "整理失敗")
            content.body = String(localized: "\(title)：\(String(error.localizedDescription.prefix(60)))")
        } else if job.kind == .transcribe {
            content.title = String(localized: "轉錄完成")
            content.body = title
        } else {
            let count = meeting.decodedNotes?.reviewItems.count ?? 0
            content.title = String(localized: "會議紀錄整理好了")
            content.body = count > 0 ? String(localized: "\(title)，有 \(count) 題待確認") : title
        }
        content.sound = .default
        content.userInfo = ["meetingId": NSNumber(value: meeting.id ?? 0)]
        #if DEBUG
        recent = Array(([["title": content.title, "body": content.body, "meetingId": meeting.id ?? 0]] + recent).prefix(10))
        #endif
        post(content)
    }

    private func post(_ content: UNNotificationContent) {
        let center = UNUserNotificationCenter.current()
        Task {
            var settings = await center.notificationSettings()
            if settings.authorizationStatus == .notDetermined {
                _ = try? await center.requestAuthorization(options: [.alert, .sound])
                settings = await center.notificationSettings()
            }
            guard settings.authorizationStatus == .authorized || settings.authorizationStatus == .provisional else { return }
            let request = UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil)
            do { try await center.add(request) } catch { log.error("notification failed: \(error.localizedDescription)") }
        }
    }

    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse,
                                            withCompletionHandler completionHandler: @escaping () -> Void) {
        let id = (response.notification.request.content.userInfo["meetingId"] as? NSNumber)?.int64Value
        Task { @MainActor in
            if let id, id > 0 { self.onOpen?(id) }
            completionHandler()
        }
    }

    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification,
                                            withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void) {
        completionHandler([.banner, .list, .sound])
    }
}
