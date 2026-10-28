using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Shiyin.LLM;
using Shiyin.Localization;
using Shiyin.Model;

namespace Shiyin.Services;

/// <summary>伺服器的 /api/v1/info（M§36.2）</summary>
public sealed record EnterpriseInfo(string Name, string Version, int ApiVersion, int MinAppApi, int? MaxAudioSeconds, string Engine, bool Llm);

/// <summary>登入的帳號（不含密鑰），存在 settings.json</summary>
/// <summary>登入的帳號（不含密鑰）；ServerVersion 是伺服器的版本（回報問題用，R52.3），4.2 以前存的帳號沒有</summary>
public sealed record EnterpriseAccount(string Email, string Name, string ServerName, string Engine, bool Llm, string? ServerVersion = null);

/// <summary>轉錄交給企業伺服器時，畫面上進度後面的說明（排隊第幾、為什麼在等）；有值就顯示「改在這台電腦轉錄」（R50.3、R50.5）</summary>
public sealed record ServerNote(string Text, bool Waiting = false);

/// <summary>refresh token 存在哪裡：Windows 是認證管理員（design-windows §12.11），測試、DEBUG 放記憶體</summary>
public interface ISecretStore
{
    string? Read(string account);
    void Save(string account, string value);
    void Delete(string account);
}

public enum EnterpriseFailure { InvalidUrl, NotShiyin, ServerTooOld, AppTooOld, Unreachable, Untrusted, LoginExpired, Forbidden, Denied, Server, Cancelled }

/// <summary>企業伺服器的錯誤；ServerKind 是伺服器的 error.kind（busy、not_found…）</summary>
public sealed class EnterpriseException(EnterpriseFailure kind, string message, string? serverKind = null) : Exception(message)
{
    public EnterpriseFailure Kind { get; } = kind;
    public string? ServerKind { get; } = serverKind;

    public static EnterpriseException Of(EnterpriseFailure kind, string? detail = null) => new(kind, kind switch
    {
        EnterpriseFailure.InvalidUrl => L.T("網址不對：要以 https:// 開頭，例如 https://shiyin.example.com"),
        EnterpriseFailure.NotShiyin => L.T("這個網址不是拾音的企業伺服器"),
        EnterpriseFailure.ServerTooOld => L.T("企業伺服器的版本太舊，請通知公司的管理者更新"),
        EnterpriseFailure.AppTooOld => L.T("拾音的版本太舊，請先更新拾音"),
        EnterpriseFailure.Unreachable => L.F("連不到企業伺服器（%@）。請確認網址，在公司外面要先連 VPN。", detail ?? ""),
        EnterpriseFailure.Untrusted => L.T("企業伺服器的憑證不受這台電腦信任，請公司的管理者協助安裝憑證"),
        EnterpriseFailure.LoginExpired => L.T("企業伺服器的登入已失效，請重新登入"),
        EnterpriseFailure.Cancelled => L.T("已取消"),
        _ => detail ?? "",
    });

    /// <summary>設定問題（登入失效、帳號被停用）：工作停著等，不算失敗（R49.4、R51.3）</summary>
    public bool IsSetupProblem => Kind is EnterpriseFailure.LoginExpired or EnterpriseFailure.Forbidden or EnterpriseFailure.Denied
        or EnterpriseFailure.NotShiyin or EnterpriseFailure.ServerTooOld or EnterpriseFailure.AppTooOld or EnterpriseFailure.InvalidUrl
        or EnterpriseFailure.Untrusted;

    /// <summary>暫時的問題：連不上、伺服器忙、5xx（R50.5：等著、自動再試）</summary>
    public bool IsTemporary => Kind == EnterpriseFailure.Unreachable || (Kind == EnterpriseFailure.Server && ServerKind is "busy" or "server_error");
}

/// <summary>
/// 企業伺服器（M§36、R49～R52、design-windows §12.11）：網址、登入（伺服器的登入頁＋OAuth 授權碼＋PKCE，D121）、換 token、呼叫 API。
/// 網址和帳號（不含密鑰）存設定，有帳號就算登入了；refresh token 只存在認證管理員（R49.6）；access token 只放記憶體。
/// Windows 的登入頁用預設瀏覽器打開，回來走 shiyin://auth（App 把網址交給 <see cref="CompleteSignIn"/>）
/// </summary>
public sealed class EnterpriseServer : IEnterpriseAccess
{
    public const string UrlKey = "enterpriseServerURL";
    public const string AccountKey = "enterpriseServerAccount";
    public const string CredentialAccount = "enterpriseServer";
    public const int ApiVersion = 1;
    public const string RedirectUri = "shiyin://auth";
    public static readonly TimeSpan AttemptLifetime = TimeSpan.FromMinutes(10);

    readonly AppSettings settings;
    readonly HttpClient http;
    readonly ISecretStore secrets;
    readonly string appVersion;
    readonly string device;
    readonly SemaphoreSlim refreshing = new(1);
    readonly object gate = new();
    string? accessToken;
    DateTime accessExpires = DateTime.MinValue;
    string? expiredMessage;

    /// <summary>登入狀態、帳號改了（在呼叫的執行緒上觸發）</summary>
    public event Action? Changed;

    /// <summary>DEBUG 可以用本機的 http（R49.2）</summary>
    public bool AllowLocalHttp { get; init; }

    /// <summary>公司詞彙表（R53）；App 給 SHIYIN_HOME 裡的檔案</summary>
    public CompanyGlossary Glossary { get; init; } = new(null);

    /// <summary>http 要用 <see cref="NoRedirectClient"/>：企業伺服器的請求不跟著轉址（M§36.2、D126）</summary>
    public EnterpriseServer(AppSettings settings, HttpClient http, ISecretStore secrets, string appVersion, string device)
    {
        this.settings = settings;
        this.http = http;
        this.secrets = secrets;
        this.appVersion = appVersion;
        this.device = device;
    }

    public EnterpriseAccount? Account
    {
        get
        {
            if (settings.String(AccountKey) is not { Length: > 0 } json) return null;
            try
            {
                var o = JsonNode.Parse(json) as JsonObject;
                return o is null ? null : new EnterpriseAccount(Str(o, "email"), Str(o, "name"), Str(o, "serverName"), Str(o, "engine"),
                    o["llm"]?.GetValue<bool>() ?? false, o["serverVersion"]?.GetValue<string>());
            }
            catch (JsonException) { return null; }
        }
    }

    public bool HasAccount => Account is not null;
    public bool IsSignedIn { get { lock (gate) return HasAccount && expiredMessage is null; } }
    /// <summary>登入失效的說明；沒有失效是 null</summary>
    public string? ExpiredMessage { get { lock (gate) return HasAccount ? expiredMessage : null; } }
    public Uri? SavedUrl => Normalized(settings.String(UrlKey) ?? "", AllowLocalHttp);
    /// <summary>隱私說明用的主機名稱（M§36.5）；國際化網域是 punycode</summary>
    public string Host => SavedUrl?.IdnHost ?? "";
    public string Language => L.English ? "en" : "zh-TW";

    // IEnterpriseAccess（整理工具，R51）
    public string? BaseUrl => HasAccount ? SavedUrl?.ToString().TrimEnd('/') : null;
    public HttpClient Http => http;
    public bool LlmReady => IsSignedIn && Account?.Llm == true;

    async Task<string> IEnterpriseAccess.AccessToken(CancellationToken cancel)
    {
        try { return await AccessToken(cancel); }
        catch (EnterpriseException e) when (e.IsSetupProblem) { throw LLMException.NotConfigured(e.Message); }
        catch (EnterpriseException e) { throw LLMException.Unreachable(e.Message); }
    }

    static string Str(JsonObject o, string key) => o[key]?.GetValue<string>() ?? "";

    /// <summary>
    /// 伺服器的回應型別不對（例如 apiVersion 是字串、transcription 不是物件）時 GetValue、索引子丟出的例外：
    /// 加入連結可以指到任何網站，接住當成不是拾音的伺服器，不能讓 app 當掉（design-windows §12.11）
    /// </summary>
    static bool IsMalformed(Exception e) => e is JsonException or InvalidOperationException or FormatException or ArgumentException;

    /// <summary>企業伺服器專用的 HttpClient（M§36.2、D126）：不跟著轉址。跟著 307、308 轉到別的主機時，
    /// Authorization 會拿掉，但 body（refresh token、錄音、逐字稿）照樣送過去。下載模型、更新用的共用 HttpClient 不變</summary>
    public static HttpClient NoRedirectClient() =>
        new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(20) };

    // MARK: 網址

    /// <summary>
    /// 只接受 https；DEBUG 可以用本機的 http。去掉結尾的 /，貼了 /api/v1/info 之類的也去掉。
    /// 有帳號密碼（user:pass@）的不接受（加入連結用來假裝成別的網站，M§36.2）
    /// </summary>
    public static Uri? Normalized(string text, bool allowLocalHttp = false)
    {
        var value = text.Trim();
        if (value.Length == 0) return null;
        if (!value.Contains("://", StringComparison.Ordinal)) value = "https://" + value;
        while (value.EndsWith('/')) value = value[..^1];
        foreach (var suffix in new[] { "/api/v1/info", "/api/v1", "/admin", "/login" })
            if (value.EndsWith(suffix, StringComparison.Ordinal)) value = value[..^suffix.Length];
        if (!Uri.TryCreate(value + "/", UriKind.Absolute, out var url) || url.Host.Length == 0 || url.UserInfo.Length > 0) return null;
        if (url.Scheme == "https") return url;
        if (allowLocalHttp && url.Scheme == "http" && url.Host is "127.0.0.1" or "localhost" or "[::1]") return url;
        return null;
    }

    /// <summary>給人看的主機名稱：國際化網域是 punycode（xn--…），長得像別的網站的字元騙不了人</summary>
    public static string DisplayHost(Uri url) => url.IdnHost;

    /// <summary>同一台伺服器（不管結尾的斜線、大小寫，同 Mac）</summary>
    public static bool SameServer(Uri a, Uri b) =>
        string.Equals(a.ToString().TrimEnd('/'), b.ToString().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>確認那是拾音的伺服器、版本相容（R49.1、R49.2）；型別不對也是「不是拾音的企業伺服器」</summary>
    public async Task<EnterpriseInfo> FetchInfo(Uri url, CancellationToken cancel = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url, "api/v1/info"));
        request.Headers.AcceptLanguage.ParseAdd(Language);
        var (status, body, _) = await Send(request, TimeSpan.FromSeconds(15), cancel);
        EnterpriseInfo? info = null;
        if (status == 200)
        {
            try
            {
                if (JsonNode.Parse(body) is JsonObject o && o["server"]?.GetValue<string>() == "shiyin")
                    info = new EnterpriseInfo(Str(o, "name"), Str(o, "version"), o["apiVersion"]?.GetValue<int>() ?? 0, o["minAppApi"]?.GetValue<int>() ?? 0,
                        o["maxAudioSeconds"]?.GetValue<int>(), o["transcription"]?["engine"]?.GetValue<string>() ?? "breeze", o["llm"]?.GetValue<bool>() ?? false);
            }
            catch (Exception e) when (IsMalformed(e)) { }
        }
        if (info is null) throw EnterpriseException.Of(EnterpriseFailure.NotShiyin);
        if (info.ApiVersion < ApiVersion) throw EnterpriseException.Of(EnterpriseFailure.ServerTooOld);
        if (info.MinAppApi > ApiVersion) throw EnterpriseException.Of(EnterpriseFailure.AppTooOld);
        return info;
    }

    // MARK: 登入（M§36.2）

    /// <summary>一次登入：PKCE 的 verifier 和 state 只放記憶體，10 分鐘有效</summary>
    public sealed record Attempt(Uri Base, EnterpriseInfo Info, string Verifier, string State, DateTime Expires)
    {
        public Uri LoginUrl
        {
            get
            {
                var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(Verifier)));
                var query = string.Join("&", new[]
                {
                    ("client_id", "shiyin-app"), ("redirect_uri", RedirectUri), ("state", State),
                    ("code_challenge", challenge), ("code_challenge_method", "S256"),
                }.Select(p => p.Item1 + "=" + Uri.EscapeDataString(p.Item2)));
                return new Uri(Base, "login?" + query);
            }
        }
    }

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    static string Random32() => Base64Url(RandomNumberGenerator.GetBytes(32));

    Attempt? pending;
    /// <summary>還在等瀏覽器登入的登入頁網址（DEBUG 的控制通道用：虛擬機沒有瀏覽器時，從外面走登入頁，再把 shiyin://auth 交回來）</summary>
    public Uri? PendingLoginUrl { get { lock (gate) return pending?.LoginUrl; } }

    /// <summary>第一步：檢查網址，準備好登入頁的網址（App 用預設瀏覽器打開）</summary>
    public async Task<Attempt> BeginSignIn(string text, CancellationToken cancel = default)
    {
        if (Normalized(text, AllowLocalHttp) is not { } url) throw EnterpriseException.Of(EnterpriseFailure.InvalidUrl);
        var info = await FetchInfo(url, cancel);
        var attempt = new Attempt(url, info, Random32() + Random32(), Random32(), DateTime.UtcNow + AttemptLifetime);
        lock (gate) pending = attempt;
        return attempt;
    }

    /// <summary>第二步：登入頁轉回 shiyin://auth?code=…&amp;state=…（App 收到網址時呼叫）。state 不是這次發的（網頁裡的連結、舊的登入）就忽略</summary>
    public async Task CompleteSignIn(Uri callback, CancellationToken cancel = default)
    {
        var query = ParseQuery(callback.Query);
        Attempt? attempt;
        lock (gate) attempt = pending;
        if (attempt is null || attempt.Expires < DateTime.UtcNow || query.GetValueOrDefault("state") != attempt.State)
            throw EnterpriseException.Of(EnterpriseFailure.Cancelled);
        lock (gate) pending = null;
        if (query.ContainsKey("error"))
            throw new EnterpriseException(EnterpriseFailure.Denied, query.GetValueOrDefault("error_description") ?? L.T("企業伺服器拒絕了這次登入"));
        if (query.GetValueOrDefault("code") is not { Length: > 0 } code) throw EnterpriseException.Of(EnterpriseFailure.Cancelled);
        var tokens = await TokenRequest(attempt.Base, new JsonObject
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["code_verifier"] = attempt.Verifier,
            ["device"] = device, ["platform"] = "windows", ["appVersion"] = appVersion,
        }, cancel);
        // 加入連結換到另一台（R49.7、M§36.2）：新的登入成功了才登出舊的。先拿舊伺服器的 access token
        //（同時在換的 token 也等它換完，不然換完會把舊伺服器的 refresh token 寫回去），存好新的登入之後再撤銷
        (Uri Url, string? Token)? previous = null;
        if (HasAccount && SavedUrl is { } old && !SameServer(old, attempt.Base))
        {
            string? token = null;
            try { token = await AccessToken(cancel); }
            catch (EnterpriseException) { }
            previous = (old, token);
        }
        secrets.Save(CredentialAccount, tokens.Refresh);
        settings.Set(UrlKey, attempt.Base.ToString().TrimEnd('/'));
        SaveAccount(tokens.Email, tokens.Name, attempt.Info);
        lock (gate)
        {
            accessToken = tokens.Access;
            accessExpires = DateTime.UtcNow + TimeSpan.FromSeconds(tokens.ExpiresIn - 60);
            expiredMessage = null;
        }
        Log.Info("enterprise server signed in");
        if (previous is { } p)
        {
            // 舊伺服器那邊撤銷這台電腦（送不出去也照樣換）；舊公司的詞彙表不能留給新的公司用
            if (p.Token is { } token) await Revoke(p.Url, token, cancel);
            Glossary.Clear();
            // 新的伺服器沒有提供整理：整理工具原本是「公司」的改成還沒設定（同登出，R49.5）
            if (!attempt.Info.Llm && settings.ChosenProvider == LLMProvider.Enterprise) settings.Remove(AppSettings.ProviderKey);
            Log.Info("enterprise server switched");
        }
        Changed?.Invoke();
        await RefreshGlossary(cancel);
    }

    /// <summary>重新讀伺服器的資訊（R51.5、M§36.3）：公司在後台加了模型、改了名稱或轉錄引擎；連不上就保留舊的。不讀認證管理員</summary>
    public async Task RefreshInfo(CancellationToken cancel = default)
    {
        if (SavedUrl is not { } url || Account is not { } before) return;
        EnterpriseInfo info;
        try { info = await FetchInfo(url, cancel); }
        catch (Exception e) when (e is EnterpriseException or HttpRequestException or OperationCanceledException) { return; }
        // 讀的時候登出、換了帳號或伺服器：不要蓋掉
        if (Account is not { } current || current.Email != before.Email || SavedUrl is not { } saved || !SameServer(saved, url)) return;
        if (current.ServerName != info.Name || current.Engine != info.Engine || current.Llm != info.Llm || current.ServerVersion != info.Version)
        {
            SaveAccount(current.Email, current.Name, info);
            Changed?.Invoke();
            settings.NotifySummarizerChanged();
        }
        await RefreshGlossary(cancel);
    }

    /// <summary>公司詞彙表（R53.1）：舊的伺服器回 404 就是沒有；連不上、登入失效保留上次的</summary>
    public async Task RefreshGlossary(CancellationToken cancel = default)
    {
        if (!IsSignedIn) return;
        try
        {
            var (status, body, _) = await Call(HttpMethod.Get, "api/v1/glossary", timeout: TimeSpan.FromSeconds(15), cancel: cancel);
            if (status == 404) { Glossary.Save([], null); return; }
            if (status != 200 || JsonNode.Parse(body) is not JsonObject o || o["terms"] is not JsonArray terms) return;
            Glossary.Save(terms.Select(t => t?.GetValue<string>()).OfType<string>(), o["updatedAt"]?.GetValue<string>());
        }
        catch (Exception e) when (e is EnterpriseException or HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException) { }
    }

    /// <summary>shiyin://join?server=… 是不是加入連結（R49.7）</summary>
    public static bool IsJoin(string url) =>
        url.StartsWith("shiyin://join", StringComparison.OrdinalIgnoreCase) || url.StartsWith("shiyin:join", StringComparison.OrdinalIgnoreCase);

    /// <summary>加入連結裡的伺服器網址（還沒檢查）</summary>
    public static string JoinServer(string url)
    {
        var query = url.IndexOf('?') is var i and >= 0 ? url[(i + 1)..] : "";
        return ParseQuery(query).GetValueOrDefault("server") ?? "";
    }

    void SaveAccount(string email, string name, EnterpriseInfo info) =>
        settings.Set(AccountKey, new JsonObject
        {
            ["email"] = email, ["name"] = name, ["serverName"] = info.Name, ["engine"] = info.Engine, ["llm"] = info.Llm,
            ["serverVersion"] = info.Version,
        }.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

    /// <summary>shiyin://auth 的網址是不是登入的回呼（App 判斷要不要交給 CompleteSignIn）</summary>
    public static bool IsCallback(string url) =>
        url.StartsWith("shiyin://auth", StringComparison.OrdinalIgnoreCase) || url.StartsWith("shiyin:auth", StringComparison.OrdinalIgnoreCase);

    static Dictionary<string, string> ParseQuery(string query) =>
        query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2).GroupBy(p => Uri.UnescapeDataString(p[0]))
            .ToDictionary(g => g.Key, g => Uri.UnescapeDataString(g.First()[1].Replace('+', ' ')));

    /// <summary>登出（R49.5）：伺服器那邊撤銷這台電腦（送不出去也照樣清掉本機的），轉錄、整理改回本機的</summary>
    public async Task SignOut(CancellationToken cancel = default)
    {
        if (SavedUrl is { } url)
        {
            try { await Revoke(url, await AccessToken(cancel), cancel); }
            catch (EnterpriseException) { }
        }
        ForgetLogin();
        Glossary.Clear();
        settings.Remove(AccountKey);
        lock (gate) expiredMessage = null;
        if (settings.TranscriptionEngine == "server") settings.Remove(AppSettings.TranscriptionEngineKey);
        if (settings.ChosenProvider == LLMProvider.Enterprise) settings.Remove(AppSettings.ProviderKey);
        Log.Info("enterprise server signed out");
        Changed?.Invoke();
        settings.NotifySummarizerChanged();
    }

    /// <summary>撤銷這台電腦在伺服器上的登入（POST /api/v1/auth/logout）；送不出去不管</summary>
    async Task Revoke(Uri url, string token, CancellationToken cancel)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(url, "api/v1/auth/logout"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            await Send(request, TimeSpan.FromSeconds(10), cancel);
        }
        catch (EnterpriseException) { }
    }

    void ForgetLogin()
    {
        secrets.Delete(CredentialAccount);
        lock (gate)
        {
            accessToken = null;
            accessExpires = DateTime.MinValue;
        }
    }

    public void InvalidateAccess()
    {
        lock (gate)
        {
            accessToken = null;
            accessExpires = DateTime.MinValue;
        }
    }

    void MarkExpired(string? message, bool forget)
    {
        if (forget) ForgetLogin();
        lock (gate) expiredMessage = message ?? L.T("企業伺服器的登入已失效，請重新登入");
        Log.Info("enterprise server login expired");
        Changed?.Invoke();
        settings.NotifySummarizerChanged();
    }

    sealed record Tokens(string Access, int ExpiresIn, string Refresh, string Email, string Name);

    async Task<Tokens> TokenRequest(Uri baseUrl, JsonObject body, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUrl, "api/v1/auth/token"))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.AcceptLanguage.ParseAdd(Language);
        var (status, text, _) = await Send(request, TimeSpan.FromSeconds(20), cancel);
        if (status != 200) throw Failure(status, text);
        // 型別不對、少了 token（同 Mac）：不是拾音的伺服器
        try
        {
            if (JsonNode.Parse(text) is JsonObject o && Str(o, "access_token") is { Length: > 0 } access && Str(o, "refresh_token") is { Length: > 0 } refresh)
                return new Tokens(access, o["expires_in"]?.GetValue<int>() ?? 3600, refresh,
                    o["user"]?["email"]?.GetValue<string>() ?? "", o["user"]?["name"]?.GetValue<string>() ?? "");
        }
        catch (Exception e) when (IsMalformed(e)) { }
        throw EnterpriseException.Of(EnterpriseFailure.NotShiyin);
    }

    /// <summary>還有效的 access token；過期了用 refresh token 換（同時只換一次：refresh token 換過就作廢）</summary>
    public async Task<string> AccessToken(CancellationToken cancel = default)
    {
        if (!HasAccount || SavedUrl is not { } url) throw EnterpriseException.Of(EnterpriseFailure.LoginExpired);
        lock (gate) if (accessToken is not null && accessExpires > DateTime.UtcNow) return accessToken;
        await refreshing.WaitAsync(cancel);
        try
        {
            lock (gate) if (accessToken is not null && accessExpires > DateTime.UtcNow) return accessToken;
            string? refresh;
            try { refresh = secrets.Read(CredentialAccount); }
            catch (Exception e) when (e is not OperationCanceledException) { refresh = null; }
            if (string.IsNullOrEmpty(refresh))
            {
                // 讀不到（沒有、系統拒絕）：要重新登入，但不刪認證管理員的項目
                MarkExpired(null, forget: false);
                throw EnterpriseException.Of(EnterpriseFailure.LoginExpired);
            }
            try
            {
                var tokens = await TokenRequest(url, new JsonObject { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh, ["appVersion"] = appVersion }, cancel);
                secrets.Save(CredentialAccount, tokens.Refresh);
                bool wasExpired;
                lock (gate)
                {
                    accessToken = tokens.Access;
                    accessExpires = DateTime.UtcNow + TimeSpan.FromSeconds(tokens.ExpiresIn - 60);
                    wasExpired = expiredMessage is not null;
                    expiredMessage = null;
                }
                if (wasExpired) Changed?.Invoke();
                return tokens.Access;
            }
            catch (EnterpriseException e) when (e.Kind is EnterpriseFailure.LoginExpired or EnterpriseFailure.Forbidden)
            {
                // 伺服器說登入失效、帳號被停用：清掉這台的登入，等使用者重新登入（R49.4）
                MarkExpired(e.Kind == EnterpriseFailure.Forbidden ? e.Message : null, forget: true);
                throw;
            }
        }
        finally { refreshing.Release(); }
    }

    // MARK: API

    /// <summary>呼叫 API：帶 access token，401 時換一次 token 再送（R49.4）。回傳狀態碼、內容、回應的標頭</summary>
    public async Task<(int Status, string Body, HttpResponseHeaders Headers)> Call(HttpMethod method, string path, HttpContent? content = null,
        IReadOnlyDictionary<string, string>? headers = null, TimeSpan? timeout = null, CancellationToken cancel = default)
    {
        if (SavedUrl is not { } url) throw EnterpriseException.Of(EnterpriseFailure.LoginExpired);
        // 中途換了伺服器（加入連結，M§36.2）：新伺服器的 token 不能送到舊的伺服器、舊伺服器的回應不能動到新的登入；
        // 當成工作不在了（轉錄在新的伺服器重建）
        bool Switched() => SavedUrl is not { } now || !SameServer(now, url);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var token = await AccessToken(cancel);
            if (Switched()) throw new EnterpriseException(EnterpriseFailure.Server, "", "not_found");
            using var request = new HttpRequestMessage(method, new Uri(url, path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.AcceptLanguage.ParseAdd(Language);
            foreach (var (name, value) in headers ?? new Dictionary<string, string>()) request.Headers.TryAddWithoutValidation(name, value);
            if (content is not null) request.Content = attempt == 0 ? content : await Clone(content);
            var (status, body, responseHeaders) = await Send(request, timeout ?? TimeSpan.FromSeconds(60), cancel);
            if (Switched()) throw new EnterpriseException(EnterpriseFailure.Server, "", "not_found");
            if (status == 401 && attempt == 0) { InvalidateAccess(); continue; }
            if (status == 401) { MarkExpired(null, forget: true); throw EnterpriseException.Of(EnterpriseFailure.LoginExpired); }
            if (status == 403)
            {
                var failure = Failure(status, body);
                MarkExpired(failure.Message, forget: true);
                throw failure;
            }
            return (status, body, responseHeaders);
        }
        throw EnterpriseException.Of(EnterpriseFailure.LoginExpired);
    }

    static async Task<HttpContent> Clone(HttpContent content)
    {
        var bytes = await content.ReadAsByteArrayAsync();
        var copy = new ByteArrayContent(bytes);
        foreach (var h in content.Headers) copy.Headers.TryAddWithoutValidation(h.Key, h.Value);
        return copy;
    }

    /// <summary>送出請求；連不上、逾時、憑證不受信任換成 EnterpriseException；伺服器要轉址（3xx）是錯誤（D126）</summary>
    async Task<(int, string, HttpResponseHeaders)> Send(HttpRequestMessage request, TimeSpan timeout, CancellationToken cancel)
    {
        using var limit = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, limit.Token);
        var target = request.RequestUri;
        try
        {
            using var response = await http.SendAsync(request, linked.Token);
            // 轉址：NoRedirectClient 會原樣交回 3xx；給錯 HttpClient、已經跟著轉了的（RequestUri 換了）也當成錯誤
            if ((int)response.StatusCode is >= 300 and < 400 || request.RequestUri != target) throw Redirected((int)response.StatusCode);
            var body = request.Method == HttpMethod.Head ? "" : await response.Content.ReadAsStringAsync(linked.Token);
            return ((int)response.StatusCode, body, response.Headers);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw EnterpriseException.Of(EnterpriseFailure.Unreachable, L.T("逾時")); }
        catch (HttpRequestException e) when (e.InnerException is System.Security.Authentication.AuthenticationException)
        {
            throw EnterpriseException.Of(EnterpriseFailure.Untrusted);
        }
        catch (HttpRequestException e) { throw EnterpriseException.Of(EnterpriseFailure.Unreachable, e.HttpRequestError.ToString()); }
    }

    /// <summary>伺服器要轉到別的網址：不跟著轉（D126），不是暫時的問題（轉錄的工作算失敗）</summary>
    public static EnterpriseException Redirected(int status) => new(EnterpriseFailure.Server,
        L.F("企業伺服器要求轉到別的網址（HTTP %lld），拾音不會跟著轉。請確認網址，或請公司的管理者檢查伺服器的設定。", (long)status), "redirect");

    /// <summary>伺服器的錯誤 {"error": {"kind", "message"}}（M§36.2）；kind、message 不是字串就當成沒有</summary>
    public static EnterpriseException Failure(int status, string body)
    {
        string? kind = null, message = null;
        try
        {
            var error = (JsonNode.Parse(body) as JsonObject)?["error"] as JsonObject;
            kind = error?["kind"] is JsonValue kv && kv.TryGetValue<string>(out var k) ? k : null;
            message = error?["message"] is JsonValue mv && mv.TryGetValue<string>(out var m) ? m : null;
        }
        catch (Exception e) when (IsMalformed(e)) { }
        kind ??= status >= 500 ? "server_error" : "bad_request";
        message ??= L.F("企業伺服器回傳錯誤（HTTP %lld）", status);
        if (status == 401 || kind == "unauthorized") return EnterpriseException.Of(EnterpriseFailure.LoginExpired);
        if (status == 403 || kind == "forbidden") return new EnterpriseException(EnterpriseFailure.Forbidden, message, kind);
        return new EnterpriseException(EnterpriseFailure.Server, message, kind);
    }

    // MARK: DEBUG

    /// <summary>控制通道的 enterprise login：伺服器開發用的登入（不經過瀏覽器），走一樣的 CompleteSignIn</summary>
    public async Task DebugDevSignIn(string text, string email, CancellationToken cancel = default)
    {
        var attempt = await BeginSignIn(text, cancel);
        using (var form = new HttpRequestMessage(HttpMethod.Get, attempt.LoginUrl)) await Send(form, TimeSpan.FromSeconds(15), cancel);
        var challenge = ParseQuery(attempt.LoginUrl.Query)["code_challenge"];
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(attempt.Base, "login/dev"))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["email"] = email, ["state"] = attempt.State, ["challenge"] = challenge }),
        };
        var (_, html, _) = await Send(request, TimeSpan.FromSeconds(15), cancel);
        var match = Regex.Match(html, "window\\.location\\.replace\\((\"[^\"]+\")\\)");
        if (!match.Success) throw EnterpriseException.Of(EnterpriseFailure.NotShiyin);
        var callback = JsonSerializer.Deserialize<string>(match.Groups[1].Value)!;
        await CompleteSignIn(new Uri(callback), cancel);
    }
}
