import Foundation

/// 轉錄排詞彙用的文字（R124、design §98.2）：這場會議、同資料夾之前的幾場、之前的幾場（一段是一場）
struct GlossaryContext {
    var meeting = ""
    var folder: [String] = []
    var recent: [String] = []
}

/// 轉錄時先放跟這場會議有關的詞（R124、design §98.1）：提示有長度上限，放不下的會被略過
enum GlossaryOrder {
    struct Ranked: Equatable {
        var terms: [String]
        /// 前三層（這場、同資料夾、最近幾場出現過）的詞數
        var related: Int
    }

    static func rank(_ terms: [String], _ context: GlossaryContext) -> Ranked {
        let meeting = folded(context.meeting), folder = context.folder.map(folded), recent = context.recent.map(folded)
        let keys = terms.map(folded)
        var placed = Array(repeating: false, count: terms.count)
        var order: [Int] = []
        for i in terms.indices where occurs(keys[i], in: meeting) {
            order.append(i)
            placed[i] = true
        }
        // 幾場出現過，大的先；一樣的照原本的順序
        for texts in [folder, recent] {
            let scored = terms.indices.filter { !placed[$0] }
                .map { i in (i, texts.count(where: { occurs(keys[i], in: $0) })) }
                .filter { $0.1 > 0 }
                .sorted { $0.1 != $1.1 ? $0.1 > $1.1 : $0.0 < $1.0 }
            for (i, _) in scored {
                order.append(i)
                placed[i] = true
            }
        }
        let related = order.count
        order += terms.indices.filter { !placed[$0] }
        return Ranked(terms: order.map { terms[$0] }, related: related)
    }

    /// 英文字母不分大小寫；頭尾是英文字母或數字時，前後不能接著英文字母或數字（R124.2）
    static func occurs(_ term: String, in text: String) -> Bool {
        occurs(folded(term), in: folded(text))
    }

    /// 只把 ASCII 的 A–Z 換成 a–z：系統的大小寫表兩平台不一樣（ß、İ、全形字），比對照 UTF-16 碼位
    private static func folded(_ s: String) -> [UInt16] {
        s.utf16.map { (0x41...0x5A).contains($0) ? $0 + 0x20 : $0 }
    }

    private static func isWordUnit(_ u: UInt16) -> Bool {
        (0x30...0x39).contains(u) || (0x41...0x5A).contains(u) || (0x61...0x7A).contains(u)
    }

    private static func occurs(_ term: [UInt16], in text: [UInt16]) -> Bool {
        guard let first = term.first, let last = term.last, term.count <= text.count else { return false }
        let checkStart = isWordUnit(first), checkEnd = isWordUnit(last)
        var i = 0
        while i <= text.count - term.count {
            if text[i] == first, text[i..<(i + term.count)].elementsEqual(term),
               !(checkStart && i > 0 && isWordUnit(text[i - 1])),
               !(checkEnd && i + term.count < text.count && isWordUnit(text[i + term.count])) {
                return true
            }
            i += 1
        }
        return false
    }
}
