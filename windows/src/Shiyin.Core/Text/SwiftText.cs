using System.Globalization;
using System.Text;

namespace Shiyin.Text;

/// <summary>
/// 跟 Swift 的 String 一樣的文字處理（design-windows §4）：Mac 版用 Character（字形叢集）計算長度、
/// 用 Unicode 純量值排序，Windows 照同樣的規則，提示詞和匯出才會逐字相同。
/// </summary>
public static class SwiftText
{
    /// <summary>字形叢集的數量（Swift 的 <c>String.count</c>）</summary>
    public static int Count(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        var e = StringInfo.GetTextElementEnumerator(s);
        var n = 0;
        while (e.MoveNext()) n++;
        return n;
    }

    /// <summary>拆成字形叢集（Swift 的 Character 陣列）</summary>
    public static List<string> Characters(string s)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(s)) return list;
        var e = StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext()) list.Add((string)e.Current);
        return list;
    }

    /// <summary>最後 n 個字形叢集（Swift 的 <c>suffix(n)</c>）</summary>
    public static string Suffix(string s, int n)
    {
        var chars = Characters(s);
        return chars.Count <= n ? s : string.Concat(chars.Skip(chars.Count - n));
    }

    /// <summary>前 n 個字形叢集（Swift 的 <c>prefix(n)</c>）</summary>
    public static string Prefix(string s, int n)
    {
        var chars = Characters(s);
        return chars.Count <= n ? s : string.Concat(chars.Take(n));
    }

    /// <summary>Swift 的 <c>Character.isLetter</c>：第一個純量有 Alphabetic 屬性</summary>
    public static bool IsLetter(string character)
    {
        if (string.IsNullOrEmpty(character)) return false;
        var rune = Rune.GetRuneAt(character, 0);
        return Rune.IsLetter(rune) || Rune.GetUnicodeCategory(rune) == UnicodeCategory.LetterNumber;
    }

    /// <summary>Swift 的 <c>Character.isNumber</c>：第一個純量有數值。除了 Nd、Nl、No，還有 Unihan 有數值的漢字
    /// （一、二、十、兩、萬、零…），Swift 正規表示式的 <c>\d</c> 也是這個規則</summary>
    public static bool IsNumber(string character)
    {
        if (string.IsNullOrEmpty(character)) return false;
        var rune = Rune.GetRuneAt(character, 0);
        return Rune.IsNumber(rune) || HanNumerals.Contains(rune.Value);
    }

    /// <summary>有數值、但一般分類不是 N 的字（從 macOS 26 的 Unicode 資料列出，design-windows §4.2）</summary>
    static readonly HashSet<int> HanNumerals = [0x3405, 0x3483, 0x382A, 0x3B4D, 0x4E00, 0x4E03, 0x4E07, 0x4E09, 0x4E24, 0x4E5D, 0x4E8C, 0x4E94, 0x4E96, 0x4EAC, 0x4EBF, 0x4EC0, 0x4EDF, 0x4EE8, 0x4F0D, 0x4F70, 0x4FE9, 0x5006, 0x5104, 0x5146, 0x5169, 0x516B, 0x516D, 0x5341, 0x5343, 0x5344, 0x5345, 0x534C, 0x53C1, 0x53C2, 0x53C3, 0x53C4, 0x56DB, 0x58F1, 0x58F9, 0x5E7A, 0x5EFE, 0x5EFF, 0x5F0C, 0x5F0D, 0x5F0E, 0x5F10, 0x62D0, 0x62FE, 0x634C, 0x67D2, 0x6D1E, 0x6F06, 0x7396, 0x767E, 0x7695, 0x79ED, 0x8086, 0x842C, 0x8CAE, 0x8CB3, 0x8D30, 0x920E, 0x94A9, 0x9621, 0x9646, 0x964C, 0x9678, 0x96F6, 0xF96B, 0xF973, 0xF978, 0xF9B2, 0xF9D1, 0xF9D3, 0xF9FD, 0x12038, 0x12039, 0x12079, 0x12226, 0x1222B, 0x1230B, 0x1230D, 0x12399, 0x20001, 0x20064, 0x200E2, 0x20121, 0x2092A, 0x20983, 0x2098C, 0x2099C, 0x20AEA, 0x20AFD, 0x20B19, 0x22390, 0x22998, 0x23B1B, 0x2626D, 0x2F890];

    /// <summary>Swift 的 <c>Character.isASCII</c></summary>
    public static bool IsAscii(string character) => character.Length == 1 && character[0] < 128;

    /// <summary>Swift 的 <c>lowercased()</c></summary>
    public static string Lower(string s) => s.ToLowerInvariant();

    /// <summary>Swift 的 <c>trimmingCharacters(in: .whitespacesAndNewlines)</c></summary>
    public static string Trim(string s) => s.Trim();

    /// <summary>Swift 的 <c>trimmingCharacters(in: .whitespaces)</c>：空白和 tab，不含換行</summary>
    public static string TrimSpaces(string s)
    {
        int start = 0, end = s.Length;
        while (start < end && IsSpace(s[start])) start++;
        while (end > start && IsSpace(s[end - 1])) end--;
        return s[start..end];
    }

    static bool IsSpace(char c) => c == '\t' || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;

    /// <summary>Swift 的 String 比較：依 Unicode 純量值（C# 的 Ordinal 是依 UTF-16，表情符號會排錯）</summary>
    public static int Compare(string? a, string? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a is null) return -1;
        if (b is null) return 1;
        var ea = a.EnumerateRunes().GetEnumerator();
        var eb = b.EnumerateRunes().GetEnumerator();
        while (true)
        {
            var ha = ea.MoveNext();
            var hb = eb.MoveNext();
            if (!ha || !hb) return ha == hb ? 0 : ha ? 1 : -1;
            var c = ea.Current.Value.CompareTo(eb.Current.Value);
            if (c != 0) return c;
        }
    }

    public static readonly IComparer<string> Comparer = Comparer<string>.Create(Compare);

    /// <summary>Swift 的 <c>Double.rounded()</c>：.5 往遠離零的方向</summary>
    public static double Round(double x) => Math.Round(x, MidpointRounding.AwayFromZero);

    /// <summary>Swift 的 <c>Int(x)</c>：往零截斷</summary>
    public static long Truncate(double x) => (long)Math.Truncate(x);
}
