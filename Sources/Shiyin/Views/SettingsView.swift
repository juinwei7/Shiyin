import SwiftUI

/// 設定的每一頁（R108、design §82、D184）：側欄的順序、分組、圖示和顏色、搜尋用的關鍵字
enum SettingsPage: String, CaseIterable, Identifiable {
    case provider, templates, recording, transcription, models, speakers, glossary, general, ai, enterprise, status

    var id: String { rawValue }
    static let storageKey = "settingsPage"
    /// 設定視窗的 id（D185）
    static let windowID = "settings"

    var title: String {
        switch self {
        case .provider: String(localized: "整理工具")
        case .templates: MeetingLanguage.ui(zh: "範本", en: "Templates")
        case .recording: MeetingLanguage.ui(zh: "錄音", en: "Recording")
        case .transcription: MeetingLanguage.ui(zh: "轉錄", en: "Transcription")   // 「轉錄」在別處是動詞（Transcribing）
        case .models: String(localized: "模型與空間")
        case .speakers: MeetingLanguage.ui(zh: "說話者", en: "Speakers")
        case .glossary: String(localized: "詞彙表")
        case .general: String(localized: "一般")
        case .ai: String(localized: "AI 工具")
        case .enterprise: String(localized: "企業伺服器")
        case .status: String(localized: "檢查與更新")
        }
    }

    var symbol: String {
        switch self {
        case .provider: "sparkles"
        case .templates: "doc.text.fill"
        case .recording: "mic.fill"
        case .transcription: "waveform"
        case .models: "internaldrive.fill"
        case .speakers: "person.2.fill"
        case .glossary: "character.book.closed.fill"
        case .general: "gearshape.fill"
        case .ai: "puzzlepiece.extension.fill"
        case .enterprise: "building.2.fill"
        case .status: "checkmark.seal.fill"
        }
    }

    /// 圖示的底色（同系統設定的彩色方塊，深淺色都用同一個）
    var color: Color {
        switch self {
        case .provider: Color(red: 0.55, green: 0.36, blue: 0.85)
        case .templates: Color(red: 0.95, green: 0.55, blue: 0.16)
        case .recording: Color(red: 0.92, green: 0.26, blue: 0.26)
        case .transcription: Color(red: 0.16, green: 0.48, blue: 0.95)
        case .models, .general: Color(red: 0.52, green: 0.53, blue: 0.57)
        case .speakers: Color(red: 0.11, green: 0.66, blue: 0.68)
        case .glossary: Color(red: 0.62, green: 0.44, blue: 0.28)
        case .ai: Color(red: 0.35, green: 0.35, blue: 0.86)
        case .enterprise: Color(red: 0.33, green: 0.45, blue: 0.62)
        case .status: Color(red: 0.2, green: 0.68, blue: 0.36)
        }
    }

    /// 側欄的分組（組和組之間空一點）
    static let groups: [[SettingsPage]] = [[.provider, .templates], [.recording, .transcription, .models], [.speakers, .glossary],
                                           [.general, .ai, .enterprise, .status]]

    /// 搜尋用（R108.4）：那一頁項目的名稱，加上常用的說法
    var keywords: [String] {
        switch self {
        case .provider:
            [String(localized: "轉錄完成後自動整理成會議紀錄"), String(localized: "轉錄完成後檢查可能聽錯的字"), String(localized: "模型"),
             String(localized: "測試整理工具"), "Claude", "Codex", "ChatGPT", "OpenAI", "Gemini", "Anthropic", "API key"]
        case .templates: [String(localized: "要產生的段落"), String(localized: "自訂段落"), String(localized: "設為預設範本")]
        case .recording:
            [String(localized: "錄音來源"), String(localized: "麥克風"), String(localized: "麥克風權限設定"), String(localized: "系統錄音權限設定"),
             String(localized: "偵測到線上會議時提醒錄音"), String(localized: "會議結束時自動停止錄音"), String(localized: "在螢幕上方顯示錄音島"),
             String(localized: "提醒與快捷鍵"), "Zoom", "Teams", "Google Meet"]
        case .transcription:
            [String(localized: "正式轉錄"), String(localized: "轉錄時辨識說話者"), String(localized: "說話者辨識的模型"),
             String(localized: "錄音中顯示即時逐字稿"), String(localized: "預設會議語言"), String(localized: "中文的字"),
             String(localized: "自動偵測會議語言"), "Breeze", "Qwen", "Whisper", "Nemotron"]
        case .models:
            [String(localized: "模型"), String(localized: "儲存空間"), String(localized: "資料位置"),
             String(localized: "轉錄完成後把錄音壓縮成 m4a（約每小時 10～30 MB）"), "Breeze", "Qwen", "Whisper"]
        case .speakers: [String(localized: "記住說話者的聲音，之後的會議自動帶入名字"), String(localized: "我的聲音"), String(localized: "認識的人")]
        case .glossary: [String(localized: "新增詞彙")]
        case .general:
            [String(localized: "會議內容的文字大小"), String(localized: "分享畫面時藏起拾音的視窗"), String(localized: "重新顯示首次使用引導"),
             String(localized: "重建搜尋索引"), String(localized: "匿名使用統計")]
        case .ai: [String(localized: "讓 AI 工具讀取會議"), "MCP", "Claude Code", "Claude Desktop", "Codex"]
        case .enterprise: [String(localized: "網址"), String(localized: "登入")]
        case .status: [String(localized: "更新"), String(localized: "檢查更新"), String(localized: "目前版本"), String(localized: "環境檢查")]
        }
    }

    func matches(_ query: String) -> Bool {
        let q = query.trimmingCharacters(in: .whitespaces)
        return q.isEmpty || ([title] + keywords).contains { $0.localizedCaseInsensitiveContains(q) }
    }
}

extension OpenWindowAction {
    /// 打開設定（R108；設定是一般視窗，D185）。給頁面就停在那一頁（設定會記住上次的頁面，R108.5；已經開著時跟著換頁）
    func settings(_ page: SettingsPage? = nil) {
        if let page { UserDefaults.standard.set(page.rawValue, forKey: SettingsPage.storageKey) }
        callAsFunction(id: SettingsPage.windowID)
    }
}

/// 設定視窗（R108、design §82）：左邊側欄、右邊那一頁
struct SettingsView: View {
    #if DEBUG
    /// shiyinctl preview settings <頁面>：預覽視窗一開始停在哪一頁（代號或 0～10）
    nonisolated(unsafe) static var previewPage: SettingsPage?
    /// shiyinctl preview settings <頁面> height=<點>：拉高看下面的段落（桌面層的預覽視窗捲不了）
    nonisolated(unsafe) static var previewHeight: CGFloat?
    #endif
    @AppStorage(SettingsPage.storageKey) private var storedPage = SettingsPage.provider.rawValue
    @State private var page: SettingsPage
    @State private var query = ""

    init() {
        // 上次停在哪一頁（R108.5）；DEBUG 的預覽指定起始頁，不寫進設定
        var start = SettingsPage(rawValue: UserDefaults.standard.string(forKey: SettingsPage.storageKey) ?? "") ?? .provider
        #if DEBUG
        if let previewPage = Self.previewPage { start = previewPage }
        #endif
        _page = State(initialValue: start)
    }

    private func select(_ value: SettingsPage) {
        page = value
        storedPage = value.rawValue
    }

    var body: some View {
        NavigationSplitView {
            List(selection: Binding(get: { page }, set: { if let value = $0 { select(value) } })) {
                ForEach(Array(SettingsPage.groups.enumerated()), id: \.offset) { _, group in
                    let pages = group.filter { $0.matches(query) }
                    if !pages.isEmpty {
                        Section {
                            ForEach(pages) { item in
                                Label { Text(item.title) } icon: { SettingsIcon(page: item) }
                                    // 選到的列灰底、字照常（同主視窗的側欄，R101.4），不用系統青瓷綠的一整塊
                                    .frame(maxWidth: .infinity, alignment: .leading)
                                    .sidebarSelectableRow(selected: item == page)
                                    .tag(item)
                            }
                        }
                    }
                }
            }
            .listStyle(.sidebar)
            .searchable(text: $query, placement: .sidebar, prompt: Text("搜尋設定"))
            .onChange(of: query) { _, value in
                // 只剩一頁時直接選它（R108.4）
                let found = SettingsPage.allCases.filter { $0.matches(value) }
                if !value.isEmpty, found.count == 1, let only = found.first { select(only) }
            }
            .onChange(of: storedPage) { _, value in
                // 從會議頁的「打開設定」指定頁面時，開著的設定視窗跟著換
                // 側欄還留著搜尋的字、篩掉了要去的那頁時清掉搜尋
                if let target = SettingsPage(rawValue: value), target != page {
                    page = target
                    if !target.matches(query) { query = "" }
                }
            }
            .navigationSplitViewColumnWidth(min: 210, ideal: 220, max: 260)
            .frame(minWidth: 210)   // 欄寬有時候不生效（設定視窗第一次打開），清單自己也要撐開
            .toolbar(removing: .sidebarToggle)
        } detail: {
            SettingsPageView(page: page)
                .navigationTitle(page.title)
        }
        .frame(minWidth: 720, idealWidth: 820, minHeight: 500, idealHeight: Self.idealHeight)
        .tint(.celadon)
    }

    private static var idealHeight: CGFloat {
        #if DEBUG
        return previewHeight ?? 620
        #else
        return 620
        #endif
    }
}

/// 側欄的圖示：彩色圓角方塊加白色符號（同系統設定）
struct SettingsIcon: View {
    let page: SettingsPage

    var body: some View {
        Image(systemName: page.symbol)
            .font(.system(size: 11, weight: .semibold))
            .foregroundStyle(.white)
            .frame(width: 20, height: 20)
            .background(RoundedRectangle(cornerRadius: 5, style: .continuous).fill(page.color.gradient))
            .accessibilityHidden(true)
    }
}

/// 右邊的那一頁：原本的段落照搬（R108.6，項目和存的鍵都沒變）
struct SettingsPageView: View {
    let page: SettingsPage
    /// 範本頁外層 Form 的上下留白（量的：再多會出現外層的捲軸）
    static let templatePageInset: CGFloat = 60

    var body: some View {
        switch page {
        case .provider: ProviderSettings()
        case .templates:
            // 高度跟著視窗（R112.5）：減掉外層 Form 的上下留白，最少 460（原本固定最少 460，編輯區在小框裡捲）
            GeometryReader { page in
                Form { Section { TemplateSettings().frame(height: max(460, page.size.height - Self.templatePageInset)) } }.formStyle(.grouped)
            }
        case .recording: RecordingSettings(part: .recording)
        case .transcription: RecordingSettings(part: .transcription)
        case .models: ModelsSettings()
        case .speakers: Form { Section { SpeakerSettings() } }.formStyle(.grouped)
        case .glossary: Form { Section { GlossarySettings() } }.formStyle(.grouped)
        case .general: AdvancedSettings(part: .general)
        case .ai: Form { Section { MCPSettings() } }.formStyle(.grouped)
        case .enterprise: Form { Section { EnterpriseServerSettings() } }.formStyle(.grouped)
        case .status: AdvancedSettings(part: .status)
        }
    }
}

// MARK: 整理工具與金鑰（R6.1）

struct ProviderSettings: View {
    @AppStorage(AppSettings.compatibleSendsImagesKey) private var compatibleSendsImages = false
    @Environment(AppModel.self) private var model
    @AppStorage(AppSettings.providerKey) private var storedProvider = ""
    @AppStorage(AppSettings.autoSummarizeKey) private var autoSummarize = true
    @AppStorage(AppSettings.correctTranscriptKey) private var correctTranscript = true
    @State private var modelName = ""
    @State private var baseURL = ""
    @State private var key = ""
    @State private var keySaved = false
    @State private var keyError: String?
    @State private var availableModels: [String] = []
    // 「選擇」的模型清單：按了才取（取的時候要讀 key，可能跳出鑰匙圈的確認，design §22.1）
    @State private var pickingModel = false
    @State private var loadingModels = false
    @State private var modelListError: String?
    @State private var testing = false
    @State private var testResult: (ok: Bool, message: String)?
    @State private var login: CLILogin.Status?
    @State private var checkingLogin = false
    @State private var recheckCount = 0
    /// 公司提供的模型（R51.1）：nil 是還沒取到或取不到
    @State private var companyModels: [EnterpriseAPI.Model]?

    /// 選單顯示的值：沒選過時顯示自動選到的那一個，找不到 CLI 時是「之後再設定」（空字串）；改了才寫進設定（R35.3）
    private var provider: String {
        if LLMProvider(rawValue: storedProvider) != nil { return storedProvider }
        let detected = AppSettings.detectedProvider
        return detected == .none ? "" : detected.rawValue
    }
    private var current: LLMProvider { LLMProvider(rawValue: provider) ?? .none }

    var body: some View {
        Form {
            Picker("整理工具", selection: Binding(get: { provider }, set: { storedProvider = $0 })) {
                // 這台 Mac 沒有 claude、codex 時才有：選它等於清掉設定，回到「還沒設定」
                if AppSettings.detectedProvider == .none { Text("之後再設定（先不整理）").tag("") }
                // 沒安裝的 claude、codex 不能選（R45.3）；目前選的就是它時照樣顯示
                // 公司：登入了企業伺服器、公司有提供整理才出現（R51.1、D124；登入在進階）
                ForEach(LLMProvider.allCases.filter { $0 != .enterprise || EnterpriseServer.shared.account?.llm == true || $0 == current }) { item in
                    let missing = (item == .claude || item == .codex) && Paths.findTool(item == .claude ? "claude" : "codex") == nil
                    Text(missing ? String(localized: "\(item.label)（沒有安裝）") : item == .enterprise ? companyLabel : item.label).tag(item.rawValue)
                        .selectionDisabled(missing && item != current)
                }
            }
            // 還沒準備好：說原因，設定好之前不會自動整理（R45.3、design §34.3）
            if current != .none, let notice = problemNotice {
                Text(notice).font(.caption).foregroundStyle(.red).fixedSize(horizontal: false, vertical: true)
            }
            if current == .claude || current == .codex {
                LabeledContent("狀態") {
                    HStack(spacing: 8) {
                        if Paths.findTool(current == .claude ? "claude" : "codex") == nil {
                            Text(current == .codex ? "這台 Mac 沒有安裝 codex：沒有在用 Codex 的話，請改選 API"
                                 : showClaudeHint ? "這台 Mac 沒有安裝 Claude Code（命令列版），安裝方法在下面"
                                 : "這台 Mac 沒有安裝 claude：沒有在用 Claude Code 的話，請改選 API")
                                .foregroundStyle(.secondary)
                        } else if checkingLogin {
                            Text("檢查登入狀態…").foregroundStyle(.secondary)
                        } else if let login {
                            Text(login.loggedIn ? String(localized: "已登入 \(login.detail)") : String(localized: "沒有登入"))
                                .foregroundStyle(login.loggedIn ? Color.celadon : Color.red)
                            if !login.loggedIn {
                                // 桌面版附的 codex：打開那個 app 登入（R55.3）
                                Button(desktopApp.map { String(localized: "打開 \($0) 登入") } ?? String(localized: "登入 \(current.shortLabel)")) {
                                    CLILogin.openLogin(current)
                                }
                            }
                        } else {
                            Text("已安裝，看不出登入狀態").foregroundStyle(.secondary)
                        }
                        Button { recheck() } label: { Image(systemName: "arrow.clockwise") }
                            .buttonStyle(.borderless)
                            .help("重新檢查登入狀態")
                            .disabled(checkingLogin)
                    }
                }
                // 用的是桌面版附的 codex：寫出來，使用者才知道沒有另外裝 CLI（R55.3）
                if let desktopApp {
                    Text("使用 \(desktopApp) 附的 codex（這台 Mac 沒有另外安裝 Codex CLI）")
                        .font(.caption).foregroundStyle(.secondary)
                }
            }
            if showClaudeHint {
                ClaudeCodeInstallHint { recheck() }
            }
            // OpenAI 相容 API 的網址（design §19.3、§19.4）
            if current == .openaiCompatible {
                HStack {
                    TextField("網址", text: $baseURL, prompt: Text(verbatim: "https://api.groq.com/openai/v1"))
                    Menu("常用") {
                        ForEach(OpenAICompatibleAPI.presets, id: \.url) { preset in
                            Button { baseURL = preset.url } label: { Text(verbatim: "\(preset.name)  \(preset.url)") }
                        }
                    }
                    .fixedSize()
                }
                .onChange(of: baseURL) { _, value in
                    UserDefaults.standard.set(value, forKey: OpenAICompatibleAPI.baseURLKey)
                    availableModels = []
                    modelListError = nil
                }
                if !baseURL.trimmingCharacters(in: .whitespaces).isEmpty && OpenAICompatibleAPI.normalized(baseURL) == nil {
                    Text("網址要以 http:// 或 https:// 開頭").font(.caption).foregroundStyle(.red)
                }
                // 會議中的畫面（design §83.5、D188）：很多本機模型不能看圖，預設只送畫面上辨識出的文字
                Toggle("整理時附上會議中的畫面（模型要能看圖）", isOn: $compatibleSendsImages)
                Text("關掉時只送畫面上辨識出的文字。模型不能看圖時拾音會自動改成只送文字。")
                    .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
            // 公司：模型由公司的後台決定，這裡只顯示（R51.1）
            if current == .enterprise && EnterpriseServer.shared.isSignedIn && EnterpriseServer.shared.account?.llm == true {
                companyModelRow
            }
            if current.usesAPIKey {
                SecureField("API key", text: $key, prompt: Text(keySaved ? "已儲存在鑰匙圈（輸入新的會取代）"
                                                                 : current.keyOptional ? "可以不填（本機的 Ollama、LM Studio 不需要）" : "貼上 API key"))
                HStack {
                    Button("儲存 key", action: saveKey)
                        .disabled(key.trimmingCharacters(in: .whitespaces).isEmpty)
                    if keySaved {
                        Button("刪除 key", role: .destructive) {
                            Keychain.delete(current.keychainAccount)
                            keySaved = false
                            availableModels = []
                            model.refreshEnvironment()
                        }
                    }
                }
                if let keyError {
                    Text(keyError).font(.caption).foregroundStyle(.red).fixedSize(horizontal: false, vertical: true)
                }
                if let url = current.apiKeyURL { Link("取得 API key", destination: url) }
            }
            if current != .none && current != .enterprise {
                HStack {
                    TextField("模型", text: $modelName, prompt: Text(current == .openaiCompatible ? "必填"
                                                                    : current == .enterprise ? "留空使用公司設定的預設模型"
                                                                    : current.defaultModel.isEmpty ? "留空使用預設模型" : "預設：\(current.defaultModel)"))
                    if canListModels {
                        Button("選擇", action: openModelPicker)
                            .popover(isPresented: $pickingModel, arrowEdge: .bottom) { modelList }
                    }
                }
                .onChange(of: modelName) { _, value in UserDefaults.standard.set(value, forKey: current.modelKey) }
            }
            // 測試：送一個很短的請求，確認登入、key、模型都能用（使用者回報：開完會才發現登入過期）
            if current != .none && (current != .enterprise || EnterpriseServer.shared.isSignedIn) {
                LabeledContent("測試") {
                    HStack(spacing: 8) {
                        if let testResult {
                            Text(testResult.message)
                                .foregroundStyle(testResult.ok ? Color.celadon : Color.red)
                                .fixedSize(horizontal: false, vertical: true)
                        }
                        Button(testing ? "測試中…" : "測試整理工具") { Task { await test() } }
                            .disabled(testing)
                    }
                }
            }
            Toggle("轉錄完成後自動整理成會議紀錄", isOn: $autoSummarize)
                .disabled(current == .none)
            // 逐字稿校正（design §17.4）
            Toggle("轉錄完成後檢查可能聽錯的字", isOn: $correctTranscript)
                .disabled(current == .none)
            Text("依詞彙表和上下文找出可能聽錯的字，標在逐字稿裡，你決定要不要改。不會自動改掉原文。")
                .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            Section {
                Text(privacyText)
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .formStyle(.grouped)
        .onAppear(perform: load)
        .onChange(of: storedProvider) { _, _ in load() }
        // 在這頁登入、重新登入之後取公司的模型
        .onChange(of: EnterpriseServer.shared.account) { _, account in
            guard current == .enterprise, account != nil else { return }
            Task {
                let models = await EnterpriseAPI.models()
                if current == .enterprise { companyModels = models }
            }
        }
        #if DEBUG
        .onChange(of: model.debugSettingsAction) { _, action in
            guard let action else { return }
            model.debugSettingsAction = nil
            if action.first == "pickmodel" { openModelPicker() }
            if action.first == "savekey", action.count > 1 { key = action[1]; saveKey() }
        }
        #endif
    }

    private func saveKey() {
        do {
            try Keychain.set(current.keychainAccount, key)
            key = ""
            keyError = nil
            // 剛存的 key 已經記在記憶體，取模型清單不會再問鑰匙圈
            refreshModels()
        } catch {
            keyError = error.localizedDescription
        }
        keySaved = Keychain.exists(current.keychainAccount)
        model.refreshEnvironment()
        // 存好 key 就測一次（R45.3）
        if keyError == nil && AppSettings.summarizerReady { Task { await test() } }
    }

    private func openModelPicker() {
        pickingModel = true
        if availableModels.isEmpty && !loadingModels { refreshModels() }
    }

    private func test() async {
        testing = true
        testResult = nil
        let tested = current
        let result: (Bool, String)
        do {
            let seconds = try await SummarizerTest.run(provider: tested, model: modelName)
            result = (true, String(localized: "可以使用（\(seconds) 秒）"))
        } catch {
            result = (false, error.localizedDescription)
        }
        // 測試期間換了整理工具：結果是舊工具的，不顯示（換工具時 load() 已經重設狀態）
        guard tested == current else { return }
        testResult = result
        testing = false
    }

    /// 用桌面版附的 codex 時是那個 app 的名稱（R55.3）
    private var desktopApp: String? { current == .codex ? CLILogin.desktopApp(.codex) : nil }

    /// 只有 Claude 桌面版：說明怎麼裝 Claude Code（R56.2、R56.3）；已經在用別的整理工具的人不顯示
    private var showClaudeHint: Bool {
        _ = recheckCount   // 按了「重新檢查」要重新算
        return (current == .claude || AppSettings.needsProviderSetup) && ClaudeCodeInstallHint.applies
    }

    /// 「重新檢查」：剛裝好、剛移除的 CLI 和桌面版馬上看得到，再查一次登入
    private func recheck() {
        DesktopApps.clearCache()
        recheckCount += 1
        model.refreshSummarizerProblem()
        Task { await checkLogin(userInitiated: true) }
    }

    /// userInitiated：使用者按了「重新檢查」，查到已登入就測一次（R45.3）；打開設定頁時的自動檢查不測（不花額度）
    private func checkLogin(userInitiated: Bool = false) async {
        guard current == .claude || current == .codex else { login = nil; return }
        checkingLogin = true
        let checked = current
        let status = await CLILogin.refresh(checked)
        guard checked == current else { return }   // 查的時候換了整理工具
        login = status
        checkingLogin = false
        if userInitiated && status?.loggedIn == true { await test() }
    }

    /// 選單下面的紅字（design §34.3）：沒安裝、沒登入在「狀態」那一列已經寫了原因
    private var problemNotice: String? {
        switch model.summarizerProblem {
        case nil, .notChosen?, .off?: return nil
        case .notInstalled?, .notLoggedIn?: return String(localized: "設定好之前不會自動整理。")
        case .missingKey?: return String(localized: "還沒有存 API key：貼上之後按「儲存 key」。設定好之前不會自動整理。")
        case .incomplete?: return String(localized: "還沒有填網址或模型。設定好之前不會自動整理。")
        case .enterpriseSignedOut?: return String(localized: "公司的伺服器還沒登入或登入已失效：到「企業伺服器」那一頁登入。設定好之前不會自動整理。")
        case .enterpriseNoModels?: return String(localized: "公司的伺服器還沒有提供整理用的模型：請管理者在伺服器後台的「整理模型」加上模型。")
        case .paused(_, let detail)?:
            let reason = MeetingLanguage.uiStored(detail.trimmingCharacters(in: CharacterSet(charactersIn: "。. ")))   // l10n-skip
            return String(localized: "上次整理失敗：\(reason)。改好設定，或按「測試整理工具」成功之後，才會再自動整理。")
        }
    }

    private func load() {
        testResult = nil
        testing = false
        login = nil
        Task { await checkLogin() }
        modelName = UserDefaults.standard.string(forKey: current.modelKey) ?? ""
        companyModels = nil
        Task { await refreshCompany() }
        baseURL = UserDefaults.standard.string(forKey: OpenAICompatibleAPI.baseURLKey) ?? ""
        // 只看有沒有存，不讀出 key：打開設定頁不能跳出鑰匙圈的確認（R18.7、R21.1）
        keySaved = current.usesAPIKey && Keychain.exists(current.keychainAccount)
        keyError = nil
        availableModels = []
        modelListError = nil
        loadingModels = false
    }

    /// 選單上的「公司（範例公司）」
    private var companyLabel: String {
        let name = EnterpriseServer.shared.account?.serverName ?? ""
        return name.isEmpty ? LLMProvider.enterprise.label : String(localized: "公司（\(name)）")
    }

    /// 重新讀伺服器的資訊（R51.5）：公司後來才提供整理，選單就多「公司」；選的是公司時再取模型
    private func refreshCompany() async {
        guard EnterpriseServer.shared.isSignedIn else { return }
        await EnterpriseServer.shared.refreshInfo()
        guard current == .enterprise else { return }
        let models = await EnterpriseAPI.models()
        guard current == .enterprise else { return }
        companyModels = models
    }

    /// 公司提供的模型（R51.1、design §36.3）：「公司的預設」存空字串；選過的不在清單裡了就當成預設，設定不清掉
    @ViewBuilder private var companyModelRow: some View {
        if let models = companyModels, !models.isEmpty {
            let fallback = models.first { $0.isDefault } ?? models[0]
            if models.count == 1 {
                LabeledContent("模型") { Text(fallback.name) }
            } else {
                Picker("模型", selection: Binding(
                    get: { models.contains { $0.id == modelName } ? modelName : "" },
                    set: { modelName = $0; UserDefaults.standard.set($0, forKey: current.modelKey) })) {
                    Text("公司的預設（\(fallback.name)）").tag("")
                    ForEach(models) { item in Text(item.name).tag(item.id) }
                }
            }
        } else {
            LabeledContent("模型") {
                Text(companyModels == nil ? "正在取得公司的模型…" : "公司的預設").foregroundStyle(.secondary)
            }
        }
        Text("模型和 API key 由公司在伺服器的後台設定。").font(.caption).foregroundStyle(.secondary)
    }

    /// 可以從清單選模型：API 要有存 key，OpenAI 相容 API 要有網址
    private var canListModels: Bool {
        current == .openaiCompatible ? OpenAICompatibleAPI.normalized(baseURL) != nil
            : current == .enterprise ? EnterpriseServer.shared.isSignedIn : current.usesAPIKey && keySaved
    }

    /// 「選擇」用的模型清單；取的時候換了整理工具就不用。
    /// 先讀 key：macOS 沒允許就顯示原因，不再呼叫 listModels（不然會再問一次，design §22.1）
    private func refreshModels() {
        let checked = current
        loadingModels = true
        modelListError = nil
        Task {
            let result = await Task.detached { () -> Result<[String], Error> in
                do {
                    _ = try Keychain.read(checked.keychainAccount)
                    return .success(await LLMClient.listModels(checked))
                } catch {
                    return .failure(error)
                }
            }.value
            guard checked == current else { return }
            loadingModels = false
            switch result {
            case .success(let models): availableModels = models
            case .failure(let error): modelListError = error.localizedDescription
            }
        }
    }

    private var modelList: some View {
        Group {
            if loadingModels {
                HStack(spacing: 8) {
                    ProgressView().controlSize(.small)
                    Text("正在取得模型清單…").foregroundStyle(.secondary)
                }
                .padding(14)
            } else if availableModels.isEmpty {
                Text(modelListError ?? String(localized: "取得不到模型清單，可以直接輸入模型名稱。"))
                    .foregroundStyle(modelListError == nil ? Color.secondary : Color.red)
                    .fixedSize(horizontal: false, vertical: true)
                    .frame(width: 300, alignment: .leading)
                    .padding(14)
            } else {
                ScrollView {
                    VStack(alignment: .leading, spacing: 0) {
                        ForEach(availableModels, id: \.self) { name in
                            Button {
                                modelName = name
                                pickingModel = false
                            } label: {
                                Text(name)
                                    .frame(maxWidth: .infinity, alignment: .leading)
                                    .padding(.horizontal, 12)
                                    .padding(.vertical, 4)
                                    .contentShape(Rectangle())
                            }
                            .buttonStyle(.plain)
                            .background(name == modelName ? Color.celadon.opacity(0.15) : .clear, in: RoundedRectangle(cornerRadius: 5))
                        }
                    }
                    .padding(6)
                }
                .frame(width: 300, height: min(320, CGFloat(availableModels.count) * 24 + 16))
            }
        }
    }

    private var privacyText: String {
        switch current {
        case .none:
            return provider.isEmpty
                ? String(localized: "還沒設定整理工具：錄音、轉錄、逐字稿全部留在這台 Mac。選好整理工具之後，才會整理成會議紀錄。")
                : String(localized: "目前不整理：錄音、轉錄、逐字稿全部留在這台 Mac。")
        case .claude, .anthropic:
            return String(localized: "整理、提問、猜說話者時會把逐字稿文字、你的筆記、詞彙表和會議基本資料送到 Anthropic（Claude）。錄音檔不會送出。")
        case .codex, .openai:
            return String(localized: "整理、提問、猜說話者時會把逐字稿文字、你的筆記、詞彙表和會議基本資料送到 OpenAI。錄音檔不會送出。")
        case .gemini:
            return String(localized: "整理、提問、猜說話者時會把逐字稿文字、你的筆記、詞彙表和會議基本資料送到 Google（Gemini）。錄音檔不會送出。")
        case .enterprise:
            // 送到公司的伺服器，再由公司設定的模型整理（R51.4、design §36.5）
            let host = EnterpriseServer.shared.host
            return host.isEmpty
                ? String(localized: "整理、提問、猜說話者時會把逐字稿文字、你的筆記、詞彙表和會議基本資料送到公司的伺服器，再由公司設定的模型整理。錄音檔不會因為整理而送出。")
                : String(localized: "整理、提問、猜說話者時會把逐字稿文字、你的筆記、詞彙表和會議基本資料送到 \(host)（公司的伺服器），再由公司設定的模型整理。錄音檔不會因為整理而送出。")
        case .openaiCompatible:
            // 說清楚送到哪一台主機；本機的服務不會離開電腦（design §19.3、R17.5）
            guard let url = OpenAICompatibleAPI.normalized(baseURL), let host = url.host(percentEncoded: false) else {
                return String(localized: "整理、提問、猜說話者時會把逐字稿文字、你的筆記、詞彙表和會議基本資料送到你填的網址。錄音檔不會送出。")
            }
            if OpenAICompatibleAPI.isLocal(url) {
                return String(localized: "整理、提問、猜說話者時會把逐字稿文字、你的筆記、詞彙表和會議基本資料送到這台 Mac 上的服務（\(host)），不會離開電腦。錄音檔不會送出。")
            }
            return url.scheme?.lowercased() == "http"
                ? String(localized: "整理、提問、猜說話者時會把逐字稿文字、你的筆記、詞彙表和會議基本資料送到 \(host)。錄音檔不會送出。用 http 傳送沒有加密，建議改用 https。")
                : String(localized: "整理、提問、猜說話者時會把逐字稿文字、你的筆記、詞彙表和會議基本資料送到 \(host)。錄音檔不會送出。")
        }
    }
}

// MARK: 範本（R6.2）

struct TemplateSettings: View {
    @Environment(AppModel.self) private var model
    @AppStorage(AppSettings.defaultTemplateKey) private var defaultTemplate = "general"
    @State private var selection: String?
    @State private var draft: TemplateDefinition?
    @State private var draftName = ""

    var body: some View {
        HSplitView {
            VStack(alignment: .leading, spacing: 8) {
                List(selection: $selection) {
                    ForEach(model.templates) { template in
                        HStack {
                            Text(template.displayName)
                            Spacer()
                            if template.id == defaultTemplate { Text("預設").font(.caption).foregroundStyle(.secondary) }
                        }
                        .tag(template.id)
                        // 右鍵（R100）：複製、設為預設、刪除（自訂的才有）
                        .contextMenu {
                            Button("複製") { create(from: template) }
                            Button("設為預設範本") { defaultTemplate = template.id }.disabled(defaultTemplate == template.id)
                            if !template.builtin {
                                Divider()
                                Button("刪除", role: .destructive) {
                                    if defaultTemplate == template.id { defaultTemplate = "general" }
                                    model.deleteTemplate(template.id)
                                    if selection == template.id { selection = model.templates.first?.id }
                                }
                            }
                        }
                    }
                }
                HStack {
                    Button("新增", systemImage: "plus") { create(from: nil) }
                    Button("複製", systemImage: "plus.square.on.square") {
                        // 複製正在編輯的內容（包括還沒存的修改）
                        create(from: model.templates.first { $0.id == selection }, definition: draft)
                    }
                    .disabled(selection == nil)
                }
                .buttonStyle(.borderless)
                .padding(8)
            }
            .frame(minWidth: 170, maxWidth: 200)
            editor
                .frame(minWidth: 380)
        }
        // 換範本或關掉設定時，沒按「儲存」的修改也存起來，不要無聲丟掉
        .onChange(of: selection) { old, id in
            saveIfChanged(old)
            load(id)
        }
        .onAppear { if selection == nil { selection = model.templates.first?.id } }
        .onDisappear { saveIfChanged(selection) }
    }

    @ViewBuilder
    private var editor: some View {
        if let template = model.templates.first(where: { $0.id == selection }), let shown = draft {
            // 不用 Binding($draft)：點清單的空白處清掉選取時 draft 先變 nil，還在畫面上的多行輸入框更新綁定時
            // 會強制解開而當掉（2026-11-25 驗 design §86 時發現）
            let binding = Binding<TemplateDefinition>(get: { draft ?? shown }, set: { draft = $0 })
            // 欄位的名稱寫在框的上面、字靠左（design §86、R112.2）：不用 Form 預設的「左邊標籤、字靠右」
            Form {
                // 內建範本不能改：說明放最上面、給一顆複製的按鈕（R112.1；原本在最下面，要捲才看得到）
                if template.builtin {
                    Section {
                        HStack(alignment: .center, spacing: 10) {
                            Image(systemName: "lock").foregroundStyle(.secondary).accessibilityHidden(true)
                            Text("內建範本不能修改。按「複製一份來改」，就能改段落、加自訂段落。")
                                .foregroundStyle(.secondary)
                                .fixedSize(horizontal: false, vertical: true)
                            Spacer(minLength: 8)
                            Button("複製一份來改") { create(from: template, definition: draft) }
                                .buttonStyle(SheetPillButtonStyle(.secondary, compact: true))
                        }
                    }
                }
                Section {
                    VStack(alignment: .leading, spacing: 6) {
                        SheetFieldLabel(String(localized: "名稱"))
                        // 分組的 Form 裡單行輸入框的字預設靠右
                        TextField("", text: $draftName, prompt: Text("範本名稱")).textFieldStyle(.sheetCompact)
                            .labelsHidden().multilineTextAlignment(.leading)
                    }
                    VStack(alignment: .leading, spacing: 6) {
                        SheetFieldLabel(String(localized: "用途說明"))
                        SheetTextEditor(placeholder: String(localized: "例如：週會、專案會議用"), text: binding.description, height: 54)
                    }
                }
                .disabled(template.builtin)
                Section {
                    ForEach(TemplateDefinition.builtinSections, id: \.id) { section in
                        Toggle(section.label, isOn: Binding(
                            get: { binding.wrappedValue.sections.contains(section.id) },
                            set: { on in
                                if on { binding.wrappedValue.sections.append(section.id) }
                                else { binding.wrappedValue.sections.removeAll { $0 == section.id } }
                            }))
                    }
                } header: {
                    Text("要產生的段落")
                } footer: {
                    Text("關掉「議題詳細」時，紀錄裡沒有議題：播放條上沒有議題帶，心智圖改用摘要。")
                        .font(.caption).foregroundStyle(.secondary)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
                .disabled(template.builtin)
                Section {
                    if binding.wrappedValue.custom.isEmpty {
                        Text("還沒有自訂段落。").foregroundStyle(.secondary)
                    }
                    ForEach(binding.wrappedValue.custom.indices, id: \.self) { i in
                        let custom = binding.custom.safe(i, fallback: .init(title: "", instruction: ""))
                        VStack(alignment: .leading, spacing: 6) {
                            HStack {
                                SheetFieldLabel(String(localized: "段落名稱"))
                                Spacer()
                                Button { binding.custom.remove(i) } label: { Image(systemName: "trash") }
                                    .buttonStyle(.borderless)
                                    .foregroundStyle(.secondary)
                                    .help("移除這個段落")
                                    .accessibilityLabel("移除這個段落")
                            }
                            TextField("", text: custom.title, prompt: Text("例如：風險")).textFieldStyle(.sheetCompact)
                                .labelsHidden().multilineTextAlignment(.leading)
                            SheetFieldLabel(String(localized: "要寫什麼")).padding(.top, 4)
                            SheetTextEditor(placeholder: String(localized: "例如：列出討論中提到的風險、負責人和期限。可以分行，用 - 條列。"),
                                            text: custom.instruction)
                        }
                        .padding(.vertical, 4)
                    }
                    Button("新增自訂段落", systemImage: "plus") { binding.wrappedValue.custom.append(.init(title: "", instruction: "")) }
                } header: {
                    Text("自訂段落")
                } footer: {
                    Text("每個自訂段落是會議紀錄裡的一節，整理工具照「要寫什麼」寫成條列。")
                        .font(.caption).foregroundStyle(.secondary)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
                .disabled(template.builtin)
                Section("額外指示（選填）") {
                    SheetTextEditor(placeholder: String(localized: "給整理工具的其他要求，例如：金額、數字照原文寫。可以分行。"), text: binding.instruction)
                }
                .disabled(template.builtin)
                HStack {
                    Spacer()
                    Button("設為預設範本") { defaultTemplate = template.id }.disabled(defaultTemplate == template.id)
                    if !template.builtin {
                        Button("刪除", role: .destructive) {
                            // 刪掉的是預設範本的話，預設改回一般會議，不然新會議會指到不存在的範本
                            if defaultTemplate == template.id { defaultTemplate = "general" }
                            model.deleteTemplate(template.id)
                            selection = model.templates.first?.id
                        }
                        Button("儲存") {
                            model.saveTemplate(TemplateRecord(id: template.id, name: draftName.isEmpty ? String(localized: "未命名範本") : draftName,
                                                              builtin: false, definition: binding.wrappedValue.jsonString()))
                        }
                        .buttonStyle(.borderedProminent)
                    }
                }
            }
            .formStyle(.grouped)
        } else {
            Text("選一個範本").foregroundStyle(.secondary).frame(maxWidth: .infinity, maxHeight: .infinity)
        }
    }

    private func load(_ id: String?) {
        guard let template = model.templates.first(where: { $0.id == id }) else { draft = nil; return }
        draft = template.definition(for: .interface)
        // 內建範本不能改、不會存檔：簡體介面顯示簡體（design §48.2）
        if template.builtin, MeetingLanguage.simplifiedInterface { draft = draft?.mappingText(ChineseScript.toSimplifiedUI) }
        draftName = template.displayName
    }

    /// 自訂範本有改過就存檔
    private func saveIfChanged(_ id: String?) {
        guard let template = model.templates.first(where: { $0.id == id }), !template.builtin, let draft else { return }
        let name = draftName.isEmpty ? String(localized: "未命名範本") : draftName
        guard draft != template.definition(for: .interface) || name != template.displayName else { return }
        model.saveTemplate(TemplateRecord(id: template.id, name: name, builtin: false, definition: draft.jsonString()))
    }

    private func create(from source: TemplateRecord?, definition edited: TemplateDefinition? = nil) {
        let id = "custom-\(UUID().uuidString.prefix(8))"
        // 沒給編輯中的內容（右鍵的「複製」）時，複製畫面上看到的：簡體介面的內建範本轉成簡體（同 select，Windows 巡檢時發現）
        let shown = source.map { template -> TemplateDefinition in
            let definition = template.definition(for: .interface)
            return template.builtin && MeetingLanguage.simplifiedInterface ? definition.mappingText(ChineseScript.toSimplifiedUI) : definition
        }
        let definition = edited ?? shown
            // 新範本預設有議題（R113.1）：沒有議題時播放條上沒有議題帶
            ?? TemplateDefinition(description: "", sections: ["summary", "decisions", "actionItems", "topics"], custom: [], instruction: "")
        model.saveTemplate(TemplateRecord(id: id, name: source.map { String(localized: "\($0.displayName)（副本）") } ?? String(localized: "新範本"), builtin: false,
                                          definition: definition.jsonString()))
        selection = id
    }
}

// MARK: 說話者、詞彙表（各自一頁，R108）

/// 說話者（design §21.4、R19.3）：記住說話者的聲音，之後的會議自動帶入名字；認識的人可以改名、刪除。
/// 是 Form 的一個 Section 的內容（R44.5）：認識的人一人一列，不用 List
struct SpeakerSettings: View {
    @Environment(AppModel.self) private var model
    @AppStorage(AppSettings.voiceprintsKey) private var voiceprints = true
    @State private var renaming: Int64?
    @State private var renameText = ""
    @State private var confirmDeleteAll = false
    /// 移除一個人的確認（R100：會丟聲音特徵，要確認；同人物頁）
    @State private var removing: PersonSummary?

    var body: some View {
        Toggle("記住說話者的聲音，之後的會議自動帶入名字", isOn: $voiceprints)
        Text("替說話者取名時，拾音會記下這個人的聲音特徵。之後的會議認出同一個人，就自動帶入名字並標「自動」，不對可以改。聲音特徵只存在這台 Mac 的資料庫，不會上傳，也不會放進匯出的 .shiyin 封包。")
            .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
        MyVoiceRow()
        HStack {
            Text("認識的人").font(.headline)
            Spacer()
            Button("全部刪除…") { confirmDeleteAll = true }
                .disabled(model.people.isEmpty)
        }
        if model.people.isEmpty {
            Text("還沒有認識的人。在逐字稿點「說話者 1」改成名字，拾音就會記住這個人。")
                .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
        }
        ForEach(model.people) { person in
            HStack(spacing: 10) {
                if renaming == person.id {
                    TextField("名字", text: $renameText)
                        .textFieldStyle(.roundedBorder)
                        .onSubmit { model.renamePerson(person.id, to: renameText); renaming = nil }
                    Button("取消") { renaming = nil }.buttonStyle(.link)
                } else {
                    VStack(alignment: .leading, spacing: 2) {
                        // 錄過「我的聲音」的人（design §56.3、R80.4）
                        Text(person.enrolledAt != nil ? String(localized: "\(person.name)（我）") : person.name)
                        Text(detailLine(person)).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                    }
                    Spacer()
                    Button("重新命名") { renameText = person.name; renaming = person.id }.buttonStyle(.link)
                    Button { removing = person } label: { Image(systemName: "trash") }
                        .buttonStyle(.borderless).help("移除「\(person.name)」（會議裡的名字留著）")
                }
            }
            .contextMenu {
                Button("重新命名…") { renameText = person.name; renaming = person.id }
                Divider()
                Button("移除這個人…", role: .destructive) { removing = person }
            }
        }
        .confirmationDialog(String(localized: "移除「\(removing?.name ?? "")」？"),
                            isPresented: Binding(get: { removing != nil }, set: { if !$0 { removing = nil } })) {
            Button("移除", role: .destructive) { if let id = removing?.id { model.deletePerson(id) } }
        } message: {
            Text("會從「人物」拿掉，也會刪掉他的聲音特徵。會議裡已經帶入的名字會留著，之後的會議不會再自動認出他。")
        }
        .onAppear { model.reloadPeople() }
        .confirmationDialog("刪除所有認識的人的聲音特徵？", isPresented: $confirmDeleteAll) {
            Button("全部刪除", role: .destructive) { model.deleteAllPeople() }
        } message: {
            Text("已經帶入的名字會留著，之後的會議不會再自動帶入。")
        }
        // 人物頁（R85.5）
        if !model.people.isEmpty {
            Text("也可以在側欄的「人物」管理").font(.caption).foregroundStyle(.secondary)
        }
    }

    private func detailLine(_ person: PersonSummary) -> String {
        let prints = String(localized: "\(person.voiceprintCount) 份聲音特徵")
        guard let title = person.lastMeetingTitle, let date = person.lastMeetingDate else { return prints }
        return String(localized: "\(prints)・最近：\(date) \(title)")
    }
}

/// 我的聲音（design §56.3、R80）：錄一段自己的聲音，第一場會議就認得自己
struct MyVoiceRow: View {
    @Environment(AppModel.self) private var model
    @AppStorage(AppSettings.voiceprintsKey) private var voiceprints = true
    @State private var showingSheet = false

    var body: some View {
        let me = model.people.first { $0.enrolledAt != nil }
        let recording = model.recorder.isRecording
        HStack(alignment: .firstTextBaseline, spacing: 10) {
            VStack(alignment: .leading, spacing: 2) {
                Text("我的聲音")
                Group {
                    if let me, let at = me.enrolledAt {
                        Text("\(me.name)・已錄（\(String(at.prefix(10)))）")
                    } else if !voiceprints {
                        Text("先打開上面的「記住說話者的聲音」")
                    } else if recording {
                        Text("錄音中不能錄（麥克風在用）")
                    } else {
                        Text("錄一段你自己的聲音，第一場會議就認得你。")
                    }
                }
                .font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            if me != nil {
                Button("重錄…") { showingSheet = true }.disabled(!voiceprints || recording)
                Button("刪除") { model.deleteEnrollment() }
                    .buttonStyle(.link)
                    .help("只刪這份聲音特徵，會議裡的名字照舊")
            } else {
                Button("錄一段我的聲音…") { showingSheet = true }.disabled(!voiceprints || recording)
            }
        }
        .sheet(isPresented: $showingSheet) { VoiceEnrollmentSheet() }
        #if DEBUG
        .onChange(of: model.debugSettingsAction) { _, action in
            if action?.first == "enrollsheet" { model.debugSettingsAction = nil; showingSheet = true }
        }
        #endif
    }
}

/// 錄「我的聲音」的視窗（design §56.3）：名字、要唸的文字、音量、秒數
struct VoiceEnrollmentSheet: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    @State private var enrollment = VoiceEnrollment()
    @State private var name = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("錄一段我的聲音").font(.title3.bold())
            Text("按「開始」，用平常開會說話的音量唸下面這段（約 20～30 秒），唸完按「完成」。拾音只用這段聲音算出你的聲音特徵，算完就刪掉錄音。")
                .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            HStack {
                Text("名字")
                TextField("名字", text: $name).textFieldStyle(.roundedBorder)
            }
            Text(VoiceEnrollment.passage)
                .font(.title3)
                .lineSpacing(6)
                .padding(14)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(Color.secondary.opacity(0.1), in: RoundedRectangle(cornerRadius: 8))
                .fixedSize(horizontal: false, vertical: true)
            HStack(spacing: 10) {
                LevelMeter { [enrollment] in enrollment.level }
                Text(status).font(.callout).monospacedDigit().foregroundStyle(.secondary)
            }
            if enrollment.micSilent {
                Text("麥克風沒有收到聲音：可能被靜音、被其他 app 佔用，或選錯了麥克風。")
                    .font(.callout).foregroundStyle(Color.signal).fixedSize(horizontal: false, vertical: true)
            }
            if case .failed(let message) = enrollment.phase {
                Text(message).font(.callout).foregroundStyle(Color.signal).fixedSize(horizontal: false, vertical: true)
            }
            HStack {
                Spacer()
                Button("取消") { enrollment.cancel(); dismiss() }
                    .keyboardShortcut(.cancelAction)
                switch enrollment.phase {
                case .recording:
                    Button("完成") { save() }
                        .keyboardShortcut(.defaultAction)
                        .disabled(!enrollment.canFinish || name.trimmingCharacters(in: .whitespaces).isEmpty)
                case .computing:
                    ProgressView().controlSize(.small)
                case .idle, .failed:
                    Button(enrollment.seconds > 0 ? "重新開始" : "開始") { enrollment.start() }
                        .keyboardShortcut(.defaultAction)
                }
            }
        }
        .padding(20)
        .frame(width: 520)
        .onAppear { name = model.enrollmentDefaultName }
        .onDisappear { enrollment.cancel() }
    }

    private var status: String {
        switch enrollment.phase {
        case .recording:
            return String(localized: "已錄 \(Int(enrollment.seconds)) 秒・有聲音的部分 \(Int(enrollment.voicedSeconds)) 秒（要 10 秒以上）")
        case .computing:
            return String(localized: "正在算聲音特徵…第一次要先下載說話者辨識的模型")
        default:
            return ""
        }
    }

    private func save() {
        Task {
            if await enrollment.finish(name: name, model: model) { dismiss() }
        }
    }
}

/// 詞彙表（沿用第二版）。是 Form 的一個 Section 的內容（R44.5）：詞彙一條一列，不用 List
struct GlossarySettings: View {
    @Environment(AppModel.self) private var model
    @State private var newTerm = ""
    /// 搜尋（R94.2）、勾選（R94.3）
    @State private var query = ""
    @State private var selected: Set<Int64> = []
    @State private var confirmDelete = false
    /// 就地改一個詞（R100）：雙擊、右鍵「重新命名…」
    @State private var editingTerm: Int64?
    @State private var termText = ""
    @FocusState private var termFocused: Bool

    /// 勾選裡還在詞彙表的（勾了之後被單獨刪掉的不算）
    private var checked: Set<Int64> { selected.intersection(model.glossary.compactMap(\.id)) }

    /// 目前列出來的詞（搜尋時只有搜到的）
    private var shown: [GlossaryTerm] {
        let q = query.trimmingCharacters(in: .whitespaces)
        return q.isEmpty ? model.glossary : model.glossary.filter { $0.term.range(of: q, options: .caseInsensitive) != nil }
    }

    var body: some View {
        Text("系統名、產品名、人名的正確寫法。轉錄時會當成提示（最多約 150 字），整理時也會參考。實驗顯示，有沒有詞彙提示，專有名詞的正確率差很多。")
            .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
        // 一次貼上好幾個也可以（R94.1）：換行、逗號、頓號、分號分開
        HStack(alignment: .top) {
            TextField("新增詞彙", text: $newTerm, prompt: Text("產品名、人名；可以一次貼上好幾個"), axis: .vertical)
                .lineLimit(1...4)
                .textFieldStyle(.roundedBorder)
                .onSubmit(add)
            Button("新增", action: add).disabled(newTerm.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
        }
        // 詞彙表建議（design §17.5）：最多 12 個
        if !model.glossarySuggestions.isEmpty {
            VStack(alignment: .leading, spacing: 6) {
                Text("建議加入：從參加者、說話者名字、校正過的字、會議標題找到的").font(.caption).foregroundStyle(.secondary)
                ForEach(model.glossarySuggestions) { suggestion in
                    HStack(spacing: 8) {
                        Text(suggestion.term)
                        Text(suggestion.sourceLabel).font(.caption).foregroundStyle(.secondary)
                        Spacer()
                        Button("忽略") { model.dismissGlossarySuggestion(suggestion.term) }.buttonStyle(.link)
                        Button("加入") { model.addTerm(suggestion.term) }.buttonStyle(.link)
                    }
                }
            }
            .padding(10)
            .background(Color.signal.opacity(0.08), in: RoundedRectangle(cornerRadius: 8))
        }
        if model.glossary.isEmpty {
            Text("還沒有詞彙。用詞修正時勾選「加進詞彙表」也會加到這裡。").font(.callout).foregroundStyle(.secondary)
        }
        // 搜尋（R94.2）：超過 10 個才出現
        if model.glossary.count > 10 {
            TextField("搜尋詞彙", text: $query, prompt: Text("輸入要找的字")).textFieldStyle(.roundedBorder)
        }
        // 全選、刪除勾選的（R94.3）
        if !shown.isEmpty {
            HStack {
                let ids = Set(shown.compactMap(\.id))
                if ids.isSubset(of: selected) {
                    Button("取消勾選") { selected.subtract(ids) }.buttonStyle(.link)
                } else {
                    Button(query.isEmpty ? "全選" : "全選搜到的 \(ids.count) 個") { selected.formUnion(ids) }.buttonStyle(.link)
                }
                Spacer()
                if !checked.isEmpty {
                    Button("刪除勾選的 \(checked.count) 個…", role: .destructive) { confirmDelete = true }
                }
            }
            .font(.callout)
        }
        ForEach(shown) { term in
            HStack {
                if editingTerm == term.id {
                    TextField("詞彙", text: $termText)
                        .textFieldStyle(.roundedBorder)
                        .focused($termFocused)
                        .onSubmit { saveTerm(term) }
                        .onExitCommand { editingTerm = nil }
                    Button("取消") { editingTerm = nil }.buttonStyle(.link)
                    Button("儲存") { saveTerm(term) }.buttonStyle(.link)
                } else {
                    Toggle(isOn: Binding(get: { selected.contains(term.id ?? -1) },
                                         set: { if let id = term.id { if $0 { selected.insert(id) } else { selected.remove(id) } } })) {
                        Text(term.term)
                            .onTapGesture(count: 2) { startEditing(term) }
                            .help("雙擊可以修改")
                    }
                    .toggleStyle(.checkbox)
                    Spacer()
                    Button { startEditing(term) } label: { Image(systemName: "pencil") }
                        .buttonStyle(.borderless).help("修改「\(term.term)」")
                    Button { model.deleteTerm(term.id!) } label: { Image(systemName: "trash") }
                        .buttonStyle(.borderless).help("刪除「\(term.term)」")
                }
            }
            .contextMenu {
                Button("重新命名…") { startEditing(term) }
                Divider()
                DestructiveMenuButton(String(localized: "刪除")) { model.deleteTerm(term.id!) }
            }
        }
        if shown.isEmpty && !model.glossary.isEmpty {
            Text("沒有含「\(query)」的詞彙").font(.callout).foregroundStyle(.secondary)
        }
        // 公司詞彙表（R53.3）：唯讀，接在自己的詞後面
        if !model.companyGlossary.isEmpty {
            DisclosureGroup {
                Text(model.companyGlossary.joined(separator: MeetingLanguage.ui(zh: "、", en: ", ")))
                    .textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
            } label: {
                VStack(alignment: .leading, spacing: 2) {
                    Text("公司的詞彙（\(model.companyGlossary.count) 個）")
                    Text("由\(EnterpriseServer.shared.account?.serverName ?? String(localized: "公司"))的管理者維護，接在你的詞後面一起用。")
                        .font(.caption).foregroundStyle(.secondary)
                }
            }
        }
        Text(String(localized: "目前的轉錄提示：\(WhisperEngine.prompt(glossary: CompanyGlossary.merge(model.glossary.map(\.term), model.companyGlossary), language: AppSettings.defaultMeetingLanguage))"))
            .font(.caption).foregroundStyle(.tertiary).textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
            // 打開這頁時更新公司詞彙表（R53.1）
            .task { await EnterpriseServer.shared.refreshGlossary() }
            .confirmationDialog("刪除勾選的 \(checked.count) 個詞彙？", isPresented: $confirmDelete) {
                Button("刪除", role: .destructive) {
                    model.deleteTerms(Array(checked))
                    selected = []
                }
            }
    }

    private func add() {
        model.addTerms(newTerm)
        newTerm = ""
    }

    private func startEditing(_ term: GlossaryTerm) {
        termText = term.term
        editingTerm = term.id
        termFocused = true
    }

    /// 改成已經有的詞時合併（R100）
    private func saveTerm(_ term: GlossaryTerm) {
        let text = termText.trimmingCharacters(in: .whitespacesAndNewlines)
        editingTerm = nil
        guard let id = term.id, !text.isEmpty, text != term.term else { return }
        model.renameTerm(id, to: text)
    }
}

// MARK: 錄音與轉錄（R1、R4、R10.3；R44.5 分成四組）

struct RecordingSettings: View {
    /// 錄音一頁（錄音來源、提醒與快捷鍵）、轉錄一頁（轉錄、語言），同一份狀態（R108）
    enum Part { case recording, transcription }
    let part: Part
    @Environment(AppModel.self) private var model
    @AppStorage(AppSettings.transcriptionEngineKey) private var engine = "breeze"

    /// 正式轉錄引擎的說明；Breeze、Qwen3-ASR 後面接不跟著這個設定的語言
    private var engineDescription: String {
        switch engine {
        case "server":
            return String(localized: "錄音會送到 \(EnterpriseServer.shared.host)（公司的伺服器）轉錄，轉完就刪；說話者辨識和聲紋在這台 Mac 上做，聲紋不會送出。會參考詞彙表。連不上時會等，也可以改在這台 Mac 轉錄。")
        case "system":
            return String(localized: "不用下載模型，速度快很多；但不參考詞彙表，英文專有名詞、人名比較容易錯。")
        case "qwen":
            return String(localized: "會參考詞彙表，多人會議的漏字和同音字比 Breeze 少、速度快三成；阿里巴巴開源的模型，全程在這台 Mac 上跑、不連網，輸出會轉成台灣繁體；需要下載約 2.5 GB 的模型。")
                + Self.otherEngines
        default:
            return String(localized: "會參考詞彙表，中英夾雜、專有名詞比較準；聯發科開源的台灣模型，需要下載約 1.7 GB。") + Self.otherEngines
        }
    }

    /// 不跟著這個設定的語言（design §41.3）：「日文和韓文會議改用 Whisper large-v3-turbo（574 MB）。廣東話會議一律用 Qwen3-ASR。」
    static var otherEngines: String {
        let space = MeetingLanguage.ui(zh: "", en: " ")
        var text = space + String(localized: "\(MeetingLanguage.uiNames(model: "turbo"))會議改用 Whisper large-v3-turbo（574 MB）。")
        let qwen = MeetingLanguage.uiNames(model: "qwen")
        if !qwen.isEmpty { text += space + String(localized: "\(qwen)會議一律用 Qwen3-ASR。") }
        return text
    }
    @AppStorage(AppSettings.defaultMeetingLanguageKey) private var meetingLanguage: MeetingLanguage = .systemDefault
    @AppStorage(AppSettings.recordSourceKey) private var source = "mic"
    @AppStorage(AppSettings.inputDeviceKey) private var device = ""
    @AppStorage(AppSettings.liveTranscriptionKey) private var live = true
    @AppStorage(AppSettings.autoDetectLanguageKey) private var autoDetect = true
    @AppStorage(AppSettings.chineseScriptKey) private var chineseScript = AppSettings.defaultChineseScript
    @AppStorage(AppSettings.diarizeKey) private var diarize = true
    @AppStorage(AppSettings.diarizationModelKey) private var diarizationModel = Diarizer.Model.standard.rawValue
    @AppStorage(AppSettings.globalHotKeyKey) private var hotKey = true
    @AppStorage(AppSettings.meetingDetectionKey) private var meetingDetection = true
    @AppStorage(AppSettings.autoStopOnMeetingEndKey) private var autoStopOnMeetingEnd = true
    @AppStorage(AppSettings.recordingIslandKey) private var recordingIsland = true
    @AppStorage(AppSettings.idleStopKey) private var idleStop = true
    @AppStorage(AppSettings.bookmarkHotKeyKey) private var bookmarkHotKey = true
    @AppStorage(AppSettings.screenshotHotKeyKey) private var screenshotHotKey = true
    @State private var mutedApps: [String] = []
    @State private var hotKeyFailed = false

    var body: some View {
        Form {
            if part == .recording { recordingSections } else { transcriptionSections }
        }
        .formStyle(.grouped)
        .onAppear {
            hotKeyFailed = hotKey && !GlobalHotKey.shared.isRegistered
            mutedApps = MeetingDetector.muted
        }
    }

    @ViewBuilder
    private var recordingSections: some View {
        Section("錄音來源") {
            Picker("錄音來源", selection: $source) {
                Text("只錄麥克風").tag("mic")
                Text("麥克風＋電腦聲音（線上會議）").tag("both")
                Text("只錄電腦聲音").tag("system")
            }
            Picker("麥克風", selection: $device) {
                MicrophoneOptions(saved: device)
            }
            .disabled(source == "system")
            // R58.6：其他換麥克風的地方、藍牙耳機會把系統預設換掉
            Text("錄音按鈕旁的箭頭、錄音畫面也可以換麥克風。藍牙耳機連著時，系統預設可能會自動變成耳機的麥克風，現場會議請選這台 Mac 的麥克風。")
                .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            Text("錄線上會議時選「麥克風＋電腦聲音」：你的聲音和對方的聲音會分別錄在左右聲道。第一次錄電腦聲音時，macOS 會詢問「錄製系統音訊」的權限。")
                .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            HStack {
                Button("麥克風權限設定") { Recorder.openPrivacySettings() }
                Button("系統錄音權限設定") { Recorder.openPrivacySettings("Privacy_AudioCapture") }
            }
        }
        Section("提醒與快捷鍵") {
            // 偵測線上會議（design §21.7、R19.4）
            Toggle("偵測到線上會議時提醒錄音", isOn: $meetingDetection)
            // 會議結束時倒數自動停止（design §69、R95）
            Toggle("會議結束時自動停止錄音", isOn: $autoStopOnMeetingEnd)
                .disabled(!meetingDetection)
            // 錄音島（design §57、R81.8）
            Toggle("在螢幕上方顯示錄音島", isOn: $recordingIsland)
            Text("錄音中在螢幕上方正中間（有瀏海的 Mac 接在瀏海兩側）顯示錄音的時間和提醒，全螢幕開會、分享畫面時也看得到，分享畫面拍不到它。偵測到線上會議時的「要開始錄音嗎？」也在這裡。")
                .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            Text("Zoom、Teams、Webex、Google Meet（瀏覽器）等開始用麥克風、拾音沒在錄音時提醒你，不會自動開始錄音。會議 app 停止用麥克風時倒數 60 秒，沒按「繼續錄」就停止並轉錄；關掉「自動停止」時只問要不要停止。")
                .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            ForEach(mutedApps, id: \.self) { bundle in
                HStack {
                    Text("不提醒：\(MeetingDetector.displayName(bundle))").font(.callout)
                    Spacer()
                    Button("恢復提醒") {
                        MeetingDetector.unmute(bundle)
                        mutedApps = MeetingDetector.muted
                    }
                    .buttonStyle(.link)
                }
            }
            // 忘了停止錄音（design §35、R48.5）
            Toggle("很久沒人說話時提醒，30 分鐘後自動停止", isOn: $idleStop)
            Text("錄音中 15 分鐘沒人說話時提醒你；再 15 分鐘還是沒人說話就自動停止，只轉錄到最後有人說話的地方，錄音檔完整保留。")
                .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            Toggle("全域快捷鍵 \(GlobalHotKey.display)：在其他 app 裡也能開始或停止錄音", isOn: $hotKey)
                .onChange(of: hotKey) { _, enabled in
                    GlobalHotKey.shared.setEnabled(enabled)
                    hotKeyFailed = enabled && !GlobalHotKey.shared.isRegistered
                }
            if hotKeyFailed {
                Text("沒辦法註冊 \(GlobalHotKey.display)，可能已經被其他 app 使用。")
                    .font(.caption).foregroundStyle(.red)
            }
            Toggle("錄音中按 \(GlobalHotKey.bookmarkDisplay) 標記重點（在其他 app 裡也能用）", isOn: $bookmarkHotKey)
                .onChange(of: bookmarkHotKey) { _, enabled in
                    GlobalHotKey.shared.setBookmarkEnabled(enabled && model.recorder.isRecording)
                }
            // 會議中的畫面（R109.1、design §83.2）
            Toggle("錄音中按 \(GlobalHotKey.screenshotDisplay) 截圖（在其他 app 裡也能用）", isOn: $screenshotHotKey)
                .onChange(of: screenshotHotKey) { _, enabled in
                    GlobalHotKey.shared.setScreenshotEnabled(enabled && model.recorder.isRecording)
                }
            if screenshotHotKey && model.recorder.isRecording && !GlobalHotKey.shared.isScreenshotRegistered {
                Text("這組按鍵被別的 app 佔用了，可以用錄音島或錄音畫面上的「截圖」。")
                    .font(.caption).foregroundStyle(.red)
            }
        }
    }

    @ViewBuilder
    private var transcriptionSections: some View {
        Section("轉錄") {
            // 正式轉錄引擎（R4.1、design §6.2.1、D11；Qwen3-ASR 見 §32.6）
            Picker("正式轉錄", selection: $engine) {
                // 企業伺服器（R50.1）：登入過才有
                if let account = EnterpriseServer.shared.account {
                    Text("企業伺服器（\(account.serverName)）").tag("server")
                }
                Text("Breeze ASR 25（聯發科，建議）").tag("breeze")
                Text("Qwen3-ASR 1.7B（阿里巴巴）").tag("qwen")
                Text("macOS 內建").tag("system")
            }
            .onChange(of: engine) { _, _ in
                model.runner.wake()
                model.refreshEnvironment()
            }
            Text(verbatim: engineDescription)
                .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            Toggle("轉錄時辨識說話者", isOn: $diarize)
            // 說話者辨識的模型（design §61.2、R87）
            Picker("說話者辨識的模型", selection: $diarizationModel) {
                Text("標準").tag(Diarizer.Model.standard.rawValue)
                Text("Nemotron-3（實驗性，英文會議比較準）").tag(Diarizer.Model.nemotron.rawValue)
            }
            .disabled(!diarize)
            .onChange(of: diarizationModel) { _, value in
                if value == Diarizer.Model.nemotron.rawValue { model.models.prepareNemotron() }
            }
            if diarizationModel == Diarizer.Model.nemotron.rawValue {
                Text("NVIDIA 的模型，最多分 8 人。英文會議的測試（AMI）中，沒被插話的句子標對 77% → 90%；中文會議會分出比較多人，還在觀察。超過 8 人的會議、模型還沒下載好時用標準的。")
                    .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
            Toggle("錄音中顯示即時逐字稿", isOn: $live)
        }
        Section("語言") {
            // 會議語言（R12.4、design §16.3）
            Picker("預設會議語言", selection: $meetingLanguage) {
                ForEach(MeetingLanguage.allCases) { Text($0.name).tag($0) }
            }
            Text("新的錄音和匯入預設用這個語言轉錄、整理會議紀錄；錄音選單、匯入時和會議頁都可以改。")
                .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            // 中文的字（R71、design §47.3）
            Picker("中文的字", selection: $chineseScript) {
                Text("繁體（台灣）").tag("traditional")
                Text("簡體").tag("simplified")
            }
            Text("只影響中文會議的逐字稿、會議紀錄和匯出，介面文字不變。已經做好的會議不會變，重新轉錄或重新整理後才會換。")
                .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            Toggle("自動偵測會議語言", isOn: $autoDetect)
                .onChange(of: autoDetect) { _, on in if on { model.models.autoStart(.languageDetection) } }
            if autoDetect && !model.models.isInstalled(.languageDetection) {
                // 模型沒下載好（下載失敗、暫停）就不會偵測，要讓人知道
                HStack {
                    Text(model.models.state(.languageDetection).isBusy ? "正在下載語言偵測模型…" : "語言偵測模型還沒下載，下載好之前錄音不會自動偵測")
                        .font(.caption).foregroundStyle(Color.signal)
                    Spacer()
                    if !model.models.state(.languageDetection).isBusy {
                        Button("下載") { model.models.start(.languageDetection) }.controlSize(.small)
                    }
                }
            }
            Text("開始錄音後聽約 10 秒，判斷是哪一種語言（\(MeetingLanguage.uiNames(MeetingLanguage.detectable))）；跟會議語言不同而且有把握時會自動切換，錄音畫面可以改回來。需要 60 MB 的語言偵測模型。")
                .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
        }
    }
}

/// 模型與空間（R108）：模型清單、儲存空間、第二版留下的檔案
struct ModelsSettings: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        Form {
            Section("模型") { ModelRows() }
            Section("儲存空間") { StorageSection() }
            if model.legacyBytes > 0 {
                Section("第二版留下的檔案") {
                    Text("第二版用的 Python 環境和舊模型已經用不到了，佔用 \(ByteCountFormatter.string(fromByteCount: model.legacyBytes, countStyle: .file))。")
                        .fixedSize(horizontal: false, vertical: true)
                    Button("刪除第二版的 Python 環境和舊模型", role: .destructive) { model.deleteLegacyItems() }
                }
            }
        }
        .formStyle(.grouped)
    }
}

/// 模型清單（R10.3；R108 在「模型與空間」一頁）
struct ModelRows: View {
    @Environment(AppModel.self) private var model

    private var transcribing: Bool { model.runner.running[.transcribe] != nil }

    var body: some View {
        ModelRow(title: ModelStore.breeze.name, state: model.models.state,
                 start: { model.models.start() }, pause: { model.models.pause() },
                 delete: { model.models.delete(); model.refreshEnvironment() }, deleteDisabled: transcribing)
        Text("中文、英文會議用，1.66 GB。").font(.caption).foregroundStyle(.secondary)
        ModelRow(title: String(localized: "Qwen3-ASR 1.7B（q8_0，含音訊編碼器）"), state: model.models.combinedState(.qwen),
                 start: { model.models.startAll(.qwen) }, pause: { model.models.pauseAll(.qwen) },
                 delete: { model.models.deleteAll(.qwen); model.refreshEnvironment() }, deleteDisabled: transcribing)
        Text(MeetingLanguage.uiNames(model: "qwen").isEmpty ? String(localized: "正式轉錄選「Qwen3-ASR」時用，2.5 GB。")
             : String(localized: "正式轉錄選「Qwen3-ASR」時用；\(MeetingLanguage.uiNames(model: "qwen"))會議一律用。2.5 GB。"))
            .font(.caption).foregroundStyle(.secondary)
        ModelRow(title: ModelStore.japanese.name, state: model.models.state(.japanese),
                 start: { model.models.start(.japanese) }, pause: { model.models.pause(.japanese) },
                 delete: { model.models.delete(.japanese); model.refreshEnvironment() }, deleteDisabled: transcribing)
        Text("\(MeetingLanguage.uiNames(model: "turbo"))會議用，574 MB。第一次轉錄這些語言的會議時會自動下載。").font(.caption).foregroundStyle(.secondary)
        ModelRow(title: ModelStore.languageDetection.name, state: model.models.state(.languageDetection),
                 start: { model.models.start(.languageDetection) }, pause: { model.models.pause(.languageDetection) },
                 delete: { model.models.delete(.languageDetection); model.refreshEnvironment() }, deleteDisabled: model.recorder.isRecording)
        Text("自動偵測會議語言用，60 MB。").font(.caption).foregroundStyle(.secondary)
        ModelRow(title: String(localized: "說話者辨識模型（FluidAudio）"), state: model.models.diarizerState,
                 start: model.models.prepareDiarizer, pause: nil, delete: nil)
        // Nemotron-3（R87）：選了或下載過才列
        if AppSettings.diarizationModel == .nemotron || model.models.nemotronState != .missing {
            ModelRow(title: String(localized: "說話者辨識模型 Nemotron-3（NVIDIA）"), state: model.models.nemotronState,
                     start: model.models.prepareNemotron, pause: nil,
                     delete: { model.models.deleteNemotron() }, deleteDisabled: transcribing)
            Text("說話者辨識選「Nemotron-3」時用，約 190 MB。").font(.caption).foregroundStyle(.secondary)
        }
    }
}

/// 儲存空間（R9.4；R44.5 從「模型與儲存空間」搬到「進階」）：是 Form 的 Section 的內容
struct StorageSection: View {
    @Environment(AppModel.self) private var model
    @AppStorage(AppSettings.compressAudioKey) private var compress = true
    @State private var audioBytes: Int64 = 0
    @State private var modelBytes: Int64 = 0

    var body: some View {
        LabeledContent(MeetingLanguage.ui(zh: "錄音", en: "Recordings"), value: ByteCountFormatter.string(fromByteCount: audioBytes, countStyle: .file))
        LabeledContent("模型", value: ByteCountFormatter.string(fromByteCount: modelBytes, countStyle: .file))
        Toggle("轉錄完成後把錄音壓縮成 m4a（約每小時 10～30 MB）", isOn: $compress)
        LabeledContent("資料位置") {
            Button(Paths.home.path) { NSWorkspace.shared.activateFileViewerSelecting([Paths.home]) }
                .buttonStyle(.link)
        }
        .onAppear(perform: measure)
    }

    private func measure() {
        audioBytes = AppModel.directorySize(Paths.audioDir)
        modelBytes = AppModel.directorySize(Paths.modelsDir)
    }
}

struct ModelRow: View {
    let title: String
    let state: ModelStore.State
    let start: () -> Void
    let pause: (() -> Void)?
    let delete: (() -> Void)?
    /// 正在轉錄時不能刪：用到一半的模型被刪掉，那場轉錄會失敗
    var deleteDisabled = false
    @State private var confirmDelete = false

    var body: some View {
        HStack(spacing: 12) {
            VStack(alignment: .leading, spacing: 4) {
                Text(title)
                switch state {
                case .downloading(let received, let total):
                    if total > 0 {
                        ProgressView(value: Double(received), total: Double(total))
                        Text("\(ByteCountFormatter.string(fromByteCount: received, countStyle: .file))／\(ByteCountFormatter.string(fromByteCount: total, countStyle: .file))")
                            .font(.caption).monospacedDigit().foregroundStyle(.secondary)
                    } else {
                        ProgressView().controlSize(.small)
                    }
                case .paused(let received, _):
                    Text(received > 0 ? "已暫停（\(ByteCountFormatter.string(fromByteCount: received, countStyle: .file))）" : "已暫停，可以接著下載")
                        .font(.caption).foregroundStyle(.secondary)
                case .verifying: Text("正在驗證檔案…").font(.caption).foregroundStyle(.secondary)
                case .installed: Text("已下載").font(.caption).foregroundStyle(Color.celadon)
                case .missing: Text("還沒下載").font(.caption).foregroundStyle(.secondary)
                case .failed(let message): Text(message).font(.caption).foregroundStyle(.red).fixedSize(horizontal: false, vertical: true)
                }
            }
            Spacer()
            switch state {
            case .downloading: if let pause { Button("暫停", action: pause) }
            case .verifying: EmptyView()
            case .installed:
                if let delete {
                    Button("刪除", role: .destructive) { confirmDelete = true }
                        .disabled(deleteDisabled)
                        .help(deleteDisabled ? "正在轉錄，完成後才能刪除模型" : "")
                        .confirmationDialog("刪除「\(title)」？", isPresented: $confirmDelete) {
                            Button("刪除", role: .destructive, action: delete)
                        } message: {
                            Text("之後要用的時候需要重新下載。")
                        }
                }
            case .paused, .failed: Button("繼續下載", action: start)
            case .missing: Button("下載", action: start).buttonStyle(.borderedProminent)
            }
        }
    }
}

// MARK: 進階：環境檢查

struct AdvancedSettings: View {
    /// 一般一頁（顯示、引導、搜尋索引、匿名統計）、檢查與更新一頁（更新、環境檢查），同一份狀態（R108）
    enum Part { case general, status }
    let part: Part
    @Environment(AppModel.self) private var model
    @AppStorage(AppSettings.textSizeKey) private var textSize = 0
    @AppStorage(AppSettings.hideFromScreenSharingKey) private var hideFromScreenSharing = false
    @AppStorage("hasCompletedOnboarding") private var hasCompletedOnboarding = false
    @AppStorage(Updater.urlKey) private var updateURL = ""
    @State private var showingUsageStats = false
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        Form {
            switch part {
            case .general:
                // 會議內容的文字大小（design §38.5、R59）
                Section("顯示") {
                    Picker("會議內容的文字大小", selection: $textSize) {
                        ForEach(TextSize.levels, id: \.self) { Text(TextSize.label($0)).tag($0) }
                    }
                    Text("紀錄、逐字稿、提問、即時逐字稿和我的筆記的文字。也可以用 ⌘＋、⌘－ 調整，⌘0 回到標準大小。")
                        .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                    // 分享畫面時藏起拾音的視窗（design §53、R77）
                    Toggle("分享畫面時藏起拾音的視窗", isOn: $hideFromScreenSharing)
                    Text("打開後，用 Zoom、Teams、Google Meet 分享整個螢幕時，別人看不到拾音（例如其他會議的紀錄）。截圖、螢幕錄影也拍不到拾音。")
                        .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                }
                Section {
                    Button("重新顯示首次使用引導") {
                        hasCompletedOnboarding = false
                        openWindow(id: "onboarding")
                    }
                    Button("重建搜尋索引") {
                        model.perform { try model.db.rebuildSearchIndex() }
                        model.noticeMessage = String(localized: "搜尋索引已重建")
                    }
                }
                // 匿名使用統計（design §30.5、R40.4）：有統計網址的版本才有。放在最下面、只留一行，開關收在「詳細…」裡
                if UsageStats.isOffered {
                    Section {
                        LabeledContent("匿名使用統計") {
                            Button("詳細…") { showingUsageStats = true }
                        }
                    }
                }
            case .status:
                // app 內更新（R10.6）
                Section("更新") {
                    LabeledContent("目前版本", value: Updater.currentVersion)
                    TextField("更新網址", text: $updateURL,
                              prompt: Text(Bundle.main.object(forInfoDictionaryKey: "ShiyinUpdateURL") as? String ?? "https://…/shiyin-update.json"))
                    Text("發布拾音的人會給你這個網址（shiyin-update.json）。留空就用安裝版本內建的網址；都沒有的話不會檢查更新。")
                        .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                    HStack {
                        Button("檢查更新") { Task { await model.updater.check(userInitiated: true) } }
                            .disabled(model.updater.state == .checking || model.updater.state == .downloading || model.updater.state == .installing)
                        if case .available(let manifest) = model.updater.state {
                            Button("下載並更新到 \(manifest.version)") { Task { await model.updater.install(manifest, model: model) } }
                                .buttonStyle(.borderedProminent)
                        }
                        Text(model.updater.progress?.text ?? model.updater.state.label)
                            .font(.callout)
                            .foregroundStyle(isFailure ? .red : .secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    // 下載的進度（design §12，4.7.1）
                    if let progress = model.updater.progress {
                        if let fraction = progress.fraction { ProgressView(value: fraction).tint(.signal) } else { ProgressView().progressViewStyle(.linear) }
                    }
                }
                Section("環境檢查") {
                    ForEach(model.environment.items) { item in
                        HStack(alignment: .firstTextBaseline) {
                            Image(systemName: item.ok ? "checkmark.circle.fill" : "minus.circle")
                                .foregroundStyle(item.ok ? Color.celadon : .secondary)
                            VStack(alignment: .leading) {
                                Text(item.name)
                                Text(item.ok ? item.detail : item.fix).font(.caption).foregroundStyle(.secondary)
                                    .fixedSize(horizontal: false, vertical: true)
                            }
                        }
                    }
                    Button("重新檢查") { model.refreshEnvironment() }
                }
            }
        }
        .formStyle(.grouped)
        .onAppear { model.refreshEnvironment() }
        .sheet(isPresented: $showingUsageStats) {
            UsageStatsSheet { showingUsageStats = false }
        }
    }

    private var isFailure: Bool {
        if case .failed = model.updater.state { return true }
        return false
    }
}

/// 「匿名使用統計」的「詳細…」（design §30.5、R40.4）：開關、說明、重設匿名 ID。不顯示送出的 JSON（使用者決定）
private struct UsageStatsSheet: View {
    let done: () -> Void
    @AppStorage(UsageStats.enabledKey) private var usageStats = true
    @State private var didReset = false

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("匿名使用統計").font(.headline)
            Toggle("分享匿名使用統計", isOn: $usageStats)
                .onChange(of: usageStats) { _, on in
                    // 關掉時清掉還沒送的資料（R40.4），打開時建立今天的匯總
                    if on { UsageStats.shared.touch() } else { UsageStats.shared.clearPending() }
                }
            Text("每天送一份匿名的使用統計給拾音的開發者：版本、系統、錄音和轉錄了幾分鐘、用了哪些功能、失敗幾次和哪一種失敗。不會送會議標題、逐字稿、會議紀錄、人名、聲紋或任何檔案。")
                .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            HStack {
                Button("重設匿名 ID") {
                    UsageStats.shared.resetInstallId()
                    didReset = true
                }
                if didReset { Text("已重設匿名 ID").font(.callout).foregroundStyle(.secondary) }
                Spacer()
                Button("完成", action: done).keyboardShortcut(.defaultAction)
            }
        }
        .padding(20)
        .frame(width: 460)
    }
}
