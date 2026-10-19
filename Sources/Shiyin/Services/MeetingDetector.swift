import AppKit
import CoreAudio
import SwiftUI

/// 偵測線上會議（design §21.7、R19.4、D31）：已知的會議 app 開始用麥克風、拾音沒在錄音時提醒開始；
/// 錄音期間用過麥克風的會議 app 都停了，提醒停止。只提醒，不自動開始或停止
@MainActor
final class MeetingDetector {
    struct MeetingApp: Equatable {
        var bundleID: String      // 已知名單裡的 ID（helper 程序也歸到主程式）
        var name: String
    }

    enum Prompt: Equatable {
        case start(MeetingApp)
        case stop(MeetingApp)
    }

    /// 已知的會議 app
    static let meetingApps: [String: String] = [
        "us.zoom.xos": "Zoom", "com.microsoft.teams2": "Microsoft Teams", "com.microsoft.teams": "Microsoft Teams",
        "Cisco-Systems.Spark": "Webex", "com.cisco.webexmeetingsapp": "Webex", "com.apple.FaceTime": "FaceTime",
        "com.tinyspeck.slackmacgap": "Slack", "jp.naver.line.mac": "LINE", "com.hnc.Discord": "Discord",
    ]
    /// 瀏覽器：可能在開 Google Meet
    static let browsers: [String: String] = [
        "com.google.Chrome": "Chrome", "com.apple.Safari": "Safari", "com.microsoft.edgemac": "Edge",
        "company.thebrowser.Browser": "Arc", "org.mozilla.firefox": "Firefox", "com.brave.Browser": "Brave",
    ]
    static let mutedKey = "meetingDetectionMuted"

    /// 要提醒時呼叫（AppModel 決定用通知還是浮動視窗）
    var onPrompt: ((Prompt) -> Void)?
    var isRecording: () -> Bool = { false }

    private var timer: Timer?
    private var activeSince: [String: Date] = [:]
    private var inactiveSince: [String: Date] = [:]
    private var promptedThisCall: Set<String> = []
    private var usedDuringRecording: Set<String> = []
    private var stopPrompted = false
    #if DEBUG
    /// shiyinctl meetingapp：模擬某個 app 開始、停止用麥克風
    var simulated: [String: Bool] = [:]
    #endif

    static var enabled: Bool { UserDefaults.standard.object(forKey: AppSettings.meetingDetectionKey) == nil
        ? true : UserDefaults.standard.bool(forKey: AppSettings.meetingDetectionKey) }

    static var muted: [String] { UserDefaults.standard.stringArray(forKey: mutedKey) ?? [] }

    static func mute(_ bundleID: String) {
        var list = muted
        if !list.contains(bundleID) { list.append(bundleID) }
        UserDefaults.standard.set(list, forKey: mutedKey)
    }

    static func unmute(_ bundleID: String) {
        UserDefaults.standard.set(muted.filter { $0 != bundleID }, forKey: mutedKey)
    }

    static func displayName(_ bundleID: String) -> String {
        meetingApps[bundleID] ?? browsers[bundleID].map { String(localized: "\($0)（可能是 Google Meet）") } ?? bundleID
    }

    func start() {
        guard timer == nil else { return }
        let timer = Timer(timeInterval: 2, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.tick() }
        }
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
    }

    private func tick() {
        let now = Date()
        let recording = isRecording()
        let using = Self.enabled ? currentMeetingApps() : []
        for bundle in Set(activeSince.keys).union(using) {
            if using.contains(bundle) {
                inactiveSince[bundle] = nil
                if activeSince[bundle] == nil { activeSince[bundle] = now }
                if recording { usedDuringRecording.insert(bundle) }
                // 持續用麥克風 5 秒、這次通話還沒提醒過、沒有選「不要再提醒」
                if !recording, !promptedThisCall.contains(bundle), !Self.muted.contains(bundle),
                   now.timeIntervalSince(activeSince[bundle]!) >= 5 {
                    promptedThisCall.insert(bundle)
                    log.info("meeting app started using the microphone: \(bundle, privacy: .public)")
                    onPrompt?(.start(MeetingApp(bundleID: bundle, name: Self.displayName(bundle))))
                }
            } else {
                // 停止用麥克風 10 秒才算這次通話結束
                if inactiveSince[bundle] == nil { inactiveSince[bundle] = now }
                if now.timeIntervalSince(inactiveSince[bundle]!) >= 10 {
                    activeSince[bundle] = nil
                    inactiveSince[bundle] = nil
                    promptedThisCall.remove(bundle)
                }
            }
        }
        if recording {
            // 錄音期間用過麥克風的會議 app 都停了 10 秒：會議好像結束了
            if !stopPrompted, let any = usedDuringRecording.first, usedDuringRecording.allSatisfy({ activeSince[$0] == nil }) {
                stopPrompted = true
                log.info("meeting apps stopped using the microphone")
                onPrompt?(.stop(MeetingApp(bundleID: any, name: Self.displayName(any))))
            }
        } else {
            usedDuringRecording = []
            stopPrompted = false
        }
    }

    /// 正在用麥克風的已知會議 app（拾音自己、系統程序不算）
    private func currentMeetingApps() -> Set<String> {
        var result = Set<String>()
        for object in Self.processObjects() {
            guard Self.isRunningInput(object), let bundle = Self.bundleID(object) else { continue }
            if let known = Self.known(bundle) { result.insert(known) }
        }
        #if DEBUG
        // 模擬的 app 也經過同一套對照（helper 程序歸到主程式）
        for (bundle, on) in simulated {
            guard let known = Self.known(bundle) else { continue }
            if on { result.insert(known) } else { result.remove(known) }
        }
        #endif
        return result
    }

    /// 已知名單裡的 ID：完全相同，或是它的 helper（前綴相同，例如 com.google.Chrome.helper）
    static func known(_ bundle: String) -> String? {
        let all = Array(meetingApps.keys) + Array(browsers.keys)
        return all.first { bundle == $0 || bundle.hasPrefix($0 + ".") }
    }

    // MARK: Core Audio 的程序物件（macOS 14.2+，不需要權限）

    private static func processObjects() -> [AudioObjectID] {
        var address = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyProcessObjectList,
                                                 mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size) == noErr else { return [] }
        var ids = [AudioObjectID](repeating: 0, count: Int(size) / MemoryLayout<AudioObjectID>.size)
        guard AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size, &ids) == noErr else { return [] }
        return ids
    }

    private static func isRunningInput(_ object: AudioObjectID) -> Bool {
        var address = AudioObjectPropertyAddress(mSelector: kAudioProcessPropertyIsRunningInput,
                                                 mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        var value: UInt32 = 0
        var size = UInt32(MemoryLayout<UInt32>.size)
        return AudioObjectGetPropertyData(object, &address, 0, nil, &size, &value) == noErr && value != 0
    }

    private static func bundleID(_ object: AudioObjectID) -> String? {
        var address = AudioObjectPropertyAddress(mSelector: kAudioProcessPropertyBundleID,
                                                 mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        var value: Unmanaged<CFString>?
        var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        guard AudioObjectGetPropertyData(object, &address, 0, nil, &size, &value) == noErr, let value else { return nil }
        let bundle = value.takeRetainedValue() as String
        return bundle.isEmpty ? nil : bundle
    }
}

/// 沒有通知權限時的提醒：不搶焦點的小浮動視窗，右上角，30 秒後自己收起（design §21.7）
@MainActor
final class MeetingPromptPanel {
    static let shared = MeetingPromptPanel()
    private var panel: NSPanel?
    private var closeTask: Task<Void, Never>?
    #if DEBUG
    /// -debugQuietPrompts YES：不顯示（驗證時不要跳到使用者的畫面上），只記下目前的提醒
    private(set) var current: MeetingDetector.Prompt?
    var quiet: Bool { UserDefaults.standard.bool(forKey: "debugQuietPrompts") }
    #endif
    /// 目前提醒的按鈕動作（DEBUG 的 meetingaction 也用）
    private(set) var actions: [String: () -> Void] = [:]

    func show(_ prompt: MeetingDetector.Prompt, actions: [(id: String, title: String, action: () -> Void)], message: String) {
        close()
        var map: [String: () -> Void] = [:]
        for item in actions { map[item.id] = { [weak self] in self?.close(); item.action() } }
        self.actions = map
        #if DEBUG
        current = prompt
        if quiet { return }
        #endif
        let content = MeetingPromptView(message: message, buttons: actions.map { item in (item.title, map[item.id]!) })
        let panel = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 360, height: 120),
                            styleMask: [.nonactivatingPanel, .titled, .fullSizeContentView], backing: .buffered, defer: false)
        panel.titleVisibility = .hidden
        panel.titlebarAppearsTransparent = true
        panel.isFloatingPanel = true
        panel.level = .floating
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        panel.hidesOnDeactivate = false
        panel.contentView = NSHostingView(rootView: content)
        panel.setContentSize(panel.contentView!.fittingSize)
        if let screen = NSScreen.main?.visibleFrame {
            panel.setFrameOrigin(NSPoint(x: screen.maxX - panel.frame.width - 16, y: screen.maxY - panel.frame.height - 16))
        }
        panel.orderFrontRegardless()
        self.panel = panel
        closeTask = Task { [weak self] in
            try? await Task.sleep(for: .seconds(30))
            guard !Task.isCancelled else { return }
            self?.close()
        }
    }

    func perform(_ id: String) { actions[id]?() }

    func close() {
        closeTask?.cancel()
        panel?.orderOut(nil)
        panel = nil
        actions = [:]
        #if DEBUG
        current = nil
        #endif
    }
}

private struct MeetingPromptView: View {
    let message: String
    let buttons: [(String, () -> Void)]

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Label(message, systemImage: "waveform.circle.fill")
                .font(.callout)
                .fixedSize(horizontal: false, vertical: true)
            HStack {
                Spacer()
                ForEach(Array(buttons.enumerated()), id: \.offset) { index, button in
                    if index == buttons.count - 1 {
                        Button(button.0, action: button.1).buttonStyle(.borderedProminent)
                    } else {
                        Button(button.0, action: button.1)
                    }
                }
            }
        }
        .padding(16)
        .frame(width: 360)
        .tint(.celadon)
    }
}
