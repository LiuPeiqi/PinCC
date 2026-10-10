// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "PinCC",
    platforms: [.macOS(.v13)],
    products: [
        .library(name: "PinCCCore", type: .static, targets: ["PinCCCore"]),
        .library(name: "PinCCDesk", type: .static, targets: ["PinCCDesk"]),
        .library(name: "PinCCMacPlatform", type: .static, targets: ["PinCCMacPlatform"]),
        .library(name: "PinCCCli", type: .static, targets: ["PinCCCli"])
    ],
    dependencies: [],
    targets: [
        .systemLibrary(name: "CSQLite", path: "Sources/CSQLite"),
        .target(name: "PinCCCore", dependencies: ["CSQLite"]),
        .target(name: "PinCCDesk", dependencies: ["PinCCCore"]),
        .target(name: "PinCCMacPlatform", dependencies: ["PinCCCore"]),
        .target(name: "PinCCCli", dependencies: ["PinCCCore", "PinCCMacPlatform"]),
        .testTarget(name: "PinCCCoreTests", dependencies: ["PinCCCore"]),
        .testTarget(name: "PinCCDeskTests", dependencies: ["PinCCDesk"]),
        .testTarget(name: "PinCCMacPlatformTests", dependencies: ["PinCCCore", "PinCCMacPlatform"]),
        .testTarget(name: "PinCCCliTests", dependencies: ["PinCCCli", "PinCCCore"])
    ],
    swiftLanguageModes: [.v6]
)
