#if DEBUG
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shiyin.LLM;
using Shiyin.Model;
using Shiyin.Platform;
using Shiyin.Services;

namespace Shiyin.App.Debug;

/// <summary>
/// DEBUG 的控制通道（design-windows §15.3，對應 Mac 的 DebugControl）：named pipe「shiyin-debug」，一行一個指令（JSON 陣列），回一行 JSON。
/// 驗證時用 shiyinctl.exe 送指令，不送鍵盤事件（CLAUDE.md）。只在 DEBUG 建置裡有
/// </summary>
public static class ControlPipe
{
    public const string Name = "shiyin-debug";

    public static void Start(AppHost host, App app)
    {
        var thread = new Thread(() => Loop(host, app)) { IsBackground = true, Name = "debug-control" };
        thread.Start();
        Log.Info("debug control installed");
    }

    static void Loop(AppHost host, App app)
    {
        while (true)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(Name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte);
                pipe.WaitForConnection();
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
                var line = reader.ReadLine();
                if (line is null) continue;
                var args = (JsonNode.Parse(line) as JsonArray)?.Select(x => x?.GetValue<string>() ?? "").ToArray() ?? [];
                var done = new TaskCompletionSource<JsonNode>();
                host.Post(() =>
                {
                    try { done.SetResult(Run(host, app, args)); }
                    catch (Exception e) { done.SetResult(new JsonObject { ["error"] = e.Message }); }
                });
                writer.WriteLine((done.Task.Wait(TimeSpan.FromSeconds(30)) ? done.Task.Result : new JsonObject { ["error"] = "timeout" }).ToJsonString());
            }
            catch (IOException) { }
        }
    }

    static JsonNode Ok => new JsonObject { ["ok"] = true };

    static JsonNode Run(AppHost host, App app, string[] args)
    {
        var main = (MainWindow?)typeof(App).GetField("main", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(app);
        switch (args.FirstOrDefault())
        {
            case "state": return State(host, main);
            case "select" when args.Length > 1 && long.TryParse(args[1], out var id): main?.Reveal(id); return Ok;
            case "tab" when args.Length > 1 && Enum.TryParse<DetailTab>(args[1], true, out var tab): main?.State.SetTab(tab); return Ok;
            case "closewindow" when args.Length > 1:
                if (args[1] == "main") main?.AppWindow.Hide();
                return Ok;
            case "open" when args.Length > 1:
                switch (args[1])
                {
                    case "settings": app.ShowSettings(); break;
                    case "guide": app.ShowGuide(); break;
                    case "report": app.ShowReport(); break;
                    case "ask": app.ShowAskAll(); break;
                    case "onboarding": app.ShowOnboarding(); break;
                    default: app.ShowMain(); break;
                }
                return Ok;
            case "quit": _ = app.Quit(confirmed: true); return Ok;
            case "record-sim" when args.Length > 1:
                var speed = args.Length > 2 && double.TryParse(args[2], out var s) ? s : 1;
                var both = args.Length > 3 && args[3] == "1";
                return new JsonObject { ["meetingId"] = host.Controller.StartSimulatedRecording(args[1], speed, both, main?.State.CurrentFolder ?? "") };
            case "recordstop": host.Recorder.Stop(); return Ok;
            case "recordpause": if (host.Recorder.IsPaused) host.Recorder.Resume(); else host.Recorder.Pause(); return Ok;
            case "recordstall" when args.Length > 1 && int.TryParse(args[1], out var channel):
                host.Recorder.SimulateStall(channel, args.Length > 2 && double.TryParse(args[2], out var seconds) ? seconds : null);
                return Ok;
            case "recordinterrupt" when args.Length > 1 && int.TryParse(args[1], out var ch): host.Recorder.SimulateInterruption(ch); return Ok;
            case "recordlang" when args.Length > 1 && MeetingLanguages.Parse(args[1]) is { } language: host.Recorder.SwitchLanguage(language); return Ok;
            case "import" when args.Length > 1:
                _ = host.Controller.ImportFiles(args[1..], new ImportOptions(), main?.State.CurrentFolder ?? "");
                return Ok;
            case "purge" when args.Length > 1 && long.TryParse(args[1], out var purge): host.Controller.DeletePermanently(purge); return Ok;
            // export <會議 id> <notes|transcript|audio|package> <格式> <路徑> [times 1/0] [speakers 1/0]：同 Mac 的 shiyinctl export。
            // 在 UI 執行緒上跑（PDF 要建 WebView2），所以不等它：失敗時寫 <路徑>.error（同 Mac）
            case "export" when args.Length > 4 && long.TryParse(args[1], out var exportId) && Enum.TryParse<ExportItem>(args[2], true, out var item):
            {
                var path = args[4];
                _ = ExportAsync();
                async Task ExportAsync()
                {
                    try { await host.Controller.Export(item, args[3], args.Length <= 5 || args[5] == "1", args.Length <= 6 || args[6] == "1", exportId, path); }
                    catch (Exception e) { File.WriteAllText(path + ".error", $"ERROR {e.Message}"); }
                }
                return Ok;
            }
            case "llmprovider" when args.Length > 1:
                host.Settings.DebugProvider = LLMProviders.Parse(args[1]);
                host.Settings.DebugModel = args.Length > 2 ? args[2] : null;
                return Ok;
            case "llmkey" when args.Length > 2 && host.Keys is MemoryKeyStore memory: memory.Keys[args[1]] = args[2]; return Ok;
            case "llmurl" when args.Length > 2 && LLMProviders.Parse(args[1]) is { } provider: host.Llm.BaseUrlOverrides[provider] = args[2]; return Ok;
            case "modelurl" when args.Length > 2 && Enum.TryParse<ModelStore.Model>(args[1], true, out var model): host.Models.DebugUrls[model] = args[2]; return Ok;
            case "model" when args.Length > 2 && Enum.TryParse<ModelStore.Model>(args[2], true, out var m):
                switch (args[1])
                {
                    case "start": host.Models.Start(m); break;
                    case "pause": host.Models.Pause(m); break;
                    case "delete": host.Models.Delete(m); break;
                }
                return Ok;
            case "hotkey": host.HotKeys.Simulate(args.Length > 1 ? args[1] : "record"); return Ok;
            case "meetingapp" when args.Length > 2:
                host.Detector.Simulated[args[1]] = args[2] == "on";
                return Ok;
            case "rediarize" when args.Length > 2 && long.TryParse(args[1], out var meeting):
                host.Controller.Rediarize(meeting, int.TryParse(args[2], out var n) ? n : null);
                return Ok;
            case "language" when args.Length > 2 && long.TryParse(args[1], out var lm) && MeetingLanguages.Parse(args[2]) is { } l:
                host.Controller.SetMeetingLanguage(lm, l);
                return Ok;
            default: return new JsonObject { ["error"] = "unknown command" };
        }
    }

    static JsonNode State(AppHost host, MainWindow? main)
    {
        var r = host.Recorder;
        var running = new JsonArray();
        foreach (var lane in Enum.GetValues<JobLane>())
            if (host.Runner.Running(lane) is { } job)
                running.Add(new JsonObject { ["lane"] = lane.Code(), ["meetingId"] = job.Job.MeetingId, ["kind"] = job.Job.Kind.Code(), ["stage"] = job.Stage, ["progress"] = job.Progress });
        var models = new JsonObject();
        foreach (var model in Enum.GetValues<ModelStore.Model>()) models[model.ToString()] = host.Models.StateOf(model).ToString();
        return new JsonObject
        {
            ["version"] = host.Version,
            ["home"] = host.Paths.Home,
            ["selection"] = main?.State.Selection,
            ["tab"] = main?.State.Tab.ToString(),
            ["meetings"] = main?.State.Meetings.Count,
            ["running"] = running,
            ["models"] = models,
            ["recorder"] = new JsonObject
            {
                ["isRecording"] = r.IsRecording, ["isPaused"] = r.IsPaused, ["meetingId"] = r.MeetingId, ["elapsed"] = r.Elapsed, ["source"] = r.Source,
                ["language"] = r.Language.Code(), ["liveLines"] = r.LiveLines.Count, ["volatile"] = r.VolatileText, ["liveStatus"] = r.LiveStatus,
                ["deviceWarning"] = r.DeviceWarning, ["hint"] = r.Hint?.ToString(), ["systemSilent"] = r.SystemSilent,
                ["levelCheck"] = new JsonObject(r.LevelCheck.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)JsonValue.Create(p.Value.ToString())))),
            },
            ["hotKeyError"] = host.HotKeyError,
            ["notifications"] = host.Notifier.Enabled,
        };
    }
}
#endif
