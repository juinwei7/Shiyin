using System.Text.Json;
using System.Text.RegularExpressions;
using Shiyin.Text;

namespace Shiyin.Model;

/// <summary>
/// 會議紀錄 v2（M§8.1）：每一條都帶錄音時間點。讀取時相容 v1（字串陣列轉成 time 是空的條目），
/// 解碼規則跟 Mac 的 <c>MeetingNotes.init(from:)</c> 一樣寬鬆：欄位型別不對就當作沒有。
/// </summary>
public sealed record MeetingNotes
{
    public sealed record Item(string Text, string Time = "");
    public sealed record Topic(string Title, string Start, List<Item> Points);
    public sealed record ActionItem(string Owner, string Task, string Due, string Time = "");
    public sealed record CustomSection(string Title, List<Item> Items);
    /// <summary>YesNo：能用是或否回答（R32、M§27），畫面上給「是」「不是」的按鈕</summary>
    public sealed record ReviewItem(string Time, string Question, bool YesNo = false);

    public string Title { get; init; } = "";
    public List<Item> Summary { get; init; } = [];
    public List<Item> Decisions { get; init; } = [];
    public List<ActionItem> ActionItems { get; init; } = [];
    public List<Item> OpenQuestions { get; init; } = [];
    public List<Topic> Topics { get; init; } = [];
    public List<CustomSection> Custom { get; init; } = [];
    public List<ReviewItem> ReviewItems { get; init; } = [];

    public bool IsEmpty => Summary.Count == 0 && Decisions.Count == 0 && ActionItems.Count == 0 && OpenQuestions.Count == 0
                           && Topics.Count == 0 && Custom.Count == 0;

    // MARK: 讀取

    /// <summary>解析失敗（不是 JSON 物件）回傳 null，跟 Swift 的 <c>try? JSONDecoder().decode</c> 一樣</summary>
    public static MeetingNotes? Decode(string? json)
    {
        if (json is null || JsonRead.Parse(json) is not { ValueKind: JsonValueKind.Object } root) return null;
        return Decode(root);
    }

    public static MeetingNotes Decode(JsonElement root) => new()
    {
        Title = root.Str("title") ?? "",
        Summary = Items(root.Get("summary")) ?? [],
        Decisions = Items(root.Get("decisions")) ?? [],
        ActionItems = List(root.Get("actionItems"), DecodeAction) ?? [],
        OpenQuestions = Items(root.Get("openQuestions")) ?? [],
        Custom = List(root.Get("custom"), DecodeCustom) ?? [],
        ReviewItems = List(root.Get("reviewItems"), DecodeReview) ?? [],
        Topics = List(root.Get("topics"), DecodeTopic) ?? [],
    };

    /// <summary>陣列裡任何一個元素解不開，整個陣列就是 null（Swift 的 <c>try? decode([T].self)</c>）</summary>
    static List<T>? List<T>(JsonElement? e, Func<JsonElement, T?> decode) where T : class
    {
        if (e is not { ValueKind: JsonValueKind.Array } array) return null;
        var list = new List<T>();
        foreach (var item in array.EnumerateArray())
        {
            if (decode(item) is not { } value) return null;
            list.Add(value);
        }
        return list;
    }

    static List<Item>? Items(JsonElement? e) => List(e, DecodeItem);

    /// <summary>字串直接當文字；物件的 text 一定要是字串，time 可以沒有</summary>
    static Item? DecodeItem(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.String) return new Item(e.GetString()!);
        if (e.ValueKind != JsonValueKind.Object || e.Str("text") is not { } text) return null;
        return new Item(text, TimeFormat.Clean(e.Str("time") ?? ""));
    }

    static Topic? DecodeTopic(JsonElement e) => e.ValueKind != JsonValueKind.Object ? null
        : new Topic(e.Str("title") ?? "", TimeFormat.Clean(e.Str("start") ?? ""), Items(e.Get("points")) ?? []);

    static ActionItem? DecodeAction(JsonElement e) => e.ValueKind != JsonValueKind.Object ? null
        : new ActionItem(e.Str("owner") ?? "", e.Str("task") ?? "", e.Str("due") ?? "", TimeFormat.Clean(e.Str("time") ?? ""));

    /// <summary>自訂段落是 Swift 自動產生的解碼：title、items 都要有</summary>
    static CustomSection? DecodeCustom(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || e.Str("title") is not { } title || Items(e.Get("items")) is not { } items) return null;
        return new CustomSection(title, items);
    }

    static ReviewItem? DecodeReview(JsonElement e) => e.ValueKind != JsonValueKind.Object ? null
        : new ReviewItem(TimeFormat.Clean(e.Str("time") ?? ""), e.Str("question") ?? "", e.Bool("yesNo") ?? false);

    // MARK: 寫出

    /// <summary>跟 Mac 的 <c>jsonString()</c> 逐字元相同：排序鍵、斜線不跳脫、緊湊</summary>
    public string JsonString() => SwiftJsonWriter.Write(ToJson(), sortKeys: true, escapeSlashes: false);

    /// <summary>內容相同（同 Mac 的 Equatable；record 的 Equals 只比清單的參考）</summary>
    public static bool Same(MeetingNotes? a, MeetingNotes? b) => a is null ? b is null : b is not null && a.JsonString() == b.JsonString();

    public JNode.Obj ToJson()
    {
        static JNode ItemNode(Item i) => new JNode.Obj().Add("text", i.Text).Add("time", i.Time);
        static JNode Items(IEnumerable<Item> items) => new JNode.Arr(items.Select(ItemNode));
        return new JNode.Obj()
            .Add("title", Title)
            .Add("summary", Items(Summary))
            .Add("decisions", Items(Decisions))
            .Add("actionItems", new JNode.Arr(ActionItems.Select(a =>
                (JNode)new JNode.Obj().Add("owner", a.Owner).Add("task", a.Task).Add("due", a.Due).Add("time", a.Time))))
            .Add("openQuestions", Items(OpenQuestions))
            .Add("topics", new JNode.Arr(Topics.Select(t =>
                (JNode)new JNode.Obj().Add("title", t.Title).Add("start", t.Start).Add("points", Items(t.Points)))))
            .Add("custom", new JNode.Arr(Custom.Select(c =>
                (JNode)new JNode.Obj().Add("title", c.Title).Add("items", Items(c.Items)))))
            // yesNo 是 false 時不寫（同 Mac）：以前的會議紀錄、沒有是非題的會議紀錄存起來跟以前一字不差
            .Add("reviewItems", new JNode.Arr(ReviewItems.Select(r =>
            {
                var item = new JNode.Obj().Add("time", r.Time).Add("question", r.Question);
                return (JNode)(r.YesNo ? item.Add("yesNo", JNode.Of(true)) : item);
            })));
    }

    // MARK: 修改

    /// <summary>全文取代：每一個文字欄位做區分大小寫的單純取代（時間點不變）</summary>
    public MeetingNotes Replacing(string find, string replacement) =>
        find.Length == 0 ? this : MappingText(s => s.Replace(find, replacement, StringComparison.Ordinal));

    /// <summary>說話者改名後，會議紀錄裡的舊名字換成新名字（M§21.6）。舊名字是預設名稱時，後面不能接數字</summary>
    public MeetingNotes ReplacingSpeakerName(string old, string replacement, bool isDefaultName)
    {
        if (!isDefaultName) return Replacing(old, replacement);
        var regex = new Regex(Regex.Escape(old) + "(?![0-9])");
        return MappingText(s => regex.Replace(s, replacement.Replace("$", "$$")));
    }

    MeetingNotes MappingText(Func<string, string> r)
    {
        Item Ri(Item i) => new(r(i.Text), i.Time);
        return new MeetingNotes
        {
            Title = r(Title),
            Summary = Summary.Select(Ri).ToList(),
            Decisions = Decisions.Select(Ri).ToList(),
            ActionItems = ActionItems.Select(a => new ActionItem(r(a.Owner), r(a.Task), a.Due, a.Time)).ToList(),
            OpenQuestions = OpenQuestions.Select(Ri).ToList(),
            Topics = Topics.Select(t => new Topic(r(t.Title), t.Start, t.Points.Select(Ri).ToList())).ToList(),
            Custom = Custom.Select(c => new CustomSection(r(c.Title), c.Items.Select(Ri).ToList())).ToList(),
            ReviewItems = ReviewItems.Select(x => new ReviewItem(x.Time, r(x.Question), x.YesNo)).ToList(),
        };
    }

    /// <summary>給搜尋索引用的純文字</summary>
    public string PlainText
    {
        get
        {
            var lines = new List<string> { Title };
            lines.AddRange(Summary.Select(i => i.Text));
            lines.AddRange(Decisions.Select(i => i.Text));
            lines.AddRange(OpenQuestions.Select(i => i.Text));
            lines.AddRange(ActionItems.Select(a => $"{a.Owner} {a.Task} {a.Due}"));
            foreach (var t in Topics) { lines.Add(t.Title); lines.AddRange(t.Points.Select(i => i.Text)); }
            foreach (var c in Custom) { lines.Add(c.Title); lines.AddRange(c.Items.Select(i => i.Text)); }
            lines.AddRange(ReviewItems.Select(r => r.Question));
            return string.Join("\n", lines.Where(l => l.Length > 0));
        }
    }

    /// <summary>內容相等（record 的 List 只比參考，這裡比 JSON）</summary>
    public bool SameAs(MeetingNotes? other) => other is not null && JsonString() == other.JsonString();

    // 給 Claude --json-schema、Codex --output-schema、OpenAI strict json_schema、Anthropic tool input_schema（同 Mac）
    const string ItemsSchema = """{"type":"array","items":{"type":"object","additionalProperties":false,"required":["text","time"],"properties":{"text":{"type":"string"},"time":{"type":"string"}}}}""";

    public static readonly string JsonSchema =
        """{"type":"object","additionalProperties":false,"required":["title","summary","decisions","actionItems","openQuestions","topics","custom","reviewItems"],"properties":{"title":{"type":"string"},"summary":""" + ItemsSchema
        + ""","decisions":""" + ItemsSchema
        + ""","actionItems":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["owner","task","due","time"],"properties":{"owner":{"type":"string"},"task":{"type":"string"},"due":{"type":"string"},"time":{"type":"string"}}}}"""
        + ""","openQuestions":""" + ItemsSchema
        + ""","topics":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["title","start","points"],"properties":{"title":{"type":"string"},"start":{"type":"string"},"points":""" + ItemsSchema + "}}}"
        + ""","custom":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["title","items"],"properties":{"title":{"type":"string"},"items":""" + ItemsSchema + "}}}"
        + ""","reviewItems":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["time","question","yesNo"],"properties":{"time":{"type":"string"},"question":{"type":"string"},"yesNo":{"type":"boolean"}}}}}}""";
}
