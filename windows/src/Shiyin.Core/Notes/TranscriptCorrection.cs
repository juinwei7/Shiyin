using Shiyin.Model;
using Shiyin.Text;

namespace Shiyin.Notes;

/// <summary>一則校正建議：把某一段裡的 original 改成 suggestion（M§17.4）</summary>
public sealed record CorrectionSuggestion(int Segment, string Original, string Suggestion, string Reason);

/// <summary>逐字稿校正：請整理工具找出疑似聽錯的字，列成建議，使用者接受才改（D17）</summary>
public static class TranscriptCorrection
{
    /// <summary>每次送出的段落數</summary>
    public const int ChunkSize = 150;

    public sealed record Context(
        MeetingLanguage Language, string Title, IReadOnlyList<string> Participants, IReadOnlyList<string> SpeakerNames,
        IReadOnlyList<string> Glossary, IReadOnlyList<(string Text, string? Speaker)> Segments);

    public const string JsonSchema = """{"type":"object","additionalProperties":false,"required":["corrections"],"properties":{"corrections":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["segment","original","suggestion","reason"],"properties":{"segment":{"type":"integer"},"original":{"type":"string"},"suggestion":{"type":"string"},"reason":{"type":"string"}}}}}}""";

    /// <summary>工具說明是給模型看的，不是介面文字</summary>
    public static readonly OutputFormat Format = new(JsonSchema, "save_corrections", "Save transcript correction suggestions");

    public static string System(MeetingLanguage language) => language.Text(SystemChinese, SystemEnglish, SystemJapanese);

    const string SystemChinese = """
        你是會議逐字稿的校對員。逐字稿是語音辨識自動產生的，會有聽錯的字。你的工作是找出「很可能是辨識聽錯」的地方，提出更正建議。

        規則：
        1. 只改聽錯的字：同音或近音的錯字、寫錯的專有名詞（人名、產品名、系統名、英文術語）、上下文明顯說不通的詞。
        2. 不要改說話習慣：口語、贅字、語助詞、重複、文法不通但聽得懂的句子都保留原樣。不要潤飾、不要補字、不要刪字、不要改標點，也不要把中文翻成英文或英文翻成中文。
        3. 專有名詞的正確寫法以詞彙表、參加者、說話者名字、會議標題為準；不在這些資料裡的，只有在上下文能確定時才改。
        4. original 必須是那一段原文裡一字不差、連續出現的文字，只包含要改的詞，越短越好；suggestion 是改正後的文字。
        5. 沒有把握就不要建議，寧可漏掉也不要改錯。沒有要改的就回傳空陣列。
        6. reason 用一句話說明為什麼，例如「詞彙表裡的產品名」「上下文在講資料庫快取」。
        """;

    const string SystemEnglish = """
        You proofread meeting transcripts. The transcript was produced by speech recognition and contains misheard words. Find the places that were most likely misheard and suggest corrections.

        Rules:
        1. Only fix misheard words: words that sound alike, misspelled names (people, products, systems, technical terms), and words that clearly make no sense in context.
        2. Do not change how people speak: keep filler words, repetitions, false starts and informal grammar as they are. Do not polish, add or remove words, change punctuation, or translate between languages.
        3. Use the glossary, participants, speaker names and meeting title for the correct spelling of names. For names not listed, only correct them when the context makes it certain.
        4. "original" must be text that appears exactly and contiguously in that segment, containing only the words to fix, as short as possible; "suggestion" is the corrected text.
        5. If you are not sure, do not suggest anything. Missing a fix is better than making a wrong one. Return an empty array if nothing needs fixing.
        6. "reason" is one sentence, for example "product name from the glossary" or "the discussion is about database caching".
        """;

    const string SystemJapanese = """
        あなたは会議の文字起こしの校正者です。文字起こしは音声認識で自動生成されたもので、聞き間違いが含まれています。「音声認識の聞き間違いである可能性が高い」箇所を見つけ、修正案を出してください。

        ルール：
        1. 聞き間違いだけを直す：同音や似た音の誤り、表記を誤った固有名詞（人名、製品名、システム名、英語の用語）、文脈上明らかに意味が通らない語。
        2. 話し方は直さない：話し言葉、言いよどみ、フィラー（えー、あのー など）、繰り返し、文法が崩れていても意味の通じる文はそのまま残す。言い換え、語の追加や削除、句読点の変更、日本語と英語の間の翻訳、漢字・ひらがな・カタカナの表記の統一はしない。
        3. 固有名詞の正しい表記は、用語集、参加者、話者名、会議タイトルに従う。これらにない語は、文脈から確実な場合だけ直す。
        4. original はその段落の原文に一字一句そのまま連続して出てくる文字列で、直したい語だけを含め、できるだけ短くする。suggestion は修正後の文字列。
        5. 自信がなければ提案しない。間違って直すより見逃すほうがよい。直す箇所がなければ空の配列を返す。
        6. reason は理由を一文で書く。例：「用語集にある製品名」「データベースのキャッシュについての話」。
        """;

    /// <summary>range：這一批的段落，[start, end)</summary>
    public static string Prompt(Context c, int start, int end)
    {
        var l = c.Language;
        string separator = l.Text("、", ", ", "、"), colon = l.Text("：", ": ", "：");
        var none = l.Text("（無）", "(none)", "（なし）");
        var lines = new List<string> { l.Text("會議標題", "Meeting title", "会議タイトル") + colon + (c.Title.Length == 0 ? none : c.Title) };
        if (c.Participants.Count > 0)
            lines.Add(l.Text("參加者", "Participants", "参加者") + colon + string.Join(separator, c.Participants));
        if (c.SpeakerNames.Count > 0)
            lines.Add(l.Text("說話者", "Speakers", "話者") + colon + string.Join(separator, c.SpeakerNames));
        var glossary = c.Glossary.Count == 0 ? none : string.Join(separator, c.Glossary);
        lines.Add(l.Text("詞彙表（正確寫法）", "Glossary (correct spellings)", "用語集（正しい表記）") + colon + glossary);
        lines.Add("");
        lines.Add(l.Text("逐字稿（每行開頭的數字是段落編號，segment 欄位填這個數字）：",
            "Transcript (the number at the start of each line is the segment number; put it in the \"segment\" field):",
            "文字起こし（各行の先頭の数字は段落番号。segment 欄にこの数字を入れる）："));
        for (var i = start; i < end; i++)
        {
            var (text, speaker) = c.Segments[i];
            lines.Add($"[{i}] {(speaker is null ? "" : speaker + colon)}{text}");
        }
        return string.Join("\n", lines);
    }

    /// <summary>丟掉不能用的建議：段落編號不在這一批、original 不在那一段的文字裡、改了跟沒改一樣、同一處重複</summary>
    public static List<CorrectionSuggestion> Validate(IEnumerable<CorrectionSuggestion> raw, Context context, int start, int end)
    {
        var seen = new HashSet<string>();
        var result = new List<CorrectionSuggestion>();
        foreach (var item in raw)
        {
            var s = item with { Original = SwiftText.TrimSpaces(item.Original), Suggestion = SwiftText.TrimSpaces(item.Suggestion) };
            if (s.Segment < start || s.Segment >= end || s.Original.Length == 0 || s.Original == s.Suggestion) continue;
            if (!context.Segments[s.Segment].Text.Contains(s.Original, StringComparison.Ordinal)) continue;
            if (!seen.Add($"{s.Segment}\u001F{s.Original}")) continue;
            result.Add(s);
        }
        return result;
    }

    /// <summary>套用一則建議：把那一段裡第一個 original 換成 suggestion；找不到回傳 null</summary>
    public static string? Apply(string original, string suggestion, string text)
    {
        var i = text.IndexOf(original, StringComparison.Ordinal);
        return i < 0 || original.Length == 0 ? null : text[..i] + suggestion + text[(i + original.Length)..];
    }

    /// <summary>還原：把 suggestion 換回 original</summary>
    public static string? Revert(string original, string suggestion, string text) => Apply(suggestion, original, text);
}
