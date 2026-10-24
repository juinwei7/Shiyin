// swift-tools-version: 6.2
import PackageDescription

let package = Package(
    name: "Shiyin",
    platforms: [.macOS("26.0")],
    dependencies: [
        .package(url: "https://github.com/groue/GRDB.swift.git", from: "7.0.0"),
        .package(url: "https://github.com/FluidInference/FluidAudio.git", from: "0.7.0"),
    ],
    targets: [
        // whisper.cpp 官方 XCFramework（design §6.2）。版本固定，更新時要一併更新 checksum
        .binaryTarget(
            name: "whisper",
            url: "https://github.com/ggml-org/whisper.cpp/releases/download/b5454/whisper-b5454-xcframework.zip",
            checksum: "e57f8c48933000acabc13bb913fbe82805d483a2deff692cc6738836bd92393b"
        ),
        // llama.cpp 官方 XCFramework（design §32.2、D106）：Qwen3-ASR 用。Swift 不直接 import 它（跟 whisper 各帶一份不同版本的 ggml.h），
        // 經 C 薄層 QwenShim
        .binaryTarget(
            name: "llama",
            url: "https://github.com/ggml-org/llama.cpp/releases/download/b11840/llama-b11840-xcframework.zip",
            checksum: "4127cc50e99f1c722ff67b7de60275f8345b1453a288668a37bb7875a520ecbe"
        ),
        .target(name: "QwenShim", dependencies: ["llama"], path: "Sources/QwenShim"),
        .executableTarget(
            name: "Shiyin",
            dependencies: [
                .product(name: "GRDB", package: "GRDB.swift"),
                .product(name: "FluidAudio", package: "FluidAudio"),
                "whisper",
                "QwenShim",
            ],
            path: "Sources/Shiyin",
            swiftSettings: [.swiftLanguageMode(.v5)],
            linkerSettings: [.unsafeFlags(["-Xlinker", "-rpath", "-Xlinker", "@executable_path/../Frameworks"])]
        ),
    ]
)
