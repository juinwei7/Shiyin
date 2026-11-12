using NAudio.Wave;

namespace Shiyin.Services.Capture;

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
