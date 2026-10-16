import Foundation

/// 範本的內容（design §8.2）
struct TemplateDefinition: Codable, Hashable {
    struct CustomSpec: Codable, Hashable {
        var title: String
        var instruction: String
    }

    /// 內建段落：summary、decisions、actionItems、openQuestions、topics
    static let builtinSectionIDs = ["summary", "decisions", "actionItems", "openQuestions", "topics"]

    /// 段落名稱：介面用介面語言，提示詞用會議語言（design §16.5）
    static func sectionLabel(_ id: String, language: MeetingLanguage) -> String {
        switch id {
        case "summary": return language.text(zh: "摘要", en: "Summary")
        case "decisions": return language.text(zh: "決議", en: "Decisions")
        case "actionItems": return language.text(zh: "待辦", en: "Action items")
        case "openQuestions": return language.text(zh: "未決問題", en: "Open questions")
        default: return language.text(zh: "議題詳細", en: "Topics")
        }
    }

    /// 設定畫面用的段落清單（介面語言）
    static var builtinSections: [(id: String, label: String)] {
        builtinSectionIDs.map { ($0, sectionLabel($0, language: .interface)) }
    }

    var description: String
    var sections: [String]
    var custom: [CustomSpec]
    var instruction: String

    func includes(_ section: String) -> Bool { sections.contains(section) }

    static func decode(_ json: String) -> TemplateDefinition {
        (try? JSONDecoder().decode(TemplateDefinition.self, from: Data(json.utf8)))
            ?? TemplateDefinition(description: "", sections: builtinSectionIDs, custom: [], instruction: "")
    }

    func jsonString() -> String {
        String(data: (try? JSONEncoder().encode(self)) ?? Data(), encoding: .utf8) ?? "{}"
    }
}

/// 內建範本（R6.2）。中文版啟動時寫進資料庫，名稱或內容改了就覆蓋；英文版不存，依 id 取用（design §16.5）。
enum BuiltinTemplates {
    static let all: [(id: String, name: String, definition: TemplateDefinition)] = [
        ("general", "一般會議", TemplateDefinition(
            description: "週會、專案會議、討論會：摘要、決議、待辦、未決問題、議題",
            sections: ["summary", "decisions", "actionItems", "openQuestions", "topics"], custom: [], instruction: "")),
        ("one-on-one", "一對一", TemplateDefinition(
            description: "主管與成員的一對一會談",
            sections: ["summary", "actionItems", "openQuestions"],
            custom: [.init(title: "近況與進展", instruction: "對方最近在做的事、進度與卡關的地方"),
                     .init(title: "回饋", instruction: "雙方給彼此的回饋，標明是誰給誰"),
                     .init(title: "需要的支持", instruction: "對方提出需要協助或資源的地方")],
            instruction: "這是一對一會談，語氣保持中性，不要評價個人。")),
        ("interview", "訪談", TemplateDefinition(
            description: "使用者訪談、專家訪談、面試",
            sections: ["summary", "topics"],
            custom: [.init(title: "受訪者背景", instruction: "受訪者的角色、經歷、使用情境"),
                     .init(title: "重點引述", instruction: "保留受訪者的原話，每條一句，附時間點"),
                     .init(title: "洞察與機會", instruction: "從訪談內容歸納出的需求、痛點與機會")],
            instruction: "這是訪談，重點是受訪者說了什麼，不要把訪談者的推測寫成受訪者的意見。")),
        ("lecture", "課堂／演講", TemplateDefinition(
            description: "課程、演講、分享會、教育訓練",
            sections: ["summary", "topics", "openQuestions"],
            custom: [.init(title: "重點概念", instruction: "提到的重要概念、名詞與解釋"),
                     .init(title: "問答", instruction: "聽眾的提問與講者的回答")],
            instruction: "這是課堂或演講，依講者的段落整理大綱。")),
        ("brainstorm", "腦力激盪", TemplateDefinition(
            description: "發想會議、工作坊",
            sections: ["summary", "decisions", "actionItems"],
            custom: [.init(title: "點子清單", instruction: "每個點子一條，盡量標出是誰提出的"),
                     .init(title: "評估與取捨", instruction: "對點子的討論、優缺點與取捨理由")],
            instruction: "這是腦力激盪，不要遺漏任何被提出的點子，即使沒有被採納。")),
    ]

    /// 英文版：英文會議整理時用，英文介面也顯示這裡的名稱和說明
    static let english: [String: (name: String, definition: TemplateDefinition)] = [
        "general": ("General meeting", TemplateDefinition(
            description: "Team meetings, project meetings, discussions: summary, decisions, action items, open questions, topics",
            sections: ["summary", "decisions", "actionItems", "openQuestions", "topics"], custom: [], instruction: "")),
        "one-on-one": ("One-on-one", TemplateDefinition(
            description: "A one-on-one between a manager and a team member",
            sections: ["summary", "actionItems", "openQuestions"],
            custom: [.init(title: "Updates and progress", instruction: "What they have been working on, progress and blockers"),
                     .init(title: "Feedback", instruction: "Feedback both ways, noting who gave it to whom"),
                     .init(title: "Support needed", instruction: "Where they asked for help or resources")],
            instruction: "This is a one-on-one. Keep a neutral tone and do not judge the person.")),
        "interview": ("Interview", TemplateDefinition(
            description: "User interviews, expert interviews, job interviews",
            sections: ["summary", "topics"],
            custom: [.init(title: "Interviewee background", instruction: "The interviewee's role, experience and context"),
                     .init(title: "Key quotes", instruction: "Keep the interviewee's own words, one quote per item, with its time"),
                     .init(title: "Insights and opportunities", instruction: "Needs, pain points and opportunities drawn from the interview")],
            instruction: "This is an interview. Focus on what the interviewee said; do not present the interviewer's assumptions as the interviewee's opinions.")),
        "lecture": ("Lecture / talk", TemplateDefinition(
            description: "Courses, talks, sharing sessions, training",
            sections: ["summary", "topics", "openQuestions"],
            custom: [.init(title: "Key concepts", instruction: "Important concepts, terms and explanations that came up"),
                     .init(title: "Q&A", instruction: "Questions from the audience and the speaker's answers")],
            instruction: "This is a lecture or talk. Outline it following the speaker's sections.")),
        "brainstorm": ("Brainstorm", TemplateDefinition(
            description: "Ideation meetings, workshops",
            sections: ["summary", "decisions", "actionItems"],
            custom: [.init(title: "Ideas", instruction: "One idea per item, noting who proposed it where possible"),
                     .init(title: "Evaluation and trade-offs", instruction: "Discussion of the ideas, pros and cons, and the reasons for each choice")],
            instruction: "This is a brainstorm. Do not leave out any idea that was raised, even if it was not adopted.")),
    ]

    static func seed(_ db: AppDatabase) {
        let existing = Dictionary(uniqueKeysWithValues: ((try? db.templates()) ?? []).map { ($0.id, $0) })
        for template in all {
            let json = template.definition.jsonString()
            if let current = existing[template.id], current.name == template.name, current.definition == json { continue }
            try? db.saveTemplate(TemplateRecord(id: template.id, name: template.name, builtin: true, definition: json))
        }
    }
}

extension TemplateRecord {
    /// 介面上顯示的名稱：內建範本跟著介面語言，自訂範本照使用者取的名字
    var displayName: String {
        guard builtin, MeetingLanguage.interface == .en, let english = BuiltinTemplates.english[id] else { return name }
        return english.name
    }

    /// 介面上顯示的說明
    var displayDescription: String {
        if builtin, MeetingLanguage.interface == .en, let english = BuiltinTemplates.english[id] { return english.definition.description }
        return TemplateDefinition.decode(definition).description
    }

    /// 整理時用的內容：內建範本跟著會議語言，自訂範本照使用者寫的
    func definition(for language: MeetingLanguage) -> TemplateDefinition {
        if builtin, language == .en, let english = BuiltinTemplates.english[id] { return english.definition }
        return TemplateDefinition.decode(definition)
    }
}
