using System.Security.Cryptography;

namespace Shiyin.Services;

/// <summary>
/// 通知的 shiyin: 網址（design-windows §12.2、D62）：點通知打開那場會議；偵測會議的按鈕要帶權杖，
/// 權杖對不上（網頁裡的連結、上次啟動留下的舊通知）只打開主視窗，不開始、不停止錄音
/// </summary>
public sealed class NotificationLinks
{
    public const string Scheme = "shiyin";

    public abstract record Link
    {
        /// <summary>打開主視窗；有會議 id 時選到那場</summary>
        public sealed record Open(long? MeetingId) : Link;
        /// <summary>偵測會議的按鈕：start、mute、stop、continue</summary>
        public sealed record Prompt(string Action, string App) : Link;
    }

    static readonly string[] Actions = ["start", "mute", "stop", "continue"];

    /// <summary>每次啟動隨機產生、只放記憶體</summary>
    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public static bool IsLink(string argument) => argument.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase);

    public static string OpenMeeting(long meetingId) => $"{Scheme}:open?meeting={meetingId}";

    public string Prompt(string action, string app) =>
        $"{Scheme}:prompt?action={Uri.EscapeDataString(action)}&app={Uri.EscapeDataString(app)}&token={Token}";

    /// <summary>不是 shiyin: 網址時回 null</summary>
    public Link? Parse(string url)
    {
        if (!IsLink(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var query = Query(uri.Query);
        switch (uri.AbsolutePath.Trim('/').ToLowerInvariant())
        {
            case "open":
                return new Link.Open(query.TryGetValue("meeting", out var id) && long.TryParse(id, out var meetingId) && meetingId > 0 ? meetingId : null);
            case "prompt":
                if (query.TryGetValue("token", out var token) && CryptographicOperations.FixedTimeEquals(
                        System.Text.Encoding.ASCII.GetBytes(token), System.Text.Encoding.ASCII.GetBytes(Token))
                    && query.TryGetValue("action", out var action) && Actions.Contains(action)
                    && query.TryGetValue("app", out var app) && app.Length > 0)
                    return new Link.Prompt(action, app);
                return new Link.Open(null);
            default:
                return new Link.Open(null);
        }
    }

    static Dictionary<string, string> Query(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var key = Uri.UnescapeDataString(eq < 0 ? part : part[..eq]);
            var value = eq < 0 ? "" : Uri.UnescapeDataString(part[(eq + 1)..]);
            result.TryAdd(key, value);
        }
        return result;
    }
}
