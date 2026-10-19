import AppKit

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private var keyMonitor: Any?

    /// 子程序還沒讀完 stdin 就結束時，寫入會收到 SIGPIPE，預設會結束整個拾音（design §20.1）。
    /// 忽略之後寫入只會失敗，照常讀子程序的結束代碼和錯誤訊息
    func applicationWillFinishLaunching(_ notification: Notification) {
        signal(SIGPIPE, SIG_IGN)
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        AppModel.shared.start()
        installSpaceKey()
        #if DEBUG
        DebugControl.install()
        #endif
    }

    /// 拖到 Dock 圖示、Finder 的「打開方式」（R1.3）
    func application(_ application: NSApplication, open urls: [URL]) {
        AppModel.shared.start()
        AppModel.shared.importDirectly(urls)
    }

    /// 關掉主視窗 app 不結束，錄音和工作照常進行（R13.3）
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        if !flag { AppModel.shared.openMainWindow?() }
        return true
    }

    /// 錄音中或有工作在跑時先確認（R11.7、R13.4）
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        let model = AppModel.shared
        let recording = model.recorder.isRecording
        let busy = model.runner.isBusy
        guard recording || busy else { return .terminateNow }
        #if DEBUG
        // shiyinctl quit：等於在確認對話框按「結束」
        if DebugControl.confirmQuit { return finishTermination(recording: recording) }
        #endif

        let alert = NSAlert()
        alert.messageText = recording ? String(localized: "正在錄音，要結束拾音嗎？") : String(localized: "還有工作在進行，要結束拾音嗎？")
        var info: [String] = []
        if recording { info.append(String(localized: "錄音會先存好，下次開啟拾音時開始轉錄。")) }
        if busy { info.append(String(localized: "進行中的轉錄或整理會中斷，下次開啟時從頭重跑。")) }
        alert.informativeText = info.joined(separator: "\n")
        alert.addButton(withTitle: String(localized: "結束"))
        alert.addButton(withTitle: String(localized: "取消"))
        NSApp.activate()
        guard alert.runModal() == .alertFirstButtonReturn else { return .terminateCancel }
        return finishTermination(recording: recording)
    }

    private func finishTermination(recording: Bool) -> NSApplication.TerminateReply {
        let model = AppModel.shared
        if recording { model.recorder.stop() }
        model.runner.terminateAll()
        return .terminateNow
    }

    /// 空白鍵播放／暫停；游標在輸入框時不攔截（design §9.4）
    private func installSpaceKey() {
        keyMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { event in
            guard event.keyCode == 49,
                  event.modifierFlags.intersection([.command, .option, .control, .shift]).isEmpty,
                  let window = NSApp.keyWindow, window.identifier?.rawValue.hasPrefix("main") == true,
                  !(window.firstResponder is NSText),
                  AppModel.shared.player.isLoaded else { return event }
            AppModel.shared.player.toggle()
            return nil
        }
    }
}
