// swift-tools-version: 5.10
// TravelRepoKit: TravelRepo for Swift apps on iOS, through the C interface of TravelRepo.Native (docs/swift.md).
// The binary target is built locally with eng/build-native-xcframework.sh artifacts/native.
import PackageDescription

// On a release, eng/swift-release.py replaces this line with the downloadable xcframework (url and checksum) on the
// commit that SwiftPM resolves for that version (docs/swift.md#using-a-released-version). Keep it on one line.
let binary: Target = .binaryTarget(name: "CTravelRepo", path: "artifacts/native/TravelRepo.xcframework")

let package = Package(
    name: "TravelRepoKit",
    platforms: [.iOS(.v17)],
    products: [.library(name: "TravelRepoKit", targets: ["TravelRepoKit"])],
    targets: [
        binary,
        .target(name: "TravelRepoKit", dependencies: ["CTravelRepo"], path: "swift/Sources/TravelRepoKit", resources: [.copy("PrivacyInfo.xcprivacy")]),
        .testTarget(name: "TravelRepoKitTests", dependencies: ["TravelRepoKit"], path: "swift/Tests/TravelRepoKitTests")
    ]
)
