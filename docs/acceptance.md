# Acceptance audit

Audit date: 2026-09-30. **Overall release status: BLOCKED.**

**Interface rebuild, 2026-10-04 (branch `modernize`).** The desktop interface was rebuilt on a design system following the brand and UX references: icon sidebar, trip header with variant switcher and save state, tabbed inspector, pinnable Inbox, library with covers, a new timetable, list, map with opt-in OpenStreetMap, redesigned pages and dialogs, and a sample trip. TravelRepo gained client-facing queries, change summaries, itinerary exports and a cleaner YAML writer. Verification for this pass: full test suites, `dotnet format`, headless captures in light, dark and German, and native X11 runs on a virtual display (Xvfb, no window manager) covering click selection, menus, pickers, drag-create and edits. It did not include new GNOME, KDE or Windows sessions, so the platform gates below are unchanged. See `docs/usability-review.md` in Jourfold.

Native user testing reopened six UI sections. Their revised workflows now pass the stated local regressions, with 70 of 76 required sections covered and six external verification gates still BLOCKED. The earlier percentage did not establish native desktop usability. CachyOS focus/icon behavior needs a retest, and further visual refinement remains NOT COMPLETE as follow-up design work. See `docs/usability-review.md` in Jourfold for the detailed review and limits.

PASS means the complete required block has implementation and the stated code/test evidence. Cross-platform execution is separately gated in sections 2, 3, 65, 71 and 75. Live GitHub authentication, discovery, private publishing and privacy checks passed; invitations remain pending in section 57. The release is not accepted until all blocked gates pass.

Both repositories were built after deleting their source/test/sample bin and obj directories. Locked restore, Release builds, formatting, tests, notice generation and dependency audits ran locally. See Jourfold `docs/release-smoke.md`, `docs/verification.md` and `docs/live-github-validation.md` for commands, results, artifacts and limits.

| Criterion | Status | Evidence or exact blocker |
| --- | --- | --- |
| 2. Supported platforms | BLOCKED | Windows x64 self-contained artifact builds, but native Windows 11 execution is unavailable. GNOME 46 and KDE Plasma 5.27 X11 sessions exercised on Ubuntu 24.04. Revised forms retained activation in Ubuntu GNOME; CachyOS/GNOME focus and overview-icon retest, plus full platform/scaling/desktop integration checklist, remain required. |
| 3. First-run behavior | BLOCKED | Linux native empty-library/create flow and compatible system Git pass. Bundled MinGit is pinned and packaged, but its fallback must execute on a clean Windows machine. |
| 4. Trip library | PASS | Library cards with trip cover or generated cover, effective dates, place summary, last opened and local/shared state; filters, search, missing-folder recovery; LocalStore rebuildable FTS. Headless captures of the empty and populated library. |
| 5. Create trip | PASS | Skippable three-step wizard: only the title is required; dates, main timezone, content language, own name, companions and folder (default `Documents/Jourfold`, changeable) are optional. Real repository creation and wizard tests pass. |
| 6. Open existing trip | PASS | Schema and semantic validation on open, compatible unknown-data retention, external reload tests and reviewed malformed-entity recovery test. |
| 7. Repair and rollback | PASS | Recovery journals and preview/apply restore with stale-state checks; MalformedEntityOpenOffersReviewedRestoreWithoutDroppingUnknownData verifies the UI orchestration. |
| 8. Autosave | PASS | Repository transactions, external recovery journals and inspector autosave; stale-write tests. |
| 9. Undo / redo | PASS | Workspace reversible edit batches, optimistic validation and external invalidation; application tests. |
| 10. Create Version | PASS | Create Version sheet lists TravelRepo `ChangeSummary` entries and suggests an editable message from them; real version commits with machine-readable trailers tested. |
| 11. Human-readable history | PASS | Timeline with trip-person author mapping, relative times, expandable semantic changes from `ChangeSummary` and restore; Advanced mode adds commit, parents, refs, author/committer and trailers. |
| 12. Git interoperability | PASS | Real Git backend; local-only config, remotes and external changes tested. |
| 13. Git identity | PASS | Repository-local identity and trip-local person identity fields. |
| 14. HTTPS and SSH remotes | PASS | Live SDK HTTPS push/fetch/clone and SSH clone/version/push/fetch passed on 2026-09-30; HTTPS safe fast-forward matched SSH commit. GNOME Secret Service persistence passed. Windows execution remains gated separately. |
| 15. Multiple remotes | PASS | Multiple remotes tested with real bare repository; preferred remote in local SQLite. |
| 16. Background synchronization | PASS | Periodic fetch/safe fast-forward/committed push with nonmodal failure status; real remotes test dirty/divergent states. Preferred remote is local state. |
| 17. Conflict-free semantic auto-merge | PASS | DivergentSyncOffersCancellableGraceAndRecordsARealMerge tests cancellation and automatic merge/push with real divergent remotes. Schema-invalid results are ineligible. |
| 18. Variants | PASS | Real branches, metadata, rename, switch and archive implemented; integration tests. |
| 19. Variant discovery | PASS | for-each-ref plus branch-tip manifest reads; no checkout enumeration. |
| 20. Variant comparison | PASS | Side-by-side day timetable marking added, removed and changed items by icon and label, plus a field-level details view with Markdown and resource changes; headless comparison test. |
| 21. Semantic merge | PASS | Three-way ID/field/resource merge with conservative ordered-sequence handling; all conflicts are resolved in one sheet with Keep, Take, Keep both and Write result; all four actions tested through reviewed two-parent commits and archive. |
| 22. TravelRepo serialization | PASS | YamlDotNet safe document envelope, JSON Schema 2020-12 and separate semantic validation. |
| 23. Stable IDs | PASS | UUIDv7 IDs, ID-based references and rename test. |
| 24. Trip manifest | PASS | Manifest schema, optional dates/timezone/cover/participants and extensions. |
| 25. Trip cover | PASS | Manifest image-document cover, inspector selection and library image preview; canonical reference remains branch-specific. |
| 26. People | PASS | Person schema/editor includes avatar, roles and Git/GitHub identities; template-to-trip mapping is idempotently tested; mentions use IDs. |
| 27. Places | PASS | Place schema/editor and offline coordinate map. |
| 28. Schedule items | PASS | Per-file schema/components and inspector backed by domain validation. Typed date/time, numeric and reference forms replace raw storage-string input; usability regressions pass. |
| 29. Timezone correctness | PASS | Noda Time exact resolution and real Berlin DST/Tokyo tests. |
| 30. Flexible time | PASS | All six precision states, fixtures and materialization tests. Combined picker form preserves day-part timezone and unknown compatible time fields; overnight range validation is tested. |
| 31. Timetable | PASS | Rebuilt timetable: category colours and icons, side-by-side overlaps, all-day and stay strip, nested group frames, now line, drag to move, resize and create, Alt+arrow moves, preserved scroll. Real pointer tests in headless mode and native X11 click, drag-create and picker checks. Native GNOME/KDE confirmation of the new UI remains within platform gate 2. |
| 32. Nested blocks | PASS | Core cycle checks and aggregate ranges, indented timetable children, parent/child inspector navigation and aggregate duration; nesting fixtures and domain tests. |
| 33. Inheritance | PASS | Core bounded inheritance tests; inspector shows local/inherited provenance and reset for participants, tags, default place, display and category. |
| 34. Constraints and warnings | PASS | Structured dependency, earliest-start and arrive-before constraints with offsets; overlap and cached travel-estimate warnings tested without blocking valid edits. |
| 35. Participants in schedule | PASS | Shared-lane/overnight tests pass. Assignment uses explicit available/assigned lists, Save/Cancel and one undoable selection update; actual form regression passes. |
| 36. Transport | PASS | Transport enums, endpoints, stops and structured fields in schema/editor. |
| 37. Accommodation | PASS | Structured accommodation place, zoned check-in/out, guests, rooms, booking and documents; schema validation and application editing tests. |
| 38. Inbox | PASS | Unscheduled schedule items and unlinked notes/documents in a collapsible, pinnable panel with quick capture and filters; drag onto the timetable materializes canonical time; keyboard scheduling available. |
| 39. Bookings | PASS | Booking entity, travelers/items/documents/price editor and smoke test. |
| 40. Tasks | PASS | Independent task schema/editor and smoke test. |
| 41. Comments | PASS | Comment target/author/time/body/mentions editors, related-comment rendering and branch-specific entities; accommodation/comment integration test. |
| 42. Documents and rich content | PASS | Repeated Markdown/image/PDF/link/reference content, CommonMark and inline image rendering, content edit/remove and external document opening; coherent Markdown/resource undo tests. |
| 43. Content-addressed assets | PASS | SHA-256 assets, metadata separation and duplicate/large-file tests. |
| 44. Files view | PASS | File tiles with image previews, type, size and usage count; content search; related entities; replacement, external opening, cover selection; files dropped on the window are imported as content-addressed documents. |
| 45. Expenses | PASS | Decimal-string money, multi-currency schema and expense editor. |
| 46. Historical conversion | PASS | Conversion context schema; no automatic current-rate rewriting. |
| 48. Collections | PASS | Collection references and editor. |
| 49. Map | PASS | Web Mercator map from trip coordinates with category markers, transport routes and shared selection; fully offline by default. OpenStreetMap tiles and Nominatim address search are opt-in, cached, rate-limited and attributed; no API key. |
| 50. Command palette | PASS | Ctrl+K palette with live search over trip content (FTS), navigation and commands, grouped results and keyboard navigation. |
| 51. Quick Add | PASS | One Quick Add sheet for activity, food, sight, transport, stay, booking, task, note, place, person, expense, budget and collection; transport modes as chips; places created by name; one undoable edit. Tests build schema-valid entities for every type. |
| 52. Search | PASS | Rebuildable SQLite FTS index for canonical fields and Markdown. |
| 53. GitHub authentication | PASS | Real GitHub App device authorization approved on 2026-09-30; token persisted through Jourfold OsSecretStore and reused by fresh instances. No client secret or App private key used. |
| 54. GitHub discovery | PASS | Live granted-installation manifest discovery and Jourfold discovery command selected, cloned and opened private OWNER/jourfold-smoke with scripted dialog choices. |
| 55. GitHub create/publish | PASS | Live SDK and Jourfold Publish command created private repositories, set remotes and pushed initial versions. Library-state bug found and fixed; regression tests cover successful and failed initial pushes. |
| 56. GitHub privacy warning | PASS | Live provider distinguishes public/private visibility; ordinary client open shows warning for a configured public GitHub remote. No push to the public repository was performed. |
| 57. GitHub collaborators | BLOCKED | Invitation endpoint and UI are implemented and HTTP-tested. User chose to proceed without a collaborator for initial testing; no live invitation has been sent. |
| 58. Share behavior | PASS | Context-specific publish/invite/share/variant actions and explicit access guidance; credential-free ShareLink round-trip and rejection tests; protocol registration in packaging. |
| 59. Export | PASS | TravelRepo `TripItinerary` drives a day-by-day PDF (Jourfold) and print HTML (TravelRepo) with localized labels; ICS uses resolved spans for every precision with location and status. Export tests pass; PDF output inspected. |
| 60. Plugins | PASS | Versioned public contracts, trusted separate-process JSON-RPC host, declarative native controls, unknown-data retention and packaged example; real process contract test. |
| 61. Provider extensibility | PASS | Capability interfaces and reusable GitHub implementation. |
| 62. Advanced Mode | PASS | Advanced diagnostics include path, refs, all remotes, raw Git status, schema/custom-component data and provider/plugin diagnostics; ordinary flows stay in normal mode. |
| 63. Localization | PASS | English and German complete for the redesigned interface; parity test passes; German layout captured and long labels checked. |
| 64. Units and locale | PASS | SI distance conversion and locale tests; display settings are local. |
| 65. Accessibility | BLOCKED | Keyboard shortcuts, inspector focus, automation names, logical controls, scaling/high contrast and non-color state implemented/tested. Full OS screen-reader and keyboard-only native audit remains unverified. |
| 66. Multiple windows | PASS | Window registry and filesystem write ownership; ownership test. |
| 67. Issue reporting and privacy | PASS | No analytics/upload runtime; editable report copied only on user action. |
| 68. CLI | PASS | CLI validate/inspect/diff/repair plus init/version/export. |
| 69. Open SDK boundary | PASS | TravelRepo has no Avalonia/Jourfold dependencies; documented SDK and schemas. |
| 70. MCP / agent readiness | PASS | Public repository/Git/merge commands and queries; no AI runtime. |
| 71. Security and secrets | BLOCKED | GNOME credential persistence and live authenticated transport passed; App token and Basic encoding absent from inspected canonical files, Git configs and validation logs. Windows Credential Manager verification remains pending. |
| 72. Large files | PASS | 25 MB pre-import confirmation and tests. |
| 73. Testing | PASS | 54 TravelRepo and 45 Jourfold tests pass after the interface rebuild, including itinerary/export, change summaries, sync status, YAML writer round trips, Quick Add validity, real pointer selection and drag tests. `dotnet format --verify-no-changes` passes in both repositories. |
| 74. End-to-end release smoke tests | PASS | Local 21-step smoke and headless gestures pass. Live GitHub authentication, discovery, private creation/publish and privacy checks passed; collaborator invitation is separately pending in section 57. |
| 75. CI | BLOCKED | Windows/Linux workflows are prepared. GitHub CLI is authenticated; private source-repository creation/push approval and hosted CI execution remain pending. Windows installer compilation/native checks remain pending. |
| 76. Samples and conformance fixtures | PASS | All required named sample categories and conformance tests. |
| 77. Documentation | PASS | Specs, schemas, architecture, UX, SDK, CLI, provider, plugin, build/test and notice docs. |
| 78. Licensing | PASS | Apache-2.0 TravelRepo and GPL-3.0-or-later Jourfold source/package notices; pinned font hashes and license files, refreshed dependency notices, NuGet and desktop package contents verified. |

## Detailed MUST checklist

Each required source block is reproduced for traceability. Its section status applies to every bullet in that block. Sections 1 and 80 require all these gates; overall release acceptance remains BLOCKED. The SHOULD-only budget section and explicit non-goals do not add MUST gates.

### 2. Supported platforms

MUST

- Build and run as a native desktop application on Windows 11 x64.
- Build and run as a native desktop application on a supported mainstream Linux x64 environment.
- Exercise Linux UI behavior on at least one GNOME-based and one KDE-based environment in release validation.
- Support HiDPI/scaling.
- Support mouse and keyboard as first-class input.
- Support Light, Dark, and System themes.
- Support Comfortable and Compact density.
- Use bundled Plus Jakarta Sans for Jourfold UI typography.
- Use canonical SVG branding assets.



### 3. First-run behavior

MUST

A clean Jourfold installation must:
- start without requiring a Jourfold account,
- not require a remote,
- not require Git knowledge,
- detect a compatible system Git,
- fall back to the bundled/tested Git distribution where defined by the platform packaging strategy,
- show an empty trip library with clear actions to create or open a trip.

No telemetry consent dialog is shown because v1 contains no telemetry.



### 4. Trip library

MUST

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



### 5. Create trip

MUST

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



### 6. Open existing trip

MUST

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



### 7. Repair and rollback

MUST

When a repository cannot be safely interpreted, Jourfold:
1. explains the problem,
2. attempts non-destructive repair when a known repair exists,
3. offers rollback to a safe Git state when available,
4. never silently deletes unknown extension data.

A repair operation that changes canonical data must itself be reviewable and versionable.



### 8. Autosave

MUST

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



### 9. Undo / redo

MUST

Jourfold provides local Undo and Redo for ordinary domain edits.

Undo/redo:
- is independent from Git commit history,
- handles logical multi-field operations as coherent actions where appropriate,
- must not apply unsafe inverse operations after state-invalidating external changes,
- explains when external changes invalidate undo history.



### 10. Create Version

MUST

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



### 11. Human-readable history

MUST

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



### 12. Git interoperability

MUST

The same repository remains usable with standard Git.

Jourfold must tolerate:
- externally created commits,
- external file edits that remain schema-compatible,
- branch operations,
- multiple remotes.

The reference Git backend uses real Git CLI semantics behind an abstraction.



MUST NOT

- silently alter global Git config,
- store credentials in remote URLs,
- require a proprietary repository wrapper,
- make a TravelRepo cease to be a normal Git repository.



### 13. Git identity

MUST

Each trip can use repository-local Git identity.

Jourfold:
- may suggest global Git identity,
- may map Git identities to TravelRepo people,
- never silently changes global Git identity.

TravelRepo people remain trip-local and do not require a Jourfold account.



### 14. HTTPS and SSH remotes

MUST

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



### 15. Multiple remotes

MUST

A repository may have multiple Git remotes.

Jourfold:
- does not destroy unknown remotes,
- allows inspection in Advanced Mode,
- can choose a preferred sync/share remote locally,
- does not make single-remote assumptions in core libraries.



### 16. Background synchronization

MUST

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



### 17. Conflict-free semantic auto-merge

MUST

When remote/local history diverges and TravelRepo can prove the semantic merge conflict-free:
- Jourfold shows a small non-modal notice,
- gives a short Review/Cancel grace period,
- applies the merge automatically if the user does not intervene,
- records the merge clearly in History.

If a semantic conflict exists, no automatic merge is performed.



### 18. Variants

MUST

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



### 19. Variant discovery

MUST

Jourfold discovers variants by enumerating Git branches and inspecting branch-tip TravelRepo metadata without requiring destructive checkout of every branch.

There is no required central `branches.yaml`.



### 20. Variant comparison

MUST

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



### 21. Semantic merge

MUST

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



### 22. TravelRepo serialization

MUST

Canonical structured data uses YAML.

Implementation:
- reads/writes valid v1 YAML,
- validates against normative JSON Schema artifacts,
- performs semantic validation separately,
- preserves unknown extension data,
- preserves unknown compatible core fields where technically possible,
- never treats YAML comments as semantic information.



### 23. Stable IDs

MUST

First-class TravelRepo entities use UUIDv7.

References use entity IDs rather than filenames.

Entity filenames are UUID-based.

Moving/renaming files within supported repository layout must not redefine entity identity.



### 24. Trip manifest

MUST

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



### 25. Trip cover

MUST

A trip may reference an image document as its cover.

The cover:
- is canonical trip data,
- may differ between variants,
- appears in the trip library where appropriate,
- is optional.

No per-trip accent-color feature is required in v1.



### 26. People

MUST

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



### 27. Places

MUST

Place entities support:
- name,
- optional coordinates,
- optional address,
- optional IANA timezone,
- optional external provider IDs,
- extensions.

Jourfold must remain usable when coordinates/provider data are absent.



### 28. Schedule items

MUST

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



### 29. Timezone correctness

MUST

Core time logic uses local wall-clock time plus IANA timezone as the canonical travel representation.

The implementation must correctly handle:
- different departure and arrival timezones,
- timezone conversion for UI,
- DST boundaries,
- ambiguous local times through an offset disambiguator,
- invalid/nonexistent exact local times.

Automated tests include real timezone transition cases.



### 30. Flexible time

MUST

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



### 31. Timetable

MUST

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



### 32. Nested blocks

MUST

Schedule items may contain child items by ID reference.

Nesting is semantic, not filesystem nesting.

Jourfold can:
- render nested items,
- navigate parent/child relationships,
- calculate aggregate parent information.



### 33. Inheritance

MUST

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



### 34. Constraints and warnings

MUST

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



### 35. Participants in schedule

MUST

A block may have:
- zero,
- one,
- multiple participants.

Participant-lane mode:
- uses one lane per person,
- may include Unassigned,
- never creates permanent combination lanes such as `Alex + Carla`,
- shared items remain one underlying entity.



### 36. Transport

MUST

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



### 37. Accommodation

MUST

Accommodation is represented through structured schedule/component data and can link:
- place,
- check-in/out,
- guests,
- rooms,
- booking,
- documents.



### 38. Inbox

MUST

Jourfold has a prominent Inbox panel.

It:
- opens/collapses,
- can be pinned,
- accepts unscheduled planning material,
- supports drag into relevant planning views.

Inbox content is represented by normal TravelRepo entities/documents rather than a hidden proprietary canonical format.



### 39. Bookings

MUST

Booking entities can:
- reference provider/reference number,
- represent status,
- link multiple schedule items,
- link multiple travelers,
- link documents,
- store structured price.

One booking may connect multiple legs or related items.



### 40. Tasks

MUST

Tasks are independent entities.

They support:
- title,
- state,
- assignees,
- optional due date,
- related entities.

A task does not need to be a schedule item.



### 41. Comments

MUST

Comments are first-class entities.

v1 supports:
- target entity,
- author person,
- timestamp,
- Markdown body,
- person mentions.

Threaded replies and reactions are not required.

Comments are naturally branch-specific because they live in the repository.



### 42. Documents and rich content

MUST

Schedule/detail content can include multiple:
- Markdown sections,
- images,
- PDFs,
- links,
- entity references.

Jourfold provides useful in-app rendering/preview where practical.

It is not limited to one attachment of each type.



### 43. Content-addressed assets

MUST

Binary blobs are stored/deduplicated by content hash.

The same binary referenced in multiple places is stored once.

Document metadata is separate from blob storage.

Jourfold warns before adding unusually large files.

Git LFS support may be architecture-ready but is not required to be active in v1.



### 44. Files view

MUST

Jourfold provides a first-class Files/Documents view that:
- lists document entities,
- previews supported content,
- searches metadata/content where indexed,
- shows related entities,
- exposes duplicate/reference information,
- can open a file externally.

Raw blob layout is an Advanced Mode concern.



### 45. Expenses

MUST

Expense data supports:
- decimal string amount,
- ISO currency code,
- estimated/actual distinction,
- payer,
- related entities.

The underlying schema is multi-currency capable from v1.



### 46. Historical conversion

MUST

If a converted amount is stored, it records enough conversion context to remain historically stable.

Jourfold must not silently recalculate a historical stored conversion using a current exchange rate.



### 48. Collections

MUST

Collections can group arbitrary relevant TravelRepo entities.

Example uses:
- restaurants to consider,
- museums,
- beaches,
- ideas,
- maybe.



### 49. Map

MUST

Jourfold provides a Map main view.

When mapping data is available:
- selecting a schedule item highlights its place/route,
- selecting a map place/route updates selection and inspector,
- selection is shared with other views.

The application remains usable offline and without any external map API key.



### 50. Command palette

MUST

`Ctrl+K` opens a command/search palette capable of:
- navigation,
- commands,
- entity search,
- variant switching,
- quick creation where appropriate.



### 51. Quick Add

MUST

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



### 52. Search

MUST

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



### 53. GitHub authentication

MUST

GitHub integration is implemented in a reusable provider library.

Recommended/default mode:
- GitHub App,
- device authorization appropriate for desktop,
- fine-grained access where possible.

Credentials are stored securely outside TravelRepo.



### 54. GitHub discovery

MUST

In GitHub App mode, Jourfold can discover TravelRepo repositories inside the installations/repositories granted to the app.

Detection is based on TravelRepo manifest content; provider metadata may accelerate discovery but is not authoritative.



### 55. GitHub create/publish

MUST

Jourfold can publish a local trip to GitHub.

New repositories are private by default.

The flow handles:
- repository creation,
- remote setup,
- initial push,
- privacy state.



### 56. GitHub privacy warning

MUST

If a GitHub-backed TravelRepo is public, Jourfold displays a prominent warning.

The warning explains that the repository can contain sensitive travel information.

The user may continue deliberately.

The reusable GitHub provider exposes privacy state so other clients can make their own UI decisions.



### 57. GitHub collaborators

MUST

Jourfold can invite a GitHub collaborator by username when provider permissions allow.



### 58. Share behavior

MUST

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



### 59. Export

MUST

v1 supports:
- print-friendly output,
- PDF export,
- ICS calendar export.



### 60. Plugins

MUST

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



MUST

Ship at least one example/demo plugin used by automated tests to prove the public plugin contract works.



### 61. Provider extensibility

MUST

GitHub is implemented through provider abstractions rather than special UI-only logic.

Core abstractions allow future GitLab/Gitea/Forgejo provider implementations without rewriting TravelRepo.Core.



### 62. Advanced Mode

MUST

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



### 63. Localization

MUST

English is complete.

German is complete enough to serve as a real second-language implementation test.

No normal user-facing strings are hard-coded directly into views.

Trip content remains in the trip's own language.



### 64. Units and locale

MUST

Canonical repository values use SI/locale-independent representation where applicable.

Jourfold can render:
- user-preferred units,
- locale-aware date/time,
- locale-aware number formatting.

Changing display preferences does not rewrite canonical data merely to change presentation.



### 65. Accessibility

MUST

Jourfold supports:
- complete keyboard navigation,
- visible focus,
- logical reading order,
- screen-reader semantics for primary workflows,
- text scaling,
- non-color-only status,
- high-contrast awareness,
- reduced-motion behavior where available.



### 66. Multiple windows

MUST

Different trips may be open in separate Jourfold windows simultaneously.

For the same repository:
- only one Jourfold workspace owns write access in v1,
- reopening normally focuses the existing writable window,
- an optional additional read-only view may be provided,
- two independent Jourfold writers are not allowed.

External programs can still modify the repository and are handled as external changes.



### 67. Issue reporting and privacy

MUST

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



### 68. CLI

MUST

Ship a usable `travelrepo` CLI with at least:

```text
travelrepo validate
travelrepo inspect
travelrepo diff
travelrepo repair
```



### 69. Open SDK boundary

MUST

TravelRepo core libraries:
- contain no Avalonia dependency,
- contain no Jourfold UI types,
- expose documented domain APIs,
- are usable by third-party .NET applications.

Specification and schemas are independently usable by non-.NET implementations.



### 70. MCP / agent readiness

MUST

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



### 71. Security and secrets

MUST

Never commit:
- OAuth tokens,
- API keys,
- passwords,
- SSH private keys,
- credential-store secrets.

Secure secret storage uses OS facilities where available.

Logs redact secrets.

Public-repository warnings are implemented.



### 72. Large files

MUST

Jourfold warns before adding large binary files.



### 73. Testing

MUST

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



### 74. End-to-end release smoke tests

MUST

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



### 75. CI

MUST

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



### 76. Samples and conformance fixtures

MUST

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



### 77. Documentation

MUST

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



### 78. Licensing

MUST

Target licensing arrangement:
- TravelRepo specification: Apache-2.0,
- schemas: Apache-2.0,
- reusable TravelRepo SDK/core: Apache-2.0,
- plugin abstractions: Apache-2.0,
- Jourfold reference application: GPL-3.0-or-later unless explicitly changed before release.

Font license files and third-party notices are included in source and distributable packages.


