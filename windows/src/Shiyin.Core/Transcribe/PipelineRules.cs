using System.IO.Compression;
using System.Text;
using Shiyin.Model;
using Shiyin.Text;

namespace Shiyin.Transcribe;

public sealed record TranscribedSegment(double Start, double End, string Text, bool LowConfidence);

/// <summary>whisper 的提示詞和分段（M§16.4、§18.2、§21.16、D34），規則跟 Mac 的 WhisperEngine 相同</summary>
public static class WhisperRules
{
    public const double SampleRate = 16000;

    /// <summary>Whisper 在靜音段常見的幻覺字串，出現在短句時直接丟掉</summary>
    public static readonly string[] HallucinationPhrases =
    [
        "字幕由Amara.org社區提供", "字幕由Amara.org社群提供", "字幕由Amara.org社区提供",  // l10n-skip
        "請不吝點贊", "請不吝點讚", "请不吝点赞", "訂閱轉發打賞", "订阅转发打赏",  // l10n-skip
        "明鏡與點點欄目", "明镜与点点栏目", "優優獨播劇場", "优优独播剧场",  // l10n-skip
        "Subtitles by the Amara.org community",
        "ご視聴ありがとうございました", "ご視聴いただきありがとうございました", "チャンネル登録",  // l10n-skip
    ];

    /// <summary>基本提示詞＋詞彙表：中文、日文 150 字、英文 400 個字元以內</summary>
    public static string Prompt(IReadOnlyList<string> glossary, MeetingLanguage language)
    {
        var baseText = language.Text("以下是繁體中文的工作會議錄音，會夾雜英文專有名詞。", "The following is a recording of a work meeting.", "以下は仕事の会議の録音です。");
        var limit = language == MeetingLanguage.En ? 400 : 150;
        string Build(List<string> terms) => terms.Count == 0 ? baseText : baseText + language.Text(
            "詞彙：" + string.Join("、", terms) + "。", " Glossary: " + string.Join(", ", terms) + ".", "用語：" + string.Join("、", terms) + "。");  // l10n-skip
        var picked = new List<string>();
        foreach (var term in glossary)
        {
            if (SwiftText.Count(Build([.. picked, term])) > limit) break;
            picked.Add(term);
        }
        return Build(picked);
    }

    /// <summary>下一段的提示詞：基本提示詞加上前一段最後的文字（中文、日文 120 字、英文 400 個字元以內）</summary>
    public static string ChunkPrompt(string basePrompt, IReadOnlyList<TranscribedSegment> previous, string language) =>
        previous.Count == 0 ? basePrompt : basePrompt + (language == "en" ? " " : "") + Tail(previous, language);

    public static string Tail(IReadOnlyList<TranscribedSegment> segments, string language)
    {
        var limit = language == "en" ? 400 : 120;
        var text = "";
        for (var i = segments.Count - 1; i >= 0; i--)
        {
            text = segments[i].Text + (text.Length == 0 ? "" : language == "en" ? " " : "") + text;
            if (SwiftText.Count(text) >= limit) break;
        }
        return SwiftText.Suffix(text, limit);
    }

    /// <summary>長錄音分段：每段約 chunkSeconds 秒，分段點選在預定位置前後 10 秒內最安靜的 0.5 秒。
    /// 最後一段不會短於 2 分鐘；chunkSeconds ≤ 0 不分段。回傳 [起點, 終點) 的取樣位置</summary>
    public static List<(int Start, int End)> Chunks(ReadOnlySpan<float> samples, double chunkSeconds)
    {
        var rate = (int)SampleRate;
        var chunk = (int)(chunkSeconds * rate);
        if (chunk <= 0) return [(0, samples.Length)];
        var result = new List<(int, int)>();
        var start = 0;
        while (samples.Length - start > chunk + 120 * rate)
        {
            var cut = QuietestCut(samples, start, start + chunk);
            result.Add((start, cut));
            start = cut;
        }
        result.Add((start, samples.Length));
        return result;
    }

    /// <summary>target 前後 10 秒內最安靜的 0.5 秒的中間：每 0.05 秒一個窗，能量（平方和）最小的</summary>
    public static int QuietestCut(ReadOnlySpan<float> samples, int start, int target)
    {
        var rate = (int)SampleRate;
        int search = 10 * rate, window = rate / 2, hop = rate / 20;
        int from = Math.Max(start + window, target - search), to = Math.Min(samples.Length - window, target + search);
        var squares = new double[to - from + 1];
        for (var i = from; i < to; i++) squares[i - from + 1] = squares[i - from] + (double)(samples[i] * samples[i]);
        int best = target;
        var bestEnergy = double.PositiveInfinity;
        for (var position = from; position + window <= to; position += hop)
        {
            var energy = squares[position + window - from] - squares[position - from];
            if (energy < bestEnergy) { bestEnergy = energy; best = position + window / 2; }
        }
        return best;
    }

    /// <summary>Whisper 用來判斷重複幻覺的壓縮比：原始位元組數 ÷ zlib 壓縮後的位元組數（Mac 用系統的 zlib，大小可能差幾個位元組）</summary>
    public static double CompressionRatio(string text)
    {
        var input = Encoding.UTF8.GetBytes(text);
        if (input.Length <= 16) return 1;
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true)) deflate.Write(input);
        var size = output.Length;
        return size > 0 ? (double)input.Length / size : 1;
    }

    /// <summary>一段 whisper 的結果要不要留下：幾乎確定沒人講話、信心又低是幻覺；短句含常見的幻覺字串也丟掉</summary>
    public static bool Keep(string text, double noSpeech, double avgLogProb) =>
        !(noSpeech > 0.6 && avgLogProb < -1.0) && !(SwiftText.Count(text) < 40 && HallucinationPhrases.Any(p => text.Contains(p, StringComparison.Ordinal)));

    /// <summary>辨識信心低：平均 log 機率低於 -1，或壓縮比大於 2.4</summary>
    public static bool LowConfidence(string text, double avgLogProb) => avgLogProb < -1.0 || CompressionRatio(text) > 2.4;

    /// <summary>接縫：下一段開頭幾秒跟前一段最後一句重複的（提示詞被念出來）去掉</summary>
    public static List<TranscribedSegment> DropSeamRepeats(List<TranscribedSegment> part, TranscribedSegment? last, double seam)
    {
        if (last is null) return part;
        var i = 0;
        while (i < part.Count && part[i].Start < seam + 3 && TextRules.Similar(part[i].Text, last.Text)) i++;
        return part.Skip(i).ToList();
    }
}

/// <summary>文字比對（M§17.3）</summary>
public static class TextRules
{
    /// <summary>兩句幾乎一樣（最長共同子序列 ÷ 較長那句的長度 > 0.8）</summary>
    public static bool Similar(string a, string b)
    {
        var x = Comparable(a);
        var y = Comparable(b);
        if (Math.Min(x.Count, y.Count) < 4) return false;
        return Similarity(x, y) * Math.Min(x.Count, y.Count) / Math.Max(x.Count, y.Count) > 0.8;
    }

    /// <summary>麥克風段落跟電腦聲音的某一段時間重疊一半以上、文字也很像，就是喇叭的回音</summary>
    public static bool IsEcho(TranscribedSegment segment, IEnumerable<TranscribedSegment> system)
    {
        var length = Math.Max(segment.End - segment.Start, 0.01);
        var text = Comparable(segment.Text);
        if (text.Count == 0) return false;
        return system.Any(other =>
        {
            var overlap = Math.Min(segment.End, other.End) - Math.Max(segment.Start, other.Start);
            return overlap / length > 0.5 && Similarity(text, Comparable(other.Text)) > 0.6;
        });
    }

    static List<string> Comparable(string text) =>
        SwiftText.Characters(SwiftText.Lower(text)).Where(c => SwiftText.IsLetter(c) || SwiftText.IsNumber(c)).ToList();

    /// <summary>最長共同子序列 ÷ 較短那段的長度</summary>
    static double Similarity(List<string> a, List<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var prev = new int[b.Count + 1];
        foreach (var x in a)
        {
            var cur = new int[b.Count + 1];
            for (var j = 0; j < b.Count; j++) cur[j + 1] = x == b[j] ? prev[j] + 1 : Math.Max(prev[j + 1], cur[j]);
            prev = cur;
        }
        return (double)prev[b.Count] / Math.Min(a.Count, b.Count);
    }

    /// <summary>方均根低於 -70 dBFS 就當作沒有聲音</summary>
    public static bool HasSound(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0) return false;
        double sum = 0;
        foreach (var s in samples) sum += s * s;
        return Math.Sqrt(sum / samples.Length) > 3e-4;
    }
}

/// <summary>說話者辨識的共用規則（M§6.3、§21.4、§23、§24）</summary>
public static class SpeakerRules
{
    public sealed record Turn(int Speaker, double Start, double End);

    /// <summary>每段逐字稿標上跟它重疊最多的說話者；完全沒重疊就用時間最接近的。依逐字稿裡出現的順序重新編號。
    /// 一樣多時取編號小的（Mac 的字典順序不固定，見 design-windows §9）</summary>
    public static (List<int?> Speakers, Dictionary<int, int> Numbering) AssignNumbering(IReadOnlyList<Turn> turns, IReadOnlyList<TranscribedSegment> segments)
    {
        if (turns.Count == 0) return (segments.Select(_ => (int?)null).ToList(), new());
        var raw = segments.Select(segment =>
        {
            var overlap = new Dictionary<int, double>();
            foreach (var turn in turns.Where(t => t.End > segment.Start && t.Start < segment.End))
                overlap[turn.Speaker] = overlap.GetValueOrDefault(turn.Speaker) + Math.Min(turn.End, segment.End) - Math.Max(turn.Start, segment.Start);
            if (overlap.Count > 0) return (int?)overlap.OrderByDescending(p => p.Value).ThenBy(p => p.Key).First().Key;
            var middle = (segment.Start + segment.End) / 2;
            return turns.OrderBy(t => Math.Abs((t.Start + t.End) / 2 - middle)).First().Speaker;
        }).ToList();
        var numbering = new Dictionary<int, int>();
        var speakers = raw.Select(speaker =>
        {
            if (speaker is not int s) return (int?)null;
            if (numbering.TryGetValue(s, out var n)) return n;
            numbering[s] = numbering.Count + 1;
            return numbering.Count;
        }).ToList();
        return (speakers, numbering);
    }

    /// <summary>一個片段裡一位說話者的聲紋（分不準的判斷用）</summary>
    public sealed record ChunkEmbedding(string SpeakerId, int ChunkIndex, int SpeakerIndex, float[] Embedding);

    /// <summary>分得準不準的自我檢查（M§24.5）</summary>
    public sealed record Quality(double CollisionRate, int MultiSpeakerChunks, double Cohesion, int Clusters, int Chunks)
    {
        /// <summary>超過這個分數就提示「說話者可能分得不夠」。Mac 是 0.4；Windows 的聲紋模型不同：AMI 9 場正常分群 0.21～0.33、
        /// 故意併成一人 0.34～0.48，取 0.37（design-windows §9、D58）</summary>
        public static double HintThreshold { get; set; } = 0.37;

        public double MergeRisk
        {
            get
            {
                if (Chunks < 30) return 0;
                if (Clusters <= 1 && MultiSpeakerChunks >= 10 && CollisionRate >= 0.5) return 1;
                return Math.Max(0, 1 - Cohesion);
            }
        }
    }

    public static Quality Measure(IReadOnlyList<ChunkEmbedding> chunks)
    {
        int multi = 0, collided = 0;
        foreach (var group in chunks.GroupBy(c => c.ChunkIndex))
        {
            var locals = group.Select(c => c.SpeakerIndex).Distinct().Count();
            if (locals < 2) continue;
            multi++;
            if (group.Select(c => c.SpeakerId).Distinct().Count() < locals) collided++;
        }
        static float[] Normalized(float[] v)
        {
            var norm = MathF.Sqrt(v.Aggregate(0f, (s, x) => s + x * x));
            return norm > 0 ? v.Select(x => x / norm).ToArray() : v;
        }
        double total = 0;
        var count = 0;
        var groups = chunks.GroupBy(c => c.SpeakerId).ToList();
        foreach (var g in groups)
        {
            var vectors = g.Select(c => Normalized(c.Embedding)).ToList();
            if (vectors.Count == 0) continue;
            var width = vectors[0].Length;
            var sum = new float[width];
            foreach (var v in vectors) for (var i = 0; i < width; i++) sum[i] += v[i];
            var centroid = Normalized(sum);
            foreach (var v in vectors)
            {
                var dot = 0f;
                for (var i = 0; i < Math.Min(v.Length, centroid.Length); i++) dot += v[i] * centroid[i];
                total += dot;
                count++;
            }
        }
        return new Quality(multi > 0 ? (double)collided / multi : 0, multi, count > 0 ? total / count : 1, groups.Count, chunks.Count);
    }

    /// <summary>舊的說話者 → 新的說話者：舊的那位（沒被使用者改過的）段落分到最多的新說話者</summary>
    public static Dictionary<int, int> OldToNew(IReadOnlyList<Segment> segments, IReadOnlyList<int?> newSpeakers)
    {
        var byOld = new Dictionary<int, Dictionary<int, double>>();
        for (var i = 0; i < Math.Min(segments.Count, newSpeakers.Count); i++)
        {
            var segment = segments[i];
            if (segment.SpeakerManual || newSpeakers[i] is not int n || segment.Speaker is not int o) continue;
            var shares = byOld.TryGetValue(o, out var d) ? d : byOld[o] = new();
            shares[n] = shares.GetValueOrDefault(n) + Math.Max(0, segment.End - segment.Start);
        }
        return byOld.ToDictionary(p => p.Key, p => p.Value.OrderByDescending(x => x.Value).ThenBy(x => x.Key).First().Key);
    }

    public sealed record Inherited(string Name, string Source, long? PersonId);

    /// <summary>名字沿用（M§23.3）：新的說話者 n 的段落（依秒數）有 60% 以上原本是同一位取過名字的 o，
    /// 而且 o 原本的段落分到 n 最多，n 就沿用 o 的名字、名字來源、連到的人</summary>
    public static Dictionary<int, Inherited> InheritNames(IReadOnlyList<SpeakerName> old, IReadOnlyList<Segment> segments, IReadOnlyList<int?> newSpeakers)
    {
        var named = new Dictionary<int, SpeakerName>();
        foreach (var s in old.Where(s => s.NameSource != "default")) named.TryAdd(s.Idx, s);
        var byNew = new Dictionary<int, Dictionary<int, double>>();
        var byOld = new Dictionary<int, Dictionary<int, double>>();
        for (var i = 0; i < Math.Min(segments.Count, newSpeakers.Count); i++)
        {
            if (newSpeakers[i] is not int n || segments[i].Speaker is not int o) continue;
            var seconds = Math.Max(0, segments[i].End - segments[i].Start);
            var a = byNew.TryGetValue(n, out var x) ? x : byNew[n] = new();
            a[o] = a.GetValueOrDefault(o) + seconds;
            var b = byOld.TryGetValue(o, out var y) ? y : byOld[o] = new();
            b[n] = b.GetValueOrDefault(n) + seconds;
        }
        var result = new Dictionary<int, Inherited>();
        foreach (var (n, shares) in byNew)
        {
            var total = shares.Values.Sum();
            if (total <= 0) continue;
            var top = shares.OrderByDescending(p => p.Value).ThenBy(p => p.Key).First();
            if (top.Value / total < 0.6 || !named.TryGetValue(top.Key, out var speaker)) continue;
            if (byOld.TryGetValue(top.Key, out var back) && back.OrderByDescending(p => p.Value).ThenBy(p => p.Key).First().Key == n)
                result[n] = new Inherited(speaker.Name, speaker.NameSource, speaker.PersonId);
        }
        return result;
    }
}

/// <summary>
/// 自動偵測語言（M§18.8、D21）：錄音時收集有聲音的片段，每 0.1 秒一段，比背景噪音大 2.5 倍（至少 -54 dBFS）才算，
/// 湊滿 10 秒就交出去；錄了 45 秒還湊不滿但有 5 秒以上也先交出去。只交一次。運算用 float，跟 Mac 一樣
/// </summary>
public sealed class LanguageCollector
{
    public const double Seconds = 10;
    public const double Threshold = 0.7;

    readonly object gate = new();
    readonly List<float> voiced = [];
    readonly List<float> pending = [];
    int seen;
    float? floor;
    bool finished;
    readonly int target = (int)(Seconds * WhisperRules.SampleRate);
    readonly int window = (int)(WhisperRules.SampleRate / 10);
    readonly Action<float[]> onReady;

    public LanguageCollector(Action<float[]> onReady) => this.onReady = onReady;

    public void Feed(ReadOnlySpan<float> samples)
    {
        float[]? ready = null;
        lock (gate)
        {
            if (finished) return;
            foreach (var s in samples) pending.Add(s);
            var offset = 0;
            while (pending.Count - offset >= window)
            {
                var sum = 0f;
                for (var i = 0; i < window; i++) { var s = pending[offset + i]; sum += s * s; }
                var rms = MathF.Sqrt(sum / window);
                var current = floor ?? rms;
                floor = rms < current ? rms : current + (rms - current) * 0.002f;
                if (rms > Math.Max(0.002f, current * 2.5f)) voiced.AddRange(pending.GetRange(offset, window));
                offset += window;
                seen += window;
            }
            pending.RemoveRange(0, offset);
            var enough = voiced.Count >= target || (seen >= (int)(45 * WhisperRules.SampleRate) && voiced.Count >= target / 2);
            if (enough)
            {
                finished = true;
                ready = voiced.ToArray();
            }
        }
        if (ready is not null) onReady(ready);
    }
}
