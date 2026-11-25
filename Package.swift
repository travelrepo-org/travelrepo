// swift-tools-version: 5.10
// TravelRepoKit: TravelRepo for Swift apps on iOS, through the C interface of TravelRepo.Native (docs/swift.md).
// The binary target is built locally with eng/build-native-xcframework.sh artifacts/native.
import PackageDescription

// On a release, eng/swift-release.py replaces this line with the downloadable xcframework (url and checksum) on the
// commit that SwiftPM resolves for that version (docs/swift.md#using-a-released-version). Keep it on one line.
let binary: Target = .binaryTarget(name: "CTravelRepo", url: "https://github.com/travelrepo-org/travelrepo/releases/download/v0.14.0/TravelRepo.xcframework.zip", checksum: "5afe815bb3540d686da64d1076a60afcfc788f9346c433561e76a2db3a459177")

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
