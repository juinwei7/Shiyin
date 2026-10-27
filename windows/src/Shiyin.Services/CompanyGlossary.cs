using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shiyin.Services;

/// <summary>
/// 公司詞彙表（R53、M§36.3、design-windows §12.11）：企業伺服器後台維護的詞，存在 SHIYIN_HOME\enterprise-glossary.json，
/// 跟自己的詞一起用。轉錄、整理在背景執行緒讀，用鎖保護。file 是 null 時只放記憶體（測試）
/// </summary>
public sealed class CompanyGlossary(string? file)
{
    readonly object gate = new();
    List<string>? cache;

    /// <summary>換了（畫面、建議的新詞要重算）</summary>
    public event Action? Changed;

    /// <summary>上次從伺服器拿到的（連不上時照樣用）</summary>
    public IReadOnlyList<string> Terms
    {
        get { lock (gate) return cache ??= Load(); }
    }

    List<string> Load()
    {
        if (file is null || !File.Exists(file)) return [];
        try { return (JsonNode.Parse(File.ReadAllText(file))?["terms"] as JsonArray)?.Select(t => t?.GetValue<string>()).OfType<string>().ToList() ?? []; }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException) { return []; }
    }

    public void Save(IEnumerable<string> terms, string? updatedAt)
    {
        var cleaned = Merge(terms, []);
        if (cleaned.SequenceEqual(Terms)) return;
        if (file is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var json = new JsonObject { ["terms"] = new JsonArray([.. cleaned.Select(t => (JsonNode)t)]), ["updatedAt"] = updatedAt };
            File.WriteAllText(file + ".tmp", json.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            File.Move(file + ".tmp", file, overwrite: true);
        }
        lock (gate) cache = cleaned;
        Changed?.Invoke();
    }

    /// <summary>登出時刪掉（R53.4）</summary>
    public void Clear()
    {
        if (file is not null && File.Exists(file)) File.Delete(file);
        lock (gate) cache = [];
        Changed?.Invoke();
    }

    /// <summary>自己的詞在前、公司的接在後面，不分大小寫去掉重複（轉錄的提示詞有長度上限，R53.2）</summary>
    public static List<string> Merge(IEnumerable<string> own, IEnumerable<string> company)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return [.. own.Concat(company).Select(t => t.Trim()).Where(t => t.Length > 0 && seen.Add(t))];
    }

    public List<string> Merged(IEnumerable<string> own) => Merge(own, Terms);

    public bool Contains(string term) => Terms.Contains(term.Trim(), StringComparer.OrdinalIgnoreCase);
}
