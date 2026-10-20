using System.Text.Json;
using Shiyin.Model;
using Shiyin.Text;

namespace Shiyin.Notes;

/// <summary>範本的內容（M§8.2）</summary>
public sealed record TemplateDefinition(string Description, List<string> Sections, List<TemplateDefinition.CustomSpec> Custom, string Instruction)
{
    public sealed record CustomSpec(string Title, string Instruction);

    /// <summary>內建段落：summary、decisions、actionItems、openQuestions、topics</summary>
    public static readonly string[] BuiltinSectionIds = ["summary", "decisions", "actionItems", "openQuestions", "topics"];

    /// <summary>段落名稱：介面用介面語言，提示詞用會議語言</summary>
    public static string SectionLabel(string id, MeetingLanguage language) => id switch
    {
        "summary" => language.Text("摘要", "Summary", "要約"),
        "decisions" => language.Text("決議", "Decisions", "決定事項"),
        "actionItems" => language.Text("待辦", "Action items", "ToDo"),
        "openQuestions" => language.Text("未決問題", "Open questions", "未決事項"),
        _ => language.Text("議題詳細", "Topics", "議題ごとの詳細"),
    };

    public bool Includes(string section) => Sections.Contains(section);

    /// <summary>Swift 自動產生的解碼：四個欄位都要有，不然整個用預設值（全部段落）</summary>
    public static TemplateDefinition Decode(string json)
    {
        var fallback = new TemplateDefinition("", [.. BuiltinSectionIds], [], "");
        if (JsonRead.Parse(json) is not { ValueKind: JsonValueKind.Object } root) return fallback;
        if (root.Str("description") is not { } description || root.Strings("sections") is not { } sections
            || root.Str("instruction") is not { } instruction || root.Get("custom") is not { ValueKind: JsonValueKind.Array } custom)
            return fallback;
        var specs = new List<CustomSpec>();
        foreach (var item in custom.EnumerateArray())
        {
            if (item.Str("title") is not { } title || item.Str("instruction") is not { } text) return fallback;
            specs.Add(new CustomSpec(title, text));
        }
        return new TemplateDefinition(description, sections, specs, instruction);
    }

    /// <summary>存進資料庫的 JSON：跟 Mac 的 JSONEncoder 預設一樣（宣告順序、斜線跳脫）。
    /// Mac 的鍵順序每次啟動不同，比對時要解析後比</summary>
    public string JsonString() => SwiftJsonWriter.Write(new JNode.Obj()
        .Add("description", Description)
        .Add("sections", JNode.Of(Sections))
        .Add("custom", new JNode.Arr(Custom.Select(c => (JNode)new JNode.Obj().Add("title", c.Title).Add("instruction", c.Instruction))))
        .Add("instruction", Instruction), sortKeys: false, escapeSlashes: true);

    public bool SameAs(TemplateDefinition other) => JsonString() == other.JsonString();
}

/// <summary>內建範本（R6.2）。中文版啟動時寫進資料庫；英文版、日文版依 id 取用（M§16.5、§18.4）</summary>
public static class BuiltinTemplates
{
    public sealed record Builtin(string Id, string Name, TemplateDefinition Definition);

    static TemplateDefinition T(string description, string[] sections, (string, string)[] custom, string instruction) =>
        new(description, [.. sections], custom.Select(c => new TemplateDefinition.CustomSpec(c.Item1, c.Item2)).ToList(), instruction);

    public static readonly Builtin[] All =
    [
        new("general", "一般會議", T("週會、專案會議、討論會：摘要、決議、待辦、未決問題、議題",
            ["summary", "decisions", "actionItems", "openQuestions", "topics"], [], "")),
        new("one-on-one", "一對一", T("主管與成員的一對一會談",
            ["summary", "actionItems", "openQuestions"],
            [("近況與進展", "對方最近在做的事、進度與卡關的地方"),
             ("回饋", "雙方給彼此的回饋，標明是誰給誰"),
             ("需要的支持", "對方提出需要協助或資源的地方")],
            "這是一對一會談，語氣保持中性，不要評價個人。")),
        new("interview", "訪談", T("使用者訪談、專家訪談、面試",
            ["summary", "topics"],
            [("受訪者背景", "受訪者的角色、經歷、使用情境"),
             ("重點引述", "保留受訪者的原話，每條一句，附時間點"),
             ("洞察與機會", "從訪談內容歸納出的需求、痛點與機會")],
            "這是訪談，重點是受訪者說了什麼，不要把訪談者的推測寫成受訪者的意見。")),
        new("lecture", "課堂／演講", T("課程、演講、分享會、教育訓練",
            ["summary", "topics", "openQuestions"],
            [("重點概念", "提到的重要概念、名詞與解釋"),
             ("問答", "聽眾的提問與講者的回答")],
            "這是課堂或演講，依講者的段落整理大綱。")),
        new("brainstorm", "腦力激盪", T("發想會議、工作坊",
            ["summary", "decisions", "actionItems"],
            [("點子清單", "每個點子一條，盡量標出是誰提出的"),
             ("評估與取捨", "對點子的討論、優缺點與取捨理由")],
            "這是腦力激盪，不要遺漏任何被提出的點子，即使沒有被採納。")),
    ];

    /// <summary>英文版：英文會議整理時用，英文介面也顯示這裡的名稱和說明</summary>
    public static readonly Dictionary<string, (string Name, TemplateDefinition Definition)> English = new()
    {
        ["general"] = ("General meeting", T("Team meetings, project meetings, discussions: summary, decisions, action items, open questions, topics",
            ["summary", "decisions", "actionItems", "openQuestions", "topics"], [], "")),
        ["one-on-one"] = ("One-on-one", T("A one-on-one between a manager and a team member",
            ["summary", "actionItems", "openQuestions"],
            [("Updates and progress", "What they have been working on, progress and blockers"),
             ("Feedback", "Feedback both ways, noting who gave it to whom"),
             ("Support needed", "Where they asked for help or resources")],
            "This is a one-on-one. Keep a neutral tone and do not judge the person.")),
        ["interview"] = ("Interview", T("User interviews, expert interviews, job interviews",
            ["summary", "topics"],
            [("Interviewee background", "The interviewee's role, experience and context"),
             ("Key quotes", "Keep the interviewee's own words, one quote per item, with its time"),
             ("Insights and opportunities", "Needs, pain points and opportunities drawn from the interview")],
            "This is an interview. Focus on what the interviewee said; do not present the interviewer's assumptions as the interviewee's opinions.")),
        ["lecture"] = ("Lecture / talk", T("Courses, talks, sharing sessions, training",
            ["summary", "topics", "openQuestions"],
            [("Key concepts", "Important concepts, terms and explanations that came up"),
             ("Q&A", "Questions from the audience and the speaker's answers")],
            "This is a lecture or talk. Outline it following the speaker's sections.")),
        ["brainstorm"] = ("Brainstorm", T("Ideation meetings, workshops",
            ["summary", "decisions", "actionItems"],
            [("Ideas", "One idea per item, noting who proposed it where possible"),
             ("Evaluation and trade-offs", "Discussion of the ideas, pros and cons, and the reasons for each choice")],
            "This is a brainstorm. Do not leave out any idea that was raised, even if it was not adopted.")),
    };

    /// <summary>日文版：日文會議整理時用（沒有日文介面，名稱不會顯示）</summary>
    public static readonly Dictionary<string, TemplateDefinition> Japanese = new()
    {
        ["general"] = T("定例会議、プロジェクト会議、打ち合わせ：要約、決定事項、ToDo、未決事項、議題",
            ["summary", "decisions", "actionItems", "openQuestions", "topics"], [], ""),
        ["one-on-one"] = T("上司とメンバーの1on1",
            ["summary", "actionItems", "openQuestions"],
            [("近況と進捗", "相手が最近取り組んでいること、進捗、困っていること"),
             ("フィードバック", "双方からのフィードバック。誰から誰へのものかを明記する"),
             ("必要なサポート", "相手が求めた支援やリソース")],
            "これは1on1です。中立的な書き方にし、個人を評価しないこと。"),
        ["interview"] = T("ユーザーインタビュー、専門家インタビュー、面接",
            ["summary", "topics"],
            [("回答者の背景", "回答者の役割、経歴、利用状況"),
             ("主な発言", "回答者の言葉をそのまま残す。1項目に1つの発言、時刻付き"),
             ("気づきと機会", "インタビューから読み取れるニーズ、課題、機会")],
            "これはインタビューです。回答者が何を言ったかを中心にし、聞き手の推測を回答者の意見として書かないこと。"),
        ["lecture"] = T("授業、講演、勉強会、研修",
            ["summary", "topics", "openQuestions"],
            [("重要な概念", "出てきた重要な概念、用語とその説明"),
             ("質疑応答", "聴衆からの質問と講師の回答")],
            "これは授業または講演です。講師の話の流れに沿って要点をまとめること。"),
        ["brainstorm"] = T("アイデア出しの会議、ワークショップ",
            ["summary", "decisions", "actionItems"],
            [("アイデア一覧", "1項目に1つのアイデア。できるだけ誰が出したかを書く"),
             ("評価と取捨選択", "アイデアについての議論、長所と短所、選んだ理由")],
            "これはブレインストーミングです。採用されなかったものも含め、出たアイデアを漏らさないこと。"),
    };

    /// <summary>介面上顯示的名稱：內建範本跟著介面語言，自訂範本照使用者取的名字</summary>
    public static string DisplayName(this TemplateRecord t) =>
        t.Builtin && MeetingLanguages.Interface == MeetingLanguage.En && English.TryGetValue(t.Id, out var e) ? e.Name : t.Name;

    public static string DisplayDescription(this TemplateRecord t) =>
        t.Builtin && MeetingLanguages.Interface == MeetingLanguage.En && English.TryGetValue(t.Id, out var e)
            ? e.Definition.Description : TemplateDefinition.Decode(t.Definition).Description;

    /// <summary>整理時用的內容：內建範本跟著會議語言，自訂範本照使用者寫的</summary>
    public static TemplateDefinition DefinitionFor(this TemplateRecord t, MeetingLanguage language)
    {
        if (t.Builtin && language == MeetingLanguage.En && English.TryGetValue(t.Id, out var e)) return e.Definition;
        if (t.Builtin && language == MeetingLanguage.Ja && Japanese.TryGetValue(t.Id, out var j)) return j;
        return TemplateDefinition.Decode(t.Definition);
    }
}
