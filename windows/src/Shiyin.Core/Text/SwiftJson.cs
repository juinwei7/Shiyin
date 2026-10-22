using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Shiyin.Text;

/// <summary>
/// 跟 Swift 的 JSONEncoder 一樣的 JSON 輸出（design-windows §4.2）：緊湊、可選擇排序鍵、
/// 跳脫規則相同（<c>\b \f \n \r \t</c>，其他控制字元 <c>\u00xx</c> 小寫，非 ASCII 原樣）。
/// 整理、修訂的提示詞裡直接放了會議紀錄的 JSON，所以要逐字元相同。
/// </summary>
public sealed class SwiftJsonWriter
{
    readonly StringBuilder sb = new();
    readonly bool sortKeys;
    readonly bool escapeSlashes;

    SwiftJsonWriter(bool sortKeys, bool escapeSlashes)
    {
        this.sortKeys = sortKeys;
        this.escapeSlashes = escapeSlashes;
    }

    /// <param name="sortKeys">Swift 的 <c>.sortedKeys</c></param>
    /// <param name="escapeSlashes">沒有 <c>.withoutEscapingSlashes</c> 時，<c>/</c> 寫成 <c>\/</c></param>
    public static string Write(JNode node, bool sortKeys, bool escapeSlashes)
    {
        var w = new SwiftJsonWriter(sortKeys, escapeSlashes);
        w.Value(node);
        return w.sb.ToString();
    }

    void Value(JNode node)
    {
        switch (node)
        {
            case JNode.Null: sb.Append("null"); break;
            case JNode.Bool b: sb.Append(b.Value ? "true" : "false"); break;
            case JNode.Int i: sb.Append(i.Value.ToString(CultureInfo.InvariantCulture)); break;
            case JNode.Number n: sb.Append(FormatDouble(n.Value)); break;
            case JNode.Str s: String(s.Value); break;
            case JNode.Arr a:
                sb.Append('[');
                for (var i = 0; i < a.Items.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Value(a.Items[i]);
                }
                sb.Append(']');
                break;
            case JNode.Obj o:
                sb.Append('{');
                var pairs = sortKeys ? o.Pairs.OrderBy(p => p.Key, SwiftText.Comparer).ToList() : o.Pairs;
                for (var i = 0; i < pairs.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    String(pairs[i].Key);
                    sb.Append(':');
                    Value(pairs[i].Value);
                }
                sb.Append('}');
                break;
        }
    }

    void String(string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '/': sb.Append(escapeSlashes ? "\\/" : "/"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    /// <summary>Swift 的寫法：整數不帶小數點，其他用最短的表示（1e-07、1e+20）</summary>
    public static string FormatDouble(double x)
    {
        if (x == Math.Floor(x) && Math.Abs(x) < 1e16) return ((long)x).ToString(CultureInfo.InvariantCulture);
        var r = x.ToString("R", CultureInfo.InvariantCulture);
        var e = r.IndexOf('E');
        if (e < 0) return r;
        var mantissa = r[..e];
        var exponent = int.Parse(r[(e + 1)..], CultureInfo.InvariantCulture);
        return mantissa + "e" + (exponent < 0 ? "-" : "+") + Math.Abs(exponent).ToString("00", CultureInfo.InvariantCulture);
    }
}

/// <summary>簡單的 JSON 節點：物件保留鍵的順序（不排序時照加入的順序輸出，對應 Swift 的宣告順序）</summary>
public abstract record JNode
{
    public sealed record Null : JNode;
    public sealed record Bool(bool Value) : JNode;
    public sealed record Int(long Value) : JNode;
    public sealed record Number(double Value) : JNode;
    public sealed record Str(string Value) : JNode;

    public sealed record Arr(List<JNode> Items) : JNode
    {
        public Arr(IEnumerable<JNode> items) : this(items.ToList()) { }
    }

    public sealed record Obj(List<KeyValuePair<string, JNode>> Pairs) : JNode
    {
        public Obj() : this(new List<KeyValuePair<string, JNode>>()) { }

        public Obj Add(string key, JNode value)
        {
            Pairs.Add(new(key, value));
            return this;
        }

        public Obj Add(string key, string value) => Add(key, new Str(value));
    }

    public static readonly JNode NullValue = new Null();
    public static JNode Of(string? s) => s is null ? NullValue : new Str(s);
    public static JNode Of(long? n) => n is null ? NullValue : new Int(n.Value);
    public static JNode Of(double n) => new Number(n);
    public static JNode Of(bool b) => new Bool(b);
    public static JNode Of(IEnumerable<string> list) => new Arr(list.Select(s => (JNode)new Str(s)));

    /// <summary>從 System.Text.Json 的元素轉過來（保留原本的鍵順序）</summary>
    public static JNode From(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => new Obj(e.EnumerateObject().Select(p => new KeyValuePair<string, JNode>(p.Name, From(p.Value))).ToList()),
        JsonValueKind.Array => new Arr(e.EnumerateArray().Select(From)),
        JsonValueKind.String => new Str(e.GetString()!),
        JsonValueKind.Number => e.TryGetInt64(out var l) ? new Int(l) : new Number(e.GetDouble()),
        JsonValueKind.True => new Bool(true),
        JsonValueKind.False => new Bool(false),
        _ => NullValue,
    };
}

/// <summary>寬鬆讀取 JSON：對應 Swift 的 <c>try? container.decode(...)</c>，型別不對就當作沒有</summary>
public static class JsonRead
{
    public static JsonElement? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static JsonElement? Get(this JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) ? v : null;

    /// <summary>字串欄位；不存在、null、不是字串都是 null（Swift 的 decode(String.self) 不會把數字轉成字串）</summary>
    public static string? Str(this JsonElement e, string key) =>
        e.Get(key) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    public static bool? Bool(this JsonElement e, string key) =>
        e.Get(key) is { } v && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : null;

    public static long? Long(this JsonElement e, string key) =>
        e.Get(key) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt64(out var n) ? n : null;

    public static double? Double(this JsonElement e, string key) =>
        e.Get(key) is { ValueKind: JsonValueKind.Number } v ? v.GetDouble() : null;

    public static List<string>? Strings(this JsonElement e, string key)
    {
        if (e.Get(key) is not { ValueKind: JsonValueKind.Array } a) return null;
        var list = new List<string>();
        foreach (var item in a.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) return null;
            list.Add(item.GetString()!);
        }
        return list;
    }
}
