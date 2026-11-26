import AppKit
import SwiftUI

/// 播放列上方的麥克風時間軸（R119.4、design §93.5）：波形、門檻線、建議收錄的區塊。
/// 拖曳空白處新增、拖區塊兩邊調整、點選後 Delete 刪除、右鍵選單、雙擊從那裡播放；點空白處跳播
struct MicTrackView: View {
    @Environment(AppModel.self) private var model
    let detail: MeetingDetail
    let duration: Double
    let currentTime: Double
    /// 錄音時間軸在播放列裡的左邊位置、寬度：時間軸照這個對齊
    var leading: CGFloat = 0
    var timelineWidth: CGFloat = 0
    /// 畫哪一塊（design §93.5）：時間軸自己一列；收合鈕、段數放在議題帶那一列的左邊、右邊
    /// （使用者：「放在議題帶的左邊跟右邊，不會比議題帶高」）
    var part: Part = .track

    enum Part { case track, toggle, info }

    private var collapsed: Bool { model.micTrackCollapsed }
    @State private var selected: Int64?
    @State private var drag: Drag?
    @State private var hoverX: CGFloat?
    @State private var hoverY: CGFloat?
    /// 按住 Control 或 Command：滑鼠的位置畫提示線（R119.11）
    @State private var controlDown = false
    @State private var width: CGFloat = 0
    @State private var confirmClear = false
    /// 捏合開始時的範圍和中心（照開始時算，不是一直乘上去）
    @State private var pinch: (zoom: MicZoom?, anchor: Double)?
    /// 滑鼠在時間軸上時收 ⌃+／⌃−／⌃0 和左右捲動（design §93.5）
    @State private var events = HoverEvents()
    @FocusState private var focused: Bool

    /// 拖曳中：新增（從空白處）或調整（拖某一塊的開始、結束）
    private enum Drag: Equatable {
        case create(from: Double, to: Double)
        case resize(id: Int64, isStart: Bool, start: Double, end: Double)
        /// 拖門檻線（R119.12）
        case threshold(Double)
    }

    static let trackHeight: CGFloat = 34
    /// 收合、展開滑動的時間
    static let slide = 0.22
    /// 滑到區塊邊緣幾 pt 內算拖邊界
    static let edge: CGFloat = 4
    /// 拖不到這麼長不算新增（同偵測的最短長度）
    static let minLength = 0.4
    /// 波形的範圍（dBFS）
    static let floorDb = -60.0

    private var meeting: Meeting { detail.meeting }
    private var regions: [MicRegion] { detail.micRegions }

    var body: some View {
        switch part {
        case .toggle: toggle
        case .info: info
        case .track: trackRow
        }
    }

    /// 時間軸那一列：跟錄音時間軸同一個左邊、同寬。收合鈕在下面議題帶那一列，所以收、開都在同一個位置
    /// （使用者：「讓收合按鈕無論是收還是合都在畫面上同一個位子」）
    private var trackRow: some View {
        HStack(spacing: 0) {
            Color.clear.frame(width: max(0, leading), height: 0)
            // 收合用滑的（使用者：「用 slide 的，不要突然出現跟消失」）：內容一直在，高度在 0 和原本之間動畫，
            // 超出的部分剪掉、同時淡出，播放列跟著慢慢變高變矮
            Group {
                if meeting.micFloorDb == nil || meeting.micThresholdDb == nil && regions.isEmpty {
                    placeholder
                } else {
                    track
                }
            }
            .frame(width: max(0, timelineWidth))
            .frame(height: collapsed ? 0 : Self.trackHeight, alignment: .bottom)
            .opacity(collapsed ? 0 : 1)
            .clipped()
            .allowsHitTesting(!collapsed)
            .accessibilityHidden(collapsed)
            // 門檻的數值：時間軸外面、門檻線的右邊，跟線同高（使用者：「顯示在門檻線的右邊，現在疊在一起看不清楚」）
            thresholdLabel
                .frame(height: collapsed ? 0 : Self.trackHeight, alignment: .top)
                .opacity(collapsed ? 0 : 1)
                .clipped()
            Spacer(minLength: 0)
        }
        .animation(.easeInOut(duration: Self.slide), value: collapsed)
        .alert("清除所有建議收錄？", isPresented: $confirmClear) {
            Button("全部清除", role: .destructive) { model.clearMicRegions(); selected = nil }
            Button("取消", role: .cancel) {}
        } message: {
            Text("麥克風時間軸上的區塊都會拿掉（包括自己新增、調整過的）。之後可以按「重新偵測」再找一次。")
        }
    }

    /// 收合鈕：整顆藥丸都能按（使用者：「只有文字可以觸發收合太難按了」）
    private var toggle: some View {
        Button {
            // 整個切換放進同一個動畫：播放列的高度、底色、上面內容區的大小都跟著時間軸一起滑
            // （使用者：「底部的白色區塊要跟麥克風區塊一起 slide」）
            withAnimation(.easeInOut(duration: Self.slide)) { model.micTrackCollapsed.toggle() }
        } label: {
            HStack(spacing: 4) {
                // 展開時朝上（時間軸在上面，使用者：「展開的 toggle 箭頭改成向上」），收起時轉成朝右
                Image(systemName: "chevron.up")
                    .font(.system(size: 9, weight: .semibold))
                    .rotationEffect(.degrees(collapsed ? 90 : 0))
                Text("麥克風")
            }
            .font(.caption)
            .padding(.horizontal, 9)
            .padding(.vertical, 3)
            .contentShape(Capsule())
        }
        .buttonStyle(PillButtonStyle())
        .help(collapsed ? "展開麥克風時間軸" : "收起麥克風時間軸")
    }

    /// 右邊：段數、偵測中、放大的倍數和「整場」
    private var info: some View {
        HStack(spacing: 6) {
            if !regions.isEmpty { Text("建議收錄 \(regions.count) 段").foregroundStyle(.secondary) }
            if model.micDetecting { ProgressView().controlSize(.mini) }
            if let zoom = model.micZoom, !collapsed {
                Text(verbatim: "×" + Self.zoomText(duration / zoom.span)).foregroundStyle(.secondary).monospacedDigit()
                Button("整場") { model.micZoom = nil }
                    .buttonStyle(.plain)
                    .foregroundStyle(Color.celadon)
                    .help("回到整場（⌘0）")
            }
        }
        .font(.caption)
        .lineLimit(1)
    }

    @ViewBuilder private var placeholder: some View {
        HStack(spacing: 8) {
            if model.micDetecting {
                Text("正在偵測建議收錄…")
            } else if meeting.micFloorDb == nil {
                Text("還沒偵測建議收錄")
                Button("偵測") { model.detectMicRegions() }.controlSize(.small)
            } else {
                Text("麥克風幾乎沒有錄到聲音，沒辦法偵測")
            }
        }
        .font(.caption)
        .foregroundStyle(.secondary)
        .frame(height: Self.trackHeight)
    }

    private var track: some View {
        Canvas { context, size in draw(context, size) }
                .contentShape(Rectangle())
                .gesture(dragGesture)
                .simultaneousGesture(SpatialTapGesture(count: 2).onEnded { value in
                    if let region = region(at: value.location.x) { model.player.seek(to: region.start, play: true) }
                })
                .simultaneousGesture(MagnifyGesture()
                    .onChanged { value in
                        if pinch == nil { pinch = (model.micZoom, time(at: value.startLocation.x)) }
                        guard let pinch else { return }
                        model.micZoom = MicZoom.zoomed(pinch.zoom, duration: duration, factor: value.magnification, anchor: pinch.anchor)
                    }
                    .onEnded { _ in pinch = nil })
                .onContinuousHover { phase in
                    switch phase {
                    case .active(let point):
                        if hoverX == nil { controlDown = Self.isZoomModifier(NSEvent.modifierFlags) }
                        hoverX = point.x
                        hoverY = point.y
                        events.x = point.x
                        events.install { event in handle(event) }
                        (onThreshold(point.y) ? NSCursor.resizeUpDown : edgeHit(at: point.x) != nil ? NSCursor.resizeLeftRight : NSCursor.arrow).set()
                    case .ended:
                        hoverX = nil
                        hoverY = nil
                        controlDown = false
                        events.remove()
                        if drag == nil { NSCursor.arrow.set() }
                    }
                }
                .onDisappear { events.remove() }
                .floatingContextMenu { menu() }
                // 提示線畫在上面一層：滑鼠一動只重畫這條線，不重畫波形
                .overlay(alignment: .topLeading) { guide }
                // 滑鼠位置換成時間用；第一次出現時也要拿到（GeometryReader 的 onAppear 可能還是 0）
                .onGeometryChange(for: CGFloat.self) { $0.size.width } action: { width = $0 }
        .frame(height: Self.trackHeight)
        .focusable()
        .focusEffectDisabled()
        .focused($focused)
        .onKeyPress(keys: [.delete, .deleteForward]) { _ in
            guard let selected else { return .ignored }
            model.deleteMicRegion(selected)
            self.selected = nil
            return .handled
        }
        .onKeyPress(.escape) {
            guard selected != nil else { return .ignored }
            selected = nil
            return .handled
        }
        // 播放中跑出範圍、跳播到範圍外：畫面跟著移過去（design §93.5）
        .onChange(of: currentTime) { old, new in
            guard let zoom = model.micZoom, !zoom.contains(new), zoom.contains(old) || abs(new - old) > 1 else { return }
            model.micZoom = zoom.following(duration: duration, time: new)
        }
        .onChange(of: regions) { _, now in if let selected, !now.contains(where: { $0.id == selected }) { self.selected = nil } }
        .accessibilityElement()
        .accessibilityLabel("麥克風時間軸")
        .accessibilityValue("建議收錄 \(regions.count) 段")
    }

    // MARK: 座標

    /// 看的範圍：放大時是 micZoom，不然是整場
    private var viewStart: Double { model.micZoom?.start ?? 0 }
    private var viewSpan: Double { model.micZoom?.span ?? duration }

    private func time(at x: CGFloat) -> Double {
        guard width > 0, viewSpan > 0 else { return 0 }
        return viewStart + Double(max(0, min(x, width)) / width) * viewSpan
    }

    private func x(of t: Double, width: CGFloat? = nil) -> CGFloat {
        guard viewSpan > 0 else { return 0 }
        return CGFloat((t - viewStart) / viewSpan) * (width ?? self.width)
    }

    /// 放大縮小、提示線的修飾鍵：⌃ 或 ⌘（只按一個）
    static func isZoomModifier(_ flags: NSEvent.ModifierFlags) -> Bool {
        let f = flags.intersection(.deviceIndependentFlagsMask)
        return f.contains(.control) != f.contains(.command)
    }

    static func dbText(_ db: Double) -> String { String(format: db.truncatingRemainder(dividingBy: 1) == 0 ? "%.0f" : "%.1f", db) }

    static func zoomText(_ factor: Double) -> String {
        factor >= 10 ? String(format: "%.0f", factor) : String(format: "%.1f", factor)
    }

    // MARK: 放大縮小（R119.10、design §93.5）

    /// ⌃+／⌃−／⌃0、左右捲動；回傳 nil 是吃掉了
    private func handle(_ event: NSEvent) -> NSEvent? {
        guard !collapsed, duration > 0 else { return event }
        let anchor = time(at: events.x)
        switch event.type {
        case .flagsChanged:
            controlDown = Self.isZoomModifier(event.modifierFlags)
            return event
        case .leftMouseDown:
            // ⌘＋點一下打開選單，同 ⌃＋點一下（右鍵）（R119.11；使用者：「mac 版也可以用 Command」）
            guard event.modifierFlags.contains(.command), hoverX != nil, let window = event.window else { return event }
            FloatingMenuController.shared.show(menu(), at: window.convertPoint(toScreen: event.locationInWindow), from: window)
            return nil
        case .keyDown:
            let flags = event.modifierFlags.intersection(.deviceIndependentFlagsMask)
            // ⌃ 或 ⌘ 都可以；⌘+／⌘− 平常是文字大小，滑鼠在時間軸上時才算放大縮小（D202）
            guard Self.isZoomModifier(flags), !flags.contains(.option),
                  let key = event.charactersIgnoringModifiers else { return event }
            switch key {
            case "=", "+": model.micZoom = MicZoom.zoomed(model.micZoom, duration: duration, factor: MicZoom.keyFactor, anchor: anchor)
            case "-", "_": model.micZoom = MicZoom.zoomed(model.micZoom, duration: duration, factor: 1 / MicZoom.keyFactor, anchor: anchor)
            case "0": model.micZoom = nil
            default: return event
            }
            return nil
        case .scrollWheel:
            guard let zoom = model.micZoom, width > 0 else { return event }
            let shift = event.modifierFlags.contains(.shift)
            let dx = shift && event.scrollingDeltaX == 0 ? event.scrollingDeltaY : event.scrollingDeltaX
            // 上下捲動照舊給頁面；左右的（或按住 Shift）才移動時間軸
            guard dx != 0, shift || abs(event.scrollingDeltaX) > abs(event.scrollingDeltaY) else { return event }
            let points = event.hasPreciseScrollingDeltas ? dx : dx * 10
            model.micZoom = zoom.panned(duration: duration, by: -Double(points / width) * zoom.span)
            return nil
        default:
            return event
        }
    }

    /// 取到 0.1 秒、夾在 0～長度
    private func snapped(_ t: Double) -> Double {
        min(max(0, (t * 10).rounded() / 10), duration)
    }

    /// 門檻線的高度：時間軸的高度是 −60～0 dB
    private func y(ofDb db: Double) -> CGFloat {
        Self.trackHeight - CGFloat(max(0, min(1, (db - Self.floorDb) / -Self.floorDb))) * Self.trackHeight
    }

    /// 滑鼠的高度換成門檻：夾在 −60～−10、取到 0.5 dB（R119.12）
    private func thresholdDb(at y: CGFloat) -> Double {
        let db = Self.floorDb + Double(1 - max(0, min(y, Self.trackHeight)) / Self.trackHeight) * -Self.floorDb
        return min(-10, max(Self.floorDb, (db * 2).rounded() / 2))
    }

    /// 滑鼠在門檻線上下 4 pt 內
    private func onThreshold(_ y: CGFloat) -> Bool {
        guard let threshold = meeting.micThresholdDb else { return false }
        return abs(y - self.y(ofDb: threshold)) <= Self.edge
    }

    private func region(at x: CGFloat) -> MicRegion? {
        let t = time(at: x)
        return regions.first { $0.start <= t && t <= $0.end }
    }

    /// 滑鼠在哪一塊的邊緣：那一塊和是不是開始那一邊
    private func edgeHit(at x: CGFloat) -> (region: MicRegion, isStart: Bool)? {
        var best: (region: MicRegion, isStart: Bool, distance: CGFloat)?
        for r in regions {
            for (t, isStart) in [(r.start, true), (r.end, false)] {
                let d = abs(self.x(of: t) - x)
                if d <= Self.edge, d < (best?.distance ?? .infinity) { best = (r, isStart, d) }
            }
        }
        return best.map { ($0.region, $0.isStart) }
    }

    // MARK: 拖曳

    private var dragGesture: some Gesture {
        DragGesture(minimumDistance: 0)
            .onChanged { value in
                focused = true
                let t = snapped(time(at: value.location.x))
                switch drag {
                case .create(let from, _):
                    drag = .create(from: from, to: t)
                case .threshold:
                    drag = .threshold(thresholdDb(at: value.location.y))
                case .resize(let id, let isStart, let start, let end):
                    // 至少留 0.1 秒，拖過另一邊就停在那裡
                    drag = isStart ? .resize(id: id, isStart: true, start: min(t, end - 0.1), end: end)
                                   : .resize(id: id, isStart: false, start: start, end: max(t, start + 0.1))
                case nil:
                    // 在門檻線上按下去：拖門檻（上下），不新增區塊、不跳播（R119.12）
                    if onThreshold(value.startLocation.y) {
                        if abs(value.translation.height) >= 1 { drag = .threshold(thresholdDb(at: value.location.y)) }
                        return
                    }
                    guard abs(value.translation.width) >= 3 else { return }
                    if let hit = edgeHit(at: value.startLocation.x), let id = hit.region.id {
                        selected = id
                        drag = .resize(id: id, isStart: hit.isStart, start: hit.isStart ? min(t, hit.region.end - 0.1) : hit.region.start,
                                       end: hit.isStart ? hit.region.end : max(t, hit.region.start + 0.1))
                    } else if region(at: value.startLocation.x) == nil {
                        drag = .create(from: snapped(time(at: value.startLocation.x)), to: t)
                    }
                }
            }
            .onEnded { value in
                defer { drag = nil }
                switch drag {
                case .create(let from, let to):
                    let start = min(from, to), end = max(from, to)
                    if end - start >= Self.minLength { model.addMicRegion(start: start, end: end) }
                case .resize(let id, _, let start, let end):
                    model.updateMicRegion(id, start: start, end: end)
                case .threshold(let db):
                    model.setMicThreshold(db)
                case nil:
                    if onThreshold(value.startLocation.y) { return }   // 點一下門檻線：不做事
                    // 沒拖：點到區塊是選取，點空白處是跳播
                    if let r = region(at: value.location.x) { selected = r.id } else {
                        selected = nil
                        model.player.seek(to: time(at: value.location.x), play: false)
                    }
                }
            }
    }

    // MARK: 右鍵選單

    private func menu() -> [MenuEntry] {
        // 從這裡播放：點的位置（R119.11）；從這段開頭播放：區塊的開始
        let here = hoverX.map { time(at: $0) }
        let playHere = MenuEntry.action(String(localized: "從這裡播放"), icon: "play", enabled: here != nil) {
            if let here { model.player.seek(to: here, play: true) }
        }
        if let x = hoverX, let r = region(at: x), let id = r.id {
            selected = id
            return [
                playHere,
                .action(String(localized: "從這段開頭播放"), icon: "backward.end") { model.player.seek(to: r.start, play: true) },
                .divider,
                .action(String(localized: "刪除這段"), icon: "trash", destructive: true) { model.deleteMicRegion(id); selected = nil },
            ]
        }
        return [
            playHere,
            .divider,
            .action(String(localized: "重新偵測"), icon: "arrow.clockwise", enabled: !model.micDetecting) { model.detectMicRegions() },
        ] + (meeting.micThresholdUser == nil ? [] : [
            .action(String(localized: "門檻改回自動"), icon: "slider.horizontal.below.rectangle", enabled: !model.micDetecting) { model.setMicThreshold(nil) },
        ]) + [
            .action(String(localized: "全部清除…"), icon: "trash", destructive: true, enabled: !regions.isEmpty) { confirmClear = true },
        ]
    }

    // MARK: 畫

    /// 正在拖的門檻（R119.12）
    private var draggedThreshold: Double? { if case .threshold(let db) = drag { db } else { nil } }

    /// 門檻的數值：時間軸右邊、跟門檻線同高；拖的時候跟著動。自己設過的是強調色，自動的是次要的顏色
    @ViewBuilder private var thresholdLabel: some View {
        if let threshold = draggedThreshold ?? meeting.micThresholdDb, meeting.micFloorDb != nil {
            let height: CGFloat = 12
            Text("門檻 \(Self.dbText(threshold)) dB")
                .font(.system(size: 9).monospacedDigit())
                .foregroundStyle(draggedThreshold != nil || meeting.micThresholdUser != nil ? Color.signal : Color.secondary)
                .lineLimit(1)
                .fixedSize()
                .frame(height: height)
                // 用 padding 留左邊的空（offset 不會加寬，字的尾巴會被剪掉）
                .padding(.leading, 6)
                .offset(y: min(max(0, y(ofDb: threshold) - height / 2), Self.trackHeight - height))
                .frame(maxHeight: .infinity, alignment: .top)
                .help(meeting.micThresholdUser != nil ? "自己設的門檻；右鍵時間軸的空白處可以改回自動" : "自動算出的門檻；上下拖門檻線可以自己調")
        }
    }

    /// 按住 Control 時滑鼠位置的提示線和時間（R119.11）
    @ViewBuilder private var guide: some View {
        if controlDown, let x = hoverX, width > 0 {
            ZStack(alignment: .topLeading) {
                Rectangle()
                    .fill(Color.primary.opacity(0.55))
                    .frame(width: 1, height: Self.trackHeight)
                    .offset(x: min(max(0, x), width - 1))
                Text(verbatim: TimeFormat.string(time(at: x)))
                    .font(.system(size: 9).monospacedDigit())
                    .padding(.horizontal, 3)
                    .background(.background.opacity(0.85), in: RoundedRectangle(cornerRadius: 2))
                    .fixedSize()
                    .offset(x: x + 48 > width ? max(0, x - 46) : x + 3, y: 1)
            }
            .allowsHitTesting(false)
        }
    }

    private func draw(_ context: GraphicsContext, _ size: CGSize) {
        let w = size.width, h = size.height
        guard duration > 0, w > 0 else { return }
        func y(_ db: Double) -> CGFloat { h - CGFloat(max(0, min(1, (db - Self.floorDb) / -Self.floorDb))) * h }
        func x(of t: Double) -> CGFloat { self.x(of: t, width: w) }

        // 建議收錄的區塊（拖曳中的那塊照拖的位置畫）
        var shown: [(id: Int64?, start: Double, end: Double)] = regions.map { ($0.id, $0.start, $0.end) }
        switch drag {
        case .resize(let id, _, let start, let end): shown = shown.map { $0.id == id ? (id, start, end) : $0 }
        case .create(let from, let to): shown.append((nil, min(from, to), max(from, to)))
        case .threshold, nil: break
        }
        for r in shown where r.end >= viewStart && r.start <= viewStart + viewSpan {
            let left = max(-2, x(of: r.start)), right = min(w + 2, x(of: r.end))
            let rect = CGRect(x: left, y: 0, width: max(1, right - left), height: h)
            context.fill(Path(roundedRect: rect, cornerRadius: 2), with: .color(Color.celadon.opacity(0.18)))
            if r.id == nil || r.id == selected {
                context.stroke(Path(roundedRect: rect.insetBy(dx: 0.5, dy: 0.5), cornerRadius: 2), with: .color(.celadon), lineWidth: 1)
            }
        }

        // 波形：每個像素欄取那段時間裡最大的音量（放大到一欄不到一格時取那一格）
        let levels = model.micLevels
        if !levels.isEmpty, viewSpan > 0 {
            let columns = max(1, Int(w))
            let frames = 1 / MicRegions.Rules.frameSeconds
            var path = Path()
            for c in 0..<columns {
                let from = max(0, Int((viewStart + Double(c) / Double(columns) * viewSpan) * frames))
                let to = max(from + 1, Int((viewStart + Double(c + 1) / Double(columns) * viewSpan) * frames))
                guard from < levels.count else { break }
                var peak = -Double.infinity
                for i in from..<min(to, levels.count) { if let level = levels[i] { peak = max(peak, level) } }
                guard peak > Self.floorDb else { continue }
                let top = y(peak)
                path.addRect(CGRect(x: CGFloat(c), y: top, width: 1, height: h - top))
            }
            context.fill(path, with: .color(.secondary.opacity(0.5)))
        }

        // 門檻線：拖的時候是實線（R119.12）；數值寫在時間軸外面（thresholdLabel）
        if let threshold = draggedThreshold ?? meeting.micThresholdDb {
            let dragging = draggedThreshold != nil
            var line = Path()
            line.move(to: CGPoint(x: 0, y: y(threshold)))
            line.addLine(to: CGPoint(x: w, y: y(threshold)))
            context.stroke(line, with: .color(.signal.opacity(dragging ? 1 : 0.8)),
                           style: StrokeStyle(lineWidth: dragging ? 1.5 : 1, dash: dragging ? [] : [3, 3]))
        }

        // 目前播放位置（放大時在範圍外就不畫）
        if model.micZoom.map({ $0.contains(currentTime) }) ?? true {
            let playhead = CGRect(x: min(w - 2, max(0, x(of: currentTime))), y: 0, width: 2, height: h)
            context.fill(Path(roundedRect: playhead, cornerRadius: 1), with: .color(.celadon))
        }
    }
}

/// 麥克風時間軸的收合鈕：藥丸形，滑過、按下時底色變深（design §93.5）
private struct PillButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View { Pill(configuration: configuration) }

    /// 滑過的狀態要放在 View 裡（ButtonStyle 本身不能有 @State）
    private struct Pill: View {
        let configuration: ButtonStyleConfiguration
        @State private var hovering = false

        var body: some View {
            configuration.label
                .background(Capsule().fill(Color.primary.opacity(configuration.isPressed ? 0.16 : hovering ? 0.11 : 0.06)))
                .onHover { hovering = $0 }
                .animation(.easeOut(duration: 0.12), value: hovering)
        }
    }
}

/// 滑鼠在時間軸上時的本地事件監看（design §93.5）：進來時裝、離開時拿掉，handler 每次更新成最新的
@MainActor
private final class HoverEvents {
    var x: CGFloat = 0
    private var monitor: Any?
    private var handler: ((NSEvent) -> NSEvent?)?

    func install(_ handler: @escaping (NSEvent) -> NSEvent?) {
        self.handler = handler
        guard monitor == nil else { return }
        monitor = NSEvent.addLocalMonitorForEvents(matching: [.keyDown, .scrollWheel, .flagsChanged, .leftMouseDown]) { [weak self] event in
            MainActor.assumeIsolated { self?.handler?(event) ?? event }
        }
    }

    func remove() {
        if let monitor { NSEvent.removeMonitor(monitor) }
        monitor = nil
        handler = nil
    }
}
