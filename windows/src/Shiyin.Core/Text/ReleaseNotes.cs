namespace Shiyin.Text;

/// <summary>
/// 更新說明的摘要（R10.6，M§12；同 Mac 的 ReleaseNotes.swift，標準答案 util/release-notes.json）：tag 的說明文字是「中文一段、空一行、英文一段」，
/// 每段第一行是標題、其餘是「- 」開頭的條目。詢問更新的視窗只列介面語言那一段的前幾項，其餘寫「還有 N 項」
/// </summary>
public static class ReleaseNotes
{
    public const int MaxItems = 4;

    /// <summary>要列出的條目（不含「- 」）和沒列的項數；沒有條目的說明就拿標題以外的行</summary>
    public static (IReadOnlyList<string> Items, int More) Brief(string notes, bool english)
    {
        var paragraphs = new List<List<string>>();
        var current = new List<string>();
        foreach (var raw in notes.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim(' ', '\t');
            if (line.Length == 0)
            {
                if (current.Count > 0) { paragraphs.Add(current); current = []; }
            }
            else current.Add(line);
        }
        if (current.Count > 0) paragraphs.Add(current);
        if (paragraphs.Count == 0) return ([], 0);
        var picked = english
            ? paragraphs.FirstOrDefault(p => p[0].StartsWith("shiyin", StringComparison.OrdinalIgnoreCase)) ?? paragraphs[^1]
            : paragraphs.FirstOrDefault(p => p[0].StartsWith("拾音", StringComparison.Ordinal)) ?? paragraphs[0];   // l10n-skip
        var items = picked.Where(l => l.StartsWith("- ", StringComparison.Ordinal)).Select(l => l[2..].Trim(' ', '\t')).Where(l => l.Length > 0).ToList();
        if (items.Count == 0) items = picked.Count > 1 ? picked.Skip(1).ToList() : picked;
        var shown = items.Take(MaxItems).ToList();
        return (shown, items.Count - shown.Count);
    }
}
