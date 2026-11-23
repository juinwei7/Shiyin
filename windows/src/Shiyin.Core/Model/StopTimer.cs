using System.Globalization;
using System.Text;
using Shiyin.Localization;

namespace Shiyin.Model;

/// <summary>
/// 定時停止錄音（R110、M§84、design-windows §11.43；同 Mac 的 StopTimer）：選項、輸入的時間、畫面上的字。
/// 時間都是本機時間（DateTime.Now），跟著時鐘走
/// </summary>
public static class StopTimer
{
    /// <summary>選單的選項：從現在起幾分鐘後</summary>
    public static IReadOnlyList<(int Minutes, string Title)> Presets =>
    [
        (30, L.T("30 分鐘後")),
        (60, L.T("1 小時後")),
        (90, L.T("1 小時 30 分鐘後")),
        (120, L.T("2 小時後")),
        (180, L.T("3 小時後")),
    ];

    /// <summary>「指定時間…」輸入的時間：15:30、15：30、1530、930、9:05、15（整點）；全形數字也可以</summary>
    public static (int Hour, int Minute)? Parse(string text)
    {
        var parts = text.Normalize(NormalizationForm.FormKC).Trim().Split(':');
        if (parts.Length > 2 || parts.Any(p => p.Length == 0 || p.Any(c => c is < '0' or > '9'))) return null;
        int hour, minute;
        if (parts.Length == 2)
        {
            if (parts[0].Length > 2 || parts[1].Length > 2) return null;
            (hour, minute) = (Int(parts[0]), Int(parts[1]));
        }
        else
        {
            var digits = parts[0];
            if (digits.Length > 4) return null;
            (hour, minute) = digits.Length <= 2 ? (Int(digits), 0) : (Int(digits[..^2]), Int(digits[^2..]));
        }
        return hour is >= 0 and <= 23 && minute is >= 0 and <= 59 ? (hour, minute) : null;
    }

    static int Int(string digits) => int.Parse(digits, CultureInfo.InvariantCulture);

    /// <summary>下一個那個時間：今天的還沒到就是今天，到了（或就是現在這一分）就是明天</summary>
    public static DateTime Next(int hour, int minute, DateTime now)
    {
        var today = now.Date.AddHours(hour).AddMinutes(minute);
        return today > now ? today : today.AddDays(1);
    }

    /// <summary>「15:30」（24 小時制，兩位數）：「指定時間…」的欄位也用這個</summary>
    public static string Clock(DateTime date) => $"{date.Hour:00}:{date.Minute:00}";

    /// <summary>畫面上寫的：「15:30」；跟現在不是同一天時「明天 09:00」</summary>
    public static string Label(DateTime date, DateTime now) => date.Date == now.Date ? Clock(date) : L.F("明天 %@", Clock(date));

    /// <summary>還有多久：分鐘無條件進位，「1 小時 25 分鐘」「5 分鐘」（不到 1 分鐘也寫 1 分鐘）</summary>
    public static string Remaining(DateTime date, DateTime now) =>
        L.Ui(TimeFormat.Duration((int)Math.Ceiling(Math.Max(0, (date - now).TotalSeconds) / 60) * 60, MeetingLanguages.Interface));

    /// <summary>「指定時間…」沒設定過時帶的值：一小時後，分鐘往上取到 5 的倍數</summary>
    public static string DefaultTime(DateTime now)
    {
        var later = now.AddHours(1);
        int hour = later.Hour, minute = (later.Minute + 4) / 5 * 5;
        if (minute == 60) (hour, minute) = ((hour + 1) % 24, 0);
        return $"{hour:00}:{minute:00}";
    }
}
