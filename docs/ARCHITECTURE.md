# Jourfold / TravelRepo Architecture v0.1

Status: Draft  
Date: 2026-09-28  
Reference client: Jourfold  
Open format and SDK: TravelRepo

## 1. Architectural goals

The architecture must support:

- a first-class native desktop client for Windows and Linux,
- real Git repositories without requiring users to understand Git,
- offline-first operation,
- generic Git remotes over HTTPS and SSH,
- deep GitHub integration,
- domain-aware variants, comparisons, and merges,
- an open, independently implementable TravelRepo specification,
- third-party clients built directly on the TravelRepo libraries,
- future web, Android, iOS, and tablet clients,
- future MCP and agent access through the same domain API,
- plugin/provider extensibility,
- no duplicate domain logic across clients,
- strong forward compatibility and no silent data loss.

Jourfold is a client of TravelRepo, not the owner of the data model.

## 1.1 Decisions locked for v0.2

- Real Git CLI semantics are the reference Git backend.
- On Windows, prefer an existing compatible Git and fall back to bundled MinGit.
- GitHub supports a recommended least-privilege GitHub App mode and an optional broader discovery mode.
- Proven conflict-free semantic merges may apply automatically after a short review/cancel grace period.
- External plugin UI is declarative in v1; Jourfold renders it.
- YAML comment preservation is best-effort, not part of semantic compatibility.
- v1 contains no telemetry or automatic reporting; issue reporting is explicitly user initiated and previewable.

## 2. Runtime and language

Use C# on .NET 10 LTS.

All reusable TravelRepo libraries should target `net10.0` for v1 and avoid desktop-only APIs.

Do not add legacy target frameworks merely for theoretical compatibility. The file format is the interoperability boundary for non-.NET consumers.

Platform-specific code belongs behind interfaces and outside TravelRepo.Core.

## 3. UI framework

Use Avalonia for Jourfold.

Initial supported client platforms:
- Windows
- Linux

Future targets:
- WebAssembly
- Android
- iOS/iPadOS

The initial desktop app should not compromise its UX merely to make the future web client identical.

Use XAML for views and `CommunityToolkit.Mvvm` for view models.

Do not use ReactiveUI unless a concrete requirement appears that CommunityToolkit.Mvvm cannot satisfy.

## 4. Proposed solution structure

```text
/
├── src/
│   ├── TravelRepo.Core/
│   ├── TravelRepo.Serialization/
│   ├── TravelRepo.Repository/
│   ├── TravelRepo.Git/
│   ├── TravelRepo.Merge/
│   ├── TravelRepo.Providers.Abstractions/
│   ├── TravelRepo.Providers.GitHub/
│   ├── TravelRepo.Plugins.Abstractions/
│   ├── TravelRepo.Cli/
│   │
│   ├── Jourfold.Application/
│   ├── Jourfold.Infrastructure/
│   ├── Jourfold.Desktop/
│   └── Jourfold.PluginHost/
│
├── schemas/
│   └── travelrepo/
│       └── 1.0/
│
├── samples/
│   ├── minimal/
│   ├── timezone-flight/
│   ├── nested-roadtrip/
│   ├── multi-person/
│   └── merge-scenarios/
│
├── tests/
│   ├── TravelRepo.Core.Tests/
│   ├── TravelRepo.Serialization.Tests/
│   ├── TravelRepo.Repository.Tests/
│   ├── TravelRepo.Git.IntegrationTests/
│   ├── TravelRepo.Merge.Tests/
│   ├── TravelRepo.ConformanceTests/
│   ├── TravelRepo.Providers.GitHub.Tests/
│   ├── Jourfold.Application.Tests/
│   └── Jourfold.Desktop.Tests/
│
├── assets/
│   ├── branding/
│   └── fonts/
│
├── docs/
├── eng/
├── .github/
└── Jourfold.sln
```

## 5. Dependency boundaries

### TravelRepo.Core

Owns:
- domain entities,
- IDs and references,
- time model,
- money model,
- constraints,
- commands and queries,
- validation concepts,
- component registry abstractions,
- domain events,
- diff primitives.

Must not depend on:
- Avalonia,
- Jourfold,
- GitHub,
- Git process execution,
- SQLite,
- desktop APIs,
- UI models.

### TravelRepo.Serialization

Owns:
- YAML parsing and writing,
- schema validation integration,
- preservation of unknown fields,
- canonical serialization rules,
- format-version handling.

Depends on:
- TravelRepo.Core.

### TravelRepo.Repository

Owns:
- filesystem repository access,
- entity loading,
- atomic writes,
- asset/blob storage,
- repository snapshots,
- local write transactions,
- repository-level validation.

Depends on:
- TravelRepo.Core,
- TravelRepo.Serialization.

### TravelRepo.Git

Owns:
- Git abstraction,
- commits/versions,
- branch/variant operations,
- remotes,
- fetch/pull/push coordination,
- Git config access,
- commit trailers,
- branch enumeration,
- tree snapshots without checkout,
- Git authentication broker integration.

Depends on:
- TravelRepo.Repository.

### TravelRepo.Merge

Owns:
- semantic diff,
- semantic three-way merge,
- merge plans,
- typed conflicts,
- component-specific merge handlers.

Depends on:
- TravelRepo.Core,
- TravelRepo.Repository.

It may consume Git-provided base/ours/theirs snapshots through abstractions, but merge semantics must not depend on Git CLI output.

### TravelRepo.Providers.Abstractions

Owns capability-based provider interfaces such as:
- repository discovery,
- authentication,
- repository creation,
- privacy inspection,
- collaborator management,
- user search,
- sharing.

### TravelRepo.Providers.GitHub

Implements GitHub-specific capabilities.

Must not depend on Avalonia.

### TravelRepo.Plugins.Abstractions

Contains the stable plugin contract and DTOs.

It must remain small and versioned separately from the Jourfold UI.

### TravelRepo.Cli

A first-class client of the same public libraries.

The CLI is not a debug-only utility.

### Jourfold.Application

Owns client use cases and orchestration:
- library/recent trips,
- current workspace,
- sync state,
- navigation intents,
- user-facing command orchestration,
- undo/redo integration,
- warnings and notifications.

No Avalonia controls or platform-specific APIs.

### Jourfold.Infrastructure

Owns Jourfold-local services:
- SQLite local database,
- search index,
- app settings,
- secure credential adapters,
- local identity defaults,
- cache,
- thumbnails,
- recovery data,
- OS integration abstractions.

### Jourfold.Desktop

Owns:
- Avalonia views,
- view models,
- design system,
- desktop platform integrations,
- command palette,
- popouts,
- accessibility behavior.

### Jourfold.PluginHost

Separate executable used to load external .NET plugins out of process.

## 6. Git engine

Define an `IGitBackend` interface so Git implementation details never leak into TravelRepo.Core.

The v1 reference backend should use the real Git command-line implementation, not reimplement Git semantics.

Reasons:
- maximum compatibility with normal Git repositories,
- multiple remotes behave exactly as Git users expect,
- SSH agents and normal OpenSSH behavior are easier to preserve,
- credential-helper infrastructure can be reused,
- external Git users see the same repository behavior,
- avoids duplicating merge/ref/config behavior in application code.

### Windows

Windows 11 includes PowerShell and provides OpenSSH as a Windows capability, but it does not provide `git.exe` as a built-in PowerShell command or guaranteed operating-system component.

Jourfold therefore uses this resolution order:

1. Detect a compatible system Git installation and prefer it.
2. If no suitable system Git exists, use a pinned MinGit distribution bundled with Jourfold.
3. Allow Advanced Settings to explicitly select a compatible Git executable.

Do not require the user to install Git.

The bundled MinGit is a compatibility fallback, not a second Git model. Jourfold still operates ordinary Git repositories and reuses normal user Git configuration where compatible.

### Linux

Packaging strategy:
- distro packages may declare an appropriate Git dependency,
- portable distributions should bundle a tested Git where practical,
- Advanced Settings may select a system Git executable.

The ordinary user must not need to configure Git manually.

### Process safety

All Git invocations:
- use process argument arrays, never shell-concatenated commands,
- disable terminal prompts unless Jourfold intentionally brokers them,
- use machine-readable Git output where available,
- prefer NUL-delimited formats for paths,
- set a deterministic locale for parsed output,
- enforce cancellation and timeouts,
- redact secrets from logs.

Do not parse localized human-facing Git prose.

## 7. Git credentials

Create an `ICredentialBroker`.

### SSH

Prefer existing infrastructure:
- `ssh-agent`,
- `SSH_AUTH_SOCK`,
- normal user SSH config,
- normal key files,
- known_hosts.

Jourfold should not copy private SSH keys into its own storage.

If a user explicitly selects a private key path, store the path/configuration, not a duplicate of the key.

### HTTPS

First try compatible configured Git credential helpers.

Jourfold may provide its own credential helper/broker when needed.

Credentials and tokens must:
- never be embedded in remote URLs,
- never be passed as visible command-line arguments,
- never be written to TravelRepo,
- be stored through the operating-system secure credential facility.

## 8. Git configuration

Per-trip Git identity should use repository-local Git configuration.

Jourfold must never silently modify global Git configuration.

It may offer the user's global identity as a suggested default.

Jourfold-specific settings should not abuse arbitrary Git config keys when normal TravelRepo metadata or local application state is more appropriate.

## 9. Versions and sync

Autosave and Git version creation remain separate.

```text
UI edit
-> domain command
-> atomic TravelRepo write
-> local dirty working tree
-> explicit Create Version
-> Git commit
-> automatic sync
```

Background sync rules:

1. Fetch may happen while the working tree is dirty.
2. Never destructively reset or checkout over dirty local changes.
3. If the local branch is clean and can fast-forward, it may do so automatically.
4. Never force-push automatically.
5. Divergence requires semantic merge handling.
6. If TravelRepo can prove that a semantic merge is conflict-free, Jourfold shows a small non-modal merge notice with a short grace period and actions such as `Review` and `Cancel`. If the user does not intervene, the merge is applied automatically.
7. Automatic semantic merges are recorded clearly in human-friendly history.
8. Any unresolved merge becomes a user-visible merge plan, not raw Git conflict-marker UX.
9. Push only committed versions, never raw autosave state.

Repository Git write operations are serialized per repository.

## 10. Semantic diff and merge

TravelRepo merge works on domain entities identified by UUID.

It must not treat file paths as entity identity.

Three-way merge inputs:
- merge base,
- ours,
- theirs.

Rules include:
- add on one side: merge automatically,
- delete on one side with no conflicting edit: merge automatically,
- same scalar changed on one side only: merge automatically,
- same scalar changed differently on both sides: typed conflict,
- maps: recursively merge by semantic key,
- ID-based sets: merge by entity identity,
- ordered lists: use type-specific list semantics,
- unknown fields: generic structural three-way merge,
- text/Markdown: line-based three-way merge with a typed conflict result,
- binary blobs: select ours, theirs, or both; never byte-merge arbitrary media.

The merge engine produces a `MergePlan`.

A MergePlan can be:
- inspected,
- rendered by any client,
- resolved without UI-specific code,
- applied atomically.

Do not write unresolved `<<<<<<<` conflict markers into canonical TravelRepo files as the primary Jourfold workflow.

## 11. YAML and schema implementation

YAML remains the human-editable canonical representation.

Use YamlDotNet for YAML parsing/serialization.

Use JSON Schema 2020-12 as the normative machine-readable schema.

Validation pipeline:

```text
YAML
-> safe YAML document model
-> JSON-compatible node representation
-> JSON Schema validation
-> TravelRepo semantic validation
```

Schema validation and semantic validation are separate.

JSON Schema validates structure.

TravelRepo.Core validates cross-entity and semantic rules such as:
- missing references,
- invalid nesting,
- temporal contradictions,
- impossible inheritance states,
- duplicate IDs.

### Unknown data

Serialization must retain unknown compatible fields and extension namespaces.

Strongly typed deserialization alone is not sufficient.

The serializer should keep a document representation/envelope that allows understood fields to be edited without dropping unknown nodes.

YAML comments are non-semantic. Preserving them is desirable where practical but is not a v1 interoperability guarantee. No required TravelRepo information may exist only in a YAML comment.

## 12. Time implementation

Use Noda Time internally.

Do not use `DateTime` as the core travel-time abstraction.

Canonical exact zoned time contains:
- local date/time,
- IANA timezone,
- optional UTC offset disambiguator.

Example:

```yaml
local: 2027-10-31T02:30:00
timezone: Europe/Berlin
offset: "+01:00"
```

The `offset` is normally omitted.

It becomes required when the local wall-clock time is ambiguous because of a timezone transition.

A nonexistent local wall-clock time is invalid unless represented as an approximate/flexible constraint rather than an exact zoned instant.

This preserves both:
- the human-facing local time,
- the chosen instant when DST makes the local time ambiguous.

## 13. Local Jourfold database

Canonical trip data never lives in SQLite.

Jourfold uses a local SQLite database only for derived/client-specific data such as:
- known repositories,
- recent trips,
- last opened timestamp,
- cached trip title/cover,
- sync status cache,
- local UI preferences,
- preferred remote,
- local identity profiles,
- plugin installation metadata,
- provider account metadata excluding secrets,
- derived search index.

Deleting the Jourfold local database must not destroy trip content.

The database should be rebuildable from repositories and user settings wherever practical.

## 14. Search

Use a derived local full-text index.

Index:
- titles,
- places,
- Markdown,
- comments,
- tasks,
- booking references where appropriate,
- tags,
- filenames,
- URLs.

PDF OCR and image OCR are not v1 requirements.

Search results always resolve back to canonical TravelRepo entities/documents.

## 15. Filesystem safety

All application-generated writes should be atomic where supported.

For multi-file domain operations:
1. validate the requested domain command,
2. stage writes,
3. check optimistic-concurrency file hashes,
4. atomically replace files,
5. refresh repository state.

External file edits are allowed.

File watching is advisory only. The app must rescan/validate at important boundaries because OS file watchers may miss events.

Maintain recoverable local autosave/recovery metadata outside the repository.

Uncommitted deletion remains recoverable locally until a version is created.

## 16. Provider model

Provider APIs are capability-based.

Do not require every provider to implement every feature.

Example capability interfaces:

```text
IProviderAuthentication
IRepositoryDiscoveryProvider
IRepositoryCreationProvider
IRepositoryPrivacyProvider
ICollaboratorProvider
IUserSearchProvider
IShareProvider
```

Generic Git has no requirement to implement collaborator management.

The UI changes actions based on capabilities:
- GitHub: Invite / Manage access
- generic remote: Share
- local-only trip: Publish

## 17. GitHub provider

GitHub behavior belongs in `TravelRepo.Providers.GitHub`, not in Jourfold.Desktop.

Jourfold supports two GitHub access modes behind the same provider capability interfaces.

### 17.1 Recommended mode: GitHub App

Use a GitHub App with fine-grained permissions and short-lived tokens.

Desktop authentication should support GitHub's device authorization flow.

The user may install the app for:
- all repositories in an account/organization, or
- selected repositories.

Jourfold automatically discovers TravelRepo repositories within the installations and repositories to which the GitHub App has been granted access.

This is the recommended mode because access can be limited to only the repositories and permissions Jourfold requires.

### 17.2 Optional extended discovery mode

Jourfold may additionally support an explicitly opt-in GitHub OAuth integration for users who want discovery across repositories accessible to their GitHub user account beyond GitHub App installations.

GitHub's classic OAuth `repo` scope is broad: it gives access to public and private repositories and is not a read-only source-code permission. Therefore:

- extended discovery is not the default,
- Jourfold must explain the permission difference before authorization,
- the UI must not imply that the broader authorization is required,
- users may remain entirely on the GitHub App path,
- direct repository URLs remain supported regardless of discovery mode.

If GitHub later provides a narrower user-wide discovery mechanism, prefer it over the broad `repo` OAuth scope.

### 17.3 Provider responsibilities

Responsibilities include:
- user authentication,
- repository discovery according to the selected access mode,
- TravelRepo detection,
- open/clone repository,
- create private repository by default,
- privacy inspection,
- strong warning for public travel repositories,
- collaborator invitations,
- optional GitHub user search,
- share links/context.

Tokens are stored through `ICredentialBroker`.

## 18. Plugins

External plugins run out of process.

Initial plugin packaging direction:

```text
plugin.jourfold/
├── jourfold.plugin.json
├── Plugin.dll
├── dependencies/
├── assets/
└── LICENSES/
```

Plugin manifest includes:
- stable plugin ID,
- version,
- minimum plugin API version,
- entry assembly,
- capabilities,
- declared permissions.

Start one host process per external plugin for crash isolation.

The host loads .NET assemblies in a dedicated AssemblyLoadContext.

Application-to-host communication should use a versioned RPC contract.

Preferred v1 direction: JSON-RPC over a local process stream/socket, so the wire contract is not permanently tied to .NET runtime types.

### Security statement

Out-of-process execution provides isolation from crashes and dependency conflicts.

It is not a security sandbox by itself.

Until OS-level sandboxing is implemented, plugin permissions are declarations and consent UX, not a guarantee that malicious native code cannot exceed them.

External plugins should therefore be treated as trusted installed software in v1.

## 19. Plugin UI

Preferred v1 direction:

Plugins may provide:
- component schemas,
- icons,
- commands,
- actions,
- provider data,
- declarative form/editor metadata.

Plugins should not inject arbitrary Avalonia controls into the main Jourfold process in v1.

Jourfold renders plugin data using its own design system.

This preserves:
- process isolation,
- accessibility,
- cross-platform behavior,
- future web/mobile portability.

## 20. Jourfold UI architecture

Use CommunityToolkit.Mvvm.

Views must remain thin.

Domain operations are invoked through application services/commands.

Do not duplicate validation or business rules in view models.

### Design system

Central resource dictionaries define:
- typography,
- spacing,
- radii,
- borders,
- elevation,
- colors,
- motion,
- density.

Typography:
- Plus Jakarta Sans for UI,
- Outfit-derived Jourfold wordmark converted to SVG paths,
- no runtime dependency on Outfit for the wordmark.

UI supports:
- Light,
- Dark,
- System,
- Comfortable density,
- Compact density.

Dark mode uses deep navy rather than generic black.

## 21. Main desktop layout

Default wide layout:

```text
Navigation sidebar | Main workspace | Detail inspector
```

Detail inspector:
- docked on timetable by default,
- may become a popout/overlay in spatial views such as Map,
- may be detached where useful.

Inbox:
- prominent toggle,
- normally collapsible,
- pinnable as a persistent panel.

## 22. Default trip view

The trip's default opening view is a user/application preference, not canonical trip data.

Recommended options:
- Remember last used view globally
- Timetable
- Map
- List

Recommended default: `Remember last used view`.

Clients may remember temporary per-window state locally, but opening-view preference must not be committed to the trip repository.

## 23. Timetable layouts

The timetable is a schedule-first view rather than a generic calendar clone.

Primary layout:
- time on the vertical axis,
- days as columns when space allows,
- adaptive number of visible days,
- clear timezone transitions,
- nested blocks,
- unplanned items available from Inbox.

Alternative layouts:
- horizontal journey timeline,
- participant lanes,
- compact list.

### Participant lanes

Do not create a separate lane for every participant combination.

If grouping by participant:
- one lane per person,
- optional Unassigned lane,
- a shared item may visually span multiple adjacent participant lanes or render linked representations of the same entity.

There is still exactly one underlying schedule item.

## 24. Map synchronization

Map and schedule selection use the same selection service.

Selecting a mapped schedule item/place updates:
- current selection,
- inspector,
- timeline emphasis.

Selecting a schedule item highlights the corresponding map place/route where available.

This is a client interaction concern, not a TravelRepo schema requirement.

## 25. Command palette and quick add

Provide a global command/search surface using `Ctrl+K`.

Provide quick-add commands for common entities/components.

Keyboard shortcuts are routed through command IDs rather than directly invoking view code so they remain remappable and testable.

## 26. i18n

English is the source language.

German ships in v1 as a complete second localization and acts as the first real i18n test.

No user-facing string should be hard-coded directly into views.

Trip content remains in the trip's chosen content language and is not automatically translated.

## 27. Accessibility

v1 requirements:
- complete keyboard navigation,
- visible focus,
- screen-reader semantics,
- logical reading order,
- scalable text,
- high-contrast awareness,
- no color-only status communication,
- reduced-motion support where available.

## 28. Platform integration

Jourfold has one recognizable design language while respecting platform behavior.

### Windows
- Windows 11 conventions where appropriate,
- system titlebar/window behavior,
- platform file pickers,
- proper taskbar integration.

### Linux
- support GNOME and KDE environments as first-class desktop environments,
- use system theme and scaling information where available,
- use native/portal file dialogs through Avalonia/platform services,
- avoid pretending to be a GTK or Qt application,
- support X11/XWayland as the reliable baseline,
- treat native Wayland support according to the stability of the Avalonia version used.

Platform integration code lives behind interfaces.

## 29. Secrets

Define:

```text
ISecretStore
```

Implementations:
- Windows: operating-system credential facilities,
- Linux: Secret Service-compatible secure storage where available.

If secure persistent storage is unavailable:
- do not silently fall back to plaintext,
- allow session-only credentials or explain the limitation.

## 30. Logging, privacy, and issue reporting

Use structured logs with secret redaction.

Logs must not contain:
- access tokens,
- passwords,
- SSH private key content,
- full secret environment variables.

Travel content may itself be sensitive.

v1 policy:
- no behavioral analytics,
- no travel-data telemetry,
- no automatic crash reporting,
- no automatic upload of logs or diagnostics.

Jourfold may provide a user-initiated `Report a problem` workflow.

That workflow may:
- collect app version, OS/runtime version, relevant error codes, and sanitized logs,
- redact repository paths, people, booking data, URLs containing credentials, and trip content by default,
- show the exact report content to the user before anything leaves the device,
- allow the user to remove or add attachments explicitly,
- copy the report to the clipboard,
- open a pre-filled GitHub Issue page,
- optionally create the issue through an authenticated GitHub integration only after an explicit user action.

Issue-report assistance is not telemetry.

## 31. Dependency injection

Use Microsoft.Extensions.DependencyInjection.

Keep DI registration near composition roots.

Do not use a service locator from domain code.

## 32. Error model

Libraries return typed errors/results for expected domain failures.

Exceptions are reserved for unexpected or infrastructure failures.

User-facing errors should carry:
- stable code,
- human-readable localized message key,
- technical detail for Advanced Mode,
- recoverability/action hints.

## 33. Testing strategy

### Unit tests
Cover:
- time calculations,
- inheritance,
- constraints,
- references,
- money,
- component semantics.

### Property-based tests
Strongly recommended for:
- timezone transitions,
- serialization round trips,
- merge invariants,
- UUID/reference behavior.

### Conformance tests
Validate:
- every sample repo,
- schema compliance,
- unknown-field round trips,
- version migration,
- other-client fixtures where available.

### Git integration tests
Use real temporary Git repositories and the actual Git backend.

Test:
- init,
- clone,
- fetch,
- push,
- multiple remotes,
- divergent branches,
- rename,
- external commits,
- dirty tree,
- branch discovery,
- commit trailers,
- SSH/HTTPS seams with mockable credential broker.

### Merge tests
Maintain fixture directories for:
- one-sided edits,
- independent edits,
- same-field conflict,
- delete-vs-edit,
- nested blocks,
- unknown plugin fields,
- Markdown conflicts,
- binary references.

### UI tests
Use Avalonia headless testing for deterministic component/view interaction where possible.

Do not rely exclusively on screenshot pixel tests.

Use end-to-end smoke tests on Windows and Linux build artifacts.

## 34. CI

GitHub Actions should run at minimum:

```text
restore
build
format/lint verification
unit tests
conformance tests
Git integration tests
merge fixture tests
desktop headless tests
package
license/notices checks
dependency/security checks
```

Run the main matrix on:
- Windows,
- Ubuntu/Linux.

Add additional Linux packaging/smoke coverage for supported distribution formats.

Use dependency caching but never make correctness depend on caches.

## 35. Build reproducibility

Pin:
- .NET SDK,
- Avalonia version,
- NuGet dependencies,
- bundled Git/MinGit version,
- font files,
- schema version.

Use lock files where practical.

Generate platform icons and raster assets from canonical SVG brand sources.

Keep third-party licenses and notices in the repository and distribution.

## 36. Packaging direction

### Windows
- self-contained build,
- x64 as mandatory v1 architecture,
- ARM64 where build/test quality is sufficient,
- installer plus a portable artifact if practical,
- bundled MinGit.

### Linux
Target user-friendly native packaging.

Initial direction:
- AppImage or equivalent portable artifact,
- distro package(s) where practical,
- x64 mandatory,
- ARM64 as a desirable additional target.

Exact packaging formats remain a release-engineering decision and should not leak into TravelRepo libraries.

## 37. Future web architecture

The web client uses TravelRepo.Core semantics but not necessarily the desktop Git transport.

Use a repository backend abstraction.

Desktop:
```text
filesystem + real Git + SSH/HTTPS
```

Web:
```text
browser storage/cache + provider/backend adapter
```

Generic SSH Git in a browser is not a v1 web requirement.

GitHub/GitLab/Forgejo direct provider integrations or an optional bridge service may provide web synchronization.

## 38. Future mobile architecture

Mobile begins as a companion/editor:
- schedule viewing,
- map,
- documents,
- tasks,
- comments,
- basic editing,
- sync.

Tablet may expose the fuller editor.

Advanced repository management and plugin management do not need to be feature-identical on mobile.

## 39. Future MCP/agent architecture

Do not put AI logic into TravelRepo.Core.

Expose domain operations as stable commands/queries.

A future MCP server can adapt those commands without bypassing validation.

Potential commands:
- list trip structure,
- query schedule,
- add/move item,
- create variant,
- compare variants,
- create version,
- validate,
- export.

## 40. Important implementation rule for AI-generated development

An implementation agent should:

- make reasonable engineering decisions autonomously,
- follow this architecture unless concrete evidence requires deviation,
- document significant deviations,
- collect genuinely blocking questions and ask them together at a natural checkpoint,
- never interrupt implementation for preferences it can resolve itself,
- prefer a working coherent vertical implementation over fake breadth,
- not replace difficult requirements with silent mocks or TODOs,
- keep UI language natural and concise,
- avoid generic AI-written marketing/copy patterns and formulaic prose.
