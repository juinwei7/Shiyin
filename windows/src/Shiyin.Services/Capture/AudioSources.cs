using System.Diagnostics;
using Shiyin.Localization;

namespace Shiyin.Services.Capture;

/// <summary>
/// 音源（M§5.1）：把收到的音訊轉成 16 kHz 單聲道後交給 OnSamples。callback 在音訊執行緒上，不能做重的事。
/// Windows 的麥克風、電腦聲音在 Platform（WASAPI，design-windows §6）
/// </summary>
public interface IAudioSource
{
    Action<float[]>? OnSamples { get; set; }
    /// <summary>音源發現裝置變了（M§20.2），任何執行緒都可能呼叫；Recorder 收到後會 Restart()</summary>
    Action? OnInterruption { get; set; }
    void Start();
    void Stop();
    void Restart()
    {
        Stop();
        Start();
    }
}

/// <summary>開始錄音的錯誤；AccessDenied 是 Windows 的麥克風權限被關掉（R27 R1.8）</summary>
public sealed class CaptureException(string message, bool accessDenied = false) : Exception(message)
{
    public bool AccessDenied { get; } = accessDenied;
}

/// <summary>
/// DEBUG 的模擬錄音（同 Mac 的 FileSource、record-sim）：用音訊檔當麥克風，0.1 秒送一段，speed 是倍速。
/// 模擬「麥克風＋電腦聲音」時同一段音訊在同一個迴圈裡也送給第二個聲道（兩個聲道各開一個的話，高倍速時兩邊的速度會不一樣）
/// </summary>
public sealed class FileSource(string path, double speed, IAudioDecoder decoder) : IAudioSource
{
    public Action<float[]>? OnSamples { get; set; }
    public Action? OnInterruption { get; set; }
    public MirroredSource? Mirror { get; set; }
    /// <summary>檔案送完了：之後沒有資料是正常的，不是裝置停了</summary>
    public Action? OnEnded { get; set; }

    readonly object gate = new();
    float[]? samples;
    int position;
    readonly Dictionary<int, double> stalledUntil = [];   // 聲道 → 恢復送資料的時間
    CancellationTokenSource? cancel;
    static readonly Stopwatch Clock = Stopwatch.StartNew();

    /// <summary>從上次停下的位置接著送，所以 Restart()（Stop 再 Start）等於繼續送</summary>
    public void Start()
    {
        var cts = new CancellationTokenSource();
        cancel = cts;
        // 專用的執行緒、照絕對時間排程（晚了就追上）：用執行緒池的 Task.Delay 時，轉錄把 CPU 吃滿會排不到，
        // 停住 3 秒被看門狗當成裝置停了（2026-10-21 在 4 核的虛擬機看到）
        var thread = new Thread(() =>
        {
            float[]? decoded;
            lock (gate) decoded = samples;
            decoded ??= decoder.Decode(path);
            lock (gate) samples = decoded;
            var chunk = 1600;   // 0.1 秒
            var clock = Stopwatch.StartNew();
            var sent = 0;
            while (!cts.IsCancellationRequested)
            {
                int start;
                HashSet<int> stalled;
                lock (gate)
                {
                    start = position;
                    var now = Clock.Elapsed.TotalSeconds;
                    stalled = stalledUntil.Where(p => p.Value > now).Select(p => p.Key).ToHashSet();
                }
                if (start >= decoded.Length)
                {
                    OnEnded?.Invoke();
                    return;
                }
                var end = Math.Min(decoded.Length, start + chunk);
                var piece = decoded[start..end];
                // 先記下位置再送：Restart() 時新的迴圈不會重送同一段
                lock (gate) position = end;
                // 停住的聲道照樣往前走（時間在過），只是沒有送出資料，跟真的裝置停了一樣
                if (!stalled.Contains(0)) OnSamples?.Invoke(piece);
                if (!stalled.Contains(1)) Mirror?.OnSamples?.Invoke(piece);
                sent += piece.Length;
                var wait = sent / 16000.0 / speed - clock.Elapsed.TotalSeconds;
                if (wait > 0 && cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(wait))) return;
            }
        }) { IsBackground = true, Name = "file-source", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    public void Stop()
    {
        cancel?.Cancel();
        cancel = null;
    }

    /// <summary>recordstall：這個聲道停止送資料幾秒（null 是馬上恢復）。Restart() 不會提早恢復，模擬裝置真的停了（M§20.2）</summary>
    public void Stall(int channel, double? seconds)
    {
        lock (gate)
        {
            if (seconds is double s) stalledUntil[channel] = Clock.Elapsed.TotalSeconds + s;
            else stalledUntil.Remove(channel);
        }
    }
}

/// <summary>只負責接收 FileSource 轉送的音訊，自己不產生資料</summary>
public sealed class MirroredSource : IAudioSource
{
    public Action<float[]>? OnSamples { get; set; }
    public Action? OnInterruption { get; set; }
    public void Start() { }
    public void Stop() { }
}

public static class CaptureErrors
{
    public static string NoInput => L.T("找不到可以用的麥克風");
}
