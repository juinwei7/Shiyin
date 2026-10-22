using System.Text.Json.Nodes;
using Shiyin.LLM;
using Shiyin.Model;
using Shiyin.Services;
using Xunit;

namespace Shiyin.Tests;

/// <summary>匿名使用統計（design-windows §12.10、M§30）：累計、送出的格式、送出和保留的規則，用本機的假後台</summary>
public sealed class UsageStatsTests : IDisposable
{
    readonly TempHome home = new();
    readonly TestServer server = new();
    readonly HttpClient http = new();
    readonly List<(string Path, string ContentType, string UserAgent, JsonObject Body)> requests = [];
    int reply = 204;
    DateTime now = new(2026, 10, 22, 15, 0, 0);

    public UsageStatsTests()
    {
        server.Handle = async c =>
        {
            using var reader = new StreamReader(c.Request.InputStream);
            var body = JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject();
            lock (requests) requests.Add((c.Request.Url!.AbsolutePath, c.Request.ContentType ?? "", c.Request.UserAgent ?? "", body));
            c.Response.StatusCode = reply;
            c.Response.Close();
        };
    }

    public void Dispose()
    {
        server.Dispose();
        http.Dispose();
        home.Dispose();
    }

    string File => Path.Combine(home.Path, "usage.json");

    UsageStats Make(AppSettings? settings = null, string? url = "", string chip = "Intel(R) Core(TM) i7-12700H") =>
        new(File, settings ?? AppSettings.InMemory(), http, () => new UsageStats.AppInfo("4.0.0", "Windows 11 24H2 (26100.4652)", chip, "zh-Hant"))
        {
            Url = url == "" ? server.Url + "/v1/usage" : url,
            Now = () => now,
        };

    static JsonObject Preview(UsageStats usage) => JsonNode.Parse(usage.PreviewJson())!.AsObject();

    [Fact]
    public void CountsAndFormatMatchTheMac()
    {
        var settings = AppSettings.InMemory(new Dictionary<string, string> { ["openaiCompatibleBaseURL"] = "http://localhost:11434/v1" });
        var usage = Make(settings);
        usage.Recorded(45.4);
        usage.Recorded(10.5);
        usage.Imported();
        usage.Transcribed(600, 30.2, "system", MeetingLanguage.En, imported: true, diarized: true);
        usage.Transcribed(100, 5, "japanese", MeetingLanguage.Ja, imported: false, diarized: false);
        usage.Transcribed(50, 4, "breeze", MeetingLanguage.Zh, imported: false, diarized: true);
        usage.Rediarized();
        foreach (var p in new[] { LLMProvider.Claude, LLMProvider.Codex, LLMProvider.Gemini, LLMProvider.OpenAICompatible, LLMProvider.None }) usage.Summarized(p);
        usage.Asked();
        usage.Failed(UsageStats.Failure.Recording);
        usage.Failed(UsageStats.Failure.Summarize);

        var json = usage.PreviewJson();
        var payload = JsonNode.Parse(json)!.AsObject();
        // 鍵跟 Mac 送的一樣，依字母排序（M§30.4），沒有其他欄位（R40.2）
        Assert.Equal(["app", "date", "errors", "installId", "schema", "usage"], payload.Select(p => p.Key));
        Assert.Equal(1, (int)payload["schema"]!);
        Assert.Equal("2026-10-22", (string)payload["date"]!);
        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", (string)payload["installId"]!);
        Assert.Equal("""{"chip":"Intel(R) Core(TM) i7-12700H","os":"Windows 11 24H2 (26100.4652)","platform":"windows","uiLanguage":"zh-Hant","version":"4.0.0"}""",
            payload["app"]!.ToJsonString());
        Assert.Equal("""{"diarizationRuns":3,"engines":{"breeze":50,"builtin":600,"whisperJa":100},"importedSeconds":600,"meetingLanguages":{"en":1,"ja":1,"zh":1},"meetingsImported":1,"meetingsRecorded":2,"questionsAsked":1,"recordedSeconds":56,"summarizer":{"api":1,"claudeCode":1,"codex":1,"local":1},"transcribeWallSeconds":39,"transcribedSeconds":750}""",
            payload["usage"]!.ToJsonString());
        Assert.Equal("""{"diarize":0,"recording":1,"summarize":1,"transcribe":0}""", payload["errors"]!.ToJsonString());

        // 存檔再讀回來：數字、安裝 ID 都在（同 Mac 的 usage.json 格式）
        var reloaded = Make(settings);
        Assert.Equal(json, reloaded.PreviewJson());
        var stored = JsonNode.Parse(System.IO.File.ReadAllText(File))!.AsObject();
        Assert.Equal(["days", "installId", "sent"], stored.Select(p => p.Key));
        Assert.Equal(56, (int)stored["days"]!["2026-10-22"]!["recordedSeconds"]!);
    }

    [Fact]
    public async Task SendsFinishedDaysOnce()
    {
        var usage = Make();
        usage.Touch();
        usage.Recorded(30);
        // 今天的不送
        await usage.SendPending();
        Assert.Empty(requests);

        now = now.AddDays(1);
        usage.Touch();
        await usage.SendPending();
        var request = Assert.Single(requests);
        Assert.Equal("/v1/usage", request.Path);
        Assert.StartsWith("application/json", request.ContentType);
        Assert.Equal("Shiyin/4.0.0", request.UserAgent);
        Assert.Equal("2026-10-22", (string)request.Body["date"]!);
        Assert.Equal(30, (int)request.Body["usage"]!["recordedSeconds"]!);
        // 送過的那天記下來、從匯總拿掉，不會再送
        Assert.Equal("2026-10-23", (string)Preview(usage)["date"]!);
        await usage.SendPending();
        Assert.Single(requests);
        var stored = JsonNode.Parse(System.IO.File.ReadAllText(File))!.AsObject();
        Assert.Equal(["2026-10-22"], stored["sent"]!.AsArray().Select(x => (string)x!));
        Assert.Equal(["2026-10-23"], stored["days"]!.AsObject().Select(p => p.Key));
    }

    [Fact]
    public async Task ServerErrorsKeepTheDayAndRejectionsDropIt()
    {
        var usage = Make();
        usage.Asked();
        now = now.AddDays(1);
        // 5xx、429：留著下次再送
        reply = 500;
        await usage.SendPending();
        reply = 429;
        await usage.SendPending();
        Assert.Equal(2, requests.Count);
        reply = 204;
        await usage.SendPending();
        Assert.Equal(3, requests.Count);
        Assert.All(requests, r => Assert.Equal("2026-10-22", (string)r.Body["date"]!));

        // 4xx：格式被拒，丟掉那一天，不記成送過、不再送
        usage.Asked();
        now = now.AddDays(1);
        reply = 400;
        await usage.SendPending();
        Assert.Equal(4, requests.Count);
        await usage.SendPending();
        Assert.Equal(4, requests.Count);
        var stored = JsonNode.Parse(System.IO.File.ReadAllText(File))!.AsObject();
        Assert.Equal(["2026-10-22"], stored["sent"]!.AsArray().Select(x => (string)x!));
        Assert.Empty(stored["days"]!.AsObject());
    }

    [Fact]
    public async Task NetworkErrorsKeepTheDayAndOldDaysAreDropped()
    {
        // 連不上：留著
        var usage = Make(url: "http://127.0.0.1:9/v1/usage");
        usage.Recorded(10);
        now = now.AddDays(1);
        await usage.SendPending();
        Assert.Contains("2026-10-22", JsonNode.Parse(System.IO.File.ReadAllText(File))!["days"]!.AsObject().Select(p => p.Key));

        // 超過 3 天的不送；超過 4 天的從檔案刪掉
        var later = Make();
        now = now.AddDays(4);
        await later.SendPending();
        Assert.Empty(requests);
        later.Touch();
        Assert.Equal(["2026-10-27"], JsonNode.Parse(System.IO.File.ReadAllText(File))!["days"]!.AsObject().Select(p => p.Key));
    }

    [Fact]
    public void OffOrNoUrlCountsNothing()
    {
        // 沒有網址：不累計、不建檔，設定頁也不顯示
        var none = Make(url: null);
        none.Recorded(10);
        Assert.False(System.IO.File.Exists(File));
        Assert.False(none.IsOffered);
        // DEBUG 版一律顯示
        Assert.True(new UsageStats(File, AppSettings.InMemory(), http, () => new("", "", "", "")) { AlwaysOffered = true }.IsOffered);
        // 只接受 https，http 只給本機
        Assert.Null(Make(url: "http://example.com/v1/usage").Endpoint);
        Assert.NotNull(Make(url: "https://usage.weibuilds.com/v1/usage").Endpoint);

        // 關掉：不累計，還沒送的清掉
        var settings = AppSettings.InMemory();
        var usage = Make(settings);
        usage.Recorded(10);
        settings.Set(UsageStats.EnabledKey, false);
        usage.Recorded(20);
        usage.ClearPending();
        Assert.Empty(JsonNode.Parse(System.IO.File.ReadAllText(File))!["days"]!.AsObject());
        settings.Set(UsageStats.EnabledKey, true);
        usage.Recorded(5);
        Assert.Equal(5, (int)Preview(usage)["usage"]!["recordedSeconds"]!);
    }

    [Fact]
    public void ResetIdLongTextAndBrokenFile()
    {
        var usage = Make(chip: new string('x', 70) + "😀");
        usage.Touch();
        var before = (string)Preview(usage)["installId"]!;
        usage.ResetInstallId();
        var after = (string)Preview(usage)["installId"]!;
        Assert.NotEqual(before, after);
        Assert.Equal(after, (string)Preview(Make())["installId"]!);
        // 後台只收 64 字以內：截斷
        Assert.Equal(new string('x', 64), (string)Preview(usage)["app"]!["chip"]!);

        // 檔案壞掉：重新開始，不出錯
        System.IO.File.WriteAllText(File, "{ not json");
        var fresh = Make();
        fresh.Recorded(7);
        Assert.Equal(7, (int)Preview(fresh)["usage"]!["recordedSeconds"]!);
        Assert.NotEqual(after, (string)Preview(fresh)["installId"]!);
    }
}
