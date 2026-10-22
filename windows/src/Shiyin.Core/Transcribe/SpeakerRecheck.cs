namespace Shiyin.Transcribe;

/// <summary>
/// 短句的說話者再確認（R34、M§6.3.2、design-windows §9、D93）：說話者辨識有時把很短的一句併進旁邊那個人（換人點放早或放晚了一兩秒）。
/// 分好說話者之後，0.5～4 秒的句子各自算一次聲紋，跟每位說話者的聲紋（說話者辨識算好的，§9 第 4 點）比；
/// 明顯比較像另一位（相似度高出 0.25 以上）就改成那一位。Mac 用另一個帶前後文的聲紋模型、跟長句的平均比，差距 0.15；
/// Windows 的聲紋模型只聽得到這一句，分數比較散，門檻另外定
/// </summary>
public static class SpeakerRecheck
{
    public static double Margin { get; set; } = 0.25;
    /// <summary>太短的聲音不夠判斷（聲紋模型不到 0.5 秒也不算）</summary>
    public const double MinSeconds = 0.5;
    /// <summary>長句說話者辨識本來就準，只查短的</summary>
    public const double MaxSeconds = 4;

    public sealed record Result(List<int?> Speakers, int Checked, int Changed);

    /// <summary>
    /// voices：每位說話者（逐字稿上的編號）的聲紋，長度 1。embed：一次算好要查的每一句的聲紋（太短的是 null）。
    /// locked：使用者改過說話者的句子，不動
    /// </summary>
    public static Result Refine(IReadOnlyList<TranscribedSegment> segments, IReadOnlyList<int?> speakers, IReadOnlyDictionary<int, float[]> voices,
                                Func<IReadOnlyList<(double Start, double End)>, IReadOnlyList<float[]?>> embed, IReadOnlySet<int>? locked = null)
    {
        var refined = speakers.ToList();
        // 只有一位說話者有聲紋：沒得比
        if (voices.Count < 2) return new Result(refined, 0, 0);
        var check = Enumerable.Range(0, segments.Count).Where(i =>
        {
            var seconds = segments[i].End - segments[i].Start;
            return speakers[i] is int s && voices.ContainsKey(s) && locked?.Contains(i) != true && seconds >= MinSeconds && seconds <= MaxSeconds;
        }).ToList();
        if (check.Count == 0) return new Result(refined, 0, 0);
        var vectors = embed(check.Select(i => (segments[i].Start, segments[i].End)).ToList());
        var changed = 0;
        for (var k = 0; k < check.Count; k++)
        {
            if (vectors[k] is not { } vector) continue;
            var i = check[k];
            var current = speakers[i]!.Value;
            var own = Dot(vector, voices[current]);
            // 一樣像時取編號小的（字典的順序不固定）
            var (best, score) = voices.Where(p => p.Key != current).Select(p => (p.Key, Score: Dot(vector, p.Value)))
                .OrderByDescending(p => p.Score).ThenBy(p => p.Key).First();
            if (score - own < Margin) continue;
            refined[i] = best;
            changed++;
        }
        return new Result(refined, check.Count, changed);
    }

    static double Dot(float[] a, float[] b)
    {
        var sum = 0.0;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++) sum += a[i] * b[i];
        return sum;
    }
}
