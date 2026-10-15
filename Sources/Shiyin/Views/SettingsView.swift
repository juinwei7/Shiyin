import SwiftUI

/// 設定視窗（T-F2）：整理工具與金鑰、範本、詞彙表、錄音、模型與儲存空間、進階
struct SettingsView: View {
    var body: some View {
        TabView {
            ProviderSettings().tabItem { Label("整理工具", systemImage: "sparkles") }
            TemplateSettings().tabItem { Label("範本", systemImage: "doc.text") }
            GlossarySettings().tabItem { Label("詞彙表", systemImage: "text.book.closed") }
            RecordingSettings().tabItem { Label("錄音", systemImage: "mic") }
            StorageSettings().tabItem { Label("模型與儲存空間", systemImage: "internaldrive") }
            AdvancedSettings().tabItem { Label("進階", systemImage: "gearshape.2") }
        }
        .frame(width: 640, height: 520)
        .tint(.celadon)
    }
}

// MARK: 整理工具與金鑰（R6.1）

struct ProviderSettings: View {
    @Environment(AppModel.self) private var model
    @AppStorage(AppSettings.providerKey) private var provider = LLMProvider.claude.rawValue
    @AppStorage(AppSettings.autoSummarizeKey) private var autoSummarize = true
    @State private var modelName = ""
    @State private var key = ""
    @State private var keySaved = false
    @State private var availableModels: [String] = []

    private var current: LLMProvider { LLMProvider(rawValue: provider) ?? .claude }

    var body: some View {
        Form {
            Picker("整理工具", selection: $provider) {
                ForEach(LLMProvider.allCases) { Text($0.label).tag($0.rawValue) }
            }
            if current == .claude || current == .codex {
                LabeledContent("狀態") {
                    Text(Paths.findTool(current == .claude ? "claude" : "codex").map { "找到 \($0.path)" } ?? "找不到，請先安裝並登入")
                        .foregroundStyle(.secondary)
                }
            }
            if current.usesAPIKey {
                SecureField("API key", text: $key, prompt: Text(keySaved ? "已儲存在鑰匙圈（輸入新的會取代）" : "貼上 API key"))
                HStack {
                    Button("儲存 key") {
                        Keychain.set(current.keychainAccount, key)
                        key = ""
                        keySaved = Keychain.get(current.keychainAccount) != nil
                        Task { availableModels = await LLMClient.listModels(current) }
                        model.refreshEnvironment()
                    }
                    .disabled(key.trimmingCharacters(in: .whitespaces).isEmpty)
                    if keySaved {
                        Button("刪除 key", role: .destructive) {
                            Keychain.delete(current.keychainAccount)
                            keySaved = false
                            model.refreshEnvironment()
                        }
                    }
                }
            }
            if current != .none {
                HStack {
                    TextField("模型", text: $modelName, prompt: Text(current.defaultModel.isEmpty ? "留空使用預設模型" : "預設：\(current.defaultModel)"))
                    if !availableModels.isEmpty {
                        Menu("選擇") {
                            ForEach(availableModels, id: \.self) { name in Button(name) { modelName = name } }
                        }
                        .fixedSize()
                    }
                }
                .onChange(of: modelName) { _, value in UserDefaults.standard.set(value, forKey: current.modelKey) }
            }
            Toggle("轉錄完成後自動整理成會議紀錄", isOn: $autoSummarize)
                .disabled(current == .none)
            Section {
                Text(privacyText)
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .formStyle(.grouped)
        .onAppear(perform: load)
        .onChange(of: provider) { _, _ in load() }
    }

    private func load() {
        modelName = UserDefaults.standard.string(forKey: current.modelKey) ?? ""
        keySaved = current.usesAPIKey && Keychain.get(current.keychainAccount) != nil
        availableModels = []
        if keySaved { Task { availableModels = await LLMClient.listModels(current) } }
    }

    private var privacyText: String {
        switch current {
        case .none: return "目前不整理：錄音、轉錄、逐字稿全部留在這台 Mac。"
        case .claude, .anthropic:
            return "整理時會把逐字稿文字、你的筆記、詞彙表和會議基本資料送到 Anthropic（Claude）。錄音檔不會送出。"
        case .codex, .openai:
            return "整理時會把逐字稿文字、你的筆記、詞彙表和會議基本資料送到 OpenAI。錄音檔不會送出。"
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
                            Text(template.name)
                            Spacer()
                            if template.id == defaultTemplate { Text("預設").font(.caption).foregroundStyle(.secondary) }
                        }
                        .tag(template.id)
                    }
                }
                HStack {
                    Button("新增", systemImage: "plus") { create(from: nil) }
                    Button("複製", systemImage: "plus.square.on.square") {
                        create(from: model.templates.first { $0.id == selection })
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
        .onChange(of: selection) { _, id in load(id) }
        .onAppear { if selection == nil { selection = model.templates.first?.id } }
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
                    ForEach(binding.custom.indices, id: \.self) { i in
                        HStack(alignment: .top) {
                            VStack {
                                TextField("段落名稱", text: binding.custom[i].title)
                                TextField("要寫什麼", text: binding.custom[i].instruction, axis: .vertical)
                            }
                            Button { binding.wrappedValue.custom.remove(at: i) } label: { Image(systemName: "trash") }
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
                            model.deleteTemplate(template.id)
                            selection = model.templates.first?.id
                        }
                        Button("儲存") {
                            model.saveTemplate(TemplateRecord(id: template.id, name: draftName.isEmpty ? "未命名範本" : draftName,
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
        draft = TemplateDefinition.decode(template.definition)
        draftName = template.name
    }

    private func create(from source: TemplateRecord?) {
        let id = "custom-\(UUID().uuidString.prefix(8))"
        let definition = source.map { TemplateDefinition.decode($0.definition) }
            ?? TemplateDefinition(description: "", sections: ["summary", "decisions", "actionItems"], custom: [], instruction: "")
        model.saveTemplate(TemplateRecord(id: id, name: source.map { "\($0.name)（副本）" } ?? "新範本", builtin: false,
                                          definition: definition.jsonString()))
        selection = id
    }
}

// MARK: 詞彙表（沿用第二版）

struct GlossarySettings: View {
    @Environment(AppModel.self) private var model
    @State private var newTerm = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("系統名、產品名、人名的正確寫法。轉錄時會當成提示（最多約 150 字），整理時也會參考。實驗顯示，有沒有詞彙提示，專有名詞的正確率差很多。")
                .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            HStack {
                TextField("新增詞彙，例如產品名或人名", text: $newTerm).textFieldStyle(.roundedBorder).onSubmit(add)
                Button("新增", action: add).disabled(newTerm.trimmingCharacters(in: .whitespaces).isEmpty)
            }
            List {
                ForEach(model.glossary) { term in
                    HStack {
                        Text(term.term)
                        Spacer()
                        Button { model.deleteTerm(term.id!) } label: { Image(systemName: "trash") }
                            .buttonStyle(.borderless).help("刪除「\(term.term)」")
                    }
                }
            }
            .overlay {
                if model.glossary.isEmpty {
                    Text("還沒有詞彙。用詞修正時勾選「加進詞彙表」也會加到這裡。").font(.callout).foregroundStyle(.secondary).padding()
                }
            }
            Text("目前的轉錄提示：\(WhisperEngine.prompt(glossary: model.glossary.map(\.term)))")
                .font(.caption).foregroundStyle(.tertiary).textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
        }
        .padding(20)
    }

    private func add() {
        model.addTerm(newTerm)
        newTerm = ""
    }
}

// MARK: 錄音（R1）

struct RecordingSettings: View {
    @Environment(AppModel.self) private var model
    @AppStorage(AppSettings.transcriptionEngineKey) private var engine = "breeze"
    @AppStorage(AppSettings.recordSourceKey) private var source = "mic"
    @AppStorage(AppSettings.inputDeviceKey) private var device = ""
    @AppStorage(AppSettings.liveTranscriptionKey) private var live = true
    @AppStorage(AppSettings.diarizeKey) private var diarize = true
    @AppStorage(AppSettings.globalHotKeyKey) private var hotKey = true
    @State private var devices: [AudioDevices.Device] = []
    @State private var hotKeyFailed = false

    var body: some View {
        Form {
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
            Toggle("錄音中顯示即時逐字稿", isOn: $live)
            Toggle("轉錄時辨識說話者", isOn: $diarize)
            // 正式轉錄引擎（R4.1、design §6.2.1、D11）
            Picker("正式轉錄", selection: $engine) {
                Text("Breeze ASR 25（建議）").tag("breeze")
                Text("macOS 內建").tag("system")
            }
            .onChange(of: engine) { _, _ in
                model.runner.wake()
                model.refreshEnvironment()
            }
            Text(engine == "system"
                 ? "不用下載模型，速度快很多；但不參考詞彙表，英文專有名詞、人名比較容易錯。"
                 : "會參考詞彙表，中英夾雜、專有名詞比較準；需要下載約 1.7 GB 的模型。")
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
            Section {
                Text("錄線上會議時選「麥克風＋電腦聲音」：你的聲音和對方的聲音會分別錄在左右聲道。第一次錄電腦聲音時，macOS 會詢問「錄製系統音訊」的權限。")
                    .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                HStack {
                    Button("麥克風權限設定") { Recorder.openPrivacySettings() }
                    Button("系統錄音權限設定") { Recorder.openPrivacySettings("Privacy_AudioCapture") }
                }
            }
        }
        .formStyle(.grouped)
        .onAppear {
            devices = AudioDevices.inputDevices()
            hotKeyFailed = hotKey && !GlobalHotKey.shared.isRegistered
        }
    }
}

// MARK: 模型與儲存空間（R9.4、R10.3）

struct StorageSettings: View {
    @Environment(AppModel.self) private var model
    @AppStorage(AppSettings.compressAudioKey) private var compress = true
    @State private var audioBytes: Int64 = 0
    @State private var modelBytes: Int64 = 0

    var body: some View {
        Form {
            Section("轉錄模型") {
                ModelRow(title: ModelStore.breeze.name, state: model.models.state,
                         start: model.models.start, pause: model.models.pause, delete: model.models.delete)
                ModelRow(title: "說話者辨識模型（FluidAudio）", state: model.models.diarizerState,
                         start: model.models.prepareDiarizer, pause: nil, delete: nil)
            }
            Section("儲存空間") {
                LabeledContent("錄音", value: ByteCountFormatter.string(fromByteCount: audioBytes, countStyle: .file))
                LabeledContent("模型", value: ByteCountFormatter.string(fromByteCount: modelBytes, countStyle: .file))
                Toggle("轉錄完成後把錄音壓縮成 m4a（約每小時 10～30 MB）", isOn: $compress)
                LabeledContent("資料位置") {
                    Button(Paths.home.path) { NSWorkspace.shared.activateFileViewerSelecting([Paths.home]) }
                        .buttonStyle(.link)
                }
            }
            if model.legacyBytes > 0 {
                Section("第二版留下的檔案") {
                    Text("第二版用的 Python 環境和舊模型已經用不到了，佔用 \(ByteCountFormatter.string(fromByteCount: model.legacyBytes, countStyle: .file))。")
                        .fixedSize(horizontal: false, vertical: true)
                    Button("刪除第二版的 Python 環境和舊模型", role: .destructive) { model.deleteLegacyItems() }
                }
            }
        }
        .formStyle(.grouped)
        .onAppear(perform: measure)
    }

    private func measure() {
        model.refreshEnvironment()
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
            case .installed: if let delete { Button("刪除", role: .destructive, action: delete) }
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
            Section {
                Button("重新顯示首次使用引導") {
                    hasCompletedOnboarding = false
                    openWindow(id: "onboarding")
                }
                Button("重建搜尋索引") {
                    model.perform { try model.db.rebuildSearchIndex() }
                    model.noticeMessage = "搜尋索引已重建"
                }
            }
        }
        .formStyle(.grouped)
        .onAppear { model.refreshEnvironment() }
    }

    private var isFailure: Bool {
        if case .failed = model.updater.state { return true }
        return false
    }
}
