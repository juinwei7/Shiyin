using System.Reflection;
using System.Text.Json;
using Shiyin.Model;
using Shiyin.Notes;
using Shiyin.Text;
using Xunit;

namespace Shiyin.Tests;

/// <summary>標準答案（design-windows §4）：shared/golden/fixtures.json 是輸入，expected/ 是 Mac 算出來的結果</summary>
public static class Golden
{
    public static readonly string Root = typeof(Golden).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .First(a => a.Key == "GoldenRoot").Value!;

    public static readonly JsonElement Fixtures = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "fixtures.json"))).RootElement.Clone();

    public static string Expected(string path) => File.ReadAllText(Path.Combine(Root, "expected", path));

    public static JsonElement ExpectedJson(string path) => JsonDocument.Parse(Expected(path)).RootElement.Clone();

    /// <summary>文字逐字元相同；不同時指出第一個不同的位置</summary>
    public static void Text(string path, string actual)
    {
        var expected = Expected(path);
        if (expected == actual) return;
        var i = 0;
        while (i < Math.Min(expected.Length, actual.Length) && expected[i] == actual[i]) i++;
        string Around(string s) => s.Substring(Math.Max(0, i - 40), Math.Min(s.Length - Math.Max(0, i - 40), 80)).Replace("\n", "⏎");
        Assert.Fail($"{path} 第 {i} 個字元不同\n預期：…{Around(expected)}…\n實際：…{Around(actual)}…");
    }

    /// <summary>JSON 比解析後的內容（鍵的順序、數字的寫法可以不同）</summary>
    public static void Json(string path, string actualJson) => SameJson(ExpectedJson(path), JsonDocument.Parse(actualJson).RootElement, path);

    public static void SameJson(JsonElement expected, JsonElement actual, string where)
    {
        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                Assert.True(actual.ValueKind == JsonValueKind.Object, $"{where}：應該是物件，實際是 {actual.ValueKind}");
                var e = expected.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                var a = actual.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                Assert.True(e.Keys.OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(a.Keys.OrderBy(k => k, StringComparer.Ordinal)),
                    $"{where}：鍵不同，預期 [{string.Join(",", e.Keys)}]，實際 [{string.Join(",", a.Keys)}]");
                foreach (var (k, v) in e) SameJson(v, a[k], $"{where}.{k}");
                break;
            case JsonValueKind.Array:
                Assert.True(actual.ValueKind == JsonValueKind.Array, $"{where}：應該是陣列，實際是 {actual.ValueKind}");
                var ea = expected.EnumerateArray().ToList();
                var aa = actual.EnumerateArray().ToList();
                Assert.True(ea.Count == aa.Count, $"{where}：陣列長度 {ea.Count} ≠ {aa.Count}");
                for (var i = 0; i < ea.Count; i++) SameJson(ea[i], aa[i], $"{where}[{i}]");
                break;
            case JsonValueKind.Number:
                Assert.True(actual.ValueKind == JsonValueKind.Number, $"{where}：應該是數字，實際是 {actual}");
                var x = expected.GetDouble();
                var y = actual.GetDouble();
                Assert.True(Math.Abs(x - y) <= 1e-9 * Math.Max(1, Math.Abs(x)), $"{where}：{x} ≠ {y}");
                break;
            case JsonValueKind.String:
                Assert.True(actual.ValueKind == JsonValueKind.String && expected.GetString() == actual.GetString(),
                    $"{where}：預期 {expected}，實際 {actual}");
                break;
            default:
                Assert.True(expected.ValueKind == actual.ValueKind, $"{where}：預期 {expected.ValueKind}，實際 {actual.ValueKind}");
                break;
        }
    }

    /// <summary>JSON 字串欄位裡又是 JSON（範本定義、說話者復原資料）：解析後再比</summary>
    public static void SameEmbeddedJson(string expected, string actual, string where) =>
        SameJson(JsonDocument.Parse(expected).RootElement, JsonDocument.Parse(actual).RootElement, where);

    public static double Number(JsonElement? e) => e is { ValueKind: JsonValueKind.Number } n ? n.GetDouble() : 0;

    // MARK: 測試會議（跟 Mac 的 GoldenDump.meetingFixture 一樣）

    public sealed record MeetingFixture(
        string Key, Meeting Meeting, List<Segment> Segments, Dictionary<int, string> Speakers, Dictionary<int, SpeakerVoice> Voices,
        List<Bookmark> Bookmarks, List<string> Glossary, MeetingNotes? Notes, List<ReviewAnswer> Answers,
        List<(string Question, string Answer)> History, string Question, List<string> KnownPeople)
    {
        public MeetingLanguage Language => Meeting.MeetingLanguage;

        /// <summary>資料庫裡的說話者表：1 到最大編號，沒改名的是預設名稱</summary>
        public Dictionary<int, string> SpeakerTable
        {
            get
            {
                var count = Segments.Select(s => s.Speaker ?? 0).DefaultIfEmpty(0).Max();
                var table = new Dictionary<int, string>();
                for (var idx = 1; idx <= count; idx++) table[idx] = Speakers.TryGetValue(idx, out var n) ? n : Language.DefaultSpeaker(idx);
                return table;
            }
        }
    }

    public static readonly List<MeetingFixture> Meetings = Fixtures.GetProperty("meetings").EnumerateArray().Select(Parse).ToList();

    public static MeetingFixture Meeting(string key) => Meetings.First(m => m.Key == key);

    static MeetingFixture Parse(JsonElement o)
    {
        var m = o.GetProperty("meeting");
        var meeting = new Meeting
        {
            Title = m.Str("title") ?? "", TitleLocked = m.Bool("titleLocked") ?? false, MeetingDate = m.Str("meetingDate") ?? "",
            Participants = m.Str("participants") ?? "", SourceFilename = m.Str("sourceFilename") ?? "",
            DurationSeconds = (int?)m.Long("durationSeconds"), TemplateId = m.Str("templateId") ?? "general",
            UserNotes = m.Str("userNotes") ?? "", Language = m.Str("language") ?? "zh", Folder = m.Str("folder") ?? "",
            Channels = m.Str("channels") ?? "mic", CreatedAt = "2026-10-20T00:00:00Z", UpdatedAt = "2026-10-20T00:00:00Z",
        };
        var segments = o.GetProperty("segments").EnumerateArray().Select((s, i) => new Segment
        {
            MeetingId = 0, Seq = i, Start = Number(s.Get("start")), End = Number(s.Get("end")), Text = s.Str("text") ?? "",
            LowConfidence = s.Bool("lowConfidence") ?? false, Speaker = (int?)s.Long("speaker"),
        }).ToList();
        var speakers = o.GetProperty("speakers").EnumerateObject().ToDictionary(p => int.Parse(p.Name), p => p.Value.GetString()!);
        var voices = o.GetProperty("voices").EnumerateObject().ToDictionary(p => int.Parse(p.Name), p => new SpeakerVoice(
            p.Value.GetProperty("embedding").EnumerateArray().Select(x => (float)x.GetDouble()).ToArray(),
            Number(p.Value.Get("seconds")), p.Value.Str("source") ?? "mix"));
        var bookmarks = o.GetProperty("bookmarks").EnumerateArray().Select(b => new Bookmark { Time = Number(b.Get("time")), Label = b.Str("label") ?? "" }).ToList();
        var notes = o.Get("notes") is { ValueKind: JsonValueKind.Object } n ? MeetingNotes.Decode(n) : null;
        var answers = o.GetProperty("answers").EnumerateArray().Select(a => new ReviewAnswer(a.Str("time") ?? "", a.Str("question") ?? "", a.Str("answer") ?? "")).ToList();
        var history = o.GetProperty("history").EnumerateArray().Select(h => (h.Str("question") ?? "", h.Str("answer") ?? "")).ToList();
        return new MeetingFixture(o.Str("key")!, meeting, segments, speakers, voices, bookmarks, o.Strings("glossary") ?? [], notes, answers,
            history, o.Str("question") ?? "", o.Strings("knownPeople") ?? []);
    }

    /// <summary>跟 Mac 一樣的亂數：線性同餘，取高 16 位當 Int16</summary>
    public struct Lcg(uint state)
    {
        uint s = state;
        public float Next()
        {
            s = unchecked(s * 1_664_525u + 1_013_904_223u);
            return (short)(s >> 16) / 32768f;
        }
    }
}
