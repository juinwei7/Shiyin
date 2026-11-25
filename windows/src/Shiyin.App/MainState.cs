using Microsoft.UI.Dispatching;
using Shiyin.Data;
using Shiyin.Model;
using Shiyin.Services;

namespace Shiyin.App;

/// <summary>
/// 主視窗的狀態（同 Mac 的 AppModel 裡畫面的部分）：選的會議、分頁、篩選、搜尋。
/// 資料改了（服務的事件）先記下，0.15 秒內合併成一次重讀，免得工作進度一直更新時畫面每次都整個重畫（M§21.12）
/// </summary>
public sealed class MainState
{
    readonly AppHost host;
    readonly DispatcherQueueTimer coalesce;
    bool listDirty, detailDirty;

    public List<MeetingRow> Meetings { get; private set; } = [];
    public List<MeetingRow> Trash { get; private set; } = [];
    public List<TemplateRecord> Templates { get; private set; } = [];
    public List<GlossaryTerm> Glossary { get; private set; } = [];
    public List<GlossarySuggestion> GlossarySuggestions { get; private set; } = [];
    public long? Selection { get; private set; }
    /// <summary>
    /// 會議列表的多選（R88、M§62）：兩場以上時右邊顯示「已選取 N 場會議」；Selection 照舊是目前這場（會議頁、播放）。
    /// 換資料夾、進出垃圾桶、搜尋時清掉；重讀列表時拿掉已經不在列表裡的
    /// </summary>
    HashSet<long> multiSelection = [];
    /// <summary>選了兩場以上時的那幾場（列表的順序），不然是空的</summary>
    public List<long> MultiSelected => multiSelection.Count > 1 ? FilteredMeetings.Select(m => m.Id).Where(multiSelection.Contains).ToList() : [];
    public MeetingDetail? Detail { get; private set; }
    public DetailTab Tab { get; private set; } = DetailTab.Summary;
    /// <summary>紀錄頂部「還有 N 題待確認」有沒有展開（R44.2）；換會議就收起</summary>
    public bool ReviewExpanded { get; set; }
    /// <summary>正在看以前的哪一版會議紀錄（R115.2、M§89）：null 是目前的；換會議、紀錄變了就回到目前的</summary>
    public NotesHistoryEntry? ViewingNotes { get; set; }
    /// <summary>逐字稿頂部「我的筆記」有沒有展開（R44.3）；換會議就收起</summary>
    public bool UserNotesExpanded { get; set; }
    public bool ShowTrash { get; private set; }
    /// <summary>側欄目前只看的資料夾；null 是所有會議（R9.2）</summary>
    public string? FolderFilter { get; private set; }
    /// <summary>會議列表的篩選（R97.3、M§71）：只放記憶體，打開拾音時是空的</summary>
    public MeetingFilter Filter { get; private set; } = new();
    /// <summary>每場會議連到哪些人物（「有誰參加」篩選、資料夾的條件和建議）</summary>
    public Dictionary<long, HashSet<long>> MeetingPeople { get; private set; } = [];
    /// <summary>資料夾的自動加入條件（R98、M§72）</summary>
    public List<FolderRule> FolderRules { get; private set; } = [];
    public string SearchQuery { get; private set; } = "";
    public List<SearchHit> SearchResults { get; private set; } = [];
    public bool SearchActive => SearchQuery.Trim().Length > 0;
    /// <summary>側欄是會議還是人物（M§60.2、R85.1）：不記在設定，每次打開都從會議開始</summary>
    public SidebarMode Sidebar { get; private set; } = SidebarMode.Meetings;
    /// <summary>人物列表、選了哪個人（M§60.2）</summary>
    public List<PersonOverview> People { get; private set; } = [];
    public long? SelectedPerson { get; private set; }
    /// <summary>右邊顯示人物頁：選了「人物」，而且不在垃圾桶、沒在搜尋（搜尋的結果是會議）</summary>
    public bool ShowsPeople => Sidebar == SidebarMode.People && !ShowTrash && !SearchActive;

    /// <summary>畫面要重畫（在 UI 執行緒）</summary>
    public event Action? Changed;
    /// <summary>錄音的狀態變了（在 UI 執行緒）：只更新錄音的畫面和工具列</summary>
    public event Action? RecorderChanged;

    public MainState(AppHost host)
    {
        this.host = host;
        coalesce = DispatcherQueue.GetForCurrentThread().CreateTimer();
        coalesce.Interval = TimeSpan.FromMilliseconds(150);
        coalesce.IsRepeating = false;
        coalesce.Tick += (_, _) => Ui.Guard(Flush);
        host.Controller.Changed += id => host.Post(() => MarkDirty(id));
        host.Models.Changed += _ => host.Post(() => MarkDirty(null));
        // 錄音中的狀態（計時、即時文字、音量提示）變得很頻繁：只通知錄音的畫面，不重讀資料庫（M§25）
        host.Recorder.Changed += () => host.Post(() => RecorderChanged?.Invoke());
        host.Runner.AudioReplaced += id => host.Post(() =>
        {
            if (Selection != id) return;
            // 錄音壓縮成 m4a 後換檔案：接著原本的位置，正在播的繼續播
            var (time, playing) = (host.Player.CurrentTime, host.Player.IsPlaying);
            host.Player.Unload();
            ReloadDetail(forceAudio: true);
            if (time > 0 || playing) host.Player.Seek(time, playing);
            Changed?.Invoke();
        });
        // 分割會議、永久刪除要換掉或刪掉錄音檔（design-windows §11.23）：正在播的那場先放掉檔案。
        // 要在搬檔案之前做完，所以不在 UI 執行緒上時等它做完（最多 5 秒）
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        host.Controller.ReleasingAudio += id =>
        {
            void Release() { if (Selection == id || Detail?.Meeting.Id == id) host.Player.Unload(); }
            if (dispatcher.HasThreadAccess) { Release(); return; }
            // 不 Dispose：超過 5 秒才跑到時還要 Set
            var done = new ManualResetEventSlim();
            if (dispatcher.TryEnqueue(() => { try { Ui.Guard(Release); } finally { done.Set(); } })) done.Wait(TimeSpan.FromSeconds(5));
        };
        // 分割會議換好錄音檔：重新載入播放器（檔名可能沒變，要強制重新載入）
        host.Controller.AudioReplaced += id => host.Post(() =>
        {
            if (Selection != id) return;
            ReloadDetail(forceAudio: true);
            Changed?.Invoke();
        });
    }

    void MarkDirty(long? meetingId)
    {
        listDirty = true;
        if (meetingId is null || meetingId == Selection) detailDirty = true;
        coalesce.Stop();
        coalesce.Start();
    }

    void Flush()
    {
        if (listDirty) LoadLists();
        if (detailDirty) ReloadDetail();
        listDirty = detailDirty = false;
        Changed?.Invoke();
    }

    public void Reload()
    {
        LoadLists();
        ReloadDetail();
        Changed?.Invoke();
    }

    void LoadLists()
    {
        try
        {
            Meetings = host.Db.MeetingRows();
            Trash = host.Db.MeetingRows(trash: true);
            Templates = host.Db.Templates();
            Glossary = host.Db.Glossary();
            // 公司已經有的詞不再建議（R53.3）
            GlossarySuggestions = [.. host.Db.GlossarySuggestions().Where(s => !host.Enterprise.Glossary.Contains(s.Term))];
            MeetingPeople = host.Db.MeetingPeople();
            FolderRules = host.Db.FolderRules();
            FolderNames = host.Db.FolderNames();
        }
        catch (Microsoft.Data.Sqlite.SqliteException e) { Log.Error($"reload failed: {e.SqliteErrorCode}"); }
        if (Selection is long id && !Meetings.Any(m => m.Id == id) && !Trash.Any(m => m.Id == id)) Selection = null;
        // 資料夾不見了（刪除、改名）就回到所有會議；資料夾表裡的（R104：會議都移走了也還在）、有自動加入條件的資料夾沒有會議也留著（M§72）
        if (FolderFilter is { } folder && !Folders.Any(f => f.Name == folder)) FolderFilter = null;
        // 多選裡已經不在列表的（移到垃圾桶、永久刪除、還原之後）拿掉，剩不到兩場就清掉（M§62）
        if (multiSelection.Count > 0)
        {
            var listed = FilteredMeetings.Select(m => m.Id).ToHashSet();
            multiSelection.IntersectWith(listed);
            if (multiSelection.Count < 2) multiSelection.Clear();
        }
        if (SearchActive) SearchResults = host.Controller.Search(SearchQuery);
        LoadPeople();
    }

    /// <summary>人物列表（M§60.1）：改名、合併、刪除、新的會議都會變；選的人不在了就清掉</summary>
    void LoadPeople()
    {
        try { People = host.Db.PeopleOverview(); }
        catch (Microsoft.Data.Sqlite.SqliteException e) { Log.Error($"people reload failed: {e.SqliteErrorCode}"); }
        if (SelectedPerson is long p && !People.Any(x => x.Id == p)) SelectedPerson = null;
        // 篩選的人不在了（合併、刪除）就不篩這一項
        if (Filter.PersonId is long filtered && !People.Any(x => x.Id == filtered)) Filter = Filter with { PersonId = null };
    }

    /// <summary>側欄的「會議｜人物」（M§60.2）</summary>
    public void SetSidebar(SidebarMode mode)
    {
        if (Sidebar == mode) return;
        Sidebar = mode;
        multiSelection.Clear();
        if (mode == SidebarMode.People) LoadPeople();
        Changed?.Invoke();
    }

    /// <summary>改名之後選這個名字的人（改成已經有的名字時合併過去，選那個人）</summary>
    public void SelectPersonNamed(string name)
    {
        LoadPeople();
        SelectPerson(People.FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))?.Id);
    }

    /// <summary>選一個人（側欄的人物列表、DEBUG 的 person）；側欄切到人物</summary>
    public void SelectPerson(long? id)
    {
        Sidebar = SidebarMode.People;
        LoadPeople();
        SelectedPerson = id is long p && People.Any(x => x.Id == p) ? p : null;
        Changed?.Invoke();
    }

    public void ReloadDetail(bool forceAudio = false)
    {
        var previous = Detail?.Meeting;
        var previousNotes = Detail?.Meeting.Notes;
        Detail = Selection is long id ? MeetingDetail.Load(host.Db, id) : null;
        // 紀錄變了（整理完、改用某一版、編輯）：回到目前的版本（R115.6）
        if (ViewingNotes is not null && (Detail is null || previous?.Id != Detail.Meeting.Id || previousNotes != Detail.Meeting.Notes)) ViewingNotes = null;
        if (Detail is null)
        {
            host.Player.Unload();
            return;
        }
        var meeting = Detail.Meeting;
        if (forceAudio || previous?.Id != meeting.Id || previous?.AudioFile != meeting.AudioFile)
        {
            if (host.Paths.HasAudio(meeting)) host.Player.Load(host.Paths.AudioPath(meeting), meeting.Channels == "both"); else host.Player.Unload();
        }
    }

    /// <summary>列表上的會議（R97、M§71）：資料夾、篩選（垃圾桶不篩選）、排序</summary>
    public List<MeetingRow> FilteredMeetings
    {
        get
        {
            var rows = MeetingList.Filtered(ShowTrash ? Trash : Meetings, ShowTrash, FolderFilter, Filter, Sort, MeetingPeople, MyName);
            // 只放資料夾以外的會議（R101.6）
            return ListShowsOthersOnly ? rows.Where(r => !SidebarCollapse.InFolder(r)).ToList() : rows;
        }
    }

    /// <summary>下面的列表是「其他會議」（R101.6）：只放不在資料夾的會議</summary>
    public bool ListShowsOthersOnly => SidebarCollapse.ShowsOthersOnly(Meetings, ShowTrash, FolderFilter, Filter, Grouping);

    /// <summary>列表的排序、分組（R97.2）：記在 settings.json</summary>
    public MeetingSort Sort => host.Settings.MeetingSort;
    public MeetingGrouping Grouping => host.Settings.MeetingGrouping;

    /// <summary>實際用的分組：看某個資料夾時不依資料夾分組；垃圾桶、搜尋不分組</summary>
    public MeetingGrouping EffectiveGrouping => ShowTrash ? MeetingGrouping.None : MeetingList.Effective(Grouping, FolderFilter);

    /// <summary>分組後的列表（不分組時一段、標題是空的）</summary>
    public List<MeetingGroup> Groups => MeetingList.Groups(FilteredMeetings, EffectiveGrouping, Sort, ShowTrash);

    /// <summary>「我」的名字（R80）：「有我的待辦的」用；沒有設定「我」是 null</summary>
    public string? MyName => MeetingList.MyName(People);

    /// <summary>篩選的說明（列表上方那一列）</summary>
    public string FilterSummary => MeetingList.FilterSummary(Filter, People, Templates);

    public FolderRule? RuleFor(string folder) => FolderRules.FirstOrDefault(r => r.Folder == folder);

    /// <summary>資料夾表裡的名字（R104、M§78：自己建的空資料夾、會議都移走了的資料夾也在）</summary>
    public List<string> FolderNames { get; private set; } = [];

    /// <summary>側欄的資料夾和場數：資料夾表、會議的資料夾、自動加入條件三者的聯集，依名稱排序（沒有會議的是 0 場）</summary>
    public List<(string Name, int Count)> Folders => Organizing.Folders(Meetings, FolderRules, FolderNames);

    public void SetSort(MeetingSort sort)
    {
        if (Sort == sort) return;
        host.Settings.Set(AppSettings.MeetingSortKey, sort.Code());
        Changed?.Invoke();
    }

    public void SetGrouping(MeetingGrouping grouping)
    {
        if (Grouping == grouping) return;
        host.Settings.Set(AppSettings.MeetingGroupingKey, grouping.Code());
        Changed?.Invoke();
    }

    /// <summary>換篩選時取消多選（M§71）</summary>
    public void SetFilter(MeetingFilter filter)
    {
        if (Filter == filter) return;
        Filter = filter;
        multiSelection.Clear();
        Changed?.Invoke();
    }

    /// <summary>新會議要放進的資料夾：正在看某個資料夾時就放那裡</summary>
    public string CurrentFolder => ShowTrash ? "" : FolderFilter ?? "";

    public MeetingStatus? DetailStatus => Detail is null ? null : host.Controller.Status(Detail);

    public void Select(long? id, bool keepSearch = true)
    {
        // 程式換到多選以外的會議（還原、錄音停止後跳到那場、點通知）時，多選換成那一場（M§62）
        var clearedMulti = false;
        if (multiSelection.Count > 0 && (id is not long target || !multiSelection.Contains(target)))
        {
            multiSelection.Clear();
            clearedMulti = true;
        }
        if (Selection == id)
        {
            if (clearedMulti) Changed?.Invoke();
            return;
        }
        Selection = id;
        ReviewExpanded = false;
        UserNotesExpanded = false;
        ViewingNotes = null;
        host.Controller.ReviewFinished.Clear();
        ReloadDetail();
        Changed?.Invoke();
    }

    /// <summary>
    /// 會議列表的選取改了（Ctrl、Shift 點選，R88.1）：兩場以上存進多選、目前這場不變；只剩一場時選那一場；都沒選時清掉（同 Mac 的 listSelection）
    /// </summary>
    public void SetListSelection(IReadOnlyCollection<long> ids)
    {
        if (ids.Count > 1)
        {
            if (multiSelection.SetEquals(ids)) return;
            multiSelection = [.. ids];
            Changed?.Invoke();
            return;
        }
        multiSelection.Clear();
        Select(ids.Count == 1 ? ids.First() : null);
    }

    public void Reveal(long id)
    {
        LoadLists();
        // 從人物頁、通知、會議連結打開一場會議：側欄切回會議（M§60.2）
        Sidebar = SidebarMode.Meetings;
        ShowTrash = Trash.Any(m => m.Id == id);
        Selection = null;
        Select(id);
        ExpandToShow(id);
    }

    /// <summary>
    /// 程式帶你去某場會議時（reveal、搜尋結果點進去）：展開側欄的會議列表和那場所在的那一組（R101.1、design-windows §11.34）。
    /// 設定改了主視窗會重畫
    /// </summary>
    public void ExpandToShow(long id) => SidebarCollapse.ExpandToShow(host.Settings, id, Groups, Meetings);

    /// <summary>
    /// 點了資料夾、定期會議在原地展開的那一場（R101.2）：人物模式換回會議、選那場；下面的列表不換（同 Mac 的 openFromSidebar）
    /// </summary>
    public void OpenFromSidebar(long id)
    {
        var changed = Sidebar != SidebarMode.Meetings || multiSelection.Count > 0;
        Sidebar = SidebarMode.Meetings;
        multiSelection.Clear();
        if (Selection != id) Select(id);
        else if (changed) Changed?.Invoke();
    }

    /// <summary>還原了好幾場（R88、M§62）：回到會議列表、選取那幾場，目前這場是第一場</summary>
    public void RevealMany(IReadOnlyList<long> ids)
    {
        LoadLists();
        Sidebar = SidebarMode.Meetings;
        ShowTrash = false;
        FolderFilter = null;
        Selection = null;
        multiSelection.Clear();
        var restored = ids.Where(id => Meetings.Any(m => m.Id == id)).ToList();
        Select(restored.Count > 0 ? restored[0] : null);
        if (restored.Count > 1)
        {
            multiSelection = [.. restored];
            Changed?.Invoke();
        }
    }

    public void SetTab(DetailTab tab)
    {
        if (Tab == tab) return;
        Tab = tab;
        Changed?.Invoke();
    }

    /// <summary>切到分頁並展開頂部的那一條（搜尋結果跳到筆記、控制通道的 tab review|notes）</summary>
    public void OpenTab(DetailTab tab, bool expandReview = false, bool expandNotes = false)
    {
        Tab = tab;
        if (expandReview) ReviewExpanded = true;
        if (expandNotes) UserNotesExpanded = true;
        Changed?.Invoke();
    }

    public void SetFolder(string? folder)
    {
        if (FolderFilter != folder) multiSelection.Clear();
        FolderFilter = folder;
        Changed?.Invoke();
    }

    /// <summary>側欄點「所有會議」或資料夾（M§60.2）：回到會議模式、只看那個資料夾，畫面重畫一次</summary>
    public void ShowMeetings(string? folder)
    {
        if (FolderFilter != folder) multiSelection.Clear();
        Sidebar = SidebarMode.Meetings;
        FolderFilter = folder;
        Changed?.Invoke();
    }

    public void ToggleTrash() => ShowTrashList(!ShowTrash);

    /// <summary>進出垃圾桶（DEBUG 的 showtrash）：選取、多選清掉</summary>
    public void ShowTrashList(bool trash)
    {
        if (ShowTrash == trash) return;
        ShowTrash = trash;
        Selection = null;
        multiSelection.Clear();
        ReloadDetail();
        Changed?.Invoke();
    }

    public void SetSearch(string query)
    {
        multiSelection.Clear();
        SearchQuery = query;
        SearchResults = host.Controller.Search(query);
        Changed?.Invoke();
    }
}

/// <summary>側欄是會議還是人物（M§60.2）</summary>
public enum SidebarMode { Meetings, People }
