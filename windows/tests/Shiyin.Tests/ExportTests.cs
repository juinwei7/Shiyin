using System.Text.Json;
using Shiyin.IO;
using Shiyin.Model;
using Shiyin.Text;
using Xunit;

namespace Shiyin.Tests;

/// <summary>匯出的 Markdown、txt、srt、vtt、HTML 跟 Mac 逐字相同；封包的 JSON 內容相同（R28.2、R28.3）</summary>
public class ExportTests
{
    public static TheoryData<string> Keys => new(Golden.Meetings.Select(m => m.Key));

    static ExportBundle Bundle(Golden.MeetingFixture fx)
    {
        var meeting = fx.Meeting.Clone();
        meeting.Notes = fx.Notes?.JsonString();
        return new ExportBundle(meeting, fx.Segments, fx.SpeakerTable, fx.Bookmarks, fx.Notes);
    }

    [Theory, MemberData(nameof(Keys))]
    public void NotesAndTranscripts(string key)
    {
        var fx = Golden.Meeting(key);
        var b = Bundle(fx);
        var dir = $"export/{key}/";
        Golden.Text(dir + "notes.md", NotesExporter.Markdown(b));
        Golden.Text(dir + "notes.txt", NotesExporter.Plain(b));
        Golden.Text(dir + "notes.html", NotesExporter.Html(b, ExportFonts.Mac));
        foreach (var (times, speakers) in new[] { (true, true), (true, false), (false, true), (false, false) })
        {
            var suffix = $"{(times ? "t" : "f")}{(speakers ? "t" : "f")}";
            Golden.Text(dir + $"transcript-{suffix}.txt", TranscriptExporter.Text(b, "txt", times, speakers));
            Golden.Text(dir + $"transcript-{suffix}.md", TranscriptExporter.Text(b, "md", times, speakers));
            Golden.Text(dir + $"transcript-{suffix}.html", TranscriptExporter.Html(b, times, speakers, ExportFonts.Mac));
        }
        Golden.Text(dir + "transcript-t.srt", TranscriptExporter.Text(b, "srt", true, true));
        Golden.Text(dir + "transcript-f.srt", TranscriptExporter.Text(b, "srt", true, false));
        Golden.Text(dir + "transcript-t.vtt", TranscriptExporter.Text(b, "vtt", true, true));
        Golden.Text(dir + "transcript-f.vtt", TranscriptExporter.Text(b, "vtt", true, false));
        var empty = b with { Notes = new MeetingNotes { Title = "x" } };
        Golden.Text(dir + "notes-empty.md", NotesExporter.Markdown(empty));
        Golden.Text(dir + "notes-empty.html", NotesExporter.Html(empty, ExportFonts.Mac));
    }

    [Theory, MemberData(nameof(Keys))]
    public void PackageJson(string key)
    {
        var fx = Golden.Meeting(key);
        var b = Bundle(fx);
        var m = b.Meeting;
        var info = new IO.PackageJson.MeetingInfo(m.Title, m.TitleLocked, m.MeetingDate, m.Participants, m.SourceFilename, m.DurationSeconds,
            m.TemplateId, m.UserNotes, m.Channels, m.Folder, m.Language, fx.Notes, "claude",
            b.Speakers.Select(p => (p.Key, p.Value)).ToList(), fx.Bookmarks.Select(x => (x.Time, x.Label)).ToList());
        Golden.Json($"package/{key}/meeting.json", IO.PackageJson.Write(info));
        Golden.Json($"package/{key}/transcript.json", IO.PackageJson.Write(fx.Segments.Select(s =>
            new IO.PackageJson.SegmentInfo(s.Start, s.End, s.Text, s.LowConfidence, s.Speaker))));
        Golden.Json($"package/{key}/manifest.json", IO.PackageJson.Write(new IO.PackageJson.Manifest(1, "3.6.0", "2026-10-20T00:00:00Z", "audio.m4a")));

        // Mac 寫的 JSON 讀得回來，內容相同
        var read = IO.PackageJson.ReadMeeting(Golden.Expected($"package/{key}/meeting.json"));
        Assert.NotNull(read);
        Assert.Equal(m.Title, read.Title);
        Assert.Equal(fx.Notes?.JsonString(), read.Notes?.JsonString());
        Assert.Equal(b.Speakers.Count, read.Speakers.Count);
        var segments = IO.PackageJson.ReadSegments(Golden.Expected($"package/{key}/transcript.json"));
        Assert.Equal(fx.Segments.Select(s => s.Text), segments!.Select(s => s.Text));
        Assert.Equal(fx.Segments.Select(s => s.Speaker), segments!.Select(s => s.Speaker));
        Assert.NotNull(IO.PackageJson.ReadManifest(Golden.Expected($"package/{key}/manifest.json")));
    }
}
