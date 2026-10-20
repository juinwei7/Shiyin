using Shiyin.Localization;

namespace Shiyin.Model;

/// <summary>會議語言（M§16.3、§18.1）：決定即時逐字稿、正式轉錄、會議紀錄和匯出用的語言，跟介面語言無關</summary>
public enum MeetingLanguage { Zh, En, Ja }

public static class MeetingLanguages
{
    public static readonly MeetingLanguage[] All = [MeetingLanguage.Zh, MeetingLanguage.En, MeetingLanguage.Ja];

    public static string Code(this MeetingLanguage l) => l switch
    {
        MeetingLanguage.En => "en",
        MeetingLanguage.Ja => "ja",
        _ => "zh",
    };

    public static MeetingLanguage? Parse(string? code) => code switch
    {
        "zh" => MeetingLanguage.Zh,
        "en" => MeetingLanguage.En,
        "ja" => MeetingLanguage.Ja,
        _ => null,
    };

    /// <summary>跟著會議語言（不是介面語言）的文字，例如匯出的標題、預設的說話者名稱</summary>
    public static string Text(this MeetingLanguage l, string zh, string en, string ja) => l switch
    {
        MeetingLanguage.En => en,
        MeetingLanguage.Ja => ja,
        _ => zh,
    };

    /// <summary>選單上的名稱：用各自語言的寫法</summary>
    public static string Name(this MeetingLanguage l) => l.Text("中文", "English", "日本語");

    /// <summary>介面上提到這個語言時的名稱（跟著介面語言）</summary>
    public static string UiName(this MeetingLanguage l) => l switch
    {
        MeetingLanguage.En => Ui("英文", "English"),
        MeetingLanguage.Ja => Ui("日文", "Japanese"),
        _ => Ui("中文", "Chinese"),
    };

    /// <summary>whisper.cpp 的 language 參數</summary>
    public static string WhisperCode(this MeetingLanguage l) => l.Code();

    /// <summary>同一個中文詞在英文要用不同說法的少數地方，直接依介面語言給兩種寫法</summary>
    public static string Ui(string zh, string en) => Interface == MeetingLanguage.En ? en : zh;

    /// <summary>目前的介面語言，只會是中文或英文</summary>
    public static MeetingLanguage Interface => L.English ? MeetingLanguage.En : MeetingLanguage.Zh;

    /// <summary>預設的說話者名稱：「說話者 1」「Speaker 1」「話者 1」</summary>
    public static string DefaultSpeaker(this MeetingLanguage l, int idx) => l.Text($"說話者 {idx}", $"Speaker {idx}", $"話者 {idx}");
}
