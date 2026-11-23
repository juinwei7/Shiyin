import AppKit
import SwiftUI

/// 錄音島的畫面（design §57.1、§57.2）：黑底白字，接在瀏海兩側（沒有瀏海時是選單列下方的膠囊）。
/// 收起：紅點、時間；提醒：展開成那一句和按鈕；偵測會議：要開始錄音嗎；滑過去：音量、最新一句、控制
struct IslandRoot: View {
    @Environment(AppModel.self) private var model
    let state: IslandState
    @State private var bookmarked = false
    @State private var captured = false
    @State private var leaveTask: Task<Void, Never>?

    /// 瀏海兩側各放多寬（紅點、時間）
    private static let wing: CGFloat = 64

    var body: some View {
        content
            .background(HoverTracker { inside in hoverChanged(inside) })
            .fixedSize()
            .onGeometryChange(for: CGSize.self) { $0.size } action: { RecordingIsland.shared.contentSizeChanged($0) }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
            .environment(\.colorScheme, .dark)
            .font(.system(size: 13))
    }

    @ViewBuilder
    private var content: some View {
        let recorder = model.recorder
        if let prompt = state.prompt {
            expanded(header: promptHeader) { promptBody(prompt) }
        } else if state.hover && recorder.isRecording {
            expanded(header: recordingHeader) { controls }
        } else if let flash = state.flash, recorder.isRecording {
            expanded(header: recordingHeader) { flashBody(flash) }
        } else if state.hintExpanded, let hint = currentHint {
            expanded(header: recordingHeader) { hintBody(hint) }
        } else {
            collapsed
        }
    }

    // MARK: 收起

    @ViewBuilder
    private var collapsed: some View {
        let notch = state.notch
        if notch.hasNotch {
            recordingHeader
                .background(IslandShape(radius: 10, flushTop: true).fill(.black))
        } else {
            HStack(spacing: 8) {
                statusDot
                elapsed
            }
            .padding(.horizontal, 12)
            .frame(height: notch.height)
            .background(Capsule().fill(.black.opacity(0.9)))
            .overlay(Capsule().strokeBorder(.white.opacity(0.1)))
        }
    }

    /// 瀏海兩側：左邊紅點、右邊時間；中間被瀏海擋住的地方不放東西。沒有瀏海時放在一起
    @ViewBuilder
    private var recordingHeader: some View {
        let notch = state.notch
        if notch.hasNotch {
            HStack(spacing: 0) {
                statusDot.frame(width: Self.wing)
                Color.clear.frame(width: notch.width)
                elapsed.frame(width: Self.wing)
            }
            .frame(height: notch.height)
        } else {
            HStack(spacing: 8) {
                statusDot
                elapsed
                Spacer(minLength: 0)
            }
            .frame(height: 30)
        }
    }

    @ViewBuilder
    private var promptHeader: some View {
        let notch = state.notch
        if notch.hasNotch {
            HStack(spacing: 0) {
                Image(systemName: "waveform").foregroundStyle(Color.celadon).frame(width: Self.wing)
                Color.clear.frame(width: notch.width)
                Color.clear.frame(width: Self.wing)
            }
            .frame(height: notch.height)
        } else {
            HStack {
                Image(systemName: "waveform").foregroundStyle(Color.celadon)
                Spacer(minLength: 0)
            }
            .frame(height: 30)
        }
    }

    /// 紅點；暫停時是暫停的圖示；有提醒（收回之後）是琥珀色的點
    @ViewBuilder
    private var statusDot: some View {
        let recorder = model.recorder
        if recorder.isPaused {
            Image(systemName: "pause.fill").font(.system(size: 10)).foregroundStyle(.white.opacity(0.8))
        } else {
            Circle()
                .fill(state.hintKey != nil ? Color.signal : Color.red)
                .frame(width: 8, height: 8)
        }
    }

    private var elapsed: some View {
        Text(TimeFormat.string(Double(model.recorder.elapsedSeconds)))
            .font(.system(size: 12, weight: .semibold).monospacedDigit())
            .foregroundStyle(.white)
    }

    // MARK: 展開

    private func expanded<Header: View, Body: View>(header: Header, @ViewBuilder body: () -> Body) -> some View {
        let notch = state.notch
        let width = max(420, notch.width + 2 * Self.wing)
        return VStack(alignment: .leading, spacing: 0) {
            header.padding(.horizontal, notch.hasNotch ? 0 : 16)
            body()
                .padding(.horizontal, 18)
                .padding(.top, notch.hasNotch ? 10 : 2)
                .padding(.bottom, 16)
        }
        .frame(width: width, alignment: .leading)
        .foregroundStyle(.white)
        .background(IslandShape(radius: 20, flushTop: notch.hasNotch).fill(.black))
        .overlay {
            if !notch.hasNotch { IslandShape(radius: 20, flushTop: false).stroke(.white.opacity(0.1)) }
        }
    }

    /// 滑過去（R81.5）：音量、最新一句即時逐字稿、有提醒時那一句，按鈕
    private var controls: some View {
        let recorder = model.recorder
        return VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 14) {
                HStack(spacing: 6) {
                    Image(systemName: "mic.fill").foregroundStyle(.secondary)
                    LevelMeter(segments: 12) { [recorder] in recorder.micLevel }
                }
                if recorder.hasSystem {
                    HStack(spacing: 6) {
                        Image(systemName: "speaker.wave.2.fill").foregroundStyle(.secondary)
                        LevelMeter(segments: 12) { [recorder] in recorder.systemLevel }
                    }
                }
                Spacer(minLength: 0)
            }
            if let line = latestLine {
                Text(line).foregroundStyle(.secondary).lineLimit(2).frame(maxWidth: .infinity, alignment: .leading)
            }
            if let stopAt = recorder.stopAt {
                // 定時停止（design §84、R110.2）
                Label("\(StopTimer.label(stopAt)) 自動停止錄音", systemImage: "timer").foregroundStyle(.secondary)
            }
            if let hint = currentHint {
                Label(hint.text, systemImage: hint.symbol).foregroundStyle(Color.signal).lineLimit(3)
                    .fixedSize(horizontal: false, vertical: true)
                // 提醒的按鈕也放這裡：展開的提醒一滑過去就換成這個畫面，不放的話「知道了」「繼續錄」永遠按不到
                hintButtons(hint)
            }
            // 按鈕放不下時（加了「截圖」、英文比較長）：先讓「暫停」「打開拾音」只留圖示，再不夠就只有「停止並轉錄」留字
            ViewThatFits(in: .horizontal) {
                controlButtons(iconOnly: [])
                controlButtons(iconOnly: [.pause, .open])
                controlButtons(iconOnly: [.pause, .bookmark, .screenshot, .open])
            }
        }
    }

    /// 截好的畫面：縮圖和時間（design §83.2）
    private func flashBody(_ flash: IslandState.Flash) -> some View {
        HStack(spacing: 12) {
            Image(nsImage: flash.image)
                .resizable()
                .aspectRatio(contentMode: .fit)
                .frame(maxWidth: 120, maxHeight: 68)
                .clipShape(RoundedRectangle(cornerRadius: 6, style: .continuous))
                .overlay(RoundedRectangle(cornerRadius: 6, style: .continuous).strokeBorder(.white.opacity(0.15)))
            VStack(alignment: .leading, spacing: 3) {
                Label("已截圖", systemImage: "camera.viewfinder").font(.system(size: 13, weight: .semibold))
                Text("\(TimeFormat.string(flash.at)) 的畫面，整理時會一起參考").foregroundStyle(.white.opacity(0.65))
            }
            Spacer(minLength: 0)
        }
    }

    private enum ControlButton { case pause, bookmark, screenshot, open }

    private func controlButtons(iconOnly: Set<ControlButton>) -> some View {
        let recorder = model.recorder
        return HStack(spacing: 8) {
            IslandButton(title: recorder.isPaused ? String(localized: "繼續") : String(localized: "暫停"),
                         symbol: recorder.isPaused ? "play.fill" : "pause.fill", iconOnly: iconOnly.contains(.pause)) {
                recorder.isPaused ? recorder.resume() : recorder.pause()
            }
            IslandButton(title: bookmarked ? String(localized: "已標記") : String(localized: "標記重點"),
                         symbol: bookmarked ? "star.fill" : "star", iconOnly: iconOnly.contains(.bookmark)) {
                model.addBookmark(at: recorder.elapsed, meetingId: recorder.meetingId)
                bookmarked = true
                Task { try? await Task.sleep(for: .seconds(1.5)); bookmarked = false }
            }
            // 會議中的畫面（R109.1）：島本身不會被截進去
            IslandButton(title: captured ? String(localized: "已截圖") : String(localized: "截圖"),
                         symbol: captured ? "checkmark" : "camera.viewfinder", iconOnly: iconOnly.contains(.screenshot)) {
                model.captureScreen()
                captured = true
                Task { try? await Task.sleep(for: .seconds(1.5)); captured = false }
            }
            IslandButton(title: String(localized: "打開拾音"), symbol: "macwindow", iconOnly: iconOnly.contains(.open)) {
                if let id = recorder.meetingId { model.reveal(id) }
                NSApp.activate()
            }
            Spacer(minLength: 0)
            IslandButton(title: String(localized: "停止並轉錄"), symbol: "stop.fill", role: .destructive) { recorder.stop() }
        }
    }

    private func hintBody(_ hint: IslandHint) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            Label(hint.text, systemImage: hint.symbol)
                .foregroundStyle(.white)
                .fixedSize(horizontal: false, vertical: true)
            hintButtons(hint)
        }
    }

    @ViewBuilder
    private func hintButtons(_ hint: IslandHint) -> some View {
        if !hint.buttons.isEmpty {
            HStack(spacing: 8) {
                Spacer(minLength: 0)
                ForEach(Array(hint.buttons.enumerated()), id: \.offset) { _, button in
                    IslandButton(title: button.title, role: button.primary ? .primary : .normal, action: button.action)
                }
            }
        }
    }

    private func promptBody(_ prompt: IslandState.Prompt) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            // 會議結束的倒數（R95）：每秒換成剩下的秒數
            if let deadline = prompt.deadline, let countdown = prompt.countdown {
                TimelineView(.periodic(from: .now, by: 1)) { context in
                    Text(countdown(max(0, Int(deadline.timeIntervalSince(context.date).rounded(.up)))))
                        .font(.system(size: 14, weight: .medium)).fixedSize(horizontal: false, vertical: true)
                }
            } else {
                Text(prompt.message).font(.system(size: 14, weight: .medium)).fixedSize(horizontal: false, vertical: true)
            }
            HStack(spacing: 8) {
                Spacer(minLength: 0)
                ForEach(Array(prompt.actions.enumerated()), id: \.offset) { index, action in
                    IslandButton(title: action.title, role: index == prompt.actions.count - 1 ? .primary : .normal, action: action.run)
                }
            }
        }
    }

    // MARK: 資料

    /// 最新一句即時逐字稿（還沒定稿的優先）
    private var latestLine: String? {
        let recorder = model.recorder
        let text = recorder.volatileText.isEmpty ? recorder.liveLines.last?.text ?? "" : recorder.volatileText
        return text.isEmpty ? nil : text
    }

    /// 目前的提醒和它的按鈕，跟錄音畫面的提示列同一句、同樣的按鈕（R81.2）
    private var currentHint: IslandHint? {
        let recorder = model.recorder
        guard recorder.isRecording else { return nil }
        if recorder.idlePrompt {
            return IslandHint(text: String(localized: "好像沒人在說話了，要停止錄音嗎？再 15 分鐘沒人說話會自動停止。"), symbol: "moon.zzz", buttons: [
                .init(title: String(localized: "繼續錄"), primary: false) { recorder.continueAfterIdle() },
                .init(title: String(localized: "停止錄音"), primary: true) { recorder.stop() },
            ])
        }
        if let problem = model.screenshotProblem {
            // 截不到圖（design §83.2）
            switch problem {
            case .denied:
                // 按鈕的順序同收音提醒（打開系統設定、知道了）
                return IslandHint(text: String(localized: "要允許拾音「錄製螢幕」才能截圖。"), symbol: "camera.viewfinder", buttons: [
                    .init(title: String(localized: "打開系統設定"), primary: true) { Recorder.openPrivacySettings("Privacy_ScreenCapture") },
                    .init(title: String(localized: "知道了"), primary: false) { model.screenshotProblem = nil },
                ])
            case .needsRelaunch:
                return IslandHint(text: String(localized: "剛允許的權限要重新打開拾音才會生效，錄完這場再重新打開。"), symbol: "camera.viewfinder",
                                  buttons: [.init(title: String(localized: "知道了"), primary: false) { model.screenshotProblem = nil }])
            }
        }
        if let hint = recorder.micHint {
            var buttons: [IslandHint.Button] = []
            if let builtIn = hint.builtIn {
                buttons.append(.init(title: String(localized: "改用 \(builtIn.name)"), primary: true) { model.switchMicrophone(builtIn.uid) })
            }
            buttons.append(.init(title: String(localized: "知道了"), primary: false) { recorder.dismissMicHint() })
            return IslandHint(text: hint.text, symbol: "headphones", buttons: buttons)
        }
        if let hint = recorder.levelHint {
            var buttons: [IslandHint.Button] = []
            if hint == .systemDenied {
                buttons.append(.init(title: String(localized: "打開系統設定"), primary: true) { Recorder.openPrivacySettings("Privacy_AudioCapture") })
            }
            buttons.append(.init(title: String(localized: "知道了"), primary: false) { recorder.dismissLevelHint() })
            return IslandHint(text: recorder.text(for: hint), symbol: hint.symbol, buttons: buttons)
        }
        if let warning = recorder.deviceWarning {
            return IslandHint(text: warning, symbol: "arrow.triangle.2.circlepath", buttons: [])
        }
        return nil
    }

    /// 滑出去 0.3 秒後才收回（滑過邊界不會閃）
    private func hoverChanged(_ inside: Bool) {
        leaveTask?.cancel()
        if inside {
            if !state.hover { state.hover = true }
        } else {
            leaveTask = Task {
                try? await Task.sleep(for: .seconds(0.3))
                guard !Task.isCancelled else { return }
                state.hover = false
            }
        }
    }
}

struct IslandHint {
    struct Button {
        var title: String
        var primary: Bool
        var action: () -> Void
    }

    var text: String
    var symbol: String
    var buttons: [Button]
}

/// 島上的按鈕：白色半透明的膠囊；主要的是青瓷綠，停止是紅色
private struct IslandButton: View {
    enum Role { case normal, primary, destructive }
    let title: String
    var symbol: String?
    var role: Role = .normal
    /// 放不下時只留圖示（滑過提示和 VoiceOver 照樣是完整的字）
    var iconOnly = false
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(spacing: 5) {
                if let symbol { Image(systemName: symbol).font(.system(size: 11)) }
                if !iconOnly || symbol == nil { Text(title).lineLimit(1) }
            }
            .font(.system(size: 12, weight: .medium))
            .foregroundStyle(.white)
            .padding(.horizontal, iconOnly ? 9 : 11)
            .padding(.vertical, 6)
            .background(Capsule().fill(background))
            .contentShape(Capsule())
        }
        .buttonStyle(.plain)
        .help(iconOnly ? title : "")
        .accessibilityLabel(Text(title))
    }

    private var background: Color {
        switch role {
        case .normal: return .white.opacity(0.14)
        case .primary: return Color(nsColor: .dynamic(light: 0x2D6A5D, dark: 0x2D6A5D))
        case .destructive: return Color(red: 0.75, green: 0.2, blue: 0.2)
        }
    }
}

/// 上緣貼著螢幕（接在瀏海下面）時上面兩個角是直角；沒有瀏海時四個角都是圓角
struct IslandShape: Shape {
    var radius: CGFloat
    var flushTop: Bool

    func path(in rect: CGRect) -> Path {
        flushTop
            ? UnevenRoundedRectangle(topLeadingRadius: 0, bottomLeadingRadius: radius, bottomTrailingRadius: radius,
                                     topTrailingRadius: 0, style: .continuous).path(in: rect)
            : RoundedRectangle(cornerRadius: radius, style: .continuous).path(in: rect)
    }
}

/// 滑鼠進出：拾音不在前面時也要收得到（.activeAlways），SwiftUI 的 onHover 在不是 key 的視窗上不一定收得到
private struct HoverTracker: NSViewRepresentable {
    let changed: (Bool) -> Void

    func makeNSView(context: Context) -> TrackingView {
        let view = TrackingView()
        view.changed = changed
        return view
    }

    func updateNSView(_ view: TrackingView, context: Context) { view.changed = changed }

    final class TrackingView: NSView {
        var changed: ((Bool) -> Void)?

        override func updateTrackingAreas() {
            super.updateTrackingAreas()
            trackingAreas.forEach(removeTrackingArea)
            addTrackingArea(NSTrackingArea(rect: bounds, options: [.mouseEnteredAndExited, .activeAlways, .inVisibleRect], owner: self))
        }

        override func mouseEntered(with event: NSEvent) { changed?(true) }
        override func mouseExited(with event: NSEvent) { changed?(false) }
        override func hitTest(_ point: NSPoint) -> NSView? { nil }
    }
}
