# Jourfold v1 Acceptance Criteria

Status: Draft for implementation handoff  
Scope: First functional Windows and Linux release  
Product: Jourfold  
Data format / SDK: TravelRepo

This document defines what "functional v1" means.

A feature is complete only when it is implemented end-to-end, tested at the appropriate layer, and usable without hidden manual setup beyond explicitly documented external credentials or provider authorization.

## 1. Release gate

Jourfold v1 is accepted only if all items marked **MUST** below are satisfied.

Items marked **SHOULD** may be deferred only when:
1. the core architecture already supports them,
2. the deferral is documented,
3. no visible UI pretends that the feature works.

Do not replace unfinished MUST requirements with mocks, dead buttons, placeholder dialogs, or TODO-only implementations.

## 2. Supported platforms

### MUST

- Build and run as a native desktop application on Windows 11 x64.
- Build and run as a native desktop application on a supported mainstream Linux x64 environment.
- Exercise Linux UI behavior on at least one GNOME-based and one KDE-based environment in release validation.
- Support HiDPI/scaling.
- Support mouse and keyboard as first-class input.
- Support Light, Dark, and System themes.
- Support Comfortable and Compact density.
- Use bundled Plus Jakarta Sans for Jourfold UI typography.
- Use canonical SVG branding assets.

### SHOULD

- Produce ARM64 artifacts where the toolchain and CI quality are sufficient.
- Produce a portable Windows artifact in addition to an installer.
- Produce at least one user-friendly portable Linux artifact.

## 3. First-run behavior

### MUST

A clean Jourfold installation must:
- start without requiring a Jourfold account,
- not require a remote,
- not require Git knowledge,
- detect a compatible system Git,
- fall back to the bundled/tested Git distribution where defined by the platform packaging strategy,
- show an empty trip library with clear actions to create or open a trip.

No telemetry consent dialog is shown because v1 contains no telemetry.

## 4. Trip library

### MUST

The start screen provides:
- Recent trips,
- known local trips,
- known remote-backed trips,
- last-opened information,
- local/remote/sync state where known.

Trip entries support:
- title,
- optional cover,
- effective trip dates when derivable,
- useful location summary,
- last changed information.

A missing local cache/database must be rebuildable without losing canonical trip data.

### SHOULD

For remote-backed repositories, show recent remote activity such as:
- last remote update,
- newly discovered variant activity.

## 5. Create trip

### MUST

`New Trip` creates:
- a valid TravelRepo repository,
- a real Git repository,
- a `main` branch,
- valid `travel.yaml`,
- initial schema metadata,
- initial primary variant metadata.

The creation wizard:
- requires only a title,
- allows optional content language,
- dates,
- timezone,
- participants,
- publishing choice,
- can skip all optional pages.

A new trip may remain local-only indefinitely.

## 6. Open existing trip

### MUST

Jourfold can open:
- a local TravelRepo working tree,
- a cloned TravelRepo,
- a compatible repository modified by standard Git tooling.

On open it:
- recognizes `travel.yaml`,
- validates structural schema,
- performs semantic validation,
- preserves unknown compatible data,
- reports external/unexpected changes in human language.

Malformed data must not be silently discarded.

## 7. Repair and rollback

### MUST

When a repository cannot be safely interpreted, Jourfold:
1. explains the problem,
2. attempts non-destructive repair when a known repair exists,
3. offers rollback to a safe Git state when available,
4. never silently deletes unknown extension data.

A repair operation that changes canonical data must itself be reviewable and versionable.

## 8. Autosave

### MUST

Ordinary edits are written locally without a manual Save action.

Autosave:
- uses safe/atomic write strategy where supported,
- never requires a commit,
- survives ordinary application restart/crash scenarios through repository state and recovery support,
- clearly distinguishes local dirty state from committed versions.

The default UI uses wording such as:
- Saved locally,
- Local changes,
rather than forcing Git terminology.

## 9. Undo / redo

### MUST

Jourfold provides local Undo and Redo for ordinary domain edits.

Undo/redo:
- is independent from Git commit history,
- handles logical multi-field operations as coherent actions where appropriate,
- must not apply unsafe inverse operations after state-invalidating external changes,
- explains when external changes invalidate undo history.

## 10. Create Version

### MUST

Jourfold has an explicit `Create Version` action.

The version UI provides:
- an automatically suggested natural-language message,
- editable message,
- semantic summary of changes,
- optional expanded details.

Creating a version:
- creates a real Git commit,
- adds TravelRepo machine-readable commit trailers,
- does not depend on exact commit message text for identification.

No normal Save button is required.

## 11. Human-readable history

### MUST

History is presented primarily as travel/application activity rather than a Git log.

It can show entries such as:
- person changed accommodation,
- person added transport,
- Jourfold merged a variant,
- external Git change.

Advanced Mode exposes:
- commit hash,
- refs/branch,
- Git author/committer,
- trailers,
- technical parent information.

## 12. Git interoperability

### MUST

The same repository remains usable with standard Git.

Jourfold must tolerate:
- externally created commits,
- external file edits that remain schema-compatible,
- branch operations,
- multiple remotes.

The reference Git backend uses real Git CLI semantics behind an abstraction.

### MUST NOT

- silently alter global Git config,
- store credentials in remote URLs,
- require a proprietary repository wrapper,
- make a TravelRepo cease to be a normal Git repository.

## 13. Git identity

### MUST

Each trip can use repository-local Git identity.

Jourfold:
- may suggest global Git identity,
- may map Git identities to TravelRepo people,
- never silently changes global Git identity.

TravelRepo people remain trip-local and do not require a Jourfold account.

## 14. HTTPS and SSH remotes

### MUST

Jourfold supports generic Git remotes over:
- HTTPS,
- SSH.

SSH should reuse:
- normal SSH configuration,
- agent infrastructure,
- existing keys where possible.

HTTPS may reuse compatible credential helpers.

Secrets are stored through secure OS credential facilities when persistent storage is needed.

No plaintext fallback is permitted without explicit user understanding.

## 15. Multiple remotes

### MUST

A repository may have multiple Git remotes.

Jourfold:
- does not destroy unknown remotes,
- allows inspection in Advanced Mode,
- can choose a preferred sync/share remote locally,
- does not make single-remote assumptions in core libraries.

## 16. Background synchronization

### MUST

For configured remotes, Jourfold can:
- fetch automatically,
- fast-forward safely when appropriate,
- push committed local versions,
- detect divergence.

It must never:
- overwrite dirty local changes,
- force-push automatically,
- push uncommitted autosave state.

Sync status is human-readable by default and technical in Advanced Mode.

## 17. Conflict-free semantic auto-merge

### MUST

When remote/local history diverges and TravelRepo can prove the semantic merge conflict-free:
- Jourfold shows a small non-modal notice,
- gives a short Review/Cancel grace period,
- applies the merge automatically if the user does not intervene,
- records the merge clearly in History.

If a semantic conflict exists, no automatic merge is performed.

## 18. Variants

### MUST

Variants use real Git branches.

Jourfold supports:
- create variant,
- create variant from another variant,
- switch variant,
- rename user-facing variant title,
- display actual branch name as subdued technical information,
- archive variants,
- retain archived branches by default,
- read variant metadata from each branch's `travel.yaml`.

Trip ID is shared across variants.

Variant ID remains stable independently of branch name.

## 19. Variant discovery

### MUST

Jourfold discovers variants by enumerating Git branches and inspecting branch-tip TravelRepo metadata without requiring destructive checkout of every branch.

There is no required central `branches.yaml`.

## 20. Variant comparison

### MUST

Jourfold offers semantic comparison.

At minimum:
- added entities,
- removed entities,
- changed entities,
- changed structured fields,
- Markdown changes,
- binary selection differences.

UI behavior:
- schedule comparison supports overlay or side-by-side,
- structured details support side-by-side comparison,
- normal mode does not require users to read YAML diffs.

## 21. Semantic merge

### MUST

TravelRepo.Merge performs three-way domain-aware merge using stable entity IDs.

It handles:
- independent field edits,
- independent entity additions,
- deletion without conflicting edit,
- same-field conflicts,
- nested entities,
- unknown extension data,
- Markdown,
- binary references.

Conflict UI supports domain actions such as:
- Use Current,
- Use Variant,
- Keep Both,
- Edit Result.

Normal Jourfold workflow must not expose raw Git conflict markers as its primary merge UI.

## 22. TravelRepo serialization

### MUST

Canonical structured data uses YAML.

Implementation:
- reads/writes valid v1 YAML,
- validates against normative JSON Schema artifacts,
- performs semantic validation separately,
- preserves unknown extension data,
- preserves unknown compatible core fields where technically possible,
- never treats YAML comments as semantic information.

## 23. Stable IDs

### MUST

First-class TravelRepo entities use UUIDv7.

References use entity IDs rather than filenames.

Entity filenames are UUID-based.

Moving/renaming files within supported repository layout must not redefine entity identity.

## 24. Trip manifest

### MUST

`travel.yaml` supports:
- schema name/version,
- trip ID,
- title,
- content language,
- optional dates,
- optional default timezone,
- participant references,
- current variant metadata,
- optional cover document reference,
- extension namespace.

Trip dates may be:
- explicit,
- partially specified,
- fully absent and derived from schedule,
- completely open.

## 25. Trip cover

### MUST

A trip may reference an image document as its cover.

The cover:
- is canonical trip data,
- may differ between variants,
- appears in the trip library where appropriate,
- is optional.

No per-trip accent-color feature is required in v1.

## 26. People

### MUST

Trip-local person entities support:
- stable ID,
- display name,
- optional avatar,
- Git identity links,
- optional GitHub identity links,
- roles,
- extensions.

Display names need not be unique.

Mentions resolve by person ID.

Jourfold can maintain a local default identity template and instantiate/map it into a trip without creating a central account system.

## 27. Places

### MUST

Place entities support:
- name,
- optional coordinates,
- optional address,
- optional IANA timezone,
- optional external provider IDs,
- extensions.

Jourfold must remain usable when coordinates/provider data are absent.

## 28. Schedule items

### MUST

Schedule items are first-class entities stored one per file.

They support:
- title,
- status,
- time,
- participants,
- components,
- child references,
- rich content references,
- extensions.

## 29. Timezone correctness

### MUST

Core time logic uses local wall-clock time plus IANA timezone as the canonical travel representation.

The implementation must correctly handle:
- different departure and arrival timezones,
- timezone conversion for UI,
- DST boundaries,
- ambiguous local times through an offset disambiguator,
- invalid/nonexistent exact local times.

Automated tests include real timezone transition cases.

## 30. Flexible time

### MUST

TravelRepo and Jourfold support:
- exact time,
- approximate time,
- time window,
- day part,
- all day,
- unscheduled.

Unscheduled items may still carry:
- duration,
- place,
- participants,
- constraints.

The timetable can materialize a more specific time when the user schedules an unscheduled item.

## 31. Timetable

### MUST

Jourfold provides a schedule-first timetable.

Core behavior:
- time on vertical axis,
- day columns when space permits,
- adaptive visible day count,
- zoom,
- drag to move,
- resize duration,
- select to open inspector,
- direct lightweight edit where appropriate,
- timezone-aware rendering.

The timetable is not merely a generic month/week calendar skin.

## 32. Nested blocks

### MUST

Schedule items may contain child items by ID reference.

Nesting is semantic, not filesystem nesting.

Jourfold can:
- render nested items,
- navigate parent/child relationships,
- calculate aggregate parent information.

## 33. Inheritance

### MUST

The core supports a bounded inheritance model.

Initial inheritable concepts include:
- participants,
- tags,
- default place,
- selected presentation/category defaults.

Children can override inherited values.

The UI can tell whether a displayed value is:
- inherited,
- local.

Arbitrary unrestricted cascading rules are not introduced.

## 34. Constraints and warnings

### MUST

Structured constraints support future planning logic and current validation.

At minimum, the model can represent:
- dependency/after,
- earliest start,
- arrive-before,
- offsets between items.

Jourfold can surface:
- overlapping participant warnings,
- insufficient travel-gap warnings where data exists,
- violated temporal constraints.

Users can intentionally keep a travel-plan warning.

Warnings are not blockers unless repository integrity is at risk.

## 35. Participants in schedule

### MUST

A block may have:
- zero,
- one,
- multiple participants.

Participant-lane mode:
- uses one lane per person,
- may include Unassigned,
- never creates permanent combination lanes such as `Alex + Carla`,
- shared items remain one underlying entity.

## 36. Transport

### MUST

Structured transport support includes:
- flight,
- train,
- bus,
- car,
- taxi,
- rideshare,
- ferry,
- ship,
- bicycle,
- walking,
- other.

Transport can support multiple stops.

Flight/train-style data can represent structured information such as:
- carrier/operator,
- number,
- origin/destination,
- departure/arrival,
- terminal,
- gate/platform,
- seat where applicable.

External transport APIs are not required for v1.

## 37. Accommodation

### MUST

Accommodation is represented through structured schedule/component data and can link:
- place,
- check-in/out,
- guests,
- rooms,
- booking,
- documents.

## 38. Inbox

### MUST

Jourfold has a prominent Inbox panel.

It:
- opens/collapses,
- can be pinned,
- accepts unscheduled planning material,
- supports drag into relevant planning views.

Inbox content is represented by normal TravelRepo entities/documents rather than a hidden proprietary canonical format.

## 39. Bookings

### MUST

Booking entities can:
- reference provider/reference number,
- represent status,
- link multiple schedule items,
- link multiple travelers,
- link documents,
- store structured price.

One booking may connect multiple legs or related items.

## 40. Tasks

### MUST

Tasks are independent entities.

They support:
- title,
- state,
- assignees,
- optional due date,
- related entities.

A task does not need to be a schedule item.

## 41. Comments

### MUST

Comments are first-class entities.

v1 supports:
- target entity,
- author person,
- timestamp,
- Markdown body,
- person mentions.

Threaded replies and reactions are not required.

Comments are naturally branch-specific because they live in the repository.

## 42. Documents and rich content

### MUST

Schedule/detail content can include multiple:
- Markdown sections,
- images,
- PDFs,
- links,
- entity references.

Jourfold provides useful in-app rendering/preview where practical.

It is not limited to one attachment of each type.

## 43. Content-addressed assets

### MUST

Binary blobs are stored/deduplicated by content hash.

The same binary referenced in multiple places is stored once.

Document metadata is separate from blob storage.

Jourfold warns before adding unusually large files.

Git LFS support may be architecture-ready but is not required to be active in v1.

## 44. Files view

### MUST

Jourfold provides a first-class Files/Documents view that:
- lists document entities,
- previews supported content,
- searches metadata/content where indexed,
- shows related entities,
- exposes duplicate/reference information,
- can open a file externally.

Raw blob layout is an Advanced Mode concern.

## 45. Expenses

### MUST

Expense data supports:
- decimal string amount,
- ISO currency code,
- estimated/actual distinction,
- payer,
- related entities.

The underlying schema is multi-currency capable from v1.

## 46. Historical conversion

### MUST

If a converted amount is stored, it records enough conversion context to remain historically stable.

Jourfold must not silently recalculate a historical stored conversion using a current exchange rate.

### SHOULD

Basic UI for viewing multiple currencies.

Full automatic exchange-rate provider integration is not required.

## 47. Budget

### SHOULD

Provide a basic trip budget view.

At minimum, the architecture and schema support:
- trip budget,
- category budget,
- participant budget.

A full Splitwise-style settlement engine is not required.

## 48. Collections

### MUST

Collections can group arbitrary relevant TravelRepo entities.

Example uses:
- restaurants to consider,
- museums,
- beaches,
- ideas,
- maybe.

## 49. Map

### MUST

Jourfold provides a Map main view.

When mapping data is available:
- selecting a schedule item highlights its place/route,
- selecting a map place/route updates selection and inspector,
- selection is shared with other views.

The application remains usable offline and without any external map API key.

### SHOULD

Provide a basic map implementation that can render cached/local/provider-independent information without requiring a paid API.

External dynamic routing is not a v1 requirement.

## 50. Command palette

### MUST

`Ctrl+K` opens a command/search palette capable of:
- navigation,
- commands,
- entity search,
- variant switching,
- quick creation where appropriate.

## 51. Quick Add

### MUST

Quick Add provides common creation paths for:
- Activity,
- Transport,
- Accommodation,
- Task,
- Note,
- Booking,
- Place,
- Person,
- Expense,
- Collection.

## 52. Search

### MUST

Jourfold provides local full-text search over useful indexed content such as:
- titles,
- places,
- Markdown,
- comments,
- tasks,
- tags,
- filenames,
- URLs,
- appropriate booking metadata.

Search index is derived local state and may be rebuilt.

OCR is not required.

## 53. GitHub authentication

### MUST

GitHub integration is implemented in a reusable provider library.

Recommended/default mode:
- GitHub App,
- device authorization appropriate for desktop,
- fine-grained access where possible.

Credentials are stored securely outside TravelRepo.

## 54. GitHub discovery

### MUST

In GitHub App mode, Jourfold can discover TravelRepo repositories inside the installations/repositories granted to the app.

Detection is based on TravelRepo manifest content; provider metadata may accelerate discovery but is not authoritative.

### SHOULD

Provide optional extended repository discovery with broader GitHub user authorization.

The UI must clearly explain that this authorization is broader and optional.

Direct repository URLs remain supported regardless.

## 55. GitHub create/publish

### MUST

Jourfold can publish a local trip to GitHub.

New repositories are private by default.

The flow handles:
- repository creation,
- remote setup,
- initial push,
- privacy state.

## 56. GitHub privacy warning

### MUST

If a GitHub-backed TravelRepo is public, Jourfold displays a prominent warning.

The warning explains that the repository can contain sensitive travel information.

The user may continue deliberately.

The reusable GitHub provider exposes privacy state so other clients can make their own UI decisions.

## 57. GitHub collaborators

### MUST

Jourfold can invite a GitHub collaborator by username when provider permissions allow.

### SHOULD

Provide GitHub user search when practical.

## 58. Share behavior

### MUST

The primary sharing action adapts to context:
- GitHub: Invite / Manage access,
- generic Git: Share,
- local-only: Publish,
- variant context: Share variant.

Generic Git sharing can provide:
- clone URL,
- known SSH/HTTPS URL,
- setup guidance,
- Jourfold deep-link context.

It explicitly states that copying a URL does not grant server access.

No credentials are embedded in links.

## 59. Export

### MUST

v1 supports:
- print-friendly output,
- PDF export,
- ICS calendar export.

### SHOULD

Architecture supports later:
- GPX,
- CSV,
- static website/export bundle.

## 60. Plugins

### MUST

Jourfold provides a stable plugin abstraction and out-of-process plugin host.

v1 external plugin capabilities may provide:
- custom component/type definitions,
- provider data,
- commands/actions,
- declarative editor metadata,
- network-backed services.

Jourfold renders plugin UI using its own controls.

Unknown plugin data survives when the plugin is absent.

The product clearly states that out-of-process v1 plugins are isolated for stability, not fully security-sandboxed.

### MUST

Ship at least one example/demo plugin used by automated tests to prove the public plugin contract works.

## 61. Provider extensibility

### MUST

GitHub is implemented through provider abstractions rather than special UI-only logic.

Core abstractions allow future GitLab/Gitea/Forgejo provider implementations without rewriting TravelRepo.Core.

## 62. Advanced Mode

### MUST

Advanced Mode can expose:
- repository path,
- branch/ref,
- remotes,
- commit hashes,
- schema validation details,
- raw Git state useful for diagnosis,
- custom entity/component diagnostics,
- provider/plugin diagnostics.

Ordinary workflows do not require Advanced Mode.

## 63. Localization

### MUST

English is complete.

German is complete enough to serve as a real second-language implementation test.

No normal user-facing strings are hard-coded directly into views.

Trip content remains in the trip's own language.

## 64. Units and locale

### MUST

Canonical repository values use SI/locale-independent representation where applicable.

Jourfold can render:
- user-preferred units,
- locale-aware date/time,
- locale-aware number formatting.

Changing display preferences does not rewrite canonical data merely to change presentation.

## 65. Accessibility

### MUST

Jourfold supports:
- complete keyboard navigation,
- visible focus,
- logical reading order,
- screen-reader semantics for primary workflows,
- text scaling,
- non-color-only status,
- high-contrast awareness,
- reduced-motion behavior where available.

## 66. Multiple windows

### MUST

Different trips may be open in separate Jourfold windows simultaneously.

For the same repository:
- only one Jourfold workspace owns write access in v1,
- reopening normally focuses the existing writable window,
- an optional additional read-only view may be provided,
- two independent Jourfold writers are not allowed.

External programs can still modify the repository and are handled as external changes.

## 67. Issue reporting and privacy

### MUST

v1 has:
- no behavioral analytics,
- no travel-data telemetry,
- no automatic crash upload,
- no automatic log upload.

`Report a problem` may:
- collect sanitized technical information locally,
- show the exact report before transmission,
- let the user remove/add content,
- copy to clipboard,
- open a pre-filled GitHub Issue.

Nothing is submitted without explicit user action.

## 68. CLI

### MUST

Ship a usable `travelrepo` CLI with at least:

```text
travelrepo validate
travelrepo inspect
travelrepo diff
travelrepo repair
```

### SHOULD

Also include:

```text
travelrepo migrate
travelrepo export
```

The CLI uses the same public TravelRepo libraries as Jourfold.

## 69. Open SDK boundary

### MUST

TravelRepo core libraries:
- contain no Avalonia dependency,
- contain no Jourfold UI types,
- expose documented domain APIs,
- are usable by third-party .NET applications.

Specification and schemas are independently usable by non-.NET implementations.

## 70. MCP / agent readiness

### MUST

No AI runtime is required in v1.

However, core use cases are exposed through stable domain commands/queries so a later MCP server or agent client can use:
- read/query,
- add/edit,
- create variant,
- compare,
- merge,
- validate,
- version/export,
without bypassing core validation.

### SHOULD

Include a documented placeholder location/convention for future MCP/skill integration without making it part of runtime v1.

## 71. Security and secrets

### MUST

Never commit:
- OAuth tokens,
- API keys,
- passwords,
- SSH private keys,
- credential-store secrets.

Secure secret storage uses OS facilities where available.

Logs redact secrets.

Public-repository warnings are implemented.

## 72. Large files

### MUST

Jourfold warns before adding large binary files.

### SHOULD

Architecture leaves room for Git LFS without making it a format dependency.

## 73. Testing

### MUST

Automated tests cover:
- core domain model,
- timezone/DST cases,
- serialization round-trip,
- unknown-field preservation,
- validation,
- repair,
- Git operations in real temporary repositories,
- multiple remotes,
- variants,
- semantic diff,
- semantic merge fixtures,
- plugin contract,
- GitHub provider seams with mocked network boundaries,
- headless desktop UI for critical flows.

Property-based tests are expected for suitable invariants such as time, merge, and serialization.

## 74. End-to-end release smoke tests

### MUST

Automated or scripted release smoke tests prove at least:

1. create local trip,
2. add participant,
3. add place,
4. add timezone-crossing flight,
5. add unscheduled activity,
6. drag it into timetable,
7. attach image/PDF,
8. create booking,
9. create task,
10. create version,
11. create variant,
12. modify variant,
13. compare variants,
14. merge variant,
15. archive merged variant,
16. add generic Git remote,
17. push/fetch,
18. reopen offline,
19. search content,
20. export PDF,
21. export ICS.

A separate GitHub-enabled smoke path proves, when credentials are available:
- authenticate,
- discover TravelRepo,
- publish private repo,
- detect privacy,
- invite collaborator.

## 75. CI

### MUST

GitHub Actions performs:
- pinned SDK restore,
- build,
- formatting/lint verification,
- unit tests,
- conformance tests,
- Git integration tests,
- merge tests,
- plugin tests,
- headless UI tests,
- packaging validation,
- license/notice checks,
- dependency/security checks.

Primary CI matrix includes Windows and Linux.

## 76. Samples and conformance fixtures

### MUST

Repository includes sample TravelRepos covering at least:
- minimal trip,
- Tokyo timezone flight,
- Aachen local planning,
- New Zealand multi-stop trip,
- nested roadtrip,
- multiple people,
- flexible/unscheduled time,
- unknown extension,
- merge conflict scenarios,
- deduplicated assets.

Samples are validated in CI.

## 77. Documentation

### MUST

Repository includes:
- TravelRepo specification,
- schema documentation,
- architecture document,
- Jourfold UX document,
- plugin development documentation,
- provider architecture documentation,
- CLI usage,
- developer setup,
- build instructions,
- test instructions,
- third-party notices.

The normal user documentation does not require Git knowledge for ordinary Jourfold workflows.

## 78. Licensing

### MUST

Target licensing arrangement:
- TravelRepo specification: Apache-2.0,
- schemas: Apache-2.0,
- reusable TravelRepo SDK/core: Apache-2.0,
- plugin abstractions: Apache-2.0,
- Jourfold reference application: GPL-3.0-or-later unless explicitly changed before release.

Font license files and third-party notices are included in source and distributable packages.

## 79. Explicit v1 non-goals

The following are not release blockers for v1:
- live collaborative editing,
- recurring schedule rules,
- OCR extraction,
- structured passport storage,
- complete encrypted multi-user key management,
- mandatory Git LFS,
- Google Maps routing,
- airline APIs,
- rail live data,
- automatic email booking import,
- Splitwise-equivalent settlement,
- voting,
- reactions,
- full mobile apps,
- web client,
- MCP server,
- AI trip generation,
- complete third-party plugin sandboxing,
- GitLab/Gitea/Forgejo deep integration,
- generic browser SSH Git.

Architecture must not deliberately prevent these later.

## 80. Definition of done

Jourfold v1 is done when a user can, on both Windows and Linux:

- install and launch Jourfold,
- create or open a TravelRepo with no Jourfold account,
- plan a real multi-day, multi-timezone trip,
- organize people, places, bookings, tasks, costs, files, comments, and flexible ideas,
- work offline,
- recover from ordinary mistakes with undo/redo,
- create meaningful Git-backed versions without knowing Git,
- create and compare alternative variants,
- merge variants through domain-aware UI,
- synchronize through generic Git,
- use GitHub for discovery, private publishing, and collaboration,
- share appropriately for the active provider,
- export useful trip output,
- reopen the repository using standard Git or another TravelRepo client,
- and retain unknown compatible data without silent loss.

If those workflows are coherent, tested, and non-destructive, the initial one-shot implementation has met the v1 target.
