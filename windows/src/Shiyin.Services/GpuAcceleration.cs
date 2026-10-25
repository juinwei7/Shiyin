using Shiyin.Whisper;

namespace Shiyin.Services;

/// <summary>
/// NVIDIA 顯示卡加速（R27 R4.6、design-windows §8、D64）：x64 有 NVIDIA 顯示卡時自動下載一次 whisper.cpp 官方的 CUDA 版（ModelStore.Model.GpuCuda）。
/// 裝好之後，第一次用 whisper 之前把安裝資料夾的 shim 和 VC++ 執行階段複製過去，whisper 整套從那裡載入
/// </summary>
public sealed class GpuAcceleration
{
    /// <summary>元件資料夾裡要跟安裝資料夾一樣的檔案：shim（app 更新後跟著換）和官方 whisper 要的 VC++ 執行階段</summary>
    static readonly string[] AppFiles = ["shiyin_whisper.dll", "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll", "vcomp140.dll"];

    readonly ModelStore models;
    readonly AppSettings settings;
    readonly string appDirectory;

    /// <summary>這台電腦用得上：Windows x64，有 NVIDIA 顯示卡（AppHost 判斷）</summary>
    public bool Applies { get; }

    /// <summary>元件下載好了，但 whisper 已經從安裝資料夾載入：要重新開啟拾音才會用上</summary>
    public event Action? RestartNeeded;

    public GpuAcceleration(ModelStore models, AppSettings settings, bool applies, string appDirectory)
    {
        this.models = models;
        this.settings = settings;
        this.appDirectory = appDirectory;
        Applies = applies;
        models.Installed += model =>
        {
            if (model is ModelStore.Model.Qwen or ModelStore.Model.GpuCuda) OfferQwenCuda();
            if (model != ModelStore.Model.GpuCuda) return;
            if (WhisperEngine.IsLoaded) RestartNeeded?.Invoke();
            else Use();
        };
    }

    /// <summary>Qwen 用 CUDA 版的條件（design-windows §8.4）：有 NVIDIA、CUDA 版裝好、whisper 的 CUDA 元件也在（cudart、cublas 用它的）</summary>
    public bool QwenCudaReady => Applies && models.IsInstalled(ModelStore.Model.QwenCuda) && models.IsInstalled(ModelStore.Model.GpuCuda);

    /// <summary>選了 Qwen（或 Qwen 已經裝好）而且有 NVIDIA 顯示卡：自動下載一次 llama.cpp 的 CUDA 版；使用者刪掉後不再下載</summary>
    public void OfferQwenCuda()
    {
        if (!Applies || settings.QwenCudaOffered || !models.IsInstalled(ModelStore.Model.GpuCuda)) return;
        if (!settings.UseQwen && !models.IsInstalled(ModelStore.Model.Qwen)) return;
        settings.Set(AppSettings.QwenCudaOfferedKey, true);
        Log.Info("gpu pack: downloading llama.cpp cuda for qwen");
        models.AutoStart(ModelStore.Model.QwenCuda);
    }

    /// <summary>啟動時：裝好了就讓 whisper 從元件資料夾載入；用得上又還沒自動下載過就下載（只自動一次，使用者刪掉後不再下載）</summary>
    public void Start()
    {
        OfferQwenCuda();
        if (models.IsInstalled(ModelStore.Model.GpuCuda))
        {
            Use();
            return;
        }
        if (!Applies || settings.GpuPackOffered) return;
        settings.Set(AppSettings.GpuPackOfferedKey, true);
        Log.Info("gpu pack: nvidia found, downloading");
        models.AutoStart(ModelStore.Model.GpuCuda);
    }

    void Use()
    {
        var dir = models.Directory(ModelStore.Model.GpuCuda);
        try
        {
            foreach (var name in AppFiles)
            {
                var source = Path.Combine(appDirectory, name);
                var target = Path.Combine(dir, name);
                if (File.Exists(source) && !Same(source, target)) File.Copy(source, target, overwrite: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error($"gpu pack: copy failed: {e.GetType().Name}");
            return;
        }
        if (!File.Exists(Path.Combine(dir, AppFiles[0]))) return;
        WhisperEngine.NativeDirectory = dir;
        Log.Info("gpu pack: whisper will load from the pack");
    }

    static bool Same(string a, string b) =>
        File.Exists(b) && new FileInfo(a).Length == new FileInfo(b).Length && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
}
