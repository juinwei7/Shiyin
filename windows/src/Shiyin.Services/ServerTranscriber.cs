using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Shiyin.Localization;
using Shiyin.Model;
using Shiyin.Transcribe;
using Shiyin.Whisper;

namespace Shiyin.Services;

/// <summary>
/// 用企業伺服器轉錄（M§36.3、R50、design-windows §12.11）：寫成 16-bit WAV → 建工作 → 分塊上傳（可以接著傳）→ 等結果 → 刪掉伺服器上的工作。
/// Windows 的說話者辨識也交給伺服器（同一個模型，聲紋直接用伺服器算的，沒有 NVIDIA 的電腦不用自己跑）。
/// 連不上、伺服器忙、登入失效時不算失敗：一直等、自動再試（R50.5、R49.4），使用者可以按「改在這台電腦轉錄」
/// </summary>
public sealed class ServerTranscriber(EnterpriseServer server)
{
    /// <summary>使用者按了「改在這台電腦轉錄」</summary>
    public sealed class SwitchToLocalException : Exception;

    /// <summary>伺服器轉錄失敗（例如錄音太長）：不是暫時的問題，工作算失敗</summary>
    public sealed class FailedException(string message) : Exception(message);

    public const int ChunkSize = 8 * 1024 * 1024;

    public Action<string> Stage { get; init; } = _ => { };
    /// <summary>0～1：上傳 0～0.1、伺服器轉錄 0.1～1</summary>
    public Action<double> Progress { get; init; } = _ => { };
    public Func<bool> ShouldContinue { get; init; } = () => true;
    public Func<bool> SwitchToLocal { get; init; } = () => false;
    /// <summary>伺服器的狀態（null：伺服器的部分做完了）</summary>
    public Action<ServerNote?> OnServer { get; init; } = _ => { };
    /// <summary>伺服器的工作 id 換了：JobRunner 記進工作的 payload，app 重開時接著做</summary>
    public Action<string?> OnRemoteId { get; init; } = _ => { };
    /// <summary>測試縮短等待</summary>
    public Func<int, TimeSpan> RetryDelay { get; init; } = attempt => TimeSpan.FromSeconds(Math.Min(300, 30 * (1 << Math.Min(attempt - 1, 4))));
    public TimeSpan PollRunning { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan PollQueued { get; init; } = TimeSpan.FromSeconds(5);

    public sealed record Result(TranscriptionResult Transcription, string TranscribedWith);

    // MARK: WAV

    /// <summary>16 kHz、16-bit、單聲道的 WAV（伺服器只收這種，D120）；回傳大小和 SHA-256</summary>
    public static (long Bytes, string Sha256) WriteWav(float[] samples, string path)
    {
        var dataBytes = samples.Length * 2;
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var header = new byte[44];
        void Put(int offset, string text) => Encoding.ASCII.GetBytes(text).CopyTo(header, offset);
        Put(0, "RIFF"); BitConverter.TryWriteBytes(header.AsSpan(4), 36 + dataBytes); Put(8, "WAVE");
        Put(12, "fmt "); BitConverter.TryWriteBytes(header.AsSpan(16), 16); BitConverter.TryWriteBytes(header.AsSpan(20), (short)1);
        BitConverter.TryWriteBytes(header.AsSpan(22), (short)1); BitConverter.TryWriteBytes(header.AsSpan(24), 16000);
        BitConverter.TryWriteBytes(header.AsSpan(28), 32000); BitConverter.TryWriteBytes(header.AsSpan(32), (short)2);
        BitConverter.TryWriteBytes(header.AsSpan(34), (short)16); Put(36, "data"); BitConverter.TryWriteBytes(header.AsSpan(40), dataBytes);
        file.Write(header);
        sha.AppendData(header);
        var block = new byte[160_000 * 2];
        for (var index = 0; index < samples.Length; index += 160_000)
        {
            var end = Math.Min(index + 160_000, samples.Length);
            for (var i = index; i < end; i++)
            {
                var v = Math.Clamp(samples[i], -1f, 1f) * 32767f;
                BitConverter.TryWriteBytes(block.AsSpan((i - index) * 2), (short)MathF.Round(v));
            }
            var length = (end - index) * 2;
            file.Write(block, 0, length);
            sha.AppendData(block, 0, length);
        }
        return (44 + (long)dataBytes, Convert.ToHexStringLower(sha.GetHashAndReset()));
    }

    // MARK: 流程

    /// <summary>回傳 null 表示被取消（伺服器上的工作也刪掉）</summary>
    public async Task<Result?> Transcribe(string wav, long bytes, string sha256, TranscriptionOptions options, string? remoteId, CancellationToken cancel)
    {
        var id = remoteId;
        var waited = 0;
        var rejected = 0;
        OnServer(new ServerNote(""));
        try
        {
            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                if (!ShouldContinue()) { await Delete(id); return null; }
                if (SwitchToLocal()) { await Delete(id); OnRemoteId(null); throw new SwitchToLocalException(); }
                try
                {
                    if (id is null)
                    {
                        id = await Create(bytes, sha256, options, cancel);
                        OnRemoteId(id);
                    }
                    var state = await Status(id, cancel);
                    if (waited > 0) { OnServer(new ServerNote("")); waited = 0; }
                    switch (state.Status)
                    {
                        case "uploading":
                            if (!await Upload(id, wav, bytes, cancel) && ++rejected > 2)
                                throw new FailedException(L.T("錄音一直沒辦法完整傳到企業伺服器"));
                            break;
                        case "queued" or "running":
                            Report(state);
                            await Task.Delay(state.Status == "running" ? PollRunning : PollQueued, cancel);
                            break;
                        case "done":
                            Progress(1);
                            var result = await Fetch(id, cancel);
                            await Delete(id);
                            OnRemoteId(null);
                            return result;
                        case "failed":
                            await Delete(id);
                            OnRemoteId(null);
                            if (state.ErrorKind == "empty") throw new WhisperException(WhisperErrorKind.Empty);
                            throw new FailedException(state.ErrorMessage ?? L.T("企業伺服器轉錄失敗"));
                        default:
                            // 取消、過期（伺服器清掉了）：重新建一個工作，從頭上傳
                            id = null;
                            OnRemoteId(null);
                            break;
                    }
                }
                catch (EnterpriseException e) when (e.ServerKind == "not_found")
                {
                    id = null;
                    OnRemoteId(null);
                }
                catch (EnterpriseException e) when (e.IsTemporary || e.IsSetupProblem)
                {
                    waited++;
                    await WaitForServer(e, waited, cancel);
                }
                catch (EnterpriseException e)
                {
                    await Delete(id);
                    OnRemoteId(null);
                    throw new FailedException(e.Message);
                }
            }
        }
        finally { OnServer(null); }
    }

    /// <summary>等伺服器（R50.5）：30 秒、1 分鐘、2 分鐘…最長 5 分鐘再試；登入失效時等使用者重新登入（每 5 秒看一次）</summary>
    async Task WaitForServer(EnterpriseException failure, int attempt, CancellationToken cancel)
    {
        var setup = failure.IsSetupProblem;
        var delay = setup ? TimeSpan.FromSeconds(5) : RetryDelay(attempt);
        var seconds = (long)delay.TotalSeconds;
        var text = setup ? failure.Message
            : failure.ServerKind == "busy" ? L.F("等企業伺服器：伺服器忙，%lld 秒後再試", seconds)
            : failure.Kind == EnterpriseFailure.Server ? L.F("等企業伺服器：伺服器出錯，%lld 秒後再試", seconds)
            : L.F("等企業伺服器：連不上，%lld 秒後再試", seconds);
        OnServer(new ServerNote(text, Waiting: true));
        Stage(L.T("等企業伺服器"));
        Log.Info($"waiting for enterprise server: {(setup ? "login" : "unreachable")}");
        var until = DateTime.UtcNow + delay;
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(1000, Math.Max(10, (until - DateTime.UtcNow).TotalMilliseconds))), cancel);
            if (!ShouldContinue() || SwitchToLocal()) return;
            // 重新登入了就馬上再試
            if (setup && server.IsSignedIn) return;
        }
    }

    /// <summary>階段的文字會接「中 N%」（「企業伺服器轉錄中 42%」），排隊第幾放在後面的說明</summary>
    void Report(JobState state)
    {
        if (state.Status == "queued")
        {
            Stage(L.T("企業伺服器排隊"));
            OnServer(state.Stage == "waiting_models" ? new ServerNote(L.T("伺服器正在準備模型"))
                : state.QueueAhead is > 0 ? new ServerNote(L.F("前面還有 %lld 場", (long)state.QueueAhead.Value)) : new ServerNote(""));
            Progress(0.1);
        }
        else
        {
            Stage(state.Stage == "diarize" ? L.T("企業伺服器辨識說話者") : L.T("企業伺服器轉錄"));
            OnServer(new ServerNote(""));
            Progress(0.1 + 0.9 * (state.Progress ?? 0));
        }
    }

    // MARK: API（M§36.2）

    sealed record JobState(string Status, int? QueueAhead, string? Stage, double? Progress, string? ErrorKind, string? ErrorMessage);

    /// <summary>建工作時送的 settings（M§36.2；標準答案 server/settings.json）：Windows 的說話者也交給伺服器（D122），分聲道不送</summary>
    public static JsonObject Settings(TranscriptionOptions options)
    {
        var settings = new JsonObject
        {
            ["language"] = options.Language.Code(), ["glossary"] = new JsonArray([.. options.Glossary.Select(t => (JsonNode)t)]),
            ["diarize"] = options.Diarize, ["splitChannels"] = false, ["audioSource"] = options.AudioSource,
        };
        if (options.MinSpeakers is int min) settings["minSpeakers"] = min;
        return settings;
    }

    async Task<string> Create(long bytes, string sha256, TranscriptionOptions options, CancellationToken cancel)
    {
        var body = new JsonObject
        {
            ["settings"] = Settings(options),
            ["audio"] = new JsonObject { ["format"] = "wav", ["sampleRate"] = 16000, ["channels"] = 1, ["bytes"] = bytes, ["sha256"] = sha256 },
        };
        var (status, text, _) = await server.Call(HttpMethod.Post, "api/v1/transcriptions",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), cancel: cancel);
        if (status != 201 || (JsonNode.Parse(text) as JsonObject)?["id"]?.GetValue<string>() is not { } id) throw EnterpriseServer.Failure(status, text);
        Log.Info("enterprise job created");
        return id;
    }

    async Task<JobState> Status(string id, CancellationToken cancel)
    {
        var (status, text, _) = await server.Call(HttpMethod.Get, $"api/v1/transcriptions/{id}", timeout: TimeSpan.FromSeconds(30), cancel: cancel);
        if (status != 200 || JsonNode.Parse(text) is not JsonObject o) throw EnterpriseServer.Failure(status, text);
        var error = o["error"] as JsonObject;
        return new JobState(o["status"]?.GetValue<string>() ?? "", o["queueAhead"]?.GetValue<int>(), o["stage"]?.GetValue<string>(),
            o["progress"]?.GetValue<double>(), error?["kind"]?.GetValue<string>(), error?["message"]?.GetValue<string>());
    }

    /// <summary>從伺服器收到的地方接著傳（HEAD 問位置），一次 8 MB。回傳 false 表示伺服器說檔案不對、要從頭傳</summary>
    async Task<bool> Upload(string id, string wav, long bytes, CancellationToken cancel)
    {
        var (headStatus, headBody, head) = await server.Call(HttpMethod.Head, $"api/v1/transcriptions/{id}/audio", timeout: TimeSpan.FromSeconds(30), cancel: cancel);
        if (headStatus != 200) throw EnterpriseServer.Failure(headStatus == 404 ? 404 : headStatus, headBody.Length > 0 ? headBody : """{"error":{"kind":"not_found"}}""");
        var offset = Offset(head) ?? 0;
        Stage(L.T("上傳錄音"));
        await using var file = File.OpenRead(wav);
        var buffer = new byte[ChunkSize];
        while (offset < bytes)
        {
            if (!ShouldContinue() || SwitchToLocal()) return true;
            file.Seek(offset, SeekOrigin.Begin);
            var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(ChunkSize, bytes - offset)), cancel);
            if (read == 0) break;
            var content = new ByteArrayContent(buffer, 0, read);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/offset+octet-stream");
            var (status, text, headers) = await server.Call(HttpMethod.Patch, $"api/v1/transcriptions/{id}/audio", content,
                new Dictionary<string, string> { ["Upload-Offset"] = offset.ToString() }, TimeSpan.FromMinutes(5), cancel);
            switch (status)
            {
                case 204: offset = Offset(headers) ?? offset + read; break;
                case 409: offset = Offset(headers) ?? 0; break;
                case 400:
                    Log.Error("enterprise upload rejected, restarting");
                    return false;
                default: throw EnterpriseServer.Failure(status, text);
            }
            Progress(0.1 * offset / Math.Max(bytes, 1));
        }
        return true;
    }

    static long? Offset(HttpResponseHeaders headers) =>
        headers.TryGetValues("Upload-Offset", out var values) && long.TryParse(values.FirstOrDefault(), out var v) ? v : null;

    async Task<Result> Fetch(string id, CancellationToken cancel)
    {
        var (status, text, _) = await server.Call(HttpMethod.Get, $"api/v1/transcriptions/{id}/result", timeout: TimeSpan.FromMinutes(2), cancel: cancel);
        if (status != 200 || JsonNode.Parse(text) is not JsonObject o) throw EnterpriseServer.Failure(status, text);
        return Parse(o);
    }

    /// <summary>
    /// 伺服器的結果（M§36.2）換成本機的 TranscriptionResult：段落、說話者、聲紋（Windows 同一個模型）、分不準的分數。
    /// 少了開始、結束、文字的段落略過（同 Mac，標準答案 server/result.json）
    /// </summary>
    public static Result Parse(JsonObject o)
    {
        var segments = new List<TranscribedSegment>();
        var speakers = new List<int?>();
        foreach (var s in (o["segments"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (s["start"] is not JsonValue start || !start.TryGetValue<double>(out var from) || s["end"] is not JsonValue end || !end.TryGetValue<double>(out var to)
                || s["text"] is not JsonValue t || !t.TryGetValue<string>(out var text)) continue;
            segments.Add(new TranscribedSegment(from, to, text, s["lowConfidence"] is JsonValue low && low.TryGetValue<bool>(out var l) && l));
            speakers.Add(s["speaker"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : null);
        }
        if (segments.Count == 0) throw new WhisperException(WhisperErrorKind.Empty);
        var voices = new Dictionary<int, SpeakerVoice>();
        foreach (var (key, value) in o["voices"] as JsonObject ?? [])
            if (int.TryParse(key, out var speaker) && value is JsonObject voice && voice["embedding"] is JsonArray embedding)
                voices[speaker] = new SpeakerVoice([.. embedding.Select(x => x!.GetValue<float>())], voice["seconds"]?.GetValue<double>() ?? 0,
                    voice["source"]?.GetValue<string>() ?? "mix");
        SpeakerRules.Quality? quality = o["diarizationQuality"] is JsonObject q
            ? new SpeakerRules.Quality(q["collisionRate"]?.GetValue<double>() ?? 0, q["multiSpeakerChunks"]?.GetValue<int>() ?? 0,
                q["cohesion"]?.GetValue<double>() ?? 0, q["clusters"]?.GetValue<int>() ?? 0, q["chunks"]?.GetValue<int>() ?? 0)
            : null;
        var seams = (o["seams"] as JsonArray ?? []).Select(x => x!.GetValue<double>()).ToList();
        var transcription = new TranscriptionResult(segments, speakers, segments.Select(_ => (int?)null).ToList(),
            o["duration"]?.GetValue<double>() ?? segments[^1].End, voices, seams, quality);
        return new Result(transcription, o["transcribedWith"]?.GetValue<string>() ?? o["engine"]?.GetValue<string>() ?? "breeze");
    }

    async Task Delete(string? id)
    {
        if (id is null) return;
        try { await server.Call(HttpMethod.Delete, $"api/v1/transcriptions/{id}", timeout: TimeSpan.FromSeconds(15)); }
        catch (Exception e) when (e is EnterpriseException or OperationCanceledException) { }
    }
}
