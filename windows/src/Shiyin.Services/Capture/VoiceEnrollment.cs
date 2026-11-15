using Shiyin.Data;
using Shiyin.Speakers;

namespace Shiyin.Services.Capture;

/// <summary>
/// 錄一段自己的聲音（R80、M§56.2、design-windows §11.20）：設定裡選的麥克風收 16 kHz 單聲道，最多 40 秒；
/// 有聲音（> −50 dBFS 的 100 毫秒）的部分到 10 秒才能存。錄音只在記憶體裡，算完聲紋就丟（R80.5）
/// </summary>
public sealed class VoiceEnrollment(Func<IAudioSource> makeSource)
{
    public const double MaxSeconds = 40;
    public const double MinVoicedSeconds = 10;
    const float VoicedLevel = -50;
    const int Frame = 1600;

    readonly object gate = new();
    readonly List<float> samples = [];
    IAudioSource? source;
    int processed;
    double zeroSeconds;

    public double Seconds { get; private set; }
    public double VoicedSeconds { get; private set; }
    /// <summary>麥克風完全沒有聲音超過 3 秒（同收音提醒 R24.3）</summary>
    public bool MicSilent { get; private set; }
    /// <summary>音量（0～1）</summary>
    public float Level { get; private set; }
    public bool Recording { get; private set; }
    public bool CanFinish => VoicedSeconds >= MinVoicedSeconds;
    /// <summary>數字變了（在音訊的執行緒上發）</summary>
    public event Action? Changed;

    public void Start()
    {
        Stop();
        lock (gate)
        {
            samples.Clear();
            (processed, zeroSeconds, Seconds, VoicedSeconds, MicSilent, Level) = (0, 0, 0, 0, false, 0);
        }
        var s = makeSource();
        s.OnSamples = Append;
        s.Start();
        source = s;
        Recording = true;
    }

    void Append(float[] chunk)
    {
        var full = false;
        lock (gate)
        {
            if (!Recording) return;
            samples.AddRange(chunk);
            while (processed + Frame <= samples.Count)
            {
                double sum = 0;
                for (var i = processed; i < processed + Frame; i++) sum += samples[i] * samples[i];
                var rms = Math.Sqrt(sum / Frame);
                var db = rms > 0 ? 20 * Math.Log10(rms) : -120;
                if (db > VoicedLevel) VoicedSeconds += 0.1;
                zeroSeconds = rms == 0 ? zeroSeconds + 0.1 : 0;
                Level = (float)Math.Clamp((db + 60) / 60, 0, 1);
                processed += Frame;
            }
            Seconds = samples.Count / 16000.0;
            MicSilent = zeroSeconds >= 3;
            full = Seconds >= MaxSeconds;
        }
        // 到 40 秒自動停（R80.3）
        if (full) Task.Run(Stop);
        Changed?.Invoke();
    }

    /// <summary>停止收音，回傳錄到的音訊</summary>
    public float[] Stop()
    {
        var s = source;
        source = null;
        Recording = false;
        s?.Stop();
        lock (gate) return [.. samples];
    }

    /// <summary>從說話者辨識的結果取「我」的聲紋：說話秒數最多的那一位，要 ≥ 8 秒（同比對的下限）；不夠時是 null</summary>
    public static float[]? BestVoiceprint(Diarizer.Output output)
    {
        if (output.SpeechSeconds.Count == 0) return null;
        var best = output.SpeechSeconds.MaxBy(x => x.Value);
        return best.Value >= AppDatabase.VoiceprintMinimumSeconds && output.Voiceprints.TryGetValue(best.Key, out var embedding) ? embedding : null;
    }
}
