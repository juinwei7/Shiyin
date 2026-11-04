using System.Text.Json;
using Shiyin.Localization;

namespace Shiyin.Model;

/// <summary>手寫、調過的提示詞和標籤有三份（M§41.2、D140）：每種會議語言用其中一份（粵語用中文、韓文用英文）</summary>
public enum PromptBase { Zh, En, Ja }

/// <summary>語言檔裡的一筆（M§41.1、D139）：一種會議語言的所有做法。跟 Mac 讀同一份 <c>Resources/languages.json</c></summary>
public sealed record LanguageProfile(
    string Code, string Name, LanguageProfile.Names UiName, string Tier, string Base, string? OutputName, LanguageProfile.NoteTexts? TranscriptNote,
    string Locale, LanguageProfile.LiveSpec Live, LanguageProfile.TranscriptionSpec Transcription, LanguageProfile.ScriptFix[]? ScriptFixes, bool Spaced,
    LanguageProfile.WhisperPromptSpec WhisperPrompt, string? Detect, string Family, LanguageProfile.OcrSpec Ocr, LanguageProfile.FontSpec Fonts,
    string Speaker, Dictionary<string, string>? Strings)
{
    public sealed record Names(string Zh, string En);
    public sealed record NoteTexts(string Notes, string Correction);
    /// <summary>即時逐字稿：Mac 的 SpeechAnalyzer、Windows 的 SenseVoice 語言；WindowsConvert 是 SenseVoice 輸出要做的 OpenCC 轉換</summary>
    public sealed record LiveSpec(string Mac, string Windows, string? WindowsConvert);
    /// <summary>Model：breeze、turbo（Whisper large-v3-turbo）、qwen；EngineSetting：跟著「Breeze／Qwen3-ASR」的設定；Whisper、Qwen 是 null 時那個引擎不能用</summary>
    public sealed record TranscriptionSpec(string Model, bool EngineSetting, string? Whisper, string? Qwen, bool Traditional);
    /// <summary>簡轉繁之後的修正：From 換成 To，除非那個字在 Keep 的詞裡（粵語的「系」「繫」多半是「係」，M§41.3）</summary>
    public sealed record ScriptFix(string From, string To, string[] Keep);
    public sealed record WhisperPromptSpec(string Base, string Glossary, string Separator, int Limit, string? Natural, string? NaturalList);
    public sealed record OcrSpec(string[] Mac, string[] Windows, bool WindowsUserProfile);
    public sealed record FontPair(string Body, string Heading);
    public sealed record FontSpec(string Typesetting, string Lang, string TitleMac, string TitleWindows, string ContentWindows,
                                  FontPair HtmlMac, FontPair HtmlWindows, FontPair Docx);

    public bool IsGeneral => Tier == "general";

    public PromptBase BaseKind => Base switch { "en" => PromptBase.En, "ja" => PromptBase.Ja, _ => PromptBase.Zh };

    /// <summary>套用 ScriptFixes：一個字一個字看，落在 Keep 的詞裡就不換（同 Mac 的 fixScript）</summary>
    public string FixScript(string text)
    {
        if (ScriptFixes is not { Length: > 0 } fixes) return text;
        var chars = text.ToCharArray();
        foreach (var fix in fixes)
        {
            if (fix.From.Length != 1 || fix.To.Length != 1) continue;
            char from = fix.From[0], to = fix.To[0];
            for (var i = 0; i < chars.Length; i++)
            {
                if (chars[i] != from) continue;
                var kept = fix.Keep.Any(word => Enumerable.Range(0, word.Length).Any(j =>
                    word[j] == from && i - j >= 0 && i - j + word.Length <= chars.Length && new string(chars, i - j, word.Length) == word));
                if (!kept) chars[i] = to;
            }
        }
        return new string(chars);
    }
}

/// <summary>語言檔（跟 Resources/opencc 一樣內嵌在組件裡）</summary>
public static class LanguageProfiles
{
    sealed record FileRoot(List<LanguageProfile> Languages);

    public static readonly IReadOnlyList<LanguageProfile> All = Load();

    public static readonly IReadOnlyDictionary<string, LanguageProfile> ByCode = All.ToDictionary(p => p.Code);

    static List<LanguageProfile> Load()
    {
        using var stream = typeof(LanguageProfiles).Assembly.GetManifestResourceStream("languages.json")
            ?? throw new InvalidOperationException("languages.json is not embedded");  // l10n-skip
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return JsonSerializer.Deserialize<FileRoot>(stream, options)!.Languages;
    }

    public static LanguageProfile Get(string code) => ByCode.TryGetValue(code, out var p) ? p : ByCode["zh"];
}

/// <summary>
/// 會議語言（M§16.3、§18.1、§41）：決定即時逐字稿、正式轉錄、會議紀錄和匯出用的語言，跟介面語言無關。
/// 以代碼為身分（D139），做法一律問 <see cref="Profile"/>；<c>default</c> 等於中文
/// </summary>
public readonly struct MeetingLanguage : IEquatable<MeetingLanguage>
{
    readonly string? code;

    public MeetingLanguage(string code) { this.code = code; }

    public static readonly MeetingLanguage Zh = new("zh");
    public static readonly MeetingLanguage En = new("en");
    public static readonly MeetingLanguage Ja = new("ja");

    public string Code => code ?? "zh";

    public LanguageProfile Profile => LanguageProfiles.Get(Code);

    public bool Equals(MeetingLanguage other) => Code == other.Code;
    public override bool Equals(object? obj) => obj is MeetingLanguage other && Equals(other);
    public override int GetHashCode() => Code.GetHashCode();
    public static bool operator ==(MeetingLanguage a, MeetingLanguage b) => a.Equals(b);
    public static bool operator !=(MeetingLanguage a, MeetingLanguage b) => !a.Equals(b);
    public override string ToString() => Code;
}

public static class MeetingLanguages
{
    /// <summary>語言檔的順序（選單的順序）</summary>
    public static readonly MeetingLanguage[] All = [.. LanguageProfiles.All.Select(p => new MeetingLanguage(p.Code))];

    public static string Code(this MeetingLanguage l) => l.Code;

    /// <summary>語言檔裡沒有的代碼回 null</summary>
    public static MeetingLanguage? Parse(string? code) => code is not null && LanguageProfiles.ByCode.ContainsKey(code) ? new MeetingLanguage(code) : null;

    /// <summary>用哪一份手寫的提示詞、標籤</summary>
    public static PromptBase Base(this MeetingLanguage l) => l.Profile.BaseKind;

    /// <summary>跟著會議語言（不是介面語言）、給人看的文字，例如匯出的標題：中文、日文那一份照用；英文那一份在一般語言查字串表，查不到用英文</summary>
    public static string Text(this MeetingLanguage l, string zh, string en, string ja) => l.Base() switch
    {
        PromptBase.En => l.Profile.Strings is { } table && table.TryGetValue(en, out var v) ? v : en,
        PromptBase.Ja => ja,
        _ => zh,
    };

    /// <summary>提示詞裡的文字：只看用哪一份，不查字串表（一般語言的提示詞維持英文，D140）</summary>
    public static string Prompt(this MeetingLanguage l, string zh, string en, string ja) => l.Base() switch
    {
        PromptBase.En => en,
        PromptBase.Ja => ja,
        _ => zh,
    };

    /// <summary>有插值的文字：三份都是 Swift 的格式字串（%@、%lld），一般語言查字串表的格式</summary>
    public static string Format(this MeetingLanguage l, string zh, string en, string ja, params object?[] args) => L.Format(l.Text(zh, en, ja), args);

    /// <summary>選單上的名稱：用各自語言的寫法；一般語言接「（實驗性）」（M§41.2）</summary>
    public static string Name(this MeetingLanguage l) =>
        l.Profile.IsGeneral ? l.Profile.Name + Ui("（實驗性）", " (Experimental)") : l.Profile.Name;

    /// <summary>介面上提到這個語言時的名稱（跟著介面語言）</summary>
    public static string UiName(this MeetingLanguage l) => Ui(l.Profile.UiName.Zh, l.Profile.UiName.En);

    /// <summary>whisper.cpp 的 language 參數</summary>
    public static string WhisperCode(this MeetingLanguage l) => l.Profile.Transcription.Whisper ?? l.Code;

    /// <summary>正式轉錄的 whisper 用 large-v3-turbo（日文、粵語、韓文…）還是 Breeze（M§41.3）</summary>
    public static bool UsesTurbo(this MeetingLanguage l) => l.Profile.Transcription.Model == "turbo";

    /// <summary>介面上列出用某個轉錄模型的語言，例如「日文和韓文」（同 Mac 的 ListFormatter）</summary>
    public static string UiNames(string model) => UiNames(All.Where(l => l.Profile.Transcription.Model == model));

    /// <summary>自動偵測認得的語言（語言檔的 detect 不是 null，M§41.4）</summary>
    public static IEnumerable<MeetingLanguage> Detectable => All.Where(l => l.Profile.Detect is not null);

    /// <summary>介面上列出幾種語言：「中文、英文、日文和韓文」「Chinese, English, Japanese, and Korean」</summary>
    public static string UiNames(IEnumerable<MeetingLanguage> languages)
    {
        var names = languages.Select(l => l.UiName()).ToList();
        if (names.Count < 2) return names.FirstOrDefault() ?? "";
        if (Interface == MeetingLanguage.En)
            return names.Count == 2 ? $"{names[0]} and {names[1]}" : string.Join(", ", names[..^1]) + ", and " + names[^1];
        return string.Join("、", names[..^1]) + "和" + names[^1];  // l10n-skip
    }

    /// <summary>同一個中文詞在英文要用不同說法的少數地方，直接依介面語言給兩種寫法</summary>
    public static string Ui(string zh, string en) => Interface == MeetingLanguage.En ? en : zh;

    /// <summary>目前的介面語言，只會是中文或英文</summary>
    public static MeetingLanguage Interface => L.English ? MeetingLanguage.En : MeetingLanguage.Zh;

    /// <summary>預設的說話者名稱：「說話者 1」「Speaker 1」「話者 1」（語言檔的 speaker）</summary>
    public static string DefaultSpeaker(this MeetingLanguage l, int idx) => L.Format(l.Profile.Speaker, idx);
}
