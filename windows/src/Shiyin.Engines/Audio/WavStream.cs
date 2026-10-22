using Shiyin.IO;
using Shiyin.Whisper;

namespace Shiyin.Audio;

/// <summary>從 WAV 依序讀出 16 kHz 單聲道（多聲道取平均；取樣率不同時線性內插）。Windows 的其他格式用 Media Foundation 解碼</summary>
public sealed class WavStream : IAudioStream
{
    readonly WavReader reader;
    readonly double step;
    double phase;
    float previous;
    bool hasPrevious;
    readonly Queue<float> source = new();

    public int EstimatedCount { get; }
    public bool Finished { get; private set; }

    public WavStream(string path)
    {
        reader = new WavReader(path);
        step = reader.SampleRate / 16000.0;
        EstimatedCount = (int)(reader.Frames / step);
    }

    public void Dispose() => reader.Dispose();

    public void Fill(List<float> buffer, int count)
    {
        while (buffer.Count < count && !Finished)
        {
            var block = reader.Read(16000);
            if (block[0].Length == 0)
            {
                Finished = true;
                return;
            }
            var mono = Mono(block);
            if (reader.SampleRate == 16000)
            {
                buffer.AddRange(mono);
                continue;
            }
            foreach (var s in mono) source.Enqueue(s);
            while (source.Count >= 2 || (source.Count >= 1 && hasPrevious))
            {
                if (!hasPrevious) { previous = source.Dequeue(); hasPrevious = true; }
                var next = source.Peek();
                buffer.Add(previous + (next - previous) * (float)phase);
                phase += step;
                while (phase >= 1 && source.Count > 0)
                {
                    phase -= 1;
                    previous = source.Dequeue();
                }
                if (phase >= 1) break;
            }
        }
    }

    static float[] Mono(float[][] channels)
    {
        if (channels.Length == 1) return channels[0];
        var n = channels[0].Length;
        var mono = new float[n];
        for (var i = 0; i < n; i++)
        {
            var sum = 0f;
            foreach (var c in channels) sum += c[i];
            mono[i] = sum / channels.Length;
        }
        return mono;
    }

    /// <summary>整段讀出來</summary>
    public static float[] ReadAll(string path)
    {
        using var stream = new WavStream(path);
        var buffer = new List<float>(stream.EstimatedCount + 16000);
        stream.Fill(buffer, int.MaxValue);
        return [.. buffer];
    }
}
