import CryptoKit
import Foundation
import Observation

/// 轉錄模型的下載、續傳、驗證（design §7、R10.3）
@MainActor
@Observable
final class ModelStore: NSObject {
    struct ModelInfo: Sendable {
        let name: String
        let filename: String
        let url: URL
        let size: Int64
        let sha256: String
    }

    enum State: Equatable {
        case missing
        case downloading(received: Int64, total: Int64)
        case paused(received: Int64, total: Int64)
        case verifying
        case installed
        case failed(String)

        var isBusy: Bool {
            switch self {
            case .downloading, .verifying: return true
            default: return false
            }
        }
    }

    /// 社群轉檔的 Breeze ASR 25 GGML q8_0，固定 commit（design §7）
    nonisolated static let breeze = ModelInfo(
        name: "Breeze ASR 25（q8_0）",
        filename: "breeze-asr-25-q8_0.bin",
        url: URL(string: "https://huggingface.co/shdennlin/breeze-asr-25-ggml/resolve/36c726093efe1760d1dd39c3cfe8b6a7282437d1/ggml-breeze-asr-25-q8_0.bin")!,
        size: 1_656_129_691,
        sha256: "d4b187c40ffbf1f620734b77821e2ca8a97c8ecf5754d02b2a175069348cafcf")

    nonisolated static var breezePath: URL { Paths.modelsDir.appendingPathComponent(breeze.filename) }
    nonisolated private static var resumeDataPath: URL { Paths.modelsDir.appendingPathComponent(".\(breeze.filename).resume") }

    private(set) var state: State = .missing
    private(set) var diarizerState: State = .missing
    @ObservationIgnored private var session: URLSession!
    @ObservationIgnored private var task: URLSessionDownloadTask?

    override init() {
        super.init()
        session = URLSession(configuration: .default, delegate: self, delegateQueue: .main)
        refresh()
    }

    var isReady: Bool { state == .installed }

    func refresh() {
        let fm = FileManager.default
        if !state.isBusy {
            if let size = (try? fm.attributesOfItem(atPath: Self.breezePath.path))?[.size] as? Int64, size == Self.breeze.size {
                state = .installed
            } else if fm.fileExists(atPath: Self.resumeDataPath.path) {
                state = .paused(received: 0, total: Self.breeze.size)
            } else {
                state = .missing
            }
        }
        if !diarizerState.isBusy { diarizerState = Diarizer.isInstalled ? .installed : .missing }
    }

    // MARK: Breeze

    func start() {
        guard !state.isBusy, state != .installed else { return }
        try? Paths.ensureDirectories()
        if let resume = try? Data(contentsOf: Self.resumeDataPath) {
            task = session.downloadTask(withResumeData: resume)
        } else {
            task = session.downloadTask(with: Self.breeze.url)
        }
        state = .downloading(received: 0, total: Self.breeze.size)
        task?.resume()
        log.info("model download started")
    }

    func pause() {
        guard case .downloading(let received, let total) = state, let task else { return }
        task.cancel { [weak self] data in
            Task { @MainActor in
                if let data { try? data.write(to: Self.resumeDataPath) }
                self?.state = .paused(received: received, total: total)
            }
        }
        self.task = nil
    }

    func delete() {
        task?.cancel()
        task = nil
        try? FileManager.default.removeItem(at: Self.breezePath)
        try? FileManager.default.removeItem(at: Self.resumeDataPath)
        state = .missing
    }

    // MARK: 說話者辨識模型（由 FluidAudio 下載，見 design §6.3）

    func prepareDiarizer() {
        guard !diarizerState.isBusy else { return }
        diarizerState = .downloading(received: 0, total: 0)
        Task {
            do {
                _ = try await Diarizer.prepare()
                diarizerState = .installed
            } catch {
                diarizerState = .failed(error.localizedDescription)
            }
        }
    }

    private func finish(downloadedFile: URL) {
        state = .verifying
        let destination = Self.breezePath
        Task.detached(priority: .utility) {
            let result = Result { try Self.verify(downloadedFile) }
            await MainActor.run {
                switch result {
                case .success(true):
                    try? FileManager.default.removeItem(at: destination)
                    do {
                        try FileManager.default.moveItem(at: downloadedFile, to: destination)
                        try? FileManager.default.removeItem(at: Self.resumeDataPath)
                        self.state = .installed
                        log.info("model installed")
                        AppModel.shared.runner.wake()
                    } catch {
                        self.state = .failed("無法存放模型：\(error.localizedDescription)")
                    }
                case .success(false):
                    try? FileManager.default.removeItem(at: downloadedFile)
                    try? FileManager.default.removeItem(at: Self.resumeDataPath)
                    self.state = .failed("下載的模型檔驗證失敗（SHA-256 不符），請重新下載")
                case .failure(let error):
                    self.state = .failed("驗證模型失敗：\(error.localizedDescription)")
                }
            }
        }
    }

    /// 串流計算 SHA-256，不一次讀進整個 1.6 GB 檔案
    nonisolated static func verify(_ file: URL) throws -> Bool {
        try sha256(file) == breeze.sha256
    }

    /// 檔案的 SHA-256（小寫十六進位）；更新器驗證下載的 DMG 也用這個
    nonisolated static func sha256(_ file: URL) throws -> String {
        let handle = try FileHandle(forReadingFrom: file)
        defer { try? handle.close() }
        var hasher = SHA256()
        while let chunk = try handle.read(upToCount: 8 << 20), !chunk.isEmpty {
            hasher.update(data: chunk)
        }
        return hasher.finalize().map { String(format: "%02x", $0) }.joined()
    }
}

extension ModelStore: URLSessionDownloadDelegate {
    nonisolated func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didWriteData bytesWritten: Int64,
                                totalBytesWritten: Int64, totalBytesExpectedToWrite: Int64) {
        MainActor.assumeIsolated {
            guard case .downloading = state else { return }
            state = .downloading(received: totalBytesWritten,
                                 total: totalBytesExpectedToWrite > 0 ? totalBytesExpectedToWrite : Self.breeze.size)
        }
    }

    nonisolated func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didFinishDownloadingTo location: URL) {
        // location 在這個方法結束後就會被刪掉，先搬到 tmp
        let kept = Paths.tmpDir.appendingPathComponent("model-\(UUID().uuidString).bin")
        do {
            try FileManager.default.moveItem(at: location, to: kept)
            MainActor.assumeIsolated { finish(downloadedFile: kept) }
        } catch {
            MainActor.assumeIsolated { state = .failed("無法存放下載的檔案：\(error.localizedDescription)") }
        }
    }

    nonisolated func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        guard let error = error as NSError? else { return }
        let resume = error.userInfo[NSURLSessionDownloadTaskResumeData] as? Data
        MainActor.assumeIsolated {
            if error.code == NSURLErrorCancelled { return }  // 暫停或刪除
            if let resume { try? resume.write(to: Self.resumeDataPath) }
            self.task = nil
            state = .failed("下載中斷：\(error.localizedDescription)。按「繼續下載」會從中斷的地方接著下載")
        }
    }
}
