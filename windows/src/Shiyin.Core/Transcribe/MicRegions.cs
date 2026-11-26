using Shiyin.Model;

namespace Shiyin.Transcribe;

/// <summary>偵測的結果：FloorDb 是基準（全部沒有聲音時是 SilentDb）；ThresholdDb 是 null 時是有聲音的太少、沒偵測</summary>
public sealed record MicRegionResult(double FloorDb, double? ThresholdDb, List<(double Start, double End)> Regions)
{
    public bool TooLittle => ThresholdDb is null;
}

/// <summary>麥克風的「建議收錄」（R119.3、M§93.2、D201），規則跟 Mac 的 MicRegions 相同（標準答案 pipeline/mic-regions.json）</summary>
public static class MicRegions
{
    /// <summary>規則都用「格」（100 ms）算，最後才換成秒，兩個平台算出來的數字一樣</summary>
    public static class Rules
    {
        public const double FrameSeconds = 0.1;
        public const int BaselineFrames = 600;
        public const int MinVoicedFrames = 100;
        public const double MarginDb = 6;
        public const double MinThresholdDb = -50;
        public const double SilentDb = -90;
        public const int JoinGapFrames = 50;
        public const int MinLengthFrames = 4;
        public const int PadBeforeFrames = 3;
        public const int PadAfterFrames = 5;
        /// <summary>整格的取樣都比這個小就當成全是 0（暫停麥克風、硬體靜音）</summary>
        public const float ZeroSample = 1e-6f;
    }

    public static MicRegionResult Detect(IReadOnlyList<double?> levels)
    {
        var voiced = levels.Where(l => l is not null).Select(l => l!.Value).ToList();
        if (voiced.Count < Rules.MinVoicedFrames)
            return new(voiced.Count == 0 ? Rules.SilentDb : Mean(voiced), null, []);
        var floor = Mean(voiced.Take(Rules.BaselineFrames).ToList());
        var threshold = Math.Max(floor + Rules.MarginDb, Rules.MinThresholdDb);

        var runs = new List<(int S, int E)>();
        for (var i = 0; i < levels.Count; i++)
        {
            if (levels[i] is not double level || !(level > threshold)) continue;
            if (runs.Count > 0 && runs[^1].E == i) runs[^1] = (runs[^1].S, i + 1);
            else runs.Add((i, i + 1));
        }
        var joined = new List<(int S, int E)>();
        foreach (var run in runs)
        {
            if (joined.Count > 0 && run.S - joined[^1].E < Rules.JoinGapFrames) joined[^1] = (joined[^1].S, run.E);
            else joined.Add(run);
        }
        var padded = new List<(int S, int E)>();
        foreach (var run in joined.Where(r => r.E - r.S >= Rules.MinLengthFrames))
        {
            int s = Math.Max(0, run.S - Rules.PadBeforeFrames), e = Math.Min(levels.Count, run.E + Rules.PadAfterFrames);
            if (padded.Count > 0 && s <= padded[^1].E) padded[^1] = (padded[^1].S, Math.Max(padded[^1].E, e));
            else padded.Add((s, e));
        }
        return new(floor, threshold, [.. padded.Select(r => (r.S / 10.0, r.E / 10.0))]);
    }

    static double Mean(List<double> values)
    {
        var sum = 0.0;
        foreach (var v in values) sum += v;
        return sum / values.Count;
    }

    /// <summary>每 100 ms 一格的音量（dBFS，下限 SilentDb）；整格是 0 的是 null。samples 是麥克風那一聲道</summary>
    public static List<double?> Levels(IEnumerable<float> samples, int sampleRate)
    {
        var frameLength = Math.Max(1, (int)Math.Round(sampleRate * Rules.FrameSeconds));
        var output = new List<double?>();
        double sum = 0;
        int count = 0;
        var loud = false;
        void Flush()
        {
            if (count == 0) return;
            output.Add(loud ? Math.Max(Rules.SilentDb, 10 * Math.Log10(Math.Max(sum / count, 1e-12))) : null);
            sum = 0; count = 0; loud = false;
        }
        foreach (var x in samples)
        {
            if (Math.Abs(x) >= Rules.ZeroSample) loud = true;
            sum += (double)x * x;
            if (++count == frameLength) Flush();
        }
        Flush();
        return output;
    }

    /// <summary>區塊照開始排好，重疊或間隔不到 gap 的接起來；接起來的有一塊是使用者的就算使用者的</summary>
    public static List<MicRegion> Normalized(IEnumerable<MicRegion> regions, double gap = 0)
    {
        var output = new List<MicRegion>();
        foreach (var r in regions.Where(r => r.End > r.Start).OrderBy(r => r.Start).ThenBy(r => r.End))
        {
            if (output.Count > 0 && r.Start - output[^1].End <= gap)
            {
                var last = output[^1];
                last.End = Math.Max(last.End, r.End);
                if (r.Origin == MicRegion.User) last.Origin = MicRegion.User;
            }
            else
            {
                output.Add(new MicRegion { MeetingId = r.MeetingId, Start = r.Start, End = r.End, Origin = r.Origin });
            }
        }
        return output;
    }
}
