using Shiyin.Model;
using Xunit;

namespace Shiyin.Tests;

/// <summary>定時停止錄音（R110、M§84、design-windows §11.43）：跟 Mac 驗證時用同樣的例子（時間的字串各自產生，沒有標準答案）</summary>
public sealed class StopTimerTests
{
    [Theory]
    [InlineData("15:30", 15, 30)]
    [InlineData("15：30", 15, 30)]
    [InlineData("１５：３０", 15, 30)]
    [InlineData("1530", 15, 30)]
    [InlineData("930", 9, 30)]
    [InlineData("9:05", 9, 5)]
    [InlineData("9:5", 9, 5)]
    [InlineData(" 15 ", 15, 0)]
    [InlineData("0", 0, 0)]
    [InlineData("23:59", 23, 59)]
    public void ParsesTimes(string text, int hour, int minute) => Assert.Equal((hour, minute), StopTimer.Parse(text));

    [Theory]
    [InlineData("24:00")]
    [InlineData("12:60")]
    [InlineData("")]
    [InlineData("15:")]
    [InlineData(":30")]
    [InlineData("12345")]
    [InlineData("1:2:3")]
    [InlineData("abc")]
    [InlineData("15.30")]
    [InlineData("100:00")]
    [InlineData("-1")]
    public void RejectsOtherText(string text) => Assert.Null(StopTimer.Parse(text));

    static DateTime D(string text) => DateTime.ParseExact(text, "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void NextLabelRemainingAndDefault()
    {
        var now = D("2026-11-23 14:30:20");
        Assert.Equal(D("2026-11-23 15:30:00"), StopTimer.Next(15, 30, now));
        // 就是現在這一分、已經過了：明天
        Assert.Equal(D("2026-11-24 14:30:00"), StopTimer.Next(14, 30, now));
        Assert.Equal(D("2026-11-24 09:00:00"), StopTimer.Next(9, 0, now));
        Assert.Equal(D("2026-11-23 14:31:00"), StopTimer.Next(14, 31, now));

        Assert.Equal("15:30", StopTimer.Label(D("2026-11-23 15:30:00"), now));
        // 「明天 %@」：別的測試可能正好把介面換成英文，只看時間
        Assert.EndsWith("09:05", StopTimer.Label(D("2026-11-24 09:05:00"), now));
        Assert.NotEqual("09:05", StopTimer.Label(D("2026-11-24 09:05:00"), now));
        Assert.Equal("09:05", StopTimer.Clock(D("2026-11-24 09:05:59")));

        // 分鐘無條件進位
        Assert.Contains(StopTimer.Remaining(D("2026-11-23 15:55:00"), now), new[] { "1 小時 25 分鐘", "1 hr 25 min" });
        Assert.Contains(StopTimer.Remaining(D("2026-11-23 14:35:20"), now), new[] { "5 分鐘", "5 min" });
        Assert.Contains(StopTimer.Remaining(D("2026-11-23 14:30:30"), now), new[] { "1 分鐘", "1 min" });
        Assert.Contains(StopTimer.Remaining(D("2026-11-23 15:30:20"), now), new[] { "1 小時", "1 hr" });

        Assert.Equal("15:30", StopTimer.DefaultTime(now));
        Assert.Equal("15:35", StopTimer.DefaultTime(D("2026-11-23 14:31:00")));
        Assert.Equal("16:00", StopTimer.DefaultTime(D("2026-11-23 14:56:00")));
        Assert.Equal("01:00", StopTimer.DefaultTime(D("2026-11-23 23:58:00")));
        Assert.Equal("23:55", StopTimer.DefaultTime(D("2026-11-23 22:55:00")));
        Assert.Equal([30, 60, 90, 120, 180], StopTimer.Presets.Select(p => p.Minutes));
    }
}
