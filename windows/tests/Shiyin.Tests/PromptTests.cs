using System.Text.Json;
using Shiyin.Localization;
using Shiyin.Model;
using Shiyin.Notes;
using Shiyin.Text;
using Shiyin.Transcribe;
using Xunit;

namespace Shiyin.Tests;

/// <summary>提示詞、schema、範本、whisper 的提示詞跟 Mac 逐字相同（R28.1）</summary>
public class PromptTests
{
    public static TheoryData<string> Keys => new(Golden.Meetings.Select(m => m.Key));

    static NotesContext Context(Golden.MeetingFixture fx, TemplateDefinition template) =>
        new(fx.Meeting, fx.Segments, fx.Speakers, fx.Bookmarks, fx.Glossary, template);

    [Theory, MemberData(nameof(Keys))]
    public void NotesPrompts(string key)
    {
        var fx = Golden.Meeting(key);
        var dir = $"prompts/{key}/";
        Golden.Text(dir + "notes-system.txt", NotesPrompt.System(fx.Language));
        foreach (var builtin in BuiltinTemplates.All)
        {
            var record = new TemplateRecord { Id = builtin.Id, Name = builtin.Name, Builtin = true, Definition = builtin.Definition.JsonString() };
            Golden.Text(dir + $"summarize-{builtin.Id}.txt", NotesPrompt.Summarize(Context(fx, record.DefinitionFor(fx.Language))));
        }
        var custom = new TemplateRecord { Id = "custom-1", Name = "客戶會議", Builtin = false, Definition = Golden.Fixtures.GetProperty("customTemplate").GetRawText() };
        Golden.Text(dir + "summarize-custom.txt", NotesPrompt.Summarize(Context(fx, custom.DefinitionFor(fx.Language))));
        if (fx.Notes is { } notes)
        {
            var general = new TemplateRecord { Id = "general", Builtin = true, Definition = BuiltinTemplates.All[0].Definition.JsonString() };
            Golden.Text(dir + "revise.txt", NotesPrompt.Revise(Context(fx, general.DefinitionFor(fx.Language)), notes, fx.Answers));
        }
    }

    [Theory, MemberData(nameof(Keys))]
    public void CorrectionAndNamingPrompts(string key)
    {
        var fx = Golden.Meeting(key);
        var l = fx.Language;
        var dir = $"prompts/{key}/";
        var names = fx.SpeakerTable;
        string? SpeakerName(int? idx) => idx is int i ? (names.TryGetValue(i, out var n) ? n : l.DefaultSpeaker(i)) : null;
        var correction = new TranscriptCorrection.Context(l, fx.Meeting.Title, fx.Meeting.ParticipantList,
            names.Values.Distinct().OrderBy(s => s, SwiftText.Comparer).ToList(), fx.Glossary,
            fx.Segments.Select(s => (s.Text, SpeakerName(s.Speaker))).ToList());
        Golden.Text(dir + "correction-system.txt", TranscriptCorrection.System(l));
        Golden.Text(dir + "correction-prompt.txt", TranscriptCorrection.Prompt(correction, 0, fx.Segments.Count));
        var range = Golden.Fixtures.GetProperty("correctionRange").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        if (range[1] <= fx.Segments.Count)
            Golden.Text(dir + "correction-prompt-range.txt", TranscriptCorrection.Prompt(correction, range[0], range[1]));

        var unnamed = names.Keys.Order().Where(i => SpeakerNames.IsDefault(names[i])).ToList();
        var naming = new SpeakerNaming.Context(l, fx.Meeting.MeetingDate, fx.Meeting.Title, fx.Meeting.ParticipantList, fx.KnownPeople,
            fx.Glossary, unnamed, i => names.TryGetValue(i, out var n) ? n : l.DefaultSpeaker(i),
            fx.Segments.Select(s => (s.Start, s.Speaker, s.Text)).ToList());
        Golden.Text(dir + "naming-system.txt", SpeakerNaming.System(l));
        Golden.Text(dir + "naming-prompt.txt", SpeakerNaming.Prompt(naming));
    }

    [Theory, MemberData(nameof(Keys))]
    public void AskPrompts(string key)
    {
        var fx = Golden.Meeting(key);
        var l = fx.Language;
        var dir = $"prompts/{key}/";
        var names = fx.SpeakerTable;
        var lines = fx.Segments.Select(s =>
        {
            var speaker = s.Speaker is int i ? (names.TryGetValue(i, out var n) ? n : l.DefaultSpeaker(i)) + l.Text("：", ": ", "：") : "";
            return $"[{TimeFormat.String(s.Start)}] {speaker}{s.Text}";
        }).ToList();
        var meeting = fx.Meeting.Clone();
        meeting.Notes = fx.Notes?.JsonString();
        Golden.Text(dir + "ask-meeting-system.txt", AskPrompt.MeetingSystem(l));
        Golden.Text(dir + "ask-meeting-prompt.txt", AskPrompt.MeetingPrompt(meeting, meeting.DecodedNotes, lines, fx.History.TakeLast(3).ToList(), fx.Question));
        Golden.Text(dir + "ask-all-system.txt", AskPrompt.AllSystem(l));
        var blocks = Golden.Fixtures.GetProperty("askAllBlocks").EnumerateArray().Select(b => new AskPrompt.MeetingBlock(
            b.Long("id") ?? 0, b.Str("title") ?? "", b.Str("date") ?? "", b.Strings("summary") ?? [], b.Strings("lines") ?? [])).ToList();
        Golden.Text(dir + "ask-all-prompt.txt", AskPrompt.AllPrompt(l, blocks, Golden.Fixtures.Str("askAllQuestion") ?? ""));
        Golden.Text(dir + "keywords-system.txt", AskPrompt.KeywordsSystem(l));
        Golden.Text(dir + "keywords-prompt.txt", AskPrompt.KeywordsPrompt(l, fx.Question));
    }

    [Fact]
    public void Schemas()
    {
        Golden.Text("schemas/notes.json", MeetingNotes.JsonSchema);
        Golden.Text("schemas/ask-meeting.json", AskPrompt.MeetingSchema);
        Golden.Text("schemas/ask-all.json", AskPrompt.AllSchema);
        Golden.Text("schemas/keywords.json", AskPrompt.KeywordsSchema);
        Golden.Text("schemas/naming.json", SpeakerNaming.JsonSchema);
        Golden.Text("schemas/correction.json", TranscriptCorrection.JsonSchema);
        var expected = Golden.ExpectedJson("schemas/formats.json");
        var formats = new Dictionary<string, OutputFormat>
        {
            ["notes"] = OutputFormat.MeetingNotes, ["askMeeting"] = AskPrompt.MeetingFormat, ["askAll"] = AskPrompt.AllFormat,
            ["keywords"] = AskPrompt.KeywordsFormat, ["naming"] = SpeakerNaming.Format, ["correction"] = TranscriptCorrection.Format,
        };
        foreach (var (name, f) in formats)
        {
            var e = expected.GetProperty(name);
            Assert.Equal(e.Str("toolName"), f.ToolName);
            // 會議紀錄的工具說明是介面文字：標準答案是中文介面產生的
            Assert.Equal(e.Str("toolDescription"), f.ToolDescription);
            Assert.Equal(e.Str("schema"), f.Schema);
        }
    }

    [Fact]
    public void Templates()
    {
        var expected = Golden.ExpectedJson("templates/builtin.json").EnumerateArray().ToList();
        Assert.Equal(expected.Count, BuiltinTemplates.All.Length);
        for (var i = 0; i < expected.Count; i++)
        {
            var e = expected[i];
            var t = BuiltinTemplates.All[i];
            Assert.Equal(e.Str("id"), t.Id);
            Assert.Equal(e.Str("name"), t.Name);
            Golden.SameEmbeddedJson(e.Str("jsonString")!, t.Definition.JsonString(), t.Id);
            Assert.Equal(e.Str("englishName"), BuiltinTemplates.English[t.Id].Name);
            Golden.SameEmbeddedJson(e.Str("englishJSON")!, BuiltinTemplates.English[t.Id].Definition.JsonString(), t.Id + " en");
            Golden.SameEmbeddedJson(e.Str("japaneseJSON")!, BuiltinTemplates.Japanese[t.Id].JsonString(), t.Id + " ja");
        }
        foreach (var item in Golden.ExpectedJson("templates/section-labels.json").EnumerateArray())
        {
            var l = MeetingLanguages.Parse(item.Str("language"))!.Value;
            Assert.Equal(item.Strings("labels"), TemplateDefinition.BuiltinSectionIds.Select(id => TemplateDefinition.SectionLabel(id, l)).ToList());
        }
        foreach (var item in Golden.ExpectedJson("templates/decode.json").EnumerateArray())
            Golden.SameEmbeddedJson(item.Str("output")!, TemplateDefinition.Decode(item.Str("input")!).JsonString(), item.Str("input")!);
    }

    [Fact]
    public void WhisperPrompts()
    {
        var glossaries = Golden.Fixtures.GetProperty("whisperGlossaries").EnumerateArray()
            .Select(g => g.EnumerateArray().Select(x => x.GetString()!).ToList()).ToList();
        foreach (var item in Golden.ExpectedJson("whisper/prompts.json").EnumerateArray())
        {
            var l = MeetingLanguages.Parse(item.Str("language"))!.Value;
            Assert.Equal(item.Str("prompt"), WhisperRules.Prompt(glossaries[(int)item.Long("glossary")!], l));
        }
        foreach (var item in Golden.ExpectedJson("whisper/chunk-prompts.json").EnumerateArray())
        {
            var fx = Golden.Meeting(item.Str("key")!);
            var code = fx.Language.WhisperCode();
            List<TranscribedSegment> previous;
            string basePrompt;
            if (item.Get("previous") is { ValueKind: JsonValueKind.String })
            {
                previous = Enumerable.Range(0, 40).Select(i => new TranscribedSegment(i, i + 1, $"第{i}句 sentence number {i}", false)).ToList();
                basePrompt = "P";
            }
            else
            {
                var count = (int)item.Long("previous")!;
                previous = fx.Segments.Take(count).Select(s => new TranscribedSegment(s.Start, s.End, s.Text, s.LowConfidence)).ToList();
                basePrompt = WhisperRules.Prompt(fx.Glossary, fx.Language);
            }
            Assert.Equal(item.Str("prompt"), WhisperRules.ChunkPrompt(basePrompt, previous, code));
        }
        Assert.Equal(Golden.ExpectedJson("whisper/hallucinations.json").EnumerateArray().Select(x => x.GetString()), WhisperRules.HallucinationPhrases);
        foreach (var item in Golden.ExpectedJson("whisper/compression.json").EnumerateArray())
        {
            // zlib 的實作不同，壓縮後的大小差幾個位元組：容許 ±0.05（design-windows §4.2）
            var ratio = WhisperRules.CompressionRatio(item.Str("text")!);
            Assert.InRange(ratio, Golden.Number(item.Get("ratio")) - 0.05, Golden.Number(item.Get("ratio")) + 0.05);
        }
    }

    [Fact]
    public void MeetingNotesToolDescriptionFollowsInterface()
    {
        L.Use(new Dictionary<string, string> { ["儲存整理好的會議紀錄"] = "Save the meeting notes" });
        try { Assert.Equal("Save the meeting notes", OutputFormat.MeetingNotes.ToolDescription); }
        finally { L.Use(null); }
    }
}
