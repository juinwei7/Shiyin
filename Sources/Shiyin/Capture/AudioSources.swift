import AVFoundation
import CoreAudio
import Foundation

/// 所有音源輸出的統一格式：16 kHz、單聲道、Float32（design §5.1）
enum CaptureFormat {
    static let sampleRate: Double = 16_000
    static let mono = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: sampleRate, channels: 1, interleaved: false)!
}

/// 音源：把收到的音訊轉成 16 kHz 單聲道後交給 `onSamples`。callback 在音訊執行緒上，不能做重的事。
protocol AudioSource: AnyObject {
    var onSamples: (([Float]) -> Void)? { get set }
    func start() throws
    func stop()
}

/// 把任意格式的 buffer 持續轉成 16 kHz 單聲道（保留重取樣的狀態，適合串流）
final class StreamConverter {
    private let converter: AVAudioConverter

    init?(from format: AVAudioFormat) {
        guard let converter = AVAudioConverter(from: format, to: CaptureFormat.mono) else { return nil }
        converter.downmix = true
        self.converter = converter
    }

    func convert(_ buffer: AVAudioPCMBuffer) -> [Float] {
        let ratio = CaptureFormat.sampleRate / buffer.format.sampleRate
        let capacity = AVAudioFrameCount(Double(buffer.frameLength) * ratio + 64)
        guard let output = AVAudioPCMBuffer(pcmFormat: CaptureFormat.mono, frameCapacity: capacity) else { return [] }
        var fed = false
        var error: NSError?
        converter.convert(to: output, error: &error) { _, status in
            if fed {
                status.pointee = .noDataNow
                return nil
            }
            fed = true
            status.pointee = .haveData
            return buffer
        }
        guard error == nil, let data = output.floatChannelData else { return [] }
        return Array(UnsafeBufferPointer(start: data[0], count: Int(output.frameLength)))
    }
}

/// 麥克風（AVAudioEngine）。可以指定輸入裝置的 UID，空字串表示系統預設。
final class MicrophoneSource: AudioSource {
    var onSamples: (([Float]) -> Void)?
    private let engine = AVAudioEngine()
    private let deviceUID: String

    init(deviceUID: String) {
        self.deviceUID = deviceUID
    }

    func start() throws {
        let input = engine.inputNode
        if !deviceUID.isEmpty, let id = AudioDevices.deviceID(forUID: deviceUID) {
            try input.auAudioUnit.setDeviceID(id)
        }
        let format = input.outputFormat(forBus: 0)
        guard format.sampleRate > 0, let converter = StreamConverter(from: format) else {
            throw CaptureError.noInput
        }
        input.installTap(onBus: 0, bufferSize: 4096, format: format) { [weak self] buffer, _ in
            let samples = converter.convert(buffer)
            if !samples.isEmpty { self?.onSamples?(samples) }
        }
        engine.prepare()
        try engine.start()
    }

    func stop() {
        engine.inputNode.removeTap(onBus: 0)
        engine.stop()
    }
}

/// 電腦播放的聲音（Core Audio process tap，macOS 14.4+，design §5.1）
final class SystemAudioSource: AudioSource {
    var onSamples: (([Float]) -> Void)?
    private var tapID = AudioObjectID(kAudioObjectUnknown)
    private var aggregateID = AudioObjectID(kAudioObjectUnknown)
    private var procID: AudioDeviceIOProcID?
    private let queue = DispatchQueue(label: "com.weiwei.shiyin.systemaudio", qos: .userInitiated)

    func start() throws {
        // 排除自己（播放錄音時不要錄進去）
        let own = AudioDevices.processObject(pid: ProcessInfo.processInfo.processIdentifier)
        let description = CATapDescription(stereoGlobalTapButExcludeProcesses: own.map { [$0] } ?? [])
        description.uuid = UUID()
        description.muteBehavior = .unmuted
        description.isPrivate = true
        try check(AudioHardwareCreateProcessTap(description, &tapID), String(localized: "建立系統聲音擷取"))

        guard let outputUID = AudioDevices.defaultOutputUID() else { throw CaptureError.noOutputDevice }
        let aggregate: [String: Any] = [
            kAudioAggregateDeviceNameKey: String(localized: "拾音系統聲音"),
            kAudioAggregateDeviceUIDKey: UUID().uuidString,
            kAudioAggregateDeviceMainSubDeviceKey: outputUID,
            kAudioAggregateDeviceIsPrivateKey: true,
            kAudioAggregateDeviceIsStackedKey: false,
            kAudioAggregateDeviceTapAutoStartKey: true,
            kAudioAggregateDeviceSubDeviceListKey: [[kAudioSubDeviceUIDKey: outputUID]],
            kAudioAggregateDeviceTapListKey: [[kAudioSubTapDriftCompensationKey: true,
                                               kAudioSubTapUIDKey: description.uuid.uuidString]],
        ]
        try check(AudioHardwareCreateAggregateDevice(aggregate as CFDictionary, &aggregateID), String(localized: "建立聚合裝置"))

        var asbd = AudioStreamBasicDescription()
        var size = UInt32(MemoryLayout<AudioStreamBasicDescription>.size)
        var address = AudioObjectPropertyAddress(mSelector: kAudioTapPropertyFormat, mScope: kAudioObjectPropertyScopeGlobal,
                                                 mElement: kAudioObjectPropertyElementMain)
        try check(AudioObjectGetPropertyData(tapID, &address, 0, nil, &size, &asbd), String(localized: "讀取系統聲音格式"))
        guard let format = AVAudioFormat(streamDescription: &asbd), let converter = StreamConverter(from: format) else {
            throw CaptureError.unsupportedFormat
        }
        try check(AudioDeviceCreateIOProcIDWithBlock(&procID, aggregateID, queue) { [weak self] _, input, _, _, _ in
            guard let buffer = AVAudioPCMBuffer(pcmFormat: format, bufferListNoCopy: input, deallocator: nil) else { return }
            let samples = converter.convert(buffer)
            if !samples.isEmpty { self?.onSamples?(samples) }
        }, String(localized: "建立系統聲音的 IO"))
        try check(AudioDeviceStart(aggregateID, procID), String(localized: "開始擷取系統聲音"))
    }

    /// 一定要依序清乾淨，否則 HAL 可能卡住（design §5.1）
    func stop() {
        if aggregateID != kAudioObjectUnknown {
            if let procID {
                AudioDeviceStop(aggregateID, procID)
                AudioDeviceDestroyIOProcID(aggregateID, procID)
            }
            AudioHardwareDestroyAggregateDevice(aggregateID)
        }
        if tapID != kAudioObjectUnknown { AudioHardwareDestroyProcessTap(tapID) }
        procID = nil
        aggregateID = AudioObjectID(kAudioObjectUnknown)
        tapID = AudioObjectID(kAudioObjectUnknown)
    }

    private func check(_ status: OSStatus, _ step: String) throws {
        if status != noErr {
            stop()
            throw CaptureError.coreAudio(step, status)
        }
    }
}

#if DEBUG
/// 只在 DEBUG：用音訊檔模擬音源，speed 是播放倍速（驗證錄音流程用，見 design §13 of v2）
final class FileSource: AudioSource {
    var onSamples: (([Float]) -> Void)?
    /// 模擬「麥克風＋電腦聲音」時，同一段音訊在同一個迴圈裡也送給第二個聲道。
    /// 兩個聲道各開一個 FileSource 的話，高倍速時兩邊的速度會不一樣，造成真實裝置不會有的「晚到」
    var mirror: MirroredSource?
    private let url: URL
    private let speed: Double
    private var task: Task<Void, Never>?

    init(url: URL, speed: Double) {
        self.url = url
        self.speed = speed
    }

    func start() throws {
        let url = url, speed = speed
        task = Task.detached { [weak self] in
            guard let samples = try? await AudioDecoder.decode(url) else { return }
            let chunk = Int(CaptureFormat.sampleRate / 10)    // 0.1 秒一段
            var index = 0
            while index < samples.count, !Task.isCancelled {
                let end = min(samples.count, index + chunk)
                let chunk = Array(samples[index..<end])
                self?.onSamples?(chunk)
                self?.mirror?.onSamples?(chunk)
                index = end
                try? await Task.sleep(for: .seconds(0.1 / speed))
            }
        }
    }

    func stop() {
        task?.cancel()
        task = nil
    }
}

/// 只負責接收 FileSource 轉送的音訊，自己不產生資料
final class MirroredSource: AudioSource {
    var onSamples: (([Float]) -> Void)?
    func start() throws {}
    func stop() {}
}
#endif

enum CaptureError: LocalizedError {
    case noInput
    case noOutputDevice
    case unsupportedFormat
    case coreAudio(String, OSStatus)

    var errorDescription: String? {
        switch self {
        case .noInput: return String(localized: "找不到可以用的麥克風")
        case .noOutputDevice: return String(localized: "找不到輸出裝置，沒辦法擷取系統聲音")
        case .unsupportedFormat: return String(localized: "不支援這個音訊格式")
        case .coreAudio(let step, let status): return String(localized: "\(step)失敗（錯誤代碼 \(status)）。請確認已允許拾音「錄製系統音訊」")
        }
    }
}

/// Core Audio 裝置查詢
enum AudioDevices {
    struct Device: Identifiable, Hashable {
        var id: String { uid }
        var uid: String
        var name: String
    }

    static func inputDevices() -> [Device] {
        allDevices().compactMap { id in
            guard hasInputStreams(id), let uid = string(id, kAudioDevicePropertyDeviceUID),
                  let name = string(id, kAudioObjectPropertyName) else { return nil }
            return Device(uid: uid, name: name)
        }
    }

    static func deviceID(forUID uid: String) -> AudioDeviceID? {
        allDevices().first { string($0, kAudioDevicePropertyDeviceUID) == uid }
    }

    static func defaultOutputUID() -> String? {
        var id = AudioDeviceID(0)
        var size = UInt32(MemoryLayout<AudioDeviceID>.size)
        var address = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDefaultOutputDevice,
                                                 mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        guard AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size, &id) == noErr else { return nil }
        return string(id, kAudioDevicePropertyDeviceUID)
    }

    static func processObject(pid: pid_t) -> AudioObjectID? {
        var pid = pid
        var object = AudioObjectID(kAudioObjectUnknown)
        var size = UInt32(MemoryLayout<AudioObjectID>.size)
        var address = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyTranslatePIDToProcessObject,
                                                 mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        let status = AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address,
                                                UInt32(MemoryLayout<pid_t>.size), &pid, &size, &object)
        return status == noErr && object != kAudioObjectUnknown ? object : nil
    }

    private static func allDevices() -> [AudioDeviceID] {
        var address = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDevices,
                                                 mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size) == noErr else { return [] }
        var ids = [AudioDeviceID](repeating: 0, count: Int(size) / MemoryLayout<AudioDeviceID>.size)
        guard AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size, &ids) == noErr else { return [] }
        return ids
    }

    private static func hasInputStreams(_ id: AudioDeviceID) -> Bool {
        var address = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyStreams, mScope: kAudioDevicePropertyScopeInput,
                                                 mElement: kAudioObjectPropertyElementMain)
        var size: UInt32 = 0
        return AudioObjectGetPropertyDataSize(id, &address, 0, nil, &size) == noErr && size > 0
    }

    private static func string(_ id: AudioObjectID, _ selector: AudioObjectPropertySelector) -> String? {
        var address = AudioObjectPropertyAddress(mSelector: selector, mScope: kAudioObjectPropertyScopeGlobal,
                                                 mElement: kAudioObjectPropertyElementMain)
        var value: Unmanaged<CFString>?
        var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        guard AudioObjectGetPropertyData(id, &address, 0, nil, &size, &value) == noErr, let value else { return nil }
        return value.takeRetainedValue() as String
    }
}
