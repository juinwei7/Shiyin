import Foundation

/// 簡體轉台灣繁體（design §32.5、D108）：Qwen3-ASR 輸出簡體，用 OpenCC 的字表（`Resources/opencc/`）轉成繁體。
/// 做法跟 OpenCC 的 s2tw 一樣：先在詞組表找最長的匹配，沒有就查單字表，查不到原樣保留；全部轉完套台灣用字，最後「臺」寫成「台」。
enum ChineseScript {
    /// 字表：鍵 → 第一個候選
    struct Tables: Sendable {
        var phrases: [String: String]
        var characters: [Character: Character]
        var variants: [Character: Character]
        var longestPhrase: Int
    }

    nonisolated(unsafe) private static var cached: Tables?
    private static let lock = NSLock()

    /// 字表所在的資料夾：app bundle 的 Resources/opencc；直接跑建置輸出（沒有 bundle）時用原始碼樹的 Resources/opencc
    static var tablesDirectory: URL {
        if let url = Bundle.main.resourceURL?.appendingPathComponent("opencc"),
           FileManager.default.fileExists(atPath: url.appendingPathComponent("STPhrases.tsv").path) {
            return url
        }
        return URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
            .deletingLastPathComponent().appendingPathComponent("Resources/opencc")
    }

    static func tables() throws -> Tables {
        lock.lock(); defer { lock.unlock() }
        if let cached { return cached }
        let dir = tablesDirectory
        func rows(_ name: String) throws -> [(String, String)] {
            let text = try String(contentsOf: dir.appendingPathComponent(name), encoding: .utf8)
            return text.split(separator: "\n").compactMap { line in
                let parts = line.split(separator: "\t", maxSplits: 1)
                guard parts.count == 2 else { return nil }
                return (String(parts[0]), String(parts[1]))
            }
        }
        var phrases: [String: String] = [:]
        var longest = 1
        for (key, value) in try rows("STPhrases.tsv") {
            phrases[key] = value
            longest = max(longest, key.count)
        }
        var characters: [Character: Character] = [:]
        for (key, value) in try rows("STCharacters.tsv") where key.count == 1 && value.count == 1 {
            characters[key.first!] = value.first!
        }
        var variants: [Character: Character] = [:]
        for (key, value) in try rows("TWVariants.tsv") where key.count == 1 && value.count == 1 {
            variants[key.first!] = value.first!
        }
        let result = Tables(phrases: phrases, characters: characters, variants: variants, longestPhrase: longest)
        cached = result
        return result
    }

    /// 轉成台灣繁體；字表讀不到時原樣回傳（寫 log，不讓轉錄失敗）
    static func toTaiwan(_ text: String) -> String {
        guard let tables = try? tables() else { return text }
        return toTaiwan(text, tables: tables)
    }

    static func toTaiwan(_ text: String, tables: Tables) -> String {
        let chars = Array(text)
        var out = String()
        out.reserveCapacity(text.utf8.count)
        var i = 0
        while i < chars.count {
            var matched = false
            // 只有中日韓的字才可能是詞組的開頭，英文、數字、標點直接跳過
            if chars[i].unicodeScalars.first.map({ (0x3400...0x9FFF).contains($0.value) || (0xF900...0xFAFF).contains($0.value) }) == true {
                var length = min(tables.longestPhrase, chars.count - i)
                while length >= 2 {
                    let candidate = String(chars[i..<i + length])
                    if let value = tables.phrases[candidate] {
                        out += value
                        i += length
                        matched = true
                        break
                    }
                    length -= 1
                }
            }
            if !matched {
                out.append(tables.characters[chars[i]] ?? chars[i])
                i += 1
            }
        }
        return String(out.map { c -> Character in
            let v = tables.variants[c] ?? c
            return v == "臺" ? "台" : v   // l10n-skip
        })
    }
}
