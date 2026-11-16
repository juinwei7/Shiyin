namespace Shiyin.Text;

/// <summary>名稱排序跟 Finder 一樣：數字照大小（「第 2 週」排在「第 10 週」前面）</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (x is null || y is null) return string.Compare(x, y, StringComparison.Ordinal);
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                var si = i; while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                var sj = j; while (j < y.Length && char.IsAsciiDigit(y[j])) j++;
                var a = x[si..i].TrimStart('0');
                var b = y[sj..j].TrimStart('0');
                var c = a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
                if (c != 0) return c;
                continue;
            }
            var d = string.Compare(x[i].ToString(), y[j].ToString(), System.Globalization.CultureInfo.CurrentCulture, System.Globalization.CompareOptions.IgnoreCase);
            if (d != 0) return d;
            i++; j++;
        }
        return (x.Length - i).CompareTo(y.Length - j);
    }
}
