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
    public MeetingDetail? Detail { get; private set; }
    public DetailTab Tab { get; private set; } = DetailTab.Summary;
    /// <summary>紀錄頂部「還有 N 題待確認」有沒有展開（R44.2）；換會議就收起</summary>
    public bool ReviewExpanded { get; set; }
    /// <summary>逐字稿頂部「我的筆記」有沒有展開（R44.3）；換會議就收起</summary>
    public bool UserNotesExpanded { get; set; }
    public bool ShowTrash { get; private set; }
    public string? FolderFilter { get; private set; }
    public string SearchQuery { get; private set; } = "";
    public List<SearchHit> SearchResults { get; private set; } = [];
    public bool SearchActive => SearchQuery.Trim().Length > 0;

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
            GlossarySuggestions = host.Db.GlossarySuggestions();
        }
        catch (Microsoft.Data.Sqlite.SqliteException e) { Log.Error($"reload failed: {e.SqliteErrorCode}"); }
        if (Selection is long id && !Meetings.Any(m => m.Id == id) && !Trash.Any(m => m.Id == id)) Selection = null;
        // 資料夾裡最後一場會議移走後，資料夾就不存在了，回到所有會議
        if (FolderFilter is { } folder && !Meetings.Any(m => m.Meeting.Folder == folder)) FolderFilter = null;
        if (SearchActive) SearchResults = host.Controller.Search(SearchQuery);
    }

    public void ReloadDetail(bool forceAudio = false)
    {
        var previous = Detail?.Meeting;
        Detail = Selection is long id ? MeetingDetail.Load(host.Db, id) : null;
        if (Detail is null)
        {
            host.Player.Unload();
            return;
        }
        var meeting = Detail.Meeting;
        if (forceAudio || previous?.Id != meeting.Id || previous?.AudioFile != meeting.AudioFile)
        {
            if (host.Paths.HasAudio(meeting)) host.Player.Load(host.Paths.AudioPath(meeting)); else host.Player.Unload();
        }
    }

    public List<MeetingRow> FilteredMeetings
    {
        get
        {
            var source = ShowTrash ? Trash : Meetings;
            if (!ShowTrash && FolderFilter is { } folder) source = source.Where(m => m.Meeting.Folder == folder).ToList();
            return source;
        }
    }

    /// <summary>新會議要放進的資料夾：正在看某個資料夾時就放那裡</summary>
    public string CurrentFolder => ShowTrash ? "" : FolderFilter ?? "";

    public MeetingStatus? DetailStatus => Detail is null ? null : host.Controller.Status(Detail);

    public void Select(long? id, bool keepSearch = true)
    {
        if (Selection == id) return;
        Selection = id;
        ReviewExpanded = false;
        UserNotesExpanded = false;
        ReloadDetail();
        Changed?.Invoke();
    }

    public void Reveal(long id)
    {
        LoadLists();
        ShowTrash = Trash.Any(m => m.Id == id);
        Selection = null;
        Select(id);
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
        FolderFilter = folder;
        Changed?.Invoke();
    }

    public void ToggleTrash()
    {
        ShowTrash = !ShowTrash;
        Selection = null;
        ReloadDetail();
        Changed?.Invoke();
    }

    public void SetSearch(string query)
    {
        SearchQuery = query;
        SearchResults = host.Controller.Search(query);
        Changed?.Invoke();
    }
}
