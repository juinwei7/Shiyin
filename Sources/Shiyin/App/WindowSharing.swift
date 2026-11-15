import AppKit

/// 分享畫面時藏起拾音的視窗（design §53、R77、D163）：設定打開時每個視窗的 sharingType 都是 .none，
/// 分享整個螢幕、截圖、錄影都拍不到，自己的螢幕照常看得到。
/// 選單列的小視窗、偵測會議的浮動提醒不會變成 key，出現時會收到 occlusion 的通知，所以也聽它
@MainActor
enum WindowSharing {
    private static var observers: [NSObjectProtocol] = []
    private static var applied: Bool?

    static func install() {
        guard observers.isEmpty else { return }
        let center = NotificationCenter.default
        for name in [NSWindow.didBecomeKeyNotification, NSWindow.didBecomeMainNotification, NSWindow.didChangeOcclusionStateNotification] {
            observers.append(center.addObserver(forName: name, object: nil, queue: .main) { note in
                MainActor.assumeIsolated { if let window = note.object as? NSWindow { apply(to: window) } }
            })
        }
        // 設定頁改了開關：馬上套用到開著的視窗（R77.3）。任何設定改了都會通知，只比對這一個值
        observers.append(center.addObserver(forName: UserDefaults.didChangeNotification, object: nil, queue: .main) { _ in
            MainActor.assumeIsolated { if applied != AppSettings.hideFromScreenSharing { applyAll() } }
        })
        applyAll()
    }

    static func applyAll() {
        applied = AppSettings.hideFromScreenSharing
        NSApp.windows.forEach(apply(to:))
    }

    static func apply(to window: NSWindow) {
        let type: NSWindow.SharingType = AppSettings.hideFromScreenSharing ? .none : .readOnly
        if window.sharingType != type { window.sharingType = type }
    }
}
