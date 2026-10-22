import Foundation

/// 拾音所有檔案的位置（design §3）。資料一律放 SHIYIN_HOME，不放專案資料夾。
enum Paths {
    static let home: URL = {
        if let custom = ProcessInfo.processInfo.environment["SHIYIN_HOME"], !custom.isEmpty {
            return URL(fileURLWithPath: (custom as NSString).expandingTildeInPath, isDirectory: true)
        }
        return FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Shiyin", isDirectory: true)
    }()

    static var modelsDir: URL { home.appendingPathComponent("models", isDirectory: true) }
    static var backupsDir: URL { home.appendingPathComponent("backups", isDirectory: true) }
    /// 第二版留下的 Python 環境與 mlx 模型，第三版用不到（R10.5）
    static var legacyItems: [URL] {
        [home.appendingPathComponent("venv", isDirectory: true),
         home.appendingPathComponent("models/breeze-asr-25", isDirectory: true)]
    }
    static var audioDir: URL { home.appendingPathComponent("audio", isDirectory: true) }
    static var recordingDir: URL { audioDir.appendingPathComponent("recording", isDirectory: true) }
    static var database: URL { home.appendingPathComponent("shiyin.sqlite") }
    static var tmpDir: URL { home.appendingPathComponent("tmp", isDirectory: true) }
    static var logsDir: URL { home.appendingPathComponent("logs", isDirectory: true) }

    /// 從 Finder 開的 app 只有系統 PATH，外部工具一律在這些目錄找
    static let toolDirectories: [String] = [
        "/opt/homebrew/bin", "/usr/local/bin",
        (NSHomeDirectory() as NSString).appendingPathComponent(".local/bin"),
    ]

    static func findTool(_ name: String) -> URL? {
        #if DEBUG
        // -debugNoCLI YES：當成沒有安裝 claude、codex，在裝了 CLI 的電腦上測（design §19.6）
        if ["claude", "codex"].contains(name) && UserDefaults.standard.bool(forKey: "debugNoCLI") { return nil }
        #endif
        for dir in toolDirectories {
            let path = (dir as NSString).appendingPathComponent(name)
            if FileManager.default.isExecutableFile(atPath: path) { return URL(fileURLWithPath: path) }
        }
        return nil
    }

    /// 子程序用的環境變數：補上工具目錄到 PATH
    static func subprocessEnvironment(extra: [String: String] = [:]) -> [String: String] {
        var env = ProcessInfo.processInfo.environment
        let current = env["PATH"] ?? "/usr/bin:/bin:/usr/sbin:/sbin"
        env["PATH"] = (toolDirectories + [current]).joined(separator: ":")
        env.merge(extra) { _, new in new }
        return env
    }

    static func ensureDirectories() throws {
        let fm = FileManager.default
        for dir in [home, audioDir, recordingDir, tmpDir, logsDir, modelsDir, backupsDir] {
            try fm.createDirectory(at: dir, withIntermediateDirectories: true)
        }
        try fm.setAttributes([.posixPermissions: 0o700], ofItemAtPath: home.path)
    }

    /// 在 tmp/ 底下建立一個用完即刪的資料夾
    static func makeTempDirectory() throws -> URL {
        let dir = tmpDir.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }
}
