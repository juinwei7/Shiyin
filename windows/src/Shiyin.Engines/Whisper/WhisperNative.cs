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

    /// <summary>找原生的 shiyin_whisper：環境變數 SHIYIN_NATIVE_DIR（評估、測試用），再來是程式的資料夾</summary>
    static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != Lib) return IntPtr.Zero;
        var file = OperatingSystem.IsWindows() ? "shiyin_whisper.dll" : OperatingSystem.IsMacOS() ? "libshiyin_whisper.dylib" : "libshiyin_whisper.so";
        foreach (var dir in new[] { Environment.GetEnvironmentVariable("SHIYIN_NATIVE_DIR"), AppContext.BaseDirectory })
        {
            if (string.IsNullOrEmpty(dir)) continue;
            var candidate = Path.Combine(dir, file);
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle)) return handle;
        }
        return IntPtr.Zero;
    }

    [LibraryImport(Lib, EntryPoint = "shiyin_version")] public static partial int Version();
    [LibraryImport(Lib, EntryPoint = "shiyin_silence_logs")] public static partial void SilenceLogs();
    [LibraryImport(Lib, EntryPoint = "shiyin_system_info")] public static partial int SystemInfo(byte* buffer, int size);
    [LibraryImport(Lib, EntryPoint = "shiyin_init", StringMarshalling = StringMarshalling.Utf8)] public static partial IntPtr Init(string path, int useGpu, int flashAttn);
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
}
