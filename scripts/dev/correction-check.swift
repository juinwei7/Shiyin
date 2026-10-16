// 逐字稿校正的逐則檢查（design §17.4）：每則建議改了的那一段，跟同一時間的標準答案比，改前改後哪個比較接近。
// 用法：swift scripts/dev/correction-check.swift <標準答案.json> <校正前.json> <校正後.json>
//   校正後.json 是 shiyinctl correct-eval 的輸出（裡面有 suggestions）
// 結果：改對（距離變小）、改錯（距離變大）、看不出差別；改錯的會列出來給人看
import Foundation

func load(_ path: String) -> [String: Any] {
    guard let data = FileManager.default.contents(atPath: path),
          let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
        FileHandle.standardError.write(Data("讀不到 \(path)\n".utf8)); exit(1)
    }
    return json
}

let args = CommandLine.arguments
guard args.count == 4 else { print("用法：swift correction-check.swift <標準答案.json> <校正前.json> <校正後.json>"); exit(1) }
let ref = load(args[1]), before = load(args[2]), after = load(args[3])
let lang = ref["lang"] as? String ?? "zh"
let refSegments = ref["segments"] as? [[String: Any]] ?? []
let beforeSegments = before["segments"] as? [[String: Any]] ?? []
let afterSegments = after["segments"] as? [[String: Any]] ?? []
let suggestions = after["suggestions"] as? [[String: Any]] ?? []

func isCJK(_ c: Character) -> Bool {
    c.unicodeScalars.contains { (0x3400...0x9FFF).contains($0.value) || (0xF900...0xFAFF).contains($0.value) }
}

/// 跟 asr-score.swift 一樣的切法：中文一個字、英文一個單字
func tokens(_ text: String) -> [String] {
    var t = text.lowercased()
    if lang == "zh" { t = t.applyingTransform(StringTransform("Hant-Hans"), reverse: false) ?? t }
    var out: [String] = [], word = ""
    for c in t {
        if isCJK(c) { if !word.isEmpty { out.append(word); word = "" }; out.append(String(c)) }
        else if c.isLetter || c.isNumber { word.append(c) }
        else if !word.isEmpty { out.append(word); word = "" }
    }
    if !word.isEmpty { out.append(word) }
    return out
}

func distance(_ a: [String], _ b: [String]) -> Int {
    var prev = Array(0...b.count)
    for (i, x) in a.enumerated() {
        var cur = [i + 1] + [Int](repeating: 0, count: b.count)
        for (j, y) in b.enumerated() { cur[j + 1] = min(prev[j + 1] + 1, cur[j] + 1, prev[j] + (x == y ? 0 : 1)) }
        prev = cur
    }
    return prev[b.count]
}

/// 同一時間（前後各多 1 秒）的標準答案文字
func referenceText(start: Double, end: Double) -> [String] {
    refSegments.filter {
        let s = $0["start"] as? Double ?? 0, e = $0["end"] as? Double ?? 0
        return e > start - 1 && s < end + 1
    }.flatMap { tokens($0["text"] as? String ?? "") }
}

var better = 0, worse = 0, same = 0
var worseList: [String] = []
for index in Set(suggestions.compactMap { $0["segment"] as? Int }).sorted() {
    guard index < beforeSegments.count, index < afterSegments.count else { continue }
    let segment = beforeSegments[index]
    let r = referenceText(start: segment["start"] as? Double ?? 0, end: segment["end"] as? Double ?? 0)
    let b = tokens(segment["text"] as? String ?? ""), a = tokens(afterSegments[index]["text"] as? String ?? "")
    let db = distance(r, b), da = distance(r, a)
    let count = suggestions.filter { $0["segment"] as? Int == index }.count
    if da < db { better += count }
    else if da > db {
        worse += count
        for s in suggestions where s["segment"] as? Int == index {
            worseList.append("[\(index)] \(s["original"] ?? "") → \(s["suggestion"] ?? "")（\(s["reason"] ?? "")）")
        }
    } else { same += count }
}
print("建議 \(suggestions.count) 則：改對 \(better)、改錯 \(worse)、看不出差別 \(same)")
for line in worseList { print("  改錯：\(line)") }
