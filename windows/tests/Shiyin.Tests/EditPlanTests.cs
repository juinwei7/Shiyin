using System.Text.Json;
using System.Text.Json.Nodes;
using Shiyin.Model;
using Xunit;

namespace Shiyin.Tests;

/// <summary>編輯會議的片段（M§81.1）：時間對應、句子留不留、試聽的反查、分成兩場、我的筆記，跟 Mac 的 util/edit-plan.json 比對</summary>
public sealed class EditPlanTests
{
    static double D(JsonElement e) => e.GetDouble();

    [Fact]
    public void SameAsMac()
    {
        var output = new JsonArray();
        foreach (var c in Golden.Fixtures.GetProperty("editPlanCases").EnumerateArray())
        {
            var plan = new EditPlan(c.GetProperty("pieces").EnumerateArray().Select(p => new EditPlan.Piece((long)D(p[0]), D(p[1]), D(p[2]))).ToList());
            var points = c.GetProperty("points").EnumerateArray().Select(p => ((long)D(p[0]), D(p[1]))).ToList();
            var sentences = c.GetProperty("sentences").EnumerateArray().Select(s => ((long)D(s[0]), D(s[1]), D(s[2]))).ToList();
            JsonArray Pieces(EditPlan p) => new([.. p.Pieces.Select(x => (JsonNode)new JsonArray(x.MeetingId, x.Start, x.End))]);
            var split = c.TryGetProperty("cut", out var cut) ? plan.Split(D(cut)) : null;
            var o = new JsonObject
            {
                ["key"] = c.GetProperty("key").GetString(),
                ["duration"] = plan.Duration,
                ["map"] = new JsonArray([.. points.Select(p => plan.Map(p.Item1, p.Item2) is double m ? JsonValue.Create(m) : null)]),
                ["snap"] = new JsonArray([.. points.Select(p => (JsonNode)plan.Snap(p.Item1, p.Item2))]),
                ["sentences"] = new JsonArray([.. sentences.Select(s => plan.MapSentence(s.Item1, s.Item2, s.Item3) is { } r ? new JsonArray(r.Start, r.End) : null)]),
                ["sources"] = new JsonArray([.. c.GetProperty("sources").EnumerateArray().Select(t => plan.Source(D(t)) is { } r ? new JsonArray(r.MeetingId, r.Time) : null)]),
                ["split"] = split is { } parts ? new JsonObject { ["head"] = Pieces(parts.Head), ["tail"] = Pieces(parts.Tail) } : null,
            };
            if (c.TryGetProperty("userNotes", out var notes))
                o["userNotes"] = plan.RemapUserNotes(notes.GetProperty("text").GetString()!, (long)D(notes.GetProperty("meetingId")));
            // 麥克風的區塊（M§93.3）：一段時間跟保留的片段取交集再對應
            if (c.TryGetProperty("ranges", out var ranges))
                o["ranges"] = new JsonArray([.. ranges.EnumerateArray().Select(r =>
                    (JsonNode)new JsonArray([.. plan.MapRange((long)D(r[0]), D(r[1]), D(r[2])).Select(x => (JsonNode)new JsonArray(x.Start, x.End))]))]);
            if (c.TryGetProperty("keeping", out var keeping))
            {
                var kept = EditPlan.Keeping((long)D(keeping.GetProperty("meetingId")), D(keeping.GetProperty("start")), D(keeping.GetProperty("end")),
                    keeping.GetProperty("deleted").EnumerateArray().Select(r => (D(r[0]), D(r[1]))));
                o["keeping"] = Pieces(new EditPlan(kept));
            }
            output.Add(o);
        }
        Golden.Json("util/edit-plan.json", output.ToJsonString());
    }

    /// <summary>麥克風時間軸放大縮小（M§93.5）：每一步之後的範圍，跟 Mac 的 util/mic-zoom.json 比對</summary>
    [Fact]
    public void MicZoomSameAsMac()
    {
        var output = new JsonArray();
        foreach (var c in Golden.Fixtures.GetProperty("micZoomCases").EnumerateArray())
        {
            var duration = D(c.GetProperty("duration"));
            MicZoom? zoom = null;
            var steps = new JsonArray();
            foreach (var step in c.GetProperty("steps").EnumerateArray())
            {
                zoom = step[0].GetString() switch
                {
                    "zoom" => MicZoom.Zoomed(zoom, duration, D(step[1]), D(step[2])),
                    "pan" => zoom?.Panned(duration, D(step[1])),
                    "follow" => zoom?.Following(duration, D(step[1])),
                    _ => zoom,
                };
                steps.Add(zoom is { } z ? new JsonArray(z.Start, z.Span) : null);
            }
            output.Add(new JsonObject { ["key"] = c.GetProperty("key").GetString(), ["steps"] = steps });
        }
        Golden.Json("util/mic-zoom.json", output.ToJsonString());
    }

    /// <summary>會議紀錄的時間點換成 Snap 的時間：條目、待辦、議題開始、待確認、每個人的重點；不是時間的照留</summary>
    [Fact]
    public void NotesMappingTimes()
    {
        var plan = new EditPlan([new EditPlan.Piece(1, 2, 20), new EditPlan.Piece(1, 30, 55)]);
        var notes = new MeetingNotes
        {
            Title = "t",
            Summary = [new("a", "00:10"), new("b", "")],
            ActionItems = [new("o", "task", "週五", "00:25")],
            Topics = [new("議題", "00:31", [new("p", "00:50")])],
            ReviewItems = [new("01:00", "q", true)],
            People = [new("甲", [new("x", "不是時間")])],
        };
        var mapped = notes.MappingTimes(t => plan.Snap(1, t));
        Assert.Equal(["00:08", ""], mapped.Summary.Select(i => i.Time));
        Assert.Equal("00:18", mapped.ActionItems[0].Time);
        Assert.Equal("週五", mapped.ActionItems[0].Due);
        Assert.Equal(("00:19", "00:38"), (mapped.Topics[0].Start, mapped.Topics[0].Points[0].Time));
        Assert.Equal(("00:43", true), (mapped.ReviewItems[0].Time, mapped.ReviewItems[0].YesNo));
        Assert.Equal("不是時間", mapped.People[0].Points[0].Time);
        Assert.Equal("t", mapped.Title);
    }
}
