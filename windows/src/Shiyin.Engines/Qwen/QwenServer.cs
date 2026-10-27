using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Shiyin.Whisper;

namespace Shiyin.Qwen;

/// <summary>
/// llama.cpp 官方的 llama-server 當子程序（design-windows §8.4、D95）：每次轉錄啟動一個、轉完結束。
/// HTTP 在 127.0.0.1 的隨機埠，逐段送 /v1/chat/completions（system = 詞彙表、user = 音訊、assistant 先填語言前綴）。
/// Windows 用 Job Object 綁住子程序，拾音當掉子程序也跟著結束
/// </summary>
public sealed class QwenServer : IQwenDecoder, IDisposable
{
    /// <summary>
    /// 啟動 llama-server 的參數：執行檔、兩個模型檔、執行緒數、要不要把模型放進 GPU、子程序 PATH 要多加的資料夾（cudart、cublas 所在）、
    /// 用第幾張 NVIDIA 顯示卡（nvidia-smi 的編號，企業伺服器分配，M§36.6、D125；null 不限制）
    /// </summary>
    public sealed record Options(string Executable, string ModelPath, string EncoderPath, int Threads, bool Gpu, string? ExtraPath = null, int? Device = null);

    public static int DefaultThreads => Math.Clamp(Environment.ProcessorCount - 2, 2, 8);

    readonly Process process;
    readonly HttpClient http;
    readonly IntPtr job;
    readonly Queue<string> stderr = new();
    readonly object stderrGate = new();
    public string Url { get; }

    QwenServer(Process process, HttpClient http, IntPtr job, string url)
    {
        this.process = process;
        this.http = http;
        this.job = job;
        Url = url;
    }

    /// <summary>啟動並等模型載入好（/health 回 200）；失敗丟 WhisperException（LoadFailed），stderr 的最後幾行寫進 log</summary>
    public static QwenServer Start(Options options, Action<string>? log = null, CancellationToken cancel = default)
    {
        if (!File.Exists(options.Executable) || !File.Exists(options.ModelPath) || !File.Exists(options.EncoderPath))
            throw new WhisperException(WhisperErrorKind.ModelMissing);
        var port = FreePort();
        var info = new ProcessStartInfo(options.Executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(options.Executable)!,
        };
        foreach (var a in new[]
                 {
                     "-m", options.ModelPath, "--mmproj", options.EncoderPath, "-c", QwenRulesContext, "-b", "2048", "-ub", "512",
                     "-t", options.Threads.ToString(), "-ngl", options.Gpu ? "999" : "0", "--host", "127.0.0.1", "--port", port.ToString(),
                     "-np", "1", "--no-webui", "--no-warmup", "--jinja",
                 })
            info.ArgumentList.Add(a);
        if (options.ExtraPath is { } extra)
            info.Environment["PATH"] = extra + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? "");
        // 只看得到分到的那一張；CUDA 預設把最快的卡排第一，照 PCI 匯流排排才跟 nvidia-smi 的編號一樣
        if (options.Device is { } device)
        {
            info.Environment["CUDA_VISIBLE_DEVICES"] = device.ToString(System.Globalization.CultureInfo.InvariantCulture);
            info.Environment["CUDA_DEVICE_ORDER"] = "PCI_BUS_ID";
        }
        var process = new Process { StartInfo = info };
        var url = $"http://127.0.0.1:{port}";
        var http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromMinutes(30) };
        var server = new QwenServer(process, http, CreateJob(), url);
        try
        {
            process.Start();
            server.Attach();
            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, e) => server.Remember(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            server.WaitReady(log, cancel);
            return server;
        }
        catch
        {
            server.Dispose();
            throw;
        }
    }

    static readonly string QwenRulesContext = Transcribe.QwenRules.ContextTokens.ToString();

    void Remember(string? line)
    {
        if (line is null) return;
        lock (stderrGate)
        {
            stderr.Enqueue(line);
            while (stderr.Count > 30) stderr.Dequeue();
        }
    }

    string StderrTail()
    {
        lock (stderrGate) return string.Join("\n", stderr);
    }

    void WaitReady(Action<string>? log, CancellationToken cancel)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(5);
        while (DateTime.UtcNow < deadline)
        {
            cancel.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                log?.Invoke($"llama-server exited with {process.ExitCode}: {StderrTail()}");
                throw new WhisperException(WhisperErrorKind.LoadFailed);
            }
            try
            {
                using var response = http.Send(new HttpRequestMessage(HttpMethod.Get, "/health"), cancel);
                if (response.StatusCode == HttpStatusCode.OK) return;
            }
            catch (HttpRequestException) { }
            Thread.Sleep(250);
        }
        log?.Invoke($"llama-server not ready in time: {StderrTail()}");
        throw new WhisperException(WhisperErrorKind.LoadFailed);
    }

    static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>一段音訊（16 kHz 單聲道）轉成文字；回傳的文字還沒整理（QwenTranscriber 會 Clean）</summary>
    public QwenDecoded Decode(ReadOnlySpan<float> samples, string context, string languageName, int maxTokens, QwenSampling sampling, CancellationToken cancel)
    {
        var prefill = Transcribe.QwenRules.Prefill(languageName);
        var body = Request(samples, context, prefill, maxTokens, sampling);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new ByteArrayContent(body) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } },
        };
        HttpResponseMessage response;
        try
        {
            response = http.Send(request, HttpCompletionOption.ResponseContentRead, cancel);
        }
        catch (HttpRequestException)
        {
            throw new WhisperException(WhisperErrorKind.QwenFailed);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode) throw new WhisperException(WhisperErrorKind.QwenFailed, (int)response.StatusCode);
            using var json = JsonDocument.Parse(response.Content.ReadAsStream(cancel));
            var choice = json.RootElement.GetProperty("choices")[0];
            var content = choice.GetProperty("message").GetProperty("content").GetString() ?? "";
            if (content.StartsWith(prefill, StringComparison.Ordinal)) content = content[prefill.Length..];
            var truncated = choice.TryGetProperty("finish_reason", out var reason) && reason.GetString() == "length";
            var tokens = json.RootElement.TryGetProperty("usage", out var usage) && usage.TryGetProperty("completion_tokens", out var n) ? n.GetInt32() : 0;
            return new QwenDecoded(content, truncated, tokens);
        }
    }

    /// <summary>請求的 JSON：同 Mac 薄層的提示詞結構（system 空的也送），取樣鏈只留懲罰和溫度</summary>
    static byte[] Request(ReadOnlySpan<float> samples, string context, string prefill, int maxTokens, QwenSampling sampling)
    {
        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WritePropertyName("messages");
            w.WriteStartArray();
            w.WriteStartObject();
            w.WriteString("role", "system");
            w.WriteString("content", context);
            w.WriteEndObject();
            w.WriteStartObject();
            w.WriteString("role", "user");
            w.WritePropertyName("content");
            w.WriteStartArray();
            w.WriteStartObject();
            w.WriteString("type", "input_audio");
            w.WritePropertyName("input_audio");
            w.WriteStartObject();
            w.WriteString("data", Convert.ToBase64String(Wav(samples)));
            w.WriteString("format", "wav");
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteStartObject();
            w.WriteString("role", "assistant");
            w.WriteString("content", prefill);
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteNumber("max_tokens", maxTokens);
            w.WriteNumber("temperature", sampling.Temperature);
            w.WriteNumber("seed", sampling.Seed);
            w.WriteNumber("repeat_penalty", sampling.RepeatPenalty);
            w.WriteNumber("repeat_last_n", sampling.RepeatLastN);
            w.WriteNumber("top_k", 0);
            w.WriteNumber("top_p", 1.0);
            w.WriteNumber("min_p", 0.0);
            w.WriteBoolean("stream", false);
            w.WriteEndObject();
        }
        return buffer.ToArray();
    }

    /// <summary>16 kHz 單聲道 16-bit PCM 的 WAV</summary>
    static byte[] Wav(ReadOnlySpan<float> samples)
    {
        var data = new byte[44 + samples.Length * 2];
        var span = data.AsSpan();
        Encoding.ASCII.GetBytes("RIFF", span[..4]);
        BitConverter.TryWriteBytes(span[4..], 36 + samples.Length * 2);
        Encoding.ASCII.GetBytes("WAVEfmt ", span[8..16]);
        BitConverter.TryWriteBytes(span[16..], 16);
        BitConverter.TryWriteBytes(span[20..], (short)1);
        BitConverter.TryWriteBytes(span[22..], (short)1);
        BitConverter.TryWriteBytes(span[24..], 16000);
        BitConverter.TryWriteBytes(span[28..], 32000);
        BitConverter.TryWriteBytes(span[32..], (short)2);
        BitConverter.TryWriteBytes(span[34..], (short)16);
        Encoding.ASCII.GetBytes("data", span[36..40]);
        BitConverter.TryWriteBytes(span[40..], samples.Length * 2);
        for (var i = 0; i < samples.Length; i++)
        {
            var v = (int)MathF.Round(Math.Clamp(samples[i], -1f, 1f) * 32767f);
            BitConverter.TryWriteBytes(span[(44 + i * 2)..], (short)v);
        }
        return data;
    }

    public void Dispose()
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        process.Dispose();
        http.Dispose();
        if (job != IntPtr.Zero) CloseHandle(job);
    }

    // MARK: Job Object（Windows）：拾音結束或當掉時 Job 的 handle 關掉，子程序跟著結束

    static IntPtr CreateJob()
    {
        if (!OperatingSystem.IsWindows()) return IntPtr.Zero;
        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle == IntPtr.Zero) return IntPtr.Zero;
        var info = new JobObjectExtendedLimitInformation { BasicLimitInformation = { LimitFlags = 0x2000 /* KILL_ON_JOB_CLOSE */ } };
        var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var memory = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, memory, false);
            if (!SetInformationJobObject(handle, 9 /* JobObjectExtendedLimitInformation */, memory, (uint)size))
            {
                CloseHandle(handle);
                return IntPtr.Zero;
            }
        }
        finally { Marshal.FreeHGlobal(memory); }
        return handle;
    }

    void Attach()
    {
        if (job == IntPtr.Zero || !OperatingSystem.IsWindows()) return;
        try { AssignProcessToJobObject(job, process.Handle); }
        catch (InvalidOperationException) { }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);
}
