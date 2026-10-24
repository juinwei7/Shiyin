// 轉錄評分（design §17.1、R14.1）：比較標準答案和 shiyinctl transcribe-eval 的輸出。
// 用法：swift scripts/dev/asr-score.swift <標準答案.json> <辨識結果.json> [--window 開始秒 結束秒] [--keywords 詞1,詞2] [--json 輸出.json] [--confusions N] [--keep-script]
//
// 標準答案：{"lang": "zh"、"en" 或 "ja", "segments": [{"start": 秒, "end": 秒, "speaker": "A", "text": "…"}]}
// 中文、日文一個字（漢字、假名）算一個單位、英文一個單字算一個單位，所以中文、日文是 CER、英文是 WER、夾雜英文是兩者混合。
// 中文兩邊都轉成簡體再比（AISHELL-4 的答案是簡體），去掉標點和空白。
// 日文（design §18.7）：全形半形統一、漢字數字轉阿拉伯數字（國會會議錄寫「二〇二五年」，辨識結果多半寫「2025年」）。
import Foundation

struct Segment { var start: Double; var end: Double; var speaker: String?; var text: String }

func load(_ path: String) -> (lang: String, segments: [Segment], extra: [String: Any]) {
    guard let data = FileManager.default.contents(atPath: path),
          let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
        FileHandle.standardError.write(Data("讀不到 \(path)\n".utf8)); exit(1)
    }
    let options = json["options"] as? [String: Any]
    let lang = json["lang"] as? String ?? options?["language"] as? String ?? "zh"
    let segments = (json["segments"] as? [[String: Any]] ?? []).map { s -> Segment in
        let speaker: String? = (s["speaker"] as? String) ?? (s["speaker"] as? Int).map { String($0) }
        return Segment(start: s["start"] as? Double ?? 0, end: s["end"] as? Double ?? 0, speaker: speaker, text: s["text"] as? String ?? "")
    }
    return (lang, segments, json)
}

var args = Array(CommandLine.arguments.dropFirst())
var window: (Double, Double)?
var keywords: [String] = []
var jsonOut: String?
var confusions = 0
var keepScript = false   // --keep-script：中文不轉簡體，繁簡寫錯也算錯（檢查簡轉繁的品質）
var positional: [String] = []
while !args.isEmpty {
    let a = args.removeFirst()
    switch a {
    case "--window": window = (Double(args.removeFirst())!, Double(args.removeFirst())!)
    case "--keywords": keywords = args.removeFirst().split(separator: ",").map { String($0) }
    case "--json": jsonOut = args.removeFirst()
    case "--confusions": confusions = Int(args.removeFirst()) ?? 20
    case "--keep-script": keepScript = true
    default: positional.append(a)
    }
}
guard positional.count == 2 else {
    print("用法：swift asr-score.swift <標準答案.json> <辨識結果.json> [--window a b] [--keywords 詞1,詞2] [--json 輸出.json]"); exit(1)
}
let ref = load(positional[0])
let hyp = load(positional[1])
let lang = ref.lang

func inWindow(_ s: Segment) -> Bool {
    guard let (a, b) = window else { return true }
    let mid = (s.start + s.end) / 2
    return mid >= a && mid < b
}
let refSegs = ref.segments.filter(inWindow).sorted { $0.start < $1.start }
let hypSegs = hyp.segments.filter(inWindow).sorted { $0.start < $1.start }

// MARK: 文字

func normalize(_ text: String) -> String {
    var t = text.lowercased()
    if lang == "zh" && !keepScript { t = t.applyingTransform(StringTransform("Hant-Hans"), reverse: false) ?? t }
    if lang == "ja" {
        t = t.precomposedStringWithCompatibilityMapping.lowercased()
        for _ in 0..<2 { t = t.replacing(#/(\d),(\d{3})/#) { "\($0.output.1)\($0.output.2)" } }  // 1,000,000 要兩次
        t = t.replacing(#/[0-9〇零一二三四五六七八九十百千万億兆]*[〇零一二三四五六七八九十百千万億兆][0-9〇零一二三四五六七八九十百千万億兆]*/#) {
            kanjiNumber(String($0.output))
        }
    }
    return t
}

/// 漢字數字轉阿拉伯數字：有十百千萬的照位數算（「二十五」→ 25、「25億」→ 2500000000），沒有的一個字一個數字（「二〇二五」→ 2025）
func kanjiNumber(_ run: String) -> String {
    var digits: [Character: Int] = ["〇": 0, "零": 0, "一": 1, "二": 2, "三": 3, "四": 4, "五": 5, "六": 6, "七": 7, "八": 8, "九": 9]
    for d in 0...9 { digits[Character(String(d))] = d }
    let small: [Character: Int] = ["十": 10, "百": 100, "千": 1000]
    let large: [Character: Int] = ["万": 10_000, "億": 100_000_000, "兆": 1_000_000_000_000]
    guard run.contains(where: { small[$0] != nil || large[$0] != nil }) else {
        return String(run.map { Character(String(digits[$0] ?? 0)) })
    }
    var total = 0, section = 0, current = 0
    for c in run {
        if let d = digits[c] { current = current * 10 + d }
        else if let u = small[c] { section += (current == 0 ? 1 : current) * u; current = 0 }
        else if let u = large[c] { total += max(section + current, 1) * u; section = 0; current = 0 }
    }
    return String(total + section + current)
}

func isCJK(_ c: Character) -> Bool {
    c.unicodeScalars.contains { (0x3400...0x9FFF).contains($0.value) || (0xF900...0xFAFF).contains($0.value) || isKana($0.value) }
}

/// 平假名、片假名、長音符號、「々」（中黑點「・」不算）
func isKana(_ v: UInt32) -> Bool {
    (0x3041...0x3096).contains(v) || (0x309D...0x309F).contains(v) || (0x30A1...0x30FA).contains(v)
        || (0x30FC...0x30FF).contains(v) || (0x31F0...0x31FF).contains(v) || v == 0x3005
}

/// 中文一個字一個單位，英文和數字連在一起的算一個單位（保留單字裡的 '）
func tokens(_ text: String) -> [String] {
    var out: [String] = []
    var word = ""
    func flush() { if !word.isEmpty { out.append(word); word = "" } }
    for c in normalize(text) {
        if isCJK(c) { flush(); out.append(String(c)) }
        else if c.isLetter || c.isNumber || (c == "'" && !word.isEmpty) { word.append(c) }
        else { flush() }
    }
    flush()
    return out.map { $0.hasSuffix("'") ? String($0.dropLast()) : $0 }
}

let fillers: Set<String> = lang == "zh"
    ? ["呃", "嗯", "啊", "哦", "唉", "诶", "哎", "呀", "嘛", "呢"]
    : ["um", "uh", "hmm", "mm", "mhm", "er", "erm", "ah", "huh"]

/// 日文的語助詞是好幾個假名，不能一個字一個字比，先整段刪掉。單獨的「あの」「え」也可能是一般的詞，不刪
func dropJapaneseFillers(_ text: String) -> String {
    normalize(text).replacing(#/えー+っ?と?|ええっ?と|えっと|あのー+|あのう|そのー+|まあ|まー+|うーん+|んー+/#, with: "")
}

/// 編輯距離，同時數出替換、刪除（漏字）、插入（多字）各幾個。只留兩列，長文章也不會吃太多記憶體
func align(_ r: [String], _ h: [String]) -> (sub: Int, del: Int, ins: Int) {
    typealias Cell = (cost: Int, sub: Int, del: Int, ins: Int)
    var prev: [Cell] = (0...h.count).map { ($0, 0, 0, $0) }
    var cur = prev
    for i in stride(from: 1, through: r.count, by: 1) {
        cur[0] = (i, 0, i, 0)
        for j in stride(from: 1, through: h.count, by: 1) {
            let diag = prev[j - 1], up = prev[j], left = cur[j - 1]
            let same = r[i - 1] == h[j - 1]
            var best: Cell = (diag.cost + (same ? 0 : 1), diag.sub + (same ? 0 : 1), diag.del, diag.ins)
            if up.cost + 1 < best.cost { best = (up.cost + 1, up.sub, up.del + 1, up.ins) }
            if left.cost + 1 < best.cost { best = (left.cost + 1, left.sub, left.del, left.ins + 1) }
            cur[j] = best
        }
        swap(&prev, &cur)
    }
    let last = prev[h.count]
    return (last.sub, last.del, last.ins)
}

/// 跟 align 一樣的編輯距離，但留下整條對齊路徑（每格一個位元組，38 分鐘的會議約 60 MB），用來列出錯在哪裡（--confusions）
enum Op: UInt8 { case same = 0, sub = 1, del = 2, ins = 3 }
func alignOps(_ r: [String], _ h: [String]) -> [(op: Op, ref: String?, hyp: String?)] {
    let w = h.count + 1
    var cost = [Int](repeating: 0, count: w), back = [UInt8](repeating: 0, count: (r.count + 1) * w)
    for j in 0...h.count { cost[j] = j; back[j] = Op.ins.rawValue }
    for i in 1...max(r.count, 1) where i <= r.count {
        var diagCost = cost[0]
        cost[0] = i; back[i * w] = Op.del.rawValue
        for j in 1...max(h.count, 1) where j <= h.count {
            let same = r[i - 1] == h[j - 1]
            var best = diagCost + (same ? 0 : 1), op = same ? Op.same : Op.sub
            if cost[j] + 1 < best { best = cost[j] + 1; op = .del }
            if cost[j - 1] + 1 < best { best = cost[j - 1] + 1; op = .ins }
            diagCost = cost[j]; cost[j] = best; back[i * w + j] = op.rawValue
        }
    }
    var ops: [(op: Op, ref: String?, hyp: String?)] = []
    var i = r.count, j = h.count
    while i > 0 || j > 0 {
        let op = Op(rawValue: back[i * w + j])!
        switch op {
        case .same, .sub: ops.append((op, r[i - 1], h[j - 1])); i -= 1; j -= 1
        case .del: ops.append((op, r[i - 1], nil)); i -= 1
        case .ins: ops.append((op, nil, h[j - 1])); j -= 1
        }
    }
    return ops.reversed()
}

/// 錯誤對照（--confusions N）：去掉語助詞後對齊，列出最常見的替換對、漏字、多字，和錯得最多的片段（前後各帶幾個字的上下文）
func dumpConfusions(_ r: [String], _ h: [String], top: Int) -> [String: Any] {
    let ops = alignOps(r, h)
    var subs: [String: Int] = [:], dels: [String: Int] = [:], inss: [String: Int] = [:]
    for o in ops {
        switch o.op {
        case .sub: subs[o.ref! + "→" + o.hyp!, default: 0] += 1
        case .del: dels[o.ref!, default: 0] += 1
        case .ins: inss[o.hyp!, default: 0] += 1
        case .same: break
        }
    }
    func topList(_ d: [String: Int]) -> [[String: Any]] {
        d.sorted { $0.value != $1.value ? $0.value > $1.value : $0.key < $1.key }.prefix(top).map { ["token": $0.key, "count": $0.value] }
    }
    // 連續的錯誤合成一個片段（中間最多隔 1 個對的字），依錯誤數排序
    var clusters: [(errors: Int, start: Int, end: Int)] = []
    var start: Int? = nil, errors = 0, lastError = -10
    for (k, o) in ops.enumerated() {
        if o.op != .same {
            if start == nil || k - lastError > 2 {
                if let s = start { clusters.append((errors, s, lastError)) }
                start = k; errors = 0
            }
            errors += 1; lastError = k
        }
    }
    if let s = start { clusters.append((errors, s, lastError)) }
    let sep = lang == "en" ? " " : ""
    let worst = clusters.sorted { $0.errors > $1.errors }.prefix(top).map { c -> [String: Any] in
        let lo = max(0, c.start - 6), hi = min(ops.count - 1, c.end + 6)
        let slice = ops[lo...hi]
        return ["errors": c.errors,
                "ref": slice.compactMap { $0.op == .ins ? nil : $0.ref }.joined(separator: sep),
                "hyp": slice.compactMap { $0.op == .del ? nil : $0.hyp }.joined(separator: sep)]
    }
    print("最常見的替換 ref→hyp：" + topList(subs).prefix(15).map { "\($0["token"]!)×\($0["count"]!)" }.joined(separator: "、"))
    print("最常漏的字：" + topList(dels).prefix(15).map { "\($0["token"]!)×\($0["count"]!)" }.joined(separator: "、"))
    print("最常多出的字：" + topList(inss).prefix(15).map { "\($0["token"]!)×\($0["count"]!)" }.joined(separator: "、"))
    print("錯最多的片段：")
    for c in worst.prefix(top) { print("  [\(c["errors"]!)] 參考：\(c["ref"]!)\n       辨識：\(c["hyp"]!)") }
    return ["substitutions": topList(subs), "deletions": topList(dels), "insertions": topList(inss), "worst": worst]
}

let refText = refSegs.map(\.text).joined(separator: " ")
let hypText = hypSegs.map(\.text).joined(separator: " ")
var metrics: [String: Any] = ["lang": lang, "refSegments": refSegs.count, "hypSegments": hypSegs.count]
let unit = lang == "en" ? "WER" : "CER"
for dropFillers in [false, true] {
    let r: [String], h: [String]
    if lang == "ja" {
        r = tokens(dropFillers ? dropJapaneseFillers(refText) : refText)
        h = tokens(dropFillers ? dropJapaneseFillers(hypText) : hypText)
    } else {
        r = tokens(refText).filter { !dropFillers || !fillers.contains($0) }
        h = tokens(hypText).filter { !dropFillers || !fillers.contains($0) }
    }
    let e = align(r, h)
    let rate = Double(e.sub + e.del + e.ins) / Double(max(r.count, 1)) * 100
    let key = dropFillers ? "noFillers" : "raw"
    metrics[key] = ["ref": r.count, "hyp": h.count, "sub": e.sub, "del": e.del, "ins": e.ins, "rate": rate]
    print(String(format: "%@%@ %.1f%%（參考 %d、辨識 %d；替換 %d、漏 %d、多 %d）",
                 unit, dropFillers ? "（去掉語助詞）" : "", rate, r.count, h.count, e.sub, e.del, e.ins))
    if dropFillers && confusions > 0 { metrics["confusions"] = dumpConfusions(r, h, top: confusions) }
}

// MARK: 關鍵詞

if !keywords.isEmpty {
    // 不管空白比對（「Power Point」和「PowerPoint」都算對）
    let refJoined = tokens(refText).joined(), hypJoined = tokens(hypText).joined()
    var hits: [String] = [], misses: [String] = []
    for k in keywords {
        let key = tokens(k).joined()
        guard refJoined.contains(key) else { continue }
        if hypJoined.contains(key) { hits.append(k) } else { misses.append(k) }
    }
    metrics["keywords"] = ["hit": hits.count, "total": hits.count + misses.count, "missed": misses]
    print("關鍵詞 \(hits.count)/\(hits.count + misses.count)" + (misses.isEmpty ? "" : "（沒對：\(misses.joined(separator: "、"))）"))
}

// MARK: 說話者：每 0.1 秒一格，只算標準答案裡只有一個人說話、而且辨識結果有標說話者的格子

let step = 0.1
let end = max(refSegs.map(\.end).max() ?? 0, hypSegs.map(\.end).max() ?? 0)
let cells = Int(end / step) + 1
if refSegs.contains(where: { $0.speaker != nil }), hypSegs.contains(where: { $0.speaker != nil }), cells > 0 {
    var refGrid = [Set<String>](repeating: [], count: cells)
    for s in refSegs { if let spk = s.speaker { for i in Int(s.start / step)..<min(cells, Int(s.end / step)) { refGrid[i].insert(spk) } } }
    var hypGrid = [String?](repeating: nil, count: cells)
    for s in hypSegs { if let spk = s.speaker { for i in Int(s.start / step)..<min(cells, max(Int(s.start / step), Int(s.end / step))) { hypGrid[i] = spk } } }
    var pairs: [String: [String: Int]] = [:]
    var scored = 0
    for i in 0..<cells where refGrid[i].count == 1 {
        guard let h = hypGrid[i] else { continue }
        scored += 1
        pairs[h, default: [:]][refGrid[i].first!, default: 0] += 1
    }
    // 一對一對應，取重疊最多的組合；人數不多時全部試過，太多就用貪婪法
    let hs = pairs.keys.sorted(), rs = Set(pairs.values.flatMap(\.keys)).sorted()
    func score(_ map: [String: String]) -> Int { map.reduce(0) { $0 + (pairs[$1.key]?[$1.value] ?? 0) } }
    var best: [String: String] = [:]
    if hs.count <= 7 && rs.count <= 7 {
        func search(_ i: Int, _ used: Set<String>, _ current: [String: String]) {
            if i == hs.count { if score(current) > score(best) { best = current }; return }
            search(i + 1, used, current)
            for r in rs where !used.contains(r) { var next = current; next[hs[i]] = r; search(i + 1, used.union([r]), next) }
        }
        search(0, [], [:])
    } else {
        var used = Set<String>()
        for (h, r, _) in pairs.flatMap({ h, m in m.map { (h, $0.key, $0.value) } }).sorted(by: { $0.2 > $1.2 })
        where best[h] == nil && !used.contains(r) { best[h] = r; used.insert(r) }
    }
    let correct = score(best)
    let accuracy = Double(correct) / Double(max(scored, 1)) * 100
    metrics["speakers"] = ["ref": rs.count, "hyp": Set(hypSegs.compactMap(\.speaker)).count, "accuracy": accuracy,
                           "minutes": Double(scored) * step / 60]
    print(String(format: "說話者：標準答案 %d 位、辨識出 %d 位；單人說話的時間裡標對 %.1f%%（%.1f 分鐘）",
                 rs.count, Set(hypSegs.compactMap(\.speaker)).count, accuracy, Double(scored) * step / 60))
}

if let elapsed = hyp.extra["elapsed"] as? Double {
    metrics["elapsed"] = elapsed
    let duration = hyp.extra["duration"] as? Double ?? 0
    print(String(format: "耗時 %.0f 秒（錄音 %.0f 秒，%.1f 倍速）", elapsed, duration, duration / max(elapsed, 0.001)))
}
if let path = jsonOut, let data = try? JSONSerialization.data(withJSONObject: metrics, options: [.prettyPrinted, .sortedKeys]) {
    try? data.write(to: URL(fileURLWithPath: path))
}
