using Shiyin.Speech;
using Xunit;

namespace Shiyin.Tests;

/// <summary>快速辨識、即時逐字稿送進 VAD 前的自動增益（design-windows §7、D61）</summary>
public sealed class AutoGainTests
{
    /// <summary>每秒前半秒是正弦波（音量 dbfs）、後半秒安靜，跟說話差不多：第 90 百分位是說話的音量</summary>
    static float[] Speech(double seconds, float dbfs)
    {
        var amplitude = MathF.Pow(10, dbfs / 20) * MathF.Sqrt(2);   // 正弦波的 RMS 是振幅 / √2
        var data = new float[(int)(seconds * 16000)];
        for (var i = 0; i < data.Length; i++)
            data[i] = i % 16000 < 8000 ? amplitude * MathF.Sin(i * 0.07f) : 0;
        return data;
    }

    static void Feed(AutoGain gain, float[] samples)
    {
        for (var i = 0; i < samples.Length; i += 512) gain.Apply(samples.AsSpan(i, Math.Min(512, samples.Length - i)));
    }

    [Fact]
    public void QuietSpeechIsRaisedToTarget()
    {
        var gain = new AutoGain();
        Feed(gain, Speech(10, -40));
        Assert.InRange(gain.GainDb, 17.5, 18.5);   // −40 → −22
    }

    [Fact]
    public void GainIsCapped()
    {
        var gain = new AutoGain();
        Feed(gain, Speech(10, -60));
        Assert.InRange(gain.GainDb, AutoGain.MaxGainDb - 0.1, AutoGain.MaxGainDb + 0.1);
    }

    [Fact]
    public void LoudSpeechIsNotReduced()
    {
        var gain = new AutoGain();
        var samples = Speech(10, -15);
        var copy = samples.ToArray();
        Feed(gain, samples);
        Assert.Equal(0, gain.GainDb, 3);
        Assert.Equal(copy, samples);
    }

    [Fact]
    public void FirstTwoSecondsAreUnchanged()
    {
        var gain = new AutoGain();
        var samples = Speech(1.9, -50);
        var copy = samples.ToArray();
        Feed(gain, samples);
        Assert.Equal(copy, samples);
    }

    [Fact]
    public void AppliesGainAndClips()
    {
        var gain = new AutoGain();
        Feed(gain, Speech(10, -40));
        var samples = new[] { 0.01f, -0.01f, 0.5f, -0.5f };
        gain.Apply(samples);
        var factor = MathF.Pow(10, gain.GainDb / 20);
        Assert.Equal(0.01f * factor, samples[0], 4);
        Assert.Equal(-0.01f * factor, samples[1], 4);
        Assert.Equal(1f, samples[2]);
        Assert.Equal(-1f, samples[3]);
    }

    [Fact]
    public void FixedGainDoesNotAdapt()
    {
        var gain = AutoGain.Fixed(6);
        Feed(gain, Speech(10, -60));
        Assert.Equal(6, gain.GainDb, 3);
    }
}
