using System.Security;
using System.Text;
using Shiyin.Localization;
using Shiyin.Model;
using Shiyin.Platform;
using Shiyin.Services;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using Shiyin.Services.Capture;

namespace Shiyin.App;

/// <summary>
/// 系統通知（R11.2、R19.4、design-windows §12.2、D62）：完成、失敗、偵測會議（有按鈕）。
/// 用 Windows 本身的 ToastNotificationManager；點通知、按按鈕都是 shiyin: 網址，第二個拾音把網址轉過來（App.Activated）。
/// 通知被關掉時，偵測會議改用不搶焦點的小視窗（App 決定）
/// </summary>
public sealed class Notifier
{
    readonly AppHost host;
    readonly ToastNotifier? notifier;

    public NotificationLinks Links { get; } = new();

    /// <summary>短暫的提示（畫面下方，同 Mac 的 NoticeToast）</summary>
    public event Action<string>? Toast;

    public Notifier(AppHost host)
    {
        this.host = host;
        try
        {
            ToastRegistration.Register(L.T("拾音"), Path.Combine(AppContext.BaseDirectory, "Assets", "Shiyin.png"));
            notifier = ToastNotificationManager.CreateToastNotifier(ToastRegistration.AppId);
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
            Log.Error($"notifications unavailable: 0x{e.HResult:X8}");
        }
    }

    /// <summary>
    /// 通知有沒有被關掉。用登錄的 AppUserModelID 時讀 ToastNotifier.Setting 會丟出例外（2026-10-21 在虛擬機看到），
    /// 改看 Windows 存使用者設定的登錄：全部的通知關掉、或只關掉拾音
    /// </summary>
    public bool Enabled
    {
        get
        {
            if (notifier is null) return false;
            try { return notifier.Setting == NotificationSetting.Enabled; }
            catch (Exception e) when (e is System.Runtime.InteropServices.COMException or ArgumentException) { return ToastRegistration.EnabledInSettings(); }
        }
    }

    public void Notice(string message) => Toast?.Invoke(message);

    /// <summary>驗證用：最近一次要發的通知（通知被關掉時也記）</summary>
    public (string Title, string Body)? LastShown { get; private set; }

    /// <summary>收音提醒（R42.3、M§31.5）：內容跟錄音畫面同一句，點了打開錄音中的會議</summary>
    public void LevelHint(Recorder.LevelHint hint, long meetingId) => Show(NotificationLinks.OpenMeeting(meetingId), L.T("收音提醒"), hint.Text());

    void Show(string launch, string title, string body, params (string Label, string Url)[] buttons)
    {
        LastShown = (title, body);
        if (!Enabled) return;
        var xml = new StringBuilder();
        xml.Append($"<toast launch=\"{SecurityElement.Escape(launch)}\" activationType=\"protocol\"><visual><binding template=\"ToastGeneric\">");
        xml.Append($"<text>{SecurityElement.Escape(title)}</text><text>{SecurityElement.Escape(body)}</text></binding></visual>");
        if (buttons.Length > 0)
        {
            xml.Append("<actions>");
            foreach (var (label, url) in buttons)
                xml.Append($"<action content=\"{SecurityElement.Escape(label)}\" activationType=\"protocol\" arguments=\"{SecurityElement.Escape(url)}\"/>");
            xml.Append("</actions>");
        }
        xml.Append("</toast>");
        try
        {
            var document = new XmlDocument();
            document.LoadXml(xml.ToString());
            notifier!.Show(new ToastNotification(document));
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or ArgumentException)
        {
            Log.Error($"notification failed: 0x{e.HResult:X8}");
        }
    }

    public void JobFinished(Job job, Exception? error)
    {
        if (host.Db.Meeting(job.MeetingId) is not { } meeting) return;
        var title = meeting.DisplayTitle;
        var launch = NotificationLinks.OpenMeeting(job.MeetingId);
        if (error is not null)
        {
            var message = error.Message.Length > 60 ? error.Message[..60] : error.Message;
            Show(launch, job.Kind == JobKind.Transcribe ? L.T("轉錄失敗") : L.T("整理失敗"), L.F("%@：%@", title, message));
        }
        else if (job.Kind == JobKind.Transcribe) Show(launch, L.T("轉錄完成"), title);
        else if (job.Kind == JobKind.Summarize && job.Payload?.Contains("\"draft\":true") == true)
            Show(launch, L.T("會議紀錄草稿好了"), L.F("%@（正式轉錄完成後會自動更新）", title));
        else
        {
            var count = meeting.DecodedNotes?.ReviewItems.Count ?? 0;
            Show(launch, L.T("會議紀錄整理好了"), count > 0 ? L.F("%@，有 %lld 題待確認", title, count) : title);
        }
    }

    /// <summary>錄音中很久沒人說話（R48.1）：停止錄音、繼續錄；點通知本身打開錄音中的會議</summary>
    public bool IdlePrompt(long meetingId)
    {
        if (!Enabled) return false;
        Show(NotificationLinks.OpenMeeting(meetingId), L.T("好像沒人在說話了"), L.T("要停止錄音嗎？再 15 分鐘沒人說話會自動停止。"),
            (L.T("停止錄音"), Links.Prompt("stop", "idle")), (L.T("繼續錄"), Links.Prompt("continue", "idle")));
        return true;
    }

    /// <summary>沒人說話太久，自動停止了（R48.2）：點了打開那場會議</summary>
    public void IdleStopped(long meetingId, string title) =>
        Show(NotificationLinks.OpenMeeting(meetingId), L.T("已自動停止錄音"), L.F("%@：30 分鐘沒有人說話", title));

    /// <summary>偵測到會議（M§21.7）：開始錄音、不要再提醒這個 app；停止並轉錄、繼續錄</summary>
    public bool MeetingPrompt(MeetingDetector.Prompt prompt)
    {
        if (!Enabled) return false;
        var app = prompt.App.Id;
        if (prompt is MeetingDetector.Prompt.Start)
            Show(NotificationLinks.Scheme + ":open", L.F("%@ 正在開會", prompt.App.Name), L.T("要開始錄音嗎？"),
                (L.T("開始錄音"), Links.Prompt("start", app)), (L.T("不要再提醒這個 app"), Links.Prompt("mute", app)));
        else
            Show(NotificationLinks.Scheme + ":open", L.T("會議好像結束了"), L.T("要停止錄音並轉錄嗎？"),
                (L.T("停止並轉錄"), Links.Prompt("stop", app)), (L.T("繼續錄"), Links.Prompt("continue", app)));
        return true;
    }

    /// <summary>點了通知或按鈕（第二個拾音轉過來的 shiyin: 網址，或拾音沒開時啟動參數裡的網址）</summary>
    public void Open(string url)
    {
        switch (Links.Parse(url))
        {
            case NotificationLinks.Link.Prompt { Action: "start" }: host.Controller.StartRecording(""); break;
            case NotificationLinks.Link.Prompt { Action: "mute", App: var app }: host.Detector.Mute(app); break;
            case NotificationLinks.Link.Prompt { Action: "stop" }: host.Recorder.Stop(); break;
            case NotificationLinks.Link.Prompt { Action: "continue", App: "idle" }: host.Recorder.ContinueAfterIdle(); break;   // 忘了停止錄音（R48.1）
            case NotificationLinks.Link.Prompt: break;   // 繼續錄
            case NotificationLinks.Link.Open { MeetingId: var id }: host.ShowMain?.Invoke(id); break;
        }
    }

    /// <summary>解除安裝時（Velopack 的 hook）清掉通知和登錄</summary>
    public static void Unregister()
    {
        try { ToastNotificationManager.History.Clear(ToastRegistration.AppId); } catch (Exception) { }
        ToastRegistration.Unregister();
    }
}
