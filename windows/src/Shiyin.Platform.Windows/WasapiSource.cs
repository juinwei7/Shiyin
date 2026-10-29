using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Shiyin.Localization;
using Shiyin.Services;
using Shiyin.Services.Capture;

namespace Shiyin.Platform;

/// <summary>
/// WASAPI 擷取（design-windows §6）：麥克風用共用模式、事件驅動，用 Console 角色的預設端點（不用 Communications：Windows 會把其他聲音自動降低）；
/// 電腦聲音錄預設輸出裝置（Console 角色）的 loopback，換預設裝置時重開，沒有輸出裝置時只補靜音
/// （R54、D129：原本用 process loopback 排除拾音自己，新版 Teams 的聲音收到的全是 0）。
/// 先請系統直接轉成 16 kHz 單聲道浮點（AUTOCONVERTPCM），不行再用裝置的格式自己降混、重新取樣。
/// 電腦聲音沒有東西在播時系統不送資料，這裡依時間補靜音，寫檔的對齊規則才跟 Mac 一樣
/// </summary>
public sealed class WasapiSource : IAudioSource
{
    public enum Kind { Microphone, SystemAudio }

    const int AccessDenied = unchecked((int)0x80070005);
    const long BufferDuration = 1_000_000;   // 100 毫秒（100 奈秒為單位）
    /// <summary>KSDATAFORMAT_SUBTYPE_IEEE_FLOAT</summary>
    static readonly Guid FloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");

    readonly Kind kind;
    readonly string deviceId;
    Thread? thread;
    volatile bool stopping;
    MMDeviceEnumerator? enumerator;
    MMDeviceNotificationClient? notifications;
    /// <summary>電腦聲音正在錄的輸出裝置（被停用、拔掉時重開）；沒有輸出裝置時是 null</summary>
    volatile string? outputId;

    public Action<float[]>? OnSamples { get; set; }
    public Action? OnInterruption { get; set; }

    /// <summary>deviceId：麥克風的端點 ID，空的是系統預設（電腦聲音不用）</summary>
    public WasapiSource(Kind kind, string deviceId = "")
    {
        this.kind = kind;
        this.deviceId = deviceId;
    }

    public void Start()
    {
        stopping = false;
        using var ready = new ManualResetEventSlim();
        Exception? failure = null;
        thread = new Thread(() => Run(ready, e => failure = e))
        {
            IsBackground = true, Priority = ThreadPriority.Highest, Name = kind == Kind.Microphone ? "capture-mic" : "capture-system",
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(10))) failure ??= new CaptureException(L.T("開始錄音"));
        if (failure is not null)
        {
            Stop();
            throw failure as CaptureException ?? new CaptureException(failure.Message);
        }
        // 換裝置、拔耳機（D25）：收到就通知 Recorder 重新開始（它會等 0.3 秒沒有新的通知再重開）
        enumerator = new MMDeviceEnumerator();
        notifications = enumerator.CreateNotificationClient(false);
        notifications.DefaultDeviceChanged += (_, e) =>
        {
            if (kind == Kind.Microphone && deviceId.Length == 0 && e.Flow == DataFlow.Capture && e.Role == Role.Console) OnInterruption?.Invoke();
            if (kind == Kind.SystemAudio && e.Flow == DataFlow.Render && e.Role == Role.Console) OnInterruption?.Invoke();
        };
        notifications.DeviceStateChanged += (_, e) => { if (Concerns(e.DeviceId)) OnInterruption?.Invoke(); };
        notifications.DeviceRemoved += (_, e) => { if (Concerns(e.DeviceId)) OnInterruption?.Invoke(); };
    }

    bool Concerns(string? id) => kind == Kind.Microphone ? deviceId.Length == 0 || id == deviceId : id is not null && id == outputId;

    public void Stop()
    {
        stopping = true;
        thread?.Join(TimeSpan.FromSeconds(3));
        thread = null;
        notifications?.Dispose();
        notifications = null;
        enumerator?.Dispose();
        enumerator = null;
    }

    /// <summary>電腦聲音沒有任何輸出裝置時回傳 null（R54.2）</summary>
    AudioClient? Activate()
    {
        using var devices = new MMDeviceEnumerator();
        if (kind == Kind.SystemAudio)
        {
            var output = devices.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Console, out var o) ? o : null;
            outputId = output?.ID;
            return output?.CreateAudioClient();
        }
        MMDevice? device = null;
        if (deviceId.Length > 0)
            try { device = devices.GetDevice(deviceId); if (device.State != DeviceState.Active) device = null; }
            catch (COMException) { device = null; }
        // 指定的裝置不在了：跟著系統預設（同 Mac）
        device ??= devices.TryGetDefaultAudioEndpoint(DataFlow.Capture, Role.Console, out var d) ? d : throw new CaptureException(CaptureErrors.NoInput);
        return device.CreateAudioClient();
    }

    void Run(ManualResetEventSlim ready, Action<Exception> fail)
    {
        AudioClient? client = null;
        try
        {
            WaveFormat format;
            var loopback = kind == Kind.SystemAudio ? AudioClientStreamFlags.Loopback : AudioClientStreamFlags.None;
            var clock = Stopwatch.StartNew();
            long emitted = 0;
            // 電腦聲音沒有東西在播時不送資料：照時間補靜音（留 50 毫秒的餘裕，真的資料晚一點到時不會重疊太多）
            void PadSilence()
            {
                var expected = (long)(clock.Elapsed.TotalSeconds * StreamResampler.OutputRate);
                if (expected - emitted <= StreamResampler.OutputRate / 10) return;
                var gap = (int)(expected - emitted - StreamResampler.OutputRate / 20);
                emitted += gap;
                OnSamples?.Invoke(new float[gap]);
            }
            client = Activate();
            if (client is null)
            {
                // 沒有輸出裝置（R54.2）：不算開始失敗，只補靜音；接上裝置時 DefaultDeviceChanged 會讓 Recorder 重開
                Log.Info("capture SystemAudio: no output device, recording silence");
                ready.Set();
                while (!stopping)
                {
                    Thread.Sleep(100);
                    PadSilence();
                }
                return;
            }
            try
            {
                format = WaveFormat.CreateIeeeFloatWaveFormat(StreamResampler.OutputRate, 1);
                client.Initialize(AudioClientShareMode.Shared,
                    AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality | loopback,
                    BufferDuration, 0, format, Guid.Empty);
            }
            catch (COMException e) when (e.HResult != AccessDenied)
            {
                // 系統不幫忙轉格式：用裝置的格式，自己轉
                client.Dispose();
                client = Activate() ?? throw new CaptureException(L.F("%@失敗（錯誤代碼 %@）", L.T("開始錄音"), $"0x{e.HResult:X8}"));
                format = client.MixFormat;
                client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.EventCallback | loopback, BufferDuration, 0, format, Guid.Empty);
            }
            using var signal = new AutoResetEvent(false);
            client.SetEventHandle(signal.SafeWaitHandle.DangerousGetHandle());
            var capture = client.AudioCaptureClient;
            var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
                || (format is WaveFormatExtensible ext && ext.SubFormat == FloatSubtype);
            var direct = isFloat && format.Channels == 1 && format.SampleRate == StreamResampler.OutputRate;
            var resampler = new StreamResampler(format.SampleRate);
            client.Start();
            ready.Set();
            clock.Restart();
            while (!stopping)
            {
                signal.WaitOne(100);
                var any = false;
                while (!stopping && capture.GetNextPacketSize() > 0)
                {
                    using var lease = capture.GetBufferLease(format.BlockAlign);
                    var frames = lease.FrameCount;
                    if (frames == 0) break;
                    float[] samples;
                    if ((lease.Flags & AudioClientBufferFlags.Silent) != 0)
                        samples = direct ? new float[frames] : resampler.Process(new float[frames]);
                    else if (direct)
                        samples = MemoryMarshal.Cast<byte, float>(lease.Buffer[..(frames * 4)]).ToArray();
                    else
                        samples = resampler.Process(StreamResampler.Mono(lease.Buffer[..(frames * format.BlockAlign)], format.Channels, format.BitsPerSample, isFloat));
                    if (samples.Length == 0) continue;
                    any = true;
                    emitted += samples.Length;
                    OnSamples?.Invoke(samples);
                }
                if (!any && kind == Kind.SystemAudio) PadSilence();
            }
            client.Stop();
        }
        catch (Exception e)
        {
            var denied = e.HResult == AccessDenied || e.InnerException?.HResult == AccessDenied || e is CaptureException { AccessDenied: true };
            // 自己丟的 CaptureException（例如「找不到可以用的麥克風」）已經寫好原因，不要換成錯誤代碼
            var message = denied ? L.T("拾音沒有麥克風的使用權限")
                : e is CaptureException capture ? capture.Message
                : L.F("%@失敗（錯誤代碼 %@）", L.T("開始錄音"), $"0x{e.HResult:X8}");
            if (!ready.IsSet)
            {
                fail(new CaptureException(message, denied && kind == Kind.Microphone));
                ready.Set();
            }
            else
            {
                Log.Error($"capture {kind} stopped: 0x{e.HResult:X8}");
                // 錄到一半裝置不見了：讓 Recorder 重新開始（看門狗也會發現沒資料）
                OnInterruption?.Invoke();
            }
        }
        finally
        {
            client?.Dispose();
        }
    }

    /// <summary>可以選的麥克風（設定頁）：端點 ID、名稱</summary>
    public static List<(string Id, string Name)> Microphones()
    {
        using var devices = new MMDeviceEnumerator();
        return devices.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).Select(d => (d.ID, d.FriendlyName)).ToList();
    }
}
