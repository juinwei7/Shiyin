import SwiftUI
import UniformTypeIdentifiers

struct RootView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openWindow) private var openWindow
    @AppStorage("hasCompletedOnboarding") private var hasCompletedOnboarding = false

    var body: some View {
        @Bindable var model = model
        MainView()
            .tint(.celadon)
            .onAppear {
                let open = openWindow
                model.openMainWindow = {
                    open(id: "main")
                    NSApp.activate()
                }
                model.openWindowById = { open(id: $0) }
                // 第一次使用：打開引導（R10.2）
                if !hasCompletedOnboarding { openWindow(id: "onboarding") }
            }
            .fileImporter(isPresented: $model.showFileImporter,
                          allowedContentTypes: Importer.contentTypes + Importer.otherTypes,
                          allowsMultipleSelection: true) { result in
                if case .success(let urls) = result { model.requestImport(urls) }
            }
            .sheet(isPresented: $model.showImportSheet) { ImportSheet(urls: model.pendingImport) }
            .sheet(isPresented: $model.showExportSheet) { ExportSheet() }
            .sheet(isPresented: $model.showRediarizeSheet) { RediarizeSheet() }
            .alert("出了一點問題", isPresented: Binding(get: { model.errorMessage != nil },
                                                   set: { if !$0 { model.errorMessage = nil } })) {
                Button("好") {}
            } message: {
                Text(model.errorMessage ?? "")
            }
            .alert("拾音沒有麥克風權限", isPresented: Binding(get: { model.recorder.permissionDenied },
                                                       set: { model.recorder.permissionDenied = $0 })) {
                Button("打開系統設定") { Recorder.openPrivacySettings() }
                Button("取消", role: .cancel) {}
            } message: {
                Text("請到「系統設定 → 隱私權與安全性 → 麥克風」打開「拾音」，再重新開始錄音。")
            }
            .alert("錄音的提醒", isPresented: Binding(get: { model.recorder.failureMessage != nil },
                                                 set: { if !$0 { model.recorder.failureMessage = nil } })) {
                Button("好") {}
            } message: {
                Text(model.recorder.failureMessage ?? "")
            }
            .overlay(alignment: .bottom) { NoticeToast() }
    }
}

/// 短暫的提示訊息
struct NoticeToast: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        if let message = model.noticeMessage {
            Text(message)
                .font(.callout)
                .padding(.horizontal, 14)
                .padding(.vertical, 8)
                .background(.regularMaterial, in: Capsule())
                .padding(.bottom, 72)
                .task(id: message) {
                    try? await Task.sleep(for: .seconds(3))
                    if model.noticeMessage == message { model.noticeMessage = nil }
                }
        }
    }
}

struct MainView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openWindow) private var openWindow
    @AppStorage("hasSeenGuide") private var hasSeenGuide = false
    @State private var dropTargeted = false

    var body: some View {
        @Bindable var model = model
        NavigationSplitView {
            SidebarView()
                .navigationSplitViewColumnWidth(min: 240, ideal: 280, max: 380)
        } detail: {
            if let detail = model.detail {
                MeetingDetailView(detail: detail)
                    .id(detail.meeting.id)
            } else {
                EmptyDetailView()
            }
        }
        .dropDestination(for: URL.self) { urls, _ in
            model.requestImport(urls)
            return true
        } isTargeted: { dropTargeted = $0 }
        .overlay {
            if dropTargeted {
                RoundedRectangle(cornerRadius: 12)
                    .strokeBorder(Color.celadon, style: StrokeStyle(lineWidth: 3, dash: [8, 6]))
                    .padding(8)
                    .allowsHitTesting(false)
            }
        }
        .onAppear {
            // 完成引導後才自動打開使用說明（只一次）
            if !hasSeenGuide && UserDefaults.standard.bool(forKey: "hasCompletedOnboarding") {
                hasSeenGuide = true
                openWindow(id: "guide")
            }
        }
        .modifier(FolderPromptModifier())
        // 有新版時詢問（R10.6）
        .alert(String(localized: "拾音 \(model.updater.prompt?.version ?? "") 可以更新了"), isPresented: Binding(
            get: { model.updater.prompt != nil }, set: { if !$0 { model.updater.prompt = nil } })) {
            Button("稍後", role: .cancel) { model.updater.prompt = nil }
            Button("下載並更新") {
                if let manifest = model.updater.prompt {
                    model.updater.prompt = nil
                    model.noticeMessage = String(localized: "正在下載更新，完成後拾音會自動重新打開")
                    Task { await model.updater.install(manifest, model: model) }
                }
            }
        } message: {
            // 更新內容只列前幾項（R10.6、design §12）：tag 的說明中英各一段、每段好幾條，整段放進對話框太長
            Text([ReleaseNotes.text(model.updater.prompt?.notes, english: MeetingLanguage.interface == .en),
                  String(localized: "目前是 \(Updater.currentVersion)。更新時拾音會結束並重新打開，會議資料都會保留。")]
                .filter { !$0.isEmpty }.joined(separator: "\n\n"))
        }
        .confirmationDialog("永久刪除這場會議？", isPresented: Binding(get: { model.confirmPurge != nil }, set: { if !$0 { model.confirmPurge = nil } })) {
            Button("永久刪除", role: .destructive) {
                if let id = model.confirmPurge { model.deletePermanently(id) }
            }
        } message: {
            Text("會議紀錄、逐字稿和錄音都會刪掉，沒辦法還原。")
        }
        .confirmationDialog("把這場會議移到垃圾桶？", isPresented: $model.confirmDelete) {
            Button("移到垃圾桶", role: .destructive) {
                if let id = model.deleteTarget { model.moveToTrash(id) }
            }
        } message: {
            Text("30 天內都可以從垃圾桶還原。")
        }
    }
}

struct SidebarView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        @Bindable var model = model
        VStack(spacing: 0) {
            if model.searchQuery.trimmingCharacters(in: .whitespaces).isEmpty {
                meetingList
            } else {
                SearchResultsList()
            }
            Divider()
            if model.importingCount > 0 {
                // 大檔案在背景匯入（design §21.13、R20.4）
                HStack(spacing: 8) {
                    ProgressView().controlSize(.small)
                    Text("正在匯入 \(model.importingCount) 個檔案…").foregroundStyle(.secondary)
                    Spacer()
                }
                .font(.callout)
                .padding(.horizontal, 14)
                .padding(.top, 8)
            }
            HStack {
                Button {
                    model.showTrash.toggle()
                    model.selection = nil
                } label: {
                    Label(model.showTrash ? "回到所有會議" : "垃圾桶（\(model.trash.count)）",
                          systemImage: model.showTrash ? "chevron.backward" : "trash")
                }
                .buttonStyle(.borderless)
                .foregroundStyle(.secondary)
                Spacer()
                // 匯入放這裡：側邊欄的工具列放不下，會被收進「>>」（design §22.2、R21.5）
                if !model.showTrash {
                    Button { model.showFileImporter = true } label: {
                        Label("匯入", systemImage: "square.and.arrow.down")
                    }
                    .buttonStyle(.borderless)
                    .foregroundStyle(.secondary)
                    .help("匯入錄音、影片、逐字稿或 .shiyin 封包（⌘O）")
                }
            }
            .font(.callout)
            .padding(.horizontal, 14)
            .padding(.vertical, 8)
        }
        .searchable(text: $model.searchQuery, placement: .sidebar, prompt: "搜尋所有會議")
        .toolbar {
            ToolbarItemGroup {
                RecordingControls()
                // 問所有會議（design §21.8）；錄音中放不下，會被收進「>>」（design §22.2）
                if !model.recorder.isRecording {
                    Button { model.openWindowById?("ask") } label: {
                        Label("提問", systemImage: "questionmark.bubble")
                    }
                    .help("問所有會議，回答附上是哪一場會議、哪個時間點")
                }
            }
        }
    }

    private var meetingList: some View {
        @Bindable var model = model
        return List(selection: $model.selection) {
            if model.showTrash {
                Text("垃圾桶裡的會議 30 天後會永久刪除").font(.caption).foregroundStyle(.secondary)
            } else if !model.folders.isEmpty {
                Section("資料夾") {
                    FolderRow(name: String(localized: "所有會議"), count: model.meetings.count, symbol: "tray.full", selected: model.folderFilter == nil) {
                        model.folderFilter = nil
                    }
                    ForEach(model.folders, id: \.name) { folder in
                        FolderRow(name: folder.name, count: folder.count, symbol: "folder", selected: model.folderFilter == folder.name) {
                            model.folderFilter = folder.name
                        }
                        .contextMenu {
                            Button("重新命名…") { model.folderPrompt = .rename(folder.name) }
                            Button("刪除資料夾（會議會保留）", role: .destructive) { model.deleteFolder(folder.name) }
                        }
                    }
                }
            }
            Section(model.showTrash || model.folders.isEmpty ? "" : (model.folderFilter ?? String(localized: "所有會議"))) {
                ForEach(model.filteredMeetings) { row in
                    MeetingRowView(row: row, status: model.status(row))
                        .tag(row.id)
                        .contextMenu {
                            if model.showTrash {
                                Button("還原") { model.restore(row.id) }
                                Button("永久刪除…", role: .destructive) { model.confirmPurge = row.id }
                            } else {
                                FolderMenu(meetingId: row.id, current: row.meeting.folder)
                                Divider()
                                Button("移到垃圾桶", role: .destructive) { model.moveToTrash(row.id) }
                            }
                        }
                }
            }
        }
        .overlay {
            if model.filteredMeetings.isEmpty && model.showTrash {
                Text("垃圾桶是空的").foregroundStyle(.secondary)
            }
        }
    }
}

/// 側欄的資料夾列（R9.2）：點了就只看這個資料夾
struct FolderRow: View {
    let name: String
    let count: Int
    let symbol: String
    let selected: Bool
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack {
                Label(name, systemImage: symbol)
                    .foregroundStyle(selected ? Color.celadon : .primary)
                    .fontWeight(selected ? .semibold : .regular)
                Spacer()
                Text("\(count)").monospacedDigit().foregroundStyle(.secondary)
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(selected ? .isSelected : [])
    }
}

/// 「移到資料夾」選單：既有的資料夾、新資料夾、移出資料夾（R9.2）
struct FolderMenu: View {
    @Environment(AppModel.self) private var model
    /// nil：目前選取的會議。工具列的選單可能沿用之前建立的，按下時才決定是哪一場，不然會移到上一場會議
    var meetingId: Int64? = nil
    var current: String? = nil

    private var target: Int64? { meetingId ?? model.selection }
    private var folder: String { current ?? model.detail?.meeting.folder ?? "" }

    var body: some View {
        Menu("移到資料夾", systemImage: "folder") {
            if meetingId == nil {
                // 工具列的選單內容不會更新，列出全部資料夾和固定的「移出資料夾」，選到原本的資料夾時 AppModel 會說明
                ForEach(model.folders, id: \.name) { item in
                    Button(item.name) { if let target { model.moveToFolder(target, folder: item.name) } }
                }
                if !model.folders.isEmpty { Divider() }
                Button("新資料夾…") { if let target { model.folderPrompt = .create(target) } }
                Button("移出資料夾") { if let target { model.moveToFolder(target, folder: "") } }
            } else {
                ForEach(model.folders.filter { $0.name != folder }, id: \.name) { item in
                    Button(item.name) { if let target { model.moveToFolder(target, folder: item.name) } }
                }
                if !model.folders.filter({ $0.name != folder }).isEmpty { Divider() }
                Button("新資料夾…") { if let target { model.folderPrompt = .create(target) } }
                if !folder.isEmpty {
                    Button("移出「\(folder)」") { if let target { model.moveToFolder(target, folder: "") } }
                }
            }
        }
    }
}

/// 新資料夾、資料夾改名的輸入框
struct FolderPromptModifier: ViewModifier {
    @Environment(AppModel.self) private var model
    @State private var name = ""

    func body(content: Content) -> some View {
        content.alert(title, isPresented: Binding(get: { model.folderPrompt != nil }, set: { if !$0 { model.folderPrompt = nil } })) {
            TextField("資料夾名稱", text: $name)
            Button("取消", role: .cancel) { model.folderPrompt = nil }
            Button(confirmTitle) {
                let value = name.trimmingCharacters(in: .whitespacesAndNewlines)
                switch model.folderPrompt {
                case .create(let id)?: if !value.isEmpty { model.moveToFolder(id, folder: value) }
                case .rename(let old)?: model.renameFolder(old, to: value)
                case nil: break
                }
                model.folderPrompt = nil
            }
        } message: {
            Text(message)
        }
        .onChange(of: model.folderPrompt) { _, prompt in
            if case .rename(let old)? = prompt { name = old } else { name = "" }
        }
    }

    private var title: String {
        if case .rename? = model.folderPrompt { return String(localized: "重新命名資料夾") }
        return String(localized: "新資料夾")
    }
    private var confirmTitle: String {
        if case .rename? = model.folderPrompt { return String(localized: "重新命名") }
        return String(localized: "建立並移入")
    }
    private var message: String {
        if case .rename? = model.folderPrompt { return String(localized: "資料夾裡的會議都會跟著改。") }
        return String(localized: "例如專案名稱或客戶名稱。這場會議會移進新資料夾。")
    }
}

/// 全文搜尋結果（R9.1）：依會議分組，顯示摘錄
struct SearchResultsList: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        let groups = Dictionary(grouping: model.searchResults, by: \.meetingId)
        let order = model.searchResults.map(\.meetingId).reduce(into: [Int64]()) { if !$0.contains($1) { $0.append($1) } }
        List {
            if model.searchResults.isEmpty {
                Text("找不到「\(model.searchQuery)」").foregroundStyle(.secondary)
            }
            ForEach(order, id: \.self) { meetingId in
                Section(model.meetings.first { $0.id == meetingId }?.meeting.displayTitle ?? String(localized: "會議")) {
                    ForEach(groups[meetingId] ?? []) { hit in
                        Button { model.openSearchHit(hit) } label: {
                            VStack(alignment: .leading, spacing: 2) {
                                Text(kindLabel(hit.kind)).font(.caption2).foregroundStyle(.secondary)
                                Text(hit.snippet).font(.callout).lineLimit(3)
                                    .contentGlyphs(model.meetings.first { $0.id == meetingId }?.meeting.meetingLanguage ?? .zh)
                            }
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                    }
                }
            }
        }
    }

    private func kindLabel(_ kind: SearchHit.Kind) -> String {
        switch kind {
        case .title: return String(localized: "標題")
        case .segment: return String(localized: "逐字稿")
        case .notes: return String(localized: "會議紀錄")
        case .userNotes: return String(localized: "我的筆記")
        }
    }
}

struct MeetingRowView: View {
    let row: MeetingRow
    let status: MeetingStatus

    var body: some View {
        VStack(alignment: .leading, spacing: 3) {
            Text(row.meeting.displayTitle).lineLimit(1).contentGlyphs(row.meeting.meetingLanguage)
            HStack(spacing: 8) {
                Text(shortDate).monospacedDigit().foregroundStyle(.secondary)
                Text(status.label).foregroundStyle(color)
            }
            .font(.caption)
            if case .transcribing(let p?, _) = status {
                ProgressView(value: p).controlSize(.mini).tint(.signal)
            }
        }
        .padding(.vertical, 3)
    }

    private var shortDate: String {
        let parts = row.meeting.meetingDate.split(separator: "-")
        guard parts.count == 3 else { return row.meeting.meetingDate }
        let thisYear = String(Calendar.current.component(.year, from: Date()))
        return parts[0] == thisYear ? "\(parts[1])/\(parts[2])" : "\(parts[0])/\(parts[1])/\(parts[2])"
    }

    private var color: Color {
        switch status {
        case .recording, .failed: return .red
        case .queued, .transcribing, .summarizing, .review: return .signal
        default: return .secondary
        }
    }
}

/// 工具列的錄音按鈕（R1）：可以選錄音來源
struct RecordingControls: View {
    static func sourceLabel(_ source: String) -> String {
        switch source {
        case "both": return String(localized: "麥克風＋電腦聲音（線上會議）")
        case "system": return String(localized: "只錄電腦聲音")
        default: return String(localized: "只錄麥克風")
        }
    }

    @Environment(AppModel.self) private var model
    @AppStorage(AppSettings.recordSourceKey) private var source = "mic"
    @AppStorage(AppSettings.defaultMeetingLanguageKey) private var language: MeetingLanguage = .systemDefault

    var body: some View {
        if model.recorder.isRecording {
            // 側邊欄上方的空間很窄，只放「● 經過時間」和停止，避免整組被收進工具列的「>>」
            HStack(spacing: 4) {
                Button {
                    if let id = model.recorder.meetingId { model.reveal(id) }
                } label: {
                    HStack(spacing: 6) {
                        Circle().fill(model.recorder.isPaused ? Color.secondary : .red).frame(width: 8, height: 8)
                        RecorderClock(recorder: model.recorder)
                    }
                }
                .help(model.recorder.isPaused ? "已暫停，點一下打開錄音中的會議" : "錄音中，點一下打開這場會議")
                .accessibilityLabel(model.recorder.isPaused ? "已暫停" : "錄音中")
                Button { model.recorder.stop() } label: { Label("停止", systemImage: "stop.fill") }
                    .help("停止錄音並開始轉錄（⌘R）")
            }
        } else {
            // 這兩個選項是之後錄音的設定，選完畫面不會變，跳一個提示讓人知道選到了
            Menu {
                Picker("錄音來源", selection: Binding(get: { source }, set: { value in
                    source = value
                    model.noticeMessage = String(localized: "之後的錄音：\(Self.sourceLabel(value))")
                })) {
                    ForEach(["mic", "both", "system"], id: \.self) { Text(Self.sourceLabel($0)).tag($0) }
                }
                .pickerStyle(.inline)
                Picker("會議語言", selection: Binding(get: { language }, set: { value in
                    language = value
                    model.noticeMessage = String(localized: "之後的錄音都用\(value.uiName)")
                })) {
                    ForEach(MeetingLanguage.allCases) { Text($0.name).tag($0) }
                }
                .pickerStyle(.inline)
            } label: {
                Label(MeetingLanguage.ui(zh: "錄音", en: "Record"), systemImage: "record.circle")
            } primaryAction: {
                model.startRecording()
            }
            .help("開始錄音（⌘R），用\(language.uiName)辨識；點右邊的箭頭可以改錄音來源和語言")
            // 工具列的選單建立一次就不會更新勾選，選項變了就換一個新的選單
            .id("\(source)-\(language.rawValue)")
        }
    }
}

struct EmptyDetailView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        if model.showTrash {
            ContentUnavailableView("垃圾桶", systemImage: "trash", description: Text("選一場會議來還原或永久刪除。"))
        } else if model.meetings.isEmpty {
            ContentUnavailableView {
                Label("還沒有會議", systemImage: "waveform")
            } description: {
                Text("按「錄音」開始，或把錄音檔拖進來。")
            } actions: {
                HStack {
                    Button("開始錄音") { model.startRecording() }
                        .buttonStyle(.borderedProminent)
                    Button("匯入錄音…") { model.showFileImporter = true }
                    Button("看使用說明") { openWindow(id: "guide") }
                }
            }
        } else {
            ContentUnavailableView("從左邊選一場會議", systemImage: "waveform",
                                   description: Text("也可以把新的錄音檔拖進來。"))
        }
    }
}

/// 從視窗匯入時的對話框
struct ImportSheet: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    let urls: [URL]
    @State private var title = ""
    @State private var date = Date()
    @State private var participants = ""
    @State private var templateId = AppSettings.defaultTemplate
    @State private var language = AppSettings.defaultMeetingLanguage
    @State private var speakerCount = 0

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text(urls.count == 1 ? "匯入錄音" : "匯入 \(urls.count) 個錄音檔").font(.title3.weight(.semibold))
            VStack(alignment: .leading, spacing: 4) {
                ForEach(urls, id: \.self) { url in
                    Label(url.lastPathComponent, systemImage: "waveform").lineLimit(1).truncationMode(.middle)
                }
            }
            .foregroundStyle(.secondary)
            Form {
                if urls.count == 1 {
                    TextField("標題", text: $title, prompt: Text("選填，沒填就由整理時命名"))
                    DatePicker("會議日期", selection: $date, displayedComponents: .date)
                } else {
                    LabeledContent(String(localized: "會議日期"), value: String(localized: "依各檔案的檔名或修改日期"))
                }
                TextField("與會者", text: $participants, prompt: Text("選填，用逗號分隔"))
                Picker("範本", selection: $templateId) {
                    ForEach(model.templates) { Text($0.displayName).tag($0.id) }
                }
                Picker("會議語言", selection: $language) {
                    ForEach(MeetingLanguage.allCases) { Text($0.name).tag($0) }
                }
                .help("決定轉錄和會議紀錄用的語言")
                // 先填人數（design §26.2）
                Picker("大約幾個人", selection: $speakerCount) {
                    Text("自動判斷").tag(0)
                    ForEach(2...12, id: \.self) { n in Text("\(n) 人").tag(n) }
                }
                .help("人多、吵雜或手機放在桌上錄音時，先告訴拾音大約幾個人，說話者會分得比較準")
            }
            .formStyle(.columns)
            HStack {
                Spacer()
                Button("取消", role: .cancel) {
                    model.pendingImport = []
                    dismiss()
                }
                .keyboardShortcut(.cancelAction)
                Button("匯入並開始轉錄") {
                    model.confirmImport(ImportOptions(title: title, date: urls.count == 1 ? DateText.string(date) : nil,
                                                      participants: participants, templateId: templateId, language: language,
                                                      minSpeakers: speakerCount == 0 ? nil : speakerCount))
                }
                .keyboardShortcut(.defaultAction)
                .buttonStyle(.borderedProminent)
            }
        }
        .padding(20)
        .frame(width: 460)
        .onAppear {
            if let first = urls.first,
               let inferred = DateText.date(Importer.inferDate(filename: first.lastPathComponent, modified: Importer.modificationDate(first))) {
                date = inferred
            }
        }
    }
}
