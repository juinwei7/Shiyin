namespace Shiyin.Services;

/// <summary>
/// 會議結束時倒數自動停止錄音（R95、M§69、design-windows §11.28；同 Mac 的 AppModel.startMeetingEndCountdown）：
/// 偵測到會議 app 停止用麥克風（MeetingDetector 的 Stop 提醒）、設定開著時開始倒數，每秒看一次：
/// 錄音停了或換了一場（按了停止、別的原因）就收起；時間到還在錄同一場、會議 app 沒有又開始用麥克風，就停止並轉錄。
/// 「繼續錄」「停止並轉錄」（島、小視窗、通知的按鈕）都取消倒數
/// </summary>
public sealed class MeetingEndCountdown(Func<long?> recordingMeeting, Func<bool> meetingAppActive, Action<long> stop)
{
    public const int DefaultSeconds = 60;

    readonly object gate = new();
    Timer? timer;
    long? meetingId;
    DateTime? deadline;

    /// <summary>倒數幾秒（R95 不讓使用者改；DEBUG 的 --set meetingEndCountdownSeconds 改短）</summary>
    public int Seconds { get; set; } = DefaultSeconds;

    /// <summary>倒數到什麼時候（UTC）；沒在倒數是 null</summary>
    public DateTime? Deadline { get { lock (gate) return deadline; } }
    public bool Active => Deadline is not null;

    /// <summary>倒數收起了（時間到、錄音先停了）：島、小視窗上的提醒跟著收起。在計時器的執行緒觸發</summary>
    public event Action? Ended;

    /// <summary>剩幾秒（無條件進位，同 Mac 的 TimelineView）；沒在倒數是 -1</summary>
    public int Remaining(DateTime now) =>
        Deadline is DateTime d ? Math.Max(0, (int)Math.Ceiling((d - now).TotalSeconds)) : -1;

    /// <summary>開始倒數（錄音中才有作用）；已經在倒數時重新計時</summary>
    public void Start(DateTime? now = null)
    {
        lock (gate)
        {
            if (recordingMeeting() is not long id) return;
            meetingId = id;
            deadline = (now ?? DateTime.UtcNow).AddSeconds(Seconds);
            timer ??= new Timer(_ => Tick(DateTime.UtcNow), null, 1000, 1000);
        }
        Log.Info($"meeting ended, auto-stopping the recording in {Seconds}s");
    }

    /// <summary>「繼續錄」「停止並轉錄」：取消倒數（不觸發 Ended，按的那個提醒自己會收起）</summary>
    public void Cancel()
    {
        lock (gate) Close();
    }

    void Close()
    {
        deadline = null;
        meetingId = null;
        timer?.Dispose();
        timer = null;
    }

    /// <summary>每秒一次（測試直接呼叫）</summary>
    public void Tick(DateTime now)
    {
        long? stopping = null;
        lock (gate)
        {
            if (deadline is not DateTime d || meetingId is not long id) return;
            // 錄音已經停了（按了停止、別的原因）或換了一場：收起倒數
            if (recordingMeeting() != id) Close();
            else if (now >= d)
            {
                Close();
                // 時間到時會議 app 又開始用麥克風（會議其實還在）：不停
                if (!meetingAppActive()) stopping = id;
            }
            else return;
        }
        Ended?.Invoke();
        if (stopping is long target)
        {
            Log.Info("meeting ended, auto-stopping the recording");
            stop(target);
        }
    }
}
