using Windows.Media.Core;
using Windows.Media.Playback;

namespace Shiyin.App;

/// <summary>
/// 播放錄音（R8.2、design-windows §2）：Windows.Media.Playback.MediaPlayer，倍速用 PlaybackRate（變速時保持音高待 T-W9 確認）。
/// 畫面讀 CurrentTime 自己定時更新，不用事件（播放中每秒變好幾次，M§25）
/// </summary>
public sealed class Player : IDisposable
{
    readonly MediaPlayer player = new() { AutoPlay = false };
    string? loaded;
    /// <summary>「麥克風＋電腦聲音」的錄音混成單聲道播（R73.1、design-windows §11.16）</summary>
    Platform.MixedAudioSource? mixed;

    public bool IsLoaded => loaded is not null;
    public bool IsMixing => mixed is not null;
    public bool IsPlaying => Get(() => player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing, false);
    public double CurrentTime => Get(() => player.PlaybackSession.Position.TotalSeconds, 0);
    public double Duration => Get(() => player.PlaybackSession.NaturalDuration.TotalSeconds, 0);
    public double Rate
    {
        get => Get(() => player.PlaybackSession.PlaybackRate, 1);
        set => Do(() => player.PlaybackSession.PlaybackRate = value);
    }

    public event Action? StateChanged;

    public Player()
    {
        player.PlaybackSession.PlaybackStateChanged += (_, _) => StateChanged?.Invoke();
        // 混音播不出來：改回直接播原檔（兩耳分開，至少聽得到）
        player.MediaFailed += (_, e) =>
        {
            if (mixed is null || loaded is not { } path) return;
            Services.Log.Error($"player: mixed source failed {e.Error} 0x{e.ExtendedErrorCode?.HResult:X8}");
            DisposeMixed();
            Do(() => player.Source = MediaSource.CreateFromUri(new Uri(path)));
            StateChanged?.Invoke();
        };
    }

    /// <summary>mixChannels：「麥克風＋電腦聲音」的錄音（左右聲道是不同的音源），兩個聲道混在一起播</summary>
    public void Load(string path, bool mixChannels = false)
    {
        if (loaded == path) return;
        var rate = Rate;
        DisposeMixed();
        loaded = path;
        if (mixChannels)
        {
            try { mixed = new Platform.MixedAudioSource(path); }
            catch (Exception e) when (e is IOException or System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException or FormatException)
            {
                Services.Log.Error($"player: can't mix {e.GetType().Name}");
            }
        }
        var source = mixed is { } m ? MediaSource.CreateFromMediaStreamSource(m.Source) : MediaSource.CreateFromUri(new Uri(path));
        Do(() => player.Source = source);
        Rate = rate <= 0 ? 1 : rate;
        StateChanged?.Invoke();
    }

    public void Unload()
    {
        Do(player.Pause);
        Do(() => player.Source = null);
        DisposeMixed();
        loaded = null;
        StateChanged?.Invoke();
    }

    void DisposeMixed()
    {
        mixed?.Dispose();
        mixed = null;
    }

    public void Seek(double seconds, bool play = true)
    {
        if (!IsLoaded) return;
        Do(() => player.PlaybackSession.Position = TimeSpan.FromSeconds(Math.Max(0, seconds)));
        if (play) Do(player.Play);
    }

    public void Toggle()
    {
        if (!IsLoaded) return;
        if (IsPlaying) Do(player.Pause); else Do(player.Play);
    }

    public void Dispose()
    {
        player.Dispose();
        DisposeMixed();
    }

    // 沒有音訊輸出裝置（沒接喇叭的桌機、裝置被停用）時 MediaPlayer 的操作會丟出 0x800F1000「並未偵測出安裝元件」：
    // 播不出聲音就算了，不能讓 app 當掉（2026-10-21 在沒有音效卡的虛擬機看到）
    static void Do(Action action)
    {
        try { action(); }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            Services.Log.Error($"player: {e.GetType().Name} 0x{e.HResult:X8}");
        }
    }

    static T Get<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException) { return fallback; }
    }
}
