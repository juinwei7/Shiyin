using System.Text.Json;
using Shiyin.Data;
using Shiyin.Model;
using Shiyin.Services;
using Shiyin.Text;
using Xunit;

namespace Shiyin.Tests;

/// <summary>
/// 會議的分類、排序（R97、R98、M§71、§72、design-windows §11.30、§11.31）：資料夾的條件和建議跟 Mac 的標準答案相同；
/// 資料庫的操作、列表的排序分組篩選；拿掉定期會議的 migration v14（R99、M§73、D177）
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
        var rule = new FolderRule { Folder = "產品", Keywords = ["週會", "sync"], People = [1, 2], TemplateId = "one-on-one" };
        var cases = Items("util/folder-rule-match.json").ToList();
        Assert.Equal(6, cases.Count);
        foreach (var c in cases)
            Assert.Equal(c.Bool("matches"), rule.Matches(c.Str("title")!, Ids(c.Get("attended")), c.Str("templateId")!));

        var expected = Golden.ExpectedJson("util/folder-suggest.json");
        var others = expected.GetProperty("others").EnumerateArray()
            .Select(o => new FolderSuggester.Input(o.Long("id")!.Value, o.Str("folder")!, o.Str("title")!, Ids(o.Get("people")))).ToList();
        foreach (var c in expected.GetProperty("cases").EnumerateArray())
        {
            var s = FolderSuggester.Best(new FolderSuggester.Input(c.Long("id")!.Value, "", c.Str("title")!, Ids(c.Get("people"))), others, NaturalComparer.Instance);
            Assert.Equal(c.Get("folder") is { ValueKind: JsonValueKind.String } f ? f.GetString() : null, s?.Folder);
            Assert.Equal((int)c.Long("count")!.Value, s?.Count ?? 0);
            var reason = c.GetProperty("reason");
            Assert.Equal(reason.Str("title"), s?.Title);
            Assert.Equal(reason.Get("people") is { } people ? people.EnumerateArray().Select(x => x.GetInt64()).ToList() : null, s?.People?.ToList());
        }
    }

    // MARK: 資料庫

    long Meeting(string title, string date, string source = "import", string createdAt = "2026-11-01T00:00:00Z", string folder = "")
    {
        var m = db.InsertMeeting(new Meeting { Title = title, MeetingDate = date, Source = source, CreatedAt = createdAt, SourceFilename = "x.wav", Folder = folder }, null);
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
    /// 資料夾的條件（M§72）：儲存、都空時移除；符合的不在資料夾的會議；自動放（名稱排第一的、已經決定過的不動、自己移過的不動）；
    /// 改名時條件跟著、刪除資料夾時一起刪
    /// </summary>
    [Fact]
    public void FolderRulesInDatabase()
    {
        var weekly = Meeting("產品週會", "2026-11-02");
        var sync = Meeting("Design Sync", "2026-11-03");
        var other = Meeting("面試", "2026-11-04");
        var filed = Meeting("週會（已在資料夾）", "2026-11-05", folder: "別的");
        Attend(other, 1, 2, 3);
        db.SaveFolderRule(new FolderRule { Folder = "產品", Keywords = ["週會", "sync/"] });
        db.SaveFolderRule(new FolderRule { Folder = "A 人", People = [1, 2] });
        db.SaveFolderRule(new FolderRule { Folder = "空的" });
        Assert.Equal(["A 人", "產品"], db.FolderRules().Select(r => r.Folder));
        Assert.Equal("[\"週會\",\"sync\\/\"]", db.Read(d => d.Scalar("SELECT keywords FROM folder_rules WHERE folder = '產品'")));
        Assert.Equal(["週會", "sync/"], db.FolderRules().First(r => r.Folder == "產品").Keywords);
        Assert.Equal([weekly], db.MeetingsMatching(db.FolderRules().First(r => r.Folder == "產品")));
        Assert.Equal([other], db.MeetingsMatching(db.FolderRules().First(r => r.Folder == "A 人")));

        Assert.Equal("產品", db.AutoFile(weekly));
        Assert.True(db.Meeting(weekly)!.FolderDecided);
        Assert.Null(db.AutoFile(weekly));
        Assert.Null(db.AutoFile(sync));
        Assert.Null(db.AutoFile(filed));
        Assert.Equal("A 人", db.AutoFile(other));
        // 自己移出資料夾之後不再自動放
        db.SetFolder([weekly], "");
        Assert.Null(db.AutoFile(weekly));

        db.RenameFolder("產品", "產品組");
        Assert.Equal(["A 人", "產品組"], db.FolderRules().Select(r => r.Folder));
        // 新名稱已經有條件時，舊的條件刪掉
        db.RenameFolder("產品組", "A 人");
        Assert.Equal(["A 人"], db.FolderRules().Select(r => r.Folder));
        Assert.Equal([1L, 2L], db.FolderRules()[0].People);
        db.RenameFolder("A 人", "");
        Assert.Empty(db.FolderRules());
        db.SetFolderDecided(sync);
        Assert.True(db.Meeting(sync)!.FolderDecided);
    }

    /// <summary>
    /// 拿掉定期會議（R99、M§73、D177）的 migration v14：每個定期會議換成同名資料夾（名字頭尾的空白去掉、空的不換），裡面不在資料夾的會議移進去
    /// （已經在別的資料夾的不動），同名的資料夾已經有時合在一起；資料夾條件的定期會議拿掉（條件都空了就刪那一筆）；series、series_rejected 清空、
    /// series_id 都是 null；MigratedSeries 是換掉的名稱（設定檔的鍵照這個換），再開一次是空的
    /// </summary>
    [Fact]
    public void MigrationV14TurnsSeriesIntoFolders()
    {
        var path = Path.Combine(home.Path, "v13.sqlite");
        long a, b, c, other;
        using (var old = new AppDatabase(path))
        {
            long Add(string title, string folder = "") =>
                old.InsertMeeting(new Meeting { Title = title, MeetingDate = "2026-11-02", SourceFilename = "x.wav", Folder = folder }, null).Id!.Value;
            a = Add("週會 1");
            b = Add("週會 2", folder: "客戶");
            c = Add("設計評審");
            other = Add("面試");
            // 回到 v13（定期會議還在的時候）：兩個定期會議（一個跟已經有的資料夾同名、一個名字是空的）、拒絕過的組、條件的定期會議
            old.Write(d =>
            {
                var now = Clock.Now();
                d.Execute("DELETE FROM grdb_migrations WHERE identifier = 'v14'");
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
            Assert.Equal(("產品週會", true), (migrated.Meeting(a)!.Folder, migrated.Meeting(a)!.FolderDecided));
            Assert.Equal("客戶", migrated.Meeting(b)!.Folder);
            Assert.Equal("設計", migrated.Meeting(c)!.Folder);
            Assert.Equal("", migrated.Meeting(other)!.Folder);
            Assert.Contains("產品週會", migrated.FolderNames());
            Assert.Single(migrated.FolderNames(), "設計");
            // 只有定期會議條件的那一筆刪掉，另一筆留著（定期會議拿掉）
            Assert.Equal(["產品"], migrated.FolderRules().Select(r => r.Folder));
            Assert.Equal(["週會"], migrated.FolderRules()[0].Keywords);
            Assert.Equal(0L, migrated.Read(d => d.Scalar("SELECT COUNT(*) FROM folder_rules WHERE series_id IS NOT NULL")));
            Assert.Equal(0L, migrated.Read(d => d.Scalar("SELECT COUNT(*) FROM meetings WHERE series_id IS NOT NULL")));
            Assert.Equal(0L, migrated.Read(d => d.Scalar("SELECT COUNT(*) FROM series")));
            Assert.Equal(0L, migrated.Read(d => d.Scalar("SELECT COUNT(*) FROM series_rejected")));
            // 之後存的條件、會議 series_id 一律是 null
            migrated.SaveFolderRule(new FolderRule { Folder = "產品", Keywords = ["sync"] });
            Assert.Equal(0L, migrated.Read(d => d.Scalar("SELECT COUNT(*) FROM folder_rules WHERE series_id IS NOT NULL")));
        }
        using (var again = new AppDatabase(path)) Assert.Empty(again.MigratedSeries);

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

    // MARK: 列表

    static MeetingRow Row(long id, string title, string date, int? duration = null, string folder = "", int review = 0, string template = "general",
        string? notes = null) =>
        new(new Meeting
        {
            Id = id, Title = title, MeetingDate = date, DurationSeconds = duration, Folder = folder, TemplateId = template, Notes = notes,
            SourceFilename = "x.wav",
        }, null, review);

    /// <summary>排序、分組、篩選（R97、M§71）：舊到新反過來、標題照數字大小、長度沒有的排最後；月份、資料夾的分組；篩選都要符合</summary>
    [Fact]
    public void MeetingListSortsGroupsAndFilters()
    {
        // 資料庫照 meeting_date DESC, id DESC 讀出來
        List<MeetingRow> rows =
        [
            Row(5, "第 10 週", "2026-11-10", 600, folder: "專案", review: 2),
            Row(4, "第 2 週", "2026-11-03", null, notes: """{"actionItems":[{"owner":"王小明、Amy","task":"寄信","due":"","time":""}]}"""),
            Row(3, "Kickoff", "2026-10-20", 1800, folder: "專案", template: "one-on-one"),
            Row(2, "b", "2026-10-02", 60, folder: "客戶"),
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
        var folders = MeetingList.Groups(rows, MeetingGrouping.Folder, MeetingSort.DateDesc, trash: false);
        Assert.Equal(["客戶", "專案"], folders.Take(2).Select(g => g.Title).Order());
        Assert.Equal(("不在資料夾", 2), (folders[^1].Title, folders[^1].Rows.Count));
        Assert.Equal([5L, 3L], folders.First(g => g.Title == "專案").Rows.Select(r => r.Id));
        Assert.Single(MeetingList.Groups(rows, MeetingGrouping.Folder, MeetingSort.DateDesc, trash: true));
        Assert.Equal(MeetingGrouping.None, MeetingList.Effective(MeetingGrouping.Folder, "專案"));
        Assert.DoesNotContain(MeetingGrouping.Folder, MeetingList.Available("專案"));
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
        Assert.Equal([5L, 3L], MeetingList.Filtered(rows, false, "專案", new MeetingFilter(), MeetingSort.DateDesc, people, null).Select(r => r.Id));
        // 垃圾桶不篩選
        Assert.Equal(5, MeetingList.Filtered(rows, true, "專案", new MeetingFilter(NeedsReview: true), MeetingSort.DateDesc, people, null).Count);

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
        Assert.Equal("folder:客戶", SidebarCollapse.FolderKey("客戶"));
        Assert.True(SidebarCollapse.IsSectionExpanded(settings, "folders"));
        Assert.False(SidebarCollapse.IsRowExpanded(settings, "folder:客戶"));
        SidebarCollapse.SetSection(settings, "folders", false);
        SidebarCollapse.SetSection(settings, "folders", false);
        SidebarCollapse.SetSection(settings, "meetings", false);
        Assert.Equal(2, writes);
        Assert.Equal(["folders", "meetings"], settings.StringList(AppSettings.SidebarCollapsedKey));
        Assert.False(SidebarCollapse.IsSectionExpanded(settings, "folders"));
        SidebarCollapse.SetRow(settings, "folder:客戶", true);
        SidebarCollapse.SetRow(settings, "folder:例會", true);
        Assert.True(SidebarCollapse.IsRowExpanded(settings, "folder:客戶"));
        SidebarCollapse.RenameFolder(settings, "客戶", "客戶 A");
        Assert.Equal(["folder:例會", "folder:客戶 A"], settings.StringList(AppSettings.SidebarExpandedKey));
        // 沒展開的資料夾改名不寫
        writes = 0;
        SidebarCollapse.RenameFolder(settings, "設計", "設計 B");
        Assert.Equal(0, writes);

        List<MeetingRow> rows =
        [
            Row(6, "f", "2026-11-12", folder: "客戶"), Row(5, "e", "2026-11-10", folder: "客戶"), Row(4, "d", "2026-11-03"),
            Row(3, "c", "2026-10-20", folder: "客戶"), Row(2, "b", "2026-10-02", folder: "設計"), Row(1, "a", "2026-09-30", folder: "客戶"),
        ];
        Assert.Equal([6L, 5L, 3L, 1L], SidebarCollapse.PeekRows(rows, "客戶").Select(r => r.Id));
        Assert.Empty(SidebarCollapse.PeekRows(rows, "例會"));

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
        Assert.Contains("folder:設計", settings.StringList(AppSettings.SidebarExpandedKey));

        // 其他會議（R101.6）：有資料夾的會議時才只放其他的；篩選、依資料夾分組、看某個資料夾時照舊
        var none = new MeetingFilter();
        Assert.True(SidebarCollapse.ShowsOthersOnly(rows, false, null, none, MeetingGrouping.Month));
        Assert.Equal([4L], rows.Where(r => !SidebarCollapse.InFolder(r)).Select(r => r.Id));
        Assert.False(SidebarCollapse.ShowsOthersOnly(rows, false, null, new MeetingFilter(NeedsReview: true), MeetingGrouping.None));
        Assert.False(SidebarCollapse.ShowsOthersOnly(rows, false, null, none, MeetingGrouping.Folder));
        Assert.False(SidebarCollapse.ShowsOthersOnly(rows, false, "客戶", none, MeetingGrouping.None));
        Assert.False(SidebarCollapse.ShowsOthersOnly(rows, true, null, none, MeetingGrouping.None));
        Assert.False(SidebarCollapse.ShowsOthersOnly([Row(4, "d", "2026-11-03")], false, null, none, MeetingGrouping.None));
    }

    /// <summary>
    /// 拖曳排序（R103.2、M§77、design-windows §11.36）：排過的照 sidebarOrder、新的排最後（照預設）；拖到前面、後面；往上往下移到頭了不動；
    /// 資料夾改名換鍵（合併時拿掉舊的、留那個的位置）、刪除拿掉；拖一場會議時帶的會議；標題收不收會議
    /// </summary>
    [Fact]
    public void SidebarOrderMovesAndFollowsRename()
    {
        var settings = AppSettings.InMemory();
        var writes = 0;
        settings.Changed += key => { if (key == AppSettings.SidebarOrderKey) writes++; };
        string[] defaults = ["folder:產品週會", "folder:客戶", "folder:設計", "folder:例會"];
        // 沒排過：照預設
        Assert.Equal(defaults, SidebarDrag.Sorted(settings, defaults));
        // 「設計」拖到「產品週會」上半：排到最前面，整段寫回
        Assert.True(SidebarDrag.Move(settings, SidebarDrag.Sorted(settings, defaults), "folder:設計", "folder:產品週會", before: true));
        Assert.Equal(["folder:設計", "folder:產品週會", "folder:客戶", "folder:例會"], settings.StringList(AppSettings.SidebarOrderKey));
        // 新的資料夾排在最後，照預設的順序
        string[] more = ["folder:產品週會", "folder:週會二", "folder:客戶", "folder:設計", "folder:例會", "folder:新專案"];
        Assert.Equal(["folder:設計", "folder:產品週會", "folder:客戶", "folder:例會", "folder:週會二", "folder:新專案"], SidebarDrag.Sorted(settings, more));
        // 拖到下半＝放在後面；拖到自己、拖到不在這一段的不動
        Assert.True(SidebarDrag.Move(settings, SidebarDrag.Sorted(settings, defaults), "folder:設計", "folder:客戶", before: false));
        Assert.Equal(["folder:產品週會", "folder:客戶", "folder:設計", "folder:例會"], SidebarDrag.Sorted(settings, defaults));
        Assert.False(SidebarDrag.Move(settings, defaults, "folder:設計", "folder:設計", before: true));
        Assert.False(SidebarDrag.Move(settings, defaults, "folder:設計", "folder:不見了", before: true));
        // 往上、往下移一格（Alt+↑／↓）；到頭了不動、不寫
        writes = 0;
        Assert.True(SidebarDrag.MoveBy(settings, SidebarDrag.Sorted(settings, defaults), "folder:例會", -1));
        Assert.Equal(["folder:產品週會", "folder:客戶", "folder:例會", "folder:設計"], SidebarDrag.Sorted(settings, defaults));
        Assert.True(SidebarDrag.MoveBy(settings, SidebarDrag.Sorted(settings, defaults), "folder:客戶", 1));
        Assert.Equal(["folder:產品週會", "folder:例會", "folder:客戶", "folder:設計"], SidebarDrag.Sorted(settings, defaults));
        Assert.False(SidebarDrag.MoveBy(settings, SidebarDrag.Sorted(settings, defaults), "folder:產品週會", -1));
        Assert.False(SidebarDrag.MoveBy(settings, SidebarDrag.Sorted(settings, defaults), "folder:設計", 1));
        Assert.Equal(2, writes);
        // 資料夾改名：鍵換掉、位置不變；改成已經有的資料夾（合併）時拿掉舊的；刪除拿掉；沒排過的資料夾改名不寫
        SidebarDrag.RenameFolder(settings, "例會", "週例會");
        Assert.Equal(["folder:產品週會", "folder:週例會", "folder:客戶", "folder:設計"], settings.StringList(AppSettings.SidebarOrderKey));
        SidebarDrag.RenameFolder(settings, "週例會", "設計");
        Assert.Equal(["folder:產品週會", "folder:客戶", "folder:設計"], settings.StringList(AppSettings.SidebarOrderKey));
        SidebarDrag.Remove(settings, "folder:產品週會");
        Assert.Equal(["folder:客戶", "folder:設計"], settings.StringList(AppSettings.SidebarOrderKey));
        writes = 0;
        SidebarDrag.RenameFolder(settings, "沒排過", "還是沒排過");
        SidebarDrag.Remove(settings, "folder:不見了");
        Assert.Equal(0, writes);

        // 拖一場會議：在多選裡就是整個多選，不然只有它
        Assert.Equal([5L, 3L, 8L], SidebarDrag.DragIds(3, [5, 3, 8]));
        Assert.Equal([4L], SidebarDrag.DragIds(4, [5, 3, 8]));
        Assert.Equal([4L], SidebarDrag.DragIds(4, []));
        Assert.Equal([5L, 3L], SidebarDrag.Decode(SidebarDrag.Encode([5, 3])));
        Assert.Empty(SidebarDrag.Decode("x,"));
        // 列表的標題：看資料夾、「其他會議」時收；看所有會議、垃圾桶、人物時不收
        Assert.True(SidebarDrag.HeaderAccepts(false, false, "客戶", false));
        Assert.True(SidebarDrag.HeaderAccepts(false, false, null, true));
        Assert.False(SidebarDrag.HeaderAccepts(false, false, null, false));
        Assert.False(SidebarDrag.HeaderAccepts(true, false, "客戶", false));
        Assert.False(SidebarDrag.HeaderAccepts(false, true, null, true));
    }

    /// <summary>重新命名的規則（R102、M§76）：空白、沒改不能存（新資料夾有字就能建立）；資料夾同名、人物同名（大小寫不分）是合併；鍵同 Mac</summary>
    [Fact]
    public void RenameRules()
    {
        Assert.False(Renaming.CanSave(new RenameTarget.Folder("客戶"), "客戶", " 客戶 "));
        Assert.False(Renaming.CanSave(new RenameTarget.Meeting(1), "週會", "  "));
        Assert.True(Renaming.CanSave(new RenameTarget.Meeting(1), "週會", "週會 2"));
        Assert.True(Renaming.CanSave(new RenameTarget.NewFolder([1]), "", "客戶"));
        Assert.False(Renaming.CanSave(new RenameTarget.NewFolder([1]), "", " "));
        string[] folders = ["客戶", "設計"];
        (long, string)[] people = [(1, "王小明"), (2, "Amy")];
        Assert.Equal("設計", Renaming.Conflict(new RenameTarget.Folder("客戶"), " 設計", folders, people));
        Assert.Null(Renaming.Conflict(new RenameTarget.Folder("客戶"), "客戶", folders, people));
        Assert.Null(Renaming.Conflict(new RenameTarget.Folder("客戶"), "新專案", folders, people));
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
        Assert.Equal("folder(\"客戶\")", new RenameTarget.Folder("客戶").Key);
        Assert.Equal("newFolder([1, 2])", new RenameTarget.NewFolder([1, 2]).Key);
        Assert.Equal("bookmark(5)", new RenameTarget.Bookmark(5).Key);
    }

    /// <summary>側欄的資料夾（有條件的沒有會議也列出來）、建議的文字、條件的說明（M§72）</summary>
    [Fact]
    public void OrganizingHelpers()
    {
        List<MeetingRow> rows = [Row(1, "a", "2026-11-02", folder: "產品"), Row(2, "b", "2026-11-02"), Row(3, "c", "2026-x")];
        var folders = Organizing.Folders(rows, [new FolderRule { Folder = "空的資料夾", Keywords = ["x"] }, new FolderRule { Folder = "產品" }]);
        Assert.Equal(2, folders.Count);
        Assert.Contains(("空的資料夾", 0), folders);
        Assert.Contains(("產品", 1), folders);
        // 資料夾表（R104：自己建的空資料夾、會議都移走了的資料夾）也列出來，不重複
        var withTable = Organizing.Folders(rows, [new FolderRule { Folder = "空的資料夾", Keywords = ["x"] }], ["新專案", "產品", ""]);
        Assert.Equal(3, withTable.Count);
        Assert.Contains(("新專案", 0), withTable);
        Assert.Contains(("產品", 1), withTable);

        var people = new List<PersonOverview> { new(2, "陳家豪", 1, null, false), new(3, "Bob", 1, null, false) };
        Assert.Equal("這場很像「客戶」裡的會議：陳家豪、Bob 都參加（2 場）", Organizing.SuggestionText(new FolderSuggestion("客戶", [2, 3], null, 2), people));
        Assert.Equal("這場很像「設計」裡的會議：標題都有「設計評審」（2 場）", Organizing.SuggestionText(new FolderSuggestion("設計", null, "設計評審", 2), people));
        var templates = new List<TemplateRecord> { new() { Id = "one-on-one", Name = "一對一", Definition = "{}" } };
        Assert.Equal("自動加入：標題包含 週會、sync；陳家豪、Bob 都參加；範本：一對一",
            Organizing.RuleSummary(new FolderRule { Folder = "x", Keywords = ["週會", "sync"], People = [2, 3], TemplateId = "one-on-one" },
                people, templates));
        var withNotes = new Meeting { Id = 9, Title = "需求確認", Notes = "{}", MeetingDate = "2026-11-02", SourceFilename = "x" };
        List<MeetingRow> folderRows = [Row(1, "客戶 A 訪談", "2026-11-01", folder: "客戶"), Row(2, "客戶 A 追蹤", "2026-11-01", folder: "客戶")];
        var attended = new Dictionary<long, HashSet<long>> { [1] = [2, 3], [2] = [1, 2, 3], [9] = [2, 3] };
        Assert.Equal("客戶", Organizing.Suggestion(withNotes, folderRows, attended, null)?.Folder);
        Assert.Null(Organizing.Suggestion(withNotes, folderRows, attended, recordingId: 9));
        withNotes.FolderDecided = true;
        Assert.Null(Organizing.Suggestion(withNotes, folderRows, attended, null));
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
