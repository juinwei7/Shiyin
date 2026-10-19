import Foundation

/// 目前有幾個 whisper context 還沒釋放（轉錄、語言偵測）。結束 app 時要等它歸零：
/// whisper.cpp 還在 GPU 上算的時候 exit，靜態解構會在 ggml_metal_device_free 裡 abort（design §20.12）
enum WhisperContexts {
    private static let lock = NSLock()
    nonisolated(unsafe) private static var count = 0

    static func opened() { lock.withLock { count += 1 } }
    static func closed() { lock.withLock { count -= 1 } }
    static var active: Int { lock.withLock { count } }
}
