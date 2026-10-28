using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Shiyin.Data;
using Shiyin.LLM;
using Shiyin.Model;
using Shiyin.Services;
using Shiyin.Notes;
using Xunit;

namespace Shiyin.Tests;

/// <summary>
/// 企業伺服器（R49～R51、M§36、design-windows §12.11）：用本機的假伺服器（照 M§36.2 的 API）測登入、換 token、
/// 上傳續傳、等伺服器、改在這台電腦轉錄、整理
/// </summary>
public sealed class EnterpriseTests : IDisposable
{
    /// <summary>照 M§36.2 的假伺服器：一個工作、一個帳號，可以設定失敗</summary>
    sealed class FakeEnterprise : IDisposable
    {
        public readonly TestServer Server = new();
        public readonly List<string> Requests = [];
        public string? Code;
        public string? Challenge;
        public string Refresh = "refresh-1";
        public string Access = "access-1";
        public int AccessExpiresIn = 3600;
        public bool RejectRefresh;
        public bool Disabled;
        public bool Down;
        public bool Llm = true;
        /// <summary>公司詞彙表（R53）；null 是舊的伺服器（404）</summary>
        public List<string>? GlossaryTerms = [];
        // 工作
        public byte[] Received = [];
        public long Expected;
        public string? Sha;
        public string JobStatus = "none";
        public int StatusPolls;
        public bool Diarize;
        public string? LastSettings;
        public int CreateCount;
        /// <summary>收到第一塊之後伺服器忙（代理、伺服器出狀況）：之後的請求都回 503，直到測試清掉 Busy</summary>
        public bool DropAfterFirstChunk;
        public bool Busy;
        public string? LastLlmAuth;
        public int LlmRejectsLeft;
        /// <summary>整理要回的錯誤（R51.6）：伺服器自己的（有 kind）或轉送的模型的</summary>
        public (int Status, JsonObject Body)? LlmReply;
        public string Name = "測試公司";
        /// <summary>info、換 token 回這段原文（型別不對的回應）</summary>
        public string? InfoBody;
        public string? TokenBody;
        /// <summary>這個路徑回 307 轉到 RedirectTo（同一個路徑）</summary>
        public string? RedirectPath;
        public string? RedirectTo;
        /// <summary>HEAD、PATCH 回這個 Upload-Offset（PATCH 不收）：不合理的位置</summary>
        public string? HeadOffset;
        public string? PatchOffset;
        public string? LastLogoutAuth;

        public FakeEnterprise() => Server.Handle = Handle;

        async Task Handle(HttpListenerContext c)
        {
            var path = c.Request.Url!.AbsolutePath;
            var method = c.Request.HttpMethod;
            lock (Requests) Requests.Add($"{method} {path}");
            if (Down) { c.Response.Abort(); return; }
            using var reader = new MemoryStream();
            await c.Request.InputStream.CopyToAsync(reader);
            var body = reader.ToArray();
            var auth = c.Request.Headers["Authorization"];
            bool Authorized() => auth == "Bearer " + Access && !Disabled;
            if (path == RedirectPath)
            {
                c.Response.StatusCode = 307;
                c.Response.RedirectLocation = RedirectTo + path;
                c.Response.Close();
                return;
            }
            switch (method, path)
            {
                case ("GET", "/api/v1/info") when InfoBody is not null:
                    await Text(c, 200, InfoBody);
                    return;
                case ("GET", "/api/v1/info"):
                    await Json(c, 200, new JsonObject
                    {
                        ["server"] = "shiyin", ["apiVersion"] = 1, ["minAppApi"] = 1, ["name"] = Name, ["version"] = "1.0.0",
                        ["maxAudioSeconds"] = 28800, ["transcription"] = new JsonObject { ["engine"] = "breeze" }, ["llm"] = Llm,
                    });
                    return;
                case ("GET", "/login"):
                    Challenge = c.Request.QueryString["code_challenge"];
                    await Text(c, 200, "<form>dev</form>");
                    return;
                case ("POST", "/login/dev"):
                {
                    var form = Encoding.UTF8.GetString(body).Split('&').Select(p => p.Split('=')).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
                    Code = "code-" + Guid.NewGuid().ToString("N");
                    var url = $"shiyin://auth?code={Code}&state={Uri.EscapeDataString(form["state"])}";
                    await Text(c, 200, $"<script>window.location.replace(\"{url}\");</script>");
                    return;
                }
                case ("POST", "/api/v1/auth/token"):
                {
                    var o = JsonNode.Parse(body)!.AsObject();
                    if (o["grant_type"]!.GetValue<string>() == "authorization_code")
                    {
                        var verifier = o["code_verifier"]!.GetValue<string>();
                        var computed = EnterpriseServer.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
                        if (o["code"]!.GetValue<string>() != Code || computed != Challenge || o["platform"]!.GetValue<string>() != "windows")
                        { await Error(c, 400, "bad_request"); return; }
                    }
                    else if (RejectRefresh || o["refresh_token"]!.GetValue<string>() != Refresh) { await Error(c, 401, "unauthorized"); return; }
                    else if (Disabled) { await Error(c, 403, "forbidden", "這個帳號已經被管理者停用"); return; }
                    if (TokenBody is not null) { await Text(c, 200, TokenBody); return; }
                    Refresh = "refresh-" + Guid.NewGuid().ToString("N");
                    Access = "access-" + Guid.NewGuid().ToString("N");
                    await Json(c, 200, new JsonObject
                    {
                        ["access_token"] = Access, ["expires_in"] = AccessExpiresIn, ["refresh_token"] = Refresh,
                        ["user"] = new JsonObject { ["email"] = "amy@example.com", ["name"] = "Amy" },
                    });
                    return;
                }
                case ("POST", "/api/v1/auth/logout"):
                    LastLogoutAuth = auth;
                    c.Response.StatusCode = 204;
                    c.Response.Close();
                    return;
            }
            if (!Authorized()) { await Error(c, Disabled ? 403 : 401, Disabled ? "forbidden" : "unauthorized"); return; }
            if (Busy) { await Error(c, 503, "busy", "伺服器忙"); return; }
            if (method == "GET" && path == "/api/v1/glossary")
            {
                if (GlossaryTerms is null) { await Error(c, 404, "not_found"); return; }
                await Json(c, 200, new JsonObject { ["terms"] = new JsonArray([.. GlossaryTerms.Select(t => (JsonNode)t)]), ["updatedAt"] = "2026-10-27T12:00:00Z" });
                return;
            }
            if (method == "GET" && path == "/api/v1/llm/models")
            {
                await Json(c, 200, JsonNode.Parse("""{"object":"list","data":[{"id":"gemma-3-27b","name":"機房的 Gemma 3","default":true},{"id":"claude-sonnet-5-5","name":"Claude"}]}""")!.AsObject());
                return;
            }
            if (method == "POST" && path == "/api/v1/llm/chat/completions")
            {
                LastLlmAuth = auth;
                if (LlmRejectsLeft-- > 0) { await Error(c, 401, "unauthorized"); return; }
                if (LlmReply is { } reply) { await Json(c, reply.Status, reply.Body); return; }
                await Json(c, 200, JsonNode.Parse("""{"choices":[{"message":{"content":"{\"answer\":\"好\"}"},"finish_reason":"stop"}]}""")!.AsObject());
                return;
            }
            if (method == "POST" && path == "/api/v1/transcriptions")
            {
                var o = JsonNode.Parse(body)!.AsObject();
                LastSettings = o["settings"]!.ToJsonString();
                Diarize = o["settings"]!["diarize"]!.GetValue<bool>();
                Expected = o["audio"]!["bytes"]!.GetValue<long>();
                Sha = o["audio"]!["sha256"]!.GetValue<string>();
                Received = [];
                JobStatus = "uploading";
                CreateCount++;
                await Json(c, 201, new JsonObject { ["id"] = "job1", ["status"] = "uploading" });
                return;
            }
            if (!path.StartsWith("/api/v1/transcriptions/job1", StringComparison.Ordinal) || JobStatus == "none") { await Error(c, 404, "not_found"); return; }
            switch (method, path)
            {
                case ("HEAD", "/api/v1/transcriptions/job1/audio"):
                    c.Response.Headers["Upload-Offset"] = HeadOffset ?? Received.Length.ToString();
                    c.Response.StatusCode = 200;
                    c.Response.Close();
                    return;
                case ("PATCH", "/api/v1/transcriptions/job1/audio"):
                {
                    var offset = long.Parse(c.Request.Headers["Upload-Offset"]!);
                    if (PatchOffset is not null)
                    {
                        c.Response.Headers["Upload-Offset"] = PatchOffset;
                        c.Response.StatusCode = 204;
                        c.Response.Close();
                        return;
                    }
                    if (offset != Received.Length)
                    {
                        c.Response.Headers["Upload-Offset"] = Received.Length.ToString();
                        await Error(c, 409, "conflict");
                        return;
                    }
                    Received = [.. Received, .. body];
                    if (DropAfterFirstChunk) { DropAfterFirstChunk = false; Busy = true; await Error(c, 503, "busy", "伺服器忙"); return; }
                    if (Received.Length == Expected)
                    {
                        if (Convert.ToHexStringLower(SHA256.HashData(Received)) != Sha) { Received = []; await Error(c, 400, "bad_request"); return; }
                        JobStatus = "queued";
                    }
                    c.Response.Headers["Upload-Offset"] = Received.Length.ToString();
                    c.Response.StatusCode = 204;
                    c.Response.Close();
                    return;
                }
                case ("GET", "/api/v1/transcriptions/job1"):
                    StatusPolls++;
                    if (JobStatus == "queued" && StatusPolls > 2) JobStatus = "running";
                    else if (JobStatus == "running") JobStatus = "done";
                    await Json(c, 200, new JsonObject { ["id"] = "job1", ["status"] = JobStatus, ["queueAhead"] = JobStatus == "queued" ? 2 : null, ["progress"] = 0.5 });
                    return;
                case ("GET", "/api/v1/transcriptions/job1/result"):
                    await Json(c, 200, JsonNode.Parse("""
                        {"engine":"breeze","transcribedWith":"breeze","duration":4.0,"seams":[],
                         "segments":[{"start":0,"end":2,"text":"大家好","lowConfidence":false,"speaker":1,"channel":null},
                                     {"start":2,"end":4,"text":"開始開會","lowConfidence":true,"speaker":2,"channel":null}],
                         "voices":{"1":{"embedding":[0.1,0.2],"seconds":2,"source":"mix"},"2":{"embedding":[0.3,0.4],"seconds":2,"source":"mix"}},
                         "diarizationQuality":{"collisionRate":0.1,"multiSpeakerChunks":1,"cohesion":0.8,"clusters":2,"chunks":40,"mergeRisk":0.2}}
                        """)!.AsObject());
                    return;
                case ("DELETE", "/api/v1/transcriptions/job1"):
                    JobStatus = "none";
                    c.Response.StatusCode = 204;
                    c.Response.Close();
                    return;
            }
            await Error(c, 404, "not_found");
        }

        static async Task Json(HttpListenerContext c, int status, JsonObject body)
        {
            var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
            c.Response.StatusCode = status;
            c.Response.ContentType = "application/json";
            await c.Response.OutputStream.WriteAsync(bytes);
            c.Response.Close();
        }

        static async Task Text(HttpListenerContext c, int status, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            c.Response.StatusCode = status;
            c.Response.ContentType = "text/html; charset=utf-8";
            await c.Response.OutputStream.WriteAsync(bytes);
            c.Response.Close();
        }

        static Task Error(HttpListenerContext c, int status, string kind, string message = "錯誤") =>
            Json(c, status, new JsonObject { ["error"] = new JsonObject { ["kind"] = kind, ["message"] = message } });

        public void Dispose() => Server.Dispose();
    }

    sealed class MemorySecrets : ISecretStore
    {
        public readonly Dictionary<string, string> Values = [];
        public string? Read(string account) => Values.GetValueOrDefault(account);
        public void Save(string account, string value) => Values[account] = value;
        public void Delete(string account) => Values.Remove(account);
    }

    sealed class MemoryKeys : IKeyStore
    {
        public string? Read(string account) => null;
        public bool Exists(string account) => false;
    }

    sealed class NoTools : IToolLocator
    {
        public ToolCommand? Find(string name) => null;
    }

    sealed class NoEngines : ISpeechEngines
    {
        public List<Transcribe.TranscribedSegment> Qwen(float[] samples, IReadOnlyList<(int Start, int End)> chunks, MeetingLanguage language,
            IReadOnlyList<string> glossary, bool retry, bool guard, Whisper.WhisperControl control) => throw new NotSupportedException();
        public (List<Transcribe.TranscribedSegment> Segments, List<int> Seams, int SampleCount) WhisperFile(Whisper.IAudioStream audio, double chunkSeconds,
            string model, string prompt, string language, Whisper.WhisperControl control) => throw new NotSupportedException();
        public List<Transcribe.TranscribedSegment> WhisperSamples(float[] samples, IReadOnlyList<(int Start, int End)> chunks, string model, string prompt,
            string language, Whisper.WhisperControl control) => throw new NotSupportedException();
        public List<Transcribe.TranscribedSegment> Fast(Whisper.IAudioStream audio, MeetingLanguage language, Action<double> progress, CancellationToken cancel) =>
            throw new NotSupportedException();
        public Speakers.Diarizer.Output Diarize(float[] samples, int? minSpeakers, Action<double> progress, CancellationToken cancel) =>
            throw new NotSupportedException();
        public List<float[]?> Embed(float[] samples, IReadOnlyList<(double Start, double End)> spans, CancellationToken cancel) =>
            throw new NotSupportedException();
    }

    sealed class NoCompressor : IAudioCompressor
    {
        public Task Compress(string wavPath, string m4aPath, CancellationToken cancel) => Task.CompletedTask;
    }

    readonly FakeEnterprise fake = new();
    // 跟 app 一樣用不跟著轉址的 HttpClient（M§36.2、D126）
    readonly HttpClient http = EnterpriseServer.NoRedirectClient();
    readonly MemorySecrets secrets = new();
    readonly AppSettings settings = AppSettings.InMemory();
    readonly TempHome home = new();

    public void Dispose()
    {
        fake.Dispose();
        http.Dispose();
        home.Dispose();
    }

    EnterpriseServer Server() => new(settings, http, secrets, "4.2.0", "測試的筆電") { AllowLocalHttp = true };

    async Task<EnterpriseServer> SignedIn()
    {
        var server = Server();
        await server.DebugDevSignIn(fake.Server.Url, "amy@example.com");
        return server;
    }

    [Fact]
    public void UrlRules()
    {
        Assert.Equal("https://shiyin.example.com/", EnterpriseServer.Normalized("shiyin.example.com")?.ToString());
        Assert.Equal("https://shiyin.example.com/base/", EnterpriseServer.Normalized("https://shiyin.example.com/base/api/v1/info")?.ToString());
        Assert.Null(EnterpriseServer.Normalized("http://shiyin.example.com"));
        Assert.Null(EnterpriseServer.Normalized("http://127.0.0.1:8787"));
        Assert.NotNull(EnterpriseServer.Normalized("http://127.0.0.1:8787", allowLocalHttp: true));
        Assert.Null(EnterpriseServer.Normalized("http://10.0.0.5:8787", allowLocalHttp: true));
        Assert.Null(EnterpriseServer.Normalized(""));
        // 有帳號密碼的不接受（加入連結用 good.com@evil.com 假裝成別的網站，M§36.2）
        Assert.Null(EnterpriseServer.Normalized("https://user:pass@shiyin.example.com"));
        Assert.Null(EnterpriseServer.Normalized("https://shiyin.example.com@evil.example.com"));
        // 國際化網域給人看的是 punycode：長得像別的網站的字元騙不了人
        Assert.Equal("xn--pple-43d.com", EnterpriseServer.DisplayHost(EnterpriseServer.Normalized("https://\u0430pple.com")!));
        Assert.True(EnterpriseServer.Normalized("https://%D0%B0pple.com") is not { } encoded || EnterpriseServer.DisplayHost(encoded).All(char.IsAscii));
        Assert.True(EnterpriseServer.SameServer(new Uri("https://Shiyin.example.com/"), new Uri("https://shiyin.example.com")));
        Assert.False(EnterpriseServer.SameServer(new Uri("https://a.example.com/"), new Uri("https://b.example.com/")));
    }

    /// <summary>加入連結可以指到任何網站：info、換 token 的型別不對都是「不是拾音的企業伺服器」，不能丟出別的例外讓 app 當掉</summary>
    [Fact]
    public async Task MalformedServerRepliesAreNotShiyin()
    {
        var server = Server();
        foreach (var body in new[]
        {
            """{"server":"shiyin","apiVersion":"1","minAppApi":1}""",
            """{"server":"shiyin","apiVersion":1e10,"minAppApi":1}""",
            """{"server":"shiyin","apiVersion":1,"minAppApi":1,"name":5}""",
            """{"server":"shiyin","apiVersion":1,"minAppApi":1,"transcription":"breeze"}""",
            """{"server":"shiyin","apiVersion":1,"minAppApi":1,"llm":"yes"}""",
            """{"server":"shiyin","server":"shiyin","apiVersion":1,"minAppApi":1}""",
            """{"server":["shiyin"]}""",
            """["shiyin"]""",
            "not json",
        })
        {
            fake.InfoBody = body;
            var failure = await Assert.ThrowsAsync<EnterpriseException>(() => server.FetchInfo(new Uri(fake.Server.Url + "/")));
            Assert.Equal(EnterpriseFailure.NotShiyin, failure.Kind);
        }
        fake.InfoBody = null;
        // 換 token 的回應型別不對、少了 token：不登入
        foreach (var body in new[] { """{"access_token":1,"refresh_token":"r"}""", """{"access_token":"a"}""", """{"access_token":"a","refresh_token":"r","user":"amy"}""" })
        {
            fake.TokenBody = body;
            var failure = await Assert.ThrowsAsync<EnterpriseException>(() => server.DebugDevSignIn(fake.Server.Url, "amy@example.com"));
            Assert.Equal(EnterpriseFailure.NotShiyin, failure.Kind);
            Assert.False(server.HasAccount);
        }
        // 錯誤的 kind、message 不是字串：當成沒有
        var odd = EnterpriseServer.Failure(500, """{"error":{"kind":5,"message":["x"]}}""");
        Assert.Equal(("server_error", EnterpriseFailure.Server), (odd.ServerKind, odd.Kind));
    }

    /// <summary>不跟著轉址（M§36.2、D126）：307 轉到別的主機時，body（refresh token、錄音、逐字稿）不能送過去</summary>
    [Fact]
    public async Task RedirectsAreNotFollowed()
    {
        using var other = new TestServer();
        var elsewhere = new List<string>();
        other.Handle = c => { lock (elsewhere) elsewhere.Add($"{c.Request.HttpMethod} {c.Request.Url!.AbsolutePath}"); c.Response.StatusCode = 404; c.Response.Close(); return Task.CompletedTask; };
        var server = await SignedIn();
        fake.RedirectTo = other.Url;
        fake.RedirectPath = "/api/v1/transcriptions";
        var content = new StringContent("""{"settings":{},"audio":{}}""", Encoding.UTF8, "application/json");
        var redirected = await Assert.ThrowsAsync<EnterpriseException>(() => server.Call(HttpMethod.Post, "api/v1/transcriptions", content));
        Assert.Equal("redirect", redirected.ServerKind);
        Assert.Contains("307", redirected.Message);
        Assert.False(redirected.IsTemporary || redirected.IsSetupProblem);   // 轉錄的工作算失敗，不會一直等
        // 換 token（body 是 refresh token）
        fake.RedirectPath = "/api/v1/auth/token";
        server.InvalidateAccess();
        Assert.Equal("redirect", (await Assert.ThrowsAsync<EnterpriseException>(() => server.AccessToken())).ServerKind);
        // 整理（body 是逐字稿）：LLMClient 共用的 HttpClient 會跟著轉址，企業伺服器用自己的
        fake.RedirectPath = null;
        using var shared = new HttpClient();
        var llm = new LLMClient(new MemoryKeys(), new NoTools(), new ProcessRunner(), shared) { Enterprise = await SignedIn() };
        var format = new OutputFormat("""{"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"]}""", "answer", "answer");
        fake.RedirectPath = "/api/v1/llm/chat/completions";
        var failed = await Assert.ThrowsAsync<LLMException>(() => llm.GenerateJson(LLMProvider.Enterprise, "", "s", "逐字稿", format, CancellationToken.None));
        Assert.Contains("307", failed.Message);
        // 加入連結、登入：info 轉到別的地方也不跟
        fake.RedirectPath = "/api/v1/info";
        Assert.Equal("redirect", (await Assert.ThrowsAsync<EnterpriseException>(() => Server().BeginSignIn(fake.Server.Url))).ServerKind);
        Assert.Empty(elsewhere);
        // 給錯 HttpClient（會跟著轉址）時也當成錯誤（body 已經送出去了，所以 app 一定要用 NoRedirectClient）
        fake.RedirectPath = "/api/v1/glossary";
        using var following = new HttpClient();
        var careless = new EnterpriseServer(settings, following, secrets, "4.2.0", "測試的筆電") { AllowLocalHttp = true };
        Assert.Equal("redirect", (await Assert.ThrowsAsync<EnterpriseException>(() => careless.Call(HttpMethod.Get, "api/v1/glossary"))).ServerKind);
        fake.RedirectPath = null;
    }

    /// <summary>
    /// 加入連結換到另一台（R49.7、M§36.2）：新的登入完成之前，舊的帳號、refresh token 都還在；
    /// 完成之後才換掉、撤銷舊伺服器上的登入（用舊伺服器自己的 token）、換掉公司詞彙表
    /// </summary>
    [Fact]
    public async Task JoiningAnotherServerKeepsTheOldLoginUntilTheNewOneCompletes()
    {
        fake.GlossaryTerms = ["舊公司的詞"];
        var server = await SignedIn();
        settings.Set(AppSettings.TranscriptionEngineKey, "server");
        settings.Set(AppSettings.ProviderKey, "enterprise");
        var oldRefresh = fake.Refresh;
        var oldAccess = await server.AccessToken();
        using var other = new FakeEnterprise { Name = "新公司", Llm = false, GlossaryTerms = ["新公司的詞"] };
        // 按了「加入」、瀏覽器還在登入：什麼都沒換
        var attempt = await server.BeginSignIn(other.Server.Url);
        Assert.Equal("新公司", attempt.Info.Name);
        Assert.True(server.IsSignedIn);
        Assert.Equal("測試公司", server.Account!.ServerName);
        Assert.Equal(oldRefresh, secrets.Values[EnterpriseServer.CredentialAccount]);
        // 新的伺服器拒絕：維持原本的登入
        await Assert.ThrowsAsync<EnterpriseException>(() => server.CompleteSignIn(
            new Uri($"shiyin://auth?error=access_denied&error_description=no&state={attempt.State}")));
        Assert.True(server.IsSignedIn);
        Assert.True(EnterpriseServer.SameServer(new Uri(fake.Server.Url), server.SavedUrl!));
        Assert.DoesNotContain("POST /api/v1/auth/logout", fake.Requests);
        Assert.Equal(["舊公司的詞"], server.Glossary.Terms);
        // 登入完成：換成新的，舊的伺服器用自己的 token 撤銷
        await server.DebugDevSignIn(other.Server.Url, "amy@example.com");
        Assert.Equal("新公司", server.Account!.ServerName);
        Assert.True(EnterpriseServer.SameServer(new Uri(other.Server.Url), server.SavedUrl!));
        Assert.Equal(other.Refresh, secrets.Values[EnterpriseServer.CredentialAccount]);
        Assert.Equal("Bearer " + oldAccess, fake.LastLogoutAuth);
        Assert.DoesNotContain("POST /api/v1/auth/logout", other.Requests);
        Assert.Equal(["新公司的詞"], server.Glossary.Terms);
        Assert.Equal(other.Access, await server.AccessToken());
        // 新的伺服器沒有提供整理：「公司」改成還沒設定；轉錄照舊用企業伺服器
        Assert.Null(settings.ChosenProvider);
        Assert.Equal("server", settings.TranscriptionEngine);
    }

    /// <summary>伺服器的結果（M§36.2）：時間、說話者、聲紋、長度不合理的不收（說話者的數字太大會讓資料庫建一大堆說話者）</summary>
    [Fact]
    public void ServerResultsAreChecked()
    {
        var r = ServerTranscriber.Parse(JsonNode.Parse("""
            {"segments":[{"start":0,"end":1,"text":"太大","speaker":2147483647},{"start":1,"end":2,"text":"0","speaker":0},
                         {"start":2,"end":3,"text":"64","speaker":64},{"start":3,"end":4,"text":"65","speaker":65},
                         {"start":4,"end":5,"text":"負的","speaker":-1},{"start":5,"end":6,"text":"小數","speaker":1.5},
                         {"start":-1,"end":2,"text":"開始是負的","speaker":1},{"start":3,"end":2,"text":"倒過來","speaker":1},
                         {"start":0,"end":1e300,"text":"太長","speaker":1}],
             "voices":{"0":{"embedding":[0.1],"seconds":1,"source":"mix"},"64":{"embedding":[0.5],"seconds":1,"source":"mix"},
                       "65":{"embedding":[0.6],"seconds":1,"source":"mix"},"99999999999":{"embedding":[0.7],"seconds":1,"source":"mix"}},
             "seams":[-1,600,1e300,"x"],"duration":1e300}
            """)!.AsObject()).Transcription;
        Assert.Equal(["太大", "0", "64", "65", "負的", "小數"], r.Segments.Select(s => s.Text));
        Assert.Equal([null, null, 64, null, null, null], r.Speakers);
        Assert.Equal([64], r.Voices.Keys);
        Assert.Equal([600.0], r.Seams);
        Assert.Equal(6, r.Duration);
        Assert.Equal(91.5, ServerTranscriber.Parse(JsonNode.Parse("""{"segments":[{"start":0,"end":1,"text":"好"}],"duration":91.5}""")!.AsObject()).Transcription.Duration);
    }

    /// <summary>伺服器給的 Upload-Offset 不合理：負的、比檔案大就從頭傳；一直不往前就算失敗，不會一直重送（M§36.2）</summary>
    [Fact]
    public async Task UploadOffsetsFromTheServerAreChecked()
    {
        var server = await SignedIn();
        var (wav, bytes, sha) = Wav(5);
        // HEAD 說收到 -5、比檔案還多：從頭傳，照樣傳完
        fake.HeadOffset = "-5";
        Assert.NotNull(await Transcriber(server, [], []).Transcribe(wav, bytes, sha, new TranscriptionOptions(), null, CancellationToken.None));
        Assert.Equal(File.ReadAllBytes(wav), fake.Received);
        fake.HeadOffset = (bytes + 100).ToString();
        Assert.NotNull(await Transcriber(server, [], []).Transcribe(wav, bytes, sha, new TranscriptionOptions(), null, CancellationToken.None));
        fake.HeadOffset = null;
        // PATCH 之後位置一直不往前（負的、比檔案大、0）：失敗，不會一直送
        foreach (var offset in new[] { "-1", (bytes * 10).ToString(), "0" })
        {
            fake.PatchOffset = offset;
            lock (fake.Requests) fake.Requests.Clear();
            var failure = await Assert.ThrowsAsync<ServerTranscriber.FailedException>(() =>
                Transcriber(server, [], []).Transcribe(wav, bytes, sha, new TranscriptionOptions(), null, CancellationToken.None));
            Assert.Equal("錄音一直沒辦法完整傳到企業伺服器", failure.Message);
            Assert.Equal(3, fake.Requests.Count(r => r.StartsWith("PATCH", StringComparison.Ordinal)));
        }
        fake.PatchOffset = null;
    }

    [Fact]
    public async Task SignInStoresAccountAndRefreshToken()
    {
        var server = await SignedIn();
        Assert.True(server.IsSignedIn);
        Assert.Equal(new EnterpriseAccount("amy@example.com", "Amy", "測試公司", "breeze", true, "1.0.0"), server.Account);   // 伺服器的版本給回報問題用（R52.3）
        Assert.Equal(fake.Refresh, secrets.Values[EnterpriseServer.CredentialAccount]);
        Assert.Equal(fake.Access, await server.AccessToken());
        // 換了一個 EnterpriseServer（等於 app 重開）：帳號在設定、refresh token 在認證管理員，換新的 access token
        var reopened = Server();
        Assert.True(reopened.IsSignedIn);
        var before = fake.Access;
        var token = await reopened.AccessToken();
        Assert.NotEqual(before, token);
        Assert.Equal(fake.Access, token);
    }

    [Fact]
    public async Task WrongSiteAndStaleCallbacksAreRejected()
    {
        var server = Server();
        var notShiyin = await Assert.ThrowsAsync<EnterpriseException>(() => server.BeginSignIn(fake.Server.Url + "/nothing-here"));
        Assert.Equal(EnterpriseFailure.NotShiyin, notShiyin.Kind);
        Assert.Equal(EnterpriseFailure.InvalidUrl, (await Assert.ThrowsAsync<EnterpriseException>(() => server.BeginSignIn("http://example.com"))).Kind);
        await server.BeginSignIn(fake.Server.Url);
        // state 不是這次發的（網頁裡的連結）：忽略，不換 token
        var stale = await Assert.ThrowsAsync<EnterpriseException>(() => server.CompleteSignIn(new Uri("shiyin://auth?code=x&state=wrong")));
        Assert.Equal(EnterpriseFailure.Cancelled, stale.Kind);
        Assert.False(server.HasAccount);
        // 伺服器拒絕（不在允許的網域）
        var attempt = await server.BeginSignIn(fake.Server.Url);
        var denied = await Assert.ThrowsAsync<EnterpriseException>(() => server.CompleteSignIn(
            new Uri($"shiyin://auth?error=access_denied&error_description={Uri.EscapeDataString("只有 @example.com 的帳號可以登入")}&state={attempt.State}")));
        Assert.Equal("只有 @example.com 的帳號可以登入", denied.Message);
    }

    [Fact]
    public async Task ExpiredLoginWaitsForSignInAgain()
    {
        var server = await SignedIn();
        server.InvalidateAccess();
        fake.RejectRefresh = true;
        var changes = 0;
        server.Changed += () => changes++;
        Assert.Equal(EnterpriseFailure.LoginExpired, (await Assert.ThrowsAsync<EnterpriseException>(() => server.AccessToken())).Kind);
        Assert.False(server.IsSignedIn);
        Assert.True(server.HasAccount);
        Assert.NotNull(server.ExpiredMessage);
        Assert.False(secrets.Values.ContainsKey(EnterpriseServer.CredentialAccount));
        Assert.True(changes > 0);
        // 重新登入
        fake.RejectRefresh = false;
        await server.DebugDevSignIn(fake.Server.Url, "amy@example.com");
        Assert.True(server.IsSignedIn);
    }

    [Fact]
    public async Task DisabledAccountShowsTheServersReason()
    {
        var server = await SignedIn();
        fake.Disabled = true;
        server.InvalidateAccess();
        var failure = await Assert.ThrowsAsync<EnterpriseException>(() => server.Call(HttpMethod.Get, "api/v1/transcriptions/x"));
        Assert.Equal(EnterpriseFailure.Forbidden, failure.Kind);
        Assert.Equal("這個帳號已經被管理者停用", server.ExpiredMessage);
    }

    [Fact]
    public async Task SignOutRevertsSettings()
    {
        var server = await SignedIn();
        settings.Set(AppSettings.TranscriptionEngineKey, "server");
        settings.Set(AppSettings.ProviderKey, "enterprise");
        await server.SignOut();
        Assert.False(server.HasAccount);
        Assert.Equal("breeze", settings.TranscriptionEngine);
        Assert.Null(settings.ChosenProvider);
        Assert.False(secrets.Values.ContainsKey(EnterpriseServer.CredentialAccount));
        Assert.Contains("POST /api/v1/auth/logout", fake.Requests);
    }

    static float[] Samples(double seconds) => [.. Enumerable.Range(0, (int)(seconds * 16000)).Select(i => (float)Math.Sin(i / 20.0) * 0.3f)];

    (string Wav, long Bytes, string Sha) Wav(double seconds)
    {
        var path = Path.Combine(home.Path, "upload.wav");
        var (bytes, sha) = ServerTranscriber.WriteWav(Samples(seconds), path);
        return (path, bytes, sha);
    }

    [Fact]
    public void WavIs16BitMono16k()
    {
        var (path, bytes, sha) = Wav(1);
        var data = File.ReadAllBytes(path);
        Assert.Equal(data.Length, bytes);
        Assert.Equal(44 + 32000, data.Length);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(data)), sha);
        Assert.Equal(1, BitConverter.ToInt16(data, 22));
        Assert.Equal(16000, BitConverter.ToInt32(data, 24));
        Assert.Equal(16, BitConverter.ToInt16(data, 34));
    }

    ServerTranscriber Transcriber(EnterpriseServer server, List<string?> remoteIds, List<ServerNote?> notes, Func<bool>? local = null) =>
        new(server)
        {
            OnRemoteId = id => remoteIds.Add(id),
            OnServer = note => { lock (notes) notes.Add(note); },
            SwitchToLocal = local ?? (() => false),
            RetryDelay = _ => TimeSpan.FromMilliseconds(50),
            PollQueued = TimeSpan.FromMilliseconds(10),
            PollRunning = TimeSpan.FromMilliseconds(10),
        };

    [Fact]
    public async Task UploadsWaitsAndParsesTheResult()
    {
        var server = await SignedIn();
        var (wav, bytes, sha) = Wav(20);
        var remoteIds = new List<string?>();
        var notes = new List<ServerNote?>();
        var options = new TranscriptionOptions { Language = MeetingLanguage.Zh, Glossary = ["拾音"], Diarize = true, MinSpeakers = 2 };
        var result = await Transcriber(server, remoteIds, notes).Transcribe(wav, bytes, sha, options, null, CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal("breeze", result.TranscribedWith);
        Assert.Equal(["大家好", "開始開會"], result.Transcription.Segments.Select(s => s.Text));
        Assert.Equal([1, 2], result.Transcription.Speakers);
        Assert.Equal([0.3f, 0.4f], result.Transcription.Voices[2].Embedding);
        Assert.Equal(0.2, result.Transcription.DiarizationQuality!.MergeRisk, 3);
        // 送出的設定：說話者也在伺服器辨識、詞彙表、人數；收到的檔案跟本機的一樣
        Assert.True(fake.Diarize);
        var sent = JsonNode.Parse(fake.LastSettings!)!;
        Assert.Equal(2, sent["minSpeakers"]!.GetValue<int>());
        Assert.Equal("拾音", sent["glossary"]![0]!.GetValue<string>());
        Assert.False(sent["splitChannels"]!.GetValue<bool>());
        Assert.Equal(File.ReadAllBytes(wav), fake.Received);
        Assert.Equal(["job1", null], remoteIds);
        Assert.Contains(notes, n => n?.Text.Contains('2') == true);
        Assert.Null(notes[^1]);
        Assert.Equal("none", fake.JobStatus);   // 拿到結果後刪掉伺服器上的工作
    }

    [Fact]
    public async Task ResumesUploadAfterTheConnectionDrops()
    {
        var server = await SignedIn();
        var (wav, bytes, sha) = Wav(300);   // 9.6 MB：分兩塊
        fake.DropAfterFirstChunk = true;
        var remoteIds = new List<string?>();
        var notes = new List<ServerNote?>();
        var transcriber = Transcriber(server, remoteIds, notes);
        var run = transcriber.Transcribe(wav, bytes, sha, new TranscriptionOptions(), null, CancellationToken.None);
        // 斷線後在等伺服器；伺服器回來就從收到的地方接著傳
        for (var i = 0; i < 100 && !notes.Any(n => n?.Waiting == true); i++) await Task.Delay(20);
        Assert.Contains(notes, n => n?.Waiting == true && n.Text.Contains("伺服器忙"));
        fake.Busy = false;
        var result = await run;
        Assert.NotNull(result);
        Assert.Equal(1, fake.CreateCount);
        Assert.Equal(File.ReadAllBytes(wav), fake.Received);
    }

    [Fact]
    public async Task ContinuesAnEarlierJobAfterRestart()
    {
        var server = await SignedIn();
        var (wav, bytes, sha) = Wav(5);
        // 上次 app 結束前已經建了工作、傳了一部分
        await server.Call(HttpMethod.Post, "api/v1/transcriptions", new StringContent(new JsonObject
        {
            ["settings"] = new JsonObject { ["language"] = "zh", ["diarize"] = true },
            ["audio"] = new JsonObject { ["format"] = "wav", ["sampleRate"] = 16000, ["channels"] = 1, ["bytes"] = bytes, ["sha256"] = sha },
        }.ToJsonString(), Encoding.UTF8, "application/json"));
        fake.Received = File.ReadAllBytes(wav)[..1000];
        var remoteIds = new List<string?>();
        var result = await Transcriber(server, remoteIds, []).Transcribe(wav, bytes, sha, new TranscriptionOptions(), "job1", CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal(1, fake.CreateCount);
        Assert.Equal(File.ReadAllBytes(wav), fake.Received);
    }

    [Fact]
    public async Task UnreachableServerCanSwitchToThisComputer()
    {
        var server = await SignedIn();
        var (wav, bytes, sha) = Wav(2);
        // 伺服器關機（連線被拒）：設定裡的網址換成沒有在聽的埠，access token 還在記憶體
        _ = await server.AccessToken();
        settings.Set(EnterpriseServer.UrlKey, "http://127.0.0.1:9");
        var notes = new List<ServerNote?>();
        var local = false;
        var run = Transcriber(server, [], notes, () => local).Transcribe(wav, bytes, sha, new TranscriptionOptions(), null, CancellationToken.None);
        for (var i = 0; i < 100 && !notes.Any(n => n?.Waiting == true); i++) await Task.Delay(20);
        var waiting = notes.First(n => n?.Waiting == true)!;
        Assert.Contains("連不上", waiting.Text);
        local = true;
        await Assert.ThrowsAsync<ServerTranscriber.SwitchToLocalException>(() => run);
        Assert.Null(notes[^1]);
    }

    [Fact]
    public async Task JobRunnerTranscribesOnTheServer()
    {
        fake.GlossaryTerms = ["拾音", "自己的詞"];
        var server = await SignedIn();
        var paths = new ShiyinPaths(home.Path);
        paths.EnsureDirectories();
        var db = new AppDatabase(paths.Database, paths.Backups);
        settings.Set(AppSettings.TranscriptionEngineKey, "server");
        settings.Set(AppSettings.ProviderKey, "none");
        // 這台電腦沒有任何模型：轉錄用企業伺服器時不用等下載（R50.7）
        var models = new ModelStore(paths.Models, http, new Dictionary<ModelStore.Model, ModelStore.ModelInfo>());
        var llm = new LLMClient(new MemoryKeys(), new NoTools(), new ProcessRunner(), http);
        var runner = new JobRunner(db, settings, paths, models, llm, new WavDecoder(), new NoEngines(), new NoCompressor(), NoPower.Instance)
        {
            IdleDelay = TimeSpan.FromMilliseconds(100), Enterprise = server,
        };
        var source = Path.Combine(home.Path, "會議.wav");
        File.Copy(Wav(4).Wav, source);
        var meeting = Importer.ImportFile(source, new ImportOptions(), db, paths, settings);
        var id = meeting.Id!.Value;
        db.AddTerm("自己的詞");
        runner.Start();
        try
        {
            for (var i = 0; i < 400 && db.Segments(id).Count == 0; i++) await Task.Delay(25);
        }
        finally { runner.TerminateAll(TimeSpan.FromSeconds(5)); }
        Assert.Equal(["大家好", "開始開會"], db.Segments(id).Select(s => s.Text));
        Assert.Equal([1, 2], db.Segments(id).Select(s => s.Speaker ?? 0));
        Assert.Equal("breeze", db.Meeting(id)!.TranscribedWith);
        Assert.Equal(2, db.Speakers(id).Count);
        // 伺服器的工作 id 用完就從 payload 清掉
        Assert.DoesNotContain("remoteId", db.LastTranscribePayload(id) ?? "");
        // 自己的詞在前、公司的接在後面，重複的去掉（R53.2）
        Assert.Equal(["自己的詞", "拾音"], JsonNode.Parse(fake.LastSettings!)!["glossary"]!.AsArray().Select(t => t!.GetValue<string>()));
    }

    /// <summary>公司詞彙表（R53）：登入就拿、存在檔案（連不上時用）、建議的新詞排除、登出刪掉；舊的伺服器沒有就是空的</summary>
    [Fact]
    public async Task CompanyGlossaryIsFetchedCachedAndClearedOnSignOut()
    {
        fake.GlossaryTerms = ["拾音", "Breeze ASR", "拾音"];
        var file = Path.Combine(home.Path, "enterprise-glossary.json");
        var server = new EnterpriseServer(settings, http, secrets, "4.2.0", "測試的筆電") { AllowLocalHttp = true, Glossary = new CompanyGlossary(file) };
        var changed = 0;
        server.Glossary.Changed += () => changed++;
        await server.DebugDevSignIn(fake.Server.Url, "amy@example.com");
        Assert.Equal(["拾音", "Breeze ASR"], server.Glossary.Terms);
        Assert.Equal(1, changed);
        Assert.True(server.Glossary.Contains("breeze asr"));
        Assert.Equal(["自己的", "breeze asr", "拾音"], server.Glossary.Merged(["自己的", "breeze asr"]));
        // 沒有公司的詞（沒登入的人）：自己的詞彙表一個字都不動，跟 4.1.2 一樣（R52.5）
        Assert.Equal(["API", "api", " 前後有空白 "], CompanyGlossary.Merge(["API", "api", " 前後有空白 "], []));
        // 沒變就不通知；重開之後從檔案讀（連不上時照樣用）
        await server.RefreshGlossary();
        Assert.Equal(1, changed);
        Assert.Equal(["拾音", "Breeze ASR"], new CompanyGlossary(file).Terms);
        // 管理者改了：重新讀伺服器的資訊時一起更新
        fake.GlossaryTerms = ["星光專案"];
        await server.RefreshInfo();
        Assert.Equal(["星光專案"], server.Glossary.Terms);
        // 舊的伺服器（404）：沒有
        fake.GlossaryTerms = null;
        await server.RefreshGlossary();
        Assert.Empty(server.Glossary.Terms);
        fake.GlossaryTerms = ["拾音"];
        await server.RefreshGlossary();
        await server.SignOut();
        Assert.Empty(server.Glossary.Terms);
        Assert.False(File.Exists(file));
    }

    /// <summary>公司詞彙表的上限（M§36.2，同 Mac）：最多 500 個、每個最多 100 字，太長的略過</summary>
    [Fact]
    public async Task CompanyGlossaryIsCapped()
    {
        var many = Enumerable.Range(0, 700).Select(i => $"詞{i}").ToList();
        many.Insert(3, new string('長', 101));
        many.Insert(5, new string('剛', 100));
        var limited = CompanyGlossary.Limited(many);
        Assert.Equal(CompanyGlossary.MaxTerms, limited.Count);
        Assert.DoesNotContain(new string('長', 101), limited);
        Assert.Equal(new string('剛', 100), limited[4]);
        Assert.Equal(["詞0", "詞1", "詞2"], limited.Take(3));
        fake.GlossaryTerms = many;
        var server = await SignedIn();
        Assert.Equal(limited, server.Glossary.Terms);
    }

    [Fact]
    public void JoinLinks()
    {
        const string link = "shiyin://join?server=https%3A%2F%2Fshiyin.example.com%2Fbase";
        Assert.True(EnterpriseServer.IsJoin(link));
        Assert.False(EnterpriseServer.IsJoin("shiyin://auth?code=1"));
        Assert.False(EnterpriseServer.IsCallback(link));
        Assert.Equal("https://shiyin.example.com/base", EnterpriseServer.JoinServer(link));
        Assert.Equal("", EnterpriseServer.JoinServer("shiyin://join"));
        Assert.Null(EnterpriseServer.Normalized(EnterpriseServer.JoinServer("shiyin://join?server=http%3A%2F%2Fevil.example.com")));
    }

    [Fact]
    public async Task SummarizerUsesTheAccessTokenAndRefreshesOnce()
    {
        var server = await SignedIn();
        var llm = new LLMClient(new MemoryKeys(), new NoTools(), new ProcessRunner(), http) { Enterprise = server };
        var format = new OutputFormat("""{"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"]}""", "answer", "answer");
        var json = await llm.GenerateJson(LLMProvider.Enterprise, "", "system", "prompt", format, CancellationToken.None);
        Assert.Equal("""{"answer":"好"}""", json);
        Assert.Equal("Bearer " + fake.Access, fake.LastLlmAuth);
        // access token 被伺服器撤銷：換一次再送
        fake.LlmRejectsLeft = 1;
        Assert.Equal("""{"answer":"好"}""", await llm.GenerateJson(LLMProvider.Enterprise, "", "system", "prompt", format, CancellationToken.None));
        // 一直被拒絕：登入失效，是設定問題（暫停自動整理）
        fake.LlmRejectsLeft = 5;
        var failure = await Assert.ThrowsAsync<LLMException>(() => llm.GenerateJson(LLMProvider.Enterprise, "", "system", "prompt", format, CancellationToken.None));
        Assert.True(failure.IsSetupProblem);
        // 沒登入
        var signedOut = new LLMClient(new MemoryKeys(), new NoTools(), new ProcessRunner(), http) { Enterprise = Server() };
        await server.SignOut();
        Assert.True((await Assert.ThrowsAsync<LLMException>(() => signedOut.GenerateJson(LLMProvider.Enterprise, "", "s", "p", format, CancellationToken.None))).IsSetupProblem);
    }

    /// <summary>標準答案（M§36.7、E1）：送給伺服器的 settings、伺服器的結果換成段落跟 Mac 一樣（Mac 的說話者在本機辨識，所以比的時候 diarize 用 false）</summary>
    [Fact]
    public void ServerSettingsAndResultsMatchMac()
    {
        var settings = Golden.ExpectedJson("server/settings.json").EnumerateArray().ToList();
        Assert.Equal(3, settings.Count);
        foreach (var item in settings)
        {
            var input = item.GetProperty("input");
            var options = new TranscriptionOptions
            {
                Language = MeetingLanguages.Parse(input.GetProperty("language").GetString()) ?? MeetingLanguage.Zh,
                Glossary = [.. input.GetProperty("glossary").EnumerateArray().Select(x => x.GetString()!)],
                AudioSource = input.GetProperty("audioSource").GetString()!,
                MinSpeakers = input.TryGetProperty("minSpeakers", out var min) ? min.GetInt32() : null,
                Diarize = false,
            };
            Golden.SameJson(item.GetProperty("settings"), System.Text.Json.JsonDocument.Parse(ServerTranscriber.Settings(options).ToJsonString()).RootElement, "server/settings.json");
        }
        var results = Golden.ExpectedJson("server/result.json").EnumerateArray().ToList();
        var inputs = Golden.Fixtures.GetProperty("serverResults").EnumerateArray().ToList();
        Assert.Equal(inputs.Count, results.Count);
        for (var i = 0; i < inputs.Count; i++)
        {
            var o = JsonNode.Parse(inputs[i].GetRawText())!.AsObject();
            string actual;
            try
            {
                var r = ServerTranscriber.Parse(o);
                actual = new JsonObject
                {
                    ["segments"] = new JsonArray([.. r.Transcription.Segments.Select(s => (JsonNode)new JsonObject
                    {
                        ["start"] = s.Start, ["end"] = s.End, ["text"] = s.Text, ["lowConfidence"] = s.LowConfidence,
                    })]),
                    ["seams"] = new JsonArray([.. r.Transcription.Seams.Select(x => (JsonNode)x)]),
                    ["engine"] = r.TranscribedWith,
                }.ToJsonString();
            }
            catch (Shiyin.Whisper.WhisperException e) when (e.Kind == Shiyin.Whisper.WhisperErrorKind.Empty) { actual = "{\"error\":\"empty\"}"; }
            Golden.SameJson(results[i], System.Text.Json.JsonDocument.Parse(actual).RootElement, $"server/result.json[{i}]");
        }
        // Windows 另外用到的：說話者跟著段落（略過的段落不留說話者）
        var first = ServerTranscriber.Parse(JsonNode.Parse(inputs[0].GetRawText())!.AsObject());
        Assert.Equal([1, 2], first.Transcription.Speakers);
        var skipped = ServerTranscriber.Parse(JsonNode.Parse(inputs[3].GetRawText())!.AsObject());
        Assert.Equal((1, 1), (skipped.Transcription.Segments.Count, skipped.Transcription.Speakers.Count));
    }

    /// <summary>伺服器自己擋下的只顯示伺服器的訊息；模型回的錯誤照舊加前綴（R51.6、M§36.3）</summary>
    [Fact]
    public async Task ServerErrorsShowOnlyTheServersMessage()
    {
        var server = await SignedIn();
        var llm = new LLMClient(new MemoryKeys(), new NoTools(), new ProcessRunner(), http) { Enterprise = server };
        var format = new OutputFormat("""{"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"]}""", "answer", "answer");
        fake.LlmReply = (400, new JsonObject { ["error"] = new JsonObject { ["kind"] = "bad_request", ["message"] = "不是拾音的整理請求，企業伺服器不轉送" } });
        var blocked = await Assert.ThrowsAsync<LLMException>(() => llm.GenerateJson(LLMProvider.Enterprise, "", "s", "p", format, CancellationToken.None));
        Assert.Equal("不是拾音的整理請求，企業伺服器不轉送", blocked.Message);
        // 轉送的模型回的錯誤（沒有 kind）：前綴說明是公司的伺服器轉回來的
        fake.LlmReply = (400, new JsonObject { ["error"] = new JsonObject { ["message"] = "model overloaded", ["type"] = "invalid_request_error" } });
        var upstream = await Assert.ThrowsAsync<LLMException>(() => llm.GenerateJson(LLMProvider.Enterprise, "", "s", "p", format, CancellationToken.None));
        Assert.Contains("HTTP 400", upstream.Message);
        Assert.Contains("model overloaded", upstream.Message);
        // 額度用完（429）是用量上限，暫停（429）一樣；只有訊息
        var limited = ApiRequest.ServerError(new ApiRequest.Response(429, JsonNode.Parse("""{"error":{"kind":"busy","message":"今天的整理額度用完了"}}"""), "今天的整理額度用完了"))!;
        Assert.Equal((LLMErrorKind.Limited, "今天的整理額度用完了"), (limited.Kind, limited.Message));
        Assert.Null(ApiRequest.ServerError(new ApiRequest.Response(429, JsonNode.Parse("""{"error":{"message":"Rate limit"}}"""), "Rate limit")));
        fake.LlmReply = null;
    }

    [Fact]
    public async Task SummarizerReadinessFollowsTheLogin()
    {
        // 登入時伺服器還沒有模型：已登入但不能整理（R51.3）
        fake.Llm = false;
        var server = await SignedIn();
        settings.EnterpriseSignedIn = () => server.IsSignedIn;
        settings.EnterpriseHasModels = () => server.Account?.Llm == true;
        settings.Set(AppSettings.ProviderKey, "enterprise");
        Assert.Equal(SummarizerProblemKind.EnterpriseNoModels, settings.SummarizerProblem?.Kind);
        // 管理者在後台加了模型：重新讀伺服器的資訊就好，不用重新登入（R51.5）
        fake.Llm = true;
        var changed = 0;
        server.Changed += () => changed++;
        await server.RefreshInfo();
        Assert.Equal(1, changed);
        Assert.Null(settings.SummarizerProblem);
        await server.RefreshInfo();
        Assert.Equal(1, changed);     // 沒變就不通知
        await server.SignOut();
        settings.Set(AppSettings.ProviderKey, "enterprise");
        Assert.Equal(SummarizerProblemKind.EnterpriseSignedOut, settings.SummarizerProblem?.Kind);
    }

    /// <summary>公司在後台設定的模型（R51.1）：名稱給人看、id 給伺服器、哪個是預設</summary>
    [Fact]
    public async Task CompanyModelsComeFromTheServer()
    {
        var server = await SignedIn();
        var llm = new LLMClient(new MemoryKeys(), new NoTools(), new ProcessRunner(), http) { Enterprise = server };
        var models = await llm.EnterpriseModels(CancellationToken.None);
        Assert.NotNull(models);
        Assert.Equal(["機房的 Gemma 3", "Claude"], models!.Select(m => m.Name));
        Assert.Equal("gemma-3-27b", models.Single(m => m.IsDefault).Id);
        Assert.Equal(["gemma-3-27b", "claude-sonnet-5-5"], await llm.ListModels(LLMProvider.Enterprise, CancellationToken.None));
        Assert.Equal("公司", LLMProvider.Enterprise.Label());
        await server.SignOut();
        Assert.Null(await llm.EnterpriseModels(CancellationToken.None));
    }
}
