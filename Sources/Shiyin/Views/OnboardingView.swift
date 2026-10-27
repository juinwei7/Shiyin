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
    /// 整理工具那一步的第一段「你平常用哪個 AI？」（R44.6、design §33.4）：claude、chatgpt、gemini、other、none；不存，只用來列第二段的選項
    @State private var usual: String?
    /// 第二段選的整理工具（R45.2、design §34.3）：選了不等於設定好，準備好（登入了、存了 key）才寫進設定
    @State private var pending: LLMProvider?
    @State private var setup: ProviderSetup = .idle

    private enum ProviderSetup: Equatable {
        case idle, checkingLogin, notLoggedIn, needsKey, needsCompat, testing
        case tested(ok: Bool, message: String)
    }

    /// startStep：DEBUG 的 preview 指令直接打開某一步（design §19.5）
    init(startStep: Int = 0) { _step = State(initialValue: startStep) }

    /// 三步（R44.6、design §33.4、D111）：權限（開頭是原本歡迎頁的三句話）、整理工具（先問平常用哪個 AI）、開始（模型在背景下載）
    private let titles = [String(localized: "歡迎使用拾音"), String(localized: "整理工具"), String(localized: "準備好了")]

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
                case 0: permissions
                case 1: providers
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
        .onAppear {
            // 模型從引導打開就在背景下載，不佔一步（R44.6）；正式轉錄選了 macOS 內建就不用
            // 轉錄用企業伺服器時不用下載（R50.7）
            if !AppSettings.useSystemTranscriber && !AppSettings.usesEnterpriseTranscription {
                model.models.autoStartTranscription(for: AppSettings.defaultMeetingLanguage)
            }
            if AppSettings.autoDetectLanguage { model.models.autoStart(.languageDetection) }
            if model.models.diarizerState == .missing { model.models.prepareDiarizer() }
        }
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

    private var permissions: some View {
        VStack(alignment: .leading, spacing: 18) {
            // 原歡迎頁的重點，三行（R44.6）
            VStack(alignment: .leading, spacing: 6) {
                Label("錄下來：現場會議或線上會議，錄音時就有即時逐字稿。", systemImage: "waveform")
                Label("轉成逐字稿：在這台 Mac 上轉錄、分辨說話者，不會上傳。", systemImage: "lock")
                Label("整理成紀錄：用你選的 AI 整理成摘要、決議和待辦，只送逐字稿文字。", systemImage: "text.bubble")
            }
            .padding(.bottom, 4)
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
            // 「錄製系統音訊」不放按鈕（畫面檢視 O-2）：macOS 的清單要等拾音第一次錄電腦聲音之後才會出現拾音
            HStack(alignment: .top, spacing: 14) {
                Image(systemName: "speaker.wave.2").font(.title2).foregroundStyle(Color.celadon).frame(width: 30)
                VStack(alignment: .leading, spacing: 4) {
                    Text("錄製系統音訊").font(.headline)
                    Text("錄線上會議對方的聲音用。第一次選「麥克風＋電腦聲音」錄音時 macOS 會詢問，按允許就好。")
                        .foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
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

    /// 整理工具（R44.6、D111）：先問「你平常用哪個 AI？」，選了才列對應的一到兩個選項，每個一行寫要不要錢。
    /// 選了不等於設定好（R45.2）：準備好才寫進設定，寫進去時測一次
    private var providers: some View {
        let claude = Paths.findTool("claude"), codex = Paths.findTool("codex")
        let usualValue = usual ?? guessedUsual(claude: claude, codex: codex)
        let list = options(for: usualValue, claude: claude, codex: codex)
        let shown = pending ?? shownProvider(in: list)
        return VStack(alignment: .leading, spacing: 14) {
            Text("會議紀錄要用哪個 AI 整理？之後可以在設定裡更改。").foregroundStyle(.secondary)
            Picker("你平常用哪個 AI？", selection: Binding(get: { usualValue }, set: { pick($0, claude: claude, codex: codex) })) {
                Text("Claude").tag("claude")
                Text("ChatGPT").tag("chatgpt")
                Text("Gemini").tag("gemini")
                Text("其他").tag("other")
                Text("先不用").tag("none")
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            Picker("整理工具", selection: Binding(get: { shown?.rawValue ?? "" }, set: { choose(LLMProvider(rawValue: $0)) })) {
                ForEach(list, id: \.provider) { option in
                    VStack(alignment: .leading, spacing: 2) {
                        Text(option.title)
                        Text(option.cost).font(.caption).foregroundStyle(.secondary)
                    }
                    .tag(option.provider.rawValue)
                }
            }
            .pickerStyle(.radioGroup)
            .labelsHidden()
            if let shown { setupStatus(shown) }
        }
    }

    /// 選了第二段的選項之後的狀態：登入、API key、測試結果
    @ViewBuilder
    private func setupStatus(_ provider: LLMProvider) -> some View {
        let state = pending == provider ? setup : .idle
        if provider == .openaiCompatible {
            // OpenAI 相容 API 要填網址、模型，引導裡只提示（design §19.4）
            Text("網址、API key 和模型請在完成引導後，到「設定 → 整理工具」填寫。填好之前不會整理。")
                .foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
        } else if provider.usesAPIKey {
            HStack {
                SecureField("貼上 API key", text: $apiKey).textFieldStyle(.roundedBorder)
                Button("儲存") { saveKey(provider) }
                    .disabled(apiKey.isEmpty)
            }
            if let keyError {
                Text(keyError).font(.caption).foregroundStyle(.red).fixedSize(horizontal: false, vertical: true)
            } else if Keychain.exists(provider.keychainAccount) {
                Label("已儲存在鑰匙圈", systemImage: "checkmark.circle.fill").foregroundStyle(Color.celadon)
            } else if state == .needsKey {
                Text("貼上 API key 並按「儲存」後才會用 \(provider.shortLabel) 整理；沒有 key 的話可以選「先不用」。")
                    .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
            if let url = provider.apiKeyURL { Link("取得 API key", destination: url) }
        } else if provider == .claude || provider == .codex {
            let name = provider == .codex ? "Codex" : "Claude Code"
            if state == .checkingLogin {
                Text("檢查登入狀態…").foregroundStyle(.secondary)
            } else if state == .notLoggedIn {
                Text("\(name) 還沒登入，登入之後才會用它整理。").foregroundStyle(.red).fixedSize(horizontal: false, vertical: true)
                HStack {
                    Button("登入 \(name)") { CLILogin.openLogin(provider) }
                    Button("重新檢查") { choose(provider) }
                }
            }
        }
        switch state {
        case .testing:
            HStack(spacing: 8) {
                ProgressView().controlSize(.small)
                Text("測試中…").foregroundStyle(.secondary)
            }
        case .tested(let ok, let message):
            Label(message, systemImage: ok ? "checkmark.circle.fill" : "exclamationmark.triangle.fill")
                .foregroundStyle(ok ? Color.celadon : Color.red).fixedSize(horizontal: false, vertical: true)
        default:
            EmptyView()
        }
    }

    private struct ProviderOption { var provider: LLMProvider; var title: String; var cost: String }

    /// 還沒選第一段時：照目前的整理工具推回去（有 claude CLI 就是 Claude…），沒有就「先不用」
    private func guessedUsual(claude: URL?, codex: URL?) -> String {
        switch LLMProvider(rawValue: storedProvider) ?? (claude != nil ? .claude : codex != nil ? .codex : .none) {
        case .claude, .anthropic: return "claude"
        case .codex, .openai: return "chatgpt"
        case .gemini: return "gemini"
        case .openaiCompatible, .enterprise: return "other"
        case .none: return "none"
        }
    }

    /// 還沒選第二段時顯示的選項：設定裡的整理工具（沒選過就是自動選到的 CLI）；沒選過也沒有 CLI 時不選（不是「不整理」）
    private func shownProvider(in list: [ProviderOption]) -> LLMProvider? {
        guard let current = LLMProvider(rawValue: storedProvider) ?? Optional(AppSettings.detectedProvider),
              current != .none || !storedProvider.isEmpty else { return nil }
        return list.contains { $0.provider == current } ? current : nil
    }

    /// 選了第一段：只篩第二段的選項，第一個選項當作選了（不直接寫設定，R45.2）
    private func pick(_ value: String, claude: URL?, codex: URL?) {
        usual = value
        choose(options(for: value, claude: claude, codex: codex).first?.provider)
    }

    private func choose(_ provider: LLMProvider?) {
        guard let provider else { return }
        pending = provider
        setup = .idle
        keyError = nil
        Task { await evaluate(provider) }
    }

    /// 準備好了才寫進設定（R45.2、design §34.3）
    private func evaluate(_ provider: LLMProvider) async {
        switch provider {
        case .none:
            storedProvider = LLMProvider.none.rawValue
        case .claude, .codex:
            setup = .checkingLogin
            let status = await CLILogin.refresh(provider)
            guard pending == provider else { return }
            if status?.loggedIn == false { setup = .notLoggedIn } else { await commit(provider) }
        case .anthropic, .openai, .gemini:
            if Keychain.exists(provider.keychainAccount) { await commit(provider) } else { setup = .needsKey }
        case .openaiCompatible:
            let url = UserDefaults.standard.string(forKey: OpenAICompatibleAPI.baseURLKey) ?? ""
            let model = UserDefaults.standard.string(forKey: provider.modelKey) ?? ""
            if OpenAICompatibleAPI.normalized(url) != nil && !model.isEmpty { await commit(provider) } else { setup = .needsCompat }
        case .enterprise:
            await commit(provider)
        }
    }

    /// 寫進設定並測一次；失敗是設定問題（key 錯誤、沒登入…）就改回原本的設定（R45.2）
    private func commit(_ provider: LLMProvider) async {
        let previous = storedProvider
        storedProvider = provider.rawValue
        setup = .testing
        do {
            let seconds = try await SummarizerTest.run(provider: provider, model: SummarizerTest.model(for: provider))
            guard pending == provider else { return }
            setup = .tested(ok: true, message: String(localized: "可以使用（\(seconds) 秒）"))
        } catch {
            let setupProblem = (error as? LLMError)?.isSetupProblem == true
            if setupProblem && storedProvider == provider.rawValue { storedProvider = previous }
            guard pending == provider else { return }
            setup = .tested(ok: false, message: setupProblem
                ? String(localized: "\(error.localizedDescription)（還沒改用 \(provider.shortLabel)）")
                : String(localized: "\(error.localizedDescription)（之後可以在設定 → 整理工具重新測試）"))
        }
    }

    private func saveKey(_ provider: LLMProvider) {
        do {
            try Keychain.set(provider.keychainAccount, apiKey)
            apiKey = ""
            keyError = nil
        } catch {
            keyError = error.localizedDescription
        }
        model.refreshEnvironment()
        if keyError == nil && pending == provider { Task { await commit(provider) } }
        else if keyError == nil && pending == nil { choose(provider) }
    }

    private func options(for usual: String, claude: URL?, codex: URL?) -> [ProviderOption] {
        let subscription = String(localized: "用你的訂閱，不另外收費")
        let api = String(localized: "要 API key，依用量計費，一小時的會議通常不到台幣 10 元（2026-10 估）")
        switch usual {
        case "claude":
            var list: [ProviderOption] = []
            if claude != nil { list.append(.init(provider: .claude, title: String(localized: "Claude Code（這台 Mac 已經裝了 claude CLI）"), cost: subscription)) }
            list.append(.init(provider: .anthropic, title: String(localized: "Anthropic API"), cost: api))
            return list
        case "chatgpt":
            var list: [ProviderOption] = []
            if codex != nil { list.append(.init(provider: .codex, title: String(localized: "Codex（這台 Mac 已經裝了 codex CLI）"), cost: subscription)) }
            list.append(.init(provider: .openai, title: String(localized: "OpenAI API"), cost: api))
            return list
        case "gemini":
            return [.init(provider: .gemini, title: String(localized: "Gemini API"), cost: api)]
        case "other":
            // 登入了企業伺服器、伺服器有提供整理：排第一個（R51.1）
            let enterprise: [ProviderOption] = EnterpriseServer.shared.account?.llm == true
                ? [.init(provider: .enterprise, title: String(localized: "企業伺服器（\(EnterpriseServer.shared.account?.serverName ?? "")）"),
                         cost: String(localized: "公司提供，不用自己的 key"))] : []
            return enterprise + [.init(provider: .openaiCompatible, title: String(localized: "OpenAI 相容 API"), cost: String(localized: "Groq、OpenRouter、Ollama、LM Studio；網址和 key 到設定填"))]
        default:
            return [.init(provider: .none, title: String(localized: "不整理，只要逐字稿"), cost: String(localized: "隨時可以到設定選"))]
        }
    }

    private var done: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("按「開始使用」後會打開使用說明，介紹錄音、即時逐字稿、會議紀錄和匯出的用法。")
                .fixedSize(horizontal: false, vertical: true)
            // 模型在背景下載的狀態，一行（R44.6）
            if AppSettings.usesEnterpriseTranscription {
                Label("正式轉錄用企業伺服器（\(EnterpriseServer.shared.account?.serverName ?? "")），這台 Mac 不用下載轉錄模型。", systemImage: "checkmark.circle.fill")
                    .foregroundStyle(Color.celadon).fixedSize(horizontal: false, vertical: true)
            } else if !AppSettings.useSystemTranscriber {
                let state = model.models.combinedState(primaryModel)
                switch state {
                case .installed:
                    Label("轉錄模型已經就緒。", systemImage: "checkmark.circle.fill").foregroundStyle(Color.celadon)
                case .downloading(let received, let total):
                    Label(String(localized: "轉錄模型下載中 \(total > 0 ? Int(Double(received) / Double(total) * 100) : 0)%，下載好之前先用 macOS 內建辨識轉錄。"), systemImage: "arrow.down.circle")
                        .foregroundStyle(Color.signal).fixedSize(horizontal: false, vertical: true)
                default:
                    Label("轉錄模型還沒下載好，會先用 macOS 內建辨識轉錄；可以到「設定 → 錄音與轉錄」下載，用模型轉錄比較準。", systemImage: "arrow.down.circle")
                        .foregroundStyle(Color.signal).fixedSize(horizontal: false, vertical: true)
                }
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
