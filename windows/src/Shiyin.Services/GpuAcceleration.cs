using Shiyin.Whisper;

namespace Shiyin.Services;

/// <summary>
/// 顯示卡加速：x64 有 NVIDIA 顯示卡時自動下載一次 whisper.cpp 官方的 CUDA 版（R27 R4.6、design-windows §8、D64，ModelStore.Model.GpuCuda）；
/// 有 AMD 顯示卡（沒有 NVIDIA）時自動下載一次拾音自己建的 Vulkan 版（R27 R4.7、§8、D127，ModelStore.Model.GpuVulkan）。
/// 裝好之後，第一次用 whisper 之前把安裝資料夾的 shim 和 VC++ 執行階段複製過去，whisper 整套從那裡載入
/// </summary>
public sealed class GpuAcceleration
{
    /// <summary>這台電腦用哪一包：None（不是 x64，或沒有 NVIDIA、AMD）、Cuda（有 NVIDIA）、Vulkan（有 AMD、沒有 NVIDIA）</summary>
    public enum Pack { None, Cuda, Vulkan }

    /// <summary>NVIDIA 和 AMD 都有（AMD 內顯＋NVIDIA 獨顯的筆電）時用 CUDA；Intel 顯示卡先不用（R4.7）；官方的 CUDA 版和拾音的 Vulkan 版都只有 x64</summary>
    public static Pack PackFor(bool x64, bool nvidia, bool amd) => !x64 ? Pack.None : nvidia ? Pack.Cuda : amd ? Pack.Vulkan : Pack.None;

    /// <summary>Qwen 用哪一版的 llama-server（design-windows §8.4）</summary>
    public enum QwenGpu { None, Cuda, Vulkan }

    /// <summary>元件資料夾裡要跟安裝資料夾一樣的檔案：shim（app 更新後跟著換）和官方 whisper 要的 VC++ 執行階段</summary>
    static readonly string[] AppFiles = ["shiyin_whisper.dll", "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll", "vcomp140.dll"];

    readonly ModelStore models;
    readonly AppSettings settings;
    readonly string appDirectory;

    public Pack Kind { get; }

    /// <summary>這台電腦用得上（AppHost 判斷）：x64，有 NVIDIA 或 AMD 顯示卡</summary>
    public bool Applies => Kind != Pack.None;

    /// <summary>元件下載好了，但 whisper 已經從安裝資料夾載入：要重新開啟拾音才會用上</summary>
    public event Action? RestartNeeded;

    public GpuAcceleration(ModelStore models, AppSettings settings, Pack pack, string appDirectory)
    {
        this.models = models;
        this.settings = settings;
        this.appDirectory = appDirectory;
        Kind = pack;
        models.Installed += model =>
        {
            if (model is ModelStore.Model.Qwen or ModelStore.Model.GpuCuda or ModelStore.Model.GpuVulkan) OfferQwenGpu();
            if (model is not (ModelStore.Model.GpuCuda or ModelStore.Model.GpuVulkan)) return;
            if (WhisperEngine.IsLoaded) RestartNeeded?.Invoke();
            else Use(model);
        };
    }

    /// <summary>whisper 用的那一包</summary>
    ModelStore.Model WhisperPack => Kind == Pack.Vulkan ? ModelStore.Model.GpuVulkan : ModelStore.Model.GpuCuda;

    /// <summary>
    /// Qwen 用哪一版（design-windows §8.4）：CUDA 版要 NVIDIA、llama.cpp 的 CUDA 版裝好、whisper 的 CUDA 元件也在（cudart、cublas 用它的）；
    /// Vulkan 版要 AMD、llama.cpp 的 Vulkan 版裝好（不用另外的執行階段）
    /// </summary>
    public QwenGpu QwenRuntime => Kind switch
    {
        Pack.Cuda when models.IsInstalled(ModelStore.Model.QwenCuda) && models.IsInstalled(ModelStore.Model.GpuCuda) => QwenGpu.Cuda,
        Pack.Vulkan when models.IsInstalled(ModelStore.Model.QwenVulkan) => QwenGpu.Vulkan,
        _ => QwenGpu.None,
    };

    /// <summary>
    /// 選了 Qwen（或 Qwen 已經裝好）：自動下載一次 llama.cpp 的顯示卡版；使用者刪掉後不再下載。
    /// CUDA 版要先有 whisper 的 CUDA 元件（cudart、cublas 用它的）；Vulkan 版不用
    /// </summary>
    public void OfferQwenGpu()
    {
        if (!settings.UseQwen && !models.IsInstalled(ModelStore.Model.Qwen)) return;
        if (Kind == Pack.Cuda)
        {
            if (settings.QwenCudaOffered || !models.IsInstalled(ModelStore.Model.GpuCuda)) return;
            settings.Set(AppSettings.QwenCudaOfferedKey, true);
            Log.Info("gpu pack: downloading llama.cpp cuda for qwen");
            models.AutoStart(ModelStore.Model.QwenCuda);
        }
        else if (Kind == Pack.Vulkan)
        {
            if (settings.QwenVulkanOffered) return;
            settings.Set(AppSettings.QwenVulkanOfferedKey, true);
            Log.Info("gpu pack: downloading llama.cpp vulkan for qwen");
            models.AutoStart(ModelStore.Model.QwenVulkan);
        }
    }

    /// <summary>啟動時：裝好了就讓 whisper 從元件資料夾載入；用得上又還沒自動下載過就下載（只自動一次，使用者刪掉後不再下載）</summary>
    public void Start()
    {
        OfferQwenGpu();
        // 這台電腦的那一包先；換過顯示卡（之前下載過另一包）時照樣用裝好的那一包
        var other = WhisperPack == ModelStore.Model.GpuCuda ? ModelStore.Model.GpuVulkan : ModelStore.Model.GpuCuda;
        foreach (var pack in new[] { WhisperPack, other })
        {
            if (!models.IsInstalled(pack)) continue;
            Use(pack);
            return;
        }
        if (!Applies) return;
        var offeredKey = Kind == Pack.Vulkan ? AppSettings.GpuVulkanOfferedKey : AppSettings.GpuPackOfferedKey;
        if (settings.Flag(offeredKey, false)) return;
        // 轉錄用企業伺服器時不下載（R50.7）；換回本機的引擎後，下次開啟拾音再判斷
        if (settings.TranscriptionEngine == "server") return;
        settings.Set(offeredKey, true);
        Log.Info(Kind == Pack.Vulkan ? "gpu pack: amd found, downloading vulkan" : "gpu pack: nvidia found, downloading");
        models.AutoStart(WhisperPack);
    }

    void Use(ModelStore.Model pack)
    {
        var dir = models.Directory(pack);
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
