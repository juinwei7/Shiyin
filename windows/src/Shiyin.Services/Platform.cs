namespace Shiyin.Services;

/// <summary>錄音壓縮成 m4a（M§6.4；Windows 是 Media Foundation 的 AAC 編碼器，design-windows §13）</summary>
public interface IAudioCompressor
{
    Task Compress(string wavPath, string m4aPath, CancellationToken cancel);
}

/// <summary>錄音、轉錄、整理時不讓系統睡眠（R1.9；Windows 是 PowerCreateRequest，design-windows §6）</summary>
public interface IPowerRequests
{
    IDisposable KeepAwake(string reason);
}

/// <summary>沒有平台功能時（測試、評估）</summary>
public sealed class NoPower : IPowerRequests
{
    public static readonly NoPower Instance = new();
    public IDisposable KeepAwake(string reason) => new Nothing();
    sealed class Nothing : IDisposable { public void Dispose() { } }
}
