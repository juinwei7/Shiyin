import AppKit
import ScreenCaptureKit

/// 錄音中截圖（design §83.2、R109.1、D187）：游標所在的整個螢幕，拾音自己的視窗（主視窗、錄音島、浮動視窗）不會被截進去
enum ScreenShot {
    enum Failure: Error {
        /// 沒有「錄製螢幕」的權限（問過、被拒絕）
        case denied
        /// 權限是允許的，ScreenCaptureKit 卻拿不到畫面：剛允許時要重新打開拾音
        case needsRelaunch
    }

    /// 有沒有權限；沒有時第一次會跳出系統的詢問（之後再呼叫不會再問）
    static func ensureAccess() -> Bool {
        if CGPreflightScreenCaptureAccess() { return true }
        return CGRequestScreenCaptureAccess()
    }

    static func capture() async throws -> CGImage {
        #if DEBUG
        // 驗證：-debugFakeScreenshot <png> 不呼叫 ScreenCaptureKit（DEBUG 版是 ad-hoc 簽章，不要讓它跳權限詢問，design §83.8）
        if let path = UserDefaults.standard.string(forKey: "debugFakeScreenshot"), !path.isEmpty {
            guard let image = ScreenImages.load(URL(fileURLWithPath: path)) else { throw Failure.needsRelaunch }
            return image
        }
        #endif
        guard ensureAccess() else { throw Failure.denied }
        let content: SCShareableContent
        do {
            content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        } catch {
            log.error("screenshot: shareable content failed: \(error.localizedDescription)")
            throw Failure.needsRelaunch
        }
        // 游標所在的螢幕（NSScreen 和 SCDisplay 用 CGDirectDisplayID 對起來）
        let mouse = NSEvent.mouseLocation
        let screen = NSScreen.screens.first { NSMouseInRect(mouse, $0.frame, false) } ?? NSScreen.main
        let displayID = (screen?.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.uint32Value
        guard let display = content.displays.first(where: { $0.displayID == displayID }) ?? content.displays.first else {
            throw Failure.needsRelaunch
        }
        let own = content.applications.filter { $0.processID == ProcessInfo.processInfo.processIdentifier }
        let filter = SCContentFilter(display: display, excludingApplications: own, exceptingWindows: [])
        let configuration = SCStreamConfiguration()
        let scale = screen?.backingScaleFactor ?? 2
        configuration.width = Int(CGFloat(display.width) * scale)
        configuration.height = Int(CGFloat(display.height) * scale)
        configuration.showsCursor = false
        do {
            return try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: configuration)
        } catch {
            log.error("screenshot: capture failed: \(error.localizedDescription)")
            throw Failure.needsRelaunch
        }
    }
}

/// 畫面的圖片：讀檔（照 EXIF 轉正）、剪貼簿、縮小存成 JPEG（design §83.1）
enum ScreenImages {
    /// 存檔的長邊上限、送給整理工具的長邊上限（design §83.5）
    static let storedMaxPixels = 2560
    static let quality = 0.85

    /// 讀圖片檔，照 EXIF 的方向轉正（文字辨識、縮圖都用轉正後的）
    static func load(_ url: URL) -> CGImage? {
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil) else { return nil }
        let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any]
        let width = properties?[kCGImagePropertyPixelWidth] as? Int ?? 0
        let height = properties?[kCGImagePropertyPixelHeight] as? Int ?? 0
        let options: [CFString: Any] = [kCGImageSourceCreateThumbnailFromImageAlways: true,
                                        kCGImageSourceCreateThumbnailWithTransform: true,
                                        kCGImageSourceThumbnailMaxPixelSize: max(width, height, 1)]
        return CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary)
    }

    /// 剪貼簿、拖進來的圖片：圖片檔的網址優先（保留原本的解析度），再來是圖片本身（截圖到剪貼簿是 png／tiff）
    static func read(_ pasteboard: NSPasteboard) -> [CGImage] {
        let urls = pasteboard.readObjects(forClasses: [NSURL.self],
                                          options: [.urlReadingFileURLsOnly: true,
                                                    .urlReadingContentsConformToTypes: ["public.image"]]) as? [URL] ?? []
        let fromFiles = urls.compactMap(load)
        if !fromFiles.isEmpty { return fromFiles }
        let images = pasteboard.readObjects(forClasses: [NSImage.self]) as? [NSImage] ?? []
        return images.compactMap { $0.cgImage(forProposedRect: nil, context: nil, hints: nil) }
    }

    static func hasImage(_ pasteboard: NSPasteboard) -> Bool { !read(pasteboard).isEmpty }

    /// 長邊縮到 maxPixels 以內的 JPEG
    static func jpeg(_ image: CGImage, maxPixels: Int = storedMaxPixels, quality: Double = quality) -> Data? {
        let scaled = opaque(resized(image, maxPixels: maxPixels))
        let data = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(data, "public.jpeg" as CFString, 1, nil) else { return nil }
        CGImageDestinationAddImage(destination, scaled, [kCGImageDestinationLossyCompressionQuality: quality] as CFDictionary)
        guard CGImageDestinationFinalize(destination) else { return nil }
        return data as Data
    }

    /// 有透明的圖片（貼上的 png）先鋪白底：JPEG 沒有透明，不鋪的話透明的地方會變黑
    private static func opaque(_ image: CGImage) -> CGImage {
        switch image.alphaInfo {
        case .none, .noneSkipFirst, .noneSkipLast: return image
        default: break
        }
        guard let context = CGContext(data: nil, width: image.width, height: image.height, bitsPerComponent: 8, bytesPerRow: 0,
                                      space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                      bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue) else { return image }
        let rect = CGRect(x: 0, y: 0, width: image.width, height: image.height)
        context.setFillColor(.white)
        context.fill(rect)
        context.draw(image, in: rect)
        return context.makeImage() ?? image
    }

    static func resized(_ image: CGImage, maxPixels: Int) -> CGImage {
        let longest = max(image.width, image.height)
        guard longest > maxPixels else { return image }
        let ratio = Double(maxPixels) / Double(longest)
        let width = max(1, Int((Double(image.width) * ratio).rounded()))
        let height = max(1, Int((Double(image.height) * ratio).rounded()))
        guard let context = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
                                      space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                      bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue) else { return image }
        context.interpolationQuality = .high
        context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
        return context.makeImage() ?? image
    }
}
