import Foundation

/// 會議紀錄畫成心智圖（design §42、R67）：從紀錄組成樹（不呼叫整理工具，D143）、排版、SVG。
/// 排版的文字寬度用估的，Windows 的 Notes/MindMap.cs 是同一套規則，標準答案逐字比（D144）
enum MindMap {
    /// 樹的一個節點：根是會議標題，第一層是議題、段落，第三層是條目
    struct Node: Equatable {
        var text: String
        var time: String
        var children: [Node] = []
    }

    /// 段落名稱和標點：畫面上用介面語言（同條列），匯出用會議語言（同 ExportText）
    struct Labels {
        let summary, decisions, actionItems, openQuestions, topics, untitled, colon: String
        let paren: (String) -> String

        init(_ language: MeetingLanguage) {
            let x = ExportText(language)
            summary = x.section("summary")
            decisions = x.section("decisions")
            actionItems = x.section("actionItems")
            openQuestions = x.section("openQuestions")
            topics = language.text(zh: "議題", en: "Topic", ja: "議題")  // l10n-skip：會議語言的文字
            untitled = language.text(zh: "會議", en: "Meeting", ja: "会議")  // l10n-skip：會議語言的文字
            colon = x.colon
            paren = x.paren
        }
    }

    // MARK: 樹（§42.1）

    static func tree(notes: MeetingNotes, title: String, labels: Labels) -> Node {
        func clean(_ s: String) -> String {
            s.replacingOccurrences(of: "\r\n", with: " ").replacingOccurrences(of: "\n", with: " ")
                .trimmingCharacters(in: .whitespacesAndNewlines)
        }
        func leaves(_ items: [MeetingNotes.Item]) -> [Node] {
            items.map { Node(text: clean($0.text), time: clean($0.time)) }.filter { !$0.text.isEmpty }
        }
        var branches: [Node] = []
        let topics = notes.topics.filter { !clean($0.title).isEmpty || !leaves($0.points).isEmpty }
        if topics.isEmpty {
            let summary = leaves(notes.summary)
            if !summary.isEmpty { branches.append(Node(text: labels.summary, time: "", children: summary)) }
        } else {
            for topic in topics {
                let name = clean(topic.title)
                branches.append(Node(text: name.isEmpty ? labels.topics : name, time: clean(topic.start), children: leaves(topic.points)))
            }
        }
        let decisions = leaves(notes.decisions)
        if !decisions.isEmpty { branches.append(Node(text: labels.decisions, time: "", children: decisions)) }
        let actions: [Node] = notes.actionItems.compactMap { a in
            let task = clean(a.task), owner = clean(a.owner), due = clean(a.due)
            guard !task.isEmpty else { return nil }
            var text = owner.isEmpty ? task : owner + labels.colon + task
            if !due.isEmpty { text += labels.paren(due) }
            return Node(text: text, time: clean(a.time))
        }
        if !actions.isEmpty { branches.append(Node(text: labels.actionItems, time: "", children: actions)) }
        let questions = leaves(notes.openQuestions)
        if !questions.isEmpty { branches.append(Node(text: labels.openQuestions, time: "", children: questions)) }
        for section in notes.custom {
            let items = leaves(section.items)
            guard !items.isEmpty else { continue }
            let name = clean(section.title)
            branches.append(Node(text: name.isEmpty ? "—" : name, time: "", children: items))
        }
        let name = clean(title)
        return Node(text: name.isEmpty ? labels.untitled : name, time: "", children: branches)
    }

    // MARK: 文字寬度、換行（§42.2）

    /// 一個字元的寬度（em）：看第一個 scalar
    static func em(_ c: Character) -> Double {
        guard let s = c.unicodeScalars.first?.value else { return 0 }
        if s == 0x20 { return 0.3 }
        if s < 0x80 {
            if (0x41...0x5A).contains(s) { return 0.66 }
            if (0x61...0x7A).contains(s) || (0x30...0x39).contains(s) { return 0.56 }
            return 0.35
        }
        return isWide(s) ? 1.0 : 0.6
    }

    static func isWide(_ s: UInt32) -> Bool {
        (0x1100...0x115F).contains(s) || (0x2E80...0xA4CF).contains(s) || (0xAC00...0xD7A3).contains(s)
            || (0xF900...0xFAFF).contains(s) || (0xFE30...0xFE4F).contains(s) || (0xFF00...0xFF60).contains(s)
            || (0xFFE0...0xFFE6).contains(s) || (0x1F300...0x1FAFF).contains(s) || (0x20000...0x3FFFD).contains(s)
    }

    static func width(_ text: String, size: Double) -> Double {
        var em = 0.0
        for c in text { em += MindMap.em(c) }
        return em * size
    }

    /// 英數連在一起的詞（含詞中的 - . _ / '）不拆
    private static func isWordChar(_ c: Character) -> Bool {
        guard let s = c.unicodeScalars.first?.value else { return false }
        if (0x30...0x39).contains(s) || (0x41...0x5A).contains(s) || (0x61...0x7A).contains(s) { return true }
        if [0x2D, 0x2E, 0x5F, 0x2F, 0x27].contains(s) { return true }
        return (0xC0...0x24F).contains(s) && s != 0xD7 && s != 0xF7
    }

    /// 英文字母（含拉丁字母），不含數字
    private static func isLetterChar(_ c: Character) -> Bool {
        guard let s = c.unicodeScalars.first?.value else { return false }
        return (0x41...0x5A).contains(s) || (0x61...0x7A).contains(s) || ((0xC0...0x24F).contains(s) && s != 0xD7 && s != 0xF7)
    }

    /// 不放行首的標點、不放行尾的括號
    private static let closing: Set<Character> = ["，", "。", "、", "；", "：", "！", "？", "）", "」", "』", ",", ";", ":", "!", "?", ")"]
    private static let opening: Set<Character> = ["（", "「", "『", "《", "〈", "【", "("]

    static func wrap(_ text: String, maxWidth: Double, size: Double) -> [String] {
        var tokens: [String] = []
        var word = ""
        for c in text {
            // 詞裡的 - / 前面是字母時後面可以斷行（日期、分數不拆：2026-11-13、3/31）
            if isWordChar(c) {
                let afterLetter = word.last.map(isLetterChar) ?? false
                word.append(c)
                if (c == "-" || c == "/") && afterLetter { tokens.append(word); word = "" }
                continue
            }
            if !word.isEmpty { tokens.append(word); word = "" }
            tokens.append(String(c))
        }
        if !word.isEmpty { tokens.append(word) }
        var lines: [String] = []
        var line = ""
        var used = 0.0
        func push() {
            while line.hasSuffix(" ") { line.removeLast() }
            lines.append(line)
            line = ""
            used = 0
        }
        // 放不下要換行：行尾的左括號跟著帶到下一行
        func breakLine() {
            var carry = ""
            if let last = line.last, opening.contains(last), line.count > 1 { carry = String(last); line.removeLast() }
            push()
            if !carry.isEmpty { line = carry; used = width(carry, size: size) }
        }
        for token in tokens {
            let w = width(token, size: size)
            if token == " " {
                if line.isEmpty { continue }
                if used + w > maxWidth { breakLine() } else { line += token; used += w }
            } else if token.count == 1 && !isWordChar(token.first!) {
                if used + w <= maxWidth || line.isEmpty || closing.contains(token.first!) {
                    line += token; used += w
                } else {
                    breakLine(); line += token; used += w
                }
            } else if used + w <= maxWidth {
                line += token; used += w
            } else if w <= maxWidth {
                breakLine(); line += token; used += w
            } else {
                // 比一行還長的詞：一個字一個字放
                for c in token {
                    let cw = width(String(c), size: size)
                    if used + cw > maxWidth && !line.isEmpty { breakLine() }
                    line.append(c)
                    used += cw
                }
            }
        }
        if !line.isEmpty { push() }
        return lines.isEmpty ? [""] : lines
    }

    // MARK: 排版（§42.2）

    struct Style {
        let size, maxEm, padH, padV: Double
        let bold: Bool
        var lineHeight: Double { size * 1.45 }
    }

    static let styles = [Style(size: 20, maxEm: 14, padH: 16, padV: 10, bold: true),
                         Style(size: 15, maxEm: 12, padH: 10, padV: 6, bold: true),
                         Style(size: 13, maxEm: 20, padH: 6, padV: 3, bold: false)]
    static let timeSize = 11.0
    static let timeGap = 6.0

    /// 排好的節點：level 0 根、1 第一層、2 條目；branch 是第幾支（根是 -1）；side 1 右、-1 左、0 根
    struct Box: Equatable {
        var id, level, branch, side: Int
        var x, y, w, h: Double
        var lines: [String]
        var time: String
        var timeWidth: Double
        var style: Style { MindMap.styles[level] }
    }

    struct Edge: Equatable {
        var from, to, branch: Int
        var x0, y0, x1, y1: Double
    }

    struct Layout: Equatable {
        var width, height: Double
        var boxes: [Box]
        var edges: [Edge]
    }

    private static func ceilHalf(_ x: Double) -> Double { (x * 2).rounded(.up) / 2 }

    private static func measure(_ node: Node, level: Int) -> (lines: [String], w: Double, h: Double, timeWidth: Double) {
        let s = styles[level]
        let timeWidth = node.time.isEmpty ? 0 : width(node.time, size: timeSize) + timeGap
        let lines = wrap(node.text, maxWidth: s.maxEm * s.size, size: s.size)
        let textWidth = lines.map { width($0, size: s.size) }.max() ?? 0
        return (lines, ceilHalf(textWidth + timeWidth + 2 * s.padH), ceilHalf(Double(lines.count) * s.lineHeight + 2 * s.padV), timeWidth)
    }

    static let branchGap = 18.0, leafGap = 6.0, rootToBranch = 56.0, branchToLeaf = 32.0, margin = 32.0

    static func layout(_ root: Node) -> Layout {
        var boxes: [Box] = []
        var edges: [Edge] = []
        let r = measure(root, level: 0)
        boxes.append(Box(id: 0, level: 0, branch: -1, side: 0, x: -r.w / 2, y: -r.h / 2, w: r.w, h: r.h,
                         lines: r.lines, time: "", timeWidth: 0))
        struct Block {
            var branch: (lines: [String], w: Double, h: Double, timeWidth: Double)
            var leaves: [(lines: [String], w: Double, h: Double, timeWidth: Double)]
            var height: Double
        }
        let blocks: [Block] = root.children.map { b in
            let m = measure(b, level: 1)
            let leaves = b.children.map { measure($0, level: 2) }
            let leavesHeight = leaves.reduce(0) { $0 + $1.h } + leafGap * Double(max(leaves.count - 1, 0))
            return Block(branch: m, leaves: leaves, height: max(m.h, leavesHeight))
        }
        // 左右分配（順時針）：一支的中心落在總高度的前一半就放右邊
        let total = blocks.reduce(0) { $0 + $1.height }
        var acc = 0.0
        var right: [Int] = [], left: [Int] = []
        for (i, b) in blocks.enumerated() {
            if acc + b.height / 2 <= total / 2 { right.append(i) } else { left.append(i) }
            acc += b.height
        }
        var nextId = 1
        func place(_ order: [Int], side: Int) {
            let column = order.reduce(0) { $0 + blocks[$1].height } + branchGap * Double(max(order.count - 1, 0))
            var top = -column / 2
            for i in order {
                let block = blocks[i]
                let center = top + block.height / 2
                let bw = block.branch.w
                let bx = side > 0 ? r.w / 2 + rootToBranch : -(r.w / 2 + rootToBranch) - bw
                let branchId = nextId
                nextId += 1
                boxes.append(Box(id: branchId, level: 1, branch: i, side: side, x: bx, y: center - block.branch.h / 2,
                                 w: bw, h: block.branch.h, lines: block.branch.lines, time: root.children[i].time,
                                 timeWidth: block.branch.timeWidth))
                edges.append(Edge(from: 0, to: branchId, branch: i, x0: side > 0 ? r.w / 2 : -r.w / 2, y0: 0,
                                  x1: side > 0 ? bx : bx + bw, y1: center))
                let leavesHeight = block.leaves.reduce(0) { $0 + $1.h } + leafGap * Double(max(block.leaves.count - 1, 0))
                var y = center - leavesHeight / 2
                for (j, leaf) in block.leaves.enumerated() {
                    let lx = side > 0 ? bx + bw + branchToLeaf : bx - branchToLeaf - leaf.w
                    let leafId = nextId
                    nextId += 1
                    boxes.append(Box(id: leafId, level: 2, branch: i, side: side, x: lx, y: y, w: leaf.w, h: leaf.h,
                                     lines: leaf.lines, time: root.children[i].children[j].time, timeWidth: leaf.timeWidth))
                    edges.append(Edge(from: branchId, to: leafId, branch: i, x0: side > 0 ? bx + bw : bx, y0: center,
                                      x1: side > 0 ? lx : lx + leaf.w, y1: y + leaf.h / 2))
                    y += leaf.h + leafGap
                }
                top += block.height + branchGap
            }
        }
        place(right, side: 1)
        place(left.reversed(), side: -1)   // 左邊由下往上，畫面上從上往下是反過來的順序
        // 平移到左上角留邊
        let minX = boxes.map(\.x).min()!, minY = boxes.map(\.y).min()!
        let maxX = boxes.map { $0.x + $0.w }.max()!, maxY = boxes.map { $0.y + $0.h }.max()!
        let dx = margin - minX, dy = margin - minY
        for i in boxes.indices { boxes[i].x += dx; boxes[i].y += dy }
        for i in edges.indices { edges[i].x0 += dx; edges[i].x1 += dx; edges[i].y0 += dy; edges[i].y1 += dy }
        return Layout(width: maxX - minX + 2 * margin, height: maxY - minY + 2 * margin, boxes: boxes, edges: edges)
    }

    // MARK: 顏色（§42.2）

    static let rootColor = "#2D6A5D"
    static let palette = ["#C0563F", "#3E6FA8", "#B7862A", "#7A5BA6", "#2F8A7E", "#B5517A"]
    static let paletteDark = ["#E07A60", "#6E9BD6", "#D9A84A", "#A587CF", "#4FB3A4", "#D97AA0"]
    static let textColor = "#1E2625"
    static let secondaryColor = "#68726F"

    static func color(_ branch: Int, dark: Bool = false) -> String {
        branch < 0 ? rootColor : (dark ? paletteDark : palette)[branch % palette.count]
    }

    // MARK: SVG（§42.3）

    /// 座標取到 0.5（四捨五入，跟 C# 的 MidpointRounding.AwayFromZero 一樣）
    static func number(_ x: Double) -> String {
        let r = (x * 2).rounded(.toNearestOrAwayFromZero) / 2
        return r == r.rounded() ? String(Int(r)) : String(format: "%.1f", r)
    }

    private static func escape(_ s: String) -> String {
        s.replacingOccurrences(of: "&", with: "&amp;").replacingOccurrences(of: "<", with: "&lt;")
            .replacingOccurrences(of: ">", with: "&gt;").replacingOccurrences(of: "\"", with: "&quot;")
    }

    /// Mac、Windows 的字型接在一起；serif、sans-serif 放最後，不然沒有前面的字型就停在通用字型
    static func fontFamily(_ mac: String, _ windows: String) -> String {
        var names: [String] = []
        for part in (mac + "," + windows).split(separator: ",") {
            let name = part.trimmingCharacters(in: .whitespaces)
            if name.isEmpty || name == "serif" || name == "sans-serif" || names.contains(name) { continue }
            names.append(name)
        }
        let generic = mac.split(separator: ",").last?.trimmingCharacters(in: .whitespaces) == "serif" ? "serif" : "sans-serif"
        return (names + [generic]).joined(separator: ", ")
    }

    static func svg(_ layout: Layout, language: MeetingLanguage) -> String {
        let fonts = language.profile.fonts
        let body = fontFamily(fonts.htmlMac.body, fonts.htmlWindows.body)
        let heading = fontFamily(fonts.htmlMac.heading, fonts.htmlWindows.heading)
        let n = number
        var out = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"\(n(layout.width))\" height=\"\(n(layout.height))\" "
            + "viewBox=\"0 0 \(n(layout.width)) \(n(layout.height))\" font-family=\"\(escape(body))\">\n"
        out += "<rect width=\"100%\" height=\"100%\" fill=\"#FFFFFF\"/>\n"
        for e in layout.edges {
            let mid = (e.x0 + e.x1) / 2
            let stroke = e.from == 0 ? "2" : "1.5"
            out += "<path d=\"M \(n(e.x0)) \(n(e.y0)) C \(n(mid)) \(n(e.y0)) \(n(mid)) \(n(e.y1)) \(n(e.x1)) \(n(e.y1))\" "
                + "fill=\"none\" stroke=\"\(color(e.branch))\" stroke-width=\"\(stroke)\"/>\n"
        }
        for b in layout.boxes {
            let s = b.style
            switch b.level {
            case 0:
                out += "<rect x=\"\(n(b.x))\" y=\"\(n(b.y))\" width=\"\(n(b.w))\" height=\"\(n(b.h))\" rx=\"12\" fill=\"\(rootColor)\"/>\n"
            case 1:
                out += "<rect x=\"\(n(b.x))\" y=\"\(n(b.y))\" width=\"\(n(b.w))\" height=\"\(n(b.h))\" rx=\"8\" fill=\"#FFFFFF\" "
                    + "stroke=\"\(color(b.branch))\" stroke-width=\"1.5\"/>\n"
            default: break
            }
            // 對齊靠連線那一側（估的寬度有誤差時，空白出現在外側）；根置中
            let anchor = b.side == 0 ? "middle" : b.side > 0 ? "start" : "end"
            let textX = b.side == 0 ? b.x + b.w / 2 : b.side > 0 ? b.x + s.padH + b.timeWidth : b.x + b.w - s.padH - b.timeWidth
            func lineY(_ i: Int) -> Double { b.y + s.padV + (Double(i) + 0.5) * s.lineHeight }
            if !b.time.isEmpty {
                let timeX = b.side > 0 ? b.x + s.padH : b.x + b.w - s.padH
                out += "<text x=\"\(n(timeX))\" y=\"\(n(lineY(0)))\" font-size=\"\(n(timeSize))\" fill=\"\(secondaryColor)\" "
                    + "text-anchor=\"\(anchor)\" dominant-baseline=\"central\">\(escape(b.time))</text>\n"
            }
            let fill = b.level == 0 ? "#FFFFFF" : textColor
            let family = b.level == 2 ? "" : " font-family=\"\(escape(heading))\""
            let weight = s.bold ? " font-weight=\"600\"" : ""
            out += "<text font-size=\"\(n(s.size))\" fill=\"\(fill)\"\(family)\(weight) text-anchor=\"\(anchor)\" dominant-baseline=\"central\">"
            for (i, line) in b.lines.enumerated() {
                out += "<tspan x=\"\(n(textX))\" y=\"\(n(lineY(i)))\">\(escape(line))</tspan>"
            }
            out += "</text>\n"
        }
        return out + "</svg>\n"
    }
}
