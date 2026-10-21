using Shiyin.Audio;
using Shiyin.IO;
using Shiyin.Localization;
using Shiyin.Model;
using Shiyin.Speakers;
using Shiyin.Speech;
using Shiyin.Transcribe;
using Shiyin.Whisper;

namespace Shiyin.Services;

/// <summary>解碼（design-windows §8）：Windows 是 Media Foundation（Platform），Mac 上測試用 WAV</summary>
public interface IAudioDecoder
{
    /// <summary>依序讀、轉成 16 kHz 單聲道（whisper 從檔案依序讀，M§21.16）</summary>
    IAudioStream Open(string path);
    /// <summary>整段解碼成 16 kHz 單聲道（說話者辨識）</summary>
    float[] Decode(string path, CancellationToken cancel = default);
    /// <summary>每個聲道各自解碼成 16 kHz（分聲道轉錄，M§17.3）</summary>
    float[][] DecodeChannels(string path, CancellationToken cancel = default);
}

/// <summary>只讀 WAV（Mac 上的測試、評估）</summary>
public sealed class WavDecoder : IAudioDecoder
{
    public IAudioStream Open(string path) => new WavStream(path);
    public float[] Decode(string path, CancellationToken cancel = default) => WavStream.ReadAll(path);

    public float[][] DecodeChannels(string path, CancellationToken cancel = default)
    {
        using var reader = new WavReader(path);
        var channels = Enumerable.Range(0, reader.Channels).Select(_ => new List<float>()).ToArray();
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            var block = reader.Read(16000);
            if (block[0].Length == 0) break;
            for (var c = 0; c < channels.Length; c++) channels[c].AddRange(block[c]);
        }
        if (reader.SampleRate != 16000) throw new NotSupportedException("16 kHz only");  // l10n-skip
        return channels.Select(c => c.ToArray()).ToArray();
    }
}

/// <summary>轉錄用的引擎（測試換成假的，不用載入模型）</summary>
public interface ISpeechEngines
{
    (List<TranscribedSegment> Segments, List<int> Seams, int SampleCount) WhisperFile(IAudioStream audio, double chunkSeconds, string model,
                                                                                       string prompt, string language, WhisperControl control);
    List<TranscribedSegment> WhisperSamples(float[] samples, IReadOnlyList<(int Start, int End)> chunks, string model, string prompt,
                                            string language, WhisperControl control);
    List<TranscribedSegment> Fast(IAudioStream audio, MeetingLanguage language, Action<double> progress, CancellationToken cancel);
    Diarizer.Output Diarize(float[] samples, int? minSpeakers, Action<double> progress, CancellationToken cancel);
}

/// <summary>真的引擎：whisper.cpp、SenseVoice、sherpa-onnx，模型從 ModelStore 的位置讀</summary>
public sealed class SpeechEngines(ModelStore models) : ISpeechEngines
{
    public SenseVoiceModels FastModels
    {
        get
        {
            var files = models.InfoOf(ModelStore.Model.FastRecognition).Files;
            return new SenseVoiceModels(models.PathOf(files[0]), models.PathOf(files[1]), models.PathOf(files[2]));
        }
    }

    public Diarizer.Options DiarizerOptions
    {
        get
        {
            var files = models.InfoOf(ModelStore.Model.Diarization).Files;
            return new Diarizer.Options(models.PathOf(files[0]), models.PathOf(files[1]));
        }
    }

    public (List<TranscribedSegment> Segments, List<int> Seams, int SampleCount) WhisperFile(
        IAudioStream audio, double chunkSeconds, string model, string prompt, string language, WhisperControl control)
    {
        LogBackend();
        return WhisperEngine.TranscribeFile(audio, chunkSeconds, model, prompt, language, control);
    }

    public List<TranscribedSegment> WhisperSamples(float[] samples, IReadOnlyList<(int Start, int End)> chunks, string model, string prompt,
                                                   string language, WhisperControl control)
    {
        LogBackend();
        return WhisperEngine.Transcribe(samples, chunks, model, prompt, language, control);
    }

    static int backendLogged;

    /// <summary>第一次轉錄時記下 whisper 用的是哪張顯示卡、從哪裡載入（顯示卡加速，design-windows §8）</summary>
    static void LogBackend()
    {
        if (Interlocked.Exchange(ref backendLogged, 1) == 1) return;
        var gpu = WhisperEngine.GpuDevice;
        var from = WhisperEngine.NativeDirectory is { } pack && WhisperEngine.LoadedDirectory == pack ? "gpu pack" : "app";
        Log.Info($"whisper: {(gpu.Length > 0 ? gpu : "CPU")}, loaded from {from}");
    }

    public List<TranscribedSegment> Fast(IAudioStream audio, MeetingLanguage language, Action<double> progress, CancellationToken cancel) =>
        FastTranscriber.Transcribe(audio, FastModels, language, progress, cancel);

    public Diarizer.Output Diarize(float[] samples, int? minSpeakers, Action<double> progress, CancellationToken cancel)
    {
        using var diarizer = new Diarizer(DiarizerOptions);
        return diarizer.Diarize(samples, minSpeakers, progress, cancel);
    }
}

/// <summary>轉錄的設定（M§6、§17）。工作佇列用目前的 app 設定產生；評估工具從 JSON 讀</summary>
public sealed record TranscriptionOptions
{
    /// <summary>Breeze：whisper.cpp，模型依會議語言；Fast：快速辨識（SenseVoice，Mac 的「macOS 內建」，D45）</summary>
    public enum EngineKind { Breeze, Fast }
    /// <summary>分聲道時怎麼處理麥克風收到的回音（M§17.3）</summary>
    public enum EchoKind { None, Dedupe }

    public EngineKind Engine { get; init; } = EngineKind.Breeze;
    public MeetingLanguage Language { get; init; } = MeetingLanguage.Zh;
    public IReadOnlyList<string> Glossary { get; init; } = [];
    public bool Diarize { get; init; } = true;
    public bool SplitChannels { get; init; }
    public EchoKind Echo { get; init; } = EchoKind.Dedupe;
    /// <summary>評估用：換一個 whisper 模型檔；null 依會議語言選</summary>
    public string? Model { get; init; }
    /// <summary>沒分聲道時聲紋記成哪種收音（M§21.4）：mic、system，雙聲道混在一起是 mix</summary>
    public string AudioSource { get; init; } = "mix";
    /// <summary>whisper 每段幾秒（M§21.16、D34）；0 不分段</summary>
    public double ChunkSeconds { get; init; } = 600;
    /// <summary>說話者辨識至少分出幾個人（M§23.2），null 自動判斷</summary>
    public int? MinSpeakers { get; init; }

    /// <summary>寫進 meetings.transcribed_with（M§21.17）：快速辨識跟 Mac 的內建一樣寫 system</summary>
    public string TranscribedWith => Engine == EngineKind.Fast ? "system" : Language == MeetingLanguage.Ja ? "japanese" : "breeze";

    /// <summary>這個語言的模型還沒下載好時，這一場先用快速辨識（M§21.17、D35）</summary>
    public static TranscriptionOptions Current(AppSettings settings, ModelStore models, MeetingLanguage language, IReadOnlyList<string> glossary,
                                               string channels, int? minSpeakers) => new()
    {
        MinSpeakers = minSpeakers,
        Engine = settings.UseFastTranscriber || !models.HasTranscriptionModel(language) ? EngineKind.Fast : EngineKind.Breeze,
        Language = language,
        Glossary = glossary,
        Diarize = settings.Diarize && models.IsInstalled(ModelStore.Model.Diarization),
        // 只有錄音時選了「麥克風＋電腦聲音」的會議才分聲道；匯入的雙聲道檔案（音樂、影片）照舊混成一軌
        SplitChannels = settings.SplitChannels && channels == "both",
        AudioSource = channels == "both" ? "mix" : channels,
    };
}

public sealed record TranscriptionResult(
    List<TranscribedSegment> Segments, List<int?> Speakers, List<int?> Channels, double Duration,
    Dictionary<int, SpeakerVoice> Voices, List<double> Seams, SpeakerRules.Quality? DiarizationQuality);

/// <summary>
/// 轉錄流程：解碼 → 辨識 → 說話者辨識（M§6）；雙聲道錄音可以分開轉錄再合併（M§17.3）。在背景執行緒跑、會卡住那個執行緒。
/// 回傳 null 表示中途被取消
/// </summary>
public sealed class TranscriptionPipeline(TranscriptionOptions options, IAudioDecoder decoder, ISpeechEngines engines, string modelPath)
{
    public TranscriptionOptions Options => options;
    /// <summary>目前在做哪一步（顯示在會議列表）</summary>
    public Action<string> Stage { get; init; } = _ => { };
    /// <summary>整體進度 0～1</summary>
    public Action<double> Progress { get; init; } = _ => { };
    /// <summary>每一步之間檢查工作是否還要繼續（使用者按了取消、工作被改成別的狀態）</summary>
    public Func<bool> ShouldContinue { get; init; } = () => true;

    public TranscriptionResult? Run(string audio, CancellationToken cancel)
    {
        if (options.Engine == TranscriptionOptions.EngineKind.Breeze && !File.Exists(modelPath)) throw new WhisperException(WhisperErrorKind.ModelMissing);
        Stage(L.T("讀取錄音"));
        if (options.SplitChannels)
        {
            var channels = decoder.DecodeChannels(audio, cancel);
            if (channels.Length == 2) return RunSplit(channels[0], channels[1], cancel);
        }
        if (!ShouldContinue()) return null;
        var fast = options.Engine == TranscriptionOptions.EngineKind.Fast;
        Stage(fast ? L.T("轉錄（快速辨識）") : L.T("轉錄"));
        // 還不知道長度（太短不辨識說話者），先照要辨識說話者算進度
        var share = options.Diarize ? 0.85 : 1.0;
        Progress(0);
        List<TranscribedSegment> segments;
        List<double> seams = [];
        double duration;
        using (var stream = new CountingStream(decoder.Open(audio)))
        {
            if (fast)
            {
                segments = engines.Fast(stream, options.Language, p => Progress(p * share), cancel);
                duration = stream.Count / WhisperRules.SampleRate;
                if (segments.Count == 0) throw new WhisperException(WhisperErrorKind.Empty);
            }
            else
            {
                var control = new WhisperControl(p => Progress(p * share), cancel);
                var prompt = WhisperRules.Prompt(options.Glossary, options.Language);
                var result = engines.WhisperFile(stream, options.ChunkSeconds, modelPath, prompt, options.Language.WhisperCode(), control);
                segments = result.Segments;
                seams = result.Seams.Select(s => s / WhisperRules.SampleRate).ToList();
                duration = result.SampleCount / WhisperRules.SampleRate;
            }
        }
        cancel.ThrowIfCancellationRequested();
        if (!ShouldContinue()) return null;
        List<int?> speakers = segments.Select(_ => (int?)null).ToList();
        var voices = new Dictionary<int, SpeakerVoice>();
        SpeakerRules.Quality? quality = null;
        if (options.Diarize && duration > 5)
        {
            Stage(L.T("辨識說話者"));
            var samples = decoder.Decode(audio, cancel);
            if (!ShouldContinue()) return null;
            (speakers, voices, quality) = Speakers(segments, samples, options.AudioSource, p => Progress(share + p * (1 - share)), cancel);
        }
        if (!ShouldContinue()) return null;
        return new TranscriptionResult(segments, speakers, segments.Select(_ => (int?)null).ToList(), duration, voices, seams, quality);
    }

    // MARK: 分聲道（M§17.3）

    TranscriptionResult? RunSplit(float[] microphone, float[] system, CancellationToken cancel)
    {
        if (!ShouldContinue()) return null;
        var duration = Math.Max(microphone.Length, system.Length) / WhisperRules.SampleRate;
        var diarize = options.Diarize && duration > 5;
        Progress(0);
        // 兩個聲道各自轉錄、各自辨識說話者；沒有聲音的聲道（例如沒有播放任何聲音）直接跳過
        var parts = new List<(int Channel, List<TranscribedSegment> Segments, List<int?> Speakers, Dictionary<int, SpeakerVoice> Voices)>();
        var inputs = new[] { (0, microphone), (1, system) }.Where(x => TextRules.HasSound(x.Item2)).ToList();
        for (var index = 0; index < inputs.Count; index++)
        {
            var (channel, samples) = inputs[index];
            var (baseline, width) = ((double)index / inputs.Count, 1.0 / inputs.Count);
            var share = diarize ? 0.85 : 1.0;
            var segments = TranscribeSamples(samples, baseline, width * share, cancel);
            if (segments is null) return null;
            List<int?> speakers = segments.Select(_ => (int?)null).ToList();
            var voices = new Dictionary<int, SpeakerVoice>();
            if (diarize && segments.Count > 0)
            {
                Stage(L.T("辨識說話者"));
                (speakers, voices, _) = Speakers(segments, samples, channel == 0 ? "mic" : "system",
                    p => Progress(baseline + width * (share + p * (1 - share))), cancel);
            }
            if (!ShouldContinue()) return null;
            parts.Add((channel, segments, speakers, voices));
        }

        // 說話者編號：麥克風那一邊的人先編，電腦聲音那一邊接著編
        var merged = new List<(TranscribedSegment Segment, int? Speaker, int Channel)>();
        var offsetVoices = new Dictionary<int, SpeakerVoice>();
        var offset = 0;
        foreach (var part in parts)
        {
            for (var i = 0; i < part.Segments.Count; i++) merged.Add((part.Segments[i], part.Speakers[i] + offset, part.Channel));
            foreach (var (number, voice) in part.Voices) offsetVoices[number + offset] = voice;
            offset += part.Speakers.Max() ?? 0;
        }
        if (options.Echo == TranscriptionOptions.EchoKind.Dedupe)
        {
            var systemSegments = merged.Where(m => m.Channel == 1).Select(m => m.Segment).ToList();
            merged.RemoveAll(m => m.Channel == 0 && TextRules.IsEcho(m.Segment, systemSegments));
        }
        merged = merged.OrderBy(m => m.Segment.Start).ToList();
        // 刪掉回音段落後，說話者編號可能有空號，依出現順序重新編
        var numbering = new Dictionary<int, int>();
        var renumbered = merged.Select(m =>
        {
            if (m.Speaker is not int s) return (int?)null;
            if (numbering.TryGetValue(s, out var n)) return n;
            numbering[s] = numbering.Count + 1;
            return numbering.Count;
        }).ToList();
        if (merged.Count == 0) throw new WhisperException(WhisperErrorKind.Empty);
        var result = new Dictionary<int, SpeakerVoice>();
        foreach (var (old, n) in numbering) if (offsetVoices.TryGetValue(old, out var v)) result[n] = v;
        return new TranscriptionResult(merged.Select(m => m.Segment).ToList(), renumbered, merged.Select(m => (int?)m.Channel).ToList(),
            duration, result, [], null);
    }

    /// <summary>辨識一段已經解碼的聲音（分聲道）；進度換算到整體的 from…from+span。回傳 null 表示被取消；沒有文字回傳空陣列</summary>
    List<TranscribedSegment>? TranscribeSamples(float[] samples, double from, double span, CancellationToken cancel)
    {
        var fast = options.Engine == TranscriptionOptions.EngineKind.Fast;
        Stage(fast ? L.T("轉錄（快速辨識）") : L.T("轉錄"));
        List<TranscribedSegment> segments;
        try
        {
            if (fast)
            {
                using var stream = new SampleStream(samples);
                segments = engines.Fast(stream, options.Language, p => Progress(from + p * span), cancel);
            }
            else
            {
                var control = new WhisperControl(p => Progress(from + p * span), cancel);
                var prompt = WhisperRules.Prompt(options.Glossary, options.Language);
                segments = engines.WhisperSamples(samples, WhisperRules.Chunks(samples, options.ChunkSeconds), modelPath, prompt,
                    options.Language.WhisperCode(), control);
            }
        }
        catch (WhisperException e) when (e.Kind == WhisperErrorKind.Empty)
        {
            // 分聲道時某一邊沒人說話是正常的
            segments = [];
        }
        cancel.ThrowIfCancellationRequested();
        return ShouldContinue() ? segments : null;
    }

    /// <summary>說話者辨識；失敗不影響逐字稿，回傳全部沒有說話者。每位說話者的聲紋換成逐字稿上的編號</summary>
    (List<int?>, Dictionary<int, SpeakerVoice>, SpeakerRules.Quality?) Speakers(List<TranscribedSegment> segments, float[] samples, string source,
                                                                               Action<double> progress, CancellationToken cancel)
    {
        try
        {
            var output = engines.Diarize(samples, options.MinSpeakers, progress, cancel);
            var (speakers, numbering) = SpeakerRules.AssignNumbering(output.Turns, segments);
            var voices = new Dictionary<int, SpeakerVoice>();
            foreach (var (raw, number) in numbering)
                if (output.Voiceprints.TryGetValue(raw, out var embedding))
                    voices[number] = new SpeakerVoice(embedding, output.SpeechSeconds.GetValueOrDefault(raw), source);
            return (speakers, voices, output.Quality);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Error($"diarization failed: {e.GetType().Name}");
            return (segments.Select(_ => (int?)null).ToList(), [], null);
        }
    }
}

/// <summary>記下讀了多少取樣（快速辨識的錄音長度；檔案標頭的長度只是估計）</summary>
sealed class CountingStream(IAudioStream inner) : IAudioStream
{
    public long Count { get; private set; }
    public int EstimatedCount => inner.EstimatedCount;
    public bool Finished => inner.Finished;

    public void Fill(List<float> buffer, int count)
    {
        var before = buffer.Count;
        inner.Fill(buffer, count);
        Count += buffer.Count - before;
    }

    public void Dispose() => inner.Dispose();
}

/// <summary>已經解碼的聲音當成依序讀的來源（分聲道的快速辨識）</summary>
sealed class SampleStream(float[] samples) : IAudioStream
{
    int position;
    public int EstimatedCount => samples.Length;
    public bool Finished => position >= samples.Length;

    public void Fill(List<float> buffer, int count)
    {
        var take = Math.Min(samples.Length - position, Math.Max(0, count - buffer.Count));
        buffer.AddRange(samples.AsSpan(position, take));
        position += take;
    }

    public void Dispose() { }
}
