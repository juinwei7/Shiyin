using Shiyin.Data;
using Shiyin.Localization;
using Shiyin.Model;
using Shiyin.Text;

namespace Shiyin.Services;

/// <summary>編輯會議（R107、M§81、design-windows §11.40）：能不能編輯、套用、還原、30 天後清掉編輯前的錄音（同 Mac 的 MeetingEditing.swift）</summary>
public sealed partial class AppController
{
    /// <summary>編輯會議的錄音（design-windows §11.40）：Windows 是 Media Foundation（Platform）；沒給（測試）只接 WAV</summary>
    public IAudioEditor AudioEditor { get; init; } = new AudioEdit.WavOnly();

    /// <summary>正在套用編輯、分成兩場、還原的那場（接錄音要幾秒，會議頁上方顯示「正在套用編輯…」）；同時只做一場</summary>
    public long? ApplyingEdit { get; private set; }

    /// <summary>錄音的長度（秒）；讀不到是 null</summary>
    public double? AudioSeconds(string path)
    {
        try
        {
            using var reader = AudioEditor.Open(path);
            return reader.Seconds;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    /// <summary>編輯畫面的波形（M§81.5）：每 step 秒一個峰值。背景執行</summary>
    public Task<float[]> Peaks(string path, double step, CancellationToken cancel) => Task.Run(() =>
    {
        using var stream = decoder.Open(path);
        return AudioEdit.Peaks(stream, step, cancel);
    }, cancel);

    /// <summary>不能編輯的原因（R107.1）；null 是可以，空字串是會議不見了（不用說）</summary>
    public string? EditProblem(long id)
    {
        if (Db.Meeting(id) is not { } meeting) return "";
        if (Recorder.IsRecording && Recorder.MeetingId == id) return L.T("這場會議還在錄音，請先停止錄音再編輯");
        if (meeting.DeletedAt is not null) return L.T("垃圾桶裡的會議要先還原才能操作");
        if (ApplyingEdit is not null || Db.HasActiveJobs(id) || Runner.RunningJob(id) is not null) return L.T("正在處理這場會議，完成後才能編輯");
        return null;
    }

    /// <summary>「編輯會議…」（右鍵、「⋯」、更多）：可以的話回傳 true，畫面打開編輯畫面；不行就說原因</summary>
    public bool RequestEditMeeting(long id)
    {
        if (EditProblem(id) is not { } problem) return true;
        if (problem.Length > 0) Notice?.Invoke(problem);
        return false;
    }

    /// <summary>
    /// 合併時可以選的會議（M§81.5）：不在垃圾桶、不是這場、不在錄音或處理中；同一天、前後三天的在前面（照相差的天數），
    /// 其他照列表的順序（日期新的在前）
    /// </summary>
    public (List<MeetingRow> Near, List<MeetingRow> Older) MergeCandidates(long id)
    {
        if (Db.Meeting(id) is not { } base_ || DateText.Date(base_.MeetingDate) is not { } day) return ([], []);
        var rows = Db.MeetingRows().Where(r => r.Id != id && !(Recorder.IsRecording && Recorder.MeetingId == r.Id) && !Db.HasActiveJobs(r.Id)).ToList();
        double Distance(MeetingRow r) => DateText.Date(r.Meeting.MeetingDate) is { } d ? Math.Abs(d.DayNumber - day.DayNumber) : double.PositiveInfinity;
        return (rows.Where(r => Distance(r) <= 3).OrderBy(Distance).ToList(), rows.Where(r => Distance(r) > 3).ToList());
    }

    /// <summary>
    /// 套用（R107.7）：背景接好錄音（分成兩場時前後各一個檔），一個交易改資料庫，再換檔案、複製參考文件、排重新整理。
    /// cut 是新時間軸上的切點
    /// </summary>
    public async Task ApplyMeetingEdit(long id, EditPlan plan, double? cut)
    {
        if (EditProblem(id) is { } problem)
        {
            if (problem.Length > 0) Notice?.Invoke(problem);
            return;
        }
        if (Db.Meeting(id) is not { } meeting) return;
        var sources = new Dictionary<long, string>();
        var titles = new Dictionary<long, string>();
        foreach (var meetingId in AudioEdit.MeetingsIn(plan))
        {
            if ((meetingId == id ? meeting : Db.Meeting(meetingId)) is not { } m) continue;
            if (Paths.HasAudio(m)) sources[meetingId] = Paths.AudioPath(m);
            titles[meetingId] = m.DisplayTitle;
        }
        var splitTitle = L.F("%@（2）", meeting.DisplayTitle);
        var parts = cut is double c ? plan.Split(c) : null;
        ApplyingEdit = id;
        Changed?.Invoke(id);
        List<string> temps = [];
        try
        {
            async Task<string?> Compose(EditPlan p)
            {
                var dir = Paths.MakeTempDirectory();
                temps.Add(dir);
                return await AudioEditor.Compose(p, sources, dir, CancellationToken.None);
            }
            var head = await Compose(parts?.Head ?? plan);
            var tail = parts is { } split ? await Compose(split.Tail) : null;
            if (Db.ApplyEdit(id, plan, parts is null ? null : cut, splitTitle, head is null ? null : PathText.Extension(head)) is not { } outcome) return;
            ReleasingAudio?.Invoke(id);
            await ReplaceAudio(meeting, head, outcome.FirstEdit);
            if (tail is not null && outcome.Created is { } created) await MoveWithRetry(tail, Paths.AudioPath(created));
            foreach (var (from, to) in outcome.DocumentCopies)
            {
                try
                {
                    var target = Path.Combine([Paths.Documents, .. to.Split('/')]);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(Path.Combine([Paths.Documents, .. from.Split('/')]), target, overwrite: true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Error($"copy document failed: {e.GetType().Name}"); }
            }
            var others = titles.Where(p => p.Key != id).Select(p => p.Value).ToList();
            var message = outcome.Created is { } split2 ? L.F("已分成兩場：「%@」", split2.DisplayTitle)
                : others.Count > 0 ? L.F("已合併「%@」，那場移到垃圾桶", others[0])
                : L.F("已編輯「%@」", meeting.DisplayTitle);
            if (Settings.SummarizerReady)
            {
                Db.Enqueue(id, JobKind.Summarize);
                if (outcome.Created?.Id is long createdId) Db.Enqueue(createdId, JobKind.Summarize);
                Runner.Wake();
                message += L.T("。會議紀錄正在重新整理");
            }
            else message += L.T("。會議紀錄要重新整理，設定好整理工具後按「重新整理」");
            AudioReplaced?.Invoke(id);
            Changed?.Invoke(null);
            Notice?.Invoke(message);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Error($"apply edit failed: {e.GetType().Name}");
            Error?.Invoke(L.F("編輯會議失敗：%@", e.Message));
            Changed?.Invoke(null);
        }
        finally
        {
            ApplyingEdit = null;
            foreach (var dir in temps) try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
            Changed?.Invoke(id);
        }
    }

    /// <summary>換上新的錄音：第一次編輯時原本的錄音搬到 audio\edits\（還原用），之後的編輯舊的直接刪</summary>
    async Task ReplaceAudio(Meeting meeting, string? replacement, bool firstEdit)
    {
        if (Paths.HasAudio(meeting))
        {
            var original = Paths.AudioPath(meeting);
            if (firstEdit)
            {
                Directory.CreateDirectory(Paths.EditsAudio);
                await MoveWithRetry(original, Path.Combine(Paths.EditsAudio, $"{meeting.Id}.{PathText.Extension(meeting.AudioFile)}"));
            }
            else await DeleteWithRetry(original);
        }
        if (replacement is not null) await MoveWithRetry(replacement, Path.Combine(Paths.Audio, $"{meeting.Id}.{PathText.Extension(replacement)}"));
    }

    /// <summary>刪檔案；播放器剛放掉的檔案可能還被系統開著一下子（同 MoveWithRetry）</summary>
    static async Task DeleteWithRetry(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && attempt < 10) { await Task.Delay(200); }
        }
    }

    /// <summary>「還原成編輯前的樣子…」：可以的話回傳 true（畫面打開確認）；沒編輯過、不能動時說明（R107.8）</summary>
    public bool RequestRestoreEdit(long id)
    {
        if (Db.EditRecord(id) is null)
        {
            Notice?.Invoke(L.T("這場會議沒有編輯過。編輯會議後 30 天內可以從這裡還原。"));
            return false;
        }
        if (EditProblem(id) is { } problem)
        {
            if (problem.Length > 0) Notice?.Invoke(problem);
            return false;
        }
        return true;
    }

    /// <summary>還原確認彈窗的說明：原本多長、編輯後的修改會不見、分出去的會議移到垃圾桶、合併進來的從垃圾桶拿出來</summary>
    public string RestoreEditMessage(long id)
    {
        if (Db.EditRecord(id) is not { } record || JsonRead.Parse(record.Snapshot) is not { ValueKind: System.Text.Json.JsonValueKind.Object } snapshot) return "";
        var lines = new List<string>
        {
            L.F("會議會回到編輯前的樣子（原本 %@），錄音、逐字稿、標記重點、會議紀錄都換回來。", TimeFormat.String(snapshot.Long("durationSeconds") ?? 0)),
            L.T("編輯之後對逐字稿、說話者、筆記的修改會不見。"),
        };
        foreach (var other in record.SplitIds)
            if (Db.Meeting(other) is { DeletedAt: null } m) lines.Add(L.F("分出去的「%@」會移到垃圾桶。", m.DisplayTitle));
        foreach (var other in record.MergedIds)
            if (Db.Meeting(other) is { DeletedAt: not null } m) lines.Add(L.F("合併進來的「%@」會從垃圾桶還原。", m.DisplayTitle));
        return string.Join("\n", lines.Select(l => "・" + l));  // l10n-skip
    }

    /// <summary>還原（確認後）：資料一個交易換回去，錄音從 audio\edits\ 搬回來、刪掉現在的，編輯後加進來的參考文件刪掉</summary>
    public async Task RestoreMeetingEdit(long id)
    {
        if (EditProblem(id) is { } problem)
        {
            if (problem.Length > 0) Notice?.Invoke(problem);
            return;
        }
        ApplyingEdit = id;
        Changed?.Invoke(id);
        try
        {
            ReleasingAudio?.Invoke(id);
            if (Db.RestoreEdit(id) is not { } outcome) return;
            if (outcome.CurrentAudio.Length > 0)
            {
                var current = Paths.AudioRelative(outcome.CurrentAudio);
                if (File.Exists(current)) await DeleteWithRetry(current);
            }
            if (outcome.EditsAudio.Length > 0) await MoveWithRetry(Paths.AudioRelative(outcome.EditsAudio), Paths.AudioRelative(outcome.RestoredAudio));
            outcome.RemovedDocuments.ForEach(Db.RemoveDocumentFile);
            var message = L.T("已還原成編輯前的樣子");
            if (outcome.Trashed.Count > 0) message += L.T("，分出去的會議移到垃圾桶");
            if (outcome.Untrashed.Count > 0) message += L.T("，合併進來的會議已從垃圾桶拿出來");
            AudioReplaced?.Invoke(id);
            Changed?.Invoke(null);
            Notice?.Invoke(message);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Error($"restore edit failed: {e.GetType().Name}");
            Error?.Invoke(L.F("還原失敗：%@", e.Message));
            Changed?.Invoke(null);
        }
        finally
        {
            ApplyingEdit = null;
            Changed?.Invoke(id);
        }
    }

    /// <summary>最後一次編輯超過 30 天：刪掉編輯前的錄音和快照（R107.8）；audio\edits\ 裡沒人用的檔（會議永久刪除了）一起刪</summary>
    public void PurgeExpiredEdits()
    {
        foreach (var record in Db.ExpiredEdits(MeetingEditRecord.KeepDays))
        {
            if (record.AudioFile.Length > 0) try { File.Delete(Paths.AudioRelative(record.AudioFile)); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            Db.DeleteEditRecord(record.MeetingId);
        }
        if (!Directory.Exists(Paths.EditsAudio)) return;
        var keep = Db.EditAudioFiles();
        foreach (var file in Directory.EnumerateFiles(Paths.EditsAudio))
        {
            if (keep.Contains("edits/" + Path.GetFileName(file))) continue;
            try { File.Delete(file); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
