using Shiyin.Model;
using Shiyin.Qwen;
using Shiyin.Transcribe;
using Shiyin.Whisper;
using Xunit;

namespace Shiyin.Tests;

/// <summary>Qwen3-ASR 的規則跟 Mac 相同（R43.9、design-windows §8.4）：分段、整理輸出、跳針的判斷、幻覺防線、重試流程</summary>
public class QwenTests
{
    const int Rate = 16000;

    /// <summary>平穩的噪音加幾處安靜：換人處要切在安靜的地方，太長的段在安靜處切，短的併進前一段</summary>
    static float[] Signal(int seconds, params double[] quietAt)
    {
        var lcg = new Golden.Lcg(7);
        var samples = new float[seconds * Rate];
        for (var i = 0; i < samples.Length; i++)
        {
            var t = (double)i / Rate;
            samples[i] = lcg.Next() * (quietAt.Any(q => Math.Abs(t - q) < 0.3) ? 0.001f : 0.3f);
        }
        return samples;
    }

    /// <summary>企業伺服器分到的顯示卡（M§36.6、D125）：llama-server 只看得到那一張，照 PCI 匯流排的順序（跟 nvidia-smi 一樣）</summary>
    [Fact]
    public void ServerSeesOnlyTheAssignedGpu()
    {
        if (OperatingSystem.IsWindows()) return;   // 假的 llama-server 是 shell 腳本
        using var home = new TempHome();
        string Run(int? device)
        {
            var script = Path.Combine(home.Path, "llama-server");
            File.WriteAllText(script, "#!/bin/sh\necho \"[$CUDA_VISIBLE_DEVICES][$CUDA_DEVICE_ORDER]\" > \"$(dirname \"$0\")/env.txt\"\nexit 1\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            foreach (var model in new[] { "m.gguf", "mmproj.gguf" }) File.WriteAllText(Path.Combine(home.Path, model), "x");
            var options = new QwenServer.Options(script, Path.Combine(home.Path, "m.gguf"), Path.Combine(home.Path, "mmproj.gguf"), 2, true, Device: device);
            var error = Assert.Throws<WhisperException>(() => QwenServer.Start(options));
            Assert.Equal(WhisperErrorKind.LoadFailed, error.Kind);
            return File.ReadAllText(Path.Combine(home.Path, "env.txt")).Trim();
        }
        Assert.Equal("[1][PCI_BUS_ID]", Run(1));
        Assert.Equal("[][]", Run(null));
    }

    [Fact]
    public void ChunksCutAtSpeakerChangesWhenQuiet()
    {
        var samples = Signal(40, 12.0, 25.0);
        List<SpeakerRules.Turn> turns = [new(0, 0, 12), new(1, 12, 25), new(0, 25, 40)];
        var chunks = QwenRules.Chunks(samples, turns);
        // 兩個換人處都安靜：切成三段，切點在 12、25 秒附近
        Assert.Equal(3, chunks.Count);
        Assert.Equal(0, chunks[0].Start);
        Assert.InRange(chunks[0].End / (double)Rate, 11.5, 12.5);
        Assert.InRange(chunks[1].End / (double)Rate, 24.5, 25.5);
        Assert.Equal(samples.Length, chunks[2].End);
        // 接縫沒有重疊也沒有空隙
        for (var i = 1; i < chunks.Count; i++) Assert.Equal(chunks[i - 1].End, chunks[i].Start);
        // 換人處不安靜就不切（只剩長度的切法）
        var loud = Signal(40);
        var none = QwenRules.Chunks(loud, turns);
        Assert.All(none, c => Assert.True(c.End - c.Start <= 15 * Rate * 1.5 + Rate));
        Assert.True(QwenRules.Chunks(loud, turns, anyChange: true).Count >= 3);
    }

    [Fact]
    public void ChunksSplitLongPiecesAndMergeShortOnes()
    {
        // 40 秒沒有說話者片段：15 秒一段，剩下超過一段半才切 → 15、15、10
        var samples = Signal(40, 15.0, 30.0);
        var chunks = QwenRules.Chunks(samples, []);
        Assert.Equal(3, chunks.Count);
        Assert.InRange(chunks[0].End / (double)Rate, 14, 16);
        Assert.InRange(chunks[1].End / (double)Rate, 29, 31);
        // 20 秒：不到一段半，不切
        Assert.Single(QwenRules.Chunks(Signal(20, 10.0), []));
        // 換人處離結尾不到 0.5 秒：那一小段併進前一段，不會有短於 0.5 秒的段（同 Mac：開頭的短段沒有前一段可併，照樣留著）
        var late = Signal(10, 9.7);
        var merged = QwenRules.Chunks(late, [new(0, 0, 9.7), new(1, 9.7, 10)]);
        Assert.Single(merged);
        Assert.All(merged, c => Assert.True(c.End - c.Start >= Rate / 2));
        Assert.Empty(QwenRules.Chunks(Array.Empty<float>(), []));
    }

    [Fact]
    public void ChunksSplitMixedPiecesAtTurnStarts()
    {
        // 一段 10 秒裡兩個人各講 5 秒、換人處不安靜：預設照片段的起點再切，關掉 splitMixed 就不切
        var samples = Signal(10);
        List<SpeakerRules.Turn> turns = [new(0, 0, 5), new(1, 5, 10)];
        var split = QwenRules.Chunks(samples, turns);
        Assert.Equal(2, split.Count);
        Assert.Equal(5 * Rate, split[0].End);
        Assert.Single(QwenRules.Chunks(samples, turns, splitMixed: false));
        // 第二個人只講 1 秒（< 1.5 秒）：不切
        Assert.Single(QwenRules.Chunks(samples, [new(0, 0, 9), new(1, 9, 10)]));
    }

    [Fact]
    public void ContextAndLanguage()
    {
        Assert.Equal("Lumio、PayLink", QwenRules.Context(["Lumio", "PayLink"], MeetingLanguage.Zh));
        Assert.Equal("Lumio, PayLink", QwenRules.Context(["Lumio", "PayLink"], MeetingLanguage.En));
        Assert.Equal("", QwenRules.Context([], MeetingLanguage.Zh));
        // 超過上限（中文 150 字）就停在前一個詞
        var terms = Enumerable.Range(0, 60).Select(i => $"詞彙{i:00}").ToList();
        var context = QwenRules.Context(terms, MeetingLanguage.Zh);
        Assert.True(context.Length <= 150);
        Assert.StartsWith("詞彙00、詞彙01", context);
        Assert.Equal("Chinese", QwenRules.LanguageName(MeetingLanguage.Zh));
        Assert.Equal("English", QwenRules.LanguageName(MeetingLanguage.En));
        Assert.Equal("Japanese", QwenRules.LanguageName(MeetingLanguage.Ja));
        Assert.Equal("language Chinese<asr_text>", QwenRules.Prefill("Chinese"));
        Assert.Equal(64 + 180, QwenRules.MaxTokens(15));
    }

    [Fact]
    public void CleanAndRepeats()
    {
        Assert.Equal("打開 APP 的設定", QwenRules.Clean(" 打開 A P P 的設定 \n"));
        Assert.Equal("OTP 和 Wi-Fi", QwenRules.JoinSpelledAcronyms("O T P 和 Wi-Fi"));
        Assert.Equal("A 跟 B", QwenRules.JoinSpelledAcronyms("A 跟 B"));   // 單一字母不併
        Assert.Equal("對對對", QwenRules.TrimRepeats("對對對對對對對對"));
        Assert.Equal("好的好的好的", QwenRules.TrimRepeats("好的好的好的好的好的好的"));
        Assert.Equal("今天天氣很好", QwenRules.TrimRepeats("今天天氣很好"));
        Assert.True(QwenRules.LooksBad("對對對對對對對", truncated: false));
        Assert.True(QwenRules.LooksBad("正常的句子", truncated: true));
        Assert.False(QwenRules.LooksBad("今天討論 Lumio 秋季新品上市時程", truncated: false));
    }

    [Fact]
    public void GuardMeasures()
    {
        Assert.Equal(4, QwenRules.Units("one two  three four", "English"));
        Assert.Equal(6, QwenRules.Units("今天 123，好。", "Chinese"));
        Assert.True(QwenRules.TooLong("一二三四五六七八九十一二三四五六七八九十", "Chinese", speechSeconds: 1));   // 20 > 8 + 4
        Assert.False(QwenRules.TooLong("一二三四五六七八九十", "Chinese", speechSeconds: 1));
        Assert.Equal(1, QwenRules.Similarity("", ""));
        Assert.Equal(0, QwenRules.Similarity("abc", ""));
        Assert.Equal(2.0 * 2 / 6, QwenRules.Similarity("abc", "abd"), 6);
        Assert.Equal(1, QwenRules.Similarity("同一句話", "同一句話"));
        // 有聲音的秒數：中間 1 秒安靜
        var samples = Signal(3, 1.5);
        var median = QwenRules.MedianFrameEnergy(samples);
        Assert.True(median > 0);
        Assert.InRange(QwenRules.SpeechSeconds(samples, 0, samples.Length, median), 2.0, 2.6);
    }

    /// <summary>假的解碼器：照呼叫的次數回答，記下每次的取樣參數</summary>
    sealed class FakeDecoder(params (string Text, bool Truncated)[] answers) : IQwenDecoder
    {
        public List<QwenSampling> Calls { get; } = [];
        public List<int> Lengths { get; } = [];

        public QwenDecoded Decode(ReadOnlySpan<float> samples, string context, string languageName, int maxTokens, QwenSampling sampling, CancellationToken cancel)
        {
            Calls.Add(sampling);
            Lengths.Add(samples.Length);
            var (text, truncated) = answers[Math.Min(Calls.Count - 1, answers.Length - 1)];
            return new QwenDecoded(text, truncated, text.Length);
        }
    }

    [Fact]
    public void TranscriberRetriesThenTrims()
    {
        // 第一次跳針、第二次還是跳針、段太短不能切半 → 留壓縮比低的、縮成 3 次、低信心
        var decoder = new FakeDecoder(("对对对对对对对对对对", false), ("好好好好好好好好", false));
        var samples = Signal(4);
        var segments = QwenTranscriber.Transcribe(samples, [(0, samples.Length)], decoder, MeetingLanguage.Zh, [], new WhisperControl(), guard: false);
        Assert.Equal(2, decoder.Calls.Count);
        Assert.Equal(QwenSampling.Greedy, decoder.Calls[0]);
        Assert.Equal(new QwenSampling(1.3f, 128, 0.2f, 1), decoder.Calls[1]);
        var segment = Assert.Single(segments);
        Assert.True(segment.LowConfidence);
        Assert.True(segment.Text is "對對對" or "好好好");   // 簡體已轉成台灣繁體
        Assert.Equal(0, segment.Start);
        Assert.Equal(4, segment.End);
    }

    [Fact]
    public void TranscriberSplitsLongBadChunks()
    {
        // 10 秒的段兩次都壞：切成兩半各自再來（兩半都好）
        var decoder = new FakeDecoder(("啊啊啊啊啊啊啊啊啊", false), ("啊啊啊啊啊啊啊啊啊", false), ("前半", false), ("後半", false));
        var samples = Signal(10, 5.0);
        var segments = QwenTranscriber.Transcribe(samples, [(0, samples.Length)], decoder, MeetingLanguage.Zh, [], new WhisperControl(), guard: false);
        Assert.Equal(["前半", "後半"], segments.Select(s => s.Text));
        Assert.All(segments, s => Assert.False(s.LowConfidence));
        Assert.Equal(4, decoder.Calls.Count);
    }

    [Fact]
    public void TranscriberGuardDropsInventedText()
    {
        // 1 秒的段吐出 30 個字：防線加溫度重解，兩次差很多就丟掉（沒有段落 → Empty）
        var decoder = new FakeDecoder(("這是一段完全沒有說過的話，模型自己編下去，編到三十幾個字才肯停下來", false), ("完全不一樣的另一段內容", false));
        var samples = Signal(1);
        Assert.Throws<WhisperException>(() =>
            QwenTranscriber.Transcribe(samples, [(0, samples.Length)], decoder, MeetingLanguage.Zh, [], new WhisperControl()));
        Assert.Equal(2, decoder.Calls.Count);
        Assert.Equal(0.3f, decoder.Calls[1].Temperature);
        Assert.Equal(7u, decoder.Calls[1].Seed);
        // 兩次差不多就留短的、標低信心
        var similar = new FakeDecoder(("一二三四五六七八九十一二三四五六七八九十", false), ("一二三四五六七八九十一二三四五六七八九", false));
        var kept = QwenTranscriber.Transcribe(samples, [(0, samples.Length)], similar, MeetingLanguage.Zh, [], new WhisperControl());
        var segment = Assert.Single(kept);
        Assert.True(segment.LowConfidence);
        Assert.Equal("一二三四五六七八九十一二三四五六七八九", segment.Text);
    }

    [Fact]
    public void TranscriberKeepsGoodTextAndEnglish()
    {
        var decoder = new FakeDecoder(("We will ship the A P P next week", false));
        var samples = Signal(5);
        var segments = QwenTranscriber.Transcribe(samples, [(0, samples.Length)], decoder, MeetingLanguage.En, ["Lumio"], new WhisperControl());
        var segment = Assert.Single(segments);
        Assert.Equal("We will ship the APP next week", segment.Text);
        Assert.False(segment.LowConfidence);
        Assert.Single(decoder.Calls);
        // 取消：一開始就丟 Cancelled
        var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var e = Assert.Throws<WhisperException>(() =>
            QwenTranscriber.Transcribe(samples, [(0, samples.Length)], decoder, MeetingLanguage.En, [], new WhisperControl(null, cancelled.Token)));
        Assert.Equal(WhisperErrorKind.Cancelled, e.Kind);
    }
}
