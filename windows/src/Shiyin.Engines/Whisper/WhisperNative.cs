using System.Reflection;
using System.Runtime.InteropServices;

namespace Shiyin.Whisper;

/// <summary>shiyin_whisper（windows/native/whisper 的轉接層，D56）的 P/Invoke</summary>
static unsafe partial class WhisperNative
{
    const string Lib = "shiyin_whisper";

    [StructLayout(LayoutKind.Sequential)]
    public struct FullParams
    {
        public int BeamSize;
        public int BestOf;
        public int Threads;
        public int SuppressBlank;
        public IntPtr Language;
        public IntPtr InitialPrompt;
        public delegate* unmanaged[Cdecl]<int, IntPtr, void> Progress;
        public IntPtr ProgressUser;
        public delegate* unmanaged[Cdecl]<IntPtr, int> Abort;
        public IntPtr AbortUser;
    }

    static WhisperNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(WhisperNative).Assembly, Resolve);
    }

    /// <summary>先從這個資料夾載入（NVIDIA 顯示卡加速的元件，design-windows §8）：第一次呼叫 whisper 之前設，載入之後就換不了</summary>
    public static string? PreferredDirectory { get; set; }

    /// <summary>shim 是從哪個資料夾載入的；還沒載入時是 null</summary>
    public static string? LoadedDirectory { get; private set; }

    /// <summary>找原生的 shiyin_whisper：環境變數 SHIYIN_NATIVE_DIR（評估、測試用）、顯示卡加速的元件，再來是程式的資料夾</summary>
    static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != Lib) return IntPtr.Zero;
        var file = OperatingSystem.IsWindows() ? "shiyin_whisper.dll" : OperatingSystem.IsMacOS() ? "libshiyin_whisper.dylib" : "libshiyin_whisper.so";
        foreach (var dir in new[] { Environment.GetEnvironmentVariable("SHIYIN_NATIVE_DIR"), PreferredDirectory, AppContext.BaseDirectory })
        {
            if (string.IsNullOrEmpty(dir)) continue;
            var candidate = Path.Combine(dir, file);
            if (!File.Exists(candidate)) continue;
            // 元件的資料夾加進 DLL 的搜尋路徑：ggml 用 LoadLibraryW 載入 ggml-cuda.dll，它要的 cudart、cublas
            // 只照一般的順序找（程式的資料夾、系統），不會去 ggml-cuda.dll 所在的資料夾找
            var preferred = OperatingSystem.IsWindows() && dir == PreferredDirectory;
            if (preferred) SetDllDirectoryW(dir);
            if (NativeLibrary.TryLoad(candidate, out var handle))
            {
                LoadedDirectory = dir;
                return handle;
            }
            if (preferred) SetDllDirectoryW(null);
        }
        return IntPtr.Zero;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "SetDllDirectoryW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetDllDirectoryW(string? path);

    [LibraryImport(Lib, EntryPoint = "shiyin_version")] public static partial int Version();
    [LibraryImport(Lib, EntryPoint = "shiyin_silence_logs")] public static partial void SilenceLogs();
    [LibraryImport(Lib, EntryPoint = "shiyin_system_info")] public static partial int SystemInfo(byte* buffer, int size);
    [LibraryImport(Lib, EntryPoint = "shiyin_gpu_device")] public static partial int GpuDevice(byte* buffer, int size);
    [LibraryImport(Lib, EntryPoint = "shiyin_init", StringMarshalling = StringMarshalling.Utf8)] public static partial IntPtr Init(string path, int useGpu, int flashAttn);
    [LibraryImport(Lib, EntryPoint = "shiyin_init_device", StringMarshalling = StringMarshalling.Utf8)] public static partial IntPtr InitDevice(string path, int useGpu, int flashAttn, int gpuDevice);
    [LibraryImport(Lib, EntryPoint = "shiyin_free")] public static partial void Free(IntPtr ctx);
    [LibraryImport(Lib, EntryPoint = "shiyin_full")] public static partial int Full(IntPtr ctx, FullParams* p, float* samples, int n);
    [LibraryImport(Lib, EntryPoint = "shiyin_n_segments")] public static partial int NSegments(IntPtr ctx);
    [LibraryImport(Lib, EntryPoint = "shiyin_segment_text")] public static partial IntPtr SegmentText(IntPtr ctx, int i);
    [LibraryImport(Lib, EntryPoint = "shiyin_segment_t0")] public static partial long SegmentT0(IntPtr ctx, int i);
    [LibraryImport(Lib, EntryPoint = "shiyin_segment_t1")] public static partial long SegmentT1(IntPtr ctx, int i);
    [LibraryImport(Lib, EntryPoint = "shiyin_segment_no_speech_prob")] public static partial float NoSpeechProb(IntPtr ctx, int i);
    [LibraryImport(Lib, EntryPoint = "shiyin_n_tokens")] public static partial int NTokens(IntPtr ctx, int i);
    [LibraryImport(Lib, EntryPoint = "shiyin_token_id")] public static partial int TokenId(IntPtr ctx, int i, int j);
    [LibraryImport(Lib, EntryPoint = "shiyin_token_p")] public static partial float TokenP(IntPtr ctx, int i, int j);
    [LibraryImport(Lib, EntryPoint = "shiyin_token_eot")] public static partial int TokenEot(IntPtr ctx);
    [LibraryImport(Lib, EntryPoint = "shiyin_pcm_to_mel")] public static partial int PcmToMel(IntPtr ctx, float* samples, int n, int threads);
    [LibraryImport(Lib, EntryPoint = "shiyin_lang_max_id")] public static partial int LangMaxId();
    [LibraryImport(Lib, EntryPoint = "shiyin_lang_id", StringMarshalling = StringMarshalling.Utf8)] public static partial int LangId(string lang);
    [LibraryImport(Lib, EntryPoint = "shiyin_lang_auto_detect")] public static partial int LangAutoDetect(IntPtr ctx, int threads, float* probs);

    public static string Info()
    {
        var buffer = new byte[4096];
        fixed (byte* b = buffer)
        {
            var n = SystemInfo(b, buffer.Length);
            return System.Text.Encoding.UTF8.GetString(buffer, 0, Math.Min(n, buffer.Length - 1));
        }
    }

    /// <summary>whisper 會用的顯示卡（「CUDA0 (NVIDIA GeForce RTX 3060)」）；只有 CPU 時是空字串</summary>
    public static string Gpu()
    {
        var buffer = new byte[512];
        fixed (byte* b = buffer)
        {
            var n = GpuDevice(b, buffer.Length);
            return System.Text.Encoding.UTF8.GetString(buffer, 0, Math.Clamp(n, 0, buffer.Length - 1));
        }
    }
}
