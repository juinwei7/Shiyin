using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Shiyin.Localization;
using Shiyin.Model;
using Shiyin.Services;

namespace Shiyin.App;

/// <summary>
/// 系統通知（R11.2、R19.4、design-windows §12.2）：完成、失敗、偵測會議（有按鈕）。點了用參數打開那場會議。
/// 通知被關掉時，偵測會議改用不搶焦點的小視窗（App 決定）
/// </summary>
public sealed class Notifier
{
    readonly AppHost host;
    bool registered;

    /// <summary>短暫的提示（畫面下方，同 Mac 的 NoticeToast）</summary>
    public event Action<string>? Toast;

    public Notifier(AppHost host)
    {
        this.host = host;
        try
        {
            // 先掛事件再 Register（AppNotification 的規定）
            AppNotificationManager.Default.NotificationInvoked += (_, args) => host.Post(() => Invoked(args.Arguments));
            AppNotificationManager.Default.Register();
            registered = true;
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            Log.Error($"notifications unavailable: 0x{e.HResult:X8}");
        }
    }

    public bool Enabled => registered && AppNotificationManager.Default.Setting == AppNotificationSetting.Enabled;

    public void Notice(string message) => Toast?.Invoke(message);

    void Show(AppNotificationBuilder builder)
    {
        if (!Enabled) return;
        try { AppNotificationManager.Default.Show(builder.BuildNotification()); }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException) { Log.Error($"notification failed: 0x{e.HResult:X8}"); }
    }

    public void JobFinished(Job job, Exception? error)
    {
        if (host.Db.Meeting(job.MeetingId) is not { } meeting) return;
        var title = meeting.DisplayTitle;
        var builder = new AppNotificationBuilder().AddArgument("meeting", job.MeetingId.ToString());
        if (error is not null)
        {
            var message = error.Message.Length > 60 ? error.Message[..60] : error.Message;
            builder.AddText(job.Kind == JobKind.Transcribe ? L.T("轉錄失敗") : L.T("整理失敗")).AddText(L.F("%@：%@", title, message));
        }
        else if (job.Kind == JobKind.Transcribe) builder.AddText(L.T("轉錄完成")).AddText(title);
        else if (job.Kind == JobKind.Summarize && job.Payload?.Contains("\"draft\":true") == true)
            builder.AddText(L.T("會議紀錄草稿好了")).AddText(L.F("%@（正式轉錄完成後會自動更新）", title));
        else
        {
            var count = meeting.DecodedNotes?.ReviewItems.Count ?? 0;
            builder.AddText(L.T("會議紀錄整理好了")).AddText(count > 0 ? L.F("%@，有 %lld 題待確認", title, count) : title);
        }
        Show(builder);
    }

    /// <summary>偵測到會議（M§21.7）：開始錄音、不要再提醒這個 app；停止並轉錄、繼續錄</summary>
    public bool MeetingPrompt(MeetingDetector.Prompt prompt)
    {
        if (!Enabled) return false;
        var builder = new AppNotificationBuilder().AddArgument("app", prompt.App.Id);
        if (prompt is MeetingDetector.Prompt.Start)
            builder.AddText(L.F("%@ 正在開會", prompt.App.Name)).AddText(L.T("要開始錄音嗎？"))
                .AddButton(new AppNotificationButton(L.T("開始錄音")).AddArgument("action", "start").AddArgument("app", prompt.App.Id))
                .AddButton(new AppNotificationButton(L.T("不要再提醒這個 app")).AddArgument("action", "mute").AddArgument("app", prompt.App.Id));
        else
            builder.AddText(L.T("會議好像結束了")).AddText(L.T("要停止錄音並轉錄嗎？"))
                .AddButton(new AppNotificationButton(L.T("停止並轉錄")).AddArgument("action", "stop").AddArgument("app", prompt.App.Id))
                .AddButton(new AppNotificationButton(L.T("繼續錄")).AddArgument("action", "continue").AddArgument("app", prompt.App.Id));
        Show(builder);
        return true;
    }

    void Invoked(IDictionary<string, string> arguments)
    {
        if (arguments.TryGetValue("action", out var action))
        {
            var app = arguments.TryGetValue("app", out var a) ? a : "";
            switch (action)
            {
                case "start": host.Controller.StartRecording(""); break;
                case "mute": host.Detector.Mute(app); break;
                case "stop": host.Recorder.Stop(); break;
            }
            return;
        }
        if (arguments.TryGetValue("meeting", out var id) && long.TryParse(id, out var meetingId)) host.ShowMain?.Invoke(meetingId);
        else host.ShowMain?.Invoke(null);
    }

    /// <summary>解除安裝時（Velopack 的 hook）清掉通知的登記</summary>
    public static void Unregister()
    {
        try { AppNotificationManager.Default.UnregisterAll(); } catch (Exception) { }
    }
}
