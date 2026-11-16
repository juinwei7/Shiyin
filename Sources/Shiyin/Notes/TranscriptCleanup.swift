import Foundation

/// 精簡逐字稿（R96、design §70）：請整理工具去掉贅字、重複、說到一半改口的部分，一句一句對回原本的段落。
/// 不改意思、不摘要、不合併或拆開句子；整句都是贅字時給空字串（畫面上不顯示那一句）
enum TranscriptCleanup {
    /// 每次送出的句數（同校正，design §17.4）
    static let chunkSize = 120

    struct Context {
        var language: MeetingLanguage
        var segments: [(text: String, speaker: String?)]
    }

    struct Line: Codable, Hashable {
        /// 句子在整份逐字稿裡的位置（從 0 開始）
        var segment: Int
        var text: String
    }

    static let jsonSchema = """
    {"type":"object","additionalProperties":false,"required":["lines"],"properties":{"lines":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["segment","text"],"properties":{"segment":{"type":"integer"},"text":{"type":"string"}}}}}}
    """

    /// 工具說明是給模型看的，不是介面文字
    static let format = LLMClient.OutputFormat(schema: jsonSchema, toolName: "save_clean_transcript",
                                               toolDescription: "Save the cleaned-up transcript lines")

    // MARK: 提示詞（中文、英文、日文各一份，design §16.5、§18.4）

    /// 共用別的語言那份的精調語言（粵語）最後接一段逐字稿的說明（同校正，design §41.2）
    static func system(_ language: MeetingLanguage) -> String {
        let text = language.prompt(zh: systemChinese, en: systemEnglish, ja: systemJapanese)
        guard let note = language.profile.transcriptNote?.correction else { return text }
        return text + "\n\n" + note
    }

    private static let systemChinese = """
    你是會議逐字稿的編輯。逐字稿是語音辨識自動產生的口語，你的工作是把每一句整理成好讀的版本。

    規則：
    1. 去掉贅字和口頭禪（嗯、呃、啊、那個、就是、然後、對對對這類沒有意思的用法）、重複的字詞、說到一半改口的部分（只留改口後的說法）。
    2. 不改意思、不摘要、不補沒說的內容、不翻譯；專有名詞、人名、數字、日期照原文。每一句獨立整理，不要把兩句合併或把一句拆開。
    3. 可以補上或整理標點，讓句子通順。
    4. segment 填句首的編號；整句都是贅字（例如「嗯。」「對對對。」）時 text 給空字串。
    5. 只回傳有改的句子；不用改的句子不要列。
    """

    private static let systemEnglish = """
    You edit meeting transcripts. The transcript is spoken language produced by speech recognition; make each line easy to read.

    Rules:
    1. Remove filler words and verbal tics (um, uh, like, you know, I mean, so, when they carry no meaning), repeated words, and false starts (keep only what the speaker settled on).
    2. Do not change the meaning, summarize, add anything that wasn't said, or translate; keep names, numbers and dates as they are. Clean each line on its own; do not merge lines or split a line.
    3. You may add or fix punctuation so the line reads well.
    4. Put the number at the start of the line in "segment". If the whole line is filler (for example "Um." or "Yeah, yeah."), set "text" to an empty string.
    5. Return only the lines you changed; leave out lines that need no changes.
    """

    private static let systemJapanese = """
    あなたは会議の文字起こしの編集者です。文字起こしは音声認識で作られた話し言葉です。各行を読みやすく整えてください。

    ルール：
    1. フィラーや口癖（えー、あのー、えっと、まあ、なんか など意味のない使い方）、繰り返し、言い直しの前半（言い直した後だけを残す）を取り除く。
    2. 意味を変えない。要約しない。言っていない内容を足さない。翻訳しない。固有名詞、人名、数字、日付は原文のまま。各行は独立して整え、行をまとめたり分けたりしない。
    3. 読みやすくなるように句読点を補ったり整えたりしてよい。
    4. segment には行頭の番号を入れる。行全体がフィラー（例：「えー。」「はいはい。」）なら text は空文字列にする。
    5. 変更した行だけを返し、変更のない行は含めない。
    """

    static func prompt(_ c: Context, range: Range<Int>) -> String {
        let l = c.language
        let colon = l.prompt(zh: "：", en: ": ", ja: "：")
        var lines = [l.prompt(zh: "逐字稿（每行開頭的數字是句子編號，segment 欄位填這個數字）：",
                              en: "Transcript (the number at the start of each line is the line number; put it in the \"segment\" field):",
                              ja: "文字起こし（各行の先頭の数字は行番号。segment 欄にこの数字を入れる）：")]  // l10n-skip
        for i in range {
            let segment = c.segments[i]
            let speaker = segment.speaker.map { $0 + colon } ?? ""
            lines.append("[\(i)] \(speaker)\(segment.text)")
        }
        return lines.joined(separator: "\n")
    }

    // MARK: 送出與驗證

    /// 分段送出，回傳「第幾句 → 精簡後的文字」（只有改過的句子；空字串是整句都是贅字）；onProgress 回報 0～1
    @MainActor
    static func clean(_ context: Context, provider: LLMProvider, model: String,
                      onStart: (Subprocess) -> Void, onProgress: (Double) -> Void) async throws -> [Int: String] {
        var result: [Int: String] = [:]
        let count = context.segments.count
        var start = 0
        while start < count {
            let range = start..<min(start + chunkSize, count)
            let data = try await LLMClient.generateJSON(provider: provider, model: model, system: system(context.language),
                                                        prompt: prompt(context, range: range), format: format, onStart: onStart)
            struct Output: Decodable { var lines: [Line] }
            let output: Output
            do { output = try JSONDecoder().decode(Output.self, from: data) }
            catch { throw LLMError.badOutput(error.localizedDescription + provider.badOutputHint) }
            for line in validate(output.lines, context: context, range: range) { result[line.segment] = line.text }
            start = range.upperBound
            onProgress(Double(start) / Double(count))
        }
        return result
    }

    /// 丟掉不能用的句子：編號不在這一批、同一句重複（留第一個）、跟原文一樣、比原文長太多（模型在補字或改寫，照原文）
    static func validate(_ raw: [Line], context: Context, range: Range<Int>) -> [Line] {
        var seen = Set<Int>()
        return raw.compactMap { item in
            let text = item.text.trimmingCharacters(in: .whitespacesAndNewlines)
            guard range.contains(item.segment), seen.insert(item.segment).inserted else { return nil }
            let original = context.segments[item.segment].text
            guard text != original, text.count <= original.count + max(10, original.count / 5) else { return nil }
            return Line(segment: item.segment, text: text)
        }
    }
}
