using System.Runtime.InteropServices;
using Shiyin.Services;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace Shiyin.Platform;

/// <summary>
/// Velopack 那一邊（design-windows §14.2、D50）：GitHub Releases 找新版、下載、套用並重開。
/// 驗證更新金鑰、SHA-256 在 Services 的 Updater；這裡只負責 Velopack
/// </summary>
public sealed class VelopackUpdates : IUpdatePlatform
{
    public const string Repository = "https://github.com/juinwei7/Shiyin";

    readonly UpdateManager manager;
    readonly HttpClient http;
    readonly string? testSource;
    UpdateInfo? pending;

    /// <param name="testSource">只有測試更新的建置（pack.ps1 -UpdateTest）會給：從這個網址讀 Velopack 的更新檔和更新說明，不讀 GitHub</param>
    public VelopackUpdates(HttpClient http, string? testSource = null)
    {
        this.http = http;
        this.testSource = testSource?.TrimEnd('/');
        // GitHub 的 release 清單讀 100 筆（Velopack 原本只讀 10 筆，Mac 的 release 多時會找不到 Windows 的，§14.3、D94）
        IUpdateSource source = this.testSource is null ? new WindowsReleaseSource(Repository) : new SimpleWebSource(this.testSource);
        manager = new UpdateManager(source, null, null);
    }

    public bool IsInstalled => manager.IsInstalled;
    public string CurrentVersion => manager.CurrentVersion?.ToString() ?? "0";
    public string Arch => RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";

    public async Task<(string Version, string? Notes)?> Check(CancellationToken cancel)
    {
        if (!manager.IsInstalled) return null;
        pending = await manager.CheckForUpdatesAsync();
        return pending is { IsDowngrade: false } info ? (info.TargetFullRelease.Version.ToString(), info.TargetFullRelease.NotesMarkdown) : null;
    }

    public async Task<IReadOnlyDictionary<string, string>> Download(string version, Action<int> progress, CancellationToken cancel)
    {
        var info = pending is not null && pending.TargetFullRelease.Version.ToString() == version ? pending
            : await manager.CheckForUpdatesAsync() ?? throw new UpdateException(Localization.L.T("更新檔裡找不到拾音"));
        pending = info;
        await manager.DownloadUpdatesAsync(info, progress, cancel);
        var packages = VelopackLocator.Current.PackagesDir ?? throw new UpdateException(Localization.L.T("更新檔裡找不到拾音"));
        return new Dictionary<string, string> { [info.TargetFullRelease.FileName] = Path.Combine(packages, info.TargetFullRelease.FileName) };
    }

    /// <summary>同一個 Release 的 shiyin-update-win-&lt;架構&gt;.json 和 .sig（tag 是 win-v&lt;版本&gt;，D51）</summary>
    public async Task<(byte[] Manifest, string Signature)> FetchManifest(string version, CancellationToken cancel)
    {
        var baseUrl = testSource is null ? $"{Repository}/releases/download/win-v{version}/shiyin-update-win-{Arch}.json" : $"{testSource}/shiyin-update-win-{Arch}.json";
        var manifest = await http.GetByteArrayAsync(baseUrl, cancel);
        var signature = await http.GetStringAsync(baseUrl + ".sig", cancel);
        return (manifest, signature);
    }

    public void DeleteDownloads(string version)
    {
        if (pending?.TargetFullRelease is not { } asset) return;
        if (VelopackLocator.Current.PackagesDir is not { } packages) return;
        try { File.Delete(Path.Combine(packages, asset.FileName)); } catch (IOException) { }
    }

    public void ApplyAndRestart(string version)
    {
        if (pending is null) return;
        manager.ApplyUpdatesAndRestart(pending.TargetFullRelease, []);
    }
}
