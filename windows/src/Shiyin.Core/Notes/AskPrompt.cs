using System.Text.Json;
using Shiyin.Model;
using Shiyin.Text;

namespace Shiyin.Notes;

/// <summary>對會議提問（M§21.8、R19.6、D32）：單場用會議紀錄和逐字稿回答；跨會議先抽關鍵詞，再用全文搜尋找到的片段回答</summary>
public static class AskPrompt
{
    public sealed record Citation(long? MeetingId, string Time, string Quote);

    public sealed record Answer(string Text, bool Found, List<Citation> Citations, List<string>? Keywords = null, List<long>? MeetingIds = null)
    {
        /// <summary>存進資料庫的 JSON：跟 Mac 一樣排序鍵、斜線不跳脫；沒有值的欄位不寫</summary>
        public string JsonString()
        {
            var o = new JNode.Obj().Add("answer", Text).Add("found", JNode.Of(Found))
                .Add("citations", new JNode.Arr(Citations.Select(c =>
                {
                    var item = new JNode.Obj();
                    if (c.MeetingId is long id) item.Add("meetingId", new JNode.Int(id));
                    return (JNode)item.Add("time", c.Time).Add("quote", c.Quote);
                })));
            if (Keywords is not null) o.Add("keywords", JNode.Of(Keywords));
            if (MeetingIds is not null) o.Add("meetingIds", new JNode.Arr(MeetingIds.Select(i => (JNode)new JNode.Int(i))));
            return SwiftJsonWriter.Write(o, sortKeys: true, escapeSlashes: false);
        }

        /// <summary>解析整理工具回傳的 JSON；格式不對回傳 null</summary>
        public static Answer? Decode(string json)
        {
            if (JsonRead.Parse(json) is not { ValueKind: JsonValueKind.Object } root) return null;
            if (root.Str("answer") is not { } text || root.Bool("found") is not { } found
                || root.Get("citations") is not { ValueKind: JsonValueKind.Array } list) return null;
            var citations = new List<Citation>();
            foreach (var c in list.EnumerateArray())
            {
                if (c.Str("time") is not { } time || c.Str("quote") is not { } quote) return null;
                citations.Add(new Citation(c.Long("meetingId"), time, quote));
            }
            List<long>? ids = null;
            if (root.Get("meetingIds") is { ValueKind: JsonValueKind.Array } idList)
                ids = idList.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt64()).ToList();
            return new Answer(text, found, citations, root.Strings("keywords"), ids);
        }
    }

    /// <summary>跨會議時一場會議的資料：會議紀錄的摘要、決議，加上搜尋命中的段落（前後各一段）</summary>
    public sealed record MeetingBlock(long Id, string Title, string Date, List<string> Summary, List<string> Lines);

    const string CitationItems = """{"type":"object","additionalProperties":false,"required":["time","quote"],"properties":{"time":{"type":"string"},"quote":{"type":"string"}}}""";
    const string CrossCitationItems = """{"type":"object","additionalProperties":false,"required":["meetingId","time","quote"],"properties":{"meetingId":{"type":"integer"},"time":{"type":"string"},"quote":{"type":"string"}}}""";

    public const string MeetingSchema = """{"type":"object","additionalProperties":false,"required":["answer","found","citations"],"properties":{"answer":{"type":"string"},"found":{"type":"boolean"},"citations":{"type":"array","items":""" + CitationItems + "}}}";
    public const string AllSchema = """{"type":"object","additionalProperties":false,"required":["answer","found","citations"],"properties":{"answer":{"type":"string"},"found":{"type":"boolean"},"citations":{"type":"array","items":""" + CrossCitationItems + "}}}";
    public const string KeywordsSchema = """{"type":"object","additionalProperties":false,"required":["keywords"],"properties":{"keywords":{"type":"array","items":{"type":"string"}}}}""";

    /// <summary>工具說明是給模型看的，不是介面文字</summary>
    public static readonly OutputFormat MeetingFormat = new(MeetingSchema, "save_answer", "Save the answer to the question");
    public static readonly OutputFormat AllFormat = new(AllSchema, "save_answer", "Save the answer to the question");
    public static readonly OutputFormat KeywordsFormat = new(KeywordsSchema, "save_search_keywords", "Save the search keywords for the question");

    public static string MeetingSystem(MeetingLanguage l) => l.Text("""
        你是會議助理，只根據提供的這場會議的內容（會議紀錄和逐字稿）回答使用者的問題。

        規則：
        1. 只用提供的內容回答，不要推測，也不要補充外部知識。內容裡沒有提到，就說這場會議沒有提到，found 填 false。
        2. 簡潔、直接回答問題；需要列點時每點用「- 」開頭。
        3. 每個重點都要有依據，放在 citations：time 照逐字稿的 [mm:ss] 或 [h:mm:ss] 寫，quote 是那一段的原文（節錄，60 字以內）。
        4. 用提問的語言回答。
        """, """
        You are a meeting assistant. Answer the user's question using only the content provided from this meeting (the meeting notes and transcript).

        Rules:
        1. Use only the provided content. Do not guess or add outside knowledge. If the content does not mention it, say the meeting did not cover it and set "found" to false.
        2. Be concise and answer the question directly. Use "- " at the start of each point when listing.
        3. Back every point with a citation: "time" as written in the transcript ([mm:ss] or [h:mm:ss]), "quote" the original words of that segment (an excerpt, at most 30 words).
        4. Answer in the language of the question.
        """, """
        あなたは会議アシスタントです。提供されたこの会議の内容（議事録と文字起こし）だけに基づいて、ユーザーの質問に答えてください。

        ルール：
        1. 提供された内容だけで答える。推測や外部の知識の補足はしない。内容に出てこない場合は、この会議では触れられていないと答え、found を false にする。
        2. 簡潔に、質問に直接答える。箇条書きは各行を「- 」で始める。
        3. 要点ごとに根拠を citations に入れる：time は文字起こしの [mm:ss] または [h:mm:ss] のとおり、quote はその段落の原文（抜粋、60 字以内）。
        4. 質問と同じ言語で答える。
        """);

    public static string AllSystem(MeetingLanguage l) => l.Text("""
        你是會議助理，只根據提供的幾場會議的片段回答使用者的問題。每場會議有編號、標題、日期、會議紀錄的摘要，以及跟問題有關的逐字稿片段。

        規則：
        1. 只用提供的內容回答，不要推測，也不要補充外部知識。都沒有提到，就說找不到相關的內容，found 填 false。
        2. 簡潔、直接回答問題；不同會議的內容要說清楚是哪一場（標題或日期）。需要列點時每點用「- 」開頭。
        3. 每個重點都要有依據，放在 citations：meetingId 是那場會議的編號，time 照逐字稿片段的 [mm:ss] 或 [h:mm:ss] 寫，quote 是原文（節錄，60 字以內）。
        4. 用提問的語言回答。
        """, """
        You are a meeting assistant. Answer the user's question using only the excerpts provided from several meetings. Each meeting has an ID, title, date, a summary from its notes, and transcript excerpts related to the question.

        Rules:
        1. Use only the provided content. Do not guess or add outside knowledge. If none of it is relevant, say nothing related was found and set "found" to false.
        2. Be concise and answer the question directly. Make clear which meeting each point comes from (title or date). Use "- " at the start of each point when listing.
        3. Back every point with a citation: "meetingId" is the meeting's ID, "time" as written in the excerpt ([mm:ss] or [h:mm:ss]), "quote" the original words (an excerpt, at most 30 words).
        4. Answer in the language of the question.
        """, """
        あなたは会議アシスタントです。提供された複数の会議の抜粋だけに基づいて、ユーザーの質問に答えてください。各会議には番号、タイトル、日付、議事録の要約、質問に関係する文字起こしの抜粋があります。

        ルール：
        1. 提供された内容だけで答える。推測や外部の知識の補足はしない。関係する内容がなければ、見つからなかったと答え、found を false にする。
        2. 簡潔に、質問に直接答える。どの会議の内容か（タイトルか日付）をはっきりさせる。箇条書きは各行を「- 」で始める。
        3. 要点ごとに根拠を citations に入れる：meetingId はその会議の番号、time は抜粋の [mm:ss] または [h:mm:ss] のとおり、quote は原文（抜粋、60 字以内）。
        4. 質問と同じ言語で答える。
        """);

    public static string KeywordsSystem(MeetingLanguage l) => l.Text("""
        把使用者對會議的問題，轉成用來全文搜尋會議逐字稿的關鍵詞。

        規則：
        1. 3～8 個關鍵詞，最重要的放前面。
        2. 包含同義詞、常見的說法、中英文寫法、簡稱（例如「寄信」也列「郵件」「mail」「SendGrid」）。
        3. 每個關鍵詞是一個詞或短語，至少 2 個字，不要整句、不要「會議」「討論」這種每場都有的詞。
        """, """
        Turn the user's question about their meetings into keywords for a full-text search of meeting transcripts.

        Rules:
        1. 3 to 8 keywords, most important first.
        2. Include synonyms, common phrasings, spellings in other languages and abbreviations (for "email", also list "mail", "SendGrid").
        3. Each keyword is a word or short phrase of at least 2 characters. No full sentences, and no words every meeting has, like "meeting" or "discuss".
        """, """
        会議についてのユーザーの質問を、会議の文字起こしを全文検索するためのキーワードに変換してください。

        ルール：
        1. キーワードは 3～8 個、重要なものから並べる。
        2. 同義語、よく使う言い方、英語・日本語の表記、略称も入れる（「メール送信」なら「メール」「mail」「SendGrid」も）。
        3. 各キーワードは語か短いフレーズで 2 文字以上。文全体や、「会議」「議論」のようにどの会議にもある語は入れない。
        """);

    public static string MeetingPrompt(Meeting meeting, MeetingNotes? notes, IReadOnlyList<string> lines,
                                       IReadOnlyList<(string Question, string Answer)> history, string question)
    {
        var l = meeting.MeetingLanguage;
        var colon = l.Text("：", ": ", "：");
        var output = new List<string>
        {
            l.Text("會議標題", "Meeting title", "会議タイトル") + colon + meeting.DisplayTitle,
            l.Text("會議日期", "Meeting date", "会議の日付") + colon + meeting.MeetingDate,
        };
        if (meeting.ParticipantList.Count > 0)
            output.Add(l.Text("參加者", "Participants", "参加者") + colon + string.Join(l.Text("、", ", ", "、"), meeting.ParticipantList));
        if (notes is not null)
        {
            output.Add("");
            output.Add(l.Text("會議紀錄（JSON）", "Meeting notes (JSON)", "議事録（JSON）") + colon);
            output.Add(notes.JsonString());
        }
        output.Add("");
        output.Add(l.Text("逐字稿（[時間] 說話者：內容）", "Transcript ([time] speaker: text)", "文字起こし（[時刻] 話者：内容）") + colon);
        output.AddRange(lines);
        if (history.Count > 0)
        {
            output.Add("");
            output.Add(l.Text("之前的問答（追問時參考）", "Earlier questions and answers (for follow-ups)", "これまでの質問と回答（追加の質問の参考）") + colon);
            foreach (var item in history)
            {
                output.Add("Q" + colon + item.Question);
                output.Add("A" + colon + item.Answer);
            }
        }
        output.Add("");
        output.Add(l.Text("問題", "Question", "質問") + colon + question);
        return string.Join("\n", output);
    }

    public static string AllPrompt(MeetingLanguage l, IReadOnlyList<MeetingBlock> blocks, string question)
    {
        var colon = l.Text("：", ": ", "：");
        var output = new List<string>();
        foreach (var block in blocks)
        {
            output.Add(l.Text($"【會議 {block.Id}】", $"[Meeting {block.Id}]", $"【会議 {block.Id}】") + $" {block.Title}（{block.Date}）");
            if (block.Summary.Count > 0)
            {
                output.Add(l.Text("摘要與決議", "Summary and decisions", "要約と決定事項") + colon);
                output.AddRange(block.Summary.Select(s => "- " + s));
            }
            if (block.Lines.Count > 0)
            {
                output.Add(l.Text("逐字稿片段", "Transcript excerpts", "文字起こしの抜粋") + colon);
                output.AddRange(block.Lines);
            }
            output.Add("");
        }
        output.Add(l.Text("問題", "Question", "質問") + colon + question);
        return string.Join("\n", output);
    }

    public static string KeywordsPrompt(MeetingLanguage l, string question) => l.Text("問題：", "Question: ", "質問：") + question;

    /// <summary>丟掉不能用的出處：時間格式不對、跨會議時不是提供的會議</summary>
    public static Answer Validate(Answer answer, IReadOnlySet<long>? meetingIds)
    {
        var citations = new List<Citation>();
        foreach (var c in answer.Citations)
        {
            var time = TimeFormat.Clean(c.Time);
            var quote = c.Quote.Trim();
            if (TimeFormat.Seconds(time) is null) continue;
            long? id = c.MeetingId;
            if (meetingIds is not null)
            {
                if (id is not long value || !meetingIds.Contains(value)) continue;
            }
            else id = null;
            citations.Add(new Citation(id, time, quote));
        }
        return answer with { Text = answer.Text.Trim(), Citations = citations };
    }
}
