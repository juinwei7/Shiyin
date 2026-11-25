using System.Globalization;
using System.Text.Json;
using Shiyin.Localization;
using Shiyin.Model;
using Shiyin.Text;

namespace Shiyin.Data;

/// <summary>編輯會議的快照（migration v15、M§81.3）：只留第一次編輯前的樣子，最後一次編輯後 30 天刪掉（D179）</summary>
public sealed class MeetingEditRecord
{
    public long MeetingId { get; set; }
    /// <summary>EditSnapshot 的 JSON（鍵同 Mac 的 Codable，Mac、Windows 打開同一份資料庫都能還原）</summary>
    public string Snapshot { get; set; } = "";
    /// <summary>編輯前的錄音，audio 底下的相對路徑（edits/&lt;id&gt;.&lt;副檔名&gt;）；沒有錄音的會議是空字串</summary>
    public string AudioFile { get; set; } = "";
    public string SplitInto { get; set; } = "[]";
    public string MergedFrom { get; set; } = "[]";
    public string CreatedAt { get; set; } = Clock.Now();
    public string EditedAt { get; set; } = Clock.Now();

    public const int KeepDays = 30;

    public List<long> SplitIds => DecodeIds(SplitInto);
    public List<long> MergedIds => DecodeIds(MergedFrom);

    /// <summary>還剩幾天自動刪掉（至少 1，當天到期也說 1 天）</summary>
    public int DaysLeft(DateTimeOffset now)
    {
        if (!DateTimeOffset.TryParse(EditedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var edited)) return KeepDays;
        var left = KeepDays - (now - edited).TotalSeconds / 86_400;
        return Math.Max(1, (int)Math.Ceiling(left));
    }

    public int DaysLeft() => DaysLeft(Clock.Source());

    static List<long> DecodeIds(string json)
    {
        try { return JsonSerializer.Deserialize<List<long>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    /// <summary>同 Swift 的 JSONEncoder 寫 [Int64]：[1,2]</summary>
    public static string EncodeIds(IEnumerable<long> ids) => "[" + string.Join(",", ids.Select(i => i.ToString(CultureInfo.InvariantCulture))) + "]";
}

/// <summary>套用編輯的結果（M§81.1）</summary>
/// <param name="Created">分成兩場時分出去的那場</param>
/// <param name="FirstEdit">第一次編輯：原本的錄音要搬到 audio/edits/（之後的編輯，舊的錄音直接刪）</param>
/// <param name="DocumentCopies">合併時複製過來的參考文件：documents 底下的來源 → 目的</param>
public sealed record EditOutcome(Meeting Meeting, Meeting? Created, bool FirstEdit, List<(string From, string To)> DocumentCopies);

/// <summary>還原的結果（M§81.3）：錄音檔、參考文件由呼叫的人搬、刪</summary>
/// <param name="CurrentAudio">現在的錄音（要刪），audio 底下的檔名</param>
/// <param name="EditsAudio">快照的錄音（audio/edits/…）要搬回 audio/RestoredAudio；沒有錄音是空字串</param>
public sealed record RestoreOutcome(string CurrentAudio, string EditsAudio, string RestoredAudio, List<long> Trashed, List<long> Untrashed, List<string> RemovedDocuments);

public sealed partial class AppDatabase
{
    // MARK: 快照的紀錄

    static MeetingEditRecord ToEditRecord(Row r) => new()
    {
        MeetingId = r.Long("meeting_id"), Snapshot = r.Str("snapshot"), AudioFile = r.Str("audio_file"), SplitInto = r.Str("split_into"),
        MergedFrom = r.Str("merged_from"), CreatedAt = r.Str("created_at"), EditedAt = r.Str("edited_at"),
    };

    static MeetingEditRecord? FetchEditRecord(Db db, long meetingId) =>
        db.QueryOne("SELECT * FROM meeting_edits WHERE meeting_id = ?", meetingId) is { } r ? ToEditRecord(r) : null;

    /// <summary>GRDB 的 save：有就整列更新，沒有就新增</summary>
    static void SaveEditRecord(Db db, MeetingEditRecord r) => db.Execute("""
        INSERT INTO meeting_edits (meeting_id, snapshot, audio_file, split_into, merged_from, created_at, edited_at) VALUES (?, ?, ?, ?, ?, ?, ?)
        ON CONFLICT (meeting_id) DO UPDATE SET snapshot = excluded.snapshot, audio_file = excluded.audio_file, split_into = excluded.split_into,
          merged_from = excluded.merged_from, created_at = excluded.created_at, edited_at = excluded.edited_at
        """, r.MeetingId, r.Snapshot, r.AudioFile, r.SplitInto, r.MergedFrom, r.CreatedAt, r.EditedAt);

    public MeetingEditRecord? EditRecord(long meetingId) => Read(db => FetchEditRecord(db, meetingId));

    /// <summary>最後一次編輯超過 days 天的（R107.8）</summary>
    public List<MeetingEditRecord> ExpiredEdits(int days)
    {
        var cutoff = Clock.Source().AddDays(-days).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        return Read(db => db.Query("SELECT * FROM meeting_edits WHERE edited_at < ?", cutoff).Select(ToEditRecord).ToList());
    }

    public void DeleteEditRecord(long meetingId) => Write(db => db.Execute("DELETE FROM meeting_edits WHERE meeting_id = ?", meetingId));

    /// <summary>DEBUG 的 editage：把最後編輯的時間改早，測 30 天的清除</summary>
    public void AgeEdit(long meetingId, double days) => Write(db => db.Execute("UPDATE meeting_edits SET edited_at = ? WHERE meeting_id = ?",
        Clock.Source().AddDays(-days).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), meetingId));

    /// <summary>還在用的編輯前錄音（audio 底下的相對路徑，用 /），清 audio/edits/ 時留著</summary>
    public HashSet<string> EditAudioFiles() =>
        Read(db => db.Query("SELECT audio_file FROM meeting_edits WHERE audio_file <> ''").Select(r => r.Str("audio_file")).ToHashSet());

    // MARK: 套用

    /// <summary>
    /// 套用編輯（R107.7、M§81.1）：一個交易改這場的句子、標記重點、說話者、筆記、會議紀錄的時間；
    /// 合併時另一場的資料複製過來、那場移到垃圾桶；cut 有值時接著分成兩場（新時間軸上的時間）。
    /// audioExtension 是新錄音的副檔名（沒有錄音是 null）；錄音檔由呼叫的人搬（同 Mac 的 applyEdit）
    /// </summary>
    public EditOutcome? ApplyEdit(long id, EditPlan plan, double? cut, string splitTitle, string? audioExtension) => Write(db =>
    {
        if (FetchMeeting(db, id) is not { } meeting) return null;
        var now = Clock.Now();
        var others = new List<long>();
        foreach (var p in plan.Pieces) if (p.MeetingId != id && !others.Contains(p.MeetingId)) others.Add(p.MeetingId);

        // 快照：第一次編輯才存（M§81.3）
        var record = FetchEditRecord(db, id);
        var firstEdit = record is null;
        if (record is null)
        {
            var ext = PathText.Extension(meeting.AudioFile);
            record = new MeetingEditRecord
            {
                MeetingId = id, Snapshot = Snapshot(db, meeting), AudioFile = meeting.AudioFile.Length == 0 ? "" : $"edits/{id}.{ext}", CreatedAt = now, EditedAt = now,
            };
        }
        record.EditedAt = now;
        var merged = record.MergedIds;
        record.MergedFrom = MeetingEditRecord.EncodeIds(merged.Concat(others.Where(o => !merged.Contains(o))));

        // 說話者時間的比例（M§81.1）：每個說話者正式逐字稿留下的長度 ÷ 原本的長度
        var spoken = new Dictionary<long, Dictionary<int, (double Before, double After)>>();
        void Count(long meetingId, Segment segment, (double Start, double End)? kept)
        {
            if (segment.Kind != "final" || segment.Speaker is not int idx) return;
            if (!spoken.TryGetValue(meetingId, out var table)) spoken[meetingId] = table = [];
            var entry = table.GetValueOrDefault(idx);
            entry.Before += Math.Max(0, segment.End - segment.Start);
            if (kept is { } k) entry.After += Math.Max(0, k.End - k.Start);
            table[idx] = entry;
        }
        double Ratio(long meetingId, int idx) =>
            spoken.TryGetValue(meetingId, out var table) && table.TryGetValue(idx, out var e) ? (e.Before > 0 ? e.After / e.Before : 1) : 1;

        // 這場的句子：留下的改時間，其餘刪掉（精簡版、校正建議跟著 ON DELETE CASCADE）
        UnindexSegments(db, id);
        foreach (var segment in db.Query("SELECT * FROM segments WHERE meeting_id = ?", id).Select(ToSegment).ToList())
        {
            var kept = plan.MapSentence(id, segment.Start, segment.End);
            Count(id, segment, kept);
            if (kept is { } k) db.Execute("UPDATE segments SET start = ?, end = ? WHERE id = ?", k.Start, k.End, segment.Id);
            else db.Execute("DELETE FROM segments WHERE id = ?", segment.Id);
        }
        foreach (var bookmark in db.Query("SELECT * FROM bookmarks WHERE meeting_id = ?", id).Select(ToBookmark).ToList())
        {
            if (plan.Map(id, bookmark.Time) is double t) db.Execute("UPDATE bookmarks SET time = ? WHERE id = ?", t, bookmark.Id);
            else db.Execute("DELETE FROM bookmarks WHERE id = ?", bookmark.Id);
        }

        // 合併：另一場的說話者對起來，句子、標記、參考文件複製過來（M§81.4）
        var documentCopies = new List<(string From, string To)>();
        var notesParts = new List<(int Order, string Text)>();
        int Order(long meetingId)
        {
            for (var i = 0; i < plan.Pieces.Count; i++) if (plan.Pieces[i].MeetingId == meetingId) return i;
            return 0;
        }
        notesParts.Add((Order(id), plan.RemapUserNotes(meeting.UserNotes, id)));
        var participants = meeting.ParticipantList;
        var addedSeconds = new Dictionary<int, double>();
        foreach (var other in others)
        {
            if (FetchMeeting(db, other) is not { } source) continue;
            var speakerMap = MergeSpeakersForEdit(db, id, other, meeting.MeetingLanguage);
            foreach (var segment in db.Query("SELECT * FROM segments WHERE meeting_id = ? ORDER BY seq", other).Select(ToSegment).ToList())
            {
                var kept = plan.MapSentence(other, segment.Start, segment.End);
                Count(other, segment, kept);
                if (kept is not { } k) continue;
                var copy = segment.Clone();
                copy.Id = null;
                copy.MeetingId = id;
                copy.Start = k.Start;
                copy.End = k.End;
                copy.Speaker = segment.Speaker is int sp && speakerMap.TryGetValue(sp, out var mapped) ? mapped : null;
                InsertSegmentRow(db, copy);
                if (db.Scalar("SELECT text FROM clean_lines WHERE segment_id = ?", segment.Id) is string clean)
                    db.Execute("INSERT INTO clean_lines (segment_id, text) VALUES (?, ?)", copy.Id, clean);
                foreach (var correction in db.Query("SELECT * FROM corrections WHERE segment_id = ?", segment.Id).Select(ToCorrection).ToList())
                    db.Execute("INSERT INTO corrections (meeting_id, segment_id, original, suggestion, reason, status, created_at) VALUES (?, ?, ?, ?, ?, ?, ?)",
                        id, copy.Id, correction.Original, correction.Suggestion, correction.Reason, correction.Status, correction.CreatedAt);
            }
            foreach (var bookmark in db.Query("SELECT * FROM bookmarks WHERE meeting_id = ?", other).Select(ToBookmark).ToList())
            {
                if (plan.Map(other, bookmark.Time) is not double t) continue;
                db.Execute("INSERT INTO bookmarks (meeting_id, time, label) VALUES (?, ?, ?)", id, t, bookmark.Label);
            }
            foreach (var document in db.Query("SELECT * FROM documents WHERE meeting_id = ? ORDER BY id", other).Select(ToDocument).ToList())
            {
                db.Execute("INSERT INTO documents (meeting_id, name, file, kind, pages, ocr, created_at) VALUES (?, ?, ?, ?, ?, ?, ?)",
                    id, document.Name, document.File, document.Kind, document.Pages, document.Ocr, now);
                var copyId = db.LastInsertRowId;
                var file = $"{copyId}/{document.Name}";
                db.Execute("UPDATE documents SET file = ? WHERE id = ?", file, copyId);
                documentCopies.Add((document.File, file));
            }
            notesParts.Add((Order(other), plan.RemapUserNotes(source.UserNotes, other)));
            foreach (var name in source.ParticipantList) if (!participants.Contains(name)) participants.Add(name);
            foreach (var (from, to) in speakerMap)
            {
                var seconds = db.Scalar("SELECT speech_seconds FROM speakers WHERE meeting_id = ? AND idx = ?", other, from) switch
                {
                    double d => d, long l => l, _ => 0.0,
                };
                addedSeconds[to] = addedSeconds.GetValueOrDefault(to) + seconds * Ratio(other, from);
            }
            db.Execute("UPDATE meetings SET deleted_at = ? WHERE id = ?", now, other);
        }

        // 句子照新的時間重新編號（正式、即時各自）
        foreach (var kind in new[] { "final", "live" })
        {
            var ids = db.Query("SELECT id FROM segments WHERE meeting_id = ? AND kind = ? ORDER BY start, seq, id", id, kind).Select(r => r.Long("id")).ToList();
            for (var seq = 0; seq < ids.Count; seq++) db.Execute("UPDATE segments SET seq = ? WHERE id = ?", seq, ids[seq]);
        }

        // 說話者：沒有句子的拿掉（聲紋、人物留著，還原時才放得回去）；說話時間照留下的比例
        foreach (var speaker in db.Query("SELECT * FROM speakers WHERE meeting_id = ?", id).Select(ToSpeaker).ToList())
        {
            var speaks = (long)db.Scalar("SELECT EXISTS (SELECT 1 FROM segments WHERE meeting_id = ? AND speaker = ?)", id, speaker.Idx)! != 0;
            if (!speaks)
            {
                db.Execute("DELETE FROM speakers WHERE meeting_id = ? AND idx = ?", id, speaker.Idx);
                continue;
            }
            var seconds = speaker.SpeechSeconds * Ratio(id, speaker.Idx) + addedSeconds.GetValueOrDefault(speaker.Idx);
            db.Execute("UPDATE speakers SET speech_seconds = ? WHERE meeting_id = ? AND idx = ?", seconds, id, speaker.Idx);
        }

        // 會議紀錄：舊的存成版本，時間點先移到切口（重新整理之前議題時間軸大致對得上）
        if (!plan.KeepsWhole(id) && meeting.DecodedNotes is { } notes && meeting.Notes is { } old)
        {
            db.Execute("INSERT INTO notes_versions (meeting_id, notes, reason, created_at, template_id) VALUES (?, ?, ?, ?, ?)", id, old, L.T("編輯會議前"), Clock.Now(),
                meeting.NotesTemplate);
            meeting.Notes = notes.MappingTimes(t => plan.Snap(id, t)).JsonString();
        }
        meeting.UserNotes = string.Join("\n\n", notesParts.OrderBy(p => p.Order).Select(p => p.Text).Where(t => !string.IsNullOrWhiteSpace(t)));
        if (!participants.SequenceEqual(meeting.ParticipantList))
            meeting.Participants = string.Join(meeting.Participants.Contains('、') ? "、" : ", ", participants);  // l10n-skip
        // 開始時間：第一個片段那場的開始加上裁掉的秒數；日期跟著那場
        if (plan.Pieces.Count > 0 && FetchMeeting(db, plan.Pieces[0].MeetingId) is { } owner
            && DateTimeOffset.TryParse(owner.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var started))
        {
            meeting.CreatedAt = started.AddSeconds(plan.Pieces[0].Start).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            meeting.MeetingDate = owner.MeetingDate;
        }
        meeting.DurationSeconds = (int)Math.Round(plan.Duration, MidpointRounding.AwayFromZero);
        if (audioExtension is not null) meeting.AudioFile = $"{id}.{audioExtension}";
        meeting.UpdatedAt = now;
        UpdateMeetingRow(db, meeting);
        db.Execute("DELETE FROM speaker_undo WHERE meeting_id = ?", id);
        ReindexMeeting(db, meeting);

        Meeting? created = null;
        if (cut is double c && SplitMeeting(db, id, c, splitTitle, audioExtension) is { } split)
        {
            created = split;
            record.SplitInto = MeetingEditRecord.EncodeIds(record.SplitIds.Append(split.Id!.Value));
            meeting = FetchMeeting(db, id) ?? meeting;
        }
        SaveEditRecord(db, record);
        return new EditOutcome(meeting, created, firstEdit, documentCopies);
    });

    // MARK: 還原

    /// <summary>
    /// 還原成編輯前的樣子（R107.8）：照快照放回欄位、句子、標記、說話者，刪掉編輯後加進來的參考文件；
    /// 分出去的移到垃圾桶、合併進來的從垃圾桶拿出來。錄音檔由呼叫的人搬
    /// </summary>
    public RestoreOutcome? RestoreEdit(long id) => Write(db =>
    {
        if (FetchEditRecord(db, id) is not { } record || FetchMeeting(db, id) is not { } meeting
            || JsonRead.Parse(record.Snapshot) is not { ValueKind: JsonValueKind.Object } snapshot) return null;
        var now = Clock.Now();
        var currentAudio = meeting.AudioFile;

        UnindexSegments(db, id);
        db.Execute("DELETE FROM segments WHERE meeting_id = ?", id);
        db.Execute("DELETE FROM bookmarks WHERE meeting_id = ?", id);
        db.Execute("DELETE FROM speakers WHERE meeting_id = ?", id);
        // 句子用新的 id 放回（分出去的那場可能還用著原本的 id）
        foreach (var line in SnapshotArray(snapshot, "lines"))
        {
            if (line.Get("segment") is not { ValueKind: JsonValueKind.Object } s) continue;
            var segment = new Segment
            {
                MeetingId = id, Seq = (int)(s.Long("seq") ?? 0), Start = s.Double("start") ?? 0, End = s.Double("end") ?? 0, Text = s.Str("text") ?? "",
                LowConfidence = s.Bool("lowConfidence") ?? false, Speaker = (int?)s.Long("speaker"), Kind = s.Str("kind") ?? "final",
                Channel = (int?)s.Long("channel"), SpeakerManual = s.Bool("speakerManual") ?? false,
            };
            InsertSegmentRow(db, segment);
            if (line.Str("clean") is { } clean) db.Execute("INSERT INTO clean_lines (segment_id, text) VALUES (?, ?)", segment.Id, clean);
            foreach (var c in SnapshotArray(line, "corrections"))
                db.Execute("INSERT INTO corrections (meeting_id, segment_id, original, suggestion, reason, status, created_at) VALUES (?, ?, ?, ?, ?, ?, ?)",
                    id, segment.Id, c.Str("original") ?? "", c.Str("suggestion") ?? "", c.Str("reason") ?? "", c.Str("status") ?? "pending", c.Str("createdAt") ?? now);
        }
        foreach (var b in SnapshotArray(snapshot, "bookmarks"))
            db.Execute("INSERT INTO bookmarks (meeting_id, time, label) VALUES (?, ?, ?)", id, b.Double("time") ?? 0, b.Str("label") ?? "");
        foreach (var s in SnapshotArray(snapshot, "speakers"))
        {
            var person = s.Long("personId");
            if (person is long p && db.Scalar("SELECT 1 FROM people WHERE id = ?", p) is null) person = null;
            InsertSpeakerRow(db, new SpeakerName
            {
                MeetingId = id, Idx = (int)(s.Long("idx") ?? 0), Name = s.Str("name") ?? "", NameSource = s.Str("nameSource") ?? "default", PersonId = person,
                Embedding = s.Str("embedding") is { } e ? Convert.FromBase64String(e) : null, SpeechSeconds = s.Double("speechSeconds") ?? 0,
                AudioSource = s.Str("audioSource") ?? "mix", SuggestedName = s.Str("suggestedName"), SuggestionReason = s.Str("suggestionReason"),
                SuggestionTime = s.Str("suggestionTime"),
            });
        }
        var keepDocuments = SnapshotArray(snapshot, "documentIds").Select(x => x.ValueKind == JsonValueKind.Number ? x.GetInt64() : 0).ToHashSet();
        var removedDocuments = new List<string>();
        foreach (var document in db.Query("SELECT * FROM documents WHERE meeting_id = ?", id).Select(ToDocument).ToList())
        {
            if (keepDocuments.Contains(document.Id ?? 0)) continue;
            db.Execute("DELETE FROM documents WHERE id = ?", document.Id);
            removedDocuments.Add(document.File);
        }

        var snapshotNotes = snapshot.Str("notes");
        if (meeting.Notes is { } current && current != snapshotNotes)
            db.Execute("INSERT INTO notes_versions (meeting_id, notes, reason, created_at, template_id) VALUES (?, ?, ?, ?, ?)", id, current, L.T("還原編輯前"), Clock.Now(),
                meeting.NotesTemplate);
        meeting.Notes = snapshotNotes;
        meeting.NotesBy = snapshot.Str("notesBy");
        meeting.NotesDraft = (int)(snapshot.Long("notesDraft") ?? 0);
        meeting.UserNotes = snapshot.Str("userNotes") ?? "";
        meeting.DurationSeconds = (int?)snapshot.Long("durationSeconds");
        meeting.CreatedAt = snapshot.Str("createdAt") ?? meeting.CreatedAt;
        meeting.MeetingDate = snapshot.Str("meetingDate") ?? meeting.MeetingDate;
        meeting.Participants = snapshot.Str("participants") ?? meeting.Participants;
        meeting.AudioFile = record.AudioFile.Length == 0 ? "" : snapshot.Str("audioFile") ?? "";
        meeting.UpdatedAt = now;
        UpdateMeetingRow(db, meeting);
        db.Execute("DELETE FROM speaker_undo WHERE meeting_id = ?", id);
        ReindexMeeting(db, meeting);

        List<long> trashed = [], untrashed = [];
        foreach (var other in record.SplitIds)
        {
            if (FetchMeeting(db, other) is not { DeletedAt: null }) continue;
            db.Execute("UPDATE meetings SET deleted_at = ? WHERE id = ?", now, other);
            trashed.Add(other);
        }
        foreach (var other in record.MergedIds)
        {
            if (FetchMeeting(db, other) is not { DeletedAt: not null }) continue;
            db.Execute("UPDATE meetings SET deleted_at = NULL WHERE id = ?", other);
            untrashed.Add(other);
        }
        db.Execute("DELETE FROM meeting_edits WHERE meeting_id = ?", id);
        return new RestoreOutcome(currentAudio, record.AudioFile, meeting.AudioFile, trashed, untrashed, removedDocuments);
    });

    static IEnumerable<JsonElement> SnapshotArray(JsonElement o, string name) =>
        o.Get(name) is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray() : [];

    /// <summary>
    /// 編輯前這場會議的樣子（M§81.3）：欄位、所有句子（含精簡版、校正建議）、標記重點、說話者、參考文件的 id。
    /// 鍵照 Mac 的 EditSnapshot（Codable，宣告的順序；沒有值的選填欄位不寫，Data 是 base64、斜線跳脫）
    /// </summary>
    static string Snapshot(Db db, Meeting meeting)
    {
        var id = meeting.Id!.Value;
        var lines = new JNode.Arr(db.Query("SELECT * FROM segments WHERE meeting_id = ? ORDER BY kind, seq", id).Select(ToSegment).Select(segment =>
        {
            var line = new JNode.Obj().Add("segment", SegmentJson(segment));
            if (db.Scalar("SELECT text FROM clean_lines WHERE segment_id = ?", segment.Id) is string clean) line.Add("clean", clean);
            var corrections = db.Query("SELECT * FROM corrections WHERE segment_id = ? ORDER BY id", segment.Id).Select(ToCorrection).Select(CorrectionJson);
            return (JNode)line.Add("corrections", new JNode.Arr(corrections));
        }).ToList());
        var bookmarks = new JNode.Arr(db.Query("SELECT * FROM bookmarks WHERE meeting_id = ? ORDER BY time", id).Select(ToBookmark).Select(b =>
            (JNode)new JNode.Obj().Add("id", new JNode.Int(b.Id ?? 0)).Add("meetingId", new JNode.Int(b.MeetingId)).Add("time", JNode.Of(b.Time)).Add("label", b.Label)).ToList());
        var speakers = new JNode.Arr(db.Query("SELECT * FROM speakers WHERE meeting_id = ? ORDER BY idx", id).Select(ToSpeaker).Select(s =>
        {
            var o = new JNode.Obj().Add("meetingId", new JNode.Int(s.MeetingId)).Add("idx", new JNode.Int(s.Idx)).Add("name", s.Name).Add("nameSource", s.NameSource);
            if (s.PersonId is long p) o.Add("personId", new JNode.Int(p));
            if (s.Embedding is { } e) o.Add("embedding", Convert.ToBase64String(e));
            o.Add("speechSeconds", JNode.Of(s.SpeechSeconds)).Add("audioSource", s.AudioSource);
            if (s.SuggestedName is { } n) o.Add("suggestedName", n);
            if (s.SuggestionReason is { } r) o.Add("suggestionReason", r);
            if (s.SuggestionTime is { } t) o.Add("suggestionTime", t);
            return (JNode)o;
        }).ToList());
        var documents = new JNode.Arr(db.Query("SELECT id FROM documents WHERE meeting_id = ? ORDER BY id", id).Select(r => (JNode)new JNode.Int(r.Long("id"))).ToList());
        var snapshot = new JNode.Obj().Add("audioFile", meeting.AudioFile);
        if (meeting.DurationSeconds is int d) snapshot.Add("durationSeconds", new JNode.Int(d));
        snapshot.Add("userNotes", meeting.UserNotes);
        if (meeting.Notes is { } notes) snapshot.Add("notes", notes);
        if (meeting.NotesBy is { } by) snapshot.Add("notesBy", by);
        snapshot.Add("notesDraft", new JNode.Int(meeting.NotesDraft)).Add("createdAt", meeting.CreatedAt).Add("meetingDate", meeting.MeetingDate)
            .Add("participants", meeting.Participants).Add("lines", lines).Add("bookmarks", bookmarks).Add("speakers", speakers).Add("documentIds", documents);
        return SwiftJsonWriter.Write(snapshot, sortKeys: false, escapeSlashes: true);
    }

    /// <summary>
    /// 合併時另一場的說話者對到這場（M§81.4）：同一個人物 → 同樣的名字（不是預設的）→ 聲紋（門檻同聲紋比對）；
    /// 都對不上的接在後面。回傳另一場的編號 → 這場的編號
    /// </summary>
    static Dictionary<int, int> MergeSpeakersForEdit(Db db, long id, long other, MeetingLanguage language)
    {
        var mine = db.Query("SELECT * FROM speakers WHERE meeting_id = ? ORDER BY idx", id).Select(ToSpeaker).ToList();
        var theirs = db.Query("SELECT * FROM speakers WHERE meeting_id = ? ORDER BY idx", other).Select(ToSpeaker).ToList();
        var map = new Dictionary<int, int>();
        var taken = new HashSet<int>();
        void Claim(int from, int to)
        {
            map[from] = to;
            taken.Add(to);
        }
        foreach (var s in theirs)
            if (s.PersonId is long person && mine.FirstOrDefault(m => m.PersonId == person && !taken.Contains(m.Idx)) is { } match) Claim(s.Idx, match.Idx);
        foreach (var s in theirs.Where(s => !map.ContainsKey(s.Idx) && s.NameSource != "default"))
        {
            var name = SwiftText.Lower(s.Name);
            if (mine.FirstOrDefault(m => m.NameSource != "default" && SwiftText.Lower(m.Name) == name && !taken.Contains(m.Idx)) is { } match) Claim(s.Idx, match.Idx);
        }
        foreach (var s in theirs.Where(s => !map.ContainsKey(s.Idx)))
        {
            if (s.Embedding is not { } data) continue;
            var vector = Voiceprint.Vector(data);
            (int Idx, double Score)? best = null;
            foreach (var m in mine.Where(m => !taken.Contains(m.Idx) && m.Embedding is not null))
            {
                var score = Voiceprint.Similarity(vector, Voiceprint.Vector(m.Embedding!));
                // Swift 的 max(by:)：一樣大時留第一個
                if (best is null || score > best.Value.Score) best = (m.Idx, score);
            }
            if (best is { } b && b.Score >= VoiceprintThreshold) Claim(s.Idx, b.Idx);
        }
        var next = mine.Select(m => m.Idx).DefaultIfEmpty(0).Max() + 1;
        foreach (var s in theirs.Where(s => !map.ContainsKey(s.Idx)))
        {
            var copy = new SpeakerName
            {
                MeetingId = id, Idx = next, Name = s.NameSource == "default" ? language.DefaultSpeaker(next) : s.Name, NameSource = s.NameSource,
                PersonId = s.PersonId, Embedding = s.Embedding, SpeechSeconds = 0, AudioSource = s.AudioSource, SuggestedName = s.SuggestedName,
                SuggestionReason = s.SuggestionReason, SuggestionTime = s.SuggestionTime,
            };
            InsertSpeakerRow(db, copy);
            mine.Add(copy);
            Claim(s.Idx, next);
            next++;
        }
        return map;
    }

    /// <summary>這場會議的搜尋索引全部重建（句子、標題、筆記、會議紀錄）</summary>
    static void ReindexMeeting(Db db, Meeting meeting)
    {
        var id = meeting.Id!.Value;
        UnindexSegments(db, id);
        UnindexMeeting(db, id);
        db.Execute("INSERT INTO search (rowid, meeting_id, kind, ref, text) SELECT id, meeting_id, 'segment', id, text FROM segments WHERE meeting_id = ?", id);
        Index(db, id, SearchKind.Title, meeting.DisplayTitle);
        Index(db, id, SearchKind.UserNotes, meeting.UserNotes);
        if (meeting.DecodedNotes is { } notes) Index(db, id, SearchKind.Notes, notes.PlainText);
    }
}
