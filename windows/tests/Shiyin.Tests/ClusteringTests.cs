using Shiyin.Transcribe;
using Xunit;

namespace Shiyin.Tests;

/// <summary>說話者分群（design-windows §9、D58）：用合成的聲紋測規則；真的錄音的結果用 Shiyin.Eval 在 AMI、AISHELL-4 上看</summary>
public sealed class ClusteringTests
{
    /// <summary>
    /// 每位說話者一個方向，加上整場共同的成分（同一個房間、麥克風）和雜訊。共同成分的比例照真實的錄音：
    /// 不同的人不減平均的相似度約 0.3（AMI、AISHELL-4 最高 0.47），減掉平均才分得開
    /// </summary>
    static (List<SpeakerRules.Turn> Turns, List<SpeakerClustering.Piece> Pieces, int[] Truth) Synthetic(int[] speakerOfTurn, double[] seconds, int seed = 1, float noise = 0.15f)
    {
        var random = new Random(seed);
        const int dim = 32;
        float[] Direction(int k) => Enumerable.Range(0, dim).Select(i => (float)(i == k ? 1 : 0)).ToArray();
        var shared = Enumerable.Range(0, dim).Select(_ => (float)random.NextDouble()).ToArray();
        var turns = new List<SpeakerRules.Turn>();
        var pieces = new List<SpeakerClustering.Piece>();
        var t = 0.0;
        for (var i = 0; i < speakerOfTurn.Length; i++)
        {
            var s = speakerOfTurn[i];
            turns.Add(new SpeakerRules.Turn(i % 7, t, t + seconds[i]));   // sherpa 的編號亂給：只用來處理沒有小段的段落
            for (var p = 0.0; p + 0.5 <= seconds[i]; p += 3)
            {
                var d = Direction(s);
                var v = Enumerable.Range(0, dim).Select(k => 0.12f * shared[k] + 0.6f * d[k] + noise * (float)(random.NextDouble() - 0.5)).ToArray();
                pieces.Add(new SpeakerClustering.Piece(i, t + p, t + Math.Min(p + 3, seconds[i]), v));
            }
            t += seconds[i] + 0.5;
        }
        return (turns, pieces, speakerOfTurn);
    }

    static int Groups(int[] labels) => labels.Distinct().Count();

    /// <summary>同一個真實說話者都分到同一群、不同人不同群</summary>
    static void AssertMatches(int[] truth, int[] labels)
    {
        for (var i = 0; i < truth.Length; i++)
            for (var j = 0; j < truth.Length; j++)
                Assert.Equal(truth[i] == truth[j], labels[i] == labels[j]);
    }

    [Fact]
    public void SeparatesSpeakersAfterCentering()
    {
        var order = Enumerable.Range(0, 60).Select(i => i % 3).ToArray();
        var (turns, pieces, truth) = Synthetic(order, order.Select(_ => 6.0).ToArray());
        AssertMatches(truth, SpeakerClustering.Cluster(turns, pieces, new()));
        // 不減掉平均時整場都很像，會全部併成一人
        Assert.Equal(1, Groups(SpeakerClustering.Cluster(turns, pieces, new() { Center = false })));
    }

    /// <summary>只有一個人說話：減掉平均後剩下的是雜訊，會被拆成好幾群，最後要併回一位（2026-10-21 在虛擬機看到合成會議分出 6 人）</summary>
    [Fact]
    public void SingleSpeakerStaysOne()
    {
        var order = Enumerable.Repeat(0, 40).ToArray();
        var (turns, pieces, _) = Synthetic(order, order.Select(_ => 6.0).ToArray(), noise: 0.6f);
        Assert.True(Groups(SpeakerClustering.Cluster(turns, pieces, new() { SameSpeaker = 2, SingleSpeaker = 2 })) > 1);   // 不合併時會被拆開
        Assert.Equal(1, Groups(SpeakerClustering.Cluster(turns, pieces, new())));
    }

    [Fact]
    public void AbsorbsTinyClustersButKeepsMinimum()
    {
        // 第 4 位只說了 2 秒：併到最像的人；至少 4 人時留著
        var order = Enumerable.Range(0, 40).Select(i => i % 3).Append(3).ToArray();
        var seconds = order.Select(s => s == 3 ? 2.0 : 9.0).ToArray();
        var (turns, pieces, _) = Synthetic(order, seconds);
        Assert.Equal(3, Groups(SpeakerClustering.Cluster(turns, pieces, new())));
        Assert.Equal(4, Groups(SpeakerClustering.Cluster(turns, pieces, new() { MinSpeakers = 4 })));
        // 「至少 2 人」不會讓 3 個人變少
        Assert.Equal(3, Groups(SpeakerClustering.Cluster(turns, pieces, new() { MinSpeakers = 2 })));
    }

    [Fact]
    public void TurnsWithoutPiecesFollowTheirNeighbours()
    {
        var order = Enumerable.Range(0, 30).Select(i => i % 2).ToArray();
        var seconds = order.Select((_, i) => i == 5 ? 0.3 : 6.0).ToArray();   // 第 6 段太短，沒有小段
        var (turns, pieces, _) = Synthetic(order, seconds);
        Assert.DoesNotContain(pieces, p => p.Turn == 5);
        var labels = SpeakerClustering.Cluster(turns, pieces, new());
        Assert.Equal(30, labels.Length);
        Assert.Equal(2, Groups(labels));
        // 沒有任何聲紋時照 sherpa 的分法
        var none = SpeakerClustering.Cluster(turns, [], new());
        Assert.Equal(turns.Select(t => t.Speaker).Distinct().Count(), Groups(none));
        Assert.Empty(SpeakerClustering.Cluster([], [], new()));
    }
}
