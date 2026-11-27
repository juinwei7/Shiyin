using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shiyin.LLM;
using Shiyin.Notes;
using Shiyin.Text;
using Xunit;

namespace Shiyin.Tests;

/// <summary>整理工具（design-windows §10、R17）：小工具跟 Mac 相同；請求的格式、重試、取消用假伺服器測</summary>
public sealed class LLMTests : IDisposable
{
    static IEnumerable<JsonElement> Items(string path) => Golden.ExpectedJson(path).EnumerateArray();

    [Fact]
    public void HelpersMatchMac()
    {
        foreach (var item in Items("util/openai-url.json"))
        {
            var text = item.Str("text")!;
            var normalized = OpenAICompatibleApi.NormalizedText(text);
            Assert.Equal(item.Str("normalized"), normalized);
            if (normalized is not null) Assert.Equal(item.Bool("isLocal"), OpenAICompatibleApi.IsLocal(OpenAICompatibleApi.Normalized(text)!));
        }
        foreach (var item in Items("util/extract-json.json"))
            Assert.Equal(item.Str("json"), OpenAICompatibleApi.ExtractJson(item.Str("text")!));
        foreach (var item in Items("util/rejects-schema.json"))
            Assert.Equal(item.Bool("rejects"), OpenAICompatibleApi.RejectsSchema((int)item.Long("status")!, item.Str("message")!));
        foreach (var item in Items("util/login-problem.json"))
            Assert.Equal(item.Bool("isLogin"), LLMException.IsLoginProblem(item.Str("text")!));
    }

    // MARK: 假伺服器

    sealed record Captured(string Method, string Path, Dictionary<string, string> Headers, JsonNode? Body);

    sealed class FakeServer : IDisposable
    {
        readonly HttpListener listener = new();
        public string Url { get; }
        public ConcurrentQueue<Captured> Requests { get; } = new();
        public Func<Captured, (int Status, string Body, Dictionary<string, string>? Headers)> Respond { get; set; } = _ => (200, "{}", null);

        public FakeServer()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            Url = $"http://127.0.0.1:{port}";
            listener.Prefixes.Add(Url + "/");
            listener.Start();
            _ = Loop();
        }

        async Task Loop()
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); } catch { return; }
                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                var text = await reader.ReadToEndAsync();
                var headers = context.Request.Headers.AllKeys.Where(k => k is not null).ToDictionary(k => k!.ToLowerInvariant(), k => context.Request.Headers[k]!);
                var captured = new Captured(context.Request.HttpMethod, context.Request.Url!.PathAndQuery, headers, text.Length > 0 ? JsonNode.Parse(text) : null);
                Requests.Enqueue(captured);
                var (status, body, extra) = Respond(captured);
                context.Response.StatusCode = status;
                if (extra is not null) foreach (var (k, v) in extra) context.Response.Headers[k] = v;
                var bytes = Encoding.UTF8.GetBytes(body);
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
        }

        public void Dispose() => listener.Stop();
    }

    sealed class MemoryKeys(Dictionary<string, string> keys) : IKeyStore
    {
        public string? Read(string account) => keys.GetValueOrDefault(account);
        public bool Exists(string account) => keys.ContainsKey(account);
    }

    sealed class NoTools : IToolLocator
    {
        public ToolCommand? Find(string name) => null;
    }

    readonly FakeServer server = new();
    readonly HttpClient http = new();

    public void Dispose()
    {
        server.Dispose();
        http.Dispose();
    }

    LLMClient Client(Dictionary<string, string>? keys = null)
    {
        var client = new LLMClient(new MemoryKeys(keys ?? new() { ["anthropic"] = "sk-ant-test", ["openai"] = "sk-test", ["gemini"] = "g-test", ["openaiCompatible"] = "c-test" }),
            new NoTools(), new ProcessRunner(), http);
        client.BaseUrlOverrides[LLMProvider.Anthropic] = server.Url;
        client.BaseUrlOverrides[LLMProvider.OpenAI] = server.Url;
        client.BaseUrlOverrides[LLMProvider.Gemini] = server.Url;
        client.CompatibleBaseUrl = () => server.Url + "/v1/";
        return client;
    }

    static readonly string NotesJson = """{"title":"t","summary":[{"text":"s","time":"00:05"}],"decisions":[],"actionItems":[],"openQuestions":[],"topics":[],"custom":[],"reviewItems":[]}""";

    static string Reply(Captured c) => c.Path switch
    {
        "/v1/messages" => new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_use", ["input"] = JsonNode.Parse(NotesJson) }) }.ToJsonString(),
        "/v1/responses" => new JsonObject { ["output"] = new JsonArray(new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = NotesJson }) }) }.ToJsonString(),
        "/v1beta/interactions" => new JsonObject { ["status"] = "completed", ["steps"] = new JsonArray(
            new JsonObject { ["type"] = "thought", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "thinking" }) },
            new JsonObject { ["type"] = "model_output", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = NotesJson }) }) }.ToJsonString(),
        _ => new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["finish_reason"] = "stop", ["message"] = new JsonObject { ["content"] = "```json\n" + NotesJson + "\n```" } }) }.ToJsonString(),
    };

    /// <summary>四種 API 送出的請求：路徑、header、body 的結構跟 Mac 的程式一樣（M§19.5）</summary>
    [Fact]
    public async Task RequestsHaveTheSameShapeAsMac()
    {
        server.Respond = c => (200, Reply(c), null);
        var client = Client();
        var format = OutputFormat.MeetingNotes;
        foreach (var provider in new[] { LLMProvider.Anthropic, LLMProvider.OpenAI, LLMProvider.Gemini, LLMProvider.OpenAICompatible })
        {
            var model = provider == LLMProvider.OpenAICompatible ? "llama" : "";
            var notes = await client.Generate(provider, model, "SYSTEM", "PROMPT", CancellationToken.None);
            Assert.Equal("t", notes.Title);
        }
        var requests = server.Requests.ToList();
        Assert.Equal(4, requests.Count);
        var schema = JsonNode.Parse(format.Schema);

        var anthropic = requests[0];
        Assert.Equal(("POST", "/v1/messages"), (anthropic.Method, anthropic.Path));
        Assert.Equal("sk-ant-test", anthropic.Headers["x-api-key"]);
        Assert.Equal("2023-06-01", anthropic.Headers["anthropic-version"]);
        Assert.StartsWith("application/json", anthropic.Headers["content-type"]);
        Same(new JsonObject
        {
            ["model"] = "claude-sonnet-5-5", ["max_tokens"] = 16000, ["system"] = "SYSTEM",
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "PROMPT" }),
            ["tools"] = new JsonArray(new JsonObject { ["name"] = "save_meeting_notes", ["description"] = "儲存整理好的會議紀錄", ["input_schema"] = schema!.DeepClone() }),
            ["tool_choice"] = new JsonObject { ["type"] = "tool", ["name"] = "save_meeting_notes" },
        }, anthropic.Body);

        var openai = requests[1];
        Assert.Equal(("POST", "/v1/responses"), (openai.Method, openai.Path));
        Assert.Equal("Bearer sk-test", openai.Headers["authorization"]);
        Same(new JsonObject
        {
            ["model"] = "gpt-5", ["instructions"] = "SYSTEM", ["input"] = "PROMPT",
            ["text"] = new JsonObject { ["format"] = new JsonObject { ["type"] = "json_schema", ["name"] = "save_meeting_notes", ["schema"] = schema.DeepClone(), ["strict"] = true } },
        }, openai.Body);

        var gemini = requests[2];
        Assert.Equal(("POST", "/v1beta/interactions"), (gemini.Method, gemini.Path));
        Assert.Equal("g-test", gemini.Headers["x-goog-api-key"]);
        Same(new JsonObject
        {
            ["model"] = "gemini-3.8-flash", ["system_instruction"] = "SYSTEM", ["input"] = "PROMPT",
            ["response_format"] = new JsonObject { ["type"] = "text", ["mime_type"] = "application/json", ["schema"] = schema.DeepClone() },
            ["store"] = false,
        }, gemini.Body);

        var compatible = requests[3];
        Assert.Equal(("POST", "/v1/chat/completions"), (compatible.Method, compatible.Path));
        Assert.Equal("Bearer c-test", compatible.Headers["authorization"]);
        Same(new JsonObject
        {
            ["model"] = "llama",
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = "SYSTEM" }, new JsonObject { ["role"] = "user", ["content"] = "PROMPT" }),
            ["response_format"] = new JsonObject { ["type"] = "json_schema", ["json_schema"] = new JsonObject { ["name"] = "save_meeting_notes", ["schema"] = schema.DeepClone(), ["strict"] = true } },
        }, compatible.Body);
    }

    /// <summary>Mac 實際送出的請求（Mac 的 DEBUG 版接假伺服器錄下來的，shared/golden/expected/llm/mac-requests.jsonl）：
    /// Windows 用同樣的提示詞、模型送出，body 解析後要完全相同（W1.7）</summary>
    [Fact]
    public async Task SameBodiesAsMacRequests()
    {
        server.Respond = c => (200, Reply(c), null);
        var client = Client();
        var lines = File.ReadAllLines(Path.Combine(Golden.Root, "expected", "llm", "mac-requests.jsonl")).Where(l => l.Length > 0).ToList();
        Assert.Equal(4, lines.Count);
        foreach (var line in lines)
        {
            var mac = JsonNode.Parse(line)!;
            var body = mac["body"]!;
            var path = mac["path"]!.GetValue<string>();
            var before = server.Requests.Count;
            switch (path)
            {
                case "/v1/messages":
                    await client.Generate(LLMProvider.Anthropic, body["model"]!.GetValue<string>(), body["system"]!.GetValue<string>(),
                        body["messages"]![0]!["content"]!.GetValue<string>(), CancellationToken.None);
                    break;
                case "/v1/responses":
                    await client.Generate(LLMProvider.OpenAI, body["model"]!.GetValue<string>(), body["instructions"]!.GetValue<string>(),
                        body["input"]!.GetValue<string>(), CancellationToken.None);
                    break;
                case "/v1beta/interactions":
                    await client.Generate(LLMProvider.Gemini, body["model"]!.GetValue<string>(), body["system_instruction"]!.GetValue<string>(),
                        body["input"]!.GetValue<string>(), CancellationToken.None);
                    break;
                default:
                    await client.Generate(LLMProvider.OpenAICompatible, body["model"]!.GetValue<string>(), body["messages"]![0]!["content"]!.GetValue<string>(),
                        body["messages"]![1]!["content"]!.GetValue<string>(), CancellationToken.None);
                    break;
            }
            var sent = server.Requests.ToList()[before];
            Assert.Equal(mac["method"]!.GetValue<string>(), sent.Method);
            Assert.Equal(path, sent.Path);
            Same(body, sent.Body);
            foreach (var header in new[] { "x-api-key", "authorization", "anthropic-version", "x-goog-api-key" })
                Assert.Equal(mac["headers"]?[header] is not null, sent.Headers.ContainsKey(header));
        }
    }

    static void Same(JsonNode expected, JsonNode? actual) =>
        Golden.SameJson(JsonDocument.Parse(expected.ToJsonString()).RootElement, JsonDocument.Parse(actual!.ToJsonString()).RootElement, "body");

    /// <summary>json_schema 被拒絕：退回 JSON 模式，schema 放進系統提示詞（D24）</summary>
    [Fact]
    public async Task CompatibleFallsBackToJsonMode()
    {
        server.Respond = c => (c.Body?["response_format"]?["type"]?.GetValue<string>() == "json_schema"
            ? (400, """{"error":{"message":"response_format json_schema is not supported"}}""", null)
            : (200, Reply(c), null));
        var json = await Client().GenerateJson(LLMProvider.OpenAICompatible, "m", "SYS", "P", SpeakerNaming.Format, CancellationToken.None);
        Assert.Equal(NotesJson, json);
        var second = server.Requests.Last();
        Assert.Equal("json_object", second.Body!["response_format"]!["type"]!.GetValue<string>());
        Assert.Equal("SYS\n\nRespond with only a JSON object that matches this JSON schema, with no other text:\n" + SpeakerNaming.JsonSchema,
            second.Body["messages"]![0]!["content"]!.GetValue<string>());
    }

    /// <summary>503 重試一次後成功；401、沒有額度的 429 不重試（R20.5）</summary>
    [Fact]
    public async Task RetriesOnceForTemporaryErrors()
    {
        ApiRequest.MaxRetryAfterSeconds = 0.05;
        try
        {
            var calls = 0;
            server.Respond = c => Interlocked.Increment(ref calls) == 1 ? (503, "{}", new() { ["retry-after"] = "1" }) : (200, Reply(c), null);
            await Client().Generate(LLMProvider.Anthropic, "", "S", "P", CancellationToken.None);
            Assert.Equal(2, calls);

            calls = 0;
            server.Respond = _ => { Interlocked.Increment(ref calls); return (401, """{"error":{"message":"invalid x-api-key"}}""", null); };
            var e = await Assert.ThrowsAsync<LLMException>(() => Client().Generate(LLMProvider.Anthropic, "", "S", "P", CancellationToken.None));
            Assert.Equal(1, calls);
            Assert.Contains("HTTP 401", e.Message);
            Assert.Contains("（API key 錯誤或已失效）", e.Message);
            Assert.Equal("LLMError.rejected", e.LogSummary);   // 設定問題（M§34.4）

            calls = 0;
            server.Respond = _ => { Interlocked.Increment(ref calls); return (429, """{"error":{"message":"Your credit balance is too low"}}""", null); };
            e = await Assert.ThrowsAsync<LLMException>(() => Client().Generate(LLMProvider.Anthropic, "", "S", "P", CancellationToken.None));
            Assert.Equal(1, calls);
            Assert.Contains("（帳戶沒有額度，要到服務的網站加值）", e.Message);
            Assert.Equal("LLMError.limited", e.LogSummary);   // 用量上限不是設定問題（M§34.4）
        }
        finally { ApiRequest.MaxRetryAfterSeconds = 30; }
    }

    /// <summary>連不到主機：重試一次後報錯</summary>
    [Fact]
    public async Task UnreachableHostRetriesOnce()
    {
        ApiRequest.NetworkRetryDelay = TimeSpan.FromMilliseconds(10);
        try
        {
            var client = Client();
            client.BaseUrlOverrides[LLMProvider.OpenAI] = "http://127.0.0.1:1";
            var e = await Assert.ThrowsAsync<LLMException>(() => client.Generate(LLMProvider.OpenAI, "", "S", "P", CancellationToken.None));
            Assert.StartsWith("連不到 OpenAI：", e.Message);
        }
        finally { ApiRequest.NetworkRetryDelay = TimeSpan.FromSeconds(5); }
    }

    /// <summary>取消在 1 秒內停下（R20.5）</summary>
    [Fact]
    public async Task CancelStopsQuickly()
    {
        server.Respond = c => { Thread.Sleep(5000); return (200, Reply(c), null); };
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client().Generate(LLMProvider.Gemini, "", "S", "P", cancel.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1.3), $"取消花了 {watch.Elapsed}");
    }

    [Fact]
    public async Task MissingKeyAndBadUrl()
    {
        var e = await Assert.ThrowsAsync<LLMException>(() => Client([]).Generate(LLMProvider.Anthropic, "", "S", "P", CancellationToken.None));
        Assert.Equal(LLMErrorKind.MissingKey, e.Kind);
        var client = Client();
        client.CompatibleBaseUrl = () => "ftp://x";
        e = await Assert.ThrowsAsync<LLMException>(() => client.Generate(LLMProvider.OpenAICompatible, "m", "S", "P", CancellationToken.None));
        Assert.Contains("http://", e.Message);
        e = await Assert.ThrowsAsync<LLMException>(() => Client().Generate(LLMProvider.Claude, "", "S", "P", CancellationToken.None));
        Assert.Equal(LLMErrorKind.NotInstalled, e.Kind);
        e = await Assert.ThrowsAsync<LLMException>(() => Client().Generate(LLMProvider.None, "", "S", "P", CancellationToken.None));
        Assert.Contains("不整理", e.Message);
    }

    /// <summary>Gemini 的錯誤包在陣列裡、狀態不是 completed</summary>
    [Fact]
    public async Task GeminiErrorsAndStatus()
    {
        server.Respond = _ => (400, """[{"error":{"code":400,"message":"API key not valid. Please pass a valid API key."}}]""", null);
        var e = await Assert.ThrowsAsync<LLMException>(() => Client().Generate(LLMProvider.Gemini, "", "S", "P", CancellationToken.None));
        Assert.Contains("（API key 錯誤或已失效）", e.Message);
        Assert.Contains("API key not valid", e.Message);
        server.Respond = _ => (200, """{"status":"incomplete"}""", null);
        e = await Assert.ThrowsAsync<LLMException>(() => Client().Generate(LLMProvider.Gemini, "", "S", "P", CancellationToken.None));
        Assert.Contains("回應不完整", e.Message);
    }

    /// <summary>「其他」的種類（R86、M§61.1）：API 照 HTTP 狀態碼、回應不完整；不看錯誤的文字</summary>
    [Fact]
    public async Task FailureCausesFromApi()
    {
        foreach (var (status, key) in new[] { (404, "http404"), (400, "http400"), (413, "http413"), (418, "httpOther") })
        {
            server.Respond = _ => (status, """{"error":{"message":"model not found"}}""", null);
            var e = await Assert.ThrowsAsync<LLMException>(() => Client().Generate(LLMProvider.Anthropic, "", "S", "P", CancellationToken.None));
            Assert.Equal($"LLMError.failed.{key}", e.LogSummary);
        }
        server.Respond = _ => (200, """{"status":"incomplete"}""", null);
        var incomplete = await Assert.ThrowsAsync<LLMException>(() => Client().Generate(LLMProvider.Gemini, "", "S", "P", CancellationToken.None));
        Assert.Equal("LLMError.failed.incomplete", incomplete.LogSummary);
    }

    /// <summary>CLI 的種類（R86）：結束代碼、沒有輸出、is_error、被系統結束</summary>
    [Fact]
    public async Task FailureCausesFromCli()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = TempDirectory.Create();
        try
        {
            var cases = new (LLMProvider Provider, string Body, string Key)[]
            {
                (LLMProvider.Claude, "echo 'boom' >&2\nexit 1", "cliExit1"),
                (LLMProvider.Claude, "exit 0", "cliNoOutput"),
                (LLMProvider.Claude, "echo '{\"is_error\":true,\"result\":\"API Error: 500\"}'", "cliError"),
                (LLMProvider.Claude, "exit 127", "cliExit127"),
                (LLMProvider.Claude, "kill -9 $$", "cliSignal"),
                (LLMProvider.Codex, "echo 'usage' >&2\nexit 2", "cliExit2"),
                (LLMProvider.Codex, "exit 0", "cliNoOutput"),
                (LLMProvider.Codex, "exit 3", "cliExitOther"),
            };
            foreach (var (provider, body, key) in cases)
            {
                var script = Path.Combine(dir, $"tool-{key}-{provider}");
                await File.WriteAllTextAsync(script, "#!/bin/sh\ncat > /dev/null\n" + body + "\n");
                File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                var e = await Assert.ThrowsAsync<LLMException>(() =>
                    new LLMClient(new MemoryKeys([]), new FixedTool(script), new ProcessRunner(), http).Generate(provider, "", "S", "P", CancellationToken.None));
                Assert.Equal($"LLMError.failed.{key}", e.LogSummary);
            }
        }
        finally { TempDirectory.Delete(dir); }
    }

    /// <summary>CLI：用參數陣列、提示詞從 stdin 送（用一個假的 claude 腳本測，只在有 /bin/sh 的機器跑）</summary>
    [Fact]
    public async Task ClaudeCliArgumentsAndStdin()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = TempDirectory.Create();
        try
        {
            var script = Path.Combine(dir, "claude");
            var log = Path.Combine(dir, "args.txt");
            await File.WriteAllTextAsync(script, $"#!/bin/sh\nfor a in \"$@\"; do printf '%s\\n' \"$a\" >> '{log}'; done\ncat > '{log}.stdin'\n"
                + "echo '{\"is_error\":false,\"structured_output\":" + NotesJson.Replace("'", "") + "}'\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var client = new LLMClient(new MemoryKeys([]), new FixedTool(script), new ProcessRunner(), http);
            var system = "系統 \"提示\" $HOME `x` & | ;" + new string('長', 12000);
            var notes = await client.Generate(LLMProvider.Claude, "opus", system, "使用者提示", CancellationToken.None);
            Assert.Equal("t", notes.Title);
            var args = (await File.ReadAllTextAsync(log)).Split('\n');
            Assert.Equal(["-p", "--output-format", "json", "--tools", "", "--no-session-persistence", "--strict-mcp-config", "--system-prompt"], args[..8]);
            Assert.Equal(system, args[8]);
            Assert.Equal("--json-schema", args[9]);
            Assert.Equal(MeetingNotesSchema, args[10]);
            Assert.Equal(["--model", "opus"], args[11..13]);
            Assert.Equal("使用者提示", await File.ReadAllTextAsync(log + ".stdin"));
        }
        finally { TempDirectory.Delete(dir); }
    }

    /// <summary>額度用完（R62、M§40）：claude 的每週額度、codex 的 usage limit 拋 Limited，訊息兩行（怎麼辦＋CLI 的原文）</summary>
    [Fact]
    public async Task CliLimitsSayWhatToDo()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = TempDirectory.Create();
        try
        {
            var claude = Path.Combine(dir, "claude");
            await File.WriteAllTextAsync(claude, "#!/bin/sh\ncat > /dev/null\n"
                + "echo '{\"type\":\"result\",\"is_error\":true,\"result\":\"You\\u0027ve hit your weekly limit · resets Nov 2 at 9pm (Asia/Taipei)\"}'\nexit 1\n");
            var codex = Path.Combine(dir, "codex");
            // codex 的 stderr 前面有別的 log（含提示詞裡剛好有的「rate limit」），錯誤在最後一行
            await File.WriteAllTextAsync(codex, "#!/bin/sh\ncat > /dev/null\n"
                + "echo 'OpenAI Codex v0.98.0' >&2\necho 'user: we discussed the rate limit of the old API' >&2\n"
                + "echo \"ERROR: You've hit your usage limit. Upgrade to Pro (https://chatgpt.com/explore/pro) or try again in 4 days 2 hours.\" >&2\nexit 1\n");
            foreach (var script in new[] { claude, codex })
                File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var e = await Assert.ThrowsAsync<LLMException>(() =>
                new LLMClient(new MemoryKeys([]), new FixedTool(claude), new ProcessRunner(), http).Generate(LLMProvider.Claude, "", "S", "P", CancellationToken.None));
            Assert.Equal(LLMErrorKind.Limited, e.Kind);
            Assert.Equal("LLMError.limited", e.LogSummary);
            Assert.Equal("Claude Code 的用量額度用完了，等恢復後再試，或到設定 → 整理工具先換成 Codex 或 API。\n"
                + "Claude Code 的訊息：You've hit your weekly limit · resets Nov 2 at 9pm (Asia/Taipei)", e.Message);
            Assert.False(LLMException.IsLoginMessage(e.Message));

            e = await Assert.ThrowsAsync<LLMException>(() =>
                new LLMClient(new MemoryKeys([]), new FixedTool(codex), new ProcessRunner(), http).Generate(LLMProvider.Codex, "", "S", "P", CancellationToken.None));
            Assert.Equal(LLMErrorKind.Limited, e.Kind);
            Assert.Equal("Codex 的用量額度用完了，等恢復後再試，或到設定 → 整理工具先換成 Claude Code 或 API。\n"
                + "Codex 的訊息：ERROR: You've hit your usage limit. Upgrade to Pro (https://chatgpt.com/explore/pro) or try again in 4 days 2 hours.", e.Message);
            Assert.False(LLMException.IsLoginMessage(e.Message));
        }
        finally { TempDirectory.Delete(dir); }
    }

    /// <summary>agy 這次的設定（M§95.2、D204）跟 Mac 的 AntigravityCLI.sessionSettings 一字不差；參數、stdin 的那一行（M§95.1）</summary>
    [Fact]
    public void AgySettingsArgumentsAndMessage()
    {
        var swift = File.ReadAllText(Path.Combine(Golden.Root, "..", "..", "Sources", "Shiyin", "Notes", "Backends", "CLIBackends.swift")).Replace("\r\n", "\n");
        var mac = System.Text.RegularExpressions.Regex.Match(swift, "static let sessionSettings = #\"(.*?)\"#").Groups[1].Value;
        Assert.NotEmpty(mac);
        Assert.Equal(mac, AgyCli.SessionSettings);
        var settings = JsonNode.Parse(AgyCli.SessionSettings)!;
        Assert.Equal("strict", (string)settings["toolPermission"]!);
        Assert.Equal(8, settings["permissions"]!["deny"]!.AsArray().Count);

        Assert.Equal(["--input-format", "stream-json", "--output-format", "stream-json", "--json-schema", @"C:\t\schema.json",
            "--settings", @"C:\t\settings.json", "--disable-slash-commands"], AgyCli.Arguments(@"C:\t\schema.json", @"C:\t\settings.json", ""));
        Assert.Equal(["--model", "gemini-3.8-flash-high"], AgyCli.Arguments("a", "b", "gemini-3.8-flash-high")[^2..]);

        // 一行 JSON（結尾換行）；內容是系統提示詞、提示詞、schema 說明，中間空一行；只有 ASCII（中文寫成 \uXXXX）
        var line = AgyCli.Message("系統 \"提示\"\n第二行", "逐字稿", """{"type":"object"}""");
        Assert.EndsWith("\n", line);
        Assert.DoesNotContain("\n", line[..^1]);
        Assert.True(line.All(c => c < 128));
        var obj = JsonNode.Parse(line)!.AsObject();
        Assert.Equal(["event", "message"], obj.Select(p => p.Key));
        Assert.Equal("user", (string)obj["event"]!);
        Assert.Equal("系統 \"提示\"\n第二行\n\n逐字稿\n\nRespond with only a JSON object that matches this JSON schema, with no other text:\n{\"type\":\"object\"}",
            (string)obj["message"]!["content"]!);
    }

    /// <summary>agy 的結果（M§95.1）：最後一行 result；SUCCESS 取 structured_output，沒有就從 response 取 JSON；其他狀態取 error</summary>
    [Fact]
    public void AgyParsesTheResultLine()
    {
        const string id = "0b5c6c9e-6f7e-4a59-9d2a-3f4f1f0c2b11";
        var success = AgyCli.Parse("{\"event\":\"start\"}\r\nnot json\n"
            + "{\"event\":\"result\",\"result\":{\"status\":\"ERROR\",\"error\":\"old\"}}\n"
            + "{\"event\":\"result\",\"result\":{\"status\":\"SUCCESS\",\"conversation_id\":\"" + id + "\",\"structured_output\":{\"title\":\"週會\"},\"response\":\"x\"}}\r\n");
        Assert.Equal(new AgyCli.Parsed("{\"title\":\"\\u9031\\u6703\"}", null, id), success);
        Assert.Equal("週會", (string)JsonNode.Parse(success.Output!)!["title"]!);
        // structured_output 沒有：從 response 取第一個 { 到最後一個 }
        var fromResponse = AgyCli.Parse("""{"event":"result","result":{"status":"SUCCESS","conversation_id":"","response":"好的：\n```json\n{\"title\":\"t\"}\n```"}}""");
        Assert.Equal(new AgyCli.Parsed("{\"title\":\"t\"}", null, null), fromResponse);
        // 不是 JSON 物件（字串、null）都不算
        Assert.Equal(new AgyCli.Parsed(null, "agy 沒有回傳 JSON", null),
            AgyCli.Parse("""{"event":"result","result":{"status":"SUCCESS","structured_output":"{}","response":"沒有 JSON"}}"""));
        Assert.Equal(new AgyCli.Parsed(null, "agy 沒有回傳 JSON", null),
            AgyCli.Parse("""{"event":"result","result":{"status":"SUCCESS","structured_output":null}}"""));
        // 失敗：error，空的時候是 status
        Assert.Equal(new AgyCli.Parsed(null, "authentication failed or timed out", id),
            AgyCli.Parse("{\"event\":\"result\",\"result\":{\"status\":\"ERROR\",\"error\":\"authentication failed or timed out\",\"conversation_id\":\"" + id + "\"}}"));
        Assert.Equal(new AgyCli.Parsed(null, "CANCELLED", null), AgyCli.Parse("""{"event":"result","result":{"status":"CANCELLED","error":""}}"""));
        Assert.Equal(new AgyCli.Parsed(), AgyCli.Parse("""{"event":"result","result":{"error":7}}"""));
        // 沒有 result 那一行、event 不是字串、result 不是物件
        Assert.Equal(new AgyCli.Parsed(), AgyCli.Parse(""));
        Assert.Equal(new AgyCli.Parsed(), AgyCli.Parse("Error: authentication required. Run 'agy' to log in, then retry.\n"));
        Assert.Equal(new AgyCli.Parsed(), AgyCli.Parse("""{"event":1,"result":{"status":"SUCCESS"}}""" + "\n" + """{"event":"result","result":"x"}"""));
    }

    /// <summary>agy models（M§95.3）：一行「代號&lt;Tab&gt;名稱」，列代號；其他的行不算</summary>
    [Fact]
    public void AgyModelsAreTheSlugs()
    {
        Assert.Equal(["gemini-4-hydrogen-high", "claude-sonnet-4-6", "gpt-oss-120b-medium"],
            AgyCli.Models("gemini-4-hydrogen-high\tGemini 4 Hydrogen High\r\nclaude-sonnet-4-6\tClaude Sonnet 4.6\n\n"
                + "Available models:\n\tno slug\nslug-only\t\n gpt-oss-120b-medium \tGPT-OSS 120B (Medium)\n"));
        Assert.Empty(AgyCli.Models("Please sign in to view available models\n"));
        Assert.Empty(AgyCli.Models(""));
    }

    /// <summary>跑完刪掉這次的對話（M§95.4、D204）：只刪 conversations/&lt;id&gt;.db 和 brain/&lt;id&gt;/；id 不是 UUID 就不刪</summary>
    [Fact]
    public void AgyRemovesOnlyThisConversation()
    {
        var root = TempDirectory.Create();
        try
        {
            string Conversation(string id)
            {
                Directory.CreateDirectory(Path.Combine(root, "conversations"));
                Directory.CreateDirectory(Path.Combine(root, "brain", id));
                File.WriteAllText(Path.Combine(root, "brain", id, "note.md"), "x");
                var db = Path.Combine(root, "conversations", id + ".db");
                File.WriteAllText(db, "x");
                return db;
            }
            const string mine = "0b5c6c9e-6f7e-4a59-9d2a-3f4f1f0c2b11", other = "7d1f0a52-3c1b-4f0e-8a77-0c3d2e1b4a90";
            var mineDb = Conversation(mine);
            var otherDb = Conversation(other);
            foreach (var bad in new[] { "..", "../x", "conversations", "{" + other + "}", other.Replace("-", ""), "" })
                AgyCli.RemoveConversation(root, bad);
            Assert.True(File.Exists(otherDb) && Directory.Exists(Path.Combine(root, "brain", other)) && Directory.Exists(Path.Combine(root, "conversations")));
            AgyCli.RemoveConversation(root, mine);
            Assert.False(File.Exists(mineDb));
            Assert.False(Directory.Exists(Path.Combine(root, "brain", mine)));
            Assert.True(File.Exists(otherDb) && Directory.Exists(Path.Combine(root, "brain", other)));
            AgyCli.RemoveConversation(root, mine);   // 已經沒有了：不會丟例外
            AgyCli.RemoveConversation(Path.Combine(root, "沒有這個資料夾"), mine);
        }
        finally { TempDirectory.Delete(root); }
    }

    /// <summary>
    /// 假的 agy（只在有 /bin/sh 的機器跑）：參數陣列、stdin 一行、工作目錄是空的 workspace、schema 和設定檔在它外面、
    /// 跑完刪掉暫存資料夾和這次的對話；沒登入、額度用完、模型不對、沒有輸出各是哪一種錯誤
    /// </summary>
    [Fact]
    public async Task AgyCliRunsLikeTheMac()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = TempDirectory.Create();
        try
        {
            const string id = "0b5c6c9e-6f7e-4a59-9d2a-3f4f1f0c2b11";
            var data = Path.Combine(dir, "antigravity-cli");
            Directory.CreateDirectory(Path.Combine(data, "conversations"));
            Directory.CreateDirectory(Path.Combine(data, "brain", id));
            await File.WriteAllTextAsync(Path.Combine(data, "conversations", id + ".db"), "x");
            var log = Path.Combine(dir, "log");
            async Task<LLMClient> Agy(string body)
            {
                var script = Path.Combine(dir, $"agy-{Guid.NewGuid():N}");
                await File.WriteAllTextAsync(script, "#!/bin/sh\nprev=''\nfor a in \"$@\"; do printf '%s\\n' \"$a\" >> '" + log + "'\n"
                    + "  [ \"$prev\" = --json-schema ] && cp \"$a\" '" + log + ".schema'\n"
                    + "  [ \"$prev\" = --settings ] && cp \"$a\" '" + log + ".settings'\n  prev=\"$a\"\ndone\n"
                    + "pwd > '" + log + ".cwd'\nls -A > '" + log + ".ls'\n[ \"$1\" = models ] || cat > '" + log + ".stdin'\n" + body + "\n");
                File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                return new LLMClient(new MemoryKeys([]), new FixedTool(script), new ProcessRunner(), http) { AgyDataDirectory = data };
            }

            var ok = await Agy("echo '{\"event\":\"assistant\",\"text\":\"…\"}'\n"
                + "echo '{\"event\":\"result\",\"result\":{\"status\":\"SUCCESS\",\"conversation_id\":\"" + id + "\",\"structured_output\":" + NotesJson + "}}'");
            var notes = await ok.Generate(LLMProvider.Antigravity, "gemini-3.8-flash-high", "系統", "逐字稿", CancellationToken.None);
            Assert.Equal("t", notes.Title);
            var args = (await File.ReadAllTextAsync(log)).TrimEnd('\n').Split('\n');
            Assert.Equal(["--input-format", "stream-json", "--output-format", "stream-json", "--json-schema"], args[..5]);
            Assert.Equal(["--settings"], args[6..7]);
            Assert.Equal(["--disable-slash-commands", "--model", "gemini-3.8-flash-high"], args[8..]);
            var temp = Path.GetDirectoryName(args[5])!;
            Assert.Equal((Path.Combine(temp, "schema.json"), Path.Combine(temp, "settings.json")), (args[5], args[7]));
            Assert.Equal(MeetingNotesSchema, await File.ReadAllTextAsync(log + ".schema"));
            Assert.Equal(AgyCli.SessionSettings, await File.ReadAllTextAsync(log + ".settings"));
            // 工作目錄是暫存資料夾裡空的 workspace（設定、schema 不在裡面）；用完整個刪掉
            Assert.EndsWith(Path.Combine(Path.GetFileName(temp), "workspace"), (await File.ReadAllTextAsync(log + ".cwd")).Trim());
            Assert.Equal("", await File.ReadAllTextAsync(log + ".ls"));
            Assert.False(Directory.Exists(temp));
            Assert.Equal(AgyCli.Message("系統", "逐字稿", MeetingNotesSchema), await File.ReadAllTextAsync(log + ".stdin"));
            // 這次的對話刪掉了
            Assert.False(File.Exists(Path.Combine(data, "conversations", id + ".db")));
            Assert.False(Directory.Exists(Path.Combine(data, "brain", id)));

            var cases = new (string Body, LLMErrorKind Kind, string Summary, string Message)[]
            {
                ("echo \"Error: authentication required. Run 'agy' to log in, then retry.\" >&2\n"
                    + "echo '{\"event\":\"result\",\"result\":{\"status\":\"ERROR\",\"error\":\"authentication failed or timed out\"}}'\nexit 1",
                    LLMErrorKind.NotLoggedIn, "LLMError.notLoggedIn(agy)", "Antigravity 還沒登入或登入已過期，請重新登入後再試一次（終端機指令：agy）。"),
                ("echo '{\"event\":\"result\",\"result\":{\"status\":\"ERROR\",\"error\":\"RESOURCE_EXHAUSTED: 429 Too Many Requests\"}}'\nexit 1",
                    LLMErrorKind.Limited, "LLMError.limited",
                    "Antigravity 的用量額度用完了，等恢復後再試，或到設定 → 整理工具先換成 Gemini API 或其他整理工具。\nAntigravity 的訊息：RESOURCE_EXHAUSTED: 429 Too Many Requests"),
                ("echo '{\"event\":\"result\",\"result\":{\"status\":\"ERROR\",\"error\":\"invalid model selection: x is not recognized\"}}'\nexit 1",
                    LLMErrorKind.Failed, "LLMError.failed.cliError", "invalid model selection: x is not recognized"),
                ("echo '{\"event\":\"result\",\"result\":{\"status\":\"SUCCESS\",\"response\":\"沒有 JSON\"}}'",
                    LLMErrorKind.Failed, "LLMError.failed.cliError", "agy 沒有回傳 JSON"),
                ("echo 'panic: boom' >&2\nexit 3", LLMErrorKind.Failed, "LLMError.failed.cliExitOther", "panic: boom"),
                ("exit 0", LLMErrorKind.Failed, "LLMError.failed.cliNoOutput", "agy 沒有回傳結果（結束代碼 0）"),
            };
            foreach (var (body, kind, summary, message) in cases)
            {
                var client = await Agy(body);
                var e = await Assert.ThrowsAsync<LLMException>(() => client.Generate(LLMProvider.Antigravity, "", "S", "P", CancellationToken.None));
                Assert.Equal((kind, summary, message), (e.Kind, e.LogSummary, e.Message));
            }

            // 取消（逾時也一樣）：沒有 result 那一行，靠第一行的 conversation_id 照樣刪掉這次的對話
            await File.WriteAllTextAsync(Path.Combine(data, "conversations", id + ".db"), "x");
            Directory.CreateDirectory(Path.Combine(data, "brain", id));
            var hanging = await Agy("echo '{\"event\":\"step_update\",\"step_update\":{\"conversation_id\":\"" + id + "\",\"state\":\"ACTIVE\"}}'\nsleep 30");
            using (var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hanging.Generate(LLMProvider.Antigravity, "", "S", "P", cancel.Token));
            Assert.False(File.Exists(Path.Combine(data, "conversations", id + ".db")));
            Assert.False(Directory.Exists(Path.Combine(data, "brain", id)));

            // 模型清單：agy models；沒登入（結束代碼 1）是空的
            var models = await Agy("printf 'gemini-4-hydrogen-high\\tGemini 4 Hydrogen High\\nclaude-sonnet-4-6\\tClaude Sonnet 4.6\\n'");
            Assert.Equal(["gemini-4-hydrogen-high", "claude-sonnet-4-6"], await models.ListModels(LLMProvider.Antigravity, CancellationToken.None));
            Assert.Equal("models", (await File.ReadAllTextAsync(log)).TrimEnd('\n').Split('\n')[^1]);
            var signedOut = await Agy("echo 'Please sign in to view available models' >&2\nexit 1");
            Assert.Empty(await signedOut.ListModels(LLMProvider.Antigravity, CancellationToken.None));
        }
        finally { TempDirectory.Delete(dir); }
    }

    /// <summary>沒有 agy：NotInstalled("agy")（R35.4），訊息說怎麼辦</summary>
    [Fact]
    public async Task AgyNotInstalled()
    {
        var e = await Assert.ThrowsAsync<LLMException>(() => Client().Generate(LLMProvider.Antigravity, "", "S", "P", CancellationToken.None));
        Assert.Equal(("LLMError.notInstalled(agy)", "這台電腦找不到 agy。沒有在用 Antigravity 的話，請到設定 → 整理工具改用 API（填 API key）或選「不整理」"),
            (e.LogSummary, e.Message));
        Assert.Empty(await Client().ListModels(LLMProvider.Antigravity, CancellationToken.None));
    }

    static string MeetingNotesSchema => Model.MeetingNotes.JsonSchema;

    sealed class FixedTool(string path) : IToolLocator
    {
        public ToolCommand? Find(string name) => new(path, []);
    }
}
