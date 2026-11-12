import SwiftUI

/// 底部播放列：播放鍵、時間、錄音時間軸（R5.3），有議題時上面加議題帶（R68）
struct PlayerBar: View {
    @Environment(AppModel.self) private var model
    let detail: MeetingDetail

    var body: some View {
        let player = model.player
        let duration = max(player.duration, Double(detail.meeting.durationSeconds ?? 0))
        let topics = detail.notes.map {
            MindMap.topicRanges(notes: $0, duration: duration, untitled: MeetingLanguage.ui(zh: MindMap.topicLabel(.zh), en: MindMap.topicLabel(.en)))
        } ?? []
        HStack(alignment: .timelineCenter, spacing: 14) {
            Button { player.toggle() } label: {
                Image(systemName: player.isPlaying ? "pause.fill" : "play.fill")
                    .font(.title3)
                    .frame(width: 28, height: 28)
            }
            .buttonStyle(.plain)
            .disabled(!player.isLoaded)
            .help(player.isPlaying ? "暫停（空白鍵）" : "播放（空白鍵）")

            Text(TimeFormat.string(player.currentTime))
                .font(.callout.monospacedDigit())
                .frame(width: 52, alignment: .trailing)

            VStack(spacing: 4) {
                if !topics.isEmpty {
                    TopicBand(ranges: topics, duration: duration, currentTime: player.currentTime,
                              onSeek: { seconds in player.seek(to: seconds, play: true) })
                }
                TimelineBar(segments: detail.segments,
                            reviewTimes: (detail.notes?.reviewItems ?? []).compactMap { TimeFormat.seconds($0.time) },
                            bookmarkTimes: detail.bookmarks.map(\.time),
                            duration: duration,
                            currentTime: player.currentTime,
                            onSeek: { seconds, play in player.seek(to: seconds, play: play) })
                    .frame(height: 30)
                    .alignmentGuide(.timelineCenter) { $0[VerticalAlignment.center] }
            }

            Text(TimeFormat.string(duration))
                .font(.callout.monospacedDigit())
                .foregroundStyle(.secondary)
                .frame(width: 52, alignment: .leading)

            Menu {
                ForEach([Float(0.75), 1, 1.25, 1.5, 2], id: \.self) { value in
                    Button {
                        player.setRate(value)
                    } label: {
                        if value == player.rate { Label(speedLabel(value), systemImage: "checkmark") } else { Text(speedLabel(value)) }
                    }
                }
            } label: {
                Text(speedLabel(player.rate)).monospacedDigit()
            }
            .menuStyle(.borderlessButton)
            .fixedSize()
            .help("播放速度")
        }
        .padding(.horizontal, 18)
        .padding(.vertical, 10)
        .background(.bar)
        .overlay(alignment: .top) { Divider() }
        .opacity(player.isLoaded ? 1 : 0.5)
    }

    private func speedLabel(_ value: Float) -> String {
        value == 1 ? "1×" : String(format: "%g×", value)
    }
}

private extension VerticalAlignment {
    /// 播放列的其他元件對齊時間軸的中間（上面有議題帶時也一樣）
    struct TimelineCenter: AlignmentID {
        static func defaultValue(in context: ViewDimensions) -> CGFloat { context[VerticalAlignment.center] }
    }

    static let timelineCenter = VerticalAlignment(TimelineCenter.self)
}

/// 議題帶（design §43.2、R68）：時間軸上面每個議題一段，顏色同心智圖的那一支，點了從那個議題開始播放
struct TopicBand: View {
    let ranges: [MindMap.TopicRange]
    let duration: Double
    let currentTime: Double
    let onSeek: (Double) -> Void
    @Environment(\.colorScheme) private var colorScheme

    var body: some View {
        GeometryReader { geo in
            let scale = duration > 0 ? geo.size.width / CGFloat(duration) : 0
            ZStack(alignment: .topLeading) {
                ForEach(Array(ranges.enumerated()), id: \.offset) { _, range in
                    segment(range, width: max(2, CGFloat(range.end - range.start) * scale - 2))
                        .offset(x: CGFloat(range.start) * scale)
                }
            }
        }
        .frame(height: 18)
    }

    private func segment(_ range: MindMap.TopicRange, width: CGFloat) -> some View {
        let color = Color(hex: MindMap.color(range.branch, dark: colorScheme == .dark))
        let isCurrent = currentTime >= range.start && currentTime < range.end
        let label = "\(range.title)　\(TimeFormat.string(range.start))–\(TimeFormat.string(range.end))"  // l10n-skip：名稱和時間，不用翻譯
        return Button { onSeek(range.start) } label: {
            ZStack(alignment: .leading) {
                RoundedRectangle(cornerRadius: 4).fill(color.opacity(isCurrent ? 0.45 : 0.18))
                // 整個標題放得下，或至少放得下一兩個字加「…」才寫字，不然只留色塊（只剩「…」看不出是什麼）
                if MindMap.width(range.title, size: 11) + 10 <= width || width >= 40 {
                    Text(range.title)
                        .font(.system(size: 11, weight: isCurrent ? .semibold : .regular))
                        .lineLimit(1)
                        .truncationMode(.tail)
                        .padding(.horizontal, 5)
                }
            }
            .frame(width: width, height: 18)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help(label)
        .accessibilityLabel(label)
    }
}

/// 錄音時間軸（design §9.3）：整場會議畫成一條帶子，每段逐字稿一格；
/// 格子高度依說話密度變化，低信心段落是琥珀色，待確認題目在上方有標記。點擊或拖曳就跳播。
struct TimelineBar: View {
    let segments: [Segment]
    let reviewTimes: [Double]
    var bookmarkTimes: [Double] = []
    let duration: Double
    let currentTime: Double
    let onSeek: (Double, Bool) -> Void

    var body: some View {
        GeometryReader { geo in
            Canvas { context, size in
                draw(context, size)
            }
            .contentShape(Rectangle())
            .gesture(
                DragGesture(minimumDistance: 0)
                    .onChanged { value in onSeek(time(at: value.location.x, width: geo.size.width), false) }
                    .onEnded { value in onSeek(time(at: value.location.x, width: geo.size.width), true) }
            )
        }
        .accessibilityElement()
        .accessibilityLabel("錄音時間軸")
        .accessibilityValue("\(TimeFormat.string(currentTime))／\(TimeFormat.string(duration))")
        .accessibilityAdjustableAction { direction in
            let step: Double = 10
            onSeek(direction == .increment ? currentTime + step : max(0, currentTime - step), false)
        }
    }

    private func time(at x: CGFloat, width: CGFloat) -> Double {
        guard width > 0, duration > 0 else { return 0 }
        return Double(max(0, min(x, width)) / width) * duration
    }

    private func draw(_ context: GraphicsContext, _ size: CGSize) {
        let width = size.width, height = size.height
        let track = CGRect(x: 0, y: height / 2 - 0.5, width: width, height: 1)
        context.fill(Path(track), with: .color(.secondary.opacity(0.25)))
        guard duration > 0 else { return }
        let scale = width / CGFloat(duration)

        // 說話密度（每秒字數）決定格子高度，看起來像波形
        let densities = segments.map { Double($0.text.count) / max(0.5, $0.end - $0.start) }
        let maxDensity = max(densities.max() ?? 1, 1)
        // 上方留給待確認的三角形、下方留給重點的圓點，格子畫在中間
        let markerSpace: CGFloat = 6
        let barHeight = height - markerSpace * 2
        for (i, segment) in segments.enumerated() {
            let x = CGFloat(segment.start) * scale
            let w = max(1.5, CGFloat(segment.end - segment.start) * scale - 1)
            let ratio = 0.35 + 0.65 * CGFloat(densities[i] / maxDensity)
            let h = barHeight * ratio
            let rect = CGRect(x: x, y: markerSpace + (barHeight - h) / 2, width: w, height: h)
            let isCurrent = currentTime >= segment.start && currentTime < segment.end
            let color: Color = segment.lowConfidence ? .signal : (isCurrent ? .celadon : .secondary.opacity(0.45))
            context.fill(Path(roundedRect: rect, cornerRadius: 1), with: .color(color))
        }

        // 待確認題目的位置：上方的小三角形
        for t in reviewTimes {
            let x = CGFloat(t) * scale
            var marker = Path()
            marker.move(to: CGPoint(x: x - 3.5, y: 0))
            marker.addLine(to: CGPoint(x: x + 3.5, y: 0))
            marker.addLine(to: CGPoint(x: x, y: 5))
            marker.closeSubpath()
            context.fill(marker, with: .color(.signal))
        }

        // 重點標記：下方的小圓點（R8.4）
        for t in bookmarkTimes {
            let x = CGFloat(t) * scale
            context.fill(Path(ellipseIn: CGRect(x: x - 2.5, y: height - 5, width: 5, height: 5)), with: .color(.celadon))
        }

        // 目前播放位置
        let playhead = CGRect(x: min(width - 2, CGFloat(currentTime) * scale), y: 0, width: 2, height: height)
        context.fill(Path(roundedRect: playhead, cornerRadius: 1), with: .color(.celadon))
    }
}
