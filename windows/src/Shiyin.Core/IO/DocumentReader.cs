using System.IO.Compression;
using System.Text;
using System.Xml;
using Shiyin.Localization;
using Shiyin.Model;

namespace Shiyin.IO;

/// <summary>讀不了文件的原因（M§39.2）：Code 給標準答案比，訊息用 Mac 的同一句中文查表</summary>
public sealed class DocumentError(string code, string message) : Exception(message)
{
    public string Code { get; } = code;

    public static DocumentError Unsupported(string ext) => new("unsupported:" + ext, ext switch
    {
        "key" => L.T("拾音讀不了 Keynote 檔。請先在 Keynote 匯出成 PDF 再加入。"),
        "pages" => L.T("拾音讀不了 Pages 檔。請先在 Pages 匯出成 PDF 再加入。"),
        "numbers" => L.T("拾音讀不了 Numbers 檔。請先在 Numbers 匯出成 PDF 再加入。"),
        "doc" => L.T("拾音讀不了舊版的 Word 檔（.doc）。請另存成 docx 或 PDF 再加入。"),
        "" => L.T("拾音讀不了沒有副檔名的檔案。請先匯出成 PDF 再加入。"),
        _ => L.F("拾音讀不了 .%@ 檔。請先匯出成 PDF 再加入。", ext),
    });

    public static DocumentError TooLarge(string name) => new("tooLarge", L.F("「%@」超過 200 MB，拾音不讀這麼大的文件。", name));
    public static DocumentError Locked(string name) => new("locked", L.F("「%@」有密碼保護，拾音讀不了。請先移除密碼再加入。", name));
    public static DocumentError Unreadable(string name) => new("unreadable", L.F("「%@」讀不出來，檔案可能壞了。", name));
    public static DocumentError Empty(string name) => new("empty", L.F("「%@」裡讀不到文字。", name));
    public static DocumentError Encoding(string name) => new("encoding", L.F("「%@」不是 UTF-8 的文字檔。請另存成 UTF-8 再加入。", name));
    public static DocumentError Duplicate(string name) => new("duplicate", L.F("「%@」已經加過了", name));
}

/// <summary>
/// 參考文件讀出文字的共同部分（M§39.2、R61.2）：種類、整理文字、純文字、docx／pptx。規則跟 Mac 的 DocumentReader、OfficeText 一樣，
/// 標準答案比對。PDF（PdfPig）和文字辨識（Windows.Media.Ocr）在 Services、Platform。
/// </summary>
public static class DocumentReader
{
    public const long MaxBytes = 200L * 1024 * 1024;
    /// <summary>掃描的頁面最多辨識幾頁（R61.7）</summary>
    public const int MaxOcrPages = 50;
    /// <summary>一頁的文字（不算空白）少於這個數就當成掃描的頁面</summary>
    public const int ScannedPageChars = 20;

    public sealed record Result(DocumentKind Kind, List<string> Pages, bool Ocr);

    /// <summary>副檔名（小寫）：同 Mac 的 URL.pathExtension（跟 NSString 不同的地方：點前面只有點的「..pdf」沒有副檔名）</summary>
    public static string ExtensionOf(string path)
    {
        var name = PathText.LastComponent(path.Replace('\\', '/'));
        var dot = name.LastIndexOf('.');
        if (dot <= 0 || name[..dot].All(c => c == '.')) return "";
        return name[(dot + 1)..].ToLowerInvariant();
    }

    public static DocumentKind? KindOf(string path) => ExtensionOf(path) switch
    {
        "pdf" => DocumentKind.Pdf,
        "docx" => DocumentKind.Docx,
        "pptx" => DocumentKind.Pptx,
        "txt" or "md" or "markdown" => DocumentKind.Text,
        "png" or "jpg" or "jpeg" or "heic" => DocumentKind.Image,
        _ => null,
    };

    /// <summary>拖進主視窗時當成文件的（R61.3）：讀得了的，加上看得出是文件、要說明怎麼辦的。txt、md 照舊是逐字稿、筆記</summary>
    public static bool IsDroppedDocument(string path) => ExtensionOf(path) is
        "pdf" or "docx" or "pptx" or "png" or "jpg" or "jpeg" or "heic" or
        "key" or "pages" or "numbers" or "doc" or "rtf" or "ppt" or "xls" or "xlsx";

    /// <summary>「加入文件…」選檔的副檔名</summary>
    public static readonly string[] PickerExtensions = [".pdf", ".docx", ".pptx", ".txt", ".md", ".markdown", ".png", ".jpg", ".jpeg", ".heic"];

    // MARK: 整理文字（同 Mac）

    static readonly char[] Blank = [' ', '\t', ' ', '　'];
    static readonly char[] BlankOrNewline = [' ', '\t', ' ', '　', '\n'];

    /// <summary>換行統一成 \n、每行去掉行尾的空白、連續的空行只留一行、整頁去掉頭尾的空白和換行</summary>
    public static string Normalize(string text)
    {
        var unified = text.Replace("\r\n", "\n").Replace("\r", "\n");
        var lines = new List<string>();
        var blankLines = 0;
        foreach (var raw in unified.Split('\n'))
        {
            var line = raw.TrimEnd(Blank);
            if (line.Length == 0)
            {
                blankLines++;
                if (blankLines > 1) continue;
            }
            else blankLines = 0;
            lines.Add(line);
        }
        return string.Join("\n", lines).Trim(BlankOrNewline);
    }

    /// <summary>
    /// 讀出文字（M§39.2）：先看種類、再看大小，順序同 Mac。PDF、圖片交給 media（Services 的 PdfPig 和文字辨識），
    /// 回傳每頁的文字（還沒整理）和有沒有用文字辨識；沒有 media 時當成讀不出來
    /// </summary>
    public static Result Read(string path, Func<string, DocumentKind, (List<string> Pages, bool Ocr)>? media = null)
    {
        var name = System.IO.Path.GetFileName(path);
        var kind = KindOf(path) ?? throw DocumentError.Unsupported(ExtensionOf(path));
        if (new FileInfo(path).Length > MaxBytes) throw DocumentError.TooLarge(name);
        List<string> raw;
        var ocr = false;
        if (kind is DocumentKind.Pdf or DocumentKind.Image)
        {
            if (media is null) throw DocumentError.Unreadable(name);
            (raw, ocr) = media(path, kind);
        }
        else raw = ReadPlain(path, kind);
        return new Result(kind, Finish(raw, name), ocr);
    }

    /// <summary>
    /// 存起來的檔名（同 Mac 的 DocumentStore.safeName）：只取最後一段、去掉開頭的「.」，是空的就叫 document。
    /// Windows 另外把檔名不能用的字元換成「-」、去掉結尾的空白和「.」、避開 CON、NUL 這些保留的名稱
    /// </summary>
    public static string SafeName(string name)
    {
        var last = PathText.LastComponent(name).TrimStart('.');
        var sb = new StringBuilder(last.Length);
        foreach (var c in last) sb.Append(c < 0x20 || "<>:\"/\\|?*".Contains(c) ? '-' : c);
        var safe = sb.ToString().TrimEnd(' ', '.');
        if (safe.Length == 0) return "document";
        var stem = safe.Split('.')[0].ToUpperInvariant();
        return Reserved.Contains(stem) ? "_" + safe : safe;
    }

    static readonly HashSet<string> Reserved = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    /// <summary>docx、pptx、純文字（PDF、圖片在 Services）。pages 還沒整理</summary>
    public static List<string> ReadPlain(string path, DocumentKind kind)
    {
        var name = System.IO.Path.GetFileName(path);
        return kind switch
        {
            DocumentKind.Docx => [OfficeText.Docx(path, name)],
            DocumentKind.Pptx => OfficeText.Pptx(path, name),
            DocumentKind.Text => [DecodeText(File.ReadAllBytes(path), name)],
            _ => throw DocumentError.Unreadable(name),
        };
    }

    /// <summary>開頭是 UTF-8 的 BOM 就去掉；UTF-16 的 BOM 用 UTF-16；其他照 UTF-8 嚴格解碼</summary>
    public static string DecodeText(byte[] data, string name)
    {
        try
        {
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
                return new UTF8Encoding(false, true).GetString(data, 3, data.Length - 3);
            if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
                return new UnicodeEncoding(false, false, true).GetString(data, 2, data.Length - 2);
            if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
                return new UnicodeEncoding(true, false, true).GetString(data, 2, data.Length - 2);
            return new UTF8Encoding(false, true).GetString(data);
        }
        catch (DecoderFallbackException) { throw DocumentError.Encoding(name); }
        catch (ArgumentException) { throw DocumentError.Encoding(name); }
    }

    /// <summary>每一頁整理過、至少一頁有字；不然丟出「讀不到文字」</summary>
    public static List<string> Finish(IEnumerable<string> raw, string name)
    {
        var pages = raw.Select(Normalize).ToList();
        if (!pages.Any(p => p.Length > 0)) throw DocumentError.Empty(name);
        return pages;
    }
}

/// <summary>docx、pptx 的文字（M§39.2、D136）：只解出要讀的 XML，同一套規則讀 Word 的段落、表格和投影片</summary>
public static class OfficeText
{
    /// <summary>一個 XML 檔解壓後最多多大：防止壓縮炸彈</summary>
    public const long MaxEntryBytes = 64L * 1024 * 1024;

    public static string Docx(string path, string name)
    {
        using var zip = Open(path, name);
        var xml = Entry(zip, "word/document.xml") ?? throw DocumentError.Unreadable(name);
        return Text(xml, name);
    }

    public static List<string> Pptx(string path, string name)
    {
        using var zip = Open(path, name);
        var presentation = Entry(zip, "ppt/presentation.xml");
        var rels = Entry(zip, "ppt/_rels/presentation.xml.rels");
        if (presentation is null || rels is null) throw DocumentError.Unreadable(name);
        var targets = Relationships(rels);
        var pages = new List<string>();
        foreach (var id in SlideIds(presentation))
        {
            if (!targets.TryGetValue(id, out var target) || Resolve(target) is not { } entry || Entry(zip, entry) is not { } xml) continue;
            pages.Add(Text(xml, name));
        }
        if (pages.Count == 0) throw DocumentError.Unreadable(name);
        return pages;
    }

    static ZipArchive Open(string path, string name)
    {
        try { return ZipFile.OpenRead(path); }
        catch (Exception e) when (e is InvalidDataException or IOException) { throw DocumentError.Unreadable(name); }
    }

    /// <summary>投影片的 Target 相對於 ppt/；「/」開頭是從根算。有 .. 或奇怪字元的不讀（同 Mac：那邊 unzip 的參數是萬用字元的樣式）</summary>
    public static string? Resolve(string target)
    {
        var path = target.StartsWith('/') ? target[1..] : "ppt/" + target;
        if (target.Length == 0 || path.EndsWith('/')) return null;
        foreach (var c in path)
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '.' or '/' or '-')) return null;
        if (path.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(p => p is ".." or ".")) return null;
        return path;
    }

    /// <summary>zip 裡的一個檔；找不到、壞掉、太大都是 null</summary>
    static byte[]? Entry(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path);
        if (entry is null || entry.Length > MaxEntryBytes) return null;
        try
        {
            using var stream = entry.Open();
            using var memory = new MemoryStream();
            var buffer = new byte[1 << 16];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                memory.Write(buffer, 0, read);
                if (memory.Length > MaxEntryBytes) return null;
            }
            return memory.Length == 0 ? null : memory.ToArray();
        }
        catch (Exception e) when (e is InvalidDataException or IOException) { return null; }
    }

    static XmlReader Reader(byte[] xml) => XmlReader.Create(new MemoryStream(xml), new XmlReaderSettings
    {
        DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreComments = true, IgnoreProcessingInstructions = true,
    });

    /// <summary>段落、表格的規則（M§39.2）：只看元素的本地名稱，Word（w:）和投影片（a:）共用</summary>
    public static string Text(byte[] xml, string name)
    {
        var output = new StringBuilder();
        var stack = new List<string>();
        var collecting = false;
        var fallback = 0;
        var tableDepth = 0;
        var cell = new StringBuilder();
        var row = new List<string>();

        void Emit(string text)
        {
            if (tableDepth > 0) cell.Append(text); else output.Append(text);
        }

        void Start(string element)
        {
            var parent = stack.Count > 0 ? stack[^1] : null;
            stack.Add(element);
            if (element == "Fallback") fallback++;
            if (fallback > 0) return;
            switch (element)
            {
                case "t": collecting = parent is "r" or "fld"; break;
                case "tab": if (parent == "r") Emit("\t"); break;
                case "br": if (parent is "r" or "p") Emit("\n"); break;
                case "cr": if (parent == "r") Emit("\n"); break;
                case "tbl":
                    tableDepth++;
                    if (tableDepth == 1) { row.Clear(); cell.Clear(); }
                    break;
                case "tr": if (tableDepth == 1) row.Clear(); break;
                case "tc": if (tableDepth == 1) cell.Clear(); break;
            }
        }

        void End(string element)
        {
            if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
            if (element == "Fallback")
            {
                fallback = Math.Max(0, fallback - 1);
                return;
            }
            if (fallback > 0) return;
            switch (element)
            {
                case "t": collecting = false; break;
                case "p": Emit(tableDepth > 0 ? " " : "\n"); break;
                case "tc":
                    if (tableDepth == 1)
                    {
                        row.Add(DocumentReader.Normalize(cell.ToString().Replace("\n", " ")));
                        cell.Clear();
                    }
                    break;
                case "tr":
                    if (tableDepth == 1)
                    {
                        output.Append(string.Join(" | ", row)).Append('\n');
                        row.Clear();
                    }
                    break;
                case "tbl": tableDepth = Math.Max(0, tableDepth - 1); break;
            }
        }

        try
        {
            using var reader = Reader(xml);
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        var element = reader.LocalName;
                        Start(element);
                        if (reader.IsEmptyElement) End(element);
                        break;
                    case XmlNodeType.EndElement:
                        End(reader.LocalName);
                        break;
                    case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace:
                        if (collecting && fallback == 0) Emit(reader.Value);
                        break;
                }
            }
        }
        catch (XmlException) { throw DocumentError.Unreadable(name); }
        return output.ToString();
    }

    /// <summary>presentation.xml 的 sldIdLst：每個 sldId 的 r:id，照順序</summary>
    static List<string> SlideIds(byte[] xml)
    {
        var ids = new List<string>();
        try
        {
            using var reader = Reader(xml);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "sldId") continue;
                for (var i = 0; i < reader.AttributeCount; i++)
                {
                    reader.MoveToAttribute(i);
                    if (reader.Name.EndsWith(":id", StringComparison.Ordinal)) { ids.Add(reader.Value); break; }
                }
                reader.MoveToElement();
            }
        }
        catch (XmlException) { }
        return ids;
    }

    /// <summary>presentation.xml.rels：Id → Target（外部連結不算）</summary>
    static Dictionary<string, string> Relationships(byte[] xml)
    {
        var targets = new Dictionary<string, string>();
        try
        {
            using var reader = Reader(xml);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Relationship") continue;
                var id = reader.GetAttribute("Id");
                var target = reader.GetAttribute("Target");
                if (id is null || target is null || reader.GetAttribute("TargetMode") == "External") continue;
                targets[id] = target;
            }
        }
        catch (XmlException) { }
        return targets;
    }
}
