using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Shiyin.IO;
using Shiyin.Localization;

namespace Shiyin.Services;

/// <summary>
/// 透過企業伺服器分享會議（R125、M§99.4、design-windows §11.58，同 Mac 的 Store/Shares.swift）：分享、名單、連結、期限、更新內容、停止分享；
/// 與我分享、匯入、shiyin:share。畫面的狀態（選到哪一場、對話框）在 App
/// </summary>
public sealed partial class AppController
{
    /// <summary>企業伺服器（分享會議用）；沒給（測試）就當成沒開分享</summary>
    public EnterpriseServer? Enterprise { get; init; }
    EnterpriseShares? shares;
    EnterpriseShares? Shares => Enterprise is { } server ? shares ??= new EnterpriseShares(server) : null;

    public const string SeenSharesKey = "seenShares";
    public const string ShareNoticeShownKey = "shareNoticeShown";

    readonly object shareGate = new();
    readonly Dictionary<long, EnterpriseShares.Detail> shareDetails = [];
    CancellationTokenSource? shareCancel;

    /// <summary>與我分享（R125.5）：查新的分享時換掉</summary>
    public IReadOnlyList<EnterpriseShares.SharedItem> SharedWithMe { get; private set; } = [];
    /// <summary>上傳、下載的進度（0～1）；沒有在傳是 null</summary>
    public double? ShareProgress { get; private set; }
    public bool ShareBusy { get; private set; }
    /// <summary>分享的狀態變了（名單、連結、進度、與我分享）：畫面重畫，不用重讀資料庫</summary>
    public event Action? SharesChanged;
    /// <summary>新的、更新過的分享（R125.5）：App 送通知</summary>
    public event Action<EnterpriseShares.SharedItem>? SharedMeetingArrived;
    /// <summary>與我分享清單上的那一場不在了（被停止、過期、從清單移除）：App 把右邊的預覽收起來</summary>
    public event Action<string>? SharedItemGone;

    /// <summary>公司的伺服器有開分享、登入了（R125.7）；否則分享對話框只有「用檔案分享…」</summary>
    public EnterpriseShares.Sharing? Sharing => Enterprise is { IsSignedIn: true, Account.Sharing: { Enabled: true } sharing } ? sharing : null;

    /// <summary>meeting_shares 的 server 欄位：目前登入的伺服器（不帶結尾的斜線，同 Mac 的 savedURL.absoluteString）</summary>
    string? ShareServer => Enterprise?.SavedUrl?.ToString().TrimEnd('/');

    /// <summary>這場在目前的伺服器分享過的 id（新的在前）</summary>
    public string? ShareId(long meetingId) => ShareServer is { } server ? Db.ShareIds(meetingId, server).FirstOrDefault() : null;

    public EnterpriseShares.Detail? ShareDetail(long meetingId)
    {
        lock (shareGate) return shareDetails.GetValueOrDefault(meetingId);
    }

    void SetShareDetail(long meetingId, EnterpriseShares.Detail? detail)
    {
        lock (shareGate)
        {
            if (detail is null) shareDetails.Remove(meetingId);
            else shareDetails[meetingId] = detail;
        }
        SharesChanged?.Invoke();
    }

    void SetShareProgress(double? value)
    {
        ShareProgress = value;
        SharesChanged?.Invoke();
    }

    // MARK: 分享者

    /// <summary>會議頁、分享對話框打開時問一次伺服器（D212）：過期、被撤銷（not_found）就忘掉這一列</summary>
    public async Task LoadShareDetail(long meetingId)
    {
        if (Sharing is null || Shares is not { } api || ShareServer is not { } server || ShareId(meetingId) is not { } id)
        {
            if (ShareDetail(meetingId) is not null) SetShareDetail(meetingId, null);
            return;
        }
        try { SetShareDetail(meetingId, await api.GetDetail(id)); }
        catch (EnterpriseException e) when (e.ServerKind == "not_found")
        {
            Db.ForgetShare(server, id);
            SetShareDetail(meetingId, null);
        }
        catch (Exception e) when (e is EnterpriseException or HttpRequestException) { Log.Error($"share detail failed: {e.GetType().Name}"); }
    }

    /// <summary>要帶的內容（R125.2）：不帶錄音時沒有錄音、不帶我的筆記時筆記是空的、不帶文件時沒有參考文件和會議中的畫面</summary>
    public static ExportBundle Apply(EnterpriseShares.Contents contents, ExportBundle bundle)
    {
        var meeting = bundle.Meeting.Clone();
        if (!contents.Audio) meeting.AudioFile = "";
        if (!contents.UserNotes) meeting.UserNotes = "";
        return bundle with { Meeting = meeting, Documents = contents.Documents ? bundle.Documents : [] };
    }

    /// <summary>打包（背景）：tmp 裡的 .shiyin、大小、SHA-256</summary>
    async Task<(string Path, long Bytes, string Sha)> SharePackage(long meetingId, EnterpriseShares.Contents contents, CancellationToken cancel)
    {
        var bundle = Apply(contents, Bundle(meetingId));
        Directory.CreateDirectory(Paths.Tmp);
        var path = Path.Combine(Paths.Tmp, $"share-{Guid.NewGuid()}.shiyin");
        return await Task.Run(async () =>
        {
            MeetingPackage.Export(bundle, path, Paths, appVersion, null, cancel);
            await using var stream = File.OpenRead(path);
            var hash = await SHA256.HashDataAsync(stream, cancel);
            return (path, stream.Length, Convert.ToHexStringLower(hash));
        }, cancel);
    }

    EnterpriseShares.MeetingInfo ShareMeetingInfo(long meetingId)
    {
        if (Db.Meeting(meetingId) is not { } m) throw new ExportException(L.T("這場會議已經被刪除，沒辦法匯出"));
        return new EnterpriseShares.MeetingInfo(m.DisplayTitle, m.MeetingDate, m.DurationSeconds, m.Language);
    }

    /// <summary>分享、更新內容的上傳（分享對話框的「取消」停掉它）</summary>
    public void CancelShare() => shareCancel?.Cancel();

    CancellationToken BeginShare()
    {
        ShareBusy = true;
        shareCancel = new CancellationTokenSource();
        SetShareProgress(0);
        return shareCancel.Token;
    }

    void EndShare()
    {
        ShareBusy = false;
        shareCancel?.Dispose();
        shareCancel = null;
        SetShareProgress(null);
    }

    static void DeleteQuietly(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// 「分享」（R125.2）：打包、上傳、記下來，再加名單、建連結。回傳沒加進名單的信箱（放回欄位讓人改）；
    /// 沒傳完（取消、失敗）的分享請伺服器刪掉，不留半套
    /// </summary>
    public async Task<List<string>> ShareMeeting(long meetingId, IReadOnlyList<string> emails, string? link, int? days, EnterpriseShares.Contents contents)
    {
        if (Sharing is null || Shares is not { } api || ShareServer is not { } server || ShareBusy) return [.. emails];
        var cancel = BeginShare();
        try
        {
            string? created = null;
            string id;
            try
            {
                var (file, bytes, sha) = await SharePackage(meetingId, contents, cancel);
                try
                {
                    if (Sharing?.MaxBytes is long max && bytes > max)
                    {
                        Error?.Invoke(L.F("這場會議超過公司的伺服器一場的上限（%@），可以不帶錄音再試", Capture.Recorder.ByteCount(max)));
                        return [.. emails];
                    }
                    cancel.ThrowIfCancellationRequested();
                    var body = EnterpriseShares.CreateBody(ShareMeetingInfo(meetingId), contents, bytes, sha, days);
                    id = await api.Create(body, cancel);
                    created = id;
                    await api.Upload(id, file, bytes, value => SetShareProgress(value), cancel);
                    Db.RecordShare(meetingId, server, id);
                }
                finally { DeleteQuietly(file); }
            }
            catch (Exception e) when (e is OperationCanceledException or EnterpriseException or ExportException or IOException
                                          or MeetingPackage.PackageException or UnauthorizedAccessException)
            {
                if (created is { } half) _ = Task.Run(async () => { try { await api.Stop(half); } catch (Exception) { } });
                if (e is not OperationCanceledException) Error?.Invoke(e.Message);
                return [.. emails];
            }
            var back = new List<string>();
            if (emails.Count > 0)
            {
                try { await api.AddRecipients(id, emails); }
                catch (EnterpriseException e)
                {
                    Error?.Invoke(e.Message);
                    back = [.. emails];
                }
            }
            if (link is not null)
            {
                try { await api.SetLink(id, link); }
                catch (EnterpriseException e) { Error?.Invoke(e.Message); }
            }
            await LoadShareDetail(meetingId);
            return back;
        }
        finally { EndShare(); }
    }

    /// <summary>「更新內容」（R125.4）：照原本帶的內容重新打包上傳，名單、連結不變</summary>
    public async Task UpdateShareContents(long meetingId)
    {
        if (Shares is not { } api || ShareId(meetingId) is not { } id || ShareDetail(meetingId) is not { } detail || ShareBusy) return;
        var cancel = BeginShare();
        try
        {
            var (file, bytes, sha) = await SharePackage(meetingId, detail.Contents, cancel);
            try
            {
                var body = EnterpriseShares.CreateBody(ShareMeetingInfo(meetingId), detail.Contents, bytes, sha, null);
                await api.StartVersion(id, body, cancel);
                await api.Upload(id, file, bytes, value => SetShareProgress(value), cancel);
            }
            finally { DeleteQuietly(file); }
            Notice?.Invoke(L.T("已更新分享的內容"));
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is EnterpriseException or ExportException or IOException or MeetingPackage.PackageException or UnauthorizedAccessException)
        {
            Error?.Invoke(e.Message);
        }
        finally { EndShare(); }
        await LoadShareDetail(meetingId);
    }

    /// <summary>回傳沒加進去的信箱（伺服器拒絕時放回欄位）</summary>
    public async Task<List<string>> AddShareRecipients(long meetingId, IReadOnlyList<string> emails)
    {
        if (Shares is not { } api || ShareId(meetingId) is not { } id || emails.Count == 0) return [.. emails];
        var back = new List<string>();
        try { await api.AddRecipients(id, emails); }
        catch (EnterpriseException e)
        {
            Error?.Invoke(e.Message);
            back = [.. emails];
        }
        await LoadShareDetail(meetingId);
        return back;
    }

    public async Task RemoveShareRecipient(long meetingId, string email)
    {
        if (Shares is not { } api || ShareId(meetingId) is not { } id) return;
        try { await api.RemoveRecipient(id, email); }
        catch (EnterpriseException e) { Error?.Invoke(e.Message); }
        await LoadShareDetail(meetingId);
    }

    /// <summary>access 是 null 時停止連結</summary>
    public async Task SetShareLink(long meetingId, string? access)
    {
        if (Shares is not { } api || ShareId(meetingId) is not { } id) return;
        try
        {
            if (access is not null) await api.SetLink(id, access);
            else await api.DeleteLink(id);
        }
        catch (EnterpriseException e) { Error?.Invoke(e.Message); }
        await LoadShareDetail(meetingId);
    }

    public async Task SetShareExpiry(long meetingId, int? days)
    {
        if (Shares is not { } api || ShareId(meetingId) is not { } id) return;
        try { SetShareDetail(meetingId, await api.SetExpiry(id, days)); }
        catch (EnterpriseException e) { Error?.Invoke(e.Message); }
    }

    /// <summary>「停止分享…」（R125.4）：伺服器刪掉這場，別人已經匯入的不受影響</summary>
    public async Task StopSharing(long meetingId)
    {
        if (Shares is not { } api || ShareServer is not { } server || ShareId(meetingId) is not { } id) return;
        try { await api.Stop(id); }
        catch (EnterpriseException e) when (e.ServerKind == "not_found") { }
        catch (EnterpriseException e)
        {
            Error?.Invoke(e.Message);
            return;
        }
        Db.ForgetShare(server, id);
        SetShareDetail(meetingId, null);
        Notice?.Invoke(L.T("已停止分享"));
    }

    // MARK: 收到的人（R125.5）

    /// <summary>看過的分享：id → 版本（新的、更新過的才通知），存在 settings.json</summary>
    Dictionary<string, int> SeenShares
    {
        get
        {
            var result = new Dictionary<string, int>();
            try
            {
                if (Settings.String(SeenSharesKey) is { } text && JsonNode.Parse(text) is JsonObject o)
                    foreach (var (id, value) in o)
                        if (EnterpriseShares.Positive(value) is long version) result[id] = (int)version;
            }
            catch (System.Text.Json.JsonException) { }
            return result;
        }
        set => Settings.Set(SeenSharesKey, new JsonObject([.. value.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, p.Value))]).ToJsonString());
    }

    /// <summary>查新的分享（啟動、回到前景、每 15 分鐘）；新的或更新過的送通知</summary>
    public async Task RefreshSharedWithMe(bool notify = true)
    {
        if (Sharing is null || Shares is not { } api)
        {
            if (SharedWithMe.Count > 0)
            {
                SharedWithMe = [];
                SharesChanged?.Invoke();
            }
            return;
        }
        try
        {
            var items = await api.SharedWithMe();
            var seen = SeenShares;
            foreach (var item in items.Where(i => seen.TryGetValue(i.Id, out var version) ? i.Version > version : true))
            {
                if (notify && item.IsNew) SharedMeetingArrived?.Invoke(item);
                seen[item.Id] = item.Version;
            }
            var ids = items.Select(i => i.Id).ToHashSet();
            SeenShares = seen.Where(p => ids.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
            var gone = SharedWithMe.Select(i => i.Id).Where(id => !ids.Contains(id)).ToList();
            SharedWithMe = items;
            foreach (var id in gone) SharedItemGone?.Invoke(id);
            SharesChanged?.Invoke();
        }
        catch (Exception e) when (e is EnterpriseException or HttpRequestException) { Log.Error($"shared-with-me failed: {e.GetType().Name}"); }
    }

    /// <summary>「匯入」：下載、匯入成自己的一份、記下是誰分享的、回報伺服器</summary>
    public async Task ImportShared(EnterpriseShares.SharedItem item, long? folder)
    {
        if (Enterprise?.SavedUrl is not { } server) return;
        await ImportShare(new EnterpriseShares.ShareLink(server, item.Id, null), item.From.Display, item.Bytes, folder);
        await RefreshSharedWithMe(notify: false);
    }

    public async Task HideShared(string id)
    {
        if (Shares is not { } api) return;
        try { await api.Hide(id); }
        catch (EnterpriseException e) { Error?.Invoke(e.Message); }
        SharedItemGone?.Invoke(id);
        await RefreshSharedWithMe(notify: false);
    }

    /// <summary>下載到 tmp（顯示百分比）→ 匯入封包 → 寫 shared_from → 回報伺服器；下載到一半的檔案留著，再按一次「匯入」接著下載（同一個版本）</summary>
    public async Task<bool> ImportShare(EnterpriseShares.ShareLink link, string from, long bytes, long? folder)
    {
        if (Shares is not { } api || ShareBusy) return false;
        var cancel = BeginShare();
        Directory.CreateDirectory(Paths.Tmp);
        var file = Path.Combine(Paths.Tmp, $"shared-{link.Id}.shiyin");
        try
        {
            var version = await api.Download(link, file, bytes, value => SetShareProgress(value), cancel);
            var meeting = await Task.Run(() => MeetingPackage.Import(file, Db, Paths, folder), cancel);
            DeleteQuietly(file);
            if (meeting.Id is long id) Db.SetSharedFrom(id, from);
            await api.Imported(link, version);
            Changed?.Invoke(null);
            if (meeting.Id is long revealed) Reveal?.Invoke(revealed);
            Notice?.Invoke(L.F("已匯入「%@」", meeting.DisplayTitle));
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception e) when (e is EnterpriseException or MeetingPackage.PackageException or IOException or UnauthorizedAccessException
                                      or Microsoft.Data.Sqlite.SqliteException)
        {
            Error?.Invoke(e.Message);
            return false;
        }
        finally { EndShare(); }
    }

    /// <summary>shiyin:share（網頁的「用拾音打開」，R125.6）：先拿標題、分享者（App 問過再匯入）。只有公司裡的人打得開的連結、沒登入時換成說明</summary>
    public async Task<EnterpriseShares.Summary> ShareSummary(EnterpriseShares.ShareLink link)
    {
        if (Shares is not { } api) throw EnterpriseException.Of(EnterpriseFailure.LoginExpired);
        try { return await api.GetSummary(link); }
        catch (EnterpriseException e) when (e.Kind == EnterpriseFailure.LoginExpired)
        {
            throw new EnterpriseException(EnterpriseFailure.LoginExpired,
                L.F("這個分享只有公司裡的人打得開，請先在「設定 → 企業伺服器」登入 %@", EnterpriseServer.DisplayHost(link.Server)));
        }
    }
}
