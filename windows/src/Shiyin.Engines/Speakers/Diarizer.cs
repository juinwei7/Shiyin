using SherpaOnnx;
using Shiyin.Transcribe;

namespace Shiyin.Speakers;

/// <summary>
/// 說話者辨識（design-windows §9、D47、D58）：sherpa-onnx 的離線說話者分段（pyannote segmentation-3.0）先分得很細，
/// 只用它切出的段落；每段切成約 3 秒的小段各算聲紋，用 <see cref="SpeakerClustering"/> 重新分群。
/// sherpa-onnx 沒有的自己補：「至少 N 人」、每位說話者的聲紋、分得準不準的判斷
/// </summary>
public sealed class Diarizer : IDisposable
{
    public sealed record Options(string SegmentationModel, string EmbeddingModel)
    {
        /// <summary>sherpa-onnx 內部分群用的聲紋模型（只影響切段落，用快的就好）；null 跟 EmbeddingModel 相同</summary>
        public string? SegmentationEmbeddingModel { get; init; }
        /// <summary>sherpa-onnx 固定分成幾群：分得很細，切出的段落才不會跨兩個人（T-W4）</summary>
        public int SegmentClusters { get; init; } = 30;
        public int Threads { get; init; } = Math.Max(1, Math.Min(4, Environment.ProcessorCount - 2));
        public float MinDurationOn { get; init; } = 0.3f;
        public float MinDurationOff { get; init; } = 0.5f;
        /// <summary>pyannote 每次往後移多少（10 秒窗的比例）：sherpa-onnx 預設 0.1；0.5 快五倍，結果差不多（T-W4）</summary>
        public float WindowShiftRatio { get; init; } = 0.5f;
        /// <summary>每段切成幾秒的小段算聲紋</summary>
        public double PieceSeconds { get; init; } = 3;
        public SpeakerClustering.Options Clustering { get; init; } = new();
        /// <summary>評估用：剛好分成幾群（null 是自動）</summary>
        public int? NumSpeakers { get; init; }
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
        config.Segmentation.Pyannote.WindowShiftRatio = options.WindowShiftRatio;
        config.Segmentation.NumThreads = options.Threads;
        config.Segmentation.Provider = "cpu";
        config.Embedding.Model = options.SegmentationEmbeddingModel ?? options.EmbeddingModel;
        config.Embedding.NumThreads = options.Threads;
        config.Embedding.Provider = "cpu";
        config.Clustering.NumClusters = options.SegmentClusters;
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

    /// <summary>minSpeakers：至少分出幾個人（M§23.2），null 自動判斷。進度：切段落 0～0.6、算聲紋 0.6～1</summary>
    public Output Diarize(float[] samples, int? minSpeakers, Action<double>? progress = null, CancellationToken cancel = default)
    {
        var (raw, pieces) = Segment(samples, progress, cancel);
        var turns = raw.Select(t => new SpeakerRules.Turn(t.Speaker, t.Start, t.End)).ToList();
        var clustering = options.NumSpeakers is int fixedCount
            ? options.Clustering with { Threshold = 2, MinSpeakers = fixedCount }
            : options.Clustering with { MinSpeakers = minSpeakers };
        var labels = SpeakerClustering.Cluster(turns, pieces, clustering);
        cancel.ThrowIfCancellationRequested();

        // 依第一次出現的順序編號（同 Mac 的 Diarizer），從 1 開始；turns 已經依開始時間排好
        var numbering = new Dictionary<int, int>();
        var numbered = new SpeakerRules.Turn[turns.Count];
        for (var i = 0; i < turns.Count; i++)
        {
            if (!numbering.TryGetValue(labels[i], out var n)) numbering[labels[i]] = n = numbering.Count + 1;
            numbered[i] = turns[i] with { Speaker = n };
        }
        var seconds = new Dictionary<int, double>();
        foreach (var t in numbered) seconds[t.Speaker] = seconds.GetValueOrDefault(t.Speaker) + Math.Max(0, t.End - t.Start);
        var chunks = ChunkEmbeddings(numbered, raw, pieces);
        progress?.Invoke(1);
        return new Output([.. numbered], Voiceprints(numbered, pieces), seconds, SpeakerRules.Measure(chunks), chunks);
    }

    /// <summary>sherpa-onnx 切的段落（sherpa 的編號，依開始時間排好），每段切成小段的聲紋</summary>
    public ((double Start, double End, int Speaker)[] Turns, List<SpeakerClustering.Piece> Pieces) Segment(
        float[] samples, Action<double>? progress = null, CancellationToken cancel = default)
    {
        // 太短的錄音：群數不超過每 10 秒一群（sherpa-onnx 片段裡的說話者比要分的群少時會出錯）
        var clusters = Math.Max(1, Math.Min(options.SegmentClusters, samples.Length / (int)(10 * WhisperRules.SampleRate)));
        var turns = Run(samples, clusters, progress, cancel);
        var pieces = new List<SpeakerClustering.Piece>();
        var total = Math.Max(turns.Sum(t => t.End - t.Start), 1e-6);
        var done = 0.0;
        for (var i = 0; i < turns.Length; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var (start, end, _) = turns[i];
            var count = Math.Max(1, (int)Math.Round((end - start) / options.PieceSeconds));
            var length = (end - start) / count;
            for (var k = 0; k < count; k++)
            {
                var (a, b) = (start + k * length, start + (k + 1) * length);
                if (Embed(samples, a, b) is { } e) pieces.Add(new SpeakerClustering.Piece(i, a, b, e));
            }
            done += end - start;
            progress?.Invoke(0.6 + 0.4 * done / total);
        }
        return (turns, pieces);
    }

    (double Start, double End, int Speaker)[] Run(float[] samples, int numClusters, Action<double>? progress, CancellationToken cancel)
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
                if (total > 0) progress?.Invoke(0.6 * done / total);
                return cancel.IsCancellationRequested ? 1 : 0;
            };
            result = diarization.ProcessWithCallback(samples, callback, IntPtr.Zero);
            GC.KeepAlive(callback);
        }
        cancel.ThrowIfCancellationRequested();
        return result.Select(s => ((double)s.Start, (double)s.End, s.Speaker)).OrderBy(s => s.Item1).ToArray();
    }

    public float[]? Embed(float[] samples, double start, double end)
    {
        var from = Math.Clamp((int)(start * WhisperRules.SampleRate), 0, samples.Length);
        var to = Math.Clamp((int)(end * WhisperRules.SampleRate), from, samples.Length);
        if (to - from < (int)(0.5 * WhisperRules.SampleRate)) return null;
        using var stream = extractor.CreateStream();
        stream.AcceptWaveform((int)WhisperRules.SampleRate, samples[from..to]);
        stream.InputFinished();
        if (!extractor.IsReady(stream)) return null;
        return SpeakerClustering.Normalized(extractor.Compute(stream));
    }

    static float[] Average(IEnumerable<SpeakerClustering.Piece> pieces)
    {
        float[]? sum = null;
        foreach (var p in pieces)
        {
            sum ??= new float[p.Embedding.Length];
            for (var i = 0; i < sum.Length; i++) sum[i] += p.Embedding[i] * (float)p.Seconds;
        }
        return sum is null ? [] : SpeakerClustering.Normalized(sum);
    }

    /// <summary>一位說話者的聲紋：最長的幾段（加起來到 60 秒）的小段，依長度加權平均（M§21.4；Mac 是最長的幾段各算一次）</summary>
    static Dictionary<int, float[]> Voiceprints(SpeakerRules.Turn[] turns, List<SpeakerClustering.Piece> pieces)
    {
        var byTurn = pieces.GroupBy(p => p.Turn).ToDictionary(g => g.Key, g => g.ToList());
        var result = new Dictionary<int, float[]>();
        foreach (var speaker in turns.Select(t => t.Speaker).Distinct())
        {
            var picked = new List<SpeakerClustering.Piece>();
            var total = 0.0;
            foreach (var i in Enumerable.Range(0, turns.Length).Where(i => turns[i].Speaker == speaker && turns[i].End - turns[i].Start >= 1)
                         .OrderByDescending(i => turns[i].End - turns[i].Start))
            {
                if (total >= 60) break;
                if (!byTurn.TryGetValue(i, out var list)) continue;
                picked.AddRange(list);
                total += list.Sum(p => p.Seconds);
            }
            if (picked.Count > 0) result[speaker] = Average(picked);
        }
        return result;
    }

    /// <summary>
    /// 分不準的判斷用（M§24.5）：每 10 秒一窗，窗裡說了 1.5 秒以上的每位說話者，用那幾個小段的聲紋平均。
    /// 窗裡的「本地說話者」是 sherpa-onnx 分得很細的編號：sherpa 分成兩個人、重新分群卻是同一人，就是碰撞
    /// </summary>
    static List<SpeakerRules.ChunkEmbedding> ChunkEmbeddings(SpeakerRules.Turn[] turns, (double Start, double End, int Speaker)[] raw,
                                                            List<SpeakerClustering.Piece> pieces)
    {
        const double window = 10;
        var chunks = new List<SpeakerRules.ChunkEmbedding>();
        foreach (var group in pieces.GroupBy(p => (int)(p.Start / window)).OrderBy(g => g.Key))
            foreach (var local in group.GroupBy(p => raw[p.Turn].Speaker).OrderBy(g => g.Key))
            {
                if (local.Sum(p => p.Seconds) < 1.5) continue;
                var speaker = turns[local.First().Turn].Speaker;
                chunks.Add(new SpeakerRules.ChunkEmbedding($"S{speaker}", group.Key, local.Key, Average(local)));
            }
        return chunks;
    }
}
