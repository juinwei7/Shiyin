using OpenccNetLib;
using SherpaOnnx;
using Shiyin.Model;
using Shiyin.Transcribe;

namespace Shiyin.Speech;

/// <summary>SenseVoice 的模型位置（design-windows §7、§8）：模型檔、tokens、VAD</summary>
public sealed record SenseVoiceModels(string Model, string Tokens, string Vad)
{
    public static SenseVoiceModels In(string dir) => new(
        Path.Combine(dir, "sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17", "model.int8.onnx"),
        Path.Combine(dir, "sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17", "tokens.txt"),
        Path.Combine(dir, "silero_vad.onnx"));

    public bool Installed => File.Exists(Model) && File.Exists(Tokens) && File.Exists(Vad);
}

/// <summary>SenseVoice 辨識一句話，中文轉成台灣繁體（OpenCC s2twp）</summary>
public sealed class SenseVoiceRecognizer : IDisposable
{
    readonly OfflineRecognizer recognizer;
    readonly MeetingLanguage language;
    readonly Opencc? opencc;
    readonly object gate = new();

    public SenseVoiceRecognizer(SenseVoiceModels models, MeetingLanguage language, int threads = 2)
    {
        this.language = language;
        var config = new OfflineRecognizerConfig();
        config.ModelConfig.SenseVoice.Model = models.Model;
        config.ModelConfig.SenseVoice.Language = language.Code();
        config.ModelConfig.SenseVoice.UseInverseTextNormalization = 1;
        config.ModelConfig.Tokens = models.Tokens;
        config.ModelConfig.NumThreads = threads;
        config.ModelConfig.Provider = "cpu";
        config.DecodingMethod = "greedy_search";
        recognizer = new OfflineRecognizer(config);
        if (language == MeetingLanguage.Zh) opencc = new Opencc("s2twp");
    }

    public void Dispose() => recognizer.Dispose();

    public string Recognize(float[] samples)
    {
        if (samples.Length < 1600) return "";
        string text;
        lock (gate)
        {
            using var stream = recognizer.CreateStream();
            stream.AcceptWaveform((int)WhisperRules.SampleRate, samples);
            recognizer.Decode(stream);
            text = stream.Result.Text;
        }
        return Clean(text);
    }

    /// <summary>拿掉模型的標籤（&lt;|zh|&gt; 這種）、中文轉台灣繁體</summary>
    string Clean(string text)
    {
        var t = System.Text.RegularExpressions.Regex.Replace(text, "<\\|[^|]*\\|>", "").Trim();
        return opencc is null ? t : opencc.Convert(t, true);
    }
}

/// <summary>VAD 的門檻、送進 VAD 前放大多少（design-windows §7）。GainDb 是 null 時用自動增益；評估時可以給固定值</summary>
public sealed record VadOptions
{
    public float Threshold { get; init; } = 0.35f;
    public float? GainDb { get; init; }

    internal AutoGain MakeGain() => GainDb is float db ? AutoGain.Fixed(db) : new AutoGain();
}

/// <summary>
/// 自動增益（design-windows §7）：遠距收音的會議太小聲時 VAD 抓不到說話（AISHELL-4 只抓到兩成的時間），
/// 看最近 30 秒的音量（每 0.1 秒一格，取第 90 百分位當說話的音量），放大到約 −22 dBFS、最多 24 dB，不縮小。每秒更新一次
/// </summary>
public sealed class AutoGain
{
    public const float TargetDb = -22, MaxGainDb = 24;
    const int FrameSamples = 1600, Frames = 300;
    readonly float[] levels = new float[Frames];
    readonly bool adaptive = true;
    int count, next, filled, sinceUpdate;
    double energy;
    float gain = 1;

    public static AutoGain Fixed(float db) => new(MathF.Pow(10, db / 20));
    public AutoGain() { }
    AutoGain(float fixedGain) { gain = fixedGain; adaptive = false; }

    public float GainDb => 20 * MathF.Log10(gain);

    /// <summary>就地放大（先用原本的聲音算音量，再乘上目前的增益）</summary>
    public void Apply(Span<float> samples)
    {
        for (var i = 0; i < samples.Length; i++)
        {
            var x = samples[i];
            if (adaptive)
            {
                energy += x * x;
                if (++filled == FrameSamples)
                {
                    var rms = Math.Sqrt(energy / FrameSamples);
                    levels[next] = rms > 0 ? (float)(20 * Math.Log10(rms)) : -120;
                    next = (next + 1) % Frames;
                    count = Math.Min(count + 1, Frames);
                    filled = 0;
                    energy = 0;
                    if (++sinceUpdate >= 10) { sinceUpdate = 0; Update(); }
                }
            }
            samples[i] = Math.Clamp(x * gain, -1f, 1f);
        }
    }

    void Update()
    {
        if (count < 20) return;   // 不到 2 秒先不放大
        var sorted = levels.AsSpan(0, count).ToArray();
        Array.Sort(sorted);
        var speech = sorted[(int)((count - 1) * 0.9)];
        gain = MathF.Pow(10, Math.Clamp(TargetDb - speech, 0, MaxGainDb) / 20);
    }
}

/// <summary>VAD 的設定：一句最長 15 秒，停 0.5 秒算講完</summary>
static class Vad
{
    public static VoiceActivityDetector Create(string model, float bufferSeconds = 60, float threshold = 0.35f)
    {
        var config = new VadModelConfig();
        config.SileroVad.Model = model;
        config.SileroVad.Threshold = threshold;
        config.SileroVad.MinSilenceDuration = 0.5f;
        config.SileroVad.MinSpeechDuration = 0.25f;
        config.SileroVad.MaxSpeechDuration = 15f;
        config.SileroVad.WindowSize = 512;
        config.SampleRate = (int)WhisperRules.SampleRate;
        config.NumThreads = 1;
        config.Provider = "cpu";
        return new VoiceActivityDetector(config, bufferSeconds);
    }
}

/// <summary>
/// 快速辨識（R27 R4.1、R20.8、D45）：整個檔案用 VAD 切成一句一句再辨識。不吃詞彙表、沒有低信心標示（同 Mac 的內建辨識）
/// </summary>
public static class FastTranscriber
{
    public static List<TranscribedSegment> Transcribe(Whisper.IAudioStream audio, SenseVoiceModels models, MeetingLanguage language,
                                                      Action<double>? progress = null, CancellationToken cancel = default, VadOptions? options = null)
    {
        options ??= new VadOptions();
        var gain = options.MakeGain();
        using var recognizer = new SenseVoiceRecognizer(models, language, Math.Max(1, Math.Min(4, Environment.ProcessorCount - 2)));
        using var vad = Vad.Create(models.Vad, threshold: options.Threshold);
        var segments = new List<TranscribedSegment>();
        var buffer = new List<float>();
        var consumed = 0;
        var total = (double)Math.Max(audio.EstimatedCount, 1);
        void Drain()
        {
            while (!vad.IsEmpty())
            {
                var s = vad.Front();
                vad.Pop();
                var text = recognizer.Recognize(s.Samples);
                if (text.Length == 0) continue;
                var start = s.Start / WhisperRules.SampleRate;
                segments.Add(new TranscribedSegment(start, start + s.Samples.Length / WhisperRules.SampleRate, text, false));
            }
        }
        while (!audio.Finished || buffer.Count > 0)
        {
            cancel.ThrowIfCancellationRequested();
            audio.Fill(buffer, 512 * 64);
            var whole = buffer.Count / 512 * 512;
            if (whole == 0 && audio.Finished) break;
            for (var i = 0; i < whole; i += 512)
            {
                var window = buffer.GetRange(i, 512).ToArray();
                gain.Apply(window);
                vad.AcceptWaveform(window);
            }
            buffer.RemoveRange(0, whole);
            consumed += whole;
            Drain();
            progress?.Invoke(Math.Min(1, consumed / total));
        }
        vad.Flush();
        Drain();
        if (segments.Count == 0) throw new Whisper.WhisperException(Whisper.WhisperErrorKind.Empty);
        return segments;
    }
}

/// <summary>
/// 即時逐字稿（design-windows §7、D44）：VAD 判定在說話時，約每 0.4 秒把這一句目前的聲音整句重新辨識，當暫定文字；
/// VAD 判定一句結束時，最後一次的結果當定稿。Feed 在寫檔的執行緒呼叫，辨識在背景執行緒
/// </summary>
public interface ILiveTranscriber : IDisposable
{
    /// <summary>暫定文字（說話中一直更新；一句講完變成空的）</summary>
    event Action<string>? Volatile;
    /// <summary>定稿：開始、結束（秒，從錄音開始算，加上 Offset）、文字</summary>
    event Action<double, double, string>? Final;
    /// <summary>時間偏移：錄音中換語言重新開始時，從錄到的秒數接著算</summary>
    double Offset { get; set; }
    /// <summary>寫檔的執行緒呼叫，不能卡住</summary>
    void Feed(ReadOnlySpan<float> samples);
    /// <summary>錄音結束：把最後一句收尾（會等辨識做完）</summary>
    void Finish();
}

public sealed class LiveTranscriber : ILiveTranscriber
{
    readonly SenseVoiceRecognizer recognizer;
    readonly VoiceActivityDetector vad;
    readonly AutoGain gain;
    readonly object gate = new();
    readonly List<float> pending = [];
    readonly List<float> current = [];
    int position;            // 已經送進 VAD 的取樣數
    int speechStart = -1;    // 目前這一句的起點（VAD 偵測到說話時往前抓 0.3 秒）
    readonly List<float> history = [];   // 最近 1 秒，往前抓用
    readonly Thread worker;
    readonly AutoResetEvent wake = new(false);
    volatile bool stopping;
    DateTime lastVolatile = DateTime.MinValue;

    /// <summary>暫定文字（說話中一直更新；一句講完變成空的）</summary>
    public event Action<string>? Volatile;
    /// <summary>定稿：開始、結束（秒，從錄音開始算）、文字</summary>
    public event Action<double, double, string>? Final;

    /// <summary>時間偏移：錄音中換語言重新開始時，從錄到的秒數接著算</summary>
    public double Offset { get; set; }

    public TimeSpan VolatileInterval { get; init; } = TimeSpan.FromMilliseconds(400);

    public LiveTranscriber(SenseVoiceModels models, MeetingLanguage language, VadOptions? options = null)
    {
        options ??= new VadOptions();
        recognizer = new SenseVoiceRecognizer(models, language, 2);
        vad = Vad.Create(models.Vad, threshold: options.Threshold);
        gain = options.MakeGain();
        worker = new Thread(Loop) { IsBackground = true, Name = "live-transcriber", Priority = ThreadPriority.BelowNormal };
        worker.Start();
    }

    public void Feed(ReadOnlySpan<float> samples)
    {
        lock (gate) { foreach (var s in samples) pending.Add(s); }
        wake.Set();
    }

    /// <summary>錄音結束：把最後一句收尾</summary>
    public void Finish()
    {
        stopping = true;
        wake.Set();
        worker.Join();
    }

    public void Dispose()
    {
        if (worker.IsAlive) Finish();
        recognizer.Dispose();
        vad.Dispose();
    }

    void Loop()
    {
        while (true)
        {
            wake.WaitOne(TimeSpan.FromMilliseconds(100));
            float[] chunk;
            lock (gate)
            {
                var whole = pending.Count / 512 * 512;
                chunk = pending.GetRange(0, whole).ToArray();
                pending.RemoveRange(0, whole);
            }
            for (var i = 0; i < chunk.Length; i += 512)
            {
                var window = chunk.AsSpan(i, 512).ToArray();
                gain.Apply(window);
                vad.AcceptWaveform(window);
                position += 512;
                history.AddRange(window);
                if (history.Count > 16000) history.RemoveRange(0, history.Count - 16000);
                if (speechStart >= 0) current.AddRange(window);
                else if (vad.IsSpeechDetected())
                {
                    // 往前抓 0.3 秒，暫定文字才不會少掉開頭
                    var back = Math.Min(history.Count, 4800);
                    speechStart = position - back;
                    current.Clear();
                    current.AddRange(history.GetRange(history.Count - back, back));
                }
                PopFinals();
            }
            if (stopping)
            {
                vad.Flush();
                PopFinals();
                if (speechStart >= 0) Volatile?.Invoke("");
                return;
            }
            if (speechStart >= 0 && current.Count >= 8000 && DateTime.UtcNow - lastVolatile >= VolatileInterval)
            {
                lastVolatile = DateTime.UtcNow;
                var text = recognizer.Recognize([.. current]);
                if (text.Length > 0) Volatile?.Invoke(text);
            }
        }
    }

    void PopFinals()
    {
        while (!vad.IsEmpty())
        {
            var s = vad.Front();
            vad.Pop();
            speechStart = -1;
            current.Clear();
            var text = recognizer.Recognize(s.Samples);
            Volatile?.Invoke("");
            if (text.Length == 0) continue;
            var start = Offset + s.Start / WhisperRules.SampleRate;
            Final?.Invoke(start, start + s.Samples.Length / WhisperRules.SampleRate, text);
        }
    }
}
