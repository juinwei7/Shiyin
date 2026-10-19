import Foundation

/// AI 猜說話者（design §21.5、R19.2、D30）：從對話內容猜出還沒有名字的說話者是誰，列成建議，使用者按「套用」才改名
enum SpeakerNaming {
    struct Suggestion: Codable, Hashable {
        var speaker: Int
        var name: String
        var reason: String
        var time: String
    }

    struct Context {
        var language: MeetingLanguage
        var meetingDate: String
        var title: String
        var participants: [String]
        /// 認識的人（聲紋名單）：名字寫法可以參考
        var knownPeople: [String]
        var glossary: [String]
        /// 要猜的說話者編號（還是預設名稱的）
        var unnamed: [Int]
        /// 說話者編號 → 逐字稿上顯示的名字（已經有名字的直接寫名字）
        var speakerName: (Int) -> String
        var segments: [(start: Double, speaker: Int?, text: String)]
    }

    static let jsonSchema = """
    {"type":"object","additionalProperties":false,"required":["speakerNames"],"properties":{"speakerNames":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["speaker","name","reason","time"],"properties":{"speaker":{"type":"integer"},"name":{"type":"string"},"reason":{"type":"string"},"time":{"type":"string"}}}}}}
    """

    /// 工具說明是給模型看的，不是介面文字
    static let format = LLMClient.OutputFormat(schema: jsonSchema, toolName: "save_speaker_names",
                                               toolDescription: "Save the speaker names inferred from the transcript")

    // MARK: 提示詞（中文、英文、日文各一份）

    static func system(_ language: MeetingLanguage) -> String {
        language.text(zh: systemChinese, en: systemEnglish, ja: systemJapanese)
    }

    private static let systemChinese = """
    你要從會議逐字稿判斷「說話者 N」是誰。逐字稿的說話者是用聲音自動分出來的，只有編號。

    規則：
    1. 只在逐字稿裡有明確根據時才猜，例如：有人叫了某人的名字，下一段就是那位說話者回應；說話者自我介紹（「我是…」）；主持人點名之後那位說話者開始講；別人說「謝謝某某的報告」而剛報告完的是那位說話者。
    2. 只猜「要猜的說話者」裡的編號。沒有把握的就不要列，寧可少列，不要猜錯。
    3. 名字照逐字稿、參加者、認識的人、詞彙表裡的寫法。只有職稱或稱謂（例如「老闆」「經理」「主任」）不算名字，不要列。
    4. 同一個名字只能給一位說話者。
    5. reason 用一句話說明根據；time 是最能證明的那一段的時間，照逐字稿裡的 [mm:ss] 或 [h:mm:ss] 寫。
    6. 都沒有把握就回傳空陣列。
    """

    private static let systemEnglish = """
    Work out who "Speaker N" is in a meeting transcript. The speakers were separated automatically by voice and only have numbers.

    Rules:
    1. Only guess when the transcript gives clear evidence, for example: someone addresses a person by name and that speaker answers in the next segment; a speaker introduces themselves ("I'm ..."); the host calls on someone and that speaker starts talking; someone says "thanks for the update, X" right after that speaker finished.
    2. Only guess the speakers listed under "Speakers to identify". Leave out anyone you are not sure about. Listing fewer is better than guessing wrong.
    3. Spell names the way they appear in the transcript, participants, known people or glossary. A job title or form of address alone ("the boss", "manager") is not a name; do not list it.
    4. Each name can go to only one speaker.
    5. "reason" is one sentence explaining the evidence; "time" is the time of the segment that shows it best, written as in the transcript ([mm:ss] or [h:mm:ss]).
    6. If you are not sure about anyone, return an empty array.
    """

    private static let systemJapanese = """
    会議の文字起こしから「話者 N」が誰かを判断してください。話者は声で自動的に分けたもので、番号しかありません。

    ルール：
    1. 文字起こしに明確な根拠があるときだけ推測する。例：誰かが名前を呼び、次の段落でその話者が応答している。話者が自己紹介している（「〇〇です」）。司会者が指名した直後にその話者が話し始めている。その話者が話し終えた直後に「〇〇さん、ありがとうございます」と言われている。
    2. 「判断する話者」に挙げた番号だけを推測する。自信がない話者は挙げない。間違えるより少なく挙げるほうがよい。
    3. 名前の表記は文字起こし、参加者、知っている人、用語集に従う。役職や呼称だけ（「部長」「社長」など）は名前ではないので挙げない。
    4. 同じ名前は一人の話者にしか付けない。
    5. reason は根拠を一文で書く。time はそれを最もよく示す段落の時刻を、文字起こしの [mm:ss] または [h:mm:ss] のとおりに書く。
    6. 誰についても自信がなければ空の配列を返す。
    """

    static func prompt(_ c: Context) -> String {
        let l = c.language
        let separator = l.text(zh: "、", en: ", ", ja: "、"), colon = l.text(zh: "：", en: ": ", ja: "：")
        let none = l.text(zh: "（無）", en: "(none)", ja: "（なし）")
        func list(_ items: [String]) -> String { items.isEmpty ? none : items.joined(separator: separator) }
        var lines: [String] = []
        lines.append(l.text(zh: "會議日期", en: "Meeting date", ja: "会議の日付") + colon + c.meetingDate)
        lines.append(l.text(zh: "會議標題", en: "Meeting title", ja: "会議タイトル") + colon + (c.title.isEmpty ? none : c.title))
        lines.append(l.text(zh: "參加者", en: "Participants", ja: "参加者") + colon + list(c.participants))
        lines.append(l.text(zh: "認識的人（之前的會議出現過的名字，寫法可以參考）", en: "Known people (names from earlier meetings, for spelling)",
                            ja: "知っている人（以前の会議に出てきた名前。表記の参考に）") + colon + list(c.knownPeople))  // l10n-skip
        lines.append(l.text(zh: "詞彙表", en: "Glossary", ja: "用語集") + colon + list(c.glossary))
        lines.append(l.text(zh: "要猜的說話者", en: "Speakers to identify", ja: "判断する話者") + colon + list(c.unnamed.map(c.speakerName)))
        lines.append("")
        lines.append(l.text(zh: "逐字稿（[時間] 說話者：內容）：", en: "Transcript ([time] speaker: text):", ja: "文字起こし（[時刻] 話者：内容）："))
        for s in c.segments {
            let speaker = s.speaker.map { c.speakerName($0) + colon } ?? ""
            lines.append("[\(TimeFormat.string(s.start))] \(speaker)\(s.text)")
        }
        return lines.joined(separator: "\n")
    }

    @MainActor
    static func suggest(_ context: Context, provider: LLMProvider, model: String,
                        onStart: (Subprocess) -> Void) async throws -> [Suggestion] {
        let data = try await LLMClient.generateJSON(provider: provider, model: model, system: system(context.language),
                                                    prompt: prompt(context), format: format, onStart: onStart)
        struct Output: Decodable { var speakerNames: [Suggestion] }
        let output: Output
        do { output = try JSONDecoder().decode(Output.self, from: data) }
        catch { throw LLMError.badOutput(error.localizedDescription + provider.badOutputHint) }
        return validate(output.speakerNames, context: context)
    }

    /// 丟掉不能用的建議：不是要猜的說話者、名字是空的或是預設名稱、太長、同一位說話者或同一個名字重複
    static func validate(_ raw: [Suggestion], context: Context) -> [Suggestion] {
        var speakers = Set<Int>(), names = Set<String>()
        return raw.compactMap { item in
            var s = item
            s.name = s.name.trimmingCharacters(in: .whitespacesAndNewlines)
            s.reason = s.reason.trimmingCharacters(in: .whitespacesAndNewlines)
            s.time = TimeFormat.clean(s.time)
            guard context.unnamed.contains(s.speaker), !s.name.isEmpty, s.name.count <= 30,
                  !AppDatabase.isDefaultSpeakerName(s.name),
                  speakers.insert(s.speaker).inserted, names.insert(s.name.lowercased()).inserted else { return nil }
            return s
        }
    }
}
