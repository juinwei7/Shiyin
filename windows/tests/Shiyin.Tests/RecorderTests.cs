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
}
