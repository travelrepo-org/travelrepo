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

For iOS apps, `eng/build-native-xcframework.sh` builds all of this as `TravelRepo.xcframework`, which the Swift package `TravelRepoKit` uses ([Swift apps](swift.md)).

## Calling it

```c
uint64_t tr_call(uint64_t session, const char *command, const char *arguments_json, tr_callback callback, void *context);
void tr_cancel(uint64_t call);
void tr_close(uint64_t session);
void tr_set_credential_handler(tr_credential_handler handler, void *context);
void tr_credential_reply(uint64_t request, const char *credential_json);
void tr_set_event_handler(tr_callback handler, void *context);
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
| `repository.changed` | The trip changed since the revision given to `apply`. Read it again. |
| `merge.unresolved` | `merge_apply` got no answer for some conflicts. |
| `internal` | An unexpected failure. |

## Commands

Without a session (pass 0):

| Command | Arguments | Result |
| --- | --- | --- |
| `info` | – | `abi_version`, `version`, `format_version`, `invariant_globalization`, the command names |
| `configure` | `libgit2`: path of the libgit2 library, for example `…/Frameworks/git2.framework/git2` | `{}` |
| `create_trip` | `root` (an empty or new folder), `title`, optional `language`, `timezone`, `recovery_root`, `git_name` and `git_email` to start Git history | `root`, `id` |
| `open` | `root`, optional `recovery_root`, `read_only`, `culture` (such as `de-DE`), `client_name`, `weather_cache`, `weather_provider`, `exchange_rates` | `session` |
| `clone` | `url`, `destination` | `destination` |
| `github_configure` | `client_id` of the GitHub App, optional `app_slug` | `{}` |
| `github_set_token` | `token` from an earlier sign-in, or `null` to forget it | `{}` |
| `github_status` | – | `connected`, `account` (`login`, `name`, `avatar_url`, `profile_url`) |
| `github_begin_sign_in` | – | `device_code`, `user_code`, `verification_uri`, `expires_in`, `interval` |
| `github_poll_sign_in` | the object `github_begin_sign_in` returned | `state` (`pending`, `slow_down`, `connected`) and, when connected, the `token` to store |
| `github_sign_out` | – | `{}` |
| `github_repository_access` | – | `has_access`, `install_url` of the GitHub App |
| `github_repositories` | – | the repositories the GitHub App may see that hold a trip |
| `github_create_repository` | `name` | the new private repository (`full_name`, `clone_url`, `web_url`, `private`) |
| `github_inspect_repository` | `full_name` | the repository |
| `github_invite` | `full_name`, `username` | `{}` |
| `github_search_users` | `query` | `logins` |

With a session:

| Command | Arguments | Result |
| --- | --- | --- |
| `snapshot` | – | `revision`, every entity file (`path`, `hash`, `data`), the resource files (`path`, `size`, `hash`) and the validation `diagnostics` |
| `revision` | – | `revision`; cheap to ask for, to notice changes made by a sync or another app |
| `apply` | `revision` from `snapshot`, `revision` or an earlier `apply`; `edits`: `[{"id", "data"}]` with the whole entity, `null` to delete; `resources`: `[{"path", "base64"}]`, `null` to delete | the new `revision` and `diagnostics`; all edits are validated and written as one transaction, or none is |
| `read_resource` | `path` | `base64`, `size` |
| `agenda` | – | `days` (`date`, `number`, `entries`, `nights`) and `unscheduled`; entries have `start` and `end` as instants, `zone`, `end_zone`, `kind`, `precision`, `role`, `status` and nested `steps` |
| `find` | `query`, optional `kinds` (`plan`, `booking`, `cost`, `place`, `task`, `packing`, `note`, `file`), `notes`, `limit` | `hits` with `kind`, `day`, `start`, `zone` and `matches` (`field`, `text`, `ranges` in UTF-16 code units) |
| `variants` | optional `remote` | every variant with its branch, title and state against the remote and the primary variant |
| `create_variant` | `branch` (such as `variants/early-start`), `title` | `{}`; creates it from the current one and switches to it |
| `switch_variant` | `branch` | `{}` |
| `delete_variant` | `branch`, optional `delete_remote`, `remote` | the deleted variant, which can be restored |
| `rollback` | `revision`: a commit from `list_versions` | `{}` |
| `merge_preview` | `branch`: a variant, or a remote branch such as `origin/main` after `sync` reported `diverged`; optional `resolutions` and `keep_both` (entity ids whose conflicts keep the current entity and add the incoming one as a copy, with everything nested below it) | `can_apply`, `conflicts` (`key`, `entity`, `path`, `kind`, `base`, `current`, `incoming`), `changes`, `diagnostics` |
| `watch` | optional `interval_seconds` (default 5), `enabled` (`false` stops it) | `watching`; sends `trip_changed` events (below) |
| `merge_apply` | `branch`, `resolutions`: `{"<key>": value}` for every conflict, optional `keep_both` and `message` | `commit`; merges into the current variant and records the merge in Git |
| `costs` | optional `currency` (converted with the saved exchange rates); amounts are decimal strings such as `"198.92"`, as in trip files | `expenses` (amount, method, payers, shares, debts, issues), `open_debts`, `balances`, `settle_up` (the fewest payments that settle everything), `totals` per currency, `categories`, `budgets` |
| `packing` | – | `lists` with their items (`group`, `quantity`, `everyone`, `assignees`, `packed_by`) |
| `bookings` | – | every booking with its plans (`items`), paid and planned expenses and `coverage` |
| `stays` | – | every stay with `first_night`, `nights`, `zone`, `check_in`, `check_out` and `issues`, and the `missing_nights` of the trip without a stay or an item through the night |
| `reminders` | optional `now` (ISO 8601 instant, default the current time) | what is due: `kind`, `entity`, `title`, `moment`, `urgent`, `dates` |
| `export` | `kind` (`plan`, `costs`, `packing`), `format` (`text`, `html` and `ics` for plans; `text` and `csv` for costs and packing), optional `person` | `text`, written in the session's culture |
| `import_document_data` | `name`, `base64`, optional `media_type` (from the name otherwise), `attach_to` | the new document's `id`, `media_type` and the new `revision` |
| `me` | optional `github_login`, `git_email` | the matching `person` and `name` with the `source` of the match (`github` or `email`), or `person: null`; all `travelers` with `claimed_by_someone_else` |
| `claim_person` | `person`, optional `github_login`, `git_name`, `git_email` | `{}`; "That's me": stores these identities on the person, so `me` finds them next time |
| `fetch_exchange_rates` | – | `date` and `currencies` of the European Central Bank's reference rates, saved to the session's `exchange_rates` file |
| `fetch_weather` | optional `refresh` | the weather by `days` and plan `items`, `sources` to credit and `problems` of services that failed; needs `weather_cache` |
| `initialize_git` | `name`, `email` | `{}`; starts Git history for a trip that has none |
| `set_identity` | `name`, `email` | `{}`; the Git author for versions, for example after `clone` |
| `sync_status` | optional `remote` | `branch`, `ahead`, `behind`, `has_local_changes`, `state` (`local_only`, `synced`, `local_changes`, `diverged`, `remote_changes`) |
| `sync` | optional `remote` (default `origin`) | `state`; fetches, takes fast-forwards and sends own versions |
| `add_remote` | `name`, `url` | `{}` |

Repair, history and variants that are not open:

| Command | Arguments | Result |
| --- | --- | --- |
| `git_health` | optional `remote` | `branch`, `detached`, an unfinished `operation` (`merge`, `rebase`, …), a `stale_lock` with `lock_age_seconds`, `trip_changes`, `other_files`, `ahead`, `behind` and a plain-text `summary` for bug reports, without addresses or credentials |
| `recover` | – | the new `revision`; finishes writes that were interrupted, for example when the app was stopped while saving |
| `remove_stale_lock` | optional `minimum_age_seconds` (default 60) | `removed`; a younger lock may belong to a running Git and stays |
| `abort_operation` | – | `{}`; cancels a merge or rebase started outside the app, losing what was changed during it, so ask first |
| `move_aside` | `paths` | the `folder` below the recovery folder the files were moved to; nothing is deleted |
| `restore_preview` | `commit` | the `changes` (`path`, `kind`: `added`, `changed` or `removed`) that make the trip look as at that version, and the `plan` to confirm them |
| `restore_apply` | `commit`, `plan` | `revision` and `diagnostics`; fails with `repair.changed` when the trip changed since the preview |
| `history` | optional `limit` (at most 100) | `versions` with `commit`, `parents`, `author`, `email`, `at`, `title`, `message`, `application_generated` and `refs` |
| `remotes` | – | `remotes` with `name` and `url` |
| `pull` | optional `remote` | `state`; takes the remote's versions only as a fast-forward and never sends, for updates in the background |
| `update_variant` | optional `title`, `archive` | `{}`; renames or archives the open variant with a small version |
| `deleted_variants` | – | the recycle bin: `reference`, `branch`, `title`, `deleted_at`, `commit` |
| `restore_variant` | `reference` | the restored `branch`, kept on this device until it is shared |
| `purge_deleted_variants` | optional `older_than_days` (default 30) | how many were `removed` |
| `keep_local_only` | `branch` | `{}`; a variant deleted on the remote stays here and is no longer uploaded |
| `share_variant` | `branch`, optional `remote` | `{}`; uploads such a variant and syncs it again |
| `sync_variant` | `branch`, optional `remote` | `state` of a variant that is not open; a `diverged` one needs a merge |
| `mark_merged` | `into_variant_id`, `into_commit` | `{}`; archives the open variant as taken over by another |
| `copy_trip` | `destination` (an empty folder) | the copy's `root`, with history, refs and remotes |

Plan edits beyond whole entities. Those that change the trip are one transaction each and return `revision` and `diagnostics`:

| Command | Arguments | Result |
| --- | --- | --- |
| `vocabulary` | – | the standard `schedule_categories`, `transport_types`, `statuses`, `expense_categories` and the trip's `custom_expense_categories` |
| `suggest_category` | `title` | the expense `category` for a title, or null |
| `delete_preview` | `ids` | what deleting them changes: `deleted`, `dependents` (such as comments on them), `updated` (references removed), `removed_resources` and `unused` places, notes, documents and bookings |
| `delete_entities` | `ids` | deletes them as previewed; add ids from `unused` to delete those too |
| `duplicate` | `id`, optional `title` | the copy's `id`, with everything nested in it |
| `schedule` | `id`, `local` (such as `2027-03-28T10:00:00`), `timezone`, optional `minutes` (default 60), `offset` | an exact time, saved with its offset; `time.gap` for a local time that is skipped and `time.ambiguous` for one that is repeated, unless `offset` (such as `+01`) says which |
| `move_to_day` | `id`, `day`, optional `offset` | the same local time and length on another day, with the same errors; an item without a time keeps its kind of day |
| `comments` | `target` | `comments` with `author`, `author_name`, `created_at` and `body`, oldest first |
| `add_comment` | `target`, `author` (a person), `body`, optional `mentions` (people) | the comment's `id` |
| `calendar` | optional `stays_as_check_in_out` | `entries` for a device calendar: `uid`, `item`, `title`, `role`, `kind`, `all_day`, `start`, `end`, `date`, `zone`, `location`, `description`, `status` |
| `cost_upgrade_preview` | optional `fallback_currency` | whether an upgrade of older cost data is `needed` and its `changes` |
| `cost_upgrade_apply` | optional `fallback_currency`, `message` | saves it; a `version` of its own when nothing else was unsaved |
| `read_mail` | `id` of an e-mail document, optional `allow_remote_images` | `subject`, `from`, `to`, `cc`, `date`, `text`, `html` cleaned for display (no scripts, frames or forms; remote images only when allowed), `remote_images`, `inline_images` and `attachments` as base64 |
| `read_pass` | `id` of a Wallet pass, optional `language` (default the session's) | `description`, `organization`, `style`, `barcode` (`message`, `format`, `alt_text`, `encoding`), `fields` (`group`, `label`, `value`), `relevant`, `background`, `foreground` |
| `plan_material` | `id` of an activity, note or document, optional `local`, `timezone`, `minutes`, `offset` | the activity's `id`; a note or document becomes a new activity that shows it, an idea with a time becomes planned, without `local` it is unscheduled |
| `align_stay` | `id` of a stay | makes check-in, check-out and time agree, filling a missing one from the nights; `stay.unscheduled` for an item without nights |
| `replace_document` | `id`, `source` (a file path), `media_type`, optional `allow_large` | the document's `id` and the new `revision` |

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

Edits go through `apply` with the revision they were based on. If the trip changed in the meantime, because of a sync, another app or a second window, `apply` fails with `repository.changed` and nothing is written. Resources can be written under `documents/` and `assets/sha256/`. Entity files are written as entities, never as resources.

To answer a merge conflict, put the value that should win under the conflict's `key` in `resolutions`. That is `current`, `incoming` or a value the user edited. Ask `merge_preview` again until `can_apply` is true, then call `merge_apply` with the same `resolutions`. After a `sync` that reports `diverged`, merge the remote branch the same way and `sync` again.

## Events

`tr_set_event_handler(handler, context)` sets a second callback for events, as JSON, on a TravelRepo thread:

- `{"type":"clone_progress","call":…,"total_objects":…,"received_objects":…,"indexed_objects":…,"received_bytes":…,"checkout_completed":…,"checkout_total":…}` while `clone` runs, about ten times a second and always at the end of each phase. A clone from a local path copies objects without a transfer and reports only the checkout.
- `{"type":"trip_changed","session":…,"revision":…}` for a session that `watch`es, when its files changed, for example through a sync or another app. TravelRepo compares file sizes and times every interval and only reads the trip again when they changed. Apps ask for `revision` when they come to the foreground or after a sync, and read again when it differs.

## GitHub

The GitHub commands use the GitHub App configured with `github_configure` (see [providers](providers.md)). TravelRepo keeps the token only in memory. The app stores the token that `github_poll_sign_in` returns, for example in the Keychain, and passes it to `github_set_token` on every start. Poll no faster than the `interval`, and wait 5 seconds longer after `slow_down`.

## Git credentials

Without a handler of its own, Git uses the GitHub token from `github_set_token` or the sign-in for `github.com`. With a handler, Git asks the app for credentials through the handler set with `tr_set_credential_handler`. The handler gets a request id and the origin, such as `https://github.com`. The app answers with `tr_credential_reply(request, "{\"username\":\"x-access-token\",\"password\":\"…\"}")`, or with `NULL` when it has none. The token comes from wherever the app keeps it, such as the Keychain. TravelRepo does not store it.

## Checks

`tests/TravelRepo.Tests/NativeTests.cs` calls the exported functions through function pointers. It also checks that every MCP tool is a command with the same arguments. `eng/aot-smoke.py` (see [development](development.md#nativeaot)) compiles the C interface with NativeAOT and runs it, including Git versions and a sync through libgit2. The CI job `ios` builds `TravelRepo.xcframework` on macOS and runs the `TravelRepoKit` tests on an iOS simulator.
