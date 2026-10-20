using Shiyin.Model;
using Shiyin.Notes;

namespace Shiyin.IO;

/// <summary>匯出用的完整資料</summary>
public sealed record ExportBundle(
    Meeting Meeting, IReadOnlyList<Segment> Segments, IReadOnlyDictionary<int, string> Speakers,
    IReadOnlyList<Bookmark> Bookmarks, MeetingNotes? Notes)
{
    public string? SpeakerName(int? idx) => idx is int i ? (Speakers.TryGetValue(i, out var n) ? n : Text.Speaker(i)) : null;

    /// <summary>匯出內容的標題、欄位名稱：跟著會議語言，不是介面語言（M§16.6）</summary>
    public ExportText Text => new(Meeting.MeetingLanguage);
}

/// <summary>匯出檔裡的固定文字（M§16.6）</summary>
public sealed record ExportText(MeetingLanguage Language)
{
    public string TranscriptSuffix => Language.Text("（逐字稿）", " (transcript)", "（文字起こし）");
    public string Date => Language.Text("日期", "Date", "日付");
    public string Participants => Language.Text("與會者", "Participants", "参加者");
    public string SpeakersLabel => Language.Text("說話者", "Speakers", "話者");
    public string Duration => Language.Text("長度", "Duration", "所要時間");
    public string NoNotes => Language.Text("（尚未整理成會議紀錄）", "(No meeting notes yet)", "（議事録はまだありません）");
    public string Details => Language.Text("詳細", "Details", "詳細");
    public string Review => Language.Text("待確認", "To confirm", "要確認");
    public string Owner => Language.Text("負責人", "Owner", "担当");
    public string Task => Language.Text("事項", "Item", "内容");
    public string Due => Language.Text("期限", "Due", "期限");
    public string Time => Language.Text("時間點", "Time", "時刻");
    public string Unassigned => Language.Text("未指定", "Unassigned", "未定");
    public string Colon => Language.Text("：", ": ", "：");
    public string ListSeparator => Language.Text("、", ", ", "、");
    public string Speaker(int idx) => Language.DefaultSpeaker(idx);
    public string Section(string id) => TemplateDefinition.SectionLabel(id, Language);
    /// <summary>括號：中文用全形、英文用半形加空白</summary>
    public string Paren(string inner) => Language.Text($"（{inner}）", $" ({inner})", $"（{inner}）");
    public string DateLine(string meetingDate) => meetingDate + Paren(DateText.Weekday(meetingDate, Language));
}

/// <summary>逐字稿（R7.2）</summary>
public static class TranscriptExporter
{
    public static string Text(ExportBundle b, string format, bool times, bool speakers)
    {
        switch (format)
        {
            case "srt":
                return string.Join("\n", b.Segments.Select((s, i) =>
                    $"{i + 1}\n{TimeFormat.Subtitle(s.Start, ",")} --> {TimeFormat.Subtitle(Math.Max(s.End, s.Start + 0.5), ",")}\n{Line(b, s, false, speakers)}\n"));
            case "vtt":
                return "WEBVTT\n\n" + string.Join("\n", b.Segments.Select(s =>
                    $"{TimeFormat.Subtitle(s.Start, ".")} --> {TimeFormat.Subtitle(Math.Max(s.End, s.Start + 0.5), ".")}\n{Line(b, s, false, speakers)}\n"));
            case "md":
                var x = b.Text;
                return $"# {b.Meeting.DisplayTitle}{x.TranscriptSuffix}\n\n- {x.Date}{x.Colon}{x.DateLine(b.Meeting.MeetingDate)}\n\n"
                       + string.Join("\n\n", b.Segments.Select(s => Line(b, s, times, speakers, markdown: true))) + "\n";
            default:
                return string.Join("\n", b.Segments.Select(s => Line(b, s, times, speakers))) + "\n";
        }
    }

    static string Line(ExportBundle b, Segment s, bool times, bool speakers, bool markdown = false)
    {
        var prefix = "";
        if (times) prefix += markdown ? $"`{TimeFormat.String(s.Start)}` " : $"[{TimeFormat.String(s.Start)}] ";
        if (speakers && b.SpeakerName(s.Speaker) is { } name) prefix += markdown ? $"**{name}**{b.Text.Colon}" : $"{name}{b.Text.Colon}";
        return prefix + s.Text;
    }

    public static string Html(ExportBundle b, bool times, bool speakers, ExportFonts? fonts = null)
    {
        var body = string.Join("\n", b.Segments.Select(s =>
        {
            var prefix = "";
            if (times) prefix += $"<span class=\"time\">{TimeFormat.String(s.Start)}</span> ";
            if (speakers && b.SpeakerName(s.Speaker) is { } name) prefix += $"<b>{HtmlText.Escape(name)}</b>{b.Text.Colon}";
            return $"<p>{prefix}{HtmlText.Escape(s.Text)}</p>";
        }));
        var x = b.Text;
        return HtmlText.Document(b.Meeting.DisplayTitle + x.TranscriptSuffix,
            $"<h1>{HtmlText.Escape(b.Meeting.DisplayTitle + x.TranscriptSuffix)}</h1>\n<p class=\"meta\">{b.Meeting.MeetingDate}</p>\n" + body,
            x.Language, fonts);
    }
}

/// <summary>會議紀錄（R7.3）</summary>
public static class NotesExporter
{
    public static string Markdown(ExportBundle b)
    {
        var m = b.Meeting;
        var x = b.Text;
        var output = new List<string> { $"# {m.DisplayTitle}", "" };
        output.Add($"- {x.Date}{x.Colon}{x.DateLine(m.MeetingDate)}");
        if (m.ParticipantList.Count > 0) output.Add($"- {x.Participants}{x.Colon}{string.Join(x.ListSeparator, m.ParticipantList)}");
        var speakerNames = b.Speakers.OrderBy(p => p.Key).Select(p => p.Value).ToList();
        if (speakerNames.Count > 0) output.Add($"- {x.SpeakersLabel}{x.Colon}{string.Join(x.ListSeparator, speakerNames)}");
        if (m.DurationSeconds is not null) output.Add($"- {x.Duration}{x.Colon}{TimeFormat.Duration(m.DurationSeconds, x.Language)}");
        if (b.Notes is not { IsEmpty: false } notes)
        {
            output.AddRange(["", x.NoNotes]);
            return string.Join("\n", output) + "\n";
        }
        string T(string time) => time.Length == 0 ? "" : x.Paren(time);
        void Section(string title, List<MeetingNotes.Item> items)
        {
            if (items.Count == 0) return;
            output.AddRange(["", $"## {title}"]);
            output.AddRange(items.Select(i => $"- {i.Text}{T(i.Time)}"));
        }
        Section(x.Section("summary"), notes.Summary);
        Section(x.Section("decisions"), notes.Decisions);
        if (notes.ActionItems.Count > 0)
        {
            output.AddRange(["", $"## {x.Section("actionItems")}", $"| {x.Owner} | {x.Task} | {x.Due} | {x.Time} |", "|---|---|---|---|"]);
            foreach (var item in notes.ActionItems)
                output.Add($"| {Cell(item.Owner)} | {Cell(item.Task)} | {Cell(item.Due)} | {Cell(item.Time)} |");
        }
        Section(x.Section("openQuestions"), notes.OpenQuestions);
        foreach (var custom in notes.Custom) Section(custom.Title, custom.Items);
        if (notes.Topics.Count > 0)
        {
            output.AddRange(["", $"## {x.Details}"]);
            foreach (var topic in notes.Topics)
            {
                output.AddRange(["", $"### [{topic.Start}] {topic.Title}"]);
                output.AddRange(topic.Points.Select(p => $"- {p.Text}{T(p.Time)}"));
            }
        }
        if (notes.ReviewItems.Count > 0)
        {
            output.AddRange(["", $"## {x.Review}"]);
            output.AddRange(notes.ReviewItems.Select(r => $"- [{r.Time}] {r.Question}"));
        }
        return string.Join("\n", output) + "\n";
    }

    /// <summary>純文字（貼到 Slack、訊息）</summary>
    public static string Plain(ExportBundle b) =>
        Markdown(b).Replace("## ", "", StringComparison.Ordinal).Replace("### ", "", StringComparison.Ordinal).Replace("# ", "", StringComparison.Ordinal);

    public static string Html(ExportBundle b, ExportFonts? fonts = null)
    {
        var m = b.Meeting;
        var x = b.Text;
        var body = $"<h1>{HtmlText.Escape(m.DisplayTitle)}</h1>\n<p class=\"meta\">{x.DateLine(m.MeetingDate)}";
        if (m.ParticipantList.Count > 0)
            body += x.Language.Text("　", " · ", "　") + $"{x.Participants}{x.Colon}{HtmlText.Escape(string.Join(x.ListSeparator, m.ParticipantList))}";
        body += "</p>\n";
        if (b.Notes is not { IsEmpty: false } notes)
            return HtmlText.Document(m.DisplayTitle, body + $"<p>{x.NoNotes}</p>", x.Language, fonts);
        string T(string time) => time.Length == 0 ? "" : $" <span class=\"time\">{time}</span>";
        void List(string title, List<MeetingNotes.Item> items)
        {
            if (items.Count == 0) return;
            body += $"<h2>{HtmlText.Escape(title)}</h2>\n<ul>" + string.Concat(items.Select(i => $"<li>{HtmlText.Escape(i.Text)}{T(i.Time)}</li>")) + "</ul>\n";
        }
        List(x.Section("summary"), notes.Summary);
        List(x.Section("decisions"), notes.Decisions);
        if (notes.ActionItems.Count > 0)
        {
            body += $"<h2>{x.Section("actionItems")}</h2>\n<table><tr><th class=\"nowrap\">{x.Owner}</th><th>{x.Task}</th><th class=\"nowrap\">{x.Due}</th></tr>";
            body += string.Concat(notes.ActionItems.Select(a =>
                $"<tr><td class=\"nowrap\">{HtmlText.Escape(a.Owner.Length == 0 ? x.Unassigned : a.Owner)}</td><td>{HtmlText.Escape(a.Task)}{T(a.Time)}</td><td class=\"nowrap\">{HtmlText.Escape(a.Due.Length == 0 ? "—" : a.Due)}</td></tr>"));
            body += "</table>\n";
        }
        List(x.Section("openQuestions"), notes.OpenQuestions);
        foreach (var custom in notes.Custom) List(custom.Title, custom.Items);
        if (notes.Topics.Count > 0)
        {
            body += $"<h2>{x.Details}</h2>\n";
            foreach (var topic in notes.Topics)
                body += $"<h3><span class=\"time\">{topic.Start}</span> {HtmlText.Escape(topic.Title)}</h3>\n<ul>"
                        + string.Concat(topic.Points.Select(p => $"<li>{HtmlText.Escape(p.Text)}{T(p.Time)}</li>")) + "</ul>\n";
        }
        if (notes.ReviewItems.Count > 0)
            body += $"<h2>{x.Review}</h2>\n<ul>" + string.Concat(notes.ReviewItems.Select(r => $"<li><span class=\"time\">{r.Time}</span> {HtmlText.Escape(r.Question)}</li>")) + "</ul>\n";
        return HtmlText.Document(m.DisplayTitle, body, x.Language, fonts);
    }

    static string Cell(string text) => text.Length == 0 ? "—" : text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}

/// <summary>匯出 HTML 用的字型（M§16.6、§18.5；Windows 的字型見 design-windows §11.3）</summary>
public sealed record ExportFonts(string BodyZh, string BodyEn, string BodyJa, string HeadingZh, string HeadingEn, string HeadingJa)
{
    /// <summary>Mac 的字型（標準答案用這一組比對）</summary>
    public static readonly ExportFonts Mac = new(
        "\"PingFang TC\", -apple-system, sans-serif", "-apple-system, \"PingFang TC\", sans-serif",
        "\"Hiragino Sans\", \"Hiragino Kaku Gothic ProN\", -apple-system, sans-serif",
        "\"Songti TC\", serif", "\"New York\", Georgia, \"Songti TC\", serif", "\"Hiragino Mincho ProN\", serif");

    /// <summary>Windows 的字型</summary>
    public static readonly ExportFonts Windows = new(
        "\"Microsoft JhengHei\", \"Segoe UI\", sans-serif", "\"Segoe UI\", \"Microsoft JhengHei\", sans-serif",
        "\"Yu Gothic\", \"Meiryo\", \"Segoe UI\", sans-serif",
        "PMingLiU, \"Microsoft JhengHei\", serif", "Georgia, PMingLiU, serif", "\"Yu Mincho\", serif");
}

public static class HtmlText
{
    public static string Escape(string s) => s.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal);

    public static string Document(string title, string body, MeetingLanguage language = MeetingLanguage.Zh, ExportFonts? fonts = null)
    {
        var f = fonts ?? ExportFonts.Windows;
        var bodyFont = language.Text(f.BodyZh, f.BodyEn, f.BodyJa);
        var headingFont = language.Text(f.HeadingZh, f.HeadingEn, f.HeadingJa);
        return $$"""
            <!doctype html><html lang="{{language.Text("zh-Hant", "en", "ja")}}"><head><meta charset="utf-8"><title>{{Escape(title)}}</title><style>
            body { font-family: {{bodyFont}}; font-size: 12pt; line-height: 1.6; color: #1E2625; }
            h1, h2, h3 { font-family: {{headingFont}}; font-weight: 600; }
            h1 { font-size: 22pt; margin-bottom: 4pt; } h2 { font-size: 15pt; margin-top: 18pt; } h3 { font-size: 13pt; }
            .meta { color: #68726F; font-size: 10pt; }
            .time { color: #2D6A5D; font-size: 9.5pt; }
            table { border-collapse: collapse; width: 100%; } th, td { border-bottom: 1px solid #DCE1DE; padding: 4pt 6pt; text-align: left; vertical-align: top; }
            th { color: #68726F; font-weight: normal; font-size: 10pt; }
            .nowrap { white-space: nowrap; }  /* 負責人、期限不拆行，內容欄自己換行（中日文可以在任何字之間換行，表格會把短欄擠到一個字寬） */
            </style></head><body>{{body}}</body></html>
            """;
    }
}
