import Carbon.HIToolbox
import Foundation

/// 全域快捷鍵（R11.3、R19.5、design §12、§21.10）：在其他 app 裡也能用。
/// ⌃⌥⌘R 開始或停止錄音；⌃⌥⌘B 錄音中標記重點、⌃⌥⌘S 錄音中截圖（design §83.2）：只在錄音中註冊，不錄音時不佔用這兩組按鍵。
/// 用 Carbon 的 RegisterEventHotKey，不需要輔助使用權限。
@MainActor
final class GlobalHotKey {
    static let shared = GlobalHotKey()
    static let display = "⌃⌥⌘R"
    static let bookmarkDisplay = "⌃⌥⌘B"
    static let screenshotDisplay = "⌃⌥⌘S"

    private static let recordID: UInt32 = 1
    private static let bookmarkID: UInt32 = 2
    private static let screenshotID: UInt32 = 3

    var onPress: (() -> Void)?
    var onBookmark: (() -> Void)?
    var onScreenshot: (() -> Void)?
    /// 最近一次註冊錄音快捷鍵的結果；被其他 app 佔用時不是 noErr
    private(set) var status: OSStatus = noErr
    var isRegistered: Bool { hotKeys[Self.recordID] != nil }
    var isBookmarkRegistered: Bool { hotKeys[Self.bookmarkID] != nil }
    var isScreenshotRegistered: Bool { hotKeys[Self.screenshotID] != nil }
    /// 最近一次註冊截圖快捷鍵的結果（被其他 app 佔用時設定頁說明）
    private(set) var screenshotStatus: OSStatus = noErr

    private var hotKeys: [UInt32: EventHotKeyRef] = [:]
    private var handler: EventHandlerRef?

    func setEnabled(_ enabled: Bool) {
        if enabled {
            status = register(Self.recordID, key: kVK_ANSI_R)
        } else {
            unregister(Self.recordID)
            status = noErr
        }
    }

    /// 錄音開始時打開、停止時關掉（design §21.10）
    func setBookmarkEnabled(_ enabled: Bool) {
        if enabled { _ = register(Self.bookmarkID, key: kVK_ANSI_B) } else { unregister(Self.bookmarkID) }
    }

    func setScreenshotEnabled(_ enabled: Bool) {
        if enabled { screenshotStatus = register(Self.screenshotID, key: kVK_ANSI_S) } else { unregister(Self.screenshotID) }
    }

    /// 按下快捷鍵；DEBUG 指令也用這個模擬
    func fire() { onPress?() }
    func fireBookmark() { onBookmark?() }
    func fireScreenshot() { onScreenshot?() }

    private func register(_ id: UInt32, key: Int) -> OSStatus {
        guard hotKeys[id] == nil else { return noErr }
        if handler == nil {
            var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
            InstallEventHandler(GetApplicationEventTarget(), { _, event, _ in
                var hotKeyID = EventHotKeyID()
                GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID), nil,
                                  MemoryLayout<EventHotKeyID>.size, nil, &hotKeyID)
                let id = hotKeyID.id
                Task { @MainActor in
                    switch id {
                    case GlobalHotKey.bookmarkID: GlobalHotKey.shared.fireBookmark()
                    case GlobalHotKey.screenshotID: GlobalHotKey.shared.fireScreenshot()
                    default: GlobalHotKey.shared.fire()
                    }
                }
                return noErr
            }, 1, &spec, nil, &handler)
        }
        let hotKeyID = EventHotKeyID(signature: OSType(0x5348_5949), id: id)   // 'SHYI'
        var ref: EventHotKeyRef?
        // 獨佔註冊：別的 app 已經獨佔這組按鍵時會失敗（設定裡會顯示），也避免兩個 app 同時被觸發
        let result = RegisterEventHotKey(UInt32(key), UInt32(cmdKey | optionKey | controlKey), hotKeyID,
                                         GetApplicationEventTarget(), OptionBits(kEventHotKeyExclusive), &ref)
        if result == noErr, let ref { hotKeys[id] = ref }
        else { log.error("global hot key \(id) registration failed: \(result)") }
        return result
    }

    private func unregister(_ id: UInt32) {
        if let ref = hotKeys.removeValue(forKey: id) { UnregisterEventHotKey(ref) }
    }
}
