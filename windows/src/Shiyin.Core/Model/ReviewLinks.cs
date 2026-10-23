namespace Shiyin.Model;

/// <summary>一條紀錄在會議紀錄裡的段落（同 Mac 的 NoteItemRef，M§31.1）。順序就是標準答案裡的排序</summary>
public enum NoteSection { Summary, Decisions, ActionItems, OpenQuestions, TopicPoint, Custom }

/// <summary>一條紀錄的位置：段落加位置；議題的要點、自訂段落的條目多一個 Sub（第幾個議題／段落的第幾條）</summary>
public readonly record struct NoteItemRef(NoteSection Section, int Index, int Sub = 0)
{
    public static NoteItemRef Summary(int i) => new(NoteSection.Summary, i);
    public static NoteItemRef Decisions(int i) => new(NoteSection.Decisions, i);
    public static NoteItemRef ActionItems(int i) => new(NoteSection.ActionItems, i);
    public static NoteItemRef OpenQuestions(int i) => new(NoteSection.OpenQuestions, i);
    public static NoteItemRef TopicPoint(int topic, int point) => new(NoteSection.TopicPoint, topic, point);
    public static NoteItemRef Custom(int section, int item) => new(NoteSection.Custom, section, item);

    /// <summary>標準答案用的名稱（同 Mac 的 GoldenDump）</summary>
    public string SectionName => Section switch
    {
        NoteSection.Summary => "summary",
        NoteSection.Decisions => "decisions",
        NoteSection.ActionItems => "actionItems",
        NoteSection.OpenQuestions => "openQuestions",
        NoteSection.TopicPoint => "topicPoint",
        _ => "custom",
    };
}

/// <summary>待確認的題目對應到紀錄的條目（R41.1、M§31.1，同 Mac 的 MeetingNotes.reviewLinks）：
/// 題目和條目的時間點同一秒的全標；沒有同一秒的，標時間最接近、差距 10 秒以內的一條。對不到的題目不在結果裡。
/// 議題的標題不算條目，只有要點算。載入會議時算一次（MeetingDetail.ReviewLinks）</summary>
public static class ReviewLinks
{
    public const double MaxGap = 10;

    public static Dictionary<NoteItemRef, List<int>> Compute(MeetingNotes notes)
    {
        var entries = new List<(NoteItemRef Ref, double Seconds)>();
        void Add(IReadOnlyList<MeetingNotes.Item> items, Func<int, NoteItemRef> make)
        {
            for (var i = 0; i < items.Count; i++)
                if (TimeFormat.Seconds(items[i].Time) is double s) entries.Add((make(i), s));
        }
        Add(notes.Summary, NoteItemRef.Summary);
        Add(notes.Decisions, NoteItemRef.Decisions);
        for (var i = 0; i < notes.ActionItems.Count; i++)
            if (TimeFormat.Seconds(notes.ActionItems[i].Time) is double s) entries.Add((NoteItemRef.ActionItems(i), s));
        Add(notes.OpenQuestions, NoteItemRef.OpenQuestions);
        for (var t = 0; t < notes.Topics.Count; t++) { var topic = t; Add(notes.Topics[t].Points, i => NoteItemRef.TopicPoint(topic, i)); }
        for (var c = 0; c < notes.Custom.Count; c++) { var section = c; Add(notes.Custom[c].Items, i => NoteItemRef.Custom(section, i)); }

        var links = new Dictionary<NoteItemRef, List<int>>();
        void Link(NoteItemRef r, int question)
        {
            if (!links.TryGetValue(r, out var list)) links[r] = list = [];
            list.Add(question);
        }
        for (var index = 0; index < notes.ReviewItems.Count; index++)
        {
            if (TimeFormat.Seconds(notes.ReviewItems[index].Time) is not double seconds) continue;
            var exact = entries.Where(e => e.Seconds == seconds).ToList();
            if (exact.Count > 0)
            {
                foreach (var e in exact) Link(e.Ref, index);
                continue;
            }
            if (entries.Count == 0) continue;
            var nearest = entries[0];
            foreach (var e in entries) if (Math.Abs(e.Seconds - seconds) < Math.Abs(nearest.Seconds - seconds)) nearest = e;
            if (Math.Abs(nearest.Seconds - seconds) <= MaxGap) Link(nearest.Ref, index);
        }
        return links;
    }
}
