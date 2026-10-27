import SwiftUI

/// 錄音中的畫面（design §5.4、R2）：即時逐字稿＋我的筆記＋重點
struct RecordingPanel: View {
    @Environment(AppModel.self) private var model
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    let detail: MeetingDetail
    @State private var followLatest = true

    var body: some View {
        #if DEBUG
        let _ = DebugMetrics.bump("recordingPanelBody")
        #endif
        let recorder = model.recorder
        VStack(spacing: 0) {
            // 計時、音量每 0.05 秒變一次，只讓這一塊重算（design §21.12）
            Isolated { controls(recorder) }
            if let change = recorder.languageSwitch {
                // 自動偵測換了語言（design §18.8）：可以改回來
                HStack(spacing: 12) {
                    Label("聽起來是\(change.to.uiName)，即時逐字稿和會議語言已經改成\(change.to.uiName)", systemImage: "globe")
                    Spacer()
                    Button("改回\(change.from.uiName)") { recorder.switchLanguage(to: change.from) }
                }
                .font(.callout)
                .padding(.horizontal, 16)
                .padding(.vertical, 8)
                .background(Color.signal.opacity(0.12))
            }
            if recorder.idlePrompt {
                // 忘了停止錄音（design §35.2、R48.1）
                HStack(spacing: 12) {
                    Label("好像沒人在說話了，要停止錄音嗎？再 15 分鐘沒人說話會自動停止。", systemImage: "moon.zzz")
                    Spacer()
                    Button("繼續錄") { recorder.continueAfterIdle() }
                    Button("停止錄音") { recorder.stop() }
                }
                .font(.callout)
                .padding(.horizontal, 16)
                .padding(.vertical, 8)
                .background(Color.signal.opacity(0.12))
            }
            if let hint = recorder.levelHint {
                // 收音提醒（design §26.1）
                HStack(spacing: 12) {
                    Label(Self.levelHintText(hint), systemImage: hint == .noisy ? "waveform.badge.exclamationmark" : "mic.badge.xmark")
                    Spacer()
                    Button("知道了") { recorder.dismissLevelHint() }
                        .help("這場錄音不再提示這件事")
                }
                .font(.callout)
                .padding(.horizontal, 16)
                .padding(.vertical, 8)
                .background(Color.signal.opacity(0.12))
            }
            Divider()
            // 不用 HSplitView：它在 NavigationSplitView 裡會把內容撐到視窗的預設寬度，視窗窄的時候左右被切掉
            HStack(spacing: 0) {
                Isolated { liveTranscript(recorder) }
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

    /// 提示的文字在 `Recorder.LevelHint.text`（錄音畫面、選單列、通知共用，R42）
    static func levelHintText(_ hint: Recorder.LevelHint) -> String { hint.text }

    private func controls(_ recorder: Recorder) -> some View {
        HStack(spacing: 14) {
            HStack(spacing: 8) {
                Circle().fill(recorder.isPaused ? Color.secondary : .red).frame(width: 10, height: 10)
                Text(recorder.isPaused ? "已暫停" : "錄音中")
                RecorderClock(recorder: recorder).foregroundStyle(.secondary)
            }
            .font(.title3)
            if recorder.hasMic {
                Label { RecorderMeter(recorder: recorder, system: false) } icon: { Image(systemName: "mic") }
                    .help("麥克風音量")
            }
            if recorder.hasSystem {
                Label { RecorderMeter(recorder: recorder, system: true) } icon: { Image(systemName: "speaker.wave.2") }
                    .help("電腦播放的聲音（線上會議的對方）")
            }
            Spacer(minLength: 0)
            // 視窗窄的時候（側邊欄打開、視窗寬 960）按鈕只留圖示，說明放在滑鼠提示。
            // 英文的字比較長，中間多一層：暫停、標記只留圖示，「停止並轉錄」保留文字
            ViewThatFits(in: .horizontal) {
                actionButtons(recorder).labelStyle(.titleAndIcon)
                HStack(spacing: 14) {
                    HStack(spacing: 14) { pauseButton(recorder); bookmarkButton(recorder) }.labelStyle(.iconOnly)
                    stopButton(recorder).labelStyle(.titleAndIcon)
                }
                .fixedSize()
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
            pauseButton(recorder)
            bookmarkButton(recorder)
            stopButton(recorder)
        }
        .fixedSize()
    }

    private func pauseButton(_ recorder: Recorder) -> some View {
        Button(recorder.isPaused ? "繼續" : "暫停", systemImage: recorder.isPaused ? "play.fill" : "pause.fill") {
            recorder.isPaused ? recorder.resume() : recorder.pause()
        }
        .help(recorder.isPaused ? "繼續錄音（⇧⌘P）" : "暫停錄音（⇧⌘P）")
    }

    private func bookmarkButton(_ recorder: Recorder) -> some View {
        Button("標記重點", systemImage: "star") {
            model.addBookmark(at: recorder.elapsed, meetingId: detail.meeting.id)
            model.noticeMessage = String(localized: "已標記 \(TimeFormat.string(recorder.elapsed)) 為重點")
        }
        .help("標記這個時間點（⌘B），整理時會被當成重點")
    }

    private func stopButton(_ recorder: Recorder) -> some View {
        Button("停止並轉錄", systemImage: "stop.fill") { recorder.stop() }
            .buttonStyle(.borderedProminent)
            .tint(.red)
            .help("停止錄音，開始正式轉錄")
    }

    private func warning(_ recorder: Recorder) -> String? {
        if let warning = recorder.deviceWarning { return warning }
        if recorder.hasSystem && recorder.systemSilent {
            return String(localized: "超過 15 秒沒有收到系統聲音：請確認線上會議有聲音，並在「系統設定 → 隱私權與安全性 → 錄製螢幕與系統錄音」允許拾音")
        }
        return recorder.liveStatus.isEmpty ? nil : recorder.liveStatus
    }

    private func liveTranscript(_ recorder: Recorder) -> some View {
        #if DEBUG
        DebugMetrics.bump("liveTranscriptBuild")
        #endif
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
                            Text(line.text).textSelection(.enabled).fixedSize(horizontal: false, vertical: true).meetingText()
                            if starred.contains(line.id) {
                                Image(systemName: "star.fill").foregroundStyle(Color.signal).font(.caption)
                            }
                        }
                        .id(line.id)
                    }
                    // 還沒定稿的文字一直在變，只讓這一行重算，不拖著整份即時逐字稿（design §21.12）
                    Isolated {
                        if !recorder.volatileText.isEmpty {
                            HStack(alignment: .firstTextBaseline, spacing: 12) {
                                Text("…").frame(width: 52, alignment: .trailing).foregroundStyle(.tertiary)
                                Text(recorder.volatileText).foregroundStyle(.secondary).meetingText()
                            }
                            .id("volatile")
                            .onChange(of: recorder.volatileText.count) { _, _ in
                                guard followLatest else { return }
                                proxy.scrollTo("bottom", anchor: .bottom)
                            }
                        }
                    }
                    Color.clear.frame(height: 1).id("bottom")
                }
                .padding(20)
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            // 使用者往回看時不要自動捲動（R2.2）：看捲動的位置。往上捲（內容沒有變短）就不跟了，回到底部再跟。
            // 不用「底部的標記出現／消失」改狀態：那是在排版途中改，可能讓排版一直重來（design §25）
            .onScrollGeometryChange(for: LivePosition.self) { geometry in
                LivePosition(offset: geometry.contentOffset.y, content: geometry.contentSize.height,
                             bottom: geometry.visibleRect.maxY)
            } action: { old, new in
                if new.bottom >= new.content - 40 {
                    if !followLatest { followLatest = true }
                } else if new.offset < old.offset - 1 && new.content >= old.content {
                    if followLatest { followLatest = false }
                }
            }
            .onChange(of: recorder.liveLines.count) { _, _ in
                guard followLatest else { return }
                if reduceMotion { proxy.scrollTo("bottom", anchor: .bottom) }
                else { withAnimation(.easeOut(duration: 0.2)) { proxy.scrollTo("bottom", anchor: .bottom) } }
            }
            .overlay(alignment: .bottomTrailing) {
                if !followLatest {
                    Button("回到最新", systemImage: "arrow.down") {
                        proxy.scrollTo("bottom", anchor: .bottom)
                        followLatest = true
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
        TextEditor(text: $text).meetingText()
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
            // 別處改了筆記（例如匯入 Markdown 筆記）：換成新的內容，不然下一次存檔會蓋回舊的
            .onChange(of: initial) { _, new in
                guard new != savedText else { return }
                saveTask?.cancel()
                text = new
                savedText = new
            }
    }

    private func save() {
        savedText = text
        model.saveUserNotes(meetingId, text)
    }
}

/// 逐字稿頂部的「我的筆記」（R44.3，design §33.1）：預設收起，標籤寫有幾行；展開是錄音中同一個編輯器，播放時換行會加上播放時間。
/// 展開狀態只記這次選取期間（AppModel.userNotesExpanded，換會議收起）
struct UserNotesDisclosure: View {
    @Environment(AppModel.self) private var model
    let detail: MeetingDetail

    /// 有字的行數
    private var lineCount: Int {
        detail.meeting.userNotes.split(whereSeparator: \.isNewline).filter { !$0.trimmingCharacters(in: .whitespaces).isEmpty }.count
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 8) {
                Button {
                    withAnimation(.easeInOut(duration: 0.15)) { model.userNotesExpanded.toggle() }
                } label: {
                    HStack(spacing: 8) {
                        Image(systemName: "note.text").foregroundStyle(.secondary)
                        Text(lineCount > 0 ? String(localized: "我的筆記（\(lineCount) 行）") : String(localized: "寫筆記")).font(.callout)
                        Image(systemName: model.userNotesExpanded ? "chevron.up" : "chevron.down").font(.caption).foregroundStyle(.secondary)
                        Spacer(minLength: 0)
                    }
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                if !detail.bookmarks.isEmpty {
                    Menu("重點標記 \(detail.bookmarks.count)") {
                        ForEach(detail.bookmarks) { bookmark in
                            Button("\(TimeFormat.string(bookmark.time)) \(bookmark.label)") { model.player.seek(to: bookmark.time) }
                        }
                    }
                    .fixedSize()
                }
            }
            .padding(.horizontal, 20)
            .padding(.vertical, 8)
            if model.userNotesExpanded {
                VStack(alignment: .leading, spacing: 8) {
                    Text("播放錄音時換行，會自動加上當下的播放時間。整理時 AI 會參考這些筆記；改完之後可以從「更多 → 重新處理」選「重新整理會議紀錄」。")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                    UserNotesEditor(meetingId: detail.meeting.id!, initial: detail.meeting.userNotes) {
                        model.player.isPlaying ? model.player.currentTime : nil
                    }
                    .frame(height: 180)
                    .id(detail.meeting.id)
                }
                .padding(.horizontal, 20)
                .padding(.bottom, 12)
                .frame(maxWidth: 760, alignment: .leading)
            }
        }
    }
}

/// 即時逐字稿捲動的位置：捲了多少、內容多高、看得到的底部在哪
struct LivePosition: Equatable {
    var offset: CGFloat
    var content: CGFloat
    var bottom: CGFloat
}

/// 錄音的經過時間：在自己的 body 裡讀，整秒變了只重算這一個 Text（design §25）
struct RecorderClock: View {
    let recorder: Recorder
    var body: some View { Text(TimeFormat.string(Double(recorder.elapsedSeconds))).monospacedDigit() }
}

/// 錄音的音量條（design §25）：AppKit 的 view 自己定時讀音量，不經過 SwiftUI 的更新。
/// DEBUG 截圖（ImageRenderer 畫不出 AppKit 的 view）時用 SwiftUI 的 VUMeter
struct RecorderMeter: View {
    @Environment(\.snapshotMode) private var snapshotMode
    let recorder: Recorder
    let system: Bool
    var segments = 14

    var body: some View {
        if snapshotMode {
            VUMeter(level: system ? recorder.systemLevel : recorder.micLevel, segments: segments)
        } else {
            LevelMeter(segments: segments) { [recorder, system] in system ? recorder.systemLevel : recorder.micLevel }
        }
    }
}

/// 讓內容在自己的 body 裡算：裡面讀到、一直在變的資料（計時、音量、播放位置、還沒定稿的文字）
/// 只會讓這一塊重算，不會拖著外面整個畫面一起重算（design §21.12）
struct Isolated<Content: View>: View {
    @ViewBuilder let content: () -> Content
    var body: some View { content() }
}
