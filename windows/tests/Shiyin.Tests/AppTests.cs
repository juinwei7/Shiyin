using System.Text;
using System.Text.Json.Nodes;
using Shiyin.Data;
using Shiyin.IO;
using Shiyin.LLM;
using Shiyin.Model;
using Shiyin.Notes;
using Shiyin.Services;
using Shiyin.Services.Capture;
using Shiyin.Speech;
using Xunit;

namespace Shiyin.Tests;

/// <summary>會議的操作（AppController）、偵測會議、更新的驗證、提問、回報問題</summary>
public sealed class AppTests : IDisposable
{
    readonly TempHome home = new();
    readonly ShiyinPaths paths;
    readonly AppDatabase db;
    readonly HttpClient http = new();
    readonly TestServer server = new();

    public AppTests()
    {
        paths = new ShiyinPaths(home.Path);
        paths.EnsureDirectories();
        db = new AppDatabase(paths.Database, paths.Backups);
    }

    public void Dispose()
    {
        server.Dispose();
        http.Dispose();
        db.Dispose();
        home.Dispose();
    }

    sealed class Keys : IKeyStore
    {
        public string? Read(string account) => "sk-test";
        public bool Exists(string account) => true;
    }

    sealed class NoTools : IToolLocator
    {
        public ToolCommand? Find(string name) => null;
    }

    LLMClient Llm()
    {
        var llm = new LLMClient(new Keys(), new NoTools(), new ProcessRunner(), http);
        llm.BaseUrlOverrides[LLMProvider.Anthropic] = server.Url;
        return llm;
    }

    static void WriteWav(string path, double seconds)
    {
        var count = (int)(seconds * 16000);
        using var writer = new WavWriter(path, 1);
        writer.Write([Enumerable.Range(0, count).Select(i => 0.1f * MathF.Sin(i * 0.05f)).ToArray()], count);
    }

    AppController Controller(AppSettings settings, out Recorder recorder)
    {
        var catalog = Enum.GetValues<ModelStore.Model>().ToDictionary(m => m, m => new ModelStore.ModelInfo(m.ToString(), [new($"{m}.bin", "http://unused", 1, "")]));
        foreach (var m in catalog.Keys) File.WriteAllText(Path.Combine(paths.Models, $"{m}.bin"), "x");
        var models = new ModelStore(paths.Models, http, catalog);
        var runner = new JobRunner(db, settings, paths, models, Llm(), new WavDecoder(), new NoEngines(), new NoCompressor(), NoPower.Instance);
        recorder = new Recorder(settings, paths, NoPower.Instance, new Recorder.Hooks((_, _) => [], _ => null, null, _ => null));
        return new AppController(db, settings, paths, models, runner, recorder, new WavDecoder(), null, "3.6.0");
    }

    sealed class NoEngines : ISpeechEngines
    {
        public (List<Transcribe.TranscribedSegment> Segments, List<int> Seams, int SampleCount) WhisperFile(Whisper.IAudioStream audio, double chunkSeconds,
            string model, string prompt, string language, Whisper.WhisperControl control) => throw new NotSupportedException();
        public List<Transcribe.TranscribedSegment> WhisperSamples(float[] samples, IReadOnlyList<(int Start, int End)> chunks, string model, string prompt,
            string language, Whisper.WhisperControl control) => throw new NotSupportedException();
        public List<Transcribe.TranscribedSegment> Fast(Whisper.IAudioStream audio, MeetingLanguage language, Action<double> progress, CancellationToken cancel) =>
            throw new NotSupportedException();
        public Speakers.Diarizer.Output Diarize(float[] samples, int? minSpeakers, Action<double> progress, CancellationToken cancel) =>
            throw new NotSupportedException();
    }

    sealed class NoCompressor : IAudioCompressor
    {
        public Task Compress(string wavPath, string m4aPath, CancellationToken cancel) => Task.CompletedTask;
    }

    // MARK: 錄音存檔、復原

    [Fact]
    public async Task RecordingIsSavedAndQueued()
    {
        var settings = AppSettings.InMemory(new Dictionary<string, string> { ["liveTranscription"] = "NO", ["autoDetectLanguage"] = "NO" });
        var controller = Controller(settings, out var recorder);
        var source = Path.Combine(home.Path, "src.wav");
        WriteWav(source, 3);
        var revealed = new List<long>();
        controller.Reveal += revealed.Add;
        var id = controller.StartSimulatedRecording(source, 10, false, "專案")!.Value;
        Assert.Equal("recording", db.Meeting(id)!.Source);
        Assert.Equal("專案", db.Meeting(id)!.Folder);
        for (var i = 0; i < 200 && recorder.Elapsed < 2.9; i++) await Task.Delay(20);
        recorder.Stop();
        var meeting = db.Meeting(id)!;
        Assert.Equal($"{id}.wav", meeting.AudioFile);
        Assert.Equal(3, meeting.DurationSeconds);
        Assert.Equal(JobKind.Transcribe, db.LatestJob(id)!.Kind);
        Assert.Empty(Directory.GetFiles(paths.Recording));
        Assert.Equal(id, revealed[^1]);

        // 太短（不到 0.5 秒）的錄音：會議刪掉
        var shortSource = Path.Combine(home.Path, "short.wav");
        WriteWav(shortSource, 0.2);
        var shortId = controller.StartSimulatedRecording(shortSource, 10, false, "")!.Value;
        await Task.Delay(300);
        recorder.Stop();
        Assert.Null(db.Meeting(shortId));
    }

    [Fact]
    public void RecoversCrashedRecordings()
    {
        var settings = AppSettings.InMemory();
        var controller = Controller(settings, out _);
        // 有會議的錄音（標頭沒更新到）、找不到會議的錄音、錄音中當掉沒有檔案的空會議
        var meeting = db.InsertMeeting(new Meeting { Source = "recording", SourceFilename = "錄音" }, null);
        var file = Path.Combine(paths.Recording, $"{meeting.Id}-{Guid.NewGuid()}.wav");
        WriteWav(file, 2);
        var bytes = File.ReadAllBytes(file);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), 0);
        File.WriteAllBytes(file, bytes);
        WriteWav(Path.Combine(paths.Recording, $"9999-{Guid.NewGuid()}.wav"), 1);
        var empty = db.InsertMeeting(new Meeting { Source = "recording", SourceFilename = "空的" }, null);
        var notices = new List<string>();
        controller.Notice += notices.Add;
        controller.RecoverRecordings();
        var recovered = db.Meeting(meeting.Id!.Value)!;
        Assert.Equal("復原的錄音", recovered.Title);
        Assert.Equal($"{meeting.Id}.wav", recovered.AudioFile);
        using (var reader = new WavReader(paths.AudioPath(recovered))) Assert.Equal(32000, reader.Frames);
        Assert.Contains(db.MeetingRows(), r => r.Meeting.SourceFilename == "復原的錄音.wav");
        Assert.Null(db.Meeting(empty.Id!.Value));
        Assert.Empty(Directory.GetFiles(paths.Recording));
        Assert.Contains("找到上次沒有正常結束的錄音，已經復原並開始轉錄。", notices);
    }

    [Fact]
    public void TrashAndChecksBeforeActions()
    {
        var settings = AppSettings.InMemory(new Dictionary<string, string> { ["llmProvider"] = "none" });
        var controller = Controller(settings, out _);
        var notices = new List<string>();
        var errors = new List<string>();
        controller.Notice += notices.Add;
        controller.Error += errors.Add;
        var meeting = db.InsertMeeting(new Meeting { SourceFilename = "a.wav" }, "wav");
        var id = meeting.Id!.Value;
        controller.Retranscribe(id);
        Assert.Contains("這場會議沒有錄音，沒辦法重新轉錄", notices);
        controller.Resummarize(id);
        Assert.Contains("還沒有逐字稿或筆記可以整理", notices);
        db.SaveTranscript(id, [new Segment { MeetingId = id, Text = "x", End = 1 }], 1, 0);
        controller.Resummarize(id);
        Assert.Contains("整理工具設定為「不整理」，請先到設定改成 Claude、Codex 或 API", errors);
        controller.MoveToTrash(id);
        Assert.NotNull(db.Meeting(id)!.DeletedAt);
        controller.Resummarize(id);
        Assert.Contains("垃圾桶裡的會議要先還原才能操作", notices);
        controller.Restore(id);
        Assert.Null(db.Meeting(id)!.DeletedAt);
        controller.DeletePermanently(id);
        Assert.Null(db.Meeting(id));
    }

    [Fact]
    public void SpeakerEditsAndRename()
    {
        var settings = AppSettings.InMemory(new Dictionary<string, string> { ["llmProvider"] = "none" });
        var controller = Controller(settings, out _);
        var id = db.InsertMeeting(new Meeting { SourceFilename = "a" }, null).Id!.Value;
        db.SaveTranscript(id, [
            new Segment { MeetingId = id, Seq = 0, Start = 0, End = 2, Text = "一", Speaker = 1 },
            new Segment { MeetingId = id, Seq = 1, Start = 2, End = 4, Text = "二", Speaker = 2 },
            new Segment { MeetingId = id, Seq = 2, Start = 4, End = 6, Text = "三", Speaker = 2 },
        ], 6, 2);
        controller.RenameSpeaker(id, 1, "  小明  ");
        Assert.Equal("小明", db.Speakers(id).Single(s => s.Idx == 1).Name);
        controller.RenameSpeaker(id, 1, "");
        Assert.Equal("說話者 1", db.Speakers(id).Single(s => s.Idx == 1).Name);
        controller.MergeSpeaker(id, 2, 1);
        Assert.All(db.Segments(id), s => Assert.Equal(1, s.Speaker));
        controller.UndoSpeakerEdit(id);
        Assert.Equal([1, 2, 2], db.Segments(id).Select(s => s.Speaker ?? 0));
        var detail = MeetingDetail.Load(db, id)!;
        Assert.Equal(detail.Segments.Skip(1).Select(s => s.Id!.Value), detail.SpeakerRun(detail.Segments[2].Id!.Value));
        Assert.Equal(detail.Segments[1].Id, detail.SegmentId(2.5));
        controller.ReassignSegments(id, [detail.Segments[2].Id!.Value], null);
        Assert.Equal([1, 2, 3], db.Segments(id).Select(s => s.Speaker ?? 0));
    }

    [Fact]
    public void SuggestedFilenamesAreSafeOnWindows()
    {
        var meeting = new Meeting { MeetingDate = "2026-10-21", Title = "A/B: 預算?" };
        Assert.Equal("2026-10-21 A／B： 預算？（逐字稿）.txt", AppController.SuggestedFilename(ExportItem.Transcript, "txt", meeting));
        Assert.Equal("2026-10-21 A／B： 預算？.md", AppController.SuggestedFilename(ExportItem.Notes, "md", meeting));
        Assert.Equal(["第 2 週", "第 10 週", "週會"], new[] { "第 10 週", "週會", "第 2 週" }.Order(NaturalComparer.Instance));
    }

    // MARK: 偵測會議

    sealed class FakeUsage : IMicrophoneUsage
    {
        public HashSet<string> Apps { get; } = [];
        public IReadOnlySet<string> AppsUsingMicrophone() => Apps;
    }

    [Fact]
    public void MeetingDetectorTiming()
    {
        var now = new DateTime(2026, 10, 21, 9, 0, 0, DateTimeKind.Utc);
        var usage = new FakeUsage();
        var settings = AppSettings.InMemory();
        var detector = new MeetingDetector(usage, settings, () => now);
        var recording = false;
        detector.IsRecording = () => recording;
        var prompts = new List<MeetingDetector.Prompt>();
        detector.Prompted += prompts.Add;
        void Advance(double seconds) { for (var t = 0.0; t < seconds; t += 2) { now = now.AddSeconds(2); detector.Tick(); } }

        usage.Apps.Add("C:#Program Files#Zoom#bin#Zoom.exe".Split('#')[^1].ToLowerInvariant());
        detector.Tick();
        Advance(4);
        Assert.Empty(prompts);
        Advance(2);
        var start = Assert.IsType<MeetingDetector.Prompt.Start>(Assert.Single(prompts));
        Assert.Equal("Zoom", start.App.Name);
        Advance(20);
        Assert.Single(prompts);            // 同一次通話只提醒一次

        // 開始錄音，會議結束（停 10 秒）提醒停止
        recording = true;
        Advance(4);
        usage.Apps.Clear();
        Advance(8);
        Assert.Single(prompts);
        Advance(4);
        Assert.IsType<MeetingDetector.Prompt.Stop>(prompts[^1]);
        recording = false;
        Advance(2);

        // 不要再提醒；封裝的 app 用套件家族名稱比對
        detector.Mute("msteams_8wekyb3d8bbwe");
        usage.Apps.Add("MSTeams_8wekyb3d8bbwe");
        Advance(10);
        Assert.Equal(2, prompts.Count);
        Assert.Equal(["msteams_8wekyb3d8bbwe"], AppSettings.InMemory().StringList("x").Concat(settings.StringList(MeetingDetector.MutedKey)));
        detector.Unmute("msteams_8wekyb3d8bbwe");
        Assert.Empty(settings.StringList(MeetingDetector.MutedKey));
        Assert.Equal("Chrome（可能是 Google Meet）", MeetingDetector.DisplayName("chrome.exe"));
        Assert.Equal("thebrowsercompany.arc", MeetingDetector.Known("TheBrowserCompany.Arc_ttt1ap7aakyb4"));
        Assert.Null(MeetingDetector.Known("notepad.exe"));
    }

    // MARK: 更新

    sealed class FakeUpdates(string version, Dictionary<string, string> files, byte[] manifest, string signature) : IUpdatePlatform
    {
        public string CurrentVersion => "3.6.0";
        public string Arch => "x64";
        public bool Applied { get; private set; }
        public bool Deleted { get; private set; }
        public Task<(string Version, string? Notes)?> Check(CancellationToken cancel) => Task.FromResult<(string, string?)?>((version, "說明"));
        public Task<IReadOnlyDictionary<string, string>> Download(string v, Action<int> progress, CancellationToken cancel) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(files);
        public Task<(byte[] Manifest, string Signature)> FetchManifest(string v, CancellationToken cancel) => Task.FromResult((manifest, signature));
        public void DeleteDownloads(string v) => Deleted = true;
        public void ApplyAndRestart(string v) => Applied = true;
    }

    [Fact]
    public async Task UpdatesAreVerifiedBeforeApplying()
    {
        var (privateKey, publicKey) = UpdateSignature.Generate();
        var (otherPrivate, _) = UpdateSignature.Generate();
        var package = Path.Combine(home.Path, "ShiyinApp-3.6.1-full.nupkg");
        File.WriteAllBytes(package, Encoding.UTF8.GetBytes("package"));
        var files = new Dictionary<string, string> { ["ShiyinApp-3.6.1-full.nupkg"] = package };
        UpdateManifest Manifest(string version = "3.6.1", string arch = "x64", string? sha = null) =>
            new(version, arch, [new("ShiyinApp-3.6.1-full.nupkg", 7, sha ?? await_sha(package))]);
        static string await_sha(string p) => ModelStore.Sha256(p).GetAwaiter().GetResult();
        async Task<(bool Applied, string? Reason)> Run(UpdateManifest m, string key, string version = "3.6.1", bool busy = false)
        {
            var bytes = Encoding.UTF8.GetBytes(m.Json());
            var platform = new FakeUpdates(version, files, bytes, UpdateSignature.Sign(bytes, key));
            var updater = new Updater(platform, publicKey, AppSettings.InMemory(), () => busy);
            await updater.Check(userInitiated: true);
            var reason = await updater.Install(version);
            Assert.Equal(reason is not null, platform.Deleted);
            return (platform.Applied, reason);
        }
        Assert.Equal((true, null), await Run(Manifest(), privateKey));
        Assert.Contains("更新金鑰不符", (await Run(Manifest(), otherPrivate)).Reason);
        Assert.Contains("SHA-256", (await Run(Manifest(sha: new string('a', 64)), privateKey)).Reason);
        Assert.Contains("版本", (await Run(Manifest(version: "3.6.2"), privateKey)).Reason);
        Assert.Contains("格式", (await Run(Manifest(arch: "arm64"), privateKey)).Reason);
        Assert.Contains("版本", (await Run(Manifest(version: "3.6.0"), privateKey, version: "3.6.0")).Reason);
        Assert.Contains("進行中", (await Run(Manifest(), privateKey, busy: true)).Reason);
        Assert.True(Updater.IsNewer("3.6.10", "3.6.9"));
        Assert.False(Updater.IsNewer("3.6", "3.6.0"));
        Assert.Null(UpdateManifest.Parse(Encoding.UTF8.GetBytes("""{"version":"1","arch":"x64","files":[{"name":"../x","size":1,"sha256":"a"}]}""")));
    }

    // MARK: 提問、回報問題

    [Fact]
    public async Task AskMeetingAndAllMeetings()
    {
        var bodies = new List<JsonNode>();
        server.Handle = async c =>
        {
            using var reader = new StreamReader(c.Request.InputStream);
            var body = JsonNode.Parse(await reader.ReadToEndAsync())!;
            lock (bodies) bodies.Add(body);
            var tool = body["tool_choice"]!["name"]!.GetValue<string>();
            var input = tool == "save_search_keywords"
                ? JsonNode.Parse("""{"keywords":["預算"," x "]}""")
                : body["tools"]![0]!["input_schema"]!.ToJsonString().Contains("meetingId")
                    ? JsonNode.Parse("""{"answer":"下週決定","found":true,"citations":[{"meetingId":1,"time":"00:01","quote":"預算"},{"meetingId":99,"time":"00:02","quote":"x"}]}""")
                    : JsonNode.Parse("""{"answer":"要省錢","found":true,"citations":[{"time":"00:01","quote":"預算"}]}""");
            var bytes = Encoding.UTF8.GetBytes(new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_use", ["name"] = tool, ["input"] = input }) }.ToJsonString());
            await c.Response.OutputStream.WriteAsync(bytes);
            c.Response.Close();
        };
        var settings = AppSettings.InMemory(new Dictionary<string, string> { ["llmProvider"] = "anthropic" });
        var id = db.InsertMeeting(new Meeting { Title = "預算會議", MeetingDate = DateText.String(DateOnly.FromDateTime(DateTime.Now)) }, null).Id!.Value;
        db.SaveTranscript(id, [new Segment { MeetingId = id, Start = 1, End = 2, Text = "我們要討論預算", Speaker = 1 }], 2, 1);
        var ask = new AskService(db, settings, Llm());
        var record = await ask.AskMeeting(id, "  結論是？ ", default);
        Assert.Equal("結論是？", record.Question);
        Assert.Equal("要省錢", AskPrompt.Answer.Decode(record.Answer)!.Text);
        Assert.Single(db.Questions(id));
        var all = await ask.AskAll("預算怎麼決定", AskScope.Recent, null, default);
        var answer = AskPrompt.Answer.Decode(all.Answer)!;
        Assert.Equal(["預算"], answer.Keywords!);
        Assert.Equal([id], answer.MeetingIds!);
        // 不在這幾場會議裡的出處拿掉
        Assert.DoesNotContain(answer.Citations, c => c.MeetingId == 99);
        Assert.Equal("recent", all.Scope);
        Assert.Null(ask.UnavailableReason);
        Assert.NotNull(new AskService(db, AppSettings.InMemory(new Dictionary<string, string> { ["llmProvider"] = "none" }), Llm()).UnavailableReason);
    }

    [Fact]
    public void ProblemReportHasNoMeetingContent()
    {
        var id = db.InsertMeeting(new Meeting { Title = "機密的併購會議" }, null).Id!.Value;
        var job = db.Enqueue(id, JobKind.Summarize);
        db.ClaimNext(JobLane.Llm);
        db.Finish(job.Id!.Value, JobStatus.Failed, "逐字稿內容：機密", "LLMError.failed");
        var text = ProblemReport.Text(db, AppSettings.InMemory(), new ProblemReport.Environment("3.6.0", "12", "Windows 11 24H2（26100.2314）", "Snapdragon X", 16L << 30, "Adreno", "CPU"));
        Assert.Contains("- 拾音 3.6.0（build 12）", text);
        Assert.Contains("summarize：LLMError.failed", text);
        Assert.Contains("記憶體 16 GB", text);
        Assert.DoesNotContain("機密", text);
        Assert.StartsWith("https://github.com/juinwei7/Shiyin/issues/new?title=&body=", ProblemReport.Url(text));
    }
}
