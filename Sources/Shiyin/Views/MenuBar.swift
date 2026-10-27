import SwiftUI

/// 選單列圖示：閒置、錄音中、工作中三種狀態（R13.1）
struct MenuBarLabel: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        Image(systemName: model.menuBarSymbol)
            .onAppear {
                // 選單列圖示一直存在，用它提供「重新打開主視窗」的方法
                let open = openWindow
                if model.openMainWindow == nil {
                    model.openMainWindow = {
                        open(id: "main")
                        NSApp.activate()
                    }
                }
            }
    }
}

/// 選單內容（R13.2）
struct MenuBarContent: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            recording
            Divider()
            jobs
            Divider()
            VStack(alignment: .leading, spacing: 6) {
                Button("打開拾音") { model.openMainWindow?() }
                Button("使用說明") {
                    openWindow(id: "guide")
                    NSApp.activate()
                }
                Button("結束拾音") { NSApp.terminate(nil) }
            }
            .buttonStyle(.plain)
        }
        .padding(14)
        .frame(width: 290, alignment: .leading)
        .tint(.celadon)
    }

    @ViewBuilder
    private var recording: some View {
        let recorder = model.recorder
        if recorder.isRecording {
            VStack(alignment: .leading, spacing: 8) {
                HStack(spacing: 8) {
                    Circle().fill(recorder.isPaused ? Color.secondary : .red).frame(width: 8, height: 8)
                    Text(recorder.isPaused ? "已暫停" : "錄音中")
                    RecorderClock(recorder: recorder).foregroundStyle(.secondary)
                    Spacer()
                    Button(recorder.isPaused ? "繼續" : "暫停") { recorder.isPaused ? recorder.resume() : recorder.pause() }
                    Button("停止") { recorder.stop() }
                }
                if recorder.hasMic { RecorderMeter(recorder: recorder, system: false, segments: 24) }
                if recorder.hasSystem { RecorderMeter(recorder: recorder, system: true, segments: 24) }
                if recorder.idlePrompt {
                    // 忘了停止錄音（R48.1）：同錄音畫面
                    VStack(alignment: .leading, spacing: 6) {
                        Label("好像沒人在說話了，要停止錄音嗎？", systemImage: "moon.zzz")
                            .font(.caption)
                            .foregroundStyle(Color.signal)
                            .fixedSize(horizontal: false, vertical: true)
                        HStack {
                            Button("繼續錄") { recorder.continueAfterIdle() }
                            Button("停止錄音") { recorder.stop() }
                        }
                        .font(.caption)
                    }
                }
                if let hint = recorder.levelHint {
                    // 收音提醒（R42.2）：跟錄音畫面同一句，「知道了」這場不再提示
                    VStack(alignment: .leading, spacing: 6) {
                        Label(hint.text, systemImage: hint == .noisy ? "waveform.badge.exclamationmark" : "mic.badge.xmark")
                            .font(.caption)
                            .foregroundStyle(Color.signal)
                            .fixedSize(horizontal: false, vertical: true)
                        Button("知道了") { recorder.dismissLevelHint() }
                            .font(.caption)
                    }
                }
                if !recorder.volatileText.isEmpty || !recorder.liveLines.isEmpty {
                    Text(recorder.volatileText.isEmpty ? recorder.liveLines.last?.text ?? "" : recorder.volatileText)
                        .font(.caption).foregroundStyle(.secondary).lineLimit(2)
                }
            }
        } else {
            Button { model.startRecording() } label: {
                Label("開始錄音", systemImage: "record.circle")
            }
        }
    }

    @ViewBuilder
    private var jobs: some View {
        VStack(alignment: .leading, spacing: 8) {
            if let t = model.runner.running[.transcribe] {
                VStack(alignment: .leading, spacing: 4) {
                    Text(String(localized: "\(t.job.kind == .compress ? String(localized: "壓縮錄音") : t.stage.isEmpty ? String(localized: "轉錄") : t.stage)中：\(title(t.job.meetingId))")).lineLimit(1)
                    if let p = t.progress {
                        ProgressView(value: p).tint(.signal)
                    } else {
                        ProgressView().controlSize(.small)
                    }
                }
            }
            // 整理線、輔助線（逐字稿校正）同時跑（design §21.2）
            ForEach([JobLane.llm, .assist], id: \.self) { lane in
                if let l = model.runner.running[lane] {
                    HStack(spacing: 8) {
                        ProgressView().controlSize(.small)
                        Text(llmStatus(l.job.kind, title(l.job.meetingId))).lineLimit(1)
                    }
                }
            }
            if model.queuedCount > 0 {
                Text("排隊中 \(model.queuedCount) 項").foregroundStyle(.secondary)
            }
            if !model.runner.isBusy && model.queuedCount == 0 {
                Text("目前沒有進行中的工作").foregroundStyle(.secondary)
            }
        }
        .font(.callout)
    }

    /// 整理那條線正在做什麼：整理、依回答修正、檢查可能聽錯的字
    private func llmStatus(_ kind: JobKind, _ title: String) -> String {
        switch kind {
        case .revise: return String(localized: "依回答修正中：\(title)")
        case .correct: return String(localized: "檢查可能聽錯的字：\(title)")
        case .nameSpeakers: return String(localized: "猜說話者：\(title)")
        default: return String(localized: "整理中：\(title)")
        }
    }

    private func title(_ meetingId: Int64) -> String {
        model.meetings.first { $0.id == meetingId }?.meeting.displayTitle ?? String(localized: "會議")
    }
}
