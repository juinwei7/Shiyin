import SwiftUI

/// 設定視窗（T-F2）：整理工具與金鑰、範本、詞彙表、錄音、模型與儲存空間、進階
struct SettingsView: View {
    #if DEBUG
    /// shiyinctl preview settings <分頁>：預覽視窗一開始停在哪個分頁（0 整理工具、1 錄音與轉錄、2 說話者與詞彙、3 進階）
    nonisolated(unsafe) static var previewTab = 0
    #endif
    @State private var tab: Int = {
        #if DEBUG
        return SettingsView.previewTab
        #else
        return 0
        #endif
    }()

    var body: some View {
        // 四個分頁（R44.5，design §33.3、D110）：只搬位置、加分組，每個項目和存的鍵都沒變
        TabView(selection: $tab) {
            ProviderSettings().tabItem { Label("整理工具", systemImage: "sparkles") }.tag(0)
            RecordingSettings().tabItem { Label("錄音與轉錄", systemImage: "mic") }.tag(1)
            NamesSettings().tabItem { Label("說話者與詞彙", systemImage: "person.text.rectangle") }.tag(2)
            AdvancedSettings().tabItem { Label("進階", systemImage: "gearshape.2") }.tag(3)
        }
        .frame(width: 640, height: 520)
        .tint(.celadon)
    }
}

// MARK: 整理工具與金鑰（R6.1）

struct ProviderSettings: View {
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
                ForEach(LLMProvider.allCases) { Text($0.label).tag($0.rawValue) }
            }
            if current == .claude || current == .codex {
                LabeledContent("狀態") {
                    HStack(spacing: 8) {
                        if Paths.findTool(current == .claude ? "claude" : "codex") == nil {
                            Text(current == .codex ? "這台 Mac 沒有安裝 codex：沒有在用 Codex 的話，請改選 API"
                                                   : "這台 Mac 沒有安裝 claude：沒有在用 Claude Code 的話，請改選 API")
                                .foregroundStyle(.secondary)
                        } else if checkingLogin {
                            Text("檢查登入狀態…").foregroundStyle(.secondary)
                        } else if let login {
                            Text(login.loggedIn ? String(localized: "已登入 \(login.detail)") : String(localized: "沒有登入"))
                                .foregroundStyle(login.loggedIn ? Color.celadon : Color.red)
                            if !login.loggedIn {
                                Button("登入 \(current.shortLabel)") { CLILogin.openLogin(current) }
                            }
                        } else {
                            Text("已安裝，看不出登入狀態").foregroundStyle(.secondary)
                        }
                        Button { Task { await checkLogin() } } label: { Image(systemName: "arrow.clockwise") }
                            .buttonStyle(.borderless)
                            .help("重新檢查登入狀態")
                            .disabled(checkingLogin)
                    }
                }
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
            if current != .none {
                HStack {
                    TextField("模型", text: $modelName, prompt: Text(current == .openaiCompatible ? "必填"
                                                                    : current.defaultModel.isEmpty ? "留空使用預設模型" : "預設：\(current.defaultModel)"))
                    if canListModels {
                        Button("選擇", action: openModelPicker)
                            .popover(isPresented: $pickingModel, arrowEdge: .bottom) { modelList }
                    }
                }
                .onChange(of: modelName) { _, value in UserDefaults.standard.set(value, forKey: current.modelKey) }
            }
            // 測試：送一個很短的請求，確認登入、key、模型都能用（使用者回報：開完會才發現登入過期）
            if current != .none {
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
            // 範本是整理的一部分（R44.5、D110）：原本自己一個分頁
            Section(MeetingLanguage.ui(zh: "範本", en: "Templates")) {
                TemplateSettings().frame(height: 420)
            }
        }
        .formStyle(.grouped)
        .onAppear(perform: load)
        .onChange(of: storedProvider) { _, _ in load() }
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
    }

    private func openModelPicker() {
        pickingModel = true
        if availableModels.isEmpty && !loadingModels { refreshModels() }
    }

    private func test() async {
        testing = true
        testResult = nil
        let started = Date()
        let language = AppSettings.defaultMeetingLanguage
        let prompt = language.text(zh: "這是連線測試。逐字稿：\n[00:00] 說話者 1：大家好，今天只是測試一下。",
                                   en: "This is a connection test. Transcript:\n[00:00] Speaker 1: Hi everyone, this is just a test.",
                                   ja: "これは接続テストです。文字起こし：\n[00:00] 話者 1：皆さん、今日はテストだけです。")  // l10n-skip
        let tested = current
        let result: (Bool, String)
        do {
            _ = try await LLMClient.generate(provider: tested, model: modelName, system: NotesPrompt.system(language),
                                             prompt: prompt, onStart: { _ in })
            result = (true, String(localized: "可以使用（\(Int(Date().timeIntervalSince(started).rounded())) 秒）"))
        } catch {
            result = (false, error.localizedDescription)
        }
        // 測試期間換了整理工具：結果是舊工具的，不顯示（換工具時 load() 已經重設狀態）
        guard tested == current else { return }
        testResult = result
        testing = false
    }

    private func checkLogin() async {
        guard current == .claude || current == .codex else { login = nil; return }
        checkingLogin = true
        let checked = current
        let status = await CLILogin.status(checked)
        guard checked == current else { return }   // 查的時候換了整理工具
        login = status
        checkingLogin = false
    }

    private func load() {
        testResult = nil
        testing = false
        login = nil
        Task { await checkLogin() }
        modelName = UserDefaults.standard.string(forKey: current.modelKey) ?? ""
        baseURL = UserDefaults.standard.string(forKey: OpenAICompatibleAPI.baseURLKey) ?? ""
        // 只看有沒有存，不讀出 key：打開設定頁不能跳出鑰匙圈的確認（R18.7、R21.1）
        keySaved = current.usesAPIKey && Keychain.exists(current.keychainAccount)
        keyError = nil
        availableModels = []
        modelListError = nil
        loadingModels = false
    }

    /// 可以從清單選模型：API 要有存 key，OpenAI 相容 API 要有網址
    private var canListModels: Bool {
        current == .openaiCompatible ? OpenAICompatibleAPI.normalized(baseURL) != nil : current.usesAPIKey && keySaved
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
        if let template = model.templates.first(where: { $0.id == selection }), let binding = Binding($draft) {
            Form {
                TextField("名稱", text: $draftName).disabled(template.builtin)
                TextField("用途說明", text: binding.description, axis: .vertical).disabled(template.builtin)
                Section("要產生的段落") {
                    ForEach(TemplateDefinition.builtinSections, id: \.id) { section in
                        Toggle(section.label, isOn: Binding(
                            get: { binding.wrappedValue.sections.contains(section.id) },
                            set: { on in
                                if on { binding.wrappedValue.sections.append(section.id) }
                                else { binding.wrappedValue.sections.removeAll { $0 == section.id } }
                            }))
                    }
                }
                .disabled(template.builtin)
                Section("自訂段落") {
                    ForEach(binding.wrappedValue.custom.indices, id: \.self) { i in
                        let custom = binding.custom.safe(i, fallback: .init(title: "", instruction: ""))
                        HStack(alignment: .top) {
                            VStack {
                                TextField("段落名稱", text: custom.title)
                                TextField("要寫什麼", text: custom.instruction, axis: .vertical)
                            }
                            Button { binding.custom.remove(i) } label: { Image(systemName: "trash") }
                                .buttonStyle(.borderless)
                        }
                    }
                    Button("新增自訂段落", systemImage: "plus") { binding.wrappedValue.custom.append(.init(title: "", instruction: "")) }
                }
                .disabled(template.builtin)
                TextField("額外指示（選填）", text: binding.instruction, axis: .vertical).disabled(template.builtin)
                HStack {
                    if template.builtin {
                        Text("內建範本不能修改，可以按「複製」做一份自己的。").font(.caption).foregroundStyle(.secondary)
                    }
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
        let definition = edited ?? source.map { $0.definition(for: .interface) }
            ?? TemplateDefinition(description: "", sections: ["summary", "decisions", "actionItems"], custom: [], instruction: "")
        model.saveTemplate(TemplateRecord(id: id, name: source.map { String(localized: "\($0.displayName)（副本）") } ?? String(localized: "新範本"), builtin: false,
                                          definition: definition.jsonString()))
        selection = id
    }
}

// MARK: 詞彙表（沿用第二版）

/// 說話者與詞彙（R44.5，design §33.3）：原本的「說話者」和「詞彙表」兩個分頁合成一頁，各一個 Section
struct NamesSettings: View {
    var body: some View {
        Form {
            Section(MeetingLanguage.ui(zh: "說話者", en: "Speakers")) { SpeakerSettings() }
            Section("詞彙表") { GlossarySettings() }
        }
        .formStyle(.grouped)
    }
}

/// 說話者（design §21.4、R19.3）：記住說話者的聲音，之後的會議自動帶入名字；認識的人可以改名、刪除。
/// 是 Form 的一個 Section 的內容（R44.5）：認識的人一人一列，不用 List
struct SpeakerSettings: View {
    @Environment(AppModel.self) private var model
    @AppStorage(AppSettings.voiceprintsKey) private var voiceprints = true
    @State private var renaming: Int64?
    @State private var renameText = ""
    @State private var confirmDeleteAll = false

    var body: some View {
        Toggle("記住說話者的聲音，之後的會議自動帶入名字", isOn: $voiceprints)
        Text("替說話者取名時，拾音會記下這個人的聲音特徵。之後的會議認出同一個人，就自動帶入名字並標「自動」，不對可以改。聲音特徵只存在這台 Mac 的資料庫，不會上傳，也不會放進匯出的 .shiyin 封包。")
            .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
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
                        Text(person.name)
                        Text(detailLine(person)).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                    }
                    Spacer()
                    Button("改名") { renameText = person.name; renaming = person.id }.buttonStyle(.link)
                    Button { model.deletePerson(person.id) } label: { Image(systemName: "trash") }
                        .buttonStyle(.borderless).help("刪除「\(person.name)」的聲音特徵（已經帶入的名字留著）")
                }
            }
        }
        .onAppear { model.reloadPeople() }
        .confirmationDialog("刪除所有認識的人的聲音特徵？", isPresented: $confirmDeleteAll) {
            Button("全部刪除", role: .destructive) { model.deleteAllPeople() }
        } message: {
            Text("已經帶入的名字會留著，之後的會議不會再自動帶入。")
        }
    }

    private func detailLine(_ person: PersonSummary) -> String {
        let prints = String(localized: "\(person.voiceprintCount) 份聲音特徵")
        guard let title = person.lastMeetingTitle, let date = person.lastMeetingDate else { return prints }
        return String(localized: "\(prints)・最近：\(date) \(title)")
    }
}

/// 詞彙表（沿用第二版）。是 Form 的一個 Section 的內容（R44.5）：詞彙一條一列，不用 List
struct GlossarySettings: View {
    @Environment(AppModel.self) private var model
    @State private var newTerm = ""

    var body: some View {
        Text("系統名、產品名、人名的正確寫法。轉錄時會當成提示（最多約 150 字），整理時也會參考。實驗顯示，有沒有詞彙提示，專有名詞的正確率差很多。")
            .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
        HStack {
            TextField("新增詞彙，例如產品名或人名", text: $newTerm).textFieldStyle(.roundedBorder).onSubmit(add)
            Button("新增", action: add).disabled(newTerm.trimmingCharacters(in: .whitespaces).isEmpty)
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
        ForEach(model.glossary) { term in
            HStack {
                Text(term.term)
                Spacer()
                Button { model.deleteTerm(term.id!) } label: { Image(systemName: "trash") }
                    .buttonStyle(.borderless).help("刪除「\(term.term)」")
            }
        }
        Text(String(localized: "目前的轉錄提示：\(WhisperEngine.prompt(glossary: model.glossary.map(\.term), language: AppSettings.defaultMeetingLanguage))"))
            .font(.caption).foregroundStyle(.tertiary).textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
    }

    private func add() {
        model.addTerm(newTerm)
        newTerm = ""
    }
}

// MARK: 錄音與轉錄（R1、R4、R10.3；R44.5 分成四組）

struct RecordingSettings: View {
    @Environment(AppModel.self) private var model
    @AppStorage(AppSettings.transcriptionEngineKey) private var engine = "breeze"
    @AppStorage(AppSettings.defaultMeetingLanguageKey) private var meetingLanguage: MeetingLanguage = .systemDefault
    @AppStorage(AppSettings.recordSourceKey) private var source = "mic"
    @AppStorage(AppSettings.inputDeviceKey) private var device = ""
    @AppStorage(AppSettings.liveTranscriptionKey) private var live = true
    @AppStorage(AppSettings.autoDetectLanguageKey) private var autoDetect = true
    @AppStorage(AppSettings.diarizeKey) private var diarize = true
    @AppStorage(AppSettings.globalHotKeyKey) private var hotKey = true
    @AppStorage(AppSettings.meetingDetectionKey) private var meetingDetection = true
    @AppStorage(AppSettings.bookmarkHotKeyKey) private var bookmarkHotKey = true
    @State private var mutedApps: [String] = []
    @State private var devices: [AudioDevices.Device] = []
    @State private var hotKeyFailed = false

    var body: some View {
        Form {
            Section("錄音來源") {
                Picker("錄音來源", selection: $source) {
                    Text("只錄麥克風").tag("mic")
                    Text("麥克風＋電腦聲音（線上會議）").tag("both")
                    Text("只錄電腦聲音").tag("system")
                }
                Picker("麥克風", selection: $device) {
                    Text("系統預設").tag("")
                    ForEach(devices) { Text($0.name).tag($0.uid) }
                }
                .disabled(source == "system")
                Text("錄線上會議時選「麥克風＋電腦聲音」：你的聲音和對方的聲音會分別錄在左右聲道。第一次錄電腦聲音時，macOS 會詢問「錄製系統音訊」的權限。")
                    .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                HStack {
                    Button("麥克風權限設定") { Recorder.openPrivacySettings() }
                    Button("系統錄音權限設定") { Recorder.openPrivacySettings("Privacy_AudioCapture") }
                }
            }
            Section("語言") {
                // 會議語言（R12.4、design §16.3）
                Picker("預設會議語言", selection: $meetingLanguage) {
                    ForEach(MeetingLanguage.allCases) { Text($0.name).tag($0) }
                }
                Text("新的錄音和匯入預設用這個語言轉錄、整理會議紀錄；錄音選單、匯入時和會議頁都可以改。")
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
                Text("開始錄音後聽約 10 秒，判斷是中文、英文還是日文；跟會議語言不同而且有把握時會自動切換，錄音畫面可以改回來。需要 60 MB 的語言偵測模型。")
                    .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
            Section("轉錄") {
                // 正式轉錄引擎（R4.1、design §6.2.1、D11；Qwen3-ASR 見 §32.6）
                Picker("正式轉錄", selection: $engine) {
                    Text("Breeze ASR 25（聯發科，建議）").tag("breeze")
                    Text("Qwen3-ASR 1.7B（阿里巴巴）").tag("qwen")
                    Text("macOS 內建").tag("system")
                }
                .onChange(of: engine) { _, _ in
                    model.runner.wake()
                    model.refreshEnvironment()
                }
                Text(engine == "system"
                     ? "不用下載模型，速度快很多；但不參考詞彙表，英文專有名詞、人名比較容易錯。"
                     : engine == "qwen"
                     ? "會參考詞彙表，多人會議的漏字和同音字比 Breeze 少、速度快三成；阿里巴巴開源的模型，全程在這台 Mac 上跑、不連網，輸出會轉成台灣繁體；需要下載約 2.5 GB 的模型。日文會議改用 Whisper large-v3-turbo（574 MB）。"
                     : "會參考詞彙表，中英夾雜、專有名詞比較準；聯發科開源的台灣模型，需要下載約 1.7 GB。日文會議改用 Whisper large-v3-turbo（574 MB）。")
                    .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                Toggle("轉錄時辨識說話者", isOn: $diarize)
                Toggle("錄音中顯示即時逐字稿", isOn: $live)
                // 模型清單（原「模型與儲存空間」分頁，R44.5）
                ModelRows()
            }
            Section("提醒與快捷鍵") {
                // 偵測線上會議（design §21.7、R19.4）
                Toggle("偵測到線上會議時提醒錄音", isOn: $meetingDetection)
                Text("Zoom、Teams、Webex、Google Meet（瀏覽器）等開始用麥克風、拾音沒在錄音時提醒你；會議結束時提醒停止。只提醒，不會自動錄音。")
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
            }
        }
        .formStyle(.grouped)
        .onAppear {
            devices = AudioDevices.inputDevices()
            hotKeyFailed = hotKey && !GlobalHotKey.shared.isRegistered
            mutedApps = MeetingDetector.muted
        }
    }
}

/// 模型清單（R10.3；R44.5 從「模型與儲存空間」搬到「錄音與轉錄 → 轉錄」）
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
        Text("正式轉錄選「Qwen3-ASR」時用，2.5 GB。").font(.caption).foregroundStyle(.secondary)
        ModelRow(title: ModelStore.japanese.name, state: model.models.state(.japanese),
                 start: { model.models.start(.japanese) }, pause: { model.models.pause(.japanese) },
                 delete: { model.models.delete(.japanese); model.refreshEnvironment() }, deleteDisabled: transcribing)
        Text("日文會議用，574 MB。第一次轉錄日文會議時會自動下載。").font(.caption).foregroundStyle(.secondary)
        ModelRow(title: ModelStore.languageDetection.name, state: model.models.state(.languageDetection),
                 start: { model.models.start(.languageDetection) }, pause: { model.models.pause(.languageDetection) },
                 delete: { model.models.delete(.languageDetection); model.refreshEnvironment() }, deleteDisabled: model.recorder.isRecording)
        Text("自動偵測會議語言用，60 MB。").font(.caption).foregroundStyle(.secondary)
        ModelRow(title: String(localized: "說話者辨識模型（FluidAudio）"), state: model.models.diarizerState,
                 start: model.models.prepareDiarizer, pause: nil, delete: nil)
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
    @Environment(AppModel.self) private var model
    @AppStorage("hasCompletedOnboarding") private var hasCompletedOnboarding = false
    @AppStorage(Updater.urlKey) private var updateURL = ""
    @State private var showingUsageStats = false
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        Form {
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
                    Text(model.updater.state.label)
                        .font(.callout)
                        .foregroundStyle(isFailure ? .red : .secondary)
                        .fixedSize(horizontal: false, vertical: true)
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
            Section("儲存空間") { StorageSection() }
            if model.legacyBytes > 0 {
                Section("第二版留下的檔案") {
                    Text("第二版用的 Python 環境和舊模型已經用不到了，佔用 \(ByteCountFormatter.string(fromByteCount: model.legacyBytes, countStyle: .file))。")
                        .fixedSize(horizontal: false, vertical: true)
                    Button("刪除第二版的 Python 環境和舊模型", role: .destructive) { model.deleteLegacyItems() }
                }
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
            Text("每天送一份匿名的使用統計給拾音的開發者：版本、系統、錄音和轉錄了幾分鐘、用了哪些功能、失敗幾次。不會送會議標題、逐字稿、會議紀錄、人名、聲紋或任何檔案。")
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
