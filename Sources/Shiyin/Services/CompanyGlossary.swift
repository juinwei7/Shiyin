import Foundation
import os

extension Notification.Name {
    /// 公司詞彙表換了（R53）：畫面、建議的新詞要重算
    static let companyGlossaryChanged = Notification.Name("companyGlossaryChanged")
}

/// 公司詞彙表（R53、design §36.3）：企業伺服器後台維護的詞，存在 SHIYIN_HOME/enterprise-glossary.json，跟自己的詞一起用。
/// 轉錄、整理在背景執行緒讀，所以不綁 MainActor，用鎖保護
enum CompanyGlossary {
    private struct Stored: Codable {
        var terms: [String]
        var updatedAt: String?
    }

    private static let cache = OSAllocatedUnfairLock<[String]?>(initialState: nil)
    static var file: URL { Paths.home.appendingPathComponent("enterprise-glossary.json") }

    /// 上次從伺服器拿到的（連不上時照樣用）
    static var terms: [String] {
        cache.withLock { cached in
            if let cached { return cached }
            let loaded = (try? Data(contentsOf: file)).flatMap { try? JSONDecoder().decode(Stored.self, from: $0) }?.terms ?? []
            cached = loaded
            return loaded
        }
    }

    /// 伺服器給的詞的上限（design §36.2，同 Windows）：最多 500 個、每個最多 100 字（UTF-16 單位），太長的略過
    static let maxTerms = 500
    static let maxTermLength = 100

    static func save(_ terms: [String], updatedAt: String?) {
        let cleaned = limited(terms)
        guard cleaned != self.terms else { return }
        if let data = try? JSONEncoder().encode(Stored(terms: cleaned, updatedAt: updatedAt)) {
            try? FileManager.default.createDirectory(at: Paths.home, withIntermediateDirectories: true)
            try? data.write(to: file, options: .atomic)
        }
        cache.withLock { $0 = cleaned }
        DispatchQueue.main.async { NotificationCenter.default.post(name: .companyGlossaryChanged, object: nil) }
    }

    /// 登出時刪掉（R53.4）
    static func clear() {
        try? FileManager.default.removeItem(at: file)
        cache.withLock { $0 = [] }
        DispatchQueue.main.async { NotificationCenter.default.post(name: .companyGlossaryChanged, object: nil) }
    }

    /// 自己的詞在前、公司的接在後面，不分大小寫去掉重複（轉錄的提示詞有長度上限，R53.2）。
    /// 沒有公司的詞時照原本的詞彙表、一個字都不動：沒登入的人跟 4.1.2 一樣（R52.5；自己存了「API」和「api」兩個都留著）
    static func merge(_ own: [String], _ company: [String]) -> [String] {
        company.isEmpty ? own : clean(own + company)
    }

    /// 去掉空白、重複之後，略過太長的、只留前 maxTerms 個
    static func limited(_ terms: [String]) -> [String] {
        Array(clean(terms).filter { $0.utf16.count <= maxTermLength }.prefix(maxTerms))
    }

    private static func clean(_ terms: [String]) -> [String] {
        var seen = Set<String>()
        return terms.map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
            .filter { !$0.isEmpty && seen.insert($0.lowercased()).inserted }
    }

    static func merged(_ own: [String]) -> [String] { merge(own, terms) }

    static func contains(_ term: String) -> Bool {
        let key = term.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        return terms.contains { $0.lowercased() == key }
    }
}
