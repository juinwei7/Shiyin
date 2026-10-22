using System.Net;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Shiyin.Model;

namespace Shiyin.Services.Export;

/// <summary>
/// Word 匯出（R7.1、design-windows §13）：把 Core 匯出的 HTML（標籤固定：h1～h3、p、ul/li、table、b、span.time、p.meta）
/// 轉成 Word 原生的段落、清單、表格（不用 altChunk 夾帶 HTML）。字型、顏色、字級照 HTML 的樣式表；
/// 待辦表格的負責人、期限欄不拆行，內容欄自己換行（同 Mac 的 fitShortColumns）
/// </summary>
public static partial class DocxWriter
{
    const string Text = "1E2625", Muted = "68726F", Accent = "2D6A5D", Rule = "DCE1DE";

    public static void Write(string html, string path, MeetingLanguage language)
    {
        var (body, heading) = Fonts(language);
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        AddStyles(main, body, heading, language);
        var numbering = AddNumbering(main);
        var content = new Body();
        foreach (var block in Parse(BodyOf(html))) content.Append(block.ToOpenXml(numbering));
        // A4、邊界 56 pt（同 PDF，design-windows §13）
        content.Append(new SectionProperties(
            new PageSize { Width = 11906, Height = 16838 },
            new PageMargin { Top = 1120, Bottom = 1120, Left = 1120, Right = 1120, Header = 708, Footer = 708, Gutter = 0 }));
        main.Document = new Document(content);
        main.Document.Save();
    }

    /// <summary>匯出用的第一個字型（Windows 的那一組，design-windows §11.3）</summary>
    static (string Body, string Heading) Fonts(MeetingLanguage language) => language switch
    {
        MeetingLanguage.En => ("Segoe UI", "Georgia"),
        MeetingLanguage.Ja => ("Yu Gothic", "Yu Mincho"),
        _ => ("Microsoft JhengHei", "PMingLiU"),
    };

    static string BodyOf(string html)
    {
        var start = html.IndexOf("<body>", StringComparison.Ordinal);
        var end = html.LastIndexOf("</body>", StringComparison.Ordinal);
        return start >= 0 && end > start ? html[(start + 6)..end] : html;
    }

    // MARK: 樣式

    static void AddStyles(MainDocumentPart main, string body, string heading, MeetingLanguage language)
    {
        var lang = language.Text("zh-TW", "en-US", "ja-JP");
        RunProperties Run(string font, int halfPoints, string color, bool bold = false)
        {
            // 子元素的順序要照 Word 的 schema：rFonts、b、color、sz、lang
            var r = new RunProperties(new RunFonts { Ascii = font, HighAnsi = font, EastAsia = font, ComplexScript = font });
            if (bold) r.Append(new Bold());
            r.Append(new Color { Val = color }, new FontSize { Val = halfPoints.ToString() }, new Languages { Val = lang, EastAsia = lang });
            return r;
        }
        Style ParagraphStyle(string id, string name, RunProperties run, int before, int after, bool keepNext = false)
        {
            var p = new StyleParagraphProperties(new SpacingBetweenLines { Before = before.ToString(), After = after.ToString(), Line = "336", LineRule = LineSpacingRuleValues.Auto });
            if (keepNext) p.PrependChild(new KeepNext());
            return new Style(new StyleName { Val = name }, new PrimaryStyle(), p, new StyleRunProperties(run.Elements().Select(e => e.CloneNode(true))))
                { Type = StyleValues.Paragraph, StyleId = id };
        }
        var styles = new Styles(
            new DocDefaults(new RunPropertiesDefault(Run(body, 24, Text)), new ParagraphPropertiesDefault()),
            ParagraphStyle("Normal", "Normal", Run(body, 24, Text), 0, 80),
            ParagraphStyle("Heading1", "heading 1", Run(heading, 44, Text, true), 0, 80, true),
            ParagraphStyle("Heading2", "heading 2", Run(heading, 30, Text, true), 360, 80, true),
            ParagraphStyle("Heading3", "heading 3", Run(heading, 26, Text, true), 200, 60, true),
            ParagraphStyle("Meta", "Meta", Run(body, 20, Muted), 0, 160));
        main.AddNewPart<StyleDefinitionsPart>().Styles = styles;
    }

    static int AddNumbering(MainDocumentPart main)
    {
        var abstractNum = new AbstractNum(new Level(
            new NumberingFormat { Val = NumberFormatValues.Bullet }, new LevelText { Val = "•" }, new LevelJustification { Val = LevelJustificationValues.Left },
            new PreviousParagraphProperties(new Indentation { Left = "360", Hanging = "240" })) { LevelIndex = 0 }) { AbstractNumberId = 1 };
        main.AddNewPart<NumberingDefinitionsPart>().Numbering = new Numbering(abstractNum, new NumberingInstance(new AbstractNumId { Val = 1 }) { NumberID = 1 });
        return 1;
    }

    // MARK: 解析（只認匯出用的這幾種標籤）

    abstract record Block
    {
        public abstract OpenXmlElement ToOpenXml(int numbering);
    }

    sealed record Inline(string Text, bool Bold, bool Time);

    sealed record Para(string? Style, List<Inline> Runs) : Block
    {
        public override OpenXmlElement ToOpenXml(int numbering) => Paragraph(Style, Runs, null);
    }

    sealed record Bullet(List<Inline> Runs) : Block
    {
        public override OpenXmlElement ToOpenXml(int numbering) => Paragraph(null, Runs, numbering);
    }

    sealed record Grid(List<List<(List<Inline> Runs, bool Header, bool NoWrap)>> Rows) : Block
    {
        public override OpenXmlElement ToOpenXml(int numbering)
        {
            // 子元素的順序要照 Word 的 schema：tblW、tblBorders、tblLayout、tblCellMar；表格一定要有 tblGrid
            var table = new Table(new TableProperties(
                new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct },
                new TableBorders(new BottomBorder { Val = BorderValues.Single, Size = 4, Color = Rule },
                                 new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4, Color = Rule }),
                new TableLayout { Type = TableLayoutValues.Autofit },
                new TableCellMarginDefault(new TopMargin { Width = "80", Type = TableWidthUnitValues.Dxa },
                    new TableCellLeftMargin { Width = 120, Type = TableWidthValues.Dxa }, new BottomMargin { Width = "80", Type = TableWidthUnitValues.Dxa },
                    new TableCellRightMargin { Width = 120, Type = TableWidthValues.Dxa })));
            var columns = Rows.Count == 0 ? 1 : Rows.Max(r => r.Count);
            table.Append(new TableGrid(Enumerable.Range(0, columns).Select(_ => new GridColumn { Width = (9666 / columns).ToString() })));
            foreach (var row in Rows)
            {
                var tr = new TableRow();
                if (row.Any(c => c.Header)) tr.Append(new TableRowProperties(new TableHeader()));
                foreach (var cell in row)
                {
                    var props = new TableCellProperties();
                    // 短欄照內容的寬度、不拆行；內容欄拿剩下的寬度
                    props.Append(cell.NoWrap ? new TableCellWidth { Type = TableWidthUnitValues.Auto } : new TableCellWidth { Width = "5000", Type = TableWidthUnitValues.Pct });
                    if (cell.NoWrap) props.Append(new NoWrap());
                    var p = Paragraph(null, cell.Runs, null, header: cell.Header);
                    tr.Append(new TableCell(props, p));
                }
                table.Append(tr);
            }
            return table;
        }
    }

    static Paragraph Paragraph(string? style, List<Inline> runs, int? numbering, bool header = false)
    {
        var p = new Paragraph();
        var props = new ParagraphProperties();
        if (style is not null) props.Append(new ParagraphStyleId { Val = style });
        if (numbering is int n) props.Append(new NumberingProperties(new NumberingLevelReference { Val = 0 }, new NumberingId { Val = n }));
        if (header || numbering is not null) props.Append(new SpacingBetweenLines { After = "40" });
        if (props.HasChildren) p.Append(props);
        foreach (var r in runs)
        {
            // 換行（逐字稿、待辦的內容可能有）
            var lines = r.Text.Split('\n');
            var run = new Run();
            var rp = new RunProperties();
            if (r.Bold) rp.Append(new Bold());
            if (r.Time) { rp.Append(new Color { Val = Accent }); rp.Append(new FontSize { Val = "19" }); }
            if (header) { rp.Append(new Color { Val = Muted }); rp.Append(new FontSize { Val = "20" }); }
            if (rp.HasChildren) run.Append(rp);
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0) run.Append(new Break());
                run.Append(new DocumentFormat.OpenXml.Wordprocessing.Text(lines[i]) { Space = SpaceProcessingModeValues.Preserve });
            }
            p.Append(run);
        }
        return p;
    }

    [GeneratedRegex(@"<(/?)([a-z0-9]+)((?:\s+[a-z-]+=""[^""]*"")*)\s*/?>|([^<]+)", RegexOptions.Singleline)]
    private static partial Regex Token();

    static List<Block> Parse(string html)
    {
        var blocks = new List<Block>();
        List<Inline>? runs = null;
        string? style = null;
        var inList = false;
        List<List<(List<Inline>, bool, bool)>>? rows = null;
        List<(List<Inline>, bool, bool)>? row = null;
        (bool Header, bool NoWrap) cell = default;
        var bold = 0;
        var time = 0;
        foreach (Match m in Token().Matches(html))
        {
            if (m.Groups[4].Success)
            {
                var text = WebUtility.HtmlDecode(m.Groups[4].Value);
                if (runs is null)
                {
                    if (text.Trim().Length == 0) continue;
                    runs = [];
                    style = null;
                }
                runs.Add(new Inline(text, bold > 0, time > 0));
                continue;
            }
            var closing = m.Groups[1].Value == "/";
            var tag = m.Groups[2].Value;
            var attributes = m.Groups[3].Value;
            switch (tag, closing)
            {
                case ("h1" or "h2" or "h3", false): runs = []; style = "Heading" + tag[1]; break;
                case ("p", false): runs = []; style = attributes.Contains("meta") ? "Meta" : null; break;
                case ("li", false): runs = []; break;
                case ("ul", false): inList = true; break;
                case ("ul", true): inList = false; break;
                case ("table", false): rows = []; break;
                case ("table", true): if (rows is not null) blocks.Add(new Grid(rows)); rows = null; break;
                case ("tr", false): row = []; break;
                case ("tr", true): if (row is not null) rows?.Add(row); row = null; break;
                case ("th" or "td", false): runs = []; cell = (tag == "th", attributes.Contains("nowrap")); break;
                case ("th" or "td", true): row?.Add((runs ?? [], cell.Header, cell.NoWrap)); runs = null; break;
                case ("b" or "strong", false): bold++; break;
                case ("b" or "strong", true): bold = Math.Max(0, bold - 1); break;
                case ("span", false): if (attributes.Contains("time")) time++; break;
                case ("span", true): time = Math.Max(0, time - 1); break;
                case ("br", false): runs?.Add(new Inline("\n", false, false)); break;
                case ("h1" or "h2" or "h3" or "p" or "li", true):
                    if (runs is not null && rows is null)
                        blocks.Add(tag == "li" && inList ? new Bullet(Trim(runs)) : new Para(style, Trim(runs)));
                    runs = null;
                    break;
            }
        }
        return blocks;
    }

    /// <summary>標籤之間的換行、空白不算內容</summary>
    static List<Inline> Trim(List<Inline> runs)
    {
        if (runs.Count == 0) return runs;
        runs[0] = runs[0] with { Text = runs[0].Text.TrimStart('\n') };
        runs[^1] = runs[^1] with { Text = runs[^1].Text.TrimEnd('\n') };
        return runs;
    }
}
