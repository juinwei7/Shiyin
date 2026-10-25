using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shiyin.LLM;
using Shiyin.Localization;
using Shiyin.Model;

namespace Shiyin.Services;

/// <summary>
/// 設定（design-windows §12.8、D48）：SHIYIN_HOME\settings.json，鍵名跟 Mac 的 UserDefaults 相同。
/// 寫入時先寫暫存檔再取代；啟動參數 <c>--set 鍵=值</c> 覆蓋單一設定，只在這次執行有效、不寫進檔案（同 Mac 的 <c>-llmProvider none</c>）
/// </summary>
public sealed class AppSettings
{
    public const string ProviderKey = "llmProvider";
    public const string AutoSummarizeKey = "autoSummarize";
    public const string CompressAudioKey = "compressAudio";
    public const string DiarizeKey = "diarize";
    public const string SplitChannelsKey = "splitChannels";
    public const string CorrectTranscriptKey = "correctTranscript";
    public const string LiveTranscriptionKey = "liveTranscription";
    public const string AutoDetectLanguageKey = "autoDetectLanguage";
    public const string RecordSourceKey = "recordSource";
    public const string InputDeviceKey = "inputDeviceUID";
    public const string DefaultTemplateKey = "defaultTemplate";
    public const string GlobalHotKeyKey = "globalHotKey";
    public const string TranscriptionEngineKey = "transcriptionEngine";
    public const string DefaultMeetingLanguageKey = "defaultMeetingLanguage";
    public const string VoiceprintsKey = "voiceprints";
    public const string MeetingDetectionKey = "meetingDetection";
    public const string BookmarkHotKeyKey = "bookmarkHotKey";
    /// <summary>NVIDIA 顯示卡加速已經自動下載過（R27 R4.6）：只自動一次，使用者刪掉後不再下載</summary>
    public const string GpuPackOfferedKey = "gpuPackOffered";
    /// <summary>Qwen3-ASR 的 CUDA 版已經自動下載過（design-windows §8.4）</summary>
    public const string QwenCudaOfferedKey = "qwenCudaOffered";
    public const string CompatibleBaseUrlKey = "openaiCompatibleBaseURL";
    public const string OnboardingKey = "hasCompletedOnboarding";
    public const string GuideKey = "hasSeenGuide";
    /// <summary>Windows 才有：介面語言（空的是跟著 Windows 的顯示語言，zh、en）</summary>
    public const string InterfaceLanguageKey = "interfaceLanguage";

    readonly string? path;
    readonly object gate = new();
    readonly JsonObject values;
    readonly Dictionary<string, string> overrides;

    /// <summary>某個設定改了（鍵）。在呼叫 Set 的執行緒上觸發</summary>
    public event Action<string>? Changed;

    AppSettings(string? path, JsonObject values, IReadOnlyDictionary<string, string>? overrides)
    {
        this.path = path;
        this.values = values;
        this.overrides = overrides?.ToDictionary() ?? [];
    }

    /// <summary>讀 settings.json；讀不出來（壞掉）就當成沒有設定，寫入時會換掉</summary>
    public static AppSettings Load(string path, IReadOnlyDictionary<string, string>? overrides = null)
    {
        JsonObject values;
        try { values = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? [] : []; }
        catch (Exception e) when (e is JsonException or IOException)
        {
            Log.Error($"settings unreadable: {e.GetType().Name}");
            values = [];
        }
        return new AppSettings(path, values, overrides);
    }

    /// <summary>只在記憶體（測試用）</summary>
    public static AppSettings InMemory(IReadOnlyDictionary<string, string>? overrides = null) => new(null, [], overrides);

    /// <summary>啟動參數裡的 <c>--set 鍵=值</c></summary>
    public static Dictionary<string, string> ParseOverrides(IEnumerable<string> args)
    {
        var result = new Dictionary<string, string>();
        using var e = args.GetEnumerator();
        while (e.MoveNext())
        {
            if (e.Current != "--set" || !e.MoveNext()) continue;
            var i = e.Current.IndexOf('=');
            if (i > 0) result[e.Current[..i]] = e.Current[(i + 1)..];
        }
        return result;
    }

    // MARK: 讀寫

    public string? String(string key)
    {
        lock (gate)
        {
            if (overrides.TryGetValue(key, out var o)) return o;
            return values[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : values[key]?.ToJsonString();
        }
    }

    /// <summary>開關類的設定。啟動參數是字串，認得 YES、NO、true、false、1、0（同 Mac 的 bool(forKey:)）</summary>
    public bool Flag(string key, bool fallback)
    {
        lock (gate)
        {
            if (overrides.TryGetValue(key, out var o)) return ParseBool(o) ?? fallback;
            return values[key] switch
            {
                JsonValue v when v.TryGetValue<bool>(out var b) => b,
                JsonValue v when v.TryGetValue<string>(out var s) => ParseBool(s) ?? fallback,
                JsonValue v when v.TryGetValue<double>(out var d) => d != 0,
                _ => fallback,
            };
        }
    }

    public double? Double(string key)
    {
        var s = String(key);
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    static bool? ParseBool(string s) => s.Trim().ToLowerInvariant() switch
    {
        "yes" or "true" or "1" => true,
        "no" or "false" or "0" => false,
        _ => null,
    };

    /// <summary>字串清單（例如不要再提醒的會議 app）</summary>
    public List<string> StringList(string key)
    {
        lock (gate)
            return values[key] is JsonArray array ? array.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToList() : [];
    }

    public void Set(string key, IEnumerable<string> list) => Store(key, new JsonArray(list.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()));

    public bool Has(string key)
    {
        lock (gate) return overrides.ContainsKey(key) || values.ContainsKey(key);
    }

    public void Set(string key, string? value) => Store(key, value is null ? null : JsonValue.Create(value));
    public void Set(string key, bool value) => Store(key, JsonValue.Create(value));
    public void Set(string key, double value) => Store(key, JsonValue.Create(value));
    public void Remove(string key) => Store(key, null);

    void Store(string key, JsonNode? value)
    {
        lock (gate)
        {
            // 這次執行被啟動參數覆蓋的設定，畫面上改了也只在記憶體（不然會把測試的值寫進使用者的設定）
            if (overrides.ContainsKey(key))
            {
                if (value is null) overrides.Remove(key);
                else overrides[key] = value is JsonValue v && v.TryGetValue<string>(out var s) ? s : value.ToJsonString();
            }
            else if (value is null) values.Remove(key);
            else values[key] = value;
            Save();
        }
        Changed?.Invoke(key);
    }

    void Save()
    {
        if (path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, values.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, overwrite: true);
        }
        catch (IOException e)
        {
            Log.Error($"settings write failed: {e.GetType().Name}");
        }
    }

    // MARK: 各項設定（預設值同 Mac 的 AppSettings）

    /// <summary>DEBUG 的 llmprovider：只在記憶體換整理工具（M§19.5）</summary>
    public LLMProvider? DebugProvider { get; set; }
    public string? DebugModel { get; set; }

    /// <summary>使用者選過的整理工具；沒選過（或不是認得的值）是 null（R35）</summary>
    public LLMProvider? ChosenProvider => LLMProviders.Parse(String(ProviderKey));
    /// <summary>找這台電腦上的 CLI（先 claude 再 codex）：App 用 WindowsToolLocator 給（快取 30 秒，design-windows §10）；沒給就是找不到</summary>
    public Func<LLMProvider?> DetectCli { get; set; } = () => null;
    /// <summary>沒選過時自動用找得到的 CLI，都沒有就先不整理（R35.1、M§19.6）</summary>
    public LLMProvider DetectedProvider => DetectCli() ?? LLMProvider.None;
    public LLMProvider Provider => DebugProvider ?? ChosenProvider ?? DetectedProvider;
    /// <summary>還沒選過整理工具、也找不到 CLI：介面說「還沒設定整理工具」，不說「不整理」（R35.2）</summary>
    public bool NeedsProviderSetup => DebugProvider is null && ChosenProvider is null && Provider == LLMProvider.None;
    public string Model => DebugModel ?? String(Provider.ModelKey()) ?? "";
    public bool AutoSummarize => Flag(AutoSummarizeKey, true);
    public bool CompressAudio => Flag(CompressAudioKey, true);
    public bool Diarize => Flag(DiarizeKey, true);
    /// <summary>線上會議的麥克風、電腦聲音分開轉錄（M§17.3）；還在評估，設定裡沒有開關</summary>
    public bool SplitChannels => Flag(SplitChannelsKey, false);
    public bool CorrectTranscript => Flag(CorrectTranscriptKey, true);
    public bool LiveTranscription => Flag(LiveTranscriptionKey, true);
    public bool AutoDetectLanguage => Flag(AutoDetectLanguageKey, true);
    public bool GpuPackOffered => Flag(GpuPackOfferedKey, false);
    public bool QwenCudaOffered => Flag(QwenCudaOfferedKey, false);
    /// <summary>mic / system / both</summary>
    public string RecordSource => String(RecordSourceKey) ?? "mic";
    /// <summary>麥克風的端點 ID；空的是系統預設</summary>
    public string InputDevice => String(InputDeviceKey) ?? "";
    public string DefaultTemplate => String(DefaultTemplateKey) ?? "general";
    public bool GlobalHotKey => Flag(GlobalHotKeyKey, true);
    public bool Voiceprints => Flag(VoiceprintsKey, true);
    public bool MeetingDetection => Flag(MeetingDetectionKey, true);
    public bool BookmarkHotKey => Flag(BookmarkHotKeyKey, true);
    /// <summary>正式轉錄用快速辨識（Mac 的「macOS 內建」，D45）：設定值同 Mac 是 system</summary>
    public bool UseFastTranscriber => String(TranscriptionEngineKey) == "system";
    /// <summary>正式轉錄的引擎：breeze（預設）、qwen、system（同 Mac 的 transcriptionEngine，M§32.3）</summary>
    public string TranscriptionEngine => String(TranscriptionEngineKey) ?? "breeze";
    public bool UseQwen => TranscriptionEngine == "qwen";
    public string CompatibleBaseUrl => String(CompatibleBaseUrlKey) ?? "";

    /// <summary>新會議的會議語言（M§16.3）；沒設定時跟著介面語言</summary>
    public MeetingLanguage DefaultMeetingLanguage => MeetingLanguages.Parse(String(DefaultMeetingLanguageKey)) ?? SystemDefaultLanguage;

    /// <summary>同 Mac 的 systemDefault：英文介面而且 Windows 的語言是日文時用日文，其他跟著介面</summary>
    public static MeetingLanguage SystemDefaultLanguage =>
        !L.English ? MeetingLanguage.Zh
        : CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ja" ? MeetingLanguage.Ja : MeetingLanguage.En;
}
