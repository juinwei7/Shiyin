using System.Text.Json;
using Shiyin.IO;
using Shiyin.Model;
using Shiyin.Notes;
using Shiyin.Text;
using Xunit;

namespace Shiyin.Tests;

/// <summary>會議的參考文件（M§39、R61）：長度分配、整理文字、檔名、路徑、讀出來的文字跟 Mac 一樣</summary>
public class DocumentTests
{
    [Fact]
    public void BudgetAndBlocks()
    {
        var cases = Golden.Fixtures.GetProperty("documentBudget").EnumerateArray().ToList();
        var expected = Golden.ExpectedJson("documents/budget.json").EnumerateArray().ToList();
        Assert.Equal(expected.Count, cases.Count);
        for (var i = 0; i < cases.Count; i++)
        {
            var limit = (int)(cases[i].Long("limit") ?? DocumentPrompt.Limit);
            var docs = Golden.Texts(cases[i].Get("documents"));
            var allocated = DocumentPrompt.Allocate(docs, limit);
            var e = expected[i].GetProperty("allocated").EnumerateArray().ToList();
            Assert.Equal(e.Count, allocated.Count);
            for (var j = 0; j < e.Count; j++)
            {
                var where = $"budget[{i}][{j}]";
                Assert.True(e[j].Strings("pages")!.SequenceEqual(allocated[j].Pages), where + " pages");
                Assert.Equal(e[j].Long("includedPages"), allocated[j].IncludedPages);
                Assert.Equal(e[j].Long("includedChars"), allocated[j].IncludedChars);
                Assert.Equal(e[j].Bool("truncated"), allocated[j].Truncated);
                Assert.Equal(e[j].Long("length"), docs[j].Length);
            }
            foreach (var block in expected[i].GetProperty("blocks").EnumerateObject())
                Assert.Equal(block.Value.GetString(), DocumentPrompt.Block(docs, MeetingLanguages.Parse(block.Name)!.Value, limit));
        }
    }

    [Fact]
    public void Normalize()
    {
        foreach (var item in Golden.ExpectedJson("documents/normalize.json").EnumerateArray())
            Assert.Equal(item.Str("output"), DocumentReader.Normalize(item.Str("input")!));
    }

    [Fact]
    public void NamesAndPaths()
    {
        foreach (var item in Golden.ExpectedJson("documents/names.json").EnumerateArray())
        {
            var name = item.Str("name")!;
            Assert.True(item.Str("kind") == DocumentReader.KindOf(name)?.Raw(), $"{name} 的種類");
            Assert.True(item.Bool("dropped") == DocumentReader.IsDroppedDocument(name), $"{name} 拖進來算不算文件");
            Assert.True(item.Str("safeName") == DocumentReader.SafeName(name), $"{name} 存的檔名");
        }
        foreach (var item in Golden.ExpectedJson("documents/paths.json").EnumerateArray())
            Assert.True(item.Bool("safe") == IO.PackageJson.IsSafeDocumentPath(item.Str("path")!), item.Str("path"));
        foreach (var item in Golden.ExpectedJson("documents/pptx-targets.json").EnumerateArray())
            Assert.True(item.Str("path") == OfficeText.Resolve(item.Str("target")!), item.Str("target"));
    }

    /// <summary>shared/golden/documents 的測試檔：docx、pptx、純文字讀出來跟 Mac 一字不差，讀不了的原因相同</summary>
    [Fact]
    public void ReadsTestFiles()
    {
        var folder = Path.Combine(Golden.Root, "documents");
        var checkedFiles = 0;
        foreach (var path in Directory.GetFiles(folder).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(path);
            if (name.StartsWith('.') || PathText.Extension(name).ToLowerInvariant() is not ("docx" or "pptx" or "txt" or "md" or "key" or "doc")) continue;
            var expected = Golden.ExpectedJson($"documents/read/{name}.json");
            try
            {
                var r = DocumentReader.Read(path);
                Assert.True(expected.Get("error") is null, $"{name}：Mac 讀不了（{expected.Str("error")}），Windows 讀得到");
                Assert.Equal(expected.Str("kind"), r.Kind.Raw());
                Assert.Equal(expected.Strings("pages"), r.Pages);
                Assert.Equal(expected.Bool("ocr"), r.Ocr);
            }
            catch (DocumentError error)
            {
                Assert.Equal(expected.Str("error"), error.Code);
            }
            checkedFiles++;
        }
        // 每個 Mac 讀過的測試檔都比過了
        Assert.Equal(Directory.GetFiles(Path.Combine(Golden.Root, "expected", "documents", "read"), "*.json").Length, checkedFiles);
    }
}
