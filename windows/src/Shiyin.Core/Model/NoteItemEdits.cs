namespace Shiyin.Model;

/// <summary>
/// 就地改、刪一條紀錄（R100、M§74，同 Mac 的 MeetingNotes.text(at:)、setText(_:at:)、remove(at:)）：待辦是事項（負責人、期限照舊用「編輯」）；
/// 位置不在了時 TextAt 是 null、WithText／Removing 原樣回傳。都回傳新的一份，原本的不動
/// </summary>
public static class NoteItemEdits
{
    public static string? TextAt(this MeetingNotes n, NoteItemRef r) => r.Section switch
    {
        NoteSection.Summary => At(n.Summary, r.Index)?.Text,
        NoteSection.Decisions => At(n.Decisions, r.Index)?.Text,
        NoteSection.ActionItems => At(n.ActionItems, r.Index)?.Task,
        NoteSection.OpenQuestions => At(n.OpenQuestions, r.Index)?.Text,
        NoteSection.TopicPoint => At(At(n.Topics, r.Index)?.Points, r.Sub)?.Text,
        NoteSection.Custom => At(At(n.Custom, r.Index)?.Items, r.Sub)?.Text,
        _ => At(At(n.People, r.Index)?.Points, r.Sub)?.Text,
    };

    static T? At<T>(List<T>? list, int i) where T : class => list is not null && i >= 0 && i < list.Count ? list[i] : null;

    /// <summary>改一條的文字（時間點不變）</summary>
    public static MeetingNotes WithText(this MeetingNotes n, NoteItemRef r, string text) => n.TextAt(r) is null ? n : r.Section switch
    {
        NoteSection.Summary => n with { Summary = Replace(n.Summary, r.Index, i => i with { Text = text }) },
        NoteSection.Decisions => n with { Decisions = Replace(n.Decisions, r.Index, i => i with { Text = text }) },
        NoteSection.ActionItems => n with { ActionItems = Replace(n.ActionItems, r.Index, a => a with { Task = text }) },
        NoteSection.OpenQuestions => n with { OpenQuestions = Replace(n.OpenQuestions, r.Index, i => i with { Text = text }) },
        NoteSection.TopicPoint => n with { Topics = Replace(n.Topics, r.Index, t => t with { Points = Replace(t.Points, r.Sub, i => i with { Text = text }) }) },
        NoteSection.Custom => n with { Custom = Replace(n.Custom, r.Index, c => c with { Items = Replace(c.Items, r.Sub, i => i with { Text = text }) }) },
        _ => n with { People = Replace(n.People, r.Index, p => p with { Points = Replace(p.Points, r.Sub, i => i with { Text = text }) }) },
    };

    /// <summary>刪掉一條</summary>
    public static MeetingNotes Removing(this MeetingNotes n, NoteItemRef r) => n.TextAt(r) is null ? n : r.Section switch
    {
        NoteSection.Summary => n with { Summary = Remove(n.Summary, r.Index) },
        NoteSection.Decisions => n with { Decisions = Remove(n.Decisions, r.Index) },
        NoteSection.ActionItems => n with { ActionItems = Remove(n.ActionItems, r.Index) },
        NoteSection.OpenQuestions => n with { OpenQuestions = Remove(n.OpenQuestions, r.Index) },
        NoteSection.TopicPoint => n with { Topics = Replace(n.Topics, r.Index, t => t with { Points = Remove(t.Points, r.Sub) }) },
        NoteSection.Custom => n with { Custom = Replace(n.Custom, r.Index, c => c with { Items = Remove(c.Items, r.Sub) }) },
        _ => n with { People = Replace(n.People, r.Index, p => p with { Points = Remove(p.Points, r.Sub) }) },
    };

    static List<T> Replace<T>(List<T> list, int index, Func<T, T> change)
    {
        var copy = new List<T>(list);
        copy[index] = change(copy[index]);
        return copy;
    }

    static List<T> Remove<T>(List<T> list, int index)
    {
        var copy = new List<T>(list);
        copy.RemoveAt(index);
        return copy;
    }
}
