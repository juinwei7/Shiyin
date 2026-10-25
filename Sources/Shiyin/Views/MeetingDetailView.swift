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

            Divider()

            if isRecordingThis {
                RecordingPanel(detail: detail)
            } else {
                Group {
                    switch model.tab {
                    case .summary: NotesPage(detail: detail)
                    // 換會議時重新建立：編輯中的狀態、草稿是每場會議自己的（design §21.15）
                    case .transcript: TranscriptTab(detail: detail).id(detail.meeting.id)
                    case .ask: AskTab(detail: detail).id(detail.meeting.id)
                    }
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
                if detail.meeting.hasAudio { PlayerBar(detail: detail) }
            }
        }
        .environment(\.meetingLanguage, detail.meeting.meetingLanguage)   // 內容的字形、標題字型（design §18.6）
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
                    ToolbarItem(placement: .primaryAction) {
                        MoreMenu(detail: detail)
                    }
                }
            }
        }
    }
}

struct MeetingHeader: View {
    @Environment(AppModel.self) private var model
    let detail: MeetingDetail
    @State private var editing = false

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
                    .popover(isPresented: $editing, arrowEdge: .bottom) {
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
            if !meeting.folder.isEmpty {
                Label(meeting.folder, systemImage: "folder").help("這場會議所在的資料夾")
            }
            Text("\(meeting.meetingDate) \(DateText.weekday(meeting.meetingDate))")
            if meeting.durationSeconds != nil { Text(TimeFormat.duration(meeting.durationSeconds)) }
            if !detail.speakers.isEmpty {
                HStack(spacing: 6) {
                    ForEach(detail.speakers.keys.sorted(), id: \.self) { idx in
                        if detail.speakerSources[idx] == "voiceprint" {
                            AutoSpeakerName(idx: idx, name: detail.speakers[idx] ?? "", others: detail.speakerCandidates[idx] ?? [])
                        } else {
                            Text(detail.speakers[idx] ?? "").foregroundStyle(Color.speaker(idx))
                        }
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

    @ViewBuilder
    private func menus(_ meeting: Meeting) -> some View {
        Menu {
            ForEach(model.templates) { template in
                Button {
                    model.setTemplate(template.id)
                } label: {
                    if template.id == meeting.templateId { Label(template.displayName, systemImage: "checkmark") }
                    else { Text(template.displayName) }
                }
            }
        } label: {
            Label(model.templates.first { $0.id == meeting.templateId }?.displayName ?? String(localized: "一般會議"), systemImage: "doc.text")
        }
        .fixedSize()
        .help("會議紀錄的範本。換了範本之後，從「更多 → 重新處理」選「重新整理會議紀錄」就會照新範本整理")
        // 會議語言（design §16.3）
        Menu {
            ForEach(MeetingLanguage.allCases) { language in
                Button {
                    model.setMeetingLanguage(language)
                } label: {
                    if language == meeting.meetingLanguage { Label(language.name, systemImage: "checkmark") }
                    else { Text(language.name) }
                }
            }
        } label: {
            Label(meeting.meetingLanguage.name, systemImage: "globe")
        }
        .fixedSize()
        .help("會議語言：決定轉錄和會議紀錄用的語言。改了之後，從「更多 → 重新處理」重新轉錄或重新整理會議紀錄")
        // 錄音中先填人數（design §26.2）：錄完轉錄時用
        if model.recorder.isRecording && model.recorder.meetingId == meeting.id {
            Menu {
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
            } label: {
                Label(meeting.minSpeakers.map { String(localized: "大約 \($0) 人") } ?? String(localized: "人數：自動判斷"),
                      systemImage: "person.2")
            }
            .fixedSize()
            .help("大約幾個人：人多、吵雜或手機放在桌上錄音時，先告訴拾音大約幾個人，說話者會分得比較準")
        }
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
        VStack(alignment: .leading, spacing: 14) {
            Form {
                TextField("標題", text: $title)
                DatePicker("會議日期", selection: $date, displayedComponents: .date)
                TextField("與會者", text: $participants, prompt: Text("用逗號分隔"))
            }
            .formStyle(.columns)
            HStack {
                Spacer()
                Button("取消", role: .cancel, action: onDone)
                    .keyboardShortcut(.cancelAction)
                Button("儲存") {
                    // 標題欄預先填的是顯示用的標題（還沒命名時是檔名）；沒動過就照原樣存，不然會被當成自訂標題鎖住，整理時不會再取名
                    let edited = title.trimmingCharacters(in: .whitespaces)
                    model.updateInfo(title: edited == meeting.displayTitle ? meeting.title : edited,
                                     date: DateText.string(date), participants: participants)
                    onDone()
                }
                .keyboardShortcut(.defaultAction)
                .buttonStyle(.borderedProminent)
            }
        }
        .padding(16)
        .frame(width: 380)
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
    @State private var confirm = false

    var body: some View {
        HStack(spacing: 12) {
            Label("這場會議在垃圾桶裡，30 天後會永久刪除", systemImage: "trash")
            Spacer()
            Button("還原") { model.restore(meetingId) }
            Button("永久刪除", role: .destructive) { confirm = true }
        }
        .font(.callout)
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
        .background(Color.secondary.opacity(0.1), in: RoundedRectangle(cornerRadius: 8))
        .confirmationDialog("永久刪除這場會議？", isPresented: $confirm) {
            Button("永久刪除", role: .destructive) { model.deletePermanently(meetingId) }
        } message: {
            Text("會議紀錄、逐字稿和錄音檔都會刪除，沒辦法復原。")
        }
    }
}

/// 工作進度、失敗原因
struct StatusBanner: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openSettings) private var openSettings
    let detail: MeetingDetail

    var body: some View {
        if let status = model.detailStatus, status != .recording, let content = content(status) {
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
        case .transcribing(let progress, let stage):
            return AnyView(HStack(spacing: 10) {
                Text(transcribingText(progress, stage: stage, remaining: running?.remaining)).monospacedDigit()
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
        case .failed(let kind, let message):
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
        case .queued, .transcribing, .summarizing: Button("取消") { model.cancelJobs() }
        case .failed(let kind, let message):
            // 設定問題（沒登入、key 錯誤…）而且還沒修好（R46.2、design §34.4）：給修的按鈕，不放「重試」（重試也一樣會失敗）
            if kind != .transcribe, LLMError.isSetupProblem(errorKind: detail.latestJob?.errorKind),
               let problem = model.summarizerProblem {
                Button(problem.actionTitle) {
                    switch problem.action {
                    case .openSettings: openSettings()
                    case .login(let provider): CLILogin.openLogin(provider)
                    }
                }
            } else {
                // 登入問題（3.1.2）：先引導重新登入，登入完再按重試
                if kind != .transcribe, LLMError.isLoginProblem(message),
                   AppSettings.provider == .claude || AppSettings.provider == .codex {
                    Button("重新登入 \(AppSettings.provider.shortLabel)") { CLILogin.openLogin(AppSettings.provider) }
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

struct MoreMenu: View {
    @Environment(AppModel.self) private var model
    let detail: MeetingDetail

    // 工具列右上角的選單建立一次就不會更新（換會議、工作開始或結束都一樣，加 .id 也沒用），
    // 所以這裡不預先停用：按下時由 AppModel 檢查目前的會議，不能做就說明原因
    var body: some View {
        // 五項（R44.4，design §33.2）：重新處理的四個動作收成子選單；兩個「復原」回到紀錄的編輯列和說話者的改名處
        Menu {
            Menu("重新處理", systemImage: "arrow.clockwise") {
                Button("重新整理會議紀錄", systemImage: "arrow.clockwise") { model.resummarize() }
                Button("檢查可能聽錯的字", systemImage: "text.magnifyingglass") { model.checkTranscript() }
                Button("重新轉錄（含說話者辨識）", systemImage: "waveform") { model.retranscribe() }
                Button("重新辨識說話者…", systemImage: "person.2.wave.2") { model.requestRediarize() }
            }
            Button("匯出…", systemImage: "square.and.arrow.up") { model.openExportSheet() }
            Menu("複製會議紀錄", systemImage: "doc.on.doc") {
                Button("保留格式（貼到 Word、Notion、郵件）") { model.copyNotes(.rich) }
                Button("Markdown") { model.copyNotes(.markdown) }
                Button("純文字（貼到 Slack、訊息）") { model.copyNotes(.plain) }
            }
            Divider()
            // 工具列的選單建立一次就不會更新（停用狀態、按鈕記住的會議），動作一律在按下時才看目前選取的會議
            FolderMenu()
            Button("移到垃圾桶", systemImage: "trash", role: .destructive) {
                if let id = model.selection { model.moveToTrash(id) }
            }
        } label: {
            Label("更多", systemImage: "ellipsis.circle")
        }
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
                        Text("猜到的說話者名字").font(.headline)
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
                                .buttonStyle(.borderedProminent)
                        }
                    }
                    .padding(16)
                    .frame(width: 420)
                }
        }
        .font(.callout)
        .padding(.horizontal, 14)
        .padding(.vertical, 8)
        .background(Color.celadon.opacity(0.08), in: RoundedRectangle(cornerRadius: 8))
    }
}
