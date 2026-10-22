using System.Net;
using System.Net.Sockets;

namespace Shiyin.Tests;

/// <summary>本機的 HTTP 伺服器：模型下載、整理工具的假伺服器</summary>
sealed class TestServer : IDisposable
{
    readonly HttpListener listener = new();
    public string Url { get; }
    public Func<HttpListenerContext, Task> Handle { get; set; } = c => { c.Response.StatusCode = 404; c.Response.Close(); return Task.CompletedTask; };

    public TestServer()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        Url = $"http://127.0.0.1:{port}";
        listener.Prefixes.Add(Url + "/");
        listener.Start();
        _ = Loop();
    }

    async Task Loop()
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); } catch { return; }
            _ = Task.Run(async () =>
            {
                try { await Handle(context); }
                catch { try { context.Response.Abort(); } catch { } }
            });
        }
    }

    public void Dispose() => listener.Stop();
}

/// <summary>測試用的 SHIYIN_HOME：暫存資料夾，測完刪掉（不碰使用者的資料）</summary>
sealed class TempHome : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "shiyin-test-" + Guid.NewGuid().ToString("N"));
    public TempHome() => Directory.CreateDirectory(Path);
    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}
