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

    public bool IsLoaded => loaded is not null;
    public bool IsPlaying => player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
    public double CurrentTime => player.PlaybackSession.Position.TotalSeconds;
    public double Duration => player.PlaybackSession.NaturalDuration.TotalSeconds;
    public double Rate
    {
        get => player.PlaybackSession.PlaybackRate;
        set => player.PlaybackSession.PlaybackRate = value;
    }

    public event Action? StateChanged;

    public Player()
    {
        player.PlaybackSession.PlaybackStateChanged += (_, _) => StateChanged?.Invoke();
    }

    public void Load(string path)
    {
        if (loaded == path) return;
        var rate = player.PlaybackSession.PlaybackRate;
        player.Source = MediaSource.CreateFromUri(new Uri(path));
        loaded = path;
        player.PlaybackSession.PlaybackRate = rate <= 0 ? 1 : rate;
        StateChanged?.Invoke();
    }

    public void Unload()
    {
        player.Pause();
        player.Source = null;
        loaded = null;
        StateChanged?.Invoke();
    }

    public void Seek(double seconds, bool play = true)
    {
        if (!IsLoaded) return;
        player.PlaybackSession.Position = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (play) player.Play();
    }

    public void Toggle()
    {
        if (!IsLoaded) return;
        if (IsPlaying) player.Pause(); else player.Play();
    }

    public void Dispose() => player.Dispose();
}
