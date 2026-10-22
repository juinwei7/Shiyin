import AVFoundation
import SwiftUI
import UserNotifications

/// 首次使用引導（design §11、R10.2）：歡迎 → 權限 → 下載模型 → 整理工具 → 完成。每一步都可以略過。
struct OnboardingView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    @Environment(\.openWindow) private var openWindow
    @AppStorage("hasCompletedOnboarding") private var hasCompletedOnboarding = false
    @AppStorage("hasSeenGuide") private var hasSeenGuide = false
    @AppStorage(AppSettings.providerKey) private var storedProvider = ""
    @State private var step: Int
    @State private var micStatus = AVCaptureDevice.authorizationStatus(for: .audio)
    @State private var notificationsAllowed: Bool?
    @State private var speechInstalled: Bool?
    @State private var speechInstalling = false
    @State private var apiKey = ""
    @State private var keyError: String?

    /// startStep：DEBUG 的 preview 指令直接打開某一步（design §19.5）
    init(startStep: Int = 0) { _step = State(initialValue: startStep) }

    private let titles = [String(localized: "歡迎使用拾音"), String(localized: "權限"), String(localized: "下載模型"), String(localized: "整理工具"), String(localized: "準備好了")]

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 6) {
                ForEach(titles.indices, id: \.self) { i in
                    Capsule().fill(i <= step ? Color.celadon : Color.secondary.opacity(0.25)).frame(height: 4)
                }
            }
            .padding(.bottom, 24)
            Text(titles[step]).font(.songti(28))
                .padding(.bottom, 12)
            Group {
                switch step {
                case 0: welcome
                case 1: permissions
                case 2: downloads
                case 3: providers
                default: done
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            HStack {
                if step > 0 && step < titles.count - 1 { Button("上一步") { step -= 1 } }
                Spacer()
                if step < titles.count - 1 {
                    Button("略過") { step += 1 }.buttonStyle(.borderless)
                    Button("下一步") { step += 1 }.keyboardShortcut(.defaultAction).buttonStyle(.borderedProminent)
                } else {
                    Button("開始使用") { finish() }.keyboardShortcut(.defaultAction).buttonStyle(.borderedProminent)
                }
            }
        }
        .padding(32)
        .frame(width: 600, height: 560)
        .tint(.celadon)
        .task {
            speechInstalled = await LiveTranscriber.isLocaleInstalled(AppSettings.defaultMeetingLanguage)
            // 已經允許或拒絕過通知的話直接顯示，不要再顯示「允許」
            switch await UNUserNotificationCenter.current().notificationSettings().authorizationStatus {
            case .authorized, .provisional, .ephemeral: notificationsAllowed = true
            case .denied: notificationsAllowed = false
            default: break
            }
        }
    }

    private var welcome: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("拾音幫你把會議變成逐字稿和會議紀錄：錄音時就能看到即時逐字稿，錄完會在這台 Mac 上正式轉錄（中文、英文用 Breeze ASR 25，日文用 Whisper）、分辨說話者，再用 Claude、Codex 或 API 整理成摘要、決議和待辦。")
                .fixedSize(horizontal: false, vertical: true)
            Label("錄音和轉錄都只在這台 Mac 上進行，不會上傳。", systemImage: "lock")
            Label("整理會議紀錄時，只會把逐字稿文字送到你選的 AI 服務。", systemImage: "text.bubble")
            Text("接下來三個步驟大約 3 分鐘（下載模型的時間另計）。").foregroundStyle(.secondary)
        }
        .lineSpacing(3)
    }

    private var permissions: some View {
        VStack(alignment: .leading, spacing: 18) {
            permissionRow(icon: "mic", title: String(localized: "麥克風"), detail: String(localized: "錄現場會議或你自己的聲音。"),
                          status: micStatus == .authorized ? String(localized: "已允許") : micStatus == .denied ? String(localized: "被拒絕") : nil) {
                if micStatus == .notDetermined {
                    AVCaptureDevice.requestAccess(for: .audio) { _ in
                        Task { @MainActor in micStatus = AVCaptureDevice.authorizationStatus(for: .audio) }
                    }
                } else {
                    Recorder.openPrivacySettings()
                }
            }
            permissionRow(icon: "speaker.wave.2", title: String(localized: "錄製系統音訊"), detail: String(localized: "錄線上會議（Zoom、Meet、Teams）對方的聲音。第一次選「麥克風＋電腦聲音」錄音時，macOS 會跳出詢問，請按允許。"),
                          status: nil, buttonTitle: String(localized: "打開系統設定")) {
                Recorder.openPrivacySettings("Privacy_AudioCapture")
            }
            permissionRow(icon: "bell", title: String(localized: "通知"), detail: String(localized: "轉錄完成、會議紀錄整理好時通知你。"),
                          status: notificationsAllowed.map { $0 ? String(localized: "已允許") : String(localized: "被拒絕") }) {
                if notificationsAllowed == false {
                    // 被拒絕之後再要權限不會有反應，要到系統設定打開
                    if let url = URL(string: "x-apple.systempreferences:com.apple.Notifications-Settings.extension") { NSWorkspace.shared.open(url) }
                    return
                }
                Task {
                    let granted = (try? await UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound])) ?? false
                    notificationsAllowed = granted
                }
            }
        }
    }

    private func permissionRow(icon: String, title: String, detail: String, status: String?,
                               buttonTitle: String = String(localized: "允許"), action: @escaping () -> Void) -> some View {
        HStack(alignment: .top, spacing: 14) {
            Image(systemName: icon).font(.title2).foregroundStyle(Color.celadon).frame(width: 30)
            VStack(alignment: .leading, spacing: 4) {
                Text(title).font(.headline)
                Text(detail).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
            Spacer()
            if let status, status == String(localized: "已允許") {
                Label(status, systemImage: "checkmark.circle.fill").foregroundStyle(Color.celadon)
            } else {
                Button(status == String(localized: "被拒絕") ? String(localized: "打開系統設定") : buttonTitle, action: action)
            }
        }
    }

    private var primaryModel: ModelStore.Model { ModelStore.transcription(for: AppSettings.defaultMeetingLanguage) }

    private var downloads: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("轉錄模型只需要下載一次，之後轉錄都不需要網路。可以先按「下一步」，在背景繼續下載，下載完成前會先用 macOS 內建辨識。不想下載的話，可以到「設定 → 錄音」把正式轉錄改成 macOS 內建（不參考詞彙表）。")
                .foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            // 預設會議語言是日文時，先下載日文轉錄模型（design §18.2）
            if primaryModel == .japanese {
                ModelRow(title: String(localized: "\(ModelStore.japanese.name)：日文會議的正式轉錄，574 MB"), state: model.models.state(.japanese),
                         start: { model.models.start(.japanese) }, pause: { model.models.pause(.japanese) }, delete: nil)
            } else {
                ModelRow(title: String(localized: "\(ModelStore.breeze.name)：正式轉錄，1.66 GB"), state: model.models.state,
                         start: { model.models.start() }, pause: { model.models.pause() }, delete: nil)
            }
            ModelRow(title: String(localized: "說話者辨識模型：分辨是誰在說話"), state: model.models.diarizerState,
                     start: model.models.prepareDiarizer, pause: nil, delete: nil)
            HStack {
                VStack(alignment: .leading, spacing: 4) {
                    Text("即時逐字稿的語音模型（macOS 內建）")
                    Text(speechInstalled == true ? "已安裝" : speechInstalling ? "下載中…" : "還沒安裝")
                        .font(.caption).foregroundStyle(speechInstalled == true ? Color.celadon : .secondary)
                }
                Spacer()
                if speechInstalled != true {
                    Button("下載") {
                        speechInstalling = true
                        Task {
                            try? await LiveTranscriber.ensureAssets(AppSettings.defaultMeetingLanguage)
                            speechInstalled = await LiveTranscriber.isLocaleInstalled(AppSettings.defaultMeetingLanguage)
                            speechInstalling = false
                        }
                    }
                    .disabled(speechInstalling)
                }
            }
        }
        .onAppear {
            // 正式轉錄選了 macOS 內建就不用下載轉錄模型（從進階重新顯示引導時）
            if !AppSettings.useSystemTranscriber { model.models.autoStart(primaryModel) }
            if AppSettings.autoDetectLanguage { model.models.autoStart(.languageDetection) }
            if model.models.diarizerState == .missing { model.models.prepareDiarizer() }
        }
    }

    private var providers: some View {
        let claude = Paths.findTool("claude"), codex = Paths.findTool("codex")
        // 沒選過時顯示自動選到的那一個；找不到 claude、codex 時是「之後再設定」（R35.3、design §19.6）。改了才寫進設定
        let selected = LLMProvider(rawValue: storedProvider) != nil ? storedProvider
            : claude != nil ? LLMProvider.claude.rawValue : codex != nil ? LLMProvider.codex.rawValue : ""
        let current = LLMProvider(rawValue: selected) ?? .none
        let noCLI = claude == nil && codex == nil
        return VStack(alignment: .leading, spacing: 14) {
            Text("會議紀錄要用哪個 AI 整理？之後可以在設定裡更改。").foregroundStyle(.secondary)
            if noCLI {
                Text("這台 Mac 沒有 Claude Code 或 Codex。要用 AI 整理會議紀錄，選一個 API 並貼上 API key（有取得 key 的連結）；也可以之後再設定，錄音和逐字稿照常能用。")
                    .foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
            Picker("整理工具", selection: Binding(get: { selected }, set: { storedProvider = $0 })) {
                if noCLI { Text("之後再設定（先不整理）").tag("") }
                Text("Claude（claude CLI）\(claude == nil ? String(localized: "：沒有安裝（給已經在用 Claude Code 的人）") : "")").tag(LLMProvider.claude.rawValue)
                Text("Codex（codex CLI）\(codex == nil ? String(localized: "：沒有安裝（給已經在用 Codex 的人）") : "")").tag(LLMProvider.codex.rawValue)
                Text("Anthropic API（填 API key）").tag(LLMProvider.anthropic.rawValue)
                Text("OpenAI API（填 API key）").tag(LLMProvider.openai.rawValue)
                Text("Gemini API（填 API key）").tag(LLMProvider.gemini.rawValue)
                Text("OpenAI 相容 API（Groq、OpenRouter、Ollama、LM Studio）").tag(LLMProvider.openaiCompatible.rawValue)
                Text("不整理，只要逐字稿").tag(LLMProvider.none.rawValue)
            }
            .pickerStyle(.radioGroup)
            // OpenAI 相容 API 要填網址、模型，引導裡只提示（design §19.4）
            if current == .openaiCompatible {
                Text("網址、API key 和模型請在完成引導後，到「設定 → 整理工具」填寫。")
                    .foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            } else if current.usesAPIKey {
                HStack {
                    SecureField("貼上 API key", text: $apiKey).textFieldStyle(.roundedBorder)
                    Button("儲存") {
                        do {
                            try Keychain.set(current.keychainAccount, apiKey)
                            apiKey = ""
                            keyError = nil
                        } catch {
                            keyError = error.localizedDescription
                        }
                        model.refreshEnvironment()
                    }
                    .disabled(apiKey.isEmpty)
                }
                if let keyError {
                    Text(keyError).font(.caption).foregroundStyle(.red).fixedSize(horizontal: false, vertical: true)
                } else if Keychain.exists(current.keychainAccount) {
                    Label("已儲存在鑰匙圈", systemImage: "checkmark.circle.fill").foregroundStyle(Color.celadon)
                }
                if let url = current.apiKeyURL { Link("取得 API key", destination: url) }
            }
        }
    }

    private var done: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("按「開始使用」後會打開使用說明，介紹錄音、即時逐字稿、會議紀錄和匯出的用法。")
                .fixedSize(horizontal: false, vertical: true)
            if !AppSettings.useSystemTranscriber && !model.models.isInstalled(primaryModel) {
                Label(model.models.state(primaryModel).isBusy
                      ? String(localized: "轉錄模型還在下載，下載完成前會先用 macOS 內建辨識轉錄。下載好之後，可以在逐字稿分頁重新轉錄。")
                      : String(localized: "轉錄模型還沒下載，會先用 macOS 內建辨識轉錄。可以到「設定 → 模型與儲存空間」下載，用模型轉錄比較準。"),
                      systemImage: "arrow.down.circle")
                    .foregroundStyle(Color.signal).fixedSize(horizontal: false, vertical: true)
            }
            Text("之後可以在「設定 → 進階」重新顯示這個引導。").foregroundStyle(.secondary)
        }
    }

    private func finish() {
        hasCompletedOnboarding = true
        hasSeenGuide = true
        model.refreshEnvironment()
        openWindow(id: "guide")
        dismiss()
    }
}
