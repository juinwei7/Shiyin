import Foundation

/// 內建辨識的繁體字依詞改正（design §6.2.2、R14.8、D89）。系統的中文辨識像是把簡體逐字換成固定的一個繁體字，
/// 常見「上麵」「隻有」「迴答」「裏麵」。只看常選錯的字，用 OpenCC 的詞組判斷，涵蓋這個字的詞組全部同意才改。
/// 一個字換一個字，每個字的時間不變
enum ChineseVariants {
    static let targets = Set(ChineseVariantsData.targets)
    /// 台灣的寫法跟 OpenCC 不同的詞：不改
    static let keep = ["麵線", "迴轉", "自製"]   // l10n-skip：不是介面文字

    private struct Tables {
        var toSimplified: [Character: Character] = [:]
        var taiwan: [Character: Character] = [:]
        /// 簡體字的繁體寫法，第一個最常見
        var forms: [Character: [Character]] = [:]
        var phrases: [String: [Character]] = [:]
        var longest = 0

        init() {
            func pairs(_ text: String) -> [(Character, Character)] {
                let chars = Array(text)
                return stride(from: 0, to: chars.count - 1, by: 2).map { (chars[$0], chars[$0 + 1]) }
            }
            for (traditional, simplified) in pairs(ChineseVariantsData.traditionalToSimplified) { toSimplified[traditional] = simplified }
            for (variant, standard) in pairs(ChineseVariantsData.taiwanVariants) { taiwan[variant] = standard }
            for line in ChineseVariantsData.characters.split(separator: "\n") where line.count > 2 {
                forms[line.first!] = Array(line.dropFirst())
            }
            for line in ChineseVariantsData.phrases.split(separator: "\n") {
                let parts = line.split(separator: "\t")
                guard parts.count == 2, parts[0].count == parts[1].count else { continue }
                phrases[String(parts[0])] = Array(parts[1])
                longest = max(longest, parts[0].count)
            }
        }
    }

    private static let tables = Tables()

    static func fix(_ text: String) -> String {
        guard text.contains(where: targets.contains) else { return text }
        let t = tables
        let chars = Array(text)
        let simplified = chars.map { t.toSimplified[$0] ?? $0 }
        var protected = Set<Int>()
        for word in keep {
            let w = Array(word)
            for start in chars.indices where chars[start...].starts(with: w) {
                protected.formUnion(start..<(start + w.count))
            }
        }
        var output = chars
        for (i, c) in chars.enumerated() where targets.contains(c) && !protected.contains(i) {
            let s = simplified[i]
            guard let forms = t.forms[s], forms.count > 1 else { continue }
            // 涵蓋這個字的詞組怎麼寫這個字
            var votes = Set<Character>()
            if t.longest >= 2 {
                for length in 2...t.longest where length <= chars.count {
                    for start in max(0, i - length + 1)...min(i, chars.count - length) {
                        if let phrase = t.phrases[String(simplified[start..<(start + length)])] {
                            let form = phrase[i - start]
                            votes.insert(t.taiwan[form] ?? form)
                        }
                    }
                }
            }
            let choice: Character
            if votes.count == 1 {
                choice = votes.first!
            } else if votes.isEmpty {
                // 沒有詞組：不是最常見的寫法才換成最常見的
                choice = t.taiwan[forms[0]] ?? forms[0]
            } else {
                continue   // 詞組意見不一致：不動
            }
            if choice != c && choice.unicodeScalars.count == 1 && c.unicodeScalars.count == 1 { output[i] = choice }
        }
        return String(output)
    }

    /// 改正後照原本每一段（每個字的時間）重組：一個字換一個字，時間不變
    static func fix(_ text: AttributedString) -> AttributedString {
        let original = String(text.characters)
        let fixed = fix(original)
        guard fixed != original, fixed.unicodeScalars.count == original.unicodeScalars.count else { return text }
        var result = AttributedString()
        var rest = fixed.unicodeScalars[...]
        for run in text.runs {
            let count = text[run.range].unicodeScalars.count
            result += AttributedString(String(String.UnicodeScalarView(rest.prefix(count))), attributes: run.attributes)
            rest = rest.dropFirst(count)
        }
        return result
    }
}
