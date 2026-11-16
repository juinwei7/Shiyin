using System.Buffers.Binary;
using System.Text.Json;
using Shiyin.Data;
using Shiyin.IO;
using Shiyin.LLM;
using Shiyin.Model;
using Shiyin.Services;
using Shiyin.Services.Capture;
using Shiyin.Text;
using Shiyin.Transcribe;
using Shiyin.Whisper;
using Xunit;

namespace Shiyin.Tests;

/// <summary>會議多選、清空垃圾桶（R88）、刪除一句（R89）、從某一句分成兩場（R90）：M§62～§64、design-windows §11.23</summary>
public sealed class MeetingEditTests : IDisposable
{
    readonly TempHome home = new();
    readonly ShiyinPaths paths;
    readonly AppDatabase db;
    readonly HttpClient http = new();
    readonly List<string> notices = [], errors = [];

    public MeetingEditTests()
    {
        paths = new ShiyinPaths(home.Path);
        paths.EnsureDirectories();
        db = new AppDatabase(paths.Database, paths.Backups);
    }

    public void Dispose()
    {
        http.Dispose();
        db.Dispose();
        home.Dispose();
    }

    sealed class NoTools : IToolLocator
    {
        public ToolCommand? Find(string name) => null;
    }

    sealed class Keys : IKeyStore
    {
        public string? Read(string account) => "sk-test";
        public bool Exists(string account) => true;
    }

    sealed class NoEngines : ISpeechEngines
    {
        public List<TranscribedSegment> Qwen(float[] samples, IReadOnlyList<(int Start, int End)> chunks, MeetingLanguage language,
                                             IReadOnlyList<string> glossary, bool retry, bool guard, WhisperControl control) => throw new NotSupportedException();
        public (List<TranscribedSegment> Segments, List<int> Seams, int SampleCount) WhisperFile(IAudioStream audio, double chunkSeconds,
            string model, string prompt, string language, WhisperControl control) => throw new NotSupportedException();
        public List<TranscribedSegment> WhisperSamples(float[] samples, IReadOnlyList<(int Start, int End)> chunks, string model, string prompt,
            string language, WhisperControl control) => throw new NotSupportedException();
        public List<TranscribedSegment> Fast(IAudioStream audio, MeetingLanguage language, Action<double> progress, CancellationToken cancel) =>
            throw new NotSupportedException();
        public Speakers.Diarizer.Output Diarize(float[] samples, int? minSpeakers, Action<double> progress, CancellationToken cancel) =>
            throw new NotSupportedException();
        public List<float[]?> Embed(float[] samples, IReadOnlyList<(double Start, double End)> spans, CancellationToken cancel) =>
            throw new NotSupportedException();
    }

    sealed class NoCompressor : IAudioCompressor
    {
        public Task Compress(string wavPath, string m4aPath, CancellationToken cancel) => Task.CompletedTask;
    }

    /// <summary>provider：llmProvider 的設定（none 是整理工具沒設定好）。工作佇列不啟動，排了的工作留在資料庫</summary>
    AppController Controller(string provider = "none")
    {
        var settings = AppSettings.InMemory(new Dictionary<string, string> { ["llmProvider"] = provider, ["liveTranscription"] = "NO", ["autoDetectLanguage"] = "NO" });
        var catalog = Enum.GetValues<ModelStore.Model>().ToDictionary(m => m, m => new ModelStore.ModelInfo(m.ToString(), [new($"{m}.bin", "http://unused", 1, "")]));
        var models = new ModelStore(paths.Models, http, catalog);
        var runner = new JobRunner(db, settings, paths, models, new LLMClient(new Keys(), new NoTools(), new ProcessRunner(), http), new WavDecoder(),
            new NoEngines(), new NoCompressor(), NoPower.Instance);
        var recorder = new Recorder(settings, paths, NoPower.Instance, new Recorder.Hooks((_, _) => [], _ => null, null, _ => null));
        var controller = new AppController(db, settings, paths, models, runner, recorder, new WavDecoder(), null, "4.8.1");
        controller.Notice += notices.Add;
        controller.Error += errors.Add;
        return controller;
    }

    static float[] OneHot(int i)
    {
        var v = new float[40];
        v[i] = 1;
        return v;
    }

    /// <summary>雙聲道、16 位元的 WAV：左右聲道不一樣，每個取樣都不同（切開後逐個比對）</summary>
    static void WriteStereo(string path, double seconds)
    {
        var count = (int)(seconds * 16000);
        using var writer = new WavWriter(path, 2);
        writer.Write([Enumerable.Range(0, count).Select(i => 0.3f * MathF.Sin(i * 0.013f)).ToArray(),
                      Enumerable.Range(0, count).Select(i => 0.2f * MathF.Cos(i * 0.0071f) + (i % 7) / 10000f).ToArray()], count);
    }

    static byte[] WavData(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var at = 12;
        while (at + 8 <= bytes.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(bytes, at, 4);
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 4));
            if (id == "data") return bytes[(at + 8)..(at + 8 + size)];
            at += 8 + size + size % 2;
        }
        throw new InvalidDataException("no data");
    }

    /// <summary>
    /// 分割會議用的測試會議（同 Mac 驗證的樣子）：8 句、3 位說話者，切在 90.8 秒那句：說話者 1 兩邊都有（Carl）、2 只在後段（Dana）、3 只在前段；
    /// 前後段各一則校正建議；標記重點 30、120.5 秒；錄音是 180 秒的雙聲道 WAV
    /// </summary>
    long SeedSplitMeeting(bool audio = true)
    {
        var meeting = db.InsertMeeting(new Meeting
        {
            Title = "ami-stereo", SourceFilename = "ami-stereo.wav", MeetingDate = "2026-11-16", CreatedAt = "2026-11-16T01:00:00Z", DurationSeconds = 180,
            Channels = "both", Folder = "專案", Language = "en", TemplateId = "general", Source = "recording", TranscribedWith = "breeze", MinSpeakers = 3,
            Participants = "Carl, Dana", UserNotes = "我的筆記", Notes = null,
        }, audio ? "wav" : null);
        var id = meeting.Id!.Value;
        if (audio) WriteStereo(paths.AudioPath(meeting), 180);
        (double, double, int, string)[] lines =
        [
            (0, 10, 1, "one"), (10, 30, 3, "two"), (30, 60, 1, "three"), (60, 90.8, 3, "four"),
            (90.8, 100, 2, "five"), (100, 120, 1, "six"), (120, 150, 2, "seven"), (150, 180, 1, "eight"),
        ];
        db.SaveTranscript(id, lines.Select((l, i) => new Segment { MeetingId = id, Seq = i, Start = l.Item1, End = l.Item2, Speaker = l.Item3, Text = l.Item4 }).ToList(),
            180, 3, new Dictionary<int, SpeakerVoice> { [1] = new(OneHot(1), 60, "mix"), [2] = new(OneHot(2), 40, "mix"), [3] = new(OneHot(3), 50, "mix") });
        db.RenameSpeaker(id, 1, "Carl");
        db.LearnVoiceprint(id, 1, "Carl");
        db.RenameSpeaker(id, 2, "Dana");
        db.LearnVoiceprint(id, 2, "Dana");
        var segments = db.Segments(id);
        db.SaveCorrections(id, [
            new Correction { MeetingId = id, SegmentId = segments[2].Id!.Value, Original = "three", Suggestion = "tree", Reason = "a" },
            new Correction { MeetingId = id, SegmentId = segments[6].Id!.Value, Original = "seven", Suggestion = "heaven", Reason = "b" },
        ]);
        db.AddBookmark(id, 30);
        db.AddBookmark(id, 120.5, "重點");
        // 說話者的調整可以復原（分割後要清掉）
        db.ReassignSegments(id, [segments[0].Id!.Value], 1, "x");
        return id;
    }

    // MARK: 分割會議（R90、M§64）

    [Fact]
    public async Task SplitMeetingMovesTranscriptAudioSpeakers()
    {
        var controller = Controller();
        var id = SeedSplitMeeting();
        var original = db.Meeting(id)!;
        var before = WavData(paths.AudioPath(original));
        var cutSegment = db.Segments(id)[4];
        Assert.Equal(90.8, cutSegment.Start);
        var released = new List<long>();
        var replaced = new List<long>();
        controller.ReleasingAudio += released.Add;
        controller.AudioReplaced += replaced.Add;

        await controller.SplitMeeting(id, cutSegment.Id!.Value);
        Assert.Empty(errors);
        Assert.Null(controller.SplittingMeeting);
        var created = db.MeetingRows().Select(r => r.Meeting).Single(m => m.Id != id);
        var newId = created.Id!.Value;

        // 新的一場：標題加「（2）」、其他欄位照原本的、開始時間加切點、長度相減；筆記、紀錄沒有
        Assert.Equal("ami-stereo（2）", created.Title);
        Assert.Equal(("2026-11-16", "both", "專案", "en", "recording", "breeze", (int?)3, "Carl, Dana"),
            (created.MeetingDate, created.Channels, created.Folder, created.Language, created.Source, created.TranscribedWith, created.MinSpeakers, created.Participants));
        Assert.Equal("2026-11-16T01:01:30Z", created.CreatedAt);
        Assert.Equal(89, created.DurationSeconds);
        Assert.Equal("", created.UserNotes);
        Assert.Null(created.Notes);
        Assert.Equal($"{newId}.wav", created.AudioFile);
        var kept = db.Meeting(id)!;
        Assert.Equal(91, kept.DurationSeconds);
        Assert.Equal("我的筆記", kept.UserNotes);
        Assert.Equal($"{id}.wav", kept.AudioFile);

        // 逐字稿：前 4 句留著、後 4 句搬過去，時間從 0 開始、seq 重新編
        Assert.Equal(["one", "two", "three", "four"], db.Segments(id).Select(s => s.Text));
        Assert.Equal([0, 1, 2, 3], db.Segments(id).Select(s => s.Seq));
        var moved = db.Segments(newId);
        Assert.Equal(["five", "six", "seven", "eight"], moved.Select(s => s.Text));
        Assert.Equal([0, 1, 2, 3], moved.Select(s => s.Seq));
        Assert.Equal([0, 9.2, 29.2, 59.2], moved.Select(s => Math.Round(s.Start, 6)));
        Assert.Equal(89.2, Math.Round(moved[^1].End, 6));
        // 校正建議跟著段落；搜尋索引的會議跟著換
        Assert.Equal(["three"], db.Corrections(id).Select(c => c.Original));
        Assert.Equal(["seven"], db.Corrections(newId).Select(c => c.Original));
        Assert.Equal(moved[2].Id, db.Corrections(newId)[0].SegmentId);
        Assert.Contains(db.Search("seven"), h => h.MeetingId == newId && h.Kind == SearchKind.Segment);
        Assert.Contains(db.Search("three"), h => h.MeetingId == id && h.Kind == SearchKind.Segment);
        Assert.Contains(db.Search("ami-stereo（2）"), h => h.MeetingId == newId && h.Kind == SearchKind.Title);
        var searchRows = db.Read(d => d.Query("SELECT meeting_id, COUNT(*) AS n FROM search WHERE kind = 'segment' GROUP BY meeting_id ORDER BY meeting_id"))
            .Select(r => (r.Long("meeting_id"), r.Int("n"))).ToList();
        Assert.Equal([(id, 4), (newId, 4)], searchRows);
        // 標記重點：120.5 秒的搬過去變 29.7 秒，30 秒的留著
        Assert.Equal([30.0], db.Bookmarks(id).Select(b => b.Time));
        Assert.Equal([29.7], db.Bookmarks(newId).Select(b => Math.Round(b.Time, 6)));
        Assert.Equal("重點", db.Bookmarks(newId)[0].Label);

        // 說話者：Carl 兩邊都有、聲紋留在原本那場；Dana 只在後段，聲紋改指新的一場；說話者 3 只在前段
        Assert.Equal([1, 3], db.Speakers(id).Select(s => s.Idx));
        Assert.Equal([1, 2], db.Speakers(newId).Select(s => s.Idx));
        Assert.Equal(["Carl", "Dana"], db.Speakers(newId).Select(s => s.Name));
        var voiceprints = db.Read(d => d.Query("SELECT p.name, v.meeting_id FROM voiceprints v JOIN people p ON p.id = v.person_id ORDER BY p.name"))
            .Select(r => (r.Str("name"), r.Long("meeting_id"))).ToList();
        Assert.Equal([("Carl", id), ("Dana", newId)], voiceprints);
        // 段落換了，原本那場的說話者復原清掉
        Assert.Null(db.SpeakerUndoLabel(id));

        // 錄音：前段、後段跟原本的錄音逐個位元組相同（2 聲道 16 位元，切在 90.8 × 16000 = 1,452,800 個音框）
        var head = WavData(paths.AudioPath(kept));
        var tail = WavData(paths.AudioPath(created));
        Assert.Equal(1_452_800 * 4, head.Length);
        Assert.Equal(before.Length, head.Length + tail.Length);
        Assert.True(before.AsSpan(0, head.Length).SequenceEqual(head));
        Assert.True(before.AsSpan(head.Length).SequenceEqual(tail));
        using (var reader = new WavReader(paths.AudioPath(created))) Assert.Equal((2, 16000, 16), (reader.Channels, reader.SampleRate, reader.BitsPerSample));
        Assert.Equal([id], released);
        Assert.Equal([id], replaced);
        Assert.Empty(Directory.GetFileSystemEntries(paths.Tmp));

        // 整理工具沒設定好：不排工作，說明怎麼重新整理
        Assert.Null(db.LatestJob(newId));
        Assert.Equal("已分成兩場：「ami-stereo（2）」。兩場都要重新整理會議紀錄，設定好整理工具後按「重新整理」", notices[^1]);
    }

    /// <summary>整理工具設定好時兩場都排重新整理（R90.3）；錄音檔不見的會議只分逐字稿（R90.4）</summary>
    [Fact]
    public async Task SplitMeetingWithoutAudioQueuesSummaries()
    {
        var controller = Controller("anthropic");
        var id = SeedSplitMeeting(audio: false);
        await controller.SplitMeeting(id, db.Segments(id)[4].Id!.Value);
        var newId = db.MeetingRows().Single(r => r.Id != id).Id;
        Assert.Equal("", db.Meeting(newId)!.AudioFile);
        Assert.Equal(JobKind.Summarize, db.LatestJob(id)!.Kind);
        Assert.Equal(JobKind.Summarize, db.LatestJob(newId)!.Kind);
        Assert.Equal("已分成兩場：「ami-stereo（2）」。兩場都在重新整理會議紀錄", notices[^1]);
    }

    /// <summary>擋下來（R90.4）：垃圾桶裡、有工作在排、第一句；會議數不變</summary>
    [Fact]
    public async Task SplitMeetingIsBlocked()
    {
        var controller = Controller();
        var id = SeedSplitMeeting();
        var segments = db.Segments(id);
        await controller.SplitMeeting(id, segments[0].Id!.Value);   // 第一句：切點是 0，不分
        db.Enqueue(id, JobKind.Correct);
        await controller.SplitMeeting(id, segments[4].Id!.Value);
        Assert.Equal("正在處理這場會議，完成後才能分成兩場", notices[^1]);
        db.Write(d => d.Execute("DELETE FROM jobs"));
        db.SetTrashed(id, true);
        await controller.SplitMeeting(id, segments[4].Id!.Value);
        Assert.Equal("垃圾桶裡的會議要先還原才能操作", notices[^1]);
        Assert.Single(db.MeetingRows(trash: true));
        Assert.Empty(db.MeetingRows());
        Assert.Equal(8, db.Segments(id).Count);
    }

    /// <summary>WAV 照位元組切：浮點、有 LIST 區段的檔案格式不變；切點不在錄音裡時是 null</summary>
    [Fact]
    public async Task SplitWavKeepsFormat()
    {
        // 32 位元浮點、單聲道、8 kHz，fmt 18 位元組（cbSize），data 前面有 LIST 區段
        var frames = 8000 * 3;
        var data = new byte[frames * 4];
        for (var i = 0; i < frames; i++) BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(i * 4), MathF.Sin(i * 0.01f));
        var fmt = new byte[18];
        BinaryPrimitives.WriteUInt16LittleEndian(fmt, 3);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(4), 8000);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(8), 32000);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(12), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(14), 32);
        using var file = new MemoryStream();
        void Chunk(string id, byte[] body)
        {
            file.Write(System.Text.Encoding.ASCII.GetBytes(id));
            file.Write(BitConverter.GetBytes((uint)body.Length));
            file.Write(body);
            if (body.Length % 2 == 1) file.WriteByte(0);
        }
        file.Write("RIFF"u8);
        file.Write(BitConverter.GetBytes(0u));
        file.Write("WAVE"u8);
        Chunk("fmt ", fmt);
        Chunk("LIST", "INFOabc"u8.ToArray());
        Chunk("data", data);
        var source = Path.Combine(home.Path, "float.wav");
        File.WriteAllBytes(source, file.ToArray());

        var dir = paths.MakeTempDirectory();
        var parts = AudioSplit.SplitWav(source, 1.25, dir)!;
        Assert.Equal("wav", parts.Extension);
        using (var head = new WavReader(parts.Head)) Assert.Equal((1, 8000, 32, true, 10_000L), (head.Channels, head.SampleRate, head.BitsPerSample, head.IsFloat, head.Frames));
        using (var tail = new WavReader(parts.Tail)) Assert.Equal(14_000L, tail.Frames);
        Assert.True(data.AsSpan(0, 40_000).SequenceEqual(WavData(parts.Head)));
        Assert.True(data.AsSpan(40_000).SequenceEqual(WavData(parts.Tail)));
        // fmt 區段原樣（18 位元組）
        Assert.True(File.ReadAllBytes(parts.Head).AsSpan(20, 18).SequenceEqual(fmt));
        Assert.Null(AudioSplit.SplitWav(source, 0, dir));
        Assert.Null(AudioSplit.SplitWav(source, 3, dir));
        Assert.Null(AudioSplit.SplitWav(source, 5, dir));
        // 只切 WAV 的版本：其他格式說不支援
        await Assert.ThrowsAsync<IOException>(() => new AudioSplit.WavOnly().Split(Path.Combine(home.Path, "a.mp3"), 1, dir, default));
    }

    // MARK: 刪除一句（R89、M§63、D171）

    /// <summary>刪掉說話者 2 唯一的一句：段落、搜尋索引、校正建議都拿掉，seq 連續，說話者 2 拿掉；復原後整份資料跟刪之前一樣</summary>
    [Fact]
    public void DeleteSegmentAndUndo()
    {
        var controller = Controller();
        var id = db.InsertMeeting(new Meeting { Title = "刪一句", SourceFilename = "a.wav" }, null).Id!.Value;
        db.SaveTranscript(id, [
            new Segment { MeetingId = id, Seq = 0, Start = 0, End = 5, Speaker = 1, Text = "第一句" },
            new Segment { MeetingId = id, Seq = 1, Start = 5, End = 10, Speaker = 1, Text = "第二句" },
            new Segment { MeetingId = id, Seq = 2, Start = 10, End = 12.75, Speaker = 2, Text = "第三句是很長的一句話，超過十二個字", LowConfidence = true, Channel = 1 },
            new Segment { MeetingId = id, Seq = 3, Start = 12.75, End = 20, Speaker = 1, Text = "第四句" },
        ], 20, 2, new Dictionary<int, SpeakerVoice> { [1] = new(OneHot(1), 15, "mix"), [2] = new(OneHot(2), 3, "mic") });
        db.RenameSpeaker(id, 2, "小陳");
        var target = db.Segments(id)[2];
        db.SaveCorrections(id, [new Correction { MeetingId = id, SegmentId = target.Id!.Value, Original = "第三", Suggestion = "第參", Reason = "用字" }]);
        string Dump() => string.Join("\n", new[] { "segments", "speakers", "corrections" }
            .Select(t => string.Join(";", db.Read(d => d.Query($"SELECT * FROM {t} ORDER BY rowid")).Select(r => string.Join(",", r.Columns.Select(c => c.Value switch
            {
                byte[] b => Convert.ToBase64String(b),
                _ => Convert.ToString(c.Value, System.Globalization.CultureInfo.InvariantCulture),
            })))))
            .Append(string.Join(";", db.Read(d => d.Query("SELECT rowid, meeting_id, kind, ref, text FROM search ORDER BY rowid")).Select(r => string.Join(",", r.Columns.Select(c => c.Value))))));
        var before = Dump();

        controller.DeleteSegment(id, target.Id!.Value);
        Assert.Empty(errors);
        Assert.Equal("已刪除這句。要復原的話，在逐字稿按右鍵選「復原」。", notices[^1]);
        Assert.Equal(["第一句", "第二句", "第四句"], db.Segments(id).Select(s => s.Text));
        Assert.Equal([0, 1, 2], db.Segments(id).Select(s => s.Seq));
        Assert.Empty(db.Corrections(id));
        Assert.DoesNotContain(db.Search("第三句"), h => h.Kind == SearchKind.Segment);
        Assert.Equal([1], db.Speakers(id).Select(s => s.Idx));
        Assert.Equal("刪除一句「第三句是很長的一句話，超…」", db.SpeakerUndoLabel(id));

        // 快照的 deleted 照 Mac 的鍵（Codable 的屬性名稱；沒有值的選填欄位不寫）
        var json = (string)db.Read(d => d.Scalar("SELECT snapshot FROM speaker_undo WHERE meeting_id = ?", id))!;
        var deleted = JsonDocument.Parse(json).RootElement.GetProperty("deleted");
        Assert.Equal(["id", "meetingId", "seq", "start", "end", "text", "lowConfidence", "speaker", "kind", "channel", "speakerManual"],
            deleted.GetProperty("segment").EnumerateObject().Select(p => p.Name));
        Assert.Equal(12.75, deleted.GetProperty("segment").GetProperty("end").GetDouble());
        Assert.Equal(["id", "meetingId", "segmentId", "original", "suggestion", "reason", "status", "createdAt"],
            deleted.GetProperty("corrections")[0].EnumerateObject().Select(p => p.Name));

        controller.UndoSpeakerEdit(id);
        Assert.Equal("已復原：刪除一句「第三句是很長的一句話，超…」", notices[^1]);
        Assert.Equal(before, Dump());
        controller.UndoSpeakerEdit(id);
        Assert.StartsWith("沒有可以復原的", notices[^1]);

        // 沒辨識說話者的會議也能刪；垃圾桶裡的不能刪
        var plain = db.InsertMeeting(new Meeting { Title = "沒有說話者", SourceFilename = "b.wav" }, null).Id!.Value;
        db.SaveTranscript(plain, [new Segment { MeetingId = plain, Seq = 0, End = 3, Text = "甲" }, new Segment { MeetingId = plain, Seq = 1, Start = 3, End = 6, Text = "乙" }], 6, 0);
        controller.DeleteSegment(plain, db.Segments(plain)[0].Id!.Value);
        Assert.Equal(["乙"], db.Segments(plain).Select(s => s.Text));
        Assert.Equal([0], db.Segments(plain).Select(s => s.Seq));
        db.SetTrashed(plain, true);
        controller.DeleteSegment(plain, db.Segments(plain)[0].Id!.Value);
        Assert.Equal("垃圾桶裡的會議要先還原才能操作", notices[^1]);
        Assert.Single(db.Segments(plain));
    }

    /// <summary>
    /// Mac 存的快照（Swift 的 JSONEncoder：鍵的順序不固定、斜線跳脫、整數的秒數沒有小數點）Windows 也能復原：
    /// 2026-11-16 用跟 Mac 的 SpeakerSnapshot 同樣的 Codable 結構產生
    /// </summary>
    [Fact]
    public void UndoMacDeletedSnapshot()
    {
        const string mac = """{"segments":[{"speaker":1,"manual":false,"id":1},{"id":2,"speaker":1,"manual":false},{"id":3,"speaker":2,"manual":true},{"id":4,"speaker":1,"manual":false}],"deleted":{"segment":{"kind":"final","lowConfidence":true,"meetingId":1,"start":10,"seq":2,"id":3,"text":"第三句\/有斜線","speaker":2,"speakerManual":true,"end":12.75},"corrections":[{"createdAt":"2026-11-16T01:00:00Z","meetingId":1,"id":7,"original":"第三","suggestion":"第參","segmentId":3,"reason":"用字","status":"pending"}]},"speakers":[{"name":"說話者 1","audioSource":"mix","speechSeconds":12.5,"idx":1,"nameSource":"default","embedding":"AACAPw=="},{"name":"小陳","speechSeconds":3,"idx":2,"nameSource":"manual","audioSource":"mic"}]}""";
        var id = db.InsertMeeting(new Meeting { Title = "Mac 刪的", SourceFilename = "a.wav" }, null).Id!.Value;
        Assert.Equal(1, id);
        // Mac 刪掉第 3 句之後的樣子：段落 1、2、4（seq 0～2），只剩說話者 1
        db.SaveTranscript(id, [
            new Segment { MeetingId = id, Seq = 0, Start = 0, End = 5, Speaker = 1, Text = "第一句" },
            new Segment { MeetingId = id, Seq = 1, Start = 5, End = 10, Speaker = 1, Text = "第二句" },
            new Segment { MeetingId = id, Seq = 2, Start = 10, End = 12.75, Speaker = 1, Text = "暫時" },
            new Segment { MeetingId = id, Seq = 3, Start = 12.75, End = 20, Speaker = 1, Text = "第四句" },
        ], 20, 1);
        db.Write(d =>
        {
            d.Execute("DELETE FROM search WHERE rowid = 3");
            d.Execute("DELETE FROM segments WHERE id = 3");
            d.Execute("UPDATE segments SET seq = 2 WHERE id = 4");
            d.Execute("INSERT INTO speaker_undo (meeting_id, snapshot, label, created_at) VALUES (?, ?, ?, ?)", id, mac, "刪除一句「第三句/有斜線」", Clock.Now());
        });
        Assert.True(db.UndoSpeakerEdit(id));
        var segments = db.Segments(id);
        Assert.Equal(["第一句", "第二句", "第三句/有斜線", "第四句"], segments.Select(s => s.Text));
        Assert.Equal([0, 1, 2, 3], segments.Select(s => s.Seq));
        Assert.Equal((3L, 10.0, 12.75, 2, true, true, (int?)null), (segments[2].Id!.Value, segments[2].Start, segments[2].End, segments[2].Speaker!.Value,
            segments[2].LowConfidence, segments[2].SpeakerManual, segments[2].Channel));
        var correction = db.Corrections(id).Single();
        Assert.Equal((7L, 3L, "第三", "第參", "pending", "2026-11-16T01:00:00Z"), (correction.Id!.Value, correction.SegmentId, correction.Original, correction.Suggestion,
            correction.Status, correction.CreatedAt));
        Assert.Equal(["說話者 1", "小陳"], db.Speakers(id).Select(s => s.Name));
        Assert.Contains(db.Search("有斜線"), h => h.Kind == SearchKind.Segment && h.Ref == 3);
    }

    // MARK: 多選、清空垃圾桶（R88、M§62）

    [Fact]
    public void MultipleMeetingsTrashRestorePurge()
    {
        var controller = Controller();
        var ids = Enumerable.Range(1, 4).Select(i => db.InsertMeeting(new Meeting { Title = $"m{i}", SourceFilename = $"m{i}.wav", Folder = i == 4 ? "甲" : "" }, "wav")).ToList();
        foreach (var m in ids) WriteStereo(paths.AudioPath(m), 0.1);
        var all = ids.Select(m => m.Id!.Value).ToList();

        // 移到資料夾：已經在那裡的跳過；都在了就說明
        controller.MoveToFolder([all[2], all[3]], "甲");
        Assert.Equal("已移到「甲」", notices[^1]);
        controller.MoveToFolder([all[2], all[3]], "甲");
        Assert.Equal("這些會議都已經在「甲」", notices[^1]);
        controller.MoveToFolder(all, "乙");
        Assert.Equal("已把 4 場會議移到「乙」", notices[^1]);
        Assert.Equal("乙", AppController.CommonFolder(db.MeetingRows(), all));
        controller.MoveToFolder([all[0]], "");
        Assert.Null(AppController.CommonFolder(db.MeetingRows(), all));
        controller.MoveToFolder(all, "");
        Assert.Equal("已把 3 場會議移出資料夾", notices[^1]);

        // 移到垃圾桶：正在錄音的那場跳過、說明原因
        var source = Path.Combine(home.Path, "src.wav");
        WriteStereo(source, 30);
        var recording = controller.StartSimulatedRecording(source, 1, false, "")!.Value;
        controller.MoveToTrash([all[0], all[1], all[2], recording]);
        Assert.Equal("還在錄音的那場會議沒有移到垃圾桶，請先停止錄音", errors[^1]);
        Assert.Equal("已把 3 場會議移到垃圾桶，30 天內都可以還原", notices[^1]);
        controller.Recorder.Stop();
        Assert.Equal(3, db.MeetingRows(trash: true).Count);

        // 還原兩場：回到列表、選那兩場
        var revealed = new List<IReadOnlyList<long>>();
        controller.RevealMany += revealed.Add;
        controller.Restore([all[0], all[1]]);
        Assert.Equal([all[0], all[1]], revealed.Single());
        Assert.Single(db.MeetingRows(trash: true));

        // 永久刪除兩場、清空垃圾桶：錄音檔一起刪，刪之前先發 ReleasingAudio
        var released = new List<long>();
        controller.ReleasingAudio += released.Add;
        controller.MoveToTrash([all[0], all[1], all[3]]);
        controller.DeletePermanently([all[0], all[1]]);
        Assert.Equal([all[0], all[1]], released);
        Assert.Null(db.Meeting(all[0]));
        Assert.False(File.Exists(paths.AudioPath(ids[0])));
        Assert.Equal(2, db.MeetingRows(trash: true).Count);
        controller.EmptyTrash();
        Assert.Empty(db.MeetingRows(trash: true));
        Assert.Equal([all[0], all[1], all[2], all[3]], released.Order());
        Assert.False(File.Exists(paths.AudioPath(ids[3])));
    }
}
