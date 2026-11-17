using Shiyin.Data;
using Shiyin.Localization;
using Shiyin.Model;
using Shiyin.Notes;

namespace Shiyin.Services;

/// <summary>
/// 編輯畫面的狀態（R107、M§81.5、同 Mac 的 MeetingEditor）：一條時間軸（合併時兩場接在一起），把手、刪掉的段落、選取、切點、播放頭
/// 都是這條時間軸上的秒數；套用時換成 EditPlan（每場自己的時間）。不碰畫面：畫面聽 Changed 重畫，試聽由畫面做（StopPlayback）
/// </summary>
public sealed class MeetingEditor(AppController controller, long meetingId)
{
    public sealed record Part(long Id, string Title, double Offset, double Length, string CreatedAt, string MeetingDate, string Language, string? AudioPath);

    public sealed record Line(long Id, long MeetingId, double Start, double End, int? Speaker, string SpeakerName, string Text)
    {
        public double Mid => (Start + Math.Max(Start, End)) / 2;
    }

    public sealed record TopicSpan(string Title, double Start, double End, int Branch);

    public const double PeakStep = 0.05;
    public const double MaxZoom = 40;

    public long MeetingId { get; } = meetingId;
    public List<Part> Parts { get; private set; } = [];
    public List<Line> Lines { get; private set; } = [];
    public List<TopicSpan> Topics { get; private set; } = [];
    public List<double> Marks { get; private set; } = [];
    /// <summary>每場的波形（每 0.05 秒一個峰值，用那場第 98 百分位的峰值當 1）；還在讀的不在裡面</summary>
    public Dictionary<long, float[]> Peaks { get; } = [];
    public HashSet<long> LoadingPeaks { get; } = [];

    public double Start { get; private set; }
    public double End { get; private set; }
    public List<(double Lower, double Upper)> Deleted { get; private set; } = [];
    public (double Lower, double Upper)? Selection { get; private set; }
    public double? Cut { get; private set; }
    public double Playhead { get; private set; }
    /// <summary>1 是整場放得下，最大 40 倍</summary>
    public double Zoom { get; private set; } = 1;
    public bool IsPlaying { get; set; }
    /// <summary>逐字稿選到的句子（Ctrl、Shift 點選）</summary>
    public HashSet<long> SelectedLines { get; } = [];

    /// <summary>畫面要重畫（任何東西改了）</summary>
    public event Action? Changed;
    /// <summary>改動之前先停止試聽（同 Mac 的 stopPlaying）</summary>
    public Action? StopPlayback { get; set; }

    public double Total => Parts.Sum(p => p.Length);
    public Part? MergedPart => Parts.FirstOrDefault(p => p.Id != MeetingId);
    public Part? OwnPart => Parts.FirstOrDefault(p => p.Id == MeetingId);

    public bool HasChanges => Start > 0.05 || End < Total - 0.05 || Deleted.Count > 0 || Cut is not null || MergedPart is not null;

    void Notify() => Changed?.Invoke();

    void Stop()
    {
        if (IsPlaying) StopPlayback?.Invoke();
    }

    // MARK: 讀資料

    public void Load()
    {
        if (controller.Db.Meeting(MeetingId) is not { } meeting) return;
        Parts = [MakePart(meeting, 0)];
        RebuildContent();
        Start = 0;
        End = Total;
        Playhead = 0;
        Notify();
    }

    Part MakePart(Meeting meeting, double offset)
    {
        var id = meeting.Id!.Value;
        var db = controller.Db;
        var segmentsEnd = Math.Max(db.Segments(id).Select(s => s.End).DefaultIfEmpty(0).Max(), db.Segments(id, "live").Select(s => s.End).DefaultIfEmpty(0).Max());
        var length = Math.Max(meeting.DurationSeconds ?? 0, segmentsEnd);
        var audio = controller.Paths.HasAudio(meeting) ? controller.Paths.AudioPath(meeting) : null;
        if (audio is not null && controller.AudioSeconds(audio) is double seconds) length = Math.Max(length, seconds);
        return new Part(id, meeting.DisplayTitle, offset, Math.Max(length, 1), meeting.CreatedAt, meeting.MeetingDate, meeting.Language, audio);
    }

    /// <summary>句子、議題、標記照各場的位置排到同一條時間軸</summary>
    void RebuildContent()
    {
        var db = controller.Db;
        List<Line> lines = [];
        List<TopicSpan> topics = [];
        List<double> marks = [];
        // 合併進來的那場：同名的說話者用這場的編號（顏色），其他接在後面（套用時還會看人物、聲紋，M§81.4）
        var ownSpeakers = db.Speakers(MeetingId);
        var ownByName = new Dictionary<string, int>();
        foreach (var s in ownSpeakers.Where(s => s.NameSource != "default")) ownByName.TryAdd(Text.SwiftText.Lower(s.Name), s.Idx);
        var nextIdx = ownSpeakers.Select(s => s.Idx).DefaultIfEmpty(0).Max();
        foreach (var part in Parts)
        {
            var speakers = db.Speakers(part.Id);
            var names = new Dictionary<int, string>();
            foreach (var s in speakers) names.TryAdd(s.Idx, s.Name);
            var display = new Dictionary<int, int>();
            if (part.Id != MeetingId)
                foreach (var s in speakers)
                    display[s.Idx] = s.NameSource != "default" && ownByName.TryGetValue(Text.SwiftText.Lower(s.Name), out var same) ? same : nextIdx + s.Idx;
            var language = MeetingLanguages.Parse(part.Language) ?? MeetingLanguage.Zh;
            foreach (var s in db.Segments(part.Id))
                lines.Add(new Line(s.Id!.Value, part.Id, part.Offset + s.Start, part.Offset + s.End,
                    s.Speaker is int sp ? display.GetValueOrDefault(sp, sp) : null,
                    s.Speaker is int idx ? names.GetValueOrDefault(idx) ?? language.DefaultSpeaker(idx) : "", s.Text));
            if (db.Meeting(part.Id)?.DecodedNotes is { } notes)
                topics.AddRange(MindMap.TopicRanges(notes, part.Length, L.T("議題"))
                    .Select(r => new TopicSpan(r.Title, part.Offset + r.Start, part.Offset + r.End, r.Branch)));
            marks.AddRange(db.Bookmarks(part.Id).Select(b => part.Offset + b.Time));
        }
        Lines = [.. lines.OrderBy(l => l.Start)];
        Topics = topics;
        Marks = marks;
    }

    /// <summary>還沒讀波形、要讀的錄音（畫面在背景讀好後呼叫 SetPeaks）</summary>
    public List<Part> PartsNeedingPeaks() => Parts.Where(p => p.AudioPath is not null && !Peaks.ContainsKey(p.Id) && !LoadingPeaks.Contains(p.Id)).ToList();

    /// <summary>讀好的峰值：錄音大小聲差很多，用第 98 百分位的峰值當 1，安靜的錄音也看得出起伏（同 Mac）。null 是讀不到</summary>
    public void SetPeaks(long id, float[]? values)
    {
        LoadingPeaks.Remove(id);
        if (values is { Length: > 0 })
        {
            var sorted = values.Order().ToArray();
            var reference = sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.98))];
            Peaks[id] = reference > 0.001f ? values.Select(v => Math.Min(1, v / reference)).ToArray() : values;
        }
        Notify();
    }

    public bool IsLoadingPeaks => LoadingPeaks.Count > 0;

    /// <summary>某段時間的波形高度（0～1）：有錄音用峰值，沒有錄音或還沒讀好時用句子（有句子的地方中等高度）</summary>
    public float Level(double t0, double t1)
    {
        var part = Parts.LastOrDefault(p => p.Offset <= t0) ?? Parts.FirstOrDefault();
        if (part is null) return 0;
        if (Peaks.TryGetValue(part.Id, out var values) && values.Length > 0)
        {
            var a = Math.Max(0, (int)((t0 - part.Offset) / PeakStep));
            var b = Math.Min(values.Length, Math.Max(a + 1, (int)((t1 - part.Offset) / PeakStep)));
            if (a >= b) return 0;
            var max = 0f;
            for (var i = a; i < b; i++) max = Math.Max(max, values[i]);
            return max;
        }
        return Lines.Any(l => l.Start < t1 && l.End > t0) ? 0.45f : 0.04f;
    }

    // MARK: 規則

    public bool Kept(double t) => t >= Start && t <= End && !Deleted.Any(r => t >= r.Lower && t < r.Upper);

    public double KeptDuration(double a = 0, double? b = null)
    {
        double lo = Math.Max(a, Start), hi = Math.Min(b ?? Total, End);
        if (hi <= lo) return 0;
        return hi - lo - Deleted.Sum(r => Math.Max(0, Math.Min(hi, r.Upper) - Math.Max(lo, r.Lower)));
    }

    public int RemovedLines => Lines.Count(l => !Kept(l.Mid));
    public int RemovedMarks => Marks.Count(t => !Kept(t));

    /// <summary>套用用的片段：每場自己的時間</summary>
    public EditPlan Plan
    {
        get
        {
            var pieces = new List<EditPlan.Piece>();
            foreach (var part in Parts)
            {
                double lo = Math.Max(0, Start - part.Offset), hi = Math.Min(part.Length, End - part.Offset);
                if (hi <= lo) continue;
                var local = Deleted.Select(r => (A: r.Lower - part.Offset, B: r.Upper - part.Offset)).Where(r => r.B > 0 && r.A < part.Length)
                    .Select(r => (Math.Max(0, r.A), Math.Min(part.Length, r.B)));
                pieces.AddRange(EditPlan.Keeping(part.Id, lo, hi, local));
            }
            return new EditPlan(pieces);
        }
    }

    /// <summary>切點換成新時間軸上的時間</summary>
    public double? PlanCut => Cut is double c && Parts.LastOrDefault(p => p.Offset <= c) is { } part ? Plan.Map(part.Id, c - part.Offset) : null;

    /// <summary>新時間軸上的時間 → 這條時間軸（試聽的播放頭）</summary>
    public double? Timeline(double planSeconds, EditPlan plan) =>
        plan.Source(planSeconds) is { } s && Parts.FirstOrDefault(p => p.Id == s.MeetingId) is { } part ? part.Offset + s.Time : null;

    /// <summary>從播放頭試聽：在刪掉、裁掉的地方時從下一個留下的地方開始；快到結尾時從頭（同 Mac）</summary>
    public double PreviewStart(EditPlan plan)
    {
        var part = Parts.LastOrDefault(p => p.Offset <= Playhead) ?? Parts[0];
        var from = plan.Snap(part.Id, Playhead - part.Offset);
        return from >= plan.Duration - 0.1 ? 0 : from;
    }

    // MARK: 編輯

    public void SetStart(double t)
    {
        Stop();
        Start = Math.Min(Math.Max(0, t), End - 1);
        if (Cut is double c && c <= Start) Cut = null;
        Notify();
    }

    public void SetEnd(double t)
    {
        Stop();
        End = Math.Max(Math.Min(Total, t), Start + 1);
        if (Cut is double c && c >= End) Cut = null;
        Notify();
    }

    public void SetSelection((double Lower, double Upper)? range)
    {
        Selection = range is { } r ? (Math.Min(r.Lower, r.Upper), Math.Max(r.Lower, r.Upper)) : null;
        Notify();
    }

    public void DeleteRange((double Lower, double Upper) range)
    {
        var r = (Math.Max(0, range.Lower), Math.Min(Total, range.Upper));
        if (r.Item2 - r.Item1 < EditPlan.MinimumPiece) return;
        Stop();
        Deleted = EditPlan.Normalized([.. Deleted, r]);
        if (Cut is double c && !Kept(c)) Cut = null;
        Selection = null;
        Notify();
    }

    public void DeleteSelection()
    {
        if (Selection is { } s) DeleteRange(s);
    }

    public void Restore((double Lower, double Upper) range)
    {
        Stop();
        Deleted.RemoveAll(r => r == range);
        Notify();
    }

    public bool CanCut => MergedPart is null && Kept(Playhead) && Playhead > Start + 1 && Playhead < End - 1;

    public void ToggleCut()
    {
        Stop();
        if (Cut is not null) Cut = null;
        else if (CanCut) Cut = Playhead;
        Notify();
    }

    public void CutAt(double t)
    {
        if (MergedPart is not null || !Kept(t) || t <= Start + 1 || t >= End - 1) return;
        Stop();
        Cut = t;
        Notify();
    }

    /// <summary>右鍵的對象：點的那句在選取裡就是全部選取的，不在就只有那句</summary>
    public List<Line> TargetLines(Line line) => SelectedLines.Contains(line.Id) ? Lines.Where(l => SelectedLines.Contains(l.Id)).ToList() : [line];

    public void DeleteLines(IReadOnlyList<Line> targets)
    {
        Stop();
        Deleted = EditPlan.Normalized([.. Deleted, .. targets.Select(l => (l.Start, Math.Max(l.Start + EditPlan.MinimumPiece, l.End)))]);
        if (Cut is double c && !Kept(c)) Cut = null;
        SelectedLines.Clear();
        Notify();
    }

    public void RestoreLines(IReadOnlyList<Line> targets)
    {
        Stop();
        foreach (var line in targets) Deleted.RemoveAll(r => line.Mid >= r.Lower && line.Mid < r.Upper);
        Notify();
    }

    public void SetZoom(double zoom)
    {
        Zoom = Math.Clamp(zoom, 1, MaxZoom);
        Notify();
    }

    /// <summary>移播放頭（試聽中由畫面接著從新的位置播）</summary>
    public void Seek(double t)
    {
        Playhead = Math.Clamp(t, 0, Total);
        Notify();
    }

    /// <summary>試聽時跟著播放的位置走（不停止試聽）</summary>
    public void Follow(double t)
    {
        Playhead = Math.Clamp(t, 0, Total);
        Notify();
    }

    public void SetSelectedLines(IEnumerable<long> ids)
    {
        SelectedLines.Clear();
        SelectedLines.UnionWith(ids);
    }

    // MARK: 合併（M§81.4）

    public void Merge(Meeting other)
    {
        if (MergedPart is not null || OwnPart is not { } own) return;
        Stop();
        // 這場的裁切先變成刪掉的段落，合併後兩端的把手放在整條的兩端
        var deletedHere = Deleted.ToList();
        if (Start > 0.05) deletedHere.Add((0, Start));
        if (End < own.Length - 0.05) deletedHere.Add((End, own.Length));
        var theirs = MakePart(other, 0);
        var first = string.CompareOrdinal(other.CreatedAt, own.CreatedAt) < 0;
        var shift = first ? theirs.Length : 0;
        var mine = own with { Offset = shift };
        var placed = theirs with { Offset = first ? 0 : own.Length };
        Parts = first ? [placed, mine] : [mine, placed];
        Deleted = EditPlan.Normalized(deletedHere.Select(r => (r.Lower + shift, r.Upper + shift)));
        Playhead += shift;
        Selection = null;
        Cut = null;
        RebuildContent();
        Start = 0;
        End = Total;
        Notify();
    }

    public void Unmerge()
    {
        if (OwnPart is not { } own || MergedPart is null) return;
        Stop();
        double shift = own.Offset, length = own.Length;
        var local = Deleted.Select(r => (A: r.Lower - shift, B: r.Upper - shift)).Where(r => r.B > 0 && r.A < length)
            .Select(r => (Lower: Math.Max(0, r.A), Upper: Math.Min(length, r.B))).ToList();
        double newStart = Math.Max(0, Start - shift), newEnd = Math.Min(length, End - shift);
        if (local.Count > 0 && local[0].Lower <= 0.05)
        {
            newStart = Math.Max(newStart, local[0].Upper);
            local.RemoveAt(0);
        }
        if (local.Count > 0 && local[^1].Upper >= length - 0.05)
        {
            newEnd = Math.Min(newEnd, local[^1].Lower);
            local.RemoveAt(local.Count - 1);
        }
        Parts = [own with { Offset = 0 }];
        Deleted = local;
        Start = newStart;
        End = Math.Max(newEnd, newStart + 1);
        Playhead = Math.Clamp(Playhead - shift, 0, length);
        Selection = null;
        RebuildContent();
        Notify();
    }

    // MARK: DEBUG 的 editorstate（直接改打開的編輯畫面，同 Mac 的 debugSet）

    public void DebugSetRange(double? start, double? end)
    {
        if (start is double s) Start = s;
        if (end is double e) End = e;
        Notify();
    }

    public void DebugSetDeleted(IEnumerable<(double Lower, double Upper)> deleted)
    {
        Deleted = EditPlan.Normalized(deleted);
        Notify();
    }

    public void DebugSetCut(double? cut)
    {
        Cut = cut;
        Notify();
    }

    // MARK: 文字（同 Mac 的 MeetingEditSheet）

    /// <summary>標題下面：「標題・原本 58:10」；合併時加「（合併「另一場」00:59）」</summary>
    public string Subtitle
    {
        get
        {
            if (OwnPart is not { } own) return "";
            var text = L.F("%@・原本 %@", own.Title, TimeFormat.String(own.Length));
            if (MergedPart is { } other) text += L.F("（合併「%@」%@）", other.Title, TimeFormat.String(other.Length));
            return text;
        }
    }

    /// <summary>下方的摘要：粗體的一句、灰字的補充</summary>
    public (string Main, string Detail) Summary
    {
        get
        {
            var kept = KeptDuration();
            var removed = RemovedLines;
            if (MergedPart is { } other && OwnPart is { } own)
                return (L.F("合併後 %@", TimeFormat.String(kept)),
                    L.F("（這場 %@＋「%@」%@）", TimeFormat.String(KeptDuration(own.Offset, own.Offset + own.Length)), other.Title,
                        TimeFormat.String(KeptDuration(other.Offset, other.Offset + other.Length))));
            if (Cut is double cut)
                return (L.F("分成兩場：%@ 和 %@", TimeFormat.String(KeptDuration(0, cut)), TimeFormat.String(KeptDuration(cut))),
                    removed > 0 ? L.F("（刪掉 %@、%lld 句）", TimeFormat.String(Total - kept), removed) : "");
            var marks = RemovedMarks;
            var detail = !HasChanges ? L.T("（拖兩端的把手裁切，或在波形上選一段刪掉）")
                : marks > 0 ? L.F("（刪掉 %@、%lld 句、%lld 個標記重點）", TimeFormat.String(Total - kept), removed, marks)
                : L.F("（刪掉 %@、%lld 句）", TimeFormat.String(Total - kept), removed);
            return (L.F("剩 %@", TimeFormat.String(kept)), detail);
        }
    }

    /// <summary>套用前說清楚會怎樣（M§81.5）：「・」開頭的好幾行，確認的彈窗畫成條列</summary>
    public string ApplyMessage(bool summarizerReady)
    {
        var lines = new List<string>();
        double kept = KeptDuration(), total = Total;
        if (MergedPart is { } other)
            lines.Add(L.F("合併「%@」，合併後 %@。那場會移到垃圾桶，30 天內可以還原。", other.Title, TimeFormat.String(kept)));
        else
        {
            var parts = new List<string>();
            if (Start > 0.05) parts.Add(L.F("裁掉開頭 %@", TimeFormat.String(Start)));
            if (End < total - 0.05) parts.Add(L.F("結尾 %@", TimeFormat.String(total - End)));
            var middle = Deleted.Where(r => r.Lower > Start && r.Upper < End).ToList();
            if (middle.Count > 0)
                parts.Add(L.F("刪掉中間 %lld 段（%@）", middle.Count, TimeFormat.String(middle.Sum(r => r.Upper - r.Lower))));
            if (parts.Count > 0)
                lines.Add(L.F("會議從 %@ 變成 %@：%@。", TimeFormat.String(total), TimeFormat.String(kept),
                    string.Join(MeetingLanguages.Ui("，", ", "), parts)));
        }
        if (Cut is double cut)
            lines.Add(L.F("從 %@ 分成兩場：%@ 和 %@，後面那場叫「%@（2）」。", TimeFormat.String(cut), TimeFormat.String(KeptDuration(0, cut)),
                TimeFormat.String(KeptDuration(cut)), OwnPart?.Title ?? ""));
        int removed = RemovedLines, marks = RemovedMarks;
        if (removed > 0 || marks > 0)
            lines.Add(marks > 0 ? L.F("刪掉 %lld 句逐字稿、%lld 個標記重點；我的筆記都留著。", removed, marks) : L.F("刪掉 %lld 句逐字稿；我的筆記都留著。", removed));
        lines.Add(summarizerReady ? L.T("會議紀錄會重新整理，現在這份留在版本紀錄。") : L.T("會議紀錄要重新整理：設定好整理工具後按「重新整理」。"));
        lines.Add(L.T("30 天內可以從「更多」還原成編輯前的樣子。"));
        return string.Join("\n", lines.Select(l => "・" + l));  // l10n-skip
    }

    /// <summary>右邊的說話者圖例：照逐字稿出現的順序、名字不重複，最多 4 位，多的寫「+N」</summary>
    public List<(string Name, int Idx)> Legend()
    {
        var list = new List<(string Name, int Idx)>();
        foreach (var line in Lines)
            if (line.Speaker is int idx && !list.Any(s => s.Name == line.SpeakerName)) list.Add((line.SpeakerName, idx));
        return list;
    }
}
