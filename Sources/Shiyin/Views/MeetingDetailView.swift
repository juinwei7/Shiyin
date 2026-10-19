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
                    case .summary: SummaryTab(detail: detail)
                    case .topics: TopicsTab(detail: detail)
                    case .transcript: TranscriptTab(detail: detail)
                    case .review: ReviewTab(detail: detail)
                    case .notes: UserNotesTab(detail: detail)
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
                        ForEach(DetailTab.allCases) { tab in
                            if tab == .review, let count = detail.notes?.reviewItems.count, count > 0 {
                                Text("\(tab.label) \(count)").tag(tab)
                            } else {
                                Text(tab.label).tag(tab)
                            }
                        }
                    }
                    .pickerStyle(.segmented)
                    .labelsHidden()
                    .frame(width: 380)
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
                        Text(detail.speakers[idx] ?? "").foregroundStyle(Color.speaker(idx))
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
        .help("會議紀錄的範本。換了範本之後，從「更多」選「重新整理會議紀錄」就會照新範本整理")
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
        .help("會議語言：決定轉錄和會議紀錄用的語言。改了之後，從「更多」重新轉錄或重新整理會議紀錄")
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
        } else if !model.canTranscribe(detail.meeting.meetingLanguage) && detail.segments.isEmpty && detail.meeting.hasAudio {
            HStack(spacing: 12) {
                if detail.meeting.meetingLanguage == .ja {
                    Label("日文會議要用日文轉錄模型（574 MB），下載完成後才能轉錄", systemImage: "arrow.down.circle")
                    Spacer()
                    if model.models.state(.japanese).isBusy {
                        ProgressView().controlSize(.small)
                    } else {
                        Button("下載") { model.models.start(.japanese) }
                    }
                } else {
                    Label("還沒有下載轉錄模型，下載完成後才能轉錄", systemImage: "arrow.down.circle")
                    Spacer()
                }
                Button("改用 macOS 內建辨識") { model.useSystemTranscriber() }
                    .help("不用下載模型、速度快，但不參考詞彙表，英文專有名詞比較容易錯。之後可以在設定 → 錄音改回來")
                SettingsLink { Text("前往下載") }
            }
            .font(.callout)
            .padding(.horizontal, 14)
            .padding(.vertical, 10)
            .background(Color.signal.opacity(0.12), in: RoundedRectangle(cornerRadius: 8))
        }
    }

    private func content(_ status: MeetingStatus) -> AnyView? {
        let running = model.runner.runningJob(for: detail.meeting.id!)
        switch status {
        case .queued:
            let kind = detail.latestJob?.kind
            let language = detail.meeting.meetingLanguage
            if kind == .transcribe && !model.canTranscribe(language) {
                // 顯示下載進度；暫停或失敗時可以直接在這裡繼續，不用到設定
                let which = ModelStore.transcription(for: language)
                return AnyView(HStack(spacing: 10) {
                    Label(language == .ja ? "等日文轉錄模型下載完成就會開始轉錄" : "等轉錄模型下載完成就會開始轉錄", systemImage: "arrow.down.circle")
                    switch model.models.state(which) {
                    case .downloading(let received, let total) where total > 0:
                        ProgressView(value: Double(received), total: Double(total)).frame(width: 140).tint(.signal)
                    case .downloading, .verifying:
                        ProgressView().controlSize(.small)
                    case .missing:
                        Button("下載") { model.models.start(which) }
                    case .paused, .failed:
                        Button("繼續下載") { model.models.start(which) }
                    case .installed:
                        EmptyView()
                    }
                })
            }
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
                case .correct: Text("正在用 \(provider) 檢查可能聽錯的字…")
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
            // 登入問題（3.1.2）：先引導重新登入，登入完再按重試
            if kind != .transcribe, LLMError.isLoginProblem(message),
               AppSettings.provider == .claude || AppSettings.provider == .codex {
                Button("重新登入 \(AppSettings.provider.shortLabel)") { CLILogin.openLogin(AppSettings.provider) }
            }
            Button("重試") { model.retry() }
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
        Menu {
            Button("重新整理會議紀錄", systemImage: "arrow.clockwise") { model.resummarize() }
            Button("檢查可能聽錯的字", systemImage: "text.magnifyingglass") { model.checkTranscript() }
            Button("重新轉錄（含說話者辨識）", systemImage: "waveform") { model.retranscribe() }
            Button("復原上一版會議紀錄", systemImage: "arrow.uturn.backward") { model.undoNotes() }
            Divider()
            Button("匯出…", systemImage: "square.and.arrow.up") { model.openExportSheet() }
            Menu("複製會議紀錄", systemImage: "doc.on.doc") {
                Button("富文字（貼到 Word、Notion、郵件）") { model.copyNotes(.rich) }
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
