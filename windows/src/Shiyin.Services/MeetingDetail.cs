using Shiyin.Data;
using Shiyin.Localization;
using Shiyin.Model;
using Shiyin.Notes;
using Shiyin.Transcribe;

namespace Shiyin.Services;

/// <summary>列表與標題列顯示的狀態（同 Mac 的 MeetingStatus）</summary>
public abstract record MeetingStatus
{
    public sealed record Recording : MeetingStatus;
    public sealed record Queued : MeetingStatus;
    public sealed record Transcribing(double? Progress, string Stage) : MeetingStatus;
    public sealed record Summarizing : MeetingStatus;
    public sealed record Failed(JobKind Kind, string Error) : MeetingStatus;
    public sealed record Cancelled : MeetingStatus;
    public sealed record Review(int Count) : MeetingStatus;
    public sealed record Done : MeetingStatus;

    public string Label => this switch
    {
        Recording => L.T("錄音中"),
        Queued => L.T("排隊中"),
        Transcribing t when IsWaitingForServer(t.Stage) => t.Stage,
        Transcribing t => Name(t) + (t.Progress is double p ? $" {(int)(p * 100)}%" : ""),
        Summarizing => L.T("整理中"),
        Failed f => f.Kind == JobKind.Transcribe ? L.T("轉錄失敗") : L.T("整理失敗"),
        Cancelled => L.T("已取消"),
        Review r => L.F("待確認 %lld 題", r.Count),
        _ => L.T("完成"),
    };

    static string Name(Transcribing t) =>
        t.Stage.Length == 0 || t.Stage == L.T("轉錄") ? L.T("轉錄中") : L.F("%@中", t.Stage);

    /// <summary>在等企業伺服器（R50.5）：不接「中」、不顯示百分比</summary>
    public static bool IsWaitingForServer(string stage) => stage == L.T("等企業伺服器");

    public bool IsActive => this is Queued or Transcribing or Summarizing or Recording;
}

/// <summary>會議頁的三個分頁（R44.1、M§33.1）：紀錄（摘要＋議題，待確認在頂部展開）、逐字稿（我的筆記在頂部展開）、提問</summary>
public enum DetailTab { Summary, Transcript, Ask }

public static class DetailTabs
{
    public static string Label(this DetailTab tab) => tab switch
    {
        DetailTab.Summary => L.T("紀錄"),
        DetailTab.Transcript => L.T("逐字稿"),
        _ => L.T("提問"),
    };

    /// <summary>控制通道、搜尋結果用的名稱（同 Mac 的 DetailTab.resolve）：舊的分頁名稱當別名，
    /// topics → 紀錄，review → 紀錄並展開待確認，notes → 逐字稿並展開我的筆記</summary>
    public static (DetailTab Tab, bool ExpandReview, bool ExpandNotes)? Resolve(string name) => name.ToLowerInvariant() switch
    {
        "summary" => (DetailTab.Summary, false, false),
        "topics" => (DetailTab.Summary, false, false),
        "review" => (DetailTab.Summary, true, false),
        "transcript" => (DetailTab.Transcript, false, false),
        "notes" => (DetailTab.Transcript, false, true),
        "ask" => (DetailTab.Ask, false, false),
        _ => null,
    };
}

/// <summary>選取中的會議的完整資料（同 Mac 的 MeetingDetail）。逐字稿分頁用的索引載入時算一次（M§21.12）</summary>
public sealed class MeetingDetail
{
    public required Meeting Meeting { get; set; }
    public required List<Segment> Segments { get; set; }
    public MeetingNotes? Notes { get; set; }
    public int VersionCount { get; set; }
    /// <summary>有上一版會議紀錄可以復原（R44.4）：紀錄的「編輯」旁顯示「復原上一版」</summary>
    public bool CanUndoNotes => VersionCount > 0;
    /// <summary>最近一次可以復原的說話者調整（合併、改句子）的說明（R44.4）；null 表示沒有，改名視窗和右鍵選單不顯示「復原」</summary>
    public string? SpeakerUndoLabel { get; set; }
    public Job? LatestJob { get; set; }
    /// <summary>最後幾分鐘沒人說話、沒有轉錄（M§35.3、R48.3）：自動停止的錄音，錄音比轉錄的範圍長 1 分鐘以上才有</summary>
    public int? UntranscribedMinutes { get; set; }
    public Dictionary<int, string> Speakers { get; set; } = [];
    public List<Bookmark> Bookmarks { get; set; } = [];
    /// <summary>逐字稿校正建議（M§17.4）</summary>
    public List<Correction> Corrections { get; set; } = [];
    /// <summary>每段的校正（不含已忽略的）</summary>
    public Dictionary<long, List<Correction>> CorrectionsBySegment { get; private set; } = [];
    public double[] TimelineStarts { get; private set; } = [];
    public long[] TimelineIds { get; private set; } = [];
    /// <summary>段落 id → 在 Segments 裡的位置</summary>
    public Dictionary<long, int> SegmentIndex { get; private set; } = [];
    /// <summary>說話者名字的來源（default / manual / voiceprint / suggestion），聲紋帶入的顯示「自動」（M§21.4）</summary>
    public Dictionary<int, string> SpeakerSources { get; private set; } = [];
    /// <summary>AI 猜的名字（M§21.5）、聲紋沒把握的（M§21.4、R33）：還是預設名稱的說話者才有</summary>
    public Dictionary<int, SpeakerNaming.Suggestion> SpeakerSuggestions { get; private set; } = [];
    /// <summary>每位說話者最像的幾位認識的人（R33）：「是別人」的選單、待確認的「不是」</summary>
    public Dictionary<int, List<string>> SpeakerCandidates { get; set; } = [];
    /// <summary>待確認的題數：會議紀錄的待確認＋說話者的建議（R33.3）</summary>
    public int ReviewCount => (Notes?.ReviewItems.Count ?? 0) + SpeakerSuggestions.Count;
    /// <summary>待確認的題目對應到哪一條紀錄（R41.1、M§31.1）：載入時算一次（Windows 每次都整份重讀）</summary>
    public Dictionary<NoteItemRef, List<int>> ReviewLinks { get; private set; } = [];

    /// <summary>這一條紀錄對到幾題待確認</summary>
    public int ReviewCountOf(NoteItemRef r) => ReviewLinks.TryGetValue(r, out var list) ? list.Count : 0;

    public void LinkReviewItems() => ReviewLinks = Notes is null ? [] : Model.ReviewLinks.Compute(Notes);

    public static MeetingDetail? Load(AppDatabase db, long id)
    {
        if (db.Meeting(id) is not { } meeting) return null;
        var detail = new MeetingDetail
        {
            Meeting = meeting, Segments = db.Segments(id), Notes = meeting.DecodedNotes, VersionCount = db.VersionCount(id),
            LatestJob = db.LatestJob(id), Bookmarks = db.Bookmarks(id), Corrections = db.Corrections(id),
            SpeakerUndoLabel = db.SpeakerUndoLabel(id),
        };
        detail.LoadSpeakers(db.Speakers(id));
        detail.SpeakerCandidates = db.VoiceprintCandidates(id);
        if (JobRunner.TranscribedUntil(db.LastTranscribePayload(id)) is double until && meeting.DurationSeconds is int duration && duration - until >= 60)
            detail.UntranscribedMinutes = (int)Math.Round((duration - until) / 60, MidpointRounding.AwayFromZero);
        detail.BuildIndexes();
        detail.LinkReviewItems();
        return detail;
    }

    public void LoadSpeakers(IReadOnlyList<SpeakerName> rows)
    {
        Speakers = rows.ToDictionary(r => r.Idx, r => r.Name);
        SpeakerSources = rows.ToDictionary(r => r.Idx, r => r.NameSource);
        SpeakerSuggestions = rows.Where(r => r.NameSource == "default" && r.SuggestedName is not null)
            .ToDictionary(r => r.Idx, r => new SpeakerNaming.Suggestion(r.Idx, r.SuggestedName!, r.SuggestionReason ?? "", r.SuggestionTime ?? ""));
    }

    public void BuildIndexes()
    {
        CorrectionsBySegment = Corrections.Where(c => c.Status != "rejected").GroupBy(c => c.SegmentId).ToDictionary(g => g.Key, g => g.ToList());
        var ordered = Segments.Where(s => s.Id is not null).Select(s => (s.Start, Id: s.Id!.Value)).OrderBy(x => x.Start).ToList();
        TimelineStarts = ordered.Select(x => x.Start).ToArray();
        TimelineIds = ordered.Select(x => x.Id).ToArray();
        SegmentIndex = [];
        for (var i = 0; i < Segments.Count; i++) if (Segments[i].Id is long id) SegmentIndex.TryAdd(id, i);
    }

    /// <summary>跟這段同一位說話者、在逐字稿裡前後連在一起的段落（「這段連續的話是誰說的」，M§24.3）</summary>
    public List<long> SpeakerRun(long id)
    {
        if (!SegmentIndex.TryGetValue(id, out var i) || Segments[i].Speaker is not int speaker) return [id];
        var (low, high) = (i, i);
        while (low > 0 && Segments[low - 1].Speaker == speaker) low--;
        while (high + 1 < Segments.Count && Segments[high + 1].Speaker == speaker) high++;
        return Segments.GetRange(low, high - low + 1).Where(s => s.Id is not null).Select(s => s.Id!.Value).ToList();
    }

    /// <summary>說話者可能分得不夠的提示（M§24.5）：自我檢查覺得可能把人併在一起、使用者沒選過人數、沒按過「不用了」</summary>
    public bool ShowsSpeakerHint =>
        (Meeting.SpeakerCheck ?? 0) >= SpeakerRules.Quality.HintThreshold && Meeting.MinSpeakers is null && !Meeting.SpeakerHintDismissed
        && Segments.Any(s => s.Kind == "final" && s.Channel is null);

    /// <summary>播放到 time 時的段落：開始時間不晚於 time + 0.05 的最後一段（二分搜尋）</summary>
    public long? SegmentId(double time)
    {
        var (low, high) = (0, TimelineStarts.Length);
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (TimelineStarts[mid] <= time + 0.05) low = mid + 1; else high = mid;
        }
        return low > 0 ? TimelineIds[low - 1] : null;
    }

    /// <summary>待確認的校正依「原文 → 建議」分組，處數多的在前；組內依逐字稿的順序（M§21.11、R20.2）</summary>
    public List<List<Correction>> CorrectionGroups
    {
        get
        {
            int Order(Correction c) => SegmentIndex.GetValueOrDefault(c.SegmentId);
            return Corrections.Where(c => c.Status == "pending").GroupBy(c => (c.Original, c.Suggestion))
                .Select(g => g.OrderBy(Order).ToList())
                .OrderByDescending(g => g.Count).ThenBy(g => Order(g[0])).ToList();
        }
    }

    public string? SpeakerName(int? idx) => idx is int i
        ? Speakers.TryGetValue(i, out var name) ? name : Meeting.MeetingLanguage.Text($"說話者 {i}", $"Speaker {i}", $"話者 {i}")  // l10n-skip
        : null;
}
