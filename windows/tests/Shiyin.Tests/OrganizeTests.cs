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
        var previous = new PreviousMeeting("t", "2026-11-09", expected.Strings("previous")!.Select(t => new MeetingNotes.ActionItem("", t, "")).ToList(), []);
        static List<MeetingNotes.FollowUp> Decode(JsonElement list) => list.EnumerateArray()
            .Select(f => new MeetingNotes.FollowUp(f.Str("task")!, f.Str("owner")!, FollowUpStatuses.Parse(f.Str("status"))!.Value)).ToList();
        static IEnumerable<(string, string, string)> Key(IEnumerable<MeetingNotes.FollowUp> list) => list.Select(f => (f.Task, f.Owner, f.Status.Code()));
        var raw = Decode(expected.GetProperty("input"));
        Assert.Equal(Key(Decode(expected.GetProperty("valid"))), Key(PreviousMeeting.ValidFollowUps(raw, previous)));
        Assert.Equal(Key(Decode(expected.GetProperty("withoutPrevious"))), Key(PreviousMeeting.ValidFollowUps(raw, null)));
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

    /// <summary>側欄的資料夾（有條件的沒有會議也列出來）、可能的定期會議的輸入、建議的文字、條件的說明（M§72、§73）</summary>
    [Fact]
    public void OrganizingHelpers()
    {
        List<MeetingRow> rows = [Row(1, "a", "2026-11-02", folder: "產品"), Row(2, "b", "2026-11-02", series: 3), Row(3, "c", "2026-x")];
        var folders = Organizing.Folders(rows, [new FolderRule { Folder = "空的資料夾", Keywords = ["x"] }, new FolderRule { Folder = "產品" }]);
        Assert.Equal(2, folders.Count);
        Assert.Contains(("空的資料夾", 0), folders);
        Assert.Contains(("產品", 1), folders);
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
