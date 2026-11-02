using System.IO.Compression;
using Shiyin.Data;
using Shiyin.IO;
using Shiyin.Localization;
using Shiyin.Model;
using Shiyin.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;

namespace Shiyin.Services;

/// <summary>文字辨識（design-windows §11.8）：Platform 用 Windows.Media.Ocr 實作。回傳 null 表示這台電腦沒有可以用的辨識語言</summary>
public interface IDocumentOcr
{
    string? RecognizeImage(string path, MeetingLanguage language);
    /// <param name="pageIndex">從 0 算</param>
    string? RecognizePdfPage(string path, int pageIndex, MeetingLanguage language);
}

/// <summary>讀出參考文件的文字（M§39.2）：PDF 用 PdfPig，掃描的頁面和圖片交給 IDocumentOcr；其他格式在 Core 的 DocumentReader</summary>
public static class DocumentFiles
{
    public static DocumentReader.Result Read(string path, MeetingLanguage language, IDocumentOcr? ocr) =>
        DocumentReader.Read(path, (p, kind) => kind == DocumentKind.Pdf ? ReadPdf(p, language, ocr) : ReadImage(p, language, ocr));

    static (List<string>, bool) ReadPdf(string path, MeetingLanguage language, IDocumentOcr? ocr)
    {
        var name = Path.GetFileName(path);
        PdfDocument pdf;
        try { pdf = PdfDocument.Open(path); }
        catch (PdfDocumentEncryptedException) { throw DocumentError.Locked(name); }
        catch (Exception e) when (e is not OutOfMemoryException) { throw DocumentError.Unreadable(name); }
        using (pdf)
        {
            var pages = new List<string>();
            var recognized = 0;
            var used = false;
            var missing = false;
            for (var number = 1; number <= pdf.NumberOfPages; number++)
            {
                string text;
                try { text = DocumentReader.Normalize(ContentOrderTextExtractor.GetText(pdf.GetPage(number))); }
                catch (Exception e) when (e is not OutOfMemoryException) { text = ""; }
                if (text.Count(c => !char.IsWhiteSpace(c)) < DocumentReader.ScannedPageChars && recognized < DocumentReader.MaxOcrPages)
                {
                    recognized++;
                    if (ocr?.RecognizePdfPage(path, number - 1, language) is { } result)
                    {
                        var recognizedText = DocumentReader.Normalize(result);
                        if (SwiftText.Count(recognizedText) > SwiftText.Count(text))
                        {
                            text = recognizedText;
                            used = true;
                        }
                    }
                    else missing = true;
                }
                pages.Add(text);
            }
            if (missing && pages.All(p => p.Length == 0)) throw NoOcr(name, language);
            return (pages, used);
        }
    }

    static (List<string>, bool) ReadImage(string path, MeetingLanguage language, IDocumentOcr? ocr)
    {
        var text = ocr?.RecognizeImage(path, language) ?? throw NoOcr(Path.GetFileName(path), language);
        return ([text], true);
    }

    /// <summary>這台電腦沒有這個語言的文字辨識（Windows 才有的說明）</summary>
    static DocumentError NoOcr(string name, MeetingLanguage language) => new("empty",
        L.F("「%@」裡讀不到文字。這台電腦沒有%@的文字辨識：到「設定 → 時間與語言 → 語言與地區」加入%@再試。", name, language.Name(), language.Name()));
}

/// <summary>存參考文件：讀出文字、寫資料庫、複製原始檔（M§39.1、§39.4）；封包匯入也用</summary>
public static class DocumentStore
{
    public static MeetingDocument Add(string path, long meetingId, MeetingLanguage language, AppDatabase db, ShiyinPaths paths, IDocumentOcr? ocr)
    {
        var result = DocumentFiles.Read(path, language, ocr);
        // 同一份（檔名和讀出來的文字都一樣）加過了就不再加：送給整理工具會重複
        var name = DocumentReader.SafeName(Path.GetFileName(path));
        if (db.Documents(meetingId).Any(d => d.Name == name && d.PageTexts.SequenceEqual(result.Pages))) throw DocumentError.Duplicate(name);
        return Save(path, name, result.Kind, result.Pages, result.Ocr, meetingId, db, paths);
    }

    /// <summary>寫資料庫（拿到 id）再複製原始檔到 documents\&lt;id&gt;\&lt;檔名&gt;；複製失敗就把那一列刪掉</summary>
    public static MeetingDocument Save(string source, string name, DocumentKind kind, IReadOnlyList<string> pages, bool ocr,
                                       long meetingId, AppDatabase db, ShiyinPaths paths)
    {
        var document = db.AddDocument(meetingId, DocumentReader.SafeName(name), kind, pages, ocr);
        try
        {
            var destination = paths.DocumentPath(document);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            db.DeleteDocument(document.Id!.Value);
            throw;
        }
        return document;
    }

    /// <summary>從封包的 zip 項目存（不先解到暫存檔）</summary>
    public static MeetingDocument Save(ZipArchiveEntry source, string name, DocumentKind kind, IReadOnlyList<string> pages,
                                       bool ocr, long meetingId, AppDatabase db, ShiyinPaths paths)
    {
        var document = db.AddDocument(meetingId, DocumentReader.SafeName(name), kind, pages, ocr);
        try
        {
            var destination = paths.DocumentPath(document);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            source.ExtractToFile(destination, overwrite: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            db.DeleteDocument(document.Id!.Value);
            throw;
        }
        return document;
    }
}
