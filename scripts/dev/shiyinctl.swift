// 驗證用：送指令給 DEBUG 建置的拾音（見 Sources/Shiyin/Debug/DebugControl.swift）。
// 編譯：swiftc scripts/dev/shiyinctl.swift -o ~/Library/Caches/ShiyinBuild/shiyinctl
// 用法：shiyinctl state /tmp/state.json、shiyinctl cancel 3、shiyinctl replace 1 錯字 正字 1 /tmp/r.json
import Foundation

let args = Array(CommandLine.arguments.dropFirst())
guard !args.isEmpty else {
    FileHandle.standardError.write(Data("用法：shiyinctl <指令> [參數…]\n".utf8))
    exit(1)
}
DistributedNotificationCenter.default().postNotificationName(
    Notification.Name("com.weiwei.shiyin.debug"), object: args.joined(separator: "\u{1F}"),
    userInfo: nil, deliverImmediately: true)
RunLoop.current.run(until: Date().addingTimeInterval(0.2))
