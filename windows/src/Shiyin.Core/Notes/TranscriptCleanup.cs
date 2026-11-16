using Shiyin.Model;
using Shiyin.Text;

namespace Shiyin.Notes;

/// <summary>
/// 精簡逐字稿（R96、M§70、design-windows §11.29，同 Mac 的 TranscriptCleanup）：請整理工具去掉贅字、重複、說到一半改口的部分，
/// 一句一句對回原本的段落。不改意思、不摘要、不合併或拆開句子；整句都是贅字時給空字串（畫面上不顯示那一句）
/// </summary>
public static class TranscriptCleanup
{
    /// <summary>每次送出的句數（同 Mac）</summary>
    public const int ChunkSize = 120;

    public sealed record Context(MeetingLanguage Language, IReadOnlyList<(string Text, string? Speaker)> Segments);

    /// <summary>Segment：句子在整份逐字稿裡的位置（從 0 開始）</summary>
    public sealed record Line(int Segment, string Text);

    public const string JsonSchema = """{"type":"object","additionalProperties":false,"required":["lines"],"properties":{"lines":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["segment","text"],"properties":{"segment":{"type":"integer"},"text":{"type":"string"}}}}}}""";

    /// <summary>工具說明是給模型看的，不是介面文字</summary>
    public static readonly OutputFormat Format = new(JsonSchema, "save_clean_transcript", "Save the cleaned-up transcript lines");

    /// <summary>共用別的語言那份的精調語言（粵語）最後接一段逐字稿的說明（同校正，M§41.2）</summary>
    public static string System(MeetingLanguage language)
    {
        var text = language.Prompt(SystemChinese, SystemEnglish, SystemJapanese);
        return language.Profile.TranscriptNote is { } note ? text + "\n\n" + note.Correction : text;
    }

    const string SystemChinese = """
        你是會議逐字稿的編輯。逐字稿是語音辨識自動產生的口語，你的工作是把每一句整理成好讀的版本。

        規則：
        1. 去掉贅字和口頭禪（嗯、呃、啊、那個、就是、然後、對對對這類沒有意思的用法）、重複的字詞、說到一半改口的部分（只留改口後的說法）。
        2. 不改意思、不摘要、不補沒說的內容、不翻譯；專有名詞、人名、數字、日期照原文。每一句獨立整理，不要把兩句合併或把一句拆開。
        3. 可以補上或整理標點，讓句子通順。
        4. segment 填句首的編號；整句都是贅字（例如「嗯。」「對對對。」）時 text 給空字串。
        5. 只回傳有改的句子；不用改的句子不要列。
        """;

    const string SystemEnglish = """
        You edit meeting transcripts. The transcript is spoken language produced by speech recognition; make each line easy to read.

        Rules:
        1. Remove filler words and verbal tics (um, uh, like, you know, I mean, so, when they carry no meaning), repeated words, and false starts (keep only what the speaker settled on).
        2. Do not change the meaning, summarize, add anything that wasn't said, or translate; keep names, numbers and dates as they are. Clean each line on its own; do not merge lines or split a line.
        3. You may add or fix punctuation so the line reads well.
        4. Put the number at the start of the line in "segment". If the whole line is filler (for example "Um." or "Yeah, yeah."), set "text" to an empty string.
        5. Return only the lines you changed; leave out lines that need no changes.
        """;

    const string SystemJapanese = """
        あなたは会議の文字起こしの編集者です。文字起こしは音声認識で作られた話し言葉です。各行を読みやすく整えてください。

        ルール：
        1. フィラーや口癖（えー、あのー、えっと、まあ、なんか など意味のない使い方）、繰り返し、言い直しの前半（言い直した後だけを残す）を取り除く。
        2. 意味を変えない。要約しない。言っていない内容を足さない。翻訳しない。固有名詞、人名、数字、日付は原文のまま。各行は独立して整え、行をまとめたり分けたりしない。
        3. 読みやすくなるように句読点を補ったり整えたりしてよい。
        4. segment には行頭の番号を入れる。行全体がフィラー（例：「えー。」「はいはい。」）なら text は空文字列にする。
        5. 変更した行だけを返し、変更のない行は含めない。
        """;

    /// <summary>這一批的句子 [start, end)：「[編號] 說話者：文字」</summary>
    public static string Prompt(Context c, int start, int end)
    {
        var l = c.Language;
        var colon = l.Prompt("：", ": ", "：");
        var lines = new List<string>
        {
            l.Prompt("逐字稿（每行開頭的數字是句子編號，segment 欄位填這個數字）：",
                "Transcript (the number at the start of each line is the line number; put it in the \"segment\" field):",
                "文字起こし（各行の先頭の数字は行番号。segment 欄にこの数字を入れる）："),  // l10n-skip
        };
        for (var i = start; i < end; i++)
        {
            var (text, speaker) = c.Segments[i];
            lines.Add($"[{i}] {(speaker is null ? "" : speaker + colon)}{text}");
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// 丟掉不能用的句子（同 Mac）：編號不在這一批、同一句重複（留第一個）、跟原文一樣、比原文長太多（原文字數＋max(10, 20%)，模型在補字或改寫，照原文）。
    /// 文字去頭尾空白；空字串（整句都是贅字）留著。字數照 Swift 的字形叢集
    /// </summary>
    public static List<Line> Validate(IEnumerable<Line> raw, Context context, int start, int end)
    {
        var seen = new HashSet<int>();
        var result = new List<Line>();
        foreach (var item in raw)
        {
            var text = SwiftText.Trim(item.Text);
            if (item.Segment < start || item.Segment >= end || !seen.Add(item.Segment)) continue;
            var original = context.Segments[item.Segment].Text;
            var count = SwiftText.Count(original);
            if (text == original || SwiftText.Count(text) > count + Math.Max(10, count / 5)) continue;
            result.Add(new Line(item.Segment, text));
        }
        return result;
    }
}
