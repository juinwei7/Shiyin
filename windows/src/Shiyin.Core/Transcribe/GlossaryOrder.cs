namespace Shiyin.Transcribe;

/// <summary>轉錄排詞彙用的文字（R124、M§98.2）：這場會議、同資料夾之前的幾場、之前的幾場（一段是一場）</summary>
public sealed record GlossaryContext(string Meeting, IReadOnlyList<string> Folder, IReadOnlyList<string> Recent)
{
    public static readonly GlossaryContext Empty = new("", [], []);
}

/// <summary>轉錄時先放跟這場會議有關的詞（R124、M§98.1），規則跟 Mac 的 GlossaryOrder 相同。標準答案 util/glossary-order.json</summary>
public static class GlossaryOrder
{
    /// <param name="Related">前幾層（這場出現過的、同組的、同資料夾、最近幾場出現過）的詞數</param>
    public sealed record Ranked(List<string> Terms, int Related);

    /// <param name="groups">groups[i] 是 terms[i] 在哪些群組（M§100.2；公司的詞是空的，長度不夠當成沒有）</param>
    public static Ranked Rank(IReadOnlyList<string> terms, GlossaryContext context, IReadOnlyList<IReadOnlyList<long>>? groups = null)
    {
        var meeting = Folded(context.Meeting);
        var folder = context.Folder.Select(Folded).ToList();
        var recent = context.Recent.Select(Folded).ToList();
        var keys = terms.Select(Folded).ToList();
        var placed = new bool[terms.Count];
        var order = new List<int>();
        for (var i = 0; i < terms.Count; i++)
        {
            if (!OccursFolded(keys[i], meeting)) continue;
            order.Add(i);
            placed[i] = true;
        }
        // 跟這場出現過的詞在同一組的其他詞（R126.6）
        IReadOnlyList<long> GroupsOf(int i) => groups is not null && i < groups.Count ? groups[i] : [];
        var hit = order.SelectMany(GroupsOf).ToHashSet();
        if (hit.Count > 0)
            for (var i = 0; i < terms.Count; i++)
            {
                if (placed[i] || !GroupsOf(i).Any(hit.Contains)) continue;
                order.Add(i);
                placed[i] = true;
            }
        // 幾場出現過，大的先；一樣的照原本的順序（OrderByDescending 是穩定排序）
        foreach (var texts in new[] { folder, recent })
        {
            var scored = Enumerable.Range(0, terms.Count).Where(i => !placed[i])
                .Select(i => (Index: i, Score: texts.Count(t => OccursFolded(keys[i], t))))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score).ToList();
            foreach (var (i, _) in scored)
            {
                order.Add(i);
                placed[i] = true;
            }
        }
        var related = order.Count;
        order.AddRange(Enumerable.Range(0, terms.Count).Where(i => !placed[i]));
        return new Ranked([.. order.Select(i => terms[i])], related);
    }

    /// <summary>英文字母不分大小寫；頭尾是英文字母或數字時，前後不能接著英文字母或數字（R124.2）</summary>
    public static bool Occurs(string term, string text) => OccursFolded(Folded(term), Folded(text));

    /// <summary>只把 ASCII 的 A–Z 換成 a–z：ToLowerInvariant 的大小寫表跟 Mac 不一樣（ß、İ、全形字），比對照 UTF-16 碼位</summary>
    static string Folded(string s) => string.Create(s.Length, s, (span, source) =>
    {
        for (var i = 0; i < source.Length; i++) span[i] = source[i] is >= 'A' and <= 'Z' ? (char)(source[i] + 32) : source[i];
    });

    static bool IsWordChar(char c) => c is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    static bool OccursFolded(string term, string text)
    {
        if (term.Length == 0 || term.Length > text.Length) return false;
        bool checkStart = IsWordChar(term[0]), checkEnd = IsWordChar(term[^1]);
        for (var i = text.IndexOf(term, StringComparison.Ordinal); i >= 0; i = text.IndexOf(term, i + 1, StringComparison.Ordinal))
        {
            var end = i + term.Length;
            if (checkStart && i > 0 && IsWordChar(text[i - 1])) continue;
            if (checkEnd && end < text.Length && IsWordChar(text[end])) continue;
            return true;
        }
        return false;
    }
}
