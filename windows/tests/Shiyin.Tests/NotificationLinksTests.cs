using Shiyin.Services;
using Xunit;
using Link = Shiyin.Services.NotificationLinks.Link;

namespace Shiyin.Tests;

/// <summary>通知的 shiyin: 網址（design-windows §12.2、D62）：會改變狀態的動作要權杖</summary>
public sealed class NotificationLinksTests
{
    [Fact]
    public void OpensMeeting()
    {
        var links = new NotificationLinks();
        Assert.Equal(new Link.Open(12), links.Parse(NotificationLinks.OpenMeeting(12)));
        Assert.Equal(new Link.Open(null), links.Parse("shiyin:open"));
        Assert.Equal(new Link.Open(null), links.Parse("shiyin:open?meeting=abc"));
        Assert.Equal(new Link.Open(null), links.Parse("shiyin:open?meeting=-3"));
        // 本地 MCP 的會議連結（M§44.5）：shiyin://open，可以帶秒數
        Assert.Equal(new Link.Open(42, 320), links.Parse("shiyin://open?meeting=42&t=320"));
        Assert.Equal(new Link.Open(42), links.Parse("shiyin://open?meeting=42"));
        Assert.Equal(new Link.Open(42, 0), links.Parse("shiyin://open?meeting=42&t=-5"));
        Assert.Equal(new Link.Open(42), links.Parse("shiyin://open?meeting=42&t=abc"));
        Assert.Equal(new Link.Open(null), links.Parse("shiyin://open?t=5"));
    }

    [Fact]
    public void PromptNeedsThisRunsToken()
    {
        var links = new NotificationLinks();
        Assert.Equal(new Link.Prompt("start", "zoom"), links.Parse(links.Prompt("start", "zoom")));
        Assert.Equal(new Link.Prompt("mute", "ms teams"), links.Parse(links.Prompt("mute", "ms teams")));
        // 網頁裡的連結、上次啟動留下的通知：只打開主視窗
        Assert.Equal(new Link.Open(null), links.Parse("shiyin:prompt?action=start&app=zoom"));
        Assert.Equal(new Link.Open(null), links.Parse(new NotificationLinks().Prompt("start", "zoom")));
        Assert.Equal(new Link.Open(null), links.Parse($"shiyin:prompt?action=delete&app=zoom&token={links.Token}"));
        Assert.Equal(new Link.Open(null), links.Parse($"shiyin:prompt?action=start&token={links.Token}"));
    }

    [Fact]
    public void IgnoresOtherArguments()
    {
        var links = new NotificationLinks();
        Assert.Null(links.Parse(@"C:\錄音\會議.m4a"));
        Assert.Null(links.Parse("https://example.com/shiyin:open"));
        Assert.True(NotificationLinks.IsLink("SHIYIN:open"));
        Assert.Equal(new Link.Open(null), links.Parse("shiyin:something-else"));
    }

    [Fact]
    public void TokensDifferPerRun() => Assert.NotEqual(new NotificationLinks().Token, new NotificationLinks().Token);
}
