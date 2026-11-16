using System.Text.Json;
using Shiyin.Data;
using Shiyin.Model;
using Shiyin.Services;
using Shiyin.Text;
using Xunit;

namespace Shiyin.Tests;

/// <summary>
/// 會議的分類、排序（R97～R99、M§71～§73、design-windows §11.30～§11.32）：定期會議、資料夾的條件和建議、上次的待辦跟 Mac 的標準答案相同；
/// 資料庫的操作、列表的排序分組篩選
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

    static int? NullableInt(JsonElement? e) => e is { ValueKind: JsonValueKind.Number } n ? n.GetInt32() : null;

    static HashSet<long> Ids(JsonElement? e) => e is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray().Select(x => x.GetInt64()).ToHashSet() : [];

    // MARK: 標準答案

    /// <summary>會議的時間（M§73）：錄音才有開始時間、要跟會議日期同一天（時區固定台北）</summary>
    [Fact]
    public void MeetingTimeMatchesMac()
    {
        var expected = Items("util/meeting-time.json").ToList();
        Assert.NotEmpty(expected);
        foreach (var e in expected)
        {
            var input = e.GetProperty("input");
            var meeting = new Meeting { MeetingDate = input.Str("meetingDate")!, CreatedAt = input.Str("createdAt")!, Source = input.Str("source")!, SourceFilename = "x.wav" };
            var time = MeetingTime.Of(meeting, TimeZoneInfo.FindSystemTimeZoneById(input.Str("timeZone")!));
            Assert.Equal(NullableInt(e.Get("day")), time?.Day);
            Assert.Equal(NullableInt(e.Get("weekday")), time?.Weekday);
            Assert.Equal(NullableInt(e.Get("minute")), time?.Minute);
        }
    }

    /// <summary>找可能的定期會議（M§73、D173）：每週一、隔週四、時間差太多、人不同、不到三場、已經拒絕過；名稱、頻率是繁中介面</summary>
    [Fact]
    public void SeriesDetectMatchesMac()
    {
        var cases = Items("util/series-detect.json").ToList();
        Assert.Equal(5, cases.Count);
        foreach (var c in cases)
        {
            var inputs = c.GetProperty("input").EnumerateArray().Select(i => new SeriesDetector.Input(i.Long("id")!.Value,
                new MeetingTime((int)i.Long("day")!.Value, NullableInt(i.Get("minute"))), Ids(i.Get("people")), i.Str("title")!)).ToList();
            var rejected = (c.Strings("rejected") ?? []).ToHashSet();
            var actual = SeriesDetector.Candidates(inputs, rejected);
            var expected = c.GetProperty("candidates").EnumerateArray().ToList();
            Assert.True(expected.Count == actual.Count, $"{c.Str("name")}：{expected.Count} 組 ≠ {actual.Count} 組");
            for (var i = 0; i < expected.Count; i++)
            {
                var e = expected[i];
                var a = actual[i];
                Assert.Equal(e.Str("key"), a.Key);
                Assert.Equal(e.GetProperty("meetingIds").EnumerateArray().Select(x => x.GetInt64()), a.MeetingIds);
                Assert.Equal((int)e.Long("weekday")!.Value, a.Weekday);
                Assert.Equal(NullableInt(e.Get("minute")), a.Minute);
                Assert.Equal((int)e.Long("intervalWeeks")!.Value, a.IntervalWeeks);
                Assert.Equal(e.GetProperty("people").EnumerateArray().Select(x => x.GetInt64()), a.People);
                Assert.Equal(e.Str("name"), a.Name);
                Assert.Equal(e.Str("frequency"), a.FrequencyText);
            }
        }
        foreach (var e in Items("util/series-common-word.json"))
            Assert.Equal(e.Get("word") is { ValueKind: JsonValueKind.String } w ? w.GetString() : null, SeriesDetector.CommonWord(e.Strings("titles")!));
        foreach (var e in Items("util/series-frequency.json"))
        {
            var (weekday, minute, interval) = ((int)e.Long("weekday")!.Value, NullableInt(e.Get("minute")), (int)e.Long("intervalWeeks")!.Value);
            Assert.Equal(e.Str("text"), SeriesDetector.FrequencyText(weekday, minute, interval));
            Assert.Equal(e.Str("defaultName"), SeriesDetector.DefaultName(["A", "B"], weekday, minute));
        }
    }

    /// <summary>還沒完成的待辦、上一場（R99.4、R99.5）：由舊到新用 followUps 拿掉完成的，再加入這一場的待辦</summary>
    [Fact]
    public void SeriesOpenItemsMatchMac()
    {
        var expected = Golden.ExpectedJson("util/series-open-items.json");
        var meetings = expected.GetProperty("meetings").EnumerateArray().Select(m => new Meeting
        {
            Id = m.Long("id"), MeetingDate = m.Str("meetingDate")!, Notes = m.Str("notes"), SourceFilename = "x.wav",
        }).ToList();
        var open = SeriesOpenItems.Compute(meetings);
        var e = expected.GetProperty("open").EnumerateArray().ToList();
        Assert.Equal(e.Select(x => (x.Long("meeting"), (int)x.Long("index")!.Value, x.Str("task"))),
            open.Select(o => (o.Meeting.Id, o.Index, (string?)o.Item.Task)));
        var previous = expected.GetProperty("previous").EnumerateArray().ToList();
        Assert.Equal(meetings.Count, previous.Count);
        for (var i = 0; i < meetings.Count; i++)
        {
            var p = PreviousMeeting.Of(meetings[i]);
            if (previous[i].ValueKind == JsonValueKind.Null) { Assert.Null(p); continue; }
            Assert.NotNull(p);
            Assert.Equal(previous[i].GetProperty("items").EnumerateArray().Select(x => (x.Str("owner"), x.Str("task"), x.Str("due"))),
                p.Items.Select(x => ((string?)x.Owner, (string?)x.Task, (string?)x.Due)));
            Assert.Equal(previous[i].Strings("openQuestions"), p.OpenQuestions.Select(q => q.Text).ToList());
        }
    }

    /// <summary>整理後的驗證（M§73）：對不到給的待辦的丟掉、同一條只留第一個；沒給上一場時清空</summary>
    [Fact]
    public void FollowUpsValidateMatchesMac()
    {
        var expected = Golden.ExpectedJson("util/followups-validate.json");
        var previous = new PreviousMeeting("t", "2026-11-09", expected.GetProperty("previousItems").EnumerateArray()
            .Select(i => new MeetingNotes.ActionItem(i.Str("owner")!, i.Str("task")!, "")).ToList(), []);
        static List<MeetingNotes.FollowUp> Decode(JsonElement list) => list.EnumerateArray()
            .Select(f => new MeetingNotes.FollowUp(f.Str("task")!, f.Str("owner")!, FollowUpStatuses.Parse(f.Str("status"))!.Value)).ToList();
        static IEnumerable<(string, string, string)> Key(IEnumerable<MeetingNotes.FollowUp> list) => list.Select(f => (f.Task, f.Owner, f.Status.Code()));
        var raw = Decode(expected.GetProperty("input"));
        Assert.Equal(Key(Decode(expected.GetProperty("valid"))), Key(PreviousMeeting.ValidFollowUps(raw, previous)));
        Assert.Equal(Key(Decode(expected.GetProperty("withoutPrevious"))), Key(PreviousMeeting.ValidFollowUps(raw, null)));
    }

    /// <summary>新的一場要不要加入定期會議（M§73）：自動加入、建議、都不是</summary>
    [Fact]
    public void SeriesJoinMatchesMac()
    {
        foreach (var c in Golden.ExpectedJson("util/series-join.json").EnumerateArray())
        {
            var minute = c.GetProperty("minute").ValueKind == JsonValueKind.Null ? (int?)null : c.GetProperty("minute").GetInt32();
            var time = new MeetingTime(c.GetProperty("day").GetInt32(), minute);
            var attended = c.GetProperty("attended").EnumerateArray().Select(x => x.GetInt64()).ToHashSet();
            var infos = c.GetProperty("series").EnumerateArray().Select(s => new SeriesDetector.SeriesInfo(
                s.GetProperty("id").GetInt64(), s.GetProperty("weekday").GetInt32(),
                s.GetProperty("minute").ValueKind == JsonValueKind.Null ? null : s.GetProperty("minute").GetInt32(),
                s.GetProperty("people").EnumerateArray().Select(x => x.GetInt64()).ToHashSet(),
                s.GetProperty("lastDay").ValueKind == JsonValueKind.Null ? null : s.GetProperty("lastDay").GetInt32())).ToList();
            var decision = SeriesDetector.Decide(time, attended, infos);
            var d = c.GetProperty("decision");
            var name = c.Str("name");
            Assert.True((d.TryGetProperty("join", out var j) ? j.GetInt64() : (long?)null) == decision.Join, name);
            Assert.True((d.TryGetProperty("suggest", out var sg) ? sg.GetInt64() : (long?)null) == decision.Suggest, name);
            var shared = d.TryGetProperty("shared", out var sh) ? sh.EnumerateArray().Select(x => x.GetInt64()).ToList() : [];
            Assert.Equal(shared, decision.Shared);
        }
    }

    /// <summary>資料夾的條件（R98.1）、建議（R98.3）</summary>
    [Fact]
    public void FolderRulesAndSuggestionsMatchMac()
    {
        var rule = new FolderRule { Folder = "產品", Keywords = ["週會", "sync"], People = [1, 2], TemplateId = "one-on-one", SeriesId = 3 };
        var cases = Items("util/folder-rule-match.json").ToList();
        Assert.Equal(7, cases.Count);
        foreach (var c in cases)
            Assert.Equal(c.Bool("matches"), rule.Matches(c.Str("title")!, Ids(c.Get("attended")), c.Str("templateId")!, c.Long("seriesId")));

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
    /// 定期會議（M§73）：建立、改名、刪除（會議留著、記進拒絕）；上一場（同系列、比較早、有紀錄）；
    /// 自動加入（星期幾、時間、同一群人、相隔 1～35 天）
    /// </summary>
    [Fact]
    public void SeriesInDatabase()
    {
        var taipei = TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");
        // 2026-11-02、09、16 是週一；10:00 台北 = 02:00Z
        var a = Meeting("週會 1", "2026-11-02", "recording", "2026-11-02T02:00:00Z");
        var b = Meeting("週會 2", "2026-11-09", "recording", "2026-11-09T02:05:00Z");
        var c = Meeting("週會 3", "2026-11-16", "recording", "2026-11-16T02:10:00Z");
        var late = Meeting("下午", "2026-11-16", "recording", "2026-11-16T08:00:00Z");
        var tuesday = Meeting("週二", "2026-11-17", "recording", "2026-11-17T02:00:00Z");
        var bob = Meeting("跟 Bob", "2026-11-16", "recording", "2026-11-16T02:00:00Z");
        foreach (var id in new[] { a, b, c, late, tuesday }) Attend(id, 1, 2, 4);
        Attend(bob, 1, 9);
        var created = db.CreateSeries(new MeetingSeries { Name = "產品週會", Weekday = 2, Minute = 600, People = [1, 2, 4] }, [a, b]);
        Assert.NotNull(created.Id);
        Assert.Equal("[1,2,4]", db.Read(d => d.Scalar("SELECT people FROM series")));
        Assert.Equal([b, a], db.SeriesMeetings(created.Id!.Value).Select(m => m.Id!.Value));

        // 上一場：b 的上一場是 a，要有紀錄
        Assert.Null(db.PreviousSeriesMeeting(db.Meeting(b)!));
        db.SaveNotes(a, new MeetingNotes { Title = "週會 1", ActionItems = [new("小明", "寄信", "")] }, "claude", "整理", NotesSource.Generated);
        Assert.Equal(a, db.PreviousSeriesMeeting(db.Meeting(b)!)!.Id);
        Assert.Null(db.PreviousSeriesMeeting(db.Meeting(a)!));
        Assert.Null(db.PreviousSeriesMeeting(db.Meeting(c)!));

        Assert.Null(db.AutoJoinSeries(late, taipei));
        Assert.Null(db.AutoJoinSeries(tuesday, taipei));
        Assert.Null(db.AutoJoinSeries(bob, taipei));
        Assert.Equal(created.Id, db.AutoJoinSeries(c, taipei)?.Id);
        Assert.Equal(created.Id, db.Meeting(c)!.SeriesId);
        Assert.Null(db.AutoJoinSeries(c, taipei));
        // c 的上一場：b 還沒有紀錄，往前找到 a
        Assert.Equal(a, db.PreviousSeriesMeeting(db.Meeting(c)!)!.Id);

        db.RenameSeries(created.Id!.Value, "PM 週會");
        Assert.Equal("PM 週會", db.Series().Single().Name);
        db.SetSeries([late], created.Id);
        db.SetSeries([late], null);
        Assert.Null(db.Meeting(late)!.SeriesId);
        db.DeleteSeries(created.Id!.Value);
        Assert.Empty(db.Series());
        Assert.Null(db.Meeting(a)!.SeriesId);
        Assert.Contains("2|10|1,2,4", db.RejectedSeriesKeys());
        db.RejectSeries("5|15|1,3");
        db.RejectSeries("5|15|1,3");
        Assert.Equal(2, db.RejectedSeriesKeys().Count);
        Assert.Equal([1L, 2L, 4L], db.MeetingPeople()[a].Order());
    }

    // MARK: 列表

    static MeetingRow Row(long id, string title, string date, int? duration = null, string folder = "", int review = 0, string template = "general",
        string? notes = null, long? series = null) =>
        new(new Meeting
        {
            Id = id, Title = title, MeetingDate = date, DurationSeconds = duration, Folder = folder, TemplateId = template, Notes = notes,
            SeriesId = series, SourceFilename = "x.wav",
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
            Row(3, "Kickoff", "2026-10-20", 1800, folder: "專案", template: "one-on-one", series: 7),
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
        Assert.Equal(MeetingGrouping.None, MeetingList.Effective(MeetingGrouping.Series, null));
        Assert.DoesNotContain(MeetingGrouping.Folder, MeetingList.Available("專案"));
        Assert.DoesNotContain(MeetingGrouping.Series, MeetingList.Available(null));

        var people = new Dictionary<long, HashSet<long>> { [3] = [1, 2], [5] = [2] };
        List<long> Ids(MeetingFilter f, string? me = "王小明") =>
            MeetingList.Filtered(rows, false, null, null, f, MeetingSort.DateDesc, people, me).Select(r => r.Id).ToList();
        Assert.Equal([5L], Ids(new MeetingFilter(NeedsReview: true)));
        Assert.Equal([4L], Ids(new MeetingFilter(MyActionItems: true)));
        Assert.Empty(Ids(new MeetingFilter(MyActionItems: true), me: null));
        Assert.Equal([5L, 3L], Ids(new MeetingFilter(PersonId: 2)));
        Assert.Equal([3L], Ids(new MeetingFilter(PersonId: 2, TemplateId: "one-on-one")));
        Assert.Empty(Ids(new MeetingFilter(NeedsReview: true, TemplateId: "one-on-one")));
        Assert.Equal([3L], MeetingList.Filtered(rows, false, null, 7, new MeetingFilter(), MeetingSort.DateDesc, people, null).Select(r => r.Id));
        Assert.Equal([5L, 3L], MeetingList.Filtered(rows, false, "專案", null, new MeetingFilter(), MeetingSort.DateDesc, people, null).Select(r => r.Id));
        // 垃圾桶不篩選
        Assert.Equal(5, MeetingList.Filtered(rows, true, "專案", 7, new MeetingFilter(NeedsReview: true), MeetingSort.DateDesc, people, null).Count);

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
        Assert.Equal("series:7", SidebarCollapse.SeriesKey(7));
        Assert.True(SidebarCollapse.IsSectionExpanded(settings, "folders"));
        Assert.False(SidebarCollapse.IsRowExpanded(settings, "folder:客戶"));
        SidebarCollapse.SetSection(settings, "folders", false);
        SidebarCollapse.SetSection(settings, "folders", false);
        SidebarCollapse.SetSection(settings, "meetings", false);
        Assert.Equal(2, writes);
        Assert.Equal(["folders", "meetings"], settings.StringList(AppSettings.SidebarCollapsedKey));
        Assert.False(SidebarCollapse.IsSectionExpanded(settings, "folders"));
        SidebarCollapse.SetRow(settings, "folder:客戶", true);
        SidebarCollapse.SetRow(settings, "series:7", true);
        Assert.True(SidebarCollapse.IsRowExpanded(settings, "folder:客戶"));
        SidebarCollapse.RenameFolder(settings, "客戶", "客戶 A");
        Assert.Equal(["folder:客戶 A", "series:7"], settings.StringList(AppSettings.SidebarExpandedKey));
        // 沒展開的資料夾改名不寫
        writes = 0;
        SidebarCollapse.RenameFolder(settings, "設計", "設計 B");
        Assert.Equal(0, writes);

        List<MeetingRow> rows =
        [
            Row(6, "f", "2026-11-12", folder: "客戶"), Row(5, "e", "2026-11-10", folder: "客戶", series: 7), Row(4, "d", "2026-11-03"),
            Row(3, "c", "2026-10-20", folder: "客戶", series: 7), Row(2, "b", "2026-10-02", folder: "設計"), Row(1, "a", "2026-09-30", folder: "客戶"),
        ];
        Assert.Equal([6L, 5L, 3L, 1L], SidebarCollapse.PeekRows(rows, "客戶").Select(r => r.Id));
        Assert.Equal([5L, 3L], SidebarCollapse.PeekRows(rows, 7).Select(r => r.Id));
        Assert.Empty(SidebarCollapse.PeekRows(rows, "例會"));

        var months = MeetingList.Groups(rows, MeetingGrouping.Month, MeetingSort.DateDesc, trash: false);
        Assert.Equal("group:month-2026-10", SidebarCollapse.GroupKey(months[1]));
        SidebarCollapse.SetSection(settings, "group:month-2026-10", false);
        SidebarCollapse.SetSection(settings, "group:month-2026-11", false);
        SidebarCollapse.SetSection(settings, "meetings", false);
        SidebarCollapse.ExpandToShow(settings, 3, months, rows);
        Assert.Equal(["folders", "group:month-2026-11"], settings.StringList(AppSettings.SidebarCollapsedKey));
        // 在資料夾、定期會議裡（下面的列表沒有）：展開「資料夾」那一段和那個定期會議或資料夾
        SidebarCollapse.SetRow(settings, "series:7", false);
        SidebarCollapse.ExpandToShow(settings, 5, [], rows);
        Assert.Equal(["group:month-2026-11"], settings.StringList(AppSettings.SidebarCollapsedKey));
        Assert.Contains("series:7", settings.StringList(AppSettings.SidebarExpandedKey));
        SidebarCollapse.ExpandToShow(settings, 2, [], rows);
        Assert.Contains("folder:設計", settings.StringList(AppSettings.SidebarExpandedKey));

        // 其他會議（R101.6）：有資料夾、定期會議的會議時才只放其他的；篩選、依資料夾分組、看某個資料夾時照舊
        var none = new MeetingFilter();
        Assert.True(SidebarCollapse.ShowsOthersOnly(rows, false, null, null, none, MeetingGrouping.Month));
        Assert.Equal([4L], rows.Where(r => !SidebarCollapse.InFolderOrSeries(r)).Select(r => r.Id));
        Assert.False(SidebarCollapse.ShowsOthersOnly(rows, false, null, null, new MeetingFilter(NeedsReview: true), MeetingGrouping.None));
        Assert.False(SidebarCollapse.ShowsOthersOnly(rows, false, null, null, none, MeetingGrouping.Folder));
        Assert.False(SidebarCollapse.ShowsOthersOnly(rows, false, "客戶", null, none, MeetingGrouping.None));
        Assert.False(SidebarCollapse.ShowsOthersOnly(rows, false, null, 7, none, MeetingGrouping.None));
        Assert.False(SidebarCollapse.ShowsOthersOnly(rows, true, null, null, none, MeetingGrouping.None));
        Assert.False(SidebarCollapse.ShowsOthersOnly([Row(4, "d", "2026-11-03")], false, null, null, none, MeetingGrouping.None));
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
        string[] defaults = ["series:1", "folder:客戶", "folder:設計", "folder:例會"];
        // 沒排過：照預設
        Assert.Equal(defaults, SidebarDrag.Sorted(settings, defaults));
        // 「設計」拖到「產品週會」（series:1）上半：排到最前面，整段寫回
        Assert.True(SidebarDrag.Move(settings, SidebarDrag.Sorted(settings, defaults), "folder:設計", "series:1", before: true));
        Assert.Equal(["folder:設計", "series:1", "folder:客戶", "folder:例會"], settings.StringList(AppSettings.SidebarOrderKey));
        // 新的資料夾、定期會議排在最後，照預設的順序（定期會議在前、資料夾照名稱）
        string[] more = ["series:1", "series:2", "folder:客戶", "folder:設計", "folder:例會", "folder:新專案"];
        Assert.Equal(["folder:設計", "series:1", "folder:客戶", "folder:例會", "series:2", "folder:新專案"], SidebarDrag.Sorted(settings, more));
        // 拖到下半＝放在後面；拖到自己、拖到不在這一段的不動
        Assert.True(SidebarDrag.Move(settings, SidebarDrag.Sorted(settings, defaults), "folder:設計", "folder:客戶", before: false));
        Assert.Equal(["series:1", "folder:客戶", "folder:設計", "folder:例會"], SidebarDrag.Sorted(settings, defaults));
        Assert.False(SidebarDrag.Move(settings, defaults, "folder:設計", "folder:設計", before: true));
        Assert.False(SidebarDrag.Move(settings, defaults, "folder:設計", "folder:不見了", before: true));
        // 往上、往下移一格（Alt+↑／↓）；到頭了不動、不寫
        writes = 0;
        Assert.True(SidebarDrag.MoveBy(settings, SidebarDrag.Sorted(settings, defaults), "folder:例會", -1));
        Assert.Equal(["series:1", "folder:客戶", "folder:例會", "folder:設計"], SidebarDrag.Sorted(settings, defaults));
        Assert.True(SidebarDrag.MoveBy(settings, SidebarDrag.Sorted(settings, defaults), "folder:客戶", 1));
        Assert.Equal(["series:1", "folder:例會", "folder:客戶", "folder:設計"], SidebarDrag.Sorted(settings, defaults));
        Assert.False(SidebarDrag.MoveBy(settings, SidebarDrag.Sorted(settings, defaults), "series:1", -1));
        Assert.False(SidebarDrag.MoveBy(settings, SidebarDrag.Sorted(settings, defaults), "folder:設計", 1));
        Assert.Equal(2, writes);
        // 資料夾改名：鍵換掉、位置不變；改成已經有的資料夾（合併）時拿掉舊的；刪除拿掉；沒排過的資料夾改名不寫
        SidebarDrag.RenameFolder(settings, "例會", "週例會");
        Assert.Equal(["series:1", "folder:週例會", "folder:客戶", "folder:設計"], settings.StringList(AppSettings.SidebarOrderKey));
        SidebarDrag.RenameFolder(settings, "週例會", "設計");
        Assert.Equal(["series:1", "folder:客戶", "folder:設計"], settings.StringList(AppSettings.SidebarOrderKey));
        SidebarDrag.Remove(settings, "series:1");
        Assert.Equal(["folder:客戶", "folder:設計"], settings.StringList(AppSettings.SidebarOrderKey));
        writes = 0;
        SidebarDrag.RenameFolder(settings, "沒排過", "還是沒排過");
        SidebarDrag.Remove(settings, "series:9");
        Assert.Equal(0, writes);

        // 拖一場會議：在多選裡就是整個多選，不然只有它
        Assert.Equal([5L, 3L, 8L], SidebarDrag.DragIds(3, [5, 3, 8]));
        Assert.Equal([4L], SidebarDrag.DragIds(4, [5, 3, 8]));
        Assert.Equal([4L], SidebarDrag.DragIds(4, []));
        Assert.Equal([5L, 3L], SidebarDrag.Decode(SidebarDrag.Encode([5, 3])));
        Assert.Empty(SidebarDrag.Decode("x,"));
        // 列表的標題：看資料夾、定期會議、「其他會議」時收；看所有會議、垃圾桶、人物時不收
        Assert.True(SidebarDrag.HeaderAccepts(false, false, "客戶", null, false));
        Assert.True(SidebarDrag.HeaderAccepts(false, false, null, 1, false));
        Assert.True(SidebarDrag.HeaderAccepts(false, false, null, null, true));
        Assert.False(SidebarDrag.HeaderAccepts(false, false, null, null, false));
        Assert.False(SidebarDrag.HeaderAccepts(true, false, "客戶", null, false));
        Assert.False(SidebarDrag.HeaderAccepts(false, true, null, null, true));
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
        Assert.Null(Renaming.Conflict(new RenameTarget.Series(1), "設計", folders, people));
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

    /// <summary>側欄的資料夾（有條件的沒有會議也列出來）、可能的定期會議的輸入、建議的文字、條件的說明（M§72、§73）</summary>
    [Fact]
    public void OrganizingHelpers()
    {
        List<MeetingRow> rows = [Row(1, "a", "2026-11-02", folder: "產品"), Row(2, "b", "2026-11-02", series: 3), Row(3, "c", "2026-x")];
        var folders = Organizing.Folders(rows, [new FolderRule { Folder = "空的資料夾", Keywords = ["x"] }, new FolderRule { Folder = "產品" }]);
        Assert.Equal(2, folders.Count);
        Assert.Contains(("空的資料夾", 0), folders);
        Assert.Contains(("產品", 1), folders);
        // 資料夾表（R104：自己建的空資料夾、會議都移走了的資料夾）也列出來，不重複
        var withTable = Organizing.Folders(rows, [new FolderRule { Folder = "空的資料夾", Keywords = ["x"] }], ["新專案", "產品", ""]);
        Assert.Equal(3, withTable.Count);
        Assert.Contains(("新專案", 0), withTable);
        Assert.Contains(("產品", 1), withTable);
        Assert.Equal([1L], Organizing.SeriesInputs(rows, new Dictionary<long, HashSet<long>>()).Select(i => i.Id));
        Assert.Equal(3, Organizing.CommonSeries(rows, [2]));
        Assert.Null(Organizing.CommonSeries(rows, [1, 2]));

        var people = new List<PersonOverview> { new(2, "陳家豪", 1, null, false), new(3, "Bob", 1, null, false) };
        Assert.Equal("這場很像「客戶」裡的會議：陳家豪、Bob 都參加（2 場）", Organizing.SuggestionText(new FolderSuggestion("客戶", [2, 3], null, 2), people));
        Assert.Equal("這場很像「設計」裡的會議：標題都有「設計評審」（2 場）", Organizing.SuggestionText(new FolderSuggestion("設計", null, "設計評審", 2), people));
        var series = new List<MeetingSeries> { new() { Id = 4, Name = "產品週會", Weekday = 2 } };
        var templates = new List<TemplateRecord> { new() { Id = "one-on-one", Name = "一對一", Definition = "{}" } };
        Assert.Equal("自動加入：標題包含 週會、sync；陳家豪、Bob 都參加；範本：一對一；定期會議：產品週會",
            Organizing.RuleSummary(new FolderRule { Folder = "x", Keywords = ["週會", "sync"], People = [2, 3], TemplateId = "one-on-one", SeriesId = 4 },
                people, templates, series));
        var withNotes = new Meeting { Id = 9, Title = "需求確認", Notes = "{}", MeetingDate = "2026-11-02", SourceFilename = "x" };
        List<MeetingRow> folderRows = [Row(1, "客戶 A 訪談", "2026-11-01", folder: "客戶"), Row(2, "客戶 A 追蹤", "2026-11-01", folder: "客戶")];
        var attended = new Dictionary<long, HashSet<long>> { [1] = [2, 3], [2] = [1, 2, 3], [9] = [2, 3] };
        Assert.Equal("客戶", Organizing.Suggestion(withNotes, folderRows, attended, null)?.Folder);
        Assert.Null(Organizing.Suggestion(withNotes, folderRows, attended, recordingId: 9));
        withNotes.FolderDecided = true;
        Assert.Null(Organizing.Suggestion(withNotes, folderRows, attended, null));
        Assert.Equal("每週一 10:00・2 場・最近 2026-11-09",
            Organizing.SeriesSummaryLine(new MeetingSeries { Weekday = 2, Minute = 600 }, [Row(2, "b", "2026-11-09"), Row(1, "a", "2026-11-02")]));
    }

    /// <summary>上次的待辦（M§73）：空的時候不寫進 JSON（以前的紀錄一字不差）；status 認不得時是「這次沒提到」；改名字、簡體、搜尋</summary>
    [Fact]
    public void FollowUpsInNotes()
    {
        const string old = """{"actionItems":[],"custom":[],"decisions":[],"openQuestions":[],"reviewItems":[],"summary":[{"text":"a","time":"00:01"}],"title":"t","topics":[]}""";
        Assert.Equal(old, MeetingNotes.Decode(old.Replace("\"title\"", "\"followUps\":[],\"title\"", StringComparison.Ordinal))!.JsonString());
        var notes = MeetingNotes.Decode("""{"followUps":[{"task":"寄信給說話者 1","owner":"說話者 1","status":"done","time":"(00:05)"},{"task":5,"status":"later"}]}""")!;
        Assert.Equal(FollowUpStatus.Done, notes.FollowUps[0].Status);
        Assert.Equal("00:05", notes.FollowUps[0].Time);
        Assert.Equal(("", FollowUpStatus.NotMentioned), (notes.FollowUps[1].Task, notes.FollowUps[1].Status));
        Assert.False(notes.IsEmpty);
        Assert.Contains("\"followUps\":[{\"owner\":\"說話者 1\",\"status\":\"done\",\"task\":\"寄信給說話者 1\",\"time\":\"00:05\"}", notes.JsonString());
        Assert.Empty(MeetingNotes.Decode("""{"followUps":[{"task":"x"},3]}""")!.FollowUps);
        var renamed = notes.ReplacingSpeakerName("說話者 1", "王小明", isDefaultName: true);
        Assert.Equal(("寄信給王小明", "王小明"), (renamed.FollowUps[0].Task, renamed.FollowUps[0].Owner));
        Assert.Contains("說話者 1 寄信給說話者 1", notes.PlainText);
    }
}
