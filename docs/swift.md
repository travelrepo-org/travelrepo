# Swift apps (TravelRepoKit)

`TravelRepoKit` is a Swift package for iOS apps. It wraps the [C interface](native.md) of `TravelRepo.Native` in async Swift functions, so an app reads, validates, edits, versions and syncs trips with TravelRepo's own rules.

## Building

The package's binary target is `artifacts/native/TravelRepo.xcframework`. Build it once on macOS with Xcode and the .NET workload for iOS (see [native clients](native.md#building-the-library)):

```sh
eng/build-native-xcframework.sh artifacts/native
```

The xcframework holds one static library per platform: iOS devices (arm64) and the iOS simulator (arm64). It contains TravelRepo and the .NET runtime, with the header and a module map. Swift imports it as `CTravelRepo`. The module map links the system libraries and frameworks it needs, so an app adds nothing to its linker settings.

Then add the repository folder as a local package in Xcode and use the product `TravelRepoKit`. For Git, put `git2.framework` from `eng/build-libgit2-ios.sh` into the app (Embed & Sign).

Tests run on an iOS simulator. The Git tests need libgit2 for the simulator, given as `TRAVELREPO_LIBGIT2`. Without it they are skipped:

```sh
TEST_RUNNER_TRAVELREPO_LIBGIT2=/path/to/git2.xcframework/ios-arm64_x86_64-simulator/git2.framework/git2 \
  xcodebuild test -scheme TravelRepoKit -destination "platform=iOS Simulator,name=iPhone 17"
```

Call `TravelRepo.configure(libgit2:)` before the first Git command. libgit2 is loaded only then, and if it is missing, Git commands fail with `git.library`.

## Using a released version

Every release has `TravelRepo.xcframework.zip` and `git2.xcframework.zip` attached. For each release tag `vX.Y.Z`, the release workflow also tags a commit `X.Y.Z` whose `Package.swift` downloads that zip (url and checksum). SwiftPM picks the tag without "v" when both exist, so a Swift app depends on a version as usual:

```swift
.package(url: "https://github.com/travelrepo-org/travelrepo", from: "0.14.0")   // product: TravelRepoKit
```

Unzip `git2.xcframework` into the app project and embed it (Embed & Sign) without linking it. TravelRepo loads it by path through `TravelRepo.configure(libgit2:)`.

On `main` and in feature branches, `Package.swift` uses the local `artifacts/native/TravelRepo.xcframework`. `eng/swift-release.py` is the step that swaps that line on the tagged commit.

`TravelRepo.Native` is also published to nuget.org with the other packages, for apps that build their own native library from it.

## Using it

```swift
import TravelRepoKit

try await TravelRepo.configure(libgit2: Bundle.main.privateFrameworksPath! + "/git2.framework/git2")
TravelRepo.setCredentialProvider { origin in
    guard origin.host == "github.com", let token = Keychain.token else { return nil }
    return ("x-access-token", token)
}

let trip = try await TravelRepoSession.open(root: tripFolder, recoveryRoot: recoveryFolder, culture: Locale.current.identifier(.bcp47))
let agenda = try await trip.agenda()
let hits = try await trip.find("kyoto")

let snapshot = try await trip.snapshot()
var person = /* the entity's data from snapshot.entities */
person["title"] = "Alex K."
try await trip.apply(revision: snapshot.revision, edits: [personID: person])
try await trip.call("create_version", ["message": "Rename Alex"])
let state = try await trip.sync()
```

- Every function is `async` and runs on a TravelRepo thread. Cancelling the Swift task cancels the call (`call.cancelled`).
- Failures throw `TravelRepoError` with the TravelRepo code and an English message. Apps show their own text for the codes.
- `TravelRepoSession` has typed results for `snapshot`, `apply`, `agenda`, `find`, `syncStatus`, `sync`, `mergePreview` and `mergeApply`.
- Every other command, including all MCP tools, goes through `call(_:_:)`. It returns `JSONValue`, or decodes into your own `Decodable` type.
- `JSONValue` keeps entity data as it is, including unknown fields and extensions. Write the whole entity back through `apply`.
- A session closes when the `TravelRepoSession` is released.

GitHub sign-in uses a device code:

```swift
try await GitHub.configure(clientId: "Iv1.example", appSlug: "your-github-app")
try await GitHub.setToken(Keychain.token)             // on every start
let authorization = try await GitHub.beginSignIn()   // show authorization.userCode, open verificationUri
while true {
    try await Task.sleep(for: .seconds(authorization.interval))
    if case .connected(let token) = try await GitHub.poll(authorization) { Keychain.token = token; break }
}
let trips = try await GitHub.tripRepositories()
```

Git uses that token for `github.com` unless the app sets a credential provider of its own.

`apply` writes only when the trip is still at the revision it was based on. After a sync, or when the app comes back to the foreground, compare `revision()` with the one shown and read again if it differs.

## App Store

- **Privacy manifest.** `TravelRepoKit` brings its own (`PrivacyInfo.xcprivacy`), and Xcode merges it into the app's privacy report. TravelRepo tracks nobody and collects no data. The linked binary calls these required-reason APIs:
  - file timestamps (`stat`, `fstat`, `lstat`): reasons `C617.1` (files in the app's container) and `3B52.1` (trip folders the user picks);
  - system boot time (`clock_gettime`): reason `35F9.1` (the .NET runtime measures elapsed time);
  - disk space (`statfs`, `fstatfs`): reason `E174.1` (the runtime checks the file system before writing).

  Check the list again after updating TravelRepo with `nm -u` on the app binary. An app that does more, such as GitHub sign-in, declares that in its own manifest.
- **Encryption.** TravelRepo uses only standard HTTPS through Apple's Security framework and stores nothing encrypted itself. That is the usual exempt case (`ITSAppUsesNonExemptEncryption` = `NO`), but the export compliance answer is the app's.
- **Size.** The whole interface adds about 24 MB to the app binary on a device after stripping, and `git2.framework` about 1.4 MB.
- **Runtime.** The pinned SDK 10.0.404 brings .NET runtime 10.0.15. Do not build releases with an SDK whose runtime is older than 10.0.14: 10.0.12 is affected by GHSA-p3jp-9v83-76w9.
- **Plugins.** Jourfold plugins are .NET assemblies loaded at run time, which NativeAOT cannot do, so they are not available in Swift apps.

