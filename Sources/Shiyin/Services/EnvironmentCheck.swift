import AVFoundation
import Foundation

/// 環境檢查（設定 → 模型與儲存空間、首次使用引導）。第三版不需要 ffmpeg、Python。
struct EnvironmentStatus: Equatable {
    struct Item: Identifiable, Equatable {
        var id: String { name }
        var name: String
        var ok: Bool
        var detail: String
        var fix: String
        var required: Bool
    }

    var items: [Item]

    @MainActor
    static func check(models: ModelStore) -> EnvironmentStatus {
        let claude = Paths.findTool("claude")
        let codex = Paths.findTool("codex")
        let mic = AVCaptureDevice.authorizationStatus(for: .audio)
        return EnvironmentStatus(items: [
            // 預設開日文會議的人不一定需要中文、英文的模型（目前引擎用的：Qwen3-ASR 或 Breeze，design §32.6）
            Item(name: AppSettings.transcriptionEngine == "breeze" ? String(localized: "轉錄模型 Breeze ASR 25") : String(localized: "轉錄模型 Qwen3-ASR 1.7B"),
                 ok: models.isInstalled(transcriptionFor: .zh) || AppSettings.useSystemTranscriber,
                 detail: models.isInstalled(transcriptionFor: .zh)
                     ? (AppSettings.transcriptionEngine == "breeze" ? String(localized: "已下載（1.66 GB）") : String(localized: "已下載（2.5 GB）")) : AppSettings.useSystemTranscriber ? String(localized: "沒有下載，正式轉錄使用 macOS 內建辨識") : String(localized: "還沒下載"),
                 fix: String(localized: "到「模型與儲存空間」下載，或在「錄音」把正式轉錄改成 macOS 內建"),
                 required: AppSettings.defaultMeetingLanguage != .ja),
            Item(name: String(localized: "日文轉錄模型"), ok: models.isInstalled(.japanese) || AppSettings.useSystemTranscriber,
                 detail: models.isInstalled(.japanese) ? String(localized: "已下載（574 MB）")
                     : AppSettings.useSystemTranscriber ? String(localized: "沒有下載，正式轉錄使用 macOS 內建辨識")
                     : String(localized: "還沒下載，第一次轉錄日文會議時會自動下載"),
                 fix: String(localized: "到「模型與儲存空間」下載"), required: AppSettings.defaultMeetingLanguage == .ja && !AppSettings.useSystemTranscriber),
            Item(name: String(localized: "語言偵測模型"), ok: models.isInstalled(.languageDetection) || !AppSettings.autoDetectLanguage,
                 detail: models.isInstalled(.languageDetection) ? String(localized: "已下載（60 MB）")
                     : AppSettings.autoDetectLanguage ? String(localized: "還沒下載，下載好之前錄音不會自動偵測語言") : String(localized: "沒有開自動偵測會議語言"),
                 fix: String(localized: "到「模型與儲存空間」下載"), required: false),
            Item(name: String(localized: "說話者辨識模型"), ok: models.diarizerState == .installed,
                 detail: models.diarizerState == .installed ? String(localized: "已下載") : String(localized: "還沒下載"), fix: String(localized: "到「模型與儲存空間」下載"), required: false),
            Item(name: String(localized: "麥克風權限"), ok: mic == .authorized,
                 detail: mic == .authorized ? String(localized: "已允許") : mic == .notDetermined ? String(localized: "還沒詢問") : String(localized: "被拒絕"),
                 fix: String(localized: "第一次錄音時會詢問；被拒絕的話到「系統設定 → 隱私權與安全性 → 麥克風」打開拾音"), required: false),
            Item(name: "claude CLI", ok: claude != nil, detail: claude?.path ?? String(localized: "找不到"),
                 fix: String(localized: "整理工具選 Claude 時需要：安裝 Claude Code 並在終端機執行 claude 登入"), required: false),
            Item(name: "codex CLI", ok: codex != nil, detail: codex?.path ?? String(localized: "找不到"),
                 fix: String(localized: "整理工具選 Codex 時需要：安裝 Codex CLI 並執行 codex login"), required: false),
            Item(name: "Anthropic API key", ok: Keychain.exists("anthropic"),
                 detail: Keychain.exists("anthropic") ? String(localized: "已儲存在鑰匙圈") : String(localized: "沒有填"), fix: String(localized: "整理工具選 Anthropic API 時需要"), required: false),
            Item(name: "OpenAI API key", ok: Keychain.exists("openai"),
                 detail: Keychain.exists("openai") ? String(localized: "已儲存在鑰匙圈") : String(localized: "沒有填"), fix: String(localized: "整理工具選 OpenAI API 時需要"), required: false),
            Item(name: "Gemini API key", ok: Keychain.exists("gemini"),
                 detail: Keychain.exists("gemini") ? String(localized: "已儲存在鑰匙圈") : String(localized: "沒有填"), fix: String(localized: "整理工具選 Gemini API 時需要"), required: false),
            compatibleItem(),
        ])
    }

    /// OpenAI 相容 API：有沒有填網址和模型（design §19.4）
    @MainActor
    private static func compatibleItem() -> Item {
        let url = OpenAICompatibleAPI.normalized(OpenAICompatibleAPI.savedBaseURL)
        let model = UserDefaults.standard.string(forKey: LLMProvider.openaiCompatible.modelKey) ?? ""
        let ok = url != nil && !model.isEmpty
        return Item(name: String(localized: "OpenAI 相容 API"), ok: ok,
                    detail: ok ? "\(url?.host(percentEncoded: false) ?? "")・\(model)" : String(localized: "沒有填網址或模型"),   // l10n-skip
                    fix: String(localized: "整理工具選 OpenAI 相容 API 時需要：到「設定 → 整理工具」填網址和模型"), required: false)
    }
}
