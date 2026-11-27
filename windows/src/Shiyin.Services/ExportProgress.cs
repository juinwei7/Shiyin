using Shiyin.Localization;

namespace Shiyin.Services;

/// <summary>
/// 側欄下方「正在匯出」那一條的狀態（R120、design-windows §11.53），畫面在 MainWindow，這裡只管規則（測得到）：
/// - 同時只有一個匯出：還有一個在跑時 TryStart 回傳 null，匯出對話框先說明、不開始
/// - 每一次有自己的編號：上一次（取消了、做完了）晚到的進度、結束不會動到這一次
/// - 百分比只往前；變了 1% 以上、或離上次超過 0.2 秒才更新畫面
/// - 按了「取消」：取消匯出、字換成「正在取消…」，之後的進度不算，等匯出真的停下來（End）才拿掉
/// 都在 UI 執行緒呼叫（背景的進度由 MainWindow 排回 UI 執行緒）
/// </summary>
public sealed class ExportProgress(Func<DateTime>? clock = null)
{
    public sealed class Run(long id, string name, CancellationTokenSource cancel)
    {
        public long Id { get; } = id;
        /// <summary>檔名（「正在匯出 週會.m4a… 45%」）</summary>
        public string Name { get; } = name;
        public CancellationTokenSource Cancel { get; } = cancel;
        /// <summary>完成的比例；還沒回報過是 null（不定進度）</summary>
        public double? Value { get; internal set; }
        public bool Cancelling { get; internal set; }
        internal DateTime Updated = DateTime.MinValue;
    }

    readonly Func<DateTime> now = clock ?? (() => DateTime.UtcNow);
    long next;

    public Run? Current { get; private set; }

    /// <summary>開始一次匯出；已經有一個在跑時回傳 null</summary>
    public Run? TryStart(string name, CancellationTokenSource cancel)
    {
        if (Current is not null) return null;
        return Current = new Run(++next, name, cancel);
    }

    /// <summary>回報進度；畫面要更新時回傳 true（是目前這一次、沒在取消、變得夠多或夠久）</summary>
    public bool Report(long id, double value)
    {
        if (Current is not { } run || run.Id != id || run.Cancelling) return false;
        var v = Math.Clamp(Math.Max(run.Value ?? 0, value), 0, 1);
        var time = now();
        if (run.Value is double old && v - old < 0.01 && (time - run.Updated).TotalSeconds < 0.2) return false;
        run.Value = v;
        run.Updated = time;
        return true;
    }

    /// <summary>按了「取消」：取消匯出；畫面要更新時回傳 true</summary>
    public bool Cancel(long id)
    {
        if (Current is not { } run || run.Id != id || run.Cancelling) return false;
        run.Cancelling = true;
        run.Cancel.Cancel();
        return true;
    }

    /// <summary>匯出停下來了（做完、取消、失敗）：是目前這一次才拿掉，回傳 true</summary>
    public bool End(long id)
    {
        if (Current?.Id != id) return false;
        Current = null;
        return true;
    }

    /// <summary>那一條的字：「正在匯出 週會.m4a…」「正在匯出 週會.m4a… 45%」「正在取消…」</summary>
    public static string Status(Run run) =>
        run.Cancelling ? L.T("正在取消…")
        : run.Value is double v ? L.F("正在匯出 %@… %lld%%", run.Name, (long)Math.Floor(v * 100))
        : L.F("正在匯出 %@…", run.Name);
}
