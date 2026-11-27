using System.Text.Json;
using Shiyin.Data;
using Shiyin.Model;
using Shiyin.Text;
using Shiyin.Transcribe;
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

    static readonly HashSet<string> TimeColumns = ["created_at", "updated_at", "started_at", "finished_at", "deleted_at", "edited_at"];

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
                    // 編輯會議的快照是程式自己的格式（裡面還有時間），同 Mac 只比套用、還原之後的資料表
                    string s => new JNode.Str(TimeColumns.Contains(name) ? "<time>" : table == "meeting_edits" && name == "snapshot" ? "<snapshot>" : s),
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
                    if (col.Name is "snapshot" or "definition" && table.Name != "meeting_edits" && col.Value.ValueKind == JsonValueKind.String)
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
        static HashSet<int> Ints(JsonElement op, string name) =>
            op.TryGetProperty(name, out var array) ? array.EnumerateArray().Select(x => x.GetInt32()).ToHashSet() : [];
        long SegmentId(long meetingId, int seq) => db.Segments(meetingId).First(s => s.Seq == seq).Id!.Value;
        void Result(JsonElement op, JNode node) => Golden.Json(op.Str("file")!, SwiftJsonWriter.Write(node, true, false));
        long GroupId(string? name) => db.GlossaryGroups().FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase))?.Id ?? 0;
        List<long> TermIds(IEnumerable<string> terms)
        {
            var all = db.Glossary();
            return [.. terms.Select(t => all.FirstOrDefault(x => x.Term == t)?.Id).OfType<long>()];
        }

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
                    var meeting = db.InsertMeeting(Golden.WithFolder(db, fx), "m4a");
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
                case "splitSegment":   // 拆成兩句（M§46.1）
                {
                    var mid = Id(op);
                    var newId = db.SplitSegment(mid, SegmentId(mid, (int)op.Long("seq")!), (int)op.Long("at")!, (int?)op.Long("to"), op.Str("label")!);
                    Result(op, new JNode.Obj().Add("segmentId", newId is long n ? new JNode.Int(n) : JNode.NullValue));
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
                case "resolveReviewItems": db.ResolveReviewItems(Id(op), Ints(op, "confirmed"), Ints(op, "dismissed")); break;
                case "reviewLinks":
                {
                    // 待確認的題目對應到哪一條紀錄（R41.1、M§31.1）：跟 Mac 的 reviewLinks() 逐字比對
                    var notes = db.Meeting(Id(op))?.DecodedNotes ?? new MeetingNotes();
                    var links = ReviewLinks.Compute(notes).OrderBy(p => p.Key.Section).ThenBy(p => p.Key.Index).ThenBy(p => p.Key.Sub)
                        .Select(p => (JNode)new JNode.Obj().Add("section", p.Key.SectionName).Add("index", new JNode.Int(p.Key.Index)).Add("sub", new JNode.Int(p.Key.Sub))
                            .Add("questions", new JNode.Arr(p.Value.OrderBy(q => q).Select(q => (JNode)new JNode.Int(q)))));
                    Result(op, new JNode.Arr(links));
                    break;
                }
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
                case "setFolder": db.SetFolder([Id(op)], db.CreateFolder(op.Str("folder")!)); break;   // 移到資料夾（名稱，沒有就建一個）
                case "appendLiveSegment": db.AppendLiveSegment(Id(op), 0, 1, op.Str("text")!); break;
                // 詞彙表的群組（M§100.1）：群組、詞都用名字指
                case "createGlossaryGroup":
                {
                    var gid = db.CreateGlossaryGroup(op.Str("name")!);
                    if (op.Str("file") is not null) Result(op, new JNode.Obj().Add("id", new JNode.Int(gid)));
                    break;
                }
                case "setGlossaryGroup": db.SetGroup(GroupId(op.Str("group")), TermIds(op.Strings("terms") ?? []), op.Bool("on") ?? true); break;
                case "addTermsInGroup":
                {
                    var added = db.AddTerms(op.Strings("terms") ?? [], GroupId(op.Str("group")));
                    if (op.Str("file") is not null) Result(op, new JNode.Obj().Add("added", new JNode.Int(added)));
                    break;
                }
                case "renameGlossaryGroup":
                {
                    var kept = db.RenameGlossaryGroup(GroupId(op.Str("group")), op.Str("to")!);
                    if (op.Str("file") is not null) Result(op, new JNode.Obj().Add("kept", new JNode.Int(kept)));
                    break;
                }
                case "deleteGlossaryGroup": db.DeleteGlossaryGroup(GroupId(op.Str("group"))); break;
                case "renameTerm": db.RenameTerm(TermIds([op.Str("from")!]).FirstOrDefault(), op.Str("to")!); break;
                case "transcriptionGlossary":   // 轉錄這場時詞彙的順序（M§98.5）
                {
                    var context = db.GlossaryContext(Id(op));
                    List<string> terms = [.. db.Glossary().Select(t => t.Term)];
                    var ranked = GlossaryOrder.Rank(terms, context, db.GlossaryGroupsOf(terms));
                    Result(op, new JNode.Obj().Add("terms", JNode.Of(ranked.Terms)).Add("related", new JNode.Int(ranked.Related))
                        .Add("folderMeetings", new JNode.Int(context.Folder.Count)).Add("recentMeetings", new JNode.Int(context.Recent.Count)));
                    break;
                }
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
                case "prioritizeTranscription":   // 先轉錄這場（M§96.4）：keys 是會議
                    Result(op, new JNode.Obj().Add("moved", new JNode.Arr(db.PrioritizeTranscription(op.GetProperty("keys").EnumerateArray()
                        .Select(k => ids[k.GetString()!])).Select(x => (JNode)new JNode.Int(x)))));
                    break;
                case "transcriptionQueue":
                    Result(op, new JNode.Arr(db.TranscriptionQueue().Select(j => (JNode)new JNode.Obj().Add("id", new JNode.Int(j.Id!.Value))
                        .Add("kind", j.Kind.Code()).Add("meetingId", new JNode.Int(j.MeetingId)))));
                    break;
                case "cancelJobs": Result(op, new JNode.Obj().Add("running", new JNode.Arr(db.CancelJobs(Id(op)).Select(x => (JNode)new JNode.Int(x))))); break;
                case "setTrashed": db.SetTrashed(Id(op), op.Bool("trashed") ?? false); break;
                case "rebuildSearchIndex": db.RebuildSearchIndex(); break;
                case "addDocument":   // 參考文件（M§39.1）
                    db.AddDocument(Id(op), op.Str("name")!, DocumentKinds.Parse(op.Str("kind"))!.Value, op.Strings("pages") ?? [], op.Bool("ocr") ?? false);
                    break;
                case "deleteDocument":
                {
                    var list = db.Documents(Id(op));
                    var deleted = db.DeleteDocument(list[(int)op.Long("index")!].Id!.Value);
                    Result(op, new JNode.Obj().Add("deleted", JNode.Of(deleted?.Name)));
                    break;
                }
                case "deleteMeeting": db.DeleteMeeting(Id(op)); break;
                // 編輯會議（M§81.1）：pieces 是 [會議的 key, 開始, 結束]，cut 是新時間軸上的切點
                case "applyEdit":
                {
                    var pieces = op.GetProperty("pieces").EnumerateArray().Select(p => new EditPlan.Piece(ids[p[0].GetString()!], p[1].GetDouble(), p[2].GetDouble())).ToList();
                    double? cut = op.Get("cut") is { ValueKind: JsonValueKind.Number } c ? c.GetDouble() : null;
                    var outcome = db.ApplyEdit(Id(op), new EditPlan(pieces), cut, op.Str("splitTitle") ?? "", null);
                    Result(op, new JNode.Obj().Add("created", outcome?.Created?.Id is long created ? new JNode.Int(created) : JNode.NullValue)
                        .Add("firstEdit", JNode.Of(outcome?.FirstEdit ?? false))
                        .Add("documentCopies", new JNode.Arr((outcome?.DocumentCopies ?? []).Select(d => (JNode)new JNode.Arr(new List<JNode> { new JNode.Str(d.From), new JNode.Str(d.To) })))));
                    break;
                }
                case "restoreEdit":   // 還原成編輯前的樣子（M§81.3）
                {
                    var r = db.RestoreEdit(Id(op));
                    static JNode Longs(IEnumerable<long> xs) => new JNode.Arr(xs.Select(x => (JNode)new JNode.Int(x)));
                    Result(op, r is null ? JNode.NullValue : new JNode.Obj().Add("trashed", Longs(r.Trashed)).Add("untrashed", Longs(r.Untrashed))
                        .Add("removedDocuments", new JNode.Arr(r.RemovedDocuments.Select(d => (JNode)new JNode.Str(d)))).Add("editsAudio", r.EditsAudio)
                        .Add("restoredAudio", r.RestoredAudio));
                    break;
                }
                // 人物頁（M§60.1、design-windows §11.22）：「我」、一起開過的會議、負責的待辦
                case "enrollVoiceprint":
                    db.EnrollVoiceprint(op.Str("name")!, [.. op.GetProperty("embedding").EnumerateArray().Select(x => (float)x.GetDouble())]);
                    break;
                case "peopleOverview":
                    Result(op, new JNode.Arr(db.PeopleOverview().Select(p => (JNode)new JNode.Obj().Add("id", new JNode.Int(p.Id)).Add("name", p.Name)
                        .Add("meetings", new JNode.Int(p.MeetingCount)).Add("last", JNode.Of(p.LastMeetingDate)).Add("me", JNode.Of(p.IsMe)))));
                    break;
                case "personMeetings":
                {
                    var o = new JNode.Obj();
                    foreach (var person in db.PeopleOverview())
                        o.Add(person.Name, new JNode.Arr(db.PersonMeetings(person.Id).Select(m => (JNode)new JNode.Obj().Add("meetingId", new JNode.Int(m.Id))
                            .Add("date", m.Meeting.MeetingDate).Add("title", m.Meeting.DisplayTitle).Add("seconds", JNode.Of(m.Seconds)))));
                    Result(op, o);
                    break;
                }
                case "personActionItems":
                {
                    var o = new JNode.Obj();
                    foreach (var name in op.GetProperty("names").EnumerateArray().Select(x => x.GetString()!))
                        o.Add(name, new JNode.Arr(db.PersonActionItems(name).Select(a => (JNode)new JNode.Obj().Add("meetingId", new JNode.Int(a.Meeting.Id ?? 0))
                            .Add("date", a.Meeting.MeetingDate).Add("title", a.Meeting.DisplayTitle).Add("index", new JNode.Int(a.Index))
                            .Add("owner", a.Item.Owner).Add("task", a.Item.Task).Add("due", a.Item.Due).Add("time", a.Item.Time))));
                    Result(op, o);
                    break;
                }
                default: Assert.Fail("不認得的步驟 " + op.Str("op")); break;
            }
        }
    }

    /// <summary>
    /// 版本紀錄（R115、M§89）：整理記下範本，舊的那一版帶著它的範本進版本紀錄；清單是目前的在前、由新到舊，
    /// 每一版怎麼來的看比它舊的那一列（最舊的不知道）。改用某一版：目前的存成一版、會議的範本換過去（範本還在時）；復原時範本也回去
    /// </summary>
    [Fact]
    public void NotesHistoryAndUseVersionCarryTemplates()
    {
        using var db = new AppDatabase(Path.Combine(dir, "history.sqlite"), Path.Combine(dir, "backups"));
        db.SeedTemplates();
        var id = db.InsertMeeting(new Meeting { MeetingDate = "2026-11-25", SourceFilename = "a.m4a" }, "m4a").Id!.Value;
        db.SaveNotes(id, new MeetingNotes { Title = "一般" }, "claude", "整理", NotesSource.Generated, "general");
        db.SetTemplate(id, "one-on-one");
        db.SaveNotes(id, new MeetingNotes { Title = "一對一" }, "claude", "重新整理", NotesSource.Generated, "one-on-one");
        db.SaveNotes(id, new MeetingNotes { Title = "一對一（改過）" }, null, "手動編輯");
        Assert.Equal("one-on-one", db.Meeting(id)!.NotesTemplate);
        var history = db.NotesHistory(id);
        Assert.Equal(["one-on-one", "one-on-one", "general"], history.Select(e => e.TemplateId));   // 手動編輯不改範本
        Assert.Equal(["手動編輯", "重新整理", null], history.Select(e => e.Origin));
        Assert.Equal(["一對一（改過）", "一對一", "一般"], history.Select(e => e.Notes.Title));
        Assert.True(history[0].IsCurrent);
        Assert.Null(history[2].Time);

        // 改用一般會議那一版：目前的存成一版（原因「改用以前的版本」、帶一對一），會議的範本換回 general
        Assert.True(db.UseNotesVersion(id, history[2].VersionId!.Value));
        var meeting = db.Meeting(id)!;
        Assert.Equal(("一般", "general", "general", 0), (meeting.DecodedNotes!.Title, meeting.NotesTemplate, meeting.TemplateId, meeting.NotesDraft));
        Assert.Equal(("改用以前的版本", (string?)"one-on-one"), ((string)db.Read(d => d.Scalar("SELECT reason FROM notes_versions WHERE meeting_id = ? ORDER BY id DESC LIMIT 1", id))!,
            (string?)db.Read(d => d.Scalar("SELECT template_id FROM notes_versions WHERE meeting_id = ? ORDER BY id DESC LIMIT 1", id))));
        Assert.Equal(4, db.NotesHistory(id).Count);
        Assert.False(db.UseNotesVersion(id + 1, history[1].VersionId!.Value));   // 別場會議的版本

        // 復原：範本跟著回到上一版的
        Assert.True(db.UndoNotes(id));
        Assert.Equal(("一對一（改過）", (string?)"one-on-one"), (db.Meeting(id)!.DecodedNotes!.Title, db.Meeting(id)!.NotesTemplate));
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
