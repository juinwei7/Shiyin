import Foundation

/// 對會議提問（design §21.8、R19.6、D32）：單場用會議紀錄和逐字稿回答；跨會議先抽搜尋關鍵詞，再用全文搜尋找到的片段回答
enum AskPrompt {
    struct Citation: Codable, Hashable {
        /// 跨會議時是哪一場會議；單場時沒有
        var meetingId: Int64?
        var time: String
        var quote: String
    }

    struct Answer: Codable, Hashable {
        var answer: String
        /// 提供的內容裡有沒有提到
        var found: Bool
        var citations: [Citation]
        /// 跨會議：用了哪些關鍵詞、送了哪些會議（存起來給畫面顯示「參考了 N 場會議」）
        var keywords: [String]?
        var meetingIds: [Int64]?
    }

    /// 跨會議時一場會議的資料：會議紀錄的摘要、決議，加上搜尋命中的段落（前後各一段）
    struct MeetingBlock {
        var id: Int64
        var title: String
        var date: String
        var summary: [String]
        var lines: [String]      // [時間] 說話者：內容
    }

    // MARK: 輸出格式

    private static let citationItems = #"{"type":"object","additionalProperties":false,"required":["time","quote"],"properties":{"time":{"type":"string"},"quote":{"type":"string"}}}"#
    private static let crossCitationItems = #"{"type":"object","additionalProperties":false,"required":["meetingId","time","quote"],"properties":{"meetingId":{"type":"integer"},"time":{"type":"string"},"quote":{"type":"string"}}}"#

    static let meetingSchema = #"{"type":"object","additionalProperties":false,"required":["answer","found","citations"],"properties":{"answer":{"type":"string"},"found":{"type":"boolean"},"citations":{"type":"array","items":"# + citationItems + "}}}"
    static let allSchema = #"{"type":"object","additionalProperties":false,"required":["answer","found","citations"],"properties":{"answer":{"type":"string"},"found":{"type":"boolean"},"citations":{"type":"array","items":"# + crossCitationItems + "}}}"
    static let keywordsSchema = #"{"type":"object","additionalProperties":false,"required":["keywords"],"properties":{"keywords":{"type":"array","items":{"type":"string"}}}}"#

    /// 工具說明是給模型看的，不是介面文字
    static let meetingFormat = LLMClient.OutputFormat(schema: meetingSchema, toolName: "save_answer", toolDescription: "Save the answer to the question")
    static let allFormat = LLMClient.OutputFormat(schema: allSchema, toolName: "save_answer", toolDescription: "Save the answer to the question")
    static let keywordsFormat = LLMClient.OutputFormat(schema: keywordsSchema, toolName: "save_search_keywords",
                                                      toolDescription: "Save the search keywords for the question")

    // MARK: 提示詞（中文、英文、日文各一份）

    static func meetingSystem(_ l: MeetingLanguage) -> String {
        l.text(zh: """
        你是會議助理，只根據提供的這場會議的內容（會議紀錄和逐字稿）回答使用者的問題。

        規則：
        1. 只用提供的內容回答，不要推測，也不要補充外部知識。內容裡沒有提到，就說這場會議沒有提到，found 填 false。
        2. 簡潔、直接回答問題；需要列點時每點用「- 」開頭。
        3. 每個重點都要有依據，放在 citations：time 照逐字稿的 [mm:ss] 或 [h:mm:ss] 寫，quote 是那一段的原文（節錄，60 字以內）。
        4. 用提問的語言回答。
        """, en: """
        You are a meeting assistant. Answer the user's question using only the content provided from this meeting (the meeting notes and transcript).

        Rules:
        1. Use only the provided content. Do not guess or add outside knowledge. If the content does not mention it, say the meeting did not cover it and set "found" to false.
        2. Be concise and answer the question directly. Use "- " at the start of each point when listing.
        3. Back every point with a citation: "time" as written in the transcript ([mm:ss] or [h:mm:ss]), "quote" the original words of that segment (an excerpt, at most 30 words).
        4. Answer in the language of the question.
        """, ja: """
        あなたは会議アシスタントです。提供されたこの会議の内容（議事録と文字起こし）だけに基づいて、ユーザーの質問に答えてください。

        ルール：
        1. 提供された内容だけで答える。推測や外部の知識の補足はしない。内容に出てこない場合は、この会議では触れられていないと答え、found を false にする。
        2. 簡潔に、質問に直接答える。箇条書きは各行を「- 」で始める。
        3. 要点ごとに根拠を citations に入れる：time は文字起こしの [mm:ss] または [h:mm:ss] のとおり、quote はその段落の原文（抜粋、60 字以内）。
        4. 質問と同じ言語で答える。
        """)
    }

    static func allSystem(_ l: MeetingLanguage) -> String {
        l.text(zh: """
        你是會議助理，只根據提供的幾場會議的片段回答使用者的問題。每場會議有編號、標題、日期、會議紀錄的摘要，以及跟問題有關的逐字稿片段。

        規則：
        1. 只用提供的內容回答，不要推測，也不要補充外部知識。都沒有提到，就說找不到相關的內容，found 填 false。
        2. 簡潔、直接回答問題；不同會議的內容要說清楚是哪一場（標題或日期）。需要列點時每點用「- 」開頭。
        3. 每個重點都要有依據，放在 citations：meetingId 是那場會議的編號，time 照逐字稿片段的 [mm:ss] 或 [h:mm:ss] 寫，quote 是原文（節錄，60 字以內）。
        4. 用提問的語言回答。
        """, en: """
        You are a meeting assistant. Answer the user's question using only the excerpts provided from several meetings. Each meeting has an ID, title, date, a summary from its notes, and transcript excerpts related to the question.

        Rules:
        1. Use only the provided content. Do not guess or add outside knowledge. If none of it is relevant, say nothing related was found and set "found" to false.
        2. Be concise and answer the question directly. Make clear which meeting each point comes from (title or date). Use "- " at the start of each point when listing.
        3. Back every point with a citation: "meetingId" is the meeting's ID, "time" as written in the excerpt ([mm:ss] or [h:mm:ss]), "quote" the original words (an excerpt, at most 30 words).
        4. Answer in the language of the question.
        """, ja: """
        あなたは会議アシスタントです。提供された複数の会議の抜粋だけに基づいて、ユーザーの質問に答えてください。各会議には番号、タイトル、日付、議事録の要約、質問に関係する文字起こしの抜粋があります。

        ルール：
        1. 提供された内容だけで答える。推測や外部の知識の補足はしない。関係する内容がなければ、見つからなかったと答え、found を false にする。
        2. 簡潔に、質問に直接答える。どの会議の内容か（タイトルか日付）をはっきりさせる。箇条書きは各行を「- 」で始める。
        3. 要点ごとに根拠を citations に入れる：meetingId はその会議の番号、time は抜粋の [mm:ss] または [h:mm:ss] のとおり、quote は原文（抜粋、60 字以内）。
        4. 質問と同じ言語で答える。
        """)
    }

    static func keywordsSystem(_ l: MeetingLanguage) -> String {
        l.text(zh: """
        把使用者對會議的問題，轉成用來全文搜尋會議逐字稿的關鍵詞。

        規則：
        1. 3～8 個關鍵詞，最重要的放前面。
        2. 包含同義詞、常見的說法、中英文寫法、簡稱（例如「寄信」也列「郵件」「mail」「SendGrid」）。
        3. 每個關鍵詞是一個詞或短語，至少 2 個字，不要整句、不要「會議」「討論」這種每場都有的詞。
        """, en: """
        Turn the user's question about their meetings into keywords for a full-text search of meeting transcripts.

        Rules:
        1. 3 to 8 keywords, most important first.
        2. Include synonyms, common phrasings, spellings in other languages and abbreviations (for "email", also list "mail", "SendGrid").
        3. Each keyword is a word or short phrase of at least 2 characters. No full sentences, and no words every meeting has, like "meeting" or "discuss".
        """, ja: """
        会議についてのユーザーの質問を、会議の文字起こしを全文検索するためのキーワードに変換してください。

        ルール：
        1. キーワードは 3～8 個、重要なものから並べる。
        2. 同義語、よく使う言い方、英語・日本語の表記、略称も入れる（「メール送信」なら「メール」「mail」「SendGrid」も）。
        3. 各キーワードは語か短いフレーズで 2 文字以上。文全体や、「会議」「議論」のようにどの会議にもある語は入れない。
        """)
    }

    static func meetingPrompt(meeting: Meeting, notes: MeetingNotes?, lines: [String], history: [(question: String, answer: String)],
                              question: String) -> String {
        let l = meeting.meetingLanguage
        let colon = l.text(zh: "：", en: ": ", ja: "：")
        var out: [String] = []
        out.append(l.text(zh: "會議標題", en: "Meeting title", ja: "会議タイトル") + colon + meeting.displayTitle)
        out.append(l.text(zh: "會議日期", en: "Meeting date", ja: "会議の日付") + colon + meeting.meetingDate)
        if !meeting.participantList.isEmpty {
            out.append(l.text(zh: "參加者", en: "Participants", ja: "参加者") + colon + meeting.participantList.joined(separator: l.text(zh: "、", en: ", ", ja: "、")))
        }
        if let notes {
            out.append("")
            out.append(l.text(zh: "會議紀錄（JSON）", en: "Meeting notes (JSON)", ja: "議事録（JSON）") + colon)
            out.append(notes.jsonString())
        }
        out.append("")
        out.append(l.text(zh: "逐字稿（[時間] 說話者：內容）", en: "Transcript ([time] speaker: text)", ja: "文字起こし（[時刻] 話者：内容）") + colon)
        out += lines
        if !history.isEmpty {
            out.append("")
            out.append(l.text(zh: "之前的問答（追問時參考）", en: "Earlier questions and answers (for follow-ups)", ja: "これまでの質問と回答（追加の質問の参考）") + colon)
            for item in history {
                out.append("Q" + colon + item.question)
                out.append("A" + colon + item.answer)
            }
        }
        out.append("")
        out.append(l.text(zh: "問題", en: "Question", ja: "質問") + colon + question)
        return out.joined(separator: "\n")
    }

    static func allPrompt(_ l: MeetingLanguage, blocks: [MeetingBlock], question: String) -> String {
        let colon = l.text(zh: "：", en: ": ", ja: "：")
        var out: [String] = []
        for block in blocks {
            out.append(l.text(zh: "【會議 \(block.id)】", en: "[Meeting \(block.id)]", ja: "【会議 \(block.id)】") + " \(block.title)（\(block.date)）")
            if !block.summary.isEmpty {
                out.append(l.text(zh: "摘要與決議", en: "Summary and decisions", ja: "要約と決定事項") + colon)
                out += block.summary.map { "- " + $0 }
            }
            if !block.lines.isEmpty {
                out.append(l.text(zh: "逐字稿片段", en: "Transcript excerpts", ja: "文字起こしの抜粋") + colon)
                out += block.lines
            }
            out.append("")
        }
        out.append(l.text(zh: "問題", en: "Question", ja: "質問") + colon + question)
        return out.joined(separator: "\n")
    }

    static func keywordsPrompt(_ l: MeetingLanguage, question: String) -> String {
        l.text(zh: "問題：", en: "Question: ", ja: "質問：") + question
    }

    // MARK: 送出

    @MainActor
    static func generate<T: Decodable>(_ type: T.Type, provider: LLMProvider, model: String, system: String, prompt: String,
                                       format: LLMClient.OutputFormat, onStart: (Subprocess) -> Void) async throws -> T {
        let data = try await LLMClient.generateJSON(provider: provider, model: model, system: system, prompt: prompt, format: format, onStart: onStart)
        do { return try JSONDecoder().decode(T.self, from: data) }
        catch { throw LLMError.badOutput(error.localizedDescription + provider.badOutputHint) }
    }

    /// 丟掉不能用的出處：時間格式不對、跨會議時不是提供的會議
    static func validate(_ answer: Answer, meetingIds: Set<Int64>?) -> Answer {
        var result = answer
        result.answer = answer.answer.trimmingCharacters(in: .whitespacesAndNewlines)
        result.citations = answer.citations.compactMap { c in
            var c = c
            c.time = TimeFormat.clean(c.time)
            c.quote = c.quote.trimmingCharacters(in: .whitespacesAndNewlines)
            guard TimeFormat.seconds(c.time) != nil else { return nil }
            if let meetingIds {
                guard let id = c.meetingId, meetingIds.contains(id) else { return nil }
            } else {
                c.meetingId = nil
            }
            return c
        }
        return result
    }
}
