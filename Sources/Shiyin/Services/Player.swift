import AVFoundation
import Observation

/// 播放錄音、時間點跳播（R4.2、R5.3）
@MainActor
@Observable
final class Player {
    private(set) var currentTime: Double = 0
    private(set) var duration: Double = 0
    private(set) var isPlaying = false
    private(set) var isLoaded = false
    /// 倍速（R8.2）
    private(set) var rate: Float = 1

    @ObservationIgnored private var player: AVPlayer?
    @ObservationIgnored private var observer: Any?
    @ObservationIgnored private var loadedURL: URL?

    func load(_ url: URL) {
        guard url != loadedURL else { return }
        unload()
        guard FileManager.default.fileExists(atPath: url.path) else { return }
        let item = AVPlayerItem(url: url)
        item.audioTimePitchAlgorithm = .timeDomain   // 變速時人聲不變調
        let player = AVPlayer(playerItem: item)
        player.defaultRate = rate
        self.player = player
        loadedURL = url
        isLoaded = true
        observer = player.addPeriodicTimeObserver(forInterval: CMTime(value: 1, timescale: 10), queue: .main) { [weak self] time in
            MainActor.assumeIsolated {
                guard let self, let player = self.player else { return }
                self.currentTime = time.seconds.isFinite ? time.seconds : 0
                self.isPlaying = player.rate > 0
            }
        }
        Task {
            if let seconds = try? await player.currentItem?.asset.load(.duration).seconds, seconds.isFinite {
                self.duration = seconds
            }
        }
    }

    func unload() {
        if let observer { player?.removeTimeObserver(observer) }
        player?.pause()
        player = nil
        observer = nil
        loadedURL = nil
        currentTime = 0
        duration = 0
        isPlaying = false
        isLoaded = false
    }

    func setRate(_ value: Float) {
        rate = value
        player?.defaultRate = value
        if let player, player.rate > 0 { player.rate = value }
    }

    func toggle() {
        guard let player else { return }
        if player.rate > 0 {
            player.pause()
        } else {
            if duration > 0 && currentTime >= duration - 0.2 { seek(to: 0, play: false) }
            player.play()
        }
        isPlaying = player.rate > 0
    }

    func seek(to seconds: Double, play: Bool = true) {
        guard let player else { return }
        let target = max(0, duration > 0 ? min(seconds, duration) : seconds)
        currentTime = target
        player.seek(to: CMTime(seconds: target, preferredTimescale: 600), toleranceBefore: .zero, toleranceAfter: .zero)
        if play { player.play() }
        isPlaying = player.rate > 0 || play
    }
}
