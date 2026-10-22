using System.Globalization;
using Shiyin.Text;

namespace Shiyin.Model;

/// <summary>時間點格式：mm:ss，超過一小時用 h:mm:ss（同 Mac 的 TimeFormat）</summary>
public static class TimeFormat
{
    public static string String(double seconds)
    {
        var s = Math.Max(0, SwiftText.Truncate(seconds));
        if (s >= 3600) return $"{s / 3600}:{s % 3600 / 60:00}:{s % 60:00}";
        return $"{s / 60:00}:{s % 60:00}";
    }

    const string CleanCharacters = "[]()（）【】";

    /// <summary>模型偶爾會照抄逐字稿的寫法，把時間寫成「[03:15]」：去掉前後的括號和空白</summary>
    public static string Clean(string text)
    {
        int start = 0, end = text.Length;
        while (start < end && IsClean(text[start])) start++;
        while (end > start && IsClean(text[end - 1])) end--;
        return text[start..end];
    }

    static bool IsClean(char c) => CleanCharacters.Contains(c) || char.IsWhiteSpace(c);

    /// <summary>「03:15–05:20」這種範圍取開頭；全形冒號也接受。不是時間回傳 null</summary>
    public static double? Seconds(string text)
    {
        var cleaned = Clean(text).Replace('：', ':');
        var first = cleaned.Split(['–', '—', '-', '~', '〜', '～', ' '], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        var parts = first.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Length > 3) return null;
        long total = 0;
        foreach (var part in parts)
        {
            if (!IsSwiftInt(part, out var n)) return null;
            total = total * 60 + n;
        }
        return total;
    }

    /// <summary>Swift 的 <c>Int(String)</c>：可以有正負號，只接受 ASCII 數字</summary>
    static bool IsSwiftInt(string s, out long value)
    {
        value = 0;
        if (s.Length == 0) return false;
        var body = s[0] is '+' or '-' ? s[1..] : s;
        if (body.Length == 0 || body.Any(c => c is < '0' or > '9')) return false;
        return long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>會議長度的口語說法，例如「9 分鐘」「1 小時 5 分鐘」</summary>
    public static string Duration(int? seconds, MeetingLanguage language)
    {
        if (seconds is null) return "";
        var minutes = Math.Max(1, (long)SwiftText.Round(seconds.Value / 60.0));
        long h = minutes / 60, m = minutes % 60;
        if (minutes >= 60)
            return m == 0 ? language.Text($"{h} 小時", $"{h} hr", $"{h} 時間")
                : language.Text($"{h} 小時 {m} 分鐘", $"{h} hr {m} min", $"{h} 時間 {m} 分");
        return language.Text($"{minutes} 分鐘", $"{minutes} min", $"{minutes} 分");
    }

    /// <summary>srt 用 00:01:02,345；vtt 用 00:01:02.345</summary>
    public static string Subtitle(double seconds, string separator)
    {
        var ms = (long)SwiftText.Round(Math.Max(0, seconds) * 1000);
        return $"{ms / 3_600_000:00}:{ms % 3_600_000 / 60_000:00}:{ms % 60_000 / 1000:00}{separator}{ms % 1000:000}";
    }
}

/// <summary>日期：yyyy-MM-dd</summary>
public static class DateText
{
    public static string String(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static DateOnly? Date(string text) =>
        DateOnly.TryParseExact(text, ["yyyy-MM-dd", "yyyy-M-d"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    static readonly string[] Zh = ["星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六"];
    static readonly string[] En = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
    static readonly string[] Ja = ["日曜日", "月曜日", "火曜日", "水曜日", "木曜日", "金曜日", "土曜日"];

    /// <summary>星期幾：中文「星期四」、英文「Thursday」、日文「木曜日」（Mac 用系統的格式，結果相同，這裡直接寫）</summary>
    public static string Weekday(string text, MeetingLanguage language)
    {
        if (Date(text) is not { } date) return "";
        var i = (int)date.DayOfWeek;
        return language.Text(Zh[i], En[i], Ja[i]);
    }
}
