using System.IO.Compression;
using System.Text;
using Shiyin.Data;
using Shiyin.IO;
using Shiyin.Model;
using Shiyin.Services;
using Xunit;

namespace Shiyin.Tests;

/// <summary>參考文件的讀取（PdfPig、文字辨識的介面）、存檔、刪除、封包（design-windows §11.8、M§39）</summary>
public sealed class DocumentServicesTests : IDisposable
{
    readonly TempHome home = new();
    readonly ShiyinPaths paths;
    readonly AppDatabase db;

    static readonly string Samples = Path.Combine(Golden.Root, "documents");

    public DocumentServicesTests()
    {
        paths = new ShiyinPaths(home.Path);
        paths.EnsureDirectories();
        db = new AppDatabase(paths.Database, paths.Backups, paths.Documents);
        db.SeedTemplates();
    }

    public void Dispose()
    {
        db.Dispose();
        home.Dispose();
    }

    /// <summary>假的文字辨識：記下被叫了幾次</summary>
    sealed class FakeOcr(string? text) : IDocumentOcr
    {
        public int Calls;
        public string? RecognizeImage(string path, MeetingLanguage language) { Calls++; return text; }
        public string? RecognizePdfPage(string path, int pageIndex, MeetingLanguage language) { Calls++; return text is null ? null : $"{text} {pageIndex + 1}"; }
    }

    long NewMeeting() => db.InsertMeeting(new Meeting { MeetingDate = "2026-11-02", SourceFilename = "a.m4a" }, null).Id!.Value;

    [Fact]
    public void ReadsPdfWithText()
    {
        var ocr = new FakeOcr("不該用到");
        var r = DocumentFiles.Read(Path.Combine(Samples, "報價單.pdf"), MeetingLanguage.Zh, ocr);
        Assert.Equal(DocumentKind.Pdf, r.Kind);
        Assert.Equal(3, r.Pages.Count);
        Assert.Contains("480,000", r.Pages[0]);
        Assert.Contains("報價單", r.Pages[0]);
        // 蘋方輸出的 PDF：PdfPig 取出來是康熙部首（⾳、⼀），換回一般的漢字
        Assert.Contains("拾音企業版", r.Pages[0]);
        Assert.Contains("第一期", r.Pages[0]);
        Assert.DoesNotContain(r.Pages, p => p.Any(c => c is >= '\u2F00' and <= '\u2FDF'));
        Assert.Contains("18%", r.Pages[1]);
        Assert.Contains("L40S", r.Pages[2]);
        Assert.False(r.Ocr);
        Assert.Equal(0, ocr.Calls);
    }

    [Fact]
    public void ScannedPdfAndImagesUseOcr()
    {
        var ocr = new FakeOcr("會議議程");
        var scanned = DocumentFiles.Read(Path.Combine(Samples, "掃描議程.pdf"), MeetingLanguage.Zh, ocr);
        Assert.Equal(["會議議程 1", "會議議程 2"], scanned.Pages);
        Assert.True(scanned.Ocr);
        var image = DocumentFiles.Read(Path.Combine(Samples, "白板.png"), MeetingLanguage.Zh, new FakeOcr("白板：Q4 目標"));
        Assert.Equal((DocumentKind.Image, true), (image.Kind, image.Ocr));
        Assert.Equal(["白板：Q4 目標"], image.Pages);
        // 這台電腦沒有可以用的辨識語言：說明怎麼加
        var e = Assert.Throws<DocumentError>(() => DocumentFiles.Read(Path.Combine(Samples, "掃描議程.pdf"), MeetingLanguage.Zh, new FakeOcr(null)));
        Assert.Contains("文字辨識", e.Message);
        Assert.Throws<DocumentError>(() => DocumentFiles.Read(Path.Combine(Samples, "白板.png"), MeetingLanguage.Zh, null));
    }

    [Fact]
    public void LockedAndBrokenPdf()
    {
        Assert.Equal("locked", Assert.Throws<DocumentError>(() => DocumentFiles.Read(Path.Combine(Samples, "locked.pdf"), MeetingLanguage.Zh, null)).Code);
        var broken = Path.Combine(home.Path, "壞的.pdf");
        File.WriteAllText(broken, "not a pdf");
        Assert.Equal("unreadable", Assert.Throws<DocumentError>(() => DocumentFiles.Read(broken, MeetingLanguage.Zh, null)).Code);
    }

    [Fact]
    public void StoreCopiesRejectsDuplicatesAndDeletes()
    {
        var id = NewMeeting();
        var document = DocumentStore.Add(Path.Combine(Samples, "規格書.docx"), id, MeetingLanguage.Zh, db, paths, null);
        Assert.Equal($"{document.Id}/規格書.docx", document.File);
        Assert.Equal(File.ReadAllBytes(Path.Combine(Samples, "規格書.docx")), File.ReadAllBytes(paths.DocumentPath(document)));
        Assert.Equal("duplicate", Assert.Throws<DocumentError>(() =>
            DocumentStore.Add(Path.Combine(Samples, "規格書.docx"), id, MeetingLanguage.Zh, db, paths, null)).Code);
        var second = DocumentStore.Add(Path.Combine(Samples, "簡報.pptx"), id, MeetingLanguage.Zh, db, paths, null);
        Assert.Equal(4, second.PageTexts.Count);
        // 移除一份：資料夾跟著刪
        db.DeleteDocument(second.Id!.Value);
        Assert.False(Directory.Exists(Path.Combine(paths.Documents, second.Id!.Value.ToString())));
        // 永久刪除會議：文件的資料夾也刪
        db.DeleteMeeting(id);
        Assert.False(Directory.Exists(Path.Combine(paths.Documents, document.Id!.Value.ToString())));
        Assert.Empty(db.Documents(id));
    }

    [Fact]
    public void PackageRoundTripWithDocuments()
    {
        var id = NewMeeting();
        DocumentStore.Add(Path.Combine(Samples, "報價單.pdf"), id, MeetingLanguage.Zh, db, paths, null);
        DocumentStore.Add(Path.Combine(Samples, "議程.md"), id, MeetingLanguage.Zh, db, paths, null);
        var meeting = db.Meeting(id)!;
        var bundle = new ExportBundle(meeting, db.Segments(id), new Dictionary<int, string>(), db.Bookmarks(id), null) { Documents = db.Documents(id) };
        var file = Path.Combine(home.Path, "有文件.shiyin");
        MeetingPackage.Export(bundle, file, paths, "4.3.0");
        using (var zip = ZipFile.OpenRead(file))
            Assert.Equal(["documents.json", "documents/1-報價單.pdf", "documents/2-議程.md", "manifest.json", "meeting.json", "transcript.json"],
                zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        var imported = MeetingPackage.Import(file, db, paths);
        var documents = db.Documents(imported.Id!.Value);
        Assert.Equal(["報價單.pdf", "議程.md"], documents.Select(d => d.Name));
        Assert.Equal(db.Documents(id).Select(d => d.Pages), documents.Select(d => d.Pages));
        Assert.Equal(File.ReadAllBytes(Path.Combine(Samples, "報價單.pdf")), File.ReadAllBytes(paths.DocumentPath(documents[0])));
    }

    /// <summary>Mac 的 DEBUG 版匯出的封包（有兩份 PDF，一份是文字辨識讀的）：Windows 匯入後文件、頁數、原始檔都在（R28.3）</summary>
    [Fact]
    public void ImportsMacPackageWithDocuments()
    {
        var imported = MeetingPackage.Import(Path.Combine(Samples, "mac-package.shiyin"), db, paths);
        var documents = db.Documents(imported.Id!.Value);
        Assert.Equal([("報價單.pdf", 3, false), ("掃描議程.pdf", 2, true)], documents.Select(d => (d.Name, d.PageTexts.Count, d.Ocr)));
        Assert.All(documents, d => Assert.True(new FileInfo(paths.DocumentPath(d)).Length > 1000));
        Assert.StartsWith("會議議程", documents[1].PageTexts[0]);
    }

    [Fact]
    public void PackageRejectsBadDocuments()
    {
        var file = Path.Combine(home.Path, "bad.shiyin");
        const string meeting = """{"title":"t","titleLocked":false,"meetingDate":"2026-01-01","participants":"","sourceFilename":"x","templateId":"general","userNotes":"","channels":"mic","speakers":[],"bookmarks":[]}""";
        const string manifest = """{"format":1,"app":"9","exportedAt":"x"}""";
        var before = db.MeetingRows().Count;
        void Rejects(string documents, Dictionary<string, string>? files = null)
        {
            File.Delete(file);
            using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
            {
                foreach (var (name, text) in new Dictionary<string, string>(files ?? []) { ["manifest.json"] = manifest, ["meeting.json"] = meeting, ["documents.json"] = documents })
                {
                    using var s = zip.CreateEntry(name).Open();
                    s.Write(Encoding.UTF8.GetBytes(text));
                }
            }
            var e = Assert.Throws<MeetingPackage.PackageException>(() => MeetingPackage.Import(file, db, paths));
            Assert.Contains("文件", e.Message);
        }
        Rejects("[{");
        Rejects("""[{"name":"a.pdf","file":"documents/../manifest.json","kind":"pdf","pages":["x"],"ocr":false}]""");
        Rejects("""[{"name":"a.pdf","file":"documents/1-a.pdf","kind":"pdf","pages":["x"],"ocr":false}]""");   // 檔案不在
        Rejects("""[{"name":"a.pdf","file":"documents/1-a.pdf","kind":"exe","pages":["x"],"ocr":false}]""", new() { ["documents/1-a.pdf"] = "x" });
        Assert.Equal(before, db.MeetingRows().Count);
    }
}
