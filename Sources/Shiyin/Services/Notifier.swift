import Foundation
import UserNotifications

/// 系統通知（design §10、R12）。沒有授權就不發，其他功能不受影響。
@MainActor
final class Notifier: NSObject, UNUserNotificationCenterDelegate {
    var onOpen: ((Int64) -> Void)?
    /// 偵測到會議的通知按鈕（design §21.7）：動作（start、mute、stop、continue）、哪個 app
    var onMeetingAction: ((String, String) -> Void)?
    #if DEBUG
    /// 驗證用：最近要發的通知內容（沒有通知權限時也記錄）
    private(set) var recent: [[String: Any]] = []
    #endif

    func activate() {
        UNUserNotificationCenter.current().delegate = self
        let start = UNNotificationCategory(identifier: "MEETING_START", actions: [
            UNNotificationAction(identifier: "start", title: String(localized: "開始錄音"), options: []),
            UNNotificationAction(identifier: "mute", title: String(localized: "不要再提醒這個 app"), options: []),
        ], intentIdentifiers: [])
        let stop = UNNotificationCategory(identifier: "MEETING_STOP", actions: [
            UNNotificationAction(identifier: "stop", title: String(localized: "停止並轉錄"), options: []),
            UNNotificationAction(identifier: "continue", title: String(localized: "繼續錄"), options: []),
        ], intentIdentifiers: [])
        UNUserNotificationCenter.current().setNotificationCategories([start, stop])
    }

    func isAuthorized() async -> Bool {
        let status = await UNUserNotificationCenter.current().notificationSettings().authorizationStatus
        return status == .authorized || status == .provisional
    }

    /// 偵測到會議（design §21.7）
    func meetingPrompt(_ prompt: MeetingDetector.Prompt) {
        let content = UNMutableNotificationContent()
        switch prompt {
        case .start(let app):
            content.title = String(localized: "\(app.name) 正在開會")
            content.body = String(localized: "要開始錄音嗎？")
            content.categoryIdentifier = "MEETING_START"
            content.userInfo = ["bundle": app.bundleID]
        case .stop(let app):
            content.title = String(localized: "會議好像結束了")
            content.body = String(localized: "要停止錄音並轉錄嗎？")
            content.categoryIdentifier = "MEETING_STOP"
            content.userInfo = ["bundle": app.bundleID]
        }
        content.sound = .default
        post(content)
    }

    /// 收音提醒（R42.1、design §31.5）：內容跟錄音畫面同一句，點了打開錄音中的會議
    func levelHint(_ hint: Recorder.LevelHint, meetingId: Int64) {
        let content = UNMutableNotificationContent()
        content.title = String(localized: "收音提醒")
        content.body = hint.text
        content.categoryIdentifier = "LEVEL_HINT"
        content.sound = .default
        content.userInfo = ["meetingId": NSNumber(value: meetingId)]
        #if DEBUG
        recent = Array(([["title": content.title, "body": content.body, "meetingId": meetingId, "hint": hint.rawValue]] + recent).prefix(10))
        #endif
        post(content)
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
        } else if job.kind == .summarize, let payload = job.payload,
                  (try? JSONDecoder().decode(SummarizePayload.self, from: Data(payload.utf8)))?.draft == true {
            content.title = String(localized: "會議紀錄草稿好了")
            content.body = String(localized: "\(title)（正式轉錄完成後會自動更新）")
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
        let bundle = response.notification.request.content.userInfo["bundle"] as? String
        let action = response.actionIdentifier
        Task { @MainActor in
            if let bundle, action != UNNotificationDefaultActionIdentifier, action != UNNotificationDismissActionIdentifier {
                self.onMeetingAction?(action, bundle)
            } else if let id, id > 0 { self.onOpen?(id) }
            completionHandler()
        }
    }

    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification,
                                            withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void) {
        completionHandler([.banner, .list, .sound])
    }
}
