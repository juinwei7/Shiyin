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
        Assert.Null(controller.ApplyingEdit);
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
        Assert.Equal("已分成兩場：「ami-stereo（2）」。會議紀錄要重新整理，設定好整理工具後按「重新整理」", notices[^1]);
        // 分成兩場也能還原（R107.8、D180）：原本的錄音搬到 audio\edits\、快照記著分出去的那場
        Assert.True(File.Exists(Path.Combine(paths.EditsAudio, $"{id}.wav")));
        Assert.Equal([newId], db.EditRecord(id)!.SplitIds);
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
        Assert.Equal("已分成兩場：「ami-stereo（2）」。會議紀錄正在重新整理", notices[^1]);
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
        Assert.Equal("正在處理這場會議，完成後才能編輯", notices[^1]);
        db.Write(d => d.Execute("DELETE FROM jobs"));
        db.SetTrashed(id, true);
        await controller.SplitMeeting(id, segments[4].Id!.Value);
        Assert.Equal("垃圾桶裡的會議要先還原才能操作", notices[^1]);
        Assert.Single(db.MeetingRows(trash: true));
        Assert.Empty(db.MeetingRows());
        Assert.Equal(8, db.Segments(id).Count);
    }

    /// <summary>一場的 WAV 照位元組接（M§81.2）：浮點、有 LIST 區段的檔案格式不變；片段超出錄音的部分當成沒有</summary>
    [Fact]
    public async Task ComposeWavKeepsFormat()
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

        // 0.5～1.25 秒、2～5 秒（錄音只到 3 秒）
        var dir = paths.MakeTempDirectory();
        var output = AudioEdit.ComposeWav(new EditPlan([new(1, 0.5, 1.25), new(1, 2, 5)]), source, dir);
        using (var reader = new WavReader(output)) Assert.Equal((1, 8000, 32, true, 6_000L + 8_000L), (reader.Channels, reader.SampleRate, reader.BitsPerSample, reader.IsFloat, reader.Frames));
        Assert.True(data.AsSpan(4_000 * 4, 6_000 * 4).ToArray().Concat(data.AsSpan(16_000 * 4).ToArray()).SequenceEqual(WavData(output)));
        // fmt 區段原樣（18 位元組）
        Assert.True(File.ReadAllBytes(output).AsSpan(20, 18).SequenceEqual(fmt));
        // 只接 WAV 的版本：其他格式說不支援
        await Assert.ThrowsAsync<IOException>(() => new AudioEdit.WavOnly().Compose(new EditPlan([new(1, 0, 1), new(2, 0, 1)]),
            new Dictionary<long, string> { [1] = source, [2] = Path.Combine(home.Path, "a.mp3") }, dir, default));
    }

    // MARK: 編輯會議（R107、M§81、design-windows §11.40）

    /// <summary>單聲道 16 位元的 WAV，每個取樣都不同（接好後逐個比對）</summary>
    static void WriteMono(string path, double seconds, int rate = 16000, float offset = 0)
    {
        var count = (int)Math.Round(seconds * rate);
        using var writer = new WavWriter(path, 1, rate);
        writer.Write([Enumerable.Range(0, count).Select(i => offset + 0.4f * MathF.Sin(i * 0.0037f) + (i % 11) / 20000f).ToArray()], count);
    }

    /// <summary>資料表的內容（不含 id、更新時間：還原時句子用新的 id 放回，同 Mac）</summary>
    string Snapshot(long id)
    {
        var m = db.Meeting(id)!;
        var lines = new List<string> { $"{m.Title}|{m.DurationSeconds}|{m.AudioFile}|{m.UserNotes}|{m.Notes}|{m.NotesBy}|{m.NotesDraft}|{m.CreatedAt}|{m.MeetingDate}|{m.Participants}|{m.DeletedAt is null}" };
        foreach (var kind in new[] { "final", "live" })
            lines.AddRange(db.Segments(id, kind).Select(s => $"{s.Kind}|{s.Seq}|{s.Start}|{s.End}|{s.Text}|{s.Speaker}|{s.LowConfidence}|{s.Channel}|{s.SpeakerManual}"));
        lines.AddRange(db.Bookmarks(id).Select(b => $"b|{b.Time}|{b.Label}"));
        lines.AddRange(db.Speakers(id).Select(s => $"s|{s.Idx}|{s.Name}|{s.NameSource}|{s.PersonId}|{Convert.ToBase64String(s.Embedding ?? [])}|{s.SpeechSeconds}|{s.AudioSource}"));
        lines.AddRange(db.Corrections(id).Select(c => $"c|{db.Segments([c.SegmentId]).Single().Text}|{c.Original}|{c.Suggestion}|{c.Status}"));
        lines.AddRange(db.Documents(id).Select(d => $"d|{d.Name}|{d.File}"));
        return string.Join("\n", lines);
    }

    /// <summary>59 秒的會議（同 Mac 驗證的樣子）：五句、兩位說話者、標記重點 10／30／50、我的筆記有時間點、會議紀錄有時間點</summary>
    long SeedTrimMeeting()
    {
        var meeting = db.InsertMeeting(new Meeting
        {
            Title = "包裝會議", SourceFilename = "a.wav", MeetingDate = "2026-11-16", CreatedAt = "2026-11-16T02:00:00Z", DurationSeconds = 59,
            Language = "zh", UserNotes = "[00:08] 包裝\n[00:20] 預算\n沒有時間的一行\n[00:50] 結論",
        }, "wav");
        var id = meeting.Id!.Value;
        WriteMono(paths.AudioPath(meeting), 59);
        (double, double, int, string)[] lines = [(0, 6.26, 1, "開場"), (14.98, 20.78, 2, "包裝"), (20.78, 24.96, 1, "閒聊"), (29.6, 34, 2, "預算"), (54, 58, 1, "散會")];
        db.SaveTranscript(id, lines.Select((l, i) => new Segment { MeetingId = id, Seq = i, Start = l.Item1, End = l.Item2, Speaker = l.Item3, Text = l.Item4 }).ToList(),
            59, 2, new Dictionary<int, SpeakerVoice> { [1] = new(OneHot(1), 20, "mix"), [2] = new(OneHot(2), 10, "mix") });
        db.RenameSpeaker(id, 2, "小美");
        foreach (var t in new[] { 10.0, 30, 50 }) db.AddBookmark(id, t);
        db.SaveNotes(id, new MeetingNotes { Title = "包裝會議", Summary = [new("包裝定案", "00:15")], Topics = [new("預算", "00:25", [new("先抓五萬", "00:31")])] },
            "claude", "整理", NotesSource.Generated);
        db.SaveCorrections(id, [new Correction { MeetingId = id, SegmentId = db.Segments(id)[3].Id!.Value, Original = "預算", Suggestion = "預算表", Reason = "r" }]);
        return id;
    }

    /// <summary>裁掉開頭、結尾，刪掉中間一段（R107.3、R107.4）：時間、錄音、標記、筆記、會議紀錄都跟著移；還原後一字不差（R107.8）</summary>
    [Fact]
    public async Task TrimDeleteAndRestore()
    {
        var controller = Controller();
        var id = SeedTrimMeeting();
        var before = Snapshot(id);
        var audioBefore = File.ReadAllBytes(paths.AudioPath(db.Meeting(id)!));
        var released = new List<long>();
        controller.ReleasingAudio += released.Add;

        await controller.ApplyMeetingEdit(id, new EditPlan([new(id, 2, 20), new(id, 30, 55)]), null);
        Assert.Empty(errors);
        Assert.Null(controller.ApplyingEdit);
        Assert.Equal("已編輯「包裝會議」。會議紀錄要重新整理，設定好整理工具後按「重新整理」", notices[^1]);
        var m = db.Meeting(id)!;
        Assert.Equal((43, $"{id}.wav", "2026-11-16T02:00:02Z"), (m.DurationSeconds, m.AudioFile, m.CreatedAt));
        // 句子看中點：閒聊（中點 22.87）、散會（56）刪掉，其他夾在片段裡再對應
        var segments = db.Segments(id);
        Assert.Equal(["開場", "包裝", "預算"], segments.Select(s => s.Text));
        Assert.Equal([(0.0, 4.26), (12.98, 18.0), (18.0, 22.0)], segments.Select(s => (Math.Round(s.Start, 6), Math.Round(s.End, 6))));
        Assert.Equal([0, 1, 2], segments.Select(s => s.Seq));
        Assert.Equal([8.0, 18, 38], db.Bookmarks(id).Select(b => Math.Round(b.Time, 6)));
        Assert.Equal("[00:06] 包裝\n[00:18] 預算\n沒有時間的一行\n[00:38] 結論", m.UserNotes);
        // 會議紀錄的時間點移到切口，舊的存成「編輯會議前」
        Assert.Equal(("00:13", "00:18", "00:19"), (m.DecodedNotes!.Summary[0].Time, m.DecodedNotes.Topics[0].Start, m.DecodedNotes.Topics[0].Points[0].Time));
        Assert.Equal("編輯會議前", db.Read(d => d.Scalar("SELECT reason FROM notes_versions WHERE meeting_id = ? ORDER BY id DESC LIMIT 1", id)));
        Assert.Single(db.Corrections(id));
        Assert.DoesNotContain(db.Search("閒聊"), h => h.Kind == SearchKind.Segment);
        // 錄音：2～20、30～55 秒的取樣接起來（16 位元單聲道，格式不變）；原本的搬到 audio\edits\
        var source = WavData(Path.Combine(paths.EditsAudio, $"{id}.wav"));
        var edited = WavData(paths.AudioPath(m));
        Assert.True(source.AsSpan(32_000 * 2, 288_000 * 2).ToArray().Concat(source.AsSpan(480_000 * 2, 400_000 * 2).ToArray()).SequenceEqual(edited));
        Assert.Equal(audioBefore, File.ReadAllBytes(Path.Combine(paths.EditsAudio, $"{id}.wav")));
        Assert.Equal([id], released);
        var record = db.EditRecord(id)!;
        Assert.Equal(("edits/" + id + ".wav", 30), (record.AudioFile, record.DaysLeft()));

        // 再編輯一次（再裁 1 秒）：快照不變、舊的錄音直接刪
        await controller.ApplyMeetingEdit(id, new EditPlan([new(id, 1, 43)]), null);
        Assert.Equal(42, db.Meeting(id)!.DurationSeconds);
        Assert.Equal(record.Snapshot, db.EditRecord(id)!.Snapshot);
        Assert.Equal(audioBefore, File.ReadAllBytes(Path.Combine(paths.EditsAudio, $"{id}.wav")));

        // 還原：說明寫原本多長；句子、欄位、標記、說話者、校正建議、錄音跟原本一字不差
        Assert.True(controller.RequestRestoreEdit(id));
        Assert.Equal("・會議會回到編輯前的樣子（原本 00:59），錄音、逐字稿、標記重點、會議紀錄都換回來。\n・編輯之後對逐字稿、說話者、筆記的修改會不見。",
            controller.RestoreEditMessage(id));
        await controller.RestoreMeetingEdit(id);
        Assert.Empty(errors);
        Assert.Equal("已還原成編輯前的樣子", notices[^1]);
        Assert.Equal(before, Snapshot(id));
        Assert.Equal(audioBefore, File.ReadAllBytes(paths.AudioPath(db.Meeting(id)!)));
        Assert.Null(db.EditRecord(id));
        Assert.Empty(Directory.GetFiles(paths.EditsAudio));
        Assert.Contains(db.Search("閒聊"), h => h.Kind == SearchKind.Segment && h.MeetingId == id);
        Assert.Equal("還原編輯前", db.Read(d => d.Scalar("SELECT reason FROM notes_versions WHERE meeting_id = ? ORDER BY id DESC LIMIT 1", id)));
        Assert.False(controller.RequestRestoreEdit(id));
        Assert.Equal("這場會議沒有編輯過。編輯會議後 30 天內可以從這裡還原。", notices[^1]);
    }

    /// <summary>
    /// 合併兩場（R107.6、M§81.4）：先開的那場在前；另一場的句子、標記、參考文件複製過來，說話者照名字、聲紋對，那場移到垃圾桶；
    /// 錄音取樣率取高的、聲道取多的；還原後兩場都跟原本一樣、那場拿出垃圾桶
    /// </summary>
    [Fact]
    public async Task MergeAndRestore()
    {
        var controller = Controller("anthropic");
        var id = SeedTrimMeeting();
        // 早一點開的另一場：8 kHz 單聲道 37 秒，「小美」同名、說話者 1 的聲紋對不上
        var other = db.InsertMeeting(new Meeting
        {
            Title = "包裝會議（前半）", SourceFilename = "b.wav", MeetingDate = "2026-11-16", CreatedAt = "2026-11-16T01:00:00Z", DurationSeconds = 37,
            Language = "zh", UserNotes = "[00:10] 前半的筆記", Participants = "阿傑",
        }, "wav");
        var otherId = other.Id!.Value;
        WriteMono(paths.AudioPath(other), 37, 8000, 0.1f);
        db.SaveTranscript(otherId, [
            new Segment { MeetingId = otherId, Seq = 0, Start = 0, End = 10, Speaker = 1, Text = "前半一" },
            new Segment { MeetingId = otherId, Seq = 1, Start = 30, End = 36.88, Speaker = 2, Text = "前半二" },
        ], 37, 2, new Dictionary<int, SpeakerVoice> { [1] = new(OneHot(7), 10, "mix"), [2] = new(OneHot(8), 6.88, "mix") });
        db.RenameSpeaker(otherId, 2, "小美");
        db.AddBookmark(otherId, 12, "前半");
        var document = db.AddDocument(otherId, "議程.txt", DocumentKind.Text, ["議程"], false);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.DocumentPath(document))!);
        File.WriteAllText(paths.DocumentPath(document), "議程");
        var otherBefore = Snapshot(otherId);
        var before = Snapshot(id);

        await controller.ApplyMeetingEdit(id, new EditPlan([new(otherId, 0, 37), new(id, 0, 59)]), null);
        Assert.Empty(errors);
        Assert.Equal("已合併「包裝會議（前半）」，那場移到垃圾桶。會議紀錄正在重新整理", notices[^1]);
        var m = db.Meeting(id)!;
        Assert.Equal((96, "2026-11-16T01:00:00Z", "阿傑"), (m.DurationSeconds, m.CreatedAt, m.Participants));
        Assert.Equal("[00:10] 前半的筆記\n\n[00:45] 包裝\n[00:57] 預算\n沒有時間的一行\n[01:27] 結論", m.UserNotes);
        Assert.Equal(["前半一", "前半二", "開場", "包裝", "閒聊", "預算", "散會"], db.Segments(id).Select(s => s.Text));
        // 「小美」同名對到這場的 2；前半的說話者 1 聲紋對不上，接在後面變成說話者 3
        Assert.Equal([3, 2, 1, 2, 1, 2, 1], db.Segments(id).Select(s => s.Speaker ?? 0));
        Assert.Equal(["說話者 1", "小美", "說話者 3"], db.Speakers(id).Select(s => s.Name));
        Assert.Equal([12.0, 47, 67, 87], db.Bookmarks(id).Select(b => Math.Round(b.Time, 6)));
        var copied = db.Documents(id).Single();
        Assert.Equal("議程", File.ReadAllText(paths.DocumentPath(copied)));
        Assert.NotNull(db.Meeting(otherId)!.DeletedAt);
        Assert.Equal(otherBefore.Replace("|True", "|False"), Snapshot(otherId).Replace("|True", "|False"));
        Assert.Equal(JobKind.Summarize, db.LatestJob(id)!.Kind);
        // 錄音：16 kHz（取高的）單聲道 16 位元，長度 96 秒
        using (var reader = new WavReader(paths.AudioPath(m))) Assert.Equal((1, 16000, 96L * 16000), (reader.Channels, reader.SampleRate, reader.Frames));
        Assert.Equal([otherId], db.EditRecord(id)!.MergedIds);

        Assert.Contains("合併進來的「包裝會議（前半）」會從垃圾桶還原。", controller.RestoreEditMessage(id));
        // 重新整理還在排隊時不能還原（同編輯）
        Assert.False(controller.RequestRestoreEdit(id));
        Assert.Equal("正在處理這場會議，完成後才能編輯", notices[^1]);
        db.Write(d => d.Execute("DELETE FROM jobs"));
        await controller.RestoreMeetingEdit(id);
        Assert.Equal("已還原成編輯前的樣子，合併進來的會議已從垃圾桶拿出來", notices[^1]);
        Assert.Equal(before, Snapshot(id));
        Assert.Equal(otherBefore, Snapshot(otherId));
    }

    /// <summary>裁切加分成兩場（R107.5）：5～80、100～170 秒保留，在新時間軸的 50 秒切開 → 50＋95 秒；還原後分出去的那場移到垃圾桶</summary>
    [Fact]
    public async Task TrimSplitAndRestore()
    {
        var controller = Controller();
        var id = SeedSplitMeeting();
        var before = Snapshot(id);
        var plan = new EditPlan([new(id, 5, 80), new(id, 100, 170)]);
        await controller.ApplyMeetingEdit(id, plan, 50);
        Assert.Empty(errors);
        var created = db.MeetingRows().Select(r => r.Meeting).Single(r => r.Id != id);
        Assert.Equal((50, 95), (db.Meeting(id)!.DurationSeconds, created.DurationSeconds));
        using (var reader = new WavReader(paths.AudioPath(db.Meeting(id)!))) Assert.Equal(50L * 16000, reader.Frames);
        using (var reader = new WavReader(paths.AudioPath(created))) Assert.Equal(95L * 16000, reader.Frames);
        Assert.Equal([created.Id!.Value], db.EditRecord(id)!.SplitIds);
        Assert.Contains($"分出去的「{created.DisplayTitle}」會移到垃圾桶。", controller.RestoreEditMessage(id));

        await controller.RestoreMeetingEdit(id);
        Assert.Equal("已還原成編輯前的樣子，分出去的會議移到垃圾桶", notices[^1]);
        Assert.Equal(before, Snapshot(id));
        Assert.NotNull(db.Meeting(created.Id!.Value)!.DeletedAt);
    }

    /// <summary>最後一次編輯 30 天後清掉快照和編輯前的錄音（R107.8）；沒人用的檔（會議永久刪除了）一起刪</summary>
    [Fact]
    public async Task PurgeExpiredEdits()
    {
        var controller = Controller();
        var id = SeedTrimMeeting();
        await controller.ApplyMeetingEdit(id, new EditPlan([new(id, 2, 50)]), null);
        var kept = Path.Combine(paths.EditsAudio, $"{id}.wav");
        var orphan = Path.Combine(paths.EditsAudio, "999.m4a");
        File.WriteAllText(orphan, "x");
        db.AgeEdit(id, 29);
        controller.PurgeExpiredEdits();
        Assert.NotNull(db.EditRecord(id));
        Assert.Equal(1, db.EditRecord(id)!.DaysLeft());
        Assert.True(File.Exists(kept));
        Assert.False(File.Exists(orphan));
        db.AgeEdit(id, 31);
        controller.PurgeExpiredEdits();
        Assert.Null(db.EditRecord(id));
        Assert.False(File.Exists(kept));
    }

    /// <summary>不能編輯的時候說原因（R107.1）：垃圾桶裡、有工作在排</summary>
    [Fact]
    public void EditProblems()
    {
        var controller = Controller();
        var id = SeedTrimMeeting();
        Assert.True(controller.RequestEditMeeting(id));
        db.Enqueue(id, JobKind.Correct);
        Assert.False(controller.RequestEditMeeting(id));
        Assert.Equal("正在處理這場會議，完成後才能編輯", notices[^1]);
        db.Write(d => d.Execute("DELETE FROM jobs"));
        db.SetTrashed(id, true);
        Assert.False(controller.RequestEditMeeting(id));
        Assert.Equal("垃圾桶裡的會議要先還原才能操作", notices[^1]);
        Assert.Equal("", controller.EditProblem(12345));
    }

    /// <summary>照片段依序讀（M§81.2）：換取樣率、補安靜（沒有錄音、錄音比片段短），長度是每段 round(長度 × 取樣率) 的和；可以跳到中間</summary>
    [Fact]
    public void PlanStreamResamplesAndFillsSilence()
    {
        var a = Path.Combine(home.Path, "a.wav");
        WriteMono(a, 2, 8000, 0.5f);
        using var reader = new AudioEdit.WavPcmReader(a);
        var plan = new EditPlan([new(1, 0, 1), new(2, 0, 0.5), new(1, 1.5, 2.5)]);
        using var stream = new PlanPcmStream(plan, id => id == 1 ? reader : null, 16000, 2);
        Assert.Equal(16000 + 8000 + 16000, stream.TotalFrames);
        var all = new List<float>();
        var buffer = new float[3000 * 2];
        int n;
        while ((n = stream.Read(buffer, 3000)) > 0) all.AddRange(buffer[..(n * 2)]);
        Assert.Equal(40000 * 2, all.Count);
        // 第一段有聲音、兩個聲道一樣；第二段（沒有錄音）安靜；第三段後半（錄音只到 2 秒）安靜
        Assert.True(all.Skip(2000 * 2).Take(2000).All(v => v > 0.05f));
        Assert.Equal(all[4000], all[4001]);
        Assert.True(all.Skip(16000 * 2).Take(8000 * 2).All(v => v == 0));
        Assert.True(all.Skip((24000 + 9000) * 2).All(v => v == 0));
        Assert.Contains(all.Skip(24000 * 2).Take(6000 * 2), v => v > 0.05f);
        stream.Seek(1.25);
        Assert.Equal(1.25, stream.Position, 6);
        Assert.Equal(1000, stream.Read(buffer, 1000));
        Assert.True(buffer.Take(2000).All(v => v == 0));
    }

    /// <summary>編輯畫面的狀態（M§81.5）：把手、刪掉的段落、切點換成片段；合併、取消合併；摘要和套用前的說明</summary>
    [Fact]
    public void EditorState()
    {
        var controller = Controller();
        var id = SeedTrimMeeting();
        var editor = new MeetingEditor(controller, id);
        editor.Load();
        Assert.Equal((59.0, 0.0, 59.0, false), (editor.Total, editor.Start, editor.End, editor.HasChanges));
        Assert.Equal("包裝會議・原本 00:59", editor.Subtitle);
        Assert.Equal(("剩 00:59", "（拖兩端的把手裁切，或在波形上選一段刪掉）"), editor.Summary);
        Assert.Equal([("說話者 1", 1), ("小美", 2)], editor.Legend());
        editor.SetStart(2);
        editor.SetEnd(55);
        editor.SetSelection((30, 20));
        editor.DeleteSelection();
        Assert.Null(editor.Selection);
        Assert.Equal([(id, 2.0, 20.0), (id, 30.0, 55.0)], editor.Plan.Pieces.Select(p => (p.MeetingId, p.Start, p.End)));
        Assert.Equal(("剩 00:43", "（刪掉 00:16、2 句、0 個標記重點）".Replace("、0 個標記重點", "")), editor.Summary);
        Assert.Equal(2, editor.RemovedLines);
        Assert.Equal(
            "・會議從 00:59 變成 00:43：裁掉開頭 00:02，結尾 00:04，刪掉中間 1 段（00:10）。\n・刪掉 2 句逐字稿；我的筆記都留著。\n" +
            "・會議紀錄要重新整理：設定好整理工具後按「重新整理」。\n・30 天內可以從「更多」還原成編輯前的樣子。", editor.ApplyMessage(false));
        // 切點：不能在刪掉的段落裡、不能太靠近把手
        editor.CutAt(25);
        Assert.Null(editor.Cut);
        editor.CutAt(40);
        Assert.Equal(28.0, editor.PlanCut);
        Assert.Equal(("分成兩場：00:28 和 00:15", "（刪掉 00:16、2 句）"), editor.Summary);
        // 逐字稿的句子：刪掉、恢復
        var line = editor.Lines.Single(l => l.Text == "開場");
        editor.DeleteLines([line]);
        Assert.False(editor.Kept(line.Mid));
        editor.RestoreLines([line]);
        Assert.True(editor.Kept(line.Mid));
        editor.ToggleCut();
        Assert.Null(editor.Cut);
        // 合併：先開的那場在前，這場的裁切變成刪掉的段落；取消合併回到把手
        var other = db.InsertMeeting(new Meeting { Title = "前一場", SourceFilename = "b.wav", MeetingDate = "2026-11-15", CreatedAt = "2026-11-15T01:00:00Z", DurationSeconds = 30 }, null);
        Assert.Equal([other.Id!.Value], controller.MergeCandidates(id).Near.Select(r => r.Id));
        editor.Merge(other);
        Assert.Equal((89.0, 0.0, 89.0), (editor.Total, editor.Start, editor.End));
        Assert.Equal([(30.0, 32.0), (50.0, 60.0), (85.0, 89.0)], editor.Deleted);
        Assert.Equal([(other.Id!.Value, 0.0, 30.0), (id, 2.0, 20.0), (id, 30.0, 55.0)], editor.Plan.Pieces.Select(p => (p.MeetingId, p.Start, p.End)));
        Assert.Equal("包裝會議・原本 00:59（合併「前一場」00:30）", editor.Subtitle);
        Assert.Equal(("合併後 01:13", "（這場 00:43＋「前一場」00:30）"), editor.Summary);
        editor.Unmerge();
        Assert.Equal((59.0, 2.0, 55.0), (editor.Total, editor.Start, editor.End));
        Assert.Equal([(20.0, 30.0)], editor.Deleted);
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
