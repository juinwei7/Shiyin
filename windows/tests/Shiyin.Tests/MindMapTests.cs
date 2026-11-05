using System.Text.Json;
using System.Text.Json.Nodes;
using Shiyin.Model;
using Shiyin.Notes;
using Shiyin.Text;
using Xunit;

namespace Shiyin.Tests;

/// <summary>心智圖（M§42、design-windows §11.10）：樹、排版、SVG 跟 Mac 的標準答案逐字相同</summary>
public class MindMapTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var m in Golden.Meetings.Where(m => m.Notes is not null)) yield return [m.Key];
        foreach (var c in Golden.Fixtures.GetProperty("mindMapCases").EnumerateArray()) yield return [c.Str("key")!];
    }

    static (MeetingNotes Notes, string Title, MeetingLanguage Language) Input(string key)
    {
        if (Golden.Meetings.FirstOrDefault(m => m.Key == key) is { Notes: { } notes } m)
            return (notes, m.Meeting.DisplayTitle, m.Meeting.MeetingLanguage);
        var c = Golden.Fixtures.GetProperty("mindMapCases").EnumerateArray().First(x => x.Str("key") == key);
        return (MeetingNotes.Decode(c.GetProperty("notes")), c.Str("title") ?? "", MeetingLanguages.Parse(c.Str("language")) ?? MeetingLanguage.Zh);
    }

    static JsonNode Tree(MindMap.Node n) =>
        new JsonObject { ["text"] = n.Text, ["time"] = n.Time, ["children"] = new JsonArray([.. n.Children.Select(Tree)]) };

    [Theory]
    [MemberData(nameof(Cases))]
    public void SameAsMac(string key)
    {
        var (notes, title, language) = Input(key);
        var tree = MindMap.Tree(notes, title, MindMap.Labels.For(language));
        var layout = MindMap.LayoutOf(tree);
        var json = new JsonObject
        {
            ["tree"] = Tree(tree),
            ["width"] = MindMap.Half(layout.Width),
            ["height"] = MindMap.Half(layout.Height),
            ["boxes"] = new JsonArray([.. layout.Boxes.Select(b => (JsonNode)new JsonObject
            {
                ["id"] = b.Id, ["level"] = b.Level, ["branch"] = b.Branch, ["side"] = b.Side,
                ["x"] = MindMap.Half(b.X), ["y"] = MindMap.Half(b.Y), ["w"] = MindMap.Half(b.W), ["h"] = MindMap.Half(b.H),
                ["lines"] = new JsonArray([.. b.Lines.Select(l => (JsonNode)l)]), ["time"] = b.Time, ["timeWidth"] = MindMap.Half(b.TimeWidth),
            })]),
            ["edges"] = new JsonArray([.. layout.Edges.Select(e => (JsonNode)new JsonObject
            {
                ["from"] = e.From, ["to"] = e.To, ["branch"] = e.Branch,
                ["x0"] = MindMap.Half(e.X0), ["y0"] = MindMap.Half(e.Y0), ["x1"] = MindMap.Half(e.X1), ["y1"] = MindMap.Half(e.Y1),
            })]),
        };
        Golden.Json($"mindmap/{key}.json", json.ToJsonString());
        Golden.Text($"mindmap/{key}.svg", MindMap.Svg(layout, language));
    }

    [Fact]
    public void WrapKeepsWordsDatesAndBrackets()
    {
        // 英文詞在 - 後面斷、日期不拆、左括號帶到下一行、數字的逗號不拆、韓文在空格斷
        Assert.Equal(["internationalization-", "pipeline"], MindMap.Wrap("internationalization-pipeline", 13 * 12, 13));
        Assert.DoesNotContain(MindMap.Wrap("週五前寄給所有人（2026-11-13）", 13 * 9, 13), l => l.EndsWith('（'));
        Assert.All(MindMap.Wrap("每月約多 8,000 元，因應下個月推廣活動", 13 * 7, 13), l => Assert.False(l.StartsWith("000", StringComparison.Ordinal)));
        Assert.Equal(["다음 버전", "진행 상황"], MindMap.Wrap("다음 버전 진행 상황", 13 * 5, 13));
    }
}
