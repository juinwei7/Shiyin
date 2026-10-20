using System.Text.Json;
using Shiyin.Data;
using Shiyin.LLM;
using Shiyin.Localization;
using Shiyin.Model;
using Shiyin.Notes;
using Shiyin.Transcribe;
using Shiyin.Whisper;

namespace Shiyin.Services;

/// <summary>校稿工作的 payload（JSON 跟 Mac 相同）</summary>
public sealed record RevisePayload(List<ReviewAnswerJson> Answers);
public sealed record ReviewAnswerJson(string Time, string Question, string Answer);
/// <summary>整理工作的 payload：draft 是用即時逐字稿整理草稿（M§21.3）</summary>
public sealed record SummarizePayload(bool Draft = false);

/// <summary>執行中的工作（顯示用）</summary>
public sealed class RunningJob(Job job)
{
    public Job Job { get; } = job;
    public DateTime StartedAt { get; } = DateTime.UtcNow;
    /// <summary>目前階段，顯示用</summary>
    public string Stage { get; internal set; } = "";
    public double? Progress { get; internal set; }
    public LLMProvider? Provider { get; internal set; }
    internal CancellationTokenSource Cancel { get; } = new();

    /// <summary>預估剩餘秒數：已經過時間 ÷ 目前進度</summary>
    public TimeSpan? Remaining
    {
        get
        {
            if (Progress is not double p || p <= 0.03) return null;
            var elapsed = DateTime.UtcNow - StartedAt;
            return elapsed / p - elapsed;
        }
    }
}

/// <summary>
/// 工作佇列（M§21.2）：轉錄線、整理線、輔助線各跑一個迴圈，互不等待。事件在背景執行緒觸發，畫面要自己切回 UI 執行緒
/// </summary>
public sealed class JobRunner
{
    readonly AppDatabase db;
    readonly AppSettings settings;
    readonly ShiyinPaths paths;
    readonly ModelStore models;
    readonly LLMClient llm;
    readonly IAudioDecoder decoder;
    readonly ISpeechEngines engines;
    readonly IAudioCompressor compressor;
    readonly IPowerRequests power;
    readonly object gate = new();
    readonly Dictionary<JobLane, RunningJob> running = [];
    readonly Dictionary<JobLane, SemaphoreSlim> wakeups = Enum.GetValues<JobLane>().ToDictionary(l => l, _ => new SemaphoreSlim(0));
    readonly CancellationTokenSource stopping = new();
    readonly List<Task> loops = [];

    /// <summary>工作開始、結束、進度以外的狀態改變</summary>
    public event Action? Changed;
    public event Action<Job, Exception?>? Finished;
    /// <summary>錄音壓縮成 m4a，換了檔案（正在播的要接著播）</summary>
    public event Action<long>? AudioReplaced;
    /// <summary>正式版取代了使用者改過的草稿（M§21.3）</summary>
    public event Action<long>? EditedDraftReplaced;
    /// <summary>重新辨識說話者做完（分出幾位）或失敗（M§23.3）</summary>
    public event Action<long, int?, Exception?>? Rediarized;

    /// <summary>每一線找不到工作時等多久再找一次</summary>
    public TimeSpan IdleDelay { get; init; } = TimeSpan.FromSeconds(2);

    public JobRunner(AppDatabase db, AppSettings settings, ShiyinPaths paths, ModelStore models, LLMClient llm, IAudioDecoder decoder,
                     ISpeechEngines engines, IAudioCompressor compressor, IPowerRequests power)
    {
        (this.db, this.settings, this.paths, this.models, this.llm) = (db, settings, paths, models, llm);
        (this.decoder, this.engines, this.compressor, this.power) = (decoder, engines, compressor, power);
        models.Installed += _ => Wake();
    }

    public void Start()
    {
        try { db.RequeueRunning(); } catch (Exception e) { Log.Error($"requeue failed: {e.GetType().Name}"); }
        foreach (var lane in Enum.GetValues<JobLane>()) loops.Add(Task.Run(() => Loop(lane)));
    }

    public void Wake()
    {
        foreach (var w in wakeups.Values) if (w.CurrentCount == 0) w.Release();
    }

    public bool IsBusy { get { lock (gate) return running.Count > 0; } }

    public RunningJob? Running(JobLane lane) { lock (gate) return running.GetValueOrDefault(lane); }

    /// <summary>畫面上顯示的執行中工作：轉錄優先，其次是整理（使用者在等的）；背景的輔助工作（壓縮、校正、猜說話者）不算</summary>
    public RunningJob? RunningJob(long meetingId)
    {
        lock (gate)
            return Enum.GetValues<JobLane>().Select(l => running.GetValueOrDefault(l))
                .FirstOrDefault(r => r is not null && r.Job.MeetingId == meetingId && !r.Job.Kind.IsAuxiliary());
    }

    public void Cancel(long meetingId)
    {
        try
        {
            var ids = db.CancelJobs(meetingId).ToHashSet();
            lock (gate)
                foreach (var r in running.Values.Where(r => ids.Contains(r.Job.Id ?? -1))) r.Cancel.Cancel();
        }
        catch (Exception e) { Log.Error($"cancel failed: {e.GetType().Name}"); }
        Changed?.Invoke();
    }

    /// <summary>結束 app 時：執行中的工作先改回排隊（不算意外結束，M§20.5），再停止，下次開啟會從頭重跑。等最多 timeout</summary>
    public void TerminateAll(TimeSpan timeout)
    {
        try { db.ReleaseRunning(); } catch (Exception e) { Log.Error($"release running jobs failed: {e.GetType().Name}"); }
        stopping.Cancel();
        lock (gate) foreach (var r in running.Values) r.Cancel.Cancel();
        Wake();
        Task.WaitAll([.. loops], timeout);
    }

    async Task Loop(JobLane lane)
    {
        while (!stopping.IsCancellationRequested)
        {
            Job? job = null;
            try
            {
                if (lane != JobLane.Transcribe || PrepareTranscription()) job = db.ClaimNext(lane);
            }
            catch (Exception e) { Log.Error($"claim failed: {e.GetType().Name}"); }
            if (job is not null)
            {
                await Execute(job, lane);
                continue;
            }
            try { await wakeups[lane].WaitAsync(IdleDelay, stopping.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// 轉錄線能不能接工作：模型還沒下載好的語言先用快速辨識（M§21.17），Breeze、日文模型在背景下載；
    /// 快速辨識、說話者辨識的模型也還沒好時先不接（Mac 的內建辨識不用下載，Windows 要，design-windows §8）
    /// </summary>
    bool PrepareTranscription()
    {
        var waiting = false;
        if (!settings.UseFastTranscriber)
            foreach (var language in Enum.GetValues<MeetingLanguage>())
                if (!models.HasTranscriptionModel(language) && db.HasQueuedTranscribe(language))
                {
                    models.AutoStart(ModelStore.Transcription(language));
                    if (!models.IsInstalled(ModelStore.Model.FastRecognition)) waiting = true;
                }
        if (settings.UseFastTranscriber && !models.IsInstalled(ModelStore.Model.FastRecognition)) waiting = true;
        if (waiting) models.AutoStart(ModelStore.Model.FastRecognition);
        if (settings.Diarize && !models.IsInstalled(ModelStore.Model.Diarization))
        {
            models.AutoStart(ModelStore.Model.Diarization);
            // 下載失敗時不擋：這次不分說話者
            if (models.StateOf(ModelStore.Model.Diarization) is not ModelStore.State.Failed) waiting = true;
        }
        return !waiting;
    }

    async Task Execute(Job job, JobLane lane)
    {
        var state = new RunningJob(job);
        lock (gate) running[lane] = state;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(state.Cancel.Token, stopping.Token);
        Exception? failure = null;
        var notify = true;
        using (power.KeepAwake(L.T("拾音正在轉錄或整理會議紀錄")))
        {
            Changed?.Invoke();
            Log.Info($"job {job.Id} {job.Kind.Code()} started");
            try
            {
                switch (job.Kind)
                {
                    case JobKind.Transcribe: await Task.Run(() => Transcribe(job, state, cancel.Token)); break;
                    case JobKind.Summarize:
                        var draft = Decode<SummarizePayload>(job.Payload)?.Draft ?? false;
                        notify = await Summarize(job, state, null, draft, cancel.Token);
                        break;
                    case JobKind.Revise:
                        var answers = Decode<RevisePayload>(job.Payload)?.Answers ?? throw LLMException.Failed("payload");
                        notify = await Summarize(job, state, answers.Select(a => new ReviewAnswer(a.Time, a.Question, a.Answer)).ToList(), false, cancel.Token);
                        break;
                    case JobKind.Compress: await Compress(job, cancel.Token); break;
                    case JobKind.Correct: await Correct(job, state, cancel.Token); break;
                    case JobKind.NameSpeakers: await NameSpeakers(job, state, cancel.Token); break;
                    case JobKind.Rediarize: await Task.Run(() => Rediarize(job, state, cancel.Token)); break;
                }
            }
            catch (Exception e)
            {
                failure = e;
            }
        }
        lock (gate) running.Remove(lane);

        var jobId = job.Id!.Value;
        if (db.JobStatusOf(jobId) == JobStatus.Running)
        {
            try
            {
                if (failure is not null)
                {
                    db.Finish(jobId, JobStatus.Failed, Message(failure), failure.LogSummary());
                    Log.Error($"job {jobId} failed: {failure.LogSummary()}");
                }
                else
                {
                    db.Finish(jobId, JobStatus.Done);
                    Log.Info($"job {jobId} done");
                }
            }
            catch (Exception e) { Log.Error($"finish failed: {e.GetType().Name}"); }
            // 壓縮、逐字稿校正是背景的輔助工作，不發通知；有草稿時正式版不再發（M§21.3）
            if (!job.Kind.IsAuxiliary() && (notify || failure is not null)) Finished?.Invoke(job, failure);
        }
        Changed?.Invoke();
    }

    static string Message(Exception e) => e is OperationCanceledException ? L.T("已取消") : e.Message;

    /// <summary>payload 的 JSON（跟 Mac 的 Codable 相同的欄位）。不用反射的序列化，修剪過的 app 也能用</summary>
    static T? Decode<T>(string? json) where T : class
    {
        try
        {
            using var doc = JsonDocument.Parse(json ?? "{}");
            var root = doc.RootElement;
            if (typeof(T) == typeof(SummarizePayload))
                return new SummarizePayload(root.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) as T;
            if (typeof(T) == typeof(RevisePayload))
                return new RevisePayload(root.GetProperty("answers").EnumerateArray().Select(a => new ReviewAnswerJson(
                    a.GetProperty("time").GetString() ?? "", a.GetProperty("question").GetString() ?? "", a.GetProperty("answer").GetString() ?? "")).ToList()) as T;
            return null;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    public static string Encode(SummarizePayload payload) => new System.Text.Json.Nodes.JsonObject { ["draft"] = payload.Draft }.ToJsonString();

    public static string Encode(RevisePayload payload) => new System.Text.Json.Nodes.JsonObject
    {
        ["answers"] = new System.Text.Json.Nodes.JsonArray(payload.Answers.Select(a => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
        {
            ["time"] = a.Time, ["question"] = a.Question, ["answer"] = a.Answer,
        }).ToArray()),
    }.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    bool IsStillRunning(Job job) => db.JobStatusOf(job.Id!.Value) == JobStatus.Running;

    // MARK: 轉錄（M§6）

    void Transcribe(Job job, RunningJob state, CancellationToken cancel)
    {
        if (db.Meeting(job.MeetingId) is not { } meeting) return;
        var options = TranscriptionOptions.Current(settings, models, meeting.MeetingLanguage, db.Glossary().Select(t => t.Term).ToList(),
            meeting.Channels, meeting.MinSpeakers);
        if (options.Engine == TranscriptionOptions.EngineKind.Fast && !settings.UseFastTranscriber)
            Log.Info("transcription model not ready, using the fast recognizer");
        var pipeline = new TranscriptionPipeline(options, decoder, engines, options.Model ?? models.PathOf(ModelStore.Transcription(options.Language)))
        {
            Stage = s => state.Stage = s,
            Progress = p => state.Progress = p,
            ShouldContinue = () => IsStillRunning(job),
        };
        if (pipeline.Run(paths.AudioPath(meeting), cancel) is not { } result) return;
        var segments = result.Segments.Select((s, i) => new Segment
        {
            MeetingId = job.MeetingId, Seq = i, Start = s.Start, End = s.End, Text = s.Text, LowConfidence = s.LowConfidence,
            Speaker = result.Speakers[i], Kind = "final", Channel = result.Channels[i],
        }).ToList();
        db.SaveTranscript(job.MeetingId, segments, (int)Math.Round(result.Duration, MidpointRounding.AwayFromZero),
            result.Speakers.Max() ?? 0, result.Voices, options.TranscribedWith);
        // 說話者辨識的碰撞率（M§24.3）：可能把人併在一起時，逐字稿分頁會提示
        db.SetSpeakerCheck(job.MeetingId, result.DiarizationQuality?.MergeRisk);
        // 聲紋比對（M§21.4）：在整理之前，會議紀錄裡就會是名字
        if (settings.Voiceprints)
        {
            var matched = db.MatchVoiceprints(job.MeetingId);
            if (matched > 0) Log.Info($"voiceprints matched {matched} speakers");
        }
        if (settings.CompressAudio && Importer.Extension(meeting.AudioFile) == "wav") db.Enqueue(job.MeetingId, JobKind.Compress);
        // 校正在輔助線、整理在整理線，兩個同時開始（M§21.2、R20.1）
        if (settings.CorrectTranscript && settings.Provider != LLMProvider.None) db.Enqueue(job.MeetingId, JobKind.Correct);
        if (settings.AutoSummarize && settings.Provider != LLMProvider.None)
        {
            db.Enqueue(job.MeetingId, JobKind.Summarize);
            // 還有預設名稱的說話者就猜名字（M§21.5）：在輔助線、校正後面，不擋整理
            if (db.Speakers(job.MeetingId).Any(s => s.NameSource == "default")) db.Enqueue(job.MeetingId, JobKind.NameSpeakers);
        }
        Wake();
    }

    // MARK: 重新辨識說話者（M§23.3、R22）

    /// <summary>只重跑說話者辨識，用這場記住的人數；逐字稿文字、校正、重點、會議紀錄都不動</summary>
    void Rediarize(Job job, RunningJob state, CancellationToken cancel)
    {
        if (db.Meeting(job.MeetingId) is not { } meeting) return;
        try
        {
            state.Stage = L.T("重新辨識說話者");
            var samples = decoder.Decode(paths.AudioPath(meeting), cancel);
            if (!IsStillRunning(job)) return;
            var output = engines.Diarize(samples, meeting.MinSpeakers, p => state.Progress = p, cancel);
            if (!IsStillRunning(job)) return;

            var segments = db.Segments(job.MeetingId);
            var transcribed = segments.Select(s => new TranscribedSegment(s.Start, s.End, s.Text, s.LowConfidence)).ToList();
            var (speakers, numbering) = SpeakerRules.AssignNumbering(output.Turns, transcribed);
            var source = meeting.Channels == "both" ? "mix" : meeting.Channels;
            var voices = new Dictionary<int, SpeakerVoice>();
            foreach (var (raw, number) in numbering)
                if (output.Voiceprints.TryGetValue(raw, out var embedding))
                    voices[number] = new SpeakerVoice(embedding, output.SpeechSeconds.GetValueOrDefault(raw), source);
            var old = db.Speakers(job.MeetingId);
            // 使用者改過說話者的句子（M§24.2）不照新的結果，跟著原本那位說話者對應到的新說話者
            var assigned = speakers.ToList();
            var auto = Enumerable.Range(0, segments.Count).Where(i => !segments[i].SpeakerManual).ToList();
            var inherited = SpeakerRules.InheritNames(old, auto.Select(i => segments[i]).ToList(), auto.Select(i => speakers[i]).ToList());
            var mapping = SpeakerRules.OldToNew(segments, speakers);
            var extra = new Dictionary<int, int>();
            var next = (speakers.Max() ?? 0) + 1;
            for (var i = 0; i < segments.Count; i++)
            {
                if (!segments[i].SpeakerManual || segments[i].Speaker is not int o) continue;
                if (mapping.TryGetValue(o, out var n)) { assigned[i] = n; continue; }
                if (!extra.ContainsKey(o))
                {
                    extra[o] = next;
                    // 預設名稱不沿用（編號變了，用新編號的預設名稱）
                    if (old.FirstOrDefault(s => s.Idx == o) is { } speaker && speaker.NameSource != "default")
                        inherited[next] = new SpeakerRules.Inherited(speaker.Name, speaker.NameSource, speaker.PersonId);
                    next++;
                }
                assigned[i] = extra[o];
            }
            db.ReassignSpeakers(job.MeetingId, segments.Select((s, i) => (s.Id!.Value, assigned[i])).ToList(), voices, inherited);
            db.SetSpeakerCheck(job.MeetingId, output.Quality?.MergeRisk);
            // 沿用使用者取的名字：用新的、比較乾淨的分群重新學聲紋（自動帶入的名字本來就不學）
            foreach (var (idx, item) in inherited.Where(p => p.Value.Source != "voiceprint"))
                db.LearnVoiceprint(job.MeetingId, idx, item.Name, item.Source);
            if (settings.Voiceprints) db.MatchVoiceprints(job.MeetingId);
            if (settings.Provider != LLMProvider.None && db.Speakers(job.MeetingId).Any(s => s.NameSource == "default"))
                db.Enqueue(job.MeetingId, JobKind.NameSpeakers);
            var count = assigned.Where(a => a is not null).Distinct().Count();
            Log.Info($"rediarized meeting {job.MeetingId}: {count} speakers, {inherited.Count} names kept");
            Rediarized?.Invoke(job.MeetingId, count, null);
            Wake();
        }
        catch (Exception e)
        {
            Rediarized?.Invoke(job.MeetingId, null, e);
            throw;
        }
    }

    // MARK: 猜說話者（M§21.5、R19.2）

    /// <summary>從對話內容猜還沒有名字的說話者，存成建議。輔助功能：失敗只記 log</summary>
    async Task NameSpeakers(Job job, RunningJob state, CancellationToken cancel)
    {
        try
        {
            if (db.Meeting(job.MeetingId) is not { } meeting) return;
            var provider = settings.Provider;
            if (provider == LLMProvider.None) return;
            var speakers = db.Speakers(job.MeetingId);
            var unnamed = speakers.Where(s => s.NameSource == "default").Select(s => s.Idx).ToList();
            var segments = db.Segments(job.MeetingId);
            if (unnamed.Count == 0 || segments.Count == 0) return;
            state.Provider = provider;
            state.Stage = L.T("猜說話者");
            var names = speakers.ToDictionary(s => s.Idx, s => s.Name);
            var language = meeting.MeetingLanguage;
            var context = new SpeakerNaming.Context(language, meeting.MeetingDate, meeting.Title, meeting.ParticipantList, db.PeopleNames(),
                db.Glossary().Select(t => t.Term).ToList(), unnamed,
                idx => names.TryGetValue(idx, out var n) ? n : language.Text($"說話者 {idx}", $"Speaker {idx}", $"話者 {idx}"),
                segments.Select(s => (s.Start, s.Speaker, s.Text)).ToList());
            var json = await llm.GenerateJson(provider, settings.Model, SpeakerNaming.System(language), SpeakerNaming.Prompt(context),
                SpeakerNaming.Format, cancel);
            var suggestions = SpeakerNaming.Validate(ParseSuggestions(json, provider), context);
            if (!IsStillRunning(job)) return;
            db.SaveSpeakerSuggestions(job.MeetingId, suggestions);
            Log.Info($"speaker names suggested: {suggestions.Count}");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Error($"speaker naming failed: {e.LogSummary()}");
        }
    }

    static List<SpeakerNaming.Suggestion> ParseSuggestions(string json, LLMProvider provider)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("speakerNames").EnumerateArray().Select(e => new SpeakerNaming.Suggestion(
                e.GetProperty("speaker").GetInt32(), e.GetProperty("name").GetString() ?? "", e.GetProperty("reason").GetString() ?? "",
                e.GetProperty("time").GetString() ?? "")).ToList();
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw LLMException.BadOutput(e.Message + provider.BadOutputHint());
        }
    }

    // MARK: 逐字稿校正（M§17.4）

    /// <summary>找出可能聽錯的字，存成待確認的建議。輔助功能：失敗只記 log，不影響整理會議紀錄</summary>
    async Task Correct(Job job, RunningJob state, CancellationToken cancel)
    {
        try
        {
            if (db.Meeting(job.MeetingId) is not { } meeting) return;
            var provider = settings.Provider;
            if (provider == LLMProvider.None) return;
            var segments = db.Segments(job.MeetingId);
            if (segments.Count == 0) return;
            state.Provider = provider;
            state.Stage = L.T("檢查可能聽錯的字");
            var language = meeting.MeetingLanguage;
            var names = db.Speakers(job.MeetingId).ToDictionary(s => s.Idx, s => s.Name);
            string? SpeakerName(int? idx) => idx is int i ? names.TryGetValue(i, out var n) ? n : language.Text($"說話者 {i}", $"Speaker {i}", $"話者 {i}") : null;
            var context = new TranscriptCorrection.Context(language, meeting.Title, meeting.ParticipantList,
                names.Values.Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList(), db.Glossary().Select(t => t.Term).ToList(),
                segments.Select(s => (s.Text, SpeakerName(s.Speaker))).ToList());
            var result = new List<CorrectionSuggestion>();
            for (var start = 0; start < segments.Count; start += TranscriptCorrection.ChunkSize)
            {
                var end = Math.Min(start + TranscriptCorrection.ChunkSize, segments.Count);
                var json = await llm.GenerateJson(provider, settings.Model, TranscriptCorrection.System(language),
                    TranscriptCorrection.Prompt(context, start, end), TranscriptCorrection.Format, cancel);
                result.AddRange(TranscriptCorrection.Validate(ParseCorrections(json, provider), context, start, end));
                state.Progress = (double)end / segments.Count;
            }
            if (!IsStillRunning(job)) return;
            db.SaveCorrections(job.MeetingId, result.Where(s => segments[s.Segment].Id is not null).Select(s => new Correction
            {
                MeetingId = job.MeetingId, SegmentId = segments[s.Segment].Id!.Value, Original = s.Original, Suggestion = s.Suggestion, Reason = s.Reason,
            }).ToList());
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Error($"transcript correction failed: {e.LogSummary()}");
        }
    }

    static List<CorrectionSuggestion> ParseCorrections(string json, LLMProvider provider)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("corrections").EnumerateArray().Select(e => new CorrectionSuggestion(
                e.GetProperty("segment").GetInt32(), e.GetProperty("original").GetString() ?? "", e.GetProperty("suggestion").GetString() ?? "",
                e.GetProperty("reason").GetString() ?? "")).ToList();
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw LLMException.BadOutput(e.Message + provider.BadOutputHint());
        }
    }

    // MARK: 壓縮（M§6.4）

    async Task Compress(Job job, CancellationToken cancel)
    {
        if (db.Meeting(job.MeetingId) is not { } meeting || !paths.HasAudio(meeting) || Importer.Extension(meeting.AudioFile) != "wav") return;
        var source = paths.AudioPath(meeting);
        var output = Path.Combine(paths.Audio, $"{job.MeetingId}.m4a");
        File.Delete(output);
        try { await compressor.Compress(source, output, cancel); }
        catch
        {
            File.Delete(output);
            throw;
        }
        db.SetAudioFile(job.MeetingId, Path.GetFileName(output));
        try { File.Delete(source); } catch (IOException) { }
        AudioReplaced?.Invoke(job.MeetingId);
    }

    // MARK: 整理（M§8）

    /// <summary>回傳要不要發通知：草稿沒做（正式逐字稿已經好了）、正式版取代了草稿時不發</summary>
    async Task<bool> Summarize(Job job, RunningJob state, List<ReviewAnswer>? answers, bool draft, CancellationToken cancel)
    {
        if (db.Meeting(job.MeetingId) is not { } meeting) return false;
        // 草稿（M§21.3）：正式逐字稿已經好了就不做（短的會議轉錄比較快）
        if (draft && db.Segments(job.MeetingId).Count > 0) return false;
        var provider = settings.Provider;
        if (provider == LLMProvider.None) throw LLMException.Failed(L.T("整理工具設定為「不整理」，請到設定改成 Claude、Codex 或 API"));
        state.Provider = provider;
        state.Stage = draft ? L.T("整理草稿") : answers is null ? L.T("整理") : L.T("校稿");
        var segments = db.Segments(job.MeetingId, draft ? "live" : "final");
        if (segments.Count == 0 && meeting.UserNotes.Length == 0) throw LLMException.Failed(L.T("還沒有逐字稿，請先完成轉錄"));
        var templates = db.Templates();
        var template = templates.FirstOrDefault(t => t.Id == meeting.TemplateId) ?? templates.FirstOrDefault(t => t.Id == "general");
        var context = new NotesContext(meeting, segments, db.Speakers(job.MeetingId).ToDictionary(s => s.Idx, s => s.Name),
            db.Bookmarks(job.MeetingId), db.Glossary().Select(t => t.Term).ToList(),
            template?.DefinitionFor(meeting.MeetingLanguage) ?? TemplateDefinition.Decode("{}"));
        string prompt;
        if (answers is not null)
        {
            if (meeting.DecodedNotes is not { } current) return false;
            prompt = NotesPrompt.Revise(context, current, answers);
        }
        else prompt = NotesPrompt.Summarize(context);
        var notes = await llm.Generate(provider, settings.Model, NotesPrompt.System(meeting.MeetingLanguage), prompt, cancel);
        if (!IsStillRunning(job)) return false;
        if (draft)
        {
            // 整理草稿的期間正式逐字稿好了、或已經有會議紀錄：丟掉草稿
            if (db.Segments(job.MeetingId).Count > 0 || db.Meeting(job.MeetingId)?.Notes is not null) return false;
            db.SaveNotes(job.MeetingId, notes, provider.Code(), L.T("草稿"), NotesSource.Draft);
            return true;
        }
        var hadDraft = db.Meeting(job.MeetingId)?.NotesDraft ?? 0;
        var replacedEdited = db.SaveNotes(job.MeetingId, notes, provider.Code(), answers is null ? L.T("重新整理") : L.T("校稿"),
            answers is null ? NotesSource.Generated : NotesSource.Revised);
        if (replacedEdited) EditedDraftReplaced?.Invoke(job.MeetingId);
        return answers is not null || hadDraft == 0;
    }
}
