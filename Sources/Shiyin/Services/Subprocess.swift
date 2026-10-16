import Foundation

struct SubprocessResult {
    var status: Int32
    var reason: Process.TerminationReason
    var stdout: Data
    var stderr: String

    var succeeded: Bool { reason == .exit && status == 0 }

    /// 錯誤訊息只取 stderr 的結尾，不帶逐字稿內容（design §4.1、§6.2）
    func stderrTail(_ limit: Int) -> String {
        let trimmed = stderr.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.count > limit ? "…" + String(trimmed.suffix(limit)) : trimmed
    }
}

enum SubprocessError: LocalizedError {
    case timeout(Int)

    var errorDescription: String? {
        switch self {
        case .timeout(let minutes): return String(localized: "執行超過 \(minutes) 分鐘，已停止")
        }
    }
}

/// 執行外部程式。一律用參數陣列，不經過 shell（N1）。
/// stdout 可以逐行回呼（轉錄進度），stderr 全部收集。
final class Subprocess: @unchecked Sendable {
    let process = Process()
    private let lock = NSLock()
    private var lineBuffer = Data()
    private var stdoutData = Data()
    private var stderrData = Data()
    private var onLine: (@Sendable (String) -> Void)?
    private var timedOut = false

    init(_ executable: URL, _ arguments: [String], environment: [String: String]? = nil, directory: URL? = nil) {
        process.executableURL = executable
        process.arguments = arguments
        process.environment = environment ?? Paths.subprocessEnvironment()
        if let directory { process.currentDirectoryURL = directory }
    }

    func run(input: Data? = nil, timeout: TimeInterval? = nil,
             onLine: (@Sendable (String) -> Void)? = nil) async throws -> SubprocessResult {
        self.onLine = onLine
        let outPipe = Pipe(), errPipe = Pipe()
        process.standardOutput = outPipe
        process.standardError = errPipe
        let inPipe = input == nil ? nil : Pipe()
        process.standardInput = inPipe ?? FileHandle.nullDevice

        let group = DispatchGroup()
        group.enter()
        DispatchQueue.global().async {
            let handle = outPipe.fileHandleForReading
            while true {
                let chunk = handle.availableData
                if chunk.isEmpty { break }
                self.appendStdout(chunk)
            }
            self.flushLine()
            group.leave()
        }
        group.enter()
        DispatchQueue.global().async {
            let data = errPipe.fileHandleForReading.readDataToEndOfFile()
            self.lock.withLock { self.stderrData = data }
            group.leave()
        }

        return try await withCheckedThrowingContinuation { continuation in
            process.terminationHandler = { process in
                group.notify(queue: .global()) {
                    let result = self.lock.withLock {
                        SubprocessResult(status: process.terminationStatus, reason: process.terminationReason,
                                         stdout: self.stdoutData,
                                         stderr: String(decoding: self.stderrData, as: UTF8.self))
                    }
                    if self.lock.withLock({ self.timedOut }), let timeout {
                        continuation.resume(throwing: SubprocessError.timeout(Int(timeout / 60)))
                    } else {
                        continuation.resume(returning: result)
                    }
                }
            }
            do {
                try process.run()
            } catch {
                process.terminationHandler = nil
                try? outPipe.fileHandleForWriting.close()
                try? errPipe.fileHandleForWriting.close()
                continuation.resume(throwing: error)
                return
            }
            if let input, let inPipe {
                DispatchQueue.global().async {
                    try? inPipe.fileHandleForWriting.write(contentsOf: input)
                    try? inPipe.fileHandleForWriting.close()
                }
            }
            if let timeout {
                DispatchQueue.global().asyncAfter(deadline: .now() + timeout) { [weak self] in
                    guard let self, self.process.isRunning else { return }
                    self.lock.withLock { self.timedOut = true }
                    self.terminate()
                }
            }
        }
    }

    /// 先送 SIGTERM，3 秒內沒結束就 SIGKILL（design §4）
    func terminate() {
        guard process.isRunning else { return }
        process.terminate()
        let pid = process.processIdentifier
        DispatchQueue.global().asyncAfter(deadline: .now() + 3) { [weak self] in
            if self?.process.isRunning == true { kill(pid, SIGKILL) }
        }
    }

    private func appendStdout(_ chunk: Data) {
        var lines: [String] = []
        lock.withLock {
            stdoutData.append(chunk)
            guard onLine != nil else { return }
            lineBuffer.append(chunk)
            while let newline = lineBuffer.firstIndex(of: 0x0A) {
                lines.append(String(decoding: lineBuffer[lineBuffer.startIndex..<newline], as: UTF8.self))
                lineBuffer.removeSubrange(lineBuffer.startIndex...newline)
            }
        }
        lines.forEach { onLine?($0) }
    }

    private func flushLine() {
        let rest: String? = lock.withLock {
            guard onLine != nil, !lineBuffer.isEmpty else { return nil }
            defer { lineBuffer.removeAll() }
            return String(decoding: lineBuffer, as: UTF8.self)
        }
        if let rest { onLine?(rest) }
    }
}
