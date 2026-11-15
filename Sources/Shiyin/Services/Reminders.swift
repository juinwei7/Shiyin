import CryptoKit
import EventKit
import Foundation

/// 要加進提醒事項的一項待辦（design §52.1）
struct ReminderItem: Equatable {
    /// 去重的鍵：事項文字的雜湊（D162），也放在 url 的 item
    var key: String
    var title: String
    var notes: String
    /// 期限是 yyyy-MM-dd 時的到期日（只有年月日，不設時間、不加鬧鐘）
    var due: DateComponents?
    var url: URL
}

enum ReminderAccess: String {
    case granted, denied, notDetermined
}

/// 提醒事項（R76，只有 Mac）。正式的是 EventKit；DEBUG 加 -debugFakeReminders YES 用記憶體裡的假的，
/// 不碰使用者真的提醒事項、不跳出權限詢問（design §52.2）
protocol ReminderStore: AnyObject {
    var access: ReminderAccess { get }
    func requestAccess() async -> Bool
    /// 「拾音」列表的 id；沒有就建立
    func list() throws -> String
    /// 這場會議已經加過的：item 鍵 → 完成了沒
    func existing(meetingId: Int64) async throws -> [String: Bool]
    func add(_ items: [ReminderItem], to list: String) throws
}

enum Reminders {
    @MainActor static let store: ReminderStore = {
        #if DEBUG
        if UserDefaults.standard.bool(forKey: "debugFakeReminders") { return FakeReminders.shared }
        #endif
        return EventKitReminders()
    }()

    /// 事項文字（去掉頭尾空白）的 SHA-256 前 12 個十六進位字
    static func key(_ task: String) -> String {
        let digest = SHA256.hash(data: Data(task.trimmingCharacters(in: .whitespacesAndNewlines).utf8))
        return digest.map { String(format: "%02x", $0) }.joined().prefix(12).description
    }

    /// 提醒事項的 url 是會議連結（R69.5）加上 item：回傳會議 id 和 item 鍵
    static func meetingAndKey(_ url: URL?) -> (meetingId: Int64, key: String)? {
        guard let url, let target = MeetingLink.parse(url),
              let key = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems?.first(where: { $0.name == "item" })?.value
        else { return nil }
        return (target.meetingId, key)
    }

    /// 這場會議的待辦變成提醒事項（design §52.1）。備註依會議語言寫
    static func items(meeting: Meeting, notes: MeetingNotes) -> [ReminderItem] {
        guard let id = meeting.id else { return [] }
        let language = meeting.meetingLanguage
        var seen = Set<String>()
        return notes.actionItems.compactMap { item in
            let task = item.task.trimmingCharacters(in: .whitespacesAndNewlines)
            let key = key(task)
            guard !task.isEmpty, seen.insert(key).inserted else { return nil }
            var lines: [String] = []
            let owner = item.owner.trimmingCharacters(in: .whitespacesAndNewlines)
            if !owner.isEmpty { lines.append(language.format(zh: "負責人：%@", en: "Owner: %@", ja: "担当：%@", owner)) }  // l10n-skip：會議語言
            lines.append(language.format(zh: "會議：%@（%@）", en: "Meeting: %@ (%@)", ja: "会議：%@（%@）",  // l10n-skip：會議語言
                                         meeting.displayTitle, meeting.meetingDate))
            let seconds = TimeFormat.seconds(item.time)
            if let seconds { lines.append(language.format(zh: "時間點：%@", en: "At: %@", ja: "時点：%@", TimeFormat.string(seconds))) }  // l10n-skip：會議語言
            let due = dueDate(item.due)
            let dueText = item.due.trimmingCharacters(in: .whitespacesAndNewlines)
            if due == nil && !dueText.isEmpty { lines.append(language.format(zh: "期限：%@", en: "Due: %@", ja: "期限：%@", dueText)) }  // l10n-skip：會議語言
            var link = "shiyin://open?meeting=\(id)"
            if let seconds { link += "&t=\(Int(max(0, seconds)))" }
            link += "&item=\(key)"
            // 提醒事項的標題是一行：事項裡的換行換成空格
            let title = task.components(separatedBy: .newlines).filter { !$0.isEmpty }.joined(separator: " ")
            return ReminderItem(key: key, title: title, notes: lines.joined(separator: "\n"), due: due, url: URL(string: link)!)
        }
    }

    /// 期限是 yyyy-MM-dd（整理的規則，design §8）才當成到期日
    static func dueDate(_ text: String) -> DateComponents? {
        let parts = text.trimmingCharacters(in: .whitespacesAndNewlines).split(separator: "-").compactMap { Int($0) }
        guard parts.count == 3, text.range(of: #"^\s*\d{4}-\d{2}-\d{2}\s*$"#, options: .regularExpression) != nil,
              (1...12).contains(parts[1]), (1...31).contains(parts[2]) else { return nil }
        return DateComponents(calendar: Calendar(identifier: .gregorian), year: parts[0], month: parts[1], day: parts[2])
    }

    /// 「拾音」列表的名稱：跟著介面語言
    static var listTitle: String { MeetingLanguage.ui(zh: "拾音", en: "Shiyin") }
}

/// EventKit 的提醒事項
final class EventKitReminders: ReminderStore {
    private let store = EKEventStore()

    var access: ReminderAccess {
        switch EKEventStore.authorizationStatus(for: .reminder) {
        case .fullAccess: .granted
        case .notDetermined: .notDetermined
        default: .denied
        }
    }

    func requestAccess() async -> Bool {
        (try? await store.requestFullAccessToReminders()) ?? false
    }

    func list() throws -> String {
        let defaults = UserDefaults.standard
        if let id = defaults.string(forKey: AppSettings.remindersListKey), let calendar = store.calendar(withIdentifier: id) {
            return calendar.calendarIdentifier
        }
        if let calendar = store.calendars(for: .reminder).first(where: { ["拾音", "Shiyin"].contains($0.title) }) {  // l10n-skip：列表的名稱
            defaults.set(calendar.calendarIdentifier, forKey: AppSettings.remindersListKey)
            return calendar.calendarIdentifier
        }
        let calendar = EKCalendar(for: .reminder, eventStore: store)
        calendar.title = Reminders.listTitle
        guard let source = store.defaultCalendarForNewReminders()?.source ?? store.sources.first(where: { $0.sourceType == .local }) else {
            throw ReminderError.noSource
        }
        calendar.source = source
        try store.saveCalendar(calendar, commit: true)
        defaults.set(calendar.calendarIdentifier, forKey: AppSettings.remindersListKey)
        return calendar.calendarIdentifier
    }

    func existing(meetingId: Int64) async throws -> [String: Bool] {
        guard let id = UserDefaults.standard.string(forKey: AppSettings.remindersListKey),
              let calendar = store.calendar(withIdentifier: id) else { return [:] }
        let predicate = store.predicateForReminders(in: [calendar])
        let found: [(String, Bool)] = await withCheckedContinuation { continuation in
            store.fetchReminders(matching: predicate) { reminders in
                let pairs = (reminders ?? []).compactMap { reminder -> (String, Bool)? in
                    guard let target = Reminders.meetingAndKey(reminder.url), target.meetingId == meetingId else { return nil }
                    return (target.key, reminder.isCompleted)
                }
                continuation.resume(returning: pairs)
            }
        }
        return Dictionary(found, uniquingKeysWith: { $0 || $1 })
    }

    func add(_ items: [ReminderItem], to list: String) throws {
        guard let calendar = store.calendar(withIdentifier: list) else { throw ReminderError.noList }
        for item in items {
            let reminder = EKReminder(eventStore: store)
            reminder.calendar = calendar
            reminder.title = item.title
            reminder.notes = item.notes
            reminder.url = item.url
            reminder.dueDateComponents = item.due
            try store.save(reminder, commit: false)
        }
        try store.commit()
    }
}

enum ReminderError: LocalizedError {
    case noSource, noList

    var errorDescription: String? {
        switch self {
        case .noSource: return String(localized: "提醒事項裡沒有可以建立列表的帳號")
        case .noList: return String(localized: "找不到提醒事項裡的「拾音」列表")
        }
    }
}

#if DEBUG
/// 記憶體裡的假提醒事項（design §52.2）：shiyinctl state 的 reminders 看得到內容，reminderdone 把某一項設成完成
final class FakeReminders: ReminderStore {
    static let shared = FakeReminders()
    struct Entry {
        var item: ReminderItem
        var completed = false
    }
    var entries: [Entry] = []
    var access: ReminderAccess = .notDetermined

    func requestAccess() async -> Bool {
        access = .granted
        return true
    }

    func list() throws -> String { "fake" }

    func existing(meetingId: Int64) async throws -> [String: Bool] {
        Dictionary(entries.compactMap { entry in
            Reminders.meetingAndKey(entry.item.url).flatMap { $0.meetingId == meetingId ? ($0.key, entry.completed) : nil }
        }, uniquingKeysWith: { $0 || $1 })
    }

    func add(_ items: [ReminderItem], to list: String) throws {
        entries += items.map { Entry(item: $0) }
    }
}
#endif
