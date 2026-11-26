namespace Shiyin.Model;

/// <summary>
/// 麥克風時間軸放大後看的範圍（R119.10、M§93.5、D202）：Start～Start + Span 秒；整場是 null。
/// 同 Mac 的 MicZoom（標準答案 util/mic-zoom.json）
/// </summary>
public sealed record MicZoom(double Start, double Span)
{
    public double End => Start + Span;

    /// <summary>最大放大到看 10 秒</summary>
    public const double MinSpan = 10;
    /// <summary>每按一次 Ctrl＋＋／−、Ctrl＋滾輪每一格的倍數</summary>
    public const double KeyFactor = 2, WheelFactor = 1.25;

    public bool Contains(double t) => t >= Start && t <= End;

    /// <summary>放大 factor 倍（小於 1 是縮小），anchor（秒）在畫面上的位置不變；長度到整場時回傳 null</summary>
    public static MicZoom? Zoomed(MicZoom? current, double duration, double factor, double anchor)
    {
        if (!(duration > 0) || !(factor > 0)) return current;
        var view = current ?? new MicZoom(0, duration);
        var span = Math.Min(duration, Math.Max(Math.Min(MinSpan, duration), view.Span / factor));
        if (!(span < duration)) return null;
        var a = Math.Min(Math.Max(anchor, 0), duration);
        var ratio = view.Span > 0 ? (a - view.Start) / view.Span : 0;
        return new MicZoom(ClampStart(a - ratio * span, span, duration), span);
    }

    /// <summary>左右移動 seconds 秒（負的往前）</summary>
    public MicZoom Panned(double duration, double seconds) => new(ClampStart(Start + seconds, Span, duration), Span);

    /// <summary>時間在範圍外時，移到讓它在左邊 10% 的位置；在範圍裡不動</summary>
    public MicZoom Following(double duration, double time) => Contains(time) ? this : new(ClampStart(time - Span * 0.1, Span, duration), Span);

    static double ClampStart(double start, double span, double duration) => Math.Min(Math.Max(0, start), Math.Max(0, duration - span));
}
