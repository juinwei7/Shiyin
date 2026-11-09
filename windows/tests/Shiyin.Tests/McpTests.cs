using System.Text.Json;
using System.Text.Json.Nodes;
using Shiyin.Data;
using Shiyin.Model;
using Shiyin.Services;
using Shiyin.Services.Mcp;
using Xunit;

namespace Shiyin.Tests;

/// <summary>本地 MCP（M§44.6、design-windows §11.12）：照 fixtures.json 的 mcp 建資料庫、叫工具，回傳的文字跟 Mac 的 mcp/calls.json 逐字比</summary>
public sealed class McpTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "shiyin-mcp-" + Guid.NewGuid().ToString("N"));

    public McpTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        SqliteCleanup();
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }

    static void SqliteCleanup() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    /// <summary>跟 Mac 的 GoldenDump.insertFixture 一樣</summary>
    static long InsertFixture(AppDatabase db, Golden.MeetingFixture fx, Meeting? meeting = null, string? audioExtension = "m4a")
    {
        var inserted = db.InsertMeeting(meeting ?? fx.Meeting, audioExtension);
        var mid = inserted.Id!.Value;
        var segments = fx.Segments.Select(s => { var c = s.Clone(); c.MeetingId = mid; return c; }).ToList();
        db.SaveTranscript(mid, segments, fx.Meeting.DurationSeconds ?? 0, segments.Select(s => s.Speaker ?? 0).DefaultIfEmpty(0).Max(), fx.Voices, "breeze");
        foreach (var (idx, name) in fx.Speakers.OrderBy(p => p.Key)) db.RenameSpeaker(mid, idx, name);
        foreach (var b in fx.Bookmarks) db.AddBookmark(mid, b.Time, b.Label);
        if (fx.Notes is { } notes) db.SaveNotes(mid, notes, "claude", "整理", NotesSource.Generated);  // l10n-skip
        db.UpdateUserNotes(mid, fx.Meeting.UserNotes);
        foreach (var term in fx.Glossary) db.AddTerm(term);
        return mid;
    }

    Dictionary<string, long> Seed(AppDatabase db)
    {
        var spec = Golden.Fixtures.GetProperty("mcp");
        var ids = new Dictionary<string, long>();
        foreach (var key in spec.GetProperty("meetings").EnumerateArray()) ids[key.GetString()!] = InsertFixture(db, Golden.Meeting(key.GetString()!));
        var trashed = InsertFixture(db, Golden.Meeting(spec.GetProperty("trashed").GetString()!));
        db.SetTrashed(trashed, true);
        ids["trashed"] = trashed;
        var fx = Golden.Meeting(spec.GetProperty("recording").GetString()!);
        var recording = fx.Meeting.Clone();
        recording.Source = "recording";
        ids["recording"] = InsertFixture(db, fx, recording, audioExtension: null);
        return ids;
    }

    [Fact]
    public void ToolsMatchMac()
    {
        using var db = new AppDatabase(Path.Combine(dir, "mcp.sqlite"));
        var ids = Seed(db);
        var expected = Golden.ExpectedJson("mcp/calls.json").EnumerateArray().ToList();
        var calls = Golden.Fixtures.GetProperty("mcp").GetProperty("calls").EnumerateArray().ToList();
        Assert.Equal(expected.Count, calls.Count);
        for (var i = 0; i < calls.Count; i++)
        {
            var call = calls[i];
            var name = call.GetProperty("name").GetString()!;
            var args = call.TryGetProperty("args", out var a) ? (JsonNode.Parse(a.GetRawText()) as JsonObject)! : [];
            if (call.TryGetProperty("meeting", out var key)) args["meeting_id"] = ids.GetValueOrDefault(key.GetString()!);
            var limit = call.TryGetProperty("maxChars", out var mc) ? mc.GetInt32() : McpTools.TranscriptLimit;
            var result = McpTools.Call(name, args, db, limit);
            var where = $"mcp/calls.json[{i}] {name} {args.ToJsonString()}";
            if (expected[i].TryGetProperty("unknown", out _))
            {
                Assert.True(result is null, where);
                continue;
            }
            Assert.NotNull(result);
            Assert.True(expected[i].GetProperty("text").GetString() == result.Text,
                $"{where}\n--- Mac\n{expected[i].GetProperty("text").GetString()}\n--- Windows\n{result.Text}");
            Assert.True(expected[i].GetProperty("isError").GetBoolean() == result.IsError, where);
        }
    }

    [Fact]
    public void DefinitionsMatchMac()
    {
        var expected = Golden.ExpectedJson("mcp/tools.json");
        Assert.Equal(expected.GetProperty("instructions").GetString(), McpTools.Instructions);
        Golden.SameJson(expected.GetProperty("tools"), JsonDocument.Parse(McpTools.Definitions().ToJsonString()).RootElement, "mcp/tools.json");
    }

    // MARK: 協定、開關、唯讀（M§44.1）

    static JsonObject Request(object id, string method, JsonObject? parameters = null) => new()
    {
        ["jsonrpc"] = "2.0", ["id"] = JsonValue.Create(id), ["method"] = method, ["params"] = parameters ?? [],
    };

    static string ToolText(JsonObject reply) => reply["result"]!["content"]![0]!["text"]!.GetValue<string>();

    [Fact]
    public void Protocol()
    {
        var paths = new ShiyinPaths(dir);
        var init = McpServer.Handle(Request(1, "initialize", new JsonObject { ["protocolVersion"] = "2025-03-26" }).ToJsonString(), paths, "4.7.0")!;
        Assert.Equal("2025-03-26", init["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.Equal("shiyin", init["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        Assert.Equal(McpTools.Instructions, init["result"]!["instructions"]!.GetValue<string>());
        var old = McpServer.Handle(Request("a", "initialize", new JsonObject { ["protocolVersion"] = "1999-01-01" }).ToJsonString(), paths, "4.7.0")!;
        Assert.Equal(McpServer.FallbackProtocolVersion, old["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.Equal("a", old["id"]!.GetValue<string>());
        Assert.Null(McpServer.Handle("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}", paths, "4.7.0"));
        Assert.Equal(5, McpServer.Handle(Request(2, "tools/list").ToJsonString(), paths, "4.7.0")!["result"]!["tools"]!.AsArray().Count);
        Assert.Equal(-32602, McpServer.Handle(Request(3, "tools/call", new JsonObject { ["name"] = "nope" }).ToJsonString(), paths, "4.7.0")!["error"]!["code"]!.GetValue<int>());
        Assert.Equal(-32601, McpServer.Handle(Request(4, "resources/list").ToJsonString(), paths, "4.7.0")!["error"]!["code"]!.GetValue<int>());
        var parse = McpServer.Handle("not json", paths, "4.7.0")!;
        Assert.Equal(-32700, parse["error"]!["code"]!.GetValue<int>());
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(parse.ToJsonString()).RootElement.GetProperty("id").ValueKind);
    }

    [Fact]
    public void SwitchCompanyAndDatabaseState()
    {
        var paths = new ShiyinPaths(dir);
        JsonObject Call(IReadOnlyDictionary<string, string>? overrides = null) =>
            McpServer.Handle(Request(1, "tools/call", new JsonObject { ["name"] = "list_meetings" }).ToJsonString(), paths, "4.7.0", overrides)!;
        // 預設關
        Assert.StartsWith("Reading meetings is turned off in Shiyin.", ToolText(Call()));
        Assert.True(Call()["result"]!["isError"]!.GetValue<bool>());
        var on = new Dictionary<string, string> { [AppSettings.McpEnabledKey] = "YES" };
        // 沒有資料庫
        Assert.Equal("Shiyin has no meetings yet.", ToolText(Call(on)));
        // 有資料庫：拾音開著（另一條連線寫入中）也讀得到
        using (var db = new AppDatabase(paths.Database)) { Seed(db); Assert.StartsWith("5 meetings (newest first).", ToolText(Call(on))); }
        SqliteCleanup();
        // 拾音沒開、旁邊沒有 -wal、-shm：改用一般連線，資料不變
        foreach (var suffix in new[] { "-wal", "-shm" }) File.Delete(paths.Database + suffix);
        Assert.False(File.Exists(paths.Database + "-wal"));
        Assert.StartsWith("5 meetings (newest first).", ToolText(Call(on)));
        SqliteCleanup();
        // 公司關掉
        var company = new Dictionary<string, string>(on)
        {
            [EnterpriseServer.AccountKey] = "{\"email\":\"t@example.com\",\"name\":\"T\",\"serverName\":\"Test\",\"engine\":\"breeze\",\"llm\":false,\"mcp\":false}",
        };
        Assert.Equal("Your company's Shiyin server has turned off reading meetings from AI tools.", ToolText(Call(company)));
        company[EnterpriseServer.AccountKey] = "{\"email\":\"t@example.com\",\"name\":\"T\",\"serverName\":\"Test\",\"engine\":\"breeze\",\"llm\":false}";
        Assert.StartsWith("5 meetings", ToolText(Call(company)));
        // 少一個 migration（更新後還沒開過拾音）、多一個（舊的程式）
        Exec(paths.Database, "DELETE FROM grdb_migrations WHERE identifier = 'v10'");
        Assert.StartsWith("Shiyin was updated and its meeting database needs a one-time upgrade.", ToolText(Call(on)));
        Exec(paths.Database, "INSERT INTO grdb_migrations VALUES ('v10'), ('v999')");
        Assert.StartsWith("This copy of Shiyin is older than the meeting database.", ToolText(Call(on)));
    }

    // MARK: 加到 AI 工具（M§44.3、design-windows §11.12）

    [Fact]
    public void AddToClaudeDesktopKeepsOtherSettings()
    {
        var config = Path.Combine(dir, "Claude", "claude_desktop_config.json");
        // 沒有檔案：建一份
        Assert.True(McpSetup.AddToClaudeDesktop(config, @"C:\ShiyinApp\current\shiyin-mcp.exe", null).Ok);
        Assert.Equal(@"C:\ShiyinApp\current\shiyin-mcp.exe", JsonNode.Parse(File.ReadAllText(config))!["mcpServers"]!["shiyin"]!["command"]!.GetValue<string>());
        // 原本有別的設定：保留，留 .bak；按兩次只有一個 shiyin；帶 SHIYIN_HOME
        File.WriteAllText(config, "{\"globalShortcut\": \"Alt+Space\", \"mcpServers\": {\"other\": {\"command\": \"x.exe\"}}}");
        Assert.True(McpSetup.AddToClaudeDesktop(config, "s.exe", @"C:\Demo").Ok);
        Assert.True(McpSetup.AddToClaudeDesktop(config, "s.exe", @"C:\Demo").Ok);
        var root = JsonNode.Parse(File.ReadAllText(config))!;
        Assert.Equal("Alt+Space", root["globalShortcut"]!.GetValue<string>());
        Assert.Equal("x.exe", root["mcpServers"]!["other"]!["command"]!.GetValue<string>());
        Assert.Equal(2, root["mcpServers"]!.AsObject().Count);
        Assert.Equal(@"C:\Demo", root["mcpServers"]!["shiyin"]!["env"]!["SHIYIN_HOME"]!.GetValue<string>());
        Assert.True(File.Exists(config + ".bak"));
        // 不是 JSON 物件：不動
        File.WriteAllText(config, "[1, 2]");
        Assert.False(McpSetup.AddToClaudeDesktop(config, "s.exe", null).Ok);
        Assert.Equal("[1, 2]", File.ReadAllText(config));
        File.WriteAllText(config, "{\"mcpServers\": 3}");
        Assert.False(McpSetup.AddToClaudeDesktop(config, "s.exe", null).Ok);
    }

    [Fact]
    public async Task AddToClaudeCodeAndCodexArguments()
    {
        if (OperatingSystem.IsWindows()) return;   // 假的 CLI 是 sh 腳本
        var log = Path.Combine(dir, "calls.txt");
        var script = Path.Combine(dir, "fake-cli.sh");
        // 每次呼叫記一行參數（用 | 分隔）；remove 失敗（還沒加過）也沒關係
        File.WriteAllText(script, $"#!/bin/sh\nIFS='|'; echo \"$*\" >> '{log}'\ncase \"$2\" in remove) echo 'not found' >&2; exit 1;; esac\nexit 0\n");
        var tool = new Shiyin.LLM.ToolCommand("/bin/sh", [script]);
        var runner = new Shiyin.LLM.ProcessRunner();
        var r = await McpSetup.AddToClaudeCode(tool, runner, "/x/shiyin-mcp.exe", "/home/demo");
        Assert.True(r.Ok, r.Message);
        r = await McpSetup.AddToCodex(tool, runner, "/x/shiyin-mcp.exe", null);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(
        [
            "mcp|remove|--scope|user|shiyin",
            "mcp|add|--scope|user|shiyin|-e|SHIYIN_HOME=/home/demo|--|/x/shiyin-mcp.exe",
            "mcp|remove|shiyin",
            "mcp|add|shiyin|--|/x/shiyin-mcp.exe",
        ], File.ReadAllLines(log));
        // 失敗時寫 CLI 的錯誤訊息第一行
        File.WriteAllText(script, "#!/bin/sh\necho 'error: bad config' >&2\necho 'second line' >&2\nexit 2\n");
        r = await McpSetup.AddToCodex(tool, runner, "/x/shiyin-mcp.exe", null);
        Assert.False(r.Ok);
        Assert.EndsWith("error: bad config", r.Message);
        Assert.False((await McpSetup.AddToClaudeCode(null, runner, "s", null)).Ok);
    }

    [Fact]
    public void CopyTexts()
    {
        Assert.Equal("claude mcp add --scope user shiyin -- \"C:\\A B\\shiyin-mcp.exe\"", McpSetup.ClaudeCodeCommand(@"C:\A B\shiyin-mcp.exe", null));
        Assert.Equal("claude mcp add --scope user shiyin -e \"SHIYIN_HOME=C:\\Demo\" -- \"s.exe\"", McpSetup.ClaudeCodeCommand("s.exe", @"C:\Demo"));
        var json = JsonNode.Parse(McpSetup.ConfigJson("s.exe", null))!;
        Assert.Equal("s.exe", json["mcpServers"]!["shiyin"]!["command"]!.GetValue<string>());
        Assert.Null(json["mcpServers"]!["shiyin"]!["env"]);
    }

    static void Exec(string path, string sql)
    {
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        SqliteCleanup();
    }
}
