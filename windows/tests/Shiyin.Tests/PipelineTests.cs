using System.Text.Json;
using Shiyin.Model;
using Shiyin.Text;
using Shiyin.Transcribe;
using Xunit;

namespace Shiyin.Tests;

/// <summary>轉錄流程的規則跟 Mac 相同（R28.4）：分段點、語言偵測的收集、接縫、回音、說話者編號、分不準的判斷</summary>
public class PipelineTests
{
    static IEnumerable<JsonElement> Items(string path) => Golden.ExpectedJson(path).EnumerateArray();

    [Fact]
    public void Chunks()
    {
        var c = Golden.Fixtures.GetProperty("chunkSignal");
        var rate = (int)WhisperRules.SampleRate;
        var lcg = new Golden.Lcg((uint)c.Long("seed")!);
        var quietAt = c.GetProperty("quietAt").EnumerateArray().Select(x => x.GetDouble()).ToArray();
        var loud = (float)Golden.Number(c.Get("loud"));
        var quiet = (float)Golden.Number(c.Get("quiet"));
        var samples = new float[(int)c.Long("seconds")! * rate];
        for (var i = 0; i < samples.Length; i++)
        {
            var t = (double)i / rate;
            var amplitude = quietAt.Any(q => Math.Abs(t - q) < 0.4) ? quiet : loud;
            samples[i] = lcg.Next() * amplitude;
        }
        var expected = Golden.ExpectedJson("pipeline/chunks.json");
        Assert.Equal(expected.GetProperty("first").EnumerateArray().Select(x => (float)x.GetDouble()), samples.Take(8));
        var chunks = WhisperRules.Chunks(samples, Golden.Number(c.Get("chunkSeconds")));
        Assert.Equal(expected.GetProperty("chunks").EnumerateArray().Select(p => (p[0].GetInt32(), p[1].GetInt32())), chunks);
        Assert.Equal(expected.GetProperty("noChunks").EnumerateArray().Select(p => (p[0].GetInt32(), p[1].GetInt32())), WhisperRules.Chunks(samples, 0));
    }

    [Fact]
    public void LanguageCollector()
    {
        var expected = Items("pipeline/collector.json").ToList();
        var seeds = Golden.Fixtures.GetProperty("collectorSeeds").EnumerateArray().ToList();
        for (var k = 0; k < seeds.Count; k++)
        {
            var c = seeds[k];
            var rate = (int)WhisperRules.SampleRate;
            var lcg = new Golden.Lcg((uint)c.Long("seed")!);
            double from = Golden.Number(c.Get("voicedFrom")), to = Golden.Number(c.Get("voicedTo"));
            float loud = (float)Golden.Number(c.Get("loud")), quiet = (float)Golden.Number(c.Get("quiet"));
            var samples = new float[(int)c.Long("seconds")! * rate];
            for (var i = 0; i < samples.Length; i++)
            {
                var t = (double)i / rate;
                samples[i] = lcg.Next() * (t >= from && t < to ? loud : quiet);
            }
            float[]? ready = null;
            var collector = new LanguageCollector(r => ready = r);
            var fed = 0;
            for (var position = 0; position < samples.Length && ready is null; position += 1234)
            {
                var end = Math.Min(samples.Length, position + 1234);
                collector.Feed(samples.AsSpan(position, end - position));
                fed = end;
            }
            var e = expected[k];
            Assert.Equal(e.Bool("ready"), ready is not null);
            Assert.Equal(e.Long("count"), ready?.Length ?? 0);
            Assert.Equal(e.Long("fed"), fed);
            var sum = 0.0;
            foreach (var s in ready ?? []) sum += s;
            Assert.Equal(Golden.Number(e.Get("sum")), sum, 9);
        }
    }

    [Fact]
    public void SimilarAndEcho()
    {
        foreach (var item in Items("pipeline/similar.json"))
            Assert.Equal(item.Bool("similar"), TextRules.Similar(item.Str("a")!, item.Str("b")!));
        static TranscribedSegment Seg(JsonElement o) => new(Golden.Number(o.Get("start")), Golden.Number(o.Get("end")), o.Str("text") ?? "", false);
        var cases = Golden.Fixtures.GetProperty("echoCases").EnumerateArray().ToList();
        var expected = Items("pipeline/echo.json").ToList();
        for (var i = 0; i < cases.Count; i++)
            Assert.Equal(expected[i].Bool("isEcho"), TextRules.IsEcho(Seg(cases[i].GetProperty("segment")), cases[i].GetProperty("system").EnumerateArray().Select(Seg)));
    }

    [Fact]
    public void AssignNumbering()
    {
        var cases = Golden.Fixtures.GetProperty("assignCases").EnumerateArray().ToList();
        var expected = Items("pipeline/assign.json").ToList();
        for (var i = 0; i < cases.Count; i++)
        {
            var turns = cases[i].GetProperty("turns").EnumerateArray().Select(t =>
                new SpeakerRules.Turn((int)t.Long("speaker")!, Golden.Number(t.Get("start")), Golden.Number(t.Get("end")))).ToList();
            var segments = cases[i].GetProperty("segments").EnumerateArray().Select(s =>
                new TranscribedSegment(Golden.Number(s.Get("start")), Golden.Number(s.Get("end")), "", false)).ToList();
            var (speakers, numbering) = SpeakerRules.AssignNumbering(turns, segments);
            Assert.Equal(expected[i].GetProperty("speakers").EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Null ? (int?)null : x.GetInt32()), speakers);
            Assert.Equal(expected[i].GetProperty("numbering").EnumerateObject().ToDictionary(p => int.Parse(p.Name), p => p.Value.GetInt32()), numbering);
        }
    }

    [Fact]
    public void DiarizationQuality()
    {
        var cases = Golden.Fixtures.GetProperty("qualityCases").EnumerateArray().ToList();
        var expected = Items("pipeline/quality.json").ToList();
        for (var i = 0; i < cases.Count; i++)
        {
            var chunks = cases[i].GetProperty("chunks").EnumerateArray().Select(c => new SpeakerRules.ChunkEmbedding(
                c.Str("speakerId")!, (int)c.Long("chunkIndex")!, (int)c.Long("speakerIndex")!,
                c.GetProperty("embedding").EnumerateArray().Select(x => (float)x.GetDouble()).ToArray())).ToList();
            var q = SpeakerRules.Measure(chunks);
            var e = expected[i];
            Assert.Equal(Golden.Number(e.Get("collisionRate")), q.CollisionRate, 9);
            Assert.Equal(e.Long("multiSpeakerChunks"), q.MultiSpeakerChunks);
            Assert.Equal(Golden.Number(e.Get("cohesion")), q.Cohesion, 6);
            Assert.Equal(e.Long("clusters"), q.Clusters);
            Assert.Equal(e.Long("chunks"), q.Chunks);
            Assert.Equal(Golden.Number(e.Get("mergeRisk")), q.MergeRisk, 6);
        }
        var risks = new SpeakerRules.Quality[]
        {
            new(0.6, 12, 0.9, 1, 40), new(0, 0, 0.48, 2, 40), new(0, 0, 0.48, 2, 29), new(0, 0, 1.2, 3, 100),
        };
        var expectedRisks = Items("pipeline/merge-risk.json").ToList();
        for (var i = 0; i < risks.Length; i++)
        {
            Assert.Equal(Golden.Number(expectedRisks[i].Get("mergeRisk")), risks[i].MergeRisk, 9);
            Assert.Equal(expectedRisks[i].Bool("hint"), risks[i].MergeRisk >= 0.4);
        }
    }

    [Fact]
    public void RediarizeNames()
    {
        var cases = Golden.Fixtures.GetProperty("rediarizeCases").EnumerateArray().ToList();
        var expected = Items("pipeline/rediarize.json").ToList();
        for (var i = 0; i < cases.Count; i++)
        {
            var old = cases[i].GetProperty("old").EnumerateArray().Select(o => new SpeakerName
            {
                MeetingId = 1, Idx = (int)o.Long("idx")!, Name = o.Str("name")!, NameSource = o.Str("nameSource")!, PersonId = o.Long("personId"),
            }).ToList();
            var segments = cases[i].GetProperty("segments").EnumerateArray().Select((o, k) => new Segment
            {
                MeetingId = 1, Seq = k, Start = Golden.Number(o.Get("start")), End = Golden.Number(o.Get("end")),
                Speaker = (int?)o.Long("speaker"), SpeakerManual = o.Bool("manual") ?? false,
            }).ToList();
            var newSpeakers = cases[i].GetProperty("newSpeakers").EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Null ? (int?)null : x.GetInt32()).ToList();
            var mapping = SpeakerRules.OldToNew(segments, newSpeakers);
            Assert.Equal(expected[i].GetProperty("oldToNew").EnumerateObject().ToDictionary(p => int.Parse(p.Name), p => p.Value.GetInt32()), mapping);
            var kept = segments.Zip(newSpeakers).Where(p => !p.First.SpeakerManual).ToList();
            var inherited = SpeakerRules.InheritNames(old, kept.Select(p => p.First).ToList(), kept.Select(p => p.Second).ToList());
            var e = expected[i].GetProperty("inherited").EnumerateObject().ToDictionary(p => int.Parse(p.Name), p => p.Value);
            Assert.Equal(e.Keys.Order(), inherited.Keys.Order());
            foreach (var (k, v) in e)
            {
                Assert.Equal(v.Str("name"), inherited[k].Name);
                Assert.Equal(v.Str("source"), inherited[k].Source);
                Assert.Equal(v.Long("personId"), inherited[k].PersonId);
            }
        }
    }
}
