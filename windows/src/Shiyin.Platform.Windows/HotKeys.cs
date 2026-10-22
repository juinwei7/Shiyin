using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Shiyin.Localization;

namespace Shiyin.Platform;

/// <summary>
/// 全域快捷鍵（R11.3、R19.5、design-windows §12.3）：Ctrl+Alt+Shift+R 開始或停止錄音、Ctrl+Alt+Shift+B 標記重點（只在錄音中註冊）。
/// RegisterHotKey 綁在呼叫的執行緒上：開一條有訊息迴圈的執行緒，註冊、取消都在那條執行緒做。被別的 app 佔走時回傳原因
/// </summary>
public sealed class GlobalHotKeys : IDisposable
{
    const int RecordId = 1, BookmarkId = 2;
    const uint VkR = 0x52, VkB = 0x42;
    const uint Run = Native.WM_APP + 1, Quit = Native.WM_APP + 2;

    readonly Thread thread;
    uint threadId;
    readonly ManualResetEventSlim started = new();
    readonly ConcurrentQueue<(Func<string?> Work, TaskCompletionSource<string?> Done)> work = new();

    public event Action? RecordPressed;
    public event Action? BookmarkPressed;

    public GlobalHotKeys()
    {
        thread = new Thread(Loop) { IsBackground = true, Name = "hotkeys" };
        thread.Start();
        started.Wait();
    }

    void Loop()
    {
        threadId = Native.GetCurrentThreadId();
        // 先拿一次訊息讓這條執行緒建立訊息佇列，PostThreadMessage 才送得到
        started.Set();
        while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == Native.WM_HOTKEY)
            {
                if (msg.wParam == RecordId) RecordPressed?.Invoke();
                else if (msg.wParam == BookmarkId) BookmarkPressed?.Invoke();
            }
            else if (msg.message == Run)
            {
                while (work.TryDequeue(out var item))
                    try { item.Done.SetResult(item.Work()); } catch (Exception e) { item.Done.SetException(e); }
            }
            else if (msg.message == Quit) break;
        }
        Native.UnregisterHotKey(IntPtr.Zero, RecordId);
        Native.UnregisterHotKey(IntPtr.Zero, BookmarkId);
    }

    string? OnThread(Func<string?> f)
    {
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        work.Enqueue((f, done));
        // 訊息佇列可能還沒建好，重試幾次
        for (var i = 0; i < 50 && !Native.PostThreadMessage(threadId, Run, 0, 0); i++) Thread.Sleep(20);
        return done.Task.Wait(TimeSpan.FromSeconds(2)) ? done.Task.Result : L.T("快捷鍵沒有回應");
    }

    static string? Register(int id, uint vk, bool on)
    {
        Native.UnregisterHotKey(IntPtr.Zero, id);
        if (!on) return null;
        if (Native.RegisterHotKey(IntPtr.Zero, id, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_SHIFT | Native.MOD_NOREPEAT, vk)) return null;
        var error = Marshal.GetLastPInvokeError();
        return error == Native.ERROR_HOTKEY_ALREADY_REGISTERED
            ? L.T("這組快捷鍵已經被別的 app 用了")
            : L.F("沒辦法註冊快捷鍵（錯誤代碼 %lld）", error);
    }

    /// <summary>開始或停止錄音：回傳 null 表示成功，否則是原因（設定頁顯示）</summary>
    public string? SetRecordEnabled(bool on) => OnThread(() => Register(RecordId, VkR, on));
    public string? SetBookmarkEnabled(bool on) => OnThread(() => Register(BookmarkId, VkB, on));

    /// <summary>DEBUG 的 hotkey：模擬按下（不能用送按鍵測，CLAUDE.md）</summary>
    public void Simulate(string which)
    {
        if (which == "bookmark") BookmarkPressed?.Invoke(); else RecordPressed?.Invoke();
    }

    public void Dispose()
    {
        Native.PostThreadMessage(threadId, Quit, 0, 0);
        thread.Join(TimeSpan.FromSeconds(2));
    }
}
