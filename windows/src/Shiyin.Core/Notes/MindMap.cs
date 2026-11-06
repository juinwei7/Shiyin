using System.Globalization;
using System.Text;
using Shiyin.IO;
using Shiyin.Model;
using Shiyin.Text;

namespace Shiyin.Notes;

/// <summary>
/// 會議紀錄畫成心智圖（M§42、R67、design-windows §11.10）：從紀錄組成樹（D143）、排版、SVG。
/// 跟 Mac 的 Notes/MindMap.swift 一字一句同一套規則（文字寬度用估的、浮點運算的順序也一樣），標準答案逐字比（D144）
/// </summary>
public static class MindMap
{
    public sealed record Node(string Text, string Time, List<Node> Children);

    /// <summary>段落名稱和標點：畫面上用介面語言（同條列），匯出用會議語言（同 ExportText）</summary>
    public sealed record Labels(string Summary, string Decisions, string ActionItems, string OpenQuestions, string Topics,
                                string Untitled, string Colon, Func<string, string> Paren)
    {
        public static Labels For(MeetingLanguage language)
        {
            var x = new ExportText(language);
            return new Labels(x.Section("summary"), x.Section("decisions"), x.Section("actionItems"), x.Section("openQuestions"),
                TopicLabel(language), language.Text("會議", "Meeting", "会議"), x.Colon, x.Paren);  // l10n-skip：會議語言的文字
        }
    }

    /// <summary>沒有標題的議題叫什麼</summary>
    public static string TopicLabel(MeetingLanguage language) => language.Text("議題", "Topic", "議題");  // l10n-skip：會議語言的文字

    // MARK: 樹（M§42.1）

    static string Clean(string s) => SwiftText.Trim(s.Replace("\r\n", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal));

    static List<Node> Leaves(List<MeetingNotes.Item> items) =>
        [.. items.Select(i => new Node(Clean(i.Text), Clean(i.Time), [])).Where(n => n.Text.Length > 0)];

    /// <summary>畫成分支的議題：有標題或有重點的（心智圖的前幾支，議題時間軸也用這一份）</summary>
    static List<MeetingNotes.Topic> ShownTopics(MeetingNotes notes) =>
        [.. notes.Topics.Where(t => Clean(t.Title).Length > 0 || t.Points.Any(p => Clean(p.Text).Length > 0))];

    public static Node Tree(MeetingNotes notes, string title, Labels labels)
    {
        var branches = new List<Node>();
        var topics = ShownTopics(notes);
        if (topics.Count == 0)
        {
            var summary = Leaves(notes.Summary);
            if (summary.Count > 0) branches.Add(new Node(labels.Summary, "", summary));
        }
        else
        {
            foreach (var topic in topics)
            {
                var name = Clean(topic.Title);
                branches.Add(new Node(name.Length == 0 ? labels.Topics : name, Clean(topic.Start), Leaves(topic.Points)));
            }
        }
        var decisions = Leaves(notes.Decisions);
        if (decisions.Count > 0) branches.Add(new Node(labels.Decisions, "", decisions));
        var actions = new List<Node>();
        foreach (var a in notes.ActionItems)
        {
            string task = Clean(a.Task), owner = Clean(a.Owner), due = Clean(a.Due);
            if (task.Length == 0) continue;
            var text = owner.Length == 0 ? task : owner + labels.Colon + task;
            if (due.Length > 0) text += labels.Paren(due);
            actions.Add(new Node(text, Clean(a.Time), []));
        }
        if (actions.Count > 0) branches.Add(new Node(labels.ActionItems, "", actions));
        var questions = Leaves(notes.OpenQuestions);
        if (questions.Count > 0) branches.Add(new Node(labels.OpenQuestions, "", questions));
        foreach (var section in notes.Custom)
        {
            var items = Leaves(section.Items);
            if (items.Count == 0) continue;
            var name = Clean(section.Title);
            branches.Add(new Node(name.Length == 0 ? "—" : name, "", items));
        }
        var rootName = Clean(title);
        return new Node(rootName.Length == 0 ? labels.Untitled : rootName, "", branches);
    }

    // MARK: 議題時間軸（M§43.1、R68）

    /// <summary>播放條上的一段議題；Branch 是心智圖的第幾支（顏色一樣）</summary>
    public sealed record TopicRange(int Branch, string Title, double Start, double End);

    /// <summary>讀不到開始時間的議題不畫；依開始時間排序，每段到下一個議題開始，最後一段到會議結束</summary>
    public static List<TopicRange> TopicRanges(MeetingNotes notes, double duration, string untitled)
    {
        if (!(duration > 0)) return [];
        var timed = new List<TopicRange>();
        var topics = ShownTopics(notes);
        for (var i = 0; i < topics.Count; i++)
        {
            if (TimeFormat.Seconds(topics[i].Start) is not { } start) continue;
            var name = Clean(topics[i].Title);
            timed.Add(new TopicRange(i, name.Length == 0 ? untitled : name, start, 0));
        }
        timed = [.. timed.OrderBy(r => r.Start).ThenBy(r => r.Branch)];
        var ranges = new List<TopicRange>();
        for (var k = 0; k < timed.Count; k++)
        {
            var end = Math.Min(k + 1 < timed.Count ? timed[k + 1].Start : duration, duration);
            if (timed[k].Start < end) ranges.Add(timed[k] with { End = end });
        }
        return ranges;
    }

    // MARK: 文字寬度、換行（M§42.2）

    static uint Scalar(string c) => string.IsNullOrEmpty(c) ? 0 : (uint)Rune.GetRuneAt(c, 0).Value;

    /// <summary>一個字元（字形叢集）的寬度（em）：看第一個 scalar</summary>
    public static double Em(string c)
    {
        var s = Scalar(c);
        if (s == 0) return 0;
        if (s == 0x20) return 0.3;
        if (s < 0x80)
        {
            if (s is >= 0x41 and <= 0x5A) return 0.66;
            if (s is >= 0x61 and <= 0x7A or >= 0x30 and <= 0x39) return 0.56;
            return 0.35;
        }
        return IsWide(s) ? 1.0 : 0.6;
    }

    static bool IsWide(uint s) =>
        s is >= 0x1100 and <= 0x115F or >= 0x2E80 and <= 0xA4CF or >= 0xAC00 and <= 0xD7A3 or >= 0xF900 and <= 0xFAFF
            or >= 0xFE30 and <= 0xFE4F or >= 0xFF00 and <= 0xFF60 or >= 0xFFE0 and <= 0xFFE6 or >= 0x1F300 and <= 0x1FAFF
            or >= 0x20000 and <= 0x3FFFD;

    public static double Width(string text, double size)
    {
        var em = 0.0;
        foreach (var c in SwiftText.Characters(text)) em += Em(c);
        return em * size;
    }

    /// <summary>英數連在一起的詞（含詞中的 - . _ / '）不拆；韓文用空格分詞，諺文連在一起也不拆</summary>
    static bool IsWordChar(string c)
    {
        var s = Scalar(c);
        if (s == 0) return false;
        if (s is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or >= 0x61 and <= 0x7A) return true;
        if (s is >= 0xAC00 and <= 0xD7A3 or >= 0x1100 and <= 0x11FF or >= 0x3130 and <= 0x318F) return true;
        if (s is 0x2D or 0x2E or 0x5F or 0x2F or 0x27) return true;
        return s is >= 0xC0 and <= 0x24F && s != 0xD7 && s != 0xF7;
    }

    static bool IsDigitChar(string c) => Scalar(c) is >= 0x30 and <= 0x39;

    /// <summary>英文字母（含拉丁字母），不含數字</summary>
    static bool IsLetterChar(string c)
    {
        var s = Scalar(c);
        return s is >= 0x41 and <= 0x5A or >= 0x61 and <= 0x7A || (s is >= 0xC0 and <= 0x24F && s != 0xD7 && s != 0xF7);
    }

    /// <summary>不放行首的標點、不放行尾的括號</summary>
    static readonly HashSet<string> Closing = ["，", "。", "、", "；", "：", "！", "？", "）", "」", "』", ",", ";", ":", "!", "?", ")"];  // l10n-skip
    static readonly HashSet<string> Opening = ["（", "「", "『", "《", "〈", "【", "("];  // l10n-skip

    public static List<string> Wrap(string text, double maxWidth, double size)
    {
        // 詞是字形叢集的串；isWord 記住是不是「詞」（Swift 用 count == 1 && !isWordChar 判斷「其他」）
        var tokens = new List<(string Text, bool Word)>();
        var word = new List<string>();
        void EndWord()
        {
            if (word.Count > 0) tokens.Add((string.Concat(word), true));
            word.Clear();
        }
        foreach (var c in SwiftText.Characters(text))
        {
            // 數字後面的 , : % 跟著數字（8,000、11:00、18%）
            if (c is "," or ":" or "%" && word.Count > 0 && IsDigitChar(word[^1])) { word.Add(c); continue; }
            // 詞裡的 - / 前面是字母時後面可以斷行（日期、分數不拆：2026-11-13、3/31）
            if (IsWordChar(c))
            {
                var afterLetter = word.Count > 0 && IsLetterChar(word[^1]);
                word.Add(c);
                if (c is "-" or "/" && afterLetter) EndWord();
                continue;
            }
            EndWord();
            tokens.Add((c, false));
        }
        EndWord();
        var lines = new List<string>();
        var line = new List<string>();
        var used = 0.0;
        void Push()
        {
            while (line.Count > 0 && line[^1] == " ") line.RemoveAt(line.Count - 1);
            lines.Add(string.Concat(line));
            line.Clear();
            used = 0;
        }
        // 放不下要換行：行尾的左括號跟著帶到下一行
        void BreakLine()
        {
            string? carry = null;
            if (line.Count > 1 && Opening.Contains(line[^1])) { carry = line[^1]; line.RemoveAt(line.Count - 1); }
            Push();
            if (carry is not null) { line.Add(carry); used = Width(carry, size); }
        }
        foreach (var (token, isWord) in tokens)
        {
            var w = Width(token, size);
            var chars = SwiftText.Characters(token);
            if (token == " ")
            {
                if (line.Count == 0) continue;
                if (used + w > maxWidth) BreakLine(); else { line.Add(token); used += w; }
            }
            else if (!isWord)
            {
                if (used + w <= maxWidth || line.Count == 0 || Closing.Contains(token)) { line.Add(token); used += w; }
                else { BreakLine(); line.Add(token); used += w; }
            }
            else if (used + w <= maxWidth) { line.AddRange(chars); used += w; }
            else if (w <= maxWidth) { BreakLine(); line.AddRange(chars); used += w; }
            else
            {
                // 比一行還長的詞：一個字一個字放
                foreach (var c in chars)
                {
                    var cw = Width(c, size);
                    if (used + cw > maxWidth && line.Count > 0) BreakLine();
                    line.Add(c);
                    used += cw;
                }
            }
        }
        if (line.Count > 0) Push();
        return lines.Count == 0 ? [""] : lines;
    }

    // MARK: 排版（M§42.2）

    public sealed record Style(double Size, double MaxEm, double PadH, double PadV, bool Bold)
    {
        public double LineHeight => Size * 1.45;
    }

    public static readonly Style[] Styles = [new(20, 14, 16, 10, true), new(15, 12, 10, 6, true), new(13, 20, 6, 3, false)];
    public const double TimeSize = 11, TimeGap = 6;
    public const double BranchGap = 18, LeafGap = 6, RootToBranch = 56, BranchToLeaf = 32, Margin = 32;

    /// <summary>排好的節點：Level 0 根、1 第一層、2 條目；Branch 是第幾支（根是 -1）；Side 1 右、-1 左、0 根</summary>
    public sealed class Box
    {
        public int Id, Level, Branch, Side;
        public double X, Y, W, H, TimeWidth;
        public List<string> Lines = [];
        public string Time = "";
        public Style Style => Styles[Level];
    }

    public sealed class Edge
    {
        public int From, To, Branch;
        public double X0, Y0, X1, Y1;
    }

    public sealed record Layout(double Width, double Height, List<Box> Boxes, List<Edge> Edges);

    static double CeilHalf(double x) => Math.Ceiling(x * 2) / 2;

    sealed record Measured(List<string> Lines, double W, double H, double TimeWidth);

    static Measured Measure(Node node, int level)
    {
        var s = Styles[level];
        var timeWidth = node.Time.Length == 0 ? 0 : Width(node.Time, TimeSize) + TimeGap;
        var lines = Wrap(node.Text, s.MaxEm * s.Size, s.Size);
        var textWidth = lines.Select(l => Width(l, s.Size)).DefaultIfEmpty(0).Max();
        return new Measured(lines, CeilHalf(textWidth + timeWidth + 2 * s.PadH), CeilHalf(lines.Count * s.LineHeight + 2 * s.PadV), timeWidth);
    }

    sealed record Block(Measured Branch, List<Measured> Leaves, double Height);

    static double Sum(IEnumerable<double> values)
    {
        var total = 0.0;
        foreach (var v in values) total += v;
        return total;
    }

    public static Layout LayoutOf(Node root)
    {
        var boxes = new List<Box>();
        var edges = new List<Edge>();
        var r = Measure(root, 0);
        boxes.Add(new Box { Id = 0, Level = 0, Branch = -1, Side = 0, X = -r.W / 2, Y = -r.H / 2, W = r.W, H = r.H, Lines = r.Lines });
        var blocks = root.Children.Select(b =>
        {
            var m = Measure(b, 1);
            var leaves = b.Children.Select(l => Measure(l, 2)).ToList();
            var leavesHeight = Sum(leaves.Select(l => l.H)) + LeafGap * Math.Max(leaves.Count - 1, 0);
            return new Block(m, leaves, Math.Max(m.H, leavesHeight));
        }).ToList();
        // 左右分配（順時針）：一支的中心落在總高度的前一半就放右邊
        var total = Sum(blocks.Select(b => b.Height));
        var acc = 0.0;
        List<int> right = [], left = [];
        for (var i = 0; i < blocks.Count; i++)
        {
            if (acc + blocks[i].Height / 2 <= total / 2) right.Add(i); else left.Add(i);
            acc += blocks[i].Height;
        }
        var nextId = 1;
        void Place(List<int> order, int side)
        {
            var column = Sum(order.Select(i => blocks[i].Height)) + BranchGap * Math.Max(order.Count - 1, 0);
            var top = -column / 2;
            foreach (var i in order)
            {
                var block = blocks[i];
                var center = top + block.Height / 2;
                var bw = block.Branch.W;
                var bx = side > 0 ? r.W / 2 + RootToBranch : -(r.W / 2 + RootToBranch) - bw;
                var branchId = nextId++;
                boxes.Add(new Box
                {
                    Id = branchId, Level = 1, Branch = i, Side = side, X = bx, Y = center - block.Branch.H / 2, W = bw, H = block.Branch.H,
                    Lines = block.Branch.Lines, Time = root.Children[i].Time, TimeWidth = block.Branch.TimeWidth,
                });
                edges.Add(new Edge { From = 0, To = branchId, Branch = i, X0 = side > 0 ? r.W / 2 : -r.W / 2, Y0 = 0, X1 = side > 0 ? bx : bx + bw, Y1 = center });
                var leavesHeight = Sum(block.Leaves.Select(l => l.H)) + LeafGap * Math.Max(block.Leaves.Count - 1, 0);
                var y = center - leavesHeight / 2;
                for (var j = 0; j < block.Leaves.Count; j++)
                {
                    var leaf = block.Leaves[j];
                    var lx = side > 0 ? bx + bw + BranchToLeaf : bx - BranchToLeaf - leaf.W;
                    var leafId = nextId++;
                    boxes.Add(new Box
                    {
                        Id = leafId, Level = 2, Branch = i, Side = side, X = lx, Y = y, W = leaf.W, H = leaf.H,
                        Lines = leaf.Lines, Time = root.Children[i].Children[j].Time, TimeWidth = leaf.TimeWidth,
                    });
                    edges.Add(new Edge { From = branchId, To = leafId, Branch = i, X0 = side > 0 ? bx + bw : bx, Y0 = center, X1 = side > 0 ? lx : lx + leaf.W, Y1 = y + leaf.H / 2 });
                    y += leaf.H + LeafGap;
                }
                top += block.Height + BranchGap;
            }
        }
        Place(right, 1);
        left.Reverse();   // 左邊由下往上，畫面上從上往下是反過來的順序
        Place(left, -1);
        var minX = boxes.Min(b => b.X);
        var minY = boxes.Min(b => b.Y);
        var maxX = boxes.Max(b => b.X + b.W);
        var maxY = boxes.Max(b => b.Y + b.H);
        double dx = Margin - minX, dy = Margin - minY;
        foreach (var b in boxes) { b.X += dx; b.Y += dy; }
        foreach (var e in edges) { e.X0 += dx; e.X1 += dx; e.Y0 += dy; e.Y1 += dy; }
        return new Layout(maxX - minX + 2 * Margin, maxY - minY + 2 * Margin, boxes, edges);
    }

    // MARK: 顏色（M§42.2）

    public const string RootColor = "#2D6A5D", TextColor = "#1E2625", SecondaryColor = "#68726F";
    public static readonly string[] Palette = ["#C0563F", "#3E6FA8", "#B7862A", "#7A5BA6", "#2F8A7E", "#B5517A"];
    public static readonly string[] PaletteDark = ["#E07A60", "#6E9BD6", "#D9A84A", "#A587CF", "#4FB3A4", "#D97AA0"];

    public static string Color(int branch, bool dark = false) => branch < 0 ? RootColor : (dark ? PaletteDark : Palette)[branch % Palette.Length];

    // MARK: SVG（M§42.3）

    /// <summary>座標取到 0.5（四捨五入，同 Swift 的 toNearestOrAwayFromZero）</summary>
    public static double Half(double x) => Math.Round(x * 2, MidpointRounding.AwayFromZero) / 2;

    public static string Number(double x)
    {
        var r = Half(x);
        return r == Math.Round(r) ? ((long)r).ToString(CultureInfo.InvariantCulture) : r.ToString("0.0", CultureInfo.InvariantCulture);
    }

    static string Escape(string s) => s.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal);

    /// <summary>Mac、Windows 的字型接在一起；serif、sans-serif 放最後</summary>
    public static string FontFamily(string mac, string windows)
    {
        var names = new List<string>();
        foreach (var part in (mac + "," + windows).Split(','))
        {
            var name = part.Trim();
            if (name.Length == 0 || name is "serif" or "sans-serif" || names.Contains(name)) continue;
            names.Add(name);
        }
        var generic = mac.Split(',')[^1].Trim() == "serif" ? "serif" : "sans-serif";
        return string.Join(", ", names.Append(generic));
    }

    public static string Svg(Layout layout, MeetingLanguage language)
    {
        var fonts = language.Profile.Fonts;
        var body = FontFamily(fonts.HtmlMac.Body, fonts.HtmlWindows.Body);
        var heading = FontFamily(fonts.HtmlMac.Heading, fonts.HtmlWindows.Heading);
        var n = Number;
        var o = new StringBuilder();
        o.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{n(layout.Width)}\" height=\"{n(layout.Height)}\" ")
            .Append($"viewBox=\"0 0 {n(layout.Width)} {n(layout.Height)}\" font-family=\"{Escape(body)}\">\n");
        o.Append("<rect width=\"100%\" height=\"100%\" fill=\"#FFFFFF\"/>\n");
        foreach (var e in layout.Edges)
        {
            var mid = (e.X0 + e.X1) / 2;
            var stroke = e.From == 0 ? "2" : "1.5";
            o.Append($"<path d=\"M {n(e.X0)} {n(e.Y0)} C {n(mid)} {n(e.Y0)} {n(mid)} {n(e.Y1)} {n(e.X1)} {n(e.Y1)}\" ")
                .Append($"fill=\"none\" stroke=\"{Color(e.Branch)}\" stroke-width=\"{stroke}\"/>\n");
        }
        foreach (var b in layout.Boxes)
        {
            var s = b.Style;
            if (b.Level == 0)
                o.Append($"<rect x=\"{n(b.X)}\" y=\"{n(b.Y)}\" width=\"{n(b.W)}\" height=\"{n(b.H)}\" rx=\"12\" fill=\"{RootColor}\"/>\n");
            else if (b.Level == 1)
                o.Append($"<rect x=\"{n(b.X)}\" y=\"{n(b.Y)}\" width=\"{n(b.W)}\" height=\"{n(b.H)}\" rx=\"8\" fill=\"#FFFFFF\" ")
                    .Append($"stroke=\"{Color(b.Branch)}\" stroke-width=\"1.5\"/>\n");
            // 對齊靠連線那一側；根置中
            var anchor = b.Side == 0 ? "middle" : b.Side > 0 ? "start" : "end";
            var textX = b.Side == 0 ? b.X + b.W / 2 : b.Side > 0 ? b.X + s.PadH + b.TimeWidth : b.X + b.W - s.PadH - b.TimeWidth;
            double LineY(int i) => b.Y + s.PadV + (i + 0.5) * s.LineHeight;
            if (b.Time.Length > 0)
            {
                var timeX = b.Side > 0 ? b.X + s.PadH : b.X + b.W - s.PadH;
                o.Append($"<text x=\"{n(timeX)}\" y=\"{n(LineY(0))}\" font-size=\"{n(TimeSize)}\" fill=\"{SecondaryColor}\" ")
                    .Append($"text-anchor=\"{anchor}\" dominant-baseline=\"central\">{Escape(b.Time)}</text>\n");
            }
            var fill = b.Level == 0 ? "#FFFFFF" : TextColor;
            var family = b.Level == 2 ? "" : $" font-family=\"{Escape(heading)}\"";
            var weight = s.Bold ? " font-weight=\"600\"" : "";
            o.Append($"<text font-size=\"{n(s.Size)}\" fill=\"{fill}\"{family}{weight} text-anchor=\"{anchor}\" dominant-baseline=\"central\">");
            for (var i = 0; i < b.Lines.Count; i++) o.Append($"<tspan x=\"{n(textX)}\" y=\"{n(LineY(i))}\">{Escape(b.Lines[i])}</tspan>");
            o.Append("</text>\n");
        }
        return o.Append("</svg>\n").ToString();
    }
}
