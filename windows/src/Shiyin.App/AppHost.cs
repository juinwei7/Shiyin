using System.Globalization;
using System.Reflection;
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
        // 遷移在交易裡進行，失敗時原本的資料不會被改到（N3）；App 顯示原因後結束
        Db = new AppDatabase(Paths.Database, Paths.Backups);
        Models = new ModelStore(Paths.Models, Http);
        // DEBUG 預設不讀真的認證管理員（同 Mac 3.4.1）：key 只放記憶體，要測時加 --set debugRealKeychain=YES
        Keys = Debug && !Settings.Flag("debugRealKeychain", false) ? new MemoryKeyStore() : new CredentialStore();
        var children = new ChildProcesses();
        var runner = new ProcessRunner { OnStarted = children.Add };
        Llm = new LLMClient(Keys, new WindowsToolLocator(), runner, Http) { CompatibleBaseUrl = () => Settings.CompatibleBaseUrl };
        var engines = new SpeechEngines(Models);
        var power = new PowerRequests();
        Runner = new JobRunner(Db, Settings, Paths, Models, Llm, Decoder, engines, Compressor, power);
        Recorder = new Recorder(Settings, Paths, power, new Recorder.Hooks(MakeSources, MakeLive, DetectLanguage, Shell.AvailableBytes));
        Controller = new AppController(Db, Settings, Paths, Models, Runner, Recorder, Decoder, new ExportPlatform(this), Version);
        Detector = new MeetingDetector(new ConsentStoreMicrophoneUsage(), Settings) { IsRecording = () => Recorder.IsRecording };
        HotKeys = new GlobalHotKeys();
        Ask = new AskService(Db, Settings, Llm);
        Notifier = new Notifier(this);
        if (!Debug && UpdateKey.Public.Length > 0)
        {
            Updates = new VelopackUpdates(Http);
            Updater = new Updater(Updates, UpdateKey.Public, Settings, () => Recorder.IsRecording || Runner.IsBusy);
        }
    }

    /// <summary>介面語言（design-windows §11.4、D57）：設定的 zh、en，沒設定時跟著 Windows 的顯示語言（中文以外都用英文）</summary>
    static void UseInterfaceLanguage(string? setting)
    {
        var english = setting switch
        {
            "zh" => false,
            "en" => true,
            _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "zh",
        };
        if (!english) { L.Use(null); return; }
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
        try { action(); }
        catch (Exception e) { Log.Error($"ui callback failed: {e.GetType().Name} 0x{e.HResult:X8}"); }
    });

    public void Start()
    {
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

    /// <summary>結束拾音（M§20.12）：錄音先存好、工作改回排隊，等 whisper 收尾最多 10 秒</summary>
    public void Shutdown()
    {
        if (Recorder.IsRecording) Recorder.Stop();
        Runner.TerminateAll(TimeSpan.FromSeconds(10));
        var waited = 0;
        while (WhisperEngine.OpenContexts > 0 && waited < 10_000) { Thread.Sleep(100); waited += 100; }
        Detector.Stop();
        HotKeys.Dispose();
        Player.Dispose();
        Db.Dispose();
    }
}

/// <summary>更新金鑰的公鑰（design-windows §14.2）：scripts/windows/update-key.cs keygen 產生後貼進來；空的時候不檢查更新</summary>
public static class UpdateKey
{
    public const string Public = "";
}
