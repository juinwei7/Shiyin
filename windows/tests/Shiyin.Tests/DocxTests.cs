using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using Shiyin.Model;
using Shiyin.Services.Export;
using Xunit;

namespace Shiyin.Tests;

/// <summary>Word 匯出（design-windows §13）：從標準答案的 HTML 轉出來，結構對、檔案是有效的 Word 文件</summary>
public sealed class DocxTests : IDisposable
{
    readonly TempHome home = new();
    public void Dispose() => home.Dispose();

    (List<(string Style, string Text, bool Bullet)> Paragraphs, List<List<string>> Rows) Convert(string golden, MeetingLanguage language)
    {
        var path = Path.Combine(home.Path, Path.GetFileNameWithoutExtension(golden) + ".docx");
        DocxWriter.Write(File.ReadAllText(Path.Combine(Golden.Root, "expected", golden)), path, language);
        using var doc = WordprocessingDocument.Open(path, false);
        var errors = new OpenXmlValidator().Validate(doc).Select(e => $"{e.Path?.XPath}: {e.Description}").ToList();
        Assert.Empty(errors);
        var body = doc.MainDocumentPart!.Document!.Body!;
        var paragraphs = body.Elements<Paragraph>().Select(p => (
            p.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? "", string.Concat(p.Descendants<DocumentFormat.OpenXml.Wordprocessing.Text>().Select(t => t.Text)),
            p.ParagraphProperties?.NumberingProperties is not null)).ToList();
        var rows = body.Descendants<TableRow>().Select(r => r.Elements<TableCell>().Select(c => c.InnerText).ToList()).ToList();
        return (paragraphs, rows);
    }

    [Fact]
    public void NotesKeepHeadingsListsAndTable()
    {
        var (paragraphs, rows) = Convert("export/zh/notes.html", MeetingLanguage.Zh);
        Assert.Equal(("Heading1", "2026-10-15 產品週會", false), paragraphs[0]);
        Assert.Equal("Meta", paragraphs[1].Style);
        Assert.Contains(paragraphs, p => p == ("Heading2", "摘要", false));
        Assert.Contains(paragraphs, p => p.Bullet && p.Text == "確認 v3.6 上線時程 00:00");
        Assert.Contains(paragraphs, p => p.Style == "Heading3" && p.Text == "00:04 快取 & 部署");
        Assert.Equal(["負責人", "事項", "期限"], rows[0]);
        Assert.Equal(["王小明", "確認 <admin> 權限 | 部署 1:02:05", "2026-10-21"], rows[1]);
        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public void TranscriptAndOtherLanguages()
    {
        var (paragraphs, _) = Convert("export/zh/transcript-tt.html", MeetingLanguage.Zh);
        Assert.Contains(paragraphs, p => p.Text == "00:00 王小明：大家好，今天討論 v3.6 的上線時程。");
        foreach (var language in new[] { "en", "ja" })
        {
            var (notes, _) = Convert($"export/{language}/notes.html", MeetingLanguages.Parse(language)!.Value);
            Assert.Equal("Heading1", notes[0].Style);
        }
        var (empty, rows) = Convert("export/zh/notes-empty.html", MeetingLanguage.Zh);
        Assert.Empty(rows);
        Assert.NotEmpty(empty);
    }
}
