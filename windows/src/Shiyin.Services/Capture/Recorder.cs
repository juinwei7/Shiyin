using Shiyin.Localization;
using Shiyin.Model;
using Shiyin.Speech;
using Shiyin.Transcribe;

namespace Shiyin.Services.Capture;

/// <summary>
/// 錄音（M§5、R1、R2；同 Mac 的 Recorder）：統籌音源、寫檔、即時逐字稿、暫停、音量、收音提醒、自動偵測語言。
/// 方法可以從任何執行緒呼叫；事件在背景執行緒觸發，畫面要自己切回 UI 執行緒（不能同步等 UI 執行緒，會卡住錄音）
/// </summary>
public sealed class Recorder
{
    /// <summary>IdleSince：沒人說話太久而自動停止（R48.2）時，最後有人說話的秒數</summary>
    public sealed record Result(long MeetingId, string? File, double Duration, string? Error, double? IdleSince = null);
    public sealed record LiveLine(double Start, double End, string Text);

    /// <summary>收音提醒（M§26.1、R24.1、R24.3）</summary>
    public enum LevelHint
    {
        /// <summary>麥克風的聲音完全是 0</summary>
        Silent,
        /// <summary>有人在說話，但聲音很小</summary>
        Quiet,
        /// <summary>背景一直很大聲</summary>
        Noisy,
    }

    /// <summary>平台提供的東西：音源、即時辨識、語言偵測、剩餘空間（測試換成假的）</summary>
    public sealed record Hooks(
        Func<string, string, IReadOnlyList<(int Channel, IAudioSource Source)>> MakeSources,
        Func<MeetingLanguage, ILiveTranscriber?> MakeLive,
        Func<float[], Dictionary<MeetingLanguage, double>>? DetectLanguage,
        Func<string, long?> AvailableBytes);

    readonly object gate = new();
    readonly AppSettings settings;
    readonly ShiyinPaths paths;
    readonly IPowerRequests power;
    readonly Hooks hooks;

    public Recorder(AppSettings settings, ShiyinPaths paths, IPowerRequests power, Hooks hooks)
    {
        (this.settings, this.paths, this.power, this.hooks) = (settings, paths, power, hooks);
    }

    // MARK: 狀態（畫面讀；Changed 只在這些值真的變了才觸發，M§25）

    public bool IsRecording { get; private set; }
    public bool IsPaused { get; private set; }
    public long? MeetingId { get; private set; }
    public DateTime? StartedAt { get; private set; }
    /// <summary>精確的錄音秒數（筆記的時間點、標記重點用）。每 0.05 秒變一次，畫面不要觀察這個，讀 ElapsedSeconds</summary>
    public double Elapsed { get; private set; }
    /// <summary>畫面顯示的錄音秒數：整秒變了才更新</summary>
    public int ElapsedSeconds { get; private set; }
    /// <summary>音量（0～1）。不觸發 Changed：音量條自己定時來讀（design-windows §11.2）</summary>
    public float MicLevel { get; private set; }
    public float SystemLevel { get; private set; }
    /// <summary>mic / system / both</summary>
    public string Source { get; private set; } = "mic";
    public MeetingLanguage Language { get; private set; } = MeetingLanguage.Zh;
    public IReadOnlyList<LiveLine> LiveLines { get { lock (gate) return [.. liveLines]; } }
    public string VolatileText { get; private set; } = "";
    public string LiveStatus { get; private set; } = "";
    /// <summary>電腦聲音一直是靜音：沒有東西在播，或擷取不到</summary>
    public bool SystemSilent { get; private set; }
    public bool PermissionDenied { get; set; }
    public string? FailureMessage { get; set; }
    /// <summary>自動偵測換了會議語言（M§18.8）：錄音畫面顯示「改回」</summary>
    public (MeetingLanguage From, MeetingLanguage To)? LanguageSwitch { get; private set; }
    /// <summary>有音源超過 3 秒沒送資料、正在重新連接（M§20.2）</summary>
    public string? DeviceWarning { get; private set; }
    public LevelHint? Hint { get; private set; }
    /// <summary>DEBUG 的 state：收音提醒的數字（M§26.1）</summary>
    public Dictionary<string, object> LevelCheck { get; private set; } = [];
    /// <summary>好像沒人在說話了，要停止錄音嗎？（R48.1、M§35.2）</summary>
    public bool IdlePrompt { get; private set; }
    /// <summary>15 分鐘提醒、30 分鐘自動停止；DEBUG 用 --set idleRemindSeconds=…、idleStopSeconds=… 縮短（AppHost 設）</summary>
    public double IdleRemindSeconds { get; set; } = 900;
    public double IdleStopSeconds { get; set; } = 1800;
    /// <summary>沒人說話多久（DEBUG 的 state）</summary>
    public double IdleSeconds { get { lock (gate) return IsRecording ? Elapsed - lastSpeechAt : 0; } }
    public double LastSpeechAt { get { lock (gate) return lastSpeechAt; } }
    public bool LiveActive { get { lock (gate) return liveActive; } }

    public bool HasMic => Source != "system";
    public bool HasSystem => Source != "mic";

    /// <summary>畫面要重畫的狀態變了</summary>
    public event Action? Changed;
    public event Action<Result>? Finished;
    /// <summary>錄音開始了（全域快捷鍵標記重點這時才註冊，M§21.10）</summary>
    public event Action? Started;
    /// <summary>停止後即時辨識收尾完，給草稿紀錄用（M§21.3）：會議 id、錄到的秒數</summary>
    public event Action<long, double>? LiveFinished;
    public event Action<long, double, double, string>? LiveFinal;
    /// <summary>錄音中換了會議語言（自動偵測或改回），要更新資料庫</summary>
    public event Action<long, MeetingLanguage>? LanguageChanged;

    readonly List<LiveLine> liveLines = [];
    readonly HashSet<LevelHint> dismissedHints = [];
    /// <summary>這場錄音已經通知過的提示（R42.1：每種每場最多一次）</summary>
    readonly HashSet<LevelHint> notifiedHints = [];
    /// <summary>提示第一次出現（M§31.5）：App 決定要不要送通知。跟 Changed 一樣在背景執行緒發</summary>
    public event Action<LevelHint>? HintAppeared;
    /// <summary>沒人說話的提醒出現（App 送通知）。跟 Changed 一樣在背景執行緒發</summary>
    public event Action? IdlePromptAppeared;
    int noisySeconds, quietSeconds;
    /// <summary>忘了停止錄音（M§35.1）：最後有人說話的錄音秒數；即時逐字稿有沒有接上音訊</summary>
    double lastSpeechAt;
    bool liveActive;
    RecordingWriter? writer;
    Dictionary<int, IAudioSource> sources = [];
    readonly Dictionary<int, DateTime> reconnecting = [];
    readonly Dictionary<int, string> reconnectErrors = [];
    readonly Dictionary<int, CancellationTokenSource> pendingRestarts = [];
    DateTime lastDiskCheck = DateTime.MinValue;
    bool warnedLowDisk;
    bool simulationEnded;
    FileSource? simulation;
    ILiveTranscriber? live;
    readonly MixTap tap = new();
    Timer? timer;
    IDisposable? keepAwake;
    long generation;

    void Notify() => Changed?.Invoke();

    // MARK: 開始、停止

    public void Start(long meetingId, MeetingLanguage language, string? source = null)
    {
        lock (gate)
        {
            if (IsRecording) return;
            Source = source ?? settings.RecordSource;
            Language = language;
            simulationEnded = false;
            simulation = null;
            Begin(meetingId, () => hooks.MakeSources(Source, settings.InputDevice));
        }
    }

    /// <summary>DEBUG：用音訊檔模擬錄音（record-sim，speed 是倍速）</summary>
    public void StartSimulated(long meetingId, string file, double speed, bool both, MeetingLanguage language, IAudioDecoder decoder)
    {
        lock (gate)
        {
            if (IsRecording) return;
            Source = both ? "both" : "mic";
            Language = language;
            simulationEnded = false;
            Begin(meetingId, () =>
            {
                var fileSource = new FileSource(file, speed, decoder);
                fileSource.OnEnded = () => { lock (gate) simulationEnded = true; };
                simulation = fileSource;
                var list = new List<(int, IAudioSource)> { (0, fileSource) };
                if (both)
                {
                    var mirror = new MirroredSource();
                    fileSource.Mirror = mirror;
                    list.Add((1, mirror));
                }
                return list;
            });
        }
    }

    /// <summary>DEBUG 的 recordstall：某個聲道停止送資料幾秒，null 是馬上恢復（M§20.2）</summary>
    public void SimulateStall(int channel, double? seconds) => simulation?.Stall(channel, seconds);

    /// <summary>DEBUG 的 recordinterrupt：等於音源發現裝置變了。連續送幾次，驗證只會重新開始一次</summary>
    public void SimulateInterruption(int channel, int times = 3)
    {
        IAudioSource? source;
        lock (gate) source = sources.GetValueOrDefault(channel);
        for (var i = 0; i < times; i++) source?.OnInterruption?.Invoke();
    }

    public void Pause()
    {
        lock (gate)
        {
            if (!IsRecording || IsPaused) return;
            writer?.SetPaused(true);
            IsPaused = true;
        }
        Notify();
    }

    public void Resume()
    {
        lock (gate)
        {
            if (!IsRecording || !IsPaused) return;
            writer?.ResetRecentLevels();
            (noisySeconds, quietSeconds) = (0, 0);
            writer?.SetPaused(false);
            IsPaused = false;
            MarkSpeechLocked();   // 按了繼續表示人還在，重新計時（M§35.1）
        }
        Notify();
    }

    /// <summary>「繼續錄」：人還在，重新計時（R48.1）</summary>
    public void ContinueAfterIdle()
    {
        lock (gate)
        {
            if (!IsRecording) return;
            MarkSpeechLocked();
        }
        Notify();
    }

    /// <summary>有人在說話：重新計時、提醒收掉。回傳提醒有沒有收掉（畫面要重畫）</summary>
    bool MarkSpeechLocked()
    {
        lastSpeechAt = Elapsed;
        if (!IdlePrompt) return false;
        IdlePrompt = false;
        return true;
    }

    /// <summary>每秒檢查沒人說話多久（M§35）：回傳畫面要不要重畫、要不要自動停止</summary>
    (bool Changed, bool Stop) CheckIdle(RecordingWriter w)
    {
        if (!settings.IdleStop || IsPaused) return (false, false);
        // 即時逐字稿沒在跑時才看聲音（打字、關門也會算，只當備用，D115）
        var changed = !liveActive && SoundsLikeSpeech(w) && MarkSpeechLocked();
        var idle = Elapsed - lastSpeechAt;
        if (idle >= IdleStopSeconds) return (changed, true);
        var prompt = idle >= IdleRemindSeconds;
        if (prompt == IdlePrompt) return (changed, false);
        IdlePrompt = prompt;
        if (prompt) IdlePromptAppeared?.Invoke();
        return (true, false);
    }

    /// <summary>最近 1 秒有 30% 以上的音框像說話（M§35.1）：比背景（最近 30 秒的第 10 百分位）大 12 dB 而且高於 -50 dBFS，
    /// 或本身就高於 -40 dBFS（一開始就一直有人說話時，背景也是說話的音量）</summary>
    bool SoundsLikeSpeech(RecordingWriter w)
    {
        for (var channel = 0; channel < (Source == "both" ? 2 : 1); channel++)
        {
            var levels = w.RecentLevels(channel);
            if (levels.Length < 20) continue;
            var sorted = (float[])levels.Clone();
            Array.Sort(sorted);
            var threshold = Math.Min(Math.Max(sorted[(int)((sorted.Length - 1) * 0.1)] + 12, -50), -40);
            var last = levels[^10..];
            if (last.Count(l => l > threshold) >= last.Length * 0.3) return true;
        }
        return false;
    }

    /// <summary>「知道了」：這一種提示這場錄音不再出現</summary>
    public void DismissLevelHint()
    {
        lock (gate)
        {
            if (Hint is not LevelHint hint) return;
            dismissedHints.Add(hint);
            Hint = null;
        }
        Notify();
    }

    public void Stop() => Stop(null);

    /// <summary>idleSince：沒人說話太久而自動停止（R48.2），最後有人說話的秒數</summary>
    public void Stop(double? idleSince)
    {
        Result result;
        ILiveTranscriber? finishing;
        double duration;
        long meetingId;
        lock (gate)
        {
            if (!IsRecording || MeetingId is not long id || writer is null) return;
            meetingId = id;
            foreach (var s in sources.Values) s.Stop();
            sources = [];
            duration = writer.Finish();
            var file = writer.Path;
            if (writer.WriteFailed)
            {
                // 寫不進去時標頭的大小可能沒更新，照實際的檔案大小補上（復原流程也是這樣修）
                try { WavRepair.Repair(file); } catch (Exception e) when (e is IOException or WavRepair.RepairException) { }
                FailureMessage = L.T("錄音寫不進磁碟（可能是空間不足），已經停止錄音。停止前錄到的部分有保留，會照常轉錄。");
            }
            finishing = live;
            Cleanup();
            result = new Result(meetingId, file, duration, null, idleSince);
        }
        Log.Info($"recording stopped, {(int)duration}s");
        _ = Task.Run(() =>
        {
            try { finishing?.Finish(); } catch (Exception e) { Log.Error($"live finish failed: {e.GetType().Name}"); }
            finishing?.Dispose();
            LiveFinished?.Invoke(meetingId, duration);
        });
        Notify();
        Finished?.Invoke(result);
    }

    void Begin(long meetingId, Func<IReadOnlyList<(int Channel, IAudioSource Source)>> makeSources)
    {
        try
        {
            paths.EnsureDirectories();
            // 檔名帶會議 id：當掉後可以接回原本的會議（復原流程）
            var path = System.IO.Path.Combine(paths.Recording, $"{meetingId}-{Guid.NewGuid()}.wav");
            var channels = Source == "both" ? 2 : 1;
            var newWriter = new RecordingWriter(path, channels);
            var started = new Dictionary<int, IAudioSource>();
            var warnings = new List<string>();
            var denied = false;
            // 先接好每個聲道的接收，再開始：模擬錄音的第二個聲道是第一個的鏡像（MirroredSource），第一個一開始就送資料，
            // 第二個還沒接上的話開頭那幾段會丟掉，兩個聲道對不齊（2026-10-22 CI 的 macOS 機器比較慢時看到）
            var made = makeSources();
            foreach (var (channel, source) in made)
            {
                var c = channel;
                source.OnSamples = samples => newWriter.Append(c, samples);
                source.OnInterruption = () => ScheduleRestart(c);
            }
            foreach (var (channel, source) in made)
            {
                try
                {
                    source.Start();
                    started[channel] = source;
                }
                catch (Exception e)
                {
                    if (e is CaptureException { AccessDenied: true }) denied = true;
                    warnings.Add(e.Message);
                }
            }
            if (started.Count == 0)
            {
                newWriter.Finish();
                File.Delete(path);
                if (denied)
                {
                    Abort(meetingId, permission: true);
                    return;
                }
                throw new CaptureException(warnings.FirstOrDefault() ?? L.T("開始錄音"));
            }
            if (warnings.Count > 0) FailureMessage = L.F("有一個音源沒辦法啟動，只錄到另一個：%@", string.Join(L.T("；"), warnings));
            newWriter.OnMix = tap.Handle;
            newWriter.Start();
            writer = newWriter;
            sources = started;
            warnedLowDisk = false;
            CheckDiskSpace(newWriter, starting: true);
            MeetingId = meetingId;
            StartedAt = DateTime.Now;
            Elapsed = 0;
            ElapsedSeconds = 0;
            Hint = null;
            dismissedHints.Clear();
            notifiedHints.Clear();
            (noisySeconds, quietSeconds) = (0, 0);
            lastSpeechAt = 0;
            liveActive = false;
            IdlePrompt = false;
            liveLines.Clear();
            VolatileText = "";
            SystemSilent = false;
            IsPaused = false;
            IsRecording = true;
            generation++;
            keepAwake = power.KeepAwake(L.T("拾音正在錄音"));
            var current = generation;
            timer = new Timer(_ => Tick(current), null, 50, 50);
            if (settings.LiveTranscription) StartLive(newWriter, meetingId, restarted: false);
            StartLanguageDetection(meetingId);
            Log.Info($"recording started ({Source})");
        }
        catch (Exception e)
        {
            Abort(meetingId, message: L.F("沒辦法開始錄音：%@", e.Message));
            return;
        }
        Notify();
        Started?.Invoke();
    }

    void StartLive(RecordingWriter w, long meetingId, bool restarted)
    {
        var language = Language;
        var current = generation;
        LiveStatus = L.T("正在準備即時逐字稿…");
        liveActive = false;
        _ = Task.Run(() =>
        {
            ILiveTranscriber? created;
            try { created = hooks.MakeLive(language); }
            catch (Exception e)
            {
                lock (gate) if (generation == current && Language == language)
                    LiveStatus = L.F("即時逐字稿沒辦法啟動（%@），錄音照常進行，錄完會正式轉錄", e.Message);
                Notify();
                return;
            }
            if (created is null)
            {
                lock (gate) if (generation == current) LiveStatus = "";
                Notify();
                return;
            }
            lock (gate)
            {
                // 準備期間錄音停了、或又換了語言
                if (generation != current || !IsRecording || Language != language)
                {
                    created.Dispose();
                    return;
                }
                live = created;
                // 中途重新開始時，新的辨識從 0 秒算，要加上接上音訊時已經錄了多久
                if (restarted) created.Offset = w.RecordedSeconds;
                created.Volatile += text =>
                {
                    lock (gate)
                    {
                        if (live != created) return;
                        // 有新的字就是有人在說話（M§35.1）
                        if (text.Length > 0 && text != VolatileText) MarkSpeechLocked();
                        VolatileText = text;
                    }
                    Notify();
                };
                created.Final += (start, end, text) =>
                {
                    lock (gate)
                    {
                        if (live != created) return;
                        liveLines.Add(new LiveLine(start, end, text));
                        if (text.Length > 0) MarkSpeechLocked();
                    }
                    LiveFinal?.Invoke(meetingId, start, end, text);
                    Notify();
                };
                tap.SetLive(created);
                LiveStatus = "";
                liveActive = true;
            }
            Notify();
        });
    }

    // MARK: 自動偵測主要語言（M§18.8、R16、D21）

    /// <summary>開始錄音後收集約 10 秒有聲音的片段，判斷是中文、英文還是日文；跟目前不同而且有把握才切換，只判斷一次</summary>
    void StartLanguageDetection(long meetingId)
    {
        LanguageSwitch = null;
        if (!settings.AutoDetectLanguage || hooks.DetectLanguage is not { } detect) return;
        var current = generation;
        tap.SetDetector(new LanguageCollector(samples => _ = Task.Run(() =>
        {
            Dictionary<MeetingLanguage, double>? probabilities = null;
            try { probabilities = detect(samples); }
            catch (Exception e) { Log.Error($"language detection failed: {e.GetType().Name}"); }
            ApplyDetection(probabilities, meetingId, current);
        })));
    }

    void ApplyDetection(Dictionary<MeetingLanguage, double>? probabilities, long meetingId, long current)
    {
        lock (gate)
        {
            tap.SetDetector(null);
            if (!IsRecording || MeetingId != meetingId || generation != current || probabilities is null || probabilities.Count == 0) return;
            Log.Info("language detection: " + string.Join(", ", probabilities.OrderBy(p => p.Key.Code(), StringComparer.Ordinal)
                .Select(p => $"{p.Key.Code()} {p.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}")));
            var best = probabilities.OrderByDescending(p => p.Value).First();
            if (best.Key == Language || best.Value < LanguageCollector.Threshold) return;
            var from = Language;
            SwitchLanguageLocked(best.Key);
            LanguageSwitch = (from, best.Key);
        }
        Notify();
    }

    /// <summary>錄音中換會議語言：即時逐字稿用新的語言重新開始（之前的逐字稿保留，錄完會正式轉錄）</summary>
    public void SwitchLanguage(MeetingLanguage language)
    {
        lock (gate) SwitchLanguageLocked(language);
        Notify();
    }

    void SwitchLanguageLocked(MeetingLanguage language)
    {
        if (!IsRecording || MeetingId is not long id || writer is null || language == Language) return;
        Language = language;
        LanguageSwitch = null;
        tap.SetDetector(null);
        LanguageChanged?.Invoke(id, language);
        if (!settings.LiveTranscription) return;
        tap.SetLive(null);
        // 舊的辨識收尾送來的是用錯的語言聽的，不加進逐字稿（live 換掉後它的事件會被略過）
        var old = live;
        live = null;
        liveActive = false;
        VolatileText = "";
        if (old is not null) _ = Task.Run(() => { old.Finish(); old.Dispose(); });
        StartLive(writer, id, restarted: true);
    }

    // MARK: 每 0.05 秒

    void Tick(long current)
    {
        var changed = false;
        var stop = false;
        double? idleSince = null;
        lock (gate)
        {
            if (generation != current || writer is not { } w) return;
            if (w.WriteFailed) stop = true;
            else
            {
                Elapsed = w.RecordedSeconds;
                var seconds = (int)Elapsed;
                if (seconds != ElapsedSeconds)
                {
                    ElapsedSeconds = seconds;
                    changed = true;
                    changed |= CheckLevels(w);
                    var idle = CheckIdle(w);
                    changed |= idle.Changed;
                    if (idle.Stop)
                    {
                        Log.Info($"recording auto-stopped after {(int)(Elapsed - lastSpeechAt)}s without speech");
                        (stop, idleSince) = (true, lastSpeechAt);
                    }
                }
                var silent = SystemSilent;
                if (Source == "both")
                {
                    MicLevel = w.Level(0);
                    SystemLevel = w.Level(1);
                    silent = w.Silence(1) > 15;
                }
                else if (Source == "system")
                {
                    SystemLevel = w.Level(0);
                    silent = w.Silence(0) > 15;
                }
                else MicLevel = w.Level(0);
                if (silent != SystemSilent) { SystemSilent = silent; changed = true; }
                changed |= Watchdog(w);
                if (DateTime.UtcNow - lastDiskCheck >= TimeSpan.FromSeconds(30)) changed |= CheckDiskSpace(w, starting: false);
            }
        }
        if (stop) Stop(idleSince);
        else if (changed) Notify();
    }

    /// <summary>收音提醒（M§26.1）：每秒看麥克風最近 30 秒的音量。第 90 百分位是說話的音量、第 10 百分位是背景</summary>
    bool CheckLevels(RecordingWriter w)
    {
        if (!HasMic || IsPaused) return false;
        var silent = w.Silence(0) > 10;
        var levels = w.RecentLevels(0);
        Array.Sort(levels);
        float speech = 0, background = 0;
        if (levels.Length >= 300)
        {
            speech = levels[(int)((levels.Length - 1) * 0.9)];
            background = levels[(int)((levels.Length - 1) * 0.1)];
            noisySeconds = background > -36 ? noisySeconds + 1 : 0;
            // 說話比背景大 4 dB 以上才算有人在說話（完全安靜時聲音很平），不是安靜的停頓
            quietSeconds = speech < -50 && speech - background >= 4 ? quietSeconds + 1 : 0;
        }
        LevelHint? hint = silent ? LevelHint.Silent : quietSeconds >= 60 ? LevelHint.Quiet : noisySeconds >= 45 ? LevelHint.Noisy : null;
        var shown = hint is LevelHint h && dismissedHints.Contains(h) ? null : hint;
        LevelCheck = new()
        {
            ["speech"] = speech, ["background"] = background, ["frames"] = levels.Length, ["silentSeconds"] = w.Silence(0),
            ["noisySeconds"] = noisySeconds, ["quietSeconds"] = quietSeconds, ["hint"] = shown?.ToString().ToLowerInvariant() ?? "",
        };
        if (shown == Hint) return false;
        Hint = shown;
        // 第一次出現才通知（R42.1）；提示消失又出現不再通知
        if (shown is LevelHint appeared && notifiedHints.Add(appeared)) HintAppeared?.Invoke(appeared);
        return true;
    }

    /// <summary>開始錄音時剩不到 1 GB、錄音中剩不到 300 MB 就提醒（錄音照常進行；寫不進去時 Tick 會停止錄音）</summary>
    bool CheckDiskSpace(RecordingWriter w, bool starting)
    {
        lastDiskCheck = DateTime.UtcNow;
        if (hooks.AvailableBytes(paths.Recording) is not long available) return false;
        if (available >= (starting ? 1_000_000_000 : 300_000_000) || (!starting && warnedLowDisk)) return false;
        if (!starting) warnedLowDisk = true;
        // 16 kHz、16 位元：單聲道每小時約 115 MB，雙聲道約 230 MB
        var bytesPerMinute = RecordingWriter.SampleRate * 2 * w.Channels * 60;
        var minutes = (int)(available / bytesPerMinute);
        Log.Info($"low disk space: {available / 1_000_000} MB");
        var message = L.F("磁碟只剩 %@，大約只能錄 %lld 分鐘。空間用完時會自動停止錄音，已經錄到的部分會保留。", ByteCount(available), minutes);
        FailureMessage = FailureMessage is null ? message : FailureMessage + "\n\n" + message;
        return true;
    }

    /// <summary>檔案大小的寫法（同 Mac 的 ByteCountFormatter .file：十進位）</summary>
    public static string ByteCount(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1) { value /= 1000; unit++; }
        return unit == 0 ? $"{bytes} bytes" : value.ToString(unit >= 3 ? "0.##" : "0.#", System.Globalization.CultureInfo.InvariantCulture) + " " + units[unit];
    }

    // MARK: 換裝置、看門狗（M§20.2、R18.2、D25）

    /// <summary>裝置切換時通知會連續來好幾次，等 0.3 秒沒有新的通知再重新開始</summary>
    void ScheduleRestart(int channel)
    {
        CancellationTokenSource cts;
        lock (gate)
        {
            if (!IsRecording) return;
            pendingRestarts.GetValueOrDefault(channel)?.Cancel();
            pendingRestarts[channel] = cts = new CancellationTokenSource();
        }
        _ = Task.Delay(300, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            lock (gate)
            {
                if (pendingRestarts.GetValueOrDefault(channel) != cts) return;
                pendingRestarts.Remove(channel);
                RestartSource(channel);
            }
            Notify();
        }, TaskScheduler.Default);
    }

    void RestartSource(int channel)
    {
        if (!IsRecording || !sources.TryGetValue(channel, out var source)) return;
        try
        {
            source.Restart();
            reconnectErrors.Remove(channel);
            Log.Info($"audio source {channel} restarted");
        }
        catch (Exception e)
        {
            // 錄音不停：另一個音源照常錄，雙聲道時這段會補靜音；看門狗每 5 秒再試
            reconnectErrors[channel] = e.Message;
            Log.Error($"audio source {channel} restart failed: {e.GetType().Name}");
        }
        UpdateDeviceWarning();
    }

    /// <summary>音源超過 3 秒沒送資料：提示並每 5 秒試一次重新開始；收到資料後提示消失</summary>
    bool Watchdog(RecordingWriter w)
    {
        if (simulationEnded) return false;
        var now = DateTime.UtcNow;
        var before = DeviceWarning;
        foreach (var channel in sources.Keys.Order().ToList())
        {
            if (w.SecondsSinceSamples(channel) > 3)
            {
                if (reconnecting.TryGetValue(channel, out var last) && now - last < TimeSpan.FromSeconds(5)) continue;
                if (!reconnecting.ContainsKey(channel)) Log.Info($"audio source {channel} stalled");
                reconnecting[channel] = now;
                RestartSource(channel);
            }
            else if (reconnecting.Remove(channel))
            {
                reconnectErrors.Remove(channel);
                Log.Info($"audio source {channel} recovered");
                UpdateDeviceWarning();
            }
        }
        return before != DeviceWarning;
    }

    void UpdateDeviceWarning()
    {
        string? warning = null;
        if (reconnecting.Count > 0)
        {
            var channel = reconnecting.Keys.Min();
            var mic = Source != "system" && channel == 0;
            warning = reconnectErrors.TryGetValue(channel, out var reason)
                ? mic ? L.F("麥克風沒有送出聲音，重新連接失敗：%@。會繼續重試，錄音沒有停", reason)
                      : L.F("電腦聲音的擷取中斷了，重新連接失敗：%@。會繼續重試，錄音沒有停", reason)
                : mic ? L.T("麥克風沒有送出聲音，正在重新連接…") : L.T("電腦聲音的擷取中斷了，正在重新連接…");
        }
        DeviceWarning = warning;
    }

    void Abort(long meetingId, bool permission = false, string? message = null)
    {
        if (permission) PermissionDenied = true;
        if (message is not null) FailureMessage = message;
        var result = new Result(meetingId, null, 0, null);
        _ = Task.Run(() =>
        {
            Notify();
            Finished?.Invoke(result);
        });
    }

    void Cleanup()
    {
        timer?.Dispose();
        timer = null;
        generation++;
        foreach (var p in pendingRestarts.Values) p.Cancel();
        pendingRestarts.Clear();
        reconnecting.Clear();
        reconnectErrors.Clear();
        DeviceWarning = null;
        keepAwake?.Dispose();
        keepAwake = null;
        writer = null;
        live = null;
        liveActive = false;
        IdlePrompt = false;
        simulation = null;
        tap.SetLive(null);
        tap.SetDetector(null);
        LanguageSwitch = null;
        Hint = null;
        MeetingId = null;
        StartedAt = null;
        IsRecording = false;
        IsPaused = false;
        MicLevel = 0;
        SystemLevel = 0;
        VolatileText = "";
        LiveStatus = "";
    }

    /// <summary>錄音的單聲道混音要同時送給即時逐字稿和語言偵測；寫檔的執行緒呼叫 Handle</summary>
    sealed class MixTap
    {
        readonly object gate = new();
        ILiveTranscriber? live;
        LanguageCollector? detector;

        public void SetLive(ILiveTranscriber? value) { lock (gate) live = value; }
        public void SetDetector(LanguageCollector? value) { lock (gate) detector = value; }

        public void Handle(float[] samples)
        {
            ILiveTranscriber? l;
            LanguageCollector? d;
            lock (gate) (l, d) = (live, detector);
            l?.Feed(samples);
            d?.Feed(samples);
        }
    }
}

/// <summary>收音提醒的文字（R42）：錄音畫面、系統匣、通知共用（同 Mac 的 Recorder.LevelHint.text）</summary>
public static class LevelHints
{
    public static string Text(this Recorder.LevelHint hint) => hint switch
    {
        Recorder.LevelHint.Silent => L.T("麥克風沒有收到聲音：可能被靜音、被其他 app 佔用，或選錯了麥克風。可以在設定的「錄音」換一個。"),
        Recorder.LevelHint.Quiet => L.T("聲音有點小：手機或筆電放近說話的人一點。"),
        _ => L.T("背景有點吵，說話者可能分不準。錄完可以用「重新辨識說話者」告訴拾音大約幾個人。"),
    };
}
