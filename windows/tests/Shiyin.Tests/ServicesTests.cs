using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Shiyin.Data;
using Shiyin.IO;
using Shiyin.LLM;
using Shiyin.Model;
using Shiyin.Services;
using Shiyin.Speakers;
using Shiyin.Transcribe;
using Shiyin.Whisper;
using Xunit;

namespace Shiyin.Tests;

/// <summary>不碰畫面的 app 邏輯（design-windows §1）：設定、模型下載、匯入、封包、轉錄流程、工作佇列</summary>
public sealed class ServicesTests : IDisposable
{
    readonly TempHome home = new();
    readonly ShiyinPaths paths;
    readonly AppDatabase db;
    readonly HttpClient http = new();
    readonly TestServer server = new();

    public ServicesTests()
    {
        paths = new ShiyinPaths(home.Path);
        paths.EnsureDirectories();
        db = new AppDatabase(paths.Database, paths.Backups);
        db.SeedTemplates();
    }

    public void Dispose()
    {
        server.Dispose();
        http.Dispose();
        db.Dispose();
        home.Dispose();
    }

    static void WriteWav(string path, int channels, double seconds, float amplitude = 0.1f)
    {
        var count = (int)(seconds * 16000);
        var data = Enumerable.Range(0, channels).Select(c => Enumerable.Range(0, count).Select(i => amplitude * MathF.Sin(i * 0.05f * (c + 1))).ToArray()).ToList();
        using var writer = new WavWriter(path, channels);
        writer.Write(data, count);
    }

    // MARK: 設定

    [Fact]
    public void SettingsPersistAndOverridesStayInMemory()
    {
        var file = Path.Combine(home.Path, "settings.json");
        var settings = AppSettings.Load(file, AppSettings.ParseOverrides(["--set", "llmProvider=none", "--set", "autoSummarize=NO", "other"]));
        Assert.Equal(LLMProvider.None, settings.Provider);
        Assert.False(settings.AutoSummarize);
        Assert.True(settings.CompressAudio);
        settings.Set(AppSettings.CompressAudioKey, false);
        settings.Set(AppSettings.ProviderKey, "gemini");          // 被啟動參數覆蓋的設定：只在記憶體
        settings.Set(AppSettings.RecordSourceKey, "both");
        var again = AppSettings.Load(file);
        Assert.False(again.CompressAudio);
        Assert.Equal("both", again.RecordSource);
        Assert.Equal(LLMProvider.Claude, again.Provider);
        Assert.True(again.AutoSummarize);
        Assert.Equal(LLMProvider.Gemini, settings.Provider);
        // 壞掉的檔案當成沒有設定
        File.WriteAllText(file, "{not json");
        Assert.Equal("mic", AppSettings.Load(file).RecordSource);
    }

    [Fact]
    public void PathsRequireHomeOutsideWindows()
    {
        if (OperatingSystem.IsWindows()) return;
        var saved = Environment.GetEnvironmentVariable("SHIYIN_HOME");
        try
        {
            Environment.SetEnvironmentVariable("SHIYIN_HOME", null);
            Assert.Throws<InvalidOperationException>(ShiyinPaths.FromEnvironment);
            Environment.SetEnvironmentVariable("SHIYIN_HOME", home.Path);
            Assert.Equal(home.Path, ShiyinPaths.FromEnvironment().Home);
        }
        finally { Environment.SetEnvironmentVariable("SHIYIN_HOME", saved); }
    }

    // MARK: 模型下載

    sealed record Served(byte[] Data, bool SupportsRange = true, int Status = 200, int DelayPerChunkMs = 0);

    void Serve(Dictionary<string, Served> files, List<string>? ranges = null)
    {
        server.Handle = async c =>
        {
            var path = c.Request.Url!.AbsolutePath;
            if (!files.TryGetValue(path, out var file)) { c.Response.StatusCode = 404; c.Response.Close(); return; }
            if (file.Status != 200) { c.Response.StatusCode = file.Status; c.Response.Close(); return; }
            var start = 0;
            var range = c.Request.Headers["Range"];
            lock (files) ranges?.Add(range ?? "");
            if (range is not null && file.SupportsRange)
            {
                start = int.Parse(range["bytes=".Length..].TrimEnd('-'));
                if (start >= file.Data.Length) { c.Response.StatusCode = 416; c.Response.Close(); return; }
                c.Response.StatusCode = 206;
                c.Response.Headers["Content-Range"] = $"bytes {start}-{file.Data.Length - 1}/{file.Data.Length}";
            }
            c.Response.ContentLength64 = file.Data.Length - start;
            for (var i = start; i < file.Data.Length; i += 4096)
            {
                await c.Response.OutputStream.WriteAsync(file.Data.AsMemory(i, Math.Min(4096, file.Data.Length - i)));
                if (file.DelayPerChunkMs > 0) await Task.Delay(file.DelayPerChunkMs);
            }
            c.Response.Close();
        };
    }

    static byte[] Bytes(int n, int seed) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }
    static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    static async Task<ModelStore.State> WaitFor(ModelStore store, ModelStore.Model model, Func<ModelStore.State, bool> done, int seconds = 20)
    {
        for (var i = 0; i < seconds * 20; i++)
        {
            var s = store.StateOf(model);
            if (done(s)) return s;
            await Task.Delay(50);
        }
        return store.StateOf(model);
    }

    [Fact]
    public async Task ModelDownloadResumesAndVerifies()
    {
        var a = Bytes(1_000_000, 1);
        var b = Bytes(5_000, 2);
        var ranges = new List<string>();
        Serve(new() { ["/a.bin"] = new(a, DelayPerChunkMs: 5), ["/b.txt"] = new(b) }, ranges);
        var catalog = new Dictionary<ModelStore.Model, ModelStore.ModelInfo>
        {
            [ModelStore.Model.FastRecognition] = new("fast", [new("sensevoice/a.bin", server.Url + "/a.bin", a.Length, Sha(a)),
                                                              new("sensevoice/b.txt", server.Url + "/b.txt", b.Length, Sha(b))]),
        };
        var store = new ModelStore(paths.Models, http, catalog);
        var installed = new TaskCompletionSource();
        store.Installed += _ => installed.TrySetResult();
        store.Start(ModelStore.Model.FastRecognition);
        await WaitFor(store, ModelStore.Model.FastRecognition, s => s is ModelStore.State.Downloading { Received: > 20_000 });
        store.Pause(ModelStore.Model.FastRecognition);
        Assert.IsType<ModelStore.State.Paused>(store.StateOf(ModelStore.Model.FastRecognition));
        await store.Pending(ModelStore.Model.FastRecognition);
        // 重開 app：從 .part 判斷是暫停的
        var reopened = new ModelStore(paths.Models, http, catalog);
        reopened.Installed += _ => installed.TrySetResult();
        Assert.IsType<ModelStore.State.Paused>(reopened.StateOf(ModelStore.Model.FastRecognition));
        reopened.Start(ModelStore.Model.FastRecognition);
        var state = await WaitFor(reopened, ModelStore.Model.FastRecognition, s => s is ModelStore.State.Installed or ModelStore.State.Failed);
        Assert.True(state is ModelStore.State.Installed, state.ToString());
        await installed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(a, File.ReadAllBytes(Path.Combine(paths.Models, "sensevoice", "a.bin")));
        Assert.Equal(b, File.ReadAllBytes(Path.Combine(paths.Models, "sensevoice", "b.txt")));
        Assert.Empty(Directory.GetFiles(paths.Models, "*.part", SearchOption.AllDirectories));
        Assert.Contains(ranges, r => r.StartsWith("bytes=") && r != "bytes=0-");
        Assert.True(new ModelStore(paths.Models, http, catalog).IsInstalled(ModelStore.Model.FastRecognition));
    }

    [Fact]
    public async Task ModelDownloadFailures()
    {
        var a = Bytes(10_000, 3);
        Serve(new() { ["/bad.bin"] = new(a), ["/missing.bin"] = new(a, Status: 404), ["/norange.bin"] = new(a, SupportsRange: false) });
        var catalog = new Dictionary<ModelStore.Model, ModelStore.ModelInfo>
        {
            [ModelStore.Model.Breeze] = new("bad", [new("bad.bin", server.Url + "/bad.bin", a.Length, new string('0', 64))]),
            [ModelStore.Model.Japanese] = new("missing", [new("missing.bin", server.Url + "/missing.bin", a.Length, Sha(a))]),
            [ModelStore.Model.LanguageDetection] = new("norange", [new("norange.bin", server.Url + "/norange.bin", a.Length, Sha(a))]),
        };
        var store = new ModelStore(paths.Models, http, catalog);
        store.Start(ModelStore.Model.Breeze);
        var bad = await WaitFor(store, ModelStore.Model.Breeze, s => s is ModelStore.State.Failed or ModelStore.State.Installed);
        Assert.Contains("SHA-256", Assert.IsType<ModelStore.State.Failed>(bad).Message);
        Assert.False(File.Exists(Path.Combine(paths.Models, ".bad.bin.part")));

        store.Start(ModelStore.Model.Japanese);
        var missing = await WaitFor(store, ModelStore.Model.Japanese, s => s is ModelStore.State.Failed or ModelStore.State.Installed);
        Assert.Contains("HTTP 404", Assert.IsType<ModelStore.State.Failed>(missing).Message);
        // 自動下載不會重試失敗過的
        store.AutoStart(ModelStore.Model.Japanese);
        Assert.IsType<ModelStore.State.Failed>(store.StateOf(ModelStore.Model.Japanese));

        // 伺服器不支援續傳：留下的一半從頭寫
        File.WriteAllBytes(Path.Combine(paths.Models, ".norange.bin.part"), a[..4000]);
        store.Refresh();
        Assert.IsType<ModelStore.State.Paused>(store.StateOf(ModelStore.Model.LanguageDetection));
        store.Start(ModelStore.Model.LanguageDetection);
        var norange = await WaitFor(store, ModelStore.Model.LanguageDetection, s => s is ModelStore.State.Failed or ModelStore.State.Installed);
        Assert.IsType<ModelStore.State.Installed>(norange);
        Assert.Equal(a, File.ReadAllBytes(Path.Combine(paths.Models, "norange.bin")));
        store.Delete(ModelStore.Model.LanguageDetection);
        Assert.False(File.Exists(Path.Combine(paths.Models, "norange.bin")));
    }

    // MARK: 匯入、封包

    [Fact]
    public void ImportCopiesAndQueues()
    {
        var settings = AppSettings.InMemory();
        var source = Path.Combine(home.Path, "週會 2026-08-27.wav");
        WriteWav(source, 1, 2);
        var meeting = Importer.ImportFile(source, new ImportOptions { Title = "  週會  ", Folder = "專案", Language = MeetingLanguage.En, MinSpeakers = 3 },
            db, paths, settings);
        Assert.Equal("週會", meeting.Title);
        Assert.True(meeting.TitleLocked);
        Assert.Equal("2026-08-27", meeting.MeetingDate);
        Assert.Equal("en", meeting.Language);
        Assert.Equal(3, meeting.MinSpeakers);
        Assert.Equal($"{meeting.Id}.wav", meeting.AudioFile);
        Assert.True(paths.HasAudio(meeting));
        Assert.True(File.Exists(source));
        Assert.Equal(JobKind.Transcribe, db.LatestJob(meeting.Id!.Value)!.Kind);

        // 搬不過去時刪掉剛建立的會議
        var before = db.MeetingRows().Count;
        Assert.Throws<ImportException>(() => Importer.ImportFile(Path.Combine(home.Path, "nothing.wav"), new ImportOptions(), db, paths, settings, move: true));
        Assert.Equal(before, db.MeetingRows().Count);
    }

    [Fact]
    public void ImportTranscriptFile()
    {
        var settings = AppSettings.InMemory(new Dictionary<string, string> { ["llmProvider"] = "none" });
        var file = Path.Combine(home.Path, "0827.srt");
        File.WriteAllText(file, "1\n00:00:01,000 --> 00:00:03,500\n大家好\n\n2\n00:00:04,000 --> 00:00:06,000\n開始吧\n");
        var meeting = Importer.ImportTranscript(file, "", db, settings);
        var segments = db.Segments(meeting.Id!.Value);
        Assert.Equal(["大家好", "開始吧"], segments.Select(s => s.Text));
        Assert.Equal(6, meeting.DurationSeconds);
        Assert.Null(db.LatestJob(meeting.Id!.Value));
        File.WriteAllText(file, "\n\n");
        Assert.Throws<ImportException>(() => Importer.ImportTranscript(file, "", db, settings));
    }

    Meeting SampleMeeting(bool audio = true)
    {
        var meeting = db.InsertMeeting(new Meeting
        {
            Title = "預算會議", TitleLocked = true, MeetingDate = "2026-10-01", Participants = "小明、小華", SourceFilename = "a.wav",
            Folder = "財務", Language = "zh", UserNotes = "記得帶發票", Channels = "both",
        }, audio ? "wav" : null);
        var id = meeting.Id!.Value;
        if (audio) WriteWav(paths.AudioPath(meeting), 2, 1);
        db.SaveTranscript(id, [
            new Segment { MeetingId = id, Seq = 0, Start = 0, End = 2.5, Text = "第一句", Speaker = 1 },
            new Segment { MeetingId = id, Seq = 1, Start = 2.5, End = 4, Text = "第二句", Speaker = 2, LowConfidence = true },
        ], 4, 2);
        db.RenameSpeaker(id, 1, "小明");
        db.AddBookmark(id, 1.5, "重點");
        db.SaveNotes(id, MeetingNotes.Decode("""{"title":"預算","summary":[{"text":"要省錢","time":"00:01"}],"decisions":[],"actionItems":[],"openQuestions":[],"topics":[],"custom":[],"reviewItems":[]}""")!,
            "claude", "重新整理", NotesSource.Generated);
        return db.Meeting(id)!;
    }

    ExportBundle Bundle(Meeting m) => new(m, db.Segments(m.Id!.Value), db.Speakers(m.Id!.Value).ToDictionary(s => s.Idx, s => s.Name),
        db.Bookmarks(m.Id!.Value), m.DecodedNotes);

    [Fact]
    public void PackageRoundTrip()
    {
        var original = SampleMeeting();
        var file = Path.Combine(home.Path, "會議.shiyin");
        MeetingPackage.Export(Bundle(original), file, paths, "3.6.0");
        using (var zip = ZipFile.OpenRead(file))
            Assert.Equal(["audio.wav", "manifest.json", "meeting.json", "transcript.json"], zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        var imported = MeetingPackage.Import(file, db, paths, "別的資料夾");
        Assert.NotEqual(original.Id, imported.Id);
        Assert.Equal(("預算會議", true, "2026-10-01", "小明、小華", "財務", "package", "both", "記得帶發票", "claude"),
            (imported.Title, imported.TitleLocked, imported.MeetingDate, imported.Participants, imported.Folder, imported.Source, imported.Channels,
             imported.UserNotes, imported.NotesBy));
        Assert.Equal(File.ReadAllBytes(paths.AudioPath(original)), File.ReadAllBytes(paths.AudioPath(imported)));
        var segments = db.Segments(imported.Id!.Value);
        Assert.Equal([("第一句", 1, false), ("第二句", 2, true)], segments.Select(s => (s.Text, s.Speaker ?? 0, s.LowConfidence)));
        Assert.Equal("小明", db.Speakers(imported.Id!.Value).Single(s => s.Idx == 1).Name);
        Assert.Equal([(1.5, "重點")], db.Bookmarks(imported.Id!.Value).Select(b => (b.Time, b.Label)));
        Assert.Equal("要省錢", imported.DecodedNotes!.Summary[0].Text);
    }

    static void WriteZip(string file, Dictionary<string, string> entries)
    {
        File.Delete(file);
        using var zip = ZipFile.Open(file, ZipArchiveMode.Create);
        foreach (var (name, text) in entries)
        {
            using var s = zip.CreateEntry(name).Open();
            s.Write(Encoding.UTF8.GetBytes(text));
        }
    }

    [Fact]
    public void PackageRejectsBadInput()
    {
        var file = Path.Combine(home.Path, "bad.shiyin");
        const string meeting = """{"title":"t","titleLocked":false,"meetingDate":"2026-01-01","participants":"","sourceFilename":"x","templateId":"general","userNotes":"","channels":"mic","speakers":[],"bookmarks":[]}""";
        var before = db.MeetingRows().Count;
        void Rejects(Dictionary<string, string> entries, string message)
        {
            WriteZip(file, entries);
            var e = Assert.Throws<MeetingPackage.PackageException>(() => MeetingPackage.Import(file, db, paths));
            Assert.Contains(message, e.Message);
        }
        Rejects(new() { ["meeting.json"] = meeting }, "manifest.json");
        Rejects(new() { ["manifest.json"] = """{"format":2,"app":"9","exportedAt":"x"}""", ["meeting.json"] = meeting }, "格式 2");
        Rejects(new() { ["manifest.json"] = """{"format":1,"app":"9","exportedAt":"x"}""" }, "meeting.json");
        Rejects(new() { ["manifest.json"] = """{"format":1,"app":"9","exportedAt":"x"}""", ["meeting.json"] = meeting, ["transcript.json"] = "[{" }, "transcript.json");
        // 錄音的檔名不能是路徑；子資料夾裡的同名檔案不算
        Rejects(new() { ["manifest.json"] = """{"format":1,"app":"9","exportedAt":"x","audio":"../../evil.wav"}""", ["meeting.json"] = meeting, ["../../evil.wav"] = "x" }, "錄音檔");
        Rejects(new() { ["manifest.json"] = """{"format":1,"app":"9","exportedAt":"x","audio":"audio.wav"}""", ["meeting.json"] = meeting, ["sub/audio.wav"] = "x" }, "錄音檔");
        Assert.Equal(before, db.MeetingRows().Count);
        // 只有逐字稿的舊封包（沒有 language、folder）
        WriteZip(file, new() { ["manifest.json"] = """{"format":1,"app":"3.0.0","exportedAt":"x"}""", ["meeting.json"] = meeting });
        var imported = MeetingPackage.Import(file, db, paths, "目前的資料夾");
        Assert.Equal(("zh", "目前的資料夾", ""), (imported.Language, imported.Folder, imported.AudioFile));
    }

    // MARK: 轉錄流程

    sealed class FakeEngines : ISpeechEngines
    {
        public List<TranscribedSegment> Segments { get; set; } = [new(0, 3, "大家好", false), new(3, 6, "今天討論預算", false), new(6, 9, "好的", true)];
        public Func<float[], List<TranscribedSegment>>? PerChannel { get; set; }
        public List<SpeakerRules.Turn> Turns { get; set; } = [new(1, 0, 6), new(2, 6, 9)];
        public int? LastMinSpeakers { get; private set; }
        public bool FailDiarize { get; set; }
        public List<string> Calls { get; } = [];

        public (List<TranscribedSegment> Segments, List<int> Seams, int SampleCount) WhisperFile(IAudioStream audio, double chunkSeconds, string model,
            string prompt, string language, WhisperControl control)
        {
            Calls.Add($"whisper-file {language} {prompt}");
            var buffer = new List<float>();
            audio.Fill(buffer, int.MaxValue);
            control.OnProgress(1);
            return (Segments, [], buffer.Count);
        }

        public List<TranscribedSegment> WhisperSamples(float[] samples, IReadOnlyList<(int Start, int End)> chunks, string model, string prompt,
            string language, WhisperControl control)
        {
            Calls.Add("whisper-samples");
            var result = PerChannel?.Invoke(samples) ?? Segments;
            if (result.Count == 0) throw new WhisperException(WhisperErrorKind.Empty);
            return result;
        }

        public List<TranscribedSegment> Fast(IAudioStream audio, MeetingLanguage language, Action<double> progress, CancellationToken cancel)
        {
            Calls.Add($"fast {language.Code()}");
            var buffer = new List<float>();
            audio.Fill(buffer, int.MaxValue);
            return Segments;
        }

        public Diarizer.Output Diarize(float[] samples, int? minSpeakers, Action<double> progress, CancellationToken cancel)
        {
            Calls.Add($"diarize {samples.Length}");
            LastMinSpeakers = minSpeakers;
            if (FailDiarize) throw new InvalidOperationException("boom");
            var speakers = Turns.Select(t => t.Speaker).Distinct().ToList();
            return new Diarizer.Output(Turns, speakers.ToDictionary(s => s, s => new float[] { s, 1, 0 }),
                speakers.ToDictionary(s => s, s => Turns.Where(t => t.Speaker == s).Sum(t => t.End - t.Start)), null, []);
        }
    }

    [Fact]
    public void PipelineTranscribesAndAssignsSpeakers()
    {
        var audio = Path.Combine(home.Path, "a.wav");
        WriteWav(audio, 1, 9);
        var model = Path.Combine(home.Path, "model.bin");
        File.WriteAllText(model, "x");
        var engines = new FakeEngines();
        var stages = new List<string>();
        var options = new TranscriptionOptions { Glossary = ["拾音"], MinSpeakers = 2, AudioSource = "mic" };
        var result = new TranscriptionPipeline(options, new WavDecoder(), engines, model) { Stage = stages.Add }.Run(audio, default)!;
        Assert.Equal([1, 1, 2], result.Speakers);
        Assert.Equal(9, result.Duration, 3);
        Assert.Equal("mic", result.Voices[1].Source);
        Assert.Equal(2, engines.LastMinSpeakers);
        Assert.StartsWith("whisper-file zh ", engines.Calls[0]);
        Assert.Contains("拾音", engines.Calls[0]);
        Assert.Contains("辨識說話者", stages);
        Assert.Equal("breeze", options.TranscribedWith);

        // 說話者辨識失敗不影響逐字稿
        engines.FailDiarize = true;
        result = new TranscriptionPipeline(options, new WavDecoder(), engines, model).Run(audio, default)!;
        Assert.Equal([null, null, null], result.Speakers);
        // 模型檔不在
        Assert.Throws<WhisperException>(() => new TranscriptionPipeline(options, new WavDecoder(), engines, model + "x").Run(audio, default));
        // 快速辨識不用模型檔；取消（ShouldContinue 回 false）回傳 null
        var fast = options with { Engine = TranscriptionOptions.EngineKind.Fast, Language = MeetingLanguage.Ja };
        Assert.Equal("system", fast.TranscribedWith);
        engines.FailDiarize = false;
        Assert.Equal([1, 1, 2], new TranscriptionPipeline(fast, new WavDecoder(), engines, "").Run(audio, default)!.Speakers);
        Assert.Contains("fast ja", engines.Calls);
        Assert.Null(new TranscriptionPipeline(fast, new WavDecoder(), engines, "") { ShouldContinue = () => false }.Run(audio, default));
    }

    [Fact]
    public void PipelineSplitsChannelsAndRemovesEcho()
    {
        var audio = Path.Combine(home.Path, "both.wav");
        WriteWav(audio, 2, 9);
        var model = Path.Combine(home.Path, "model.bin");
        File.WriteAllText(model, "x");
        var engines = new FakeEngines();
        var call = 0;
        // 麥克風：自己說的話＋喇叭的回音；電腦聲音：對方說的話
        engines.PerChannel = _ => call++ == 0
            ? [new(0, 2, "我先報告", false), new(4, 6, "對方說的這一句話", false)]
            : [new(4, 6, "對方說的這一句話", false), new(7, 8, "了解", false)];
        engines.Turns = [new(1, 0, 9)];
        var options = new TranscriptionOptions { SplitChannels = true };
        var result = new TranscriptionPipeline(options, new WavDecoder(), engines, model).Run(audio, default)!;
        Assert.Equal(["我先報告", "對方說的這一句話", "了解"], result.Segments.Select(s => s.Text));
        Assert.Equal([0, 1, 1], result.Channels);
        Assert.Equal([1, 2, 2], result.Speakers);
        Assert.Equal(["mic", "system"], result.Voices.OrderBy(v => v.Key).Select(v => v.Value.Source));
    }

    // MARK: 工作佇列

    sealed class CopyCompressor : IAudioCompressor
    {
        public Task Compress(string wavPath, string m4aPath, CancellationToken cancel)
        {
            File.Copy(wavPath, m4aPath);
            return Task.CompletedTask;
        }
    }

    sealed class MemoryKeys : IKeyStore
    {
        public string? Read(string account) => "sk-test";
        public bool Exists(string account) => true;
    }

    sealed class NoTools : IToolLocator
    {
        public ToolCommand? Find(string name) => null;
    }

    const string NotesJson = """{"title":"預算","summary":[{"text":"決定省錢","time":"00:03"}],"decisions":[],"actionItems":[],"openQuestions":[],"topics":[],"custom":[],"reviewItems":[]}""";

    /// <summary>假的 Anthropic API：依工具名稱回會議紀錄、校正、猜說話者</summary>
    void ServeLLM(List<string> tools)
    {
        server.Handle = async c =>
        {
            using var reader = new StreamReader(c.Request.InputStream);
            var body = JsonNode.Parse(await reader.ReadToEndAsync())!;
            var tool = body["tool_choice"]?["name"]?.GetValue<string>() ?? body["tools"]![0]!["name"]!.GetValue<string>();
            lock (tools) tools.Add(tool);
            var input = tool switch
            {
                "save_corrections" => JsonNode.Parse("""{"corrections":[{"segment":1,"original":"預算","suggestion":"豫算","reason":"測試"},{"segment":9,"original":"x","suggestion":"y","reason":"不在範圍"}]}"""),
                "save_speaker_names" => JsonNode.Parse("""{"speakerNames":[{"speaker":2,"name":"小華","reason":"被叫名字","time":"00:06"}]}"""),
                _ => JsonNode.Parse(NotesJson),
            };
            var reply = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_use", ["name"] = tool, ["input"] = input }) };
            var bytes = Encoding.UTF8.GetBytes(reply.ToJsonString());
            c.Response.ContentType = "application/json";
            await c.Response.OutputStream.WriteAsync(bytes);
            c.Response.Close();
        };
    }

    (JobRunner Runner, FakeEngines Engines, ModelStore Models) Runner(AppSettings settings)
    {
        // 模型都「裝好了」：放跟目錄裡大小一樣的空檔案
        var catalog = Enum.GetValues<ModelStore.Model>().ToDictionary(m => m, m => new ModelStore.ModelInfo(m.ToString(), [new($"{m}.bin", "http://unused", 1, "")]));
        foreach (var m in catalog.Keys) File.WriteAllText(Path.Combine(paths.Models, $"{m}.bin"), "x");
        var models = new ModelStore(paths.Models, http, catalog);
        var llm = new LLMClient(new MemoryKeys(), new NoTools(), new ProcessRunner(), http);
        llm.BaseUrlOverrides[LLMProvider.Anthropic] = server.Url;
        var engines = new FakeEngines();
        var runner = new JobRunner(db, settings, paths, models, llm, new WavDecoder(), engines, new CopyCompressor(), NoPower.Instance)
        {
            IdleDelay = TimeSpan.FromMilliseconds(100),
        };
        return (runner, engines, models);
    }

    static async Task Until(Func<bool> condition, int seconds = 20)
    {
        for (var i = 0; i < seconds * 20 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition());
    }

    [Fact]
    public async Task JobsRunFromTranscriptionToNotes()
    {
        var tools = new List<string>();
        ServeLLM(tools);
        var settings = AppSettings.InMemory(new Dictionary<string, string> { ["llmProvider"] = "anthropic" });
        var (runner, engines, _) = Runner(settings);
        var source = Path.Combine(home.Path, "會議.wav");
        WriteWav(source, 1, 9);
        var meeting = Importer.ImportFile(source, new ImportOptions(), db, paths, settings);
        var id = meeting.Id!.Value;
        var finished = new List<JobKind>();
        runner.Finished += (job, error) => { lock (finished) finished.Add(job.Kind); Assert.Null(error); };
        runner.Start();
        try
        {
            await Until(() => db.Meeting(id)?.Notes is not null && db.Corrections(id).Count > 0
                              && db.Speakers(id).Any(s => s.SuggestedName == "小華") && db.Meeting(id)!.AudioFile.EndsWith(".m4a")
                              && db.ActiveJobs().Count == 0);
        }
        finally { runner.TerminateAll(TimeSpan.FromSeconds(5)); }
        var m = db.Meeting(id)!;
        Assert.Equal("breeze", m.TranscribedWith);
        Assert.Equal(9, m.DurationSeconds);
        Assert.Equal("anthropic", m.NotesBy);
        Assert.Equal("決定省錢", m.DecodedNotes!.Summary[0].Text);
        Assert.Equal([1, 1, 2], db.Segments(id).Select(s => s.Speaker ?? 0));
        Assert.Equal(["豫算"], db.Corrections(id).Select(c => c.Suggestion));
        Assert.False(File.Exists(Path.Combine(paths.Audio, $"{id}.wav")));
        Assert.True(File.Exists(Path.Combine(paths.Audio, $"{id}.m4a")));
        Assert.Equal(["save_corrections", "save_meeting_notes", "save_speaker_names"], tools.Order(StringComparer.Ordinal));
        Assert.Equal([JobKind.Transcribe, JobKind.Summarize], finished.Order());
    }

    [Fact]
    public async Task FastRecognitionWhenModelMissingAndCancel()
    {
        var settings = AppSettings.InMemory(new Dictionary<string, string> { ["llmProvider"] = "none" });
        var (runner, engines, models) = Runner(settings);
        // Breeze 還沒下載：這一場用快速辨識，Breeze 在背景開始下載（網址指到不存在的地方，會失敗）
        File.Delete(Path.Combine(paths.Models, "Breeze.bin"));
        models.Refresh();
        var source = Path.Combine(home.Path, "會議.wav");
        WriteWav(source, 1, 9);
        var meeting = Importer.ImportFile(source, new ImportOptions { Language = MeetingLanguage.En }, db, paths, settings);
        runner.Start();
        try
        {
            await Until(() => db.LatestJob(meeting.Id!.Value)?.Status == JobStatus.Done);
            Assert.Equal("system", db.Meeting(meeting.Id!.Value)!.TranscribedWith);
            Assert.Contains("fast en", engines.Calls);
            Assert.IsNotType<ModelStore.State.Missing>(models.StateOf(ModelStore.Model.Breeze));
        }
        finally { runner.TerminateAll(TimeSpan.FromSeconds(5)); }
    }
}
