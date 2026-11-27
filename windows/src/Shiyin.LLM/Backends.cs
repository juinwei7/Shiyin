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
            case "incomplete": throw LLMException.Failed(L.T("回應不完整（可能超過長度上限）"), FailureCause.Incomplete);
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
    /// <summary>企業伺服器（M§36.3）：key 換成登入拿到的 access token、模型可以留空（伺服器用公司的預設模型）、錯誤訊息裡的名稱</summary>
    public Func<CancellationToken, Task<string?>>? Key { get; init; }
    public bool RequiresModel { get; init; } = true;
    public string? Name { get; init; }
    public string? Language { get; init; }
    /// <summary>企業伺服器自己擋下的（額度、暫停、格式）只顯示伺服器的訊息（R51.6）</summary>
    public bool PlainServerErrors { get; init; }
    /// <summary>企業伺服器用不跟著轉址的 HttpClient（M§36.2、D126）；null 用整理工具共用的</summary>
    public HttpClient? Http { get; init; }

    public const string BaseUrlKey = "openaiCompatibleBaseURL";

    /// <summary>模型不支援 JSON schema 時寫進提示詞的說明，後面接 schema（D24）；agy 也用（M§95.1）</summary>
    public const string SchemaInstruction = "Respond with only a JSON object that matches this JSON schema, with no other text:\n";

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
        if (model.Length == 0 && RequiresModel) throw LLMException.NotConfigured(L.T("還沒有填 OpenAI 相容 API 的模型名稱，請到設定 → 整理工具填寫"));
        var provider = Name ?? (url.Host.Length > 0 ? url.Host : L.T("OpenAI 相容 API"));
        var strict = new JsonObject
        {
            ["type"] = "json_schema",
            ["json_schema"] = new JsonObject { ["name"] = format.ToolName, ["schema"] = JsonNode.Parse(format.Schema), ["strict"] = true },
        };
        var response = await Complete(baseText, provider, system, prompt, strict, cancel);
        if (RejectsSchema(response.Status, response.Message))
        {
            // 模型不支援 json_schema：改用 JSON 模式，schema 放進系統提示詞（D24）
            var fallback = system + "\n\n" + SchemaInstruction + format.Schema;
            response = await Complete(baseText, provider, fallback, prompt, new JsonObject { ["type"] = "json_object" }, cancel);
        }
        if (response.Status != 200) throw (PlainServerErrors ? ApiRequest.ServerError(response) : null) ?? ApiRequest.Error(response, provider);
        var choice = (response.Object["choices"] as JsonArray)?.FirstOrDefault() as JsonObject;
        var finish = choice?["finish_reason"]?.GetValue<string>();
        if (finish == "length") throw LLMException.Failed(L.T("回應不完整（可能超過長度上限）"), FailureCause.Incomplete);
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
        try { key = await CurrentKey(cancel); } catch { return []; }
        try
        {
            using var r = new HttpRequestMessage(HttpMethod.Get, baseText + "/models");
            if (!string.IsNullOrEmpty(key)) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, timeout.Token);
            using var response = await (Http ?? client.Http).SendAsync(r, linked.Token);
            var obj = JsonNode.Parse(await response.Content.ReadAsStringAsync(linked.Token)) as JsonObject;
            return (obj?["data"] as JsonArray)?.OfType<JsonObject>().Select(m => m["id"]?.GetValue<string>()).OfType<string>()
                .OrderBy(x => x, StringComparer.Ordinal).ToList() ?? [];
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or OperationCanceledException) { return []; }
    }

    async Task<string?> CurrentKey(CancellationToken cancel) => Key is not null ? await Key(cancel) : client.Keys.Read("openaiCompatible");

    async Task<ApiRequest.Response> Complete(string baseText, string provider, string system, string prompt, JsonObject responseFormat, CancellationToken cancel)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = system }, new JsonObject { ["role"] = "user", ["content"] = prompt }),
            ["response_format"] = responseFormat,
        };
        var key = await CurrentKey(cancel);
        return await ApiRequest.Fetch(Http ?? client.Http, () =>
        {
            var r = new HttpRequestMessage(HttpMethod.Post, baseText + "/chat/completions") { Content = ApiRequest.Json(body) };
            if (!string.IsNullOrEmpty(key)) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            if (Language is not null) r.Headers.AcceptLanguage.ParseAdd(Language);
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
                if (LLMException.IsLimitProblem(detail + result.Stdout)) throw LLMException.CliLimited(LLMProvider.Claude, detail + "\n" + result.Stdout);
                throw LLMException.Failed(detail.Length == 0 ? L.F("claude 沒有回傳結果（結束代碼 %lld）", (long)result.ExitCode) : detail,
                    FailureCauses.Cli(result));
            }
            if (obj["is_error"]?.GetValueKind() == JsonValueKind.True || obj["structured_output"] is null)
            {
                var message = obj["result"]?.GetValueKind() == JsonValueKind.String ? obj["result"]!.GetValue<string>() : result.StderrTail(500);
                if (LLMException.IsLoginProblem(message)) throw LLMException.NotLoggedIn("claude");
                if (LLMException.IsLimitProblem(message)) throw LLMException.CliLimited(LLMProvider.Claude, message);
                throw LLMException.Failed(message.Length > 500 ? message[..500] : message, FailureCause.CliError);
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
            ProcessResult result;
            try
            {
                result = await client.Runner.Run(tool, args, system + "\n\n" + prompt, dir, LLMClient.Timeout, cancel);
            }
            catch (System.ComponentModel.Win32Exception e) when (tool.DesktopApp is { } desktop)
            {
                // 桌面版附的 codex 有些電腦執行不了（拒絕存取，openai/codex #40867、#42645）：說原因、建議另外裝 CLI（R55.4）
                throw LLMException.Failed(L.F("沒辦法執行 %@ 附的 codex（%@）。可以另外安裝 Codex CLI（npm install -g @openai/codex）再試。", desktop, e.Message));
            }
            var data = File.Exists(output) ? await File.ReadAllTextAsync(output, cancel) : "";
            if (!result.Succeeded || data.Length == 0)
            {
                var detail = result.StderrTail(500);
                if (LLMException.IsLoginProblem(detail)) throw LLMException.NotLoggedIn("codex");
                if (LLMException.IsLimitProblem(detail)) throw LLMException.CliLimited(LLMProvider.Codex, detail);
                var message = detail.Length == 0 ? L.F("codex 沒有回傳結果（結束代碼 %lld）", (long)result.ExitCode) : detail;
                // 桌面版附的 codex 出錯：建議另外裝 CLI（R55.4）
                if (tool.DesktopApp is { } app)
                    message += "\n" + L.F("這是 %@ 附的 codex，也可以另外安裝 Codex CLI（npm install -g @openai/codex）再試。", app);
                throw LLMException.Failed(message, FailureCauses.Cli(result));
            }
            return data;
        }
        finally { TempDirectory.Delete(dir); }
    }

    public Task<List<string>> ListModels(CancellationToken cancel) => Task.FromResult(new List<string>());
}

/// <summary>
/// Antigravity CLI（agy，R121、M§95、design-windows §11.54）。agy 是會自己讀寫檔案、執行指令的代理：每次用 --settings 帶只在這次有效的設定，
/// 把讀寫檔案、指令、連網、MCP 都擋掉（D204），使用者自己的設定檔不動；工作目錄是空的暫存資料夾，用完就刪
/// </summary>
public sealed class AgyCli(LLMClient client, string model) : ILLMBackend
{
    /// <summary>只在這次有效的設定（--settings，不會存檔）：每個工具動作都要核准，拒絕規則擋掉所有會碰到檔案、指令、網路、MCP 的動作；
    /// 背景模式沒有人核准，等於全部不能用（M§95.2）。跟 Mac 一字不差</summary>
    public const string SessionSettings = """{"toolPermission":"strict","agentMode":"","permissions":{"deny":["read_file(*)","write_file(*)","command(*)","unsandboxed(*)","read_url(*)","execute_url(*)","download_url(*)","mcp(*)"]}}""";

    public async Task<string> GenerateJson(string system, string prompt, OutputFormat format, CancellationToken cancel)
    {
        var tool = client.Tools.Find("agy") ?? throw LLMException.NotInstalled("agy");
        var dir = TempDirectory.Create();
        // 第一行就帶 conversation_id：先記下來，逾時、取消、沒有 result 那一行時也刪得到這次的對話（M§95.4）
        string? conversation = null;
        try
        {
            // 設定、schema 放在工作目錄外面；工作目錄是空的
            var workspace = Path.Combine(dir, "workspace");
            Directory.CreateDirectory(workspace);
            var schemaFile = Path.Combine(dir, "schema.json");
            var settingsFile = Path.Combine(dir, "settings.json");
            await File.WriteAllTextAsync(schemaFile, format.Schema, cancel);
            await File.WriteAllTextAsync(settingsFile, SessionSettings, cancel);
            // 提示詞從 stdin 送（stream-json 一行一則），不放在命令列參數（R121.4）
            var result = await client.Runner.Run(tool, Arguments(schemaFile, settingsFile, model), Message(system, prompt, format.Schema),
                workspace, LLMClient.Timeout, cancel, line => conversation ??= ConversationIdIn(line));
            var parsed = Parse(result.Stdout);
            conversation ??= parsed.ConversationId;
            if (parsed.Output is { } output) return output;
            var detail = string.Join("\n", new[] { parsed.Error ?? "", result.StderrTail(500) }.Where(t => t.Length > 0));
            if (LLMException.IsLoginProblem(detail)) throw LLMException.NotLoggedIn("agy");
            if (LLMException.IsLimitProblem(detail)) throw LLMException.CliLimited(LLMProvider.Antigravity, detail);
            if (detail.Length == 0) throw LLMException.Failed(L.F("agy 沒有回傳結果（結束代碼 %d）", result.ExitCode), FailureCauses.Cli(result));
            throw LLMException.Failed(detail.Length > 500 ? detail[..500] : detail, parsed.Error is null ? FailureCauses.Cli(result) : FailureCause.CliError);
        }
        finally
        {
            if (conversation is { } id) RemoveConversation(client.AgyDataDirectory, id);
            TempDirectory.Delete(dir);
        }
    }

    /// <summary>stream-json 一行裡的 conversation_id（step_update、result 都有）</summary>
    public static string? ConversationIdIn(string line) =>
        line.Contains("conversation_id") && ConversationIdPattern.Match(line) is { Success: true } m ? m.Groups[1].Value : null;

    static readonly System.Text.RegularExpressions.Regex ConversationIdPattern = new("\"conversation_id\"\\s*:\\s*\"([0-9A-Fa-f-]{36})\"");

    /// <summary>agy 的參數：stream-json 進出、schema 和這次的設定用檔案給、不認斜線指令；有填模型才加 --model</summary>
    public static List<string> Arguments(string schemaFile, string settingsFile, string model)
    {
        var args = new List<string> { "--input-format", "stream-json", "--output-format", "stream-json", "--json-schema", schemaFile,
            "--settings", settingsFile, "--disable-slash-commands" };
        if (model.Length > 0) args.AddRange(["--model", model]);
        return args;
    }

    /// <summary>stream-json 的一行使用者訊息。agy 沒有系統提示詞參數，放在前面；--json-schema 只在最後整理格式，
    /// 不在提示詞裡寫出 schema 時第一輪會自己發明格式（M§95.1 實測），所以照 OpenAI 相容 API 的退回做法把 schema 也寫進去（D24）。
    /// 非 ASCII 的字寫成 \uXXXX（JSON 的預設），標準輸入只有 ASCII</summary>
    public static string Message(string system, string prompt, string schema)
    {
        var text = system + "\n\n" + prompt + "\n\n" + OpenAICompatibleApi.SchemaInstruction + schema;
        return new JsonObject { ["event"] = "user", ["message"] = new JsonObject { ["content"] = text } }.ToJsonString() + "\n";
    }

    /// <summary>Output：結果的 JSON；Error：失敗的說明；ConversationId：這次的對話（要刪掉）</summary>
    public sealed record Parsed(string? Output = null, string? Error = null, string? ConversationId = null);

    /// <summary>最後一行 result：status 是 SUCCESS 時取 structured_output（沒有就從 response 取 JSON）；其他狀態取 error</summary>
    public static Parsed Parse(string stdout)
    {
        JsonObject? result = null;
        var lines = stdout.Split('\n');
        for (var i = lines.Length - 1; i >= 0 && result is null; i--)
            if (Json(lines[i].Trim()) is { } obj && Str(obj["event"]) == "result" && obj["result"] is JsonObject r) result = r;
        if (result is null) return new Parsed();
        var conversation = Str(result["conversation_id"]) is { Length: > 0 } c ? c : null;
        var status = Str(result["status"]);
        if (status != "SUCCESS")
            return new Parsed(Error: Str(result["error"]) is { Length: > 0 } e ? e : status, ConversationId: conversation);
        string? output = null;
        if (result["structured_output"] is JsonObject or JsonArray) output = result["structured_output"]!.ToJsonString();
        else if (Str(result["response"]) is { } response)
        {
            var json = OpenAICompatibleApi.ExtractJson(response);
            if (Json(json) is not null) output = json;
        }
        return new Parsed(output, output is null ? L.T("agy 沒有回傳 JSON") : null, conversation);
    }

    /// <summary>agy 沒有不留紀錄的參數，每次都會把整段對話存進 %USERPROFILE%\.gemini\antigravity-cli（D204）：只刪這次的對話檔和 brain 資料夾。
    /// id 一定要是 UUID（8-4-4-4-12）才刪；agy 改了存放方式時這裡什麼都不做</summary>
    public static void RemoveConversation(string root, string id)
    {
        if (!Guid.TryParseExact(id, "D", out _)) return;
        try { File.Delete(Path.Combine(root, "conversations", id + ".db")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        try { Directory.Delete(Path.Combine(root, "brain", id), recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>agy models：一行一個「代號&lt;Tab&gt;名稱」，沒登入時結束代碼 1</summary>
    public async Task<List<string>> ListModels(CancellationToken cancel)
    {
        if (client.Tools.Find("agy") is not { } tool) return [];
        try
        {
            var result = await client.Runner.Run(tool, ["models"], null, Path.GetTempPath(), TimeSpan.FromSeconds(30), cancel);
            return result.Succeeded ? Models(result.Stdout) : [];
        }
        catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException
                                  or System.ComponentModel.Win32Exception or InvalidOperationException) { return []; }
    }

    /// <summary>「代號&lt;Tab&gt;名稱」的代號；Tab 前後是空的行不算（同 Mac 的 split）</summary>
    public static List<string> Models(string output) =>
        [.. output.Split('\n').Select(l => l.TrimEnd('\r')).Select(l => (Line: l, Tab: l.IndexOf('\t')))
            .Where(x => x.Tab > 0 && x.Tab < x.Line.Length - 1).Select(x => x.Line[..x.Tab].Trim())];

    static JsonObject? Json(string text)
    {
        if (text.Length == 0) return null;
        try { return JsonNode.Parse(text) as JsonObject; } catch (JsonException) { return null; }
    }

    static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
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

/// <summary>
/// 企業伺服器的整理（R51、M§36.3、D123）：伺服器轉送 OpenAI 相容格式給公司設定的模型。提示詞照舊在 app 組好；
/// key 是登入企業伺服器拿到的 access token；被拒絕（401、403）時換一次 token 再送，換不到就是登入失效
/// </summary>
public sealed class EnterpriseApi(LLMClient client, string model) : ILLMBackend
{
    OpenAICompatibleApi Inner(IEnterpriseAccess access)
    {
        var baseUrl = access.BaseUrl;
        return new(client, model, (baseUrl ?? "").TrimEnd('/') + "/api/v1/llm")
        {
            Key = async cancel =>
            {
                var token = await access.AccessToken(cancel);
                // 等 token 的時候換了伺服器（加入連結）：新伺服器的 token 不能送到舊的伺服器
                if (access.BaseUrl != baseUrl) throw LLMException.NotConfigured(L.T("企業伺服器的登入已失效，請重新登入"));
                return token;
            },
            RequiresModel = false, Name = L.T("公司的伺服器"), Language = access.Language, PlainServerErrors = true, Http = access.Http,
        };
    }

    public async Task<string> GenerateJson(string system, string prompt, OutputFormat format, CancellationToken cancel)
    {
        if (client.Enterprise is not { BaseUrl: not null } access)
            throw LLMException.NotConfigured(L.T("公司的伺服器還沒登入或登入已失效，請到設定 → 企業伺服器登入"));
        try
        {
            return await Inner(access).GenerateJson(system, prompt, format, cancel);
        }
        catch (LLMException e) when (e.Kind == LLMErrorKind.Rejected)
        {
            access.InvalidateAccess();
            try { return await Inner(access).GenerateJson(system, prompt, format, cancel); }
            catch (LLMException again) when (again.Kind == LLMErrorKind.Rejected)
            {
                throw LLMException.NotConfigured(L.T("企業伺服器的登入已失效，請重新登入"));
            }
        }
    }

    /// <summary>伺服器提供的模型（GET /api/v1/llm/models）</summary>
    public async Task<List<string>> ListModels(CancellationToken cancel) => (await Models(cancel))?.Select(m => m.Id).ToList() ?? [];

    /// <summary>公司在後台設定的模型（R51.1）：id 給伺服器、name 給人看；取不到是 null</summary>
    public async Task<List<EnterpriseModel>?> Models(CancellationToken cancel)
    {
        if (client.Enterprise is not { BaseUrl: not null } access) return null;
        try
        {
            using var r = new HttpRequestMessage(HttpMethod.Get, access.BaseUrl.TrimEnd('/') + "/api/v1/llm/models");
            r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await access.AccessToken(cancel));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, timeout.Token);
            using var response = await access.Http.SendAsync(r, linked.Token);
            if (!response.IsSuccessStatusCode) return null;
            var obj = JsonNode.Parse(await response.Content.ReadAsStringAsync(linked.Token)) as JsonObject;
            if (obj?["data"] is not JsonArray list) return null;
            return [.. list.OfType<JsonObject>().Where(m => m["id"]?.GetValue<string>() is { Length: > 0 }).Select(m =>
            {
                var id = m["id"]!.GetValue<string>();
                var name = m["name"]?.GetValue<string>() is { Length: > 0 } n ? n : id;
                return new EnterpriseModel(id, name, m["default"]?.GetValue<bool>() ?? false);
            })];
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or OperationCanceledException or LLMException or InvalidOperationException) { return null; }
    }
}

/// <summary>公司在後台設定的一個整理模型（R51.1）</summary>
public sealed record EnterpriseModel(string Id, string Name, bool IsDefault);
