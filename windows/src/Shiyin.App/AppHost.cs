using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Shiyin.Data;
using Shiyin.LLM;
using Shiyin.Localization;
using Shiyin.Model;
using Shiyin.Platform;
using Shiyin.Services;
using Shiyin.Services.Capture;
using Shiyin.Speech;
using Shiyin.Whisper;

namespace Shiyin.App;

/// <summary>
/// 組裝 app 的服務（同 Mac 的 AppModel.init）：路徑、設定、資料庫、模型、整理工具、轉錄、工作佇列、錄音、偵測會議、快捷鍵、更新。
/// 服務的事件在背景執行緒觸發，這裡統一用 Post 切回 UI 執行緒
/// </summary>
public sealed class AppHost
{
    public static AppHost Current { get; private set; } = null!;

    public ShiyinPaths Paths { get; }
    public AppSettings Settings { get; }
    public AppDatabase Db { get; }
    public HttpClient Http { get; } = new() { Timeout = TimeSpan.FromMinutes(20) };
    public ModelStore Models { get; }
    public GpuAcceleration Gpu { get; }
    public IKeyStore Keys { get; }
    public LLMClient Llm { get; }
    public JobRunner Runner { get; }
    public Recorder Recorder { get; }
    public AppController Controller { get; }
    public MeetingDetector Detector { get; }
    public GlobalHotKeys HotKeys { get; }
    public Updater? Updater { get; }
    public VelopackUpdates? Updates { get; }
    public AskService Ask { get; }
    public UsageStats Usage { get; }
    public Player Player { get; } = new();
    public Notifier Notifier { get; }
    public MediaFoundationDecoder Decoder { get; } = new();
    public MediaFoundationCompressor Compressor { get; } = new();
    public string Version { get; } = typeof(AppHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0";
    public bool Debug { get; }

    readonly DispatcherQueue dispatcher;

    /// <summary>熱鍵、通知、偵測會議要開主視窗、提醒時呼叫（App 設定）</summary>
    public Action<long?>? ShowMain { get; set; }
    public Action<MeetingDetector.Prompt>? ShowMeetingPrompt { get; set; }

    public AppHost(DispatcherQueue dispatcher, IReadOnlyList<string> args)
    {
        Current = this;
        this.dispatcher = dispatcher;
#if DEBUG
        Debug = true;
#endif
        Paths = ShiyinPaths.FromEnvironment();
        Paths.EnsureDirectories();
        Log.Configure(Paths.Logs);
        Settings = AppSettings.Load(Paths.Settings, AppSettings.ParseOverrides(args));
        UseInterfaceLanguage(Settings.String(AppSettings.InterfaceLanguageKey));
        // 換了介面語言時，開始功能表、桌面的捷徑和檔案類型的名稱跟著換（R27 R12.3）
        if (Shortcuts.IsInstalledApp)
        {
            Shortcuts.Rename(L.T("拾音"));
            FileAssociations.UpdateNames();
        }
        // 遷移在交易裡進行，失敗時原本的資料不會被改到（N3）；App 顯示原因後結束
        Db = new AppDatabase(Paths.Database, Paths.Backups);
        Models = new ModelStore(Paths.Models, Http);
        // NVIDIA 顯示卡加速（R27 R4.6）：官方的 CUDA 版只有 x64。DEBUG 的 --set debugNvidia=YES 當作有 NVIDIA（虛擬機測自動下載）
        var nvidia = SystemInfo.HasNvidiaGpu() || (Debug && Settings.Flag("debugNvidia", false));
        Gpu = new GpuAcceleration(Models, Settings, nvidia && RuntimeInformation.ProcessArchitecture == Architecture.X64, AppContext.BaseDirectory);
        // DEBUG 預設不讀真的認證管理員（同 Mac 3.4.1）：key 只放記憶體，要測時加 --set debugRealKeychain=YES
        Keys = Debug && !Settings.Flag("debugRealKeychain", false) ? new MemoryKeyStore() : new CredentialStore();
        var children = new ChildProcesses();
        var runner = new ProcessRunner { OnStarted = children.Add };
        // 找 claude、codex：DEBUG 加 --set debugNoCli=YES 當成沒有安裝（M§19.6）
        Tools = Debug && Settings.Flag("debugNoCli", false) ? new NoCliTools(new WindowsToolLocator()) : new WindowsToolLocator();
        Llm = new LLMClient(Keys, Tools, runner, Http) { CompatibleBaseUrl = () => Settings.CompatibleBaseUrl };
        // 沒選過整理工具時用找得到的 CLI（R35.1）：要掃過 PATH 的每個資料夾，快取 30 秒
        Settings.DetectCli = CachedCliDetector(Tools);
        // 匿名使用統計（design-windows §12.10）：正式版用打包時寫進 app 的網址；DEBUG 不讀，只用控制通道的 usageurl
        Usage = new UsageStats(Paths.Usage, Settings, Http,
            () => new UsageStats.AppInfo(Version, SystemInfo.UsageOs(), SystemInfo.CpuName(), L.English ? "en" : "zh-Hant"))
        {
            Url = Debug ? null : BuiltUsageUrl, AlwaysOffered = Debug,
        };
        var engines = new SpeechEngines(Models, () => Gpu.QwenCudaReady);
        var power = new PowerRequests();
        Runner = new JobRunner(Db, Settings, Paths, Models, Llm, Decoder, engines, Compressor, power) { Usage = Usage };
        Recorder = new Recorder(Settings, Paths, power, new Recorder.Hooks(MakeSources, MakeLive, DetectLanguage, Shell.AvailableBytes));
        Controller = new AppController(Db, Settings, Paths, Models, Runner, Recorder, Decoder, new ExportPlatform(this), Version) { Usage = Usage };
        Detector = new MeetingDetector(new ConsentStoreMicrophoneUsage(), Settings) { IsRecording = () => Recorder.IsRecording };
        HotKeys = new GlobalHotKeys();
        Ask = new AskService(Db, Settings, Llm) { Usage = Usage };
        Notifier = new Notifier(this);
        string? updateSource = null;
        var updateKey = UpdateKey.Public;
#if UPDATE_TEST
        // 測試更新的建置（pack.ps1 -UpdateTest，design-windows §14.2）：更新來源和公鑰從環境變數讀，正式版不會編進來
        updateSource = Environment.GetEnvironmentVariable("SHIYIN_UPDATE_SOURCE");
        updateKey = Environment.GetEnvironmentVariable("SHIYIN_UPDATE_PUBLIC_KEY") ?? updateKey;
#endif
        if (!Debug && updateKey.Length > 0)
        {
            Updates = new VelopackUpdates(Http, updateSource);
            Updater = new Updater(Updates, updateKey, Settings, () => Recorder.IsRecording || Runner.IsBusy);
        }
    }

    /// <summary>打包時寫進 app 的統計網址（pack.ps1 的 SHIYIN_USAGE_URL → AssemblyMetadata）；沒有就是不送統計的版本</summary>
    static string? BuiltUsageUrl => typeof(AppHost).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "ShiyinUsageURL")?.Value;

    /// <summary>介面語言（design-windows §11.4、D57）：設定的 zh、en，沒設定時跟著 Windows 的顯示語言（中文以外都用英文）</summary>
    public static bool UsesEnglish(string? setting) => setting switch
    {
        "zh" => false,
        "en" => true,
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "zh",
    };

    /// <summary>Velopack 的安裝、更新 hook 用（這時還沒有 AppHost）：照設定的介面語言，捷徑、檔案類型的名稱才會對</summary>
    public static void UseSavedInterfaceLanguage()
    {
        try { UseInterfaceLanguage(AppSettings.Load(ShiyinPaths.FromEnvironment().Settings).String(AppSettings.InterfaceLanguageKey)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { }
    }

    static void UseInterfaceLanguage(string? setting)
    {
        if (!UsesEnglish(setting)) { L.Use(null); return; }
        var table = new Dictionary<string, string>();
        var plurals = new Dictionary<string, (string, string)>();
        // 先讀 Mac 的，再讀 Windows 才有的（同一個鍵以 Windows 的為準）
        foreach (var name in new[] { "Localizable.xcstrings", "Windows.xcstrings" })
        {
            using var stream = typeof(AppHost).Assembly.GetManifestResourceStream(name);
            if (stream is null) continue;
            using var reader = new StreamReader(stream);
            var (t, p) = L.ReadXcstrings(reader.ReadToEnd());
            foreach (var (k, v) in t) table[k] = v;
            foreach (var (k, v) in p) plurals[k] = v;
        }
        L.Use(table, plurals);
    }

    /// <summary>切回 UI 執行緒（服務的事件都在背景執行緒）</summary>
    /// <summary>
    /// 丟到 UI 執行緒。DispatcherQueue 的回呼丟出例外時 WinUI 不會走 UnhandledException，直接結束程式（CoreMessagingXP 0xc000027b，
    /// 2026-10-21 在虛擬機錄音時看到）：錄音中的 app 不能因為畫面更新的一個錯誤就消失，接住、寫進 log
    /// </summary>
    public void Post(Action action) => dispatcher.TryEnqueue(() =>
    {
        // 結束中：服務一個一個關掉了，畫面不再跟著更新（不然會用到關掉的播放器、資料庫）
        if (shuttingDown) return;
        try { action(); }
        catch (Exception e) { Log.Error($"ui callback failed: {e.GetType().Name} 0x{e.HResult:X8}"); }
    });

    volatile bool shuttingDown;

    public void Start()
    {
        // 在任何轉錄之前（Controller.Start 會接著做當掉前沒做完的工作）：決定 whisper 從哪裡載入
        Gpu.RestartNeeded += () => Post(() => Notifier.Notice(L.T("NVIDIA 顯示卡加速下載好了，重新開啟拾音後，轉錄就會用顯示卡")));
        Gpu.Start();
        Controller.Start();
        Detector.Prompted += p => Post(() => ShowMeetingPrompt?.Invoke(p));
        Detector.Start();
        HotKeys.RecordPressed += () => Post(() => Controller.ToggleRecording(""));
        HotKeys.BookmarkPressed += () => Post(() =>
        {
            if (Controller.BookmarkRecording()) Notifier.Notice(L.F("已標記 %@ 為重點", TimeFormat.String(Recorder.Elapsed)));
        });
        Recorder.Started += () => Post(() => HotKeyError = HotKeys.SetBookmarkEnabled(Settings.BookmarkHotKey));
        Recorder.Finished += _ => Post(() => HotKeys.SetBookmarkEnabled(false));
        HotKeyError = HotKeys.SetRecordEnabled(Settings.GlobalHotKey);
        Runner.Finished += (job, error) => Post(() => Notifier.JobFinished(job, error));
        if (Updater is not null) _ = Updater.RunAutomaticChecks(CancellationToken.None);
        // 打開 app 就建立今天的空白匯總（沒有用量的日子也算活躍，M§30.3），60 秒後開始送
        Usage.Touch();
        _ = Usage.RunAutomaticSends(CancellationToken.None);
    }

    /// <summary>找 claude、codex（設定頁、引導、整理工具共用；DEBUG 的 debugNoCli 會換掉）</summary>
    public IToolLocator Tools { get; }

    /// <summary>找這台電腦上的 CLI，先 claude 再 codex（R35.1）；30 秒內不重找</summary>
    static Func<LLMProvider?> CachedCliDetector(IToolLocator tools)
    {
        var gate = new object();
        LLMProvider? found = null;
        var checkedAt = DateTime.MinValue;
        return () =>
        {
            lock (gate)
            {
                if (DateTime.UtcNow - checkedAt > TimeSpan.FromSeconds(30))
                {
                    found = tools.Find("claude") is not null ? LLMProvider.Claude : tools.Find("codex") is not null ? LLMProvider.Codex : null;
                    checkedAt = DateTime.UtcNow;
                }
                return found;
            }
        };
    }

    /// <summary>快捷鍵被別的 app 佔走的原因（設定頁顯示）</summary>
    public string? HotKeyError { get; set; }

    IReadOnlyList<(int Channel, IAudioSource Source)> MakeSources(string source, string device) => source switch
    {
        "system" => [(0, new WasapiSource(WasapiSource.Kind.SystemAudio))],
        "both" => [(0, new WasapiSource(WasapiSource.Kind.Microphone, device)), (1, new WasapiSource(WasapiSource.Kind.SystemAudio))],
        _ => [(0, new WasapiSource(WasapiSource.Kind.Microphone, device))],
    };

    /// <summary>即時逐字稿：快速辨識的模型還沒下載好時這場不做（模型在背景下載），錄完照常正式轉錄</summary>
    ILiveTranscriber? MakeLive(MeetingLanguage language)
    {
        if (!Models.IsInstalled(ModelStore.Model.FastRecognition))
        {
            Models.AutoStart(ModelStore.Model.FastRecognition);
            throw new InvalidOperationException(L.T("快速辨識的模型還在下載"));
        }
        var files = ModelStore.FastRecognition.Files;
        return new LiveTranscriber(new SenseVoiceModels(Models.PathOf(files[0]), Models.PathOf(files[1]), Models.PathOf(files[2])), language);
    }

    /// <summary>自動偵測語言（M§18.8）：模型還沒下載就這次不偵測、開始下載，下次錄音就能用</summary>
    Dictionary<MeetingLanguage, double> DetectLanguage(float[] samples)
    {
        if (!Models.IsInstalled(ModelStore.Model.LanguageDetection))
        {
            Models.AutoStart(ModelStore.Model.LanguageDetection);
            return [];
        }
        return WhisperEngine.DetectLanguage(samples, Models.PathOf(ModelStore.Model.LanguageDetection));
    }

    /// <summary>
    /// 結束拾音（M§20.12）的第一步，在背景執行緒：錄音先存好、工作改回排隊，等 whisper 收尾最多 10 秒。
    /// 從這裡開始畫面不再更新（停止錄音會觸發選取那場會議、重畫播放列）
    /// </summary>
    public void StopWork()
    {
        shuttingDown = true;
        if (Recorder.IsRecording) Recorder.Stop();
        Runner.TerminateAll(TimeSpan.FromSeconds(10));
        var waited = 0;
        while (WhisperEngine.OpenContexts > 0 && waited < 10_000) { Thread.Sleep(100); waited += 100; }
    }

    /// <summary>
    /// 第二步，回到 UI 執行緒再關：播放器（MediaPlayer）、快捷鍵是在 UI 執行緒建的，在背景執行緒關會跟畫面互等
    ///（2026-10-21 在虛擬機錄音中按「結束」，畫面讀播放速度、背景關播放器，兩邊卡住，程式結束不了）
    /// </summary>
    public void Close()
    {
        Detector.Stop();
        HotKeys.Dispose();
        Player.Dispose();
        Db.Dispose();
    }
}

/// <summary>當成沒有安裝 claude、codex（DEBUG 的 debugNoCli，M§19.6）</summary>
sealed class NoCliTools(IToolLocator inner) : IToolLocator
{
    public ToolCommand? Find(string name) => name is "claude" or "codex" ? null : inner.Find(name);
}

/// <summary>更新金鑰的公鑰（design-windows §14.2）：scripts/windows/update-key.cs keygen 產生（2026-10-22）；空的時候不檢查更新。
/// 私鑰在 GitHub secrets 的 SHIYIN_UPDATE_KEY，備份在使用者的登入鑰匙圈（com.weiwei.shiyin.update-key）</summary>
public static class UpdateKey
{
    public const string Public = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEg37C581Do8y5IvsVofw2YXfoH0Hi
        UxR3xgLs84CLNcSjvALzm4wXVClOMZ4Y2lwFhwPDYSu21mwNIpyYC6Euog==
        -----END PUBLIC KEY-----
        """;
}
