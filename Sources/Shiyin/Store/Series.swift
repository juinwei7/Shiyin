import Foundation
import GRDB

/// 資料夾的自動加入條件（R98、design §72）：任一個條件符合就放進去
struct FolderRule: SnakeCaseRecord, Hashable {
    static let databaseTableName = "folder_rules"
    var folder: String
    var keywords: [String] = []
    /// 人物 id：選的人都有參加才算
    var people: [Int64] = []
    var templateId: String?
    var seriesId: Int64?
    var createdAt: String = Clock.now()

    var isEmpty: Bool { keywords.isEmpty && people.isEmpty && templateId == nil && seriesId == nil }

    func matches(title: String, attended: Set<Int64>, templateId: String, seriesId: Int64?) -> Bool {
        if keywords.contains(where: { !$0.isEmpty && title.range(of: $0, options: .caseInsensitive) != nil }) { return true }
        if !people.isEmpty && Set(people).isSubset(of: attended) { return true }
        if let id = self.templateId, id == templateId { return true }
        if let id = self.seriesId, id == seriesId { return true }
        return false
    }
}

/// 定期會議（R99、design §73）
struct MeetingSeries: SnakeCaseRecord, Identifiable, Hashable {
    static let databaseTableName = "series"
    var id: Int64?
    var name: String
    /// 1～7：週日～週六（同 `Calendar` 的 weekday）
    var weekday: Int
    /// 當地時間的開始時間（從 0 點算的分鐘）；不知道時 nil
    var minute: Int?
    var intervalWeeks: Int = 1
    /// 建立時那一群人（人物 id），自動加入時比對
    var people: [Int64] = []
    var createdAt: String = Clock.now()

    mutating func didInsert(_ inserted: InsertionSuccess) { id = inserted.rowID }

    var frequencyText: String { SeriesDetector.frequencyText(weekday: weekday, minute: minute, intervalWeeks: intervalWeeks) }

    /// 刪除時記進「不是定期會議」，之後不再建議同一組（design §73）
    var key: String { SeriesDetector.key(weekday: weekday, minute: minute, people: people) }
}

/// 會議的星期幾、開始時間、第幾天（design §73）
struct MeetingTime: Hashable {
    /// 從 1970-01-01 算的第幾天
    var day: Int
    var weekday: Int
    var minute: Int?

    /// 開始時間只有錄音的會議才知道：created_at 換成當地時間、跟 meeting_date 同一天時
    init?(_ meeting: Meeting, timeZone: TimeZone = .current) {
        let parts = meeting.meetingDate.prefix(10).split(separator: "-").compactMap { Int($0) }
        guard parts.count == 3 else { return nil }
        day = Self.dayNumber(year: parts[0], month: parts[1], day: parts[2])
        weekday = Self.weekday(day)
        minute = nil
        if meeting.source == "recording", let created = ISO8601DateFormatter().date(from: meeting.createdAt) {
            var calendar = Calendar(identifier: .gregorian)
            calendar.timeZone = timeZone
            let c = calendar.dateComponents([.year, .month, .day, .hour, .minute], from: created)
            if c.year == parts[0], c.month == parts[1], c.day == parts[2], let h = c.hour, let m = c.minute { minute = h * 60 + m }
        }
    }

    init(day: Int, minute: Int?) {
        self.day = day
        weekday = Self.weekday(day)
        self.minute = minute
    }

    /// 公曆日期換成從 1970-01-01 算的天數（Howard Hinnant 的 days_from_civil）
    static func dayNumber(year: Int, month: Int, day: Int) -> Int {
        let y = month <= 2 ? year - 1 : year
        let era = (y >= 0 ? y : y - 399) / 400
        let yoe = y - era * 400
        let doy = (153 * (month + (month > 2 ? -3 : 9)) + 2) / 5 + day - 1
        let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy
        return era * 146097 + doe - 719468
    }

    /// 1970-01-01 是週四
    static func weekday(_ day: Int) -> Int { ((day + 4) % 7 + 7) % 7 + 1 }
}

/// 一組可能的定期會議（design §73）
struct SeriesCandidate: Hashable, Identifiable {
    var key: String
    var id: String { key }
    /// 由舊到新
    var meetingIds: [Int64]
    var weekday: Int
    var minute: Int?
    var intervalWeeks: Int
    /// 核心人物（過半數場次都有的人），照 id 排序
    var people: [Int64]
    var name: String

    var frequencyText: String { SeriesDetector.frequencyText(weekday: weekday, minute: minute, intervalWeeks: intervalWeeks) }
}

/// 找可能的定期會議：同一群人、星期幾相同、開始時間差不到一小時、每週或每兩週、至少 3 場；不靠標題（D173）
enum SeriesDetector {
    struct Input: Hashable {
        var id: Int64
        var time: MeetingTime
        var people: Set<Int64>
        var title: String
    }

    /// 同一群人：共同的人物至少 2 位、Jaccard ≥ 0.5
    static func samePeople(_ a: Set<Int64>, _ b: Set<Int64>) -> Bool {
        let common = a.intersection(b).count
        return common >= 2 && Double(common) / Double(a.union(b).count) >= 0.5
    }

    /// 開始時間差不到 60 分鐘；任一邊不知道時算符合
    static func closeInTime(_ a: Int?, _ b: Int?) -> Bool {
        guard let a, let b else { return true }
        return abs(a - b) < 60
    }

    static func candidates(_ meetings: [Input], rejected: Set<String>) -> [SeriesCandidate] {
        var groups: [[Input]] = []
        for m in meetings.filter({ $0.people.count >= 2 }).sorted(by: { ($0.time.day, $0.id) < ($1.time.day, $1.id) }) {
            if let i = groups.firstIndex(where: { fits(m, $0) }) { groups[i].append(m) } else { groups.append([m]) }
        }
        return groups.filter { $0.count >= 3 }.map(candidate).filter { !rejected.contains($0.key) }
    }

    private static func fits(_ m: Input, _ group: [Input]) -> Bool {
        guard let last = group.last, m.time.weekday == last.time.weekday, (7...28).contains(m.time.day - last.time.day),
              closeInTime(m.time.minute, last.time.minute) else { return false }
        return samePeople(m.people, core(group.map(\.people)))
    }

    /// 過半數場次都有的人
    static func core(_ people: [Set<Int64>]) -> Set<Int64> {
        var counts: [Int64: Int] = [:]
        for set in people { for id in set { counts[id, default: 0] += 1 } }
        return Set(counts.filter { $0.value * 2 > people.count }.keys)
    }

    private static func candidate(_ group: [Input]) -> SeriesCandidate {
        let gaps = zip(group.dropFirst(), group).map { $0.time.day - $1.time.day }.sorted()
        let minutes = group.compactMap(\.time.minute).sorted()
        // 偶數個時取前面那個中位數
        let minute = minutes.isEmpty ? nil : minutes[(minutes.count - 1) / 2]
        let people = core(group.map(\.people)).sorted()
        let weekday = group[0].time.weekday
        return SeriesCandidate(key: key(weekday: weekday, minute: minute, people: people), meetingIds: group.map(\.id),
                               weekday: weekday, minute: minute, intervalWeeks: gaps[(gaps.count - 1) / 2] >= 14 ? 2 : 1, people: people,
                               name: defaultName(titles: group.map(\.title), weekday: weekday, minute: minute))
    }

    // MARK: 新的一場要不要加入（design §73）

    /// 一個定期會議比對用的資料：最近一場（不算這場）的第幾天
    struct SeriesInfo: Hashable {
        var id: Int64
        var weekday: Int
        var minute: Int?
        var people: Set<Int64>
        var lastDay: Int?
    }

    enum JoinDecision: Hashable {
        /// 自動加入
        case join(Int64)
        /// 證據不夠：建議加入（星期幾、時間、相隔天數對得上，至少一位系列的人有參加）；shared 是共同的人
        case suggest(Int64, shared: [Int64])
        case none
    }

    /// 星期幾相同、時間差不到 60 分鐘、跟最近一場相隔 1～35 天的系列裡：同一群人 → 自動加入（相隔最近的）；
    /// 不然至少一位共同的人 → 建議（共同的人最多、再來相隔最近的）
    static func joinDecision(time: MeetingTime, attended: Set<Int64>, series: [SeriesInfo]) -> JoinDecision {
        let eligible = series.compactMap { s -> (SeriesInfo, Int)? in
            guard s.weekday == time.weekday, closeInTime(s.minute, time.minute), let last = s.lastDay else { return nil }
            let gap = abs(time.day - last)
            return (1...35).contains(gap) ? (s, gap) : nil
        }
        if let strong = eligible.filter({ samePeople(attended, $0.0.people) }).min(by: { ($0.1, $0.0.id) < ($1.1, $1.0.id) }) {
            return .join(strong.0.id)
        }
        let weak = eligible.map { ($0.0, $0.1, attended.intersection($0.0.people).count) }.filter { $0.2 > 0 }
        guard let best = weak.min(by: { (-$0.2, $0.1, $0.0.id) < (-$1.2, $1.1, $1.0.id) }) else { return .none }
        return .suggest(best.0.id, shared: attended.intersection(best.0.people).sorted())
    }

    static func joinKey(meeting: Int64, series: Int64) -> String { "join|\(meeting)|\(series)" }

    static func key(weekday: Int, minute: Int?, people: [Int64]) -> String {
        "\(weekday)|\(minute.map { $0 / 60 } ?? -1)|" + people.sorted().map(String.init).joined(separator: ",")
    }

    // MARK: 名稱、頻率

    /// 所有標題最長的共同子字串（頭尾去掉數字、日期、空白、標點）；沒有時「週一 10:00 的會議」
    static func defaultName(titles: [String], weekday: Int, minute: Int?) -> String {
        if let common = commonWord(titles) { return common }
        let day = weekdayName(weekday)
        if let minute {
            return MeetingLanguage.ui(zh: "\(day) \(timeText(minute)) 的會議", en: "\(day) \(timeText(minute)) meeting")
        }
        return MeetingLanguage.ui(zh: "\(day)的會議", en: "\(day) meeting")
    }

    /// 共同的詞：標題先拿掉數字和跟著的日期單位（11/3、11月3日、2026-11-16），最長的共同子字串去掉頭尾的空白、標點後
    /// 至少 2 個字（只有拉丁字母時至少 3 個字母）
    static func commonWord(_ titles: [String]) -> String? {
        let list = titles.map { Array(withoutNumbers($0)) }.filter { !$0.isEmpty }
        guard list.count >= 2, let shortest = list.min(by: { $0.count < $1.count }) else { return nil }
        let lowered = list.map { String($0).lowercased() }
        for length in stride(from: shortest.count, through: 2, by: -1) {
            for start in 0...(shortest.count - length) {
                let piece = String(shortest[start..<(start + length)])
                guard lowered.allSatisfy({ $0.contains(piece.lowercased()) }) else { continue }
                let cleaned = clean(piece)
                if isSignificant(cleaned), lowered.allSatisfy({ isWholeWord(cleaned.lowercased(), in: $0) }) { return cleaned }
            }
        }
        return nil
    }

    /// 英文要是完整的單字：頭尾是英文字母或數字時，前後不能接著英文字母或數字（「Control」「Conceptual」不會取出「Con」；
    /// 2026-11-16 用 AMI 真人錄音驗收時發現）。中文沒有空格，頭尾是中文時不檢查
    static func isWholeWord(_ word: String, in text: String) -> Bool {
        let w = Array(word), t = Array(text)
        guard let first = w.first, let last = w.last, w.count <= t.count else { return false }
        func latin(_ c: Character) -> Bool { c.isASCII && (c.isLetter || c.isNumber) }
        for i in 0...(t.count - w.count) where Array(t[i..<(i + w.count)]) == w {
            let before = !latin(first) || i == 0 || !latin(t[i - 1])
            let after = !latin(last) || i + w.count == t.count || !latin(t[i + w.count])
            if before && after { return true }
        }
        return false
    }

    private static let edgeCharacters = CharacterSet.whitespacesAndNewlines.union(.punctuationCharacters).union(.symbols)

    /// 前面接英文字母的數字（Q3、v2）留著
    private static let numberPattern = try! NSRegularExpression(pattern: "(?<![A-Za-z])[0-9０-９]+[年月日號号/.\\-]?")  // l10n-skip
    private static let spaces = try! NSRegularExpression(pattern: "\\s+")

    static func withoutNumbers(_ title: String) -> String {
        let stripped = numberPattern.stringByReplacingMatches(in: title, range: NSRange(title.startIndex..., in: title), withTemplate: " ")
        return spaces.stringByReplacingMatches(in: stripped, range: NSRange(stripped.startIndex..., in: stripped), withTemplate: " ")
    }

    static func clean(_ text: String) -> String {
        var scalars = Array(text.unicodeScalars)
        while let first = scalars.first, edgeCharacters.contains(first) { scalars.removeFirst() }
        while let last = scalars.last, edgeCharacters.contains(last) { scalars.removeLast() }
        return String(String.UnicodeScalarView(scalars))
    }

    static func isSignificant(_ word: String) -> Bool {
        let letters = word.unicodeScalars.filter { CharacterSet.letters.contains($0) }
        guard letters.count >= 2 else { return false }
        let latinOnly = letters.allSatisfy { $0.isASCII }
        return !latinOnly || letters.count >= 3
    }

    static func weekdayName(_ weekday: Int) -> String {
        let zh = ["週日", "週一", "週二", "週三", "週四", "週五", "週六"]  // l10n-skip
        let en = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"]
        let i = max(0, min(6, weekday - 1))
        return MeetingLanguage.ui(zh: zh[i], en: en[i])
    }

    static func timeText(_ minute: Int) -> String { String(format: "%d:%02d", minute / 60, minute % 60) }

    /// 「每週一 10:00」「每兩週的週一 10:00」「每週一」
    static func frequencyText(weekday: Int, minute: Int?, intervalWeeks: Int) -> String {
        let day = weekdayName(weekday)
        let time = minute.map { " " + timeText($0) } ?? ""
        let enTime = minute.map { " at " + timeText($0) } ?? ""
        return intervalWeeks >= 2
            ? MeetingLanguage.ui(zh: "每兩週的\(day)\(time)", en: "Every other \(day)\(enTime)")
            : MeetingLanguage.ui(zh: "每\(day)\(time)", en: "Every \(day)\(enTime)")
    }
}

/// 不在資料夾的會議很像某個資料夾裡的會議（R98.3、design §72）
struct FolderSuggestion: Hashable {
    enum Reason: Hashable {
        /// 都有這幾位參加（人物 id）
        case people([Int64])
        /// 標題都有這個詞
        case title(String)
    }
    var folder: String
    var reason: Reason
    var count: Int
}

enum FolderSuggester {
    struct Input: Hashable {
        var id: Int64
        var folder: String
        var title: String
        var people: Set<Int64>
    }

    /// 通用的詞不算共同的詞
    static let genericWords: Set<String> = ["會議", "会议", "會議記錄", "meeting", "meetings", "討論", "讨论", "会議", "회의"]  // l10n-skip

    static func best(for meeting: Input, among others: [Input]) -> FolderSuggestion? {
        var best: FolderSuggestion?
        let byFolder = Dictionary(grouping: others.filter { !$0.folder.isEmpty && $0.id != meeting.id }, by: \.folder)
        for folder in byFolder.keys.sorted(by: { $0.localizedStandardCompare($1) == .orderedAscending }) {
            let members = byFolder[folder]!
            guard members.count >= 2 else { continue }
            var options: [FolderSuggestion] = []
            let samePeople = members.filter { SeriesDetector.samePeople(meeting.people, $0.people) }
            if samePeople.count >= 2 {
                let core = SeriesDetector.core(samePeople.map(\.people)).intersection(meeting.people)
                options.append(FolderSuggestion(folder: folder, reason: .people(core.sorted()), count: samePeople.count))
            }
            var words: [String: Int] = [:]
            for member in members {
                if let word = SeriesDetector.commonWord([meeting.title, member.title]), !genericWords.contains(word.lowercased()) {
                    words[word, default: 0] += 1
                }
            }
            // 場數最多的詞；一樣多時照字串排序取第一個
            if let (word, count) = words.sorted(by: { ($1.value, $0.key) < ($0.value, $1.key) }).first, count >= 2 {
                options.append(FolderSuggestion(folder: folder, reason: .title(word), count: count))
            }
            // 同一個資料夾：人的比較準，場數一樣時用人
            for option in options where best == nil || option.count > best!.count {
                best = option
            }
        }
        return best
    }
}

/// 定期會議裡還沒完成的待辦（R99.4、design §73）：由舊到新，先用這一場的 followUps 拿掉完成的，再加入這一場的待辦
enum SeriesOpenItems {
    static func compute(_ meetings: [Meeting]) -> [PersonActionItem] {
        var open: [PersonActionItem] = []
        for meeting in meetings.sorted(by: { ($0.meetingDate, $0.id ?? 0) < ($1.meetingDate, $1.id ?? 0) }) {
            guard let notes = meeting.decodedNotes else { continue }
            for followUp in notes.followUps where followUp.status == .done {
                open.removeAll { sameTask($0.item.task, followUp.task) }
            }
            for (index, item) in notes.actionItems.enumerated() where !item.task.isEmpty {
                open.removeAll { sameTask($0.item.task, item.task) }
                open.append(PersonActionItem(meeting: meeting, index: index, item: item))
            }
        }
        // 新的會議在前，同一場照紀錄裡的順序
        return open.sorted { a, b in
            if a.meeting.meetingDate != b.meeting.meetingDate { return a.meeting.meetingDate > b.meeting.meetingDate }
            if a.meeting.id != b.meeting.id { return (a.meeting.id ?? 0) > (b.meeting.id ?? 0) }
            return a.index < b.index
        }
    }

    /// 去掉頭尾空白、不分大小寫相同，或一邊包含另一邊（短的那邊至少 4 個字）
    static func sameTask(_ a: String, _ b: String) -> Bool {
        let x = a.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        let y = b.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        guard !x.isEmpty, !y.isEmpty else { return false }
        if x == y { return true }
        return min(x.count, y.count) >= 4 && (x.contains(y) || y.contains(x))
    }
}

/// 定期會議的上一場（R99.5、design §73）：它的待辦（加上它的 followUps 裡還沒完成的）、未決問題
struct PreviousMeeting: Hashable {
    var title: String
    var date: String
    var items: [MeetingNotes.ActionItem]
    var openQuestions: [MeetingNotes.Item]

    init(title: String, date: String, items: [MeetingNotes.ActionItem], openQuestions: [MeetingNotes.Item]) {
        self.title = title
        self.date = date
        self.items = items
        self.openQuestions = openQuestions
    }

    /// 沒有待辦也沒有未決問題時是 nil（提示詞不加這一段）
    init?(_ meeting: Meeting) {
        guard let notes = meeting.decodedNotes else { return nil }
        var items = notes.actionItems.filter { !$0.task.isEmpty }
        for followUp in notes.followUps where followUp.status != .done && !items.contains(where: { SeriesOpenItems.sameTask($0.task, followUp.task) }) {
            items.append(MeetingNotes.ActionItem(owner: followUp.owner, task: followUp.task, due: ""))
        }
        guard !items.isEmpty || !notes.openQuestions.isEmpty else { return nil }
        self.init(title: meeting.displayTitle, date: meeting.meetingDate, items: items, openQuestions: notes.openQuestions)
    }

    /// 整理後的驗證：task 對不到給的待辦的丟掉、同一條只留第一個；沒給上一場（nil）時清空。
    /// 對到的那一條，task 和 owner 換成上一場原本寫的（模型會把沒有負責人時提示詞寫的「(unassigned)」抄進 owner）
    static func validFollowUps(_ followUps: [MeetingNotes.FollowUp], previous: PreviousMeeting?) -> [MeetingNotes.FollowUp] {
        guard let previous else { return [] }
        var used = Set<Int>()
        return followUps.compactMap { followUp in
            guard let i = previous.items.indices.first(where: { !used.contains($0) && SeriesOpenItems.sameTask(previous.items[$0].task, followUp.task) })
            else { return nil }
            used.insert(i)
            var fixed = followUp
            fixed.task = previous.items[i].task
            fixed.owner = previous.items[i].owner
            return fixed
        }
    }
}

// MARK: - 資料庫

extension AppDatabase {
    func folderRules() throws -> [FolderRule] {
        try pool.read { db in try FolderRule.order(Column("folder")).fetchAll(db) }
    }

    /// 條件都空時當成移除
    func saveFolderRule(_ rule: FolderRule) throws {
        try pool.write { db in
            if rule.isEmpty {
                try db.execute(sql: "DELETE FROM folder_rules WHERE folder = ?", arguments: [rule.folder])
            } else {
                var rule = rule
                try rule.save(db)
            }
        }
    }

    func series() throws -> [MeetingSeries] {
        try pool.read { db in try MeetingSeries.order(Column("name")).fetchAll(db) }
    }

    func rejectedSeriesKeys() throws -> Set<String> {
        try pool.read { db in Set(try String.fetchAll(db, sql: "SELECT key FROM series_rejected")) }
    }

    func rejectSeries(_ key: String) throws {
        try pool.write { db in
            try db.execute(sql: "INSERT OR IGNORE INTO series_rejected(key, created_at) VALUES (?, ?)", arguments: [key, Clock.now()])
        }
    }

    @discardableResult
    func createSeries(_ series: MeetingSeries, meetingIds: [Int64]) throws -> MeetingSeries {
        try pool.write { db in
            var inserted = series
            try inserted.insert(db)
            for id in meetingIds {
                try db.execute(sql: "UPDATE meetings SET series_id = ?, updated_at = ? WHERE id = ?", arguments: [inserted.id, Clock.now(), id])
            }
            return inserted
        }
    }

    func renameSeries(_ id: Int64, to name: String) throws {
        try pool.write { db in try db.execute(sql: "UPDATE series SET name = ? WHERE id = ?", arguments: [name, id]) }
    }

    /// 會議留著（series_id 變 null）；記進「不是定期會議」，之後不再建議同一組
    func deleteSeries(_ id: Int64) throws {
        try pool.write { db in
            if let series = try MeetingSeries.fetchOne(db, key: id) {
                try db.execute(sql: "INSERT OR IGNORE INTO series_rejected(key, created_at) VALUES (?, ?)", arguments: [series.key, Clock.now()])
            }
            try db.execute(sql: "UPDATE meetings SET series_id = NULL WHERE series_id = ?", arguments: [id])
            try db.execute(sql: "DELETE FROM series WHERE id = ?", arguments: [id])
        }
    }

    func setSeries(_ ids: [Int64], series: Int64?) throws {
        try pool.write { db in
            for id in ids {
                try db.execute(sql: "UPDATE meetings SET series_id = ?, updated_at = ? WHERE id = ?", arguments: [series, Clock.now(), id])
            }
        }
    }

    func setFolderDecided(_ id: Int64) throws {
        try pool.write { db in try db.execute(sql: "UPDATE meetings SET folder_decided = 1 WHERE id = ?", arguments: [id]) }
    }

    func seriesMeetings(_ seriesId: Int64) throws -> [Meeting] {
        try pool.read { db in
            try Meeting.filter(Column("series_id") == seriesId && Column("deleted_at") == nil)
                .order(sql: "meeting_date DESC, id DESC").fetchAll(db)
        }
    }

    /// 上一場（R99.5）：同系列、日期比這場早（同一天時 id 比較小）、有會議紀錄的最近一場
    func previousSeriesMeeting(_ meeting: Meeting) throws -> Meeting? {
        guard let series = meeting.seriesId, let id = meeting.id else { return nil }
        return try pool.read { db in
            try Meeting.fetchOne(db, sql: """
                SELECT * FROM meetings WHERE series_id = ? AND deleted_at IS NULL AND notes IS NOT NULL AND notes != ''
                  AND (meeting_date < ? OR (meeting_date = ? AND id < ?))
                ORDER BY meeting_date DESC, id DESC LIMIT 1
                """, arguments: [series, meeting.meetingDate, meeting.meetingDate, id])
        }
    }

    /// 轉錄完自動加入定期會議（design §73）：星期幾相同、時間接近、同一群人、跟系列最近一場相隔 1～35 天；
    /// 好幾個符合時取最近一場最近的。回傳加入的系列
    func autoJoinSeries(_ meetingId: Int64) throws -> MeetingSeries? {
        guard let meeting = try meeting(meetingId), meeting.seriesId == nil, meeting.deletedAt == nil,
              let time = MeetingTime(meeting) else { return nil }
        let attended = try meetingPeople()[meetingId] ?? []
        let all = try series()
        let infos = try all.compactMap { s -> SeriesDetector.SeriesInfo? in
            guard let id = s.id else { return nil }
            let last = try seriesMeetings(id).first(where: { $0.id != meetingId }).flatMap { MeetingTime($0) }
            return SeriesDetector.SeriesInfo(id: id, weekday: s.weekday, minute: s.minute, people: Set(s.people), lastDay: last?.day)
        }
        // 證據不夠（只認出一位）時不加，會議頁建議（AppModel.seriesJoinSuggestion）
        guard case .join(let id) = SeriesDetector.joinDecision(time: time, attended: attended, series: infos),
              let chosen = all.first(where: { $0.id == id }) else { return nil }
        try setSeries([meetingId], series: id)
        return chosen
    }

    /// 自動放進資料夾（design §72）：不在資料夾、還沒決定過、不在垃圾桶；符合好幾個時放名稱排第一的。回傳資料夾名稱
    func autoFile(_ meetingId: Int64) throws -> String? {
        guard let meeting = try meeting(meetingId), meeting.folder.isEmpty, !meeting.folderDecided, meeting.deletedAt == nil else { return nil }
        let attended = try meetingPeople()[meetingId] ?? []
        let rules = try folderRules().sorted { $0.folder.localizedStandardCompare($1.folder) == .orderedAscending }
        guard let rule = rules.first(where: {
            $0.matches(title: meeting.displayTitle, attended: attended, templateId: meeting.templateId, seriesId: meeting.seriesId)
        }) else { return nil }
        try pool.write { db in
            try db.execute(sql: "UPDATE meetings SET folder = ?, folder_decided = 1, updated_at = ? WHERE id = ?",
                           arguments: [rule.folder, Clock.now(), meetingId])
        }
        return rule.folder
    }

    /// 「現在不在資料夾裡的會議也套用」：符合條件的會議（不在資料夾、不在垃圾桶）
    func meetingsMatching(_ rule: FolderRule) throws -> [Int64] {
        let people = try meetingPeople()
        return try pool.read { db in
            try Meeting.filter(Column("deleted_at") == nil && Column("folder") == "").fetchAll(db)
        }
        .filter { meeting in
            rule.matches(title: meeting.displayTitle, attended: people[meeting.id ?? 0] ?? [], templateId: meeting.templateId, seriesId: meeting.seriesId)
        }
        .compactMap(\.id)
    }
}
