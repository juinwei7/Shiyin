import Foundation
import os
import Security

/// API key 存在 macOS 鑰匙圈（design §8.4、N6），不寫進檔案或 log
enum Keychain {
    static let service = "com.weiwei.shiyin.apikey"

    /// 讀不到、存不進去的原因（design §22.1、R21.2、R21.4）
    enum AccessError: LocalizedError {
        /// macOS 沒有允許：使用者按「拒絕」、關掉確認視窗
        case denied
        /// 鑰匙圈裡有舊的 key，macOS 不讓這一版取代
        case duplicate(String)
        case readFailed(OSStatus)
        case writeFailed(OSStatus)

        var errorDescription: String? {
            switch self {
            case .denied:
                return String(localized: "macOS 沒有讓拾音讀取鑰匙圈裡的 API key。再試一次時，系統詢問就輸入這台 Mac 的登入密碼（不是 API key），按「永遠允許」；或到設定 → 整理工具重新貼上 key。")
            case .duplicate(let account):
                return String(localized: "鑰匙圈裡已經有舊的 key，macOS 沒有讓拾音取代它。請打開「鑰匙圈存取」，刪除「拾音 API key（\(account)）」後再存一次。")
            case .readFailed(let status):
                return String(localized: "讀取鑰匙圈裡的 API key 失敗（錯誤 \(Int(status))）")
            case .writeFailed(let status):
                return String(localized: "沒辦法把 API key 存進鑰匙圈（錯誤 \(Int(status))）")
            }
        }
    }

    /// 讀到的 key 記在記憶體，拾音開著時同一個 key 只問鑰匙圈一次（design §22.1、D36）
    private static let cache = OSAllocatedUnfairLock(initialState: [String: String]())

    #if DEBUG
    /// shiyinctl llmkey：測試用的 key 只放記憶體，不寫進鑰匙圈（design §19.5）
    nonisolated(unsafe) static var debugKeys: [String: String] = [:]
    /// shiyinctl keychainsim：模擬鑰匙圈回「拒絕」或「已經有舊的」（design §22.1）
    nonisolated(unsafe) static var debugSimulated: [String: String] = [:]
    /// 實際查鑰匙圈的次數（驗證快取用）
    nonisolated(unsafe) static var debugReads = 0
    /// llmkey、keychainsim 改了之後，記在記憶體的 key 要清掉
    static func debugForget(_ account: String) { cache.withLock { $0[account] = nil } }
    /// DEBUG 版預設不讀真的鑰匙圈：每次重建簽章都不同，讀使用者存的 key 會跳出系統的存取確認（design §20.7）。
    /// 要測鑰匙圈時用啟動參數 -debugRealKeychain YES
    static var usesRealKeychain: Bool { UserDefaults.standard.bool(forKey: "debugRealKeychain") }
    #endif

    /// 讀出 key：沒存回傳 nil；macOS 沒允許或其他錯誤丟出 AccessError
    static func read(_ account: String) throws -> String? {
        if let key = cache.withLock({ $0[account] }) { return key }
        #if DEBUG
        debugReads += 1
        if !usesRealKeychain || debugSimulated[account] != nil || debugKeys[account] != nil {
            if debugSimulated[account] == "denied" { throw AccessError.denied }
            guard let key = debugKeys[account] else { return nil }
            cache.withLock { $0[account] = key }
            return key
        }
        #endif
        var item: CFTypeRef?
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]
        let status = SecItemCopyMatching(query as CFDictionary, &item)
        switch status {
        case errSecSuccess:
            guard let data = item as? Data, let key = String(data: data, encoding: .utf8) else { return nil }
            cache.withLock { $0[account] = key }
            return key
        case errSecItemNotFound:
            return nil
        case errSecAuthFailed, errSecUserCanceled, errSecInteractionNotAllowed, errSecNoAccessForItem:
            throw AccessError.denied
        default:
            throw AccessError.readFailed(status)
        }
    }

    /// 不需要分辨原因的地方用：讀不到就是 nil
    /// 存或刪 key 的次數（只在記憶體）：整理工具設定的指紋用，換了 key 就解除暫停（design §34.4）
    nonisolated(unsafe) static var generation = 0 {
        didSet { NotificationCenter.default.post(name: .summarizerStateChanged, object: nil) }
    }

    static func get(_ account: String) -> String? {
        try? read(account)
    }

    /// 有沒有存這個 key：只讀屬性、不讀內容。讀內容要解密，換了簽章會跳出系統的存取確認；只讀屬性不會（design §20.7、R18.7）
    static func exists(_ account: String) -> Bool {
        #if DEBUG
        if debugKeys[account] != nil || debugSimulated[account] != nil { return true }
        guard usesRealKeychain else { return false }
        #endif
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnAttributes as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]
        var item: CFTypeRef?
        return SecItemCopyMatching(query as CFDictionary, &item) == errSecSuccess
    }

    /// 存 key（空字串等於刪除）；存不進去丟出 AccessError
    static func set(_ account: String, _ value: String) throws {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        defer { generation += 1 }   // 存好之後才通知（通知裡會查 exists）
        #if DEBUG
        if debugSimulated[account] == "duplicate" { throw AccessError.duplicate(account) }
        // 沒加 -debugRealKeychain 時只存在記憶體：不能寫進、刪掉使用者真的鑰匙圈（design §20.7）
        if !usesRealKeychain {
            debugKeys[account] = trimmed.isEmpty ? nil : trimmed
            cache.withLock { $0[account] = trimmed.isEmpty ? nil : trimmed }
            return
        }
        #endif
        delete(account)
        guard !trimmed.isEmpty else { return }
        let attributes: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecAttrLabel as String: String(localized: "拾音 API key（\(account)）"),
            kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlock,
            kSecValueData as String: Data(trimmed.utf8),
        ]
        let status = SecItemAdd(attributes as CFDictionary, nil)
        switch status {
        case errSecSuccess: cache.withLock { $0[account] = trimmed }
        case errSecDuplicateItem: throw AccessError.duplicate(account)
        default: throw AccessError.writeFailed(status)
        }
    }

    static func delete(_ account: String) {
        defer { generation += 1 }
        cache.withLock { $0[account] = nil }
        #if DEBUG
        if !usesRealKeychain { debugKeys[account] = nil; return }
        #endif
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
        SecItemDelete(query as CFDictionary)
    }
}
