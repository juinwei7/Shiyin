import CryptoKit
import Foundation

/// 轉錄交給企業伺服器時，畫面上進度後面的說明（排隊第幾、為什麼在等）；有值就顯示「改在這台電腦轉錄」（R50.3、R50.5）
struct ServerNote: Equatable {
    var text: String
    /// 在等伺服器（連不上、登入失效）：不顯示百分比
    var waiting = false
}

/// 用企業伺服器轉錄（design §36.3、R50）：寫成 16-bit WAV → 建工作 → 分塊上傳（可以接著傳）→ 等結果 → 刪掉伺服器上的工作。
/// 說話者辨識、聲紋在 Mac 本機做（D122），這裡只拿逐字稿的段落。
/// 連不上、伺服器忙、登入失效時不算失敗：一直等、自動再試（R50.5、R49.4），使用者可以按「改在這台電腦轉錄」
@MainActor
struct ServerTranscriber {
    struct Remote {
        var segments: [TranscribedSegment]
        var seams: [Double]
        /// 伺服器用的引擎：breeze、qwen、japanese（寫進 transcribed_with）
        var engine: String
    }

    /// 使用者按了「改在這台電腦轉錄」
    struct SwitchToLocal: Error {}

    /// 伺服器轉錄失敗（例如錄音太長）：不是暫時的問題，工作算失敗
    struct Failed: LocalizedError {
        var message: String
        var errorDescription: String? { message }
    }

    var options: TranscriptionOptions
    var stage: (String) -> Void
    /// 0～1：上傳 0～0.1、伺服器轉錄 0.1～0.9（之後的 0.9～1 是本機的說話者辨識）
    var progress: @Sendable (Double) -> Void
    var shouldContinue: () -> Bool
    var switchToLocal: () -> Bool
    /// 伺服器的狀態（nil：伺服器的部分做完了）
    var onServer: (ServerNote?) -> Void
    /// 伺服器的工作 id 換了（建立、完成、重建）：JobRunner 記進工作的 payload，app 重開時接著做
    var onRemoteId: (String?) -> Void

    static let chunkSize = 8 * 1024 * 1024
    private var server: EnterpriseServer { .shared }

    // MARK: WAV

    /// 16 kHz、16-bit、單聲道的 WAV（伺服器只收這種，D120）；邊換算邊寫，不在記憶體多留一份
    nonisolated static func writeWAV(_ samples: [Float], to url: URL) throws -> (bytes: Int64, sha256: String) {
        let dataBytes = samples.count * 2
        var header = Data()
        func append<T: FixedWidthInteger>(_ value: T) { withUnsafeBytes(of: value.littleEndian) { header.append(contentsOf: $0) } }
        header.append(contentsOf: Array("RIFF".utf8)); append(UInt32(36 + dataBytes)); header.append(contentsOf: Array("WAVE".utf8))
        header.append(contentsOf: Array("fmt ".utf8)); append(UInt32(16)); append(UInt16(1)); append(UInt16(1))
        append(UInt32(16_000)); append(UInt32(32_000)); append(UInt16(2)); append(UInt16(16))
        header.append(contentsOf: Array("data".utf8)); append(UInt32(dataBytes))
        FileManager.default.createFile(atPath: url.path, contents: nil)
        let handle = try FileHandle(forWritingTo: url)
        defer { try? handle.close() }
        var hasher = SHA256()
        try handle.write(contentsOf: header)
        hasher.update(data: header)
        let block = 160_000
        var index = 0
        while index < samples.count {
            let end = min(index + block, samples.count)
            var chunk = Data(count: (end - index) * 2)
            chunk.withUnsafeMutableBytes { raw in
                let out = raw.bindMemory(to: Int16.self)
                for i in index..<end {
                    let v = max(-1, min(1, samples[i])) * 32767
                    out[i - index] = Int16(v.rounded()).littleEndian
                }
            }
            try handle.write(contentsOf: chunk)
            hasher.update(data: chunk)
            index = end
        }
        let digest = hasher.finalize().map { String(format: "%02x", $0) }.joined()
        return (Int64(header.count + dataBytes), digest)
    }

    // MARK: 流程

    /// 回傳 nil 表示被取消（伺服器上的工作也刪掉）
    func transcribe(wav: URL, bytes: Int64, sha256: String, remoteId: String?) async throws -> Remote? {
        var id = remoteId
        var waited = 0
        var rejectedUploads = 0
        onServer(ServerNote(text: ""))
        defer { onServer(nil) }
        while true {
            guard shouldContinue() else { await delete(id); return nil }
            if switchToLocal() { await delete(id); onRemoteId(nil); throw SwitchToLocal() }
            do {
                if id == nil {
                    id = try await create(bytes: bytes, sha256: sha256)
                    onRemoteId(id)
                }
                let state = try await status(id!)
                if waited > 0 { onServer(ServerNote(text: "")); waited = 0 }
                switch state.status {
                case "uploading":
                    if try await upload(id!, wav: wav, bytes: bytes) == false {
                        // 伺服器說收到的檔案不對，從頭再傳；連續兩次就放棄
                        rejectedUploads += 1
                        if rejectedUploads > 2 { throw Failed(message: String(localized: "錄音一直沒辦法完整傳到企業伺服器")) }
                    }
                case "queued", "running":
                    report(state)
                    try await Task.sleep(for: .seconds(state.status == "running" ? 2 : 5))
                case "done":
                    progress(0.9)
                    let remote = try await result(id!)
                    await delete(id)
                    onRemoteId(nil)
                    return remote
                case "failed":
                    await delete(id)
                    onRemoteId(nil)
                    if state.errorKind == "empty" { throw WhisperError.empty }
                    throw Failed(message: state.errorMessage ?? String(localized: "企業伺服器轉錄失敗"))
                default:
                    // 取消、過期（伺服器清掉了）：重新建一個工作，從頭上傳
                    id = nil
                    onRemoteId(nil)
                }
            } catch let failure as EnterpriseServer.Failure where failure.isTemporary || failure.isSetupProblem {
                if case .server(let kind, _) = failure, kind == "not_found" {
                    id = nil
                    onRemoteId(nil)
                    continue
                }
                waited += 1
                try await waitForServer(failure, attempt: waited)
            } catch let failure as EnterpriseServer.Failure {
                if case .server(let kind, _) = failure, kind == "not_found" {
                    id = nil
                    onRemoteId(nil)
                    continue
                }
                await delete(id)
                onRemoteId(nil)
                throw Failed(message: failure.errorDescription ?? "")
            }
        }
    }

    /// 等伺服器（R50.5）：30 秒、1 分鐘、2 分鐘…最長 5 分鐘再試；登入失效時等使用者重新登入（每 5 秒看一次）
    private func waitForServer(_ failure: EnterpriseServer.Failure, attempt: Int) async throws {
        let setup = failure.isSetupProblem
        let seconds = setup ? 5 : min(300, 30 * (1 << min(attempt - 1, 4)))
        let text: String
        if setup { text = failure.errorDescription ?? "" }
        else if case .server(let kind, _) = failure { text = kind == "busy" ? String(localized: "等企業伺服器：伺服器忙，\(seconds) 秒後再試")
                                                                            : String(localized: "等企業伺服器：伺服器出錯，\(seconds) 秒後再試") }
        else { text = String(localized: "等企業伺服器：連不上，\(seconds) 秒後再試") }
        onServer(ServerNote(text: text, waiting: true))
        stage(String(localized: "等企業伺服器"))
        log.info("waiting for enterprise server: \(failure.isSetupProblem ? "login" : "unreachable", privacy: .public)")
        for _ in 0..<seconds {
            try await Task.sleep(for: .seconds(1))
            guard shouldContinue() else { return }
            if switchToLocal() { return }
            // 重新登入了就馬上再試
            if setup && server.isSignedIn { return }
        }
    }

    /// 階段的文字會接「中 N%」（「企業伺服器轉錄中 42%」），排隊第幾放在後面的說明
    private func report(_ state: JobState) {
        if state.status == "queued" {
            stage(String(localized: "企業伺服器排隊"))
            if state.stage == "waiting_models" { onServer(ServerNote(text: String(localized: "伺服器正在準備模型"))) }
            else if let ahead = state.queueAhead, ahead > 0 { onServer(ServerNote(text: String(localized: "前面還有 \(ahead) 場"))) }
            else { onServer(ServerNote(text: "")) }
            progress(0.1)
        } else {
            stage(String(localized: "企業伺服器轉錄"))
            onServer(ServerNote(text: ""))
            progress(0.1 + 0.8 * (state.progress ?? 0))
        }
    }

    // MARK: API（M§36.2）

    struct JobState {
        var status: String
        var queueAhead: Int?
        var stage: String?
        var progress: Double?
        var errorKind: String?
        var errorMessage: String?
    }

    /// 建工作時送的 settings（M§36.2；標準答案 server/settings.json）：說話者在本機辨識（D122），分聲道不送（伺服器收的是混好的單聲道）
    nonisolated static func settings(language: MeetingLanguage, glossary: [String], audioSource: String, minSpeakers: Int?) -> [String: Any] {
        var settings: [String: Any] = ["language": language.rawValue, "glossary": glossary, "diarize": false,
                                       "splitChannels": false, "audioSource": audioSource]
        if let minSpeakers { settings["minSpeakers"] = minSpeakers }
        return settings
    }

    private func create(bytes: Int64, sha256: String) async throws -> String {
        let settings = Self.settings(language: options.language, glossary: options.glossary, audioSource: options.audioSource,
                                     minSpeakers: options.minSpeakers)
        let (data, response) = try await server.call("POST", "api/v1/transcriptions", json: [
            "settings": settings,
            "audio": ["format": "wav", "sampleRate": 16000, "channels": 1, "bytes": bytes, "sha256": sha256],
        ])
        guard response.statusCode == 201, let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let id = json["id"] as? String else { throw EnterpriseServer.failure(data, response) }
        log.info("enterprise job created")
        return id
    }

    private func status(_ id: String) async throws -> JobState {
        let (data, response) = try await server.call("GET", "api/v1/transcriptions/\(id)", timeout: 30)
        guard response.statusCode == 200, let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let status = json["status"] as? String else { throw EnterpriseServer.failure(data, response) }
        let error = json["error"] as? [String: Any]
        return JobState(status: status, queueAhead: json["queueAhead"] as? Int, stage: json["stage"] as? String,
                      progress: json["progress"] as? Double, errorKind: error?["kind"] as? String, errorMessage: error?["message"] as? String)
    }

    /// 從伺服器收到的地方接著傳（HEAD 問位置），一次 8 MB。回傳 false 表示伺服器說檔案不對、要從頭傳
    private func upload(_ id: String, wav: URL, bytes: Int64) async throws -> Bool {
        let (_, head) = try await server.call("HEAD", "api/v1/transcriptions/\(id)/audio", timeout: 30)
        guard head.statusCode == 200 else { throw EnterpriseServer.Failure.server(kind: "not_found", message: "") }
        var offset = Int64(head.value(forHTTPHeaderField: "Upload-Offset") ?? "0") ?? 0
        let file = try FileHandle(forReadingFrom: wav)
        defer { try? file.close() }
        stage(String(localized: "上傳錄音"))
        while offset < bytes {
            guard shouldContinue(), !switchToLocal() else { return true }
            try file.seek(toOffset: UInt64(offset))
            let chunk = try file.read(upToCount: Self.chunkSize) ?? Data()
            if chunk.isEmpty { break }
            let (data, response) = try await server.call("PATCH", "api/v1/transcriptions/\(id)/audio", body: chunk, headers: [
                "Upload-Offset": String(offset), "Content-Type": "application/offset+octet-stream",
            ], timeout: 300)
            switch response.statusCode {
            case 204:
                offset = Int64(response.value(forHTTPHeaderField: "Upload-Offset") ?? "") ?? offset + Int64(chunk.count)
            case 409:
                offset = Int64(response.value(forHTTPHeaderField: "Upload-Offset") ?? "") ?? 0
            case 400:
                log.error("enterprise upload rejected, restarting")
                return false
            default:
                throw EnterpriseServer.failure(data, response)
            }
            progress(0.1 * Double(offset) / Double(max(bytes, 1)))
        }
        return true
    }

    private func result(_ id: String) async throws -> Remote {
        let (data, response) = try await server.call("GET", "api/v1/transcriptions/\(id)/result", timeout: 120)
        guard response.statusCode == 200, let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              json["segments"] is [[String: Any]] else { throw EnterpriseServer.failure(data, response) }
        return try Self.parse(json)
    }

    /// 伺服器的結果換成段落（M§36.2；標準答案 server/result.json）：少了開始、結束、文字的段落略過；一段都沒有是「沒有辨識出文字」。
    /// 說話者在本機辨識，結果裡的 speaker、voices 不用
    nonisolated static func parse(_ json: [String: Any]) throws -> Remote {
        let items = json["segments"] as? [[String: Any]] ?? []
        let segments = items.compactMap { item -> TranscribedSegment? in
            guard let start = item["start"] as? Double, let end = item["end"] as? Double, let text = item["text"] as? String else { return nil }
            return TranscribedSegment(start: start, end: end, text: text, lowConfidence: item["lowConfidence"] as? Bool ?? false)
        }
        if segments.isEmpty { throw WhisperError.empty }
        return Remote(segments: segments, seams: json["seams"] as? [Double] ?? [],
                      engine: json["transcribedWith"] as? String ?? json["engine"] as? String ?? "breeze")
    }

    private func delete(_ id: String?) async {
        guard let id else { return }
        _ = try? await server.call("DELETE", "api/v1/transcriptions/\(id)", timeout: 15)
    }
}
