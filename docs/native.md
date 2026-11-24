# Native clients (C ABI)

`TravelRepo.Native` lets apps that are not written in .NET use TravelRepo, for example a SwiftUI app on iOS. It is compiled with NativeAOT into a native library with a small C interface: a command name and JSON go in, JSON comes back. The trip logic stays in TravelRepo, so a native app reads, validates, edits and versions trips exactly as Jourfold and the MCP server do.

## Building the library

```sh
eng/build-native.sh ios-arm64 artifacts/native/ios-arm64
eng/build-native.sh iossimulator-arm64 artifacts/native/iossimulator-arm64
```

It needs macOS with Xcode. For iOS, install the .NET workload that provides the NativeAOT runtime for iOS once. `--skip-manifest-update` leaves the other workloads, such as `ios`, as they are:

```sh
dotnet workload install mobile-librarybuilder --skip-manifest-update
```

Each output directory holds:

- `lib/libTravelRepoNative.a`, TravelRepo and the .NET runtime it needs, as static libraries;
- `include/travelrepo.h`, the C interface;
- `link-flags.txt`, the system libraries and frameworks to link as well.

Link all of `lib/` and the flags into the app. For example, with `swiftc` and the header as bridging header:

```sh
swiftc -import-objc-header include/travelrepo.h App.swift lib/*.a lib/*.o $(cat link-flags.txt) ...
```

On iOS, `git2.framework` from `eng/build-libgit2-ios.sh` goes into the app's `Frameworks` folder. Pass its path to `configure` (below) before the first Git command.

Cultures, sorting and date formats use the system's ICU (`libicucore`), so no ICU data ships with the app. A trip opened with `"culture": "de-DE"` sorts and exports in German.

## Calling it

```c
uint64_t tr_call(uint64_t session, const char *command, const char *arguments_json, tr_callback callback, void *context);
void tr_cancel(uint64_t call);
void tr_close(uint64_t session);
void tr_set_credential_handler(tr_credential_handler handler, void *context);
void tr_credential_reply(uint64_t request, const char *credential_json);
```

- Strings are UTF-8 and end with a zero byte. TravelRepo copies them before `tr_call` returns.
- Every call answers exactly once through `callback`. The answer comes on a TravelRepo thread, possibly before `tr_call` returns.
- The response is valid only while the callback runs, so copy it.
- Do not block an app's main thread waiting for an answer.

A response is either of these:

```json
{"ok": true, "result": {"session": 1}}
{"ok": false, "error": {"code": "argument.missing", "message": "The argument id is missing."}}
```

`tr_cancel` stops a running call, which then answers with `call.cancelled`. Error codes are the TravelRepo codes (`repository.invalid`, `git.not_repository`, ...), plus:

| Code | Meaning |
| --- | --- |
| `argument.missing` | A required argument is missing. |
| `argument.invalid` | An argument is malformed or has the wrong type. |
| `command.unknown` | No command with that name exists here. |
| `command.invalid` | The command rejected its arguments. |
| `session.unknown` | The session id is not open. |
| `session.not_trip` | The folder given to `open` is not a trip. |
| `session.read_only` | A change was asked of a read-only session. |
| `internal` | An unexpected failure. |

## Commands

Without a session (pass 0):

| Command | Arguments | Result |
| --- | --- | --- |
| `info` | – | `version`, `format_version`, `invariant_globalization`, the command names |
| `configure` | `libgit2`: path of the libgit2 library, for example `…/Frameworks/git2.framework/git2` | `{}` |
| `open` | `root`, optional `recovery_root`, `read_only`, `culture` (such as `de-DE`), `client_name`, `weather_cache`, `weather_provider`, `exchange_rates` | `session` |
| `clone` | `url`, `destination` | `destination` |

With a session:

| Command | Arguments | Result |
| --- | --- | --- |
| `snapshot` | – | every entity file (`path`, `hash`, `data`) and the validation `diagnostics` |
| `initialize_git` | `name`, `email` | `{}`; starts Git history for a trip that has none |
| `sync_status` | optional `remote` | `branch`, `ahead`, `behind`, `has_local_changes`, `state` (`local_only`, `synced`, `local_changes`, `diverged`, `remote_changes`) |
| `sync` | optional `remote` (default `origin`) | `state`; fetches, takes fast-forwards and sends own versions |
| `add_remote` | `name`, `url` | `{}` |

Every tool of the [MCP server](mcp.md) also works as a session command, with the same name, arguments and results. For example: `get_trip`, `get_schedule`, `search`, `get_entity`, `create_entity`, `update_entity`, `delete_entity`, `get_costs`, `add_expense`, `export_text`, `create_version` and `list_versions`. Changes are validated and written as one transaction, as there. `info` lists all commands.

For an example, this Swift function wraps one call:

```swift
func call(_ session: UInt64, _ command: String, _ arguments: String?) async -> String {
    final class Box { let done: CheckedContinuation<String, Never>; init(_ done: CheckedContinuation<String, Never>) { self.done = done } }
    return await withCheckedContinuation { done in
        let context = Unmanaged.passRetained(Box(done)).toOpaque()
        let callback: tr_callback = { context, response in
            Unmanaged<Box>.fromOpaque(context!).takeRetainedValue().done.resume(returning: String(cString: response!))
        }
        command.withCString { name in
            if let arguments { _ = arguments.withCString { tr_call(session, name, $0, callback, context) } }
            else { _ = tr_call(session, name, nil, callback, context) }
        }
    }
}
```

## Git credentials

Git asks the app for credentials through the handler set with `tr_set_credential_handler`. The handler gets a request id and the origin, such as `https://github.com`. The app answers with `tr_credential_reply(request, "{\"username\":\"x-access-token\",\"password\":\"…\"}")`, or with `NULL` when it has none. The token comes from wherever the app keeps it, such as the Keychain. TravelRepo does not store it.

## Checks

`tests/TravelRepo.Tests/NativeTests.cs` calls the exported functions through function pointers. It also checks that every MCP tool is a command with the same arguments. `eng/aot-smoke.py` (see [development](development.md#nativeaot)) compiles the C interface with NativeAOT and runs it, including Git versions and a sync through libgit2. The iOS build itself is not part of CI.
