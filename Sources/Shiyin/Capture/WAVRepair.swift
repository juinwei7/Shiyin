import Foundation

enum WAVRepairError: LocalizedError {
    case invalid(String)
    var errorDescription: String? {
        switch self { case .invalid(let reason): return reason }
    }
}

/// 修復沒有正常結束的 WAV：用實際檔案大小改寫 RIFF 與 data 的大小（design §8）
enum WAVRepair {
    static func repair(_ url: URL) throws {
        let handle = try FileHandle(forUpdating: url)
        defer { try? handle.close() }
        let size = try handle.seekToEnd()
        guard size > 44 else { throw WAVRepairError.invalid(String(localized: "錄音檔太小，沒有內容")) }
        try handle.seek(toOffset: 0)
        let header = try handle.read(upToCount: 12) ?? Data()
        guard header.count == 12, header.prefix(4) == Data("RIFF".utf8), header.suffix(4) == Data("WAVE".utf8) else {
            throw WAVRepairError.invalid(String(localized: "不是 WAV 檔"))
        }
        var offset: UInt64 = 12
        while offset + 8 <= size {
            try handle.seek(toOffset: offset)
            guard let chunk = try handle.read(upToCount: 8), chunk.count == 8 else { break }
            let id = String(decoding: chunk.prefix(4), as: UTF8.self)
            let chunkSize = chunk.suffix(4).withUnsafeBytes { UInt64($0.loadUnaligned(as: UInt32.self).littleEndian) }
            if id == "data" {
                let dataSize = UInt32(min(size - offset - 8, UInt64(UInt32.max)))
                try write(dataSize, at: offset + 4, handle)
                try write(UInt32(min(size - 8, UInt64(UInt32.max))), at: 4, handle)
                return
            }
            offset += 8 + chunkSize + (chunkSize % 2)
        }
        throw WAVRepairError.invalid(String(localized: "找不到音訊資料區段"))
    }

    private static func write(_ value: UInt32, at offset: UInt64, _ handle: FileHandle) throws {
        try handle.seek(toOffset: offset)
        var little = value.littleEndian
        try handle.write(contentsOf: Data(bytes: &little, count: 4))
    }
}
