using System.Globalization;
using Shiyin.Localization;
using Shiyin.Model;
using Shiyin.Notes;
using Shiyin.Text;

namespace Shiyin.Data;

/// <summary>列表用：會議加上推算狀態所需的資料</summary>
public sealed record MeetingRow(Meeting Meeting, Job? LatestJob, int ReviewCount)
{
    public long Id => Meeting.Id ?? 0;
}

public sealed record ReplaceResult(int SegmentsChanged, bool NotesChanged);

/// <summary>會議紀錄怎麼來的，決定草稿狀態和要不要留版本（M§21.3）</summary>
public enum NotesSource
{
    /// <summary>手動編輯、取代、略過待確認、匯入：草稿被改過就標成 2</summary>
    Edit,
    /// <summary>用即時逐字稿整理的草稿：notes_draft = 1，不留版本</summary>
    Draft,
    /// <summary>正式整理：取代沒改過的草稿不留版本；取代改過的草稿留一版（原因「草稿（改過）」）</summary>
    Generated,
    /// <summary>校稿：在草稿上校稿，結果仍然是草稿（2）</summary>
    Revised,
}

public sealed partial class AppDatabase
{
    static string AuxiliaryKinds => string.Join(", ", JobKinds.All.Where(k => k.IsAuxiliary()).Select(k => $"'{k.Code()}'"));

    // MARK: 會議

    public List<MeetingRow> MeetingRows(bool trash = false) => Read(db =>
    {
        var meetings = db.Query($"SELECT * FROM meetings WHERE deleted_at IS {(trash ? "NOT " : "")}NULL ORDER BY meeting_date DESC, id DESC")
            .Select(ToMeeting).ToList();
        // 背景的輔助工作不算會議的狀態（M§21.2、§23.3）
        var latest = db.Query($"SELECT * FROM jobs WHERE id IN (SELECT MAX(id) FROM jobs WHERE kind NOT IN ({AuxiliaryKinds}) GROUP BY meeting_id)")
            .Select(ToJob).ToDictionary(j => j.MeetingId);
        return meetings.Select(m => new MeetingRow(m, latest.GetValueOrDefault(m.Id ?? 0), m.DecodedNotes?.ReviewItems.Count ?? 0)).ToList();
    });

    public Meeting? Meeting(long id) => Read(db => FetchMeeting(db, id));

    public List<Segment> Segments(long meetingId, string kind = "final") =>
        Read(db => db.Query("SELECT * FROM segments WHERE meeting_id = ? AND kind = ? ORDER BY seq", meetingId, kind).Select(ToSegment).ToList());

    /// <summary>只讀這幾段（改一段文字、接受校正之後，M§21.12）</summary>
    public List<Segment> Segments(IReadOnlyList<long> ids) => ids.Count == 0 ? [] : Read(db =>
        db.Query($"SELECT * FROM segments WHERE id IN ({string.Join(", ", ids.Select(_ => "?"))})", ids.Cast<object?>().ToArray()).Select(ToSegment).ToList());

    public int VersionCount(long meetingId) => Read(db => (int)(long)db.Scalar("SELECT COUNT(*) FROM notes_versions WHERE meeting_id = ?", meetingId)!);

    public Job? LatestJob(long meetingId) => Read(db =>
        db.QueryOne($"SELECT * FROM jobs WHERE meeting_id = ? AND kind NOT IN ({AuxiliaryKinds}) ORDER BY id DESC LIMIT 1", meetingId) is { } r ? ToJob(r) : null);

    /// <summary>建立會議並給定錄音檔名（需要先拿到 id 才能命名，所以在同一個交易裡做兩步）</summary>
    public Meeting InsertMeeting(Meeting meeting, string? audioExtension) => Write(db =>
    {
        var m = meeting.Clone();
        m.Id = InsertMeetingRow(db, m);
        if (audioExtension is not null) m.AudioFile = $"{m.Id}.{audioExtension}";
        UpdateMeetingRow(db, m);
        Index(db, m.Id!.Value, SearchKind.Title, m.DisplayTitle);
        return m;
    });

    public void UpdateMeeting(Meeting meeting) => Write(db =>
    {
        var m = meeting.Clone();
        m.UpdatedAt = Clock.Now();
        UpdateMeetingRow(db, m);
        Index(db, m.Id!.Value, SearchKind.Title, m.DisplayTitle);
        Index(db, m.Id!.Value, SearchKind.UserNotes, m.UserNotes);
    });

    /// <summary>資料夾（R9.2）：資料夾就是 meetings.folder 的值，空字串表示不在任何資料夾</summary>
    public void SetFolder(IReadOnlyList<long> ids, string folder)
    {
        if (ids.Count == 0) return;
        Write(db => { foreach (var id in ids) db.Execute("UPDATE meetings SET folder = ?, updated_at = ? WHERE id = ?", folder, Clock.Now(), id); });
    }

    /// <summary>改名或刪除（newName 為空字串）一個資料夾，垃圾桶裡的會議也一起改</summary>
    public void RenameFolder(string old, string newName) =>
        Write(db => db.Execute("UPDATE meetings SET folder = ?, updated_at = ? WHERE folder = ?", newName, Clock.Now(), old));

    /// <summary>錄音中途當掉、連錄音檔都沒有的會議（M§20.6）</summary>
    public List<long> EmptyRecordingMeetings() => Read(db =>
        db.Query("SELECT id FROM meetings WHERE source = 'recording' AND audio_file = '' AND deleted_at IS NULL").Select(r => r.Long("id")).ToList());

    /// <summary>永久刪除</summary>
    public void DeleteMeeting(long id) => Write(db =>
    {
        // 段落會跟著會議刪掉（ON DELETE CASCADE），所以先刪索引
        UnindexSegments(db, id);
        UnindexMeeting(db, id);
        db.Execute("DELETE FROM meetings WHERE id = ?", id);
    });

    /// <summary>移到垃圾桶或還原（R9.3）</summary>
    public void SetTrashed(long id, bool trashed) =>
        Write(db => db.Execute("UPDATE meetings SET deleted_at = ? WHERE id = ?", trashed ? Clock.Now() : null, id));

    public List<Meeting> ExpiredTrash(int days)
    {
        var cutoff = Clock.Source().AddDays(-days).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        return Read(db => db.Query("SELECT * FROM meetings WHERE deleted_at IS NOT NULL AND deleted_at < ?", cutoff).Select(ToMeeting).ToList());
    }

    /// <summary>只改一個欄位（範本、會議語言）：不要整列寫回，畫面上的 Meeting 可能是舊的</summary>
    public void SetTemplate(long id, string templateId) =>
        Write(db => db.Execute("UPDATE meetings SET template_id = ?, updated_at = ? WHERE id = ?", templateId, Clock.Now(), id));

    public void SetLanguage(long id, MeetingLanguage language) =>
        Write(db => db.Execute("UPDATE meetings SET language = ?, updated_at = ? WHERE id = ?", language.Code(), Clock.Now(), id));

    public void UpdateInfo(long id, string title, string date, string participants) => Write(db =>
    {
        if (FetchMeeting(db, id) is not { } m) return;
        if (m.Title != title) m.TitleLocked = true;
        m.Title = title;
        m.MeetingDate = date;
        m.Participants = participants;
        m.UpdatedAt = Clock.Now();
        UpdateMeetingRow(db, m);
        Index(db, id, SearchKind.Title, m.DisplayTitle);
    });

    public void SetAudioFile(long meetingId, string filename) =>
        Write(db => db.Execute("UPDATE meetings SET audio_file = ? WHERE id = ?", filename, meetingId));

    // MARK: 逐字稿

    /// <summary>寫入正式逐字稿：取代舊的正式版，並刪掉即時預覽（M§5.3）。voices：每位說話者的聲紋、說話秒數、收音</summary>
    public void SaveTranscript(long meetingId, IReadOnlyList<Segment> segments, int duration, int speakerCount,
                               IReadOnlyDictionary<int, SpeakerVoice>? voices = null, string? transcribedWith = null) => Write(db =>
    {
        UnindexSegments(db, meetingId);
        db.Execute("DELETE FROM segments WHERE meeting_id = ?", meetingId);
        // 逐字稿換掉了，說話者的復原資料對不上（M§24.2）
        db.Execute("DELETE FROM speaker_undo WHERE meeting_id = ?", meetingId);
        foreach (var s in segments)
        {
            var copy = s.Clone();
            copy.MeetingId = meetingId;
            InsertSegmentRow(db, copy);
            IndexSegment(db, meetingId, copy.Id!.Value, copy.Text);
        }
        // 說話者：保留已改過的名字，新的編號補上預設名字（跟著會議語言）
        var language = FetchMeeting(db, meetingId)?.MeetingLanguage ?? MeetingLanguage.Zh;
        var existing = db.Query("SELECT * FROM speakers WHERE meeting_id = ?", meetingId).Select(ToSpeaker).ToList();
        var named = new Dictionary<int, SpeakerName>();
        foreach (var s in existing.Where(s => !SpeakerNames.IsDefault(s.Name))) named[s.Idx] = s;
        db.Execute("DELETE FROM speakers WHERE meeting_id = ?", meetingId);
        for (var idx = 1; idx <= speakerCount; idx++)
        {
            var speaker = new SpeakerName { MeetingId = meetingId, Idx = idx, Name = language.DefaultSpeaker(idx) };
            if (named.TryGetValue(idx, out var old))
            {
                speaker.Name = old.Name;
                speaker.NameSource = old.NameSource == "default" ? "manual" : old.NameSource;
                speaker.PersonId = old.PersonId;
            }
            if (voices is not null && voices.TryGetValue(idx, out var voice))
            {
                speaker.Embedding = Voiceprint.Data(voice.Embedding);
                speaker.SpeechSeconds = voice.Seconds;
                speaker.AudioSource = voice.Source;
            }
            InsertSpeakerRow(db, speaker);
        }
        if (FetchMeeting(db, meetingId) is { } m)
        {
            m.DurationSeconds = duration;
            if (transcribedWith is not null) m.TranscribedWith = transcribedWith;
            m.UpdatedAt = Clock.Now();
            UpdateMeetingRow(db, m);
        }
    });

    /// <summary>錄音中的即時預覽段落（kind = live）</summary>
    public void AppendLiveSegment(long meetingId, double start, double end, string text) => Write(db =>
    {
        var seq = (long)db.Scalar("SELECT COALESCE(MAX(seq), -1) + 1 FROM segments WHERE meeting_id = ?", meetingId)!;
        InsertSegmentRow(db, new Segment { MeetingId = meetingId, Seq = (int)seq, Start = start, End = end, Text = text, Kind = "live" });
    });

    public void UpdateSegmentText(long id, string text) => Write(db =>
    {
        db.Execute("UPDATE segments SET text = ? WHERE id = ?", text, id);
        db.Execute("UPDATE search SET text = ? WHERE rowid = ?", text, id);
    });

    // MARK: 編輯中的草稿（M§21.15、R20.6）

    /// <summary>key：review:&lt;題目&gt;、review-skip:&lt;題目&gt;、segment:&lt;段落 id&gt;</summary>
    public Dictionary<string, string> Drafts(long meetingId) => Read(db =>
    {
        var result = new Dictionary<string, string>();
        foreach (var r in db.Query("SELECT key, text FROM drafts WHERE meeting_id = ?", meetingId)) result.TryAdd(r.Str("key"), r.Str("text"));
        return result;
    });

    public (string Key, string Text)? LatestDraft(long meetingId, string prefix) => Read(db =>
        db.QueryOne("SELECT key, text FROM drafts WHERE meeting_id = ? AND substr(key, 1, ?) = ? ORDER BY updated_at DESC LIMIT 1",
            meetingId, SqliteLength(prefix), prefix) is { } r ? (r.Str("key"), r.Str("text")) : ((string, string)?)null);

    /// <summary>存草稿；text 是空的就刪掉</summary>
    public void SetDraft(long meetingId, string key, string text) => Write(db =>
    {
        if (text.Length == 0) db.Execute("DELETE FROM drafts WHERE meeting_id = ? AND key = ?", meetingId, key);
        else db.Execute("""
            INSERT INTO drafts (meeting_id, key, text, updated_at) VALUES (?, ?, ?, ?)
            ON CONFLICT (meeting_id, key) DO UPDATE SET text = excluded.text, updated_at = excluded.updated_at
            """, meetingId, key, text, Clock.Now());
    });

    public void DeleteDrafts(long meetingId, string prefix) =>
        Write(db => db.Execute("DELETE FROM drafts WHERE meeting_id = ? AND substr(key, 1, ?) = ?", meetingId, SqliteLength(prefix), prefix));

    /// <summary>SQLite 的 substr 用字元（Unicode 碼位）算；Mac 傳的是 Swift 的 count（字形叢集），一般的前綴（ASCII）兩者相同</summary>
    static int SqliteLength(string s) => s.EnumerateRunes().Count();

    // MARK: 逐字稿校正（M§17.4）

    public List<Correction> Corrections(long meetingId) =>
        Read(db => db.Query("SELECT * FROM corrections WHERE meeting_id = ? ORDER BY id", meetingId).Select(ToCorrection).ToList());

    /// <summary>存一次校正的結果：之前還沒處理的建議換成這次的，已接受、已拒絕的保留</summary>
    public void SaveCorrections(long meetingId, IReadOnlyList<Correction> items) => Write(db =>
    {
        db.Execute("DELETE FROM corrections WHERE meeting_id = ? AND status = 'pending'", meetingId);
        foreach (var c in items)
        {
            db.Execute("INSERT INTO corrections (meeting_id, segment_id, original, suggestion, reason, status, created_at) VALUES (?, ?, ?, ?, ?, ?, ?)",
                c.MeetingId, c.SegmentId, c.Original, c.Suggestion, c.Reason, c.Status, c.CreatedAt);
            c.Id = db.LastInsertRowId;
        }
    });

    /// <summary>接受：改段落文字（含搜尋索引）。段落之後被改過、找不到原文時回傳 false</summary>
    public bool AcceptCorrection(long id) => Write(db => Accept(db, id));

    /// <summary>一次接受多則：同一個交易；回傳沒套用的數量</summary>
    public int AcceptCorrections(IReadOnlyList<long> ids) => Write(db => ids.Count(id => !Accept(db, id)));

    static bool Accept(Db db, long id)
    {
        if (db.QueryOne("SELECT * FROM corrections WHERE id = ?", id) is not { } r) return false;
        var c = ToCorrection(r);
        if (c.Status != "pending" || db.QueryOne("SELECT * FROM segments WHERE id = ?", c.SegmentId) is not { } sr) return false;
        var segment = ToSegment(sr);
        if (TranscriptCorrection.Apply(c.Original, c.Suggestion, segment.Text) is not { } text) return false;
        db.Execute("UPDATE segments SET text = ? WHERE id = ?", text, segment.Id);
        db.Execute("UPDATE search SET text = ? WHERE rowid = ?", text, segment.Id);
        db.Execute("UPDATE corrections SET status = 'accepted' WHERE id = ?", id);
        return true;
    }

    /// <summary>還原已接受的建議：改回原文，回到待確認</summary>
    public bool RevertCorrection(long id) => Write(db =>
    {
        if (db.QueryOne("SELECT * FROM corrections WHERE id = ?", id) is not { } r) return false;
        var c = ToCorrection(r);
        if (c.Status != "accepted" || db.QueryOne("SELECT * FROM segments WHERE id = ?", c.SegmentId) is not { } sr) return false;
        var segment = ToSegment(sr);
        if (TranscriptCorrection.Revert(c.Original, c.Suggestion, segment.Text) is not { } text) return false;
        db.Execute("UPDATE segments SET text = ? WHERE id = ?", text, segment.Id);
        db.Execute("UPDATE search SET text = ? WHERE rowid = ?", text, segment.Id);
        db.Execute("UPDATE corrections SET status = 'pending' WHERE id = ?", id);
        return true;
    });

    public void RejectCorrections(IReadOnlyList<long> ids) => Write(db =>
    {
        foreach (var id in ids) db.Execute("UPDATE corrections SET status = 'rejected' WHERE id = ? AND status = 'pending'", id);
    });

    public List<SpeakerName> Speakers(long meetingId) =>
        Read(db => db.Query("SELECT * FROM speakers WHERE meeting_id = ? ORDER BY idx", meetingId).Select(ToSpeaker).ToList());

    /// <summary>只改名字和名字的來源，不動聲紋等其他欄位（M§21.1）</summary>
    public void RenameSpeaker(long meetingId, int idx, string name) => Write(db =>
    {
        db.Execute("""
            INSERT INTO speakers (meeting_id, idx, name, name_source) VALUES (?, ?, ?, ?)
            ON CONFLICT (meeting_id, idx) DO UPDATE SET name = excluded.name, name_source = excluded.name_source
            """, meetingId, idx, name, SpeakerNames.IsDefault(name) ? "default" : "manual");
        // 改名之後不能再復原合併、改句子（M§24.4）
        db.Execute("DELETE FROM speaker_undo WHERE meeting_id = ?", meetingId);
    });

    // MARK: 重點與筆記

    public List<Bookmark> Bookmarks(long meetingId) =>
        Read(db => db.Query("SELECT * FROM bookmarks WHERE meeting_id = ? ORDER BY time", meetingId).Select(ToBookmark).ToList());

    public void AddBookmark(long meetingId, double time, string label = "") =>
        Write(db => db.Execute("INSERT INTO bookmarks (meeting_id, time, label) VALUES (?, ?, ?)", meetingId, time, label));

    public void DeleteBookmark(long id) => Write(db => db.Execute("DELETE FROM bookmarks WHERE id = ?", id));

    public void UpdateUserNotes(long meetingId, string text) => Write(db =>
    {
        db.Execute("UPDATE meetings SET user_notes = ?, updated_at = ? WHERE id = ?", text, Clock.Now(), meetingId);
        Index(db, meetingId, SearchKind.UserNotes, text);
    });

    // MARK: 會議紀錄

    /// <summary>寫入新的會議紀錄，舊的那一版存進 notes_versions（R6.7）。回傳是否取代了使用者改過的草稿（M§21.3）</summary>
    public bool SaveNotes(long meetingId, MeetingNotes notes, string? by, string reason, NotesSource source = NotesSource.Edit) =>
        Write(db => SaveNotes(db, meetingId, notes, by, reason, source));

    internal static bool SaveNotes(Db db, long meetingId, MeetingNotes notes, string? by, string reason, NotesSource source = NotesSource.Edit)
    {
        if (FetchMeeting(db, meetingId) is not { } m) return false;
        var wasDraft = m.NotesDraft;
        var keepVersion = true;
        var versionReason = reason;
        switch (source)
        {
            case NotesSource.Edit: if (wasDraft != 0) m.NotesDraft = 2; break;
            case NotesSource.Draft:
                keepVersion = false;
                m.NotesDraft = 1;
                break;
            case NotesSource.Generated:
                if (wasDraft == 1) keepVersion = false;
                if (wasDraft == 2) versionReason = L.T("草稿（改過）");
                m.NotesDraft = 0;
                break;
            case NotesSource.Revised: if (wasDraft != 0) m.NotesDraft = 2; break;
        }
        if (keepVersion && m.Notes is { } old)
            db.Execute("INSERT INTO notes_versions (meeting_id, notes, reason, created_at) VALUES (?, ?, ?, ?)", meetingId, old, versionReason, Clock.Now());
        m.Notes = notes.JsonString();
        if (by is not null) m.NotesBy = by;
        var title = SwiftText.TrimSpaces(notes.Title);
        if (!m.TitleLocked && title.Length > 0) m.Title = title;
        m.UpdatedAt = Clock.Now();
        UpdateMeetingRow(db, m);
        Index(db, meetingId, SearchKind.Notes, notes.PlainText);
        Index(db, meetingId, SearchKind.Title, m.DisplayTitle);
        return source == NotesSource.Generated && wasDraft == 2;
    }

    /// <summary>復原上一版：成功回傳 true</summary>
    public bool UndoNotes(long meetingId) => Write(db => UndoNotes(db, meetingId));

    internal static bool UndoNotes(Db db, long meetingId)
    {
        if (db.QueryOne("SELECT * FROM notes_versions WHERE meeting_id = ? ORDER BY id DESC LIMIT 1", meetingId) is not { } vr
            || FetchMeeting(db, meetingId) is not { } m) return false;
        var version = ToVersion(vr);
        m.Notes = version.Notes;
        m.NotesDraft = 0;   // 復原的是之前的某一版，不再當草稿自動更新
        if (!m.TitleLocked && m.DecodedNotes?.Title is { Length: > 0 } title) m.Title = title;
        m.UpdatedAt = Clock.Now();
        UpdateMeetingRow(db, m);
        db.Execute("DELETE FROM notes_versions WHERE id = ?", version.Id);
        Index(db, meetingId, SearchKind.Notes, m.DecodedNotes?.PlainText ?? "");
        Index(db, meetingId, SearchKind.Title, m.DisplayTitle);
        return true;
    }

    /// <summary>全文取代：逐字稿＋會議紀錄</summary>
    public ReplaceResult Replace(long meetingId, string find, string replacement) => Write(db =>
    {
        var changed = 0;
        foreach (var s in db.Query("SELECT * FROM segments WHERE meeting_id = ? ORDER BY rowid", meetingId).Select(ToSegment))
        {
            if (find.Length == 0 || !s.Text.Contains(find, StringComparison.Ordinal)) continue;
            s.Text = s.Text.Replace(find, replacement, StringComparison.Ordinal);
            db.Execute("UPDATE segments SET text = ? WHERE id = ?", s.Text, s.Id);
            db.Execute("UPDATE search SET text = ? WHERE rowid = ?", s.Text, s.Id);
            changed++;
        }
        var notesChanged = false;
        if (FetchMeeting(db, meetingId)?.DecodedNotes is { } notes)
        {
            var updated = notes.Replacing(find, replacement);
            if (!updated.SameAs(notes))
            {
                SaveNotes(db, meetingId, updated, null, L.F("取代「%@」→「%@」", find, replacement));
                notesChanged = true;
            }
        }
        return new ReplaceResult(changed, notesChanged);
    });

    /// <summary>只略過、不需要 LLM 的待確認題目：直接從會議紀錄移除</summary>
    public void DismissReviewItems(long meetingId, IReadOnlySet<int> indexes) => Write(db =>
    {
        if (FetchMeeting(db, meetingId)?.DecodedNotes is not { } notes) return;
        var kept = notes.ReviewItems.Where((_, i) => !indexes.Contains(i)).ToList();
        SaveNotes(db, meetingId, notes with { ReviewItems = kept }, null, L.T("略過待確認"));
    });
}
