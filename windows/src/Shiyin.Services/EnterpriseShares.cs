using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shiyin.Localization;

namespace Shiyin.Services;

/// <summary>
/// 透過企業伺服器分享會議（R125、M§99、design-windows §11.58，同 Mac 的 EnterpriseShares）：M§99.2 的端點、上傳和下載的續傳、伺服器回應的檢查。
/// 名單、連結、期限只存在伺服器（D212），app 只記「這場分享過」的 id（meeting_shares）
/// </summary>
public sealed class EnterpriseShares(EnterpriseServer server)
{
    /// <summary>info 的 sharing（M§99.2）：關著、舊的伺服器沒有這段（解析成 null）。MaxDays 是 null 時可以不限期</summary>
    public sealed record Sharing(bool Enabled, bool PublicLinks, int? MaxDays, long? MaxBytes);

    /// <summary>要帶的內容（R125.2）：會議資訊、逐字稿、說話者名字、會議紀錄、重點標記一定帶</summary>
    public sealed record Contents(bool Audio = true, bool UserNotes = true, bool Documents = true);

    public sealed record MeetingInfo(string Title, string MeetingDate, int? DurationSeconds, string Language);

    public sealed record Person(string Email, string? Name)
    {
        public string Display => Name is { Length: > 0 } name ? name : Email;
    }

    public sealed record Recipient(string Email, string? Name, string? OpenedAt, string? ImportedAt, int? ImportedVersion);

    /// <summary>Access：company | public</summary>
    public sealed record Link(string Url, string Access);

    public sealed record Detail(string Id, string Status, int Version, string? ExpiresAt, MeetingInfo? Meeting, Contents Contents, long Bytes, bool Uploading,
        IReadOnlyList<Recipient> Recipients, Link? Link);

    public sealed record SharedItem(string Id, Person From, MeetingInfo? Meeting, Contents Contents, long Bytes, int Version, string UpdatedAt, string? ExpiresAt,
        int? ImportedVersion)
    {
        /// <summary>沒匯入過、或分享者更新過（R125.5）</summary>
        public bool IsNew => ImportedVersion is int imported ? Version > imported : true;
        public string Title => Meeting is { Title.Length: > 0 } m ? m.Title : L.T("未命名的會議");
    }

    public sealed record Summary(Person From, MeetingInfo? Meeting, Contents Contents, long Bytes, int Version, string? ExpiresAt);

    /// <summary>shiyin://share?server=…&amp;id=…&amp;t=…（網頁的「用拾音打開」，R125.6）</summary>
    public sealed record ShareLink(Uri Server, string Id, string? Token);

    public const int ChunkSize = 8 * 1024 * 1024;
    /// <summary>名單最多幾個人（M§99.2：伺服器一場最多 500 人，回應超過時截斷）</summary>
    public const int MaxRecipients = 500;

    // MARK: 請求的內容、回應的檢查（純函式，標準答案 server/shares.json）

    public static JsonObject CreateBody(MeetingInfo meeting, Contents contents, long bytes, string sha256, int? expiresInDays) => new()
    {
        ["meeting"] = new JsonObject
        {
            ["title"] = meeting.Title, ["meetingDate"] = meeting.MeetingDate, ["language"] = meeting.Language, ["durationSeconds"] = meeting.DurationSeconds,
        },
        ["contents"] = new JsonObject { ["audio"] = contents.Audio, ["userNotes"] = contents.UserNotes, ["documents"] = contents.Documents },
        ["package"] = new JsonObject { ["bytes"] = bytes, ["sha256"] = sha256 },
        ["expiresInDays"] = expiresInDays,
    };

    public static Sharing? ParseSharing(JsonNode? value)
    {
        if (value is not JsonObject json || AsBool(json["enabled"]) != true) return null;
        return new Sharing(true, AsBool(json["publicLinks"]) ?? false, (int?)Positive(json["maxDays"]), Positive(json["maxBytes"]));
    }

    /// <summary>正的整數（不是數字、小數、負的、0 是 null；true、false 不算數字）</summary>
    public static long? Positive(JsonNode? value)
    {
        if (value is not JsonValue v || v.GetValueKind() != JsonValueKind.Number || !v.TryGetValue<double>(out var d)) return null;
        if (!double.IsFinite(d) || d < 1 || d > 9_007_199_254_740_992 || d != Math.Floor(d)) return null;
        return (long)d;
    }

    /// <summary>true、false；數字 0、1 也算（同 Mac 的 NSNumber as? Bool）；其他是 null</summary>
    public static bool? AsBool(JsonNode? value)
    {
        if (value is not JsonValue v) return null;
        switch (v.GetValueKind())
        {
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.Number when v.TryGetValue<double>(out var d) && d is 0 or 1: return d == 1;
            default: return null;
        }
    }

    public static string? Str(JsonNode? value) => value is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    public static MeetingInfo? ParseMeeting(JsonNode? value)
    {
        if (value is not JsonObject json) return null;
        return new MeetingInfo(Str(json["title"]) ?? "", Str(json["meetingDate"]) ?? "", (int?)Positive(json["durationSeconds"]), Str(json["language"]) ?? "zh");
    }

    public static Contents ParseContents(JsonNode? value)
    {
        var json = value as JsonObject;
        return new Contents(AsBool(json?["audio"]) ?? true, AsBool(json?["userNotes"]) ?? true, AsBool(json?["documents"]) ?? true);
    }

    public static Person? ParsePerson(JsonNode? value) =>
        value is JsonObject json && Str(json["email"]) is { Length: > 0 } email ? new Person(email, Str(json["name"])) : null;

    public static List<Recipient> ParseRecipients(JsonNode? value) =>
        (value as JsonArray ?? []).Take(MaxRecipients).Select(item =>
            item is JsonObject json && Str(json["email"]) is { Length: > 0 } email
                ? new Recipient(email, Str(json["name"]), Str(json["openedAt"]), Str(json["importedAt"]), (int?)Positive(json["importedVersion"]))
                : null).OfType<Recipient>().ToList();

    public static Link? ParseLink(JsonNode? value)
    {
        if (value is not JsonObject json || Str(json["url"]) is not { } url
            || !(url.StartsWith("https://", StringComparison.Ordinal) || url.StartsWith("http://", StringComparison.Ordinal))
            || Str(json["access"]) is not { } access || access is not ("company" or "public")) return null;
        return new Link(url, access);
    }

    /// <summary>GET /api/v1/shares/&lt;id&gt;：id、status 不對就是 null；version 不合理當成 1、bytes 不合理當成 0</summary>
    public static Detail? ParseDetail(JsonNode? value)
    {
        if (value is not JsonObject json || Str(json["id"]) is not { Length: > 0 } id || Str(json["status"]) is not ("uploading" or "ready")) return null;
        return new Detail(id, Str(json["status"])!, (int?)Positive(json["version"]) ?? 1, Str(json["expiresAt"]), ParseMeeting(json["meeting"]),
            ParseContents(json["contents"]), Positive(json["bytes"]) ?? 0, AsBool(json["uploading"]) ?? false, ParseRecipients(json["recipients"]),
            ParseLink(json["link"]));
    }

    public static List<SharedItem> ParseSharedWithMe(JsonNode? value) =>
        ((value as JsonObject)?["shares"] as JsonArray ?? []).Select(item =>
            item is JsonObject json && Str(json["id"]) is { Length: > 0 } id && ParsePerson(json["from"]) is { } from
                ? new SharedItem(id, from, ParseMeeting(json["meeting"]), ParseContents(json["contents"]), Positive(json["bytes"]) ?? 0,
                    (int?)Positive(json["version"]) ?? 1, Str(json["updatedAt"]) ?? "", Str(json["expiresAt"]), (int?)Positive(json["importedVersion"]))
                : null).OfType<SharedItem>().ToList();

    public static Summary? ParseSummary(JsonNode? value)
    {
        if (value is not JsonObject json || ParsePerson(json["from"]) is not { } from) return null;
        return new Summary(from, ParseMeeting(json["meeting"]), ParseContents(json["contents"]), Positive(json["bytes"]) ?? 0,
            (int?)Positive(json["version"]) ?? 1, Str(json["expiresAt"]));
    }

    /// <summary>shiyin: 後面的那一段（shiyin://share?…、Windows 交過來的 shiyin://share/?…、shiyin:share?…）和查詢字串</summary>
    static string? Target(string url, out string query)
    {
        query = "";
        if (!url.StartsWith("shiyin:", StringComparison.OrdinalIgnoreCase)) return null;
        var rest = url["shiyin:".Length..];
        if (rest.StartsWith("//", StringComparison.Ordinal)) rest = rest[2..];
        var hash = rest.IndexOf('#');
        if (hash >= 0) rest = rest[..hash];
        var mark = rest.IndexOf('?');
        if (mark >= 0) query = rest[(mark + 1)..];
        var target = mark >= 0 ? rest[..mark] : rest;
        var slash = target.IndexOf('/');
        return slash >= 0 ? target[..slash] : target;
    }

    public static bool IsShareLink(string url) => string.Equals(Target(url, out _), "share", StringComparison.OrdinalIgnoreCase);

    /// <summary>server 照 EnterpriseServer.Normalized 檢查、id 是 32 個十六進位字（轉小寫）、t 空的當成沒有</summary>
    public static ShareLink? ParseShareLink(string url, bool allowLocalHttp = false)
    {
        if (!IsShareLink(url)) return null;
        Target(url, out var query);
        var items = new List<(string Name, string? Value)>();
        foreach (var part in query.Split('&'))
        {
            if (part.Length == 0) continue;
            var eq = part.IndexOf('=');
            items.Add(eq < 0 ? (Unescape(part), null) : (Unescape(part[..eq]), Unescape(part[(eq + 1)..])));
        }
        string? First(string name) => items.FirstOrDefault(i => i.Name == name).Value;
        if (First("server") is not { } serverText || EnterpriseServer.Normalized(serverText, allowLocalHttp) is not { } serverUrl) return null;
        if (First("id") is not { Length: 32 } id || !id.All(IsHex)) return null;
        return new ShareLink(serverUrl, id.ToLowerInvariant(), First("t") is { Length: > 0 } token ? token : null);
    }

    static string Unescape(string text)
    {
        try { return Uri.UnescapeDataString(text); }
        catch (UriFormatException) { return text; }
    }

    /// <summary>十六進位的字（含全形，同 Mac 的 Character.isHexDigit）</summary>
    static bool IsHex(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F'
        or >= '０' and <= '９' or >= 'Ａ' and <= 'Ｆ' or >= 'ａ' and <= 'ｆ';

    static bool IsShareId(string? id) => id is { Length: 32 } && id.All(IsHex);

    // MARK: API（M§99.2）

    static EnterpriseException BadResponse() => new(EnterpriseFailure.Server, L.T("企業伺服器的回應不對"), "bad_response");

    static JsonNode? ParseBody(byte[] body)
    {
        if (body.Length == 0) return null;
        try { return JsonNode.Parse(body); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException) { return null; }
    }

    static StringContent JsonContent(JsonNode json) => new(json.ToJsonString(), Encoding.UTF8, "application/json");

    /// <summary>403 是「公司關掉了分享」這類，不當成登入失效；不是 2xx 就是錯誤</summary>
    async Task<(JsonNode? Json, EnterpriseResponse Response)> Request(HttpMethod method, string path, JsonNode? json = null, TimeSpan? timeout = null,
        CancellationToken cancel = default)
    {
        var response = await server.CallRaw(method, path, json is null ? null : JsonContent(json), null, timeout, cancel, expireOnForbidden: false);
        if (response.Status is < 200 or >= 300) throw EnterpriseServer.Failure(response.Status, response.Text);
        return (ParseBody(response.Body), response);
    }

    public async Task<List<Person>> People(string query, CancellationToken cancel = default)
    {
        var (json, _) = await Request(HttpMethod.Get, "api/v1/people?q=" + Uri.EscapeDataString(query), timeout: TimeSpan.FromSeconds(15), cancel: cancel);
        return ((json as JsonObject)?["people"] as JsonArray ?? []).Select(ParsePerson).OfType<Person>().ToList();
    }

    /// <summary>建分享（status uploading），回傳 id</summary>
    public async Task<string> Create(JsonObject body, CancellationToken cancel = default)
    {
        var (json, _) = await Request(HttpMethod.Post, "api/v1/shares", body, cancel: cancel);
        return Str((json as JsonObject)?["id"]) is { } id && IsShareId(id) ? id : throw BadResponse();
    }

    /// <summary>更新內容（R125.4）：建新的版本，接著 Upload</summary>
    public async Task StartVersion(string id, JsonObject body, CancellationToken cancel = default) =>
        await Request(HttpMethod.Post, $"api/v1/shares/{id}/versions", body, cancel: cancel);

    /// <summary>從伺服器收到的地方接著傳，一次 8 MB（同轉錄的錄音上傳，M§36.2 的防線）。SHA-256 不對時從頭傳一次</summary>
    public async Task Upload(string id, string file, long bytes, Action<double> progress, CancellationToken cancel = default)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var head = await server.CallRaw(HttpMethod.Head, $"api/v1/shares/{id}/package", timeout: TimeSpan.FromSeconds(30), cancel: cancel, expireOnForbidden: false);
            if (head.Status != 200) throw EnterpriseServer.Failure(head.Status, "");
            var offset = Offset(head, bytes) ?? 0;
            await using var stream = File.OpenRead(file);
            var buffer = new byte[ChunkSize];
            var chunks = (bytes + ChunkSize - 1) / ChunkSize;
            var (sent, stalled, restart) = (0, 0, false);
            while (offset < bytes)
            {
                cancel.ThrowIfCancellationRequested();
                sent++;
                if (stalled >= 3 || sent > chunks * 2 + 3)
                    throw new EnterpriseException(EnterpriseFailure.Server, L.T("會議一直沒辦法完整傳到企業伺服器"), "upload_stalled");
                stream.Seek(offset, SeekOrigin.Begin);
                var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(ChunkSize, Math.Max(0, stream.Length - offset))), cancel);
                if (read == 0) break;
                var content = new ByteArrayContent(buffer, 0, read);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/offset+octet-stream");
                var response = await server.CallRaw(HttpMethod.Patch, $"api/v1/shares/{id}/package", content,
                    new Dictionary<string, string> { ["Upload-Offset"] = offset.ToString() }, TimeSpan.FromMinutes(5), cancel, expireOnForbidden: false);
                var before = offset;
                switch (response.Status)
                {
                    case 204: offset = Offset(response, bytes) ?? offset + read; break;
                    case 409: offset = Offset(response, bytes) ?? 0; break;
                    case 400 when attempt == 0: restart = true; break;
                    default: throw EnterpriseServer.Failure(response.Status, response.Text);
                }
                if (restart) break;
                stalled = offset > before ? 0 : stalled + 1;
                progress((double)offset / Math.Max(bytes, 1));
            }
            if (!restart) return;
        }
    }

    /// <summary>伺服器回的 Upload-Offset：沒有、不是數字是 null；負的、比檔案大當成 0（從頭傳）</summary>
    public static long? Offset(EnterpriseResponse response, long bytes) =>
        long.TryParse(response.Header("Upload-Offset"), out var value) ? value >= 0 && value <= bytes ? value : 0 : null;

    public async Task<Detail> GetDetail(string id, CancellationToken cancel = default)
    {
        var (json, _) = await Request(HttpMethod.Get, $"api/v1/shares/{id}", timeout: TimeSpan.FromSeconds(20), cancel: cancel);
        return ParseDetail(json) ?? throw BadResponse();
    }

    public async Task<Detail> SetExpiry(string id, int? days, CancellationToken cancel = default)
    {
        var (json, _) = await Request(HttpMethod.Patch, $"api/v1/shares/{id}", new JsonObject { ["expiresInDays"] = days }, cancel: cancel);
        return ParseDetail(json) ?? throw BadResponse();
    }

    public async Task<List<Recipient>> AddRecipients(string id, IReadOnlyList<string> emails, CancellationToken cancel = default)
    {
        var (json, _) = await Request(HttpMethod.Post, $"api/v1/shares/{id}/recipients",
            new JsonObject { ["emails"] = new JsonArray([.. emails.Select(e => (JsonNode)e)]) }, cancel: cancel);
        return ParseRecipients((json as JsonObject)?["recipients"]);
    }

    public async Task RemoveRecipient(string id, string email, CancellationToken cancel = default) =>
        await Request(HttpMethod.Delete, $"api/v1/shares/{id}/recipients/{Uri.EscapeDataString(email)}", cancel: cancel);

    public async Task<Link> SetLink(string id, string access, CancellationToken cancel = default)
    {
        var (json, _) = await Request(HttpMethod.Post, $"api/v1/shares/{id}/link", new JsonObject { ["access"] = access }, cancel: cancel);
        return ParseLink(json) ?? throw BadResponse();
    }

    public async Task DeleteLink(string id, CancellationToken cancel = default) => await Request(HttpMethod.Delete, $"api/v1/shares/{id}/link", cancel: cancel);

    public async Task Stop(string id, CancellationToken cancel = default) => await Request(HttpMethod.Delete, $"api/v1/shares/{id}", cancel: cancel);

    public async Task<List<SharedItem>> SharedWithMe(CancellationToken cancel = default)
    {
        var (json, _) = await Request(HttpMethod.Get, "api/v1/shared-with-me", timeout: TimeSpan.FromSeconds(20), cancel: cancel);
        return ParseSharedWithMe(json);
    }

    public async Task Hide(string id, CancellationToken cancel = default) => await Request(HttpMethod.Delete, $"api/v1/shared-with-me/{id}", cancel: cancel);

    // MARK: 收到的人：摘要、下載、回報匯入（登入的是同一台時帶 token；用連結的只帶代碼）

    /// <summary>連結的請求：登入的是同一台伺服器時走 CallRaw（帶 token），否則直接送（公開的連結不用登入）</summary>
    async Task<EnterpriseResponse> LinkRequest(ShareLink link, HttpMethod method, string path, JsonNode? json = null,
        IReadOnlyDictionary<string, string>? headers = null, TimeSpan? timeout = null, CancellationToken cancel = default)
    {
        var full = path;
        if (link.Token is { } token) full += (path.Contains('?') ? "&" : "?") + "t=" + Uri.EscapeDataString(token);
        if (server.IsSignedIn && server.SavedUrl is { } saved && EnterpriseServer.SameServer(saved, link.Server))
            return await server.CallRaw(method, full, json is null ? null : JsonContent(json), headers, timeout, cancel, expireOnForbidden: false);
        using var request = new HttpRequestMessage(method, new Uri(link.Server, full));
        foreach (var (name, value) in headers ?? new Dictionary<string, string>()) request.Headers.TryAddWithoutValidation(name, value);
        if (json is not null) request.Content = JsonContent(json);
        return await server.SendRaw(request, timeout ?? TimeSpan.FromSeconds(60), cancel);
    }

    public async Task<Summary> GetSummary(ShareLink link, CancellationToken cancel = default)
    {
        var response = await LinkRequest(link, HttpMethod.Get, $"api/v1/shares/{link.Id}/summary", timeout: TimeSpan.FromSeconds(20), cancel: cancel);
        if (response.Status != 200) throw EnterpriseServer.Failure(response.Status, response.Text);
        return ParseSummary(ParseBody(response.Body)) ?? throw BadResponse();
    }

    /// <summary>
    /// 一次 8 MB 用 Range 下載到 file（斷線時重新叫這個函式會接著下載），回傳下載到的版本。每一段都看 X-Shiyin-Share-Version：
    /// 跟 version（檔案是哪一版）不同表示分享者剛更新過，前面下載的不能接，清掉從頭下載（最多重來兩次，同 Mac）
    /// </summary>
    public async Task<int> Download(ShareLink link, string file, long bytes, int version, Action<double> progress, CancellationToken cancel = default)
    {
        await using var stream = new FileStream(file, FileMode.OpenOrCreate, FileAccess.Write);
        var have = stream.Seek(0, SeekOrigin.End);
        var total = bytes;
        var restarts = 0;
        do
        {
            cancel.ThrowIfCancellationRequested();
            var end = total > 0 ? Math.Min(have + ChunkSize, total) - 1 : have + ChunkSize - 1;
            var response = await LinkRequest(link, HttpMethod.Get, $"api/v1/shares/{link.Id}/package",
                headers: new Dictionary<string, string> { ["Range"] = $"bytes={have}-{end}" }, timeout: TimeSpan.FromMinutes(5), cancel: cancel);
            if (int.TryParse(response.Header("X-Shiyin-Share-Version"), out var served) && served != version)
            {
                if (++restarts > 2) throw new EnterpriseException(EnterpriseFailure.Server, L.T("會議沒辦法完整下載"), "download_stalled");
                stream.SetLength(0);
                stream.Seek(0, SeekOrigin.Begin);
                (have, total, version) = (0, 0, served);
                continue;
            }
            switch (response.Status)
            {
                case 206 or 200:
                    if (response.Status == 200)
                    {
                        // 伺服器整個送過來（不支援 Range）：從頭寫
                        stream.SetLength(0);
                        stream.Seek(0, SeekOrigin.Begin);
                        have = 0;
                    }
                    if (response.Header("Content-Range") is { } range && range.LastIndexOf('/') is var slash and >= 0 && long.TryParse(range[(slash + 1)..], out var all))
                        total = all;
                    else if (response.Status == 200) total = response.Body.Length;
                    await stream.WriteAsync(response.Body, cancel);
                    have += response.Body.Length;
                    if (response.Body.Length == 0) throw new EnterpriseException(EnterpriseFailure.Server, L.T("會議沒辦法完整下載"), "download_stalled");
                    break;
                case 416 when total > 0 && have >= total:
                    break;
                default:
                    throw EnterpriseServer.Failure(response.Status, response.Text);
            }
            progress((double)have / Math.Max(total, 1));
        } while (total == 0 || have < total);
        return version;
    }

    /// <summary>回報匯入了（送不出去不管，R125.5 的「已匯入」只是顯示用）</summary>
    public async Task Imported(ShareLink link, int version)
    {
        try { await LinkRequest(link, HttpMethod.Post, $"api/v1/shares/{link.Id}/imported", new JsonObject { ["version"] = version }, timeout: TimeSpan.FromSeconds(15)); }
        catch (Exception e) when (e is EnterpriseException or HttpRequestException or OperationCanceledException) { }
    }
}
