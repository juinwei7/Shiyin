using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Shiyin.Localization;
using Shiyin.Transcribe;

namespace Shiyin.Whisper;

public enum WhisperErrorKind { ModelMissing, LoadFailed, Failed, Cancelled, Empty, QwenFailed }

public sealed class WhisperException(WhisperErrorKind kind, int code = 0) : Exception(Describe(kind, code))
{
    public WhisperErrorKind Kind { get; } = kind;

    static string Describe(WhisperErrorKind kind, int code) => kind switch
    {
        WhisperErrorKind.ModelMissing => L.T("還沒有下載轉錄模型，請到設定 → 模型與儲存空間下載"),
        WhisperErrorKind.LoadFailed => L.T("轉錄模型載入失敗，模型檔可能已損壞，請到設定重新下載"),
        WhisperErrorKind.Failed => L.F("轉錄失敗（whisper 錯誤代碼 %d）", (long)code),
        WhisperErrorKind.Cancelled => L.T("已取消"),
        WhisperErrorKind.QwenFailed => L.T("Qwen3-ASR 的轉錄程式（llama-server）出錯，請再試一次；一直失敗的話到設定改回 Breeze"),
        _ => L.T("錄音裡沒有辨識到任何語音"),
    };
}

/// <summary>進度與取消（給 C 的 callback 用）</summary>
public sealed class WhisperControl(Action<double>? onProgress = null, CancellationToken cancel = default)
{
    public Action<double> OnProgress { get; } = onProgress ?? (_ => { });
    public CancellationToken Cancel { get; } = cancel;
    public bool IsCancelled => Cancel.IsCancellationRequested;
}

/// <summary>依序讀出 16 kHz 單聲道 float（長錄音分段轉錄，M§21.16）</summary>
public interface IAudioStream : IDisposable
{
    /// <summary>依檔案長度估計的取樣數，算進度用</summary>
    int EstimatedCount { get; }
    bool Finished { get; }
    /// <summary>接著讀，直到 buffer 至少有 count 個取樣或讀完</summary>
    void Fill(List<float> buffer, int count);
}

/// <summary>
/// whisper.cpp 的包裝（M§6.2，design-windows §8）：跟 Mac 的 WhisperEngine 同樣的參數、分段、接縫、過濾。
/// 在呼叫端的背景執行緒執行，會卡住那個執行緒
/// </summary>
public static class WhisperEngine
{
    static int contexts;

    /// <summary>還開著的 whisper（結束拾音時等它們收尾，R18.12）</summary>
    public static int OpenContexts => Volatile.Read(ref contexts);

    public static bool UseGpu { get; set; } = true;

    static readonly Lazy<bool> Silenced = new(() => { WhisperNative.SilenceLogs(); return true; });

    public static string SystemInfo => WhisperNative.Info();

    /// <summary>NVIDIA 顯示卡加速的元件資料夾（design-windows §8、D64）：第一次用 whisper 之前設才有用</summary>
    public static string? NativeDirectory
    {
        get => WhisperNative.PreferredDirectory;
        set => WhisperNative.PreferredDirectory = value;
    }

    /// <summary>whisper 已經載入（之後換不了元件，要重新開啟拾音）</summary>
    public static bool IsLoaded => WhisperNative.LoadedDirectory is not null;

    /// <summary>whisper 是從哪個資料夾載入的</summary>
    public static string? LoadedDirectory => WhisperNative.LoadedDirectory;

    static readonly Lazy<string> gpuDevice = new(() =>
    {
        try { return WhisperNative.Gpu(); }
        catch (EntryPointNotFoundException) { return ""; }   // 舊的 shim 沒有這個函式
    });

    /// <summary>whisper 會用的顯示卡；只有 CPU 時是空字串。會載入 whisper（換元件要在這之前）</summary>
    public static string GpuDevice => gpuDevice.Value;

    public static int Threads => Math.Max(2, Math.Min(8, Environment.ProcessorCount - 2));

    /// <summary>一次轉錄：模型只載入一次，一段一段送進去；每段用基本提示詞加上前一段最後的文字</summary>
    public sealed class Session : IDisposable
    {
        IntPtr ctx;
        readonly string prompt;
        readonly string language;
        public WhisperControl Control { get; }
        public List<TranscribedSegment> Segments { get; } = [];

        public Session(string modelPath, string prompt, string language, WhisperControl control)
        {
            _ = Silenced.Value;
            if (!File.Exists(modelPath)) throw new WhisperException(WhisperErrorKind.ModelMissing);
            ctx = WhisperNative.Init(modelPath, UseGpu ? 1 : 0, 1);
            if (ctx == IntPtr.Zero) throw new WhisperException(WhisperErrorKind.LoadFailed);
            Interlocked.Increment(ref contexts);
            (this.prompt, this.language, Control) = (prompt, language, control);
        }

        public void Dispose()
        {
            if (ctx == IntPtr.Zero) return;
            WhisperNative.Free(ctx);
            ctx = IntPtr.Zero;
            Interlocked.Decrement(ref contexts);
        }

        /// <summary>辨識一段：offset 是這一段在整段錄音的第幾個取樣</summary>
        public void Feed(ReadOnlySpan<float> samples, int offset, double progressBase, double progressSpan)
        {
            if (samples.Length == 0) return;
            // 前一段最後的文字接在提示詞後面，跟 whisper 自己連續轉錄時一樣
            var chunkPrompt = WhisperRules.ChunkPrompt(prompt, Segments, language);
            var part = Run(ctx, samples, offset, chunkPrompt, language, Control, progressBase, progressSpan);
            if (offset > 0 && Segments.Count > 0)
                part = WhisperRules.DropSeamRepeats(part, Segments[^1], offset / WhisperRules.SampleRate);
            Segments.AddRange(part);
        }
    }

    /// <summary>辨識已經解碼的整段錄音（分聲道時用）</summary>
    public static List<TranscribedSegment> Transcribe(float[] samples, IReadOnlyList<(int Start, int End)>? chunks, string modelPath, string prompt,
                                                      string language, WhisperControl control)
    {
        using var session = new Session(modelPath, prompt, language, control);
        var total = (double)Math.Max(samples.Length, 1);
        foreach (var (start, end) in chunks ?? [(0, samples.Length)])
            session.Feed(samples.AsSpan(start, end - start), start, start / total, (end - start) / total);
        if (session.Segments.Count == 0) throw new WhisperException(WhisperErrorKind.Empty);
        return session.Segments;
    }

    /// <summary>從檔案依序讀、一段一段轉錄，不先解碼整段（M§21.16）。切的位置跟 <see cref="WhisperRules.Chunks"/> 相同。
    /// 回傳逐字稿、分段的位置（第幾個取樣）、總取樣數</summary>
    public static (List<TranscribedSegment> Segments, List<int> Seams, int SampleCount) TranscribeFile(
        IAudioStream stream, double chunkSeconds, string modelPath, string prompt, string language, WhisperControl control)
    {
        using var session = new Session(modelPath, prompt, language, control);
        var rate = (int)WhisperRules.SampleRate;
        var chunk = (int)(chunkSeconds * rate);
        // 剩下超過「一段＋2 分鐘」才切，所以要讀到這麼多才能決定；不分段時讀完整段
        var need = chunk > 0 ? chunk + 120 * rate + 1 : int.MaxValue;
        var total = (double)Math.Max(stream.EstimatedCount, 1);
        var buffer = new List<float>();
        var offset = 0;
        var seams = new List<int>();
        while (true)
        {
            if (control.IsCancelled) throw new WhisperException(WhisperErrorKind.Cancelled);
            stream.Fill(buffer, need);
            var span = CollectionsMarshal.AsSpan(buffer);
            var cut = chunk > 0 && buffer.Count >= need ? WhisperRules.QuietestCut(span, 0, chunk) : buffer.Count;
            session.Feed(span[..cut], offset, Math.Min(offset / total, 1), cut / total);
            buffer.RemoveRange(0, cut);
            offset += cut;
            // 沒切（cut 是整個 buffer）時一定已經讀完
            if (buffer.Count == 0 && stream.Finished) break;
            seams.Add(offset);
        }
        if (session.Segments.Count == 0) throw new WhisperException(WhisperErrorKind.Empty);
        return (session.Segments, seams, offset);
    }

    sealed class CallState(WhisperControl control, double progressBase, double progressSpan)
    {
        public readonly WhisperControl Control = control;
        public readonly double Base = progressBase;
        public readonly double Span = progressSpan;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void OnProgress(int percent, IntPtr user)
    {
        if (GCHandle.FromIntPtr(user).Target is CallState s) s.Control.OnProgress(Math.Min(1, s.Base + percent / 100.0 * s.Span));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnAbort(IntPtr user) => GCHandle.FromIntPtr(user).Target is CallState s && s.Control.IsCancelled ? 1 : 0;

    /// <summary>辨識一段；時間點換成整段錄音的時間（M§6.2 的過濾和低信心規則）</summary>
    static unsafe List<TranscribedSegment> Run(IntPtr ctx, ReadOnlySpan<float> samples, int start, string prompt, string language,
                                              WhisperControl control, double progressBase, double progressSpan)
    {
        var state = GCHandle.Alloc(new CallState(control, progressBase, progressSpan));
        var lang = Marshal.StringToCoTaskMemUTF8(language);
        var promptPtr = Marshal.StringToCoTaskMemUTF8(prompt);
        int result;
        try
        {
            var p = new WhisperNative.FullParams
            {
                BeamSize = 5, BestOf = 5, Threads = Threads, SuppressBlank = 1, Language = lang, InitialPrompt = promptPtr,
                Progress = &OnProgress, ProgressUser = GCHandle.ToIntPtr(state), Abort = &OnAbort, AbortUser = GCHandle.ToIntPtr(state),
            };
            fixed (float* s = samples) result = WhisperNative.Full(ctx, &p, s, samples.Length);
        }
        finally
        {
            Marshal.FreeCoTaskMem(lang);
            Marshal.FreeCoTaskMem(promptPtr);
            state.Free();
        }
        if (control.IsCancelled) throw new WhisperException(WhisperErrorKind.Cancelled);
        if (result != 0) throw new WhisperException(WhisperErrorKind.Failed, result);

        var offset = start / WhisperRules.SampleRate;
        var limit = (start + samples.Length) / WhisperRules.SampleRate;
        var eot = WhisperNative.TokenEot(ctx);
        var segments = new List<TranscribedSegment>();
        var n = WhisperNative.NSegments(ctx);
        for (var i = 0; i < n; i++)
        {
            var text = (Marshal.PtrToStringUTF8(WhisperNative.SegmentText(ctx, i)) ?? "").Trim();
            if (text.Length == 0) continue;
            double logSum = 0;
            var count = 0;
            var tokens = WhisperNative.NTokens(ctx, i);
            for (var j = 0; j < tokens; j++)
            {
                if (WhisperNative.TokenId(ctx, i, j) >= eot) continue;
                logSum += Math.Log(Math.Max((double)WhisperNative.TokenP(ctx, i, j), 1e-6));
                count++;
            }
            var avgLogProb = count > 0 ? logSum / count : -10;
            var noSpeech = (double)WhisperNative.NoSpeechProb(ctx, i);
            if (!WhisperRules.Keep(text, noSpeech, avgLogProb)) continue;
            // 最後一個 30 秒的窗會補零，時間點可能超過這一段的結尾
            segments.Add(new TranscribedSegment(
                Math.Min(offset + WhisperNative.SegmentT0(ctx, i) / 100.0, limit),
                Math.Min(offset + WhisperNative.SegmentT1(ctx, i) / 100.0, limit),
                text, WhisperRules.LowConfidence(text, avgLogProb)));
        }
        return segments;
    }

    /// <summary>自動偵測會議的主要語言（M§18.8、D21）：中文、英文、日文各自的機率（加總為 1）</summary>
    public static unsafe Dictionary<Model.MeetingLanguage, double> DetectLanguage(float[] samples, string modelPath)
    {
        _ = Silenced.Value;
        if (!File.Exists(modelPath)) throw new WhisperException(WhisperErrorKind.ModelMissing);
        // 跟 Mac 一樣只設 use_gpu，flash_attn 用預設（開）
        var ctx = WhisperNative.Init(modelPath, UseGpu ? 1 : 0, 1);
        if (ctx == IntPtr.Zero) throw new WhisperException(WhisperErrorKind.LoadFailed);
        Interlocked.Increment(ref contexts);
        try
        {
            var threads = Math.Max(2, Math.Min(4, Environment.ProcessorCount - 2));
            int mel;
            fixed (float* s = samples) mel = WhisperNative.PcmToMel(ctx, s, samples.Length, threads);
            if (mel != 0) throw new WhisperException(WhisperErrorKind.Failed, mel);
            var probs = new float[WhisperNative.LangMaxId() + 1];
            int top;
            fixed (float* p = probs) top = WhisperNative.LangAutoDetect(ctx, threads, p);
            if (top < 0) throw new WhisperException(WhisperErrorKind.Failed, top);
            var result = new Dictionary<Model.MeetingLanguage, double>();
            foreach (var language in Model.MeetingLanguages.All)
            {
                var id = WhisperNative.LangId(Model.MeetingLanguages.WhisperCode(language));
                result[language] = id >= 0 && id < probs.Length ? probs[id] : 0;
            }
            var sum = result.Values.Sum();
            return sum > 0 ? result.ToDictionary(p => p.Key, p => p.Value / sum) : result;
        }
        finally
        {
            WhisperNative.Free(ctx);
            Interlocked.Decrement(ref contexts);
        }
    }
}
