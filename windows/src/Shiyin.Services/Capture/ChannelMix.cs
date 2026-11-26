using NAudio.Wave;

namespace Shiyin.Services.Capture;

/// <summary>播放時聽哪一邊（R119.2、M§93.4）：存在 meetings.play_channels 的字是 both / mic / system</summary>
public enum PlayChannels { Both, Mic, System }

public static class PlayChannelsText
{
    public static PlayChannels Parse(string? value) => value switch { "mic" => PlayChannels.Mic, "system" => PlayChannels.System, _ => PlayChannels.Both };
    public static string Key(this PlayChannels c) => c switch { PlayChannels.Mic => "mic", PlayChannels.System => "system", _ => "both" };
}

/// <summary>混音的設定（M§93.4）：左右聲道的音量、麥克風只留建議收錄的區塊（照開始排好、不重疊）</summary>
public sealed record ChannelSettings(float MicGain, float SystemGain, bool GateMic, IReadOnlyList<(double Start, double End)> Regions)
{
    public static readonly ChannelSettings Default = new(1, 1, false, []);

    public static ChannelSettings For(PlayChannels channels, bool gateMic, IEnumerable<(double Start, double End)> regions) =>
        new(channels == PlayChannels.System ? 0 : 1, channels == PlayChannels.Mic ? 0 : 1, gateMic && channels != PlayChannels.System,
            [.. regions.OrderBy(r => r.Start)]);

    public bool SameAs(ChannelSettings other) =>
        MicGain == other.MicGain && SystemGain == other.SystemGain && GateMic == other.GateMic && Regions.SequenceEqual(other.Regions);
}

/// <summary>畫面改、播放的執行緒讀（播放中切換不用重建 MediaStreamSource）</summary>
public sealed class ChannelControl
{
    readonly object gate = new();
    ChannelSettings settings = ChannelSettings.Default;

    public ChannelSettings Settings
    {
        get { lock (gate) return settings; }
        set { lock (gate) settings = value; }
    }
}

/// <summary>
/// 雙聲道的錄音（左聲道麥克風、右聲道電腦聲音）播放、匯出 m4a 時混在一起，兩耳聽到的一樣（R73、M§49、design-windows §11.16）。
/// 錄音檔本身不變
/// </summary>
public static class ChannelMix
{
    /// <summary>同 Mac（AVAssetReader 混成單聲道的 −3 dB）：只有一邊有聲音時不會小一半</summary>
    public const float Gain = 0.7071f;

    /// <summary>交錯排列的取樣混成單聲道：2 個聲道是 (L + R) × 0.7071、超過 ±1 截掉；其他聲道數取平均</summary>
    public static void Mix(ReadOnlySpan<float> interleaved, int channels, Span<float> mono)
    {
        var frames = Math.Min(mono.Length, interleaved.Length / channels);
        for (var f = 0; f < frames; f++)
        {
            if (channels == 2)
            {
                mono[f] = Math.Clamp((interleaved[2 * f] + interleaved[2 * f + 1]) * Gain, -1f, 1f);
                continue;
            }
            var sum = 0f;
            for (var c = 0; c < channels; c++) sum += interleaved[f * channels + c];
            mono[f] = sum / channels;
        }
    }

    /// <summary>區塊邊界的淡入淡出（避免爆音）</summary>
    public const double Fade = 0.01;

    /// <summary>
    /// 麥克風在 t 秒的音量（0～1）：區塊裡是 1、外面是 0，區塊的頭尾 10 ms 線性淡入淡出。regions 照開始排好、不重疊。
    /// 同 Mac 的 ChannelMix.gate（標準答案 util/mic-gate.json）
    /// </summary>
    public static double Gate(double t, IReadOnlyList<(double Start, double End)> regions)
    {
        int low = 0, high = regions.Count;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (regions[mid].Start <= t) low = mid + 1; else high = mid;
        }
        if (low == 0) return 0;
        var r = regions[low - 1];
        if (!(t < r.End)) return 0;
        return Math.Min(1, Math.Max(0, Math.Min(t - r.Start, r.End - t) / Fade));
    }

    /// <summary>
    /// 雙聲道的交錯取樣混成單聲道（M§93.4）：m = (L × 麥克風音量 × gate(t) + R × 電腦聲音音量) × 0.7071，超過 ±1 截掉。
    /// start 是第一個取樣的時間（秒）
    /// </summary>
    public static void MixGated(ReadOnlySpan<float> interleaved, Span<float> mono, ChannelSettings s, double start, int sampleRate)
    {
        var frames = Math.Min(mono.Length, interleaved.Length / 2);
        var gating = s.GateMic && s.MicGain > 0;
        for (var f = 0; f < frames; f++)
        {
            var g = gating ? (float)Gate(start + f / (double)sampleRate, s.Regions) : 1f;
            var mic = interleaved[2 * f] * s.MicGain * g;
            var system = interleaved[2 * f + 1] * s.SystemGain;
            mono[f] = Math.Clamp((mic + system) * Gain, -1f, 1f);
        }
    }

    /// <summary>匯出 wav 用（M§93.6，同 Mac 的 mixGated）：m = (L × gate(t) + R) × 0.7071，超過 ±1 截掉；長度取兩邊較短的</summary>
    public static float[] MixGatedChannels(float[] left, float[] right, IEnumerable<(double Start, double End)> regions, int sampleRate)
    {
        var sorted = regions.OrderBy(r => r.Start).ToList();
        var count = Math.Min(left.Length, right.Length);
        var output = new float[count];
        for (var i = 0; i < count; i++)
            output[i] = Math.Clamp((left[i] * (float)Gate(i / (double)sampleRate, sorted) + right[i]) * Gain, -1f, 1f);
        return output;
    }

    /// <summary>播放用：跟 ToMono 一樣，2 個聲道時照 control 的設定混（M§93.4）；Time 是下一個取樣的時間，跳播時設</summary>
    public sealed class GatedMono(ISampleProvider source, ChannelControl control) : ISampleProvider
    {
        float[] buffer = [];

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
        public double Time { get; set; }

        public int Read(Span<float> mono)
        {
            var channels = source.WaveFormat.Channels;
            if (buffer.Length < mono.Length * channels) buffer = new float[mono.Length * channels];
            var frames = source.Read(buffer.AsSpan(0, mono.Length * channels)) / channels;
            if (channels == 2) MixGated(buffer.AsSpan(0, frames * 2), mono[..frames], control.Settings, Time, WaveFormat.SampleRate);
            else Mix(buffer.AsSpan(0, frames * channels), channels, mono[..frames]);
            Time += frames / (double)WaveFormat.SampleRate;
            return frames;
        }
    }

    /// <summary>多聲道的取樣混成單聲道（播放的 MediaStreamSource、匯出 m4a 用）</summary>
    public sealed class ToMono(ISampleProvider source) : ISampleProvider
    {
        float[] buffer = [];

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);

        public int Read(Span<float> mono)
        {
            var channels = source.WaveFormat.Channels;
            if (buffer.Length < mono.Length * channels) buffer = new float[mono.Length * channels];
            var frames = source.Read(buffer.AsSpan(0, mono.Length * channels)) / channels;
            Mix(buffer.AsSpan(0, frames * channels), channels, mono[..frames]);
            return frames;
        }
    }
}
