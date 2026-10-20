using System.Globalization;
using System.Text.RegularExpressions;
using Shiyin.Localization;
using Shiyin.Model;
using Shiyin.Notes;
using Shiyin.Text;

namespace Shiyin.Data;

public enum SearchKind { Title, Segment, Notes, UserNotes }

/// <summary>全文搜尋的一筆結果（R9.1）</summary>
public sealed record SearchHit(long MeetingId, SearchKind Kind, long Ref, string Snippet)
{
    public string Id => $"{MeetingId}-{SearchKinds.Code(Kind)}-{Ref}";
}

public static class SearchKinds
{
    public static string Code(SearchKind k) => k switch
    {
        SearchKind.Title => "title",
        SearchKind.Segment => "segment",
        SearchKind.Notes => "notes",
        _ => "user_notes",
    };

    public static SearchKind? Parse(string code) => code switch
    {
        "title" => SearchKind.Title,
        "segment" => SearchKind.Segment,
        "notes" => SearchKind.Notes,
        "user_notes" => SearchKind.UserNotes,
        _ => null,
    };
}

/// <summary>詞彙表建議（M§17.5）：使用者確認才加進詞彙表</summary>
public sealed record GlossarySuggestion(string Term, GlossarySuggestion.From Source)
{
    public enum From { Participant, Speaker, Correction, Title }
    public string Id => SwiftText.Lower(Term);

    public string SourceLabel => Source switch
    {
        From.Participant => L.T("參加者"),
        From.Speaker => L.T("說話者名字"),
        From.Correction => L.T("校正過的字"),
        _ => L.T("會議標題"),
    };
}

public sealed partial class AppDatabase
{
    // MARK: 搜尋（R9.1）

    /// <summary>搜尋表的 rowid（M§20.11、D27）：段落用段落的 id；標題、會議紀錄、筆記用 2^50 + 會議 id × 4 + 種類</summary>
    const long MetaRowidBase = 1L << 50;

    static long SearchRowid(long meetingId, SearchKind kind) => MetaRowidBase + meetingId * 4 + kind switch
    {
        SearchKind.Title => 1,
        SearchKind.Notes => 2,
        SearchKind.UserNotes => 3,
        _ => throw new ArgumentException("段落的 rowid 就是段落的 id"),  // l10n-skip
    };

    static void IndexSegment(Db db, long meetingId, long id, string text) =>
        db.Execute("INSERT INTO search (rowid, meeting_id, kind, ref, text) VALUES (?, ?, 'segment', ?, ?)", id, meetingId, id, text);

    /// <summary>刪掉這場會議所有段落的索引（段落還在資料庫裡的時候呼叫）</summary>
    static void UnindexSegments(Db db, long meetingId) =>
        db.Execute("DELETE FROM search WHERE rowid IN (SELECT id FROM segments WHERE meeting_id = ?)", meetingId);

    static void UnindexMeeting(Db db, long meetingId) =>
        db.Execute("DELETE FROM search WHERE rowid IN (?, ?, ?)",
            SearchRowid(meetingId, SearchKind.Title), SearchRowid(meetingId, SearchKind.Notes), SearchRowid(meetingId, SearchKind.UserNotes));

    /// <summary>標題、會議紀錄、筆記（ref 是 0）</summary>
    static void Index(Db db, long meetingId, SearchKind kind, string text)
    {
        var rowid = SearchRowid(meetingId, kind);
        db.Execute("DELETE FROM search WHERE rowid = ?", rowid);
        if (text.Length == 0) return;
        db.Execute("INSERT INTO search (rowid, meeting_id, kind, ref, text) VALUES (?, ?, ?, ?, ?)", rowid, meetingId, SearchKinds.Code(kind), 0L, text);
    }

    static void RebuildSearch(Db db)
    {
        db.Execute("DELETE FROM search");
        db.Execute("INSERT INTO search (rowid, meeting_id, kind, ref, text) SELECT id, meeting_id, 'segment', id, text FROM segments");
        // 只讀需要的欄位：migration 裡呼叫時，後面版本才加的欄位還不存在
        foreach (var row in db.Query("SELECT id, title, source_filename, user_notes, notes FROM meetings"))
        {
            var m = new Meeting { Id = row.Long("id"), Title = row.Str("title"), SourceFilename = row.Str("source_filename"),
                Notes = row.StrOrNull("notes"), UserNotes = row.Str("user_notes") };
            Index(db, m.Id!.Value, SearchKind.Title, m.DisplayTitle);
            Index(db, m.Id!.Value, SearchKind.UserNotes, m.UserNotes);
            if (m.DecodedNotes is { } notes) Index(db, m.Id!.Value, SearchKind.Notes, notes.PlainText);
        }
    }

    public void RebuildSearchIndex() => Write(RebuildSearch);

    /// <summary>trigram 至少要 3 個字；1～2 個字改用 LIKE</summary>
    public List<SearchHit> Search(string query, int limit = 200)
    {
        var q = query.Trim();
        if (q.Length == 0) return [];
        return Read(db =>
        {
            List<Row> rows;
            if (SwiftText.Count(q) >= 3)
            {
                var phrase = "\"" + q.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
                rows = db.Query("""
                    SELECT s.meeting_id, s.kind, s.ref, s.text FROM search s
                    JOIN meetings m ON m.id = s.meeting_id AND m.deleted_at IS NULL
                    WHERE search MATCH ? ORDER BY m.meeting_date DESC, s.meeting_id DESC, s.ref LIMIT ?
                    """, phrase, limit);
            }
            else
            {
                rows = db.Query("""
                    SELECT s.meeting_id, s.kind, s.ref, s.text FROM search s
                    JOIN meetings m ON m.id = s.meeting_id AND m.deleted_at IS NULL
                    WHERE s.text LIKE ? ESCAPE '\' ORDER BY m.meeting_date DESC, s.meeting_id DESC, s.ref LIMIT ?
                    """, "%" + q.Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%", limit);
            }
            var hits = new List<SearchHit>();
            foreach (var row in rows)
            {
                if (SearchKinds.Parse(row.Str("kind")) is not { } kind) continue;
                hits.Add(new SearchHit(row.Long("meeting_id"), kind, row.Long("ref"), Snippet(row.Str("text"), q)));
            }
            return hits;
        });
    }

    /// <summary>命中處前 30 個字、後 50 個字（字形叢集）</summary>
    static string Snippet(string text, string query)
    {
        var at = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        var starts = StringInfo.ParseCombiningCharacters(text);
        if (at < 0) return SwiftText.Prefix(text, 80);
        var first = Array.FindLastIndex(starts, s => s <= at);
        var endChar = at + query.Length;
        var last = Array.FindIndex(starts, s => s >= endChar);
        if (last < 0) last = starts.Length;
        var from = Math.Max(0, first - 30);
        var to = Math.Min(starts.Length, last + 50);
        var startIndex = starts[from];
        var endIndex = to < starts.Length ? starts[to] : text.Length;
        return (from > 0 ? "…" : "") + text[startIndex..endIndex].Replace("\n", " ", StringComparison.Ordinal) + (to < starts.Length ? "…" : "");
    }

    // MARK: 範本

    /// <summary>內建範本依固定順序，自訂範本接在後面依名稱排序</summary>
    public List<TemplateRecord> Templates()
    {
        var order = BuiltinTemplates.All.Select(t => t.Id).ToList();
        var list = Read(db => db.Query("SELECT * FROM templates").Select(ToTemplate).ToList());
        list.Sort((a, b) =>
        {
            int x = order.IndexOf(a.Id), y = order.IndexOf(b.Id);
            if (x >= 0 && y >= 0) return x.CompareTo(y);
            if (x >= 0) return -1;
            if (y >= 0) return 1;
            return SwiftText.Compare(a.Name, b.Name);
        });
        return list;
    }

    public void SaveTemplate(TemplateRecord template) => Write(db => db.Execute("""
        INSERT INTO templates (id, name, builtin, definition, updated_at) VALUES (?, ?, ?, ?, ?)
        ON CONFLICT (id) DO UPDATE SET name = excluded.name, builtin = excluded.builtin, definition = excluded.definition, updated_at = excluded.updated_at
        """, template.Id, template.Name, template.Builtin, template.Definition, Clock.Now()));

    public void DeleteTemplate(string id) => Write(db => db.Execute("DELETE FROM templates WHERE id = ?", id));

    /// <summary>中文版內建範本寫進資料庫，名稱或內容改了就覆蓋（比對解析後的內容：Mac 寫的鍵順序不固定）</summary>
    public void SeedTemplates()
    {
        var existing = Templates().ToDictionary(t => t.Id);
        foreach (var t in BuiltinTemplates.All)
        {
            if (existing.TryGetValue(t.Id, out var current) && current.Name == t.Name
                && TemplateDefinition.Decode(current.Definition).SameAs(t.Definition)) continue;
            SaveTemplate(new TemplateRecord { Id = t.Id, Name = t.Name, Builtin = true, Definition = t.Definition.JsonString() });
        }
    }

    // MARK: 工作

    public Job Enqueue(long meetingId, JobKind kind, string? payload = null) => Write(db =>
    {
        var job = new Job { MeetingId = meetingId, Kind = kind, Payload = payload };
        db.Execute("INSERT INTO jobs (meeting_id, kind, status, payload, error, created_at, started_at, finished_at, crashes, error_kind) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
            job.MeetingId, job.Kind.Code(), job.Status.Code(), job.Payload, job.Error, job.CreatedAt, job.StartedAt, job.FinishedAt, job.Crashes, job.ErrorKind);
        job.Id = db.LastInsertRowId;
        return job;
    });

    /// <summary>取出下一個排隊的工作，改成 running</summary>
    public Job? ClaimNext(JobLane lane)
    {
        var kinds = string.Join(", ", JobKinds.All.Where(k => k.Lane() == lane).Select(k => $"'{k.Code()}'"));
        return Write(db =>
        {
            if (db.QueryOne($"SELECT * FROM jobs WHERE status = 'queued' AND kind IN ({kinds}) ORDER BY id LIMIT 1") is not { } r) return null;
            var job = ToJob(r);
            job.Status = JobStatus.Running;
            job.StartedAt = Clock.Now();
            db.Execute("UPDATE jobs SET status = ?, started_at = ? WHERE id = ?", job.Status.Code(), job.StartedAt, job.Id);
            return job;
        });
    }

    /// <summary>errorKind：錯誤的種類，回報問題時用，不含內容（M§21.9）</summary>
    public void Finish(long jobId, JobStatus status, string? error = null, string? errorKind = null) =>
        Write(db => db.Execute("UPDATE jobs SET status = ?, error = ?, error_kind = ?, finished_at = ? WHERE id = ?",
            status.Code(), error, errorKind, Clock.Now(), jobId));

    /// <summary>啟動時：還是 running 的工作表示上次意外結束（M§20.5、D26）；累計 2 次就標成失敗，不到 2 次改回排隊</summary>
    public void RequeueRunning() => Write(db =>
    {
        db.Execute("UPDATE jobs SET crashes = crashes + 1 WHERE status = 'running'");
        db.Execute("UPDATE jobs SET status = 'failed', error = ?, finished_at = ? WHERE status = 'running' AND crashes >= 2",
            L.T("執行這個工作時拾音意外結束了 2 次，已經停止自動重試。可以按「重試」再試一次。"), Clock.Now());
        db.Execute("UPDATE jobs SET status = 'queued', started_at = NULL WHERE status = 'running'");
    });

    /// <summary>正常結束拾音時：執行中的工作改回排隊，不算意外結束</summary>
    public void ReleaseRunning() => Write(db => db.Execute("UPDATE jobs SET status = 'queued', started_at = NULL WHERE status = 'running'"));

    /// <summary>取消這場會議所有排隊中、執行中的工作，回傳原本執行中的工作 id</summary>
    public List<long> CancelJobs(long meetingId) => Write(db =>
    {
        var running = db.Query("SELECT id FROM jobs WHERE meeting_id = ? AND status = 'running'", meetingId).Select(r => r.Long("id")).ToList();
        db.Execute("UPDATE jobs SET status = 'cancelled', finished_at = ? WHERE meeting_id = ? AND status IN ('queued', 'running')", Clock.Now(), meetingId);
        return running;
    });

    public bool HasActiveJob(long meetingId, JobKind kind) => Read(db =>
        (long)db.Scalar("SELECT COUNT(*) FROM jobs WHERE meeting_id = ? AND kind = ? AND status IN ('queued', 'running')", meetingId, kind.Code())! > 0);

    public bool HasQueuedTranscribe(MeetingLanguage language) => Read(db => (long)db.Scalar("""
        SELECT EXISTS (SELECT 1 FROM jobs j JOIN meetings m ON m.id = j.meeting_id
                       WHERE j.status = 'queued' AND j.kind = 'transcribe' AND m.language = ?)
        """, language.Code())! != 0);

    public JobStatus? JobStatusOf(long jobId) => Read(db => db.Scalar("SELECT status FROM jobs WHERE id = ?", jobId) is string s ? JobKinds.ParseStatus(s) : (JobStatus?)null);

    public List<Job> ActiveJobs() => Read(db => db.Query("SELECT * FROM jobs WHERE status IN ('queued', 'running') ORDER BY id").Select(ToJob).ToList());

    // MARK: 詞彙表

    public List<GlossaryTerm> Glossary() => Read(db => db.Query("SELECT * FROM glossary ORDER BY id")
        .Select(r => new GlossaryTerm { Id = r.Long("id"), Term = r.Str("term"), CreatedAt = r.Str("created_at") }).ToList());

    public void AddTerm(string term)
    {
        var trimmed = term.Trim();
        if (trimmed.Length == 0) return;
        Write(db => db.Execute("INSERT OR IGNORE INTO glossary (term, created_at) VALUES (?, ?)", trimmed, Clock.Now()));
    }

    public void DeleteTerm(long id) => Write(db => db.Execute("DELETE FROM glossary WHERE id = ?", id));

    static readonly Regex TitleWord = new("[A-Za-z][A-Za-z0-9.+\\-]{2,}");

    /// <summary>詞彙表建議（M§17.5、R14.5）：從參加者、改過名字的說話者、接受過的校正、中文會議標題裡的英文詞找</summary>
    public List<GlossarySuggestion> GlossarySuggestions(int limit = 12) => Read(db =>
    {
        var found = new Dictionary<string, (string Term, GlossarySuggestion.From Source, int Count)>();
        void Add(string raw, GlossarySuggestion.From source)
        {
            var term = raw.Trim();
            var length = SwiftText.Count(term);
            if (length < 2 || length > 40) return;
            var key = SwiftText.Lower(term);
            found[key] = found.TryGetValue(key, out var e) ? (e.Term, e.Source, e.Count + 1) : (term, source, 1);
        }
        var meetings = db.Query("SELECT * FROM meetings WHERE deleted_at IS NULL").Select(ToMeeting).ToList();
        foreach (var m in meetings) foreach (var p in m.ParticipantList) Add(p, GlossarySuggestion.From.Participant);
        foreach (var r in db.Query("SELECT s.name FROM speakers s JOIN meetings m ON m.id = s.meeting_id WHERE m.deleted_at IS NULL"))
        {
            var name = r.Str("name");
            if (!SpeakerNames.IsDefault(name)) Add(name, GlossarySuggestion.From.Speaker);
        }
        foreach (var r in db.Query("SELECT suggestion FROM corrections WHERE status = 'accepted'"))
        {
            var term = r.Str("suggestion");
            if (SwiftText.Characters(term).Any(c => SwiftText.IsAscii(c) && SwiftText.IsLetter(c))) Add(term, GlossarySuggestion.From.Correction);
        }
        // 中文、日文會議標題裡的英文詞通常是產品名、專案名；英文會議的標題就不拆了
        foreach (var m in meetings.Where(m => m.MeetingLanguage != MeetingLanguage.En))
            foreach (Match match in TitleWord.Matches(m.Title)) Add(match.Value, GlossarySuggestion.From.Title);
        var existing = db.Query("SELECT term FROM glossary").Select(r => SwiftText.Lower(r.Str("term"))).ToHashSet();
        var dismissed = db.Query("SELECT term FROM glossary_dismissed").Select(r => SwiftText.Lower(r.Str("term"))).ToHashSet();
        return found.Where(p => !existing.Contains(p.Key) && !dismissed.Contains(p.Key)).Select(p => p.Value)
            .OrderByDescending(v => v.Count).ThenBy(v => v.Term, SwiftText.Comparer)
            .Take(limit).Select(v => new GlossarySuggestion(v.Term, v.Source)).ToList();
    });

    public void DismissGlossarySuggestion(string term) =>
        Write(db => db.Execute("INSERT OR IGNORE INTO glossary_dismissed (term) VALUES (?)", term));
}
