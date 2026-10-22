using System.Text.Json.Nodes;
using Velopack.Sources;

namespace Shiyin.Services;

/// <summary>
/// 從 GitHub Releases 找 Windows 的新版（design-windows §14.3、D94）。Velopack 的 GithubSource 只讀最新的 10 筆 release，
/// 同一個 repo 裡 Mac 的 release 比較多：Mac 連發 10 版之後 Windows 的就不在清單裡，使用者會看到「已是最新版本」。
/// 只換掉讀清單的那一步：讀 100 筆（GitHub 一次最多），留下有 Velopack 更新說明（releases.*.json）的最新 10 個；找新版、下載照 Velopack
/// </summary>
public sealed class WindowsReleaseSource(string repository, IFileDownloader? downloader = null)
    : GithubSource(repository, null, false, downloader)
{
    public const int PerPage = 100;
    public const int Keep = 10;

    protected override async Task<GithubRelease[]> GetReleases(bool includePrereleases)
    {
        var url = new Uri(GetApiBaseUrl(RepoUri), $"repos{RepoUri.AbsolutePath}/releases?per_page={PerPage}&page=1");
        var json = await Downloader.DownloadString(url.ToString(), GetRequestHeaders("application/vnd.github.v3+json")).ConfigureAwait(false);
        // 自己讀需要的欄位，不用反射的 JsonSerializer（關掉反射的建置會失敗，檢查更新就靜靜地不動）
        var releases = (JsonNode.Parse(json) as JsonArray ?? []).OfType<JsonObject>().Select(r => new GithubRelease
        {
            Name = (string?)r["name"],
            Prerelease = (bool?)r["prerelease"] ?? false,
            PublishedAt = DateTime.TryParse((string?)r["published_at"], null, System.Globalization.DateTimeStyles.RoundtripKind, out var at) ? at : null,
            Assets = (r["assets"] as JsonArray ?? []).OfType<JsonObject>().Select(a => new GithubReleaseAsset
            {
                Name = (string?)a["name"], Url = (string?)a["url"], BrowserDownloadUrl = (string?)a["browser_download_url"],
                ContentType = (string?)a["content_type"],
            }).ToArray(),
        });
        return releases
            .Where(r => (includePrereleases || !r.Prerelease) && r.Assets?.Any(IsFeed) == true)
            .OrderByDescending(r => r.PublishedAt)
            .Take(Keep)
            .ToArray();
    }

    static bool IsFeed(GithubReleaseAsset asset) =>
        asset.Name is { } name && name.StartsWith("releases.", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal);
}
