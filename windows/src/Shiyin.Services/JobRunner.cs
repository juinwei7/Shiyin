using System.Text.Json;
using Shiyin.Data;
using Shiyin.LLM;
using Shiyin.Localization;
using Shiyin.Model;
using Shiyin.Notes;
using Shiyin.Speakers;
using Shiyin.Transcribe;
using Shiyin.Whisper;

namespace Shiyin.Services;

/// <summary>校稿工作的 payload（JSON 跟 Mac 相同）</summary>
public sealed record RevisePayload(List<ReviewAnswerJson> Answers);
public sealed record ReviewAnswerJson(string Time, string Question, string Answer);
/// <summary>
/// 整理工作的 payload：draft 是用即時逐字稿整理草稿（M§21.3）；people 是只補上每個人的重點，其他段落不動（M§66）；
/// review 是再找不確定的地方，只加待確認（M§88）
/// </summary>
public sealed record SummarizePayload(bool Draft = false, bool People = false, bool Review = false);
/// <summary>轉錄工作的 payload（M§35.3）：Until 是只轉錄到第幾秒（沒人說話太久而自動停止的錄音）</summary>
/// <summary>until：只轉錄到第幾秒（M§35.3）；remoteId：企業伺服器上還沒拿到結果的工作（M§36.3），app 重開時接著傳、接著等</summary>
public sealed record TranscribePayload(double? Until, string? RemoteId = null);

/// <summary>執行中的工作（顯示用）</summary>
public sealed class RunningJob(Job job)
{
    public Job Job { get; } = job;
    public DateTime StartedAt { get; } = DateTime.UtcNow;
    /// <summary>目前階段，顯示用</summary>
    public string Stage { get; internal set; } = "";
    public double? Progress { get; internal set; }
    public LLMProvider? Provider { get; internal set; }
    /// <summary>轉錄交給企業伺服器時的狀態：有值就顯示「改在這台電腦轉錄」（R50.3、R50.5）</summary>
    public ServerNote? Server { get; internal set; }
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
    /// <summary>麥克風的「建議收錄」偵測好了（M§93.2）</summary>
    public event Action<long>? MicRegionsDetected;
    /// <summary>正式版取代了使用者改過的草稿（M§21.3）</summary>
    public event Action<long>? EditedDraftReplaced;
    /// <summary>重新辨識說話者做完（分出幾位）或失敗（M§23.3）</summary>
    public event Action<long, int?, Exception?>? Rediarized;
    /// <summary>企業伺服器連不上太久，自動改在這台電腦轉錄了（R93、M§67）：App 發通知</summary>
    public event Action<long>? ServerFallback;
    /// <summary>自動放進資料夾了（M§72）：會議 id、資料夾 id（R118）；App 提示「「X」已放進「Y」」</summary>
    public event Action<long, long?>? Organized;
    /// <summary>再找不確定的地方做完（M§88）：會議 id、多找到幾題</summary>
    public event Action<long, int>? MoreReviewFound;

    /// <summary>匿名使用統計（design-windows §12.10）；沒給（測試）就不累計</summary>
    public UsageStats? Usage { get; init; }

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
    /// <summary>「我的聲音」（R80、design-windows §11.20）：一段錄音辨識說話者、算聲紋。每次建新的辨識器，不跟轉錄線共用</summary>
    public Diarizer.Output DiarizeClip(float[] samples, CancellationToken cancel = default) => engines.Diarize(samples, null, _ => { }, cancel);

    bool PrepareTranscription()
    {
        // 轉錄用企業伺服器：這台電腦不用模型（R50.7），說話者也在伺服器辨識
        if (UsesEnterprise) return true;
        var waiting = false;
        if (!settings.UseFastTranscriber)
            foreach (var language in MeetingLanguages.All)
            {
                var wanted = ModelStore.Transcription(language, settings.TranscriptionEngine);
                if (models.IsInstalled(wanted) || !db.HasQueuedTranscribe(language)) continue;
                models.AutoStart(wanted);
                // Qwen 還沒下載好、Breeze 在時先用 Breeze（R43.7）；都沒有才等快速辨識
                if (wanted == ModelStore.Model.Qwen && ModelStore.BreezeStandsInForQwen(language) && models.IsInstalled(ModelStore.Model.Breeze)) continue;
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
                        var payload = Decode<SummarizePayload>(job.Payload) ?? new SummarizePayload();
                        notify = await Summarize(job, state, null, payload.Draft, cancel.Token, peopleOnly: payload.People, review: payload.Review);
                        break;
                    case JobKind.Revise:
                        var answers = Decode<RevisePayload>(job.Payload)?.Answers ?? throw LLMException.Failed("payload");
                        notify = await Summarize(job, state, answers.Select(a => new ReviewAnswer(a.Time, a.Question, a.Answer)).ToList(), false, cancel.Token);
                        break;
                    case JobKind.Compress: await Compress(job, cancel.Token); break;
                    case JobKind.Correct: await Correct(job, state, cancel.Token); break;
                    case JobKind.NameSpeakers: await NameSpeakers(job, state, cancel.Token); break;
                    case JobKind.Rediarize: await Task.Run(() => Rediarize(job, state, cancel.Token)); break;
                    case JobKind.Clean: await Clean(job, state, cancel.Token); break;
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
                    // 設定問題（沒登入、key 錯誤…）：暫停自動整理，設定改了、登入了、測試成功才再排（R46.3、M§34.4）
                    if (job.Kind is JobKind.Summarize or JobKind.Revise && failure is LLMException { IsSetupProblem: true }
                        && settings.Provider != LLMProvider.None)
                    {
                        settings.PauseSummarizer(settings.Provider, failure.Message);
                        Log.Info("summarizer paused after setup problem");
                    }
                }
                else
                {
                    db.Finish(jobId, JobStatus.Done);
                    Log.Info($"job {jobId} done");
                    if (job.Kind is JobKind.Summarize or JobKind.Revise) settings.ClearSummarizerPause();
                }
                CountUsage(job, failure);
            }
            catch (Exception e) { Log.Error($"finish failed: {e.GetType().Name}"); }
            // 壓縮、逐字稿校正是背景的輔助工作，不發通知；有草稿時正式版不再發（M§21.3）
            if (!job.Kind.IsAuxiliary() && (notify || failure is not null)) Finished?.Invoke(job, failure);
        }
        Changed?.Invoke();
    }

    static string Message(Exception e) => e is OperationCanceledException ? L.T("已取消") : e.Message;

    /// <summary>匿名使用統計（M§30.1）：只算真的跑完的工作（取消的不算）；轉錄成功在 Transcribe 裡算（要用到長度）</summary>
    void CountUsage(Job job, Exception? failure)
    {
        if (Usage is not { } usage) return;
        switch (job.Kind, failure is not null)
        {
            case (JobKind.Transcribe, true): usage.Failed(UsageStats.Failure.Transcribe); break;
            case (JobKind.Rediarize, false): usage.Rediarized(); break;
            case (JobKind.Rediarize, true): usage.Failed(UsageStats.Failure.Diarize); break;
            case (JobKind.Summarize or JobKind.Revise, false): usage.Summarized(settings.Provider); break;
            case (JobKind.Summarize or JobKind.Revise, true):
                // 種類、整理工具、是不是草稿另外記（R47、M§34.5）
                var draft = job.Kind == JobKind.Summarize && (Decode<SummarizePayload>(job.Payload)?.Draft ?? false);
                usage.SummarizeFailed(failure!, settings.Provider, draft);
                break;
        }
    }

    /// <summary>payload 的 JSON（跟 Mac 的 Codable 相同的欄位）。不用反射的序列化，修剪過的 app 也能用</summary>
    static T? Decode<T>(string? json) where T : class
    {
        try
        {
            using var doc = JsonDocument.Parse(json ?? "{}");
            var root = doc.RootElement;
            if (typeof(T) == typeof(TranscribePayload))
                return new TranscribePayload(root.TryGetProperty("until", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetDouble() : null,
                    root.TryGetProperty("remoteId", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null) as T;
            if (typeof(T) == typeof(SummarizePayload))
                return new SummarizePayload(root.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True,
                    root.TryGetProperty("people", out var p) && p.ValueKind == JsonValueKind.True,
                    root.TryGetProperty("review", out var rv) && rv.ValueKind == JsonValueKind.True) as T;
            if (typeof(T) == typeof(RevisePayload))
                return new RevisePayload(root.GetProperty("answers").EnumerateArray().Select(a => new ReviewAnswerJson(
                    a.GetProperty("time").GetString() ?? "", a.GetProperty("question").GetString() ?? "", a.GetProperty("answer").GetString() ?? "")).ToList()) as T;
            return null;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    /// <summary>people 是 false 時不寫：草稿的 payload 跟以前一字不差（同 Mac 的 encode，M§66）</summary>
    public static string Encode(SummarizePayload payload)
    {
        var json = new System.Text.Json.Nodes.JsonObject { ["draft"] = payload.Draft };
        if (payload.People) json["people"] = true;
        if (payload.Review) json["review"] = true;
        return json.ToJsonString();
    }

    public static string Encode(TranscribePayload payload)
    {
        var o = new System.Text.Json.Nodes.JsonObject { ["until"] = payload.Until };
        if (payload.RemoteId is not null) o["remoteId"] = payload.RemoteId;
        return o.ToJsonString();
    }

    /// <summary>最後一次完成的轉錄只做到第幾秒（M§35.3）；轉錄了全部是 null</summary>
    public static double? TranscribedUntil(string? payload) => Decode<TranscribePayload>(payload)?.Until;

    public static string Encode(RevisePayload payload) => new System.Text.Json.Nodes.JsonObject
    {
        ["answers"] = new System.Text.Json.Nodes.JsonArray(payload.Answers.Select(a => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
        {
            ["time"] = a.Time, ["question"] = a.Question, ["answer"] = a.Answer,
        }).ToArray()),
    }.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    bool IsStillRunning(Job job) => db.JobStatusOf(job.Id!.Value) == JobStatus.Running;

    // MARK: 轉錄（M§6）

    /// <summary>企業伺服器（M§36.3）；沒有就是 null</summary>
    public EnterpriseServer? Enterprise { get; init; }

    /// <summary>自己的詞＋公司的詞（R53.2）：轉錄、猜說話者、校正、整理共用</summary>
    List<string> GlossaryTerms()
    {
        var own = db.Glossary().Select(t => t.Term);
        return Enterprise is { } enterprise ? enterprise.Glossary.Merged(own) : [.. own];
    }

    /// <summary>正式轉錄用企業伺服器（R50.1）：選了而且登入過（登入失效也算，工作停著等重新登入）</summary>
    public bool UsesEnterprise => Enterprise is { HasAccount: true } && settings.TranscriptionEngine == "server";

    /// <summary>企業伺服器連不上連續多久就自動改在這台電腦轉錄（R93）；DEBUG 的 --set serverAutoLocalSeconds 改短</summary>
    public TimeSpan ServerAutoLocalAfter { get; init; } = ServerTranscriber.DefaultAutoLocalAfter;

    /// <summary>使用者按了「改在這台電腦轉錄」的工作（R50.5）：只在記憶體，app 重開後照設定</summary>
    readonly HashSet<long> localJobs = [];

    /// <summary>這場會議正在等企業伺服器的轉錄改在這台電腦轉（R50.5）</summary>
    public void TranscribeLocally(long meetingId)
    {
        lock (gate)
        {
            if (!running.TryGetValue(JobLane.Transcribe, out var state) || state.Job.MeetingId != meetingId || state.Job.Id is not long id) return;
            localJobs.Add(id);
        }
        Log.Info("transcription switched to this computer");
    }

    bool IsLocal(Job job) { lock (gate) return job.Id is long id && localJobs.Contains(id); }

    void Transcribe(Job job, RunningJob state, CancellationToken cancel)
    {
        if (db.Meeting(job.MeetingId) is not { } meeting) return;
        // 麥克風的「建議收錄」（R119.3、M§93.2）：錄音停止後的第一步，讀原始的錄音；失敗不影響轉錄
        if (meeting.Channels == "both" && meeting.MicFloorDb is null)
        {
            try
            {
                if (MicDetection.Run(job.MeetingId, db, paths, decoder, cancel)) MicRegionsDetected?.Invoke(job.MeetingId);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Log.Error($"mic regions failed: {e.Message}");
            }
        }
        var startedAt = DateTime.UtcNow;
        var payload = Decode<TranscribePayload>(job.Payload) ?? new TranscribePayload(null);
        var options = TranscriptionOptions.Current(settings, models, meeting.MeetingLanguage, GlossaryTerms(),
            meeting.Channels, meeting.MinSpeakers);
        // 沒人說話太久而自動停止的錄音：只轉錄到最後說話後 15 秒，錄音檔不動（M§35.3、R48.3）
        var until = payload.Until;
        var trimmed = until is double u ? AudioTrim.Prefix(paths.AudioPath(meeting), u, paths.Tmp) : null;
        if (trimmed is not null) Log.Info($"transcribing only the first {(int)until!.Value}s (idle auto-stop)");
        TranscriptionResult? ran = null;
        var transcribedWith = options.TranscribedWith;
        var onServer = false;
        try
        {
            if (UsesEnterprise && !IsLocal(job))
            {
                try
                {
                    if (TranscribeOnServer(job, state, trimmed ?? paths.AudioPath(meeting), options, payload, cancel) is not { } remote) return;
                    (ran, transcribedWith, onServer) = (remote.Transcription, remote.TranscribedWith, true);
                }
                catch (ServerTranscriber.SwitchToLocalException switched)
                {
                    // 改在這台電腦轉（R50.5）：用本機的引擎，模型還沒下載好就先下載、先用快速辨識（同 D35）；
                    // 連不上太久自動改的（R93）發通知
                    state.Server = null;
                    if (switched.Automatic) ServerFallback?.Invoke(job.MeetingId);
                    if (!settings.UseFastTranscriber) models.AutoStart(ModelStore.Model.Breeze);
                    models.AutoStart(ModelStore.Model.Diarization);
                }
            }
            if (ran is null)
            {
                if (options.Engine == TranscriptionOptions.EngineKind.Fast && !settings.UseFastTranscriber)
                    Log.Info("transcription model not ready, using the fast recognizer");
                var pipeline = new TranscriptionPipeline(options, decoder, engines, options.Model ?? models.PathOf(ModelStore.Transcription(options.Language)))
                {
                    Stage = s => state.Stage = s,
                    Progress = p => state.Progress = p,
                    ShouldContinue = () => IsStillRunning(job),
                };
                ran = pipeline.Run(trimmed ?? paths.AudioPath(meeting), cancel);
            }
        }
        finally
        {
            if (trimmed is not null) try { Directory.Delete(Path.GetDirectoryName(trimmed)!, true); } catch (IOException) { }
            if (job.Id is long jobId) lock (gate) localJobs.Remove(jobId);
        }
        if (ran is not { } result) return;
        // 只轉錄前段時，會議長度維持整個錄音（播放、資訊列用）
        var savedDuration = trimmed is not null ? Math.Max(meeting.DurationSeconds ?? 0, result.Duration) : result.Duration;
        // 中文會議用簡體時，存檔前才轉（M§47.2、D154）
        var language = meeting.MeetingLanguage;
        var segments = result.Segments.Select((s, i) => new Segment
        {
            MeetingId = job.MeetingId, Seq = i, Start = s.Start, End = s.End, Text = settings.Script(s.Text, language), LowConfidence = s.LowConfidence,
            Speaker = result.Speakers[i], Kind = "final", Channel = result.Channels[i],
        }).ToList();
        db.SaveTranscript(job.MeetingId, segments, (int)Math.Round(savedDuration, MidpointRounding.AwayFromZero),
            result.Speakers.Max() ?? 0, result.Voices, transcribedWith);
        // 匿名使用統計（M§30.1）：語音長度、實際花的時間、引擎；說話者辨識跟 pipeline 的條件一樣
        Usage?.Transcribed(result.Duration, (DateTime.UtcNow - startedAt).TotalSeconds, onServer ? "server" : transcribedWith, meeting.MeetingLanguage,
            meeting.Source == "import", options.Diarize && result.Duration > 5);
        // 說話者辨識的碰撞率（M§24.3）：可能把人併在一起時，逐字稿分頁會提示
        db.SetSpeakerCheck(job.MeetingId, result.DiarizationQuality?.MergeRisk);
        // 聲紋比對（M§21.4）：在整理之前，會議紀錄裡就會是名字
        if (settings.Voiceprints)
        {
            var matched = db.MatchVoiceprints(job.MeetingId);
            if (matched > 0) Log.Info($"voiceprints matched {matched} speakers");
        }
        // 說話者對到人物之後：照資料夾的條件放（M§72；人物的條件要先對到人）
        var filed = db.AutoFile(job.MeetingId);
        if (filed is not null) Organized?.Invoke(job.MeetingId, filed);
        if (settings.CompressAudio && Importer.Extension(meeting.AudioFile) == "wav") db.Enqueue(job.MeetingId, JobKind.Compress);
        // 校正在輔助線、整理在整理線，兩個同時開始（M§21.2、R20.1）。
        // 整理工具還沒準備好（沒登入、沒有 key…）就都不排，不會「整理失敗」（R45.4、M§34.2）
        var summarizerReady = settings.SummarizerReady;
        if (!summarizerReady) Log.Info("summarizer not ready, skip auto jobs");
        if (settings.CorrectTranscript && summarizerReady) db.Enqueue(job.MeetingId, JobKind.Correct);
        if (settings.AutoSummarize && summarizerReady)
        {
            db.Enqueue(job.MeetingId, JobKind.Summarize);
            // 還有預設名稱的說話者就猜名字（M§21.5）：在輔助線、校正後面，不擋整理
            if (db.Speakers(job.MeetingId).Any(s => s.NameSource == "default")) db.Enqueue(job.MeetingId, JobKind.NameSpeakers);
        }
        Wake();
    }

    /// <summary>
    /// 交給企業伺服器（M§36.3、design-windows §12.11）：解碼成 16 kHz 單聲道、寫成 16-bit WAV 上傳；說話者也在伺服器辨識（聲紋是同一個模型）。
    /// 回傳 null 表示被取消
    /// </summary>
    ServerTranscriber.Result? TranscribeOnServer(Job job, RunningJob state, string audio, TranscriptionOptions options, TranscribePayload payload,
                                                 CancellationToken cancel)
    {
        state.Stage = L.T("讀取錄音");
        var folder = Path.Combine(paths.Tmp, "enterprise-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var wav = Path.Combine(folder, "upload.wav");
            var (bytes, sha256) = ServerTranscriber.WriteWav(decoder.Decode(audio, cancel), wav);
            if (!IsStillRunning(job)) return null;
            var transcriber = new ServerTranscriber(Enterprise!)
            {
                Stage = s => state.Stage = s,
                Progress = p => state.Progress = p,
                ShouldContinue = () => IsStillRunning(job),
                SwitchToLocal = () => IsLocal(job),
                AutoLocalAfter = ServerAutoLocalAfter,
                OnServer = note => { state.Server = note; Changed?.Invoke(); },
                OnRemoteId = remoteId => { if (job.Id is long id) db.SetJobPayload(id, Encode(payload with { RemoteId = remoteId })); },
            };
            // 說話者在伺服器辨識：不看這台電腦有沒有說話者辨識的模型
            return transcriber.Transcribe(wav, bytes, sha256, options with { Diarize = settings.Diarize }, payload.RemoteId, cancel).GetAwaiter().GetResult();
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
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
            // 短句再跟每位說話者比一次（R34）；使用者改過的句子下面另外處理，不用查
            var locked = Enumerable.Range(0, segments.Count).Where(i => segments[i].SpeakerManual).ToHashSet();
            speakers = TranscriptionPipeline.Recheck(engines, transcribed, speakers, voices, samples, locked, cancel);
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
            if (settings.SummarizerReady && db.Speakers(job.MeetingId).Any(s => s.NameSource == "default"))
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
                GlossaryTerms(), unnamed,
                idx => names.TryGetValue(idx, out var n) ? n : language.DefaultSpeaker(idx),
                segments.Select(s => (s.Start, s.Speaker, s.Text)).ToList());
            var json = await llm.GenerateJson(provider, settings.Model, SpeakerNaming.System(language), SpeakerNaming.Prompt(context),
                SpeakerNaming.Format, cancel);
            var suggestions = SpeakerNaming.Validate(ParseSuggestions(json, provider), context);
            if (!IsStillRunning(job)) return;
            // 中文會議用簡體時，原因、名字轉成簡體（M§47.2）
            suggestions = suggestions.Select(s => s with { Name = settings.Script(s.Name, language), Reason = settings.Script(s.Reason, language) }).ToList();
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
            string? SpeakerName(int? idx) => idx is int i ? names.TryGetValue(i, out var n) ? n : language.DefaultSpeaker(i) : null;
            var context = new TranscriptCorrection.Context(language, meeting.Title, meeting.ParticipantList,
                names.Values.Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList(), GlossaryTerms(),
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
                // 中文會議用簡體時，建議和原因轉成簡體（原文照逐字稿，已經對過，M§47.2）
                MeetingId = job.MeetingId, SegmentId = segments[s.Segment].Id!.Value, Original = s.Original,
                Suggestion = settings.Script(s.Suggestion, language), Reason = settings.Script(s.Reason, language),
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

    // MARK: 精簡逐字稿（R96、M§70）

    /// <summary>
    /// 每一句都存（同 Mac）：整理工具改過的用改過的，沒改的存原文（有沒有產生過看有沒有這份）；中文會議用簡體時轉成簡體。
    /// 失敗照一般的工作記下原因（逐字稿的「精簡」寫「上次產生精簡版失敗：…」）；輔助工作，不發通知
    /// </summary>
    async Task Clean(Job job, RunningJob state, CancellationToken cancel)
    {
        if (db.Meeting(job.MeetingId) is not { } meeting) return;
        var provider = settings.Provider;
        if (provider == LLMProvider.None) throw LLMException.NotConfigured(L.T("還沒設定整理工具，請到設定選擇 Claude、Codex 或 API"));
        var segments = db.Segments(job.MeetingId);
        if (segments.Count == 0) return;
        state.Provider = provider;
        state.Stage = L.T("產生精簡版");
        var language = meeting.MeetingLanguage;
        var names = db.Speakers(job.MeetingId).ToDictionary(s => s.Idx, s => s.Name);
        var context = new TranscriptCleanup.Context(language, segments.Select(s =>
            (s.Text, s.Speaker is int i ? names.TryGetValue(i, out var n) ? n : language.DefaultSpeaker(i) : null)).ToList());
        var changed = new Dictionary<int, string>();
        for (var start = 0; start < segments.Count; start += TranscriptCleanup.ChunkSize)
        {
            var end = Math.Min(start + TranscriptCleanup.ChunkSize, segments.Count);
            var json = await llm.GenerateJson(provider, settings.Model, TranscriptCleanup.System(language),
                TranscriptCleanup.Prompt(context, start, end), TranscriptCleanup.Format, cancel);
            foreach (var line in TranscriptCleanup.Validate(ParseCleanLines(json, provider), context, start, end)) changed[line.Segment] = line.Text;
            state.Progress = (double)end / segments.Count;
        }
        if (!IsStillRunning(job)) return;
        var lines = new Dictionary<long, string>();
        for (var i = 0; i < segments.Count; i++)
            if (segments[i].Id is long id) lines[id] = changed.TryGetValue(i, out var text) ? settings.Script(text, language) : segments[i].Text;
        db.SaveCleanLines(job.MeetingId, lines);
    }

    static List<TranscriptCleanup.Line> ParseCleanLines(string json, LLMProvider provider)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("lines").EnumerateArray().Select(e => new TranscriptCleanup.Line(
                e.GetProperty("segment").GetInt32(), e.GetProperty("text").GetString() ?? "")).ToList();
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

    /// <summary>回傳要不要發通知：草稿沒做（正式逐字稿已經好了）、正式版取代了草稿、只補上每個人的重點時不發</summary>
    async Task<bool> Summarize(Job job, RunningJob state, List<ReviewAnswer>? answers, bool draft, CancellationToken cancel, bool peopleOnly = false,
                               bool review = false)
    {
        if (db.Meeting(job.MeetingId) is not { } meeting) return false;
        // 草稿（M§21.3）：正式逐字稿已經好了就不做（短的會議轉錄比較快）
        if (draft && db.Segments(job.MeetingId).Count > 0) return false;
        var provider = settings.Provider;
        if (provider == LLMProvider.None)
            throw LLMException.NotConfigured(settings.NeedsProviderSetup ? L.T("還沒設定整理工具，請到設定選擇 Claude、Codex 或 API")
                : L.T("整理工具設定為「不整理」，請到設定改成 Claude、Codex 或 API"));
        state.Provider = provider;
        state.Stage = draft ? L.T("整理草稿") : review ? L.T("找不確定的地方") : answers is null ? L.T("整理") : L.T("校稿");
        var segments = db.Segments(job.MeetingId, draft ? "live" : "final");
        if (segments.Count == 0 && meeting.UserNotes.Length == 0) throw LLMException.Failed(L.T("還沒有逐字稿，請先完成轉錄"));
        var templates = db.Templates();
        var template = templates.FirstOrDefault(t => t.Id == meeting.TemplateId) ?? templates.FirstOrDefault(t => t.Id == "general");
        var definition = template?.DefinitionFor(meeting.MeetingLanguage) ?? TemplateDefinition.Decode("{}");
        // 只補上每個人的重點（M§66）：範本只要 people（說明、額外指示照舊，沒有自訂段落），整理完只換掉 people，其他段落不動
        if (peopleOnly)
        {
            if (meeting.DecodedNotes is null) return false;
            definition = new TemplateDefinition(definition.Description, ["people"], [], definition.Instruction);
        }
        // 再找不確定的地方（M§88）：範本不要任何段落，只剩待確認
        if (review)
        {
            if (meeting.DecodedNotes is null) return false;
            definition = new TemplateDefinition(definition.Description, [], [], definition.Instruction);
        }
        var asked = review ? db.AskedReviewQuestions(job.MeetingId) : [];
        var context = new NotesContext(meeting, segments, db.Speakers(job.MeetingId).ToDictionary(s => s.Idx, s => s.Name),
            db.Bookmarks(job.MeetingId), GlossaryTerms(), definition,
            db.Documents(job.MeetingId).Select(DocumentText.From).ToList());
        string prompt;
        if (answers is not null)
        {
            if (meeting.DecodedNotes is not { } current) return false;
            prompt = NotesPrompt.Revise(context, current, answers);
        }
        else if (review && meeting.DecodedNotes is { } shown) prompt = NotesPrompt.MoreReview(context, shown, asked);
        else prompt = NotesPrompt.Summarize(context);
        var notes = await llm.Generate(provider, settings.Model, NotesPrompt.System(meeting.MeetingLanguage, context.Documents is { Count: > 0 }), prompt, cancel);
        if (!IsStillRunning(job)) return false;
        // 中文會議用簡體時，整理完才轉（提示詞照舊，M§47.2、D154）
        if (settings.SimplifiedChinese(meeting.MeetingLanguage)) notes = notes.Simplified();
        if (peopleOnly)
        {
            // 整理的期間紀錄可能被改過：用現在的紀錄，只換掉 people
            if (db.Meeting(job.MeetingId)?.DecodedNotes is not { } current) return false;
            db.SaveNotes(job.MeetingId, current with { People = notes.People }, null, L.T("補上每個人的重點"), NotesSource.Edit);
            return false;
        }
        if (review)
        {
            // 去掉空的、問過的（同一題只留一次），接在現在的紀錄後面；其他段落不動
            var seen = asked.Select(MeetingNotes.QuestionKey).ToHashSet();
            var found = notes.ReviewItems.Where(item => MeetingNotes.QuestionKey(item.Question) is { Length: > 0 } key && seen.Add(key)).ToList();
            if (found.Count > 0 && db.Meeting(job.MeetingId)?.DecodedNotes is { } latest)
                db.SaveNotes(job.MeetingId, latest with { ReviewItems = [.. latest.ReviewItems, .. found] }, null, L.T("再找不確定的地方"), NotesSource.Edit);
            MoreReviewFound?.Invoke(job.MeetingId, found.Count);
            return false;
        }
        if (draft)
        {
            // 整理草稿的期間正式逐字稿好了、或已經有會議紀錄：丟掉草稿
            if (db.Segments(job.MeetingId).Count > 0 || db.Meeting(job.MeetingId)?.Notes is not null) return false;
            db.SaveNotes(job.MeetingId, notes, provider.Code(), L.T("草稿"), NotesSource.Draft, template?.Id);
            return true;
        }
        var hadDraft = db.Meeting(job.MeetingId)?.NotesDraft ?? 0;
        var replacedEdited = db.SaveNotes(job.MeetingId, notes, provider.Code(), answers is null ? L.T("重新整理") : L.T("校稿"),
            answers is null ? NotesSource.Generated : NotesSource.Revised, template?.Id);
        if (replacedEdited) EditedDraftReplaced?.Invoke(job.MeetingId);
        // 整理完有標題了，照資料夾的條件放（M§72）
        if (db.AutoFile(job.MeetingId) is { } folder) Organized?.Invoke(job.MeetingId, folder);
        return answers is not null || hadDraft == 0;
    }
}
