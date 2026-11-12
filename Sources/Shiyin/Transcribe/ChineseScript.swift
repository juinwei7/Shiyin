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

    // MARK: 繁轉簡（design §47.1、D153）

    /// 繁轉簡的字表：台灣用字換回一般繁體（詞組、單字），再轉簡體（詞組、單字）
    struct SimplifiedTables: Sendable {
        var standardPhrases: [String: String]
        var standardLongest: Int
        var standardCharacters: [Character: Character]
        var phrases: [String: String]
        var longest: Int
        var characters: [Character: Character]
    }

    nonisolated(unsafe) private static var cachedSimplified: SimplifiedTables?

    static func simplifiedTables() throws -> SimplifiedTables {
        lock.lock(); defer { lock.unlock() }
        if let cachedSimplified { return cachedSimplified }
        let dir = tablesDirectory
        func rows(_ name: String) throws -> [(String, String)] {
            let text = try String(contentsOf: dir.appendingPathComponent(name), encoding: .utf8)
            return text.split(separator: "\n").compactMap { line in
                let parts = line.split(separator: "\t", maxSplits: 1)
                guard parts.count == 2 else { return nil }
                return (String(parts[0]), String(parts[1]))
            }
        }
        func phrases(_ name: String) throws -> ([String: String], Int) {
            var map: [String: String] = [:], longest = 1
            for (key, value) in try rows(name) {
                map[key] = value
                longest = max(longest, key.count)
            }
            return (map, longest)
        }
        let (standardPhrases, standardLongest) = try phrases("TWVariantsRevPhrases.tsv")
        let (simplifiedPhrases, longest) = try phrases("TSPhrases.tsv")
        // 台灣用字的單字表反過來查（OpenCC 的 TWVariantsRev 是建置時從 TWVariants 反轉的）；同一個字有兩個來源時用先出現的
        var standard: [Character: Character] = [:]
        for (key, value) in try rows("TWVariants.tsv") where key.count == 1 && value.count == 1 && standard[value.first!] == nil {
            standard[value.first!] = key.first!
        }
        var characters: [Character: Character] = [:]
        for (key, value) in try rows("TSCharacters.tsv") where key.count == 1 && value.count == 1 {
            characters[key.first!] = value.first!
        }
        let result = SimplifiedTables(standardPhrases: standardPhrases, standardLongest: standardLongest, standardCharacters: standard,
                                      phrases: simplifiedPhrases, longest: longest, characters: characters)
        cachedSimplified = result
        return result
    }

    /// 台灣繁體轉簡體（同 OpenCC 的 tw2s，只換字不換用語）；字表讀不到時原樣回傳
    static func toSimplified(_ text: String) -> String {
        guard let tables = try? simplifiedTables() else {
            log.error("simplified tables missing")
            return text
        }
        let standard = convert(Array(text), phrases: tables.standardPhrases, longest: tables.standardLongest, characters: tables.standardCharacters)
        return convert(Array(standard), phrases: tables.phrases, longest: tables.longest, characters: tables.characters)
    }

    /// 詞組最長匹配（只有中日韓的字才可能是詞組的開頭），沒有就查單字表，查不到原樣保留
    private static func convert(_ chars: [Character], phrases: [String: String], longest: Int, characters: [Character: Character]) -> String {
        var out = String()
        var i = 0
        while i < chars.count {
            var matched = false
            if chars[i].unicodeScalars.first.map({ (0x3400...0x9FFF).contains($0.value) || (0xF900...0xFAFF).contains($0.value) }) == true {
                var length = min(longest, chars.count - i)
                while length >= 2 {
                    if let value = phrases[String(chars[i..<i + length])] {
                        out += value
                        i += length
                        matched = true
                        break
                    }
                    length -= 1
                }
            }
            if !matched {
                out.append(characters[chars[i]] ?? chars[i])
                i += 1
            }
        }
        return out
    }

    // MARK: 簡體介面的用語（design §48.1、D156）

    nonisolated(unsafe) private static var cachedTerms: (map: [String: String], longest: Int)?

    /// 用語表（`Resources/l10n/zh-Hans-terms.tsv`）：app bundle 的 Resources/l10n，沒有 bundle 時用原始碼樹的
    private static func terms() -> (map: [String: String], longest: Int) {
        lock.lock(); defer { lock.unlock() }
        if let cachedTerms { return cachedTerms }
        var url = Bundle.main.resourceURL?.appendingPathComponent("l10n/zh-Hans-terms.tsv")
        if url.map({ !FileManager.default.fileExists(atPath: $0.path) }) ?? true {
            url = tablesDirectory.deletingLastPathComponent().appendingPathComponent("l10n/zh-Hans-terms.tsv")
        }
        var map: [String: String] = [:], longest = 1
        if let url, let text = try? String(contentsOf: url, encoding: .utf8) {
            for line in text.split(separator: "\n") {
                let parts = line.split(separator: "\t", maxSplits: 1)
                guard parts.count == 2 else { continue }
                map[String(parts[0])] = String(parts[1])
                longest = max(longest, parts[0].count)
            }
        } else {
            log.error("zh-Hans terms missing")
        }
        cachedTerms = (map, longest)
        return (map, longest)
    }

    /// 介面文字轉成簡體（同 scripts/dev/l10n.py 的 to_simplified_ui）：用語表在繁體原文上最長匹配，換過的不再轉；其餘的字繁轉簡
    static func toSimplifiedUI(_ text: String) -> String {
        let (map, longest) = terms()
        let chars = Array(text)
        var out = String(), gap = String()
        var i = 0
        while i < chars.count {
            var matched = false
            var length = min(longest, chars.count - i)
            while length >= 1 {
                if let value = map[String(chars[i..<i + length])] {
                    out += toSimplified(gap)
                    gap = ""
                    out += value
                    i += length
                    matched = true
                    break
                }
                length -= 1
            }
            if !matched {
                gap.append(chars[i])
                i += 1
            }
        }
        return out + toSimplified(gap)
    }
}

