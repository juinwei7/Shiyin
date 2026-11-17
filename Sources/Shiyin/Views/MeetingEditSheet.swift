import AppKit
import SwiftUI

/// 編輯畫面打開的那場（sheet(item:) 要 Identifiable）
struct EditingMeeting: Identifiable { let id: Int64 }

/// 編輯會議（R107、design §81.5）：時間軸（議題帶、波形、把手、刪掉的段落、選取、切點）＋逐字稿＋套用
struct MeetingEditSheet: View {
    let meetingId: Int64
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    @State private var editor: MeetingEditor
    @State private var confirmation: ConfirmRequest?
    @State private var showMergePicker = false
    @FocusState private var timelineFocused: Bool

    /// sheet 照內容的最小尺寸開：依主視窗的大小給，比主視窗小一點（時間軸越寬越好拖），最寬 1000、最高 760
    private let size: CGSize

    init(meetingId: Int64) {
        self.meetingId = meetingId
        _editor = State(initialValue: MeetingEditor(meetingId: meetingId))
        let parent = (NSApp.windows.first { $0.identifier?.rawValue == "main" } ?? NSApp.mainWindow)?.frame.size ?? CGSize(width: 1200, height: 800)
        size = CGSize(width: min(1000, max(760, parent.width - 80)), height: min(760, max(580, parent.height - 60)))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            SheetHeader(title: String(localized: "編輯會議"), subtitle: subtitle, onClose: cancel)
            EditTimelinePanel(editor: editor, focused: $timelineFocused)
            transportRow
            actionsRow
            EditTranscriptList(editor: editor)
            footer
        }
        .padding(24)
        .frame(width: size.width, height: size.height)
        .onAppear {
            if model.player.isPlaying { model.player.toggle() }
            editor.load(db: model.db)
            timelineFocused = true
        }
        .onDisappear { editor.stopPlaying() }
        .sheet(isPresented: $showMergePicker) {
            MergePickerSheet(meetingId: meetingId) { other in editor.merge(with: other, db: model.db) }
        }
        .sheet(item: $confirmation) { ConfirmSheet(request: $0) }
    }

    private var subtitle: String {
        guard let own = editor.ownPart else { return "" }
        var text = String(localized: "\(own.title)・原本 \(TimeFormat.string(own.length))")
        if let other = editor.mergedPart { text += String(localized: "（合併「\(other.title)」\(TimeFormat.string(other.length))）") }
        return text
    }

    // MARK: 播放列

    private var transportRow: some View {
        HStack(spacing: 12) {
            Button { editor.togglePlay() } label: {
                Image(systemName: editor.isPlaying ? "pause.fill" : "play.fill")
                    .font(.system(size: 12, weight: .semibold))
                    .frame(width: 30, height: 30)
                    .background(Circle().fill(Color.primary.opacity(0.1)))
                    .contentShape(Circle())
            }
            .buttonStyle(.plain)
            .help(editor.isPlaying ? String(localized: "暫停（空白鍵）") : String(localized: "試聽（空白鍵）：跳過刪掉、裁掉的部分"))
            .accessibilityLabel(editor.isPlaying ? String(localized: "暫停") : String(localized: "試聽"))
            Text(TimeFormat.string(editor.playhead))
                .font(.system(size: 15).monospacedDigit())
            Text("試聽時跳過刪掉、裁掉的部分")
                .font(.callout).foregroundStyle(.secondary)
                .lineLimit(1)
                .layoutPriority(-1)   // 窄的時候先縮這句，說話者的名字留完整
            Spacer(minLength: 8)
            speakerLegend
            zoomControl
        }
    }

    private var speakerLegend: some View {
        let speakers = editor.lines.reduce(into: [(name: String, idx: Int)]()) { list, line in
            guard let idx = line.speaker, !list.contains(where: { $0.name == line.speakerName }) else { return }
            list.append((line.speakerName, idx))
        }
        return HStack(spacing: 10) {
            ForEach(speakers.prefix(4), id: \.name) { s in
                HStack(spacing: 5) {
                    Circle().fill(Color.speaker(s.idx)).frame(width: 8, height: 8)
                    Text(s.name).font(.callout).lineLimit(1).fixedSize()
                }
            }
            if speakers.count > 4 { Text("+\(speakers.count - 4)").font(.callout).foregroundStyle(.secondary) }
        }
        .accessibilityElement(children: .combine)
    }

    private var zoomControl: some View {
        @Bindable var editor = editor
        return HStack(spacing: 4) {
            Button { editor.zoom = max(1, editor.zoom / 1.5) } label: { Image(systemName: "minus.magnifyingglass") }
                .buttonStyle(.borderless).help("縮小時間軸").disabled(editor.zoom <= 1)
            Slider(value: Binding(get: { Foundation.log(editor.zoom) }, set: { editor.zoom = exp($0) }), in: 0...Foundation.log(40.0))
                .controlSize(.small).frame(width: 80)
                .accessibilityLabel("時間軸的縮放")
            Button { editor.zoom = min(40, editor.zoom * 1.5) } label: { Image(systemName: "plus.magnifyingglass") }
                .buttonStyle(.borderless).help("放大時間軸（也可以在時間軸上用觸控板捏合）").disabled(editor.zoom >= 40)
        }
        .foregroundStyle(.secondary)
    }

    // MARK: 動作

    private var actionsRow: some View {
        HStack(spacing: 8) {
            Button { editor.setStart(editor.playhead) } label: { Label("設成開頭", systemImage: "arrow.right.to.line") }
                .help("把開頭的把手移到播放頭：前面的部分裁掉")
            Button { editor.setEnd(editor.playhead) } label: { Label("設成結尾", systemImage: "arrow.left.to.line") }
                .help("把結尾的把手移到播放頭：後面的部分裁掉")
            Button { editor.deleteSelection() } label: { Label("刪掉選取的範圍", systemImage: "trash") }
                .disabled(editor.selection == nil && editor.selectedLines.isEmpty)
                .help("在波形上拖曳選一段，再按這裡（或 Delete）刪掉")
            Button { editor.toggleCut() } label: {
                Label(editor.cut == nil ? String(localized: "在這裡分成兩場") : String(localized: "拿掉切點"), systemImage: "scissors")
            }
            .disabled(editor.cut == nil && !editor.canCut)
            .help(editor.mergedPart != nil ? String(localized: "合併時不能同時分成兩場") : String(localized: "在播放頭的位置切開，後半變成新的一場"))
            Button {
                if editor.mergedPart != nil { editor.unmerge(db: model.db) } else { showMergePicker = true }
            } label: {
                Label(editor.mergedPart == nil ? String(localized: "合併另一場…") : String(localized: "取消合併"),
                      systemImage: editor.mergedPart == nil ? "rectangle.stack.badge.plus" : "rectangle.stack.badge.minus")
            }
            .disabled(editor.cut != nil)
            .help(editor.cut != nil ? String(localized: "有切點時不能同時合併") : String(localized: "把另一場會議接到這場，照開始的時間排先後"))
        }
        .buttonStyle(SheetPillButtonStyle(.secondary, compact: true))
        .labelStyle(EditActionLabelStyle())
    }

    // MARK: 底下

    private var footer: some View {
        HStack(spacing: 12) {
            summary
            Spacer(minLength: 12)
            Button("取消", action: cancel)
                .buttonStyle(SheetPillButtonStyle(.secondary))
                .keyboardShortcut(.cancelAction)
            Button("套用…", action: confirmApply)
                .buttonStyle(SheetPillButtonStyle(.primary))
                .keyboardShortcut(.defaultAction)
                .disabled(!editor.hasChanges || editor.plan.duration < 1)
        }
    }

    private var summary: some View {
        let kept = editor.keptDuration()
        let removed = editor.removedLines
        let main: String, detail: String
        if let other = editor.mergedPart, let own = editor.ownPart {
            main = String(localized: "合併後 \(TimeFormat.string(kept))")
            detail = String(localized: "（這場 \(TimeFormat.string(editor.keptDuration(own.offset, own.offset + own.length)))＋「\(other.title)」\(TimeFormat.string(editor.keptDuration(other.offset, other.offset + other.length)))）")
        } else if let cut = editor.cut {
            main = String(localized: "分成兩場：\(TimeFormat.string(editor.keptDuration(0, cut))) 和 \(TimeFormat.string(editor.keptDuration(cut, nil)))")
            detail = removed > 0 ? String(localized: "（刪掉 \(TimeFormat.string(editor.total - kept))、\(removed) 句）") : ""
        } else {
            main = String(localized: "剩 \(TimeFormat.string(kept))")
            let marks = editor.removedMarks
            detail = editor.hasChanges ? (marks > 0 ? String(localized: "（刪掉 \(TimeFormat.string(editor.total - kept))、\(removed) 句、\(marks) 個標記重點）")
                                                    : String(localized: "（刪掉 \(TimeFormat.string(editor.total - kept))、\(removed) 句）"))
                                       : String(localized: "（拖兩端的把手裁切，或在波形上選一段刪掉）")
        }
        return (Text(main).fontWeight(.semibold) + Text(detail).foregroundStyle(.secondary))
            .font(.body)
            .lineLimit(2)
    }

    private func cancel() {
        guard editor.hasChanges else { close(); return }
        confirmation = ConfirmRequest(id: "discardEdit", title: String(localized: "放棄這些改動？"),
                                      message: String(localized: "編輯畫面上的裁切、刪掉的段落、切點和合併都不會套用。"),
                                      confirmTitle: String(localized: "放棄")) { close() }
    }

    private func close() {
        editor.stopPlaying()
        model.editingMeeting = nil
        dismiss()
    }

    private func confirmApply() {
        editor.stopPlaying()
        let plan = editor.plan, cut = editor.planCut
        confirmation = ConfirmRequest(id: "applyEdit", title: String(localized: "套用這些改動？"), message: applyMessage(),
                                      confirmTitle: String(localized: "套用"), destructive: false) {
            model.applyMeetingEdit(meetingId, plan: plan, cut: cut)
            close()
        }
    }

    /// 套用前說清楚會怎樣（§81.5）
    private func applyMessage() -> String {
        var lines: [String] = []
        let kept = editor.keptDuration(), total = editor.total
        if let other = editor.mergedPart {
            lines.append(String(localized: "合併「\(other.title)」，合併後 \(TimeFormat.string(kept))。那場會移到垃圾桶，30 天內可以還原。"))
        } else {
            var parts: [String] = []
            if editor.start > 0.05 { parts.append(String(localized: "裁掉開頭 \(TimeFormat.string(editor.start))")) }
            if editor.end < total - 0.05 { parts.append(String(localized: "結尾 \(TimeFormat.string(total - editor.end))")) }
            let middle = editor.deleted.filter { $0.lowerBound > editor.start && $0.upperBound < editor.end }
            if !middle.isEmpty {
                let seconds = middle.reduce(0) { $0 + $1.upperBound - $1.lowerBound }
                parts.append(String(localized: "刪掉中間 \(middle.count) 段（\(TimeFormat.string(seconds))）"))
            }
            if !parts.isEmpty {
                lines.append(String(localized: "會議從 \(TimeFormat.string(total)) 變成 \(TimeFormat.string(kept))：\(parts.joined(separator: MeetingLanguage.ui(zh: "，", en: ", ")))。"))
            }
        }
        if let cut = editor.cut {
            lines.append(String(localized: "從 \(TimeFormat.string(cut)) 分成兩場：\(TimeFormat.string(editor.keptDuration(0, cut))) 和 \(TimeFormat.string(editor.keptDuration(cut, nil)))，後面那場叫「\(editor.ownPart?.title ?? "")（2）」。"))
        }
        let removed = editor.removedLines, marks = editor.removedMarks
        if removed > 0 || marks > 0 {
            lines.append(marks > 0 ? String(localized: "刪掉 \(removed) 句逐字稿、\(marks) 個標記重點；我的筆記都留著。")
                                   : String(localized: "刪掉 \(removed) 句逐字稿；我的筆記都留著。"))
        }
        lines.append(AppSettings.summarizerReady ? String(localized: "會議紀錄會重新整理，現在這份留在版本紀錄。")
                                                 : String(localized: "會議紀錄要重新整理：設定好整理工具後按「重新整理」。"))
        lines.append(String(localized: "30 天內可以從「更多」還原成編輯前的樣子。"))
        return lines.map { "・" + $0 }.joined(separator: "\n")  // l10n-skip
    }
}

/// 動作列的按鈕：圖示＋字，圖示小一點
private struct EditActionLabelStyle: LabelStyle {
    func makeBody(configuration: Configuration) -> some View {
        HStack(spacing: 6) {
            configuration.icon.font(.system(size: 12))
            configuration.title
        }
    }
}

// MARK: - 時間軸

/// 上面一列標籤（開頭、結尾、切點、合併進來的那場）、中間的時間軸、下面的刻度；放大時左右捲動
struct EditTimelinePanel: View {
    let editor: MeetingEditor
    var focused: FocusState<Bool>.Binding
    @State private var dragStart: Double?
    @State private var zoomBase: Double?
    @Environment(\.colorScheme) private var colorScheme

    static let trackHeight: CGFloat = 132

    var body: some View {
        GeometryReader { geo in
            let width = max(1, geo.size.width - 24) * editor.zoom
            ScrollViewReader { proxy in
                ScrollView(.horizontal, showsIndicators: editor.zoom > 1.01) {
                    VStack(alignment: .leading, spacing: 4) {
                        EditLabelsRow(editor: editor, width: width)
                        track(width: width)
                        EditRuler(total: editor.total, width: width)
                    }
                    .frame(width: width)
                    .padding(.horizontal, 12)
                    .padding(.vertical, 8)
                }
                .onChange(of: editor.zoom) { proxy.scrollTo("playhead", anchor: .center) }
                .onChange(of: Int(editor.playhead / max(1, editor.total / editor.zoom / 2))) {
                    if editor.isPlaying && editor.zoom > 1.01 { proxy.scrollTo("playhead", anchor: .center) }
                }
            }
        }
        .frame(height: (editor.mergedPart == nil ? 18 : 38) + 4 + Self.trackHeight + 4 + 16 + 16)
        .background(RoundedRectangle(cornerRadius: 10).fill(Color.primary.opacity(colorScheme == .dark ? 0.12 : 0.05)))
        .focusable()
        .focused(focused)
        .focusEffectDisabled()
        .onKeyPress(.space) { editor.togglePlay(); return .handled }
        .onKeyPress(.leftArrow, phases: [.down, .repeat]) { press in
            editor.seek(editor.playhead - (press.modifiers.contains(.shift) ? 10 : 1)); return .handled
        }
        .onKeyPress(.rightArrow, phases: [.down, .repeat]) { press in
            editor.seek(editor.playhead + (press.modifiers.contains(.shift) ? 10 : 1)); return .handled
        }
        // Delete 鍵走 onDeleteCommand（同側欄）：onKeyPress(.delete) 對的是 U+0008，Mac 的 Delete 鍵送的是 U+007F，接不到
        .onDeleteCommand { editor.deleteSelection() }
        .gesture(MagnifyGesture()
            .onChanged { value in
                if zoomBase == nil { zoomBase = editor.zoom }
                editor.zoom = min(40, max(1, (zoomBase ?? 1) * value.magnification))
            }
            .onEnded { _ in zoomBase = nil })
        .accessibilityElement(children: .contain)
        .accessibilityLabel("編輯的時間軸")
    }

    private func time(_ x: CGFloat, _ width: CGFloat) -> Double {
        Double(min(max(0, x), width) / width) * editor.total
    }

    private func x(_ t: Double, _ width: CGFloat) -> CGFloat {
        editor.total > 0 ? CGFloat(t / editor.total) * width : 0
    }

    private func track(width: CGFloat) -> some View {
        ZStack(alignment: .topLeading) {
            EditWaveform(editor: editor, width: width, dark: colorScheme == .dark)
                .frame(width: width, height: Self.trackHeight)
                .contentShape(Rectangle())
                .gesture(DragGesture(minimumDistance: 0)
                    .onChanged { value in
                        focused.wrappedValue = true
                        if abs(value.translation.width) > 4 {
                            let a = time(value.startLocation.x, width), b = time(value.location.x, width)
                            editor.selection = min(a, b)...max(a, b)
                        }
                    }
                    .onEnded { value in
                        if abs(value.translation.width) <= 4 {
                            editor.selection = nil
                            editor.seek(time(value.location.x, width))
                            editor.revealPlayhead += 1
                        }
                    })
                .accessibilityElement()
                .accessibilityLabel("播放位置")
                .accessibilityValue("\(TimeFormat.string(editor.playhead))／\(TimeFormat.string(editor.total))")
                .accessibilityAdjustableAction { direction in
                    editor.seek(editor.playhead + (direction == .increment ? 10 : -10))
                }
            ForEach(editor.deleted, id: \.lowerBound) { range in
                restoreChip(range, width: width)
            }
            if let cut = editor.cut {
                Button { editor.toggleCut() } label: {
                    Image(systemName: "scissors")
                        .font(.system(size: 11, weight: .bold))
                        .foregroundStyle(Color(nsColor: .windowBackgroundColor))
                        .frame(width: 22, height: 22)
                        .background(Circle().fill(Color.signal))
                }
                .buttonStyle(.plain)
                .help("切點 \(TimeFormat.string(cut))：按一下拿掉")
                .accessibilityLabel("切點 \(TimeFormat.string(cut))，按一下拿掉")
                .offset(x: x(cut, width) - 11, y: -6)
            }
            EditHandle(editor: editor, isStart: true, width: width)
                .offset(x: x(editor.start, width) - 4.5, y: -4)
            EditHandle(editor: editor, isStart: false, width: width)
                .offset(x: x(editor.end, width) - 4.5, y: -4)
            playhead(width: width)
        }
        .frame(width: width, height: Self.trackHeight, alignment: .topLeading)
        .coordinateSpace(.named("editTrack"))
    }

    private func restoreChip(_ range: ClosedRange<Double>, width: CGFloat) -> some View {
        let x0 = x(range.lowerBound, width), x1 = x(range.upperBound, width)
        let label = String(localized: "刪掉 \(TimeFormat.string(range.upperBound - range.lowerBound))・恢復")
        return Button { editor.restore(range) } label: {
            Group {
                if x1 - x0 >= 130 { Text(label) }
                else if x1 - x0 >= 56 {
                    HStack(spacing: 4) { Image(systemName: "arrow.uturn.backward"); Text("恢復") }
                } else { Image(systemName: "arrow.uturn.backward") }
            }
            .font(.system(size: 11))
            .padding(.horizontal, 7)
            .padding(.vertical, 2)
            .background(Capsule().fill(Color(nsColor: .windowBackgroundColor).opacity(0.92)))
            .overlay(Capsule().strokeBorder(Color.primary.opacity(0.25)))
            .fixedSize()
        }
        .buttonStyle(.plain)
        .help(String(localized: "刪掉的段落 \(TimeFormat.string(range.lowerBound))–\(TimeFormat.string(range.upperBound))，按一下恢復"))
        .accessibilityLabel(String(localized: "刪掉的段落 \(TimeFormat.string(range.lowerBound)) 到 \(TimeFormat.string(range.upperBound))，按一下恢復"))
        .frame(width: 0)
        .offset(x: (x0 + x1) / 2, y: 36)
    }

    private func playhead(width: CGFloat) -> some View {
        ZStack(alignment: .top) {
            Rectangle().fill(Color.primary).frame(width: 2, height: Self.trackHeight - 8)
            Circle().fill(Color.primary).frame(width: 10, height: 10).offset(y: -4)
        }
        .frame(width: 10)
        .offset(x: x(editor.playhead, width) - 5, y: -2)
        .allowsHitTesting(false)
        .id("playhead")
        .accessibilityHidden(true)
    }
}

/// 開頭、結尾的把手：拖曳、焦點在把手上時 ← → 微調（⇧ 一秒），VoiceOver 唸時間（R107.9）
private struct EditHandle: View {
    let editor: MeetingEditor
    let isStart: Bool
    let width: CGFloat
    @FocusState private var focused: Bool
    @Environment(\.colorScheme) private var colorScheme

    var body: some View {
        let value = isStart ? editor.start : editor.end
        RoundedRectangle(cornerRadius: 4)
            .fill(Color.celadon)
            .overlay(RoundedRectangle(cornerRadius: 1).fill(Color.black.opacity(0.35)).frame(width: 2, height: 22))
            .overlay(RoundedRectangle(cornerRadius: 4).strokeBorder(focused ? Color.accentColor : Color.black.opacity(0.3), lineWidth: focused ? 2 : 1))
            .frame(width: 9, height: EditTimelinePanel.trackHeight - 4)
            .contentShape(Rectangle().inset(by: -4))
            .onHover { inside in if inside { NSCursor.resizeLeftRight.push() } else { NSCursor.pop() } }
            .gesture(DragGesture(minimumDistance: 1, coordinateSpace: .named("editTrack"))
                .onChanged { drag in
                    let t = Double(min(max(0, drag.location.x), width) / width) * editor.total
                    if isStart { editor.setStart(t) } else { editor.setEnd(t) }
                })
            .focusable()
            .focused($focused)
            .focusEffectDisabled()
            .onKeyPress(.leftArrow, phases: [.down, .repeat]) { press in nudge(press.modifiers.contains(.shift) ? -1 : -0.1); return .handled }
            .onKeyPress(.rightArrow, phases: [.down, .repeat]) { press in nudge(press.modifiers.contains(.shift) ? 1 : 0.1); return .handled }
            .help(isStart ? String(localized: "開頭 \(TimeFormat.string(value))：拖到會議真正開始的地方") : String(localized: "結尾 \(TimeFormat.string(value))：拖到會議真正結束的地方"))
            .accessibilityElement()
            .accessibilityLabel(isStart ? String(localized: "開頭") : String(localized: "結尾"))
            .accessibilityValue(String(localized: "\(TimeFormat.string(value))，剩下 \(TimeFormat.string(editor.keptDuration()))"))
            .accessibilityAdjustableAction { direction in nudge(direction == .increment ? 1 : -1) }
    }

    private func nudge(_ seconds: Double) {
        if isStart { editor.setStart(editor.start + seconds) } else { editor.setEnd(editor.end + seconds) }
    }
}

/// 時間軸上面的標籤：開頭、結尾（青瓷綠）、切點（琥珀色）、合併進來的那場的名字
private struct EditLabelsRow: View {
    let editor: MeetingEditor
    let width: CGFloat

    var body: some View {
        ZStack(alignment: .topLeading) {
            if editor.start > 0.05 {
                label(String(localized: "開頭 \(TimeFormat.string(editor.start))"), at: editor.start).foregroundStyle(Color.celadon)
            }
            if editor.end < editor.total - 0.05 {
                label(String(localized: "結尾 \(TimeFormat.string(editor.end))"), at: editor.end).foregroundStyle(Color.celadon)
            }
            if let cut = editor.cut {
                label(String(localized: "切點 \(TimeFormat.string(cut))"), at: cut).foregroundStyle(Color.signal)
            }
        }
        .frame(width: width, height: 18, alignment: .topLeading)
        .padding(.top, editor.mergedPart == nil ? 0 : 20)
        .overlay(alignment: .topLeading) {
            // 合併時多一行：每一場從哪裡開始、叫什麼
            if editor.mergedPart != nil {
                ZStack(alignment: .topLeading) {
                    ForEach(editor.parts) { part in
                        Text(part.title)
                            .font(.system(size: 11))
                            .lineLimit(1)
                            .truncationMode(.tail)
                            .padding(.horizontal, 6)
                            .frame(maxWidth: max(40, CGFloat(part.length / max(1, editor.total)) * width - 8), alignment: .leading)
                            .background(RoundedRectangle(cornerRadius: 4).fill(Color.primary.opacity(0.12)))
                            .fixedSize(horizontal: false, vertical: true)
                            .offset(x: CGFloat(part.offset / max(1, editor.total)) * width + 2)
                    }
                }
                .frame(width: width, height: 18, alignment: .topLeading)
            }
        }
    }

    private func label(_ text: String, at t: Double) -> some View {
        let x = CGFloat(t / max(1, editor.total)) * width
        return Text(text)
            .font(.system(size: 11).monospacedDigit())
            .fixedSize()
            .frame(width: 0, alignment: x < 50 ? .leading : x > width - 50 ? .trailing : .center)
            .offset(x: x)
    }
}

/// 下面的刻度：間隔跟著寬度挑（至少 70 點一格）
private struct EditRuler: View {
    let total: Double
    let width: CGFloat

    var body: some View {
        let steps: [Double] = [5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600]
        let step = steps.first { total > 0 && CGFloat($0 / total) * width >= 70 } ?? 3600
        let ticks = stride(from: 0, through: max(0, total - step * 0.4), by: step).map { $0 }
        ZStack(alignment: .topLeading) {
            ForEach(ticks, id: \.self) { t in
                let x = CGFloat(t / max(1, total)) * width
                Text(TimeFormat.string(t))
                    .fixedSize()
                    .frame(width: 0, alignment: x < 30 ? .leading : .center)
                    .offset(x: x)
            }
            Text(TimeFormat.string(total))
                .frame(width: width, alignment: .trailing)
        }
        .font(.system(size: 11).monospacedDigit())
        .foregroundStyle(.secondary)
        .frame(width: width, height: 16, alignment: .topLeading)
    }
}

/// 議題帶、波形（依說話者著色）、標記重點、裁掉和刪掉的部分、選取、切點、合併的分隔線（不含播放頭：播放時才不會整張重畫）
private struct EditWaveform: View {
    let editor: MeetingEditor
    let width: CGFloat
    let dark: Bool

    var body: some View {
        let bars = Self.bars(editor, width: width)
        let topics = editor.topics, marks = editor.marks, total = max(1, editor.total)
        let start = editor.start, end = editor.end, deleted = editor.deleted, selection = editor.selection, cut = editor.cut
        let boundary: Double? = editor.mergedPart.flatMap { other in editor.ownPart.map { max($0.offset, other.offset) } }
        let keptAt = { (t: Double) in t >= start && t <= end && !deleted.contains { t >= $0.lowerBound && t < $0.upperBound } }
        Canvas { context, size in
            let scale = size.width / CGFloat(total)
            let veil = dark ? Color.black.opacity(0.55) : Color.white.opacity(0.6)
            // 議題帶
            for topic in topics {
                let rect = CGRect(x: CGFloat(topic.start) * scale, y: 0, width: max(2, CGFloat(topic.end - topic.start) * scale - 2), height: 18)
                let color = Color(hex: MindMap.color(topic.branch, dark: dark))
                let alpha = keptAt((topic.start + topic.end) / 2) ? 1.0 : 0.45
                context.fill(Path(roundedRect: rect, cornerRadius: 4), with: .color(color.opacity(0.3 * alpha)))
                if rect.width >= 40 {
                    var text = context.resolve(Text(topic.title).font(.system(size: 11)))
                    text.shading = .color(.primary.opacity(alpha))
                    context.drawLayer { layer in
                        layer.clip(to: Path(rect.insetBy(dx: 5, dy: 0)))
                        layer.draw(text, at: CGPoint(x: rect.minX + 5, y: rect.midY), anchor: .leading)
                    }
                }
            }
            // 波形
            let waveTop: CGFloat = 26, waveHeight: CGFloat = 92
            for bar in bars {
                let h = max(2, CGFloat(bar.level.squareRoot()) * waveHeight)
                let rect = CGRect(x: bar.x, y: waveTop + (waveHeight - h) / 2, width: 2, height: h)
                let color = bar.speaker.map { Color.speaker($0) } ?? Color.secondary
                context.fill(Path(roundedRect: rect, cornerRadius: 1), with: .color(color.opacity(bar.kept ? 0.9 : 0.25)))
            }
            // 標記重點
            for t in marks {
                let x = CGFloat(t) * scale
                var diamond = Path()
                diamond.move(to: CGPoint(x: x, y: 120))
                diamond.addLine(to: CGPoint(x: x + 4, y: 124))
                diamond.addLine(to: CGPoint(x: x, y: 128))
                diamond.addLine(to: CGPoint(x: x - 4, y: 124))
                diamond.closeSubpath()
                context.fill(diamond, with: .color(Color.signal.opacity(keptAt(t) ? 1 : 0.35)))
            }
            // 裁掉的兩端
            let fullHeight: CGFloat = 120
            context.fill(Path(CGRect(x: 0, y: 0, width: CGFloat(start) * scale, height: fullHeight)), with: .color(veil))
            context.fill(Path(CGRect(x: CGFloat(end) * scale, y: 0, width: size.width - CGFloat(end) * scale, height: fullHeight)), with: .color(veil))
            // 刪掉的段落：斜線、虛線邊
            for range in deleted {
                let rect = CGRect(x: CGFloat(range.lowerBound) * scale, y: 0, width: CGFloat(range.upperBound - range.lowerBound) * scale, height: fullHeight)
                context.fill(Path(rect), with: .color(veil))
                context.drawLayer { layer in
                    layer.clip(to: Path(rect))
                    var stripes = Path()
                    var sx = rect.minX - fullHeight
                    while sx < rect.maxX {
                        stripes.move(to: CGPoint(x: sx, y: fullHeight))
                        stripes.addLine(to: CGPoint(x: sx + fullHeight, y: 0))
                        sx += 8
                    }
                    layer.stroke(stripes, with: .color(.primary.opacity(0.16)), lineWidth: 3)
                }
                var edges = Path()
                edges.move(to: CGPoint(x: rect.minX, y: 0)); edges.addLine(to: CGPoint(x: rect.minX, y: fullHeight))
                edges.move(to: CGPoint(x: rect.maxX, y: 0)); edges.addLine(to: CGPoint(x: rect.maxX, y: fullHeight))
                context.stroke(edges, with: .color(.primary.opacity(0.45)), style: StrokeStyle(lineWidth: 1, dash: [3, 3]))
            }
            // 選取
            if let selection {
                let rect = CGRect(x: CGFloat(selection.lowerBound) * scale, y: 0,
                                  width: max(1, CGFloat(selection.upperBound - selection.lowerBound) * scale), height: fullHeight)
                // 選取用系統藍：把手、時間點是青瓷綠，同色分不出來
                let blue = Color(nsColor: .systemBlue)
                context.fill(Path(roundedRect: rect, cornerRadius: 3), with: .color(blue.opacity(0.22)))
                context.stroke(Path(roundedRect: rect, cornerRadius: 3), with: .color(blue.opacity(0.9)), lineWidth: 1)
                let label = context.resolve(Text("選取 \(TimeFormat.string(selection.upperBound - selection.lowerBound))")
                    .font(.system(size: 11).monospacedDigit()).foregroundStyle(.white))
                let labelSize = label.measure(in: size)
                let chip = CGRect(x: rect.midX - labelSize.width / 2 - 7, y: 36, width: labelSize.width + 14, height: labelSize.height + 4)
                context.fill(Path(roundedRect: chip, cornerRadius: 6), with: .color(blue))
                context.draw(label, at: CGPoint(x: chip.midX, y: chip.midY))
            }
            // 合併的分隔線
            if let boundary {
                let x = CGFloat(boundary) * scale
                context.fill(Path(CGRect(x: x - 1, y: -4, width: 2, height: fullHeight + 8)), with: .color(.primary.opacity(0.75)))
            }
            // 切點
            if let cut {
                var line = Path()
                line.move(to: CGPoint(x: CGFloat(cut) * scale, y: 0))
                line.addLine(to: CGPoint(x: CGFloat(cut) * scale, y: fullHeight + 8))
                context.stroke(line, with: .color(Color.signal), style: StrokeStyle(lineWidth: 2, dash: [5, 3]))
            }
        }
        .overlay(alignment: .center) {
            if editor.isLoadingPeaks {
                HStack(spacing: 6) {
                    ProgressView().controlSize(.small)
                    Text("正在讀取錄音…").font(.callout).foregroundStyle(.secondary)
                }
                .padding(.horizontal, 10).padding(.vertical, 4)
                .background(Capsule().fill(.regularMaterial))
            }
        }
    }

    struct Bar { var x: CGFloat; var level: Float; var speaker: Int?; var kept: Bool }

    /// 每 3 點一條（2 點寬）：那段時間的峰值、說話者（那時候在說話的那句）、留不留
    static func bars(_ editor: MeetingEditor, width: CGFloat) -> [Bar] {
        let total = editor.total
        guard total > 0, width > 3 else { return [] }
        let count = Int(width / 3)
        let lines = editor.lines
        var cursor = 0
        var bars: [Bar] = []
        bars.reserveCapacity(count)
        for i in 0..<count {
            let t0 = Double(i) * 3 / Double(width) * total, t1 = Double(i + 1) * 3 / Double(width) * total
            let mid = (t0 + t1) / 2
            while cursor < lines.count - 1 && lines[cursor + 1].start <= mid { cursor += 1 }
            let line = lines.isEmpty ? nil : lines[cursor]
            let speaking = line.map { $0.start <= mid && $0.end >= t0 } ?? false
            bars.append(Bar(x: CGFloat(i) * 3, level: editor.level(from: t0, to: t1), speaker: speaking ? line?.speaker : nil, kept: editor.kept(mid)))
        }
        return bars
    }
}

// MARK: - 逐字稿

/// 下半部的逐字稿：播放到哪句就反白、捲過去；點一句從那裡播放，⌘、⇧ 點選好幾句；右鍵刪掉、恢復、設成開頭結尾、分成兩場
private struct EditTranscriptList: View {
    let editor: MeetingEditor
    @State private var anchor: Int64?
    @State private var hovered: Int64?

    var body: some View {
        let current = editor.lines.last { $0.start <= editor.playhead }?.id
        VStack(alignment: .leading, spacing: 6) {
            HStack(alignment: .firstTextBaseline) {
                Text("逐字稿").font(.callout).fontWeight(.semibold).foregroundStyle(.secondary)
                Spacer()
                Text("點一句從那裡播放；按住 ⌘、⇧ 選好幾句，按右鍵刪掉、設成開頭或結尾、從這句分成兩場")
                    .font(.caption).foregroundStyle(.secondary).lineLimit(1).truncationMode(.head)
            }
            Divider()
            ScrollViewReader { proxy in
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 1) {
                        if editor.lines.isEmpty {
                            Text("這場會議沒有逐字稿，用上面的時間軸裁切。").foregroundStyle(.secondary).padding(8)
                        }
                        ForEach(editor.lines) { line in
                            row(line, isCurrent: line.id == current).id(line.id)
                        }
                    }
                    .padding(.vertical, 2)
                }
                .onChange(of: current) { _, id in
                    if editor.isPlaying, let id { withAnimation(.easeInOut(duration: 0.2)) { proxy.scrollTo(id, anchor: .center) } }
                }
                // 在時間軸上點了：捲到那一句（點逐字稿的句子時不捲，免得清單跳）
                .onChange(of: editor.revealPlayhead) {
                    if let id = editor.lines.last(where: { $0.start <= editor.playhead })?.id {
                        withAnimation(.easeInOut(duration: 0.2)) { proxy.scrollTo(id, anchor: .center) }
                    }
                }
            }
        }
        .frame(maxHeight: .infinity)
    }

    private func row(_ line: MeetingEditor.Line, isCurrent: Bool) -> some View {
        let kept = editor.kept(line.mid)
        let selected = editor.selectedLines.contains(line.id)
        return HStack(alignment: .firstTextBaseline, spacing: 10) {
            Text(TimeFormat.string(line.start))
                .font(.callout.monospacedDigit()).foregroundStyle(Color.celadon)
                .frame(width: 56, alignment: .trailing)
            HStack(spacing: 5) {
                if let idx = line.speaker { Circle().fill(Color.speaker(idx)).frame(width: 8, height: 8) }
                Text(line.speakerName).lineLimit(1)
            }
            .font(.callout)
            .frame(width: 84, alignment: .leading)
            Text(line.text)
                .strikethrough(!kept)
                .foregroundStyle(kept ? .primary : .secondary)
                .lineLimit(2)
                .frame(maxWidth: .infinity, alignment: .leading)
            if hovered == line.id || selected {
                Button(actionTitle(line)) { primaryAction(line) }
                    .buttonStyle(SheetPillButtonStyle(.secondary, compact: true))
                    .font(.callout)
            }
        }
        .padding(.vertical, 4)
        .padding(.horizontal, 8)
        .background(RoundedRectangle(cornerRadius: 6).fill(selected ? Color.accentColor.opacity(0.18) : isCurrent ? Color.primary.opacity(0.07) : .clear))
        .contentShape(Rectangle())
        .onHover { inside in hovered = inside ? line.id : (hovered == line.id ? nil : hovered) }
        .onTapGesture { click(line) }
        .contextMenu { menu(line) }
        .accessibilityElement(children: .combine)
        .accessibilityValue(kept ? "" : String(localized: "刪掉"))
        .accessibilityAction(named: Text(actionTitle(line))) { primaryAction(line) }
    }

    private func actionTitle(_ line: MeetingEditor.Line) -> String {
        if editor.kept(line.mid) { return String(localized: "刪掉") }
        if line.mid < editor.start { return String(localized: "從這句開始") }
        if line.mid > editor.end { return String(localized: "到這句結束") }
        return String(localized: "恢復")
    }

    private func primaryAction(_ line: MeetingEditor.Line) {
        if editor.kept(line.mid) { editor.deleteLines(editor.targetLines(for: line)) }
        else if line.mid < editor.start { editor.setStart(line.start) }
        else if line.mid > editor.end { editor.setEnd(line.end) }
        else { editor.restoreLines(editor.targetLines(for: line)) }
    }

    private func click(_ line: MeetingEditor.Line) {
        // 這一下點擊帶的修飾鍵（NSEvent.modifierFlags 是鍵盤現在的狀態）
        let flags = NSApp.currentEvent?.modifierFlags ?? NSEvent.modifierFlags
        if flags.contains(.command) {
            if editor.selectedLines.contains(line.id) { editor.selectedLines.remove(line.id) } else { editor.selectedLines.insert(line.id) }
            anchor = line.id
        } else if flags.contains(.shift), let anchor, let a = editor.lines.firstIndex(where: { $0.id == anchor }),
                  let b = editor.lines.firstIndex(where: { $0.id == line.id }) {
            editor.selectedLines = Set(editor.lines[min(a, b)...max(a, b)].map(\.id))
        } else {
            editor.selectedLines = []
            anchor = line.id
            editor.seek(line.start)
        }
    }

    @ViewBuilder
    private func menu(_ line: MeetingEditor.Line) -> some View {
        let targets = editor.targetLines(for: line)
        let anyKept = targets.contains { editor.kept($0.mid) }
        let anyRemoved = targets.contains { !editor.kept($0.mid) && $0.mid >= editor.start && $0.mid <= editor.end }
        Button("從這裡播放", systemImage: "play") { editor.seek(line.start); if !editor.isPlaying { editor.togglePlay() } }
        Divider()
        if anyKept {
            Button(targets.count > 1 ? String(localized: "刪掉這 \(targets.count) 句") : String(localized: "刪掉這句"), systemImage: "trash") {
                editor.deleteLines(targets)
            }
        }
        if anyRemoved {
            Button(targets.count > 1 ? String(localized: "恢復這 \(targets.count) 句") : String(localized: "恢復這句"), systemImage: "arrow.uturn.backward") {
                editor.restoreLines(targets)
            }
        }
        Button("從這句開始（設成開頭）", systemImage: "arrow.right.to.line") { editor.setStart(targets.map(\.start).min() ?? line.start) }
        Button("到這句結束（設成結尾）", systemImage: "arrow.left.to.line") { editor.setEnd(targets.map(\.end).max() ?? line.end) }
        Divider()
        Button("從這句分成兩場", systemImage: "scissors") { editor.cut(at: line.start) }
            .disabled(editor.mergedPart != nil || !editor.kept(line.mid) || line.start <= editor.start + 1)
    }
}

// MARK: - 合併另一場

/// 選要合併的會議（§81.5）：同一天、前後三天的在上面，可以搜尋標題；會議語言不同時說明照這場的語言整理
struct MergePickerSheet: View {
    let meetingId: Int64
    let onPick: (Meeting) -> Void
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    @State private var query = ""
    @State private var picked: Int64?

    var body: some View {
        let candidates = model.mergeCandidates(for: meetingId)
        let matches = { (rows: [MeetingRow]) in
            query.isEmpty ? rows : rows.filter { $0.meeting.displayTitle.localizedCaseInsensitiveContains(query) }
        }
        let near = matches(candidates.near), older = matches(candidates.older)
        VStack(alignment: .leading, spacing: 14) {
            SheetHeader(title: String(localized: "合併另一場會議"),
                        subtitle: String(localized: "照會議開始的先後接在一起。合併後另一場移到垃圾桶，30 天內可以還原。"))
            SheetTextField(placeholder: String(localized: "搜尋會議標題"), text: $query, focusOnAppear: true)
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 4) {
                    if near.isEmpty && older.isEmpty {
                        Text(query.isEmpty ? String(localized: "沒有可以合併的會議") : String(localized: "找不到「\(query)」"))
                            .foregroundStyle(.secondary).padding(8)
                    }
                    if !near.isEmpty { section(String(localized: "前後三天"), near) }
                    if !older.isEmpty { section(String(localized: "更早或更晚"), older) }
                }
            }
            .frame(height: 300)
            HStack {
                Spacer()
                Button("取消") { dismiss() }
                    .buttonStyle(SheetPillButtonStyle(.secondary))
                    .keyboardShortcut(.cancelAction)
                Button("合併") {
                    if let picked, let meeting = try? model.db.meeting(picked) { onPick(meeting) }
                    dismiss()
                }
                .buttonStyle(SheetPillButtonStyle(.primary))
                .keyboardShortcut(.defaultAction)
                .disabled(picked == nil)
            }
        }
        .sheetChrome(width: 520)
    }

    private func section(_ title: String, _ rows: [MeetingRow]) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(title).font(.callout).foregroundStyle(.secondary).padding(.leading, 4).padding(.top, 4)
            ForEach(rows) { row in item(row) }
        }
    }

    private func item(_ row: MeetingRow) -> some View {
        let meeting = row.meeting
        let selected = picked == row.id
        let own = model.meetings.first { $0.id == meetingId }?.meeting
        let otherLanguage = own.map { $0.language != meeting.language } ?? false
        var meta = [meeting.meetingDate, TimeFormat.string(Double(meeting.durationSeconds ?? 0))]
        if !meeting.folder.isEmpty { meta.append(meeting.folder) }
        return Button { picked = row.id } label: {
            HStack(spacing: 12) {
                VStack(alignment: .leading, spacing: 3) {
                    Text(meeting.displayTitle).font(.body).lineLimit(1)
                    Text(meta.joined(separator: "・")).font(.callout).foregroundStyle(.secondary)  // l10n-skip
                    if selected && otherLanguage {
                        Text("兩場的會議語言不同，合併後照這場的語言整理").font(.callout).foregroundStyle(Color.signal)
                    }
                }
                Spacer()
                if selected { Image(systemName: "checkmark").foregroundStyle(Color.celadon).fontWeight(.semibold) }
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 8)
            .background(RoundedRectangle(cornerRadius: 10).fill(selected ? Color.celadon.opacity(0.16) : .clear))
            .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(selected ? Color.celadon.opacity(0.7) : .clear))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(selected ? .isSelected : [])
    }
}
