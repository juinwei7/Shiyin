using System.Security.Cryptography;
using System.Text.Json;
using Shiyin.Localization;

namespace Shiyin.Services;

public sealed class UpdateException(string message) : Exception(message);

/// <summary>
/// 更新檔的說明（design-windows §14.2、D50）：CI 打包後寫「版本、架構、每個套件檔的 SHA-256」，用更新金鑰（ECDSA P-256）簽章，
/// 跟 Release 一起上傳（shiyin-update-win-&lt;架構&gt;.json 和 .sig）。app 內建公鑰，套用前驗證。等於 Mac 的「新版要跟目前同一個簽章身分」
/// </summary>
public sealed record UpdateManifest(string Version, string Arch, IReadOnlyList<UpdateManifest.PackageFile> Files, string? Notes = null)
{
    public sealed record PackageFile(string Name, long Size, string Sha256);

    public static UpdateManifest? Parse(byte[] json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var files = r.GetProperty("files").EnumerateArray().Select(f => new PackageFile(
                f.GetProperty("name").GetString()!, f.GetProperty("size").GetInt64(), f.GetProperty("sha256").GetString()!.ToLowerInvariant())).ToList();
            var version = r.GetProperty("version").GetString()!;
            var arch = r.GetProperty("arch").GetString()!;
            if (files.Count == 0 || files.Any(f => f.Name.IndexOfAny(['/', '\\', ':']) >= 0 || f.Name.StartsWith('.'))) return null;
            return new UpdateManifest(version, arch, files, r.TryGetProperty("notes", out var n) ? n.GetString() : null);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or NullReferenceException)
        {
            return null;
        }
    }

    public string Json() => JsonSerializer.Serialize(new
    {
        version = Version, arch = Arch,
        files = Files.Select(f => new { name = f.Name, size = f.Size, sha256 = f.Sha256 }),
        notes = Notes,
    }, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
}

public static class UpdateSignature
{
    /// <summary>簽 manifest 檔案的原始位元組（DER 格式的 ECDSA P-256、SHA-256），回傳 base64</summary>
    public static string Sign(byte[] manifest, string privateKeyPem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        return Convert.ToBase64String(key.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
    }

    public static bool Verify(byte[] manifest, string signatureBase64, string publicKeyPem)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);
            return key.KeySize == 256
                && key.VerifyData(manifest, Convert.FromBase64String(signatureBase64.Trim()), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception e) when (e is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>產生一把更新金鑰（scripts/windows/update-key.cs 用）：私鑰、公鑰的 PEM</summary>
    public static (string Private, string Public) Generate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key.ExportPkcs8PrivateKeyPem(), key.ExportSubjectPublicKeyInfoPem());
    }
}

/// <summary>Velopack 那一邊（Platform）：找新版、下載、套用並重開</summary>
public interface IUpdatePlatform
{
    string CurrentVersion { get; }
    /// <summary>x64 / arm64</summary>
    string Arch { get; }
    /// <summary>有比較新的版本就回傳版本號和說明；沒有回傳 null</summary>
    Task<(string Version, string? Notes)?> Check(CancellationToken cancel);
    /// <summary>下載新版的套件，回傳下載好的檔案（檔名 → 路徑）</summary>
    Task<IReadOnlyDictionary<string, string>> Download(string version, Action<int> progress, CancellationToken cancel);
    /// <summary>同一個 Release 裡的 shiyin-update-win-&lt;架構&gt;.json 和 .sig</summary>
    Task<(byte[] Manifest, string Signature)> FetchManifest(string version, CancellationToken cancel);
    void DeleteDownloads(string version);
    /// <summary>結束拾音、套用、重新開啟</summary>
    void ApplyAndRestart(string version);
}

/// <summary>
/// app 內更新（R10.6、M§12；design-windows §14.2）：檢查 → 使用者同意 → 下載 → 驗證更新金鑰的簽章、版本、架構、SHA-256 → 套用。
/// 錄音、轉錄、整理進行中不更新；驗證不過就刪掉下載的檔案，舊版完全不受影響
/// </summary>
public sealed class Updater(IUpdatePlatform platform, string publicKeyPem, AppSettings settings, Func<bool> busy)
{
    public abstract record State
    {
        public sealed record Idle : State;
        public sealed record Checking : State;
        public sealed record UpToDate : State;
        public sealed record Available(string Version, string? Notes) : State;
        public sealed record Downloading(int Percent) : State;
        public sealed record Installing : State;
        public sealed record Failed(string Reason) : State;

        public string Label => this switch
        {
            Checking => L.T("正在檢查…"),
            UpToDate => L.T("已是最新版本"),
            Available a => L.F("有新版本 %@", a.Version),
            Downloading => L.T("正在下載更新…"),
            Installing => L.T("正在準備安裝…"),
            Failed f => f.Reason,
            _ => "",
        };
    }

    const string LastCheckedKey = "updateLastChecked";

    public State Current { get; private set; } = new State.Idle();
    public event Action? Changed;

    void Set(State state)
    {
        Current = state;
        Changed?.Invoke();
    }

    /// <summary>版本號依數字逐段比較，例如 3.0.10 比 3.0.9 新</summary>
    public static bool IsNewer(string candidate, string current)
    {
        static int[] Parts(string v) => v.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        var (a, b) = (Parts(candidate), Parts(current));
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var (x, y) = (i < a.Length ? a[i] : 0, i < b.Length ? b[i] : 0);
            if (x != y) return x > y;
        }
        return false;
    }

    /// <summary>啟動 30 秒後檢查一次，之後每小時看看距離上次檢查是否超過 24 小時</summary>
    public async Task RunAutomaticChecks(CancellationToken cancel)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancel);
            while (!cancel.IsCancellationRequested)
            {
                var last = settings.Double(LastCheckedKey) is double t ? DateTimeOffset.FromUnixTimeSeconds((long)t) : DateTimeOffset.MinValue;
                if (DateTimeOffset.UtcNow - last > TimeSpan.FromHours(24)) await Check(userInitiated: false, cancel);
                await Task.Delay(TimeSpan.FromHours(1), cancel);
            }
        }
        catch (OperationCanceledException) { }
    }

    public async Task Check(bool userInitiated, CancellationToken cancel = default)
    {
        if (Current is State.Downloading or State.Installing) return;
        try
        {
            Set(new State.Checking());
            var found = await platform.Check(cancel);
            settings.Set(LastCheckedKey, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            Set(found is { } f && IsNewer(f.Version, platform.CurrentVersion) ? new State.Available(f.Version, f.Notes) : new State.UpToDate());
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // 自動檢查失敗不跳視窗打擾使用者，只顯示在設定裡
            Set(new State.Failed(e.Message));
            Log.Error($"update check failed: {e.GetType().Name}");
        }
    }

    /// <summary>下載、驗證、套用。失敗回傳原因（也會寫進 Current）；成功時 app 會結束重開，不會回來</summary>
    public async Task<string?> Install(string version, CancellationToken cancel = default)
    {
        try
        {
            if (busy()) throw new UpdateException(L.T("錄音或轉錄、整理進行中，完成後再更新"));
            if (!IsNewer(version, platform.CurrentVersion)) throw new UpdateException(L.T("更新檔的版本沒有比較新"));
            Set(new State.Downloading(0));
            var files = await platform.Download(version, p => Set(new State.Downloading(p)), cancel);
            Set(new State.Installing());
            var (manifestBytes, signature) = await platform.FetchManifest(version, cancel);
            await Verify(manifestBytes, signature, version, files, cancel);
            // 下載期間可能開始了錄音或轉錄
            if (busy()) throw new UpdateException(L.T("錄音或轉錄、整理進行中，完成後再更新"));
            platform.ApplyAndRestart(version);
            return null;
        }
        catch (Exception e) when (e is UpdateException or HttpRequestException or IOException or OperationCanceledException)
        {
            platform.DeleteDownloads(version);
            var reason = e is OperationCanceledException ? L.T("已取消更新，舊版沒有改動") : e.Message;
            Set(new State.Failed(reason));
            Log.Error($"update failed: {e.GetType().Name}");
            return reason;
        }
    }

    /// <summary>簽章、版本、架構、每個套件檔的 SHA-256 都要對</summary>
    public async Task Verify(byte[] manifestBytes, string signature, string version, IReadOnlyDictionary<string, string> files, CancellationToken cancel)
    {
        if (!UpdateSignature.Verify(manifestBytes, signature, publicKeyPem))
            throw new UpdateException(L.F("新版的簽章跟目前的拾音不符，拒絕安裝（%@）", L.T("更新金鑰不符")));
        var manifest = UpdateManifest.Parse(manifestBytes) ?? throw new UpdateException(L.T("更新資訊的格式不對"));
        if (manifest.Version != version || !IsNewer(manifest.Version, platform.CurrentVersion))
            throw new UpdateException(L.T("更新檔的版本沒有比較新"));
        if (!string.Equals(manifest.Arch, platform.Arch, StringComparison.OrdinalIgnoreCase))
            throw new UpdateException(L.T("更新資訊的格式不對"));
        foreach (var (name, path) in files)
        {
            var expected = manifest.Files.FirstOrDefault(f => f.Name == name) ?? throw new UpdateException(L.T("更新資訊的格式不對"));
            if (new FileInfo(path).Length != expected.Size || await ModelStore.Sha256(path, cancel) != expected.Sha256)
                throw new UpdateException(L.T("下載的檔案驗證失敗（SHA-256 不符），已刪除"));
        }
        if (files.Count == 0) throw new UpdateException(L.T("更新檔裡找不到拾音"));
    }
}
