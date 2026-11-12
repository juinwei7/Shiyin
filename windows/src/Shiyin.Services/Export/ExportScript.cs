using Shiyin.IO;

namespace Shiyin.Services.Export;

/// <summary>中文會議用簡體時，匯出整份轉成簡體（R71.3、M§47.2）：標題、欄位名稱、預設的說話者名稱也一起轉</summary>
public static class ExportScript
{
    public static string Text(AppSettings settings, ExportBundle b, string text) => settings.Script(text, b.Meeting.MeetingLanguage);

    /// <summary>HTML 另外把語言標記、繁體中文的字型換成簡體的</summary>
    public static string Html(AppSettings settings, ExportBundle b, string html)
    {
        if (!settings.SimplifiedChinese(b.Meeting.MeetingLanguage)) return html;
        var result = Shiyin.Text.ChineseScript.ToSimplified(html);
        foreach (var (from, to) in new[] { ("lang=\"zh-Hant\"", "lang=\"zh-Hans\""), ("Microsoft JhengHei", "Microsoft YaHei"), ("PMingLiU", "SimSun"),
                                           ("PingFang TC", "PingFang SC"), ("Songti TC", "Songti SC") })   // l10n-skip
            result = result.Replace(from, to, StringComparison.Ordinal);
        return result;
    }
}
