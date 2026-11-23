import SwiftUI

/// 錄音中的畫面（design §5.4、R2）：即時逐字稿＋我的筆記＋重點
struct RecordingPanel: View {
    @Environment(AppModel.self) private var model
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @Environment(\.textScale) private var scale
    let detail: MeetingDetail
    @State private var followLatest = true
    @State private var screenDropTargeted = false

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
            if let countdown = recorder.stopCountdown {
                // 定時停止的時間到了、倒數中（design §84、R110.3）
                HStack(spacing: 12) {
                    TimelineView(.periodic(from: .now, by: 1)) { context in
                        Label(String(localized: "已到設定的時間，\(StopTimer.secondsLeft(countdown, now: context.date)) 秒後停止錄音"),
                              systemImage: "timer")
                    }
                    Spacer()
                    Button("繼續錄") { model.continueAfterStopTimer() }
                        .help("取消定時停止，照常錄下去")
                    Button("停止並轉錄") { recorder.stop() }
                }
                .font(.callout)
                .padding(.horizontal, 16)
                .padding(.vertical, 8)
                .background(Color.signal.opacity(0.12))
            }
            if let problem = model.screenshotProblem {
                // 截不到圖（design §83.2、R109.3）
                HStack(spacing: 12) {
                    Label(problem == .denied ? "要允許拾音「錄製螢幕」才能截圖。貼上、拖進來的圖片不需要這個權限。"
                                             : "剛允許的權限要重新打開拾音才會生效，錄完這場再重新打開。",
                          systemImage: "camera.viewfinder")
                    Spacer()
                    if problem == .denied {
                        Button("打開系統設定") { Recorder.openPrivacySettings("Privacy_ScreenCapture") }
                            .buttonStyle(.borderedProminent)
                    }
                    Button("知道了") { model.screenshotProblem = nil }
                }
                .font(.callout)
                .padding(.horizontal, 16)
                .padding(.vertical, 8)
                .background(Color.signal.opacity(0.12))
            }
            if let hint = recorder.micHint {
                // 用藍牙耳機的麥克風錄現場會議（design §38.4、R58.3）
                HStack(spacing: 12) {
                    Label(hint.text, systemImage: "headphones")
                    Spacer()
                    if let builtIn = hint.builtIn {
                        Button("改用 \(builtIn.name)") { model.switchMicrophone(builtIn.uid) }
                            .buttonStyle(.borderedProminent)
                            .help("錄音不會中斷，接著用這支麥克風錄；之後的錄音也用這支")
                    }
                    Button("知道了") { recorder.dismissMicHint() }
                        .help("這場錄音不再提示這件事")
                }
                .font(.callout)
                .controlSize(.small)
                .padding(.horizontal, 16)
                .padding(.vertical, 8)
                .background(Color.signal.opacity(0.12))
            }
            if let hint = recorder.levelHint {
                // 收音提醒（design §26.1）
                HStack(spacing: 12) {
                    Label(recorder.text(for: hint), systemImage: hint.symbol)
                    Spacer()
                    if hint == .systemDenied {
                        // 錄不到電腦聲音（design §51.1、R75.2）：允許之後拾音自己接上，不用重新開始錄音
                        Button("打開系統設定") { Recorder.openPrivacySettings("Privacy_AudioCapture") }
                            .buttonStyle(.borderedProminent)
                            .help("允許之後，拾音會自己接上電腦的聲音")
                    }
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
                    // 拖進來的圖片是這個時間點的畫面（design §83.3）；其他檔案照主視窗的規則（參考文件、匯入）
                    .dropDestination(for: URL.self) { urls, _ in
                        let images = urls.filter { DocumentReader.kind(of: $0) == .image }
                        let others = urls.filter { DocumentReader.kind(of: $0) != .image }
                        if !images.isEmpty { model.addScreensHere(images.compactMap(ScreenImages.load)) }
                        if !others.isEmpty { model.requestImport(others) }
                        return true
                    } isTargeted: { screenDropTargeted = $0 }
                    .overlay {
                        if screenDropTargeted {
                            RoundedRectangle(cornerRadius: 12)
                                .strokeBorder(Color.celadon, style: StrokeStyle(lineWidth: 2, dash: [7, 5]))
                                .overlay(alignment: .bottom) {
                                    Label("放開就加到 \(TimeFormat.string(model.recorder.elapsed)) 的畫面", systemImage: "photo.badge.plus")
                                        .font(.callout)
                                        .padding(.horizontal, 12).padding(.vertical, 6)
                                        .background(.regularMaterial, in: Capsule())
                                        .padding(16)
                                }
                                .padding(6)
                                .allowsHitTesting(false)
                        }
                    }
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
                RecorderClock(recorder: recorder).foregroundStyle(.secondary)
            }
            .font(.title3)
            // 定時停止（design §84、R110）：跟麥克風選單一起先縮（放不下時只留圖示），按鈕的字留到最後
            StopTimerMenu()
                .layoutPriority(-1)
            if recorder.hasMic {
                Label { RecorderMeter(recorder: recorder, system: false) } icon: { Image(systemName: "mic") }
                    .help("麥克風音量")
                // 正在用哪一支麥克風，點一下可以換（design §38.3、R58.2）；視窗窄的時候先縮這個
                MicrophoneMenu(recorder: recorder)
                    .frame(maxWidth: 180, alignment: .leading)
                    .layoutPriority(-1)
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
                    HStack(spacing: 14) { pauseButton(recorder); bookmarkButton(recorder); screenshotButton }.labelStyle(.iconOnly)
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
            screenshotButton
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

    /// 會議中的畫面（R109.1）：截游標所在的螢幕，拾音自己的視窗不會進去
    private var screenshotButton: some View {
        Button("截圖", systemImage: "camera.viewfinder") { model.captureScreen() }
            .help("截下游標所在的螢幕（⇧⌘S；在其他 app 裡按 \(GlobalHotKey.screenshotDisplay)），整理時會一起參考")
    }

    private func stopButton(_ recorder: Recorder) -> some View {
        Button("停止並轉錄", systemImage: "stop.fill") { recorder.stop() }
            .buttonStyle(.borderedProminent)
            .tint(.red)
            .help("停止錄音，開始正式轉錄")
    }

    private func warning(_ recorder: Recorder) -> String? {
        if let warning = recorder.deviceWarning { return warning }
        // 錄不到電腦聲音的提示（design §51.1）出現時不再講一次
        if recorder.hasSystem && recorder.systemSilent && recorder.levelHint != .systemDenied {
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
        // 截好的畫面插在當下的位置（選了「插在即時逐字稿裡」，design §83.4）
        let placement = ScreenPlacement.place(detail.screens, starts: recorder.liveLines.map(\.start))
        return ScrollViewReader { proxy in
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 8) {
                    if recorder.liveLines.isEmpty && recorder.volatileText.isEmpty {
                        Text(AppSettings.liveTranscription ? "開始說話後，逐字稿會即時出現在這裡。" : "即時逐字稿已在設定中關閉，錄音結束後會正式轉錄。")
                            .foregroundStyle(.secondary)
                    }
                    ForEach(Array(recorder.liveLines.enumerated()), id: \.element.id) { index, line in
                        if let screens = placement.before[index] {
                            ForEach(screens) { ScreenRow(screen: $0, compact: true) }
                        }
                        HStack(alignment: .firstTextBaseline, spacing: 12) {
                            Text(TimeFormat.string(line.start)).contentFont(.callout, monospacedDigit: true).foregroundStyle(Color.celadon)
                                .frame(width: 52 * scale, alignment: .trailing)
                            Text(line.text).textSelection(.enabled).fixedSize(horizontal: false, vertical: true).meetingText()
                            if starred.contains(line.id) {
                                Image(systemName: "star.fill").foregroundStyle(Color.signal).contentFont(.caption)
                            }
                        }
                        .id(line.id)
                    }
                    ForEach(placement.trailing) { ScreenRow(screen: $0, compact: true) }
                    // 還沒定稿的文字一直在變，只讓這一行重算，不拖著整份即時逐字稿（design §21.12）
                    Isolated {
                        if !recorder.volatileText.isEmpty {
                            HStack(alignment: .firstTextBaseline, spacing: 12) {
                                Text("…").frame(width: 52 * scale, alignment: .trailing).foregroundStyle(.tertiary)
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
                .contentFont()   // 文字大小（design §38.5）
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
            .onChange(of: recorder.liveLines.count + detail.screens.count) { _, _ in
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
            .contentFont()   // 文字大小（design §38.5）
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
                    // 每個標記：跳過去、重新命名、刪除（R100）
                    Menu("重點標記 \(detail.bookmarks.count)") {
                        ForEach(detail.bookmarks) { bookmark in
                            Menu("\(TimeFormat.string(bookmark.time)) \(bookmark.label)") {
                                Button("跳到這裡") { model.player.seek(to: bookmark.time) }
                                Button("重新命名…") { if let id = bookmark.id { model.renameTarget = .bookmark(id) } }
                                Divider()
                                DestructiveMenuButton(String(localized: "刪除")) { if let id = bookmark.id { model.deleteBookmark(id) } }
                            }
                        }
                    }
                    .fixedSize()
                }
            }
            .padding(.horizontal, 20)
            .padding(.vertical, 8)
            if model.userNotesExpanded {
                VStack(alignment: .leading, spacing: 8) {
                    // 不能加 fixedSize：量最小高度時會用很窄的寬度，算出很高的高度，整個視窗的內容被往上推（2026-11-05 使用者看到）
                    Text("播放錄音時換行，會自動加上當下的播放時間。整理時 AI 會參考這些筆記；改完之後可以從「更多 → 重新處理」選「重新整理會議紀錄」。")
                        .font(.caption)
                        .foregroundStyle(.secondary)
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

/// 錄音畫面的麥克風名稱（design §38.3、R58.2）：寫出實際在收音的那一支，點一下從清單換，錄音不中斷
struct MicrophoneMenu: View {
    @Environment(AppModel.self) private var model
    let recorder: Recorder

    var body: some View {
        let monitor = MicrophoneMonitor.shared
        let current = recorder.micDevice
        Menu {
            Button {
                model.switchMicrophone("")
            } label: {
                Text(monitor.defaultInput.map { String(localized: "系統預設（\($0.name)）") } ?? String(localized: "系統預設"))
            }
            Divider()
            ForEach(monitor.inputs) { device in
                Button {
                    model.switchMicrophone(device.uid)
                } label: {
                    if device.uid == current?.uid { Label(device.name, systemImage: "checkmark") } else { Text(device.name) }
                }
            }
        } label: {
            Text(current?.name ?? String(localized: "麥克風")).lineLimit(1).truncationMode(.middle)
        }
        .menuStyle(.borderlessButton)
        .help(current.map { String(localized: "正在用「\($0.name)」收音，點一下可以換") } ?? String(localized: "點一下可以換麥克風"))
        .accessibilityLabel(String(localized: "麥克風：\(current?.name ?? "")"))
    }
}

// MARK: - 定時停止（design §84、R110）

/// 定時停止的選單內容：錄音畫面、「檔案」選單、選單列的小視窗共用
struct StopTimerMenuItems: View {
    let model: AppModel
    /// 「指定時間…」要先打開主視窗（從選單列、「檔案」選單）；錄音畫面本來就在主視窗裡，不用（也不搶前景）
    var opensWindow = true

    var body: some View {
        let recorder = model.recorder
        if let stopAt = recorder.stopAt {
            Text("\(StopTimer.label(stopAt)) 自動停止錄音")
            Divider()
        }
        ForEach(StopTimer.presets, id: \.minutes) { preset in
            Button(preset.title) { model.setStopTimer(minutes: preset.minutes) }
        }
        Button("指定時間…") {
            if opensWindow { model.openStopTimerSheet() } else { model.showStopTimerSheet = true }
        }
        if recorder.stopAt != nil {
            Divider()
            Button("取消定時停止") { model.setStopTimer(nil) }
        }
    }
}

/// 錄音畫面計時旁邊的選單：沒設定時寫「定時停止」，設定了寫「15:30 停止」；放不下時只留圖示（說明在滑過提示）
struct StopTimerMenu: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        ViewThatFits(in: .horizontal) {
            menu.labelStyle(.titleAndIcon)
            menu.labelStyle(.iconOnly)
        }
    }

    private var menu: some View {
        let stopAt = model.recorder.stopAt
        return Menu {
            StopTimerMenuItems(model: model, opensWindow: false)
        } label: {
            Label {
                if let stopAt { Text("\(StopTimer.label(stopAt)) 停止") } else { Text("定時停止") }
            } icon: {
                Image(systemName: "timer")
            }
        }
        .menuStyle(.borderlessButton)
        .fixedSize()
        .help(stopAt.map { String(localized: "\(StopTimer.label($0)) 自動停止錄音（還有 \(StopTimer.remaining($0))），點一下可以改或取消") }
              ?? String(localized: "時間到自動停止錄音並轉錄"))
        .accessibilityLabel(stopAt.map { String(localized: "\(StopTimer.label($0)) 自動停止錄音") } ?? String(localized: "定時停止"))
    }
}

/// 「指定時間…」：輸入幾點幾分（共用的彈窗，design §80）
struct StopTimerSheet: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    @State private var text = ""

    var body: some View {
        let time = StopTimer.parse(text)
        VStack(alignment: .leading, spacing: 18) {
            // 說明行寫還有多久，跟著時間走
            TimelineView(.periodic(from: .now, by: 1)) { context in
                SheetHeader(title: String(localized: "定時停止錄音"), subtitle: hint(time, now: context.date))
            }
            VStack(alignment: .leading, spacing: 6) {
                SheetFieldLabel(String(localized: "停止的時間"))
                SheetTextField(placeholder: "15:30", text: $text, focusOnAppear: true) { if let time { save(time) } }
            }
            HStack(spacing: 10) {
                Spacer()
                Button("取消") { dismiss() }
                    .buttonStyle(SheetPillButtonStyle(.secondary))
                    .keyboardShortcut(.cancelAction)
                // 「設定」在別處是名詞（Settings），這裡是動詞
                Button(MeetingLanguage.ui(zh: "設定", en: "Set")) { if let time { save(time) } }
                    .buttonStyle(SheetPillButtonStyle(.primary))
                    .keyboardShortcut(.defaultAction)
                    .disabled(time == nil)
            }
        }
        .sheetChrome()
        .onAppear { text = model.recorder.stopAt.map { StopTimer.clock($0) } ?? StopTimer.defaultTime() }
        // 彈窗開著時錄音停了（按了停止、時間到）：不用設了
        .onChange(of: model.recorder.isRecording) { _, recording in if !recording { dismiss() } }
    }

    /// 說明行：幾點停、還有多久；格式不對時說要怎麼寫
    private func hint(_ time: (hour: Int, minute: Int)?, now: Date) -> String {
        guard let time else {
            return text.trimmingCharacters(in: .whitespaces).isEmpty
                ? String(localized: "輸入幾點幾分，到時間自動停止錄音並轉錄。") : String(localized: "請輸入像 15:30 的時間。")
        }
        let date = StopTimer.next(hour: time.hour, minute: time.minute, after: now)
        let clock = StopTimer.clock(date), remaining = StopTimer.remaining(date, now: now)
        return Calendar.current.isDate(date, inSameDayAs: now)
            ? String(localized: "今天 \(clock) 自動停止錄音並轉錄（\(remaining)後）。")
            : String(localized: "明天 \(clock) 自動停止錄音並轉錄（\(remaining)後）。")
    }

    private func save(_ time: (hour: Int, minute: Int)) {
        model.setStopTimer(StopTimer.next(hour: time.hour, minute: time.minute, after: Date()))
        dismiss()
    }
}
