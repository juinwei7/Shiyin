using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Shiyin.Model;

namespace Shiyin.Transcribe;

/// <summary>
/// Qwen3-ASR 的規則（同 Mac 的 QwenEngine，M§32.4；design-windows §8.4）：先依說話者片段和安靜處分段，每段各自轉錄；
/// 跳針的判斷、修剪、幻覺防線、縮寫併回去。純邏輯，跟引擎無關，兩邊要一字不差
/// </summary>
public static class QwenRules
{
    /// <summary>每段最長幾秒（評估：15 秒跟 30 秒的錯誤率一樣，短一點說話者標得細）</summary>
    public const double MaxChunkSeconds = 15.0;
    /// <summary>文字＋音訊 token 的上限：15 秒音訊約 190 個 token，加提示詞和輸出綽綽有餘；分段最長 120 秒時也夠</summary>
    public const int ContextTokens = 4096;

    // MARK: 分段

    /// <summary>
    /// 在安靜處切、優先切在換人說話的地方（M§32.4）：
    /// 每個換人的時間點前後 1 秒內最安靜的 0.3 秒，能量低於整段中位數的 15% 才切；超過 maxSeconds 的段在預定位置前後 5 秒內最安靜的 0.5 秒切開；
    /// 短於 0.5 秒的併進前一段。splitMixed：混到第二個人 ≥ 1.5 秒且 ≥ 25% 的段，照片段邊界再切；anyChange（評估用）：換人處不看能量門檻
    /// </summary>
    public static List<(int Start, int End)> Chunks(ReadOnlySpan<float> samples, IReadOnlyList<SpeakerRules.Turn> turns,
                                                   double maxSeconds = MaxChunkSeconds, bool anyChange = false, bool splitMixed = true)
    {
        var rate = (int)WhisperRules.SampleRate;
        var n = samples.Length;
        if (n == 0) return [];
        // 能量的前綴和，任何一段的能量都能 O(1) 算出來（乘法照 Mac 用 float，再加進 double）
        var prefix = new double[n + 1];
        for (var i = 0; i < n; i++) prefix[i + 1] = prefix[i] + (double)(samples[i] * samples[i]);
        double Energy(int a, int b) => prefix[Math.Min(b, n)] - prefix[Math.Max(a, 0)];
        // [lo, hi) 裡最安靜的 window 個取樣的中點和能量
        (int Center, double Energy)? Quietest(int lo, int hi, int window)
        {
            if (hi - window <= lo) return null;
            var best = lo;
            var bestEnergy = double.MaxValue;
            var p = lo;
            while (p + window <= hi)
            {
                var e = Energy(p, p + window);
                if (e < bestEnergy) { bestEnergy = e; best = p; }
                p += rate / 100;
            }
            return (best + window / 2, bestEnergy);
        }

        var cuts = new HashSet<int>();
        if (turns.Count > 1)
        {
            // 整段的能量中位數（每 0.1 秒一格）
            var frame = rate / 10;
            var frames = new List<double>();
            var p = 0;
            while (p < n) { frames.Add(Energy(p, p + frame)); p += frame; }
            var nonzero = frames.Where(f => f > 0).Order().ToList();
            var median = nonzero.Count == 0 ? 0 : nonzero[nonzero.Count / 2];
            var window = rate * 3 / 10;
            var threshold = median * 0.15 * window / frame;
            var sorted = turns.OrderBy(t => t.Start).ToList();
            for (var i = 1; i < sorted.Count; i++)
            {
                var (previous, turn) = (sorted[i - 1], sorted[i]);
                if (previous.Speaker == turn.Speaker) continue;
                var c = (int)(turn.Start * rate);
                if (Quietest(Math.Max(window, c - rate), Math.Min(n - window, c + rate), window) is { } q && (anyChange || q.Energy <= threshold))
                    cuts.Add(q.Center);
            }
        }
        var bounds = new List<int> { 0 };
        bounds.AddRange(cuts.Where(c => c > 0 && c < n).Order());
        bounds.Add(n);
        var maxLength = (int)(maxSeconds * rate);
        var pieces = new List<(int Start, int End)>();
        for (var i = 1; i < bounds.Count; i++)
        {
            var (a, b) = (bounds[i - 1], bounds[i]);
            if (b <= a) continue;
            var start = a;
            // 剩下超過一段半才切（最後一段不短於半段）
            while (b - start > maxLength + maxLength / 2)
            {
                var target = start + maxLength;
                var cut = Quietest(Math.Max(start + rate / 2, target - 5 * rate), Math.Min(b - rate / 2, target + 5 * rate), rate / 2)?.Center ?? target;
                pieces.Add((start, cut));
                start = cut;
            }
            pieces.Add((start, b));
        }
        var merged = new List<(int Start, int End)>();
        foreach (var piece in pieces)
        {
            if (merged.Count > 0 && piece.End - piece.Start < rate / 2) merged[^1] = (merged[^1].Start, piece.End);
            else merged.Add(piece);
        }
        if (!splitMixed || turns.Count == 0) return merged;
        // 每位說話者在 [a, b) 裡講了幾秒
        Dictionary<int, double> Overlap(int a, int b)
        {
            var o = new Dictionary<int, double>();
            var (lo, hi) = ((double)a / rate, (double)b / rate);
            foreach (var t in turns)
                if (t.End > lo && t.Start < hi) o[t.Speaker] = o.GetValueOrDefault(t.Speaker) + Math.Min(t.End, hi) - Math.Max(t.Start, lo);
            return o;
        }
        var result = new List<(int Start, int End)>();
        foreach (var piece in merged)
        {
            var ranked = Overlap(piece.Start, piece.End).Values.OrderDescending().ToList();
            if (ranked.Count < 2 || ranked[1] < 1.5 || ranked[1] < 0.25 * (piece.End - piece.Start) / rate)
            {
                result.Add(piece);
                continue;
            }
            // 照片段的起點切，太短的併進前一段
            var points = turns.Select(t => (int)(t.Start * rate)).Where(x => x > piece.Start && x < piece.End).Distinct().Order().ToList();
            var starts = new List<int> { piece.Start };
            starts.AddRange(points);
            var ends = new List<int>(points) { piece.End };
            var parts = new List<(int Start, int End)>();
            for (var i = 0; i < starts.Count; i++)
            {
                var (a, b) = (starts[i], ends[i]);
                if (b <= a) continue;
                if (parts.Count > 0 && b - a < rate / 2) parts[^1] = (parts[^1].Start, b);
                else parts.Add((a, b));
            }
            result.AddRange(parts);
        }
        return result;
    }

    // MARK: 提示詞與語言

    /// <summary>詞彙表當作提示（Qwen3-ASR 的 contextual biasing）：跟 whisper 的提示詞同樣的上限（中文 150 字、英文 400 個字元）</summary>
    public static string Context(IReadOnlyList<string> glossary, MeetingLanguage language)
    {
        var limit = language == MeetingLanguage.En ? 400 : 150;
        var separator = language == MeetingLanguage.En ? ", " : "、";   // l10n-skip
        var picked = new List<string>();
        foreach (var term in glossary)
        {
            var candidate = string.Join(separator, picked.Append(term));
            if (new StringInfo(candidate).LengthInTextElements > limit) break;
            picked.Add(term);
        }
        return string.Join(separator, picked);
    }

    public static string LanguageName(MeetingLanguage language) => language switch
    {
        MeetingLanguage.Zh => "Chinese",
        MeetingLanguage.En => "English",
        _ => "Japanese",
    };

    /// <summary>assistant 先填的開頭（Qwen3-ASR 的輸出格式）：強制語言</summary>
    public static string Prefill(string languageName) => $"language {languageName}<asr_text>";

    /// <summary>最多生成幾個 token：每秒 12 個加一點餘裕（同 Mac）</summary>
    public static int MaxTokens(double seconds) => (int)(seconds * 12) + 64;

    // MARK: 幻覺防線

    /// <summary>每 0.1 秒一格的能量中位數（只算有聲音的格）：分段和幻覺防線都用它當「有聲音」的基準</summary>
    public static double MedianFrameEnergy(ReadOnlySpan<float> samples)
    {
        var frame = (int)WhisperRules.SampleRate / 10;
        var frames = new List<double>();
        var p = 0;
        while (p + frame <= samples.Length)
        {
            var e = 0.0;
            for (var i = p; i < p + frame; i++) e += (double)(samples[i] * samples[i]);
            if (e > 0) frames.Add(e);
            p += frame;
        }
        if (frames.Count == 0) return 0;
        frames.Sort();
        return frames[frames.Count / 2];
    }

    /// <summary>這一段裡有聲音的秒數：能量超過整段中位數一半的 0.1 秒格數</summary>
    public static double SpeechSeconds(ReadOnlySpan<float> samples, int start, int end, double median)
    {
        var frame = (int)WhisperRules.SampleRate / 10;
        var threshold = median * 0.5;
        var count = 0;
        var p = start;
        while (p + frame <= end)
        {
            var e = 0.0;
            for (var i = p; i < p + frame; i++) e += (double)(samples[i] * samples[i]);
            if (e > threshold) count++;
            p += frame;
        }
        return count * 0.1;
    }

    /// <summary>文字的長度單位：英文算字數，其他算字母和數字的個數</summary>
    public static int Units(string text, string languageName)
    {
        if (languageName == "English") return text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var count = 0;
        foreach (var rune in text.EnumerateRunes()) if (Rune.IsLetter(rune) || Rune.IsNumber(rune)) count++;
        return count;
    }

    /// <summary>輸出比有聲音的秒數能講的還多（英文 5 字／秒、中日文 8 字／秒，加一點餘裕）：短而吵的段落常會編出整段沒說過的話</summary>
    public static bool TooLong(string text, string languageName, double speechSeconds)
    {
        var cap = languageName == "English" ? speechSeconds * 5 + 3 : speechSeconds * 8 + 4;
        return Units(text, languageName) > cap;
    }

    /// <summary>兩段文字的相似度（difflib 的 ratio：2 × 最長共同子序列 ÷ 兩段長度和），以字（grapheme）為單位</summary>
    public static double Similarity(string a, string b)
    {
        var x = TextElements(a);
        var y = TextElements(b);
        if (x.Count == 0 && y.Count == 0) return 1;
        var prev = new int[y.Count + 1];
        var cur = new int[y.Count + 1];
        for (var i = 1; i <= x.Count; i++)
        {
            for (var j = 1; j <= y.Count; j++)
                cur[j] = x[i - 1] == y[j - 1] ? prev[j - 1] + 1 : Math.Max(prev[j], cur[j - 1]);
            (prev, cur) = (cur, prev);
        }
        return 2.0 * prev[y.Count] / (x.Count + y.Count);
    }

    static List<string> TextElements(string s)
    {
        var list = new List<string>();
        var e = StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext()) list.Add((string)e.Current);
        return list;
    }

    // MARK: 輸出的整理

    /// <summary>去掉 tokenizer 漏出來的私用區字元和前後空白；模型把縮寫一個字母一個字母拆開（「A P P」「O T P」），併回去</summary>
    public static string Clean(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
            if (rune.Value is < 0xE000 or > 0xF8FF) sb.Append(rune.ToString());
        return JoinSpelledAcronyms(sb.ToString().Trim());
    }

    static readonly Regex Spelled = new(@"(?<![A-Za-z0-9])(?:[A-Z] )+[A-Z](?![A-Za-z0-9])", RegexOptions.Compiled);

    /// <summary>「A P P」→「APP」：兩個以上單獨的大寫字母用空白隔開時併成一個字</summary>
    public static string JoinSpelledAcronyms(string text) => Spelled.Replace(text, m => m.Value.Replace(" ", ""));

    /// <summary>同一個 1～6 字的片段連續出現 5 次以上</summary>
    static readonly Regex Repeats = new(@"(.{1,6}?)\1{4,}", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>壞掉：到達 max_tokens、壓縮比 > 2.4、同一片段連續重複 5 次以上</summary>
    public static bool LooksBad(string text, bool truncated) =>
        truncated || WhisperRules.CompressionRatio(text) > 2.4 || Repeats.IsMatch(text);

    /// <summary>連續重複的片段縮成 3 次（「對對對對對對對對」→「對對對」）</summary>
    public static string TrimRepeats(string text) => Repeats.Replace(text, m => string.Concat(Enumerable.Repeat(m.Groups[1].Value, 3)));
}
