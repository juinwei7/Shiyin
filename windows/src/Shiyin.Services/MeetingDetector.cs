using Shiyin.Localization;

namespace Shiyin.Services;

/// <summary>哪些 app 正在用麥克風（Windows 讀隱私設定的使用紀錄 ConsentStore，design-windows §12.4、D55）</summary>
public interface IMicrophoneUsage
{
    /// <summary>正在用麥克風的 app：封裝的 app 是套件家族名稱，桌面 app 是執行檔名稱（小寫）。拾音自己不算</summary>
    IReadOnlySet<string> AppsUsingMicrophone();
}

/// <summary>
/// 偵測線上會議（M§21.7、R19.4、D31，同 Mac 的 MeetingDetector）：已知的會議 app 開始用麥克風、拾音沒在錄音時提醒開始；
/// 錄音期間用過麥克風的會議 app 都停了，提醒停止。只提醒，不自動開始或停止
/// </summary>
public sealed class MeetingDetector
{
    public sealed record MeetingApp(string Id, string Name);

    public abstract record Prompt(MeetingApp App)
    {
        public sealed record Start(MeetingApp App) : Prompt(App);
        public sealed record Stop(MeetingApp App) : Prompt(App);
    }

    /// <summary>已知的會議 app（確切名稱待 T-W8 在真的電腦上確認）</summary>
    public static readonly Dictionary<string, string> MeetingApps = new()
    {
        ["zoom.exe"] = "Zoom", ["msteams_8wekyb3d8bbwe"] = "Microsoft Teams", ["ms-teams.exe"] = "Microsoft Teams", ["teams.exe"] = "Microsoft Teams",
        ["ciscocollabhost.exe"] = "Webex", ["webex.exe"] = "Webex", ["atmgr.exe"] = "Webex",
        ["slack.exe"] = "Slack", ["line.exe"] = "LINE", ["discord.exe"] = "Discord",
    };

    /// <summary>瀏覽器：可能在開 Google Meet</summary>
    public static readonly Dictionary<string, string> Browsers = new()
    {
        ["chrome.exe"] = "Chrome", ["msedge.exe"] = "Edge", ["firefox.exe"] = "Firefox", ["brave.exe"] = "Brave",
        ["thebrowsercompany.arc"] = "Arc", ["opera.exe"] = "Opera", ["vivaldi.exe"] = "Vivaldi",
    };

    public const string MutedKey = "meetingDetectionMuted";

    readonly IMicrophoneUsage usage;
    readonly AppSettings settings;
    readonly Func<DateTime> clock;
    readonly object gate = new();
    readonly Dictionary<string, DateTime> activeSince = [];
    readonly Dictionary<string, DateTime> inactiveSince = [];
    readonly HashSet<string> promptedThisCall = [];
    readonly HashSet<string> usedDuringRecording = [];
    bool stopPrompted;
    Timer? timer;

    /// <summary>要提醒時（AppController 決定用通知還是小視窗）。在背景執行緒觸發</summary>
    public event Action<Prompt>? Prompted;
    public Func<bool> IsRecording { get; set; } = () => false;
    /// <summary>DEBUG 的 meetingapp：模擬某個 app 開始、停止用麥克風</summary>
    public Dictionary<string, bool> Simulated { get; } = [];

    public MeetingDetector(IMicrophoneUsage usage, AppSettings settings, Func<DateTime>? clock = null)
    {
        (this.usage, this.settings) = (usage, settings);
        this.clock = clock ?? (() => DateTime.UtcNow);
    }

    public bool Enabled => settings.MeetingDetection;
    public List<string> Muted => settings.StringList(MutedKey);

    public void Mute(string id)
    {
        var list = Muted;
        if (!list.Contains(id)) list.Add(id);
        settings.Set(MutedKey, list);
    }

    public void Unmute(string id) => settings.Set(MutedKey, Muted.Where(x => x != id));

    public static string DisplayName(string id) =>
        MeetingApps.TryGetValue(id, out var name) ? name : Browsers.TryGetValue(id, out var browser) ? L.F("%@（可能是 Google Meet）", browser) : id;

    /// <summary>已知名單裡的 ID：完全相同；封裝的 app 的套件家族名稱比對前綴（Arc 的套件家族名稱後面有發行者代碼）</summary>
    public static string? Known(string id)
    {
        var lower = id.ToLowerInvariant();
        return MeetingApps.Keys.Concat(Browsers.Keys).FirstOrDefault(k => lower == k || lower.StartsWith(k + "_"));
    }

    public void Start() => timer ??= new Timer(_ => Tick(), null, 2000, 2000);

    public void Stop()
    {
        timer?.Dispose();
        timer = null;
    }

    public void Tick()
    {
        var prompts = new List<Prompt>();
        lock (gate)
        {
            var now = clock();
            var recording = IsRecording();
            var using_ = Enabled ? Current() : [];
            var muted = Muted;
            foreach (var app in activeSince.Keys.Union(using_).ToList())
            {
                if (using_.Contains(app))
                {
                    inactiveSince.Remove(app);
                    activeSince.TryAdd(app, now);
                    if (recording) usedDuringRecording.Add(app);
                    // 持續用麥克風 5 秒、這次通話還沒提醒過、沒有選「不要再提醒」
                    if (!recording && !promptedThisCall.Contains(app) && !muted.Contains(app) && now - activeSince[app] >= TimeSpan.FromSeconds(5))
                    {
                        promptedThisCall.Add(app);
                        Log.Info($"meeting app started using the microphone: {app}");
                        prompts.Add(new Prompt.Start(new MeetingApp(app, DisplayName(app))));
                    }
                }
                else
                {
                    // 停止用麥克風 10 秒才算這次通話結束
                    inactiveSince.TryAdd(app, now);
                    if (now - inactiveSince[app] >= TimeSpan.FromSeconds(10))
                    {
                        activeSince.Remove(app);
                        inactiveSince.Remove(app);
                        promptedThisCall.Remove(app);
                    }
                }
            }
            if (recording)
            {
                // 錄音期間用過麥克風的會議 app 都停了 10 秒：會議好像結束了
                if (!stopPrompted && usedDuringRecording.Count > 0 && usedDuringRecording.All(a => !activeSince.ContainsKey(a)))
                {
                    stopPrompted = true;
                    var any = usedDuringRecording.Order(StringComparer.Ordinal).First();
                    Log.Info("meeting apps stopped using the microphone");
                    prompts.Add(new Prompt.Stop(new MeetingApp(any, DisplayName(any))));
                }
            }
            else
            {
                usedDuringRecording.Clear();
                stopPrompted = false;
            }
        }
        foreach (var p in prompts) Prompted?.Invoke(p);
    }

    HashSet<string> Current()
    {
        var result = new HashSet<string>();
        IReadOnlySet<string> apps;
        try { apps = usage.AppsUsingMicrophone(); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Error($"microphone usage unreadable: {e.GetType().Name}");
            apps = new HashSet<string>();
        }
        foreach (var app in apps) if (Known(app) is { } known) result.Add(known);
        // 模擬的 app 也經過同一套對照
        foreach (var (app, on) in Simulated)
        {
            if (Known(app) is not { } known) continue;
            if (on) result.Add(known); else result.Remove(known);
        }
        return result;
    }
}
