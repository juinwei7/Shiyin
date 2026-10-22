import Foundation

/// 校稿時使用者的一題回答
struct ReviewAnswer: Codable, Hashable {
    var time: String
    var question: String
    var answer: String
}

/// 整理需要的材料
struct NotesContext {
    var meeting: Meeting
    var segments: [Segment]
    var speakers: [Int: String]
    var bookmarks: [Bookmark]
    var glossary: [String]
    var template: TemplateDefinition
}

/// 整理與校稿的提示詞（design §8.3），規則沿用第二版；依會議語言分成中文、英文、日文三份（design §16.5、§18.4）
enum NotesPrompt {
    static func system(_ language: MeetingLanguage) -> String {
        language.text(zh: systemChinese, en: systemEnglish, ja: systemJapanese)
    }

    static let systemChinese = """
    你是會議紀錄整理者。根據使用者提供的會議逐字稿，整理成結構化的會議紀錄，一律使用繁體中文（台灣用語）。

    規則：
    1. 只根據逐字稿與使用者筆記撰寫，不要補充沒有提到的資訊。
    2. 逐字稿來自語音辨識，英文術語、人名、縮寫常被聽錯（同一個詞可能出現好幾種寫法）。請依上下文和詞彙表推斷正確寫法，但凡是推斷出來的專有名詞、人名，一定要列進 reviewItems 讓使用者確認。
    3. 每一條紀錄（summary、decisions、actionItems、openQuestions、topics 的 points、custom 的 items）都要填 time：該內容在逐字稿中出現的時間點，寫成 mm:ss（例如 03:15，超過一小時寫 1:03:15），不要加括號。綜合多處的內容，填最主要的那一處。
    4. 逐字稿每行標有說話者。actionItems 的 owner 只填明確提到的負責人，優先使用說話者的名字；不明確就留空字串並列進 reviewItems。
    5. actionItems 的 due 依提示中的「會議日期」換算成 yyyy-MM-dd（例如「下週三」要依會議日期和星期幾推算，星期幾要算對）；沒提到期限就留空字串，提到但不明確就留空並列進 reviewItems。
    6. reviewItems 最多 8 題，相近的問題要合併；只問會影響紀錄正確性的地方：專有名詞、人名、負責人、期限、方案內容。每題的 time 填相關段落的時間點。能用「是」或「不是」回答的題目（例如「負責人是小田切嗎？」）寫成是非題，yesNo 填 true；要使用者說出答案的（例如「這個系統的正確名稱是什麼？」）yesNo 填 false。
    7. 「使用者筆記」與「重點標記」代表使用者自己認為重要的內容，必須納入紀錄；筆記裡逐字稿沒有的資訊也可以寫入，但不要改變原意。
    8. 依「範本」只產生指定的段落，沒指定的段落給空陣列。custom 依範本的自訂段落順序產生，title 用範本給的段落名稱。
    9. topics 依議題分段，start 填該議題開始的時間點。
    10. title：提示中有「現有標題」就原樣回傳；沒有就依內容取一個 20 字以內的標題。
    11. 時間點格式一律是 mm:ss，超過一小時用 h:mm:ss，與逐字稿相同。沒有時間資訊時填空字串。
    """

    static let systemEnglish = """
    You write meeting notes. Based on the meeting transcript the user provides, produce structured meeting notes, written entirely in English.

    Rules:
    1. Write only from the transcript and the user's notes. Do not add information that was not mentioned.
    2. The transcript comes from speech recognition, so technical terms, names and abbreviations are often misheard (the same word may appear spelled several ways). Infer the correct spelling from context and the glossary, but every proper noun or name you inferred must be listed in reviewItems for the user to confirm.
    3. Every item (summary, decisions, actionItems, openQuestions, the points in topics, the items in custom) must have a time: when that content appears in the transcript, written as mm:ss (for example 03:15; over an hour, 1:03:15), without brackets. If it combines several places, use the main one.
    4. Each transcript line is labeled with a speaker. For actionItems, owner is only someone explicitly named as responsible, preferring the speaker's name; if it is unclear, leave an empty string and add it to reviewItems.
    5. For actionItems, convert due to yyyy-MM-dd based on the "Meeting date" in the prompt (for example, "next Wednesday" must be worked out from the meeting date and its weekday, and the weekday must be right). If no deadline is mentioned, leave an empty string; if one is mentioned but unclear, leave it empty and add it to reviewItems.
    6. At most 8 reviewItems; merge similar questions. Only ask about things that affect accuracy: proper nouns, names, owners, deadlines, details of a proposal. Each item's time is the related part of the transcript. If a question can be answered with yes or no (for example, "Is Odagiri the owner?"), write it as a yes/no question and set yesNo to true; if the user has to supply the answer (for example, "What is the correct name of this system?"), set yesNo to false.
    7. "User notes" and "Bookmarks" mark what the user considers important and must be included. Information in the notes that is not in the transcript may be included, without changing its meaning.
    8. Produce only the sections specified by the "Template"; give empty arrays for the others. Produce custom in the order of the template's custom sections, with title as given by the template (translated into English if it is not in English).
    9. Split topics by subject; start is the time the topic begins.
    10. title: if the prompt gives an "Existing title", return it unchanged; otherwise write a title of at most 10 words.
    11. Times are always mm:ss, or h:mm:ss over an hour, the same as the transcript. Use an empty string when there is no time information.
    """

    static let systemJapanese = """
    あなたは議事録の作成者です。ユーザーが提供する会議の文字起こしをもとに、構造化された議事録を作成します。すべて日本語で書いてください。

    ルール：
    1. 文字起こしとユーザーのメモだけをもとに書き、言及されていない情報を補わないこと。
    2. 文字起こしは音声認識によるもので、専門用語、人名、略語がよく聞き間違えられている（同じ語が何通りもの表記で出てくることがある）。文脈と用語集から正しい表記を推測すること。ただし、推測した固有名詞や人名は必ず reviewItems に入れ、ユーザーに確認してもらうこと。
    3. すべての項目（summary、decisions、actionItems、openQuestions、topics の points、custom の items）に time を入れること。その内容が文字起こしに出てくる時刻を mm:ss（例：03:15。1 時間を超える場合は 1:03:15）で書き、括弧は付けない。複数の箇所をまとめた内容は、主な箇所の時刻にする。
    4. 文字起こしの各行には話者が付いている。actionItems の owner は担当者として明示された人だけを入れ、話者の名前を優先する。はっきりしない場合は空文字列にして reviewItems に入れる。
    5. actionItems の due は、プロンプトの「会議日」をもとに yyyy-MM-dd に換算する（例：「来週の水曜」は会議日とその曜日から計算し、曜日を正しく数えること）。期限の言及がなければ空文字列、言及があっても曖昧なら空文字列にして reviewItems に入れる。
    6. reviewItems は最大 8 問。似た質問はまとめる。記録の正確さに関わる点（固有名詞、人名、担当者、期限、案の内容）だけを質問する。各問の time は関連する箇所の時刻。「はい」か「いいえ」で答えられる質問（例：「担当者は小田切さんですか？」）はその形で書いて yesNo を true にする。答えを書いてもらう必要がある質問（例：「このシステムの正しい名前は？」）は yesNo を false にする。
    7. 「ユーザーのメモ」と「ブックマーク」はユーザーが重要だと考えた内容なので、必ず記録に含める。メモにあって文字起こしにない情報も書いてよいが、意味を変えないこと。
    8. 「テンプレート」で指定されたセクションだけを作成し、指定のないセクションは空配列にする。custom はテンプレートのカスタムセクションの順に作成し、title はテンプレートのセクション名を使う（日本語でなければ日本語に訳す）。
    9. topics は議題ごとに分け、start に議題が始まる時刻を入れる。
    10. title：プロンプトに「既存のタイトル」があればそのまま返す。なければ内容から 30 文字以内のタイトルを付ける。
    11. 時刻の形式は常に mm:ss、1 時間を超える場合は h:mm:ss で、文字起こしと同じにする。時刻の情報がない場合は空文字列にする。
    12. 文体は簡潔な常体（である調）か体言止めにする。
    """

    /// 提示詞裡的欄位名稱
    private struct Labels {
        var meetingDate, existingTitle, participants, notProvided, glossary, none, template, sections, custom, extra: String
        var userNotes, bookmarks, transcript, currentNotes, answers, answer, reviseInstructions, listSeparator, colon: String
        var speaker: (Int) -> String
        var section: (String, String) -> String

        static let zh = Labels(
            meetingDate: "會議日期", existingTitle: "現有標題", participants: "與會者", notProvided: "未提供",
            glossary: "詞彙表（正確寫法）", none: "無", template: "範本", sections: "要產生的段落",
            custom: "自訂段落（放在 custom，依序）", extra: "額外指示",
            userNotes: "使用者筆記（[時間] 是做筆記時的錄音時間）", bookmarks: "重點標記",
            transcript: "逐字稿（[時間] 說話者：內容，標 (?) 的是辨識信心低的段落）",
            currentNotes: "目前的會議紀錄（JSON）", answers: "使用者對待確認題目的回答", answer: "回答",
            reviseInstructions: """

            請依使用者的回答修正會議紀錄後，回傳完整的會議紀錄：
            - 依回答修正所有相關內容（摘要、決議、待辦、未決問題、議題、自訂段落、標題都要一併檢查），時間點維持不變。
            - 已回答的題目從 reviewItems 移除；目前會議紀錄裡沒被回答的題目原樣保留。
            - 除非回答本身帶出新的疑點，否則不要新增題目。
            """,
            listSeparator: "、", colon: "：",
            speaker: { "說話者 \($0)" },
            section: { id, label in "\(id)（\(label)）" })

        static let en = Labels(
            meetingDate: "Meeting date", existingTitle: "Existing title", participants: "Participants", notProvided: "not provided",
            glossary: "Glossary (correct spellings)", none: "none", template: "Template", sections: "Sections to produce",
            custom: "Custom sections (in custom, in this order)", extra: "Additional instructions",
            userNotes: "User notes ([time] is the recording time when the note was taken)", bookmarks: "Bookmarks",
            transcript: "Transcript ([time] speaker: text; (?) marks low-confidence segments)",
            currentNotes: "Current meeting notes (JSON)", answers: "The user's answers to the review questions", answer: "Answer",
            reviseInstructions: """

            Revise the meeting notes according to the user's answers and return the complete notes:
            - Fix everything related to each answer (check the summary, decisions, action items, open questions, topics, custom sections and title), keeping the times unchanged.
            - Remove answered questions from reviewItems; keep unanswered questions from the current notes as they are.
            - Do not add new questions unless an answer raises a new doubt.
            """,
            listSeparator: ", ", colon: ": ",
            speaker: { "Speaker \($0)" },
            section: { id, label in "\(id) (\(label))" })

        static let ja = Labels(
            meetingDate: "会議日", existingTitle: "既存のタイトル", participants: "参加者", notProvided: "未記入",
            glossary: "用語集（正しい表記）", none: "なし", template: "テンプレート", sections: "作成するセクション",
            custom: "カスタムセクション（custom に順番どおり）", extra: "追加の指示",
            userNotes: "ユーザーのメモ（[時刻] はメモを取ったときの録音時刻）", bookmarks: "ブックマーク",
            transcript: "文字起こし（[時刻] 話者：内容。(?) は認識の信頼度が低い部分）",
            currentNotes: "現在の議事録（JSON）", answers: "確認事項へのユーザーの回答", answer: "回答",
            reviseInstructions: """

            ユーザーの回答にもとづいて議事録を修正し、議事録全体を返してください：
            - 回答に関係する内容をすべて修正する（要約、決定事項、ToDo、未決事項、議題、カスタムセクション、タイトルをすべて確認する）。時刻は変えない。
            - 回答済みの質問は reviewItems から削除し、現在の議事録にある未回答の質問はそのまま残す。
            - 回答そのものが新しい疑問を生まない限り、質問を追加しない。
            """,
            listSeparator: "、", colon: "：",
            speaker: { "話者 \($0)" },
            section: { id, label in "\(id)（\(label)）" })

        static func of(_ language: MeetingLanguage) -> Labels {
            switch language {
            case .zh: return zh
            case .en: return en
            case .ja: return ja
            }
        }
    }

    static func summarize(_ c: NotesContext) -> String {
        let m = c.meeting
        let language = m.meetingLanguage
        let l = Labels.of(language)
        var lines: [String] = []
        let weekday = DateText.weekday(m.meetingDate, language: language)
        lines.append("\(l.meetingDate)\(l.colon)\(m.meetingDate)" + (language == .en ? " (\(weekday))" : "（\(weekday)）"))
        lines.append("\(l.existingTitle)\(l.colon)\(m.titleLocked ? m.title : "")")
        let participants = m.participantList
        lines.append("\(l.participants)\(l.colon)\(participants.isEmpty ? l.notProvided : participants.joined(separator: l.listSeparator))")
        lines.append("\(l.glossary)\(l.colon)\(c.glossary.isEmpty ? l.none : c.glossary.joined(separator: l.listSeparator))")
        lines.append("")
        lines.append("\(l.template)\(l.colon)\(c.template.description)")
        let sectionNames = TemplateDefinition.builtinSectionIDs.filter { c.template.includes($0) }
            .map { l.section($0, TemplateDefinition.sectionLabel($0, language: language)) }
        lines.append("\(l.sections)\(l.colon)\(sectionNames.joined(separator: l.listSeparator))")
        if !c.template.custom.isEmpty {
            lines.append("\(l.custom)\(l.colon)")
            for spec in c.template.custom { lines.append("- \(spec.title)\(l.colon)\(spec.instruction)") }
        }
        if !c.template.instruction.isEmpty { lines.append("\(l.extra)\(l.colon)\(c.template.instruction)") }
        let notes = m.userNotes.trimmingCharacters(in: .whitespacesAndNewlines)
        if !notes.isEmpty {
            lines.append("")
            lines.append("\(l.userNotes)\(l.colon)")
            lines.append(notes)
        }
        if !c.bookmarks.isEmpty {
            lines.append("")
            lines.append("\(l.bookmarks)\(l.colon)" + c.bookmarks.map { TimeFormat.string($0.time) + ($0.label.isEmpty ? "" : " \($0.label)") }
                .joined(separator: l.listSeparator))
        }
        lines.append("")
        lines.append("\(l.transcript)\(l.colon)")
        for s in c.segments {
            let speaker = s.speaker.map { (c.speakers[$0] ?? l.speaker($0)) + l.colon } ?? ""
            lines.append("[\(TimeFormat.string(s.start))] \(speaker)\(s.lowConfidence ? "(?) " : "")\(s.text)")
        }
        return lines.joined(separator: "\n")
    }

    static func revise(_ c: NotesContext, notes: MeetingNotes, answers: [ReviewAnswer]) -> String {
        let l = Labels.of(c.meeting.meetingLanguage)
        var text = summarize(c)
        text += "\n\n\(l.currentNotes)\(l.colon)\n" + notes.jsonString()
        text += "\n\n\(l.answers)\(l.colon)\n"
        for (i, a) in answers.enumerated() {
            text += "\(i + 1). [\(a.time)] \(a.question)\n   \(l.answer)\(l.colon)\(a.answer)\n"
        }
        text += l.reviseInstructions
        return text
    }
}
