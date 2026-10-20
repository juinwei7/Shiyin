using SherpaOnnx;
using Shiyin.Transcribe;

namespace Shiyin.Speakers;

/// <summary>
/// 說話者辨識（design-windows §9、D47）：sherpa-onnx 的離線說話者分段（pyannote segmentation-3.0）＋聲紋嵌入，凝聚式分群。
/// sherpa-onnx 沒有的自己補：「大約幾個人」（先用門檻分，少於 N 人就改成 N 群再分一次）、每位說話者的聲紋、分得準不準的判斷
/// </summary>
public sealed class Diarizer : IDisposable
{
    public sealed record Options(string SegmentationModel, string EmbeddingModel)
    {
        /// <summary>分群的門檻（cosine 距離）：越小分出越多人。待 T-W4 用 AMI 定</summary>
        public float Threshold { get; init; } = 0.5f;
        public int Threads { get; init; } = Math.Max(1, Math.Min(4, Environment.ProcessorCount - 2));
        public float MinDurationOn { get; init; } = 0.3f;
        public float MinDurationOff { get; init; } = 0.5f;
        /// <summary>評估用：剛好分成幾群（null 是自動）</summary>
        public int? NumSpeakers { get; init; }
        /// <summary>評估用：只分人，不算聲紋和分不準的判斷（調門檻時快一點）</summary>
        public bool TurnsOnly { get; init; }
    }

    /// <summary>說話者辨識的結果：每段誰在講（依第一次出現的順序編號，從 1 開始），每位說話者的聲紋和說話的總秒數</summary>
    public sealed record Output(List<SpeakerRules.Turn> Turns, Dictionary<int, float[]> Voiceprints, Dictionary<int, double> SpeechSeconds,
                                SpeakerRules.Quality? Quality, List<SpeakerRules.ChunkEmbedding> Chunks);

    readonly Options options;
    readonly OfflineSpeakerDiarization diarization;
    readonly SpeakerEmbeddingExtractor extractor;
    OfflineSpeakerDiarizationConfig config;

    public Diarizer(Options options)
    {
        this.options = options;
        config = new OfflineSpeakerDiarizationConfig();
        config.Segmentation.Pyannote.Model = options.SegmentationModel;
        config.Segmentation.NumThreads = options.Threads;
        config.Segmentation.Provider = "cpu";
        config.Embedding.Model = options.EmbeddingModel;
        config.Embedding.NumThreads = options.Threads;
        config.Embedding.Provider = "cpu";
        config.Clustering.NumClusters = options.NumSpeakers ?? -1;
        config.Clustering.Threshold = options.Threshold;
        config.MinDurationOn = options.MinDurationOn;
        config.MinDurationOff = options.MinDurationOff;
        diarization = new OfflineSpeakerDiarization(config);
        var e = new SpeakerEmbeddingExtractorConfig { Model = options.EmbeddingModel, NumThreads = options.Threads, Provider = "cpu" };
        extractor = new SpeakerEmbeddingExtractor(e);
    }

    public void Dispose()
    {
        diarization.Dispose();
        extractor.Dispose();
    }

    public int EmbeddingDimension => extractor.Dim;

    /// <summary>minSpeakers：至少分出幾個人（M§23.2），null 自動判斷</summary>
    public Output Diarize(float[] samples, int? minSpeakers, Action<double>? progress = null, CancellationToken cancel = default)
    {
        var segments = Run(samples, options.NumSpeakers ?? -1, progress, cancel, 0, minSpeakers is null ? 0.9 : 0.45);
        var clusters = segments.Select(s => s.Speaker).Distinct().Count();
        if (minSpeakers is int min && options.NumSpeakers is null && clusters < min)
            segments = Run(samples, min, progress, cancel, 0.45, 0.45);
        cancel.ThrowIfCancellationRequested();

        // 依第一次出現的順序編號（同 Mac 的 Diarizer）
        var numbering = new Dictionary<int, int>();
        var turns = new List<SpeakerRules.Turn>();
        foreach (var s in segments.OrderBy(s => s.Start))
        {
            if (!numbering.TryGetValue(s.Speaker, out var n)) numbering[s.Speaker] = n = numbering.Count + 1;
            turns.Add(new SpeakerRules.Turn(n, s.Start, s.End));
        }
        var seconds = new Dictionary<int, double>();
        foreach (var t in turns) seconds[t.Speaker] = seconds.GetValueOrDefault(t.Speaker) + Math.Max(0, t.End - t.Start);
        var voiceprints = new Dictionary<int, float[]>();
        if (options.TurnsOnly) return new Output(turns, voiceprints, seconds, null, []);
        foreach (var speaker in seconds.Keys)
        {
            cancel.ThrowIfCancellationRequested();
            if (Voiceprint(samples, turns.Where(t => t.Speaker == speaker)) is { } v) voiceprints[speaker] = v;
        }
        var chunks = ChunkEmbeddings(samples, turns, cancel);
        progress?.Invoke(1);
        return new Output(turns, voiceprints, seconds, SpeakerRules.Measure(chunks), chunks);
    }

    (double Start, double End, int Speaker)[] Run(float[] samples, int numClusters, Action<double>? progress, CancellationToken cancel,
                                                  double progressBase, double progressSpan)
    {
        config.Clustering.NumClusters = numClusters;
        diarization.SetConfig(config);
        OfflineSpeakerDiarizationSegment[] result;
        if (progress is null && !cancel.CanBeCanceled) result = diarization.Process(samples);
        else
        {
            // sherpa-onnx 的 callback 回傳非 0 會停下
            OfflineSpeakerDiarizationProgressCallback callback = (done, total, _) =>
            {
                if (total > 0) progress?.Invoke(progressBase + progressSpan * done / total);
                return cancel.IsCancellationRequested ? 1 : 0;
            };
            result = diarization.ProcessWithCallback(samples, callback, IntPtr.Zero);
            GC.KeepAlive(callback);
        }
        cancel.ThrowIfCancellationRequested();
        return result.Select(s => ((double)s.Start, (double)s.End, s.Speaker)).ToArray();
    }

    float[]? Embed(float[] samples, double start, double end)
    {
        var from = Math.Clamp((int)(start * WhisperRules.SampleRate), 0, samples.Length);
        var to = Math.Clamp((int)(end * WhisperRules.SampleRate), from, samples.Length);
        if (to - from < (int)(0.5 * WhisperRules.SampleRate)) return null;
        using var stream = extractor.CreateStream();
        stream.AcceptWaveform((int)WhisperRules.SampleRate, samples[from..to]);
        stream.InputFinished();
        if (!extractor.IsReady(stream)) return null;
        return Normalized(extractor.Compute(stream));
    }

    static float[] Normalized(float[] v)
    {
        var norm = MathF.Sqrt(v.Sum(x => x * x));
        return norm > 0 ? v.Select(x => x / norm).ToArray() : v;
    }

    /// <summary>一位說話者的聲紋：最長的幾段（加起來最多 60 秒）各算一次再平均（M§21.4）</summary>
    float[]? Voiceprint(float[] samples, IEnumerable<SpeakerRules.Turn> turns)
    {
        var picked = new List<float[]>();
        var total = 0.0;
        foreach (var t in turns.Where(t => t.End - t.Start >= 1).OrderByDescending(t => t.End - t.Start))
        {
            if (total >= 60 || picked.Count >= 12) break;
            var end = Math.Min(t.End, t.Start + 20);
            if (Embed(samples, t.Start, end) is { } e) { picked.Add(e); total += end - t.Start; }
        }
        if (picked.Count == 0) return null;
        var sum = new float[picked[0].Length];
        foreach (var p in picked) for (var i = 0; i < sum.Length; i++) sum[i] += p[i];
        return Normalized(sum);
    }

    /// <summary>每 10 秒一窗，窗裡說了 1.5 秒以上的每位說話者算一次聲紋：給分不準的判斷（M§24.5）</summary>
    List<SpeakerRules.ChunkEmbedding> ChunkEmbeddings(float[] samples, List<SpeakerRules.Turn> turns, CancellationToken cancel)
    {
        const double window = 10;
        var chunks = new List<SpeakerRules.ChunkEmbedding>();
        var duration = samples.Length / WhisperRules.SampleRate;
        for (var (index, start) = (0, 0.0); start < duration; index++, start += window)
        {
            cancel.ThrowIfCancellationRequested();
            var end = start + window;
            var local = 0;
            foreach (var group in turns.Where(t => t.End > start && t.Start < end).GroupBy(t => t.Speaker).OrderBy(g => g.Key))
            {
                var pieces = group.Select(t => (Math.Max(t.Start, start), Math.Min(t.End, end))).ToList();
                if (pieces.Sum(p => p.Item2 - p.Item1) < 1.5) continue;
                var audio = pieces.SelectMany(p => samples[Math.Clamp((int)(p.Item1 * WhisperRules.SampleRate), 0, samples.Length)..
                                                           Math.Clamp((int)(p.Item2 * WhisperRules.SampleRate), 0, samples.Length)]).ToArray();
                using var stream = extractor.CreateStream();
                stream.AcceptWaveform((int)WhisperRules.SampleRate, audio);
                stream.InputFinished();
                if (!extractor.IsReady(stream)) continue;
                chunks.Add(new SpeakerRules.ChunkEmbedding($"S{group.Key}", index, local++, Normalized(extractor.Compute(stream))));
            }
        }
        return chunks;
    }
}
