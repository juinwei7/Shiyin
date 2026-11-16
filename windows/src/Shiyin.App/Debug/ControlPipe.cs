#if DEBUG
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shiyin.LLM;
using Shiyin.Model;
using Shiyin.Notes;
using Shiyin.Platform;
using Shiyin.Services;

namespace Shiyin.App.Debug;

/// <summary>
/// DEBUG 的控制通道（design-windows §15.3，對應 Mac 的 DebugControl）：named pipe「shiyin-debug」，一行一個指令（JSON 陣列），回一行 JSON。
/// 驗證時用 shiyinctl.exe 送指令，不送鍵盤事件（CLAUDE.md）。只在 DEBUG 建置裡有
/// </summary>
public static partial class ControlPipe
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
        // 會議列表的排序、分組、篩選，資料夾自動歸類，定期會議（M§71～§73、design-windows §11.30～§11.32）
        if (Organize(host, main, args) is { } organized) return organized;
        switch (args.FirstOrDefault())
        {
            case "state": return State(host, main);
            // packages <package family>：用 Packages.ByFamily、Manifest 查 MSIX 套件（測查 Codex、Claude 桌面版的系統呼叫，design-windows §10）
            case "packages" when args.Length > 1:
                return new JsonArray([.. Platform.Packages.ByFamily(args[1]).Select(p =>
                {
                    var (name, appId) = Platform.Packages.Manifest(p.Path, "?");
                    return (JsonNode)new JsonObject { ["fullName"] = p.FullName, ["path"] = p.Path, ["name"] = name, ["appId"] = appId };
                })]);
            // tools refresh：清掉桌面版的快取（等於「重新檢查」）
            case "tools" when args.Length > 1 && args[1] == "refresh": host.Locator.ClearCache(); return Ok;
            case "select" when args.Length > 1 && long.TryParse(args[1], out var id): main?.Reveal(id); return Ok;
            // tab <summary|transcript|ask>；舊名稱當別名：topics → 紀錄、review → 紀錄並展開待確認、notes → 逐字稿並展開我的筆記（R44.1）
            // notesview list|mindmap|people：紀錄分頁切換成條列、心智圖或人物（M§42.5、M§66；寫進測試家目錄的 settings.json）
            case "notesview" when args.Length > 1:
                host.Settings.Set(AppSettings.NotesViewKey, args[1].ToLowerInvariant() switch { "mindmap" => "mindMap", "people" => "people", _ => "list" });
                main?.Refresh();
                return Ok;
            // peoplenotes <會議 id>：等於按「人物」的「產生每個人的重點」（M§66、design-windows §11.25）
            case "peoplenotes" when args.Length > 1 && long.TryParse(args[1], out var peopleMeeting):
                host.Controller.GeneratePeopleNotes(peopleMeeting);
                return Ok;
            // cleantranscript <會議 id>：等於按逐字稿「精簡」的「產生精簡版」（M§70、design-windows §11.29，同 Mac）；
            // transcriptview original|clean：逐字稿的「原文｜精簡」（寫進測試家目錄的 settings.json）
            case "cleantranscript" when args.Length > 1 && long.TryParse(args[1], out var cleanMeeting):
                host.Controller.GenerateCleanTranscript(cleanMeeting);
                return Ok;
            case "transcriptview" when args.Length > 1:
                host.Settings.Set(AppSettings.TranscriptViewKey, args[1] == "clean" ? "clean" : "original");
                main?.Refresh();
                return Ok;
            // addterm <文字>：新增詞彙（R94.1，同 Mac 的 addterm：「\n」當換行、一次好幾個），回傳說明（沒有要說的是空的）
            case "addterm" when args.Length > 1:
                return new JsonObject { ["message"] = host.Controller.AddTerms(args[1].Replace("\\n", "\n")) ?? "" };
            // seek <秒> [play]、toggle：播放（同 Mac）
            case "seek" when args.Length > 1 && double.TryParse(args[1], System.Globalization.CultureInfo.InvariantCulture, out var seekTo):
                host.Player.Seek(seekTo, args.Length > 2 && args[2] == "play");
                return Ok;
            case "toggle": host.Player.Toggle(); return Ok;
            case "tab" when args.Length > 1 && DetailTabs.Resolve(args[1]) is { } resolved:
                main?.State.OpenTab(resolved.Tab, resolved.ExpandReview, resolved.ExpandNotes);
                return Ok;
            // 人物頁（M§60.2、design-windows §11.22）：sidebar people|meetings（側欄的「會議｜人物」）；person <人的 id>（切到人物、選這個人）
            case "sidebar" when args.Length > 1 && main is not null:
                main.State.SetSidebar(args[1] == "people" ? SidebarMode.People : SidebarMode.Meetings);
                return Ok;
            case "person" when args.Length > 1 && long.TryParse(args[1], out var personId) && main is not null:
                main.State.SelectPerson(personId);
                return Ok;
            case "closewindow" when args.Length > 1:
                if (args[1] == "main") main?.AppWindow.Hide();
                return Ok;
            case "open" when args.Length > 1:
                switch (args[1])
                {
                    case "settings": app.ShowSettings(); if (args.Length > 2) app.OpenSettingsPage(args[2]); break;   // open settings [provider|recording|names|advanced]
                    case "guide": app.ShowGuide(); break;
                    case "report": app.ShowReport(); break;
                    case "ask": app.ShowAskAll(); break;
                    case "onboarding": app.ShowOnboarding(); break;
                    default: app.ShowMain(); break;
                }
                return Ok;
            case "quit": _ = app.Quit(confirmed: true); return Ok;
            // openlink <shiyin:…>：等於點了這個網址（AI 工具給的會議連結 shiyin://open?meeting=…&t=…，M§44.5）
            case "openlink" when args.Length > 1: { var link = args[1]; host.Post(() => App.Current.OpenLink(link)); } return Ok;
            // 待確認就地回答（R41，M§31.2）：reviewexpand <summary|decisions|actionItems|openQuestions|topic|custom|person> <位置> [第二個位置]（- 收合）；
            // reviewsubmit（送目前的草稿）；reviewskipall（全部略過）；review <答案 JSON：{"0":"答案"}> <略過的題號，逗號分隔>（直接送 SubmitReview）
            case "reviewexpand" when args.Length > 1 && main is not null:
            {
                var a = args.Length > 2 && int.TryParse(args[2], out var x) ? x : 0;
                var b = args.Length > 3 && int.TryParse(args[3], out var y) ? y : 0;
                main.ReviewDraft.Expanded = args[1] switch
                {
                    "summary" => NoteItemRef.Summary(a), "decisions" => NoteItemRef.Decisions(a), "actionItems" => NoteItemRef.ActionItems(a),
                    "openQuestions" => NoteItemRef.OpenQuestions(a), "topic" => NoteItemRef.TopicPoint(a, b), "custom" => NoteItemRef.Custom(a, b),
                    "person" => NoteItemRef.Person(a, b), _ => null,
                };
                main.Refresh();
                return Ok;
            }
            case "reviewsubmit" when main is not null:   // reviewsubmit [段落 位置 [第二個位置]]：給了段落就是按那一條卡片的「送出」（只送那幾題）
            {
                IReadOnlySet<int>? only = null;
                if (args.Length > 2 && main.State.Detail is { } submitDetail)
                {
                    var a = args.Length > 2 && int.TryParse(args[2], out var x) ? x : 0;
                    var b = args.Length > 3 && int.TryParse(args[3], out var y) ? y : 0;
                    NoteItemRef? r = args[1] switch
                    {
                        "summary" => NoteItemRef.Summary(a), "decisions" => NoteItemRef.Decisions(a), "actionItems" => NoteItemRef.ActionItems(a),
                        "openQuestions" => NoteItemRef.OpenQuestions(a), "topic" => NoteItemRef.TopicPoint(a, b), "custom" => NoteItemRef.Custom(a, b),
                        "person" => NoteItemRef.Person(a, b), _ => null,
                    };
                    only = r is { } rr && submitDetail.ReviewLinks.TryGetValue(rr, out var list) ? list.ToHashSet() : new HashSet<int>();
                }
                main.ReviewDraft.Submit(only);
                main.Refresh();
                return Ok;
            }
            // editnotes <會議 id> <會議紀錄 JSON 或 JSON 檔的路徑>：存成新的一版，同按「完成」（同 Mac 的 editnotes；design-windows §11.25）
            case "editnotes" when args.Length > 2 && long.TryParse(args[1], out var notesId):
            {
                var json = File.Exists(args[2]) ? File.ReadAllText(args[2]) : args[2];
                if (MeetingNotes.Decode(json) is not { } edited) return new JsonObject { ["error"] = "not notes JSON" };
                host.Controller.SaveEditedNotes(edited, notesId);
                return Ok;
            }
            case "reviewskipall" when main?.State.Detail is { } skipDetail:
                main.ReviewDraft.SkipAllUnanswered(skipDetail);
                main.ReviewDraft.Flush();
                main.Refresh();
                return Ok;
            case "review" when args.Length > 2 && main?.State.Detail is { } reviewDetail:
            {
                var answers = (JsonNode.Parse(args[2]) as JsonObject)?.Where(p => int.TryParse(p.Key, out _))
                    .ToDictionary(p => int.Parse(p.Key), p => p.Value?.GetValue<string>() ?? "") ?? [];
                var dismissed = (args.Length > 3 ? args[3] : "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToHashSet();
                return new JsonObject { ["ok"] = host.Controller.SubmitReview(reviewDetail.Meeting.Id!.Value, answers, dismissed) };
            }
            case "record-sim" when args.Length > 1:
                var speed = args.Length > 2 && double.TryParse(args[2], out var s) ? s : 1;
                // 第三個參數：1 是雙聲道，2 是雙聲道、電腦聲音全是 0（design-windows §11.18）
                var both = args.Length > 3 && args[3] is "1" or "2";
                return new JsonObject { ["meetingId"] = host.Controller.StartSimulatedRecording(args[1], speed, both, main?.State.CurrentFolder ?? "", args.Length > 3 && args[3] == "2") };
            case "recordstop": host.Recorder.Stop(); return Ok;
            case "recordpause": if (host.Recorder.IsPaused) host.Recorder.Resume(); else host.Recorder.Pause(); return Ok;
            case "recordstall" when args.Length > 1 && int.TryParse(args[1], out var channel):
                host.Recorder.SimulateStall(channel, args.Length > 2 && double.TryParse(args[2], out var seconds) ? seconds : null);
                return Ok;
            case "recordinterrupt" when args.Length > 1 && int.TryParse(args[1], out var ch): host.Recorder.SimulateInterruption(ch); return Ok;
            case "recordlang" when args.Length > 1 && MeetingLanguages.Parse(args[1]) is { } language: host.Recorder.SwitchLanguage(language); return Ok;
            // 麥克風（R58、M§38.7）：fakemic <名稱> <bluetooth 0|1> [內建的名稱]：假的麥克風（只在記憶體，第一支當成系統預設）；fakemic off 清掉。
            // 模擬錄音（record-sim）的 micDevice 照設定從這裡找，測得到選單、提示列、通知、換了之後錄音不中斷
            // enrollvoice <音檔> <名字>：用音檔代替麥克風錄「我的聲音」（design-windows §11.20），回傳錯誤訊息（成功是空的）
            case "enrollvoice" when args.Length > 2:
            {
                var samples = host.Decoder.Decode(args[1]);
                var error = host.Controller.EnrollVoice(args[2], samples).GetAwaiter().GetResult();
                return new JsonObject { ["error"] = error ?? "" };
            }
            case "enrolldelete": host.Controller.DeleteEnrollment(); return Ok;
            // playingapp <名稱> <meeting|browser|other> <裝置> <1=在預設裝置上> <預設裝置的名稱>｜playingapp off：假的正在出聲的 app（design-windows §11.18）
            case "playingapp" when args.Length > 1:
                host.FakePlayingApps = args[1] == "off" ? null
                    : args.Length > 5 ? [new Services.Capture.Recorder.PlayingApp(args[1], args[2], args[3], args[4] == "1", args[5])] : [];
                return Ok;
            case "fakemic" when args.Length > 1:
                if (args[1] == "off") host.FakeMicrophones = [];
                else
                {
                    var fakes = new List<Services.Capture.MicrophoneDevice> { new("fake:default", args[1], IsBluetooth: args.Length > 2 && args[2] == "1") };
                    if (args.Length > 3) fakes.Add(new("fake:builtin", args[3], IsBuiltIn: true));
                    host.FakeMicrophones = fakes;
                }
                return Ok;
            // micswitch <id|default>：等於在錄音列的麥克風清單選一支（沒在錄音時只改設定）
            case "micswitch" when args.Length > 1: host.Recorder.SwitchMicrophone(args[1] == "default" ? "" : args[1]); return Ok;
            case "michint" when args.Length > 1 && args[1] == "dismiss": host.Recorder.DismissMicHint(); return Ok;
            // textsize <−1～5>：會議內容的文字大小（R59），等於按快捷鍵或在設定頁選
            case "textsize" when args.Length > 1 && int.TryParse(args[1], out var level):
                host.Settings.Set(AppSettings.TextSizeKey, (double)TextSizes.Clamp(level));
                return Ok;
            // import [lang=zh|en|ja] <檔案…>：同 Mac 的 shiyinctl import。跟拖進來一樣分流：封包、逐字稿、Markdown 直接匯入，錄音不跳匯入選項
            // 會議的參考文件（design-windows §11.8、M§39.7）：adddoc <會議 id> <檔案>…（同「加入文件…」選了這些檔）；drop <檔案>…（等於拖進主視窗）；
            // removedoc <文件 id>；docread <檔案> <zh|en|ja>（只讀出文字，回傳 kind、pages、ocr 或 error）；docbanner <會議 id> dismiss|resummarize
            case "adddoc" when args.Length > 2 && long.TryParse(args[1], out var docMeeting):
                _ = host.Controller.AddDocuments(args[2..], docMeeting);
                return Ok;
            case "drop" when args.Length > 1: main?.RequestImport(args[1..]); return Ok;
            case "removedoc" when args.Length > 1 && long.TryParse(args[1], out var documentId): host.Controller.RemoveDocument(documentId); return Ok;
            case "docread" when args.Length > 2:
            {
                var docLanguage = MeetingLanguages.Parse(args[2]) ?? MeetingLanguage.Zh;
                var started = DateTime.UtcNow;
                try
                {
                    var r = Task.Run(() => DocumentFiles.Read(args[1], docLanguage, new Platform.WindowsOcr())).GetAwaiter().GetResult();
                    return new JsonObject
                    {
                        ["kind"] = r.Kind.Raw(), ["pages"] = new JsonArray([.. r.Pages.Select(x => (JsonNode)x)]), ["ocr"] = r.Ocr,
                        ["seconds"] = (DateTime.UtcNow - started).TotalSeconds,
                    };
                }
                catch (Shiyin.IO.DocumentError e) { return new JsonObject { ["error"] = e.Message, ["code"] = e.Code }; }
            }
            case "docbanner" when args.Length > 2 && long.TryParse(args[1], out var bannerMeeting):
                if (args[2] == "dismiss") host.Controller.DismissDocumentsChanged(bannerMeeting); else host.Controller.Resummarize(bannerMeeting);
                return Ok;
            case "import" when args.Length > 1:
            {
                var files = args[1..].ToList();
                var options = new ImportOptions();
                if (files[0].StartsWith("lang=") && MeetingLanguages.Parse(files[0][5..]) is { } importLanguage)
                {
                    options = options with { Language = importLanguage };
                    files.RemoveAt(0);
                }
                var folder = main?.State.CurrentFolder ?? "";
                var media = host.Controller.RequestImport(files, folder, main?.State.Selection);
                _ = host.Controller.ImportFiles(media, options, folder);
                return Ok;
            }
            // 拆成兩句（M§46.3，對目前選取的會議）：splitsegment <段落 id> <UTF-16 位置> <說話者|new>；
            // splitmode <段落 id> 打開對話框（等於右鍵「拆成兩句…」）；splitcaret <位置> 把對話框的游標放到那裡；undospeaker 復原
            case "splitsegment" when args.Length > 3 && main?.State.Selection is long splitMeeting && long.TryParse(args[1], out var splitId) && int.TryParse(args[2], out var at):
                host.Controller.SplitSegment(splitMeeting, splitId, at, args[3] == "new" ? null : int.Parse(args[3]));
                return Ok;
            case "splitmode" when args.Length > 1 && main?.State.Detail is { } splitDetail && long.TryParse(args[1], out var modeId)
                                  && splitDetail.Segments.FirstOrDefault(s => s.Id == modeId) is { } splitSegment:
                _ = Dialogs.SplitSegment(main, splitDetail, splitSegment);
                return Ok;
            case "splitcaret" when args.Length > 1 && int.TryParse(args[1], out var caret): Dialogs.SplitBox?.Select(caret, 0); return Ok;
            case "undospeaker" when main?.State.Selection is long undoMeeting: host.Controller.UndoSpeakerEdit(undoMeeting); return Ok;
            // 多選（R88、M§62、design-windows §11.23）：selectmany <id,id,…>（等於 Ctrl 點選）；trashmany、restoremany、purgemany <id,id,…>（不經過確認）；
            // showtrash 0|1；confirmemptytrash（打開確認對話框）；emptytrash（不經過確認）
            case "selectmany" when args.Length > 1 && main is not null: main.State.SetListSelection(Ids(args[1])); return Ok;
            case "trashmany" when args.Length > 1: host.Controller.MoveToTrash(Ids(args[1])); return Ok;
            case "restoremany" when args.Length > 1: host.Controller.Restore(Ids(args[1])); return Ok;
            case "purgemany" when args.Length > 1: host.Controller.DeletePermanently(Ids(args[1])); return Ok;
            case "showtrash" when main is not null: main.State.ShowTrashList(args.Length > 1 && args[1] == "1"); return Ok;
            case "confirmemptytrash" when main is not null: _ = main.ConfirmEmptyTrash(); return Ok;
            case "emptytrash": host.Controller.EmptyTrash(); return Ok;
            // 刪除一句（R89、M§63）：deletesegment <會議 id> <段落 id>；分割會議（R90、M§64）：splitmeeting <會議 id> <段落 id>（不經過確認），
            // splitconfirm <會議 id> <段落 id>（打開確認對話框，等於右鍵「從這裡分成兩場…」）
            case "deletesegment" when args.Length > 2 && long.TryParse(args[1], out var deleteMeeting) && long.TryParse(args[2], out var deleteId):
                main?.State.Reveal(deleteMeeting);
                host.Controller.DeleteSegment(deleteMeeting, deleteId);
                return Ok;
            case "splitmeeting" when args.Length > 2 && long.TryParse(args[1], out var splitWhole) && long.TryParse(args[2], out var splitAt):
                main?.State.Reveal(splitWhole);
                _ = host.Controller.SplitMeeting(splitWhole, splitAt);
                return Ok;
            case "splitconfirm" when args.Length > 2 && main is not null && long.TryParse(args[1], out var confirmMeeting) && long.TryParse(args[2], out var confirmAt):
            {
                main.State.Reveal(confirmMeeting);
                main.State.SetTab(DetailTab.Transcript);
                if (main.State.Detail?.Segments.FirstOrDefault(s => s.Id == confirmAt) is not { } confirmSegment) return new JsonObject { ["error"] = "no such segment" };
                _ = main.ConfirmSplitMeeting(confirmMeeting, confirmSegment);
                return Ok;
            }
            case "purge" when args.Length > 1 && long.TryParse(args[1], out var purge): host.Controller.DeletePermanently(purge); return Ok;
            // export <notes|transcript|audio|package> <格式> <路徑> [times 1/0] [speakers 1/0] [會議 id，預設目前選取的]：同 Mac 的 shiyinctl export。
            // 在 UI 執行緒上跑（PDF 要建 WebView2），所以不等它：失敗時寫 <路徑>.error（同 Mac）
            case "export" when args.Length > 3 && Enum.TryParse<ExportItem>(args[1], true, out var item):
            {
                var path = args[3];
                var exportId = args.Length > 6 && long.TryParse(args[6], out var given) ? given : main?.State.Selection;
                _ = ExportAsync();
                async Task ExportAsync()
                {
                    try
                    {
                        if (exportId is not long id) throw new InvalidOperationException("no meeting selected");  // l10n-skip
                        await host.Controller.Export(item, args[2], args.Length <= 4 || args[4] == "1", args.Length <= 5 || args[5] == "1", id, path);
                    }
                    catch (Exception e) { File.WriteAllText(path + ".error", $"ERROR {e.Message}"); }
                }
                return Ok;
            }
            // metrics [reset]：UI 執行緒最長卡住多久（同 Mac 的 metrics，M§21.12）；加 reset 讀完歸零
            case "metrics":
            {
                var result = new JsonObject { ["maxMainGapMs"] = Math.Round(DebugMetrics.MaxMainGapMs, 1) };
                if (args.Length > 1 && args[1] == "reset") DebugMetrics.Reset();
                return result;
            }
            case "llmprovider" when args.Length > 1:
                host.Settings.DebugProvider = LLMProviders.Parse(args[1]);
                host.Settings.DebugModel = args.Length > 2 ? args[2] : null;
                host.Settings.NotifySummarizerChanged();
                return Ok;
            // 企業伺服器（design-windows §12.11）：login <網址> <信箱>（伺服器開發用的登入，不經過瀏覽器，登入後套用預設）、logout、
            // expire（access token 立刻過期）、local <會議 id>（等於按「改在這台電腦轉錄」）。結果看 state 的 enterprise
            case "enterprise" when args.Length > 1:
                switch (args[1])
                {
                    case "login" when args.Length > 3:
                        enterpriseError = null;
                        _ = Task.Run(async () =>
                        {
                            try { await host.Enterprise.DebugDevSignIn(args[2], args[3]); host.ApplyEnterpriseDefaults(); }
                            catch (Exception e) { enterpriseError = e.Message; }
                        });
                        break;
                    case "logout": _ = Task.Run(() => host.Enterprise.SignOut()); break;
                    case "expire": host.Enterprise.InvalidateAccess(); break;
                    case "glossary": _ = Task.Run(() => host.Enterprise.RefreshGlossary()); break;   // 重新拿公司詞彙表（R53）
                    // pending：還在等瀏覽器的登入頁網址（虛擬機沒有瀏覽器時，從外面登入、再用 Start-Process 交回 shiyin://auth）
                    case "pending": return new JsonObject { ["url"] = host.Enterprise.PendingLoginUrl?.ToString() ?? "" };
                    case "join" when args.Length > 2:   // join <shiyin://join?server=…>：等於點了加入連結（R49.7）
                        { var link = args[2]; host.Post(() => App.Current.OpenLink(link)); }
                        break;
                    case "local" when args.Length > 2 && long.TryParse(args[2], out var meeting): host.Runner.TranscribeLocally(meeting); break;
                }
                return Ok;
            case "llmkey" when args.Length > 2 && host.Keys is MemoryKeyStore memory:
                memory.Keys[args[1]] = args[2];
                host.Settings.NotifyKeysChanged();   // 等於存了新的 key：解除暫停（M§34.4）
                return Ok;
            case "summarizer":   // 整理工具準備好了沒、登入快取（M§34.6）
            {
                var problem = host.Settings.SummarizerProblem;
                return new JsonObject
                {
                    ["provider"] = host.Settings.Provider.Code(), ["ready"] = problem is null,
                    ["problem"] = problem is null ? "" : problem.Kind.ToString(), ["message"] = problem?.Message ?? "",
                    ["action"] = problem?.ActionTitle ?? "",
                    ["loggedIn"] = new JsonObject
                    {
                        ["claude"] = CliLogin.Cached(LLMProvider.Claude) is { } c ? (c ? "yes" : "no") : "unknown",
                        ["codex"] = CliLogin.Cached(LLMProvider.Codex) is { } x ? (x ? "yes" : "no") : "unknown",
                    },
                };
            }
            case "loginrefresh":   // 重新查 claude、codex 的登入狀態
                host.Post(async () => { await CliLogin.Refresh(LLMProvider.Claude); await CliLogin.Refresh(LLMProvider.Codex); });
                return Ok;
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
            // 錄音島（design-windows §11.21、M§57.3）：island hover on|off（等於滑鼠移上去、移開）；meetingaction <later|mute|start|continue|stop>（按島上偵測會議的按鈕）
            case "island" when args.Length > 2 && args[1] == "hover": app.Island?.SetHover(args[2] == "on"); return Ok;
            case "meetingaction" when args.Length > 1: app.Island?.Perform(args[1]); return Ok;
            case "rediarize" when args.Length > 2 && long.TryParse(args[1], out var meeting):
                host.Controller.Rediarize(meeting, int.TryParse(args[2], out var n) ? n : null);
                return Ok;
            case "language" when args.Length > 2 && long.TryParse(args[1], out var lm) && MeetingLanguages.Parse(args[2]) is { } l:
                host.Controller.SetMeetingLanguage(lm, l);
                return Ok;
            // 匿名使用統計（design-windows §12.10、M§30.7）：DEBUG 版只在這裡設了網址才累計、送出
            case "usageurl" when args.Length > 1:   // usageurl <網址|off>：只在記憶體設統計網址
                host.Usage.Url = args[1] == "off" ? null : args[1];
                host.Usage.Touch();
                return Ok;
            case "usage": return host.Usage.DebugSnapshot();   // 目前的檔案和下一份要送的
            case "usageage" when args.Length > 1 && int.TryParse(args[1], out var days): host.Usage.DebugAge(days); return Ok;   // 把今天的匯總改成幾天前
            case "usagesend": _ = host.Usage.SendPending(); return Ok;
            default: return new JsonObject { ["error"] = "unknown command" };
        }
    }

    static string? enterpriseError;

    /// <summary>「1,3,4」這種逗號分隔的會議 id</summary>
    static List<long> Ids(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => long.TryParse(s, out var v) ? v : -1).Where(v => v >= 0).ToList();

    /// <summary>state 的 tools：執行檔、桌面版的名稱和啟動用的 id、子程序的 PATH</summary>
    static JsonObject? ToolState(ToolCommand? tool) => tool is null ? null : new JsonObject
    {
        ["path"] = tool.Executable, ["desktopApp"] = tool.DesktopApp, ["appId"] = tool.AppUserModelId,
        ["pathEnv"] = tool.Environment?.GetValueOrDefault("PATH")?.Split(';').FirstOrDefault(),
    };

    static JsonNode State(AppHost host, MainWindow? main)
    {
        var r = host.Recorder;
        var running = new JsonArray();
        foreach (var lane in Enum.GetValues<JobLane>())
            if (host.Runner.Running(lane) is { } job)
                running.Add(new JsonObject
                {
                    ["lane"] = lane.Code(), ["meetingId"] = job.Job.MeetingId, ["kind"] = job.Job.Kind.Code(), ["stage"] = job.Stage, ["progress"] = job.Progress,
                    ["server"] = job.Server is { } note ? new JsonObject { ["text"] = note.Text, ["waiting"] = note.Waiting } : null,
                });
        var models = new JsonObject();
        foreach (var model in Enum.GetValues<ModelStore.Model>()) models[model.ToString()] = host.Models.StateOf(model).ToString();
        return new JsonObject
        {
            ["version"] = host.Version,
            ["home"] = host.Paths.Home,
            ["selection"] = main?.State.Selection,
            // 多選、清空垃圾桶、分割會議（M§62、§64）：對話框開著時是要處理的那幾場、那一句
            ["multiSelected"] = new JsonArray([.. (main?.State.MultiSelected ?? []).Select(x => (JsonNode)x)]),
            ["showTrash"] = main?.State.ShowTrash,
            ["trashCount"] = main?.State.Trash.Count,
            ["confirmDelete"] = main?.DeleteTargets.Count > 0,
            ["deleteTargets"] = new JsonArray([.. (main?.DeleteTargets ?? []).Select(x => (JsonNode)x)]),
            ["confirmPurge"] = new JsonArray([.. (main?.PurgeTargets ?? []).Select(x => (JsonNode)x)]),
            ["confirmEmptyTrash"] = main?.ConfirmingEmptyTrash,
            ["splitMeetingRequest"] = main?.SplitMeetingRequest ?? -1,
            ["splittingMeeting"] = host.Controller.SplittingMeeting ?? -1,
            ["tab"] = main?.State.Tab.ToString().ToLowerInvariant(),
            ["reviewExpanded"] = main?.State.ReviewExpanded,
            ["notesView"] = host.Settings.NotesView,
            // 「人物」（M§66、design-windows §11.25）：選取中的會議每一張的名字、對到的說話者、說了幾秒和百分比、重點幾條、負責的待辦（原本的位置）
            ["peopleCards"] = new JsonArray([.. (main?.State.Detail is { Notes: { } peopleNotes } peopleDetail ? peopleDetail.PeopleCards(peopleNotes) : [])
                .Select(c => (JsonNode)new JsonObject
                {
                    ["index"] = c.Index, ["name"] = c.Person.Name, ["speaker"] = c.Speaker, ["seconds"] = c.Seconds, ["percent"] = c.Percent,
                    ["points"] = c.Person.Points.Count, ["owned"] = new JsonArray([.. c.Owned.Select(o => (JsonNode)o.Index)]),
                })]),
            // 心智圖的縮放（R91、design-windows §11.24）：使用者選的倍數（null 是預設）、畫面上用的倍數（不含文字大小）
            ["mindMapZoom"] = main?.MindMapZoom.Value,
            ["mindMapScale"] = main?.MindMapZoom.Scale,
            // 播放（同 Mac 的 player）、播放條上的議題帶（M§43.3）：選取中的會議，長度同播放條
            ["player"] = new JsonObject
            {
                ["loaded"] = host.Player.IsLoaded, ["mixing"] = host.Player.IsMixing, ["playing"] = host.Player.IsPlaying, ["time"] = host.Player.CurrentTime, ["duration"] = host.Player.Duration,
            },
            ["topicRanges"] = new JsonArray([.. (main?.State.Detail is { Notes: { } topicNotes } topicDetail
                ? MindMap.TopicRanges(topicNotes, Math.Max(host.Player.Duration, topicDetail.Meeting.DurationSeconds ?? 0), MindMap.TopicLabel(MeetingLanguages.Interface))
                : []).Select(r => (JsonNode)new JsonObject { ["branch"] = r.Branch, ["title"] = r.Title, ["start"] = r.Start, ["end"] = r.End })]),
            ["userNotesExpanded"] = main?.State.UserNotesExpanded,
            // 精簡逐字稿（M§70、design-windows §11.29）：原文｜精簡、選取中的會議有幾句精簡版（0 是還沒產生）、上次失敗的原因、產生中的進度
            ["transcriptView"] = host.Settings.TranscriptView,
            ["cleanLines"] = main?.State.Detail?.CleanLines.Count,
            ["cleanEmpty"] = main?.State.Detail?.CleanLines.Count(p => p.Value.Length == 0),
            ["cleanFailure"] = main?.State.Detail?.CleanFailure,
            ["cleaning"] = main?.State.Detail?.Meeting.Id is long cleaningId && host.Controller.CleaningProgress(cleaningId) is { Active: true } cleaning
                ? new JsonObject { ["progress"] = cleaning.Progress } : null,
            // 詞彙表（R94）
            ["glossary"] = new JsonArray([.. host.Db.Glossary().Select(t => (JsonNode)t.Term)]),
            // 會議列表、資料夾的條件、定期會議（M§71～§73，同 Mac 的 list、folderRules、series、seriesCandidates、seriesFilter）
            ["list"] = ListState(main),
            ["folderRules"] = FolderRulesState(main),
            ["series"] = SeriesState(main),
            ["seriesCandidates"] = main?.State.SeriesCandidates.Count,
            ["seriesFilter"] = main?.State.SeriesFilter ?? -1,
            ["seriesDialog"] = Dialogs.SeriesSuggestionKey,
            // 移除一個人的確認開著時是誰（R85.7，同 Mac；沒有是 -1）、人物頁改名的輸入框開著沒（Mac 沒有）
            ["confirmRemovePerson"] = main?.RemovingPerson ?? -1,
            // 每一種東西都能直接改、刪（R100，同 Mac）：刪除資料夾、定期會議的確認開著時是哪個；重新命名中的會議、標記；就地改的那一條
            ["confirmDeleteFolder"] = main?.DeletingFolder ?? "",
            ["confirmDeleteSeries"] = main?.DeletingSeries ?? -1,
            ["renameMeetingRequest"] = main?.RenamingMeeting ?? -1,
            ["renamingBookmark"] = Dialogs.RenamingBookmark ?? -1,
            ["editingNoteItem"] = main?.NotesTabs.EditingItem(main.State.Detail)?.ToString() ?? "",
            ["bookmarks"] = new JsonArray([.. (main?.State.Detail?.Bookmarks ?? []).Select(b => (JsonNode)new JsonObject { ["id"] = b.Id, ["time"] = b.Time, ["label"] = b.Label })]),
            ["personRenameOpen"] = main?.PersonRenameOpen,
            ["folderRuleDialog"] = Dialogs.EditingFolderRule,
            // 會議結束的倒數（R95、M§69）：剩幾秒，沒在倒數是 -1（同 Mac 的 meetingprompts 的 countdown）
            ["meetingEndCountdown"] = host.MeetingEnd.Remaining(DateTime.UtcNow),
            ["reviewInline"] = main?.ReviewDraft.Expanded?.ToString(),
            // 會議的參考文件（M§39.7）：選取中的會議
            ["documents"] = new JsonArray([.. (main?.State.Detail?.Documents ?? []).Select(d =>
            {
                var use = main!.State.Detail!.DocumentUse.GetValueOrDefault(d.Id ?? 0);
                return (JsonNode)new JsonObject
                {
                    ["id"] = d.Id, ["name"] = d.Name, ["kind"] = d.Kind, ["file"] = d.File, ["pages"] = d.PageTexts.Count, ["ocr"] = d.Ocr,
                    ["includedPages"] = use?.IncludedPages ?? 0, ["truncated"] = use?.Truncated ?? false,
                };
            })]),
            ["readingDocuments"] = new JsonObject(host.Controller.ReadingDocuments.Select(p =>
                new KeyValuePair<string, JsonNode?>(p.Key.ToString(), new JsonArray([.. p.Value.Select(n => (JsonNode)n)])))),
            ["documentsChanged"] = new JsonArray([.. host.Controller.DocumentsChanged.Order().Select(x => (JsonNode)x)]),
            ["meetings"] = main?.State.Meetings.Count,
            ["running"] = running,
            ["models"] = models,
            ["transcriptionEngine"] = host.Settings.TranscriptionEngine,
            ["provider"] = host.Settings.Provider.Code(),
            // 找到的 claude、codex：使用者自己裝的，或桌面版附的（M§37.4、design-windows §10）
            ["tools"] = new JsonObject
            {
                ["claude"] = ToolState(host.Tools.Find("claude")),
                ["codex"] = ToolState(host.Tools.Find("codex")),
                ["claudeDesktop"] = DesktopApps.HasClaude(),
            },
            ["enterprise"] = new JsonObject
            {
                ["url"] = host.Enterprise.SavedUrl?.ToString(), ["hasAccount"] = host.Enterprise.HasAccount, ["signedIn"] = host.Enterprise.IsSignedIn,
                ["email"] = host.Enterprise.Account?.Email, ["serverName"] = host.Enterprise.Account?.ServerName,
                ["expired"] = host.Enterprise.ExpiredMessage, ["error"] = enterpriseError,
                ["glossary"] = new JsonArray([.. host.Enterprise.Glossary.Terms.Select(t => (JsonNode)t)]),
            },
            // 錄音島（M§57.3）：mode 是 hidden／collapsed／hint／prompt／hover，frame 是實際像素
            ["island"] = App.Current.Island is { } island ? new JsonObject
            {
                ["enabled"] = island.Enabled, ["visible"] = island.AppWindow.IsVisible, ["mode"] = island.Mode,
                ["frame"] = new JsonArray(island.Frame.X, island.Frame.Y, island.Frame.W, island.Frame.H),
                ["hintKey"] = island.HintKey ?? "", ["prompt"] = island.Prompt?.Message ?? "", ["promptText"] = island.PromptText ?? "",
                ["actions"] = new JsonArray([.. (island.Prompt?.Actions ?? []).Select(a => (JsonNode)a.Id)]),
            } : null,
            // 人物頁（M§60.2）
            ["sidebarMode"] = main?.State.Sidebar == SidebarMode.People ? "people" : "meetings",
            ["selectedPerson"] = main?.State.SelectedPerson ?? -1,
            ["peopleOverview"] = new JsonArray([.. (main?.State.People ?? []).Select(p => (JsonNode)new JsonObject
            {
                ["id"] = p.Id, ["name"] = p.Name, ["meetings"] = p.MeetingCount, ["last"] = p.LastMeetingDate ?? "", ["me"] = p.IsMe,
            })]),
            // 我的聲音（design-windows §11.20）
            ["selfPerson"] = host.Db.Enrollment() is { } me ? new JsonObject { ["id"] = me.PersonId, ["name"] = me.Name, ["createdAt"] = me.CreatedAt } : null,
            ["recorder"] = new JsonObject
            {
                ["isRecording"] = r.IsRecording, ["isPaused"] = r.IsPaused, ["meetingId"] = r.MeetingId, ["elapsed"] = r.Elapsed, ["source"] = r.Source,
                ["language"] = r.Language.Code(), ["liveLines"] = r.LiveLines.Count, ["volatile"] = r.VolatileText, ["liveStatus"] = r.LiveStatus,
                ["deviceWarning"] = r.DeviceWarning, ["hint"] = r.Hint?.ToString(), ["systemSilent"] = r.SystemSilent,
                ["hintText"] = r.Hint is { } shown ? r.HintText(shown) : null, ["systemCulprit"] = r.SystemCulprit?.Name,
                // 正在收音的麥克風、藍牙耳機的提醒（R58、M§38.7）
                ["micDevice"] = r.MicDevice is { } mic
                    ? new JsonObject { ["name"] = mic.Name, ["id"] = mic.Id, ["bluetooth"] = mic.IsBluetooth, ["builtIn"] = mic.IsBuiltIn } : null,
                ["micHint"] = r.MicHint is { } micHint ? new JsonObject { ["name"] = micHint.Name, ["builtIn"] = micHint.BuiltIn?.Name } : null,
                ["levelCheck"] = new JsonObject(r.LevelCheck.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)JsonValue.Create(p.Value.ToString())))),
                // 忘了停止錄音（M§35）
                ["idle"] = new JsonObject
                {
                    ["idleSeconds"] = r.IdleSeconds, ["lastSpeechAt"] = r.LastSpeechAt, ["prompt"] = r.IdlePrompt, ["liveActive"] = r.LiveActive,
                    ["remindAfter"] = r.IdleRemindSeconds, ["stopAfter"] = r.IdleStopSeconds,
                },
            },
            ["hotKeyError"] = host.HotKeyError,
            ["textSize"] = host.Settings.TextSize,
            ["inputDevice"] = host.Settings.InputDevice,
            ["notifications"] = host.Notifier.Enabled,
            ["lastToast"] = host.Notifier.LastShown is { } toast ? new JsonObject { ["title"] = toast.Title, ["body"] = toast.Body } : null,
        };
    }
}
#endif
