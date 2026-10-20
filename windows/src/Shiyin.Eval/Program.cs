using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shiyin.Audio;
using Shiyin.Model;
using Shiyin.Speakers;
using Shiyin.Speech;
using Shiyin.Transcribe;
using Shiyin.Whisper;

// shiyin-eval transcribe-eval <錄音.wav> <設定.json> <輸出.json>   跟 Mac 的 shiyinctl transcribe-eval 一樣（M§17.1）
// shiyin-eval detect-eval <錄音.wav> <開始秒數> <輸出.json>          跟 Mac 的 shiyinctl detect-eval 一樣（M§18.8）
// 模型在 SHIYIN_MODELS（預設是 Mac 拾音的模型資料夾）；原生的 shiyin_whisper 在 SHIYIN_NATIVE_DIR

var models = Environment.GetEnvironmentVariable("SHIYIN_MODELS")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/Application Support/Shiyin/models");

switch (args.FirstOrDefault())
{
    case "transcribe-eval": return Transcribe(args[1], args[2], args[3]);
    case "detect-eval": return Detect(args[1], double.Parse(args[2]), args[3]);
    // diarize-eval <錄音.wav> <逐字稿.json（transcribe-eval 的輸出）> <設定.json> <輸出.json>：只重跑說話者辨識，標到逐字稿上（T-W4）
    case "diarize-eval": return Diarize(args[1], args[2], args[3], args[4]);
    // fast-eval <錄音.wav> <設定.json> <輸出.json>：快速辨識（SenseVoice）整個檔案（T-W5、R27 R4.1）
    case "fast-eval": return Fast(args[1], args[2], args[3]);
    // live-eval <錄音.wav> <設定.json> <輸出.json>：照錄音的速度餵即時逐字稿，記下暫定、定稿的延遲（T-W5）
    case "live-eval": return Live(args[1], args[2], args[3]);
    case "info":
        Console.WriteLine(WhisperEngine.SystemInfo);
        return 0;
    default:
        Console.Error.WriteLine("用法：shiyin-eval transcribe-eval|detect-eval|info …");
        return 2;
}

int Transcribe(string audio, string optionsPath, string output)
{
    var watch = Stopwatch.StartNew();
    var options = JsonNode.Parse(File.ReadAllText(optionsPath))!.AsObject();
    var language = MeetingLanguages.Parse(options["language"]?.GetValue<string>()) ?? MeetingLanguage.Zh;
    var glossary = options["glossary"]?.AsArray().Select(x => x!.GetValue<string>()).ToList() ?? [];
    var chunkSeconds = options["chunkSeconds"]?.GetValue<double>() ?? 600;
    var model = options["model"]?.GetValue<string>()
        ?? Path.Combine(models, language == MeetingLanguage.Ja ? "ggml-large-v3-turbo-q5_0.bin" : "breeze-asr-25-q8_0.bin");
    var result = new JsonObject { ["audio"] = audio, ["options"] = options.DeepClone(), ["engine"] = "windows-whisper", ["system"] = WhisperEngine.SystemInfo };
    try
    {
        using var stream = new WavStream(audio);
        var prompt = WhisperRules.Prompt(glossary, language);
        var (segments, seams, count) = WhisperEngine.TranscribeFile(stream, chunkSeconds, model, prompt, language.WhisperCode(), new WhisperControl());
        result["duration"] = count / WhisperRules.SampleRate;
        result["seams"] = new JsonArray(seams.Select(s => (JsonNode)(s / WhisperRules.SampleRate)).ToArray());
        result["segments"] = new JsonArray(segments.Select(s => (JsonNode)new JsonObject
        {
            ["start"] = s.Start, ["end"] = s.End, ["text"] = s.Text, ["speaker"] = null, ["channel"] = null, ["lowConfidence"] = s.LowConfidence,
        }).ToArray());
    }
    catch (Exception e)
    {
        result["error"] = e.Message;
    }
    result["elapsed"] = watch.Elapsed.TotalSeconds;
    File.WriteAllText(output + ".tmp", result.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    File.Move(output + ".tmp", output, overwrite: true);
    return result["error"] is null ? 0 : 1;
}

int Detect(string audio, double start, string output)
{
    var samples = WavStream.ReadAll(audio);
    var from = Math.Min(samples.Length, (int)(start * WhisperRules.SampleRate));
    float[]? ready = null;
    var collector = new LanguageCollector(r => ready = r);
    var position = from;
    while (ready is null && position < samples.Length)
    {
        var end = Math.Min(samples.Length, position + 1600);
        collector.Feed(samples.AsSpan(position, end - position));
        position = end;
    }
    var result = new JsonObject { ["audio"] = audio, ["start"] = start, ["ready"] = ready is not null };
    if (ready is not null)
    {
        // 欄位同 Mac 的 shiyinctl detect-eval（used：從開始秒數起錄了多久才湊滿）
        var probabilities = WhisperEngine.DetectLanguage(ready, Path.Combine(models, "ggml-base-q5_1.bin"));
        result["used"] = (position - from) / WhisperRules.SampleRate;
        result["probabilities"] = new JsonObject(probabilities.Select(p => KeyValuePair.Create(p.Key.Code(), (JsonNode?)p.Value)));
        result["detected"] = probabilities.MaxBy(p => p.Value).Key.Code();
        result["voicedSeconds"] = ready.Length / WhisperRules.SampleRate;
    }
    File.WriteAllText(output, result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

int Diarize(string audio, string transcriptPath, string optionsPath, string output)
{
    var watch = Stopwatch.StartNew();
    var options = JsonNode.Parse(File.ReadAllText(optionsPath))!.AsObject();
    var root = Environment.GetEnvironmentVariable("SHIYIN_DIARIZATION_MODELS") ?? models;
    // 逐字稿給 "-"：只看分出幾個人、每人的聲紋（AMI 9 場的人數、聲紋比對）
    var segments = transcriptPath == "-" ? [] : JsonNode.Parse(File.ReadAllText(transcriptPath))!["segments"]!.AsArray()
        .Select(s => new TranscribedSegment(s!["start"]!.GetValue<double>(), s["end"]!.GetValue<double>(),
            s["text"]!.GetValue<string>(), s["lowConfidence"]?.GetValue<bool>() ?? false)).ToList();
    var samples = WavStream.ReadAll(audio);
    var embedding = options["embedding"]?.GetValue<string>() ?? "3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx";
    var diarizerOptions = new Diarizer.Options(
        Path.Combine(root, options["segmentation"]?.GetValue<string>() ?? "sherpa-onnx-pyannote-segmentation-3-0/model.onnx"),
        Path.Combine(root, embedding))
    {
        SegmentationEmbeddingModel = options["segmentationEmbedding"]?.GetValue<string>() is { } se ? Path.Combine(root, se) : null,
        SegmentClusters = options["segmentClusters"]?.GetValue<int>() ?? 30,
        NumSpeakers = options["numSpeakers"]?.GetValue<int>(),
        Threads = options["threads"]?.GetValue<int>() ?? Math.Max(1, Math.Min(4, Environment.ProcessorCount - 2)),
        WindowShiftRatio = options["windowShift"]?.GetValue<float>() ?? 0.5f,
        PieceSeconds = options["pieceSeconds"]?.GetValue<double>() ?? 3,
        Clustering = new SpeakerClustering.Options
        {
            Threshold = options["threshold"]?.GetValue<double>() ?? 0.25,
            MinClusterSeconds = options["minClusterSeconds"]?.GetValue<double>() ?? 30,
            Center = options["center"]?.GetValue<bool>() ?? true,
        },
    };
    using var diarizer = new Diarizer(diarizerOptions);
    if (options["pieces"]?.GetValue<bool>() == true) return DumpPieces(diarizer, samples, output, watch);
    var result = diarizer.Diarize(samples, options["minSpeakers"]?.GetValue<int>());
    var (speakers, numbering) = SpeakerRules.AssignNumbering(result.Turns, segments);
    if (segments.Count == 0) numbering = result.Voiceprints.Keys.ToDictionary(k => k, k => k);
    var voices = new JsonObject();
    foreach (var (raw, number) in numbering)
        if (result.Voiceprints.TryGetValue(raw, out var v))
            voices[number.ToString()] = new JsonObject { ["seconds"] = result.SpeechSeconds.GetValueOrDefault(raw), ["embedding"] = new JsonArray(v.Select(x => (JsonNode)x).ToArray()) };
    var outSegments = new JsonArray();
    for (var i = 0; i < segments.Count; i++)
    {
        var s = segments[i];
        outSegments.Add(new JsonObject { ["start"] = s.Start, ["end"] = s.End, ["text"] = s.Text, ["speaker"] = speakers[i], ["channel"] = null, ["lowConfidence"] = s.LowConfidence });
    }
    var q = result.Quality;
    var json = new JsonObject
    {
        ["audio"] = audio, ["options"] = options.DeepClone(), ["duration"] = samples.Length / WhisperRules.SampleRate, ["segments"] = outSegments,
        ["clusters"] = result.Turns.Select(t => t.Speaker).Distinct().Count(), ["dimension"] = diarizer.EmbeddingDimension,
        ["quality"] = q is null ? null : new JsonObject { ["cohesion"] = q.Cohesion, ["chunks"] = q.Chunks, ["clusters"] = q.Clusters, ["mergeRisk"] = q.MergeRisk },
        ["voices"] = voices,
        ["turns"] = segments.Count > 0 ? null : new JsonArray(result.Turns.Select(t => (JsonNode)new JsonArray(t.Start, t.End, t.Speaker)).ToArray()),
        ["chunks"] = options["dump"]?.GetValue<bool>() == true ? new JsonArray(result.Chunks.Select(c => (JsonNode)new JsonObject
        {
            ["speakerId"] = c.SpeakerId, ["chunkIndex"] = c.ChunkIndex, ["embedding"] = new JsonArray(c.Embedding.Select(x => (JsonNode)x).ToArray()),
        }).ToArray()) : null,
        ["elapsed"] = watch.Elapsed.TotalSeconds,
    };
    File.WriteAllText(output, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    return 0;
}

// 離線試分群用（T-W4）：sherpa-onnx 切的段落，以及每個小段的聲紋（Diarizer.Segment 的結果）
int DumpPieces(Diarizer diarizer, float[] samples, string output, Stopwatch watch)
{
    var (turns, pieces) = diarizer.Segment(samples);
    var json = new JsonObject
    {
        ["turns"] = new JsonArray(turns.Select(t => (JsonNode)new JsonArray(t.Start, t.End, t.Speaker)).ToArray()),
        ["pieces"] = new JsonArray(pieces.Select(p => (JsonNode)new JsonObject
        {
            ["turn"] = p.Turn, ["start"] = p.Start, ["end"] = p.End, ["embedding"] = new JsonArray(p.Embedding.Select(x => (JsonNode)x).ToArray()),
        }).ToArray()),
        ["elapsed"] = watch.Elapsed.TotalSeconds,
    };
    File.WriteAllText(output, json.ToJsonString());
    return 0;
}

int Fast(string audio, string optionsPath, string output)
{
    var watch = Stopwatch.StartNew();
    var options = JsonNode.Parse(File.ReadAllText(optionsPath))!.AsObject();
    var language = MeetingLanguages.Parse(options["language"]?.GetValue<string>()) ?? MeetingLanguage.Zh;
    var root = Environment.GetEnvironmentVariable("SHIYIN_SPEECH_MODELS") ?? models;
    using var stream = new WavStream(audio);
    var vad = new VadOptions { Threshold = options["vadThreshold"]?.GetValue<float>() ?? new VadOptions().Threshold, GainDb = options["gainDb"]?.GetValue<float>() };
    var segments = FastTranscriber.Transcribe(stream, SenseVoiceModels.In(root), language, options: vad);
    var json = new JsonObject
    {
        ["audio"] = audio, ["options"] = options.DeepClone(), ["engine"] = "sensevoice",
        ["segments"] = new JsonArray(segments.Select(s => (JsonNode)new JsonObject { ["start"] = s.Start, ["end"] = s.End, ["text"] = s.Text, ["speaker"] = null, ["lowConfidence"] = false }).ToArray()),
        ["elapsed"] = watch.Elapsed.TotalSeconds,
    };
    File.WriteAllText(output, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    return 0;
}

int Live(string audio, string optionsPath, string output)
{
    var options = JsonNode.Parse(File.ReadAllText(optionsPath))!.AsObject();
    var language = MeetingLanguages.Parse(options["language"]?.GetValue<string>()) ?? MeetingLanguage.Zh;
    var speed = options["speed"]?.GetValue<double>() ?? 1;
    var seconds = options["seconds"]?.GetValue<double>() ?? double.MaxValue;
    var root = Environment.GetEnvironmentVariable("SHIYIN_SPEECH_MODELS") ?? models;
    var samples = WavStream.ReadAll(audio);
    var count = (int)Math.Min(samples.Length, seconds * WhisperRules.SampleRate);
    var finals = new JsonArray();
    var volatileDelays = new List<double>();
    var finalDelays = new List<double>();
    var clock = Stopwatch.StartNew();
    double Fed() => clock.Elapsed.TotalSeconds * speed;   // 目前送到錄音的第幾秒
    var volatileCount = 0;
    var vad = new VadOptions { Threshold = options["vadThreshold"]?.GetValue<float>() ?? new VadOptions().Threshold, GainDb = options["gainDb"]?.GetValue<float>() };
    using (var live = new LiveTranscriber(SenseVoiceModels.In(root), language, vad))
    {
        live.Volatile += text => { if (text.Length > 0) volatileCount++; };
        live.Final += (start, end, text) =>
        {
            finalDelays.Add(Fed() - end);
            lock (finals) finals.Add(new JsonObject { ["start"] = start, ["end"] = end, ["text"] = text, ["speaker"] = null, ["lowConfidence"] = false });
        };
        var chunk = 1600;   // 0.1 秒一次，跟錄音一樣
        for (var i = 0; i < count; i += chunk)
        {
            var n = Math.Min(chunk, count - i);
            live.Feed(samples.AsSpan(i, n));
            var due = (i + n) / WhisperRules.SampleRate / speed;
            var wait = due - clock.Elapsed.TotalSeconds;
            if (wait > 0) Thread.Sleep(TimeSpan.FromSeconds(wait));
        }
        live.Finish();
    }
    finalDelays.Sort();
    var json = new JsonObject
    {
        ["audio"] = audio, ["options"] = options.DeepClone(), ["engine"] = "sensevoice-live", ["segments"] = finals,
        ["volatileUpdates"] = volatileCount,
        ["finalDelayMedian"] = finalDelays.Count > 0 ? finalDelays[finalDelays.Count / 2] : null,
        ["finalDelayP90"] = finalDelays.Count > 0 ? finalDelays[(int)(finalDelays.Count * 0.9)] : null,
        ["elapsed"] = clock.Elapsed.TotalSeconds,
    };
    File.WriteAllText(output, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    return 0;
}
