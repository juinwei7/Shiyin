namespace Shiyin.Transcribe;

/// <summary>
/// Intel 顯示卡加速的檢查（R27 R4.8、design-windows §8、D138）：同一段錄音讓顯示卡和 CPU 各轉一次，
/// 文字幾乎一樣、顯示卡又比較快才用顯示卡。純邏輯，跟引擎無關
/// </summary>
public static class GpuCheckRules
{
    /// <summary>比對用的長度</summary>
    public const double SampleSeconds = 30;
    /// <summary>從開頭最多讀幾秒來挑那一段</summary>
    public const double ScanSeconds = 180;
    /// <summary>每隔幾秒試一個起點</summary>
    public const double StepSeconds = 5;
    /// <summary>兩邊的文字至少要這麼像（difflib 的 ratio）</summary>
    public const double MinSimilarity = 0.8;
    /// <summary>CPU 聽出的字少於這個數：那一段沒什麼人說話，判斷不了</summary>
    public const int MinChars = 10;
    /// <summary>顯示卡的時間要在 CPU 的這個比例以內才算比較快（差一點點時寧可用 CPU）</summary>
    public const double MaxGpuRatio = 0.8;
    /// <summary>顯示卡先用開頭這幾秒暖機一次、不計時（第一次會編譯 shader）</summary>
    public const double WarmupSeconds = 2;

    public enum Verdict { Inconclusive, Gpu, Cpu }

    /// <summary>平方和最大的那一段（最可能有人說話）的起點；比一段短時從 0 開始</summary>
    public static int LoudestWindow(ReadOnlySpan<float> samples, int window, int step)
    {
        if (samples.Length <= window || step <= 0) return 0;
        // 前綴和，每個起點 O(1)
        var prefix = new double[samples.Length + 1];
        for (var i = 0; i < samples.Length; i++) prefix[i + 1] = prefix[i] + (double)samples[i] * samples[i];
        var best = 0;
        var bestEnergy = -1.0;
        for (var start = 0; start + window <= samples.Length; start += step)
        {
            var energy = prefix[start + window] - prefix[start];
            if (energy > bestEnergy) (best, bestEnergy) = (start, energy);
        }
        return best;
    }

    /// <summary>判斷這台電腦的顯示卡能不能用：文字幾乎一樣、顯示卡的時間又在 CPU 的 8 成以內</summary>
    public static Verdict Decide(string gpuText, string cpuText, double gpuSeconds, double cpuSeconds)
    {
        var cpu = Compact(cpuText);
        if (cpu.Length < MinChars) return Verdict.Inconclusive;
        if (QwenRules.Similarity(Compact(gpuText), cpu) < MinSimilarity) return Verdict.Cpu;
        return gpuSeconds <= cpuSeconds * MaxGpuRatio ? Verdict.Gpu : Verdict.Cpu;
    }

    /// <summary>兩邊文字的相似度（去掉空白，log 用）</summary>
    public static double TextSimilarity(string gpuText, string cpuText) => QwenRules.Similarity(Compact(gpuText), Compact(cpuText));

    static string Compact(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
}
