import SwiftUI

struct MeetingDetailView: View {
    @Environment(AppModel.self) private var model
    let detail: MeetingDetail

    private var isRecordingThis: Bool { model.recorder.meetingId == detail.meeting.id }
    private var isTrashed: Bool { detail.meeting.deletedAt != nil }

    var body: some View {
        @Bindable var model = model
        VStack(spacing: 0) {
            MeetingTop(detail: detail, isRecording: isRecordingThis)
                .environment(\.textScale, 1)   // 標題列不跟著文字大小（R59.2）

            Divider()

            if isRecordingThis {
                RecordingPanel(detail: detail)
            } else {
                // 看以前的某一版會議紀錄時（R115.2），紀錄分頁和播放條的議題帶用那一版
                let shown = model.viewingNotes.map { detail.showingNotesVersion($0.notes) } ?? detail
                Group {
                    switch model.tab {
                    case .summary: NotesPage(detail: shown)
                    // 換會議時重新建立：編輯中的狀態、草稿是每場會議自己的（design §21.15）
                    case .transcript: TranscriptTab(detail: detail).id(detail.meeting.id)
                    case .ask: AskTab(detail: detail).id(detail.meeting.id)
                    }
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
                if detail.meeting.hasAudio { PlayerBar(detail: shown) }
            }
        }
        .environment(\.meetingLanguage, detail.meeting.meetingLanguage)   // 內容的字形、標題字型（design §18.6）
        .contentTextScale()   // 會議內容的文字大小（design §38.5）
        .navigationTitle(detail.meeting.displayTitle)
        .toolbar {
            if !isRecordingThis {
                ToolbarItem(placement: .principal) {
                    Picker("分頁", selection: $model.tab) {
                        ForEach(DetailTab.allCases) { tab in Text(tab.label).tag(tab) }
                    }
                    .pickerStyle(.segmented)
                    .labelsHidden()
                    .frame(width: 300)
                }
                // 垃圾桶裡的會議不能操作
                if !isTrashed {
                    // 匯出放在「更多」左邊，看得到（R60、design §38.6）
                    ToolbarItem(placement: .primaryAction) {
                        ExportMenu()
                    }
                    ToolbarItem(placement: .primaryAction) {
                        MoreMenu(detail: detail)
                    }
                }
            }
            SettingsToolbarItem()
        }
    }
}

struct MeetingHeader: View {
    @Environment(AppModel.self) private var model
    let detail: MeetingDetail
    @State private var editing = false
    /// 會議頁上方的說話者：點名字重新命名（R100，同逐字稿點說話者）
    @State private var renamingSpeaker: Int?
    @State private var speakerName = ""

    var body: some View {
        let meeting = detail.meeting
        VStack(alignment: .leading, spacing: 6) {
            HStack(alignment: .firstTextBaseline, spacing: 12) {
                Text(meeting.displayTitle)
                    .contentTitleFont(26)
                    .textSelection(.enabled)
                    .lineLimit(2)
                Button("編輯") { editing = true }
                    .buttonStyle(.plain)
                    .foregroundStyle(Color.celadon)
                    .font(.callout)
                    // 同其他彈窗的樣子（R106、design §80）
                    .sheet(isPresented: $editing) {
                        EditInfoForm(meeting: meeting) { editing = false }
                    }
                Spacer(minLength: 0)
            }
            // 範本和語言選單放在資訊列右邊，標題才能用整行的寬度（英文標題比較長，design §16.3）；
            // 資訊列放不下時（說話者多、英文字比較長），選單改到下一行，說話者名字才不會被截斷
            ViewThatFits(in: .horizontal) {
                HStack(spacing: 12) {
                    infoRow(meeting)
                    Spacer(minLength: 0)
                    menus(meeting)
                }
                VStack(alignment: .leading, spacing: 8) {
                    infoRow(meeting)
                    HStack(spacing: 12) { menus(meeting) }
                }
            }
        }
    }

    private func infoRow(_ meeting: Meeting) -> some View {
        HStack(spacing: 16) {
            if meeting.folderId != nil {
                Label(model.folderName(meeting.folderId), systemImage: "folder").help("這場會議所在的資料夾")
            }
            Text("\(meeting.meetingDate) \(DateText.weekday(meeting.meetingDate))")
            if meeting.durationSeconds != nil { Text(TimeFormat.duration(meeting.durationSeconds)) }
            if let minutes = detail.untranscribedMinutes {
                // 自動停止的錄音（design §35.3、R48.3）：後面沒人說話的部分沒有轉錄
                Label("最後 \(minutes) 分鐘沒有人說話，沒有轉錄", systemImage: "moon.zzz")
                    .help("錄音中很久沒人說話，拾音自動停止了錄音。要轉錄全部的話，用「更多 → 重新處理 → 重新轉錄」。")
            }
            if !detail.speakers.isEmpty {
                // 與會者：頭像加名字，最多 4 位，其他收成「+N」（design §59、R84.1）
                let ids = detail.speakers.keys.sorted()
                HStack(spacing: 10) {
                    ForEach(ids.prefix(Self.visibleSpeakers), id: \.self) { idx in speakerChip(idx) }
                    if ids.count > Self.visibleSpeakers {
                        Menu {
                            ForEach(ids.dropFirst(Self.visibleSpeakers), id: \.self) { idx in
                                Button(detail.speakers[idx] ?? "") { model.tab = .transcript }
                            }
                        } label: {
                            Text("+\(ids.count - Self.visibleSpeakers)")
                        }
                        .menuStyle(.borderlessButton)
                        .menuIndicator(.hidden)
                        .fixedSize()
                        .help("其他與會者")
                    }
                }
            } else if !meeting.participantList.isEmpty {
                Text(meeting.participantList.joined(separator: String(localized: "、")))
            }
        }
        .lineLimit(1)
        .font(.callout)
        .foregroundStyle(.secondary)
    }

    /// 會議頁上方最多顯示幾位與會者（design §59）
    private static let visibleSpeakers = 4

    /// 一位與會者：頭像加名字。選單按鈕只畫得出文字，頭像放在選單外面
    @ViewBuilder
    private func speakerChip(_ idx: Int) -> some View {
        let name = detail.speakers[idx] ?? ""
        HStack(spacing: 5) {
            PersonAvatar(name: name, color: Color.speaker(idx), size: 20, isMe: idx == detail.selfIdx, scales: false)
            if detail.speakerSources[idx] == "voiceprint" {
                AutoSpeakerName(idx: idx, name: name, others: detail.speakerCandidates[idx] ?? [])
            } else {
                Button { startRenaming(idx) } label: { Text(name).foregroundStyle(Color.speaker(idx)) }
                    .buttonStyle(.plain)
                    .help("重新命名「\(name)」，整場的逐字稿和會議紀錄都會跟著改")
                    .contextMenu { speakerMenu(idx) }   // 按鈕會吃掉右鍵，要掛在按鈕本身
            }
        }
        .contextMenu { speakerMenu(idx) }
        .popover(isPresented: Binding(get: { renamingSpeaker == idx }, set: { if !$0 { renamingSpeaker = nil } }), arrowEdge: .bottom) {
            // 同彈窗的元件（R106、design §80）
            VStack(alignment: .leading, spacing: 12) {
                Text("說話者名字").font(.title3).fontWeight(.bold)
                SheetTextField(placeholder: String(localized: "名字"), text: $speakerName, focusOnAppear: true) { saveSpeakerName(idx) }
                    .frame(width: 260)
                HStack(spacing: 8) {
                    Spacer()
                    Button("取消") { renamingSpeaker = nil }
                        .buttonStyle(SheetPillButtonStyle(.secondary))
                        .keyboardShortcut(.cancelAction)
                    Button("儲存") { saveSpeakerName(idx) }
                        .buttonStyle(SheetPillButtonStyle(.primary))
                        .keyboardShortcut(.defaultAction)
                        .disabled(speakerName.trimmingCharacters(in: .whitespaces).isEmpty)
                }
            }
            .padding(16)
        }
    }

    /// 說話者的右鍵（R100）：重新命名、合併到別的說話者（同逐字稿）
    @ViewBuilder
    private func speakerMenu(_ idx: Int) -> some View {
        Button("重新命名…") { startRenaming(idx) }
        let others = detail.speakers.keys.sorted().filter { $0 != idx }
        if !others.isEmpty {
            Menu("合併到") {
                ForEach(others, id: \.self) { other in
                    Button(detail.speakers[other] ?? "") { model.mergeSpeaker(idx, into: other) }
                }
            }
        }
    }

    private func startRenaming(_ idx: Int) {
        speakerName = detail.speakers[idx] ?? ""
        renamingSpeaker = idx
    }

    private func saveSpeakerName(_ idx: Int) {
        let name = speakerName.trimmingCharacters(in: .whitespaces)
        renamingSpeaker = nil
        guard !name.isEmpty, name != detail.speakers[idx] else { return }
        model.renameSpeaker(idx, to: name)
    }

    /// 「會議設定」：範本、會議語言、大約幾個人放在一起（design §59、R84.2）；文件選單照舊
    @ViewBuilder
    private func menus(_ meeting: Meeting) -> some View {
        let template = model.templates.first { $0.id == meeting.templateId }?.displayName ?? String(localized: "一般會議")
        let recordingThis = model.recorder.isRecording && model.recorder.meetingId == meeting.id
        Menu {
            Section("範本") {
                ForEach(model.templates) { item in
                    Button {
                        model.setTemplate(item.id)
                    } label: {
                        if item.id == meeting.templateId { Label(item.displayName, systemImage: "checkmark") }
                        else { Text(item.displayName) }
                    }
                }
            }
            // 會議語言（design §16.3）
            Section("會議語言") {
                ForEach(MeetingLanguage.allCases) { language in
                    Button {
                        model.setMeetingLanguage(language)
                    } label: {
                        if language == meeting.meetingLanguage { Label(language.name, systemImage: "checkmark") }
                        else { Text(language.name) }
                    }
                }
            }
            // 錄音中先填人數（design §26.2）：錄完轉錄時用
            if recordingThis {
                Section("大約幾個人") {
                    Button {
                        model.setMinSpeakers(nil)
                    } label: {
                        if meeting.minSpeakers == nil { Label("自動判斷", systemImage: "checkmark") } else { Text("自動判斷") }
                    }
                    ForEach(2...12, id: \.self) { n in
                        Button {
                            model.setMinSpeakers(n)
                        } label: {
                            if meeting.minSpeakers == n { Label("\(n) 人", systemImage: "checkmark") } else { Text("\(n) 人") }
                        }
                    }
                }
            }
        } label: {
            Label(recordingThis && meeting.minSpeakers != nil
                  ? String(localized: "\(template)・\(meeting.meetingLanguage.name)・大約 \(meeting.minSpeakers ?? 0) 人")
                  : String(localized: "\(template)・\(meeting.meetingLanguage.name)"),
                  systemImage: "slider.horizontal.3")
        }
        // 沒有框：跟資訊列同一層，不比標題搶眼（design §59）
        .menuStyle(.borderlessButton)
        .fixedSize()
        .help(recordingThis
              ? String(localized: "範本、會議語言、大約幾個人。人多、吵雜或手機放在桌上錄音時，先告訴拾音大約幾個人，說話者會分得比較準")
              : String(localized: "範本和會議語言。換了之後，從「更多 → 重新處理」重新轉錄或重新整理會議紀錄"))
        // 會議的參考文件（design §39.4）：垃圾桶裡的會議不放
        if meeting.deletedAt == nil { DocumentsMenu(detail: detail) }
    }
}

/// 修改標題、日期、與會者
struct EditInfoForm: View {
    @Environment(AppModel.self) private var model
    let meeting: Meeting
    let onDone: () -> Void
    @State private var title = ""
    @State private var date = Date()
    @State private var participants = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            SheetHeader(title: String(localized: "編輯會議資訊"), subtitle: String(localized: "標題、日期、與會者。"), onClose: onDone)
            VStack(alignment: .leading, spacing: 6) {
                SheetFieldLabel(String(localized: "標題"))
                SheetTextField(placeholder: String(localized: "標題"), text: $title, focusOnAppear: true)
            }
            VStack(alignment: .leading, spacing: 6) {
                SheetFieldLabel(String(localized: "會議日期"))
                SheetDateField(label: String(localized: "會議日期"), date: $date)
            }
            VStack(alignment: .leading, spacing: 6) {
                SheetFieldLabel(String(localized: "與會者"))
                SheetTextField(placeholder: String(localized: "用逗號分隔"), text: $participants)
            }
            HStack(spacing: 10) {
                Spacer()
                Button("取消", action: onDone)
                    .buttonStyle(SheetPillButtonStyle(.secondary))
                    .keyboardShortcut(.cancelAction)
                Button("儲存") {
                    // 標題欄預先填的是顯示用的標題（還沒命名時是檔名）；沒動過就照原樣存，不然會被當成自訂標題鎖住，整理時不會再取名
                    let edited = title.trimmingCharacters(in: .whitespaces)
                    model.updateInfo(title: edited == meeting.displayTitle ? meeting.title : edited,
                                     date: DateText.string(date), participants: participants)
                    onDone()
                }
                .buttonStyle(SheetPillButtonStyle(.primary))
                .keyboardShortcut(.defaultAction)
            }
        }
        .sheetChrome()
        .onAppear {
            title = meeting.displayTitle
            date = DateText.date(meeting.meetingDate) ?? Date()
            participants = meeting.participants
        }
    }
}

struct TrashBanner: View {
    @Environment(AppModel.self) private var model
    let meetingId: Int64

    var body: some View {
        HStack(spacing: 12) {
            Label("這場會議在垃圾桶裡，30 天後會永久刪除", systemImage: "trash")
            Spacer()
            Button("還原") { model.restore(meetingId) }
            Button("永久刪除", role: .destructive) { model.confirmPurge = [meetingId] }
        }
        .font(.callout)
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .background(Color.secondary.opacity(0.1), in: RoundedRectangle(cornerRadius: 8))
    }
}

/// 工作進度、失敗原因
struct StatusBanner: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openWindow) private var openWindow
    let detail: MeetingDetail

    var body: some View {
        if model.applyingEdit == detail.meeting.id {
            // 編輯會議、分成兩場、還原：接錄音要幾秒（design §81.5）
            HStack(spacing: 10) {
                ProgressView().controlSize(.small)
                Text("正在套用編輯…")
                Spacer(minLength: 12)
            }
            .font(.callout)
            .foregroundStyle(.secondary)
            .padding(.horizontal, 14)
            .padding(.vertical, 8)
            .background(Color.secondary.opacity(0.06), in: RoundedRectangle(cornerRadius: 8))
        } else if let status = model.detailStatus, status != .recording, let content = content(status) {
            HStack(spacing: 12) {
                content
                Spacer(minLength: 12)
                actions(status)
            }
            .font(.callout)
            .padding(.horizontal, 14)
            .padding(.vertical, 10)
            .background(background(status), in: RoundedRectangle(cornerRadius: 8))
        } else if let helper = model.runner.running.values.first(where: {
            $0.job.meetingId == detail.meeting.id && ($0.job.kind == .correct || $0.job.kind == .nameSpeakers || $0.job.kind == .rediarize)
        }) {
            // 背景的輔助工作（design §21.2）：不算會議的狀態，但讓使用者知道還在跑
            let provider = helper.provider?.shortLabel ?? ""
            HStack(spacing: 10) {
                ProgressView().controlSize(.small)
                switch helper.job.kind {
                case .correct: Text("正在用 \(provider) 檢查可能聽錯的字…")
                case .rediarize: Text("正在重新辨識說話者…")
                default: Text("正在用 \(provider) 猜說話者的名字…")
                }
                Spacer(minLength: 12)
                Button("取消") { model.cancelJobs() }
            }
            .font(.callout)
            .foregroundStyle(.secondary)
            .padding(.horizontal, 14)
            .padding(.vertical, 8)
            .background(Color.secondary.opacity(0.06), in: RoundedRectangle(cornerRadius: 8))
        }
    }

    private func content(_ status: MeetingStatus) -> AnyView? {
        let running = model.runner.runningJob(for: detail.meeting.id!)
        switch status {
        case .queued:
            let kind = detail.latestJob?.kind
            return AnyView(Label(kind == .transcribe ? "排隊等待轉錄" : "排隊等待整理", systemImage: "clock"))
        case .transcribing(_, _) where running?.server?.waiting == true:
            // 在等企業伺服器（連不上、登入失效，R50.5）：不顯示百分比
            return AnyView(Label(running?.server?.text ?? "", systemImage: "clock").lineLimit(2))
        case .transcribing(let progress, let stage):
            let note = running?.server?.text ?? ""
            return AnyView(HStack(spacing: 10) {
                Text(transcribingText(progress, stage: stage, remaining: running?.remaining) + (note.isEmpty ? "" : String(localized: "，\(note)")))
                    .monospacedDigit()
                if let progress {
                    ProgressView(value: progress).frame(width: 160).tint(.signal)
                } else {
                    ProgressView().controlSize(.small)
                }
            })
        case .summarizing:
            let provider = running?.provider?.shortLabel ?? ""
            return AnyView(HStack(spacing: 10) {
                ProgressView().controlSize(.small)
                switch running?.job.kind {
                case .revise: Text("正在用 \(provider) 依你的回答修正會議紀錄…")
                default: Text("正在用 \(provider) 整理會議紀錄…")
                }
            })
        case .failed(let kind, let stored):
            let message = MeetingLanguage.uiStored(stored)
            return AnyView(Label {
                Text(kind == .transcribe ? String(localized: "轉錄失敗：\(message)") : String(localized: "整理失敗：\(message)")).lineLimit(3).textSelection(.enabled)
            } icon: {
                Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.red)
            })
        case .cancelled:
            return AnyView(Label("已取消轉錄", systemImage: "xmark.circle"))
        default:
            return nil
        }
    }

    @ViewBuilder
    private func actions(_ status: MeetingStatus) -> some View {
        switch status {
        case .transcribing where model.runner.runningJob(for: detail.meeting.id!)?.server != nil:
            // 企業伺服器在排隊、連不上時，可以改在這台 Mac 轉錄（R50.5）
            Button("改在這台電腦轉錄") { model.runner.transcribeLocally(meetingId: detail.meeting.id!) }
            Button("取消") { model.cancelJobs() }
        case .queued, .transcribing, .summarizing: Button("取消") { model.cancelJobs() }
        case .failed(let kind, let message):
            // 設定問題（沒登入、key 錯誤…）而且還沒修好（R46.2、design §34.4）：給修的按鈕，不放「重試」（重試也一樣會失敗）
            if kind != .transcribe, LLMError.isSetupProblem(errorKind: detail.latestJob?.errorKind),
               let problem = model.summarizerProblem {
                Button(problem.actionTitle) {
                    switch problem.action {
                    case .openSettings: openWindow.settings(problem.settingsPage)
                    case .login(let provider): CLILogin.openLogin(provider)
                    }
                }
            } else {
                // 登入問題（3.1.2）：先引導重新登入，登入完再按重試
                if kind != .transcribe, LLMError.isLoginMessage(message),
                   AppSettings.provider == .claude || AppSettings.provider == .codex {
                    // 桌面版附的 codex：打開那個 app 登入（R55.3）
                    let title = CLILogin.desktopApp(AppSettings.provider).map { String(localized: "打開 \($0) 登入") }
                        ?? String(localized: "重新登入 \(AppSettings.provider.shortLabel)")
                    Button(title) { CLILogin.openLogin(AppSettings.provider) }
                }
                Button("重試") { model.retry() }
            }
        case .cancelled: Button("重新轉錄") { model.retry() }
        default: EmptyView()
        }
    }

    private func background(_ status: MeetingStatus) -> Color {
        if case .failed = status { return Color.red.opacity(0.1) }
        return Color.signal.opacity(0.12)
    }

    private func transcribingText(_ progress: Double?, stage: String, remaining: TimeInterval?) -> String {
        guard let progress else { return stage.isEmpty ? String(localized: "準備轉錄…") : "\(stage)…" }
        var text = String(localized: "\(stage.isEmpty ? String(localized: "轉錄") : stage)中 \(Int(progress * 100))%")
        if let remaining, remaining > 0 {
            text += remaining < 60 ? String(localized: "，快好了") : String(localized: "，約剩 \(Int((remaining / 60).rounded(.up))) 分鐘")
        }
        return text
    }
}

/// 右上角的「匯出」（R60、design §38.6）：按下打開匯出對話框，箭頭直接複製會議紀錄。
/// 按鈕和箭頭分成兩個控制項，各自有輔助使用的名字（A11Y1：分割選單的箭頭在 VoiceOver 沒有名字）。
/// 工具列的選單建立一次就不更新，動作在按下時才看目前選取的會議（同「更多」）
struct ExportMenu: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        ControlGroup {
            Button { model.openExportSheet() } label: {
                Label("匯出", systemImage: "square.and.arrow.up")
            }
            .labelStyle(.titleAndIcon)
            .help("匯出會議紀錄、逐字稿或錄音（⌘E）；箭頭可以直接複製會議紀錄")
            Menu {
                Button("複製會議紀錄：保留格式（貼到 Word、Notion、郵件）") { model.copyNotes(.rich) }
                Button("複製會議紀錄：Markdown") { model.copyNotes(.markdown) }
                Button("複製會議紀錄：純文字（貼到 Slack、訊息）") { model.copyNotes(.plain) }
                Divider()
                Button("複製心智圖（圖片）") { model.copyMindMap() }
            } label: {
                Label("複製", systemImage: "chevron.down")
            }
            .menuIndicator(.hidden)
            .help("複製會議紀錄或心智圖")
            .accessibilityLabel(Text("複製"))
        }
        .controlGroupStyle(.navigation)
    }
}

struct MoreMenu: View {
    @Environment(AppModel.self) private var model
    let detail: MeetingDetail

    // 工具列右上角的選單建立一次就不會更新（換會議、工作開始或結束都一樣，加 .id 也沒用），
    // 所以這裡不預先停用：按下時由 AppModel 檢查目前的會議，不能做就說明原因
    var body: some View {
        // 三項（R44.4、R60，design §33.2、§38.6）：重新處理的四個動作收成子選單；兩個「復原」回到紀錄的編輯列和說話者的改名處；
        // 匯出、複製會議紀錄在左邊的「匯出」
        Menu {
            Menu("重新處理", systemImage: "arrow.clockwise") {
                Button("重新整理會議紀錄", systemImage: "arrow.clockwise") { model.resummarize() }
                Button("檢查可能聽錯的字", systemImage: "text.magnifyingglass") { model.checkTranscript() }
                Button("再找不確定的地方", systemImage: "questionmark.bubble") { model.findMoreReview() }
                Button("重新轉錄（含說話者辨識）", systemImage: "waveform") { model.retranscribe() }
                Button("重新辨識說話者…", systemImage: "person.2.wave.2") { model.requestRediarize() }
            }
            // 編輯會議（R107.1）。選單建立一次就不更新，「還原」一直在，沒編輯過時按了說明（R107.8）
            Button("編輯會議…", systemImage: "scissors") { if let id = model.selection { model.requestEditMeeting(id) } }
            Button("還原成編輯前的樣子…", systemImage: "arrow.uturn.backward") { if let id = model.selection { model.requestRestoreEdit(id) } }
            Divider()
            // 工具列的選單建立一次就不會更新（停用狀態、按鈕記住的會議），動作一律在按下時才看目前選取的會議
            FolderMenu()
            DestructiveMenuButton(String(localized: "移到垃圾桶")) {
                if let id = model.selection { model.moveToTrash(id) }
            }
        } label: {
            Label("更多", systemImage: "ellipsis.circle")
        }
        // 工具列的選單只顯示圖示，輔助使用拿不到 Label 的文字，要另外給名字
        .accessibilityLabel(Text("更多"))
        .help("更多動作")
    }
}

/// 會議頁上方：標題、狀態或垃圾桶提示、草稿提示。截圖（DEBUG 的 snapshot）也用這一份
struct MeetingTop: View {
    let detail: MeetingDetail
    var isRecording = false

    var body: some View {
        let trashed = detail.meeting.deletedAt != nil
        VStack(alignment: .leading, spacing: 14) {
            MeetingHeader(detail: detail)
            if trashed { TrashBanner(meetingId: detail.meeting.id!) } else { StatusBanner(detail: detail) }
            if detail.meeting.notesDraft != 0 && detail.notes != nil && !isRecording && !trashed {
                DraftNotesBanner()
            }
            if !detail.speakerSuggestions.isEmpty && !isRecording && !trashed {
                SpeakerSuggestionsBanner(detail: detail)
            }
        }
        .padding(.horizontal, 32)
        .padding(.top, 22)
        .padding(.bottom, 12)
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

/// 會議紀錄是用即時逐字稿整理的草稿（design §21.3、R19.1）
struct DraftNotesBanner: View {
    var body: some View {
        Label("草稿：用錄音中的即時逐字稿整理的，正式轉錄完成後會自動更新", systemImage: "doc.badge.clock")
            .font(.callout)
            .foregroundStyle(.secondary)
            .padding(.horizontal, 14)
            .padding(.vertical, 8)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(Color.secondary.opacity(0.08), in: RoundedRectangle(cornerRadius: 8))
    }
}

/// 聲紋自動帶入的說話者名字（design §21.4、R19.3）：名字後面標「自動」。
/// 點一下直接選是別人（最像的幾位認識的人，一次改名、記住聲音、改會議紀錄，R33.4），或說不是他
private struct AutoSpeakerName: View {
    @Environment(AppModel.self) private var model
    let idx: Int
    let name: String
    let others: [String]

    var body: some View {
        HStack(spacing: 3) {
            Menu {
                if !others.isEmpty {
                    Section("是別人：") {
                        ForEach(others, id: \.self) { other in
                            Button(other) { model.renameSpeaker(idx, to: other) }
                        }
                    }
                }
                Button("其他人…（到逐字稿改名）") { model.tab = .transcript }
                Divider()
                Button("不是\(name)（不知道是誰）") { model.rejectVoiceprintMatch(idx) }
            } label: {
                Text(name).foregroundStyle(Color.speaker(idx))
            }
            .menuStyle(.borderlessButton)
            .menuIndicator(.hidden)
            .fixedSize()
            // 選單按鈕在畫面上只畫標題的文字（標籤裡的背景會被拿掉），「自動」標記要放在選單外面
            Text("自動")
                .font(.caption2)
                .padding(.horizontal, 4)
                .padding(.vertical, 1)
                .background(Color.secondary.opacity(0.15), in: Capsule())
        }
        .help("拾音認出這個人的聲音，自動帶入名字。不對的話點名字")
    }
}

/// AI 猜到的說話者名字（design §21.5、R19.2）：按「套用」才改名
private struct SpeakerSuggestionsBanner: View {
    @Environment(AppModel.self) private var model
    let detail: MeetingDetail
    @State private var showing = false

    var body: some View {
        let suggestions = detail.speakerSuggestions.sorted { $0.key < $1.key }
        HStack(spacing: 10) {
            Label("猜到 \(suggestions.count) 位說話者的名字", systemImage: "person.crop.circle.badge.questionmark")
            Spacer()
            Button("查看") { showing = true }
                .popover(isPresented: $showing, arrowEdge: .bottom) {
                    VStack(alignment: .leading, spacing: 12) {
                        Text("猜到的說話者名字").font(.title3).fontWeight(.bold)
                        ForEach(suggestions, id: \.key) { idx, suggestion in
                            VStack(alignment: .leading, spacing: 4) {
                                HStack(spacing: 8) {
                                    Text(detail.speakers[idx] ?? "").foregroundStyle(Color.speaker(idx))
                                    Image(systemName: "arrow.right").foregroundStyle(.secondary)
                                    Text(suggestion.name).fontWeight(.semibold)
                                    Spacer()
                                    Button("忽略") { model.dismissSpeakerSuggestion(idx) }.buttonStyle(.link)
                                    Button("套用") { model.applySpeakerSuggestion(idx) }.buttonStyle(.link)
                                }
                                HStack(alignment: .firstTextBaseline, spacing: 6) {
                                    if !suggestion.time.isEmpty { TimeButton(time: suggestion.time) }
                                    Text(suggestion.reason).font(.caption).foregroundStyle(.secondary)
                                        .fixedSize(horizontal: false, vertical: true)
                                }
                            }
                        }
                        Divider()
                        HStack {
                            Text("套用後，逐字稿和會議紀錄都會換成名字。").font(.caption).foregroundStyle(.secondary)
                            Spacer()
                            Button("全部套用") { model.applyAllSpeakerSuggestions(); showing = false }
                                .buttonStyle(SheetPillButtonStyle(.primary, compact: true))
                        }
                    }
                    .padding(18)
                    .frame(width: 420)
                }
        }
        .font(.callout)
        .padding(.horizontal, 14)
        .padding(.vertical, 8)
        .background(Color.celadon.opacity(0.08), in: RoundedRectangle(cornerRadius: 8))
    }
}
