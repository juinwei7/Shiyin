using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Shiyin.Localization;

/// <summary>
/// 介面文字（design-windows §11.4）：鍵是中文原文，跟 Mac 的 Localizable.xcstrings 一樣；英文介面查表，
/// 中文介面直接用鍵。插值的寫法照 xcstrings（<c>%@</c>、<c>%lld</c>、<c>%1$@</c>），才能沿用 Mac 的英文。
/// </summary>
public static partial class L
{
    static IReadOnlyDictionary<string, string> table = new Dictionary<string, string>();
    static IReadOnlyDictionary<string, (string One, string Other)> plurals = new Dictionary<string, (string, string)>();

    /// <summary>目前的介面語言是不是英文（app 啟動時依 Windows 的顯示語言或設定決定）</summary>
    public static bool English { get; private set; }

    /// <summary>pluralTable：英文有單複數的條目（xcstrings 的 variations.plural）</summary>
    public static void Use(IReadOnlyDictionary<string, string>? englishTable, IReadOnlyDictionary<string, (string One, string Other)>? pluralTable = null)
    {
        table = englishTable ?? new Dictionary<string, string>();
        plurals = pluralTable ?? new Dictionary<string, (string, string)>();
        English = englishTable is not null;
    }

    /// <summary>沒有插值的文字</summary>
    public static string T(string key) => English && table.TryGetValue(key, out var v) ? v : key;

    /// <summary>有插值的文字：<c>L.F("找不到 %@ 指令", name)</c>。英文有單複數時看第一個整數</summary>
    public static string F(string key, params object?[] args)
    {
        if (English && plurals.TryGetValue(key, out var p))
        {
            var count = args.FirstOrDefault(a => a is int or long);
            return Format(count is not null && Convert.ToInt64(count, CultureInfo.InvariantCulture) == 1 ? p.One : p.Other, args);
        }
        return Format(T(key), args);
    }

    /// <summary>讀 xcstrings（Mac 的 Localizable.xcstrings 同一種格式）的英文：一般的條目、單複數的條目</summary>
    public static (Dictionary<string, string> Table, Dictionary<string, (string One, string Other)> Plurals) ReadXcstrings(string json)
    {
        var result = new Dictionary<string, string>();
        var plural = new Dictionary<string, (string, string)>();
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        foreach (var entry in doc.RootElement.GetProperty("strings").EnumerateObject())
        {
            if (!entry.Value.TryGetProperty("localizations", out var locs) || !locs.TryGetProperty("en", out var en)) continue;
            if (en.TryGetProperty("stringUnit", out var unit) && unit.TryGetProperty("value", out var value))
                result[entry.Name] = value.GetString() ?? entry.Name;
            else if (en.TryGetProperty("variations", out var variations) && variations.TryGetProperty("plural", out var forms))
            {
                string Form(string name) => forms.TryGetProperty(name, out var f) && f.TryGetProperty("stringUnit", out var u)
                    && u.TryGetProperty("value", out var v) ? v.GetString() ?? entry.Name : entry.Name;
                var other = Form("other");
                plural[entry.Name] = (forms.TryGetProperty("one", out _) ? Form("one") : other, other);
                result[entry.Name] = other;
            }
        }
        return (result, plural);
    }

    [GeneratedRegex(@"%(?:(\d+)\$)?(?:@|lld|ld|d|lf|f)")]
    private static partial Regex Placeholder();

    public static string Format(string format, params object?[] args)
    {
        var next = 0;
        return Placeholder().Replace(format, m =>
        {
            var index = m.Groups[1].Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) - 1 : next++;
            return index >= 0 && index < args.Length ? Convert.ToString(args[index], CultureInfo.InvariantCulture) ?? "" : "";
        });
    }
}
