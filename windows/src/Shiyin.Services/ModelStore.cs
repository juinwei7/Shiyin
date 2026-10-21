using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Shiyin.Localization;
using Shiyin.Model;

namespace Shiyin.Services;

/// <summary>
/// 模型的下載、續傳、驗證（M§7、§18.2、R10.3；design-windows §8、§9）。轉錄模型跟 Mac 同一份網址和 SHA-256；
/// Windows 另外有快速辨識（SenseVoice＋VAD）和說話者辨識（sherpa-onnx）的模型。一個模型可以有好幾個檔案，全部下載、驗證過才算裝好。
/// 下載中的檔案是 models\.&lt;檔名&gt;.part，暫停、中斷後用 HTTP Range 接著下載
/// </summary>
public sealed class ModelStore
{
    public enum Model { Breeze, Japanese, LanguageDetection, FastRecognition, Diarization, GpuCuda }

    /// <summary>壓縮檔裡要解出來的檔案：壓縮檔裡的路徑、解到哪裡（models 底下）、大小</summary>
    public sealed record ArchiveEntry(string Entry, string Path, long Size);

    /// <summary>Extract 不是 null 時是壓縮檔：驗證後解出這些檔案、刪掉壓縮檔，解出來的都在才算裝好</summary>
    public sealed record ModelFile(string Path, string Url, long Size, string Sha256, IReadOnlyList<ArchiveEntry>? Extract = null);

    /// <summary>Folder：這個模型自己的資料夾（models 底下），刪除時整個刪掉（裡面可能有拾音另外放的檔案）</summary>
    public sealed record ModelInfo(string Name, IReadOnlyList<ModelFile> Files, string? Folder = null)
    {
        public long Size => Files.Sum(f => f.Size);
    }

    public abstract record State
    {
        public sealed record Missing : State;
        public sealed record Downloading(long Received, long Total) : State;
        public sealed record Paused(long Received, long Total) : State;
        public sealed record Verifying : State;
        public sealed record Installed : State;
        public sealed record Failed(string Message) : State;

        public bool IsBusy => this is Downloading or Verifying;
    }

    const string HuggingFace = "https://huggingface.co";

    /// <summary>社群轉檔的 Breeze ASR 25 GGML q8_0，固定 commit（同 Mac 的 ModelStore.breeze）</summary>
    public static readonly ModelInfo Breeze = new(L.T("Breeze ASR 25（q8_0）"), [new("breeze-asr-25-q8_0.bin",
        $"{HuggingFace}/shdennlin/breeze-asr-25-ggml/resolve/36c726093efe1760d1dd39c3cfe8b6a7282437d1/ggml-breeze-asr-25-q8_0.bin",
        1_656_129_691, "d4b187c40ffbf1f620734b77821e2ca8a97c8ecf5754d02b2a175069348cafcf")]);

    public static readonly ModelInfo Japanese = new(L.T("日文轉錄模型 Whisper large-v3-turbo（q5_0）"), [new("ggml-large-v3-turbo-q5_0.bin",
        $"{HuggingFace}/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/ggml-large-v3-turbo-q5_0.bin",
        574_041_195, "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2")]);

    public static readonly ModelInfo LanguageDetection = new(L.T("語言偵測模型 Whisper base（q5_1）"), [new("ggml-base-q5_1.bin",
        $"{HuggingFace}/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/ggml-base-q5_1.bin",
        59_707_625, "422f1ae452ade6f30a004d7e5c6a43195e4433bc370bf23fac9cc591f01a8898")]);

    /// <summary>快速辨識、即時逐字稿：SenseVoice-Small int8（FunASR Model License，原版 2024-07-17，D61）＋ Silero VAD（MIT），design-windows §7、§8</summary>
    public static readonly ModelInfo FastRecognition = new(L.T("快速辨識模型 SenseVoice"), [
        new("sensevoice/model.int8.onnx",
            $"{HuggingFace}/csukuangfj/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17/resolve/2365baeacb507f821a0c8120fcee3d484dba7a07/model.int8.onnx",
            239_233_841, "c71f0ce00bec95b07744e116345e33d8cbbe08cef896382cf907bf4b51a2cd51"),
        new("sensevoice/tokens.txt",
            $"{HuggingFace}/csukuangfj/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17/resolve/2365baeacb507f821a0c8120fcee3d484dba7a07/tokens.txt",
            315_894, "f449eb28dc567533d7fa59be34e2abca8784f771850c78a47fb731a31429a1dc"),
        new("vad/silero_vad.onnx", "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx",
            643_854, "9e2449e1087496d8d4caba907f23e0bd3f78d91fa552479bb9c23ac09cbb1fd6"),
    ]);

    /// <summary>說話者辨識：pyannote segmentation-3.0（MIT）＋聲紋嵌入（design-windows §9、D47、D58）</summary>
    public static readonly ModelInfo Diarization = new(L.T("說話者辨識模型"), [
        new("diarization/segmentation.onnx",
            $"{HuggingFace}/csukuangfj/sherpa-onnx-pyannote-segmentation-3-0/resolve/9403a6902bb58e3d5ae8c7e77c3422de279db2e0/model.onnx",
            5_992_913, "220ad67ca923bef2fa91f2390c786097bf305bceb5e261d4af67b38e938e1079"),
        // 3D-Speaker ERes2Net base（Apache-2.0）：中文會議分得開（D58）
        new("diarization/embedding.onnx",
            $"{HuggingFace}/csukuangfj/speaker-embedding-models/resolve/0743f301363dec56491a490f6d6cbc9d67f9a3bf/3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx",
            39_593_761, "1a331345f04805badbb495c775a6ddffcdd1a732567d5ec8b3d5749e3c7a5e4b"),
    ]);

    // 要在 GpuCuda 之前宣告：靜態欄位照順序初始化
    static readonly (string Name, long Size)[] CudaFiles =
    [
        ("whisper.dll", 1_312_256), ("ggml.dll", 60_928), ("ggml-base.dll", 677_376), ("ggml-cuda.dll", 569_981_440),
        ("ggml-cpu-alderlake.dll", 1_004_544), ("ggml-cpu-cannonlake.dll", 1_047_040), ("ggml-cpu-cascadelake.dll", 1_045_504),
        ("ggml-cpu-haswell.dll", 1_005_568), ("ggml-cpu-icelake.dll", 1_045_504), ("ggml-cpu-sandybridge.dll", 1_001_472),
        ("ggml-cpu-skylakex.dll", 1_047_040), ("ggml-cpu-sse42.dll", 838_656), ("ggml-cpu-x64.dll", 839_168),
        ("cudart64_12.dll", 553_984), ("cublas64_12.dll", 100_033_536), ("cublasLt64_12.dll", 473_551_360),
    ];

    /// <summary>
    /// NVIDIA 顯示卡加速（R27 R4.6、design-windows §8、D64）：whisper.cpp 官方同一版（b5454）的 CUDA 12.4 版，只解出 whisper 要的 DLL（約 1.1 GB）。
    /// 整套 whisper 都用這裡的（同一次建置的 ggml 才能搭配 ggml-cuda）
    /// </summary>
    public static readonly ModelInfo GpuCuda = new(L.T("NVIDIA 顯示卡加速（CUDA）"), [new("gpu-cuda12/whisper-cuda.zip",
        "https://github.com/ggml-org/whisper.cpp/releases/download/b5454/whisper-bin-win-cuda-12.4.0-x64.zip",
        684_913_404, "afef0b881c500958921c3f5523b50e59ee2ec9b6f5cbd25b324c51ed308a957a",
        [.. CudaFiles.Select(f => new ArchiveEntry("Release/" + f.Name, "gpu-cuda12/" + f.Name, f.Size))])], Folder: "gpu-cuda12");

    public static ModelInfo Info(Model model) => model switch
    {
        Model.Breeze => Breeze,
        Model.GpuCuda => GpuCuda,
        Model.Japanese => Japanese,
        Model.LanguageDetection => LanguageDetection,
        Model.FastRecognition => FastRecognition,
        _ => Diarization,
    };

    /// <summary>正式轉錄用哪個模型：日文會議用 large-v3-turbo，中文、英文用 Breeze</summary>
    public static Model Transcription(MeetingLanguage language) => language == MeetingLanguage.Ja ? Model.Japanese : Model.Breeze;

    readonly string root;
    readonly HttpClient http;
    readonly object gate = new();
    readonly Dictionary<Model, State> states = [];
    readonly Dictionary<Model, CancellationTokenSource> tasks = [];
    /// <summary>每個模型最近一次的下載：暫停後馬上再開始時，等上一次真的停了（還在寫 .part）再接著下載</summary>
    readonly Dictionary<Model, Task> downloads = [];

    /// <summary>某個模型的狀態變了；下載中約每 0.25 秒一次。在背景執行緒觸發</summary>
    public event Action<Model>? Changed;
    /// <summary>某個模型裝好了（工作佇列收到後馬上再找工作）</summary>
    public event Action<Model>? Installed;

    /// <summary>DEBUG 的 modelurl：改從別的網址下載，測下載出錯（同 Mac）。只用在只有一個檔案的模型</summary>
    public Dictionary<Model, string> DebugUrls { get; } = [];

    readonly IReadOnlyDictionary<Model, ModelInfo>? catalog;

    /// <summary>catalog：測試換成小檔案；平常用上面固定的網址和 SHA-256</summary>
    public ModelStore(string modelsDirectory, HttpClient http, IReadOnlyDictionary<Model, ModelInfo>? catalog = null)
    {
        root = modelsDirectory;
        this.http = http;
        this.catalog = catalog;
        Refresh();
    }

    public ModelInfo InfoOf(Model model) => catalog?.GetValueOrDefault(model) ?? Info(model);

    public string PathOf(ModelFile file) => PathIn(file.Path);
    string PathIn(string relative) => System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
    public string PathOf(Model model) => PathOf(InfoOf(model).Files[0]);
    public string Directory(Model model) => System.IO.Path.GetDirectoryName(PathOf(model))!;
    string PartPath(ModelFile file) => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(PathOf(file))!, "." + System.IO.Path.GetFileName(file.Path) + ".part");

    public State StateOf(Model model) { lock (gate) return states.GetValueOrDefault(model) ?? new State.Missing(); }
    public bool IsInstalled(Model model) => StateOf(model) is State.Installed;
    public bool HasTranscriptionModel(MeetingLanguage language) => IsInstalled(Transcription(language));

    /// <summary>從檔案重新判斷每個模型的狀態（下載中、驗證中的不動；失敗的保留訊息，使用者按「繼續下載」才重來）</summary>
    public void Refresh()
    {
        foreach (var model in Enum.GetValues<Model>())
        {
            lock (gate)
            {
                var current = states.GetValueOrDefault(model);
                if (current?.IsBusy == true) continue;
                var info = InfoOf(model);
                if (info.Files.All(IsDone))
                    states[model] = new State.Installed();
                else if (info.Files.Any(f => File.Exists(PartPath(f))))
                    states[model] = new State.Paused(Received(info), info.Size);
                else if (current is not State.Failed)
                    states[model] = new State.Missing();
            }
        }
    }

    /// <summary>這個檔案裝好了：一般的檔案大小對；壓縮檔是解出來的檔案都在、大小對</summary>
    bool IsDone(ModelFile file) => file.Extract is { } entries
        ? entries.All(e => new FileInfo(PathIn(e.Path)) is { Exists: true } fi && fi.Length == e.Size)
        : new FileInfo(PathOf(file)) is { Exists: true } done && done.Length == file.Size;

    long Received(ModelInfo info) => info.Files.Sum(f =>
        IsDone(f) ? f.Size : new FileInfo(PartPath(f)) is { Exists: true } part ? part.Length : 0);

    void Set(Model model, State state)
    {
        lock (gate) states[model] = state;
        Changed?.Invoke(model);
    }

    // MARK: 下載

    public void Start(Model model)
    {
        CancellationTokenSource cts;
        lock (gate)
        {
            var state = StateOf(model);
            if (state.IsBusy || state is State.Installed) return;
            cts = new CancellationTokenSource();
            tasks[model] = cts;
        }
        var info = InfoOf(model);
        Set(model, new State.Downloading(Received(info), info.Size));
        Log.Info($"model download started: {model}");
        lock (gate)
        {
            var previous = downloads.GetValueOrDefault(model) ?? Task.CompletedTask;
            downloads[model] = Task.Run(async () =>
            {
                try { await previous; } catch { }
                await Download(model, info, cts.Token);
            });
        }
    }

    /// <summary>目前這個模型的下載（測試等它停下來用）</summary>
    internal Task Pending(Model model) { lock (gate) return downloads.GetValueOrDefault(model) ?? Task.CompletedTask; }

    /// <summary>需要時自動下載（日文會議第一次轉錄）：只在還沒下載過時開始，使用者暫停或下載失敗就不自動重來</summary>
    public void AutoStart(Model model)
    {
        if (StateOf(model) is State.Missing) Start(model);
    }

    public void Pause(Model model)
    {
        CancellationTokenSource? cts;
        lock (gate)
        {
            if (StateOf(model) is not State.Downloading || !tasks.Remove(model, out cts)) return;
        }
        cts.Cancel();
        var info = InfoOf(model);
        Set(model, new State.Paused(Received(info), info.Size));
    }

    public void Delete(Model model)
    {
        CancellationTokenSource? cts;
        lock (gate) tasks.Remove(model, out cts);
        cts?.Cancel();
        var info = InfoOf(model);
        foreach (var file in info.Files)
        {
            TryDelete(PathOf(file));
            TryDelete(PartPath(file));
            foreach (var entry in file.Extract ?? []) TryDelete(PathIn(entry.Path));
        }
        if (info.Folder is { } folder)
        {
            try { System.IO.Directory.Delete(PathIn(folder), recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Error($"model folder delete failed: {e.GetType().Name}"); }
        }
        Set(model, new State.Missing());
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    async Task Download(Model model, ModelInfo info, CancellationToken cancel)
    {
        try
        {
            var before = 0L;
            foreach (var file in info.Files)
            {
                var path = PathOf(file);
                if (IsDone(file))
                {
                    before += file.Size;
                    continue;
                }
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                var url = DebugUrls.TryGetValue(model, out var custom) && info.Files.Count == 1 ? custom : file.Url;
                await DownloadFile(model, info, file, url, before, cancel);
                // 暫停、刪除之後不能再改狀態（暫停時剛好下載完的檔案留著 .part，下次直接驗證）
                cancel.ThrowIfCancellationRequested();
                Set(model, new State.Verifying());
                var part = PartPath(file);
                if (await Sha256(part, cancel) != file.Sha256)
                {
                    TryDelete(part);
                    Finish(model, new State.Failed(L.T("下載的模型檔驗證失敗（SHA-256 不符），請重新下載")));
                    return;
                }
                cancel.ThrowIfCancellationRequested();
                if (file.Extract is { } entries)
                {
                    await Task.Run(() => Extract(part, entries, cancel), cancel);
                    TryDelete(part);
                }
                else File.Move(part, path, overwrite: true);
                before += file.Size;
                Set(model, new State.Downloading(before, info.Size));
            }
            cancel.ThrowIfCancellationRequested();
            Finish(model, new State.Installed());
            Log.Info($"model installed: {model}");
            Installed?.Invoke(model);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // 暫停或刪除，狀態已經設好
        }
        catch (ModelDownloadException e)
        {
            Finish(model, new State.Failed(e.Message));
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException)
        {
            // 系統的錯誤說明本身可能已經有句號
            var reason = e.Message.TrimEnd('。', '.', ' ');
            var resumable = InfoOf(model).Files.Any(f => File.Exists(PartPath(f)));
            Finish(model, new State.Failed(resumable
                ? L.F("下載中斷：%@。按「繼續下載」會從中斷的地方接著下載", reason)
                : L.F("下載中斷：%@。按「繼續下載」會重新下載", reason)));
        }
    }

    /// <summary>從壓縮檔解出指定的檔案：先寫成 .part，大小對了才換上</summary>
    void Extract(string archive, IReadOnlyList<ArchiveEntry> entries, CancellationToken cancel)
    {
        using var zip = ZipFile.OpenRead(archive);
        foreach (var e in entries)
        {
            cancel.ThrowIfCancellationRequested();
            var entry = zip.GetEntry(e.Entry) ?? throw new ModelDownloadException(L.T("下載的壓縮檔裡少了需要的檔案，請重新下載"));
            var target = PathIn(e.Path);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            var temp = target + ".part";
            entry.ExtractToFile(temp, overwrite: true);
            if (new FileInfo(temp).Length != e.Size)
            {
                TryDelete(temp);
                throw new ModelDownloadException(L.T("下載的壓縮檔裡少了需要的檔案，請重新下載"));
            }
            File.Move(temp, target, overwrite: true);
        }
    }

    void Finish(Model model, State state)
    {
        lock (gate) tasks.Remove(model);
        Set(model, state);
    }

    sealed class ModelDownloadException(string message) : Exception(message);

    async Task DownloadFile(Model model, ModelInfo info, ModelFile file, string url, long before, CancellationToken cancel)
    {
        var part = PartPath(file);
        var existing = new FileInfo(part) is { Exists: true } fi ? fi.Length : 0;
        if (existing > file.Size) { TryDelete(part); existing = 0; }
        if (existing == file.Size) return;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // 留下的 .part 跟伺服器上的檔案對不起來：從頭下載
            TryDelete(part);
            await DownloadFile(model, info, file, url, before, cancel);
            return;
        }
        // 伺服器回傳錯誤頁（404、500…）不要拿去驗 SHA-256（會誤報成檔案損壞）
        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
        {
            TryDelete(part);
            throw new ModelDownloadException(L.F("下載失敗：伺服器回傳 HTTP %lld，請稍後再試", (int)response.StatusCode));
        }
        // 伺服器不支援續傳（回 200）就從頭寫
        var append = response.StatusCode == HttpStatusCode.PartialContent && existing > 0;
        if (!append) existing = 0;
        await using var output = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        await using var input = await response.Content.ReadAsStreamAsync(cancel);
        var buffer = new byte[1 << 16];
        var received = existing;
        var lastReport = DateTime.UtcNow;
        // 整個下載沒有逾時（模型很大），但 30 秒沒收到資料就算中斷
        while (true)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            idle.CancelAfter(TimeSpan.FromSeconds(30));
            int n;
            try { n = await input.ReadAsync(buffer, idle.Token); }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                throw new IOException(L.T("連線逾時"));
            }
            if (n == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, n), cancel);
            received += n;
            if (DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(250))
            {
                lastReport = DateTime.UtcNow;
                Set(model, new State.Downloading(before + received, info.Size));
            }
        }
        if (received != file.Size) throw new IOException(L.T("下載的檔案不完整"));
    }

    /// <summary>檔案的 SHA-256（小寫十六進位），串流計算；更新檔的驗證也用這個</summary>
    public static async Task<string> Sha256(string path, CancellationToken cancel = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancel));
    }
}
