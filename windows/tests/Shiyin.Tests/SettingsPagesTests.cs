using System.Text.Json;
using System.Text.RegularExpressions;
using Shiyin.LLM;
using Shiyin.Localization;
using Shiyin.Services;
using Xunit;

namespace Shiyin.Tests;

/// <summary>設定的十一頁（R108、design-windows §11.41）：代號和分組同 Mac、搜尋、記住的頁面、控制通道的頁名</summary>
public sealed class SettingsPagesTests
{
    static string Repo(params string[] parts) => Path.Combine([Golden.Root, "..", "..", .. parts]);

    [Fact]
    public void PagesAndGroupsMatchMac()
    {
        // 代號、順序、分組照 Mac 的 SettingsPage（Sources/Shiyin/Views/SettingsView.swift）
        // Windows 的 git 取出 .swift 時換行是 CRLF（.gitattributes 只把 .cs 固定成 LF），下面的正規表示式照 LF 寫
        var swift = File.ReadAllText(Repo("Sources", "Shiyin", "Views", "SettingsView.swift")).Replace("\r\n", "\n");
        var cases = Regex.Match(swift, @"enum SettingsPage[^{]*\{\s*case ([a-z, ]+)\n").Groups[1].Value.Split(',', StringSplitOptions.TrimEntries);
        Assert.Equal(cases, SettingsPages.All);
        var groups = Regex.Match(swift, @"static let groups: \[\[SettingsPage\]\] = \[(.*?)\]\n", RegexOptions.Singleline).Groups[1].Value;
        var macGroups = Regex.Matches(groups, @"\[([^\]]*)\]").Select(m => Regex.Matches(m.Groups[1].Value, @"\.(\w+)").Select(x => x.Groups[1].Value).ToList()).ToList();
        Assert.Equal(4, macGroups.Count);
        Assert.Equal(macGroups, SettingsPages.Groups.Select(g => g.ToList()).ToList());
        Assert.Contains("static let storageKey = \"settingsPage\"", swift);
        Assert.Equal("settingsPage", AppSettings.SettingsPageKey);
        Assert.Equal(["整理工具", "範本", "錄音", "轉錄", "模型與空間", "說話者", "詞彙表", "一般", "AI 工具", "企業伺服器", "檢查與更新"],
            SettingsPages.All.Select(SettingsPages.Title));
    }

    [Fact]
    public void SearchFindsThePage()
    {
        // R108.4 的三個例子都只剩一頁（設定視窗會直接選它）
        Assert.Equal(["recording"], SettingsPages.Find("麥克風"));
        Assert.Equal(["provider"], SettingsPages.Find("API key"));
        Assert.Equal(["recording"], SettingsPages.Find("快捷鍵"));
        Assert.Equal(["provider"], SettingsPages.Find("  api KEY "));   // 不分大小寫、去掉前後空白
        Assert.Equal(["ai"], SettingsPages.Find("mcp"));
        Assert.Equal(["provider", "ai"], SettingsPages.Find("Codex"));
        Assert.Equal(["models"], SettingsPages.Find("顯示卡"));
        Assert.Equal(["general"], SettingsPages.Find("介面語言"));
        Assert.Equal(["status"], SettingsPages.Find("關於"));
        Assert.Equal(["glossary"], SettingsPages.Find("詞彙表"));
        Assert.Equal(SettingsPages.All, SettingsPages.Find(""));
        Assert.Equal(SettingsPages.All, SettingsPages.Find("   "));
        Assert.Empty(SettingsPages.Find("找不到的設定"));
    }

    [Fact]
    public void ChineseKeywordsAreTheSettingsOwnWords()
    {
        // 關鍵字是那一頁項目的名稱：設定視窗、模型清單改了字，搜尋就要跟著改
        var source = File.ReadAllText(Repo("windows", "src", "Shiyin.App", "SettingsWindow.cs"))
            + File.ReadAllText(Repo("windows", "src", "Shiyin.Services", "ModelStore.cs"));
        foreach (var id in SettingsPages.All)
            foreach (var keyword in SettingsPages.Keywords(id).Where(k => k.Any(c => c >= 0x3000)))
                Assert.True(source.Contains($"\"{keyword}\"", StringComparison.Ordinal), $"{id}: {keyword}");
    }

    [Fact]
    public void EnglishTitlesAndSearch()
    {
        var (table, plurals) = L.ReadXcstrings(File.ReadAllText(Repo("Resources", "Localizable.xcstrings")));
        var (windows, _) = L.ReadXcstrings(File.ReadAllText(Repo("windows", "src", "Shiyin.App", "Strings", "Windows.xcstrings")));
        foreach (var (key, value) in windows) table[key] = value;
        try
        {
            L.Use(table, plurals);
            Assert.Equal(["Summarizer", "Templates", "Recording", "Transcription", "Models & Storage", "Speakers", "Glossary", "General", "AI Tools",
                          "Enterprise server", "Status & Updates"], SettingsPages.All.Select(SettingsPages.Title));
            // 英文介面每個關鍵字都有英文（沒有中文字）
            foreach (var id in SettingsPages.All)
                Assert.All(SettingsPages.Keywords(id), k => Assert.DoesNotContain(k, c => c >= 0x3000));
            Assert.Equal(["recording"], SettingsPages.Find("microphone"));
            Assert.Equal(["recording"], SettingsPages.Find("shortcut"));
            Assert.Equal(["provider"], SettingsPages.Find("API key"));
        }
        finally { L.Use(null); }
    }

    [Fact]
    public void ResolvesPageNames()
    {
        Assert.Equal("models", SettingsPages.Resolve("models"));
        Assert.Equal("models", SettingsPages.Resolve(" Models "));
        Assert.Equal("provider", SettingsPages.Resolve("0"));
        Assert.Equal("status", SettingsPages.Resolve("10"));
        Assert.Null(SettingsPages.Resolve("11"));
        Assert.Null(SettingsPages.Resolve("-1"));
        // 4.8 以前的四頁
        Assert.Equal("provider", SettingsPages.Resolve("provider"));
        Assert.Equal("recording", SettingsPages.Resolve("recording"));
        Assert.Equal("speakers", SettingsPages.Resolve("names"));
        Assert.Equal("general", SettingsPages.Resolve("advanced"));
        Assert.Equal("models", SettingsPages.Resolve("storage"));
        Assert.Null(SettingsPages.Resolve("bogus"));
        Assert.Null(SettingsPages.Resolve(null));
        // 會議頁的「打開設定」：公司的伺服器沒登入時打開企業伺服器，其他打開整理工具
        Assert.Equal("enterprise", new SummarizerProblem(SummarizerProblemKind.EnterpriseSignedOut, LLMProvider.Enterprise).SettingsPage);
        Assert.Equal("provider", new SummarizerProblem(SummarizerProblemKind.MissingKey, LLMProvider.Anthropic).SettingsPage);
        Assert.Equal("provider", new SummarizerProblem(SummarizerProblemKind.NotChosen, LLMProvider.None).SettingsPage);
    }

    [Fact]
    public void RemembersTheLastPage()
    {
        using var home = new TempHome();
        var path = Path.Combine(home.Path, "settings.json");
        var settings = AppSettings.Load(path);
        var writes = 0;
        settings.Changed += key => { if (key == AppSettings.SettingsPageKey) writes++; };
        Assert.Equal("provider", SettingsPages.Stored(settings));   // 沒存過：整理工具
        SettingsPages.Remember(settings, "glossary");
        SettingsPages.Remember(settings, "glossary");               // 一樣的不再寫
        SettingsPages.Remember(settings, "bogus");                  // 不是十一頁的不寫
        Assert.Equal(1, writes);
        Assert.Equal("glossary", SettingsPages.Stored(settings));
        // 寫進 settings.json，鍵、值同 Mac 的 UserDefaults
        Assert.Equal("glossary", JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("settingsPage").GetString());
        Assert.Equal("glossary", SettingsPages.Stored(AppSettings.Load(path)));
        // 存的不是十一頁的代號（舊的頁名、壞掉）：整理工具
        settings.Set(AppSettings.SettingsPageKey, "names");
        Assert.Equal("provider", SettingsPages.Stored(settings));
    }
}
