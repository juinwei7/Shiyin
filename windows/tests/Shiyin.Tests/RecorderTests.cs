using System.Buffers.Binary;
using Shiyin.IO;
using Shiyin.Model;
using Shiyin.Services;
using Shiyin.Services.Capture;
using Shiyin.Speech;
using Xunit;

namespace Shiyin.Tests;

/// <summary>錄音的測試不跟其他測試同時跑：看時間的測試在忙的 CI 機器上會被拖慢（2026-10-22 CI 的 macOS 失敗過）</summary>
[CollectionDefinition(nameof(RecorderTests), DisableParallelization = true)]
public sealed class RecorderCollection;

/// <summary>錄音（M§5、§20.2、§26；design-windows §6）：用模擬錄音（同 Mac 的 record-sim）在 Mac 上測</summary>
[Collection(nameof(RecorderTests))]
public sealed class RecorderTests : IDisposable
{
    readonly TempHome home = new();
    readonly ShiyinPaths paths;

    public RecorderTests()
    {
        paths = new ShiyinPaths(home.Path);
        paths.EnsureDirectories();
    }

    public void Dispose() => home.Dispose();

    /// <summary>每 blockSeconds 秒交替大聲、小聲（語言偵測要比背景大聲的片段）</summary>
    string Wav(double seconds, double blockSeconds = 0, float loud = 0.2f, float quiet = 0.2f)
    {
        var path = Path.Combine(home.Path, Guid.NewGuid() + ".wav");
        var count = (int)(seconds * 16000);
        var data = new float[count];
        for (var i = 0; i < count; i++)
        {
            var block = blockSeconds > 0 ? (int)(i / (blockSeconds * 16000)) : 0;
            data[i] = (block % 2 == 0 ? loud : quiet) * MathF.Sin(i * 0.07f);
        }
        using var writer = new WavWriter(path, 1);
        writer.Write([data], count);
        return path;
    }

    sealed class FakeLive : ILiveTranscriber
    {
        public event Action<string>? Volatile;
        public event Action<double, double, string>? Final;
        public double Offset { get; set; }
        long fed;
        public bool Finished { get; private set; }

        public void Feed(ReadOnlySpan<float> samples)
        {
            var before = fed / 16000;
            fed += samples.Length;
            Volatile?.Invoke("說話中");
            // 每 1 秒定稿一句
            if (fed / 16000 > before) Final?.Invoke(Offset + before, Offset + before + 1, $"第 {before + 1} 句");
        }

        public void Finish() => Finished = true;
        public void Dispose() { }
    }

    sealed class FailingSource(bool denied) : IAudioSource
    {
        public Action<float[]>? OnSamples { get; set; }
        public Action? OnInterruption { get; set; }
        public void Start() => throw new CaptureException("沒有權限", denied);
        public void Stop() { }
    }

    Recorder Make(AppSettings settings, List<FakeLive>? lives = null, Func<float[], Dictionary<MeetingLanguage, double>>? detect = null,
                  long? available = 50_000_000_000, IAudioSource? source = null) =>
        new(settings, paths, NoPower.Instance, new Recorder.Hooks(
            (_, _) => source is null ? [] : [(0, source)],
            _ =>
            {
                var live = new FakeLive();
                lock (lives ?? []) lives?.Add(live);
                return live;
            },
            detect, _ => available));

    static async Task Until(Func<bool> condition, int seconds = 20)
    {
        for (var i = 0; i < seconds * 50 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition());
    }

    static (int Channels, long Frames, float[][] Data) Read(string path)
    {
        using var reader = new WavReader(path);
        var frames = reader.Frames;
        return (reader.Channels, frames, reader.Read((int)frames));
    }

    [Fact]
    public async Task SimulatedRecordingWritesBothChannelsAndLiveTranscript()
    {
        var lives = new List<FakeLive>();
        var recorder = Make(AppSettings.InMemory(new Dictionary<string, string> { ["autoDetectLanguage"] = "NO" }), lives);
        var finals = new List<string>();
        Recorder.Result? result = null;
        var liveDone = new TaskCompletionSource<double>();
        recorder.LiveFinal += (_, _, _, text) => { lock (finals) finals.Add(text); };
        recorder.Finished += r => result = r;
        recorder.LiveFinished += (_, d) => liveDone.TrySetResult(d);
        // 12 秒的錄音（8 倍速約 1.5 秒）：即時逐字稿是在背景建好才開始收音，機器忙的時候會晚一點接上，
        // 錄音太短的話收到的不夠 3 秒、等不到 3 句（4 秒的錄音在 CI 的 macOS 上失敗過）
        recorder.StartSimulated(7, Wav(12), 8, both: true, MeetingLanguage.Zh, new WavDecoder());
        Assert.True(recorder.IsRecording);
        Assert.Equal("both", recorder.Source);
        await Until(() => recorder.Elapsed >= 11.95);
        await Until(() => recorder.LiveLines.Count >= 3);
        recorder.Stop();
        Assert.False(recorder.IsRecording);
        Assert.NotNull(result);
        Assert.Equal(7, result!.MeetingId);
        Assert.StartsWith(Path.Combine(paths.Recording, "7-"), result.File);
        Assert.Equal(12, result.Duration, 1);
        var (channels, frames, data) = Read(result.File!);
        Assert.Equal(2, channels);
        Assert.Equal(192000, frames);
        Assert.Equal(data[0], data[1]);
        Assert.Equal(12, await liveDone.Task.WaitAsync(TimeSpan.FromSeconds(5)), 1);
        Assert.True(lives.Single().Finished);
        Assert.Contains("第 1 句", finals);
    }

    [Fact]
    public async Task PauseDropsAudio()
    {
        var recorder = Make(AppSettings.InMemory(new Dictionary<string, string> { ["liveTranscription"] = "NO", ["autoDetectLanguage"] = "NO" }));
        Recorder.Result? result = null;
        recorder.Finished += r => result = r;
        recorder.StartSimulated(1, Wav(6), 4, both: false, MeetingLanguage.Zh, new WavDecoder());
        // 只錄麥克風時不能單獨暫停麥克風（R116.1，用「暫停」）
        Assert.False(recorder.CanPauseMic);
        recorder.SetMicPaused(true);
        Assert.False(recorder.MicPaused);
        await Until(() => recorder.Elapsed >= 1);
        recorder.Pause();
        Assert.True(recorder.IsPaused);
        var paused = recorder.Elapsed;
        await Task.Delay(500);       // 4 倍速：約 2 秒的音訊丟掉
        // 暫停前收進來、還在排隊寫入的幾段（每段 0.1 秒）會晚一點才算進去：慢的機器（CI 的 ARM）會到 0.2 秒；
        // 沒有丟掉的話會多出約 2 秒，0.5 秒以內就分得出來
        Assert.InRange(recorder.Elapsed, paused, paused + 0.5);
        recorder.Resume();
        await Task.Delay(1500);
        recorder.Stop();
        Assert.InRange(result!.Duration, 2.5, 4.5);
        Assert.Equal((long)Math.Round(result.Duration * 16000), Read(result.File!).Frames);
    }

    [Fact]
    public async Task MicPauseWritesSilenceOnMicChannelOnly()
    {
        var recorder = Make(AppSettings.InMemory(new Dictionary<string, string> { ["liveTranscription"] = "NO", ["autoDetectLanguage"] = "NO" }));
        Recorder.Result? result = null;
        recorder.Finished += r => result = r;
        // 兩聲道是同一個音檔（同 Mac 的 record-sim）：沒暫停的地方兩邊一樣
        recorder.StartSimulated(1, Wav(8), 4, both: true, MeetingLanguage.Zh, new WavDecoder());
        Assert.True(recorder.CanPauseMic);
        await Until(() => recorder.Elapsed >= 2);
        recorder.SetMicPaused(true);
        Assert.True(recorder.MicPaused);
        await Until(() => recorder.Elapsed >= 5);
        recorder.SetMicPaused(false);
        await Until(() => recorder.Elapsed >= 7.95);
        recorder.Stop();
        var (_, frames, data) = Read(result!.File!);
        // 長度不變：暫停麥克風時那一聲道寫 0，不是丟掉（R116.1）
        Assert.Equal(8 * 16000, frames);
        // 暫停的那段（3～4 秒，離切換的時間點遠一點）麥克風全是 0、電腦聲音照常
        Assert.All(data[0][(3 * 16000)..(4 * 16000)], s => Assert.Equal(0f, s));
        Assert.Contains(data[1][(3 * 16000)..(4 * 16000)], s => s != 0);
        Assert.Equal(data[1][..16000], data[0][..16000]);
        Assert.Equal(data[1][^8000..], data[0][^8000..]);
    }

    [Fact]
    public async Task DetectsLanguageAndSwitchesLiveTranscript()
    {
        var lives = new List<FakeLive>();
        float[]? detected = null;
        var recorder = Make(AppSettings.InMemory(), lives, samples =>
        {
            detected = samples;
            return new() { [MeetingLanguage.Zh] = 0.1, [MeetingLanguage.En] = 0.85, [MeetingLanguage.Ja] = 0.05 };
        });
        var changes = new List<MeetingLanguage>();
        recorder.LanguageChanged += (_, l) => { lock (changes) changes.Add(l); };
        recorder.StartSimulated(3, Wav(30, blockSeconds: 1, loud: 0.2f, quiet: 0.002f), 20, both: false, MeetingLanguage.Zh, new WavDecoder());
        await Until(() => recorder.LanguageSwitch is not null);
        Assert.Equal((MeetingLanguage.Zh, MeetingLanguage.En), recorder.LanguageSwitch);
        Assert.Equal(MeetingLanguage.En, recorder.Language);
        Assert.Equal([MeetingLanguage.En], changes);
        // 湊滿 10 秒有聲音的片段才交出去；20 倍速時寫檔一次送 2 秒，會多一點
        Assert.InRange(detected!.Length / 16000.0, 9.9, 12.1);
        // 新的即時辨識從錄到的秒數接著算
        await Until(() => lives.Count == 2 && lives[1].Offset > 0);
        recorder.Stop();
    }

    static readonly MicrophoneDevice AirPods = new("bt", "AirPods Pro", IsBluetooth: true);
    static readonly MicrophoneDevice BuiltIn = new("in", "麥克風陣列", IsBuiltIn: true);

    Recorder MakeWithMicrophones(AppSettings settings) =>
        new(settings, paths, NoPower.Instance, new Recorder.Hooks((_, _) => [], _ => null, null, _ => 50_000_000_000, () => [AirPods, BuiltIn], () => AirPods));

    static AppSettings Quiet(Dictionary<string, string>? extra = null)
    {
        var values = new Dictionary<string, string> { ["liveTranscription"] = "NO", ["autoDetectLanguage"] = "NO" };
        foreach (var (k, v) in extra ?? []) values[k] = v;
        return AppSettings.InMemory(values);
    }

    /// <summary>藍牙耳機的提醒、錄音中換麥克風（R58.2～R58.4、M§38.3、§38.4）：用模擬錄音和假的麥克風清單（同 DEBUG 的 fakemic）</summary>
    [Fact]
    public async Task BluetoothMicHintAndSwitchingMicrophone()
    {
        var settings = Quiet();
        var recorder = MakeWithMicrophones(settings);
        var appeared = new List<Recorder.MicHintInfo>();
        recorder.MicHintAppeared += h => { lock (appeared) appeared.Add(h); };
        Recorder.Result? result = null;
        recorder.Finished += r => result = r;
        recorder.StartSimulated(1, Wav(8), 2, both: false, MeetingLanguage.Zh, new WavDecoder());
        // 系統預設是藍牙耳機、只錄麥克風：提醒，可以改用內建麥克風
        Assert.Equal(AirPods, recorder.MicDevice);
        Assert.Equal(new Recorder.MicHintInfo("AirPods Pro", BuiltIn), recorder.MicHint);
        Assert.Contains("AirPods Pro", recorder.MicHint!.Text);
        Assert.Single(appeared);
        await Until(() => recorder.Elapsed > 0.5);
        // 「改用」：存成設定、換成內建、提醒收起來，錄音不中斷
        recorder.SwitchMicrophone(BuiltIn.Id);
        Assert.Equal("in", settings.InputDevice);
        Assert.Equal(BuiltIn, recorder.MicDevice);
        Assert.Null(recorder.MicHint);
        var before = recorder.Elapsed;
        await Task.Delay(400);
        Assert.True(recorder.Elapsed > before);
        // 換回系統預設：又是藍牙，提醒再出現，但同一場不再通知
        recorder.SwitchMicrophone("");
        Assert.Equal("", settings.InputDevice);
        Assert.NotNull(recorder.MicHint);
        Assert.Single(appeared);
        // 「知道了」之後這場不再出現
        recorder.DismissMicHint();
        Assert.Null(recorder.MicHint);
        recorder.SwitchMicrophone(BuiltIn.Id);
        recorder.SwitchMicrophone("");
        Assert.Null(recorder.MicHint);
        recorder.Stop();
        Assert.Null(recorder.MicDevice);
        // 換了三次麥克風，錄到的長度照樣是整段（2 倍速的 8 秒，停的時候還沒送完的不算）
        Assert.Equal((long)Math.Round(result!.Duration * 16000), Read(result.File!).Frames);
    }

    [Theory]
    [InlineData("bt", false)]      // 自己指定那支藍牙耳機：選了就是知道（R58.4）
    [InlineData("", true)]         // 線上會議（有電腦聲音）：戴耳機說話是正常的
    public void NoBluetoothHintWhenChosenOrOnlineMeeting(string device, bool both)
    {
        var settings = Quiet(device.Length > 0 ? new() { ["inputDeviceUID"] = device } : null);
        var recorder = MakeWithMicrophones(settings);
        var appeared = 0;
        recorder.MicHintAppeared += _ => appeared++;
        recorder.StartSimulated(2, Wav(4), 1, both, MeetingLanguage.Zh, new WavDecoder());
        Assert.Equal(AirPods, recorder.MicDevice);
        Assert.Null(recorder.MicHint);
        Assert.Equal(0, appeared);
        recorder.Stop();
    }

    [Fact]
    public async Task WatchdogWarnsWhenSourceStalls()
    {
        var recorder = Make(AppSettings.InMemory(new Dictionary<string, string> { ["liveTranscription"] = "NO", ["autoDetectLanguage"] = "NO" }));
        recorder.StartSimulated(4, Wav(20), 1, both: false, MeetingLanguage.Zh, new WavDecoder());
        await Until(() => recorder.Elapsed > 0.3);
        recorder.SimulateStall(0, 4.5);
        await Until(() => recorder.DeviceWarning is not null, 6);
        Assert.Contains("麥克風", recorder.DeviceWarning);
        await Until(() => recorder.DeviceWarning is null, 6);
        // 換裝置的通知連續來好幾次，只重新開始一次（之後照常收到資料）
        recorder.SimulateInterruption(0);
        await Task.Delay(600);
        var before = recorder.Elapsed;
        await Task.Delay(400);
        Assert.True(recorder.Elapsed > before);
        recorder.Stop();
    }

    /// <summary>
    /// 一直說裝置變了、重新開始後很久才有資料的音源（藍牙耳機切換成通話模式，M§50.4）：開始後 0.2 秒送「裝置變了」（Flaps 次），
    /// 重新開始後 SilentAfterRestart 秒才有資料
    /// </summary>
    sealed class FlappingSource : IAudioSource
    {
        public Action<float[]>? OnSamples { get; set; }
        public Action? OnInterruption { get; set; }
        public int Flaps;
        public double SilentAfterRestart;
        public readonly List<DateTime> Starts = [];
        CancellationTokenSource? cancel;

        public void Start()
        {
            var cts = cancel = new CancellationTokenSource();
            bool flap;
            double silent;
            lock (Starts)
            {
                Starts.Add(DateTime.UtcNow);
                flap = Flaps-- > 0;
                silent = Starts.Count > 1 ? SilentAfterRestart : 0;
            }
            new Thread(() =>
            {
                if (silent > 0 && cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(silent))) return;
                while (!cts.IsCancellationRequested)
                {
                    OnSamples?.Invoke(new float[1600]);
                    if (cts.Token.WaitHandle.WaitOne(100)) return;
                }
            }) { IsBackground = true }.Start();
            if (flap) _ = Task.Delay(200).ContinueWith(_ => OnInterruption?.Invoke());
        }

        public void Stop() => cancel?.Cancel();
        public double[] Gaps() { lock (Starts) return [.. Starts.Zip(Starts.Skip(1), (a, b) => (b - a).TotalSeconds)]; }
        public int Count { get { lock (Starts) return Starts.Count; } }
    }

    /// <summary>每 0.1 秒送一段：Loud 時是正弦波，不然全是 0（模擬錄不到的電腦聲音）</summary>
    sealed class ToneSource : IAudioSource
    {
        public Action<float[]>? OnSamples { get; set; }
        public Action? OnInterruption { get; set; }
        public volatile bool Loud;
        CancellationTokenSource? cancel;

        public void Start()
        {
            var cts = cancel = new CancellationTokenSource();
            new Thread(() =>
            {
                var t = 0;
                while (!cts.IsCancellationRequested)
                {
                    var chunk = new float[1600];
                    if (Loud) for (var i = 0; i < chunk.Length; i++) chunk[i] = 0.2f * MathF.Sin(t++ * 0.07f);
                    OnSamples?.Invoke(chunk);
                    if (cts.Token.WaitHandle.WaitOne(100)) return;
                }
            }) { IsBackground = true }.Start();
        }

        public void Stop() => cancel?.Cancel();
    }

    Recorder MakeBoth(AppSettings settings, IAudioSource mic, IAudioSource system, Func<IReadOnlyList<Recorder.PlayingApp>> playing) =>
        new(settings, paths, NoPower.Instance, new Recorder.Hooks(
            (_, _) => [(0, mic), (1, system)], _ => new FakeLive(), null, _ => 50_000_000_000, PlayingApps: playing))
        { SystemMissingSeconds = 1 };

    /// <summary>R75.3：電腦聲音全是 0、有 app 在別的輸出裝置出聲：提示寫出 app、它的裝置、預設裝置；會議 app 排在瀏覽器前面</summary>
    [Fact]
    public async Task SystemAudioOnOtherDeviceHints()
    {
        var settings = Quiet();
        var mic = new ToneSource { Loud = true };
        var system = new ToneSource();
        Recorder.PlayingApp[] apps =
        [
            new("Chrome", "browser", "Speakers", true, "Speakers"),
            new("Microsoft Teams", "meeting", "Jabra Speak 710", false, "Speakers"),
        ];
        var recorder = MakeBoth(settings, mic, system, () => apps);
        recorder.Start(11, MeetingLanguage.Zh, "both");
        await Until(() => recorder.Hint == Recorder.LevelHint.SystemElsewhere);
        Assert.Equal("Microsoft Teams", recorder.SystemCulprit?.Name);
        var text = recorder.HintText(Recorder.LevelHint.SystemElsewhere);
        Assert.Contains("Microsoft Teams", text);
        Assert.Contains("「Jabra Speak 710」", text);
        Assert.Contains("「Speakers」", text);
        // 電腦聲音恢復就收起來（R75.1）
        system.Loud = true;
        await Until(() => recorder.Hint is null);
        recorder.Stop();
    }

    /// <summary>R75.3：app 就在預設裝置上還是錄不到 → SystemMissing；沒有 app 在出聲 → 不提示</summary>
    [Fact]
    public async Task SystemAudioMissingOrNobodyPlaying()
    {
        var settings = Quiet();
        Recorder.PlayingApp[] apps = [new("Zoom", "meeting", "Speakers", true, "Speakers")];
        var playing = true;
        var recorder = MakeBoth(settings, new ToneSource { Loud = true }, new ToneSource(), () => playing ? apps : []);
        recorder.Start(12, MeetingLanguage.Zh, "both");
        await Until(() => recorder.Hint == Recorder.LevelHint.SystemMissing);
        Assert.Contains("Zoom", recorder.HintText(Recorder.LevelHint.SystemMissing));
        playing = false;
        await Until(() => recorder.Hint is null);
        recorder.Stop();

        var quiet = MakeBoth(settings, new ToneSource { Loud = true }, new ToneSource(), () => []);
        quiet.Start(13, MeetingLanguage.Zh, "both");
        await Task.Delay(3500);
        Assert.Null(quiet.Hint);
        quiet.Stop();
    }

    [Fact]
    public void RestartDelayBacksOff()
    {
        Assert.Equal([0.3, 0.3, 0.3, 2, 4, 8, 16, 30, 30], Enumerable.Range(0, 9).Select(Recorder.RestartDelay));
    }

    /// <summary>一直重新連接時間隔拉長、提示「一直在重新連接」（R74.2、R74.3、M§50.1）</summary>
    [Fact]
    public async Task FlappingSourceBacksOffAndWarns()
    {
        var source = new FlappingSource { Flaps = 5 };
        var recorder = Make(Quiet(), source: source);
        recorder.Start(9, MeetingLanguage.Zh, "mic");
        // 開始 + 5 次重新開始：0.3、0.3、0.3 秒之後是 2、4 秒（加上每次開始後 0.2 秒才送通知）
        await Until(() => source.Count >= 6, 15);
        var gaps = source.Gaps();
        Assert.All(gaps[..3], g => Assert.InRange(g, 0.4, 1.5));
        Assert.InRange(gaps[3], 2.1, 3.5);
        Assert.InRange(gaps[4], 4.1, 5.5);
        Assert.Contains("一直在重新連接", recorder.DeviceWarning);
        // 通知用完了：不再重新開始
        await Task.Delay(1500);
        Assert.Equal(6, source.Count);
        recorder.Stop();
    }

    /// <summary>重新開始後要等幾秒才有資料（藍牙）：看門狗在重新連接完 5 秒內不再重開，等資料來（R74.1）</summary>
    [Fact]
    public async Task WatchdogWaitsAfterRestart()
    {
        var source = new FlappingSource { Flaps = 1, SilentAfterRestart = 4.5 };
        var recorder = Make(Quiet(), source: source);
        recorder.Start(10, MeetingLanguage.Zh, "mic");
        await Until(() => source.Count >= 2, 5);
        // 3 秒沒有資料時照舊提示，但不重新開始；資料來了提示收起
        await Until(() => recorder.DeviceWarning is not null, 6);
        Assert.Contains("正在重新連接", recorder.DeviceWarning);
        await Until(() => recorder.DeviceWarning is null, 6);
        Assert.Equal(2, source.Count);
        recorder.Stop();
    }

    [Fact]
    public async Task WarnsAboutDiskAndPermission()
    {
        var settings = AppSettings.InMemory(new Dictionary<string, string> { ["liveTranscription"] = "NO", ["autoDetectLanguage"] = "NO" });
        var low = Make(settings, available: 500_000_000);
        low.StartSimulated(5, Wav(2), 4, both: false, MeetingLanguage.Zh, new WavDecoder());
        Assert.Contains("500 MB", low.FailureMessage);
        low.Stop();

        var denied = Make(settings, source: new FailingSource(denied: true));
        var finished = new TaskCompletionSource<Recorder.Result>();
        denied.Finished += r => finished.TrySetResult(r);
        denied.Start(6, MeetingLanguage.Zh, "mic");
        var result = await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(result.File);
        Assert.True(denied.PermissionDenied);
        Assert.False(denied.IsRecording);
        Assert.Empty(Directory.GetFiles(paths.Recording, "6-*"));

        var failing = Make(settings, source: new FailingSource(denied: false));
        failing.Start(8, MeetingLanguage.Zh, "mic");
        Assert.Contains("沒有權限", failing.FailureMessage);
        Assert.False(failing.PermissionDenied);
    }

    [Fact]
    public void RepairsTruncatedWav()
    {
        var path = Wav(1);
        var bytes = File.ReadAllBytes(path);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 36);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), 0);
        File.WriteAllBytes(path, bytes);
        WavRepair.Repair(path);
        Assert.Equal(16000, Read(path).Frames);
        File.WriteAllBytes(path, new byte[20]);
        Assert.Throws<WavRepair.RepairException>(() => WavRepair.Repair(path));
    }

    [Fact]
    public void ByteCountLikeMac()
    {
        Assert.Equal("500 MB", Recorder.ByteCount(500_000_000));
        Assert.Equal("1.23 GB", Recorder.ByteCount(1_234_567_890));
        Assert.Equal("999 bytes", Recorder.ByteCount(999));
    }

    /// <summary>前 speechSeconds 秒是大聲的音（像說話），後面是很小的底噪（約 −66 dBFS）</summary>
    string SpeechThenQuiet(double speechSeconds, double seconds)
    {
        var path = Path.Combine(home.Path, Guid.NewGuid() + ".wav");
        var count = (int)(seconds * 16000);
        var data = new float[count];
        var random = new Random(1);
        for (var i = 0; i < count; i++)
            data[i] = i < speechSeconds * 16000 ? 0.2f * MathF.Sin(i * 0.07f) : 0.0005f * (float)(random.NextDouble() * 2 - 1);
        using var writer = new WavWriter(path, 1);
        writer.Write([data], count);
        return path;
    }

    [Fact]
    public async Task IdlePromptsThenStopsWithoutSpeech()
    {
        // 忘了停止錄音（R48.1、R48.2、M§35）：沒有即時逐字稿時看聲音；4 秒提醒、8 秒自動停止（正式是 15、30 分鐘）
        var recorder = Make(AppSettings.InMemory(new Dictionary<string, string> { ["liveTranscription"] = "NO", ["autoDetectLanguage"] = "NO" }));
        recorder.IdleRemindSeconds = 4;
        recorder.IdleStopSeconds = 8;
        var prompts = 0;
        Recorder.Result? result = null;
        recorder.IdlePromptAppeared += () => Interlocked.Increment(ref prompts);
        recorder.Finished += r => result = r;
        recorder.StartSimulated(5, SpeechThenQuiet(2, 30), 4, both: false, MeetingLanguage.Zh, new WavDecoder());
        await Until(() => recorder.IdlePrompt);
        Assert.InRange(recorder.LastSpeechAt, 1, 3);
        Assert.Equal(1, prompts);
        await Until(() => result is not null);
        Assert.False(recorder.IsRecording);
        Assert.InRange(result!.IdleSince!.Value, 1, 3);
        Assert.InRange(result.Duration, result.IdleSince.Value + 7.5, result.IdleSince.Value + 9.5);
    }

    [Fact]
    public async Task StopTimerCountsDownThenStopsEvenWhilePaused()
    {
        // 定時停止（R110、M§84）：跟著時鐘走；時間到先倒數（R110.3），倒數完才停，暫停中也停；停止就取消，下一場沒有定時
        var recorder = Make(AppSettings.InMemory(new Dictionary<string, string> { ["liveTranscription"] = "NO", ["autoDetectLanguage"] = "NO" }));
        recorder.StopCountdownSeconds = 1.5;
        Recorder.Result? result = null;
        var due = 0;
        recorder.Finished += r => result = r;
        recorder.StopTimerDue += () => Interlocked.Increment(ref due);
        recorder.SetStopTimer(DateTime.Now.AddSeconds(1));
        Assert.Null(recorder.StopAt);   // 沒在錄音：沒作用
        recorder.StartSimulated(9, Wav(30), 1, both: false, MeetingLanguage.Zh, new WavDecoder());
        recorder.SetStopTimer(DateTime.Now.AddSeconds(30));
        recorder.SetStopTimer(null);
        Assert.Null(recorder.StopAt);
        recorder.SetStopTimer(DateTime.Now.AddSeconds(0.5));
        recorder.Pause();
        // StopTimerDue 在鎖外面、倒數設好之後才發：等事件，不是等 StopCountdown（CI 的機器慢時兩者之間有空檔，win-v5.1.0 第一次建置失敗在這裡）
        await Until(() => Volatile.Read(ref due) == 1, 10);
        Assert.NotNull(recorder.StopCountdown);
        Assert.True(recorder.IsRecording);   // 倒數中還在錄
        await Until(() => result is not null, 10);
        Assert.True(result!.Scheduled);
        Assert.Null(result.IdleSince);
        Assert.Equal(1, due);
        Assert.False(recorder.IsRecording);
        Assert.Null(recorder.StopAt);
        Assert.Null(recorder.StopCountdown);
        result = null;
        recorder.StartSimulated(10, Wav(30), 1, both: false, MeetingLanguage.Zh, new WavDecoder());
        Assert.Null(recorder.StopAt);
        recorder.Stop();
        Assert.False(result!.Scheduled);
    }

    [Fact]
    public async Task ContinueDuringStopTimerCountdownKeepsRecording()
    {
        // 倒數中按「繼續錄」（R110.3）：定時取消、照常錄下去，倒數過了也不停
        var recorder = Make(AppSettings.InMemory(new Dictionary<string, string> { ["liveTranscription"] = "NO", ["autoDetectLanguage"] = "NO" }));
        recorder.StopCountdownSeconds = 1;
        recorder.StartSimulated(11, Wav(30), 1, both: false, MeetingLanguage.Zh, new WavDecoder());
        recorder.SetStopTimer(DateTime.Now.AddSeconds(0.3));
        await Until(() => recorder.StopCountdown is not null, 10);
        recorder.SetStopTimer(null);
        Assert.Null(recorder.StopCountdown);
        Assert.Null(recorder.StopAt);
        await Task.Delay(1800);
        Assert.True(recorder.IsRecording);
        // 倒數中換成新的時間：倒數收起、等新的時間
        recorder.SetStopTimer(DateTime.Now.AddSeconds(0.3));
        await Until(() => recorder.StopCountdown is not null, 10);
        recorder.SetStopTimer(DateTime.Now.AddMinutes(30));
        Assert.Null(recorder.StopCountdown);
        await Task.Delay(1500);
        Assert.True(recorder.IsRecording);
        recorder.Stop();
    }

    [Fact]
    public async Task ContinueAfterIdleRestartsTheTimer()
    {
        var recorder = Make(AppSettings.InMemory(new Dictionary<string, string> { ["liveTranscription"] = "NO", ["autoDetectLanguage"] = "NO" }));
        recorder.IdleRemindSeconds = 3;
        recorder.IdleStopSeconds = 60;
        recorder.StartSimulated(6, SpeechThenQuiet(1, 30), 4, both: false, MeetingLanguage.Zh, new WavDecoder());
        await Until(() => recorder.IdlePrompt);
        recorder.ContinueAfterIdle();
        Assert.False(recorder.IdlePrompt);
        Assert.InRange(recorder.IdleSeconds, 0, 1);
        recorder.Stop();
    }

    [Fact]
    public async Task IdleOffOrSpeechNeverPrompts()
    {
        // 設定關掉：不提醒、不停止
        var off = Make(AppSettings.InMemory(new Dictionary<string, string> { ["liveTranscription"] = "NO", ["autoDetectLanguage"] = "NO", ["idleStop"] = "NO" }));
        off.IdleRemindSeconds = 2;
        off.IdleStopSeconds = 4;
        off.StartSimulated(7, SpeechThenQuiet(1, 40), 8, both: false, MeetingLanguage.Zh, new WavDecoder());
        await Until(() => off.Elapsed >= 10);
        Assert.False(off.IdlePrompt);
        Assert.True(off.IsRecording);
        off.Stop();
        // 即時逐字稿一直有新的字（FakeLive 每秒一句）：打字之類的聲音沒有也算有人說話，不提醒
        var talking = Make(AppSettings.InMemory(new Dictionary<string, string> { ["autoDetectLanguage"] = "NO" }), []);
        talking.IdleRemindSeconds = 3;
        talking.IdleStopSeconds = 6;
        talking.StartSimulated(8, SpeechThenQuiet(0, 40), 4, both: false, MeetingLanguage.Zh, new WavDecoder());
        await Until(() => talking.LiveActive);
        await Until(() => talking.Elapsed >= 10);
        Assert.False(talking.IdlePrompt);
        Assert.True(talking.IsRecording);
        talking.Stop();
    }

    [Fact]
    public void TrimsTheTailForIdleStoppedRecordings()
    {
        // 只轉錄到最後說話（M§35.3）：前段複製成暫存 WAV，錄音不動；payload 存 until
        var source = SpeechThenQuiet(2, 10);
        var trimmed = AudioTrim.Prefix(source, 4, paths.Tmp);
        Assert.NotNull(trimmed);
        Assert.Equal(64000, Read(trimmed!).Frames);
        Assert.Equal(160000, Read(source).Frames);
        Assert.Null(AudioTrim.Prefix(source, 12, paths.Tmp));   // 錄音沒有比 until 長
        Assert.Equal(19.5, JobRunner.TranscribedUntil(JobRunner.Encode(new TranscribePayload(19.5))));
        Assert.Null(JobRunner.TranscribedUntil(null));
        Assert.Null(JobRunner.TranscribedUntil("{}"));
    }
}
