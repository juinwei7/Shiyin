import Foundation
import Security

/// API key 存在 macOS 鑰匙圈（design §8.4、N6），不寫進檔案或 log
enum Keychain {
    static let service = "com.weiwei.shiyin.apikey"

    #if DEBUG
    /// shiyinctl llmkey：測試用的 key 只放記憶體，不寫進鑰匙圈（design §19.5）
    nonisolated(unsafe) static var debugKeys: [String: String] = [:]
    /// DEBUG 版預設不讀真的鑰匙圈：每次重建簽章都不同，讀使用者存的 key 會跳出系統的存取確認（design §20.7）。
    /// 要測鑰匙圈時用啟動參數 -debugRealKeychain YES
    static var usesRealKeychain: Bool { UserDefaults.standard.bool(forKey: "debugRealKeychain") }
    #endif

    static func get(_ account: String) -> String? {
        #if DEBUG
        if let key = debugKeys[account] { return key }
        guard usesRealKeychain else { return nil }
        #endif
        var item: CFTypeRef?
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]
        guard SecItemCopyMatching(query as CFDictionary, &item) == errSecSuccess, let data = item as? Data else { return nil }
        return String(data: data, encoding: .utf8)
    }

    /// 有沒有存這個 key：只讀屬性、不讀內容。讀內容要解密，換了簽章會跳出系統的存取確認；只讀屬性不會（design §20.7、R18.7）
    static func exists(_ account: String) -> Bool {
        #if DEBUG
        if debugKeys[account] != nil { return true }
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

    @discardableResult
    static func set(_ account: String, _ value: String) -> Bool {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        delete(account)
        guard !trimmed.isEmpty else { return true }
        let attributes: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecAttrLabel as String: String(localized: "拾音 API key（\(account)）"),
            kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlock,
            kSecValueData as String: Data(trimmed.utf8),
        ]
        return SecItemAdd(attributes as CFDictionary, nil) == errSecSuccess
    }

    static func delete(_ account: String) {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
        SecItemDelete(query as CFDictionary)
    }
}
