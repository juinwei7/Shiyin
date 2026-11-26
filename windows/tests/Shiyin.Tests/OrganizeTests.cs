using System.Text.Json;
using Shiyin.Data;
using Shiyin.Model;
using Shiyin.Services;
using Shiyin.Text;
using Xunit;

namespace Shiyin.Tests;

/// <summary>
/// 會議的分類、排序（R97、R98、M§71、§72、design-windows §11.30、§11.31）：資料夾的條件和建議跟 Mac 的標準答案相同；
/// 資料庫的操作、列表的排序分組篩選；拿掉定期會議的 migration v14（R99、M§73、D177）；資料夾改用 id 的 migration v18（R118、M§92）
/// </summary>
public sealed class OrganizeTests : IDisposable
{
    readonly TempHome home = new();
    readonly AppDatabase db;

    public OrganizeTests()
    {
        db = new AppDatabase(Path.Combine(home.Path, "shiyin.sqlite"));
        db.SeedTemplates();
    }

    public void Dispose()
    {
        db.Dispose();
        home.Dispose();
    }

    static IEnumerable<JsonElement> Items(string path) => Golden.ExpectedJson(path).EnumerateArray();

    static HashSet<long> Ids(JsonElement? e) => e is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray().Select(x => x.GetInt64()).ToHashSet() : [];

    // MARK: 標準答案

    /// <summary>標題的共同詞（資料夾的建議用，M§72；原本是定期會議的預設名稱，D177 拿掉後改名 common-word）</summary>
    [Fact]
    public void CommonWordMatchesMac()
    {
        var cases = Items("util/common-word.json").ToList();
        Assert.NotEmpty(cases);
        foreach (var e in cases)
            Assert.Equal(e.Get("word") is { ValueKind: JsonValueKind.String } w ? w.GetString() : null, MeetingSimilarity.CommonWord(e.Strings("titles")!));
    }

    /// <summary>資料夾的條件（R98.1）、建議（R98.3）</summary>
    [Fact]
    public void FolderRulesAndSuggestionsMatchMac()
    {
        var rule = new FolderRule { FolderId = 1, Keywords = ["週會", "sync"], People = [1, 2], TemplateId = "one-on-one" };
        var cases = Items("util/folder-rule-match.json").ToList();
        Assert.Equal(6, cases.Count);
        foreach (var c in cases)
            Assert.Equal(c.Bool("matches"), rule.Matches(c.Str("title")!, Ids(c.Get("attended")), c.Str("templateId")!));

        var expected = Golden.ExpectedJson("util/folder-suggest.json");
        var others = expected.GetProperty("others").EnumerateArray()
            .Select(o => new FolderSuggester.Input(o.Long("id")!.Value, o.Long("folderId"), o.Str("folder")!, o.Str("title")!, Ids(o.Get("people")))).ToList();
        foreach (var c in expected.GetProperty("cases").EnumerateArray())
        {
            var s = FolderSuggester.Best(new FolderSuggester.Input(c.Long("id")!.Value, null, "", c.Str("title")!, Ids(c.Get("people"))), others, NaturalComparer.Instance);
            Assert.Equal(c.Get("folder") is { ValueKind: JsonValueKind.String } f ? f.GetString() : null, s?.Folder);
            Assert.Equal(c.Long("folderId"), s?.FolderId);
            Assert.Equal((int)c.Long("count")!.Value, s?.Count ?? 0);
            var reason = c.GetProperty("reason");
            Assert.Equal(reason.Str("title"), s?.Title);
            Assert.Equal(reason.Get("people") is { } people ? people.EnumerateArray().Select(x => x.GetInt64()).ToList() : null, s?.People?.ToList());
        }
    }

    // MARK: 資料庫

    long Meeting(string title, string date, string source = "import", string createdAt = "2026-11-01T00:00:00Z", long? folder = null)
    {
        var m = db.InsertMeeting(new Meeting { Title = title, MeetingDate = date, Source = source, CreatedAt = createdAt, SourceFilename = "x.wav", FolderId = folder }, null);
        return m.Id!.Value;
    }

    /// <summary>會議的說話者連到人物（用聲紋的人物表）</summary>
    void Attend(long meetingId, params long[] people)
    {
        db.Write(d =>
        {
            foreach (var p in people)
            {
                if (d.Scalar("SELECT 1 FROM people WHERE id = ?", p) is null)
                    d.Execute("INSERT INTO people (id, name, created_at) VALUES (?, ?, ?)", p, $"P{p}", Clock.Now());
                var idx = (long)(d.Scalar("SELECT COALESCE(MAX(idx), 0) + 1 FROM speakers WHERE meeting_id = ?", meetingId) ?? 1L);
                d.Execute("INSERT INTO speakers (meeting_id, idx, name, name_source, person_id) VALUES (?, ?, ?, 'voiceprint', ?)", meetingId, idx, $"P{p}", p);
            }
        });
    }

    /// <summary>
    /// 資料夾的條件（M§72）：儲存、都空時移除；符合的不在資料夾的會議；自動放（名稱排第一的、已經決定過的不動、自己移過的不動，回傳資料夾 id）；
    /// 改名只改資料夾那一筆（R118）、刪除資料夾時條件一起刪
    /// </summary>
    [Fact]
    public void FolderRulesInDatabase()
    {
        var (product, people, empty, other) = (db.CreateFolder("產品"), db.CreateFolder("A 人"), db.CreateFolder("空的"), db.CreateFolder("別的"));
        var weekly = Meeting("產品週會", "2026-11-02");
        var sync = Meeting("Design Sync", "2026-11-03");
        var interview = Meeting("面試", "2026-11-04");
        var filed = Meeting("週會（已在資料夾）", "2026-11-05", folder: other);
        Attend(interview, 1, 2, 3);
        db.SaveFolderRule(new FolderRule { FolderId = product, Keywords = ["週會", "sync/"] });
        db.SaveFolderRule(new FolderRule { FolderId = people, People = [1, 2] });
        db.SaveFolderRule(new FolderRule { FolderId = empty });
        Assert.Equal([product, people], db.FolderRules().Select(r => r.FolderId));
        Assert.Equal("[\"週會\",\"sync\\/\"]", db.Read(d => d.Scalar("SELECT keywords FROM folder_rules WHERE folder_id = ?", product)));
        Assert.Equal(["週會", "sync/"], db.FolderRules().First(r => r.FolderId == product).Keywords);
        Assert.Equal([weekly], db.MeetingsMatching(db.FolderRules().First(r => r.FolderId == product)));
        Assert.Equal([interview], db.MeetingsMatching(db.FolderRules().First(r => r.FolderId == people)));
        // 再存一次同一個資料夾的條件是改掉，不是多一筆
        db.SaveFolderRule(new FolderRule { FolderId = product, Keywords = ["週會"] });
        Assert.Equal(2, db.FolderRules().Count);

        Assert.Equal(product, db.AutoFile(weekly));
        Assert.True(db.Meeting(weekly)!.FolderDecided);
        Assert.Null(db.AutoFile(weekly));
        Assert.Null(db.AutoFile(sync));
        Assert.Null(db.AutoFile(filed));
        Assert.Equal(people, db.AutoFile(interview));
        // 自己移出資料夾之後不再自動放
        db.SetFolder([weekly], null);
        Assert.Null(db.Meeting(weekly)!.FolderId);
        Assert.Null(db.AutoFile(weekly));

        // 好幾個資料夾的條件都符合：放名稱排第一的（照名稱，不是照 id）
        var (zeta, alpha) = (db.CreateFolder("Z 組"), db.CreateFolder("A 組"));
        db.SaveFolderRule(new FolderRule { FolderId = zeta, Keywords = ["評審"] });
        db.SaveFolderRule(new FolderRule { FolderId = alpha, Keywords = ["評審"] });
        Assert.Equal(alpha, db.AutoFile(Meeting("設計評審", "2026-11-06")));

        // 改名只改名字：id、條件、會議都不動
        Assert.Equal(product, db.RenameFolder(product, "產品組"));
        Assert.Equal("產品組", db.FolderName(product));
        Assert.Contains(db.FolderRules(), r => r.FolderId == product);
        // 名字是空的不做
        Assert.Equal(people, db.RenameFolder(people, ""));
        Assert.Equal("A 人", db.FolderName(people));
        db.DeleteFolder(people);
        Assert.DoesNotContain(db.FolderRules(), r => r.FolderId == people);
        Assert.Null(db.Meeting(interview)!.FolderId);
        Assert.True(db.Meeting(interview)!.FolderDecided);
        db.SetFolderDecided(sync);
        Assert.True(db.Meeting(sync)!.FolderDecided);
    }

    /// <summary>
    /// 資料夾的資料庫操作（R118、M§92.2）：CreateFolder 已經有就回傳那一個（名字區分大小寫）；改成已經有的名字時合併：會議（垃圾桶裡的也是）移過去、
    /// 目標沒有條件就把條件帶過去、有就用目標的；刪除時會議移回不在資料夾（垃圾桶裡的也是）、條件跟著刪；空的資料夾照樣在
    /// </summary>
    [Fact]
    public void FolderIdsCreateRenameMergeDelete()
    {
        var a = db.CreateFolder("客戶");
        Assert.Equal(a, db.CreateFolder("客戶"));
        var lower = db.CreateFolder("abc");
        Assert.NotEqual(lower, db.CreateFolder("ABC"));
        Assert.Equal(["客戶", "abc", "ABC"], db.Folders().Select(f => f.Name));
        var b = db.CreateFolder("設計");
        var c = db.CreateFolder("例會");
        var m1 = Meeting("m1", "2026-11-01", folder: a);
        var m2 = Meeting("m2", "2026-11-02", folder: b);
        var trashed = Meeting("m3", "2026-11-03", folder: a);
        db.SetTrashed(trashed, true);
        db.SaveFolderRule(new FolderRule { FolderId = a, Keywords = ["客戶"] });
        db.SaveFolderRule(new FolderRule { FolderId = b, Keywords = ["設計"] });

        // 合併進有條件的「設計」：會議（含垃圾桶）移過去、用「設計」的條件、「客戶」不見
        Assert.Equal(b, db.RenameFolder(a, "設計"));
        Assert.Equal([b, b, b], new[] { m1, m2, trashed }.Select(id => db.Meeting(id)!.FolderId!.Value));
        Assert.Equal(["設計"], db.FolderRules().Single(r => r.FolderId == b).Keywords);
        Assert.DoesNotContain(db.Folders(), f => f.Id == a);
        Assert.Single(db.FolderRules());
        // 合併進沒有條件的「例會」：條件帶過去
        Assert.Equal(c, db.RenameFolder(b, "例會"));
        Assert.Equal(["設計"], db.FolderRules().Single().Keywords);
        Assert.Equal(c, db.FolderRules().Single().FolderId);
        Assert.Equal([c, c, c], new[] { m1, m2, trashed }.Select(id => db.Meeting(id)!.FolderId!.Value));
        // 刪除：會議（含垃圾桶）移回不在資料夾，條件一起刪；空的資料夾留著
        db.DeleteFolder(c);
        Assert.All(new[] { m1, m2, trashed }, id => Assert.Null(db.Meeting(id)!.FolderId));
        Assert.Empty(db.FolderRules());
        Assert.Equal(["abc", "ABC"], db.Folders().Select(f => f.Name));
        Assert.Equal("", db.FolderName(c));
        Assert.Equal("", db.FolderName(null));
    }

    /// <summary>
    /// 拿掉定期會議（R99、M§73、D177）的 migration v14：每個定期會議換成同名資料夾（名字頭尾的空白去掉、空的不換），裡面不在資料夾的會議移進去
    /// （已經在別的資料夾的不動），同名的資料夾已經有時合在一起；資料夾條件的定期會議拿掉（條件都空了就刪那一筆）；series、series_rejected 清空、
    /// series_id 都是 null；MigratedSeries 是換掉的名稱（設定檔的鍵照這個換），再開一次是空的。接著跑 v18：資料夾換成 id（MigratedFolders）
    /// </summary>
    [Fact]
    public void MigrationV14TurnsSeriesIntoFolders()
    {
        var path = Path.Combine(home.Path, "v13.sqlite");
        long a, b, c, other;
        // 回到 v13（定期會議還在、資料夾還是名字的時候）：兩個定期會議（一個跟已經有的資料夾同名、一個名字是空的）、拒絕過的組、條件的定期會議
        using (var old = new AppDatabase(path, null, null, migrateUntil: "v13"))
        {
            long Add(string title, string folder = "") => old.Write(d =>
            {
                d.Execute("INSERT INTO meetings (title, meeting_date, source_filename, audio_file, folder, created_at, updated_at) VALUES (?, '2026-11-02', 'x.wav', '', ?, ?, ?)",
                    title, folder, Clock.Now(), Clock.Now());
                return d.LastInsertRowId;
            });
            a = Add("週會 1");
            b = Add("週會 2", folder: "客戶");
            c = Add("設計評審");
            other = Add("面試");
            old.Write(d =>
            {
                var now = Clock.Now();
                d.Execute("INSERT INTO folders (name, created_at) VALUES ('客戶', ?)", now);
                d.Execute("INSERT INTO series (id, name, weekday, minute, interval_weeks, people, created_at) VALUES (1, ' 產品週會 ', 2, 600, 1, '[1,2]', ?)", now);
                d.Execute("INSERT INTO series (id, name, weekday, minute, interval_weeks, people, created_at) VALUES (2, '設計', 5, NULL, 2, '[]', ?)", now);
                d.Execute("INSERT INTO series (id, name, weekday, minute, interval_weeks, people, created_at) VALUES (3, '  ', 3, NULL, 1, '[]', ?)", now);
                d.Execute("UPDATE meetings SET series_id = 1 WHERE id IN (?, ?)", a, b);
                d.Execute("UPDATE meetings SET series_id = 2 WHERE id = ?", c);
                d.Execute("UPDATE meetings SET series_id = 3 WHERE id = ?", other);
                d.Execute("INSERT OR IGNORE INTO folders (name, created_at) VALUES ('設計', ?)", now);
                d.Execute("INSERT INTO series_rejected (key, created_at) VALUES ('5|15|1,3', ?)", now);
                d.Execute("INSERT INTO folder_rules (folder, keywords, people, template_id, series_id, created_at) VALUES ('只有定期會議', '[]', '[]', NULL, 1, ?)", now);
                d.Execute("INSERT INTO folder_rules (folder, keywords, people, template_id, series_id, created_at) VALUES ('產品', '[\"週會\"]', '[]', NULL, 1, ?)", now);
            });
        }
        using (var migrated = new AppDatabase(path))
        {
            Assert.Equal(new Dictionary<long, string> { [1] = "產品週會", [2] = "設計" }, migrated.MigratedSeries);
            Assert.Equal(("產品週會", true), (migrated.FolderName(migrated.Meeting(a)!.FolderId), migrated.Meeting(a)!.FolderDecided));
            Assert.Equal("客戶", migrated.FolderName(migrated.Meeting(b)!.FolderId));
            Assert.Equal("設計", migrated.FolderName(migrated.Meeting(c)!.FolderId));
            Assert.Null(migrated.Meeting(other)!.FolderId);
            var names = migrated.Folders().Select(f => f.Name).ToList();
            Assert.Contains("產品週會", names);
            Assert.Single(names, "設計");
            // v18 的對照：每個資料夾（資料夾表、會議用到的、只有條件的「產品」）
            Assert.Equal(migrated.Folders().ToDictionary(f => f.Name, f => f.Id), migrated.MigratedFolders);
            Assert.Contains("產品", migrated.MigratedFolders.Keys);
            // 只有定期會議條件的那一筆刪掉，另一筆留著（定期會議拿掉）
            Assert.Equal(["產品"], migrated.FolderRules().Select(r => migrated.FolderName(r.FolderId)));
            Assert.Equal(["週會"], migrated.FolderRules()[0].Keywords);
            Assert.Equal(0L, migrated.Read(d => d.Scalar("SELECT COUNT(*) FROM folder_rules WHERE series_id IS NOT NULL")));
            Assert.Equal(0L, migrated.Read(d => d.Scalar("SELECT COUNT(*) FROM meetings WHERE series_id IS NOT NULL")));
            Assert.Equal(0L, migrated.Read(d => d.Scalar("SELECT COUNT(*) FROM series")));
            Assert.Equal(0L, migrated.Read(d => d.Scalar("SELECT COUNT(*) FROM series_rejected")));
            // 之後存的條件、會議 series_id 一律是 null
            migrated.SaveFolderRule(new FolderRule { FolderId = migrated.FolderRules()[0].FolderId, Keywords = ["sync"] });
            Assert.Equal(0L, migrated.Read(d => d.Scalar("SELECT COUNT(*) FROM folder_rules WHERE series_id IS NOT NULL")));
        }
        using (var again = new AppDatabase(path))
        {
            Assert.Empty(again.MigratedSeries);
            Assert.Empty(again.MigratedFolders);
        }
        // 新的資料庫（v1 到 v18 一次跑完）：沒有資料夾，對照是空的（設定檔不動）
        using (var fresh = new AppDatabase(Path.Combine(home.Path, "fresh.sqlite"))) Assert.Empty(fresh.MigratedFolders);

        // 設定檔裡的鍵（同 Mac 的 replaceSeriesKeys）：series:<id> 換成 folder:<名稱>，跟原本的重複時留前面的、找不到的拿掉；
        // 展開的照 Ordinal 排序、排序的照原本的順序
        var settings = AppSettings.InMemory();
        settings.Set(AppSettings.SidebarExpandedKey, ["folder:客戶", "series:1", "series:9"]);
        settings.Set(AppSettings.SidebarOrderKey, ["series:2", "folder:客戶", "folder:設計", "series:1"]);
        SidebarCollapse.ReplaceSeriesKeys(settings, new Dictionary<long, string> { [1] = "產品週會", [2] = "設計" });
        Assert.Equal(["folder:客戶", "folder:產品週會"], settings.StringList(AppSettings.SidebarExpandedKey));
        Assert.Equal(["folder:設計", "folder:客戶", "folder:產品週會"], settings.StringList(AppSettings.SidebarOrderKey));
        var writes = 0;
        settings.Changed += _ => writes++;
        SidebarCollapse.ReplaceSeriesKeys(settings, new Dictionary<long, string> { [1] = "產品週會" });
        Assert.Equal(0, writes);
    }

    /// <summary>
    /// 資料夾用 id 辨識的 migration v18（R118、M§92.1、§92.5）：只跑到 v17 的資料庫照 fixtures 的 migrateV18 寫入資料夾表、會議、條件，
    /// 再跑 v18，資料夾、條件、會議跟 Mac 的 db/migrate-v18.json 逐筆相同（id 的順序：資料夾表、會議用到的、條件的；名字區分大小寫；
    /// 垃圾桶裡的會議照樣記得資料夾）
    /// </summary>
    [Fact]
    public void MigrationV18MatchesMac()
    {
        var path = Path.Combine(home.Path, "v17.sqlite");
        var fx = Golden.Fixtures.GetProperty("migrateV18");
        using (var old = new AppDatabase(path, null, null, migrateUntil: "v17"))
            old.Write(d =>
            {
                foreach (var o in fx.GetProperty("folders").EnumerateArray())
                    d.Execute("INSERT INTO folders (name, created_at) VALUES (?, ?)", o.Str("name"), o.Str("created_at"));
                foreach (var o in fx.GetProperty("meetings").EnumerateArray())
                {
                    var created = o.Str("created_at");
                    d.Execute("INSERT INTO meetings (title, folder, deleted_at, created_at, meeting_date, updated_at, source_filename, audio_file) VALUES (?, ?, ?, ?, ?, ?, '', '')",
                        o.Str("title"), o.Str("folder"), o.Str("deleted_at"), created, created, created);
                }
                foreach (var o in fx.GetProperty("folder_rules").EnumerateArray())
                    d.Execute("INSERT INTO folder_rules (folder, keywords, people, template_id, created_at) VALUES (?, ?, ?, ?, ?)",
                        o.Str("folder"), o.Str("keywords"), o.Str("people"), o.Str("template_id"), o.Str("created_at"));
            });
        using var migrated = new AppDatabase(path);
        JNode Rows(string sql) => new JNode.Arr(migrated.Read(d => d.Query(sql)).Select(r =>
        {
            var o = new JNode.Obj();
            foreach (var (name, value) in r.Columns)
                o.Add(name, value switch { null => JNode.NullValue, long l => new JNode.Int(l), _ => new JNode.Str(value.ToString() ?? "") });
            return (JNode)o;
        }));
        var actual = new JNode.Obj()
            .Add("folders", Rows("SELECT id, name, created_at FROM folders ORDER BY id"))
            .Add("folder_rules", Rows("SELECT folder_id, keywords, people, template_id, created_at FROM folder_rules ORDER BY folder_id"))
            .Add("meetings", Rows("SELECT id, title, folder_id, deleted_at FROM meetings ORDER BY id"));
        Golden.Json("db/migrate-v18.json", SwiftJsonWriter.Write(actual, true, false));
        // 交給設定檔的對照：v18 之後的每個資料夾
        Assert.Equal(6, migrated.MigratedFolders.Count);
        Assert.Equal(migrated.MigratedFolders["專案 a"], migrated.Meeting(5)!.FolderId);
        Assert.Equal(0L, migrated.Read(d => d.Scalar("SELECT COUNT(*) FROM pragma_table_info('meetings') WHERE name = 'folder'")));
    }

    /// <summary>
    /// 側欄的鍵換成 id（R118、M§92.1，同 Mac 的 replaceFolderKeys）：展開、排序的 folder:&lt;名稱&gt; → folderid:&lt;id&gt;，依資料夾分組的收合
    /// group:folder-&lt;名稱&gt; → group:folder-&lt;id&gt;、group:folder- → group:folder-none；找不到的名字留著、換完重複的留前面的；
    /// 展開、收合照 Ordinal 排序，排序照原本的順序；沒變不寫
    /// </summary>
    [Fact]
    public void ReplaceFolderKeysWithIds()
    {
        var settings = AppSettings.InMemory();
        settings.Set(AppSettings.SidebarExpandedKey, ["folder:2026", "folder:別台電腦的", "folder:客戶"]);
        settings.Set(AppSettings.SidebarOrderKey, ["folder:客戶", "folderid:1", "folder:2026", "folder:別台電腦的"]);
        settings.Set(AppSettings.SidebarCollapsedKey, ["folders", "group:folder-", "group:folder-客戶", "group:folder-none", "group:month-2026-11"]);
        var ids = new Dictionary<string, long> { ["客戶"] = 1, ["2026"] = 12, ["none"] = 3 };
        SidebarCollapse.ReplaceFolderKeys(settings, ids);
        Assert.Equal(["folder:別台電腦的", "folderid:1", "folderid:12"], settings.StringList(AppSettings.SidebarExpandedKey));
        Assert.Equal(["folderid:1", "folderid:12", "folder:別台電腦的"], settings.StringList(AppSettings.SidebarOrderKey));
        // 名字是「none」的資料夾（以前的 group:folder-none）換成它的 id；不在資料夾那一組換成 group:folder-none
        Assert.Equal(["folders", "group:folder-1", "group:folder-3", "group:folder-none", "group:month-2026-11"], settings.StringList(AppSettings.SidebarCollapsedKey));
        var writes = 0;
        settings.Changed += _ => writes++;
        SidebarCollapse.ReplaceFolderKeys(settings, new Dictionary<string, long> { ["別的"] = 9 });
        Assert.Equal(0, writes);
        Assert.Equal(12, SidebarCollapse.FolderId("folderid:12"));
        Assert.Null(SidebarCollapse.FolderId("folder:12"));
        Assert.Null(SidebarCollapse.FolderId("folderid:x"));
    }

    // MARK: 列表

    static MeetingRow Row(long id, string title, string date, int? duration = null, long? folder = null, int review = 0, string template = "general",
        string? notes = null) =>
        new(new Meeting
        {
            Id = id, Title = title, MeetingDate = date, DurationSeconds = duration, FolderId = folder, TemplateId = template, Notes = notes,
            SourceFilename = "x.wav",
        }, null, review);

    // 列表測試用的資料夾：id → 名字（名字的順序跟 id 相反，看得出分組照名字）
    const long Project = 1, Client = 2, Design = 3, Weekly = 4;
    static readonly Dictionary<long, string> FolderNames = new() { [Project] = "專案", [Client] = "客戶", [Design] = "設計", [Weekly] = "例會" };
    static string FolderName(long? id) => id is long f ? FolderNames.GetValueOrDefault(f) ?? "" : "";

    /// <summary>排序、分組、篩選（R97、M§71）：舊到新反過來、標題照數字大小、長度沒有的排最後；月份、資料夾的分組；篩選都要符合</summary>
    [Fact]
    public void MeetingListSortsGroupsAndFilters()
    {
        // 資料庫照 meeting_date DESC, id DESC 讀出來
        List<MeetingRow> rows =
        [
            Row(5, "第 10 週", "2026-11-10", 600, folder: Project, review: 2),
            Row(4, "第 2 週", "2026-11-03", null, notes: """{"actionItems":[{"owner":"王小明、Amy","task":"寄信","due":"","time":""}]}"""),
            Row(3, "Kickoff", "2026-10-20", 1800, folder: Project, template: "one-on-one"),
            Row(2, "b", "2026-10-02", 60, folder: Client),
            Row(1, "A", "2026-09-30", 60),
        ];
        Assert.Equal([1L, 2L, 3L, 4L, 5L], MeetingList.Sorted(rows, MeetingSort.DateAsc).Select(r => r.Id));
        Assert.Equal([3L, 5L, 2L, 1L, 4L], MeetingList.Sorted(rows, MeetingSort.Duration).Select(r => r.Id));
        // 標題：數字照大小（第 2 週在第 10 週前面）、不分大小寫（A 在 b 前面）；中英文誰在前跟著語系（同 Finder）
        var byTitle = MeetingList.Sorted(rows, MeetingSort.Title).Select(r => r.Id).ToList();
        Assert.True(byTitle.IndexOf(4) < byTitle.IndexOf(5) && byTitle.IndexOf(1) < byTitle.IndexOf(2) && byTitle.IndexOf(2) < byTitle.IndexOf(3));

        var months = MeetingList.Groups(rows, MeetingGrouping.Month, MeetingSort.DateDesc, trash: false);
        Assert.Equal(["2026年11月", "2026年10月", "2026年9月"], months.Select(g => g.Title));
        Assert.Equal([2, 2, 1], months.Select(g => g.Rows.Count));
        var asc = MeetingList.Groups(MeetingList.Sorted(rows, MeetingSort.DateAsc), MeetingGrouping.Month, MeetingSort.DateAsc, trash: false);
        Assert.Equal(["2026年9月", "2026年10月", "2026年11月"], asc.Select(g => g.Title));
        // 依資料夾：組的先後照名字（同 Finder），組的 id 是資料夾 id（R118：改名時收合的狀態不跑掉），不在資料夾的放最後（folder-none）
        var folders = MeetingList.Groups(rows, MeetingGrouping.Folder, MeetingSort.DateDesc, trash: false, FolderName);
        var named = folders.Take(2).Select(g => g.Title).ToList();
        Assert.Equal(named.Order(NaturalComparer.Instance), named);
        Assert.Equal(["folder-1", "folder-2"], folders.Take(2).Select(g => g.Id).Order());
        Assert.Equal(("不在資料夾", 2, "folder-none"), (folders[^1].Title, folders[^1].Rows.Count, folders[^1].Id));
        Assert.Equal([5L, 3L], folders.First(g => g.Title == "專案").Rows.Select(r => r.Id));
        Assert.Equal("group:folder-1", SidebarCollapse.GroupKey(folders.First(g => g.Title == "專案")));
        Assert.Single(MeetingList.Groups(rows, MeetingGrouping.Folder, MeetingSort.DateDesc, trash: true, FolderName));
        Assert.Equal(MeetingGrouping.None, MeetingList.Effective(MeetingGrouping.Folder, Project));
        Assert.DoesNotContain(MeetingGrouping.Folder, MeetingList.Available(Project));
        Assert.Equal([MeetingGrouping.None, MeetingGrouping.Month, MeetingGrouping.Folder], MeetingList.Available(null));
        // 以前存的「依定期會議」（series）讀成不分組
        Assert.Null(MeetingGroupings.Parse("series"));

        var people = new Dictionary<long, HashSet<long>> { [3] = [1, 2], [5] = [2] };
        List<long> Ids(MeetingFilter f, string? me = "王小明") =>
            MeetingList.Filtered(rows, false, null, f, MeetingSort.DateDesc, people, me).Select(r => r.Id).ToList();
        Assert.Equal([5L], Ids(new MeetingFilter(NeedsReview: true)));
        Assert.Equal([4L], Ids(new MeetingFilter(MyActionItems: true)));
        Assert.Empty(Ids(new MeetingFilter(MyActionItems: true), me: null));
        Assert.Equal([5L, 3L], Ids(new MeetingFilter(PersonId: 2)));
        Assert.Equal([3L], Ids(new MeetingFilter(PersonId: 2, TemplateId: "one-on-one")));
        Assert.Empty(Ids(new MeetingFilter(NeedsReview: true, TemplateId: "one-on-one")));
        Assert.Equal([5L, 3L], MeetingList.Filtered(rows, false, Project, new MeetingFilter(), MeetingSort.DateDesc, people, null).Select(r => r.Id));
        // 垃圾桶不篩選
        Assert.Equal(5, MeetingList.Filtered(rows, true, Project, new MeetingFilter(NeedsReview: true), MeetingSort.DateDesc, people, null).Count);

        var overview = new List<PersonOverview> { new(2, "王小明", 2, "2026-11-10", true) };
        var templates = new List<TemplateRecord> { new() { Id = "one-on-one", Name = "一對一", Builtin = false, Definition = "{}" } };
        Assert.Equal("有待確認、有我的待辦、王小明 有參加、範本：一對一",
            MeetingList.FilterSummary(new MeetingFilter(true, true, 2, "one-on-one"), overview, templates));
        Assert.Equal(MeetingSort.Title, MeetingSorts.Parse("title"));
        Assert.Null(MeetingGroupings.Parse("x"));
    }

    /// <summary>
    /// 側欄的收合（R101、M§75、design-windows §11.34）：鍵同 Mac；段落預設展開、列預設收起；值沒變時不寫；
    /// 展開的會議照列表的順序（日期新到舊）、不含別的資料夾；reveal 展開會議列表和那一組
    /// </summary>
    [Fact]
    public void SidebarCollapseKeys()
    {
        var settings = AppSettings.InMemory();
        var writes = 0;
        settings.Changed += _ => writes++;
        Assert.Equal("folderid:2", SidebarCollapse.FolderKey(Client));
        Assert.True(SidebarCollapse.IsSectionExpanded(settings, "folders"));
        Assert.False(SidebarCollapse.IsRowExpanded(settings, "folderid:2"));
        SidebarCollapse.SetSection(settings, "folders", false);
        SidebarCollapse.SetSection(settings, "folders", false);
        SidebarCollapse.SetSection(settings, "meetings", false);
        Assert.Equal(2, writes);
        Assert.Equal(["folders", "meetings"], settings.StringList(AppSettings.SidebarCollapsedKey));
        Assert.False(SidebarCollapse.IsSectionExpanded(settings, "folders"));
        SidebarCollapse.SetRow(settings, SidebarCollapse.FolderKey(Client), true);
        SidebarCollapse.SetRow(settings, SidebarCollapse.FolderKey(Weekly), true);
        Assert.True(SidebarCollapse.IsRowExpanded(settings, "folderid:2"));
        Assert.Equal(["folderid:2", "folderid:4"], settings.StringList(AppSettings.SidebarExpandedKey));

        List<MeetingRow> rows =
        [
            Row(6, "f", "2026-11-12", folder: Client), Row(5, "e", "2026-11-10", folder: Client), Row(4, "d", "2026-11-03"),
            Row(3, "c", "2026-10-20", folder: Client), Row(2, "b", "2026-10-02", folder: Design), Row(1, "a", "2026-09-30", folder: Client),
        ];
        Assert.Equal([6L, 5L, 3L, 1L], SidebarCollapse.PeekRows(rows, Client).Select(r => r.Id));
        Assert.Empty(SidebarCollapse.PeekRows(rows, Weekly));

        var months = MeetingList.Groups(rows, MeetingGrouping.Month, MeetingSort.DateDesc, trash: false);
        Assert.Equal("group:month-2026-10", SidebarCollapse.GroupKey(months[1]));
        SidebarCollapse.SetSection(settings, "group:month-2026-10", false);
        SidebarCollapse.SetSection(settings, "group:month-2026-11", false);
        SidebarCollapse.SetSection(settings, "meetings", false);
        SidebarCollapse.ExpandToShow(settings, 3, months, rows);
        Assert.Equal(["folders", "group:month-2026-11"], settings.StringList(AppSettings.SidebarCollapsedKey));
        // 在資料夾裡（下面的列表沒有）：展開「資料夾」那一段和那個資料夾
        SidebarCollapse.ExpandToShow(settings, 2, [], rows);
        Assert.Equal(["group:month-2026-11"], settings.StringList(AppSettings.SidebarCollapsedKey));
        Assert.Contains("folderid:3", settings.StringList(AppSettings.SidebarExpandedKey));

        // 其他會議（R101.6）：有資料夾的會議時才只放其他的；篩選、依資料夾分組、看某個資料夾時照舊
        var none = new MeetingFilter();
        Assert.True(SidebarCollapse.ShowsOthersOnly(rows, false, null, none, MeetingGrouping.Month));
        Assert.Equal([4L], rows.Where(r => !SidebarCollapse.InFolder(r)).Select(r => r.Id));
        Assert.False(SidebarCollapse.ShowsOthersOnly(rows, false, null, new MeetingFilter(NeedsReview: true), MeetingGrouping.None));
        Assert.False(SidebarCollapse.ShowsOthersOnly(rows, false, null, none, MeetingGrouping.Folder));
        Assert.False(SidebarCollapse.ShowsOthersOnly(rows, false, Client, none, MeetingGrouping.None));
        Assert.False(SidebarCollapse.ShowsOthersOnly(rows, true, null, none, MeetingGrouping.None));
        Assert.False(SidebarCollapse.ShowsOthersOnly([Row(4, "d", "2026-11-03")], false, null, none, MeetingGrouping.None));
    }

    /// <summary>
    /// 拖曳排序（R103.2、M§77、design-windows §11.36）：排過的照 sidebarOrder、新的排最後（照預設）；拖到前面、後面；往上往下移到頭了不動；
    /// 刪除、合併掉的資料夾拿掉鍵（鍵是 folderid:&lt;id&gt;，改名不用換，R118）；拖一場會議時帶的會議；標題收不收會議
    /// </summary>
    [Fact]
    public void SidebarOrderMovesAndRemoves()
    {
        var settings = AppSettings.InMemory();
        var writes = 0;
        settings.Changed += key => { if (key == AppSettings.SidebarOrderKey) writes++; };
        string[] defaults = ["folderid:1", "folderid:2", "folderid:3", "folderid:4"];
        // 沒排過：照預設
        Assert.Equal(defaults, SidebarDrag.Sorted(settings, defaults));
        // 「設計」拖到「產品週會」上半：排到最前面，整段寫回
        Assert.True(SidebarDrag.Move(settings, SidebarDrag.Sorted(settings, defaults), "folderid:3", "folderid:1", before: true));
        Assert.Equal(["folderid:3", "folderid:1", "folderid:2", "folderid:4"], settings.StringList(AppSettings.SidebarOrderKey));
        // 新的資料夾排在最後，照預設的順序
        string[] more = ["folderid:1", "folderid:5", "folderid:2", "folderid:3", "folderid:4", "folderid:6"];
        Assert.Equal(["folderid:3", "folderid:1", "folderid:2", "folderid:4", "folderid:5", "folderid:6"], SidebarDrag.Sorted(settings, more));
        // 拖到下半＝放在後面；拖到自己、拖到不在這一段的不動
        Assert.True(SidebarDrag.Move(settings, SidebarDrag.Sorted(settings, defaults), "folderid:3", "folderid:2", before: false));
        Assert.Equal(["folderid:1", "folderid:2", "folderid:3", "folderid:4"], SidebarDrag.Sorted(settings, defaults));
        Assert.False(SidebarDrag.Move(settings, defaults, "folderid:3", "folderid:3", before: true));
        Assert.False(SidebarDrag.Move(settings, defaults, "folderid:3", "folderid:99", before: true));
        // 往上、往下移一格（Alt+↑／↓）；到頭了不動、不寫
        writes = 0;
        Assert.True(SidebarDrag.MoveBy(settings, SidebarDrag.Sorted(settings, defaults), "folderid:4", -1));
        Assert.Equal(["folderid:1", "folderid:2", "folderid:4", "folderid:3"], SidebarDrag.Sorted(settings, defaults));
        Assert.True(SidebarDrag.MoveBy(settings, SidebarDrag.Sorted(settings, defaults), "folderid:2", 1));
        Assert.Equal(["folderid:1", "folderid:4", "folderid:2", "folderid:3"], SidebarDrag.Sorted(settings, defaults));
        Assert.False(SidebarDrag.MoveBy(settings, SidebarDrag.Sorted(settings, defaults), "folderid:1", -1));
        Assert.False(SidebarDrag.MoveBy(settings, SidebarDrag.Sorted(settings, defaults), "folderid:3", 1));
        Assert.Equal(2, writes);
        // 資料夾改名：鍵換掉、位置不變；改成已經有的資料夾（合併）時拿掉舊的；刪除拿掉；沒排過的資料夾改名不寫
        // 刪除、合併掉的資料夾拿掉它的鍵（改名不用動：鍵是 id，R118）；不在裡面的不寫
        SidebarDrag.Remove(settings, "folderid:1");
        Assert.Equal(["folderid:4", "folderid:2", "folderid:3"], settings.StringList(AppSettings.SidebarOrderKey));
        writes = 0;
        SidebarDrag.Remove(settings, "folderid:99");
        Assert.Equal(0, writes);

        // 拖一場會議：在多選裡就是整個多選，不然只有它
        Assert.Equal([5L, 3L, 8L], SidebarDrag.DragIds(3, [5, 3, 8]));
        Assert.Equal([4L], SidebarDrag.DragIds(4, [5, 3, 8]));
        Assert.Equal([4L], SidebarDrag.DragIds(4, []));
        Assert.Equal([5L, 3L], SidebarDrag.Decode(SidebarDrag.Encode([5, 3])));
        Assert.Empty(SidebarDrag.Decode("x,"));
        // 列表的標題：看資料夾、「其他會議」時收；看所有會議、垃圾桶、人物時不收
        Assert.True(SidebarDrag.HeaderAccepts(false, false, 2, false));
        Assert.True(SidebarDrag.HeaderAccepts(false, false, null, true));
        Assert.False(SidebarDrag.HeaderAccepts(false, false, null, false));
        Assert.False(SidebarDrag.HeaderAccepts(true, false, 2, false));
        Assert.False(SidebarDrag.HeaderAccepts(false, true, null, true));
    }

    /// <summary>重新命名的規則（R102、M§76）：空白、沒改不能存（新資料夾有字就能建立）；資料夾同名、人物同名（大小寫不分）是合併；鍵同 Mac</summary>
    [Fact]
    public void RenameRules()
    {
        Assert.False(Renaming.CanSave(new RenameTarget.Folder(Client), "客戶", " 客戶 "));
        Assert.False(Renaming.CanSave(new RenameTarget.Meeting(1), "週會", "  "));
        Assert.True(Renaming.CanSave(new RenameTarget.Meeting(1), "週會", "週會 2"));
        Assert.True(Renaming.CanSave(new RenameTarget.NewFolder([1]), "", "客戶"));
        Assert.False(Renaming.CanSave(new RenameTarget.NewFolder([1]), "", " "));
        (long, string)[] folders = [(Client, "客戶"), (Design, "設計")];
        (long, string)[] people = [(1, "王小明"), (2, "Amy")];
        Assert.Equal("設計", Renaming.Conflict(new RenameTarget.Folder(Client), " 設計", folders, people));
        Assert.Null(Renaming.Conflict(new RenameTarget.Folder(Client), "客戶", folders, people));
        Assert.Null(Renaming.Conflict(new RenameTarget.Folder(Client), "新專案", folders, people));
        // 資料夾的名字區分大小寫（同資料庫的 UNIQUE）：只差大小寫不算同名
        Assert.Null(Renaming.Conflict(new RenameTarget.Folder(Client), "abc", [(Client, "客戶"), (Design, "ABC")], people));
        Assert.Equal("客戶", Renaming.Conflict(new RenameTarget.NewFolder([3]), "客戶", folders, people));
        Assert.Equal("Amy", Renaming.Conflict(new RenameTarget.Person(1), "amy", folders, people));
        Assert.Null(Renaming.Conflict(new RenameTarget.Person(2), "AMY", folders, people));
        // 「+」建空的資料夾（R104）：同名的已經有就不能建；從會議的選單建的照舊可以（移進那個資料夾）
        Assert.True(Renaming.IsEmptyNewFolder(new RenameTarget.NewFolder([])));
        Assert.False(Renaming.IsEmptyNewFolder(new RenameTarget.NewFolder([3])));
        Assert.Equal("客戶", Renaming.Conflict(new RenameTarget.NewFolder([]), "客戶", folders, people));
        Assert.False(Renaming.CanSave(new RenameTarget.NewFolder([]), "", "客戶", "客戶"));
        Assert.True(Renaming.CanSave(new RenameTarget.NewFolder([]), "", "新專案", null));
        Assert.True(Renaming.CanSave(new RenameTarget.NewFolder([3]), "", "客戶", "客戶"));
        Assert.Equal("newFolder([])", new RenameTarget.NewFolder([]).Key);
        Assert.Equal("meeting(42)", new RenameTarget.Meeting(42).Key);
        Assert.Equal("folder(2)", new RenameTarget.Folder(Client).Key);
        Assert.Equal("newFolder([1, 2])", new RenameTarget.NewFolder([1, 2]).Key);
        Assert.Equal("bookmark(5)", new RenameTarget.Bookmark(5).Key);
    }

    /// <summary>側欄的資料夾（有條件的沒有會議也列出來）、建議的文字、條件的說明（M§72）</summary>
    [Fact]
    public void OrganizingHelpers()
    {
        // 側欄的資料夾：資料夾表裡的每一個（R104 的空資料夾、只有條件的也在表裡，R118），場數不含別的資料夾，照名稱排
        List<MeetingRow> rows = [Row(1, "a", "2026-11-02", folder: 7), Row(2, "b", "2026-11-02"), Row(3, "c", "2026-x"), Row(4, "d", "2026-11-03", folder: 7)];
        var folders = Organizing.Folders(rows, [new Folder(7, "產品", ""), new Folder(8, "空的資料夾", ""), new Folder(9, "A 組", "")]);
        Assert.Equal(3, folders.Count);
        Assert.Contains((7L, "產品", 2), folders);
        Assert.Contains((8L, "空的資料夾", 0), folders);
        Assert.Contains((9L, "A 組", 0), folders);
        Assert.Equal(folders.Select(f => f.Name).Order(NaturalComparer.Instance), folders.Select(f => f.Name));

        var people = new List<PersonOverview> { new(2, "陳家豪", 1, null, false), new(3, "Bob", 1, null, false) };
        Assert.Equal("這場很像「客戶」裡的會議：陳家豪、Bob 都參加（2 場）", Organizing.SuggestionText(new FolderSuggestion(Client, "客戶", [2, 3], null, 2), people));
        Assert.Equal("這場很像「設計」裡的會議：標題都有「設計評審」（2 場）", Organizing.SuggestionText(new FolderSuggestion(Design, "設計", null, "設計評審", 2), people));
        var templates = new List<TemplateRecord> { new() { Id = "one-on-one", Name = "一對一", Definition = "{}" } };
        Assert.Equal("自動加入：標題包含 週會、sync；陳家豪、Bob 都參加；範本：一對一",
            Organizing.RuleSummary(new FolderRule { FolderId = Client, Keywords = ["週會", "sync"], People = [2, 3], TemplateId = "one-on-one" },
                people, templates));
        var withNotes = new Meeting { Id = 9, Title = "需求確認", Notes = "{}", MeetingDate = "2026-11-02", SourceFilename = "x" };
        List<MeetingRow> folderRows = [Row(1, "客戶 A 訪談", "2026-11-01", folder: Client), Row(2, "客戶 A 追蹤", "2026-11-01", folder: Client)];
        var attended = new Dictionary<long, HashSet<long>> { [1] = [2, 3], [2] = [1, 2, 3], [9] = [2, 3] };
        Assert.Equal((Client, "客戶"), Organizing.Suggestion(withNotes, folderRows, attended, null, FolderName) is { } s ? (s.FolderId, s.Folder) : default);
        Assert.Null(Organizing.Suggestion(withNotes, folderRows, attended, recordingId: 9, FolderName));
        withNotes.FolderDecided = true;
        Assert.Null(Organizing.Suggestion(withNotes, folderRows, attended, null, FolderName));
    }

    /// <summary>
    /// 以前定期會議的「上次的待辦」（followUps，M§73，D177 拿掉）：解碼時略過，存回去時就沒了；沒有這一段的紀錄存起來一字不差（R99.3）
    /// </summary>
    [Fact]
    public void OldFollowUpsAreDropped()
    {
        const string old = """{"actionItems":[],"custom":[],"decisions":[],"openQuestions":[],"reviewItems":[],"summary":[{"text":"a","time":"00:01"}],"title":"t","topics":[]}""";
        Assert.Equal(old, MeetingNotes.Decode(old)!.JsonString());
        var withFollowUps = old.Replace("\"title\"", "\"followUps\":[{\"task\":\"寄信\",\"owner\":\"王小明\",\"status\":\"done\",\"time\":\"00:05\"}],\"title\"", StringComparison.Ordinal);
        var notes = MeetingNotes.Decode(withFollowUps)!;
        Assert.Equal(old, notes.JsonString());
        Assert.DoesNotContain("寄信", notes.PlainText);
        Assert.True(MeetingNotes.Decode("""{"followUps":[{"task":"x"}]}""")!.IsEmpty);
    }
}
