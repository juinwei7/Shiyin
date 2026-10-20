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

    /// <summary>目前的介面語言是不是英文（app 啟動時依 Windows 的顯示語言或設定決定）</summary>
    public static bool English { get; private set; }

    public static void Use(IReadOnlyDictionary<string, string>? englishTable)
    {
        table = englishTable ?? new Dictionary<string, string>();
        English = englishTable is not null;
    }

    /// <summary>沒有插值的文字</summary>
    public static string T(string key) => English && table.TryGetValue(key, out var v) ? v : key;

    /// <summary>有插值的文字：<c>L.F("找不到 %@ 指令", name)</c></summary>
    public static string F(string key, params object?[] args) => Format(T(key), args);

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
