using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Shiyin.Model;
using Shiyin.Text;

namespace Shiyin.IO;

/// <summary>匯入的共用規則（M§7、R3）</summary>
public static class ImportRules
{
    /// <summary>會議日期預設值：檔名有日期就用檔名，年份取不晚於今天的最近一年；否則用檔案修改日期</summary>
    public static string InferDate(string filename, DateOnly? modified, DateOnly today)
    {
        var name = PathText.DeletingExtension(filename);

        string? Valid(int y, int m, int d)
        {
            if (m is < 1 or > 12 || d is < 1 or > 31 || y < 1 || y > 9999) return null;
            if (d > DateTime.DaysInMonth(y, m)) return null;
            return DateText.String(new DateOnly(y, m, d));
        }
        string? WithNearestYear(int m, int d)
        {
            var isFuture = m > today.Month || (m == today.Month && d > today.Day);
            return Valid(isFuture ? today.Year - 1 : today.Year, m, d);
        }
        // Swift 的 Int(String) 只認 ASCII 數字：全形數字比對得到，但轉不成數字
        List<int>? Match(string pattern)
        {
            var match = Regex.Match(name, pattern);
            if (!match.Success) return null;
            var parts = new List<int>();
            for (var i = 1; i < match.Groups.Count; i++)
            {
                var g = match.Groups[i];
                if (g.Success && g.Value.All(c => c is >= '0' and <= '9')
                    && int.TryParse(g.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) parts.Add(n);
            }
            return parts;
        }

        // 2026-08-27、2026_08_27、20260827
        if (Match(@"(?<!\d)(20\d{2})[-_./]?(\d{1,2})[-_./]?(\d{1,2})(?!\d)") is { Count: 3 } p1 && Valid(p1[0], p1[1], p1[2]) is { } d1) return d1;
        // 8_27、08-27、8.27
        if (Match(@"(?<!\d)(\d{1,2})[-_./](\d{1,2})(?!\d)") is { Count: 2 } p2 && WithNearestYear(p2[0], p2[1]) is { } d2) return d2;
        // 0827
        if (Match(@"(?<!\d)(\d{2})(\d{2})(?!\d)") is { Count: 2 } p3 && WithNearestYear(p3[0], p3[1]) is { } d3) return d3;
        return DateText.String(modified ?? today);
    }

    /// <summary>只保留英數字的副檔名，避免路徑穿越（N1）</summary>
    public static string SafeExtension(string filename)
    {
        var ext = string.Concat(SwiftText.Characters(SwiftText.Lower(PathText.Extension(filename)))
            .Where(c => SwiftText.IsAscii(c) && (SwiftText.IsLetter(c) || SwiftText.IsNumber(c))));
        return ext.Length == 0 ? "bin" : SwiftText.Prefix(ext, 8);
    }
}

/// <summary>匯入逐字稿（srt、vtt、txt，R3.4）</summary>
public static class TranscriptImporter
{
    public static List<(double Start, double End, string Text)> Parse(string text, string ext)
    {
        if (ext == "txt")
        {
            return SplitNewlines(text).Select(SwiftText.TrimSpaces).Where(l => l.Length > 0).Select(l => (0.0, 0.0, l)).ToList();
        }
        // srt／vtt：時間碼行之後到空行之前是文字
        var segments = new List<(double, double, string)>();
        foreach (var block in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n"))
        {
            var lines = block.Split('\n');
            var index = Array.FindIndex(lines, l => l.Contains("-->", StringComparison.Ordinal));
            if (index < 0) continue;
            var times = lines[index].Split("-->").Select(SwiftText.TrimSpaces).ToArray();
            if (times.Length != 2 || Seconds(times[0]) is not { } start || Seconds(times[1].Split(' ')[0]) is not { } end) continue;
            var content = SwiftText.TrimSpaces(string.Join(" ", lines.Skip(index + 1)));
            if (content.Length > 0) segments.Add((start, end, content));
        }
        return segments;
    }

    /// <summary>Swift 的 <c>components(separatedBy: .newlines)</c>：\n、\r、\r\n 分別都算一個換行</summary>
    static IEnumerable<string> SplitNewlines(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '\n' or '\r' or '\u000B' or '\u000C' or '\u0085' or '\u2028' or '\u2029')
            {
                yield return text[start..i];
                start = i + 1;
            }
        }
        yield return text[start..];
    }

    /// <summary>00:01:02,345 或 00:01:02.345 或 01:02.345</summary>
    public static double? Seconds(string text)
    {
        var parts = text.Replace(',', '.').Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        double total = 0;
        foreach (var part in parts)
        {
            if (!SwiftDouble(part, out var v)) return null;
            total = total * 60 + v;
        }
        return total;
    }

    /// <summary>Swift 的 <c>Double(String)</c>：不接受前後空白</summary>
    static bool SwiftDouble(string s, out double value)
    {
        value = 0;
        if (s.Length == 0 || char.IsWhiteSpace(s[0]) || char.IsWhiteSpace(s[^1])) return false;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}

/// <summary>.shiyin 封包的 JSON（M§10、R7.4、R28.3）：zip 根目錄放 manifest.json、meeting.json、transcript.json、audio.*</summary>
public static class PackageJson
{
    public const int FormatVersion = 1;

    public sealed record Manifest(int Format, string App, string ExportedAt, string? Audio);

    public sealed record MeetingInfo(
        string Title, bool TitleLocked, string MeetingDate, string Participants, string SourceFilename, int? DurationSeconds,
        string TemplateId, string UserNotes, string Channels, string? Folder, string? Language, MeetingNotes? Notes, string? NotesBy,
        List<(int Idx, string Name)> Speakers, List<(double Time, string Label)> Bookmarks);

    public sealed record SegmentInfo(double Start, double End, string Text, bool LowConfidence, int? Speaker);

    public static string Write(Manifest m)
    {
        var o = new JNode.Obj().Add("format", new JNode.Int(m.Format)).Add("app", m.App).Add("exportedAt", m.ExportedAt);
        if (m.Audio is not null) o.Add("audio", m.Audio);
        return Pretty(o);
    }

    public static string Write(MeetingInfo i)
    {
        var o = new JNode.Obj()
            .Add("title", i.Title).Add("titleLocked", JNode.Of(i.TitleLocked)).Add("meetingDate", i.MeetingDate)
            .Add("participants", i.Participants).Add("sourceFilename", i.SourceFilename);
        if (i.DurationSeconds is int d) o.Add("durationSeconds", new JNode.Int(d));
        o.Add("templateId", i.TemplateId).Add("userNotes", i.UserNotes).Add("channels", i.Channels);
        if (i.Folder is not null) o.Add("folder", i.Folder);
        if (i.Language is not null) o.Add("language", i.Language);
        if (i.Notes is not null) o.Add("notes", i.Notes.ToJson());
        if (i.NotesBy is not null) o.Add("notesBy", i.NotesBy);
        o.Add("speakers", new JNode.Arr(i.Speakers.OrderBy(s => s.Idx).Select(s =>
            (JNode)new JNode.Obj().Add("idx", new JNode.Int(s.Idx)).Add("name", s.Name))));
        o.Add("bookmarks", new JNode.Arr(i.Bookmarks.Select(b =>
            (JNode)new JNode.Obj().Add("time", JNode.Of(b.Time)).Add("label", b.Label))));
        return Pretty(o);
    }

    public static string Write(IEnumerable<SegmentInfo> segments) => Pretty(new JNode.Arr(segments.Select(s =>
    {
        var o = new JNode.Obj().Add("start", JNode.Of(s.Start)).Add("end", JNode.Of(s.End)).Add("text", s.Text)
            .Add("lowConfidence", JNode.Of(s.LowConfidence));
        if (s.Speaker is int sp) o.Add("speaker", new JNode.Int(sp));
        return (JNode)o;
    })));

    /// <summary>Swift 的 prettyPrinted＋sortedKeys 的樣子（Mac 讀的時候不在意格式，只是看起來一樣）</summary>
    static string Pretty(JNode node)
    {
        var compact = SwiftJsonWriter.Write(node, sortKeys: true, escapeSlashes: false);
        using var doc = JsonDocument.Parse(compact);
        var options = new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, options)) doc.WriteTo(writer);
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    public static Manifest? ReadManifest(string json)
    {
        if (JsonRead.Parse(json) is not { ValueKind: JsonValueKind.Object } r) return null;
        if (r.Long("format") is not { } format || r.Str("app") is not { } app || r.Str("exportedAt") is not { } at) return null;
        return new Manifest((int)format, app, at, r.Str("audio"));
    }

    /// <summary>跟 Mac 的 Codable 一樣：必填的欄位缺了就整個不收</summary>
    public static MeetingInfo? ReadMeeting(string json)
    {
        if (JsonRead.Parse(json) is not { ValueKind: JsonValueKind.Object } r) return null;
        if (r.Str("title") is not { } title || r.Bool("titleLocked") is not { } locked || r.Str("meetingDate") is not { } date
            || r.Str("participants") is not { } participants || r.Str("sourceFilename") is not { } source
            || r.Str("templateId") is not { } template || r.Str("userNotes") is not { } userNotes || r.Str("channels") is not { } channels
            || r.Get("speakers") is not { ValueKind: JsonValueKind.Array } speakerList || r.Get("bookmarks") is not { ValueKind: JsonValueKind.Array } bookmarkList)
            return null;
        var speakers = new List<(int, string)>();
        foreach (var s in speakerList.EnumerateArray())
        {
            if (s.Long("idx") is not { } idx || s.Str("name") is not { } name) return null;
            speakers.Add(((int)idx, name));
        }
        var bookmarks = new List<(double, string)>();
        foreach (var b in bookmarkList.EnumerateArray())
        {
            if (b.Double("time") is not { } time || b.Str("label") is not { } label) return null;
            bookmarks.Add((time, label));
        }
        MeetingNotes? notes = r.Get("notes") is { ValueKind: JsonValueKind.Object } n ? MeetingNotes.Decode(n) : null;
        return new MeetingInfo(title, locked, date, participants, source, (int?)r.Long("durationSeconds"), template, userNotes, channels,
            r.Str("folder"), r.Str("language"), notes, r.Str("notesBy"), speakers, bookmarks);
    }

    public static List<SegmentInfo>? ReadSegments(string json)
    {
        if (JsonRead.Parse(json) is not { ValueKind: JsonValueKind.Array } r) return null;
        var list = new List<SegmentInfo>();
        foreach (var s in r.EnumerateArray())
        {
            if (s.Double("start") is not { } start || s.Double("end") is not { } end || s.Str("text") is not { } text
                || s.Bool("lowConfidence") is not { } low) return null;
            list.Add(new SegmentInfo(start, end, text, low, (int?)s.Long("speaker")));
        }
        return list;
    }
}
