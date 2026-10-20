using System.Text.Json;
using Shiyin.IO;
using Shiyin.Model;
using Shiyin.Notes;
using Shiyin.Text;
using Xunit;

namespace Shiyin.Tests;

/// <summary>小工具：會議紀錄的寬鬆解碼、時間格式、日期、檔名、匯入、回傳結果的檢查</summary>
public class UtilTests
{
    static IEnumerable<JsonElement> Items(string path) => Golden.ExpectedJson(path).EnumerateArray();

    [Fact]
    public void NotesDecode()
    {
        foreach (var item in Items("util/notes-decode.json"))
        {
            var input = item.Str("input")!;
            var notes = input is "zh" or "en" or "ja" ? Golden.Meeting(input).Notes : MeetingNotes.Decode(input);
            if (item.Get("jsonString") is { ValueKind: JsonValueKind.Null })
            {
                Assert.Null(notes);
                continue;
            }
            Assert.NotNull(notes);
            Assert.Equal(item.Str("jsonString"), notes.JsonString());
            Assert.Equal(item.Str("plainText"), notes.PlainText);
            Assert.Equal(item.Bool("isEmpty"), notes.IsEmpty);
        }
        foreach (var item in Items("util/renames.json"))
        {
            var old = item.Str("old")!;
            var replacement = item.Str("new")!;
            var key = Golden.Fixtures.GetProperty("speakerRenames").EnumerateArray().First(r => r.Str("old") == old).Str("notesKey")!;
            var notes = Golden.Meeting(key).Notes!;
            Assert.Equal(item.Str("speakerName"), notes.ReplacingSpeakerName(old, replacement, SpeakerNames.IsDefault(old)).JsonString());
            Assert.Equal(item.Str("replacing"), notes.Replacing(old, replacement).JsonString());
        }
    }

    [Fact]
    public void TimeAndDate()
    {
        foreach (var item in Items("util/time-string.json"))
            Assert.Equal(item.Str("text"), TimeFormat.String(Golden.Number(item.Get("seconds"))));
        foreach (var item in Items("util/time-parse.json"))
        {
            Assert.Equal(item.Str("clean"), TimeFormat.Clean(item.Str("text")!));
            var seconds = item.Get("seconds") is { ValueKind: JsonValueKind.Number } n ? n.GetDouble() : (double?)null;
            Assert.Equal(seconds, TimeFormat.Seconds(item.Str("text")!));
        }
        foreach (var item in Items("util/durations.json"))
        {
            var s = item.Get("seconds") is { ValueKind: JsonValueKind.Number } n ? n.GetInt32() : (int?)null;
            Assert.Equal(item.Str("zh"), TimeFormat.Duration(s, MeetingLanguage.Zh));
            Assert.Equal(item.Str("en"), TimeFormat.Duration(s, MeetingLanguage.En));
            Assert.Equal(item.Str("ja"), TimeFormat.Duration(s, MeetingLanguage.Ja));
        }
        foreach (var item in Items("util/subtitle.json"))
        {
            Assert.Equal(item.Str("srt"), TimeFormat.Subtitle(Golden.Number(item.Get("seconds")), ","));
            Assert.Equal(item.Str("vtt"), TimeFormat.Subtitle(Golden.Number(item.Get("seconds")), "."));
        }
        foreach (var item in Items("util/weekday.json"))
        {
            var date = item.Str("date")!;
            Assert.Equal(item.Str("zh"), DateText.Weekday(date, MeetingLanguage.Zh));
            Assert.Equal(item.Str("en"), DateText.Weekday(date, MeetingLanguage.En));
            Assert.Equal(item.Str("ja"), DateText.Weekday(date, MeetingLanguage.Ja));
            Assert.Equal(item.Strings("dateLine"), MeetingLanguages.All.Select(l => new ExportText(l).DateLine(date)).ToList());
        }
    }

    [Fact]
    public void ImportRulesMatchMac()
    {
        var today = DateText.Date(Golden.Fixtures.Str("today")!)!.Value;
        foreach (var item in Items("util/infer-date.json"))
        {
            var modified = item.Str("modified") is { } m ? DateText.Date(m) : null;
            Assert.Equal(item.Str("date"), ImportRules.InferDate(item.Str("filename")!, modified, today));
        }
        foreach (var item in Items("util/safe-extension.json"))
            Assert.Equal(item.Str("ext"), ImportRules.SafeExtension(item.Str("name")!));
        foreach (var item in Items("util/display-title.json"))
            Assert.Equal(item.Str("displayTitle"), new Meeting { Title = item.Str("title")!, SourceFilename = item.Str("sourceFilename")! }.DisplayTitle);
        foreach (var item in Items("util/participants.json"))
            Assert.Equal(item.Strings("list"), new Meeting { Participants = item.Str("text")! }.ParticipantList);
        foreach (var item in Items("util/default-speaker-names.json"))
            Assert.Equal(item.Bool("isDefault"), SpeakerNames.IsDefault(item.Str("name")!));
        foreach (var item in Items("util/transcript-import.json"))
        {
            var input = Golden.Fixtures.GetProperty("transcriptImports").EnumerateArray().First(x => x.Str("ext") == item.Str("ext")).Str("text")!;
            var parsed = TranscriptImporter.Parse(input, item.Str("ext")!);
            var expected = item.GetProperty("segments").EnumerateArray().ToList();
            Assert.Equal(expected.Count, parsed.Count);
            for (var i = 0; i < expected.Count; i++)
            {
                Assert.Equal(Golden.Number(expected[i].Get("start")), parsed[i].Start, 9);
                Assert.Equal(Golden.Number(expected[i].Get("end")), parsed[i].End, 9);
                Assert.Equal(expected[i].Str("text"), parsed[i].Text);
            }
        }
    }

    [Fact]
    public void Voiceprints()
    {
        foreach (var item in Items("util/similarity.json"))
        {
            var a = item.GetProperty("a").EnumerateArray().Select(x => (float)x.GetDouble()).ToArray();
            var b = item.GetProperty("b").EnumerateArray().Select(x => (float)x.GetDouble()).ToArray();
            Assert.Equal(Golden.Number(item.Get("similarity")), Voiceprint.Similarity(a, b), 12);
        }
        var data = Items("util/voiceprint-data.json").First();
        var vector = data.GetProperty("vector").EnumerateArray().Select(x => (float)x.GetDouble()).ToArray();
        Assert.Equal(data.Str("base64"), Convert.ToBase64String(Voiceprint.Data(vector)));
        Assert.Equal(vector, Voiceprint.Vector(Convert.FromBase64String(data.Str("base64")!)));
    }

    [Fact]
    public void ValidateModelOutput()
    {
        var expected = Golden.ExpectedJson("util/validate-answer.json");
        var source = Golden.Fixtures.GetProperty("validateAnswers");
        var raw = AskPrompt.Answer.Decode(source.GetProperty("answer").GetRawText())!;
        var ids = source.GetProperty("meetingIds").EnumerateArray().Select(x => x.GetInt64()).ToHashSet();
        Assert.Equal(expected.Str("single"), AskPrompt.Validate(raw, null).JsonString());
        Assert.Equal(expected.Str("all"), AskPrompt.Validate(raw, ids).JsonString());

        var zh = Golden.Meeting("zh");
        var names = zh.SpeakerTable;
        var unnamed = names.Keys.Order().Where(i => SpeakerNames.IsDefault(names[i])).ToList();
        var naming = new SpeakerNaming.Context(MeetingLanguage.Zh, "", "", [], [], [], unnamed, i => names.GetValueOrDefault(i, ""), []);
        var suggestions = Golden.Fixtures.GetProperty("validateNames").EnumerateArray()
            .Select(s => new SpeakerNaming.Suggestion((int)s.Long("speaker")!, s.Str("name")!, s.Str("reason")!, s.Str("time")!));
        var valid = SpeakerNaming.Validate(suggestions, naming);
        var expectedNames = Items("util/validate-names.json").ToList();
        Assert.Equal(expectedNames.Count, valid.Count);
        for (var i = 0; i < valid.Count; i++)
        {
            Assert.Equal(expectedNames[i].Long("speaker"), valid[i].Speaker);
            Assert.Equal(expectedNames[i].Str("name"), valid[i].Name);
            Assert.Equal(expectedNames[i].Str("reason"), valid[i].Reason);
            Assert.Equal(expectedNames[i].Str("time"), valid[i].Time);
        }

        var context = new TranscriptCorrection.Context(MeetingLanguage.Zh, "", [], [], [], zh.Segments.Select(s => (s.Text, (string?)null)).ToList());
        var corrections = Golden.Fixtures.GetProperty("validateCorrections").EnumerateArray()
            .Select(c => new CorrectionSuggestion((int)c.Long("segment")!, c.Str("original")!, c.Str("suggestion")!, c.Str("reason")!));
        var validCorrections = TranscriptCorrection.Validate(corrections, context, 0, 4);
        var expectedCorrections = Items("util/validate-corrections.json").ToList();
        Assert.Equal(expectedCorrections.Count, validCorrections.Count);
        for (var i = 0; i < validCorrections.Count; i++)
        {
            var e = expectedCorrections[i];
            var s = validCorrections[i];
            Assert.Equal(e.Long("segment"), s.Segment);
            Assert.Equal(e.Str("original"), s.Original);
            Assert.Equal(e.Str("suggestion"), s.Suggestion);
            var applied = TranscriptCorrection.Apply(s.Original, s.Suggestion, zh.Segments[s.Segment].Text);
            Assert.Equal(e.Str("applied"), applied);
            Assert.Equal(e.Str("reverted"), applied is null ? null : TranscriptCorrection.Revert(s.Original, s.Suggestion, applied));
        }
    }
}
