using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shiyin.Localization;
using Shiyin.Notes;

namespace Shiyin.LLM;

/// <summary>Anthropic API（M§8.4）：強制呼叫一個 tool，input_schema 就是要的格式</summary>
sealed class AnthropicApi(LLMClient client, string model) : ILLMBackend
{
    string Base => client.Base(LLMProvider.Anthropic, "https://api.anthropic.com");

    public async Task<string> GenerateJson(string system, string prompt, OutputFormat format, CancellationToken cancel)
    {
        var key = client.Keys.Read("anthropic");
        if (string.IsNullOrEmpty(key)) throw LLMException.MissingKey("Anthropic");
        var body = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = 16_000,
            ["system"] = system,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt }),
            ["tools"] = new JsonArray(new JsonObject { ["name"] = format.ToolName, ["description"] = format.ToolDescription, ["input_schema"] = JsonNode.Parse(format.Schema) }),
            ["tool_choice"] = new JsonObject { ["type"] = "tool", ["name"] = format.ToolName },
        };
        var obj = await ApiRequest.Send(client.Http, () =>
        {
            var r = new HttpRequestMessage(HttpMethod.Post, Base + "/v1/messages") { Content = ApiRequest.Json(body) };
            r.Headers.Add("x-api-key", key);
            r.Headers.Add("anthropic-version", "2023-06-01");
            return r;
        }, "Anthropic", cancel);
        var input = (obj["content"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(c => c["type"]?.GetValue<string>() == "tool_use")?["input"];
        if (input is null)
            throw LLMException.BadOutput(L.F("回應裡沒有會議紀錄（stop_reason：%@）", obj["stop_reason"]?.GetValue<string>() ?? L.T("未知")));
        return input.ToJsonString();
    }

    public async Task<List<string>> ListModels(CancellationToken cancel)
    {
        string? key;
        try { key = client.Keys.Read("anthropic"); } catch { return []; }
        if (string.IsNullOrEmpty(key)) return [];
        try
        {
            using var r = new HttpRequestMessage(HttpMethod.Get, Base + "/v1/models?limit=100");
            r.Headers.Add("x-api-key", key);
            r.Headers.Add("anthropic-version", "2023-06-01");
            using var response = await client.Http.SendAsync(r, cancel);
            var obj = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancel)) as JsonObject;
            return (obj?["data"] as JsonArray)?.OfType<JsonObject>().Select(m => m["id"]?.GetValue<string>()).OfType<string>().ToList() ?? [];
        }
        catch (Exception e) when (e is HttpRequestException or JsonException) { return []; }
    }
}

/// <summary>OpenAI API（M§8.4）：Responses API，text.format 用 strict json_schema</summary>
sealed class OpenAIApi(LLMClient client, string model) : ILLMBackend
{
    string Base => client.Base(LLMProvider.OpenAI, "https://api.openai.com");

    public async Task<string> GenerateJson(string system, string prompt, OutputFormat format, CancellationToken cancel)
    {
        var key = client.Keys.Read("openai");
        if (string.IsNullOrEmpty(key)) throw LLMException.MissingKey("OpenAI");
        var body = new JsonObject
        {
            ["model"] = model,
            ["instructions"] = system,
            ["input"] = prompt,
            ["text"] = new JsonObject { ["format"] = new JsonObject { ["type"] = "json_schema", ["name"] = format.ToolName, ["schema"] = JsonNode.Parse(format.Schema), ["strict"] = true } },
        };
        var obj = await ApiRequest.Send(client.Http, () =>
        {
            var r = new HttpRequestMessage(HttpMethod.Post, Base + "/v1/responses") { Content = ApiRequest.Json(body) };
            r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            return r;
        }, "OpenAI", cancel);
        var text = (obj["output"] as JsonArray)?.OfType<JsonObject>().SelectMany(o => (o["content"] as JsonArray)?.OfType<JsonObject>() ?? [])
            .FirstOrDefault(c => c["type"]?.GetValue<string>() == "output_text")?["text"]?.GetValue<string>();
        return text ?? throw LLMException.BadOutput(L.F("回應裡沒有文字（status：%@）", obj["status"]?.GetValue<string>() ?? L.T("未知")));
    }

    public async Task<List<string>> ListModels(CancellationToken cancel)
    {
        string? key;
        try { key = client.Keys.Read("openai"); } catch { return []; }
        if (string.IsNullOrEmpty(key)) return [];
        try
        {
            using var r = new HttpRequestMessage(HttpMethod.Get, Base + "/v1/models");
            r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await client.Http.SendAsync(r, cancel);
            var obj = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancel)) as JsonObject;
            var ids = (obj?["data"] as JsonArray)?.OfType<JsonObject>().Select(m => m["id"]?.GetValue<string>()).OfType<string>() ?? [];
            return ids.Where(i => i.StartsWith("gpt-", StringComparison.Ordinal) || i.StartsWith('o')).OrderByDescending(i => i, StringComparer.Ordinal).ToList();
        }
        catch (Exception e) when (e is HttpRequestException or JsonException) { return []; }
    }
}

/// <summary>Gemini API（M§19.2、D23）：Interactions API；store: false，不讓 Google 保存會議內容</summary>
sealed class GeminiApi(LLMClient client, string model) : ILLMBackend
{
    string Base => client.Base(LLMProvider.Gemini, "https://generativelanguage.googleapis.com");

    public async Task<string> GenerateJson(string system, string prompt, OutputFormat format, CancellationToken cancel)
    {
        var key = client.Keys.Read("gemini");
        if (string.IsNullOrEmpty(key)) throw LLMException.MissingKey("Gemini");
        var body = new JsonObject
        {
            ["model"] = model,
            ["system_instruction"] = system,
            ["input"] = prompt,
            ["response_format"] = new JsonObject { ["type"] = "text", ["mime_type"] = "application/json", ["schema"] = JsonNode.Parse(format.Schema) },
            ["store"] = false,
        };
        var obj = await ApiRequest.Send(client.Http, () =>
        {
            var r = new HttpRequestMessage(HttpMethod.Post, Base + "/v1beta/interactions") { Content = ApiRequest.Json(body) };
            r.Headers.Add("x-goog-api-key", key);
            return r;
        }, "Gemini", cancel);
        var status = obj["status"]?.GetValue<string>() ?? L.T("未知");
        switch (status)
        {
            case "completed": break;
            case "incomplete": throw LLMException.Failed(L.T("回應不完整（可能超過長度上限）"));
            default: throw LLMException.Failed(L.F("Gemini 沒有完成整理（status：%@）", status));
        }
        // steps 裡還有思考過程（thought），只取模型的輸出
        var text = string.Concat((obj["steps"] as JsonArray)?.OfType<JsonObject>()
            .Where(s => s["type"]?.GetValue<string>() == "model_output")
            .SelectMany(s => (s["content"] as JsonArray)?.OfType<JsonObject>() ?? [])
            .Where(c => c["type"]?.GetValue<string>() == "text")
            .Select(c => c["text"]?.GetValue<string>()).OfType<string>() ?? []);
        if (text.Length == 0) throw LLMException.BadOutput(L.F("回應裡沒有文字（status：%@）", status));
        return text;
    }

    static readonly string[] Excluded = ["embedding", "tts", "image", "live", "audio", "banana", "robotics", "computer-use", "transcribe", "customtools", "omni"];

    public async Task<List<string>> ListModels(CancellationToken cancel)
    {
        string? key;
        try { key = client.Keys.Read("gemini"); } catch { return []; }
        if (string.IsNullOrEmpty(key)) return [];
        try
        {
            using var r = new HttpRequestMessage(HttpMethod.Get, Base + "/v1beta/models?pageSize=1000");
            r.Headers.Add("x-goog-api-key", key);
            using var response = await client.Http.SendAsync(r, cancel);
            var obj = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancel)) as JsonObject;
            // 只留能整理文字的 Gemini 模型：圖片、語音、機器人等模型也支援 generateContent，要再依名稱排除
            return (obj?["models"] as JsonArray)?.OfType<JsonObject>()
                .Where(m => (m["supportedGenerationMethods"] as JsonArray)?.Any(x => x?.GetValue<string>() == "generateContent") == true)
                .Select(m => m["name"]?.GetValue<string>()).OfType<string>()
                .Where(n => n.StartsWith("models/gemini-", StringComparison.Ordinal))
                .Select(n => n["models/".Length..])
                .Where(n => !Excluded.Any(n.Contains)).ToList() ?? [];
        }
        catch (Exception e) when (e is HttpRequestException or JsonException) { return []; }
    }
}

/// <summary>OpenAI 相容 API（M§19.3、D24）：Chat Completions；模型不支援 json_schema 時退回 JSON 模式</summary>
public sealed class OpenAICompatibleApi(LLMClient client, string model, string baseUrl) : ILLMBackend
{
    public const string BaseUrlKey = "openaiCompatibleBaseURL";

    /// <summary>設定頁「常用」選單</summary>
    public static readonly (string Name, string Url)[] Presets =
    [
        ("Groq", "https://api.groq.com/openai/v1"),
        ("OpenRouter", "https://openrouter.ai/api/v1"),
        ("Ollama", "http://localhost:11434/v1"),
        ("LM Studio", "http://localhost:1234/v1"),
    ];

    /// <summary>整理過的網址：只接受 http、https；去掉結尾的「/」和使用者多貼的「/chat/completions」</summary>
    public static Uri? Normalized(string text)
    {
        var s = text.Trim();
        while (s.EndsWith('/')) s = s[..^1];
        if (s.EndsWith("/chat/completions", StringComparison.Ordinal)) s = s[..^"/chat/completions".Length];
        while (s.EndsWith('/')) s = s[..^1];
        if (!Uri.TryCreate(s, UriKind.Absolute, out var url)) return null;
        if (url.Scheme is not ("http" or "https") || url.IdnHost.Length == 0) return null;
        return url;
    }

    /// <summary>Mac 的 URL.absoluteString：照使用者寫的樣子（不補結尾的斜線）</summary>
    public static string? NormalizedText(string text)
    {
        if (Normalized(text) is null) return null;
        var s = text.Trim();
        while (s.EndsWith('/')) s = s[..^1];
        if (s.EndsWith("/chat/completions", StringComparison.Ordinal)) s = s[..^"/chat/completions".Length];
        while (s.EndsWith('/')) s = s[..^1];
        return s;
    }

    /// <summary>主機是這台電腦：內容不會離開電腦（隱私說明用）</summary>
    public static bool IsLocal(Uri url) => url.Host.Trim('[', ']').ToLowerInvariant() is "localhost" or "127.0.0.1" or "::1";

    public async Task<string> GenerateJson(string system, string prompt, OutputFormat format, CancellationToken cancel)
    {
        if (baseUrl.Trim().Length == 0) throw LLMException.NotConfigured(L.T("還沒有填 OpenAI 相容 API 的網址，請到設定 → 整理工具填寫"));
        if (NormalizedText(baseUrl) is not { } baseText || Normalized(baseUrl) is not { } url)
            throw LLMException.NotConfigured(L.T("OpenAI 相容 API 的網址不對，要以 http:// 或 https:// 開頭"));
        if (model.Length == 0) throw LLMException.NotConfigured(L.T("還沒有填 OpenAI 相容 API 的模型名稱，請到設定 → 整理工具填寫"));
        var provider = url.Host.Length > 0 ? url.Host : L.T("OpenAI 相容 API");
        var strict = new JsonObject
        {
            ["type"] = "json_schema",
            ["json_schema"] = new JsonObject { ["name"] = format.ToolName, ["schema"] = JsonNode.Parse(format.Schema), ["strict"] = true },
        };
        var response = await Complete(baseText, provider, system, prompt, strict, cancel);
        if (RejectsSchema(response.Status, response.Message))
        {
            // 模型不支援 json_schema：改用 JSON 模式，schema 放進系統提示詞（D24）
            var fallback = system + "\n\nRespond with only a JSON object that matches this JSON schema, with no other text:\n" + format.Schema;
            response = await Complete(baseText, provider, fallback, prompt, new JsonObject { ["type"] = "json_object" }, cancel);
        }
        if (response.Status != 200) throw ApiRequest.Error(response, provider);
        var choice = (response.Object["choices"] as JsonArray)?.FirstOrDefault() as JsonObject;
        var finish = choice?["finish_reason"]?.GetValue<string>();
        if (finish == "length") throw LLMException.Failed(L.T("回應不完整（可能超過長度上限）"));
        var text = (choice?["message"] as JsonObject)?["content"]?.GetValue<string>();
        if (string.IsNullOrEmpty(text)) throw LLMException.BadOutput(L.F("回應裡沒有文字（status：%@）", finish ?? L.T("未知")));
        var json = ExtractJson(text);
        try
        {
            if (JsonNode.Parse(json) is JsonObject) return json;
        }
        catch (JsonException) { }
        throw LLMException.BadOutput(L.T("回應不是 JSON。") + LLMProvider.OpenAICompatible.BadOutputHint());
    }

    public async Task<List<string>> ListModels(CancellationToken cancel)
    {
        if (NormalizedText(baseUrl) is not { } baseText) return [];
        string? key;
        // 有存 key 卻讀不到時不送出：不帶 key 送出只會換來看不懂的錯誤（M§22.1）
        try { key = client.Keys.Read("openaiCompatible"); } catch { return []; }
        try
        {
            using var r = new HttpRequestMessage(HttpMethod.Get, baseText + "/models");
            if (!string.IsNullOrEmpty(key)) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, timeout.Token);
            using var response = await client.Http.SendAsync(r, linked.Token);
            var obj = JsonNode.Parse(await response.Content.ReadAsStringAsync(linked.Token)) as JsonObject;
            return (obj?["data"] as JsonArray)?.OfType<JsonObject>().Select(m => m["id"]?.GetValue<string>()).OfType<string>()
                .OrderBy(x => x, StringComparer.Ordinal).ToList() ?? [];
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or OperationCanceledException) { return []; }
    }

    Task<ApiRequest.Response> Complete(string baseText, string provider, string system, string prompt, JsonObject responseFormat, CancellationToken cancel)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = system }, new JsonObject { ["role"] = "user", ["content"] = prompt }),
            ["response_format"] = responseFormat,
        };
        var key = client.Keys.Read("openaiCompatible");
        return ApiRequest.Fetch(client.Http, () =>
        {
            var r = new HttpRequestMessage(HttpMethod.Post, baseText + "/chat/completions") { Content = ApiRequest.Json(body) };
            if (!string.IsNullOrEmpty(key)) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            return r;
        }, provider, cancel);
    }

    /// <summary>400／422 而且錯誤訊息提到 response_format、json_schema 或 schema：模型不支援 JSON schema</summary>
    public static bool RejectsSchema(int status, string message)
    {
        if (status is not (400 or 422)) return false;
        var m = message.ToLowerInvariant();
        return m.Contains("response_format") || m.Contains("json_schema") || m.Contains("schema");
    }

    /// <summary>模型在 JSON 前後加了說明或 ```json 圍欄時，取第一個「{」到最後一個「}」</summary>
    public static string ExtractJson(string text)
    {
        int start = text.IndexOf('{'), end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }
}

/// <summary>claude CLI（M§8.4）。工作目錄是一個空的暫存資料夾：不會讀到任何專案的 CLAUDE.md，用完就刪</summary>
sealed class ClaudeCli(LLMClient client, string model) : ILLMBackend
{
    public async Task<string> GenerateJson(string system, string prompt, OutputFormat format, CancellationToken cancel)
    {
        var tool = client.Tools.Find("claude") ?? throw LLMException.NotInstalled("claude");
        var dir = TempDirectory.Create();
        try
        {
            // 不能加 --bare：--bare 不讀 OAuth 登入資訊
            var args = new List<string> { "-p", "--output-format", "json", "--tools", "", "--no-session-persistence", "--strict-mcp-config",
                "--system-prompt", system, "--json-schema", format.Schema };
            if (model.Length > 0) args.AddRange(["--model", model]);
            var result = await client.Runner.Run(tool, args, prompt, dir, LLMClient.Timeout, cancel);
            var line = result.Stdout.Split('\n').LastOrDefault(l => l.StartsWith('{'));
            JsonObject? obj = null;
            if (line is not null) try { obj = JsonNode.Parse(line) as JsonObject; } catch (JsonException) { }
            if (obj is null)
            {
                var detail = result.StderrTail(500);
                if (LLMException.IsLoginProblem(detail + result.Stdout)) throw LLMException.NotLoggedIn("claude");
                if (LLMException.IsLimitProblem(detail + result.Stdout)) throw LLMException.Limited(detail.Length == 0 ? result.Stdout[..Math.Min(500, result.Stdout.Length)] : detail);
                throw LLMException.Failed(detail.Length == 0 ? L.F("claude 沒有回傳結果（結束代碼 %lld）", (long)result.ExitCode) : detail);
            }
            if (obj["is_error"]?.GetValueKind() == JsonValueKind.True || obj["structured_output"] is null)
            {
                var message = obj["result"]?.GetValueKind() == JsonValueKind.String ? obj["result"]!.GetValue<string>() : result.StderrTail(500);
                if (LLMException.IsLoginProblem(message)) throw LLMException.NotLoggedIn("claude");
                if (LLMException.IsLimitProblem(message)) throw LLMException.Limited(message.Length > 500 ? message[..500] : message);
                throw LLMException.Failed(message.Length > 500 ? message[..500] : message);
            }
            return obj["structured_output"]!.ToJsonString();
        }
        finally { TempDirectory.Delete(dir); }
    }

    public Task<List<string>> ListModels(CancellationToken cancel) => Task.FromResult(new List<string>());
}

/// <summary>codex CLI（M§8.4）：沒有獨立的系統提示詞參數，放在使用者提示詞前面</summary>
sealed class CodexCli(LLMClient client, string model) : ILLMBackend
{
    public async Task<string> GenerateJson(string system, string prompt, OutputFormat format, CancellationToken cancel)
    {
        var tool = client.Tools.Find("codex") ?? throw LLMException.NotInstalled("codex");
        var dir = TempDirectory.Create();
        try
        {
            var schemaFile = Path.Combine(dir, "schema.json");
            var output = Path.Combine(dir, "out.json");
            await File.WriteAllTextAsync(schemaFile, format.Schema, cancel);
            var args = new List<string> { "exec", "--skip-git-repo-check", "--ephemeral", "--sandbox", "read-only", "--color", "never",
                "--output-schema", schemaFile, "-o", output };
            if (model.Length > 0) args.AddRange(["-m", model]);
            args.Add("-");
            var result = await client.Runner.Run(tool, args, system + "\n\n" + prompt, dir, LLMClient.Timeout, cancel);
            var data = File.Exists(output) ? await File.ReadAllTextAsync(output, cancel) : "";
            if (!result.Succeeded || data.Length == 0)
            {
                var detail = result.StderrTail(500);
                if (LLMException.IsLoginProblem(detail)) throw LLMException.NotLoggedIn("codex");
                if (LLMException.IsLimitProblem(detail)) throw LLMException.Limited(detail);
                throw LLMException.Failed(detail.Length == 0 ? L.F("codex 沒有回傳結果（結束代碼 %lld）", (long)result.ExitCode) : detail);
            }
            return data;
        }
        finally { TempDirectory.Delete(dir); }
    }

    public Task<List<string>> ListModels(CancellationToken cancel) => Task.FromResult(new List<string>());
}

/// <summary>暫存資料夾（SHIYIN_HOME\tmp 下，用完即刪）</summary>
public static class TempDirectory
{
    /// <summary>暫存資料夾的根目錄；app 啟動時設成 SHIYIN_HOME\tmp</summary>
    public static string Root { get; set; } = Path.Combine(Path.GetTempPath(), "shiyin");

    public static string Create()
    {
        var dir = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void Delete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
