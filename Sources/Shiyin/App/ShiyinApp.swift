import SwiftUI

@main
struct ShiyinApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate

    var body: some Scene {
        Window("拾音", id: "main") {
            RootView()
                .environment(AppModel.shared)
                .frame(minWidth: 960, minHeight: 620)
                .chineseGlyphs()
        }
        .defaultSize(width: 1180, height: 780)
        .defaultLaunchBehavior(.presented)
        .commands { AppCommands(model: AppModel.shared) }

        Window("歡迎使用拾音", id: "onboarding") {
            OnboardingView()
                .environment(AppModel.shared)
                .chineseGlyphs()
        }
        .windowResizability(.contentSize)
        .defaultLaunchBehavior(.suppressed)

        Window("拾音使用說明", id: "guide") {
            GuideView()
                .tint(.celadon)
                .frame(minWidth: 520, minHeight: 480)
                .chineseGlyphs()
        }
        .defaultSize(width: 680, height: 780)
        .defaultLaunchBehavior(.suppressed)

        Settings {
            SettingsView()
                .environment(AppModel.shared)
                .chineseGlyphs()
        }

        MenuBarExtra {
            MenuBarContent()
                .environment(AppModel.shared)
                .chineseGlyphs()
        } label: {
            MenuBarLabel()
                .environment(AppModel.shared)
        }
        .menuBarExtraStyle(.window)
    }
}

struct AppCommands: Commands {
    let model: AppModel
    @Environment(\.openWindow) private var openWindow

    var body: some Commands {
        CommandGroup(after: .appInfo) {
            Button("檢查更新…") { Task { await model.checkForUpdatesFromMenu() } }
        }
        CommandGroup(replacing: .newItem) {
            Button("匯入…") { model.showFileImporter = true }
                .keyboardShortcut("o")
            Button(model.recorder.isRecording ? "停止錄音" : "開始錄音") { model.toggleRecording() }
                .keyboardShortcut("r")
            Button(model.recorder.isPaused ? "繼續錄音" : "暫停錄音") {
                model.recorder.isPaused ? model.recorder.resume() : model.recorder.pause()
            }
            .keyboardShortcut("p", modifiers: [.command, .shift])
            .disabled(!model.recorder.isRecording)
            Button("標記重點") {
                model.addBookmark(at: model.recorder.elapsed, meetingId: model.recorder.meetingId)
            }
            .keyboardShortcut("b")
            .disabled(!model.recorder.isRecording)
            Divider()
            Button("匯出…") { model.showExportSheet = true }
                .keyboardShortcut("e")
                .disabled(model.detail == nil)
        }
        CommandGroup(replacing: .help) {
            Button("拾音使用說明") { openWindow(id: "guide") }
                .keyboardShortcut("?", modifiers: .command)
        }
        CommandMenu("會議") {
            ForEach(Array(DetailTab.allCases.enumerated()), id: \.element) { index, tab in
                Button(tab.label) { model.tab = tab }
                    .keyboardShortcut(KeyEquivalent(Character("\(index + 1)")))
                    .disabled(model.detail == nil)
            }
            Divider()
            Button("播放／暫停") { model.player.toggle() }
                .disabled(!model.player.isLoaded)
            Divider()
            Button("重新整理會議紀錄") { model.resummarize() }
                .disabled(model.detail?.segments.isEmpty ?? true)
            Button("復原上一版會議紀錄") { model.undoNotes() }
                .disabled((model.detail?.versionCount ?? 0) == 0)
            Divider()
            Button("複製會議紀錄（富文字）") { model.copyNotes(.rich) }
                .disabled(model.detail == nil)
            Button("複製會議紀錄（Markdown）") { model.copyNotes(.markdown) }
                .disabled(model.detail == nil)
            Divider()
            Button("移到垃圾桶…") { model.confirmDelete = true }
                .keyboardShortcut(.delete)
                .disabled(model.detail == nil || model.showTrash)
        }
    }
}

extension View {
    /// 會議內容裡的中文一律用繁體中文的字形（design §16.2）：英文介面時系統會改用簡體的字型，
    /// 全形逗號、問號的位置跟繁體不同。只影響中文字的字形，英文不受影響
    func chineseGlyphs() -> some View {
        typesettingLanguage(Locale.Language(identifier: "zh-Hant"))
    }
}
