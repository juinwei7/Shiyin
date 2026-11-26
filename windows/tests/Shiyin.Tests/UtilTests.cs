using System.Text.Json;
using Shiyin.IO;
using Shiyin.Model;
using Shiyin.Notes;
using Shiyin.Services.Capture;
using Shiyin.Text;
using Shiyin.Transcribe;
using Xunit;

namespace Shiyin.Tests;

/// <summary>小工具：會議紀錄的寬鬆解碼、時間格式、日期、檔名、匯入、回傳結果的檢查</summary>
public class UtilTests
{
    /// <summary>會議內容的文字大小（R59.3、M§38.5）：七段、存 −1～5、超出範圍拉回來</summary>
    [Fact]
    public void TextSizesClampAndLabel()
    {
        Assert.Equal(0.85, Shiyin.Services.TextSizes.Scale(-1));
        Assert.Equal(1, Shiyin.Services.TextSizes.Scale(0));
        Assert.Equal(2, Shiyin.Services.TextSizes.Scale(5));
        Assert.Equal(2, Shiyin.Services.TextSizes.Scale(9));
        Assert.Equal("130%", Shiyin.Services.TextSizes.Label(2));
        Assert.Equal(0, Shiyin.Services.AppSettings.InMemory().TextSize);
        Assert.Equal(5, Shiyin.Services.AppSettings.InMemory(new Dictionary<string, string> { ["contentTextSize"] = "12" }).TextSize);
        var settings = Shiyin.Services.AppSettings.InMemory();
        settings.Set(Shiyin.Services.AppSettings.TextSizeKey, 3.0);
        Assert.Equal(3, settings.TextSize);
    }

    /// <summary>雙聲道的錄音混在一起（R73、M§49.1）：(L + R) × 0.7071、截在 ±1；其他聲道數取平均</summary>
    [Fact]
    public void ChannelMixSumsStereo()
    {
        var mono = new float[3];
        Shiyin.Services.Capture.ChannelMix.Mix([0.5f, 0, -0.5f, 0.2f, 0.9f, 0.9f], 2, mono);
        Assert.Equal([0.35355f, -0.21213f, 1f], mono.Select(v => MathF.Round(v, 5)));
        var three = new float[1];
        Shiyin.Services.Capture.ChannelMix.Mix([0.3f, 0.6f, 0.9f], 3, three);
        Assert.Equal(0.6f, three[0], 5);
    }

    /// <summary>麥克風的 gate（M§93.4）：區塊裡 1、外面 0、頭尾 10 ms 淡入淡出，跟 Mac 的 util/mic-gate.json 比對</summary>
    [Fact]
    public void MicGateSameAsMac()
    {
        var output = new System.Text.Json.Nodes.JsonArray();
        foreach (var c in Golden.Fixtures.GetProperty("micGateCases").EnumerateArray())
        {
            var regions = c.GetProperty("regions").EnumerateArray().Select(r => (r[0].GetDouble(), r[1].GetDouble())).ToList();
            output.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["key"] = c.Str("key"),
                ["gates"] = new System.Text.Json.Nodes.JsonArray([.. c.GetProperty("times").EnumerateArray()
                    .Select(t => (System.Text.Json.Nodes.JsonNode)Shiyin.Services.Capture.ChannelMix.Gate(t.GetDouble(), regions))]),
            });
        }
        Golden.Json("util/mic-gate.json", output.ToJsonString());
    }

    /// <summary>聽哪一邊、麥克風只播建議收錄（M§93.4）：(L × 麥克風音量 × gate + R × 電腦聲音音量) × 0.7071</summary>
    [Fact]
    public void ChannelMixGated()
    {
        float[] Mix(ChannelSettings s, double start)
        {
            var mono = new float[4];
            ChannelMix.MixGated([0.5f, 0.2f, 0.5f, 0.2f, 0.5f, 0.2f, 0.5f, 0.2f], mono, s, start, 10);   // 每個取樣 0.1 秒
            return [.. mono.Select(v => MathF.Round(v, 5))];
        }
        float both = MathF.Round(0.7f * ChannelMix.Gain, 5), mic = MathF.Round(0.5f * ChannelMix.Gain, 5), system = MathF.Round(0.2f * ChannelMix.Gain, 5);
        Assert.Equal([both, both, both, both], Mix(ChannelSettings.Default, 0));
        Assert.Equal([mic, mic, mic, mic], Mix(ChannelSettings.For(PlayChannels.Mic, false, []), 0));
        Assert.Equal([system, system, system, system], Mix(ChannelSettings.For(PlayChannels.System, true, [(0, 10)]), 0));
        // 區塊 0.1～0.25 秒：第 2 個取樣（0.1 秒）在開頭是 0，第 3 個（0.2 秒）離結尾 0.05 秒（超過淡出的 10 ms）是 1
        var gated = ChannelSettings.For(PlayChannels.Both, true, [(0.1, 0.25)]);
        Assert.Equal([system, system, both, system], Mix(gated, 0));
        Assert.True(gated.GateMic);
        Assert.False(ChannelSettings.For(PlayChannels.System, true, [(0, 1)]).GateMic);
    }

    /// <summary>匯出 wav 的混音（R119.6、M§93.6）：區塊外麥克風是 0、電腦聲音照留，長度取兩邊較短的</summary>
    [Fact]
    public void MixGatedChannelsForExport()
    {
        float[] left = [0.5f, 0.5f, 0.5f, 0.5f, 0.5f], right = [0.2f, 0.2f, 0.2f, 0.2f];
        var mixed = ChannelMix.MixGatedChannels(left, right, [(0.15, 0.35)], 10);   // 每個取樣 0.1 秒：第 3 個（0.2 秒）在區塊裡、離邊界超過 10 ms
        Assert.Equal(4, mixed.Length);
        float both = MathF.Round(0.7f * ChannelMix.Gain, 5), system = MathF.Round(0.2f * ChannelMix.Gain, 5);
        Assert.Equal([system, system, both, both], mixed.Select(v => MathF.Round(v, 5)));
    }

    /// <summary>GatedMono 記得播到哪裡：跳播時設 Time，接著讀的取樣照那個時間篩</summary>
    [Fact]
    public void GatedMonoTracksTime()
    {
        using var home = new TempHome();
        var path = Path.Combine(home.Path, "both.wav");
        var count = 16000 * 3;
        using (var writer = new WavWriter(path, 2)) writer.Write([Enumerable.Repeat(0.5f, count).ToArray(), new float[count]], count);
        using var reader = new NAudio.Wave.WaveFileReader(path);
        var control = new Shiyin.Services.Capture.ChannelControl();
        control.Settings = Shiyin.Services.Capture.ChannelSettings.For(Shiyin.Services.Capture.PlayChannels.Both, true, [(1.0, 2.0)]);
        var mono = new Shiyin.Services.Capture.ChannelMix.GatedMono(NAudio.Wave.WaveExtensionMethods.ToSampleProvider(reader), control);
        var buffer = new float[1600];
        float Peak() { var n = mono.Read(buffer); return buffer[..n].Max(MathF.Abs); }
        Assert.Equal(0, Peak());                        // 0～0.1 秒：區塊外
        reader.CurrentTime = TimeSpan.FromSeconds(1.5);
        mono.Time = 1.5;
        Assert.Equal(0.5f * Shiyin.Services.Capture.ChannelMix.Gain, Peak(), 4);   // 1.5～1.6 秒：區塊裡
        Assert.Equal(1.6, mono.Time, 6);
        control.Settings = Shiyin.Services.Capture.ChannelSettings.For(Shiyin.Services.Capture.PlayChannels.System, false, []);
        Assert.Equal(0, Peak());                        // 改成只電腦聲音：下一批就生效
    }

    /// <summary>左聲道有聲音、右聲道靜音的 WAV 讀成單聲道：峰值是左聲道 × 0.7071、長度不變</summary>
    [Fact]
    public void ChannelMixReadsStereoWavAsMono()
    {
        using var home = new TempHome();
        var path = Path.Combine(home.Path, "both.wav");
        var count = 16000;
        var left = Enumerable.Range(0, count).Select(i => 0.5f * MathF.Sin(i * 0.1f)).ToArray();
        using (var writer = new WavWriter(path, 2)) writer.Write([left, new float[count]], count);
        using var reader = new NAudio.Wave.WaveFileReader(path);
        var mono = new Shiyin.Services.Capture.ChannelMix.ToMono(NAudio.Wave.WaveExtensionMethods.ToSampleProvider(reader));
        Assert.Equal(1, mono.WaveFormat.Channels);
        var all = new List<float>();
        var buffer = new float[1600];
        int n;
        while ((n = mono.Read(buffer)) > 0) all.AddRange(buffer[..n]);
        Assert.Equal(count, all.Count);
        Assert.Equal(0.5f * 0.7071f, all.Max(MathF.Abs), 3);
    }

    static IEnumerable<JsonElement> Items(string path) => Golden.ExpectedJson(path).EnumerateArray();

    /// <summary>語言檔推出來的東西（M§41.8）：每種語言的名稱、預設說話者、轉錄的參數、給人看的字、提示詞的字，跟 Mac 逐項相同</summary>
    [Fact]
    public void LanguageProfiles()
    {
        string[] glossary = ["Lumio", "Redis", "Kubernetes"];
        var expected = Items("util/languages.json").ToList();
        Assert.Equal(expected.Select(e => e.Str("code")!).ToList(), MeetingLanguages.All.Select(l => l.Code).ToList());
        foreach (var item in expected)
        {
            var l = MeetingLanguages.Parse(item.Str("code"))!.Value;
            var p = l.Profile;
            Assert.Equal(item.Str("name"), l.Name());
            Assert.Equal(item.Strings("uiName"), [p.UiName.Zh, p.UiName.En]);
            Assert.Equal(item.Str("tier"), p.Tier);
            Assert.Equal(item.Str("base"), p.Base);
            Assert.Equal(item.Strings("speaker"), [l.DefaultSpeaker(1), l.DefaultSpeaker(12)]);
            Assert.Equal(item.Get("isDefault")!.Value.EnumerateArray().Select(b => b.GetBoolean()).ToList(),
                [SpeakerNames.IsDefault(l.DefaultSpeaker(3)), SpeakerNames.IsDefault(l.DefaultSpeaker(3) + "x")]);
            Assert.Equal(item.Str("whisper"), l.WhisperCode());
            Assert.Equal(item.Str("qwen"), QwenRules.LanguageName(l));
            Assert.Equal(item.Str("detect"), p.Detect);
            Assert.Equal(item.Str("family"), p.Family);
            Assert.Equal(item.Get("usesTurbo")!.Value.GetBoolean(), l.UsesTurbo());
            Assert.Equal(item.Str("whisperPrompt"), WhisperRules.Prompt(glossary, l));
            Assert.Equal(item.Str("qwenContext"), QwenRules.Context(glossary, l));
            Assert.Equal(item.Strings("duration"), [TimeFormat.Duration(65, l), TimeFormat.Duration(3600, l), TimeFormat.Duration(3900, l)]);
            Assert.Equal(item.Str("weekday"), DateText.Weekday("2026-11-04", l));
            Assert.Equal(item.Strings("text"), [l.Text("摘要", "Summary", "要約"), l.Text("沒有的字", "Not in table", "ない")]);
            Assert.Equal(item.Strings("prompt"), [l.Prompt("摘要", "Summary", "要約")]);
            Assert.Equal(item.Strings("format"), [l.Format("「%@」是%@嗎？", "Is “%@” %@?", "「%@」は%@ですか？", "A", "B")]);
            Assert.Equal(item.Strings("yesNo"), [new YesNoAnswer(true).Text(l), new YesNoAnswer(false, "C").Text(l)]);
            Assert.Equal(item.Strings("sections"), TemplateDefinition.BuiltinSectionIds.Select(id => TemplateDefinition.SectionLabel(id, l)).ToList());
            Assert.Equal(item.Strings("promptSections"), TemplateDefinition.BuiltinSectionIds.Select(id => TemplateDefinition.SectionLabel(id, l, prompt: true)).ToList());
            Assert.Equal(item.Str("script"), p.FixScript(ChineseScript.ToTaiwan("我系陈家豪，唔系好稳定，系统同关系都冇问题，唔该晒，联系我，晒太阳")));
        }
    }

    [Fact]
    public void SimplifiedToTaiwan()
    {
        // 簡體轉台灣繁體（M§32.5）：跟 Mac 的 ChineseScript.toTaiwan 逐字相同
        foreach (var item in Items("util/s2tw.json"))
            Assert.Equal(item.Str("out"), ChineseScript.ToTaiwan(item.Str("in")!));
    }

    [Fact]
    public void TaiwanToSimplified()
    {
        // 繁轉簡（M§47.1）：跟 Mac 的 ChineseScript.toSimplified 逐字相同
        foreach (var item in Items("util/tw2s.json"))
            Assert.Equal(item.Str("out"), ChineseScript.ToSimplified(item.Str("in")!));
    }

    [Fact]
    public void SimplifiedInterfaceTerms()
    {
        // 簡體介面的用語（M§48.1）：跟 Mac 的 ChineseScript.toSimplifiedUI 逐字相同
        foreach (var item in Items("util/ui-hans.json"))
            Assert.Equal(item.Str("out"), ChineseScript.ToSimplifiedUI(item.Str("in")!));
    }

    [Fact]
    public void SimplifiedInterfaceTables()
    {
        // 兩份翻譯表的中文條目都有簡體（l10n.py hans 產生），沒有繁體字
        foreach (var path in new[] { "Resources/Localizable.xcstrings", "windows/src/Shiyin.App/Strings/Windows.xcstrings" })
        {
            var file = Path.Combine(Golden.Root, "..", "..", path);
            var hans = Localization.L.ReadXcstringsHans(File.ReadAllText(file));
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var entry in doc.RootElement.GetProperty("strings").EnumerateObject())
            {
                if (!entry.Name.Any(c => c >= 0x4E00 && c <= 0x9FFF)) continue;
                Assert.True(hans.TryGetValue(entry.Name, out var value), $"{path}：{entry.Name} 沒有簡體");
                Assert.Equal(value, ChineseScript.ToSimplified(value!));
            }
        }
    }

    [Fact]
    public void ReleaseNotesBrief()
    {
        // 更新說明只列前幾項（R10.6）：取段落、取條目跟 Mac 的 ReleaseNotes.brief 相同
        foreach (var item in Items("util/release-notes.json"))
        {
            var (items, more) = ReleaseNotes.Brief(item.Str("notes")!, item.GetProperty("english").GetBoolean());
            Assert.Equal(item.GetProperty("items").EnumerateArray().Select(x => x.GetString()), items);
            Assert.Equal(item.GetProperty("more").GetInt32(), more);
        }
    }

    [Fact]
    public void NotesDecode()
    {
        foreach (var item in Items("util/notes-decode.json"))
        {
            var input = item.Str("input")!;
            // 會議的 key（zh、en、ja、yue、ko…）是那場會議的紀錄，其他是要解碼的 JSON
            var notes = Golden.Meetings.Any(m => m.Key == input) ? Golden.Meeting(input).Notes : MeetingNotes.Decode(input);
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

    /// <summary>每個人的重點（R92、M§66、design-windows §11.25）：空的時候不寫進 JSON（以前的紀錄一字不差）；
    /// 解碼的寬鬆規則同 Mac 的 Person.init(from:)；改說話者名字、簡體、搜尋；待確認的連結（排序值在 custom 後面）</summary>
    [Fact]
    public void PeopleNotes()
    {
        const string old = """{"actionItems":[],"custom":[],"decisions":[],"openQuestions":[],"reviewItems":[],"summary":[{"text":"a","time":"00:01"}],"title":"t","topics":[]}""";
        Assert.Equal(old, MeetingNotes.Decode(old)!.JsonString());
        var empty = MeetingNotes.Decode(old.Replace("\"title\"", "\"people\":[],\"title\"", StringComparison.Ordinal))!;
        Assert.Empty(empty.People);
        Assert.Equal(old, empty.JsonString());

        // 陣列裡有不是物件的：整個 people 當作沒有；name、points 型別不對當作空的；條目可以是字串（v1）
        Assert.Empty(MeetingNotes.Decode("""{"people":[{"name":"A","points":[]},3]}""")!.People);
        var loose = MeetingNotes.Decode("""{"people":[{"name":5,"points":"x"},{"points":["提議"]}]}""")!;
        Assert.Equal(new[] { "", "" }, loose.People.Select(p => p.Name));
        Assert.Empty(loose.People[0].Points);
        Assert.Equal("提議", loose.People[1].Points[0].Text);
        Assert.False(loose.IsEmpty);
        Assert.Equal("""{"actionItems":[],"custom":[],"decisions":[],"openQuestions":[],"people":[{"name":"","points":[]},{"name":"","points":[{"text":"提議","time":""}]}],"reviewItems":[],"summary":[],"title":"","topics":[]}""",
            loose.JsonString());

        // 改說話者名字：名字、重點都換；預設名稱後面接數字的（說話者 10）不換
        var notes = new MeetingNotes
        {
            People = [new("說話者 1", [new("同意說話者 10 的提議", "00:05")]), new("說話者 10", [new("負責測試", "00:20")])],
            Summary = [new("摘要", "00:30")],
            ReviewItems = [new("00:20", "負責人是說話者 10 嗎？", true)],
        };
        var renamed = notes.ReplacingSpeakerName("說話者 1", "王小明", isDefaultName: true);
        Assert.Equal(new[] { "王小明", "說話者 10" }, renamed.People.Select(p => p.Name));
        Assert.Equal("同意說話者 10 的提議", renamed.People[0].Points[0].Text);
        Assert.Equal("00:05", renamed.People[0].Points[0].Time);
        Assert.Contains("王小明\n同意說話者 10 的提議\n說話者 10\n負責測試", renamed.PlainText);
        Assert.Equal("说话者 10", notes.Simplified().People[1].Name);

        // 待確認的連結：第幾個人的第幾條
        var links = ReviewLinks.Compute(notes);
        Assert.Equal(new[] { 0 }, links[NoteItemRef.Person(1, 0)]);
        Assert.Single(links);
        Assert.True(NoteSection.Person > NoteSection.Custom);
        Assert.Equal("person", NoteItemRef.Person(1, 0).SectionName);
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
        // 頭像的字和釉色（M§58、design-windows §11.22）：Mac 的 PersonAvatar.initials、Color.person、Color.speaker
        static string[] Hexes((uint Light, uint Dark) c) => [c.Light.ToString("X6"), c.Dark.ToString("X6")];
        var avatars = Golden.ExpectedJson("util/avatars.json");
        foreach (var item in avatars.GetProperty("people").EnumerateArray())
        {
            var name = item.Str("name")!;
            Assert.True(item.Str("initials") == Avatars.Initials(name), $"「{name}」的頭像：預期 {item.Str("initials")}，實際 {Avatars.Initials(name)}");
            Assert.Equal(item.Strings("color"), Hexes(Avatars.Speaker(Avatars.PersonIndex(name))));
        }
        foreach (var item in avatars.GetProperty("speakers").EnumerateArray())
            Assert.Equal(item.Strings("color"), Hexes(Avatars.Speaker((int)item.Long("idx")!)));
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

    /// <summary>提問的回答解析（M§21.8）：缺欄位用預設值，只有沒有 answer 才失敗，跟 Mac 的 util/answer-decode.json 逐字比</summary>
    [Fact]
    public void DecodeAnswerLeniently()
    {
        var expected = Golden.ExpectedJson("util/answer-decode.json").EnumerateArray().ToList();
        var cases = Golden.Fixtures.GetProperty("answerDecodeCases").EnumerateArray().ToList();
        Assert.Equal(expected.Count, cases.Count);
        for (var i = 0; i < cases.Count; i++)
        {
            var decoded = AskPrompt.Answer.Decode(cases[i].GetRawText());
            if (expected[i].ValueKind == System.Text.Json.JsonValueKind.Null) Assert.True(decoded is null, $"answerDecodeCases[{i}]");
            else Assert.Equal(expected[i].GetString(), decoded?.JsonString());
        }
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

        // 精簡逐字稿的驗證（R96、M§70）：輸入同 Mac 的 GoldenDump（去頭尾空白、重複的留第一個、跟原文一樣的丟、空字串留、比原文長太多的丟、範圍外的丟）
        var clean = new TranscriptCleanup.Context(MeetingLanguage.Zh, zh.Segments.Select(s => (s.Text, (string?)null)).ToList());
        var first = zh.Segments[0].Text;
        TranscriptCleanup.Line[] cleanRaw =
        [
            new(0, "  " + SwiftText.Prefix(first, SwiftText.Count(first) - 1) + " "), new(0, "重複的第二個"), new(1, zh.Segments[1].Text),
            new(2, ""), new(3, zh.Segments[3].Text + new string('補', 40)), new(9, "範圍外"),
        ];
        var validClean = TranscriptCleanup.Validate(cleanRaw, clean, 0, 4);
        var expectedClean = Items("util/validate-clean.json").ToList();
        Assert.Equal(expectedClean.Count, validClean.Count);
        for (var i = 0; i < validClean.Count; i++)
        {
            Assert.Equal(expectedClean[i].Long("segment"), validClean[i].Segment);
            Assert.Equal(expectedClean[i].Str("text"), validClean[i].Text);
        }
    }

    /// <summary>一次貼上好幾個詞彙（R94.1、M§68）：分隔、去空白、去重複同 Mac 的 GlossaryInput.split</summary>
    [Fact]
    public void GlossarySplitMatchesMac()
    {
        var expected = Items("util/glossary-split.json").ToList();
        Assert.NotEmpty(expected);
        foreach (var e in expected)
            Assert.Equal(e.Strings("terms") ?? [], GlossaryInput.Split(e.Str("input")!));
    }

    /// <summary>詞彙表的檔案（R117、M§91）：匯入的分法、匯出的格式、文字編碼同 Mac 的 GlossaryFile</summary>
    [Fact]
    public void GlossaryFileMatchesMac()
    {
        var expected = Golden.ExpectedJson("util/glossary-file.json");
        var parse = expected.GetProperty("parse").EnumerateArray().ToList();
        Assert.NotEmpty(parse);
        foreach (var e in parse)
            Assert.Equal(e.Strings("terms") ?? [], GlossaryFile.TermsFromFile(e.Str("input")!));
        foreach (var e in expected.GetProperty("export").EnumerateArray())
            Assert.Equal(e.Str("text"), GlossaryFile.Export(e.Strings("terms") ?? []));
        foreach (var e in expected.GetProperty("decode").EnumerateArray())
            Assert.Equal(e.Str("text"), GlossaryFile.Decode(Convert.FromBase64String(e.Str("base64")!)));
    }
}

/// <summary>翻譯表（design-windows §11.4）：直接讀 Mac 的 Localizable.xcstrings</summary>
public sealed class StringsTests
{
    [Fact]
    public void ReadsMacTableWithPlurals()
    {
        var path = Path.Combine(Golden.Root, "..", "..", "Resources", "Localizable.xcstrings");
        var (table, plurals) = Shiyin.Localization.L.ReadXcstrings(File.ReadAllText(path));
        Assert.Equal("## What happened", table["## 問題描述"]);
        Assert.Equal(("%lld person", "%lld people"), plurals["%lld 人"]);
        try
        {
            Shiyin.Localization.L.Use(table, plurals);
            Assert.Equal("1 person", Shiyin.Localization.L.F("%lld 人", 1));
            Assert.Equal("3 people", Shiyin.Localization.L.F("%lld 人", 3));
            Assert.Equal("找不到的鍵", Shiyin.Localization.L.T("找不到的鍵"));
        }
        finally { Shiyin.Localization.L.Use(null); }
        Assert.Equal("3 人", Shiyin.Localization.L.F("%lld 人", 3));
    }
}
