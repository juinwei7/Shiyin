using System.Text.Json;
using Shiyin.Localization;
using Shiyin.Model;
using Shiyin.Notes;
using Shiyin.Text;
using Shiyin.Transcribe;

namespace Shiyin.Data;

/// <summary>設定頁的認識的人：幾份聲紋、最近出現在哪場會議</summary>
public sealed record PersonSummary(long Id, string Name, int VoiceprintCount, string? LastMeetingTitle, string? LastMeetingDate);

/// <summary>一組問答（M§21.8）：meeting_id 是 NULL 時是問所有會議</summary>
public sealed class QuestionRecord
{
    public long? Id { get; set; }
    public long? MeetingId { get; set; }
    public string Scope { get; set; } = "";
    public string Question { get; set; } = "";
    /// <summary>AskPrompt.Answer 的 JSON</summary>
    public string Answer { get; set; } = "";
    public string Provider { get; set; } = "";
    public string CreatedAt { get; set; } = Clock.Now();
    public AskPrompt.Answer? Decoded => AskPrompt.Answer.Decode(Answer);
}

public sealed partial class AppDatabase
{
    /// <summary>說話少於這個秒數不比對、不學：太短的聲紋不準</summary>
    public static double VoiceprintMinimumSeconds { get; set; } = 8.0;
    /// <summary>相似度門檻（cosine）。Mac 是 0.65；Windows 的聲紋模型（3D-Speaker）不同：AMI 9 場陌生人最高 0.535、
    /// 同一人最低 0.632，取 0.6（design-windows §9、D58）</summary>
    public static double VoiceprintThreshold { get; set; } = 0.6;
    /// <summary>同一個人的聲紋最多留幾份，超過刪最舊的</summary>
    public const int VoiceprintsPerPerson = 30;

    /// <summary>轉錄完：還是預設名稱的說話者跟認識的人比對，夠像的自動帶入名字。一個人在一場會議只對到一位說話者，
    /// 依相似度由高到低分配。回傳帶入了幾位</summary>
    public int MatchVoiceprints(long meetingId) => Write(db =>
    {
        var speakers = db.Query("SELECT * FROM speakers WHERE meeting_id = ?", meetingId).Select(ToSpeaker)
            .Where(s => s.NameSource == "default" && s.Embedding is not null && s.SpeechSeconds >= VoiceprintMinimumSeconds).ToList();
        if (speakers.Count == 0) return 0;
        var library = Library(db);
        if (library.Count == 0) return 0;
        var candidates = new List<(double Score, int Idx, long Person)>();
        foreach (var speaker in speakers)
        {
            var vector = Voiceprint.Vector(speaker.Embedding!);
            foreach (var (person, prints) in library)
            {
                var score = prints.Select(p => Voiceprint.Similarity(vector, p)).DefaultIfEmpty(0).Max();
                if (score >= VoiceprintThreshold) candidates.Add((score, speaker.Idx, person));
            }
        }
        var usedSpeakers = new HashSet<int>();
        var usedPeople = db.Query("SELECT person_id FROM speakers WHERE meeting_id = ? AND person_id IS NOT NULL", meetingId)
            .Select(r => r.Long("person_id")).ToHashSet();
        var matched = 0;
        foreach (var c in candidates.OrderByDescending(c => c.Score).ThenBy(c => c.Idx).ThenBy(c => c.Person))
        {
            if (usedSpeakers.Contains(c.Idx) || usedPeople.Contains(c.Person)) continue;
            if (db.Scalar("SELECT name FROM people WHERE id = ?", c.Person) is not string name) continue;
            usedSpeakers.Add(c.Idx);
            usedPeople.Add(c.Person);
            db.Execute("UPDATE speakers SET name = ?, name_source = 'voiceprint', person_id = ? WHERE meeting_id = ? AND idx = ?", name, c.Person, meetingId, c.Idx);
            matched++;
        }
        return matched;
    });

    static Dictionary<long, List<float[]>> Library(Db db)
    {
        var result = new Dictionary<long, List<float[]>>();
        foreach (var r in db.Query("SELECT person_id, embedding FROM voiceprints ORDER BY id"))
        {
            var id = r.Long("person_id");
            if (!result.TryGetValue(id, out var list)) result[id] = list = [];
            list.Add(Voiceprint.Vector(r.Blob("embedding") ?? []));
        }
        return result;
    }

    /// <summary>說話者被取名之後：連到同名的人（沒有就建立），把這場會議的聲紋存起來（M§21.4）。
    /// 名字是預設的「說話者 N」時，取消連結、刪掉這場會議這位說話者存過的聲紋</summary>
    public void LearnVoiceprint(long meetingId, int idx, string name, string source = "manual") => Write(db =>
    {
        db.Execute("DELETE FROM voiceprints WHERE meeting_id = ? AND speaker_idx = ?", meetingId, idx);
        if (SpeakerNames.IsDefault(name))
        {
            db.Execute("UPDATE speakers SET person_id = NULL WHERE meeting_id = ? AND idx = ?", meetingId, idx);
            DeleteOrphanPeople(db);
            return;
        }
        var personId = db.Scalar("SELECT id FROM people WHERE name = ? COLLATE NOCASE", name) as long?;
        if (personId is null)
        {
            db.Execute("INSERT INTO people (name, created_at) VALUES (?, ?)", name, Clock.Now());
            personId = db.LastInsertRowId;
        }
        db.Execute("UPDATE speakers SET person_id = ?, name_source = ? WHERE meeting_id = ? AND idx = ?", personId, source, meetingId, idx);
        if (db.QueryOne("SELECT * FROM speakers WHERE meeting_id = ? AND idx = ?", meetingId, idx) is not { } sr) return;
        var speaker = ToSpeaker(sr);
        if (speaker.Embedding is not { } embedding || speaker.SpeechSeconds < VoiceprintMinimumSeconds) return;
        var vector = Voiceprint.Vector(embedding);
        var existing = db.Query("SELECT embedding FROM voiceprints WHERE person_id = ?", personId).Select(r => Voiceprint.Vector(r.Blob("embedding") ?? []));
        // 跟已有的某份幾乎一樣就不重複存
        if (existing.Any(e => Voiceprint.Similarity(vector, e) > 0.95)) return;
        db.Execute("INSERT INTO voiceprints (person_id, embedding, audio_source, meeting_id, speaker_idx, created_at) VALUES (?, ?, ?, ?, ?, ?)",
            personId, embedding, speaker.AudioSource, meetingId, idx, Clock.Now());
        db.Execute("""
            DELETE FROM voiceprints WHERE person_id = ? AND id NOT IN
              (SELECT id FROM voiceprints WHERE person_id = ? ORDER BY id DESC LIMIT ?)
            """, personId, personId, VoiceprintsPerPerson);
    });

    /// <summary>這場會議說話者辨識至少分出幾個人（M§23.2），null 自動判斷</summary>
    public void SetMinSpeakers(long meetingId, int? minSpeakers) =>
        Write(db => db.Execute("UPDATE meetings SET min_speakers = ? WHERE id = ?", minSpeakers, meetingId));

    /// <summary>重新辨識說話者（M§23.3）：段落換成新的說話者；刪掉這場存過的聲紋；重建說話者，inherited 的沿用名字</summary>
    public void ReassignSpeakers(long meetingId, IReadOnlyList<(long Id, int? Speaker)> segmentSpeakers, IReadOnlyDictionary<int, SpeakerVoice> voices,
                                 IReadOnlyDictionary<int, SpeakerRules.Inherited> inherited) => Write(db =>
    {
        foreach (var (id, speaker) in segmentSpeakers) db.Execute("UPDATE segments SET speaker = ? WHERE id = ?", speaker, id);
        db.Execute("DELETE FROM voiceprints WHERE meeting_id = ?", meetingId);
        var language = FetchMeeting(db, meetingId)?.MeetingLanguage ?? MeetingLanguage.Zh;
        db.Execute("DELETE FROM speakers WHERE meeting_id = ?", meetingId);
        var count = segmentSpeakers.Select(s => s.Speaker ?? 0).DefaultIfEmpty(0).Max();
        for (var idx = 1; idx <= count; idx++)
        {
            var speaker = new SpeakerName { MeetingId = meetingId, Idx = idx, Name = language.DefaultSpeaker(idx) };
            if (inherited.TryGetValue(idx, out var old))
            {
                speaker.Name = old.Name;
                speaker.NameSource = old.Source;
                speaker.PersonId = old.PersonId;
            }
            if (voices.TryGetValue(idx, out var voice))
            {
                speaker.Embedding = Voiceprint.Data(voice.Embedding);
                speaker.SpeechSeconds = voice.Seconds;
                speaker.AudioSource = voice.Source;
            }
            InsertSpeakerRow(db, speaker);
        }
        DeleteOrphanPeople(db);
        db.Execute("DELETE FROM speaker_undo WHERE meeting_id = ?", meetingId);
        db.Execute("UPDATE meetings SET updated_at = ? WHERE id = ?", Clock.Now(), meetingId);
    });

    /// <summary>「不是他」：自動帶入的名字不對，回到預設名稱；之後重新比對也不再帶入這位</summary>
    public void RejectVoiceprintMatch(long meetingId, int idx, string defaultName) => Write(db =>
    {
        db.Execute("UPDATE speakers SET name = ?, name_source = 'manual', person_id = NULL WHERE meeting_id = ? AND idx = ?", defaultName, meetingId, idx);
        db.Execute("DELETE FROM voiceprints WHERE meeting_id = ? AND speaker_idx = ?", meetingId, idx);
        DeleteOrphanPeople(db);
        db.Execute("DELETE FROM speaker_undo WHERE meeting_id = ?", meetingId);
    });

    /// <summary>沒有聲紋、也沒有連到任何說話者的人，不留在認識的人裡</summary>
    static void DeleteOrphanPeople(Db db) => db.Execute("""
        DELETE FROM people WHERE id NOT IN (SELECT person_id FROM voiceprints)
          AND id NOT IN (SELECT person_id FROM speakers WHERE person_id IS NOT NULL)
        """);

    /// <summary>存 AI 猜的名字：只寫還是預設名稱的說話者</summary>
    public void SaveSpeakerSuggestions(long meetingId, IReadOnlyList<SpeakerNaming.Suggestion> suggestions) => Write(db =>
    {
        foreach (var s in suggestions)
            db.Execute("""
                UPDATE speakers SET suggested_name = ?, suggestion_reason = ?, suggestion_time = ?
                WHERE meeting_id = ? AND idx = ? AND name_source = 'default'
                """, s.Name, s.Reason, s.Time, meetingId, s.Speaker);
    });

    public void ClearSpeakerSuggestion(long meetingId, int idx) => Write(db =>
        db.Execute("UPDATE speakers SET suggested_name = NULL, suggestion_reason = NULL, suggestion_time = NULL WHERE meeting_id = ? AND idx = ?", meetingId, idx));

    /// <summary>說話者改名後，會議紀錄裡的舊名字換成新名字（M§21.6）；有改到就存一版</summary>
    public bool ReplaceSpeakerNameInNotes(long meetingId, string old, string replacement)
    {
        if (old == replacement || Meeting(meetingId)?.DecodedNotes is not { } notes) return false;
        var updated = notes.ReplacingSpeakerName(old, replacement, SpeakerNames.IsDefault(old));
        if (updated.SameAs(notes)) return false;
        SaveNotes(meetingId, updated, null, L.T("說話者改名"));
        return true;
    }

    public List<PersonSummary> PeopleSummaries() => Read(db => db.Query("""
        SELECT p.id, p.name,
          (SELECT count(*) FROM voiceprints v WHERE v.person_id = p.id) AS prints,
          (SELECT m.id FROM speakers s JOIN meetings m ON m.id = s.meeting_id
             WHERE s.person_id = p.id AND m.deleted_at IS NULL ORDER BY m.meeting_date DESC LIMIT 1) AS last_id
        FROM people p ORDER BY p.name COLLATE NOCASE
        """).Select(r =>
    {
        var last = r.LongOrNull("last_id") is long id ? FetchMeeting(db, id) : null;
        return new PersonSummary(r.Long("id"), r.Str("name"), r.Int("prints"), last?.DisplayTitle, last?.MeetingDate);
    }).ToList());

    /// <summary>改名：認識的人和所有連到他的說話者一起改；新名字已經有人用了就合併過去</summary>
    public void RenamePerson(long id, string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) return;
        Write(db =>
        {
            if (db.QueryOne("SELECT * FROM people WHERE name = ? COLLATE NOCASE AND id <> ?", trimmed, id) is { } other)
            {
                var otherId = other.Long("id");
                db.Execute("UPDATE voiceprints SET person_id = ? WHERE person_id = ?", otherId, id);
                db.Execute("UPDATE speakers SET person_id = ?, name = ? WHERE person_id = ?", otherId, other.Str("name"), id);
                db.Execute("DELETE FROM people WHERE id = ?", id);
            }
            else
            {
                db.Execute("UPDATE people SET name = ? WHERE id = ?", trimmed, id);
                db.Execute("UPDATE speakers SET name = ? WHERE person_id = ?", trimmed, id);
            }
        });
    }

    /// <summary>刪除認識的人：聲紋刪掉（外鍵），已經帶入的名字留著</summary>
    public void DeletePerson(long id) => Write(db => db.Execute("DELETE FROM people WHERE id = ?", id));

    public void DeleteAllPeople() => Write(db => db.Execute("DELETE FROM people"));

    public List<string> PeopleNames() => Read(db => db.Query("SELECT name FROM people ORDER BY name COLLATE NOCASE").Select(r => r.Str("name")).ToList());

    // MARK: 說話者的修正（M§24.2～§24.4、R23）

    /// <summary>改之前先存一份，只留最近一次</summary>
    static void SaveSpeakerUndo(Db db, long meetingId, string label, string? notesBefore = null, string? notesAfter = null)
    {
        var segments = new JNode.Arr(db.Query("SELECT id, speaker, speaker_manual FROM segments WHERE meeting_id = ? AND kind = 'final'", meetingId)
            .Select(r =>
            {
                var o = new JNode.Obj().Add("id", new JNode.Int(r.Long("id")));
                if (r.LongOrNull("speaker") is long sp) o.Add("speaker", new JNode.Int(sp));
                return (JNode)o.Add("manual", JNode.Of(r.Bool("speaker_manual")));
            }));
        var speakers = new JNode.Arr(db.Query("SELECT * FROM speakers WHERE meeting_id = ?", meetingId).Select(ToSpeaker).Select(s =>
        {
            var o = new JNode.Obj().Add("idx", new JNode.Int(s.Idx)).Add("name", s.Name).Add("nameSource", s.NameSource);
            if (s.PersonId is long p) o.Add("personId", new JNode.Int(p));
            if (s.Embedding is { } e) o.Add("embedding", Convert.ToBase64String(e));
            return (JNode)o.Add("speechSeconds", JNode.Of(s.SpeechSeconds)).Add("audioSource", s.AudioSource);
        }));
        var snapshot = new JNode.Obj().Add("segments", segments).Add("speakers", speakers);
        if (notesBefore is not null) snapshot.Add("notesBefore", notesBefore);
        if (notesAfter is not null) snapshot.Add("notesAfter", notesAfter);
        // Mac 用沒有排序的 JSONEncoder 寫（Data 是 base64、斜線會跳脫）；讀的時候兩邊都只看內容
        var json = SwiftJsonWriter.Write(snapshot, sortKeys: false, escapeSlashes: true);
        db.Execute("INSERT OR REPLACE INTO speaker_undo (meeting_id, snapshot, label, created_at) VALUES (?, ?, ?, ?)", meetingId, json, label, Clock.Now());
    }

    /// <summary>可以復原的調整（顯示用的說明），沒有就是 null</summary>
    public string? SpeakerUndoLabel(long meetingId) => Read(db => db.Scalar("SELECT label FROM speaker_undo WHERE meeting_id = ?", meetingId) as string);

    /// <summary>復原最近一次說話者的調整。合併時換過名字的會議紀錄，之後沒有再改的話一起換回去</summary>
    public bool UndoSpeakerEdit(long meetingId) => Write(db =>
    {
        if (db.Scalar("SELECT snapshot FROM speaker_undo WHERE meeting_id = ?", meetingId) is not string json
            || JsonRead.Parse(json) is not { ValueKind: JsonValueKind.Object } snapshot) return false;
        if (snapshot.Str("notesAfter") is { } after && snapshot.Str("notesBefore") is { } before
            && db.Scalar("SELECT notes FROM meetings WHERE id = ?", meetingId) as string == after
            && db.Scalar("SELECT notes FROM notes_versions WHERE meeting_id = ? ORDER BY id DESC LIMIT 1", meetingId) as string == before)
            UndoNotes(db, meetingId);
        foreach (var s in snapshot.GetProperty("segments").EnumerateArray())
            db.Execute("UPDATE segments SET speaker = ?, speaker_manual = ? WHERE id = ?", s.Long("speaker"), s.Bool("manual") ?? false, s.Long("id"));
        var people = db.Query("SELECT id FROM people").Select(r => r.Long("id")).ToHashSet();
        db.Execute("DELETE FROM speakers WHERE meeting_id = ?", meetingId);
        foreach (var s in snapshot.GetProperty("speakers").EnumerateArray())
        {
            var personId = s.Long("personId");
            InsertSpeakerRow(db, new SpeakerName
            {
                MeetingId = meetingId, Idx = (int)(s.Long("idx") ?? 0), Name = s.Str("name") ?? "", NameSource = s.Str("nameSource") ?? "default",
                PersonId = personId is long p && people.Contains(p) ? p : null,
                Embedding = s.Str("embedding") is { } b ? Convert.FromBase64String(b) : null,
                SpeechSeconds = s.Double("speechSeconds") ?? 0, AudioSource = s.Str("audioSource") ?? "mix",
            });
        }
        db.Execute("DELETE FROM speaker_undo WHERE meeting_id = ?", meetingId);
        return true;
    });

    /// <summary>合併說話者（R23.1）：from 的段落併到 into；聲紋依說話秒數加權平均；from 存過的聲紋刪掉；
    /// 會議紀錄裡 from 的名字換成 into 的名字（存一版）。回傳兩人原本的名字</summary>
    public (string From, string Into)? MergeSpeakers(long meetingId, int from, int into, string label) => Write(db =>
    {
        if (from == into
            || db.QueryOne("SELECT * FROM speakers WHERE meeting_id = ? AND idx = ?", meetingId, into) is not { } ar
            || db.QueryOne("SELECT * FROM speakers WHERE meeting_id = ? AND idx = ?", meetingId, from) is not { } br
            || FetchMeeting(db, meetingId) is not { } meeting) return ((string, string)?)null;
        var a = ToSpeaker(ar);
        var b = ToSpeaker(br);
        string? notesAfter = null;
        if (meeting.DecodedNotes is { } notes && a.Name != b.Name)
        {
            var updated = notes.ReplacingSpeakerName(b.Name, a.Name, SpeakerNames.IsDefault(b.Name));
            if (!updated.SameAs(notes))
            {
                SaveNotes(db, meetingId, updated, null, L.T("合併說話者"));
                notesAfter = db.Scalar("SELECT notes FROM meetings WHERE id = ?", meetingId) as string;
            }
        }
        SaveSpeakerUndo(db, meetingId, label, notesAfter is null ? null : meeting.Notes, notesAfter);
        db.Execute("UPDATE segments SET speaker = ? WHERE meeting_id = ? AND speaker = ?", into, meetingId, from);
        var seconds = a.SpeechSeconds + b.SpeechSeconds;
        var embedding = a.Embedding;
        if (a.Embedding is { } x && b.Embedding is { } y)
        {
            var (u, v) = (Voiceprint.Vector(x), Voiceprint.Vector(y));
            float wa = (float)Math.Max(a.SpeechSeconds, 1), wb = (float)Math.Max(b.SpeechSeconds, 1);
            var sum = u.Zip(v, (p, q) => p * wa + q * wb).ToArray();
            var norm = MathF.Sqrt(sum.Aggregate(0f, (s, z) => s + z * z));
            if (norm > 0) sum = sum.Select(z => z / norm).ToArray();
            embedding = Voiceprint.Data(sum);
        }
        else if (a.Embedding is null && b.Embedding is { } only) embedding = only;
        db.Execute("UPDATE speakers SET speech_seconds = ?, embedding = ? WHERE meeting_id = ? AND idx = ?", seconds, embedding, meetingId, into);
        db.Execute("DELETE FROM speakers WHERE meeting_id = ? AND idx = ?", meetingId, from);
        db.Execute("DELETE FROM voiceprints WHERE meeting_id = ? AND speaker_idx = ?", meetingId, from);
        DeleteOrphanPeople(db);
        return (b.Name, a.Name);
    });

    /// <summary>改句子的說話者（R23.2）：to 是 null 時新增一位說話者（預設名稱）；改過的句子標成 speaker_manual；
    /// 沒有段落的說話者拿掉。回傳句子現在的說話者</summary>
    public int ReassignSegments(long meetingId, IReadOnlyList<long> ids, int? to, string label) => Write(db =>
    {
        SaveSpeakerUndo(db, meetingId, label);
        var target = to ?? 0;
        if (to is null)
        {
            target = (int)((db.Scalar("SELECT MAX(idx) FROM speakers WHERE meeting_id = ?", meetingId) as long?) ?? 0) + 1;
            var language = FetchMeeting(db, meetingId)?.MeetingLanguage ?? MeetingLanguage.Zh;
            InsertSpeakerRow(db, new SpeakerName
            {
                MeetingId = meetingId, Idx = target, Name = language.DefaultSpeaker(target),
                AudioSource = db.Scalar("SELECT audio_source FROM speakers WHERE meeting_id = ? LIMIT 1", meetingId) as string ?? "mix",
            });
        }
        foreach (var id in ids) db.Execute("UPDATE segments SET speaker = ?, speaker_manual = 1 WHERE id = ? AND meeting_id = ?", target, id, meetingId);
        var empty = db.Query("""
            SELECT idx FROM speakers s WHERE meeting_id = ?
              AND NOT EXISTS (SELECT 1 FROM segments g WHERE g.meeting_id = s.meeting_id AND g.speaker = s.idx)
            """, meetingId).Select(r => r.Int("idx")).ToList();
        foreach (var e in empty)
        {
            db.Execute("DELETE FROM speakers WHERE meeting_id = ? AND idx = ?", meetingId, e);
            db.Execute("DELETE FROM voiceprints WHERE meeting_id = ? AND speaker_idx = ?", meetingId, e);
        }
        DeleteOrphanPeople(db);
        return target;
    });

    /// <summary>說話者辨識完記下自我檢查的分數（M§24.5），之前按過的「不用了」重設</summary>
    public void SetSpeakerCheck(long meetingId, double? score) =>
        Write(db => db.Execute("UPDATE meetings SET speaker_check = ?, speaker_hint_dismissed = 0 WHERE id = ?", score, meetingId));

    public void DismissSpeakerHint(long meetingId) =>
        Write(db => db.Execute("UPDATE meetings SET speaker_hint_dismissed = 1 WHERE id = ?", meetingId));

    // MARK: 問答（M§21.8）

    public List<QuestionRecord> Questions(long? meetingId) => Read(db =>
        (meetingId is long id
            ? db.Query("SELECT * FROM questions WHERE meeting_id = ? ORDER BY id", id)
            : db.Query("SELECT * FROM questions WHERE meeting_id IS NULL ORDER BY id"))
        .Select(r => new QuestionRecord
        {
            Id = r.Long("id"), MeetingId = r.LongOrNull("meeting_id"), Scope = r.Str("scope"), Question = r.Str("question"),
            Answer = r.Str("answer"), Provider = r.Str("provider"), CreatedAt = r.Str("created_at"),
        }).ToList());

    public void SaveQuestion(QuestionRecord q) => Write(db =>
    {
        db.Execute("INSERT INTO questions (meeting_id, scope, question, answer, provider, created_at) VALUES (?, ?, ?, ?, ?, ?)",
            q.MeetingId, q.Scope, q.Question, q.Answer, q.Provider, q.CreatedAt);
        q.Id = db.LastInsertRowId;
    });

    public void DeleteQuestions(long? meetingId) => Write(db =>
    {
        if (meetingId is long id) db.Execute("DELETE FROM questions WHERE meeting_id = ?", id);
        else db.Execute("DELETE FROM questions WHERE meeting_id IS NULL");
    });
}
