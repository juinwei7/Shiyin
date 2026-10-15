import Foundation

/// 範本的內容（design §8.2）
struct TemplateDefinition: Codable, Hashable {
    struct CustomSpec: Codable, Hashable {
        var title: String
        var instruction: String
    }

    /// 內建段落：summary、decisions、actionItems、openQuestions、topics
    static let builtinSections: [(id: String, label: String)] = [
        ("summary", "摘要"), ("decisions", "決議"), ("actionItems", "待辦"), ("openQuestions", "未決問題"), ("topics", "議題詳細"),
    ]

    var description: String
    var sections: [String]
    var custom: [CustomSpec]
    var instruction: String

    func includes(_ section: String) -> Bool { sections.contains(section) }

    static func decode(_ json: String) -> TemplateDefinition {
        (try? JSONDecoder().decode(TemplateDefinition.self, from: Data(json.utf8)))
            ?? TemplateDefinition(description: "", sections: builtinSections.map(\.id), custom: [], instruction: "")
    }

    func jsonString() -> String {
        String(data: (try? JSONEncoder().encode(self)) ?? Data(), encoding: .utf8) ?? "{}"
    }
}

/// 內建範本（R6.2）。啟動時寫進資料庫，名稱或內容改了就覆蓋。
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

    static func seed(_ db: AppDatabase) {
        let existing = Dictionary(uniqueKeysWithValues: ((try? db.templates()) ?? []).map { ($0.id, $0) })
        for template in all {
            let json = template.definition.jsonString()
            if let current = existing[template.id], current.name == template.name, current.definition == json { continue }
            try? db.saveTemplate(TemplateRecord(id: template.id, name: template.name, builtin: true, definition: json))
        }
    }
}
