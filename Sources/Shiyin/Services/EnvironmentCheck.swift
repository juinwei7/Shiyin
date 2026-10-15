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
            Item(name: "轉錄模型 Breeze ASR 25", ok: models.isReady || AppSettings.useSystemTranscriber,
                 detail: models.isReady ? "已下載（1.66 GB）" : AppSettings.useSystemTranscriber ? "沒有下載，正式轉錄使用 macOS 內建辨識" : "還沒下載",
                 fix: "到「模型與儲存空間」下載，或在「錄音」把正式轉錄改成 macOS 內建", required: true),
            Item(name: "說話者辨識模型", ok: models.diarizerState == .installed,
                 detail: models.diarizerState == .installed ? "已下載" : "還沒下載", fix: "到「模型與儲存空間」下載", required: false),
            Item(name: "麥克風權限", ok: mic == .authorized,
                 detail: mic == .authorized ? "已允許" : mic == .notDetermined ? "還沒詢問" : "被拒絕",
                 fix: "第一次錄音時會詢問；被拒絕的話到「系統設定 → 隱私權與安全性 → 麥克風」打開拾音", required: false),
            Item(name: "claude CLI", ok: claude != nil, detail: claude?.path ?? "找不到",
                 fix: "整理工具選 Claude 時需要：安裝 Claude Code 並在終端機執行 claude 登入", required: false),
            Item(name: "codex CLI", ok: codex != nil, detail: codex?.path ?? "找不到",
                 fix: "整理工具選 Codex 時需要：安裝 Codex CLI 並執行 codex login", required: false),
            Item(name: "Anthropic API key", ok: Keychain.get("anthropic") != nil,
                 detail: Keychain.get("anthropic") != nil ? "已儲存在鑰匙圈" : "沒有填", fix: "整理工具選 Anthropic API 時需要", required: false),
            Item(name: "OpenAI API key", ok: Keychain.get("openai") != nil,
                 detail: Keychain.get("openai") != nil ? "已儲存在鑰匙圈" : "沒有填", fix: "整理工具選 OpenAI API 時需要", required: false),
        ])
    }
}
