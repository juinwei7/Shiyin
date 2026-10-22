using Shiyin.Model;
using Shiyin.Text;

namespace Shiyin.Notes;

/// <summary>要模型輸出的格式：JSON schema，以及 Anthropic API 用的工具名稱和說明</summary>
public sealed record OutputFormat(string Schema, string ToolName, string ToolDescription)
{
    /// <summary>會議紀錄：工具說明是介面文字（Mac 是 String(localized:)）</summary>
    public static OutputFormat MeetingNotes => new(Model.MeetingNotes.JsonSchema, "save_meeting_notes", Localization.L.T("儲存整理好的會議紀錄"));
}

/// <summary>AI 猜說話者（M§21.5、R19.2、D30）：從對話內容猜出還沒有名字的說話者是誰，列成建議</summary>
public static class SpeakerNaming
{
    public sealed record Suggestion(int Speaker, string Name, string Reason, string Time);

    public sealed record Context(
        MeetingLanguage Language, string MeetingDate, string Title, IReadOnlyList<string> Participants,
        IReadOnlyList<string> KnownPeople, IReadOnlyList<string> Glossary, IReadOnlyList<int> Unnamed,
        Func<int, string> SpeakerName, IReadOnlyList<(double Start, int? Speaker, string Text)> Segments);

    public const string JsonSchema = """{"type":"object","additionalProperties":false,"required":["speakerNames"],"properties":{"speakerNames":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["speaker","name","reason","time"],"properties":{"speaker":{"type":"integer"},"name":{"type":"string"},"reason":{"type":"string"},"time":{"type":"string"}}}}}}""";

    /// <summary>工具說明是給模型看的，不是介面文字</summary>
    public static readonly OutputFormat Format = new(JsonSchema, "save_speaker_names", "Save the speaker names inferred from the transcript");

    public static string System(MeetingLanguage language) => language.Text(SystemChinese, SystemEnglish, SystemJapanese);

    const string SystemChinese = """
        你要從會議逐字稿判斷「說話者 N」是誰。逐字稿的說話者是用聲音自動分出來的，只有編號。

        規則：
        1. 只在逐字稿裡有明確根據時才猜，例如：有人叫了某人的名字，下一段就是那位說話者回應；說話者自我介紹（「我是…」）；主持人點名之後那位說話者開始講；別人說「謝謝某某的報告」而剛報告完的是那位說話者。
        2. 只猜「要猜的說話者」裡的編號。沒有把握的就不要列，寧可少列，不要猜錯。
        3. 名字照逐字稿、參加者、認識的人、詞彙表裡的寫法。只有職稱或稱謂（例如「老闆」「經理」「主任」）不算名字，不要列。
        4. 同一個名字只能給一位說話者。
        5. reason 用一句話說明根據；time 是最能證明的那一段的時間，照逐字稿裡的 [mm:ss] 或 [h:mm:ss] 寫。
        6. 都沒有把握就回傳空陣列。
        """;

    const string SystemEnglish = """
        Work out who "Speaker N" is in a meeting transcript. The speakers were separated automatically by voice and only have numbers.

        Rules:
        1. Only guess when the transcript gives clear evidence, for example: someone addresses a person by name and that speaker answers in the next segment; a speaker introduces themselves ("I'm ..."); the host calls on someone and that speaker starts talking; someone says "thanks for the update, X" right after that speaker finished.
        2. Only guess the speakers listed under "Speakers to identify". Leave out anyone you are not sure about. Listing fewer is better than guessing wrong.
        3. Spell names the way they appear in the transcript, participants, known people or glossary. A job title or form of address alone ("the boss", "manager") is not a name; do not list it.
        4. Each name can go to only one speaker.
        5. "reason" is one sentence explaining the evidence; "time" is the time of the segment that shows it best, written as in the transcript ([mm:ss] or [h:mm:ss]).
        6. If you are not sure about anyone, return an empty array.
        """;

    const string SystemJapanese = """
        会議の文字起こしから「話者 N」が誰かを判断してください。話者は声で自動的に分けたもので、番号しかありません。

        ルール：
        1. 文字起こしに明確な根拠があるときだけ推測する。例：誰かが名前を呼び、次の段落でその話者が応答している。話者が自己紹介している（「〇〇です」）。司会者が指名した直後にその話者が話し始めている。その話者が話し終えた直後に「〇〇さん、ありがとうございます」と言われている。
        2. 「判断する話者」に挙げた番号だけを推測する。自信がない話者は挙げない。間違えるより少なく挙げるほうがよい。
        3. 名前の表記は文字起こし、参加者、知っている人、用語集に従う。役職や呼称だけ（「部長」「社長」など）は名前ではないので挙げない。
        4. 同じ名前は一人の話者にしか付けない。
        5. reason は根拠を一文で書く。time はそれを最もよく示す段落の時刻を、文字起こしの [mm:ss] または [h:mm:ss] のとおりに書く。
        6. 誰についても自信がなければ空の配列を返す。
        """;

    public static string Prompt(Context c)
    {
        var l = c.Language;
        string separator = l.Text("、", ", ", "、"), colon = l.Text("：", ": ", "：");
        var none = l.Text("（無）", "(none)", "（なし）");
        string List(IEnumerable<string> items)
        {
            var list = items.ToList();
            return list.Count == 0 ? none : string.Join(separator, list);
        }
        var lines = new List<string>
        {
            l.Text("會議日期", "Meeting date", "会議の日付") + colon + c.MeetingDate,
            l.Text("會議標題", "Meeting title", "会議タイトル") + colon + (c.Title.Length == 0 ? none : c.Title),
            l.Text("參加者", "Participants", "参加者") + colon + List(c.Participants),
            l.Text("認識的人（之前的會議出現過的名字，寫法可以參考）", "Known people (names from earlier meetings, for spelling)",
                "知っている人（以前の会議に出てきた名前。表記の参考に）") + colon + List(c.KnownPeople),
            l.Text("詞彙表", "Glossary", "用語集") + colon + List(c.Glossary),
            l.Text("要猜的說話者", "Speakers to identify", "判断する話者") + colon + List(c.Unnamed.Select(c.SpeakerName)),
            "",
            l.Text("逐字稿（[時間] 說話者：內容）：", "Transcript ([time] speaker: text):", "文字起こし（[時刻] 話者：内容）："),
        };
        foreach (var s in c.Segments)
        {
            var speaker = s.Speaker is int idx ? c.SpeakerName(idx) + colon : "";
            lines.Add($"[{TimeFormat.String(s.Start)}] {speaker}{s.Text}");
        }
        return string.Join("\n", lines);
    }

    /// <summary>丟掉不能用的建議：不是要猜的說話者、名字是空的或是預設名稱、太長、同一位說話者或同一個名字重複</summary>
    public static List<Suggestion> Validate(IEnumerable<Suggestion> raw, Context context)
    {
        var speakers = new HashSet<int>();
        var names = new HashSet<string>();
        var result = new List<Suggestion>();
        foreach (var item in raw)
        {
            var s = item with { Name = item.Name.Trim(), Reason = item.Reason.Trim(), Time = TimeFormat.Clean(item.Time) };
            if (!context.Unnamed.Contains(s.Speaker) || s.Name.Length == 0 || SwiftText.Count(s.Name) > 30
                || SpeakerNames.IsDefault(s.Name)) continue;
            if (!speakers.Add(s.Speaker)) continue;
            if (!names.Add(SwiftText.Lower(s.Name))) continue;
            result.Add(s);
        }
        return result;
    }
}

public static class SpeakerNames
{
    static readonly string[] Prefixes = ["說話者 ", "Speaker ", "話者 "];

    /// <summary>「說話者 1」「Speaker 1」「話者 1」這種預設名稱。跟 Mac 的 <c>^(說話者|Speaker|話者) \d+$</c> 一樣：
    /// Swift 的 <c>\d</c> 是 Character.isNumber，「說話者 一」也算</summary>
    public static bool IsDefault(string name)
    {
        foreach (var prefix in Prefixes)
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var rest = SwiftText.Characters(name[prefix.Length..]);
            return rest.Count > 0 && rest.All(SwiftText.IsNumber);
        }
        return false;
    }
}
