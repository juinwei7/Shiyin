import AppKit
import SwiftUI

/// 錄音島（design §57、R81、D165、D166）：錄音中、偵測到會議時，螢幕上方正中間（有瀏海就接在瀏海兩側）的小視窗。
/// 全螢幕的 app 上、每個桌面都看得到；分享畫面、截圖拍不到；不搶焦點
@MainActor
final class RecordingIsland {
    static let shared = RecordingIsland()

    /// 設定「在螢幕上方顯示錄音島」（R81.8），預設開
    static var isEnabled: Bool { AppSettings.recordingIsland }

    let state = IslandState()
    private var panel: IslandPanel?
    private var timer: Timer?
    private var observers: [NSObjectProtocol] = []
    private var contentSize = CGSize(width: 200, height: 32)
    private weak var model: AppModel?

    func install(_ model: AppModel) {
        guard timer == nil else { return }
        self.model = model
        // 錄音的狀態、提醒都在 Recorder 上，每 0.5 秒看一次要不要出現、展開（便宜：只比對幾個值）
        timer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { _ in
            MainActor.assumeIsolated { RecordingIsland.shared.update() }
        }
        // 換 app、換桌面、螢幕設定變了：重新放到目前在用的螢幕
        let workspace = NSWorkspace.shared.notificationCenter
        for name in [NSWorkspace.didActivateApplicationNotification, NSWorkspace.activeSpaceDidChangeNotification] {
            observers.append(workspace.addObserver(forName: name, object: nil, queue: .main) { _ in
                MainActor.assumeIsolated { RecordingIsland.shared.place(animated: false) }
            })
        }
        observers.append(NotificationCenter.default.addObserver(forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: .main) { _ in
            MainActor.assumeIsolated { RecordingIsland.shared.place(animated: false) }
        })
    }

    // MARK: 偵測會議的提醒（R81.3）

    /// deadline、countdown：會議結束的倒數（R95）：訊息每秒換成剩下的秒數，倒數完才收起來（不是 30 秒）。
    /// prompt 是 nil：定時停止的倒數（R110.3）
    func showPrompt(_ prompt: MeetingDetector.Prompt?, actions: [(id: String, title: String, action: () -> Void)], message: String,
                    deadline: Date? = nil, countdown: ((Int) -> String)? = nil) {
        state.prompt = IslandState.Prompt(prompt: prompt, message: message, actions: actions.map { item in
            IslandState.Action(id: item.id, title: item.title, run: { [weak self] in
                self?.closePrompt()
                item.action()
            })
        }, expires: deadline?.addingTimeInterval(2) ?? Date().addingTimeInterval(30), deadline: deadline, countdown: countdown)
        update()
    }

    func perform(_ id: String) { state.prompt?.actions.first { $0.id == id }?.run() }

    // MARK: 截圖（design §83.2）

    /// 截好了：展開 1.5 秒顯示縮圖和「已截圖 12:34」
    func flashScreen(_ image: NSImage, at: Double) {
        state.flash = IslandState.Flash(image: image, at: at, until: Date().addingTimeInterval(1.5))
        update()
    }

    /// 截不到（沒有權限、要重新打開拾音）：當成提醒展開 10 秒
    func showScreenshotProblem() { update() }

    func closePrompt() {
        state.prompt = nil
        update()
    }

    // MARK: 出現、位置

    func update() {
        guard let model else { return }
        state.tick(model.recorder, screenshotProblem: model.screenshotProblem)
        let show = Self.isEnabled && (model.recorder.isRecording || state.prompt != nil)
        if show {
            let panel = ensurePanel(model)
            place(animated: false)
            if !panel.isVisible { panel.orderFrontRegardless() }
        } else if let panel, panel.isVisible {
            panel.orderOut(nil)
            state.hover = false
        }
    }

    /// SwiftUI 的內容量好大小時呼叫：視窗跟著內容（收起時只有一小塊，不擋下面的視窗，R81.6）
    func contentSizeChanged(_ size: CGSize) {
        guard size.width > 0, size.height > 0, size != contentSize else { return }
        contentSize = size
        place(animated: true)
    }

    private func ensurePanel(_ model: AppModel) -> IslandPanel {
        if let panel { return panel }
        let panel = IslandPanel(contentRect: NSRect(x: 0, y: 0, width: 600, height: 300),
                                styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.level = .statusBar
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        panel.backgroundColor = .clear
        panel.isOpaque = false
        panel.hasShadow = false
        panel.hidesOnDeactivate = false
        panel.isMovable = false
        panel.acceptsMouseMovedEvents = true
        // 一律拍不到（R81.7，不看「分享畫面時藏起拾音的視窗」的設定）
        panel.sharingType = .none
        #if DEBUG
        // 驗證：-debugIslandCapture YES 才拍得到；-debugIslandBehind YES 放在桌面層，不會出現在使用者的畫面上（design §57.3）
        if UserDefaults.standard.bool(forKey: "debugIslandCapture") { panel.sharingType = .readOnly }
        if UserDefaults.standard.bool(forKey: "debugIslandBehind") { panel.level = NSWindow.Level(Int(CGWindowLevelForKey(.desktopWindow)) + 1) }
        #endif
        let hosting = IslandHostingView(rootView: IslandRoot(state: state).environment(model))
        hosting.safeAreaRegions = []
        panel.contentView = hosting
        self.panel = panel
        return panel
    }

    func place(animated: Bool) {
        guard let panel, let screen = NSScreen.main ?? NSScreen.screens.first else { return }
        let notch = IslandNotch(screen: screen)
        if state.notch != notch { state.notch = notch }
        let width = max(contentSize.width, 1), height = max(contentSize.height, 1)
        let midX = notch.midX ?? screen.frame.midX
        // 有瀏海：上緣貼著螢幕的上緣；沒有：選單列下方 6 pt
        let top = notch.hasNotch ? screen.frame.maxY : screen.frame.maxY - notch.menuBarHeight - 6
        let frame = NSRect(x: (midX - width / 2).rounded(), y: top - height, width: width, height: height)
        guard panel.frame != frame else { return }
        if animated && panel.isVisible {
            NSAnimationContext.runAnimationGroup { context in
                context.duration = 0.2
                context.timingFunction = CAMediaTimingFunction(name: .easeOut)
                panel.animator().setFrame(frame, display: true)
            }
        } else {
            panel.setFrame(frame, display: true)
        }
    }

    #if DEBUG
    /// shiyinctl state 的 island（design §57.3）
    var debugInfo: [String: Any] {
        ["visible": panel?.isVisible ?? false, "mode": state.debugMode(model?.recorder),
         "frame": panel.map { [$0.frame.minX, $0.frame.minY, $0.frame.width, $0.frame.height] } ?? [],
         "notch": state.notch.hasNotch, "windowNumber": panel?.windowNumber ?? 0,
         "prompt": state.prompt?.message ?? "", "hintKey": state.hintKey ?? ""]
    }
    #endif
}

/// 錄音島的狀態：滑過去、偵測會議的提醒、提醒展開到什麼時候（design §57.2）
@MainActor
@Observable
final class IslandState {
    struct Action {
        var id: String
        var title: String
        var run: () -> Void
    }

    struct Prompt {
        /// 會議 app 的提醒；nil 是定時停止的倒數（R110.3）
        var prompt: MeetingDetector.Prompt?
        var message: String
        var actions: [Action]
        var expires: Date
        /// 倒數到什麼時候、訊息怎麼寫（R95）；nil 是一般的提醒
        var deadline: Date? = nil
        var countdown: ((Int) -> String)? = nil
    }

    struct Flash {
        var image: NSImage
        var at: Double
        var until: Date
    }

    var hover = false
    var prompt: Prompt?
    /// 剛截好的畫面（design §83.2）
    var flash: Flash?
    /// 新的提醒出現後展開 10 秒（R81.2），之後收回成琥珀色的點
    private(set) var hintExpanded = false
    private(set) var hintKey: String?
    @ObservationIgnored private var expandedUntil = Date.distantPast
    var notch = IslandNotch.none

    /// 每 0.5 秒：提醒換了就展開 10 秒；偵測會議的提醒 30 秒沒按就收起來；截圖的縮圖 1.5 秒後收起來
    func tick(_ recorder: Recorder, screenshotProblem: ScreenShot.Failure? = nil) {
        let now = Date()
        if let prompt, prompt.expires < now { self.prompt = nil }
        if let flash, flash.until < now { self.flash = nil }
        let key = Self.hintKey(recorder, screenshotProblem: screenshotProblem)
        if key != hintKey {
            hintKey = key
            if key != nil { expandedUntil = now.addingTimeInterval(10) }
        }
        let expanded = key != nil && now < expandedUntil
        if expanded != hintExpanded { hintExpanded = expanded }
    }

    /// 目前的提醒是哪一個（順序同錄音畫面的提示列）：沒人說話 → 截不到圖 → 藍牙 → 收音 → 重新連接
    static func hintKey(_ recorder: Recorder, screenshotProblem: ScreenShot.Failure? = nil) -> String? {
        guard recorder.isRecording else { return nil }
        if recorder.idlePrompt { return "idle" }
        if let screenshotProblem { return "screenshot:\(screenshotProblem)" }
        if let mic = recorder.micHint { return "mic:\(mic.name)" }
        if let hint = recorder.levelHint { return "level:\(hint.rawValue)" }
        if let warning = recorder.deviceWarning { return "device:\(warning)" }
        return nil
    }

    #if DEBUG
    func debugMode(_ recorder: Recorder?) -> String {
        if prompt != nil { return "prompt" }
        guard recorder?.isRecording == true else { return "hidden" }
        if hover { return "hover" }
        if flash != nil { return "flash" }
        if hintExpanded { return "hint" }
        return "collapsed"
    }
    #endif
}

/// 瀏海的位置和大小；沒有瀏海時是選單列的高度
struct IslandNotch: Equatable {
    var width: CGFloat
    var height: CGFloat
    var midX: CGFloat?
    var menuBarHeight: CGFloat

    var hasNotch: Bool { midX != nil }

    static let none = IslandNotch(width: 0, height: 26, midX: nil, menuBarHeight: 24)

    init(width: CGFloat, height: CGFloat, midX: CGFloat?, menuBarHeight: CGFloat) {
        self.width = width
        self.height = height
        self.midX = midX
        self.menuBarHeight = menuBarHeight
    }

    init(screen: NSScreen) {
        let menuBar = max(0, screen.frame.maxY - screen.visibleFrame.maxY)
        var noNotch = false
        #if DEBUG
        noNotch = UserDefaults.standard.bool(forKey: "debugIslandNoNotch")
        #endif
        // 瀏海＝螢幕的寬度扣掉左右兩塊選單列
        if !noNotch, screen.safeAreaInsets.top > 0, let left = screen.auxiliaryTopLeftArea, let right = screen.auxiliaryTopRightArea {
            let width = screen.frame.width - left.width - right.width
            self.init(width: width, height: screen.safeAreaInsets.top, midX: screen.frame.minX + left.width + width / 2, menuBarHeight: menuBar)
        } else {
            self.init(width: 0, height: 26, midX: nil, menuBarHeight: menuBar)
        }
    }
}

/// 不會變成 key、可以放在選單列上的視窗
final class IslandPanel: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
    override func constrainFrameRect(_ frameRect: NSRect, to screen: NSScreen?) -> NSRect { frameRect }
}

/// 拾音不在前面時，第一下就按得到島上的按鈕
final class IslandHostingView<Content: View>: NSHostingView<Content> {
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
}
