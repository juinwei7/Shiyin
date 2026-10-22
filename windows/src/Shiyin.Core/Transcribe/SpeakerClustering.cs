namespace Shiyin.Transcribe;

/// <summary>
/// 說話者分群（design-windows §9、D58）：sherpa-onnx 先分得很細（只用它切出的段落），每段切成約 3 秒的小段各算聲紋，
/// 再在這裡重新分群。sherpa-onnx 的門檻分群在中文會議（AISHELL-4）會把人併在一起，因為整場錄音的聲紋都很像（同一個房間、麥克風）：
/// 先減掉整場的平均聲紋（centering）再比 cosine，同一人、不同人才分得開。
/// 1. 凝聚式分群：每次合併最像的兩群（群中心是依長度加權的和），最像的也低於門檻就停；有「至少 N 人」時合併到剩 N 群也停
/// 2. 太小的群（說話總長不到 MinClusterSeconds）的小段各自併到最像的大群，但至少留 N 群
/// 3. 看「不減平均」的群中心：只有一個人說話時，減掉平均後剩下的是雜訊，會被拆成好幾群。
///    最像的兩群 ≥ SameSpeaker 就合併；剩下的每一對都 ≥ SingleSpeaker，就是只有一個人
/// 4. 每段取它小段裡長度最多的群
/// </summary>
public static class SpeakerClustering
{
    public sealed record Piece(int Turn, double Start, double End, float[] Embedding)
    {
        public double Seconds => Math.Max(End - Start, 0);
    }

    public sealed record Options
    {
        /// <summary>減掉平均之後的 cosine 相似度門檻；AMI、AISHELL-4 在 0.2～0.35 都一樣好（T-W4）</summary>
        public double Threshold { get; init; } = 0.25;
        /// <summary>說話總長不到這個秒數的群併掉；另外不超過總長的 5%（短的會議不會把話少的人併掉）</summary>
        public double MinClusterSeconds { get; init; } = 30;
        /// <summary>至少分出幾個人（M§23.2），null 自動</summary>
        public int? MinSpeakers { get; init; }
        public bool Center { get; init; } = true;
        /// <summary>
        /// 不減平均的群中心 cosine 在這以上，這兩群就是同一個人。分群後不同的人最高 0.58（AMI 陣列麥克風）、
        /// 同一個人被拆開的群最低 0.63（design-windows §9，2026-10-21）
        /// </summary>
        public double SameSpeaker { get; init; } = 0.7;
        /// <summary>剩下的群每一對都在這以上：只有一個人說話，全部合併</summary>
        public double SingleSpeaker { get; init; } = 0.6;
    }

    /// <summary>
    /// turns：sherpa-onnx 切出的段落（說話者是 sherpa 的編號，只用來處理沒有小段的短段落）；pieces：每段的小段和聲紋。
    /// 回傳每段的新編號（0 起算，同一個編號是同一人）
    /// </summary>
    public static int[] Cluster(IReadOnlyList<SpeakerRules.Turn> turns, IReadOnlyList<Piece> pieces, Options options)
    {
        var labels = new int[turns.Count];
        if (turns.Count == 0) return labels;
        if (pieces.Count == 0)
        {
            // 每段都太短算不了聲紋：照 sherpa 的分法
            var map = new Dictionary<int, int>();
            for (var i = 0; i < turns.Count; i++)
                labels[i] = map.TryGetValue(turns[i].Speaker, out var l) ? l : map[turns[i].Speaker] = map.Count;
            return labels;
        }
        var vectors = Prepare(pieces, options.Center);
        var weights = pieces.Select(p => (float)Math.Max(p.Seconds, 0.01)).ToArray();
        var pieceLabels = Agglomerate(vectors, weights, options.Threshold, options.MinSpeakers ?? 1);
        var total = weights.Sum();
        AbsorbSmall(pieceLabels, vectors, weights, Math.Min(options.MinClusterSeconds, total * 0.05), options.MinSpeakers ?? 1);
        if (options.Center) MergeSameSpeaker(pieceLabels, pieces.Select(p => Normalized(p.Embedding)).ToArray(), weights, options, options.MinSpeakers ?? 1);

        // 每段取它小段裡長度最多的群
        Array.Fill(labels, -1);
        var byTurn = new Dictionary<int, Dictionary<int, double>>();
        for (var i = 0; i < pieces.Count; i++)
        {
            var shares = byTurn.TryGetValue(pieces[i].Turn, out var d) ? d : byTurn[pieces[i].Turn] = [];
            shares[pieceLabels[i]] = shares.GetValueOrDefault(pieceLabels[i]) + pieces[i].Seconds;
        }
        foreach (var (turn, shares) in byTurn)
            if (turn >= 0 && turn < labels.Length) labels[turn] = Top(shares);
        // 沒有小段的短段落：sherpa 的同一位最常被分到哪一群；sherpa 那位全部都沒有小段的話，用時間最接近的段落
        var bySherpa = new Dictionary<int, Dictionary<int, double>>();
        for (var i = 0; i < turns.Count; i++)
        {
            if (labels[i] < 0) continue;
            var shares = bySherpa.TryGetValue(turns[i].Speaker, out var d) ? d : bySherpa[turns[i].Speaker] = [];
            shares[labels[i]] = shares.GetValueOrDefault(labels[i]) + Math.Max(turns[i].End - turns[i].Start, 0);
        }
        var resolved = labels.ToArray();
        for (var i = 0; i < turns.Count; i++)
        {
            if (labels[i] >= 0) continue;
            if (bySherpa.TryGetValue(turns[i].Speaker, out var shares)) { resolved[i] = Top(shares); continue; }
            var middle = (turns[i].Start + turns[i].End) / 2;
            var nearest = Enumerable.Range(0, turns.Count).Where(j => labels[j] >= 0)
                .OrderBy(j => Math.Abs((turns[j].Start + turns[j].End) / 2 - middle)).First();
            resolved[i] = labels[nearest];
        }
        return Renumber(resolved);
    }

    static int Top(Dictionary<int, double> shares) => shares.OrderByDescending(p => p.Value).ThenBy(p => p.Key).First().Key;

    static int[] Renumber(int[] labels)
    {
        var map = new Dictionary<int, int>();
        return labels.Select(l => map.TryGetValue(l, out var n) ? n : map[l] = map.Count).ToArray();
    }

    public static float[] Normalized(float[] v)
    {
        var norm = 0f;
        foreach (var x in v) norm += x * x;
        norm = MathF.Sqrt(norm);
        if (norm <= 0) return v.ToArray();
        var r = new float[v.Length];
        for (var i = 0; i < v.Length; i++) r[i] = v[i] / norm;
        return r;
    }

    static float Dot(float[] a, float[] b)
    {
        var s = 0f;
        for (var i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }

    /// <summary>正規化；center 時減掉整場的平均再正規化</summary>
    static float[][] Prepare(IReadOnlyList<Piece> pieces, bool center)
    {
        var vectors = pieces.Select(p => Normalized(p.Embedding)).ToArray();
        if (!center || vectors.Length < 2) return vectors;
        var mean = new float[vectors[0].Length];
        foreach (var v in vectors) for (var i = 0; i < mean.Length; i++) mean[i] += v[i];
        for (var i = 0; i < mean.Length; i++) mean[i] /= vectors.Length;
        return vectors.Select(v => Normalized(v.Select((x, i) => x - mean[i]).ToArray())).ToArray();
    }

    /// <summary>
    /// 凝聚式分群，群中心是依長度加權的和。每一群記著最像的另一群，合併後只重算受影響的（n 個小段約 O(n²·維度)）
    /// </summary>
    static int[] Agglomerate(float[][] vectors, float[] weights, double threshold, int minClusters)
    {
        var n = vectors.Length;
        var dim = vectors[0].Length;
        var sums = new float[n][];
        var centers = new float[n][];
        for (var i = 0; i < n; i++)
        {
            sums[i] = vectors[i].Select(x => x * weights[i]).ToArray();
            centers[i] = vectors[i];
        }
        var parent = Enumerable.Range(0, n).ToArray();
        var active = new bool[n];
        Array.Fill(active, true);
        var best = new int[n];
        var bestSim = new float[n];
        void FindBest(int i)
        {
            best[i] = -1;
            bestSim[i] = float.NegativeInfinity;
            for (var j = 0; j < n; j++)
            {
                if (j == i || !active[j]) continue;
                var s = Dot(centers[i], centers[j]);
                if (s > bestSim[i]) { bestSim[i] = s; best[i] = j; }
            }
        }
        for (var i = 0; i < n; i++) FindBest(i);
        var count = n;
        while (count > minClusters)
        {
            var a = -1;
            for (var i = 0; i < n; i++)
                if (active[i] && best[i] >= 0 && (a < 0 || bestSim[i] > bestSim[a])) a = i;
            if (a < 0 || bestSim[a] < threshold) break;
            var b = best[a];
            for (var k = 0; k < dim; k++) sums[a][k] += sums[b][k];
            centers[a] = Normalized(sums[a]);
            active[b] = false;
            parent[b] = a;
            count--;
            for (var i = 0; i < n; i++)
            {
                if (!active[i] || i == a) continue;
                if (best[i] == a || best[i] == b) FindBest(i);
                else
                {
                    var s = Dot(centers[i], centers[a]);
                    if (s > bestSim[i]) { bestSim[i] = s; best[i] = a; }
                }
            }
            FindBest(a);
        }
        int Root(int i)
        {
            while (parent[i] != i) i = parent[i] = parent[parent[i]];
            return i;
        }
        return Enumerable.Range(0, n).Select(Root).ToArray();
    }

    /// <summary>
    /// 不減平均的群中心（依長度加權）：最像的兩群在 SameSpeaker 以上就合併，重複到沒有為止；
    /// 剩下的每一對都在 SingleSpeaker 以上就全部合併。至少留 minClusters 群
    /// </summary>
    static void MergeSameSpeaker(int[] labels, float[][] raw, float[] weights, Options options, int minClusters)
    {
        var dim = raw[0].Length;
        while (true)
        {
            var sums = new Dictionary<int, float[]>();
            for (var i = 0; i < labels.Length; i++)
            {
                var s = sums.TryGetValue(labels[i], out var x) ? x : sums[labels[i]] = new float[dim];
                for (var k = 0; k < dim; k++) s[k] += raw[i][k] * weights[i];
            }
            if (sums.Count <= Math.Max(minClusters, 1)) return;
            var clusters = sums.Keys.Order().ToArray();
            var centers = clusters.Select(c => Normalized(sums[c])).ToArray();
            var (a, b, best) = (-1, -1, double.NegativeInfinity);
            for (var i = 0; i < clusters.Length; i++)
                for (var j = i + 1; j < clusters.Length; j++)
                {
                    var sim = Dot(centers[i], centers[j]);
                    if (sim > best) (a, b, best) = (i, j, sim);
                }
            if (best < options.SameSpeaker)
            {
                if (minClusters > 1) return;
                var lowest = double.PositiveInfinity;
                for (var i = 0; i < clusters.Length; i++)
                    for (var j = i + 1; j < clusters.Length; j++) lowest = Math.Min(lowest, Dot(centers[i], centers[j]));
                if (lowest >= options.SingleSpeaker) Array.Fill(labels, clusters[0]);
                return;
            }
            for (var i = 0; i < labels.Length; i++) if (labels[i] == clusters[b]) labels[i] = clusters[a];
        }
    }

    /// <summary>說話總長不到 minSeconds 的群併到最像的大群，至少留 minClusters 群（留最大的）</summary>
    static void AbsorbSmall(int[] labels, float[][] vectors, float[] weights, double minSeconds, int minClusters)
    {
        var dim = vectors[0].Length;
        var size = new Dictionary<int, double>();
        var sums = new Dictionary<int, float[]>();
        for (var i = 0; i < labels.Length; i++)
        {
            size[labels[i]] = size.GetValueOrDefault(labels[i]) + weights[i];
            var s = sums.TryGetValue(labels[i], out var x) ? x : sums[labels[i]] = new float[dim];
            for (var k = 0; k < dim; k++) s[k] += vectors[i][k] * weights[i];
        }
        var ordered = size.OrderByDescending(p => p.Value).ThenBy(p => p.Key).Select(p => p.Key).ToList();
        var big = ordered.Where((c, rank) => size[c] >= minSeconds || rank < minClusters).ToHashSet();
        if (big.Count == 0) big.Add(ordered[0]);
        if (big.Count == size.Count) return;
        // 小群的每個小段各自找最像的大群（同一個小群可能是好幾個人的零碎片段）
        var centers = big.OrderBy(c => c).Select(c => (Cluster: c, Center: Normalized(sums[c]))).ToList();
        for (var i = 0; i < labels.Length; i++)
        {
            if (big.Contains(labels[i])) continue;
            var v = vectors[i];
            labels[i] = centers.OrderByDescending(c => Dot(c.Center, v)).First().Cluster;
        }
    }
}
