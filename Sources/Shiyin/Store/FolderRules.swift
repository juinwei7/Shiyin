import Foundation
import GRDB

/// 資料夾的自動加入條件（R98、design §72）：任一個條件符合就放進去
struct FolderRule: SnakeCaseRecord, Hashable {
    static let databaseTableName = "folder_rules"
    /// 哪個資料夾的條件（migration v18、design §92）
    var folderId: Int64
    var keywords: [String] = []
    /// 人物 id：選的人都有參加才算
    var people: [Int64] = []
    var templateId: String?
    var createdAt: String = Clock.now()

    var isEmpty: Bool { keywords.isEmpty && people.isEmpty && templateId == nil }

    func matches(title: String, attended: Set<Int64>, templateId: String) -> Bool {
        if keywords.contains(where: { !$0.isEmpty && title.range(of: $0, options: .caseInsensitive) != nil }) { return true }
        if !people.isEmpty && Set(people).isSubset(of: attended) { return true }
        if let id = self.templateId, id == templateId { return true }
        return false
    }
}

/// 選取的會議所在的資料夾（「移到資料夾」選單打勾、「移出」用）
enum CommonFolder: Equatable {
    /// 都在同一個資料夾；nil 是都不在資料夾
    case same(Int64?)
    case mixed
}

/// 會議像不像（資料夾的建議用，R98.3、design §72）：同一群人、核心人物、標題的共同詞
enum MeetingSimilarity {
    /// 同一群人：共同的人物至少 2 位、Jaccard ≥ 0.5
    static func samePeople(_ a: Set<Int64>, _ b: Set<Int64>) -> Bool {
        let common = a.intersection(b).count
        return common >= 2 && Double(common) / Double(a.union(b).count) >= 0.5
    }

    /// 過半數場次都有的人
    static func core(_ people: [Set<Int64>]) -> Set<Int64> {
        var counts: [Int64: Int] = [:]
        for set in people { for id in set { counts[id, default: 0] += 1 } }
        return Set(counts.filter { $0.value * 2 > people.count }.keys)
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
}

/// 不在資料夾的會議很像某個資料夾裡的會議（R98.3、design §72）
struct FolderSuggestion: Hashable {
    enum Reason: Hashable {
        /// 都有這幾位參加（人物 id）
        case people([Int64])
        /// 標題都有這個詞
        case title(String)
    }
    var folderId: Int64
    /// 資料夾的名字（顯示、排序用）
    var folder: String
    var reason: Reason
    var count: Int
}

enum FolderSuggester {
    struct Input: Hashable {
        var id: Int64
        var folderId: Int64?
        /// 資料夾的名字：好幾個資料夾都像時照名字挑（同以前）
        var folder: String
        var title: String
        var people: Set<Int64>
    }

    /// 通用的詞不算共同的詞
    static let genericWords: Set<String> = ["會議", "会议", "會議記錄", "meeting", "meetings", "討論", "讨论", "会議", "회의"]  // l10n-skip

    static func best(for meeting: Input, among others: [Input]) -> FolderSuggestion? {
        var best: FolderSuggestion?
        let byFolder = Dictionary(grouping: others.filter { $0.folderId != nil && $0.id != meeting.id }, by: { $0.folderId! })
        for folderId in byFolder.keys.sorted(by: { byFolder[$0]![0].folder.localizedStandardCompare(byFolder[$1]![0].folder) == .orderedAscending }) {
            let members = byFolder[folderId]!
            let folder = members[0].folder
            guard members.count >= 2 else { continue }
            var options: [FolderSuggestion] = []
            let samePeople = members.filter { MeetingSimilarity.samePeople(meeting.people, $0.people) }
            if samePeople.count >= 2 {
                let core = MeetingSimilarity.core(samePeople.map(\.people)).intersection(meeting.people)
                options.append(FolderSuggestion(folderId: folderId, folder: folder, reason: .people(core.sorted()), count: samePeople.count))
            }
            var words: [String: Int] = [:]
            for member in members {
                if let word = MeetingSimilarity.commonWord([meeting.title, member.title]), !genericWords.contains(word.lowercased()) {
                    words[word, default: 0] += 1
                }
            }
            // 場數最多的詞；一樣多時照字串排序取第一個
            if let (word, count) = words.sorted(by: { ($1.value, $0.key) < ($0.value, $1.key) }).first, count >= 2 {
                options.append(FolderSuggestion(folderId: folderId, folder: folder, reason: .title(word), count: count))
            }
            // 同一個資料夾：人的比較準，場數一樣時用人
            for option in options where best == nil || option.count > best!.count {
                best = option
            }
        }
        return best
    }
}

// MARK: - 資料庫

extension AppDatabase {
    func folderRules() throws -> [FolderRule] {
        try pool.read { db in try FolderRule.order(Column("folder_id")).fetchAll(db) }
    }

    /// 條件都空時當成移除
    func saveFolderRule(_ rule: FolderRule) throws {
        try pool.write { db in
            if rule.isEmpty {
                try db.execute(sql: "DELETE FROM folder_rules WHERE folder_id = ?", arguments: [rule.folderId])
            } else {
                var rule = rule
                try rule.save(db)
            }
        }
    }

    func setFolderDecided(_ id: Int64) throws {
        try pool.write { db in try db.execute(sql: "UPDATE meetings SET folder_decided = 1 WHERE id = ?", arguments: [id]) }
    }

    /// 自動放進資料夾（design §72）：不在資料夾、還沒決定過、不在垃圾桶；符合好幾個時放名稱排第一的。回傳資料夾 id
    func autoFile(_ meetingId: Int64) throws -> Int64? {
        guard let meeting = try meeting(meetingId), meeting.folderId == nil, !meeting.folderDecided, meeting.deletedAt == nil else { return nil }
        let attended = try meetingPeople()[meetingId] ?? []
        let names = Dictionary(uniqueKeysWithValues: try folders().compactMap { f in f.id.map { ($0, f.name) } })
        let rules = try folderRules().sorted { (names[$0.folderId] ?? "").localizedStandardCompare(names[$1.folderId] ?? "") == .orderedAscending }
        guard let rule = rules.first(where: {
            $0.matches(title: meeting.displayTitle, attended: attended, templateId: meeting.templateId)
        }) else { return nil }
        try pool.write { db in
            try db.execute(sql: "UPDATE meetings SET folder_id = ?, folder_decided = 1, updated_at = ? WHERE id = ?",
                           arguments: [rule.folderId, Clock.now(), meetingId])
        }
        return rule.folderId
    }

    /// 「現在不在資料夾裡的會議也套用」：符合條件的會議（不在資料夾、不在垃圾桶）
    func meetingsMatching(_ rule: FolderRule) throws -> [Int64] {
        let people = try meetingPeople()
        return try pool.read { db in
            try Meeting.filter(Column("deleted_at") == nil && Column("folder_id") == nil).fetchAll(db)
        }
        .filter { meeting in
            rule.matches(title: meeting.displayTitle, attended: people[meeting.id ?? 0] ?? [], templateId: meeting.templateId)
        }
        .compactMap(\.id)
    }
}
