import Foundation

/// 會議紀錄 v2（design §8.1）：每一條都帶錄音時間點（R6.4）。
/// 讀取時相容 v1：字串陣列會轉成 `Item(text:, time: "")`。
struct MeetingNotes: Codable, Hashable {
    struct Item: Codable, Hashable {
        var text: String
        var time: String

        init(text: String, time: String = "") {
            self.text = text
            self.time = time
        }

        init(from decoder: Decoder) throws {
            if let single = try? decoder.singleValueContainer(), let string = try? single.decode(String.self) {
                self.init(text: string)
                return
            }
            let c = try decoder.container(keyedBy: CodingKeys.self)
            self.init(text: try c.decode(String.self, forKey: .text), time: TimeFormat.clean((try? c.decode(String.self, forKey: .time)) ?? ""))
        }
    }

    struct Topic: Codable, Hashable {
        var title: String
        var start: String
        var points: [Item]

        init(title: String, start: String, points: [Item]) {
            self.title = title
            self.start = start
            self.points = points
        }

        init(from decoder: Decoder) throws {
            let c = try decoder.container(keyedBy: CodingKeys.self)
            title = (try? c.decode(String.self, forKey: .title)) ?? ""
            start = TimeFormat.clean((try? c.decode(String.self, forKey: .start)) ?? "")
            points = (try? c.decode([Item].self, forKey: .points)) ?? []
        }
    }

    struct ActionItem: Codable, Hashable {
        var owner: String
        var task: String
        var due: String
        var time: String

        init(owner: String, task: String, due: String, time: String = "") {
            self.owner = owner
            self.task = task
            self.due = due
            self.time = time
        }

        init(from decoder: Decoder) throws {
            let c = try decoder.container(keyedBy: CodingKeys.self)
            owner = (try? c.decode(String.self, forKey: .owner)) ?? ""
            task = (try? c.decode(String.self, forKey: .task)) ?? ""
            due = (try? c.decode(String.self, forKey: .due)) ?? ""
            time = TimeFormat.clean((try? c.decode(String.self, forKey: .time)) ?? "")
        }
    }

    struct CustomSection: Codable, Hashable {
        var title: String
        var items: [Item]
    }

    struct ReviewItem: Codable, Hashable {
        var time: String
        var question: String
        /// 能用是或否回答（R32、design §27）：畫面上給「是」「不是」的按鈕
        var yesNo: Bool

        init(time: String, question: String, yesNo: Bool = false) {
            self.time = time
            self.question = question
            self.yesNo = yesNo
        }

        init(from decoder: Decoder) throws {
            let c = try decoder.container(keyedBy: CodingKeys.self)
            time = TimeFormat.clean((try? c.decode(String.self, forKey: .time)) ?? "")
            question = (try? c.decode(String.self, forKey: .question)) ?? ""
            yesNo = (try? c.decode(Bool.self, forKey: .yesNo)) ?? false
        }

        private enum CodingKeys: String, CodingKey { case time, question, yesNo }

        /// yesNo 是 false 時不寫：以前的會議紀錄、沒有是非題的會議紀錄存起來跟以前一字不差
        func encode(to encoder: Encoder) throws {
            var c = encoder.container(keyedBy: CodingKeys.self)
            try c.encode(time, forKey: .time)
            try c.encode(question, forKey: .question)
            if yesNo { try c.encode(true, forKey: .yesNo) }
        }
    }

    var title: String
    var summary: [Item]
    var decisions: [Item]
    var actionItems: [ActionItem]
    var openQuestions: [Item]
    var topics: [Topic]
    var custom: [CustomSection]
    var reviewItems: [ReviewItem]

    init(title: String = "", summary: [Item] = [], decisions: [Item] = [], actionItems: [ActionItem] = [],
         openQuestions: [Item] = [], topics: [Topic] = [], custom: [CustomSection] = [], reviewItems: [ReviewItem] = []) {
        self.title = title
        self.summary = summary
        self.decisions = decisions
        self.actionItems = actionItems
        self.openQuestions = openQuestions
        self.topics = topics
        self.custom = custom
        self.reviewItems = reviewItems
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        title = (try? c.decode(String.self, forKey: .title)) ?? ""
        summary = (try? c.decode([Item].self, forKey: .summary)) ?? []
        decisions = (try? c.decode([Item].self, forKey: .decisions)) ?? []
        actionItems = (try? c.decode([ActionItem].self, forKey: .actionItems)) ?? []
        openQuestions = (try? c.decode([Item].self, forKey: .openQuestions)) ?? []
        custom = (try? c.decode([CustomSection].self, forKey: .custom)) ?? []
        reviewItems = (try? c.decode([ReviewItem].self, forKey: .reviewItems)) ?? []
        topics = (try? c.decode([Topic].self, forKey: .topics)) ?? []
    }

    func jsonString() -> String {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
        return String(data: (try? encoder.encode(self)) ?? Data(), encoding: .utf8) ?? "{}"
    }

    var isEmpty: Bool {
        summary.isEmpty && decisions.isEmpty && actionItems.isEmpty && openQuestions.isEmpty && topics.isEmpty && custom.isEmpty
    }

    /// 全文取代：對每一個字串值做區分大小寫的單純取代
    func replacing(_ find: String, with replacement: String) -> MeetingNotes {
        mappingText { $0.replacingOccurrences(of: find, with: replacement) }
    }

    /// 說話者改名後，會議紀錄裡的舊名字換成新名字（design §21.6）。
    /// 舊名字是「說話者 1」這種預設名稱時，後面不能接數字，「說話者 1」不會換到「說話者 10」
    func replacingSpeakerName(_ old: String, with new: String, isDefaultName: Bool) -> MeetingNotes {
        guard isDefaultName, let regex = try? NSRegularExpression(pattern: NSRegularExpression.escapedPattern(for: old) + "(?![0-9])") else {
            return replacing(old, with: new)
        }
        let template = NSRegularExpression.escapedTemplate(for: new)
        return mappingText { regex.stringByReplacingMatches(in: $0, range: NSRange($0.startIndex..., in: $0), withTemplate: template) }
    }

    /// 每一個文字欄位都套用 transform（時間點不變）
    private func mappingText(_ r: (String) -> String) -> MeetingNotes {
        func ri(_ i: Item) -> Item { Item(text: r(i.text), time: i.time) }
        return MeetingNotes(
            title: r(title),
            summary: summary.map(ri),
            decisions: decisions.map(ri),
            actionItems: actionItems.map { ActionItem(owner: r($0.owner), task: r($0.task), due: $0.due, time: $0.time) },
            openQuestions: openQuestions.map(ri),
            topics: topics.map { Topic(title: r($0.title), start: $0.start, points: $0.points.map(ri)) },
            custom: custom.map { CustomSection(title: r($0.title), items: $0.items.map(ri)) },
            reviewItems: reviewItems.map { ReviewItem(time: $0.time, question: r($0.question), yesNo: $0.yesNo) })
    }

    /// 給搜尋索引用的純文字
    var plainText: String {
        var lines = [title]
        lines += summary.map(\.text) + decisions.map(\.text) + openQuestions.map(\.text)
        lines += actionItems.map { "\($0.owner) \($0.task) \($0.due)" }
        for topic in topics { lines.append(topic.title); lines += topic.points.map(\.text) }
        for section in custom { lines.append(section.title); lines += section.items.map(\.text) }
        lines += reviewItems.map(\.question)
        return lines.filter { !$0.isEmpty }.joined(separator: "\n")
    }

    // 給 Claude --json-schema、Codex --output-schema、OpenAI strict json_schema、Anthropic tool input_schema。
    // 不用 $ref／$defs：不是每個服務都支援，條目的定義直接展開
    static let jsonSchema: String = {
        let items = #"{"type":"array","items":{"type":"object","additionalProperties":false,"required":["text","time"],"properties":{"text":{"type":"string"},"time":{"type":"string"}}}}"#
        return #"{"type":"object","additionalProperties":false,"required":["title","summary","decisions","actionItems","openQuestions","topics","custom","reviewItems"],"properties":{"title":{"type":"string"},"summary":"# + items
            + #","decisions":"# + items
            + #","actionItems":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["owner","task","due","time"],"properties":{"owner":{"type":"string"},"task":{"type":"string"},"due":{"type":"string"},"time":{"type":"string"}}}}"#
            + #","openQuestions":"# + items
            + #","topics":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["title","start","points"],"properties":{"title":{"type":"string"},"start":{"type":"string"},"points":"# + items + #"}}}"#
            + #","custom":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["title","items"],"properties":{"title":{"type":"string"},"items":"# + items + #"}}}"#
            + #","reviewItems":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["time","question","yesNo"],"properties":{"time":{"type":"string"},"question":{"type":"string"},"yesNo":{"type":"boolean"}}}}}}"#
    }()
}

/// 一條紀錄的位置（design §31.1）：待確認的題目對應到哪一條
enum NoteItemRef: Hashable {
    case summary(Int), decisions(Int), actionItems(Int), openQuestions(Int)
    /// 第幾個議題的第幾個要點
    case topicPoint(Int, Int)
    /// 第幾個自訂段落的第幾條
    case custom(Int, Int)

    /// 標準答案用的名稱和排序（design-windows §4：Windows 的 ReviewLinks 跟這裡比對）
    var sectionName: String {
        switch self {
        case .summary: return "summary"
        case .decisions: return "decisions"
        case .actionItems: return "actionItems"
        case .openQuestions: return "openQuestions"
        case .topicPoint: return "topicPoint"
        case .custom: return "custom"
        }
    }

    var sortKey: (Int, Int, Int) {
        switch self {
        case .summary(let i): return (0, i, 0)
        case .decisions(let i): return (1, i, 0)
        case .actionItems(let i): return (2, i, 0)
        case .openQuestions(let i): return (3, i, 0)
        case .topicPoint(let t, let i): return (4, t, i)
        case .custom(let c, let i): return (5, c, i)
        }
    }
}

extension MeetingNotes {
    /// 待確認的題目對應到紀錄的條目（R41.1，design §31.1）：題目和條目的時間點同一秒的全標；
    /// 沒有同一秒的，標時間最接近、差距 10 秒以內的一條。對不到的題目不在結果裡（只在待確認分頁）。
    /// 議題的標題不算條目，只有要點算。載入會議時算一次（`MeetingDetail.reviewLinks`）
    func reviewLinks() -> [NoteItemRef: [Int]] {
        var entries: [(ref: NoteItemRef, seconds: Double)] = []
        func add(_ items: [Item], _ make: (Int) -> NoteItemRef) {
            for (i, item) in items.enumerated() {
                if let seconds = TimeFormat.seconds(item.time) { entries.append((make(i), seconds)) }
            }
        }
        add(summary) { .summary($0) }
        add(decisions) { .decisions($0) }
        for (i, item) in actionItems.enumerated() {
            if let seconds = TimeFormat.seconds(item.time) { entries.append((.actionItems(i), seconds)) }
        }
        add(openQuestions) { .openQuestions($0) }
        for (t, topic) in topics.enumerated() { add(topic.points) { .topicPoint(t, $0) } }
        for (c, section) in custom.enumerated() { add(section.items) { .custom(c, $0) } }

        var links: [NoteItemRef: [Int]] = [:]
        for (index, item) in reviewItems.enumerated() {
            guard let seconds = TimeFormat.seconds(item.time) else { continue }
            let exact = entries.filter { $0.seconds == seconds }
            if !exact.isEmpty {
                for entry in exact { links[entry.ref, default: []].append(index) }
            } else if let nearest = entries.min(by: { abs($0.seconds - seconds) < abs($1.seconds - seconds) }),
                      abs(nearest.seconds - seconds) <= 10 {
                links[nearest.ref, default: []].append(index)
            }
        }
        return links
    }
}

/// 時間點格式：mm:ss，超過一小時用 h:mm:ss（與逐字稿一致）
enum TimeFormat {
    static func string(_ seconds: Double) -> String {
        // Int() 遇到不是有限的數字、超過範圍的值會當掉（例如伺服器、模型給的怪時間）：先限在 0～10 億秒，一般的值結果不變
        let s = seconds.isFinite ? Int(min(max(0, seconds), 1e9)) : 0
        if s >= 3600 { return String(format: "%d:%02d:%02d", s / 3600, s % 3600 / 60, s % 60) }
        return String(format: "%02d:%02d", s / 60, s % 60)
    }

    /// 模型偶爾會照抄逐字稿的寫法，把時間寫成「[03:15]」：去掉前後的括號和空白
    static func clean(_ text: String) -> String {
        text.trimmingCharacters(in: CharacterSet(charactersIn: "[]()（）【】").union(.whitespacesAndNewlines))  // l10n-skip
    }

    static func seconds(_ text: String) -> Double? {
        // 「03:15–05:20」這種範圍取開頭；全形冒號也接受
        let first = clean(text).replacingOccurrences(of: "：", with: ":")  // l10n-skip
            .split(whereSeparator: { "–—-~〜～ ".contains($0) }).first.map(String.init) ?? ""  // l10n-skip
        let parts = first.split(separator: ":").map { Int($0) }
        guard !parts.isEmpty, parts.count <= 3, parts.allSatisfy({ $0 != nil }) else { return nil }
        return Double(parts.compactMap { $0 }.reduce(0) { $0 * 60 + $1 })
    }

    /// 會議長度的口語說法，例如「9 分鐘」「1 小時 5 分鐘」；介面用介面語言，匯出用會議語言
    static func duration(_ seconds: Int?, language: MeetingLanguage = .interface) -> String {
        guard let seconds else { return "" }
        let minutes = max(1, Int((Double(seconds) / 60).rounded()))
        let h = minutes / 60, m = minutes % 60
        if minutes >= 60 {
            return m == 0 ? language.text(zh: "\(h) 小時", en: "\(h) hr", ja: "\(h) 時間")
            : language.text(zh: "\(h) 小時 \(m) 分鐘", en: "\(h) hr \(m) min", ja: "\(h) 時間 \(m) 分")
        }
        return language.text(zh: "\(minutes) 分鐘", en: "\(minutes) min", ja: "\(minutes) 分")
    }

    /// srt 用 00:01:02,345；vtt 用 00:01:02.345
    static func subtitle(_ seconds: Double, separator: String) -> String {
        let ms = Int((max(0, seconds) * 1000).rounded())
        return String(format: "%02d:%02d:%02d%@%03d", ms / 3_600_000, ms % 3_600_000 / 60_000, ms % 60_000 / 1000, separator, ms % 1000)
    }
}

enum DateText {
    static let formatter: DateFormatter = {
        let f = DateFormatter()
        f.calendar = Calendar(identifier: .gregorian)
        f.locale = Locale(identifier: "en_US_POSIX")
        f.dateFormat = "yyyy-MM-dd"
        return f
    }()

    static func string(_ date: Date) -> String { formatter.string(from: date) }
    static func date(_ text: String) -> Date? { formatter.date(from: text) }

    /// 星期幾：中文「星期四」、英文「Thursday」
    static func weekday(_ text: String, language: MeetingLanguage = .interface) -> String {
        guard let date = date(text) else { return "" }
        return date.formatted(Date.FormatStyle(locale: language.locale).weekday(.wide))
    }
}
