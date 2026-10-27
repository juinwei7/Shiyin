using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shiyin.LLM;
using Shiyin.Model;

namespace Shiyin.Services;

/// <summary>
/// 匿名使用統計（design-windows §12.10、M§30、R40）：每天一份匯總，送到打包時寫進 app 的統計網址。
/// 不含任何會議內容（R40.2）。沒有網址、或使用者關掉時，不累計也不送。任何執行緒都能呼叫
/// </summary>
public sealed class UsageStats(string file, AppSettings settings, HttpClient http, Func<UsageStats.AppInfo> app)
{
    /// <summary>設定 → 進階的開關（預設開，鍵名同 Mac）</summary>
    public const string EnabledKey = "usageStats";
    /// <summary>後台只收 64 字以內的字串（workers/usage/worker.js），超過的那一天會被整份拒絕</summary>
    public const int MaxText = 64;

    /// <summary>送出當下的環境（M§30.4）：版本、系統、CPU、介面語言（zh-Hant、en）</summary>
    public sealed record AppInfo(string Version, string Os, string Chip, string UiLanguage);

    public enum Failure { Recording, Transcribe, Diarize, Summarize }

    // 整理失敗的種類（R47、M§34.5）：要在 Groups 前面（靜態欄位照順序初始化）
    public static readonly string[] SummarizeErrorKinds =
        ["notInstalled", "notLoggedIn", "missingKey", "notConfigured", "keyRejected", "limited", "timeout", "unreachable", "badOutput", "other"];
    static readonly string[] DetailFields = ["summarizeErrors", "summarizeErrorProviders", "summarizeDraftErrors"];

    // 欄位和鍵名同 Mac 的 Counters（usage.json、送出的格式都跟 Mac 一樣）
    static readonly string[] Numbers =
        ["meetingsRecorded", "recordedSeconds", "meetingsImported", "importedSeconds", "transcribedSeconds", "transcribeWallSeconds",
         "diarizationRuns", "questionsAsked", "summarizeDraftErrors"];
    static readonly (string Name, string[] Keys)[] Groups =
    [
        ("engines", ["breeze", "builtin", "whisperJa", "qwen"]),
        ("meetingLanguages", ["zh", "en", "ja"]),
        ("summarizer", ["claudeCode", "codex", "api", "local"]),
        ("review", ["answered", "confirmed", "skipped"]),   // 待確認送出的題數（R41.7、M§31.3）
        ("errors", ["recording", "transcribe", "diarize", "summarize"]),
        // 整理失敗的種類、當時的整理工具（R47、M§34.5）；summarizeDraftErrors 在 Numbers
        ("summarizeErrors", SummarizeErrorKinds),
        ("summarizeErrorProviders", ["claudeCode", "codex", "api", "local"]),
    ];


    /// <summary>一天的匯總：數字和四組字典，鍵一定都在</summary>
    sealed class Counters
    {
        public readonly Dictionary<string, int> Values = Numbers.ToDictionary(n => n, _ => 0);
        public readonly Dictionary<string, Dictionary<string, int>> Dicts = Groups.ToDictionary(g => g.Name, g => g.Keys.ToDictionary(k => k, _ => 0));

        public void Add(string name, int value) => Values[name] += value;
        public void Add(string group, string key, int value) => Dicts[group][key] = Dicts[group].GetValueOrDefault(key) + value;

        public JsonObject Json(params string[] skip)
        {
            var o = new SortedDictionary<string, JsonNode?>(StringComparer.Ordinal);
            foreach (var (k, v) in Values) if (!skip.Contains(k)) o[k] = v;
            foreach (var (g, d) in Dicts) if (!skip.Contains(g)) o[g] = Sorted(d.ToDictionary(p => p.Key, p => (JsonNode?)p.Value));
            return new JsonObject(o);
        }

        public static Counters From(JsonNode? node)
        {
            var c = new Counters();
            if (node is not JsonObject o) return c;
            foreach (var n in Numbers) c.Values[n] = Int(o[n]);
            foreach (var (g, keys) in Groups)
                if (o[g] is JsonObject d)
                    foreach (var (k, v) in d) c.Dicts[g][k] = Int(v);
            return c;
        }

        static int Int(JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;
    }

    sealed class Store
    {
        public string InstallId = NewId();
        public Dictionary<string, Counters> Days = [];
        public List<string> Sent = [];
    }

    readonly object gate = new();
    Store? cached;
    bool sending;

    /// <summary>統計網址：正式版是打包時寫進 app 的（AppHost 設）；DEBUG 只用控制通道的 usageurl（M§30.7）</summary>
    public string? Url { get; set; }
    /// <summary>設定頁、使用說明要不要顯示這個功能：有網址的版本才有；DEBUG 一律顯示（M§30.5）</summary>
    public bool AlwaysOffered { get; init; }
    public Func<DateTime> Now { get; init; } = () => DateTime.Now;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(20);

    // MARK: 開關、網址

    /// <summary>只接受 https；localhost、127.0.0.1 給測試用（同 Mac）</summary>
    public Uri? Endpoint
    {
        get
        {
            var text = (Url ?? "").Trim();
            if (text.Length == 0 || !Uri.TryCreate(text, UriKind.Absolute, out var url)) return null;
            if (url.Scheme == Uri.UriSchemeHttps) return url;
            if (url.Scheme == Uri.UriSchemeHttp && url.Host.ToLowerInvariant() is "localhost" or "127.0.0.1") return url;
            return null;
        }
    }

    public bool IsOffered => AlwaysOffered || Endpoint is not null;
    public bool IsEnabled => settings.Flag(EnabledKey, true);
    bool IsActive => Endpoint is not null && IsEnabled;

    // MARK: 累計（M§30.1）

    /// <summary>錄音存檔成功</summary>
    public void Recorded(double seconds) => Add(c => { c.Add("meetingsRecorded", 1); c.Add("recordedSeconds", Whole(seconds)); });

    /// <summary>匯入音訊或影片成功（長度在轉錄完成時才加，見 Transcribed）</summary>
    public void Imported() => Add(c => c.Add("meetingsImported", 1));

    /// <summary>轉錄完成。engine 是會議的 transcribedWith（breeze、system、japanese）；system 記成 builtin（跨平台的名稱，M§30.1）</summary>
    public void Transcribed(double seconds, double wallSeconds, string engine, MeetingLanguage language, bool imported, bool diarized)
    {
        var audio = Whole(seconds);
        var key = engine switch { "system" => "builtin", "japanese" => "whisperJa", "qwen" => "qwen", "server" => "server", _ => "breeze" };
        Add(c =>
        {
            c.Add("transcribedSeconds", audio);
            c.Add("transcribeWallSeconds", Whole(wallSeconds));
            c.Add("engines", key, audio);
            c.Add("meetingLanguages", language.Code(), 1);
            if (imported) c.Add("importedSeconds", audio);
            if (diarized) c.Add("diarizationRuns", 1);
        });
    }

    /// <summary>重新辨識說話者完成</summary>
    public void Rediarized() => Add(c => c.Add("diarizationRuns", 1));

    /// <summary>整理（含草稿、修正）成功。只記整理工具的種類，不記網址、模型</summary>
    public void Summarized(LLMProvider provider)
    {
        if (ProviderKind(provider) is { } key) Add(c => c.Add("summarizer", key, 1));
    }

    /// <summary>整理（含草稿、修正）失敗（R47、M§34.5）：原本的次數照舊，另外記種類、整理工具、是不是草稿；不記錯誤訊息</summary>
    public void SummarizeFailed(Exception error, LLMProvider provider, bool draft)
    {
        var kind = SummarizeFailureKind(error);
        var providerKey = ProviderKind(provider);
        Add(c =>
        {
            c.Add("errors", "summarize", 1);
            c.Add("summarizeErrors", kind, 1);
            if (providerKey is not null) c.Add("summarizeErrorProviders", providerKey, 1);
            if (draft) c.Add("summarizeDraftErrors", 1);
        });
    }

    string? ProviderKind(LLMProvider provider) => provider switch
    {
        LLMProvider.Claude => "claudeCode",
        LLMProvider.Codex => "codex",
        LLMProvider.OpenAICompatible =>
            OpenAICompatibleApi.Normalized(settings.CompatibleBaseUrl) is { } url && OpenAICompatibleApi.IsLocal(url) ? "local" : "api",
        LLMProvider.Anthropic or LLMProvider.OpenAI or LLMProvider.Gemini => "api",
        _ => null,
    };

    /// <summary>失敗的種類（M§34.5）：LLM 的錯誤照種類；CLI 逾時是 timeout；其他是 other</summary>
    public static string SummarizeFailureKind(Exception error) => error switch
    {
        LLMException { Kind: LLMErrorKind.NotInstalled } => "notInstalled",
        LLMException { Kind: LLMErrorKind.NotLoggedIn } => "notLoggedIn",
        LLMException { Kind: LLMErrorKind.MissingKey } => "missingKey",
        LLMException { Kind: LLMErrorKind.NotConfigured } => "notConfigured",
        LLMException { Kind: LLMErrorKind.Rejected } => "keyRejected",
        LLMException { Kind: LLMErrorKind.Limited } => "limited",
        LLMException { Kind: LLMErrorKind.Unreachable } => "unreachable",
        LLMException { Kind: LLMErrorKind.BadOutput } => "badOutput",
        TimeoutException => "timeout",
        _ => "other",
    };

    public void Asked() => Add(c => c.Add("questionsAsked", 1));

    /// <summary>待確認送出成功（R41.7）：只記題數，不記題目和答案</summary>
    public void Reviewed(int answered, int confirmed, int skipped)
    {
        if (answered + confirmed + skipped <= 0) return;
        Add(c => { c.Add("review", "answered", answered); c.Add("review", "confirmed", confirmed); c.Add("review", "skipped", skipped); });
    }

    public void Failed(Failure kind) => Add(c => c.Add("errors", kind.ToString().ToLowerInvariant(), 1));

    /// <summary>打開 app、每小時檢查時建立今天的空白匯總：沒有用量的日子也算活躍（M§30.3）</summary>
    public void Touch() => Add(_ => { });

    void Add(Action<Counters> update)
    {
        if (!IsActive) return;
        lock (gate)
        {
            var store = Load();
            var today = Day(Now());
            if (!store.Days.TryGetValue(today, out var counters)) store.Days[today] = counters = new Counters();
            update(counters);
            Save(store);
        }
    }

    // MARK: 設定頁、DEBUG

    /// <summary>下一份要送的內容（控制通道的 usage、測試用；設定頁不顯示）；還沒有已過完的日子就是今天到目前為止的</summary>
    public string PreviewJson()
    {
        string installId, date;
        Counters counters;
        lock (gate)
        {
            var store = Load();
            var today = Day(Now());
            date = PendingDates(store, today).FirstOrDefault() ?? today;
            installId = store.InstallId;
            counters = store.Days.GetValueOrDefault(date) ?? new Counters();
        }
        return Payload(installId, date, counters).ToJsonString(Pretty);
    }

    public void ResetInstallId()
    {
        lock (gate)
        {
            var store = Load();
            store.InstallId = NewId();
            Save(store);
        }
    }

    /// <summary>關掉開關時：清掉還沒送的資料（R40.4）</summary>
    public void ClearPending()
    {
        lock (gate)
        {
            var store = Load();
            store.Days.Clear();
            Save(store);
        }
    }

    // MARK: 送出（M§30.3）

    /// <summary>啟動 60 秒後送一次，之後每小時檢查一次</summary>
    public async Task RunAutomaticSends(CancellationToken cancel)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(60), cancel);
            while (!cancel.IsCancellationRequested)
            {
                // 送統計失敗不能影響 app（R40.5）：任何錯誤只記 log，下一小時再來
                try
                {
                    if (!IsEnabled) ClearPending();
                    Touch();
                    await SendPending(cancel);
                }
                catch (Exception e) when (e is not OperationCanceledException) { Log.Error($"usage stats failed: {e.GetType().Name}"); }
                await Task.Delay(TimeSpan.FromHours(1), cancel);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>送今天以前、最近 3 天內、還沒送過的日子，一天一個請求，從舊到新</summary>
    public async Task SendPending(CancellationToken cancel = default)
    {
        if (Endpoint is not { } url || !IsEnabled) return;
        string installId;
        List<(string Date, Counters Counters)> pending;
        lock (gate)
        {
            if (sending) return;
            sending = true;
            var store = Load();
            installId = store.InstallId;
            // 複製一份：送出時其他執行緒可能還在累計今天的（送的是以前的日子，但不要共用同一個物件）
            pending = PendingDates(store, Day(Now())).Select(d => (d, Counters.From(store.Days[d].Json()))).ToList();
        }
        try
        {
            foreach (var (date, counters) in pending)
            {
                int code;
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(Payload(installId, date, counters).ToJsonString(Compact), Encoding.UTF8, "application/json"),
                    };
                    request.Headers.UserAgent.TryParseAdd($"Shiyin/{app().Version}");
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                    timeout.CancelAfter(RequestTimeout);
                    using var response = await http.SendAsync(request, timeout.Token);
                    code = (int)response.StatusCode;
                }
                catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException)
                {
                    if (cancel.IsCancellationRequested) return;
                    Log.Info($"usage stats not sent: {e.GetType().Name}");
                    break;   // 網路有問題：剩下的下次再送
                }
                var delivered = code is >= 200 and < 300;
                var rejected = code is >= 400 and < 500 && code != 429;
                lock (gate)
                {
                    var store = Load();
                    if (delivered || rejected) store.Days.Remove(date);
                    if (delivered) store.Sent.Add(date);
                    Save(store);
                }
                if (rejected) Log.Error($"usage stats rejected: HTTP {code}");   // 格式被拒，那一天不再送
                if (!delivered && !rejected)
                {
                    Log.Info($"usage stats not sent: HTTP {code}");
                    break;   // 5xx、429：下次再送
                }
            }
        }
        finally
        {
            lock (gate) sending = false;
        }
    }

    /// <summary>今天以前、最近 3 天內、還沒送過的日子（從舊到新）</summary>
    List<string> PendingDates(Store store, string today)
    {
        var oldest = Day(Now().AddDays(-3));
        return store.Days.Keys.Where(d => string.CompareOrdinal(d, today) < 0 && string.CompareOrdinal(d, oldest) >= 0 && !store.Sent.Contains(d))
            .Order(StringComparer.Ordinal).ToList();
    }

    // MARK: 格式（M§30.4）

    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>送出的內容，鍵依字母排序（同 Mac 的 sortedKeys）</summary>
    JsonObject Payload(string installId, string date, Counters counters)
    {
        var info = app();
        return Sorted(new Dictionary<string, JsonNode?>
        {
            ["schema"] = 1,
            ["installId"] = installId,
            ["date"] = date,
            ["app"] = Sorted(new Dictionary<string, JsonNode?>
            {
                ["platform"] = "windows", ["version"] = Text(info.Version), ["os"] = Text(info.Os), ["chip"] = Text(info.Chip),
                ["uiLanguage"] = Text(info.UiLanguage),
            }),
            ["usage"] = counters.Json(["errors", .. DetailFields]),
            ["errors"] = Sorted(counters.Dicts["errors"].ToDictionary(p => p.Key, p => (JsonNode?)p.Value)),
            // 2026-10-25 加（R47、M§34.5）：舊後台忽略這個鍵，schema 仍是 1
            ["errorDetail"] = Sorted(new Dictionary<string, JsonNode?>
            {
                ["summarize"] = Sorted(counters.Dicts["summarizeErrors"].ToDictionary(p => p.Key, p => (JsonNode?)p.Value)),
                ["summarizeProvider"] = Sorted(counters.Dicts["summarizeErrorProviders"].ToDictionary(p => p.Key, p => (JsonNode?)p.Value)),
                ["summarizeDraft"] = counters.Values["summarizeDraftErrors"],
            }),
        });
    }

    static JsonObject Sorted(IDictionary<string, JsonNode?> values) => new(new SortedDictionary<string, JsonNode?>(values, StringComparer.Ordinal));

    /// <summary>超過 64 字截斷（不切開代理字組）</summary>
    static string Text(string value)
    {
        value = value.Trim();
        if (value.Length <= MaxText) return value;
        var end = char.IsHighSurrogate(value[MaxText - 1]) ? MaxText - 1 : MaxText;
        return value[..end];
    }

    // MARK: 存檔（M§30.2）

    Store Load()
    {
        if (cached is not null) return cached;
        var store = new Store();
        try
        {
            if (File.Exists(file) && JsonNode.Parse(File.ReadAllText(file)) is JsonObject o)
            {
                if (o["installId"] is JsonValue id && id.TryGetValue<string>(out var s) && s.Length > 0) store.InstallId = s;
                if (o["days"] is JsonObject days) foreach (var (date, c) in days) store.Days[date] = Counters.From(c);
                if (o["sent"] is JsonArray sent)
                    store.Sent = sent.Select(x => x is JsonValue v && v.TryGetValue<string>(out var d) ? d : null).OfType<string>().ToList();
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            // 檔案讀不懂就重新開始（R40.5）
            Log.Error($"usage stats unreadable: {e.GetType().Name}");
            store = new Store();
        }
        return cached = store;
    }

    /// <summary>只留最近 4 天的匯總、最近 7 天送過的日期；先寫暫存檔再取代</summary>
    void Save(Store store)
    {
        var keepDays = Day(Now().AddDays(-4));
        var keepSent = Day(Now().AddDays(-7));
        foreach (var date in store.Days.Keys.Where(d => string.CompareOrdinal(d, keepDays) < 0).ToList()) store.Days.Remove(date);
        store.Sent = store.Sent.Where(d => string.CompareOrdinal(d, keepSent) >= 0).Distinct().Order(StringComparer.Ordinal).ToList();
        cached = store;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var json = Sorted(new Dictionary<string, JsonNode?>
            {
                ["installId"] = store.InstallId,
                ["days"] = Sorted(store.Days.ToDictionary(p => p.Key, p => (JsonNode?)p.Value.Json())),
                ["sent"] = new JsonArray(store.Sent.Select(d => (JsonNode?)d).ToArray()),
            });
            var temp = file + ".tmp";
            File.WriteAllText(temp, json.ToJsonString(Compact));
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error($"usage stats save failed: {e.GetType().Name}");
        }
    }

    static string Day(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    static string NewId() => Guid.NewGuid().ToString().ToLowerInvariant();
    static int Whole(double seconds) => double.IsFinite(seconds) ? (int)Math.Max(0, Math.Min(int.MaxValue, Math.Round(seconds, MidpointRounding.AwayFromZero))) : 0;

    // MARK: DEBUG（控制通道）

    /// <summary>usage：目前的檔案和下一份要送的內容</summary>
    public JsonObject DebugSnapshot()
    {
        JsonNode? stored;
        lock (gate)
        {
            Load();
            stored = File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)) : null;
        }
        return new JsonObject
        {
            ["store"] = stored, ["next"] = JsonNode.Parse(PreviewJson()), ["endpoint"] = Endpoint?.ToString() ?? "", ["enabled"] = IsEnabled,
        };
    }

    /// <summary>usageage：把今天的匯總改成幾天前（模擬時間過去）</summary>
    public void DebugAge(int days)
    {
        lock (gate)
        {
            var store = Load();
            var today = Day(Now());
            if (!store.Days.Remove(today, out var counters)) return;
            store.Days[Day(Now().AddDays(-days))] = counters;
            Save(store);
        }
    }
}
