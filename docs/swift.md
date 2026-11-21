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

`apply` writes only when the trip is still at the revision it was based on. After a sync, or when the app comes back to the foreground, compare `revision()` with the one shown and read again if it differs.
