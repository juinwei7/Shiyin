using System.Diagnostics;
using Shiyin.IO;

namespace Shiyin.Services.Capture;

/// <summary>
/// 把各音源的 16 kHz 音訊對齊後寫成 WAV（M§5.2，同 Mac 的 RecordingWriter），同時把混音後的單聲道交給即時轉錄。
/// 兩個音源時：左聲道麥克風、右聲道電腦聲音。暫停時丟掉收到的音訊，所以檔案裡沒有暫停的那一段。
/// Windows 的電腦聲音沒有東西在播時不送資料，音源自己依時間補靜音（design-windows §6），這裡的對齊規則跟 Mac 相同
/// </summary>
public sealed class RecordingWriter : IDisposable
{
    public const double SampleRate = 16000;

    public int Channels { get; }
    public string Path { get; }
    public Action<float[]>? OnMix { get; set; }

    readonly WavWriter file;
    readonly object gate = new();
    readonly List<float>[] pending;
    readonly float[] levels;
    readonly double[] silentSeconds;
    bool paused;
    readonly long[] acceptedFrames;   // 各聲道累計收到的音框（不含暫停）
    readonly double[] lastReceived;   // 各聲道最後一次收到音訊的時間，看門狗用（M§20.2）
    bool failed;
    /// <summary>收音提醒（M§26.1）：各聲道每 100 毫秒以上一格的音量（dBFS），留最近 300 格（30 秒）</summary>
    readonly float[] frameEnergy;
    readonly int[] frameSamples;
    readonly Queue<float>[] recent;
    long writtenFrames;               // 只在寫檔的執行緒上讀寫
    Thread? thread;
    volatile bool finishing;
    readonly object drainGate = new();
    static readonly Stopwatch Clock = Stopwatch.StartNew();
    DateTime lastHeader = DateTime.UtcNow;

    public RecordingWriter(string path, int channels)
    {
        Path = path;
        Channels = channels;
        pending = Enumerable.Range(0, channels).Select(_ => new List<float>()).ToArray();
        levels = new float[channels];
        silentSeconds = new double[channels];
        acceptedFrames = new long[channels];
        frameEnergy = new float[channels];
        frameSamples = new int[channels];
        recent = Enumerable.Range(0, channels).Select(_ => new Queue<float>()).ToArray();
        lastReceived = Enumerable.Repeat(Clock.Elapsed.TotalSeconds, channels).ToArray();
        file = new WavWriter(path, channels);
    }

    public void Start()
    {
        lock (gate) Array.Fill(lastReceived, Clock.Elapsed.TotalSeconds);
        thread = new Thread(() =>
        {
            while (!finishing)
            {
                Thread.Sleep(100);
                if (!finishing) Drain(false);
            }
        }) { IsBackground = true, Name = "recording-writer", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    /// <summary>音訊執行緒呼叫</summary>
    public void Append(int channel, float[] samples)
    {
        if (channel >= Channels || samples.Length == 0) return;
        var sum = 0f;
        foreach (var s in samples) sum += s * s;
        var rms = MathF.Sqrt(sum / samples.Length);
        var now = Clock.Elapsed.TotalSeconds;
        lock (gate)
        {
            lastReceived[channel] = now;
            levels[channel] = rms;
            silentSeconds[channel] = rms == 0 ? silentSeconds[channel] + samples.Length / SampleRate : 0;
            if (paused) return;
            pending[channel].AddRange(samples);
            acceptedFrames[channel] += samples.Length;
            frameEnergy[channel] += sum;
            frameSamples[channel] += samples.Length;
            if (frameSamples[channel] >= (int)(SampleRate / 10))
            {
                var frameRms = MathF.Sqrt(frameEnergy[channel] / frameSamples[channel]);
                recent[channel].Enqueue(20 * MathF.Log10(MathF.Max(frameRms, 1e-6f)));
                while (recent[channel].Count > 300) recent[channel].Dequeue();
                frameEnergy[channel] = 0;
                frameSamples[channel] = 0;
            }
        }
    }

    public void SetPaused(bool value)
    {
        lock (gate)
        {
            paused = value;
            if (value) foreach (var p in pending) p.Clear();
        }
    }

    /// <summary>目前錄到的秒數（不含暫停），給計時、筆記和重點標記的時間點用</summary>
    public double RecordedSeconds { get { lock (gate) return acceptedFrames.Max() / SampleRate; } }

    /// <summary>0～1 的音量（RMS 換成 dB 再正規化）</summary>
    public float Level(int channel)
    {
        float rms;
        lock (gate) rms = channel < Channels ? levels[channel] : 0;
        if (rms <= 0) return 0;
        return Math.Clamp((20 * MathF.Log10(rms) + 60) / 60, 0, 1);
    }

    /// <summary>寫檔失敗了（例如磁碟滿了）：Recorder 看到就停止錄音（M§20.3）</summary>
    public bool WriteFailed { get { lock (gate) return failed; } }

    public double Silence(int channel) { lock (gate) return channel < Channels ? silentSeconds[channel] : 0; }

    /// <summary>最近 30 秒（不含暫停）每 100 毫秒的音量，dBFS（M§26.1）</summary>
    public float[] RecentLevels(int channel) { lock (gate) return channel < Channels ? [.. recent[channel]] : []; }

    /// <summary>暫停後重新開始：之前的音量不算（暫停前後可能換了地方）</summary>
    public void ResetRecentLevels()
    {
        lock (gate)
            for (var i = 0; i < Channels; i++) { recent[i].Clear(); frameEnergy[i] = 0; frameSamples[i] = 0; }
    }

    /// <summary>這個聲道多久沒有送音訊來（秒）。暫停時音源照樣送資料，所以暫停不影響</summary>
    public double SecondsSinceSamples(int channel)
    {
        var now = Clock.Elapsed.TotalSeconds;
        lock (gate) return channel < Channels ? now - lastReceived[channel] : 0;
    }

    /// <summary>停止並關檔，回傳寫進檔案的秒數（寫檔失敗時是失敗前寫進去的部分，看 WriteFailed）</summary>
    public double Finish()
    {
        finishing = true;
        thread?.Join();
        Drain(true);
        try { file.Dispose(); }
        catch (IOException) { lock (gate) failed = true; }
        return writtenFrames / SampleRate;
    }

    public void Dispose()
    {
        if (!finishing) Finish();
    }

    void Drain(bool final)
    {
        lock (drainGate)
        {
            float[][] frames;
            lock (gate)
            {
                if (Channels == 1)
                {
                    frames = [[.. pending[0]]];
                    pending[0].Clear();
                }
                else
                {
                    // 兩個音源：取兩邊都有的長度；一邊落後超過 0.5 秒（或收尾時），補靜音讓時間對齊
                    var count = pending.Min(p => p.Count);
                    var longest = pending.Max(p => p.Count);
                    if (final || longest - count > (int)(SampleRate / 2))
                    {
                        foreach (var p in pending) if (p.Count < longest) p.AddRange(new float[longest - p.Count]);
                        count = longest;
                    }
                    frames = pending.Select(p => p.GetRange(0, count).ToArray()).ToArray();
                    foreach (var p in pending) p.RemoveRange(0, count);
                }
            }
            var n = frames[0].Length;
            if (n == 0) return;
            try
            {
                if (!WriteFailed)
                {
                    file.Write(frames, n);
                    writtenFrames += n;
                    // 每 2 秒更新一次標頭：意外結束時檔案大小跟標頭差不多，WavRepair 也修得回來
                    if (DateTime.UtcNow - lastHeader > TimeSpan.FromSeconds(2))
                    {
                        lastHeader = DateTime.UtcNow;
                        file.Flush();
                    }
                }
            }
            catch (IOException e)
            {
                if (!WriteFailed) Log.Error($"recording write failed: {e.GetType().Name}");
                lock (gate) failed = true;
            }
            // 即時轉錄用的單聲道混音
            if (Channels == 1) OnMix?.Invoke(frames[0]);
            else
            {
                var mix = new float[n];
                for (var i = 0; i < n; i++) mix[i] = (frames[0][i] + frames[1][i]) * 0.5f;
                OnMix?.Invoke(mix);
            }
        }
    }
}
