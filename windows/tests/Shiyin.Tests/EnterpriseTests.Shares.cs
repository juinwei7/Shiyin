using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shiyin.Data;
using Shiyin.LLM;
using Shiyin.Model;
using Shiyin.Services;
using Shiyin.Services.Capture;
using Xunit;

namespace Shiyin.Tests;

/// <summary>
/// 透過企業伺服器分享會議（R125、M§99、design-windows §11.58）：標準答案 server/shares.json；
/// 假伺服器照 M§99.2 回應：建分享、續傳、更新版本、名單、連結、與我分享、匯入、用連結打開、伺服器關掉分享、不合理的回應
/// </summary>
public sealed partial class EnterpriseTests
{
    sealed partial class FakeEnterprise
    {
        public sealed class FakeShare
        {
            public string Id = "";
            public string Status = "uploading";
            public int Version = 1;
            public long Expected;
            public string Sha = "";
            public byte[] Received = [];
            public byte[] Package = [];
            public bool Uploading = true;
            public bool NewVersion;
            public JsonNode? Meeting;
            public JsonNode? Contents;
            public string? ExpiresAt;
            public readonly List<string> Recipients = [];
            public readonly Dictionary<string, int> Imported = [];
            public string? LinkAccess;
            public string LinkToken = "tok-" + Guid.NewGuid().ToString("N")[..8];
        }

        /// <summary>info 的 sharing；null 是舊的伺服器、沒開</summary>
        public JsonObject? Sharing;
        /// <summary>公司在後台關掉了分享：分享的端點都回 403</summary>
        public bool SharingOff;
        public readonly Dictionary<string, FakeShare> Shares = [];
        public readonly HashSet<string> Hidden = [];
        /// <summary>第一次 PATCH 回 409、位置 0（斷線後接著傳）</summary>
        public bool ConflictOnce;
        /// <summary>每次 PATCH 先等這麼久（測取消）</summary>
        public int PatchDelayMs;
        /// <summary>建分享回這個 id（不合理的回應）</summary>
        public string? CreateId;
        /// <summary>詳情回這段原文（不合理的回應）</summary>
        public string? DetailBody;
        public string? LastPeopleQuery;

        async Task<bool> HandleShare(HttpListenerContext c, string method, string path, byte[] body, bool authorized)
        {
            if (!path.StartsWith("/api/v1/shares", StringComparison.Ordinal) && !path.StartsWith("/api/v1/shared-with-me", StringComparison.Ordinal)
                && path != "/api/v1/people") return false;
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var share = parts.Length >= 4 && parts[2] == "shares" ? Shares.GetValueOrDefault(parts[3]) : null;
            var rest = parts.Length >= 5 ? parts[4] : "";
            if (!authorized)
            {
                // 公開的連結（代碼對）：摘要、下載、回報匯入
                var byLink = share is { LinkAccess: "public" } && c.Request.QueryString["t"] == share.LinkToken
                             && (method, rest) is ("GET", "summary") or ("GET", "package") or ("POST", "imported");
                if (!byLink) { await Error(c, Disabled ? 403 : 401, Disabled ? "forbidden" : "unauthorized"); return true; }
            }
            else if (SharingOff) { await Error(c, 403, "forbidden", "公司沒有開放分享會議"); return true; }
            if (path == "/api/v1/people")
            {
                LastPeopleQuery = c.Request.QueryString["q"];
                await Json(c, 200, new JsonObject { ["people"] = new JsonArray(new JsonObject { ["email"] = "bob@example.com", ["name"] = "Bob" }, new JsonObject { ["name"] = "沒有信箱" }) });
                return true;
            }
            if (path == "/api/v1/shared-with-me" && method == "GET")
            {
                var list = Shares.Values.Where(s => s.Recipients.Contains("amy@example.com") && !Hidden.Contains(s.Id) && s.Package.Length > 0)
                    .Select(s => (JsonNode)new JsonObject
                    {
                        ["id"] = s.Id, ["from"] = new JsonObject { ["email"] = "bob@example.com", ["name"] = "Bob" }, ["meeting"] = s.Meeting?.DeepClone(),
                        ["contents"] = s.Contents?.DeepClone(), ["bytes"] = s.Package.Length, ["version"] = s.Version, ["updatedAt"] = "2026-11-27T05:00:00Z",
                        ["expiresAt"] = s.ExpiresAt, ["importedVersion"] = s.Imported.TryGetValue("amy@example.com", out var v) ? v : null,
                    });
                await Json(c, 200, new JsonObject { ["shares"] = new JsonArray([.. list]) });
                return true;
            }
            if (parts.Length == 4 && parts[2] == "shared-with-me" && method == "DELETE")
            {
                Hidden.Add(parts[3]);
                await NoContent(c);
                return true;
            }
            if (path == "/api/v1/shares" && method == "POST")
            {
                var o = JsonNode.Parse(body)!.AsObject();
                var id = CreateId ?? Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
                Shares[id] = new FakeShare
                {
                    Id = id, Expected = o["package"]!["bytes"]!.GetValue<long>(), Sha = o["package"]!["sha256"]!.GetValue<string>(),
                    Meeting = o["meeting"]!.DeepClone(), Contents = o["contents"]!.DeepClone(),
                    ExpiresAt = o["expiresInDays"] is JsonValue days ? $"2026-12-{days.GetValue<int>():00}T00:00:00Z" : null,
                };
                await Json(c, 201, new JsonObject { ["id"] = id, ["status"] = "uploading" });
                return true;
            }
            if (share is null) { await Error(c, 404, "not_found", "找不到這個分享"); return true; }
            switch (method, rest)
            {
                case ("GET", ""):
                    if (DetailBody is not null) { await Text(c, 200, DetailBody); return true; }
                    await Json(c, 200, Detail(share));
                    return true;
                case ("PATCH", ""):
                {
                    var o = JsonNode.Parse(body)!.AsObject();
                    share.ExpiresAt = o["expiresInDays"] is JsonValue days ? $"2026-12-{days.GetValue<int>():00}T00:00:00Z" : null;
                    await Json(c, 200, Detail(share));
                    return true;
                }
                case ("DELETE", ""):
                    Shares.Remove(share.Id);
                    await NoContent(c);
                    return true;
                case ("POST", "versions"):
                {
                    var o = JsonNode.Parse(body)!.AsObject();
                    (share.Expected, share.Sha, share.Received, share.Uploading, share.NewVersion) =
                        (o["package"]!["bytes"]!.GetValue<long>(), o["package"]!["sha256"]!.GetValue<string>(), [], true, true);
                    share.Meeting = o["meeting"]!.DeepClone();
                    await Json(c, 200, Detail(share));
                    return true;
                }
                case ("HEAD", "package"):
                    c.Response.Headers["Upload-Offset"] = share.Received.Length.ToString();
                    c.Response.StatusCode = 200;
                    c.Response.Close();
                    return true;
                case ("PATCH", "package"):
                {
                    if (PatchDelayMs > 0) await Task.Delay(PatchDelayMs);
                    var offset = long.Parse(c.Request.Headers["Upload-Offset"]!);
                    if (ConflictOnce || offset != share.Received.Length)
                    {
                        ConflictOnce = false;
                        c.Response.Headers["Upload-Offset"] = share.Received.Length.ToString();
                        await Error(c, 409, "conflict");
                        return true;
                    }
                    share.Received = [.. share.Received, .. body];
                    if (share.Received.Length == share.Expected)
                    {
                        if (Convert.ToHexStringLower(SHA256.HashData(share.Received)) != share.Sha) { share.Received = []; await Error(c, 400, "bad_request"); return true; }
                        share.Package = share.Received;
                        share.Received = [];
                        if (share.NewVersion) share.Version++;
                        (share.Status, share.Uploading, share.NewVersion) = ("ready", false, false);
                    }
                    c.Response.Headers["Upload-Offset"] = (share.Package.Length > 0 && share.Received.Length == 0 ? share.Package.Length : share.Received.Length).ToString();
                    c.Response.StatusCode = 204;
                    c.Response.Close();
                    return true;
                }
                case ("POST", "recipients"):
                {
                    var emails = JsonNode.Parse(body)!["emails"]!.AsArray().Select(e => e!.GetValue<string>()).ToList();
                    var refused = emails.Where(e => !e.EndsWith("@example.com", StringComparison.Ordinal)).ToList();
                    if (refused.Count > 0) { await Error(c, 400, "bad_request", "不能分享給公司以外的信箱：" + string.Join("、", refused)); return true; }
                    foreach (var e in emails) if (!share.Recipients.Contains(e)) share.Recipients.Add(e);
                    await Json(c, 200, new JsonObject { ["recipients"] = RecipientsJson(share) });
                    return true;
                }
                case ("DELETE", "recipients") when parts.Length == 6:
                    share.Recipients.Remove(Uri.UnescapeDataString(parts[5]));
                    await NoContent(c);
                    return true;
                case ("POST", "link"):
                    share.LinkAccess = JsonNode.Parse(body)!["access"]!.GetValue<string>();
                    await Json(c, 200, LinkJson(share)!);
                    return true;
                case ("DELETE", "link"):
                    share.LinkAccess = null;
                    await NoContent(c);
                    return true;
                case ("GET", "summary"):
                    await Json(c, 200, new JsonObject
                    {
                        ["from"] = new JsonObject { ["email"] = "bob@example.com", ["name"] = "Bob" }, ["meeting"] = share.Meeting?.DeepClone(),
                        ["contents"] = share.Contents?.DeepClone(), ["bytes"] = share.Package.Length, ["version"] = share.Version, ["expiresAt"] = share.ExpiresAt,
                    });
                    return true;
                case ("GET", "package"):
                {
                    var range = c.Request.Headers["Range"] ?? "";
                    var match = System.Text.RegularExpressions.Regex.Match(range, @"^bytes=(\d+)-(\d+)$");
                    var total = share.Package.Length;
                    var start = match.Success ? int.Parse(match.Groups[1].Value) : 0;
                    var end = match.Success ? Math.Min(int.Parse(match.Groups[2].Value), total - 1) : total - 1;
                    if (start >= total) { await Error(c, 416, "range_not_satisfiable"); return true; }
                    var slice = share.Package[start..(end + 1)];
                    c.Response.StatusCode = match.Success ? 206 : 200;
                    if (match.Success) c.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{total}";
                    c.Response.Headers["X-Shiyin-Share-Version"] = share.Version.ToString();
                    await c.Response.OutputStream.WriteAsync(slice);
                    c.Response.Close();
                    return true;
                }
                case ("POST", "imported"):
                    share.Imported["amy@example.com"] = JsonNode.Parse(body)!["version"]!.GetValue<int>();
                    await NoContent(c);
                    return true;
            }
            await Error(c, 404, "not_found");
            return true;
        }

        static JsonArray RecipientsJson(FakeShare share) => new([.. share.Recipients.Select(e => (JsonNode)new JsonObject
        {
            ["email"] = e, ["name"] = e == "bob@example.com" ? "Bob" : null, ["openedAt"] = null,
            ["importedAt"] = share.Imported.ContainsKey(e) ? "2026-11-27T05:04:00Z" : null, ["importedVersion"] = share.Imported.TryGetValue(e, out var v) ? v : null,
        })]);

        JsonObject? LinkJson(FakeShare share) => share.LinkAccess is null ? null
            : new JsonObject { ["url"] = $"{Server.Url}/s/{share.LinkToken}", ["access"] = share.LinkAccess };

        JsonObject Detail(FakeShare share) => new()
        {
            ["id"] = share.Id, ["status"] = share.Status, ["version"] = share.Version, ["expiresAt"] = share.ExpiresAt, ["meeting"] = share.Meeting?.DeepClone(),
            ["contents"] = share.Contents?.DeepClone(), ["bytes"] = share.Package.Length, ["uploading"] = share.Uploading,
            ["recipients"] = RecipientsJson(share), ["link"] = LinkJson(share),
        };

        static Task NoContent(HttpListenerContext c)
        {
            c.Response.StatusCode = 204;
            c.Response.Close();
            return Task.CompletedTask;
        }
    }

    // MARK: 標準答案

    static JsonNode? Nullable<T>(T? value) where T : struct => value is { } v ? JsonValue.Create(v) : null;

    static JsonNode? MeetingJson(EnterpriseShares.MeetingInfo? m) => m is null ? null : new JsonObject
    {
        ["title"] = m.Title, ["meetingDate"] = m.MeetingDate, ["durationSeconds"] = Nullable(m.DurationSeconds), ["language"] = m.Language,
    };

    static JsonObject ContentsJson(EnterpriseShares.Contents c) => new() { ["audio"] = c.Audio, ["userNotes"] = c.UserNotes, ["documents"] = c.Documents };

    static JsonObject PersonJson(EnterpriseShares.Person p) => new() { ["email"] = p.Email, ["name"] = p.Name, ["display"] = p.Display };

    /// <summary>標準答案（M§99.6）：建分享送的本體、info.sharing／詳情／與我分享／摘要的解析、不合理的值、名單 500 人截斷、shiyin://share 連結</summary>
    [Fact]
    public void SharesMatchMac()
    {
        var f = JsonNode.Parse(Golden.Fixtures.GetProperty("shares").GetRawText())!.AsObject();
        static int? Int(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<int>() : null;
        var actual = new JsonObject
        {
            ["create"] = new JsonArray([.. f["create"]!.AsArray().Select(item =>
            {
                var m = item!["meeting"]!;
                var k = item["contents"]!;
                var info = new EnterpriseShares.MeetingInfo(m["title"]?.GetValue<string>() ?? "", m["meetingDate"]?.GetValue<string>() ?? "", Int(m["durationSeconds"]),
                    m["language"]?.GetValue<string>() ?? "zh");
                var contents = new EnterpriseShares.Contents(k["audio"]?.GetValue<bool>() ?? true, k["userNotes"]?.GetValue<bool>() ?? true, k["documents"]?.GetValue<bool>() ?? true);
                return (JsonNode)new JsonObject
                {
                    ["input"] = item.DeepClone(),
                    ["body"] = EnterpriseShares.CreateBody(info, contents, Int(item["bytes"]) ?? 0, item["sha256"]?.GetValue<string>() ?? "", Int(item["expiresInDays"])),
                };
            })]),
            ["sharing"] = new JsonArray([.. f["sharing"]!.AsArray().Select(input => (JsonNode)new JsonObject
            {
                ["input"] = input?.DeepClone(),
                ["sharing"] = EnterpriseShares.ParseSharing(input) is { } s
                    ? new JsonObject { ["publicLinks"] = s.PublicLinks, ["maxDays"] = Nullable(s.MaxDays), ["maxBytes"] = Nullable(s.MaxBytes) } : null,
            })]),
            ["detail"] = new JsonArray([.. f["detail"]!.AsArray().Select(input =>
            {
                var d = EnterpriseShares.ParseDetail(input);
                return (JsonNode)new JsonObject
                {
                    ["input"] = input?.DeepClone(),
                    ["detail"] = d is null ? null : new JsonObject
                    {
                        ["id"] = d.Id, ["status"] = d.Status, ["version"] = d.Version, ["expiresAt"] = d.ExpiresAt, ["meeting"] = MeetingJson(d.Meeting),
                        ["contents"] = ContentsJson(d.Contents), ["bytes"] = d.Bytes, ["uploading"] = d.Uploading, ["recipientCount"] = d.Recipients.Count,
                        ["recipients"] = new JsonArray([.. d.Recipients.Take(3).Select(r => (JsonNode)new JsonObject
                        {
                            ["email"] = r.Email, ["name"] = r.Name, ["openedAt"] = r.OpenedAt, ["importedAt"] = r.ImportedAt, ["importedVersion"] = Nullable(r.ImportedVersion),
                        })]),
                        ["link"] = d.Link is { } link ? new JsonObject { ["url"] = link.Url, ["access"] = link.Access } : null,
                    },
                };
            })]),
            ["sharedWithMe"] = new JsonArray([.. f["sharedWithMe"]!.AsArray().Select(input => (JsonNode)new JsonObject
            {
                ["input"] = input?.DeepClone(),
                ["items"] = new JsonArray([.. EnterpriseShares.ParseSharedWithMe(input).Select(i => (JsonNode)new JsonObject
                {
                    ["id"] = i.Id, ["from"] = PersonJson(i.From), ["meeting"] = MeetingJson(i.Meeting), ["contents"] = ContentsJson(i.Contents), ["bytes"] = i.Bytes,
                    ["version"] = i.Version, ["updatedAt"] = i.UpdatedAt, ["expiresAt"] = i.ExpiresAt, ["importedVersion"] = Nullable(i.ImportedVersion),
                    ["isNew"] = i.IsNew, ["title"] = i.Title,
                })]),
            })]),
            ["summary"] = new JsonArray([.. f["summary"]!.AsArray().Select(input =>
            {
                var s = EnterpriseShares.ParseSummary(input);
                return (JsonNode)new JsonObject
                {
                    ["input"] = input?.DeepClone(),
                    ["summary"] = s is null ? null : new JsonObject
                    {
                        ["from"] = PersonJson(s.From), ["meeting"] = MeetingJson(s.Meeting), ["contents"] = ContentsJson(s.Contents), ["bytes"] = s.Bytes,
                        ["version"] = s.Version, ["expiresAt"] = s.ExpiresAt,
                    },
                };
            })]),
            ["links"] = new JsonArray([.. f["links"]!.AsArray().Select(input =>
            {
                var link = EnterpriseShares.ParseShareLink(input!.GetValue<string>());
                return (JsonNode)new JsonObject
                {
                    ["input"] = input.DeepClone(),
                    ["link"] = link is null ? null : new JsonObject { ["server"] = link.Server.ToString().TrimEnd('/'), ["id"] = link.Id, ["token"] = link.Token },
                };
            })]),
        };
        Golden.Json("server/shares.json", actual.ToJsonString());
        // Windows 交過來的網址多一個斜線（shiyin://share/?…）、沒有 // 的寫法也認得
        Assert.NotNull(EnterpriseShares.ParseShareLink("shiyin://share/?server=https://shiyin.example.com&id=a5e7e310b4ea9ff95109b5b46dab6b74"));
        Assert.NotNull(EnterpriseShares.ParseShareLink("shiyin:share?server=https://shiyin.example.com&id=a5e7e310b4ea9ff95109b5b46dab6b74"));
        Assert.False(EnterpriseShares.IsShareLink("shiyin://shared?server=x"));
        // DEBUG 可以連本機的 http（同登入）
        Assert.Null(EnterpriseShares.ParseShareLink("shiyin://share?server=http://127.0.0.1:8788&id=a5e7e310b4ea9ff95109b5b46dab6b74"));
        Assert.NotNull(EnterpriseShares.ParseShareLink("shiyin://share?server=http://127.0.0.1:8788&id=a5e7e310b4ea9ff95109b5b46dab6b74", allowLocalHttp: true));
    }

    // MARK: 流程

    (AppController Controller, AppDatabase Db, ShiyinPaths Paths) ShareController(EnterpriseServer server)
    {
        var paths = new ShiyinPaths(home.Path);
        paths.EnsureDirectories();
        var db = new AppDatabase(paths.Database, paths.Backups);
        settings.Set(AppSettings.ProviderKey, "none");
        var models = new ModelStore(paths.Models, http, new Dictionary<ModelStore.Model, ModelStore.ModelInfo>());
        var llm = new LLMClient(new MemoryKeys(), new NoTools(), new ProcessRunner(), http);
        var runner = new JobRunner(db, settings, paths, models, llm, new WavDecoder(), new NoEngines(), new NoCompressor(), NoPower.Instance);
        var recorder = new Recorder(settings, paths, NoPower.Instance, new Recorder.Hooks((_, _) => [], _ => null, null, _ => null));
        var controller = new AppController(db, settings, paths, models, runner, recorder, new WavDecoder(), null, "5.1.0") { Enterprise = server };
        return (controller, db, paths);
    }

    long ShareableMeeting(AppDatabase db, ShiyinPaths paths)
    {
        var source = Path.Combine(home.Path, "週會.wav");
        File.Copy(Wav(2).Wav, source, overwrite: true);
        var meeting = Importer.ImportFile(source, new ImportOptions(), db, paths, settings);
        var id = meeting.Id!.Value;
        db.SaveTranscript(id, [new Segment { MeetingId = id, Start = 0, End = 2, Text = "大家好", Speaker = 1 }], 2, 1, transcribedWith: "breeze");
        db.UpdateUserNotes(id, "我的筆記");
        return id;
    }

    static readonly JsonObject SharingOn = new() { ["enabled"] = true, ["publicLinks"] = true, ["maxDays"] = 30, ["maxBytes"] = 2147483648 };

    /// <summary>分享、續傳、名單（公司以外的放回欄位）、連結、期限、更新內容、停止分享（R125.2～R125.4）</summary>
    [Fact]
    public async Task ShareUploadsAndManagesTheShare()
    {
        fake.Sharing = (JsonObject)SharingOn.DeepClone();
        var server = await SignedIn();
        Assert.Equal(new EnterpriseShares.Sharing(true, true, 30, 2147483648), server.Account?.Sharing);
        var (controller, db, paths) = ShareController(server);
        var errors = new List<string>();
        controller.Error += e => { lock (errors) errors.Add(e); };
        var id = ShareableMeeting(db, paths);
        Assert.NotNull(controller.Sharing);
        fake.ConflictOnce = true;   // 第一塊 409：從伺服器說的位置接著傳
        var back = await controller.ShareMeeting(id, ["bob@example.com", "x@outside.org"], "company", 7, new EnterpriseShares.Contents(Audio: false, UserNotes: false));
        // 名單有公司以外的：分享和連結留著，信箱放回欄位
        Assert.Equal(["bob@example.com", "x@outside.org"], back);
        Assert.Contains(errors, e => e.Contains("不能分享給公司以外的信箱：x@outside.org"));
        var shareId = controller.ShareId(id);
        Assert.NotNull(shareId);
        var share = fake.Shares[shareId!];
        Assert.Equal("ready", share.Status);
        // 不帶錄音、筆記：封包裡沒有錄音，筆記是空的
        using (var zip = new ZipArchive(new MemoryStream(share.Package)))
        {
            Assert.DoesNotContain(zip.Entries, e => e.Name.StartsWith("audio.", StringComparison.Ordinal));
            using var reader = new StreamReader(zip.GetEntry("meeting.json")!.Open());
            Assert.Equal("", JsonNode.Parse(reader.ReadToEnd())!["userNotes"]!.GetValue<string>());
        }
        Assert.False(share.Contents!["audio"]!.GetValue<bool>());
        var detail = controller.ShareDetail(id)!;
        Assert.Equal(("company", 0), (detail.Link?.Access, detail.Recipients.Count));
        Assert.Equal(["bob@example.com", "x@outside.org"], await controller.AddShareRecipients(id, ["bob@example.com", "x@outside.org"]));
        Assert.Empty(await controller.AddShareRecipients(id, ["bob@example.com"]));
        Assert.Equal(["bob@example.com"], controller.ShareDetail(id)!.Recipients.Select(r => r.Email));
        Assert.Equal("Bob", controller.ShareDetail(id)!.Recipients[0].Name);
        await controller.SetShareExpiry(id, 30);
        Assert.Equal("2026-12-30T00:00:00Z", controller.ShareDetail(id)!.ExpiresAt);
        // 更新內容：版本 2，帶的內容照原本的
        db.UpdateUserNotes(id, "改過的筆記");
        await controller.UpdateShareContents(id);
        Assert.Equal(2, controller.ShareDetail(id)!.Version);
        Assert.False(controller.ShareDetail(id)!.Contents.Audio);
        await controller.RemoveShareRecipient(id, "bob@example.com");
        Assert.Empty(controller.ShareDetail(id)!.Recipients);
        await controller.SetShareLink(id, null);
        Assert.Null(controller.ShareDetail(id)!.Link);
        await controller.StopSharing(id);
        Assert.Empty(fake.Shares);
        Assert.Null(controller.ShareId(id));
        Assert.Null(controller.ShareDetail(id));
        Assert.Equal(2, errors.Count);
    }

    /// <summary>上傳到一半取消：伺服器上建到一半的分享刪掉、不記進 meeting_shares</summary>
    [Fact]
    public async Task CancelledShareIsDeletedOnTheServer()
    {
        fake.Sharing = (JsonObject)SharingOn.DeepClone();
        var server = await SignedIn();
        var (controller, db, paths) = ShareController(server);
        var id = ShareableMeeting(db, paths);
        fake.PatchDelayMs = 800;
        var task = controller.ShareMeeting(id, ["bob@example.com"], null, null, new EnterpriseShares.Contents());
        for (var i = 0; i < 200 && fake.Shares.Count == 0; i++) await Task.Delay(10);
        Assert.Single(fake.Shares);
        Assert.True(controller.ShareBusy);
        controller.CancelShare();
        Assert.Equal(["bob@example.com"], await task);
        for (var i = 0; i < 200 && fake.Shares.Count > 0; i++) await Task.Delay(10);
        Assert.Empty(fake.Shares);
        Assert.Null(controller.ShareId(id));
        Assert.False(controller.ShareBusy);
        Assert.Null(controller.ShareProgress);
    }

    /// <summary>與我分享（R125.5）：新的才通知、匯入成自己的一份、記下是誰分享的、回報伺服器；從清單移除</summary>
    [Fact]
    public async Task SharedWithMeImportsAndHides()
    {
        fake.Sharing = (JsonObject)SharingOn.DeepClone();
        var server = await SignedIn();
        var (controller, db, paths) = ShareController(server);
        var id = ShareableMeeting(db, paths);
        await controller.ShareMeeting(id, [], null, 7, new EnterpriseShares.Contents());
        var share = fake.Shares[controller.ShareId(id)!];
        share.Recipients.Add("amy@example.com");
        var arrived = new List<string>();
        controller.SharedMeetingArrived += item => arrived.Add(item.Id);
        await controller.RefreshSharedWithMe();
        await controller.RefreshSharedWithMe();
        Assert.Equal([share.Id], arrived);
        var item = Assert.Single(controller.SharedWithMe);
        Assert.Equal(("Bob", "週會", true), (item.From.Display, item.Title, item.IsNew));
        long? revealed = null;
        controller.Reveal += m => revealed = m;
        await controller.ImportShared(item, null);
        Assert.NotNull(revealed);
        var imported = db.Meeting(revealed!.Value)!;
        Assert.Equal(("Bob", "package"), (imported.SharedFrom, imported.Source));
        Assert.Equal(["大家好"], db.Segments(revealed.Value).Select(s => s.Text));
        Assert.True(paths.HasAudio(imported));
        Assert.Equal(1, share.Imported["amy@example.com"]);
        Assert.False(Assert.Single(controller.SharedWithMe).IsNew);
        Assert.Empty(Directory.GetFiles(paths.Tmp, "shared-*"));
        // 分享者更新過：再通知一次
        await controller.UpdateShareContents(id);
        await controller.RefreshSharedWithMe();
        Assert.Equal([share.Id, share.Id], arrived);
        Assert.True(Assert.Single(controller.SharedWithMe).IsNew);
        // 再匯入（M§99.4）：另存一份 → 兩場都在；取代 → 之前的兩場移到垃圾桶（新的成功後才動），只剩新的
        var first = revealed.Value;
        Assert.Equal([first], controller.PreviousImports(share.Id));
        await controller.ImportShared(Assert.Single(controller.SharedWithMe), null);
        var copy = revealed!.Value;
        Assert.NotEqual(first, copy);
        Assert.Equal([first, copy], controller.PreviousImports(share.Id));
        await controller.ImportShared(Assert.Single(controller.SharedWithMe), null, controller.PreviousImports(share.Id));
        var latest = revealed!.Value;
        Assert.Equal([latest], controller.PreviousImports(share.Id));
        Assert.All(new[] { first, copy }, m => Assert.NotNull(db.Meeting(m)!.DeletedAt));
        Assert.Equal((share.Id, "Bob"), (db.Meeting(latest)!.SharedId, db.Meeting(latest)!.SharedFrom));
        var gone = new List<string>();
        controller.SharedItemGone += g => gone.Add(g);
        await controller.HideShared(share.Id);
        Assert.Empty(controller.SharedWithMe);
        Assert.Contains(share.Id, gone);
    }

    /// <summary>公開的連結（R125.6）：沒登入也拿得到摘要、下載得了（Range 一段一段）；只有公司的連結沒登入時說明要先登入</summary>
    [Fact]
    public async Task ShareLinkWorksWithoutSigningIn()
    {
        fake.Sharing = (JsonObject)SharingOn.DeepClone();
        var server = await SignedIn();
        var (controller, db, paths) = ShareController(server);
        var id = ShareableMeeting(db, paths);
        await controller.ShareMeeting(id, [], "public", 7, new EnterpriseShares.Contents());
        var share = fake.Shares[controller.ShareId(id)!];
        var link = EnterpriseShares.ParseShareLink($"shiyin://share?server={Uri.EscapeDataString(fake.Server.Url)}&id={share.Id.ToUpperInvariant()}&t={share.LinkToken}", allowLocalHttp: true)!;
        Assert.Equal(share.Id, link.Id);
        var otherSettings = AppSettings.InMemory();
        var stranger = new EnterpriseServer(otherSettings, http, new MemorySecrets(), "5.1.0", "別台") { AllowLocalHttp = true };
        var api = new EnterpriseShares(stranger);
        var summary = await api.GetSummary(link);
        Assert.Equal(("Bob", "週會", share.Package.Length), (summary.From.Display, summary.Meeting?.Title, (int)summary.Bytes));
        var file = Path.Combine(home.Path, "下載.shiyin");
        var progress = new List<double>();
        Assert.Equal(1, await api.Download(link, file, summary.Bytes, summary.Version, progress.Add));
        Assert.Equal(share.Package, File.ReadAllBytes(file));
        Assert.Equal(1, progress[^1]);
        // 下載到一半時分享者更新了（M§99.4）：伺服器送的版本不同，前面那段不能接，從頭下載新的那版
        var stale = Path.Combine(home.Path, "一半.shiyin");
        File.WriteAllBytes(stale, share.Package[..(share.Package.Length / 2)]);
        var (oldPackage, oldVersion) = (share.Package, share.Version);
        share.Package = [.. share.Package.Reverse()];
        share.Version = 2;
        Assert.Equal(2, await api.Download(link, stale, oldPackage.Length, 1, _ => { }));
        Assert.Equal(share.Package, File.ReadAllBytes(stale));
        (share.Package, share.Version) = (oldPackage, oldVersion);
        await api.Imported(link, 1);
        Assert.Equal(1, share.Imported["amy@example.com"]);
        // 只有公司裡的人打得開：沒登入的回 401，說明要先登入哪一台
        await controller.SetShareLink(id, "company");
        var strangerController = ShareController(stranger).Controller;
        var e = await Assert.ThrowsAsync<EnterpriseException>(() => strangerController.ShareSummary(link));
        Assert.Contains("這個分享只有公司裡的人打得開", e.Message);
        Assert.Contains("127.0.0.1", e.Message);
    }

    /// <summary>公司關掉分享（403）不是登入失效；不合理的回應是錯誤，不會當掉；員工名單的查詢字有編碼</summary>
    [Fact]
    public async Task SharingErrorsDoNotSignOut()
    {
        fake.Sharing = (JsonObject)SharingOn.DeepClone();
        var server = await SignedIn();
        var api = new EnterpriseShares(server);
        var people = await api.People("a&b+c=d#?");
        Assert.Equal("a&b+c=d#?", fake.LastPeopleQuery);
        Assert.Equal(["bob@example.com"], people.Select(p => p.Email));
        fake.SharingOff = true;
        var off = await Assert.ThrowsAsync<EnterpriseException>(() => api.SharedWithMe());
        Assert.Equal((EnterpriseFailure.Forbidden, "公司沒有開放分享會議"), (off.Kind, off.Message));
        Assert.True(server.IsSignedIn);
        Assert.Null(server.ExpiredMessage);
        Assert.True(secrets.Values.ContainsKey(EnterpriseServer.CredentialAccount));
        var (controller, _, _) = ShareController(server);
        await controller.RefreshSharedWithMe();
        Assert.True(server.IsSignedIn);
        fake.SharingOff = false;
        // 不合理的回應：建分享回的 id 不是 32 個十六進位字、詳情少了 status
        fake.CreateId = "not-an-id";
        var bad = await Assert.ThrowsAsync<EnterpriseException>(() => api.Create(EnterpriseShares.CreateBody(new("t", "2026-11-27", null, "zh"), new(), 1, new string('a', 64), null)));
        Assert.Equal("bad_response", bad.ServerKind);
        fake.CreateId = null;
        var created = await api.Create(EnterpriseShares.CreateBody(new("t", "2026-11-27", null, "zh"), new(), 1, new string('a', 64), null));
        fake.DetailBody = """{"id":"x","status":"gone"}""";
        Assert.Equal("bad_response", (await Assert.ThrowsAsync<EnterpriseException>(() => api.GetDetail(created))).ServerKind);
        fake.DetailBody = "不是 JSON";
        Assert.Equal("bad_response", (await Assert.ThrowsAsync<EnterpriseException>(() => api.GetDetail(created))).ServerKind);
        // 沒有開分享的伺服器：帳號沒有 sharing，分享對話框只有「用檔案分享…」
        fake.Sharing = new JsonObject { ["enabled"] = false };
        await server.RefreshInfo();
        Assert.Null(server.Account?.Sharing);
        Assert.Null(controller.Sharing);
        Assert.Equal(["x@example.com"], await controller.ShareMeeting(1, ["x@example.com"], null, null, new EnterpriseShares.Contents()));
    }
}
