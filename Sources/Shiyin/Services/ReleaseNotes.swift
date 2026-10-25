import Foundation

/// 更新說明的摘要（R10.6，design §12）：tag 的說明文字是「中文一段、空一行、英文一段」，每段第一行是標題、其餘是「- 」開頭的條目。
/// 詢問更新的視窗只列介面語言那一段的前幾項，其餘寫「還有 N 項」；完整說明在官網。Windows 的 ReleaseNotes.cs 同一套規則（標準答案 util/release-notes.json）
enum ReleaseNotes {
    /// 視窗裡最多列幾項
    static let maxItems = 4

    /// 回傳要列出的條目和沒列的項數；條目不含「- 」。沒有條目的說明（只有一兩行字）就拿標題以外的行
    static func brief(_ notes: String, english: Bool) -> (items: [String], more: Int) {
        let lines = notes.replacingOccurrences(of: "\r\n", with: "\n").components(separatedBy: "\n")
        var paragraphs: [[String]] = []
        var current: [String] = []
        for line in lines {
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            if trimmed.isEmpty {
                if !current.isEmpty { paragraphs.append(current); current = [] }
            } else {
                current.append(trimmed)
            }
        }
        if !current.isEmpty { paragraphs.append(current) }
        guard !paragraphs.isEmpty else { return ([], 0) }
        let picked = english
            ? paragraphs.first { $0[0].lowercased().hasPrefix("shiyin") } ?? paragraphs[paragraphs.count - 1]
            : paragraphs.first { $0[0].hasPrefix("拾音") } ?? paragraphs[0]   // l10n-skip
        var items = picked.filter { $0.hasPrefix("- ") }.map { String($0.dropFirst(2)).trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
        if items.isEmpty { items = picked.count > 1 ? Array(picked.dropFirst()) : picked }
        let shown = Array(items.prefix(maxItems))
        return (shown, items.count - shown.count)
    }

    /// 對話框裡的文字：每項前面加「• 」，沒列完的加一行說還有幾項
    static func text(_ notes: String?, english: Bool) -> String {
        guard let notes else { return "" }
        let (items, more) = brief(notes, english: english)
        var out = items.map { "• " + $0 }
        if more > 0 { out.append(String(localized: "……還有 \(more) 項，完整說明在官網。")) }
        return out.joined(separator: "\n")
    }
}
