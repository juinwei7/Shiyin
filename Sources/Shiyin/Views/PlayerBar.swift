import SwiftUI

/// 底部播放列：播放鍵、時間、錄音時間軸（R5.3）
struct PlayerBar: View {
    @Environment(AppModel.self) private var model
    let detail: MeetingDetail

    var body: some View {
        let player = model.player
        HStack(spacing: 14) {
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

            TimelineBar(segments: detail.segments,
                        reviewTimes: (detail.notes?.reviewItems ?? []).compactMap { TimeFormat.seconds($0.time) },
                        bookmarkTimes: detail.bookmarks.map(\.time),
                        duration: max(player.duration, Double(detail.meeting.durationSeconds ?? 0)),
                        currentTime: player.currentTime,
                        onSeek: { seconds, play in player.seek(to: seconds, play: play) })
                .frame(height: 30)

            Text(TimeFormat.string(max(player.duration, Double(detail.meeting.durationSeconds ?? 0))))
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
