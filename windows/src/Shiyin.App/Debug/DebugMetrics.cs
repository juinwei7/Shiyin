#if DEBUG
using System.Diagnostics;
using Microsoft.UI.Dispatching;

namespace Shiyin.App.Debug;

/// <summary>
/// 只在 DEBUG：驗證效能用的計數（同 Mac 的 DebugMetrics，M§21.12）。`shiyinctl metrics [reset]` 讀出或歸零
/// </summary>
public static class DebugMetrics
{
    /// <summary>UI 執行緒最長卡住多久：每 10 毫秒一次的計時器，兩次之間隔了多久</summary>
    public static double MaxMainGapMs { get; private set; }
    static readonly Stopwatch clock = Stopwatch.StartNew();
    static double lastTick;
    static DispatcherQueueTimer? timer;

    public static void Start(DispatcherQueue queue)
    {
        if (timer is not null) return;
        timer = queue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(10);
        timer.IsRepeating = true;
        lastTick = clock.Elapsed.TotalMilliseconds;
        timer.Tick += (_, _) =>
        {
            var now = clock.Elapsed.TotalMilliseconds;
            MaxMainGapMs = Math.Max(MaxMainGapMs, now - lastTick);
            lastTick = now;
        };
        timer.Start();
    }

    public static void Reset()
    {
        MaxMainGapMs = 0;
        lastTick = clock.Elapsed.TotalMilliseconds;
    }
}
#endif
