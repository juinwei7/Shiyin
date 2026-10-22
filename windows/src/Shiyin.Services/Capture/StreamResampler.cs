using NAudio.Dsp;

namespace Shiyin.Services.Capture;

/// <summary>
/// 把任意格式的音訊持續轉成 16 kHz 單聲道（同 Mac 的 StreamConverter，design-windows §6）：每個音源一個，重新取樣的狀態跨 buffer 保留。
/// 用 NAudio 的 WdlResampler（純 C#），版本釘死，升級時重跑轉錄的比對
/// </summary>
public sealed class StreamResampler
{
    public const int OutputRate = 16000;

    readonly WdlResampler? resampler;
    readonly int inputRate;

    public StreamResampler(int inputRate)
    {
        this.inputRate = inputRate;
        if (inputRate == OutputRate) return;
        resampler = new WdlResampler();
        resampler.SetMode(true, 2, false);
        resampler.SetFilterParms();
        resampler.SetFeedMode(true);
        resampler.SetRates(inputRate, OutputRate);
    }

    /// <summary>單聲道的輸入（原本的取樣率）→ 16 kHz</summary>
    public float[] Process(ReadOnlySpan<float> mono)
    {
        if (resampler is null) return mono.ToArray();
        if (mono.Length == 0) return [];
        var accepted = resampler.ResamplePrepare(mono.Length, 1, out Span<float> input);
        mono[..Math.Min(accepted, mono.Length)].CopyTo(input);
        var capacity = (int)Math.Ceiling(mono.Length * (double)OutputRate / inputRate) + 32;
        var output = new float[capacity];
        var produced = resampler.ResampleOut(output, Math.Min(accepted, mono.Length), capacity, 1);
        return produced == capacity ? output : output[..produced];
    }

    /// <summary>交錯的多聲道 PCM 或浮點 → 單聲道浮點（各聲道平均，同 Mac 的 downmix）</summary>
    public static float[] Mono(ReadOnlySpan<byte> data, int channels, int bitsPerSample, bool isFloat)
    {
        var bytes = bitsPerSample / 8;
        var frames = data.Length / (bytes * channels);
        var result = new float[frames];
        for (var f = 0; f < frames; f++)
        {
            var sum = 0f;
            for (var c = 0; c < channels; c++)
            {
                var s = data.Slice((f * channels + c) * bytes, bytes);
                sum += (isFloat, bitsPerSample) switch
                {
                    (true, 32) => BitConverter.ToSingle(s),
                    (true, 64) => (float)BitConverter.ToDouble(s),
                    (false, 16) => BitConverter.ToInt16(s) / 32768f,
                    (false, 24) => ((s[2] << 24 | s[1] << 16 | s[0] << 8) >> 8) / 8388608f,
                    (false, 32) => BitConverter.ToInt32(s) / 2147483648f,
                    (false, 8) => (s[0] - 128) / 128f,
                    _ => 0,
                };
            }
            result[f] = sum / channels;
        }
        return result;
    }
}
