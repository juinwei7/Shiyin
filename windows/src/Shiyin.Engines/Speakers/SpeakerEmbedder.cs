using SherpaOnnx;
using Shiyin.Transcribe;

namespace Shiyin.Speakers;

/// <summary>說話者辨識的聲紋模型（3D-Speaker ERes2Net，design-windows §9）：一段聲音算一個長度 1 的聲紋</summary>
public sealed class SpeakerEmbedder : IDisposable
{
    readonly SpeakerEmbeddingExtractor extractor;

    public SpeakerEmbedder(string model, int threads)
    {
        var config = new SpeakerEmbeddingExtractorConfig { Model = model, NumThreads = threads, Provider = "cpu" };
        extractor = new SpeakerEmbeddingExtractor(config);
    }

    public void Dispose() => extractor.Dispose();

    public int Dimension => extractor.Dim;

    /// <summary>不到 0.5 秒的回傳 null</summary>
    public float[]? Embed(float[] samples, double start, double end)
    {
        var from = Math.Clamp((int)(start * WhisperRules.SampleRate), 0, samples.Length);
        var to = Math.Clamp((int)(end * WhisperRules.SampleRate), from, samples.Length);
        if (to - from < (int)(0.5 * WhisperRules.SampleRate)) return null;
        using var stream = extractor.CreateStream();
        stream.AcceptWaveform((int)WhisperRules.SampleRate, samples[from..to]);
        stream.InputFinished();
        if (!extractor.IsReady(stream)) return null;
        return SpeakerClustering.Normalized(extractor.Compute(stream));
    }
}
