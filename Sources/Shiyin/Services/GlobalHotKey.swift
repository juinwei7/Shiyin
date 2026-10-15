import Carbon.HIToolbox
import Foundation

/// 全域快捷鍵 ⌃⌥⌘R（R11.3、design §12）：在其他 app 裡也能開始或停止錄音。
/// 用 Carbon 的 RegisterEventHotKey，不需要輔助使用權限。
@MainActor
final class GlobalHotKey {
    static let shared = GlobalHotKey()
    static let display = "⌃⌥⌘R"

    var onPress: (() -> Void)?
    /// 最近一次註冊的結果；被其他 app 佔用時不是 noErr
    private(set) var status: OSStatus = noErr
    var isRegistered: Bool { hotKey != nil }

    private var hotKey: EventHotKeyRef?
    private var handler: EventHandlerRef?

    func setEnabled(_ enabled: Bool) {
        enabled ? register() : unregister()
    }

    /// 按下快捷鍵；DEBUG 指令也用這個模擬
    func fire() { onPress?() }

    private func register() {
        guard hotKey == nil else { return }
        if handler == nil {
            var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
            InstallEventHandler(GetApplicationEventTarget(), { _, _, _ in
                Task { @MainActor in GlobalHotKey.shared.fire() }
                return noErr
            }, 1, &spec, nil, &handler)
        }
        let id = EventHotKeyID(signature: OSType(0x5348_5949), id: 1)   // 'SHYI'
        var ref: EventHotKeyRef?
        // 獨佔註冊：別的 app 已經獨佔這組按鍵時會失敗（設定裡會顯示），也避免兩個 app 同時被觸發
        status = RegisterEventHotKey(UInt32(kVK_ANSI_R), UInt32(cmdKey | optionKey | controlKey), id,
                                     GetApplicationEventTarget(), OptionBits(kEventHotKeyExclusive), &ref)
        hotKey = status == noErr ? ref : nil
        if status != noErr { log.error("global hot key registration failed: \(self.status)") }
    }

    private func unregister() {
        if let hotKey { UnregisterEventHotKey(hotKey) }
        hotKey = nil
        status = noErr
    }
}
