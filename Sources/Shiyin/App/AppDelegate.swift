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

    /// 回到前景時更新 claude、codex 的登入狀態（使用者可能剛在終端機登入，design §34.1）
    func applicationDidBecomeActive(_ notification: Notification) {
        Task { await CLILogin.refreshIfStale() }
    }

    /// 拖到 Dock 圖示、Finder 的「打開方式」（R1.3）；企業伺服器的加入連結 shiyin://join（R49.7）；
    /// AI 工具給的會議連結 shiyin://open（design §44.5）
    func application(_ application: NSApplication, open urls: [URL]) {
        AppModel.shared.start()
        for link in urls where EnterpriseJoin.isJoin(link) { Task { await EnterpriseJoin.handle(link) } }
        for link in urls { if let target = MeetingLink.parse(link) { AppModel.shared.openMeetingLink(target.meetingId, seconds: target.seconds) } }
        let files = urls.filter(\.isFileURL)
        if !files.isEmpty { AppModel.shared.importDirectly(files) }
    }

    /// 關掉主視窗 app 不結束，錄音和工作照常進行（R13.3）
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    /// 結束前讓還開著的編輯器存草稿（design §21.15、R20.6）
    func applicationWillTerminate(_ notification: Notification) {
        NotificationCenter.default.post(name: .flushDrafts, object: nil)
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        if !flag { AppModel.shared.openMainWindow?() }
        return true
    }

    /// 錄音中或有工作在跑時先確認（R11.7、R13.4）
    /// 結束前要問的話（R11.7、R13.4；更新還在下載，design §12，4.7.1）：沒有要問的是 nil。DEBUG 的 quitprompt 也讀這個
    static func terminationPrompt() -> (title: String, info: String)? {
        let model = AppModel.shared
        let recording = model.recorder.isRecording
        let busy = model.runner.isBusy
        // 更新器自己下載完、替換前結束時狀態是「準備安裝」，不會問
        let updating = model.updater.state == .downloading
        guard recording || busy || updating else { return nil }
        let title = recording ? String(localized: "正在錄音，要結束拾音嗎？")
            : busy ? String(localized: "還有工作在進行，要結束拾音嗎？") : String(localized: "更新還在下載，要結束拾音嗎？")
        var info: [String] = []
        if recording { info.append(String(localized: "錄音會先存好，下次開啟拾音時開始轉錄。")) }
        if busy { info.append(String(localized: "進行中的轉錄或整理會中斷，下次開啟時從頭重跑。")) }
        if updating { info.append(String(localized: "更新的下載會中斷，之後再按一次「下載並更新」就好。")) }
        return (title, info.joined(separator: "\n"))
    }

    /// 錄音中、有工作在跑、更新還在下載時先確認
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        let recording = AppModel.shared.recorder.isRecording
        guard let prompt = Self.terminationPrompt() else { return .terminateNow }
        #if DEBUG
        // shiyinctl quit：等於在確認對話框按「結束」
        if DebugControl.confirmQuit { return finishTermination(recording: recording) }
        #endif

        let alert = NSAlert()
        alert.messageText = prompt.title
        alert.informativeText = prompt.info
        alert.addButton(withTitle: String(localized: "結束"))
        alert.addButton(withTitle: String(localized: "取消"))
        NSApp.activate()
        guard alert.runModal() == .alertFirstButtonReturn else { return .terminateCancel }
        return finishTermination(recording: recording)
    }

    /// 送出取消後要等 whisper 的 context 都釋放了再結束：還在 GPU 上算的時候 exit，
    /// 靜態解構會在 ggml_metal_device_free 裡 abort（design §20.12）。最多等 10 秒。
    /// 用 run loop 的 Timer 等，不用 MainActor 的 Task：從主佇列裡呼叫 terminate 時，主佇列不會再執行別的工作
    private func finishTermination(recording: Bool) -> NSApplication.TerminateReply {
        let model = AppModel.shared
        if recording { model.recorder.stop() }
        model.runner.terminateAll()
        guard WhisperContexts.active > 0 else { return .terminateNow }
        let started = Date()
        let timer = Timer(timeInterval: 0.1, repeats: true) { timer in
            let waited = Date().timeIntervalSince(started)
            guard WhisperContexts.active == 0 || waited >= 10 else { return }
            timer.invalidate()
            log.info("terminate: whisper stopped after \(String(format: "%.1f", waited), privacy: .public)s")
            NSApp.reply(toApplicationShouldTerminate: true)
        }
        RunLoop.main.add(timer, forMode: .common)
        return .terminateLater
    }

    /// 空白鍵播放／暫停；游標在輸入框時不攔截（design §9.4）。
    /// ⌘=（美式鍵盤上 + 的那個鍵，不按 Shift）也算「放大文字」：選單的 ⌘+ 要按 Shift 才對得上（design §38.5）
    private func installSpaceKey() {
        keyMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { event in
            if event.modifierFlags.intersection([.command, .option, .control, .shift]) == .command,
               event.charactersIgnoringModifiers == "=" {
                TextSize.change(1)
                return nil
            }
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
