import SwiftUI

/// 錄音中的畫面（design §5.4、R2）：即時逐字稿＋我的筆記＋重點
struct RecordingPanel: View {
    @Environment(AppModel.self) private var model
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    let detail: MeetingDetail
    @State private var followLatest = true

    var body: some View {
        let recorder = model.recorder
        VStack(spacing: 0) {
            controls(recorder)
            Divider()
            // 不用 HSplitView：它在 NavigationSplitView 裡會把內容撐到視窗的預設寬度，視窗窄的時候左右被切掉
            HStack(spacing: 0) {
                liveTranscript(recorder)
                    .frame(minWidth: 300, maxWidth: .infinity)
                Divider()
                VStack(alignment: .leading, spacing: 8) {
                    Text("我的筆記").font(.songti(16))
                    Text("每換一行會自動加上當下的錄音時間；整理時會優先納入這些內容。")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                    UserNotesEditor(meetingId: detail.meeting.id!, initial: detail.meeting.userNotes) { recorder.elapsed }
                }
                .padding(16)
                .frame(width: 300)
            }
        }
    }

    private func controls(_ recorder: Recorder) -> some View {
        HStack(spacing: 14) {
            HStack(spacing: 8) {
                Circle().fill(recorder.isPaused ? Color.secondary : .red).frame(width: 10, height: 10)
                Text(recorder.isPaused ? "已暫停" : "錄音中")
                Text(TimeFormat.string(recorder.elapsed)).monospacedDigit().foregroundStyle(.secondary)
            }
            .font(.title3)
            if recorder.hasMic {
                Label { VUMeter(level: recorder.micLevel) } icon: { Image(systemName: "mic") }
                    .help("麥克風音量")
            }
            if recorder.hasSystem {
                Label { VUMeter(level: recorder.systemLevel) } icon: { Image(systemName: "speaker.wave.2") }
                    .help("電腦播放的聲音（線上會議的對方）")
            }
            Spacer(minLength: 0)
            // 視窗窄的時候（側邊欄打開、視窗寬 960）按鈕只留圖示，說明放在滑鼠提示
            ViewThatFits(in: .horizontal) {
                actionButtons(recorder).labelStyle(.titleAndIcon)
                actionButtons(recorder).labelStyle(.iconOnly)
            }
        }
        .padding(.horizontal, 20)
        .padding(.vertical, 12)
        .overlay(alignment: .bottomLeading) {
            if let warning = warning(recorder) {
                Label(warning, systemImage: "exclamationmark.triangle.fill")
                    .font(.caption)
                    .foregroundStyle(Color.signal)
                    .padding(.leading, 20)
                    .offset(y: 14)
            }
        }
    }

    private func actionButtons(_ recorder: Recorder) -> some View {
        HStack(spacing: 14) {
            Button(recorder.isPaused ? "繼續" : "暫停", systemImage: recorder.isPaused ? "play.fill" : "pause.fill") {
                recorder.isPaused ? recorder.resume() : recorder.pause()
            }
            .help(recorder.isPaused ? "繼續錄音（⇧⌘P）" : "暫停錄音（⇧⌘P）")
            Button("標記重點", systemImage: "star") {
                model.addBookmark(at: recorder.elapsed, meetingId: detail.meeting.id)
                model.noticeMessage = "已標記 \(TimeFormat.string(recorder.elapsed)) 為重點"
            }
            .help("標記這個時間點（⌘B），整理時會被當成重點")
            Button("停止並轉錄", systemImage: "stop.fill") { recorder.stop() }
                .buttonStyle(.borderedProminent)
                .tint(.red)
                .help("停止錄音，開始正式轉錄")
        }
        .fixedSize()
    }

    private func warning(_ recorder: Recorder) -> String? {
        if recorder.hasSystem && recorder.systemSilent {
            return "超過 15 秒沒有收到系統聲音：請確認線上會議有聲音，並在「系統設定 → 隱私權與安全性 → 錄製螢幕與系統錄音」允許拾音"
        }
        return recorder.liveStatus.isEmpty ? nil : recorder.liveStatus
    }

    private func liveTranscript(_ recorder: Recorder) -> some View {
        // 每個重點只標在一段上：開始時間不晚於它的最後一段
        let starred = Set(detail.bookmarks.compactMap { mark in recorder.liveLines.last(where: { $0.start <= mark.time })?.id })
        return ScrollViewReader { proxy in
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 8) {
                    if recorder.liveLines.isEmpty && recorder.volatileText.isEmpty {
                        Text(AppSettings.liveTranscription ? "開始說話後，逐字稿會即時出現在這裡。" : "即時逐字稿已在設定中關閉，錄音結束後會正式轉錄。")
                            .foregroundStyle(.secondary)
                    }
                    ForEach(recorder.liveLines) { line in
                        HStack(alignment: .firstTextBaseline, spacing: 12) {
                            Text(TimeFormat.string(line.start)).font(.callout.monospacedDigit()).foregroundStyle(Color.celadon)
                                .frame(width: 52, alignment: .trailing)
                            Text(line.text).textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
                            if starred.contains(line.id) {
                                Image(systemName: "star.fill").foregroundStyle(Color.signal).font(.caption)
                            }
                        }
                        .id(line.id)
                    }
                    if !recorder.volatileText.isEmpty {
                        HStack(alignment: .firstTextBaseline, spacing: 12) {
                            Text("…").frame(width: 52, alignment: .trailing).foregroundStyle(.tertiary)
                            Text(recorder.volatileText).foregroundStyle(.secondary)
                        }
                        .id("volatile")
                    }
                    Color.clear.frame(height: 1).id("bottom")
                        .onAppear { followLatest = true }
                        .onDisappear { followLatest = false }
                }
                .padding(20)
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            // 使用者往回看時（底部不在畫面內）不要自動捲動（R2.2）
            .onChange(of: recorder.liveLines.count + recorder.volatileText.count) { _, _ in
                guard followLatest else { return }
                if reduceMotion { proxy.scrollTo("bottom", anchor: .bottom) }
                else { withAnimation(.easeOut(duration: 0.2)) { proxy.scrollTo("bottom", anchor: .bottom) } }
            }
            .overlay(alignment: .bottomTrailing) {
                if !followLatest {
                    Button("回到最新", systemImage: "arrow.down") {
                        proxy.scrollTo("bottom", anchor: .bottom)
                    }
                    .padding(12)
                }
            }
        }
    }
}

/// 使用者筆記（R2.3）：換行時自動加上時間點，停下來 0.8 秒後存檔
struct UserNotesEditor: View {
    @Environment(AppModel.self) private var model
    let meetingId: Int64
    let initial: String
    let now: () -> Double?
    @State private var text = ""
    @State private var savedText = ""
    @State private var saveTask: Task<Void, Never>?

    var body: some View {
        TextEditor(text: $text)
            .font(.body)
            .scrollContentBackground(.hidden)
            .padding(8)
            .background(Color.secondary.opacity(0.07), in: RoundedRectangle(cornerRadius: 8))
            .onAppear {
                text = initial
                savedText = initial
            }
            .onChange(of: text) { old, new in
                if let stamp = now().map({ "[\(TimeFormat.string($0))] " }) {
                    if old.isEmpty && !new.isEmpty && !new.hasPrefix("[") {
                        text = stamp + new
                        return
                    }
                    if new.count == old.count + 1 && new.hasSuffix("\n") {
                        text = new + stamp
                        return
                    }
                }
                guard new != savedText else { return }
                saveTask?.cancel()
                saveTask = Task {
                    try? await Task.sleep(for: .milliseconds(800))
                    guard !Task.isCancelled else { return }
                    save()
                }
            }
            .onDisappear {
                saveTask?.cancel()
                // 只有使用者改過才存，避免用舊內容蓋掉別處寫進去的筆記
                if text != savedText { save() }
            }
    }

    private func save() {
        try? model.db.updateUserNotes(meetingId, text)
        savedText = text
    }
}

/// 筆記分頁：錄完之後也可以繼續寫或修改筆記
struct UserNotesTab: View {
    @Environment(AppModel.self) private var model
    let detail: MeetingDetail

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Text("我的筆記").font(.songti(18))
                Spacer()
                if !detail.bookmarks.isEmpty {
                    Menu("重點標記 \(detail.bookmarks.count)") {
                        ForEach(detail.bookmarks) { bookmark in
                            Button("\(TimeFormat.string(bookmark.time)) \(bookmark.label)") { model.player.seek(to: bookmark.time) }
                        }
                    }
                    .fixedSize()
                }
            }
            Text("播放錄音時換行，會自動加上當下的播放時間。整理時 AI 會參考這些筆記；改完之後可以從「更多」選「重新整理會議紀錄」。")
                .font(.caption)
                .foregroundStyle(.secondary)
            UserNotesEditor(meetingId: detail.meeting.id!, initial: detail.meeting.userNotes) {
                model.player.isPlaying ? model.player.currentTime : nil
            }
            .id(detail.meeting.id)
        }
        .padding(.horizontal, 32)
        .padding(.vertical, 20)
        .frame(maxWidth: 760, alignment: .leading)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
    }
}
