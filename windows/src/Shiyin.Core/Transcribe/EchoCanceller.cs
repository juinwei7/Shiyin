using Shiyin.IO;

namespace Shiyin.Transcribe;

/// <summary>依序讀出 16 kHz 的雙聲道（聲道 0 麥克風、1 電腦聲音），每次接在 mic、reference 後面加一段；讀完就不再加</summary>
public interface IStereoSource : IDisposable
{
    bool Finished { get; }
    void Read(EchoCanceller.Window mic, EchoCanceller.Window reference);
}

/// <summary>
/// 喇叭回音消除（R123、M§97、design-windows §11.56）：用喇叭開線上會議時，麥克風會收到電腦放出來的對方聲音。轉錄前拿電腦聲音那一聲道當參考，
/// 把麥克風聲道裡的回音扣掉，寫成暫存的雙聲道檔給後面的轉錄、說話者辨識用；錄音檔本身不動。算法跟 Mac 的 EchoCanceller 相同：
/// 1. 找時間差：每 1 秒取 4 秒，用 GCC-PHAT 找回音比電腦聲音晚多少（可能是負的），連成分段的直線（時鐘漂移是斜率、中途跳一下就換一段）。
///    找不到穩定的時間差就是沒有回音（戴耳機），不處理
/// 2. 電腦聲音照時間差重新取樣對齊，再用分塊頻域 Kalman 濾波器估計回音路徑、從麥克風扣掉
/// 3. 每個頻帶不讓輸出比原本的麥克風大聲；離回音太遠的地方照原本的麥克風
/// 依序讀檔，記憶體不隨錄音長度增加。很花 CPU、會卡住呼叫的執行緒
/// </summary>
public static class EchoCanceller
{
    public const int Rate = 16000;

    /// <summary>時間差是固定斜率的一段：時間差（取樣點）＝ Slope × 位置 ＋ Intercept；位置、時間差都是 16 kHz 的取樣點</summary>
    public sealed record LagSegment(int Start, int End, double Slope, double Intercept)
    {
        public double LagAt(int n) => Slope * n + Intercept;
    }

    public sealed class Report
    {
        /// <summary>電腦聲音、麥克風都有聲音的 4 秒視窗數，和其中找得到回音的</summary>
        public int Windows { get; set; }
        public int Confident { get; set; }
        public List<LagSegment> Segments { get; set; } = [];
        /// <summary>有回音、有處理的時間（秒，[開始, 結束]）</summary>
        public List<double[]> Zones { get; set; } = [];
        public double Seconds { get; set; }
        public bool EchoFound => Segments.Count > 0;
    }

    /// <summary>有回音時寫出 output（聲道 0 扣掉回音的麥克風、聲道 1 原本的電腦聲音，16-bit WAV）；沒有回音時不寫檔。open 會叫三次（找時間差、預習、濾波）</summary>
    public static Report Process(Func<IStereoSource> open, string output, CancellationToken cancel = default)
    {
        var started = DateTime.UtcNow;
        Report report;
        Plan? plan;
        using (var source = open()) (report, plan) = Analyze(source, cancel);
        if (plan != null)
        {
            using var writer = new WavWriter(output, 2);
            using var prime = open();
            using var main = open();
            FilterPass.Run(prime, main, plan, cancel, (mic, reference, count) => writer.Write([mic, reference], count));
            writer.Flush();
        }
        report.Seconds = (DateTime.UtcNow - started).TotalSeconds;
        return report;
    }

    /// <summary>記憶體裡的雙聲道（標準答案、測試用）：回傳扣掉回音的麥克風，沒有回音時是 null</summary>
    public static (Report Report, float[]? Cleaned) Process(float[] mic, float[] reference)
    {
        var (report, plan) = Analyze(new MemoryStereo(mic, reference), default);
        if (plan == null) return (report, null);
        var cleaned = new List<float>(mic.Length);
        FilterPass.Run(new MemoryStereo(mic, reference), new MemoryStereo(mic, reference), plan, default,
            (block, _, count) => cleaned.AddRange(block.AsSpan(0, count)));
        return (report, [.. cleaned]);
    }

    static (Report, Plan?) Analyze(IStereoSource source, CancellationToken cancel)
    {
        var found = LagFinder.Run(source, cancel);
        var report = new Report { Windows = found.Windows, Confident = found.Confident };
        var plan = Plan.Make(found);
        if (plan == null) return (report, null);
        report.Segments = plan.Segments;
        report.Zones = plan.Zones.Select(z => new[] { (double)z.Start / Rate, (double)z.End / Rate }).ToList();
        return (report, plan);
    }

    // ---- 讀檔 ----

    /// <summary>從頭依序讀 WAV（16 kHz 雙聲道；Mac 上的測試、錄音還沒壓縮時）</summary>
    public sealed class WavStereo(string path) : IStereoSource
    {
        readonly WavReader reader = new(path);
        public bool Finished { get; private set; }

        public void Read(Window mic, Window reference)
        {
            var block = reader.Read(Rate);
            if (block.Length == 0 || block[0].Length == 0)
            {
                Finished = true;
                return;
            }
            mic.Append(block[0]);
            reference.Append(block.Length > 1 ? block[1] : new float[block[0].Length]);
        }

        public void Dispose() => reader.Dispose();
    }

    sealed class MemoryStereo(float[] mic, float[] reference) : IStereoSource
    {
        int position;
        public bool Finished => position >= mic.Length;

        public void Read(Window outMic, Window outReference)
        {
            var end = Math.Min(mic.Length, position + Rate);
            outMic.Append(mic.AsSpan(position, end - position));
            var r = new float[end - position];
            for (var i = 0; i < r.Length; i++) r[i] = position + i < reference.Length ? reference[position + i] : 0;
            outReference.Append(r);
            position = end;
        }

        public void Dispose() { }
    }

    /// <summary>依序讀雙聲道，留著還要用的那一段（絕對位置存取，超出範圍是 0）</summary>
    sealed class Reader(IStereoSource source)
    {
        public Window Mic { get; } = new();
        public Window Reference { get; } = new();

        /// <summary>讀到兩個聲道都至少有 end 個取樣點（或讀完）</summary>
        public void ReadUpTo(int end)
        {
            while (Mic.End < end && !source.Finished) source.Read(Mic, Reference);
        }

        public void Drop(int before)
        {
            Mic.Drop(before);
            Reference.Drop(before);
        }
    }

    /// <summary>一段連續的取樣，用絕對位置存取（超出範圍是 0）</summary>
    public sealed class Window
    {
        float[] data = new float[Rate * 8];
        int count;
        public int Base { get; private set; }
        public int End => Base + count;

        public float this[int n] => n >= Base && n < Base + count ? data[n - Base] : 0;

        public void Append(ReadOnlySpan<float> values)
        {
            Reserve(values.Length);
            values.CopyTo(data.AsSpan(count));
            count += values.Length;
        }

        public void Append(int length, Func<int, float> value)
        {
            Reserve(length);
            for (var i = 0; i < length; i++) data[count + i] = value(i);
            count += length;
        }

        void Reserve(int more)
        {
            if (count + more <= data.Length) return;
            Array.Resize(ref data, Math.Max(data.Length * 2, count + more));
        }

        /// <summary>丟掉 n 之前的（累積夠多才真的搬）</summary>
        public void Drop(int n)
        {
            var k = Math.Min(Math.Max(0, n - Base), count);
            if (k < Rate * 4) return;
            Array.Copy(data, k, data, 0, count - k);
            count -= k;
            Base += k;
        }

        /// <summary>Σ weights[offset + j] × this[first + j]</summary>
        public float Dot(float[] weights, int offset, int first, int length)
        {
            var sum = 0f;
            if (first >= Base && first + length <= Base + count)
            {
                var o = first - Base;
                for (var j = 0; j < length; j++) sum += weights[offset + j] * data[o + j];
            }
            else
            {
                for (var j = 0; j < length; j++) sum += weights[offset + j] * this[first + j];
            }
            return sum;
        }

        /// <summary>從絕對位置 start 起複製 length 個到 output（超出範圍補 0）</summary>
        public void CopyTo(int start, int length, Span<float> output)
        {
            for (var i = 0; i < length; i++) output[i] = this[start + i];
        }
    }

    // ---- 1. 找時間差 ----

    /// <summary>GCC-PHAT 在 8 kHz 做（相鄰兩點平均降頻）：4 秒的電腦聲音對前後各多 2 秒的麥克風，看 ±2 秒內哪裡最像</summary>
    public static class LagFinder
    {
        public const int Rate8 = 8000;
        public const int WindowLength = 4 * Rate8;
        public const int Hop = Rate8;
        public const int MaxLag = 2 * Rate8;
        const int Log2N = 16;   // 65536 ≥ WindowLength + 2 × MaxLag
        /// <summary>PHAT 只看這段頻率：低頻有冷氣、電源的嗡嗡聲，筆電喇叭也放不出來</summary>
        const double BandLow = 150, BandHigh = 3800;
        /// <summary>峰值比平均高幾個標準差才算找到；沒有回音時最高的峰大約 4～5</summary>
        const float ZMin = 9;

        public sealed record Point(int Time, double Lag, float Z);

        public sealed class Found
        {
            public List<Point> Points { get; } = [];   // 找到的（16 kHz 的取樣點）
            public int Windows { get; set; }
            public int Confident { get; set; }
            public double MicEnergy { get; set; }
            public double RefEnergy { get; set; }
            public int Total { get; set; }              // 錄音長度（16 kHz 的取樣點）
        }

        public static Found Run(IStereoSource source, CancellationToken cancel)
        {
            var reader = new Reader(source);
            var fft = new RealFft(Log2N);
            var n = 1 << Log2N;
            var bins = n / 2 + 1;
            var found = new Found();
            var mic8 = new Window();
            var ref8 = new Window();
            var seg = new float[n];
            var refBuf = new float[n];
            float[] sr = new float[bins], si = new float[bins], rr = new float[bins], ri = new float[bins];
            var corr = new float[n];
            var low = (int)Math.Ceiling(BandLow * n / Rate8);
            var high = (int)Math.Floor(BandHigh * n / Rate8);
            var start = 0;
            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                // 降頻：16 kHz 的第 2i、2i+1 點平均成 8 kHz 的第 i 點
                reader.ReadUpTo(2 * (start + WindowLength + MaxLag));
                var available = reader.Mic.End / 2;
                if (mic8.End < available)
                {
                    var from = mic8.End;
                    mic8.Append(available - from, i => (reader.Mic[2 * (from + i)] + reader.Mic[2 * (from + i) + 1]) * 0.5f);
                    ref8.Append(available - from, i => (reader.Reference[2 * (from + i)] + reader.Reference[2 * (from + i) + 1]) * 0.5f);
                }
                reader.Drop(2 * (start - MaxLag));
                if (start + WindowLength > available) break;
                // 這個視窗：電腦聲音 [start, start+window)，麥克風 [start-maxLag, start+window+maxLag)
                Array.Clear(seg);
                Array.Clear(refBuf);
                mic8.CopyTo(start - MaxLag, WindowLength + 2 * MaxLag, seg);
                ref8.CopyTo(start, WindowLength, refBuf);
                var refPower = MeanSquare(refBuf, 0, WindowLength);
                var micPower = MeanSquare(seg, MaxLag, WindowLength);
                var segPower = MeanSquare(seg, 0, WindowLength + 2 * MaxLag);
                if (refPower > 3.2e-6f && segPower > 1e-7f)   // -55 dBFS、-70 dBFS
                {
                    found.Windows++;
                    found.MicEnergy += micPower;
                    found.RefEnergy += refPower;
                    fft.Forward(seg, sr, si);
                    fft.Forward(refBuf, rr, ri);
                    for (var k = 0; k < bins; k++)
                    {
                        if (k < low || k > high)
                        {
                            sr[k] = 0;
                            si[k] = 0;
                            continue;
                        }
                        // seg × conj(ref)，再除以大小（PHAT）
                        var re = sr[k] * rr[k] + si[k] * ri[k];
                        var im = si[k] * rr[k] - sr[k] * ri[k];
                        var mag = MathF.Sqrt(re * re + im * im) + 1e-12f;
                        sr[k] = re / mag;
                        si[k] = im / mag;
                    }
                    fft.Inverse(sr, si, corr);
                    var count = 2 * MaxLag + 1;
                    var best = 0;
                    float mean = 0, sq = 0;
                    for (var i = 0; i < count; i++)
                    {
                        mean += corr[i];
                        sq += corr[i] * corr[i];
                        if (corr[i] > corr[best]) best = i;
                    }
                    mean /= count;
                    var std = MathF.Sqrt(Math.Max(0, sq / count - mean * mean));
                    var z = (corr[best] - mean) / (std + 1e-12f);
                    if (z >= ZMin)
                    {
                        var frac = 0.0;
                        if (best > 0 && best < count - 1)
                        {
                            double a = corr[best - 1], b = corr[best], c = corr[best + 1];
                            var den = a - 2 * b + c;
                            if (den < 0) frac = 0.5 * (a - c) / den;
                        }
                        found.Confident++;
                        found.Points.Add(new Point(2 * (start + WindowLength / 2), 2 * (best - MaxLag + frac), z));
                    }
                }
                mic8.Drop(start + Hop - MaxLag);
                ref8.Drop(start + Hop - MaxLag);
                start += Hop;
            }
            found.Total = reader.Mic.End;
            return found;
        }

        static float MeanSquare(float[] x, int offset, int count)
        {
            var sum = 0f;
            for (var i = 0; i < count; i++) sum += x[offset + i] * x[offset + i];
            return sum / count;
        }
    }

    // ---- 時間差 → 分段直線、要處理的區間 ----

    public sealed class Plan
    {
        const double Tolerance = 0.003 * Rate;   // 離直線 3 ms 以內算同一段
        const int MinPoints = 5;                 // 一段至少幾個點才算數（沒有回音時偶爾有零星的假峰）
        const int ZonePadding = 10 * Rate;       // 找到回音的地方前後 10 秒內才處理
        public required List<LagSegment> Segments { get; init; }
        public required List<(int Start, int End)> Zones { get; init; }
        /// <summary>參考訊號（電腦聲音）的音量調到跟麥克風差不多，濾波器的初始值才不受音量影響</summary>
        public float Gain { get; init; }
        public int Total { get; init; }

        public static Plan? Make(LagFinder.Found found)
        {
            var groups = new List<List<LagFinder.Point>>();
            var current = new List<LagFinder.Point>();
            var pending = new List<LagFinder.Point>();
            foreach (var point in found.Points)
            {
                if (current.Count == 0)
                {
                    current.Add(point);
                    continue;
                }
                var (k, b) = Fit(current.Skip(Math.Max(0, current.Count - 30)).ToList());
                if (Math.Abs(point.Lag - (k * point.Time + b)) <= Tolerance)
                {
                    current.AddRange(pending);
                    pending.Clear();
                    current.Add(point);
                }
                else
                {
                    pending.Add(point);
                    if (pending.Count < 3) continue;
                    // 連續三個點都不在線上、彼此一致：從這裡換一段
                    if (pending.Max(p => p.Lag) - pending.Min(p => p.Lag) <= 2 * Tolerance)
                    {
                        groups.Add(current);
                        current = pending;
                        pending = [];
                    }
                    else
                    {
                        pending.RemoveAt(0);
                    }
                }
            }
            if (current.Count > 0) groups.Add(current);
            groups = groups.Where(g => g.Count >= MinPoints).ToList();
            var used = groups.Sum(g => g.Count);
            // 有回音：夠多個點、佔有聲音的視窗至少一成
            if (used < 8 || used < 0.1 * found.Windows || found.RefEnergy <= 0) return null;
            var segments = groups.Select(g =>
            {
                var (k, b) = Fit(g);
                return new LagSegment(g[0].Time, g[^1].Time, k, b);
            }).ToList();
            // 段跟段之間在中點換；頭尾延伸到整段錄音
            for (var i = 0; i < segments.Count - 1; i++)
            {
                var mid = (segments[i].End + segments[i + 1].Start) / 2;
                segments[i] = segments[i] with { End = mid };
                segments[i + 1] = segments[i + 1] with { Start = mid };
            }
            segments[0] = segments[0] with { Start = 0 };
            segments[^1] = segments[^1] with { End = Math.Max(found.Total, segments[^1].End) };
            // 處理的區間：找到回音的點前後 10 秒，重疊的接起來
            var zones = new List<(int Start, int End)>();
            foreach (var point in groups.SelectMany(g => g).OrderBy(p => p.Time))
            {
                var (lo, hi) = (Math.Max(0, point.Time - ZonePadding), Math.Min(found.Total, point.Time + ZonePadding));
                if (zones.Count > 0 && lo <= zones[^1].End) zones[^1] = (zones[^1].Start, Math.Max(zones[^1].End, hi));
                else zones.Add((lo, hi));
            }
            return new Plan
            {
                Segments = segments, Zones = zones, Total = found.Total,
                Gain = (float)Math.Sqrt(found.MicEnergy / found.RefEnergy),
            };
        }

        /// <summary>最小平方法；離群的（超過殘差中位數 3 倍、也超過 2 個取樣點）去掉再算一次</summary>
        public static (double Slope, double Intercept) Fit(IReadOnlyList<LagFinder.Point> points)
        {
            if (points.Count < 3)
            {
                var lags = points.Select(p => p.Lag).Order().ToList();
                return (0, lags[lags.Count / 2]);
            }
            static (double, double) Solve(IReadOnlyList<LagFinder.Point> ps)
            {
                double n = ps.Count;
                double t0 = ps[0].Time;   // 時間從第一個點算起，數值比較穩
                double st = 0, sl = 0, stt = 0, stl = 0;
                foreach (var p in ps)
                {
                    var t = p.Time - t0;
                    st += t;
                    sl += p.Lag;
                    stt += t * t;
                    stl += t * p.Lag;
                }
                var den = n * stt - st * st;
                var k = Math.Abs(den) < 1e-9 ? 0 : (n * stl - st * sl) / den;
                var b = (sl - k * st) / n;
                return (k, b - k * t0);
            }
            var (k, b) = Solve(points);
            var residuals = points.Select(p => Math.Abs(p.Lag - (k * p.Time + b))).ToList();
            var sorted = residuals.Order().ToList();
            var limit = Math.Max(2, 3 * sorted[sorted.Count / 2]);
            var kept = points.Where((_, i) => residuals[i] <= limit).ToList();
            return kept.Count >= 3 ? Solve(kept) : (k, b);
        }
    }

    // ---- 2、3. 對齊、濾波、輸出 ----

    static class FilterPass
    {
        const int Block = KalmanFilter.Block;
        /// <summary>回音路徑從濾波器的第 2 塊開始（32 ms），時間差估得不準、時鐘漂移都還在範圍內</summary>
        const int Margin = 2 * Block;
        /// <summary>先用前 30 秒學一遍再從頭處理，開頭的回音才扣得掉</summary>
        const int PrimeSeconds = 30;
        /// <summary>換段（時間差跳動）的前後 2 秒不學：這時對齊可能是錯的，學了會把濾波器帶歪</summary>
        const int FreezeSamples = 2 * Rate;
        /// <summary>進出處理區間時跟原本的麥克風交叉淡入淡出</summary>
        const int FadeSamples = Rate / 4;

        /// <summary>sink 收到（扣掉回音的麥克風、原本的電腦聲音、這一塊有幾個是有效的）</summary>
        public static void Run(IStereoSource prime, IStereoSource main, Plan plan, CancellationToken cancel, Action<float[], float[], int> sink)
        {
            var filter = new KalmanFilter();
            var resampler = new Resampler();
            var frozen = plan.Segments.Skip(1).Select(s => (Start: Math.Max(0, s.Start - FreezeSamples), End: s.Start + FreezeSamples)).ToList();
            // 預習：前 30 秒跑一遍，只留下濾波器學到的東西
            var primeBlocks = Math.Min(plan.Total, PrimeSeconds * Rate) / Block;
            Pass(prime, plan, filter, resampler, frozen, primeBlocks, cancel, null);
            filter.ResetHistory();
            var clamp = new Clamp();
            var pendingRef = new Window();
            var outMic = new float[Block];
            var blocks = (plan.Total + Block - 1) / Block;
            Pass(main, plan, filter, resampler, frozen, blocks, cancel, (k, mic, cleaned, reference) =>
            {
                // 每個頻帶不比原本的麥克風大聲；輸出晚一塊
                clamp.Process(mic, cleaned, outMic);
                pendingRef.Append(reference);
                if (k > 0)
                {
                    var start = (k - 1) * Block;
                    var r = new float[Block];
                    for (var i = 0; i < Block; i++) r[i] = pendingRef[start + i];
                    sink(outMic, r, Math.Min(Block, plan.Total - start));
                }
                pendingRef.Drop((k - 1) * Block);
            });
            // 最後一塊
            var last = (blocks - 1) * Block;
            if (last >= 0 && last < plan.Total)
            {
                clamp.Process(new float[Block], new float[Block], outMic);
                var r = new float[Block];
                for (var i = 0; i < Block; i++) r[i] = pendingRef[last + i];
                sink(outMic, r, Math.Min(Block, plan.Total - last));
            }
        }

        /// <summary>從頭讀 blocks 塊：對齊參考訊號、濾波；sink 收到（第幾塊、麥克風、扣掉回音的麥克風、原本的電腦聲音），null 是預習</summary>
        static void Pass(IStereoSource source, Plan plan, KalmanFilter filter, Resampler resampler, List<(int Start, int End)> frozen, int blocks,
                         CancellationToken cancel, Action<int, float[], float[], float[]>? sink)
        {
            var reader = new Reader(source);
            var maxShift = plan.Segments.Max(s => Math.Max(Math.Abs(s.LagAt(s.Start)), Math.Abs(s.LagAt(s.End))));
            var lookahead = (int)maxShift + Margin + Resampler.Taps + Block;
            var aligned = new float[Block];
            var mic = new float[Block];
            var reference = new float[Block];
            var cleaned = new float[Block];
            int seg = 0, zone = 0;
            for (var k = 0; k < blocks; k++)
            {
                if (k % 64 == 0) cancel.ThrowIfCancellationRequested();
                var start = k * Block;
                reader.ReadUpTo(start + Block + lookahead);
                reader.Drop(start - lookahead);
                for (var i = 0; i < Block; i++)
                {
                    var n = start + i;
                    while (seg < plan.Segments.Count - 1 && n >= plan.Segments[seg].End) seg++;
                    var position = n - (plan.Segments[seg].LagAt(n) - Margin);
                    aligned[i] = resampler.Read(reader.Reference, position) * plan.Gain;
                    mic[i] = reader.Mic[n];
                    reference[i] = reader.Reference[n];
                }
                var adapt = !frozen.Any(f => start >= f.Start && start < f.End);
                filter.Step(aligned, mic, adapt, cleaned);
                if (sink == null) continue;
                // 處理區間外照原本的麥克風，邊界交叉淡入淡出（區間照時間排、不重疊：只看還沒過去的前兩個）
                for (var i = 0; i < Block; i++)
                {
                    var n = start + i;
                    while (zone < plan.Zones.Count && n >= plan.Zones[zone].End + FadeSamples) zone++;
                    var w = ZoneWeight(n, plan.Zones, zone, Math.Min(plan.Zones.Count, zone + 2));
                    cleaned[i] = w * cleaned[i] + (1 - w) * mic[i];
                }
                sink(k, mic, cleaned, reference);
            }
        }

        public static float ZoneWeight(int n, List<(int Start, int End)> zones, int from, int to)
        {
            var best = 0f;
            for (var z = from; z < to; z++)
            {
                var zone = zones[z];
                if (n < zone.Start - FadeSamples || n >= zone.End + FadeSamples) continue;
                var into = Math.Min(n - zone.Start, zone.End - 1 - n);
                best = Math.Max(best, Math.Min(1, Math.Max(0, (float)(into + FadeSamples) / (2 * FadeSamples))));
            }
            return best;
        }
    }

    // ---- 元件 ----

    /// <summary>小數位置讀取：Kaiser 窗的 sinc（32 點、512 個相位查表）</summary>
    sealed class Resampler
    {
        public const int Taps = 32;
        const int Phases = 512;
        readonly float[] table = new float[(Phases + 1) * Taps];

        public Resampler()
        {
            const int half = Taps / 2;
            const double beta = 8.0, cutoff = 0.95;
            static double Bessel0(double x)
            {
                double sum = 1, term = 1;
                for (var k = 1; k < 30; k++)
                {
                    term *= x / (2.0 * k) * (x / (2.0 * k));
                    sum += term;
                }
                return sum;
            }
            for (var p = 0; p <= Phases; p++)
            {
                var frac = (double)p / Phases;
                for (var j = 0; j < Taps; j++)
                {
                    var u = j - half + 1 - frac;
                    var sinc = u == 0 ? 1 : Math.Sin(Math.PI * u * cutoff) / (Math.PI * u * cutoff);
                    var r = u / half;
                    var window = r * r < 1 ? Bessel0(beta * Math.Sqrt(1 - r * r)) / Bessel0(beta) : 0;
                    table[p * Taps + j] = (float)(cutoff * sinc * window);
                }
            }
        }

        public float Read(Window x, double position)
        {
            var b = (int)Math.Floor(position);
            var phase = (int)Math.Round((position - b) * Phases, MidpointRounding.AwayFromZero);
            return x.Dot(table, phase * Taps, b - Taps / 2 + 1, Taps);
        }
    }

    /// <summary>
    /// 分塊頻域 Kalman 濾波器（每塊 256 點、16 塊 = 256 ms 的回音路徑；FFT 512 點）。
    /// 觀測雜訊用誤差的功率估計：本地的人在說話時誤差變大，自動學得慢，不會被本地人聲帶歪
    /// </summary>
    sealed class KalmanFilter
    {
        public const int Block = 256;
        const int Partitions = 16;
        const float A2 = 0.9995f * 0.9995f;
        const float InitialP = 0.1f;
        const int Bins = Block + 1;
        readonly RealFft fft = new(9);
        int round;
        // 分區 × 頻帶：回音路徑 H、狀態的變異數 P、參考訊號最近 16 塊的頻譜 X（第 0 塊是最新的）
        readonly float[] hr = new float[Partitions * Bins], hi = new float[Partitions * Bins], p = new float[Partitions * Bins];
        readonly float[] xr = new float[Partitions * Bins], xi = new float[Partitions * Bins];
        // 頻帶：誤差功率、分母、暫存
        readonly float[] psi = new float[Bins], total = new float[Bins], yr = new float[Bins], yi = new float[Bins], er = new float[Bins], ei = new float[Bins];
        readonly float[] previous = new float[Block], frame = new float[2 * Block], time = new float[2 * Block];

        public KalmanFilter() => Array.Fill(p, InitialP);

        /// <summary>預習完從頭再來：回音路徑留著，參考訊號的歷史清掉</summary>
        public void ResetHistory()
        {
            Array.Clear(xr);
            Array.Clear(xi);
            Array.Clear(previous);
        }

        public void Step(float[] reference, float[] mic, bool adapt, float[] error)
        {
            // 參考訊號：前一塊＋這一塊做 FFT，放進歷史的最前面
            Array.Copy(xr, 0, xr, Bins, (Partitions - 1) * Bins);
            Array.Copy(xi, 0, xi, Bins, (Partitions - 1) * Bins);
            Array.Copy(previous, frame, Block);
            Array.Copy(reference, 0, frame, Block, Block);
            Array.Copy(reference, previous, Block);
            fft.Forward(frame, xr.AsSpan(0, Bins), xi.AsSpan(0, Bins));
            // 估計的回音 Y = Σ H·X，取後半塊
            Array.Clear(yr);
            Array.Clear(yi);
            for (var q = 0; q < Partitions; q++)
            {
                var o = q * Bins;
                for (var k = 0; k < Bins; k++)
                {
                    yr[k] += hr[o + k] * xr[o + k] - hi[o + k] * xi[o + k];
                    yi[k] += hr[o + k] * xi[o + k] + hi[o + k] * xr[o + k];
                }
            }
            fft.Inverse(yr, yi, time);
            for (var i = 0; i < Block; i++) error[i] = mic[i] - time[Block + i];
            if (!adapt) return;
            // 誤差的頻譜（前半補 0）
            Array.Clear(frame, 0, Block);
            Array.Copy(error, 0, frame, Block, Block);
            fft.Forward(frame, er, ei);
            // Kalman 增益：μ = P / (Σ|X|²P + 誤差功率)
            for (var k = 0; k < Bins; k++)
            {
                psi[k] = 0.5f * psi[k] + 0.5f * (er[k] * er[k] + ei[k] * ei[k]);
                total[k] = psi[k];
            }
            for (var q = 0; q < Partitions; q++)
            {
                var o = q * Bins;
                for (var k = 0; k < Bins; k++) total[k] += (xr[o + k] * xr[o + k] + xi[o + k] * xi[o + k]) * p[o + k];
            }
            for (var q = 0; q < Partitions; q++)
            {
                var o = q * Bins;
                for (var k = 0; k < Bins; k++)
                {
                    var x2 = xr[o + k] * xr[o + k] + xi[o + k] * xi[o + k];
                    var mu = p[o + k] / (total[k] + 1e-10f);
                    // H += μ · conj(X) · E
                    hr[o + k] += mu * (xr[o + k] * er[k] + xi[o + k] * ei[k]);
                    hi[o + k] += mu * (xr[o + k] * ei[k] - xi[o + k] * er[k]);
                    var h2 = hr[o + k] * hr[o + k] + hi[o + k] * hi[o + k];
                    p[o + k] = A2 * (1 - 0.5f * mu * x2) * p[o + k] + (1 - A2) * h2;
                }
            }
            // 每塊輪流把一個分區的時域後半清掉（線性卷積的限制）
            var r0 = round * Bins;
            fft.Inverse(hr.AsSpan(r0, Bins), hi.AsSpan(r0, Bins), time);
            Array.Clear(time, Block, Block);
            fft.Forward(time, hr.AsSpan(r0, Bins), hi.AsSpan(r0, Bins));
            round = (round + 1) % Partitions;
        }
    }

    /// <summary>
    /// 每個頻帶不讓輸出比原本的麥克風大聲（STFT 512 點、跳 256 點、開根號的 Hann 窗）：濾波器沒學好、
    /// 回音路徑突然變了（換成耳機）時，不會把電腦聲音反過來加進麥克風
    /// </summary>
    sealed class Clamp
    {
        const int N = 512, HopSize = 256;
        readonly RealFft fft = new(9);
        readonly float[] window = new float[N];
        readonly float[] micFrame = new float[N], outFrame = new float[N], accumulator = new float[N], time = new float[N];
        readonly float[] dr = new float[HopSize + 1], di = new float[HopSize + 1], er = new float[HopSize + 1], ei = new float[HopSize + 1];

        public Clamp()
        {
            for (var i = 0; i < N; i++) window[i] = (float)Math.Sin(Math.PI * i / N);   // sqrt(periodic hann)
        }

        /// <summary>收一塊（256 點），吐出前一塊處理好的</summary>
        public void Process(float[] mic, float[] echoFree, float[] output)
        {
            Array.Copy(micFrame, HopSize, micFrame, 0, HopSize);
            Array.Copy(outFrame, HopSize, outFrame, 0, HopSize);
            Array.Copy(mic, 0, micFrame, HopSize, HopSize);
            Array.Copy(echoFree, 0, outFrame, HopSize, HopSize);
            for (var i = 0; i < N; i++) time[i] = micFrame[i] * window[i];
            fft.Forward(time, dr, di);
            for (var i = 0; i < N; i++) time[i] = outFrame[i] * window[i];
            fft.Forward(time, er, ei);
            for (var k = 0; k <= HopSize; k++)
            {
                var d = MathF.Sqrt(dr[k] * dr[k] + di[k] * di[k]);
                var e = MathF.Sqrt(er[k] * er[k] + ei[k] * ei[k]);
                if (e > d)
                {
                    var ratio = d / (e + 1e-12f);
                    er[k] *= ratio;
                    ei[k] *= ratio;
                }
            }
            fft.Inverse(er, ei, time);
            for (var i = 0; i < N; i++) accumulator[i] += time[i] * window[i];
            Array.Copy(accumulator, output, HopSize);
            Array.Copy(accumulator, HopSize, accumulator, 0, HopSize);
            Array.Clear(accumulator, HopSize, HopSize);
        }
    }
}

/// <summary>實數 FFT（radix-2，用一半長度的複數 FFT 算），頻譜的格式、大小跟 numpy 的 rfft／irfft 一樣：n/2+1 個複數，正轉不縮放，反轉除以 n</summary>
public sealed class RealFft
{
    readonly int n, half;
    readonly float[] cos, sin;          // 複數 FFT（長度 half）的旋轉因子
    readonly float[] splitCos, splitSin; // 拆成實數頻譜用的 e^{-2πik/n}
    readonly int[] reverse;
    readonly float[] zr, zi;

    public RealFft(int log2n)
    {
        n = 1 << log2n;
        half = n / 2;
        cos = new float[half / 2 + 1];
        sin = new float[half / 2 + 1];
        for (var k = 0; k < cos.Length; k++)
        {
            cos[k] = (float)Math.Cos(2 * Math.PI * k / half);
            sin[k] = (float)Math.Sin(2 * Math.PI * k / half);
        }
        splitCos = new float[half + 1];
        splitSin = new float[half + 1];
        for (var k = 0; k <= half; k++)
        {
            splitCos[k] = (float)Math.Cos(2 * Math.PI * k / n);
            splitSin[k] = (float)Math.Sin(2 * Math.PI * k / n);
        }
        reverse = new int[half];
        var bits = log2n - 1;
        for (var i = 0; i < half; i++)
        {
            var r = 0;
            for (var b = 0; b < bits; b++) r |= ((i >> b) & 1) << (bits - 1 - b);
            reverse[i] = r;
        }
        zr = new float[half];
        zi = new float[half];
    }

    /// <summary>x（n 個實數）→ 頻譜（n/2+1 個）</summary>
    public void Forward(ReadOnlySpan<float> x, Span<float> re, Span<float> im)
    {
        for (var k = 0; k < half; k++)
        {
            zr[reverse[k]] = x[2 * k];
            zi[reverse[k]] = x[2 * k + 1];
        }
        Transform(false);
        // X[k] = E[k] + W^k O[k]；E = (Z[k] + conj(Z[half-k])) / 2，O = (Z[k] - conj(Z[half-k])) / 2i
        for (var k = 0; k <= half; k++)
        {
            var a = k % half;
            var b = (half - k) % half;
            float er = 0.5f * (zr[a] + zr[b]), ei = 0.5f * (zi[a] - zi[b]);
            float or = 0.5f * (zi[a] + zi[b]), oi = -0.5f * (zr[a] - zr[b]);
            float c = splitCos[k], s = -splitSin[k];
            re[k] = er + c * or - s * oi;
            im[k] = ei + c * oi + s * or;
        }
    }

    /// <summary>頻譜（n/2+1 個）→ x（n 個實數）</summary>
    public void Inverse(ReadOnlySpan<float> re, ReadOnlySpan<float> im, Span<float> x)
    {
        // E = (X[k] + conj(X[half-k])) / 2，O = (X[k] - conj(X[half-k])) / 2 × W^-k，Z = E + iO
        for (var k = 0; k < half; k++)
        {
            var b = half - k;
            float er = 0.5f * (re[k] + re[b]), ei = 0.5f * (im[k] - im[b]);
            float dr = 0.5f * (re[k] - re[b]), di = 0.5f * (im[k] + im[b]);
            float c = splitCos[k], s = splitSin[k];
            var or = dr * c - di * s;
            var oi = dr * s + di * c;
            zr[reverse[k]] = er - oi;
            zi[reverse[k]] = ei + or;
        }
        Transform(true);
        var scale = 1f / half;
        for (var k = 0; k < half; k++)
        {
            x[2 * k] = zr[k] * scale;
            x[2 * k + 1] = zi[k] * scale;
        }
    }

    /// <summary>長度 half 的複數 FFT（輸入已經照位元反轉排好）；inverse 不縮放</summary>
    void Transform(bool inverse)
    {
        var sign = inverse ? 1f : -1f;
        for (var size = 2; size <= half; size <<= 1)
        {
            var step = half / size;
            var h = size / 2;
            for (var start = 0; start < half; start += size)
            {
                for (var j = 0; j < h; j++)
                {
                    // t < half/2，旋轉因子 e^{∓2πit/half}
                    var t = j * step;
                    var wr = cos[t];
                    var wi = sign * sin[t];
                    var a = start + j;
                    var b = a + h;
                    var tr = zr[b] * wr - zi[b] * wi;
                    var ti = zr[b] * wi + zi[b] * wr;
                    zr[b] = zr[a] - tr;
                    zi[b] = zi[a] - ti;
                    zr[a] += tr;
                    zi[a] += ti;
                }
            }
        }
    }
}
