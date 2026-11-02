using Shiyin.Model;

namespace Shiyin.Data;

/// <summary>會議的參考文件（M§39.1、R61）</summary>
public sealed partial class AppDatabase
{
    public List<MeetingDocument> Documents(long meetingId) =>
        Read(db => db.Query("SELECT * FROM documents WHERE meeting_id = ? ORDER BY id", meetingId).Select(ToDocument).ToList());

    public MeetingDocument? Document(long id) =>
        Read(db => db.Query("SELECT * FROM documents WHERE id = ?", id).Select(ToDocument).FirstOrDefault());

    /// <summary>先插入、再用 id 填 file（&lt;id&gt;/&lt;檔名&gt;），同一個交易</summary>
    public MeetingDocument AddDocument(long meetingId, string name, DocumentKind kind, IReadOnlyList<string> pages, bool ocr) => Write(db =>
    {
        var document = new MeetingDocument
        {
            MeetingId = meetingId, Name = name, Kind = kind.Raw(), Pages = MeetingDocument.EncodePages(pages), Ocr = ocr,
        };
        db.Execute("INSERT INTO documents (meeting_id, name, file, kind, pages, ocr, created_at) VALUES (?, ?, ?, ?, ?, ?, ?)",
            document.MeetingId, document.Name, "", document.Kind, document.Pages, document.Ocr, document.CreatedAt);
        document.Id = db.LastInsertRowId;
        document.File = $"{document.Id}/{name}";
        db.Execute("UPDATE documents SET file = ? WHERE id = ?", document.File, document.Id);
        return document;
    });

    /// <summary>刪掉一份文件，連同原始檔；回傳刪掉的那筆</summary>
    public MeetingDocument? DeleteDocument(long id)
    {
        var document = Write(db =>
        {
            var row = db.Query("SELECT * FROM documents WHERE id = ?", id).Select(ToDocument).FirstOrDefault();
            if (row is not null) db.Execute("DELETE FROM documents WHERE id = ?", id);
            return row;
        });
        if (document is not null) RemoveDocumentFile(document.File);
        return document;
    }

    /// <summary>原始檔放在 documents/&lt;id&gt;/ 底下，整個資料夾刪掉。DocumentsRoot 是 null 時不碰檔案</summary>
    void RemoveDocumentFile(string file)
    {
        if (DocumentsRoot is null) return;
        var folder = file.Split('/')[0];
        if (!long.TryParse(folder, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _)) return;
        try { Directory.Delete(System.IO.Path.Combine(DocumentsRoot, folder), recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    internal static MeetingDocument ToDocument(Row r) => new()
    {
        Id = r.Long("id"), MeetingId = r.Long("meeting_id"), Name = r.Str("name"), File = r.Str("file"), Kind = r.Str("kind"),
        Pages = r.Str("pages"), Ocr = r.Bool("ocr"), CreatedAt = r.Str("created_at"),
    };
}
