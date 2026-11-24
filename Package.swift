// swift-tools-version: 5.10
// TravelRepoKit: TravelRepo for Swift apps on iOS, through the C interface of TravelRepo.Native (docs/swift.md).
// The binary target is built locally with eng/build-native-xcframework.sh artifacts/native.
import PackageDescription

let package = Package(
    name: "TravelRepoKit",
    platforms: [.iOS(.v17)],
    products: [.library(name: "TravelRepoKit", targets: ["TravelRepoKit"])],
    targets: [
        .binaryTarget(name: "CTravelRepo", path: "artifacts/native/TravelRepo.xcframework"),
        .target(name: "TravelRepoKit", dependencies: ["CTravelRepo"], path: "swift/Sources/TravelRepoKit"),
        .testTarget(name: "TravelRepoKitTests", dependencies: ["TravelRepoKit"], path: "swift/Tests/TravelRepoKitTests")
    ]
)
