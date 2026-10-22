using System.Text.Json;
using Shiyin.Data;
using Shiyin.Model;
using Shiyin.Text;
using Xunit;

namespace Shiyin.Tests;

/// <summary>資料庫跟 Mac 相同（R28.5）：照 fixtures.json 的 db 步驟操作，每一步的資料表、回傳值跟 Mac 的結果比對</summary>
public sealed class DatabaseTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "shiyin-golden-" + Guid.NewGuid().ToString("N"));

    public DatabaseTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }

    static readonly HashSet<string> TimeColumns = ["created_at", "updated_at", "started_at", "finished_at", "deleted_at"];

    /// <summary>跟 Mac 的 GoldenDump.dumpTable 一樣：時間欄位有值時是 "&lt;time&gt;"，BLOB 是 base64</summary>
    static string DumpTable(AppDatabase db, string table)
    {
        var sql = table == "search" ? "SELECT rowid, meeting_id, kind, ref, text FROM search ORDER BY rowid" : $"SELECT * FROM {table} ORDER BY rowid";
        var rows = db.Read(d => d.Query(sql));
        var arr = new JNode.Arr(rows.Select(r =>
        {
            var o = new JNode.Obj();
            foreach (var (name, value) in r.Columns)
            {
                o.Add(name, value switch
                {
                    null => JNode.NullValue,
                    long l => TimeColumns.Contains(name) ? new JNode.Str("<time>") : new JNode.Int(l),
                    double x => new JNode.Number(x),
                    string s => new JNode.Str(TimeColumns.Contains(name) ? "<time>" : s),
                    byte[] b => new JNode.Str(Convert.ToBase64String(b)),
                    _ => new JNode.Str(value.ToString() ?? ""),
                });
            }
            return (JNode)o;
        }));
        return SwiftJsonWriter.Write(arr, sortKeys: true, escapeSlashes: false);
    }

    /// <summary>資料表比對：snapshot、definition 欄位裡是 JSON，Mac 的鍵順序不固定，解析後再比</summary>
    static void SameTables(JsonElement expected, Dictionary<string, string> actual, string file)
    {
        foreach (var table in expected.EnumerateObject())
        {
            var a = JsonDocument.Parse(actual[table.Name]).RootElement;
            var e = table.Value.EnumerateArray().ToList();
            var rows = a.EnumerateArray().ToList();
            Assert.True(e.Count == rows.Count, $"{file} {table.Name}：{e.Count} 列 ≠ {rows.Count} 列");
            for (var i = 0; i < e.Count; i++)
            {
                foreach (var col in e[i].EnumerateObject())
                {
                    var where = $"{file} {table.Name}[{i}].{col.Name}";
                    Assert.True(rows[i].TryGetProperty(col.Name, out var v), $"{where}：沒有這個欄位");
                    if (col.Name is "snapshot" or "definition" && col.Value.ValueKind == JsonValueKind.String)
                        Golden.SameEmbeddedJson(col.Value.GetString()!, v.GetString()!, where);
                    else Golden.SameJson(col.Value, v, where);
                }
                Assert.True(e[i].EnumerateObject().Count() == rows[i].EnumerateObject().Count(), $"{file} {table.Name}[{i}]：欄位數不同");
            }
        }
    }

    [Fact]
    public void SameAsMac()
    {
        // 標準答案是 Mac 的門檻算的（Windows 的聲紋模型不同，門檻也不同，D58、M§21.4）
        var (threshold, auto, margin) = (AppDatabase.VoiceprintThreshold, AppDatabase.VoiceprintAutoThreshold, AppDatabase.VoiceprintAutoMargin);
        (AppDatabase.VoiceprintThreshold, AppDatabase.VoiceprintAutoThreshold, AppDatabase.VoiceprintAutoMargin) = (0.65, 0.88, 0.15);
        try { ReplayMacOperations(); }
        finally { (AppDatabase.VoiceprintThreshold, AppDatabase.VoiceprintAutoThreshold, AppDatabase.VoiceprintAutoMargin) = (threshold, auto, margin); }
    }

    void ReplayMacOperations()
    {
        using var db = new AppDatabase(Path.Combine(dir, "golden.sqlite"), Path.Combine(dir, "backups"));
        var ids = new Dictionary<string, long>();
        long Id(JsonElement op) => ids[op.Str("key")!];
        long SegmentId(long meetingId, int seq) => db.Segments(meetingId).First(s => s.Seq == seq).Id!.Value;
        void Result(JsonElement op, JNode node) => Golden.Json(op.Str("file")!, SwiftJsonWriter.Write(node, true, false));

        foreach (var op in Golden.Fixtures.GetProperty("db").EnumerateArray())
        {
            switch (op.Str("op"))
            {
                case "dumpSchema":
                    var sql = db.Read(d => d.Query("SELECT sql FROM sqlite_master WHERE sql IS NOT NULL ORDER BY type, name")).Select(r => r.Str("sql"));
                    Golden.Text(op.Str("file")!, string.Join(";\n", sql) + ";\n");
                    break;
                case "importFixture":
                {
                    var fx = Golden.Meeting(op.Str("key")!);
                    var meeting = db.InsertMeeting(fx.Meeting, "m4a");
                    var mid = meeting.Id!.Value;
                    ids[fx.Key] = mid;
                    var segments = fx.Segments.Select(s => { var c = s.Clone(); c.MeetingId = mid; return c; }).ToList();
                    db.SaveTranscript(mid, segments, fx.Meeting.DurationSeconds ?? 0, segments.Select(s => s.Speaker ?? 0).DefaultIfEmpty(0).Max(),
                        fx.Voices, "breeze");
                    foreach (var (idx, name) in fx.Speakers.OrderBy(p => p.Key)) db.RenameSpeaker(mid, idx, name);
                    foreach (var b in fx.Bookmarks) db.AddBookmark(mid, b.Time, b.Label);
                    if (fx.Notes is { } notes) db.SaveNotes(mid, notes, "claude", "整理", NotesSource.Generated);
                    db.UpdateUserNotes(mid, fx.Meeting.UserNotes);
                    foreach (var term in fx.Glossary) db.AddTerm(term);
                    break;
                }
                case "dumpTables":
                {
                    var file = op.Str("file")!;
                    var tables = op.GetProperty("tables").EnumerateArray().Select(t => t.GetString()!).ToDictionary(t => t, t => DumpTable(db, t));
                    SameTables(Golden.ExpectedJson(file), tables, file);
                    break;
                }
                case "search":
                {
                    var o = new JNode.Obj();
                    foreach (var q in op.GetProperty("queries").EnumerateArray().Select(x => x.GetString()!))
                        o.Add(q, new JNode.Arr(db.Search(q).Select(h => (JNode)new JNode.Obj()
                            .Add("meetingId", new JNode.Int(h.MeetingId)).Add("kind", SearchKinds.Code(h.Kind))
                            .Add("ref", new JNode.Int(h.Ref)).Add("snippet", h.Snippet))));
                    Result(op, o);
                    break;
                }
                case "glossarySuggestions":
                    Result(op, new JNode.Arr(db.GlossarySuggestions().Select(s => (JNode)new JNode.Obj().Add("term", s.Term)
                        .Add("source", s.Source.ToString().ToLowerInvariant()))));
                    break;
                case "learnVoiceprint": db.LearnVoiceprint(Id(op), (int)op.Long("idx")!, op.Str("name")!); break;
                case "matchVoiceprints": Result(op, new JNode.Obj().Add("matched", new JNode.Int(db.MatchVoiceprints(Id(op))))); break;
                case "mergeSpeakers":
                {
                    var r = db.MergeSpeakers(Id(op), (int)op.Long("from")!, (int)op.Long("into")!, op.Str("label")!);
                    Result(op, r is { } x ? new JNode.Obj().Add("from", x.From).Add("into", x.Into) : JNode.NullValue);
                    break;
                }
                case "undoSpeakerEdit": Result(op, new JNode.Obj().Add("undone", JNode.Of(db.UndoSpeakerEdit(Id(op))))); break;
                case "reassignSegments":
                {
                    var mid = Id(op);
                    var segmentIds = op.GetProperty("seqs").EnumerateArray().Select(x => SegmentId(mid, x.GetInt32())).ToList();
                    var target = db.ReassignSegments(mid, segmentIds, (int?)op.Long("to"), op.Str("label")!);
                    Result(op, new JNode.Obj().Add("speaker", new JNode.Int(target)));
                    break;
                }
                case "replace":
                {
                    var r = db.Replace(Id(op), op.Str("find")!, op.Str("with")!);
                    Result(op, new JNode.Obj().Add("segmentsChanged", new JNode.Int(r.SegmentsChanged)).Add("notesChanged", JNode.Of(r.NotesChanged)));
                    break;
                }
                case "saveNotes":
                {
                    var source = op.Str("source") switch { "draft" => NotesSource.Draft, "generated" => NotesSource.Generated, "revised" => NotesSource.Revised, _ => NotesSource.Edit };
                    var notes = MeetingNotes.Decode(op.GetProperty("notes")) ?? new MeetingNotes();
                    var replaced = db.SaveNotes(Id(op), notes, "claude", op.Str("reason")!, source);
                    if (op.Str("file") is not null) Result(op, new JNode.Obj().Add("replacedEditedDraft", JNode.Of(replaced)));
                    break;
                }
                case "undoNotes": Result(op, new JNode.Obj().Add("undone", JNode.Of(db.UndoNotes(Id(op))))); break;
                case "dismissReviewItems": db.DismissReviewItems(Id(op), op.GetProperty("indexes").EnumerateArray().Select(x => x.GetInt32()).ToHashSet()); break;
                case "saveCorrections":
                {
                    var mid = Id(op);
                    db.SaveCorrections(mid, op.GetProperty("items").EnumerateArray().Select(c => new Correction
                    {
                        MeetingId = mid, SegmentId = SegmentId(mid, (int)c.Long("seq")!), Original = c.Str("original")!,
                        Suggestion = c.Str("suggestion")!, Reason = c.Str("reason")!,
                    }).ToList());
                    break;
                }
                case "acceptCorrection":
                    Result(op, new JNode.Obj().Add("accepted", JNode.Of(db.AcceptCorrection(db.Corrections(Id(op))[(int)op.Long("index")!].Id!.Value))));
                    break;
                case "acceptCorrections":
                {
                    var list = db.Corrections(Id(op));
                    var notApplied = db.AcceptCorrections(op.GetProperty("indexes").EnumerateArray().Select(i => list[i.GetInt32()].Id!.Value).ToList());
                    Result(op, new JNode.Obj().Add("notApplied", new JNode.Int(notApplied)));
                    break;
                }
                case "revertCorrection":
                    Result(op, new JNode.Obj().Add("reverted", JNode.Of(db.RevertCorrection(db.Corrections(Id(op))[(int)op.Long("index")!].Id!.Value))));
                    break;
                case "rejectCorrections":
                {
                    var list = db.Corrections(Id(op));
                    db.RejectCorrections(op.GetProperty("indexes").EnumerateArray().Select(i => list[i.GetInt32()].Id!.Value).ToList());
                    break;
                }
                case "addTerm": db.AddTerm(op.Str("term")!); break;
                case "dismissGlossary": db.DismissGlossarySuggestion(op.Str("term")!); break;
                case "enqueue": db.Enqueue(Id(op), JobKinds.Parse(op.Str("kind"))!.Value, op.Str("payload")); break;
                case "claimNext":
                {
                    var job = db.ClaimNext(JobKinds.ParseLane(op.Str("lane"))!.Value);
                    Result(op, job is null ? JNode.NullValue : new JNode.Obj().Add("id", new JNode.Int(job.Id!.Value))
                        .Add("kind", job.Kind.Code()).Add("meetingId", new JNode.Int(job.MeetingId)));
                    break;
                }
                case "requeueRunning": db.RequeueRunning(); break;
                case "cancelJobs": Result(op, new JNode.Obj().Add("running", new JNode.Arr(db.CancelJobs(Id(op)).Select(x => (JNode)new JNode.Int(x))))); break;
                case "setTrashed": db.SetTrashed(Id(op), op.Bool("trashed") ?? false); break;
                case "rebuildSearchIndex": db.RebuildSearchIndex(); break;
                case "deleteMeeting": db.DeleteMeeting(Id(op)); break;
                default: Assert.Fail("不認得的步驟 " + op.Str("op")); break;
            }
        }
    }

    [Fact]
    public void ReopenDoesNotMigrateAgainAndBacksUpBeforeMigration()
    {
        var path = Path.Combine(dir, "old.sqlite");
        var backups = Path.Combine(dir, "backups");
        using (var db = new AppDatabase(path, backups)) db.InsertMeeting(new Meeting { MeetingDate = "2026-10-20", SourceFilename = "a.m4a" }, "m4a");
        Assert.False(Directory.Exists(backups) && Directory.EnumerateFiles(backups).Any());
        // 模擬 Mac 3.5 的資料庫：拿掉 v8、v9 的紀錄，再開一次要先備份
        using (var db = new AppDatabase(path, backups))
            db.Write(d => d.Execute("DELETE FROM grdb_migrations WHERE identifier IN ('v8', 'v9')"));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => new AppDatabase(path, backups));   // v8 的 ALTER TABLE 重複加欄位
        Assert.Single(Directory.EnumerateFiles(backups), f => Path.GetFileName(f).EndsWith("-before-v8.sqlite"));
    }
}
