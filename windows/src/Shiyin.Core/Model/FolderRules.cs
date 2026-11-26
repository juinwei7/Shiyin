using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Shiyin.Text;

namespace Shiyin.Model;

/// <summary>資料夾的自動加入條件（R98、M§72，同 Mac 的 Store/FolderRules.swift 的 FolderRule）：任一個條件符合就放進去</summary>
public sealed class FolderRule
{
    /// <summary>哪個資料夾的條件（v18、R118、M§92）</summary>
    public long FolderId { get; set; }
    public List<string> Keywords { get; set; } = [];
    /// <summary>人物 id：選的人都有參加才算</summary>
    public List<long> People { get; set; } = [];
    public string? TemplateId { get; set; }
    public string CreatedAt { get; set; } = Clock.Now();

    public bool IsEmpty => Keywords.Count == 0 && People.Count == 0 && TemplateId is null;

    /// <summary>標題包含任一個詞（不分大小寫）、選的人都有參加、用這個範本：任一個符合就算（定期會議的條件 D177 拿掉）</summary>
    public bool Matches(string title, IReadOnlySet<long> attended, string templateId)
    {
        if (Keywords.Any(k => k.Length > 0 && title.Contains(k, StringComparison.OrdinalIgnoreCase))) return true;
        if (People.Count > 0 && People.All(attended.Contains)) return true;
        if (TemplateId is { } t && t == templateId) return true;
        return false;
    }
}

/// <summary>
/// 會議像不像（資料夾的建議用，R98.3、M§72，同 Mac 的 MeetingSimilarity）：同一群人、核心人物、標題的共同詞。
/// 原本是找定期會議的 SeriesDetector（M§73，D177 拿掉），資料夾的建議用到的這幾個留著
/// </summary>
public static partial class MeetingSimilarity
{
    /// <summary>同一群人：共同的人物至少 2 位、Jaccard ≥ 0.5</summary>
    public static bool SamePeople(IReadOnlySet<long> a, IReadOnlySet<long> b)
    {
        var common = a.Count(b.Contains);
        return common >= 2 && (double)common / a.Union(b).Count() >= 0.5;
    }

    /// <summary>過半數場次都有的人</summary>
    public static HashSet<long> Core(IEnumerable<IReadOnlySet<long>> people)
    {
        var list = people.ToList();
        var counts = new Dictionary<long, int>();
        foreach (var set in list)
            foreach (var id in set) counts[id] = counts.GetValueOrDefault(id) + 1;
        return counts.Where(p => p.Value * 2 > list.Count).Select(p => p.Key).ToHashSet();
    }

    /// <summary>
    /// 共同的詞：標題先拿掉數字和跟著的日期單位（11/3、11月3日、2026-11-16），最長的共同子字串去掉頭尾的空白、標點後
    /// 至少 2 個字（只有拉丁字母時至少 3 個字母）。字數照 Swift 的 Character（字形叢集）
    /// </summary>
    public static string? CommonWord(IReadOnlyList<string> titles)
    {
        var list = titles.Select(t => SwiftText.Characters(WithoutNumbers(t))).Where(c => c.Count > 0).ToList();
        if (list.Count < 2) return null;
        var shortest = list.MinBy(c => c.Count)!;
        var lowered = list.Select(c => SwiftText.Lower(string.Concat(c))).ToList();
        for (var length = shortest.Count; length >= 2; length--)
            for (var start = 0; start <= shortest.Count - length; start++)
            {
                var piece = string.Concat(shortest.Skip(start).Take(length));
                var lower = SwiftText.Lower(piece);
                if (!lowered.All(t => t.Contains(lower, StringComparison.Ordinal))) continue;
                var cleaned = Clean(piece);
                if (IsSignificant(cleaned) && lowered.All(t => IsWholeWord(SwiftText.Lower(cleaned), t))) return cleaned;
            }
        return null;
    }

    /// <summary>
    /// 英文要是完整的單字：頭尾是英文字母或數字時，前後不能接著英文字母或數字（「Control」「Conceptual」不會取出「Con」）。
    /// 中文沒有空格，頭尾是中文時不檢查。字照 Swift 的 Character
    /// </summary>
    public static bool IsWholeWord(string word, string text)
    {
        var w = SwiftText.Characters(word);
        var t = SwiftText.Characters(text);
        if (w.Count == 0 || w.Count > t.Count) return false;
        static bool Latin(string c) => c.Length == 1 && c[0] < 128 && char.IsLetterOrDigit(c[0]);
        for (var i = 0; i <= t.Count - w.Count; i++)
        {
            if (!w.Select((c, k) => c == t[i + k]).All(x => x)) continue;
            var before = !Latin(w[0]) || i == 0 || !Latin(t[i - 1]);
            var after = !Latin(w[^1]) || i + w.Count == t.Count || !Latin(t[i + w.Count]);
            if (before && after) return true;
        }
        return false;
    }

    /// <summary>前面接英文字母的數字（Q3、v2）留著</summary>
    [GeneratedRegex(@"(?<![A-Za-z])[0-9０-９]+[年月日號号/.\-]?")]  // l10n-skip
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    public static string WithoutNumbers(string title) => Spaces().Replace(NumberPattern().Replace(title, " "), " ");

    /// <summary>頭尾去掉空白、換行、標點、符號（Swift 的 CharacterSet：whitespacesAndNewlines、punctuationCharacters、symbols）</summary>
    public static string Clean(string text)
    {
        var runes = text.EnumerateRunes().ToList();
        static bool Edge(Rune r) => Rune.IsWhiteSpace(r) || Rune.IsPunctuation(r) || Rune.IsSymbol(r);
        var start = 0;
        var end = runes.Count;
        while (start < end && Edge(runes[start])) start++;
        while (end > start && Edge(runes[end - 1])) end--;
        var sb = new StringBuilder();
        for (var i = start; i < end; i++) sb.Append(runes[i].ToString());
        return sb.ToString();
    }

    /// <summary>至少 2 個字母（CharacterSet.letters：L、M 類），只有 ASCII 時至少 3 個</summary>
    public static bool IsSignificant(string word)
    {
        var letters = word.EnumerateRunes().Where(r => Rune.GetUnicodeCategory(r) is
            UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter
            or UnicodeCategory.OtherLetter or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark).ToList();
        if (letters.Count < 2) return false;
        var latinOnly = letters.All(r => r.IsAscii);
        return !latinOnly || letters.Count >= 3;
    }
}

/// <summary>
/// 不在資料夾的會議很像某個資料夾裡的會議（R98.3、M§72）：Reason 是都有這幾位參加（People）或標題都有這個詞（Title）。
/// FolderId 是那個資料夾，Folder 是它的名字（顯示用，R118、M§92.2）
/// </summary>
public sealed record FolderSuggestion(long FolderId, string Folder, IReadOnlyList<long>? People, string? Title, int Count);

public static class FolderSuggester
{
    /// <summary>FolderId 是 null 時不在資料夾；Folder 是資料夾的名字（好幾個資料夾都像時照名字挑，同以前）</summary>
    public sealed record Input(long Id, long? FolderId, string Folder, string Title, IReadOnlySet<long> People);

    /// <summary>通用的詞不算共同的詞</summary>
    public static readonly HashSet<string> GenericWords = ["會議", "会议", "會議記錄", "meeting", "meetings", "討論", "讨论", "会議", "회의"];  // l10n-skip

    /// <summary>
    /// 跟每個資料夾（至少 2 場）比：同一群人的場數，或標題有共同的詞的場數；至少 2 場才建議，取場數最多的（一樣多時名稱排前面的、
    /// 同一個資料夾人的比較準）。資料夾照 id 分組；folderOrder 是資料夾名稱的排序（同 Mac 的 localizedStandardCompare）
    /// </summary>
    public static FolderSuggestion? Best(Input meeting, IEnumerable<Input> others, IComparer<string> folderOrder)
    {
        FolderSuggestion? best = null;
        var byFolder = others.Where(o => o.FolderId is not null && o.Id != meeting.Id).GroupBy(o => o.FolderId!.Value).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var folderId in byFolder.Keys.OrderBy(id => byFolder[id][0].Folder, folderOrder))
        {
            var members = byFolder[folderId];
            var folder = members[0].Folder;
            if (members.Count < 2) continue;
            var options = new List<FolderSuggestion>();
            var same = members.Where(m => MeetingSimilarity.SamePeople(meeting.People, m.People)).ToList();
            if (same.Count >= 2)
            {
                var core = MeetingSimilarity.Core(same.Select(m => m.People));
                core.IntersectWith(meeting.People);
                options.Add(new FolderSuggestion(folderId, folder, core.Order().ToList(), null, same.Count));
            }
            var words = new Dictionary<string, int>();
            foreach (var member in members)
                if (MeetingSimilarity.CommonWord([meeting.Title, member.Title]) is { } word && !GenericWords.Contains(SwiftText.Lower(word)))
                    words[word] = words.GetValueOrDefault(word) + 1;
            // 場數最多的詞；一樣多時照字串排序取第一個
            var top = words.OrderByDescending(p => p.Value).ThenBy(p => p.Key, SwiftText.Comparer).FirstOrDefault();
            if (top.Key is not null && top.Value >= 2) options.Add(new FolderSuggestion(folderId, folder, null, top.Key, top.Value));
            foreach (var option in options)
                if (best is null || option.Count > best.Count) best = option;
        }
        return best;
    }
}
