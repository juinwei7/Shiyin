using Shiyin.Data;
using Shiyin.IO;
using Shiyin.LLM;
using Shiyin.Localization;
using Shiyin.Model;
using Shiyin.Notes;
using Shiyin.Services.Capture;

namespace Shiyin.Services;

/// <summary>匯出 docx、PDF、錄音（平台的部分：OpenXml、WebView2、Media Foundation，design-windows §13）</summary>
public interface IExportPlatform
{
    Task NotesPdf(ExportBundle bundle, string path, CancellationToken cancel);
    Task NotesDocx(ExportBundle bundle, string path, CancellationToken cancel);
    Task TranscriptDocx(ExportBundle bundle, bool times, bool speakers, string path, CancellationToken cancel);
    /// <summary>錄音轉成 m4a 或 wav（同格式時直接複製）</summary>
    Task Audio(string source, string format, string path, CancellationToken cancel);
}

public enum ExportItem { Notes, Transcript, Audio, Package }

public static class ExportItems
{
    public static string Label(this ExportItem item) => item switch
    {
        ExportItem.Notes => L.T("會議紀錄"),
        ExportItem.Transcript => L.T("逐字稿"),
        ExportItem.Audio => L.T("錄音"),
        _ => L.T("整場會議（.shiyin）"),
    };

    public static string[] Formats(this ExportItem item) => item switch
    {
        ExportItem.Notes => ["md", "pdf", "docx"],
        ExportItem.Transcript => ["txt", "md", "srt", "vtt", "docx"],
        ExportItem.Audio => ["m4a", "wav"],
        _ => ["shiyin"],
    };
}

/// <summary>
/// 會議的操作（同 Mac 的 AppModel，不含畫面的狀態）：匯入、錄音存檔、當掉後復原、垃圾桶、重新轉錄、說話者的修正、整理、校正。
/// 畫面（ViewModel）傳入要操作哪一場會議；結果用 Notice、Error 事件告訴使用者，資料改了用 Changed 通知重讀
/// </summary>
public sealed class AppController
{
    public AppDatabase Db { get; }
    public AppSettings Settings { get; }
    public ShiyinPaths Paths { get; }
    public ModelStore Models { get; }
    public JobRunner Runner { get; }
    public Recorder Recorder { get; }
    readonly IAudioDecoder decoder;
    readonly IExportPlatform? exporter;
    readonly string appVersion;

    /// <summary>給使用者看的訊息（一般提示、錯誤）</summary>
    public event Action<string>? Notice;
    public event Action<string>? Error;
    /// <summary>資料改了：會議 id（null 是列表也要重讀）</summary>
    public event Action<long?>? Changed;
    /// <summary>要顯示這場會議（匯入完、錄音開始、點了通知）</summary>
    public event Action<long>? Reveal;

    /// <summary>正在匯入的檔案數，側邊欄下方顯示（M§21.13、R20.4）</summary>
    public int ImportingCount => Volatile.Read(ref importing);
    int importing;

    public AppController(AppDatabase db, AppSettings settings, ShiyinPaths paths, ModelStore models, JobRunner runner, Recorder recorder,
                         IAudioDecoder decoder, IExportPlatform? exporter, string appVersion)
    {
        (Db, Settings, Paths, Models, Runner, Recorder) = (db, settings, paths, models, runner, recorder);
        (this.decoder, this.exporter, this.appVersion) = (decoder, exporter, appVersion);
        recorder.Finished += RecordingFinished;
        recorder.LiveFinished += EnqueueDraftNotes;
        recorder.LiveFinal += (id, start, end, text) =>
        {
            try { db.AppendLiveSegment(id, start, end, text); } catch (Exception e) { Log.Error($"append live failed: {e.GetType().Name}"); }
        };
        recorder.LanguageChanged += (id, language) => Perform(id, () => db.SetLanguage(id, language));
        runner.Rediarized += (id, count, error) =>
        {
            if (count is int n) Notice?.Invoke(L.F("已重新辨識說話者：分出 %lld 位。名字請再確認一下；會議紀錄要更新的話，請重新整理會議紀錄。", n));
            else if (error is not null and not OperationCanceledException) Error?.Invoke(L.F("重新辨識說話者失敗：%@", error.Message));
        };
        runner.EditedDraftReplaced += _ =>
            Notice?.Invoke(L.T("正式版的會議紀錄好了；你改過的草稿留在版本紀錄裡，可以從「更多 → 復原上一版會議紀錄」找回"));
        runner.Changed += () => Changed?.Invoke(null);
    }

    /// <summary>啟動時：內建範本、清掉上次的暫存、過期的垃圾桶、復原錄音、開始工作佇列（同 Mac 的 AppModel.start）</summary>
    public void Start()
    {
        Db.SeedTemplates();
        CleanUpPreviousRun();
        PurgeExpiredTrash();
        RecoverRecordings();
        Runner.Start();
        // 語言偵測模型很小（60 MB），開著自動偵測就先下載好
        if (Settings.AutoDetectLanguage) Models.AutoStart(ModelStore.Model.LanguageDetection);
    }

    void Perform(long? meetingId, Action work)
    {
        try { work(); }
        catch (Exception e) when (e is not OutOfMemoryException) { Error?.Invoke(e.Message); }
        Changed?.Invoke(meetingId);
    }

    // MARK: 狀態

    public MeetingStatus Status(MeetingRow row) => Status(row.Id, row.LatestJob, row.ReviewCount);

    public MeetingStatus Status(long meetingId, Job? latestJob, int reviewCount)
    {
        if (Recorder.MeetingId == meetingId) return new MeetingStatus.Recording();
        if (Runner.RunningJob(meetingId) is { } running)
            return running.Job.Kind == JobKind.Transcribe ? new MeetingStatus.Transcribing(running.Progress, running.Stage) : new MeetingStatus.Summarizing();
        if (latestJob is not null)
            switch (latestJob.Status)
            {
                case JobStatus.Queued or JobStatus.Running: return new MeetingStatus.Queued();
                case JobStatus.Failed: return new MeetingStatus.Failed(latestJob.Kind, latestJob.Error ?? "");
                case JobStatus.Cancelled when latestJob.Kind == JobKind.Transcribe: return new MeetingStatus.Cancelled();
            }
        return reviewCount > 0 ? new MeetingStatus.Review(reviewCount) : new MeetingStatus.Done();
    }

    public MeetingStatus Status(MeetingDetail detail) => Status(detail.Meeting.Id!.Value, detail.LatestJob, detail.Notes?.ReviewItems.Count ?? 0);

    /// <summary>這場會議是在模型下載好之前用快速辨識轉錄的，現在可以用 Breeze 重新轉錄（M§21.17）</summary>
    public bool CanUpgradeTranscript(Meeting meeting) =>
        meeting.TranscribedWith == "system" && !Settings.UseFastTranscriber && Paths.HasAudio(meeting) && meeting.DeletedAt is null
        && Models.IsInstalled(ModelStore.Transcription(meeting.MeetingLanguage));

    // MARK: 資料夾（R9.2）

    /// <summary>現有的資料夾和各自的場數（不含垃圾桶），依名稱排序</summary>
    public static List<(string Name, int Count)> Folders(IEnumerable<MeetingRow> meetings) =>
        meetings.Where(m => m.Meeting.Folder.Length > 0).GroupBy(m => m.Meeting.Folder)
            .Select(g => (g.Key, g.Count())).OrderBy(f => f.Key, NaturalComparer.Instance).ToList();

    public void MoveToFolder(long id, string folder)
    {
        var name = folder.Trim();
        if (Db.Meeting(id)?.Folder == name)
        {
            Notice?.Invoke(name.Length == 0 ? L.T("這場會議沒有在任何資料夾裡") : L.F("這場會議已經在「%@」", name));
            return;
        }
        Perform(null, () => Db.SetFolder([id], name));
        Notice?.Invoke(name.Length == 0 ? L.T("已移出資料夾") : L.F("已移到「%@」", name));
    }

    public void RenameFolder(string old, string newName)
    {
        var name = newName.Trim();
        if (name.Length == 0 || name == old) return;
        Perform(null, () => Db.RenameFolder(old, name));
    }

    /// <summary>刪除資料夾：會議留著，移回「不在資料夾」</summary>
    public void DeleteFolder(string name) => Perform(null, () => Db.RenameFolder(name, ""));

    // MARK: 匯入（R3）

    /// <summary>
    /// 拖進來或選的檔案：封包、逐字稿、Markdown 直接匯入；回傳要問匯入選項的錄音、影片（R1.2）。
    /// selected：目前選的會議（Markdown 筆記加到那一場）
    /// </summary>
    public List<string> RequestImport(IReadOnlyList<string> files, string folder, long? selected)
    {
        var packages = files.Where(f => Importer.Extension(f) == "shiyin").ToList();
        var transcripts = files.Where(f => Importer.TranscriptExtensions.Contains(Importer.Extension(f))).ToList();
        var markdown = files.Where(f => Importer.MarkdownExtensions.Contains(Importer.Extension(f))).ToList();
        foreach (var p in packages) ImportPackage(p, folder);
        foreach (var t in transcripts) ImportTranscript(t, folder);
        if (markdown.Count > 0) ImportMarkdownNotes(markdown, folder, selected);
        var media = files.Where(Importer.IsMedia).ToList();
        if (media.Count == 0 && packages.Count == 0 && transcripts.Count == 0 && markdown.Count == 0)
            Error?.Invoke(L.T("只能匯入錄音、影片、逐字稿（srt、vtt、txt）、Markdown 或 .shiyin 檔"));
        return media;
    }

    /// <summary>匯入選項按「匯入」：多個檔案時標題、日期各自推算</summary>
    public Task ConfirmImport(IReadOnlyList<string> files, ImportOptions options, string folder) =>
        ImportFiles(files, files.Count == 1 ? options
            : new ImportOptions { Participants = options.Participants, TemplateId = options.TemplateId, Language = options.Language }, folder);

    /// <summary>從檔案總管開、拖到圖示上：不跳對話框，直接用預設值</summary>
    public Task ImportDirectly(IReadOnlyList<string> files, string folder, long? selected)
    {
        var other = files.Where(f => !Importer.IsMedia(f)).ToList();
        if (other.Count > 0) RequestImport(other, folder, selected);
        return ImportFiles(files.Where(Importer.IsMedia).ToList(), new ImportOptions(), folder);
    }

    /// <summary>複製檔案在背景做，大檔案（或從外接硬碟複製）時不卡住畫面（M§21.13、R20.4）</summary>
    public async Task ImportFiles(IReadOnlyList<string> files, ImportOptions options, string folder)
    {
        if (files.Count == 0) return;
        if (options.Folder.Length == 0) options = options with { Folder = folder };
        Interlocked.Add(ref importing, files.Count);
        Changed?.Invoke(null);
        Meeting? last = null;
        var failures = new List<string>();
        foreach (var file in files)
        {
            try
            {
                last = await Task.Run(() => Importer.ImportFile(file, options, Db, Paths, Settings));
                Runner.Wake();
            }
            catch (ImportException e) { failures.Add(e.Message); }
            Interlocked.Decrement(ref importing);
            Changed?.Invoke(null);
        }
        if (failures.Count > 0) Error?.Invoke(string.Join("\n", failures));
        if (last?.Id is long id) Reveal?.Invoke(id);
    }

    /// <summary>解開封包、複製錄音在背景做（M§21.13、R20.4）</summary>
    public async void ImportPackage(string file, string folder)
    {
        Interlocked.Increment(ref importing);
        Changed?.Invoke(null);
        try
        {
            var meeting = await Task.Run(() => MeetingPackage.Import(file, Db, Paths, folder));
            Changed?.Invoke(null);
            Reveal?.Invoke(meeting.Id!.Value);
            Notice?.Invoke(L.F("已匯入「%@」", meeting.DisplayTitle));
        }
        catch (Exception e) when (e is MeetingPackage.PackageException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            Error?.Invoke(e.Message);
        }
        finally
        {
            Interlocked.Decrement(ref importing);
            Changed?.Invoke(null);
        }
    }

    public void ImportTranscript(string file, string folder)
    {
        try
        {
            var meeting = Importer.ImportTranscript(file, folder, Db, Settings);
            Runner.Wake();
            Changed?.Invoke(null);
            Reveal?.Invoke(meeting.Id!.Value);
        }
        catch (Exception e) when (e is ImportException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            Error?.Invoke(e.Message);
        }
    }

    /// <summary>Markdown 當成使用者筆記：有選會議就加到那場，沒有就建一場新的（R3.5）。回傳要顯示的會議</summary>
    public long? ImportMarkdownNotes(IReadOnlyList<string> files, string folder, long? selected)
    {
        long? shown = null;
        foreach (var file in files)
        {
            string text;
            try { text = File.ReadAllText(file); } catch (IOException) { continue; }
            if (selected is long id && Db.Meeting(id) is { } meeting)
            {
                var merged = meeting.UserNotes.Length == 0 ? text : meeting.UserNotes + "\n\n" + text;
                Perform(id, () => Db.UpdateUserNotes(id, merged));
                shown = id;
            }
            else
            {
                try
                {
                    var created = Db.InsertMeeting(new Meeting
                    {
                        MeetingDate = DateText.String(Importer.Today), SourceFilename = Path.GetFileName(file), UserNotes = text,
                        Source = "transcript", Folder = folder, Language = Settings.DefaultMeetingLanguage.Code(),
                    }, null);
                    Perform(null, () => Db.UpdateUserNotes(created.Id!.Value, text));
                    shown = selected = created.Id;
                }
                catch (Microsoft.Data.Sqlite.SqliteException e) { Error?.Invoke(e.Message); }
            }
        }
        if (shown is long s) Reveal?.Invoke(s);
        return shown;
    }

    // MARK: 錄音（R1、R2）

    /// <summary>開始錄音：建一場會議、開始錄。回傳會議 id（沒開始回傳 null）</summary>
    public long? StartRecording(string folder)
    {
        if (Recorder.IsRecording) return null;
        var now = DateTime.Now;
        try
        {
            var meeting = Db.InsertMeeting(new Meeting
            {
                MeetingDate = DateText.String(DateOnly.FromDateTime(now)),
                SourceFilename = L.F("錄音 %@", now.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)),
                TemplateId = Settings.DefaultTemplate, Source = "recording", Channels = Settings.RecordSource, Folder = folder,
                Language = Settings.DefaultMeetingLanguage.Code(),
            }, null);
            Recorder.Start(meeting.Id!.Value, meeting.MeetingLanguage);
            Changed?.Invoke(null);
            Reveal?.Invoke(meeting.Id!.Value);
            return meeting.Id;
        }
        catch (Microsoft.Data.Sqlite.SqliteException e)
        {
            Error?.Invoke(L.F("沒辦法開始錄音：%@", e.Message));
            return null;
        }
    }

    /// <summary>DEBUG：用音訊檔模擬錄音（record-sim）</summary>
    public long? StartSimulatedRecording(string file, double speed, bool both, string folder)
    {
        if (Recorder.IsRecording) return null;
        var meeting = Db.InsertMeeting(new Meeting
        {
            MeetingDate = DateText.String(Importer.Today), SourceFilename = L.T("模擬錄音"), Source = "recording",
            Channels = both ? "both" : "mic", Folder = folder, Language = Settings.DefaultMeetingLanguage.Code(),
        }, null);
        Recorder.StartSimulated(meeting.Id!.Value, file, speed, both, meeting.MeetingLanguage, decoder);
        Changed?.Invoke(null);
        Reveal?.Invoke(meeting.Id!.Value);
        return meeting.Id;
    }

    public void ToggleRecording(string folder)
    {
        if (Recorder.IsRecording) Recorder.Stop();
        else StartRecording(folder);
    }

    /// <summary>錄音結束：把錄音檔接到會議上並排轉錄（R2.5）</summary>
    void RecordingFinished(Recorder.Result result)
    {
        try
        {
            if (result.Error is not null) Error?.Invoke(result.Error);
            if (Db.Meeting(result.MeetingId) is not { } meeting) return;
            // 只有檔案本身確實幾乎是空的（不到 0.5 秒的音訊）才當作沒錄到東西；不能只看 duration，萬一計算出錯會把整場錄音刪掉
            var bytes = result.File is { } f && File.Exists(f) ? new FileInfo(f).Length : 0;
            if (result.File is not { } file || bytes <= 44 + 16000)
            {
                if (result.File is { } empty) File.Delete(empty);
                Db.DeleteMeeting(result.MeetingId);
                Changed?.Invoke(null);
                return;
            }
            var destination = Path.Combine(Paths.Audio, $"{result.MeetingId}.wav");
            File.Delete(destination);
            File.Move(file, destination);
            var original = meeting.Clone();
            try
            {
                meeting.AudioFile = Path.GetFileName(destination);
                if (result.Duration > 0) meeting.DurationSeconds = (int)Math.Round(result.Duration, MidpointRounding.AwayFromZero);
                Db.UpdateMeeting(meeting);
                Db.Enqueue(result.MeetingId, JobKind.Transcribe);
            }
            catch
            {
                // 資料庫寫不進去（磁碟滿時常一起發生）：搬回 recording，下次開啟由復原流程接回（M§20.3）。會議改到一半的話改回去
                try { File.Move(destination, file); } catch (IOException) { }
                try { Db.UpdateMeeting(original); } catch (Microsoft.Data.Sqlite.SqliteException) { }
                throw;
            }
            Runner.Wake();
            Changed?.Invoke(null);
            Reveal?.Invoke(result.MeetingId);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            Error?.Invoke(L.F("錄音存檔失敗：%@（檔案還在 %@）", e.Message, result.File ?? ""));
        }
    }

    /// <summary>錄音太短就不整理草稿：轉錄很快，等正式版就好（DEBUG 用 debugDraftMinimumSeconds 改）</summary>
    public double DraftMinimumSeconds => Settings.Double("debugDraftMinimumSeconds") ?? 180;

    /// <summary>
    /// 草稿紀錄（M§21.3、R19.1）：即時辨識收尾後，錄音夠長、即時逐字稿夠多、會自動整理時，轉錄的同時先用即時逐字稿整理一份草稿
    /// </summary>
    void EnqueueDraftNotes(long meetingId, double duration)
    {
        if (duration < DraftMinimumSeconds || !Settings.AutoSummarize || Settings.Provider == LLMProvider.None) return;
        if (Db.Meeting(meetingId) is not { } meeting || !Paths.HasAudio(meeting) || meeting.Notes is not null) return;
        if (Db.Segments(meetingId, "live").Sum(s => Shiyin.Text.SwiftText.Count(s.Text)) < 100) return;
        try
        {
            Db.Enqueue(meetingId, JobKind.Summarize, JobRunner.Encode(new SummarizePayload(true)));
            Runner.Wake();
        }
        catch (Exception e) { Log.Error($"enqueue draft notes failed: {e.GetType().Name}"); }
    }

    /// <summary>啟動時處理上次沒正常結束的錄音（R1.5）</summary>
    public void RecoverRecordings()
    {
        var files = Directory.Exists(Paths.Recording) ? Directory.GetFiles(Paths.Recording) : [];
        foreach (var file in files.Where(f => Importer.Extension(f) == "wav"))
        {
            try
            {
                WavRepair.Repair(file);
                // 檔名是 <會議id>-<uuid>.wav；找不到會議就建一場新的
                if (MeetingIdOf(file) is long id && Db.Meeting(id) is { } meeting)
                {
                    var destination = Path.Combine(Paths.Audio, $"{id}.wav");
                    File.Delete(destination);
                    File.Move(file, destination);
                    var original = meeting.Clone();
                    try
                    {
                        meeting.AudioFile = Path.GetFileName(destination);
                        if (meeting.Title.Length == 0) meeting.Title = L.T("復原的錄音");
                        Db.UpdateMeeting(meeting);
                        Db.Enqueue(id, JobKind.Transcribe);
                    }
                    catch
                    {
                        // 搬回 recording、會議改回去，下次開啟再試（M§20.6）
                        try { File.Move(destination, file); } catch (IOException) { }
                        try { Db.UpdateMeeting(original); } catch (Microsoft.Data.Sqlite.SqliteException) { }
                        throw;
                    }
                }
                else
                {
                    Importer.ImportFile(file, new ImportOptions { Title = L.T("復原的錄音"), SourceName = L.T("復原的錄音.wav") }, Db, Paths, Settings, move: true);
                }
                Notice?.Invoke(L.T("找到上次沒有正常結束的錄音，已經復原並開始轉錄。"));
            }
            catch (Exception e) when (e is IOException or WavRepair.RepairException or ImportException or Microsoft.Data.Sqlite.SqliteException)
            {
                Log.Error($"recover recording failed: {e.GetType().Name}");
            }
        }
        // 錄音中途當掉、連錄音檔都沒有的空會議（M§20.6）；recording 裡還有錄音檔的會議是這次沒復原成功的，留著下次再試
        var remaining = (Directory.Exists(Paths.Recording) ? Directory.GetFiles(Paths.Recording) : []).Select(MeetingIdOf).OfType<long>().ToHashSet();
        foreach (var id in Db.EmptyRecordingMeetings())
            if (!remaining.Contains(id) && Recorder.MeetingId != id)
                try { Db.DeleteMeeting(id); } catch (Microsoft.Data.Sqlite.SqliteException) { }
    }

    static long? MeetingIdOf(string file) =>
        long.TryParse(Path.GetFileNameWithoutExtension(file).Split('-')[0], out var id) ? id : null;

    void CleanUpPreviousRun()
    {
        if (!Directory.Exists(Paths.Tmp)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(Paths.Tmp))
            try
            {
                if (Directory.Exists(entry)) Directory.Delete(entry, true); else File.Delete(entry);
            }
            catch (IOException) { }
    }

    // MARK: 會議操作

    public void UpdateInfo(long id, string title, string date, string participants) =>
        Perform(id, () => Db.UpdateInfo(id, title, date, participants));

    /// <summary>改會議語言（M§16.3）：之後的轉錄、整理、匯出都用新的語言，已經有的逐字稿和紀錄不會自動重做</summary>
    public void SetMeetingLanguage(long id, MeetingLanguage language)
    {
        if (Db.Meeting(id) is not { } meeting || meeting.MeetingLanguage == language) return;
        if (Recorder.IsRecording && Recorder.MeetingId == id)
        {
            // 錄音中：即時逐字稿也要換語言（跟自動偵測換語言同一條路，會一起更新資料庫）
            Recorder.SwitchLanguage(language);
            Notice?.Invoke(L.F("會議語言改成 %@，即時逐字稿也跟著換了", language.Name()));
            return;
        }
        Perform(id, () => Db.SetLanguage(id, language));
        Notice?.Invoke(L.F("會議語言改成 %@，要重新轉錄或整理的話，從「更多」選", language.Name()));
    }

    /// <summary>錄音中選「大約幾個人」（M§26.2）：錄完轉錄時用</summary>
    public void SetMinSpeakers(long id, int? count) => Perform(id, () => Db.SetMinSpeakers(id, count));

    public void SetTemplate(long id, string templateId) => Perform(id, () => Db.SetTemplate(id, templateId));

    /// <summary>移到垃圾桶（R9.3）</summary>
    public void MoveToTrash(long id)
    {
        if (Recorder.IsRecording && Recorder.MeetingId == id)
        {
            Error?.Invoke(L.T("這場會議還在錄音，請先停止錄音再移到垃圾桶"));
            return;
        }
        // 已經在垃圾桶裡就不再處理，不然 30 天的期限會重新計算
        if (Db.Meeting(id)?.DeletedAt is not null) return;
        Runner.Cancel(id);
        Perform(null, () => Db.SetTrashed(id, true));
        Notice?.Invoke(L.T("已移到垃圾桶，30 天內都可以還原"));
    }

    public void Restore(long id)
    {
        Perform(null, () => Db.SetTrashed(id, false));
        Reveal?.Invoke(id);
    }

    /// <summary>永久刪除（垃圾桶裡的「立即刪除」）</summary>
    public void DeletePermanently(long id)
    {
        if (Db.Meeting(id) is not { } meeting || (Recorder.IsRecording && Recorder.MeetingId == id)) return;
        Runner.Cancel(id);
        Perform(null, () =>
        {
            Db.DeleteMeeting(id);
            if (Paths.HasAudio(meeting)) try { File.Delete(Paths.AudioPath(meeting)); } catch (IOException) { }
        });
    }

    void PurgeExpiredTrash()
    {
        foreach (var meeting in Db.ExpiredTrash(30))
        {
            try { Db.DeleteMeeting(meeting.Id!.Value); } catch (Microsoft.Data.Sqlite.SqliteException) { continue; }
            if (Paths.HasAudio(meeting)) try { File.Delete(Paths.AudioPath(meeting)); } catch (IOException) { }
        }
    }

    public void CancelJobs(long id) => Runner.Cancel(id);

    public void Retry(long id)
    {
        if (Db.LatestJob(id) is not { Status: JobStatus.Failed or JobStatus.Cancelled } job) return;
        Perform(id, () => Db.Enqueue(id, job.Kind, job.Payload));
        Runner.Wake();
    }

    /// <summary>
    /// 重新轉錄、重新整理、檢查聽錯的字、復原上一版之前再擋一次：選單的停用狀態可能沒有更新，
    /// 不擋的話會在處理中重複排工作（多花整理工具的額度），或替垃圾桶裡的會議跑
    /// </summary>
    bool ReadyForAction(long id)
    {
        if (MeetingDetail.Load(Db, id) is not { } detail) return false;
        if (detail.Meeting.DeletedAt is not null) { Notice?.Invoke(L.T("垃圾桶裡的會議要先還原才能操作")); return false; }
        if (Recorder.IsRecording && Recorder.MeetingId == id) { Notice?.Invoke(L.T("這場會議還在錄音")); return false; }
        if (Status(detail).IsActive) { Notice?.Invoke(L.T("這場會議還在處理中，完成後再試一次")); return false; }
        return true;
    }

    public void Retranscribe(long id)
    {
        if (!ReadyForAction(id)) return;
        if (Db.Meeting(id) is not { } meeting || !Paths.HasAudio(meeting))
        {
            Notice?.Invoke(L.T("這場會議沒有錄音，沒辦法重新轉錄"));
            return;
        }
        Perform(id, () => Db.Enqueue(id, JobKind.Transcribe));
        Runner.Wake();
    }

    /// <summary>「更多 → 重新辨識說話者…」（M§23.4）：能做的話回傳 true，畫面打開選人數的視窗（R22.4）</summary>
    public bool RequestRediarize(long id)
    {
        if (!ReadyForAction(id)) return false;
        if (RediarizeProblem(id) is { } problem)
        {
            Notice?.Invoke(problem);
            return false;
        }
        return true;
    }

    string? RediarizeProblem(long id)
    {
        if (MeetingDetail.Load(Db, id) is not { } detail) return null;
        if (!Paths.HasAudio(detail.Meeting)) return L.T("這場會議沒有錄音，沒辦法重新辨識說話者");
        if (!detail.Segments.Any(s => s.Kind == "final")) return L.T("還沒有正式逐字稿，轉錄完成後再試一次");
        if (detail.Segments.Any(s => s.Channel is not null)) return L.T("這場會議是分聲道轉錄的，請用「重新轉錄」");
        if (Db.HasActiveJob(id, JobKind.Rediarize)) return L.T("已經在重新辨識說話者了");
        if (!Models.IsInstalled(ModelStore.Model.Diarization))
        {
            Models.AutoStart(ModelStore.Model.Diarization);
            return L.T("說話者辨識的模型還在下載，下載好後再試一次");
        }
        return null;
    }

    /// <summary>選人數的視窗按「開始」：記住人數（R22.3），排重新辨識的工作</summary>
    public void Rediarize(long id, int? minSpeakers)
    {
        Perform(id, () =>
        {
            Db.SetMinSpeakers(id, minSpeakers);
            Db.Enqueue(id, JobKind.Rediarize);
        });
        Runner.Wake();
    }

    // MARK: 說話者的修正（M§24、R23）

    /// <summary>轉錄或重新辨識說話者時不能改：完成時會整份寫回段落的說話者</summary>
    string? SpeakerEditProblem(long id, MeetingDetail detail)
    {
        if (detail.Meeting.DeletedAt is not null) return L.T("垃圾桶裡的會議要先還原才能操作");
        if (Status(detail).IsActive || Db.HasActiveJob(id, JobKind.Rediarize)) return L.T("正在處理這場會議，完成後才能改說話者");
        return null;
    }

    /// <summary>「合併到…」（R23.1）：from 說的話全部算 into 的；會議紀錄裡的名字跟著換</summary>
    public void MergeSpeaker(long id, int from, int into)
    {
        if (MeetingDetail.Load(Db, id) is not { } detail) return;
        if (SpeakerEditProblem(id, detail) is { } problem) { Notice?.Invoke(problem); return; }
        var fromName = detail.SpeakerName(from)!;
        var intoName = detail.SpeakerName(into)!;
        var source = detail.SpeakerSources.GetValueOrDefault(into);
        Perform(id, () =>
        {
            if (Db.MergeSpeakers(id, from, into, L.F("把「%@」合併到「%@」", fromName, intoName)) is null) return;
            // 使用者取的名字：用合併後的聲紋重新學（自動帶入的名字不學，M§21.4）
            if (Settings.Voiceprints && source is "manual" or "suggestion") Db.LearnVoiceprint(id, into, intoName, source);
            Notice?.Invoke(L.F("已把「%@」合併到「%@」。要復原的話，從「更多 → 復原說話者的調整」。", fromName, intoName));
        });
    }

    /// <summary>「這句是誰說的」（R23.2）：to 是 null 時新增一位說話者</summary>
    public void ReassignSegments(long id, IReadOnlyList<long> ids, int? to)
    {
        if (ids.Count == 0 || MeetingDetail.Load(Db, id) is not { } detail) return;
        if (SpeakerEditProblem(id, detail) is { } problem) { Notice?.Invoke(problem); return; }
        var target = to is int t ? detail.SpeakerName(t)! : L.T("新的說話者");
        var label = ids.Count == 1 ? L.F("把一句改成「%@」說的", target) : L.F("把 %lld 句改成「%@」說的", ids.Count, target);
        Perform(id, () => Db.ReassignSegments(id, ids, to, label));
    }

    /// <summary>「更多 → 復原說話者的調整」：只復原最近一次合併或改句子</summary>
    public void UndoSpeakerEdit(long id)
    {
        if (MeetingDetail.Load(Db, id) is not { } detail) return;
        if (SpeakerEditProblem(id, detail) is { } problem) { Notice?.Invoke(problem); return; }
        if (Db.SpeakerUndoLabel(id) is not { } label)
        {
            Notice?.Invoke(L.T("沒有可以復原的說話者調整（改名、重新辨識說話者之後就不能復原之前的調整）"));
            return;
        }
        Perform(id, () => { if (Db.UndoSpeakerEdit(id)) Notice?.Invoke(L.F("已復原：%@", label)); });
    }

    public void DismissSpeakerHint(long id) => Perform(id, () => Db.DismissSpeakerHint(id));

    /// <summary>source：manual（使用者改名）或 suggestion（套用 AI 猜的名字，M§21.5）</summary>
    public void RenameSpeaker(long id, int idx, string name, string source = "manual")
    {
        if (Db.Meeting(id) is not { } meeting) return;
        var trimmed = name.Trim();
        // 清空就回到預設名稱，用會議語言（會帶進會議紀錄和匯出），不是介面語言
        var fallback = meeting.MeetingLanguage.Text($"說話者 {idx}", $"Speaker {idx}", $"話者 {idx}");  // l10n-skip
        var newName = trimmed.Length == 0 ? fallback : trimmed;
        var old = Db.Speakers(id).FirstOrDefault(s => s.Idx == idx)?.Name;
        Perform(id, () =>
        {
            Db.RenameSpeaker(id, idx, newName);
            // 記住這個人的聲音（M§21.4）；改回預設名稱就忘掉這場會議的這一份
            if (Settings.Voiceprints) Db.LearnVoiceprint(id, idx, newName, source);
            Db.ClearSpeakerSuggestion(id, idx);
            // 會議紀錄裡的舊名字換成新名字（M§21.6）
            if (old is not null) Db.ReplaceSpeakerNameInNotes(id, old, newName);
        });
    }

    /// <summary>「不是他」：聲紋自動帶入的名字不對，回到預設名稱（M§21.4）；會議紀錄裡的名字也換回去</summary>
    public void RejectVoiceprintMatch(long id, int idx)
    {
        if (Db.Meeting(id) is not { } meeting) return;
        var fallback = meeting.MeetingLanguage.Text($"說話者 {idx}", $"Speaker {idx}", $"話者 {idx}");  // l10n-skip
        var old = Db.Speakers(id).FirstOrDefault(s => s.Idx == idx)?.Name;
        Perform(id, () =>
        {
            Db.RejectVoiceprintMatch(id, idx, fallback);
            if (old is not null) Db.ReplaceSpeakerNameInNotes(id, old, fallback);
        });
    }

    public void ApplySpeakerSuggestion(long id, int idx)
    {
        if (Db.Speakers(id).FirstOrDefault(s => s.Idx == idx) is { NameSource: "default", SuggestedName: { } name })
            RenameSpeaker(id, idx, name, "suggestion");
    }

    public void ApplyAllSpeakerSuggestions(long id)
    {
        foreach (var s in Db.Speakers(id).Where(s => s.NameSource == "default" && s.SuggestedName is not null).OrderBy(s => s.Idx))
            ApplySpeakerSuggestion(id, s.Idx);
    }

    public void DismissSpeakerSuggestion(long id, int idx) => Perform(id, () => Db.ClearSpeakerSuggestion(id, idx));

    // MARK: 整理（R6）

    public void Resummarize(long id)
    {
        if (!ReadyForAction(id) || Db.Meeting(id) is not { } meeting) return;
        if (Db.Segments(id).Count == 0 && meeting.UserNotes.Length == 0)
        {
            Notice?.Invoke(L.T("還沒有逐字稿或筆記可以整理"));
            return;
        }
        if (Settings.Provider == LLMProvider.None)
        {
            Error?.Invoke(L.T("整理工具設定為「不整理」，請先到設定改成 Claude、Codex 或 API"));
            return;
        }
        Perform(id, () => Db.Enqueue(id, JobKind.Summarize));
        Runner.Wake();
    }

    public void UndoNotes(long id)
    {
        if (!ReadyForAction(id)) return;
        Perform(id, () => { if (!Db.UndoNotes(id)) Notice?.Invoke(L.T("沒有上一版可以復原")); });
    }

    /// <summary>編輯器按「完成」或關掉時存檔（R6.6）。編輯期間會議紀錄被別的工作改過的話提醒一下：舊的那一版還在版本紀錄裡</summary>
    public void FinishNotesEdit(MeetingNotes draft, MeetingNotes? baseline, long meetingId, bool automatically = false)
    {
        if (draft.Equals(baseline)) return;
        if (baseline is not null && Db.Meeting(meetingId)?.DecodedNotes is { } current && !current.Equals(baseline))
            Notice?.Invoke(L.T("編輯期間會議紀錄有更新，已用你改的版本存檔；從「更多 → 復原上一版會議紀錄」可以找回更新的內容"));
        else if (automatically)
            Notice?.Invoke(L.T("已儲存剛才編輯的會議紀錄"));
        SaveEditedNotes(draft, meetingId);
    }

    /// <summary>meetingId 要明確給：編輯器關掉時（切到別場會議、錄音停止後自動跳到那場），選的會議可能已經是別場</summary>
    public void SaveEditedNotes(MeetingNotes notes, long meetingId)
    {
        if (Db.Meeting(meetingId)?.DecodedNotes is not { } current || notes.Equals(current)) return;
        Perform(meetingId, () => Db.SaveNotes(meetingId, notes, null, L.T("手動編輯")));
    }

    /// <summary>回傳是否送出成功；失敗時畫面上的回答要留著</summary>
    public bool SubmitReview(long id, IReadOnlyDictionary<int, string> answers, IReadOnlySet<int> dismissed)
    {
        if (Db.Meeting(id)?.DecodedNotes is not { } notes || !ReadyForAction(id)) return false;
        var answered = answers.Select(p => (Index: p.Key, Text: p.Value.Trim()))
            .Where(a => a.Text.Length > 0 && !dismissed.Contains(a.Index) && a.Index >= 0 && a.Index < notes.ReviewItems.Count)
            .OrderBy(a => a.Index)
            .Select(a => new ReviewAnswerJson(notes.ReviewItems[a.Index].Time, notes.ReviewItems[a.Index].Question, a.Text)).ToList();
        if (answered.Count > 0 && Settings.Provider == LLMProvider.None)
        {
            Error?.Invoke(L.T("整理工具設定為「不整理」，沒辦法依回答修正。請先到設定改成 Claude、Codex 或 API"));
            return false;
        }
        Perform(id, () =>
        {
            if (dismissed.Count > 0) Db.DismissReviewItems(id, dismissed);
            if (answered.Count > 0) Db.Enqueue(id, JobKind.Revise, JobRunner.Encode(new RevisePayload(answered)));
        });
        Runner.Wake();
        return true;
    }

    public void UpdateSegment(long meetingId, long segmentId, string text) => Perform(meetingId, () => Db.UpdateSegmentText(segmentId, text));

    // MARK: 逐字稿校正（M§17.4）

    /// <summary>手動檢查可能聽錯的字（「更多」選單）</summary>
    public void CheckTranscript(long id)
    {
        if (!ReadyForAction(id)) return;
        if (Db.Segments(id).Count == 0) { Notice?.Invoke(L.T("還沒有逐字稿可以檢查")); return; }
        if (Settings.Provider == LLMProvider.None)
        {
            Error?.Invoke(L.T("整理工具設定為「不整理」，請先到設定改成 Claude、Codex 或 API"));
            return;
        }
        Perform(id, () => Db.Enqueue(id, JobKind.Correct));
        Runner.Wake();
    }

    public void AcceptCorrection(long meetingId, long id) =>
        Perform(meetingId, () => { if (!Db.AcceptCorrection(id)) Notice?.Invoke(L.T("這一段已經改過，找不到建議要改的文字")); });

    public void RejectCorrection(long meetingId, long id) => Perform(meetingId, () => Db.RejectCorrections([id]));

    public void RevertCorrection(long meetingId, long id) =>
        Perform(meetingId, () => { if (!Db.RevertCorrection(id)) Notice?.Invoke(L.T("這一段已經改過，沒辦法還原")); });

    /// <summary>同一個詞的每一處一起接受或忽略（M§21.11、R20.2）</summary>
    public void AcceptCorrections(long meetingId, IReadOnlyList<long> ids) => Perform(meetingId, () =>
    {
        var failed = Db.AcceptCorrections(ids);
        if (failed > 0) Notice?.Invoke(L.F("有 %lld 則建議的段落已經改過，沒有套用", failed));
    });

    public void RejectCorrections(long meetingId, IReadOnlyList<long> ids) => Perform(meetingId, () => Db.RejectCorrections(ids));

    public void AcceptAllCorrections(long meetingId) =>
        AcceptCorrections(meetingId, Db.Corrections(meetingId).Where(c => c.Status == "pending" && c.Id is not null).Select(c => c.Id!.Value).ToList());

    public void RejectAllCorrections(long meetingId) =>
        RejectCorrections(meetingId, Db.Corrections(meetingId).Where(c => c.Status == "pending" && c.Id is not null).Select(c => c.Id!.Value).ToList());

    // MARK: 編輯中的草稿（M§21.15、R20.6）

    public Dictionary<string, string> Drafts(long meetingId) => Db.Drafts(meetingId);
    public (string Key, string Text)? LatestDraft(long meetingId, string prefix) => Db.LatestDraft(meetingId, prefix);

    public void SaveDraft(long meetingId, string key, string text)
    {
        try { Db.SetDraft(meetingId, key, text); } catch (Microsoft.Data.Sqlite.SqliteException e) { Log.Error($"save draft failed: {e.SqliteErrorCode}"); }
    }

    public void DeleteDrafts(long meetingId, string prefix)
    {
        try { Db.DeleteDrafts(meetingId, prefix); } catch (Microsoft.Data.Sqlite.SqliteException) { }
    }

    // MARK: 筆記、重點、取代、詞彙表、範本、認識的人

    /// <summary>筆記編輯器存檔（M§20.4）：只更新筆記，不整份重新讀取</summary>
    public void SaveUserNotes(long id, string text)
    {
        try { Db.UpdateUserNotes(id, text); } catch (Microsoft.Data.Sqlite.SqliteException e) { Log.Error($"save user notes failed: {e.SqliteErrorCode}"); }
    }

    public void AddBookmark(long meetingId, double time, string label = "") => Perform(meetingId, () => Db.AddBookmark(meetingId, time, label));
    public void DeleteBookmark(long meetingId, long id) => Perform(meetingId, () => Db.DeleteBookmark(id));

    /// <summary>錄音中標記重點（全域快捷鍵、錄音畫面的按鈕）：回傳有沒有標</summary>
    public bool BookmarkRecording()
    {
        if (!Recorder.IsRecording || Recorder.MeetingId is not long id) return false;
        AddBookmark(id, Recorder.Elapsed);
        return true;
    }

    public ReplaceResult? Replace(long id, string find, string replacement, bool addToGlossary)
    {
        if (find.Length == 0 || find == replacement) return null;
        ReplaceResult? result = null;
        Perform(id, () =>
        {
            result = Db.Replace(id, find, replacement);
            if (addToGlossary) Db.AddTerm(replacement);
        });
        return result;
    }

    public void AddTerm(string term) => Perform(null, () => Db.AddTerm(term));
    public void DismissGlossarySuggestion(string term) => Perform(null, () => Db.DismissGlossarySuggestion(term));
    public void DeleteTerm(long id) => Perform(null, () => Db.DeleteTerm(id));
    public void SaveTemplate(TemplateRecord template) => Perform(null, () => Db.SaveTemplate(template));
    public void DeleteTemplate(string id) => Perform(null, () => Db.DeleteTemplate(id));
    public void RenamePerson(long id, string name) => Perform(null, () => Db.RenamePerson(id, name));
    public void DeletePerson(long id) => Perform(null, () => Db.DeletePerson(id));
    public void DeleteAllPeople() => Perform(null, Db.DeleteAllPeople);

    // MARK: 匯出（R7）

    public sealed class ExportException(string message) : Exception(message);

    /// <summary>依會議 id 從資料庫讀要匯出的資料；會議已經被永久刪除時丟出錯誤（M§20.9）</summary>
    public ExportBundle Bundle(long meetingId)
    {
        if (Db.Meeting(meetingId) is not { } meeting) throw new ExportException(L.T("這場會議已經被刪除，沒辦法匯出"));
        return new ExportBundle(meeting, Db.Segments(meetingId), Db.Speakers(meetingId).ToDictionary(s => s.Idx, s => s.Name),
            Db.Bookmarks(meetingId), meeting.DecodedNotes);
    }

    /// <summary>寫出檔案；文字格式、封包在背景寫，長會議不卡住畫面（M§21.13）</summary>
    public async Task Export(ExportItem item, string format, bool times, bool speakers, long meetingId, string path, CancellationToken cancel = default)
    {
        var b = Bundle(meetingId);
        switch (item)
        {
            case ExportItem.Notes when format == "pdf": await Platform().NotesPdf(b, path, cancel); break;
            case ExportItem.Notes when format == "docx": await Platform().NotesDocx(b, path, cancel); break;
            case ExportItem.Notes: await Task.Run(() => File.WriteAllText(path, NotesExporter.Markdown(b)), cancel); break;
            case ExportItem.Transcript when format == "docx": await Platform().TranscriptDocx(b, times, speakers, path, cancel); break;
            case ExportItem.Transcript: await Task.Run(() => File.WriteAllText(path, TranscriptExporter.Text(b, format, times, speakers)), cancel); break;
            case ExportItem.Audio:
                if (!Paths.HasAudio(b.Meeting)) throw new ExportException(L.T("這場會議沒有錄音檔"));
                await Platform().Audio(Paths.AudioPath(b.Meeting), format, path, cancel);
                break;
            case ExportItem.Package: await Task.Run(() => MeetingPackage.Export(b, path, Paths, appVersion), cancel); break;
        }
    }

    IExportPlatform Platform() => exporter ?? throw new ExportException(L.T("這個格式在這裡不能匯出"));

    public static string SuggestedFilename(ExportItem item, string format, Meeting? meeting)
    {
        if (meeting is null) return L.F("會議.%@", format);
        // Windows 的檔名不能有 \ / : * ? " < > |，換成全形
        var name = $"{meeting.MeetingDate} {meeting.DisplayTitle}";
        foreach (var (from, to) in new[] { ('/', '／'), ('\\', '＼'), (':', '：'), ('*', '＊'), ('?', '？'), ('"', '＂'), ('<', '＜'), ('>', '＞'), ('|', '｜') })
            name = name.Replace(from, to);
        return item == ExportItem.Transcript ? L.F("%@（逐字稿）.%@", name, format) : $"{name}.{format}";
    }

    // MARK: 搜尋（R9.1）

    public List<SearchHit> Search(string query) => query.Trim().Length == 0 ? [] : Db.Search(query);
}

/// <summary>名稱排序跟 Finder 一樣：數字照大小（「第 2 週」排在「第 10 週」前面）</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (x is null || y is null) return string.Compare(x, y, StringComparison.Ordinal);
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                var si = i; while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                var sj = j; while (j < y.Length && char.IsAsciiDigit(y[j])) j++;
                var a = x[si..i].TrimStart('0');
                var b = y[sj..j].TrimStart('0');
                var c = a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
                if (c != 0) return c;
                continue;
            }
            var d = string.Compare(x[i].ToString(), y[j].ToString(), System.Globalization.CultureInfo.CurrentCulture, System.Globalization.CompareOptions.IgnoreCase);
            if (d != 0) return d;
            i++; j++;
        }
        return (x.Length - i).CompareTo(y.Length - j);
    }
}
