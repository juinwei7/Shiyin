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

    static string MeetingNotesSchema => Model.MeetingNotes.JsonSchema;

    sealed class FixedTool(string path) : IToolLocator
    {
        public ToolCommand? Find(string name) => new(path, []);
    }
}
