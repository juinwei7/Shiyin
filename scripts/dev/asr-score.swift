// 轉錄評分（design §17.1、R14.1）：比較標準答案和 shiyinctl transcribe-eval 的輸出。
// 用法：swift scripts/dev/asr-score.swift <標準答案.json> <辨識結果.json> [--window 開始秒 結束秒] [--keywords 詞1,詞2] [--json 輸出.json]
//
// 標準答案：{"lang": "zh" 或 "en", "segments": [{"start": 秒, "end": 秒, "speaker": "A", "text": "…"}]}
// 中文一個字算一個單位、英文一個單字算一個單位，所以中文是 CER、英文是 WER、中英夾雜是兩者混合。
// 中文兩邊都轉成簡體再比（AISHELL-4 的答案是簡體），去掉標點和空白。
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
var positional: [String] = []
while !args.isEmpty {
    let a = args.removeFirst()
    switch a {
    case "--window": window = (Double(args.removeFirst())!, Double(args.removeFirst())!)
    case "--keywords": keywords = args.removeFirst().split(separator: ",").map { String($0) }
    case "--json": jsonOut = args.removeFirst()
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
    if lang == "zh" { t = t.applyingTransform(StringTransform("Hant-Hans"), reverse: false) ?? t }
    return t
}

func isCJK(_ c: Character) -> Bool {
    c.unicodeScalars.contains { (0x3400...0x9FFF).contains($0.value) || (0xF900...0xFAFF).contains($0.value) }
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

let refText = refSegs.map(\.text).joined(separator: " ")
let hypText = hypSegs.map(\.text).joined(separator: " ")
var metrics: [String: Any] = ["lang": lang, "refSegments": refSegs.count, "hypSegments": hypSegs.count]
let unit = lang == "zh" ? "CER" : "WER"
for dropFillers in [false, true] {
    let r = tokens(refText).filter { !dropFillers || !fillers.contains($0) }
    let h = tokens(hypText).filter { !dropFillers || !fillers.contains($0) }
    let e = align(r, h)
    let rate = Double(e.sub + e.del + e.ins) / Double(max(r.count, 1)) * 100
    let key = dropFillers ? "noFillers" : "raw"
    metrics[key] = ["ref": r.count, "hyp": h.count, "sub": e.sub, "del": e.del, "ins": e.ins, "rate": rate]
    print(String(format: "%@%@ %.1f%%（參考 %d、辨識 %d；替換 %d、漏 %d、多 %d）",
                 unit, dropFillers ? "（去掉語助詞）" : "", rate, r.count, h.count, e.sub, e.del, e.ins))
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
