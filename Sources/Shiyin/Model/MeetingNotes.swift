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

        init(time: String, question: String) {
            self.time = time
            self.question = question
        }

        init(from decoder: Decoder) throws {
            let c = try decoder.container(keyedBy: CodingKeys.self)
            time = TimeFormat.clean((try? c.decode(String.self, forKey: .time)) ?? "")
            question = (try? c.decode(String.self, forKey: .question)) ?? ""
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
        func r(_ s: String) -> String { s.replacingOccurrences(of: find, with: replacement) }
        func ri(_ i: Item) -> Item { Item(text: r(i.text), time: i.time) }
        return MeetingNotes(
            title: r(title),
            summary: summary.map(ri),
            decisions: decisions.map(ri),
            actionItems: actionItems.map { ActionItem(owner: r($0.owner), task: r($0.task), due: $0.due, time: $0.time) },
            openQuestions: openQuestions.map(ri),
            topics: topics.map { Topic(title: r($0.title), start: $0.start, points: $0.points.map(ri)) },
            custom: custom.map { CustomSection(title: r($0.title), items: $0.items.map(ri)) },
            reviewItems: reviewItems.map { ReviewItem(time: $0.time, question: r($0.question)) })
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
            + #","reviewItems":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["time","question"],"properties":{"time":{"type":"string"},"question":{"type":"string"}}}}}}"#
    }()
}

/// 時間點格式：mm:ss，超過一小時用 h:mm:ss（與逐字稿一致）
enum TimeFormat {
    static func string(_ seconds: Double) -> String {
        let s = max(0, Int(seconds))
        if s >= 3600 { return String(format: "%d:%02d:%02d", s / 3600, s % 3600 / 60, s % 60) }
        return String(format: "%02d:%02d", s / 60, s % 60)
    }

    /// 模型偶爾會照抄逐字稿的寫法，把時間寫成「[03:15]」：去掉前後的括號和空白
    static func clean(_ text: String) -> String {
        text.trimmingCharacters(in: CharacterSet(charactersIn: "[]()（）【】").union(.whitespacesAndNewlines))
    }

    static func seconds(_ text: String) -> Double? {
        let parts = clean(text).split(separator: ":").map { Int($0) }
        guard !parts.isEmpty, parts.count <= 3, parts.allSatisfy({ $0 != nil }) else { return nil }
        return Double(parts.compactMap { $0 }.reduce(0) { $0 * 60 + $1 })
    }

    /// 會議長度的口語說法，例如「9 分鐘」「1 小時 5 分鐘」
    static func duration(_ seconds: Int?) -> String {
        guard let seconds else { return "" }
        let minutes = max(1, Int((Double(seconds) / 60).rounded()))
        if minutes >= 60 { return minutes % 60 == 0 ? "\(minutes / 60) 小時" : "\(minutes / 60) 小時 \(minutes % 60) 分鐘" }
        return "\(minutes) 分鐘"
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

    static func weekday(_ text: String) -> String {
        guard let date = date(text) else { return "" }
        let names = ["星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六"]
        return names[Calendar(identifier: .gregorian).component(.weekday, from: date) - 1]
    }
}
