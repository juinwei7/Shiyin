using System.Reflection;
using System.Text;

namespace Shiyin.Text;

/// <summary>
/// 簡體轉台灣繁體（M§32.5、D108）：Qwen3-ASR 輸出簡體，用 OpenCC 的字表轉成繁體。跟 Mac 的 <c>ChineseScript.toTaiwan</c> 同一套：
/// 先在詞組表找最長的匹配（鍵和值相同的詞組也算，它們擋住逐字轉換），沒有就查單字表，查不到原樣保留；
/// 全部轉完套台灣用字，最後「臺」寫成「台」。字表是 <c>Resources/opencc/*.tsv</c>，內嵌在這個組件裡。
/// </summary>
public static class ChineseScript
{
    sealed class Tables
    {
        public readonly Dictionary<string, string> Phrases = new();
        public readonly Dictionary<string, string> Characters = new();
        public readonly Dictionary<string, string> Variants = new();
        public int LongestPhrase = 1;
    }

    static readonly Lazy<Tables> Loaded = new(Load);

    static Tables Load()
    {
        var tables = new Tables();
        foreach (var (name, target) in new[] { ("STPhrases.tsv", tables.Phrases), ("STCharacters.tsv", tables.Characters), ("TWVariants.tsv", tables.Variants) })
        {
            using var stream = typeof(ChineseScript).Assembly.GetManifestResourceStream("opencc/" + name)
                ?? throw new InvalidOperationException($"找不到內嵌的字表 {name}");   // l10n-skip
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                var tab = line.IndexOf('\t');
                if (tab <= 0) continue;
                var key = line[..tab];
                target[key] = line[(tab + 1)..];
                if (ReferenceEquals(target, tables.Phrases))
                    tables.LongestPhrase = Math.Max(tables.LongestPhrase, SwiftText.Count(key));
            }
        }
        return tables;
    }

    static bool IsCjk(string grapheme)
    {
        var cp = char.ConvertToUtf32(grapheme, 0);
        return (cp >= 0x3400 && cp <= 0x9FFF) || (cp >= 0xF900 && cp <= 0xFAFF);
    }

    public static string ToTaiwan(string text)
    {
        var tables = Loaded.Value;
        var chars = SwiftText.Characters(text);
        var converted = new StringBuilder(text.Length);
        var i = 0;
        while (i < chars.Count)
        {
            var matched = false;
            if (IsCjk(chars[i]))
            {
                for (var length = Math.Min(tables.LongestPhrase, chars.Count - i); length >= 2; length--)
                {
                    var candidate = string.Concat(chars.GetRange(i, length));
                    if (tables.Phrases.TryGetValue(candidate, out var value))
                    {
                        converted.Append(value);
                        i += length;
                        matched = true;
                        break;
                    }
                }
            }
            if (!matched)
            {
                converted.Append(tables.Characters.TryGetValue(chars[i], out var single) ? single : chars[i]);
                i++;
            }
        }
        var result = new StringBuilder(converted.Length);
        foreach (var g in SwiftText.Characters(converted.ToString()))
        {
            var v = tables.Variants.TryGetValue(g, out var variant) ? variant : g;
            result.Append(v == "臺" ? "台" : v);   // l10n-skip
        }
        return result.ToString();
    }

    // MARK: 繁轉簡（M§47.1、D153）

    /// <summary>繁轉簡的字表：台灣用字換回一般繁體（詞組、單字），再轉簡體（詞組、單字）</summary>
    sealed class SimplifiedTables
    {
        public readonly Dictionary<string, string> StandardPhrases = new();
        public int StandardLongest = 1;
        public readonly Dictionary<string, string> StandardCharacters = new();
        public readonly Dictionary<string, string> Phrases = new();
        public int Longest = 1;
        public readonly Dictionary<string, string> Characters = new();
    }

    static readonly Lazy<SimplifiedTables> LoadedSimplified = new(LoadSimplified);

    static IEnumerable<(string Key, string Value)> Rows(string name)
    {
        using var stream = typeof(ChineseScript).Assembly.GetManifestResourceStream("opencc/" + name)
            ?? throw new InvalidOperationException($"找不到內嵌的字表 {name}");   // l10n-skip
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            var tab = line.IndexOf('\t');
            if (tab > 0) yield return (line[..tab], line[(tab + 1)..]);
        }
    }

    static SimplifiedTables LoadSimplified()
    {
        var t = new SimplifiedTables();
        foreach (var (key, value) in Rows("TWVariantsRevPhrases.tsv"))
        {
            t.StandardPhrases[key] = value;
            t.StandardLongest = Math.Max(t.StandardLongest, SwiftText.Count(key));
        }
        foreach (var (key, value) in Rows("TSPhrases.tsv"))
        {
            t.Phrases[key] = value;
            t.Longest = Math.Max(t.Longest, SwiftText.Count(key));
        }
        // 台灣用字的單字表反過來查（同 Mac）；同一個字有兩個來源時用先出現的
        foreach (var (key, value) in Rows("TWVariants.tsv"))
            if (SwiftText.Count(key) == 1 && SwiftText.Count(value) == 1) t.StandardCharacters.TryAdd(value, key);
        foreach (var (key, value) in Rows("TSCharacters.tsv"))
            if (SwiftText.Count(key) == 1 && SwiftText.Count(value) == 1) t.Characters[key] = value;
        return t;
    }

    /// <summary>台灣繁體轉簡體（同 OpenCC 的 tw2s，只換字不換用語；同 Mac 的 toSimplified）</summary>
    public static string ToSimplified(string text)
    {
        var t = LoadedSimplified.Value;
        var standard = Convert(SwiftText.Characters(text), t.StandardPhrases, t.StandardLongest, t.StandardCharacters);
        return Convert(SwiftText.Characters(standard), t.Phrases, t.Longest, t.Characters);
    }

    /// <summary>詞組最長匹配（只有中日韓的字才可能是詞組的開頭），沒有就查單字表，查不到原樣保留</summary>
    static string Convert(List<string> chars, Dictionary<string, string> phrases, int longest, Dictionary<string, string> characters)
    {
        var out_ = new StringBuilder();
        var i = 0;
        while (i < chars.Count)
        {
            var matched = false;
            if (IsCjk(chars[i]))
            {
                for (var length = Math.Min(longest, chars.Count - i); length >= 2; length--)
                {
                    if (phrases.TryGetValue(string.Concat(chars.GetRange(i, length)), out var value))
                    {
                        out_.Append(value);
                        i += length;
                        matched = true;
                        break;
                    }
                }
            }
            if (!matched)
            {
                out_.Append(characters.TryGetValue(chars[i], out var single) ? single : chars[i]);
                i++;
            }
        }
        return out_.ToString();
    }
}

