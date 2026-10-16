import Foundation

/// 一則校正建議：把某一段裡的 original 改成 suggestion（design §17.4、R14.4）
struct CorrectionSuggestion: Codable, Hashable {
    /// 段落在整份逐字稿裡的位置（從 0 開始）
    var segment: Int
    var original: String
    var suggestion: String
    var reason: String
}

/// 逐字稿校正：請整理工具找出疑似聽錯的字，列成建議，使用者接受才改（D17）
enum TranscriptCorrection {
    /// 每次送出的段落數：太多容易漏看，太少要送很多次
    static let chunkSize = 150

    struct Context {
        var language: MeetingLanguage
        var title: String
        var participants: [String]
        var speakerNames: [String]
        var glossary: [String]
        var segments: [(text: String, speaker: String?)]
    }

    static let jsonSchema = """
    {"type":"object","additionalProperties":false,"required":["corrections"],"properties":{"corrections":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["segment","original","suggestion","reason"],"properties":{"segment":{"type":"integer"},"original":{"type":"string"},"suggestion":{"type":"string"},"reason":{"type":"string"}}}}}}
    """

    /// 工具說明是給模型看的，不是介面文字
    static let format = LLMClient.OutputFormat(schema: jsonSchema, toolName: "save_corrections",
                                               toolDescription: "Save transcript correction suggestions")

    // MARK: 提示詞（中文、英文、日文各一份，design §16.5、§18.4）

    static func system(_ language: MeetingLanguage) -> String {
        language.text(zh: systemChinese, en: systemEnglish, ja: systemJapanese)
    }

    private static let systemChinese = """
    你是會議逐字稿的校對員。逐字稿是語音辨識自動產生的，會有聽錯的字。你的工作是找出「很可能是辨識聽錯」的地方，提出更正建議。

    規則：
    1. 只改聽錯的字：同音或近音的錯字、寫錯的專有名詞（人名、產品名、系統名、英文術語）、上下文明顯說不通的詞。
    2. 不要改說話習慣：口語、贅字、語助詞、重複、文法不通但聽得懂的句子都保留原樣。不要潤飾、不要補字、不要刪字、不要改標點，也不要把中文翻成英文或英文翻成中文。
    3. 專有名詞的正確寫法以詞彙表、參加者、說話者名字、會議標題為準；不在這些資料裡的，只有在上下文能確定時才改。
    4. original 必須是那一段原文裡一字不差、連續出現的文字，只包含要改的詞，越短越好；suggestion 是改正後的文字。
    5. 沒有把握就不要建議，寧可漏掉也不要改錯。沒有要改的就回傳空陣列。
    6. reason 用一句話說明為什麼，例如「詞彙表裡的產品名」「上下文在講資料庫快取」。
    """

    private static let systemEnglish = """
    You proofread meeting transcripts. The transcript was produced by speech recognition and contains misheard words. Find the places that were most likely misheard and suggest corrections.

    Rules:
    1. Only fix misheard words: words that sound alike, misspelled names (people, products, systems, technical terms), and words that clearly make no sense in context.
    2. Do not change how people speak: keep filler words, repetitions, false starts and informal grammar as they are. Do not polish, add or remove words, change punctuation, or translate between languages.
    3. Use the glossary, participants, speaker names and meeting title for the correct spelling of names. For names not listed, only correct them when the context makes it certain.
    4. "original" must be text that appears exactly and contiguously in that segment, containing only the words to fix, as short as possible; "suggestion" is the corrected text.
    5. If you are not sure, do not suggest anything. Missing a fix is better than making a wrong one. Return an empty array if nothing needs fixing.
    6. "reason" is one sentence, for example "product name from the glossary" or "the discussion is about database caching".
    """

    private static let systemJapanese = """
    あなたは会議の文字起こしの校正者です。文字起こしは音声認識で自動生成されたもので、聞き間違いが含まれています。「音声認識の聞き間違いである可能性が高い」箇所を見つけ、修正案を出してください。

    ルール：
    1. 聞き間違いだけを直す：同音や似た音の誤り、表記を誤った固有名詞（人名、製品名、システム名、英語の用語）、文脈上明らかに意味が通らない語。
    2. 話し方は直さない：話し言葉、言いよどみ、フィラー（えー、あのー など）、繰り返し、文法が崩れていても意味の通じる文はそのまま残す。言い換え、語の追加や削除、句読点の変更、日本語と英語の間の翻訳、漢字・ひらがな・カタカナの表記の統一はしない。
    3. 固有名詞の正しい表記は、用語集、参加者、話者名、会議タイトルに従う。これらにない語は、文脈から確実な場合だけ直す。
    4. original はその段落の原文に一字一句そのまま連続して出てくる文字列で、直したい語だけを含め、できるだけ短くする。suggestion は修正後の文字列。
    5. 自信がなければ提案しない。間違って直すより見逃すほうがよい。直す箇所がなければ空の配列を返す。
    6. reason は理由を一文で書く。例：「用語集にある製品名」「データベースのキャッシュについての話」。
    """

    static func prompt(_ c: Context, range: Range<Int>) -> String {
        let l = c.language
        let separator = l.text(zh: "、", en: ", ", ja: "、"), colon = l.text(zh: "：", en: ": ", ja: "：")
        let none = l.text(zh: "（無）", en: "(none)", ja: "（なし）")
        var lines: [String] = []
        lines.append(l.text(zh: "會議標題", en: "Meeting title", ja: "会議タイトル") + colon + (c.title.isEmpty ? none : c.title))
        if !c.participants.isEmpty {
            lines.append(l.text(zh: "參加者", en: "Participants", ja: "参加者") + colon + c.participants.joined(separator: separator))
        }
        if !c.speakerNames.isEmpty {
            lines.append(l.text(zh: "說話者", en: "Speakers", ja: "話者") + colon + c.speakerNames.joined(separator: separator))
        }
        let glossary = c.glossary.isEmpty ? none : c.glossary.joined(separator: separator)
        lines.append(l.text(zh: "詞彙表（正確寫法）", en: "Glossary (correct spellings)", ja: "用語集（正しい表記）") + colon + glossary)
        lines.append("")
        lines.append(l.text(zh: "逐字稿（每行開頭的數字是段落編號，segment 欄位填這個數字）：",
                            en: "Transcript (the number at the start of each line is the segment number; put it in the \"segment\" field):",
                            ja: "文字起こし（各行の先頭の数字は段落番号。segment 欄にこの数字を入れる）："))
        for i in range {
            let segment = c.segments[i]
            let speaker = segment.speaker.map { $0 + colon } ?? ""
            lines.append("[\(i)] \(speaker)\(segment.text)")
        }
        return lines.joined(separator: "\n")
    }

    // MARK: 送出與驗證

    /// 分段送出，回傳驗證過的建議；onProgress 回報 0～1
    @MainActor
    static func suggest(_ context: Context, provider: LLMProvider, model: String,
                        onStart: (Subprocess) -> Void, onProgress: (Double) -> Void) async throws -> [CorrectionSuggestion] {
        var result: [CorrectionSuggestion] = []
        let count = context.segments.count
        var start = 0
        while start < count {
            let range = start..<min(start + chunkSize, count)
            let data = try await LLMClient.generateJSON(provider: provider, model: model, system: system(context.language),
                                                        prompt: prompt(context, range: range), format: format, onStart: onStart)
            struct Output: Decodable { var corrections: [CorrectionSuggestion] }
            let output: Output
            do { output = try JSONDecoder().decode(Output.self, from: data) }
            catch { throw LLMError.badOutput(error.localizedDescription) }
            result += validate(output.corrections, context: context, range: range)
            start = range.upperBound
            onProgress(Double(start) / Double(count))
        }
        return result
    }

    /// 丟掉不能用的建議：段落編號不在這一批、original 不在那一段的文字裡、改了跟沒改一樣、同一處重複
    static func validate(_ raw: [CorrectionSuggestion], context: Context, range: Range<Int>) -> [CorrectionSuggestion] {
        var seen = Set<String>()
        return raw.compactMap { item in
            var s = item
            s.original = s.original.trimmingCharacters(in: .whitespaces)
            s.suggestion = s.suggestion.trimmingCharacters(in: .whitespaces)
            guard range.contains(s.segment), !s.original.isEmpty, s.original != s.suggestion,
                  context.segments[s.segment].text.contains(s.original),
                  seen.insert("\(s.segment)\u{1F}\(s.original)").inserted else { return nil }
            return s
        }
    }

    /// 套用一則建議：把那一段裡第一個 original 換成 suggestion；找不到（之後被改過）就回傳 nil
    static func apply(_ s: CorrectionSuggestion, to text: String) -> String? {
        guard let range = text.range(of: s.original) else { return nil }
        return text.replacingCharacters(in: range, with: s.suggestion)
    }

    /// 還原：把 suggestion 換回 original
    static func revert(_ s: CorrectionSuggestion, in text: String) -> String? {
        guard let range = text.range(of: s.suggestion) else { return nil }
        return text.replacingCharacters(in: range, with: s.original)
    }
}
