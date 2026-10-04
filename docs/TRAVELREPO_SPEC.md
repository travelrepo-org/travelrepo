# TravelRepo Specification v0.2

Status: Draft  
Reference client: Jourfold  
License target: Apache-2.0 for the specification and schema

## 1. Purpose

TravelRepo is an open, Git-native repository format for travel planning.

A TravelRepo repository represents exactly one trip. Git provides version history, variants, collaboration, synchronization, offline work, branching, comparison, and merging. Clients are expected to hide unnecessary Git complexity from normal users while preserving compatibility with standard Git tooling.

TravelRepo is independent from Jourfold. Jourfold is a reference client. Other applications may implement the format directly or use the TravelRepo libraries.

## 2. Design principles

1. One Git repository represents one trip.
2. A repository may exist without any remote.
3. A repository is always a real Git repository.
4. Standard Git tooling may be used directly. Clients should detect and tolerate external changes where possible.
5. All core trip data must remain usable offline.
6. Human-facing clients should use travel concepts rather than Git terminology by default.
7. The format must be readable and writable without Jourfold.
8. The format must support deterministic validation and migration.
9. Unknown compatible data must survive read-write cycles.
10. Structured domain data and free-form content must remain separate.
11. Local caches, secrets, indexes, and UI state must not be committed.
12. SI units and locale-independent machine representations are canonical in the repository.
13. Clients may render dates, times, units, numbers, and currencies according to user preferences.

## 3. Repository layout

Recommended v1 layout:

```text
trip/
├── travel.yaml
├── README.md
├── people/
├── places/
├── schedule/
├── bookings/
├── tasks/
├── expenses/
├── budgets/
├── collections/
├── comments/
├── documents/
├── assets/
│   └── sha256/
└── .travelrepo/
```

Each structured entity should normally be stored in its own YAML file.

Entity filenames use the entity UUID rather than a human-readable slug:

```text
schedule/0195f36a-....yaml
people/0195f36b-....yaml
places/0195f36c-....yaml
```

Human-readable names are stored inside the entity.

`.travelrepo/` is reserved for portable format-level metadata only. Local caches, thumbnails, recovery data, search indexes, credentials, API keys, provider caches, and UI state must live outside the repository.

## 4. Manifest

A repository is recognized as TravelRepo when the repository root contains `travel.yaml` with a valid TravelRepo schema declaration.

Example:

```yaml
schema:
  name: TravelRepo
  version: "1.0"

id: 0195f32d-4f45-7f8c-a7f8-1ec73ddad111
title: Japan 2027
language: en

dates:
  start: null
  end: null

default_timezone: Asia/Tokyo

cover:
  document: 0195f37a-51ab-76c8-a919-cfd5940fc777

participants:
  - 0195f36b-4d2c-7701-9c76-b1a215244001
  - 0195f36b-4d2c-7701-9c76-b1a215244002

variant:
  id: 0195f370-51ab-76c8-a919-cfd5940fb001
  title: Current Plan
  role: primary
  state: active
  parent: null
  created_at: 2027-01-12T14:23:10Z

extensions: {}
```

### 4.1 Trip dates

`dates.start` and `dates.end` are optional.

If one or both are absent, a client may derive an effective trip range from scheduled items. If no usable schedule data exists, the trip is considered temporally open.

### 4.2 Trip language

A trip has one primary content language. Schema field names, enum values, identifiers, and machine-readable metadata remain language-neutral and English-based.

### 4.3 Default timezone

A trip may define an optional default IANA timezone. It is a convenience only. Individual time-bearing entities may use other timezones.

### 4.4 Cover

A trip may define an optional cover image by referencing a TravelRepo document entity:

```yaml
cover:
  document: 0195f37a-51ab-76c8-a919-cfd5940fc777
```

The referenced document should be an image.

Cover selection is canonical trip data and therefore may differ between variants. This is intentional: changing a trip cover inside a branch is a normal versioned TravelRepo change.

A client may derive thumbnails or dominant colors locally, but those derived values are not required repository data in v1.

## 5. IDs and references

All first-class entities use UUIDv7.

References use entity IDs, not file paths.

Example:

```yaml
place: 0195f36c-4d2c-7701-9c76-b1a215244100
```

Renaming or moving an entity file must not invalidate references.

## 6. Git variants

Travel variants are Git branches.

A new TravelRepo repository should use `main` as its default branch unless the environment requires otherwise.

The normal user-facing term is `Variant`. Git branch names remain visible as secondary technical information and become more prominent in an advanced mode.

### 6.1 Variant metadata

There is no central `branches.yaml`.

Each branch describes itself through the `variant` section in that branch's own `travel.yaml`.

Example variant branch:

```yaml
variant:
  id: 0195f370-51ab-76c8-a919-cfd5940fb881
  title: Cheaper flights
  role: variant
  state: active
  parent:
    variant_id: 0195f370-51ab-76c8-a919-cfd5940fb001
    branch: main
    commit: 0beec7b5ea3f0fdbc95d0dd47f3c5bc275da8a33
  created_at: 2027-01-13T09:10:00Z
```

Archived variant:

```yaml
variant:
  id: 0195f370-51ab-76c8-a919-cfd5940fb881
  title: Cheaper flights
  role: variant
  state: archived
  parent:
    variant_id: 0195f370-51ab-76c8-a919-cfd5940fb001
    branch: main
    commit: 0beec7b5ea3f0fdbc95d0dd47f3c5bc275da8a33
  created_at: 2027-01-13T09:10:00Z
```

Clients discover variants by enumerating relevant Git branches and reading `travel.yaml` from each branch tip without requiring checkout.

The trip `id` remains identical across all variants. The `variant.id` is unique per variant.

Variant identity must not depend on the branch name. Branches may be renamed.

Archiving a variant updates its own metadata. A client may create a small administrative commit for this change and hide such commits from the default human-friendly history.

Dedicated Git refs may be introduced in later versions for metadata that cannot be represented safely inside the branch itself, but they are not required by v1.

## 7. Git behavior

### 7.1 Autosave and versions

Clients should distinguish local autosave from explicit versions.

Recommended behavior:

```text
edit
-> atomic write to working tree
-> local autosave state
-> Create Version
-> Git commit
-> background synchronization where configured
```

Autosave does not require a commit.

### 7.2 Application-generated commits

Application-generated commits should be recognizable independently of the human-readable commit message.

Recommended Git trailers:

```text
TravelRepo-Version: 1
TravelRepo-Action: user-version
TravelRepo-Client: Jourfold/1.0.0
```

Clients must not rely on exact commit messages for recognition.

### 7.3 External Git changes

External commits and file modifications are permitted.

Clients should:
- detect them,
- validate the resulting repository,
- explain unexpected changes,
- preserve valid unknown data,
- offer repair or rollback when practical,
- never claim that arbitrary external changes are guaranteed to be recoverable.

### 7.4 Multiple remotes

Multiple Git remotes are allowed.

Clients may define a preferred sharing or synchronization remote in local application state. This preference is not required to be part of the TravelRepo format.

## 8. Serialization rules

Canonical v1 structured data format: YAML.

Recommended constraints:
- UTF-8
- LF line endings
- no duplicate mapping keys
- no YAML anchors or aliases
- no executable or implementation-specific YAML tags
- stable key ordering when written by a reference implementation
- decimal monetary values serialized as strings, never binary floating point
- ISO 8601 for date and date-time syntax
- IANA timezone names for timezone identity

Clients should minimize unrelated formatting churn to keep Git diffs readable.

Reference writer style (TravelRepo 1.0 SDK):
- block mappings and sequences, two-space indentation, no document markers,
- plain scalars only for strings that every common YAML 1.1 and 1.2 parser reads identically (text starting with a letter, UUIDs); dates, times, numeric-looking text and reserved words such as `yes` or `null` are double-quoted,
- literal block style for multi-line text.

`travelrepo format PATH` rewrites an existing repository in this style without changing data.

## 9. Forward compatibility

Unknown extension data must be preserved during read-write cycles.

Where practical, unknown core fields in known entities must also be preserved.

A client must not silently discard data it does not understand.

## 10. Extensions

Every extensible first-class entity may contain:

```yaml
extensions:
  com.example.provider:
    value: 123
```

Extension namespaces should use reverse-domain naming.

Unknown extensions are opaque to clients that do not implement them.

## 11. Custom entity and component types

Custom types are allowed using namespaced identifiers.

Example:

```yaml
type: com.example.diving.excursion
```

A client without the responsible plugin must preserve the data and should offer a generic representation rather than refusing to open the repository.

## 12. People

People are trip-local entities. TravelRepo has no central user-account requirement.

Example:

```yaml
id: 0195f36b-4d2c-7701-9c76-b1a215244001
type: person
display_name: Alex

avatar:
  document: 0195f37a-51ab-76c8-a919-cfd5940fc001

identities:
  git:
    - name: Alex
      email: alex@example.com

  github:
    - username: alex-example

roles:
  - traveler

extensions: {}
```

Display names do not need to be unique.

Mentions and references use person IDs.

A client may maintain a local default travel identity and use it as a template when creating a person inside a trip. The resulting person remains trip-local.

## 13. Places

Example:

```yaml
id: 0195f36c-4d2c-7701-9c76-b1a215244100
type: place
name: Tokyo Station

location:
  latitude: 35.681236
  longitude: 139.767125

address:
  formatted: "1 Chome Marunouchi, Chiyoda City, Tokyo"

timezone: Asia/Tokyo

external_ids:
  openstreetmap: null
  google_maps: null

extensions: {}
```

Except for `id`, `type`, and `name`, place fields may be optional.

## 14. Schedule items

Each schedule item is stored in a separate file.

Example:

```yaml
id: 0195f390-2231-75f5-9500-c660bc001001
type: schedule_item
title: Flight to Tokyo
status: confirmed

time:
  precision: exact
  start:
    local: 2027-05-12T13:20:00
    timezone: Europe/Berlin
  end:
    local: 2027-05-13T08:35:00
    timezone: Asia/Tokyo

participants:
  inherit: true
  values:
    - 0195f36b-4d2c-7701-9c76-b1a215244001

components:
  transport:
    type: flight
    carrier: ANA
    flight_number: NH204
    departure:
      place: 0195f36c-4d2c-7701-9c76-b1a215244201
      terminal: "1"
      gate: null
    arrival:
      place: 0195f36c-4d2c-7701-9c76-b1a215244202
      terminal: "2"
      gate: null
    seat: "18A"

  booking:
    ref: 0195f3a2-2352-7a8e-9ef3-5a6e92000111

children:
  - 0195f390-2231-75f5-9500-c660bc001002

content:
  - type: markdown
    file: documents/0195f3b0-....md

extensions: {}
```

## 15. Time model

Local wall-clock time plus IANA timezone is the source of truth for zoned schedule data.

Example:

```yaml
start:
  local: 2027-05-12T13:20:00
  timezone: Europe/Berlin
```

Derived UTC instants should normally be computed by the library rather than duplicated in the repository.

This preserves the human meaning of travel times and supports correct display in local or converted timezones.

## 16. Flexible and uncertain time

TravelRepo must support schedule data that is not an exact timestamp.

### 16.1 Exact

```yaml
time:
  precision: exact
  start:
    local: 2027-05-12T13:20:00
    timezone: Europe/Berlin
```

### 16.2 Approximate

```yaml
time:
  precision: approximate
  start:
    local: 2027-05-12T13:00:00
    timezone: Europe/Berlin
```

### 16.3 Window

```yaml
time:
  precision: window
  earliest:
    local: 2027-05-12T14:00:00
    timezone: Europe/Berlin
  latest:
    local: 2027-05-12T18:00:00
    timezone: Europe/Berlin
```

### 16.4 Day part

```yaml
time:
  precision: day_part
  date: 2027-05-12
  day_part: afternoon
  timezone: Europe/Berlin
```

Recommended standard day-part values:
- morning
- afternoon
- evening
- night

Recommended display ranges, used when a client needs to place a day part on a timetable: morning 08:00 to 12:00, afternoon 12:00 to 17:00, evening 17:00 to 21:00, night 21:00 to midnight. These are presentation defaults, never stored data.

### 16.5 All day

```yaml
time:
  precision: all_day
  date: 2027-05-12
```

### 16.6 Unscheduled

```yaml
time: null
```

Unscheduled items may still define duration, location, preferences, and constraints.

## 17. Nested schedule items

Schedule items may contain child schedule items through ID references.

Children remain independent first-class entities stored in separate files.

Nesting is semantic, not filesystem nesting.

## 18. Inheritance

Nested items may inherit a limited set of explicitly defined fields.

Initial v1 inheritable fields:
- participants
- tags
- default place
- selected display metadata
- selected category defaults

Initial v1 non-inheritable fields:
- ID
- title
- time
- status
- concrete booking references
- transport details

An inherited field must be overridable by a child.

Clients must be able to distinguish inherited values from locally defined values.

Inheritance must not behave as an unrestricted cascading rule system.

## 19. Aggregation

Parents may derive aggregate information from children.

Examples:
- effective time range,
- total estimated cost,
- participant set,
- warning count.

Derived aggregate values should not be duplicated in the repository unless explicitly required for interoperability.

## 20. Schedule conflicts

TravelRepo permits overlapping schedule items.

Items may involve:
- one participant,
- multiple participants,
- no participants.

A conflict detection system may warn when the same participant is expected in incompatible places or overlapping activities.

Clients must not automatically block such plans unless the repository would become technically invalid.

An item with an `accommodation` component describes a stay that runs alongside other plans. Overlap with a stay is not a schedule conflict.

## 21. Travel gaps

Clients may derive expected travel gaps between schedule items.

For example, if a route provider estimates 25 minutes between two places and only 15 minutes remain, the client may show a warning.

Users may intentionally ignore these warnings.

Derived travel gaps do not need to become stored schedule items unless the user or a plugin chooses to materialize them.

## 22. Constraints

Constraints are structured data designed to support validation and future automatic planning.

Example:

```yaml
constraints:
  - type: after
    item: 0195f390-2231-75f5-9500-c660bc001111
    offset: PT2H

  - type: earliest_start
    local_time: "10:00"

  - type: arrive_before
    item: 0195f390-2231-75f5-9500-c660bc001222
    offset: PT30M
```

Custom constraint types may use extension namespaces.

## 23. Standard transport types

Initial standard transport component types:
- flight
- train
- bus
- car
- taxi
- rideshare
- ferry
- ship
- bicycle
- walking
- other

A transport item may contain multiple stops.

External routing providers may enrich transport components but must not be required for basic repository validity.

## 24. Accommodation

Accommodation should be represented as a semantic component rather than a wholly separate scheduling system.

Structured fields may include:
- place
- check-in
- check-out
- rooms
- guests
- booking reference
- confirmation data
- documents

## 25. Activities and restaurants

General activities should use schedule items plus semantic components.

Restaurants should normally be represented as:
- schedule item,
- place,
- optional booking,
- optional expense,
rather than requiring a dedicated top-level restaurant entity type.

### 25.1 Standard categories

The inheritable `category` field holds one string. Recommended values:

- activity
- sightseeing
- food
- culture
- nature
- shopping
- nightlife
- meeting
- free_time
- transport
- accommodation
- other

Clients may use the category for icons, colors and grouping. A `transport` or `accommodation` component takes precedence over the category when classifying an item. Custom categories use reverse-domain names, for example `com.example.wine_tasting`, and are treated as `other` by clients that do not know them.

## 26. Status

Initial core schedule status values:
- idea
- planned
- reserved
- confirmed
- completed
- cancelled

Plugins may add namespaced metadata without redefining the core meaning of these values.

## 27. Content

Free-form text content uses Markdown.

Recommended baseline:
- CommonMark semantics,
- a documented GitHub-Flavored Markdown-compatible subset where useful.

Rich content is represented as multiple content entries rather than a proprietary document format.

Example:

```yaml
content:
  - type: markdown
    file: documents/0195f3b0-....md

  - type: image
    document: 0195f3b1-....

  - type: pdf
    document: 0195f3b2-....

  - type: link
    url: https://example.com

  - type: reference
    entity: 0195f390-....
```

## 28. Documents and assets

Binary data is deduplicated by content hash.

Recommended storage:

```text
assets/
  sha256/
    ab/
      abcdef1234567890...
```

Document metadata remains separate from the blob.

Example:

```yaml
id: 0195f3b2-2352-7a8e-9ef3-5a6e92000444
type: document

name: booking-confirmation.pdf
media_type: application/pdf

blob:
  algorithm: sha256
  hash: abcdef1234567890...

caption: Flight booking confirmation
tags:
  - booking

extensions: {}
```

The same blob may be referenced by multiple document or content entries without duplication.

Large-file handling may integrate with Git LFS, but Git LFS is not mandatory in v1.

Clients should warn before adding unusually large files.

## 29. Images

Image document metadata may include:
- caption,
- credits,
- tags,
- related place,
- capture time,
- optional location.

## 30. PDF content

PDF files may be attached and displayed as content.

Future versions may allow OCR-derived entities or references to specific PDF pages. OCR is not required by v1.

## 31. Bookings

Bookings are independent entities and may relate to multiple schedule items.

Example:

```yaml
id: 0195f3a2-2352-7a8e-9ef3-5a6e92000111
type: booking

provider:
  name: ANA

reference: ABC123
status: confirmed

travelers:
  - 0195f36b-4d2c-7701-9c76-b1a215244001

items:
  - 0195f390-2231-75f5-9500-c660bc001001
  - 0195f390-2231-75f5-9500-c660bc001099

documents:
  - 0195f3b2-2352-7a8e-9ef3-5a6e92000444

price:
  value: "1499.90"
  currency: EUR

extensions: {}
```

A single booking may represent multiple flights, rooms, tickets, or other planned items.

## 32. Tasks

Tasks are independent entities and do not have to be schedule items.

Example:

```yaml
id: 0195f3c0-....
type: task
title: Book Shinkansen
status: open

assigned_to:
  - 0195f36b-....

due:
  date: 2027-04-15

related:
  - type: schedule_item
    id: 0195f390-....

extensions: {}
```

A task may optionally link to a schedule item when it is itself scheduled.

## 33. Comments

Comments are first-class entities.

Example:

```yaml
id: 0195f3d0-....
type: comment

target:
  type: schedule_item
  id: 0195f390-....

author: 0195f36b-....
created_at: 2027-04-10T12:42:00Z

body: |
  Maybe book the later train?

extensions: {}
```

v1 comments are linear. Threaded replies and reactions are not required.

Mentions should resolve to person IDs, not display names.

## 34. Expenses

Money values are decimal strings plus ISO 4217 currency codes.

Example:

```yaml
id: 0195f3e0-....
type: expense

title: Hotel deposit

amount:
  value: "149.90"
  currency: EUR

estimated: false

paid_by:
  - 0195f36b-....

related:
  - type: booking
    id: 0195f3a2-....

extensions: {}
```

The schema should support multi-currency from the first version even if a reference client exposes only part of the functionality initially.

## 35. Historical currency conversion

When a converted monetary value is stored, the conversion metadata must remain historically stable.

Example:

```yaml
converted:
  value: "24510"
  currency: JPY
  rate: "163.509"
  rate_date: 2027-05-01
  source: user
```

Clients must not silently rewrite historical conversions using current rates.

## 36. Budgets

Budgets may exist for:
- whole trip,
- category,
- participant,
- selected collection or group.

Budget support is part of the format direction. A reference client may ship only a basic implementation in v1.

## 37. Collections

Collections group arbitrary trip entities without changing their primary type.

Examples:
- Restaurants to consider
- Museums
- Beaches
- Maybe
- Kyoto ideas

Collections may contain references to schedule items, places, documents, or other supported entities.

## 38. Inbox

Inbox is primarily a client concept.

A TravelRepo-compatible client may represent inbox entries as normal unscheduled entities, documents, links, notes, or collections rather than introducing a format-specific hidden inbox state.

## 39. Deletion

Uncommitted deletion may be recoverable through client-local undo, trash, or recovery systems.

Once a version is created, deletion becomes a normal Git deletion.

The format does not require permanent soft-delete tombstones for v1.

## 40. Secrets

The repository must never contain:
- OAuth tokens,
- provider API secrets,
- SSH private keys,
- password credentials,
- local credential-store data.

Secrets belong in platform-native or otherwise secure local secret storage.

## 41. Encryption

The schema should leave room for encrypted document blobs.

Example direction:

```yaml
blob:
  algorithm: sha256
  hash: ...
  encryption:
    scheme: ...
    key_id: ...
```

A complete interoperable encryption and multi-user key-management scheme is not required for v1.

## 42. Plugins

The open plugin model should support capabilities such as:
- block/component providers,
- route providers,
- booking importers,
- document importers,
- place providers,
- search providers,
- price providers,
- weather providers,
- export providers,
- sharing providers,
- authentication providers.

Plugin-provided data must use stable namespaced types and must survive round trips when the plugin is absent.

## 43. External provider cache

External provider data may be cached locally for offline use.

Provider caches are not part of the canonical TravelRepo repository unless explicitly materialized into normal TravelRepo entities.

## 44. GitHub and other Git hosts

GitHub integration belongs to a reusable provider library rather than the Jourfold UI layer.

Provider integrations may implement:
- authentication,
- repository discovery,
- repository creation,
- collaborator management,
- user search,
- privacy checks,
- sharing,
- provider metadata.

Generic Git remains supported over HTTPS and SSH.

Future provider integrations may include GitLab, Gitea, Forgejo, and others.

## 45. Sharing semantics

Sharing means helping another person gain access to a trip or a specific variant.

For a provider with access-management APIs, a client may invite collaborators directly.

For generic Git:
- copy clone URL,
- copy SSH or HTTPS URL,
- copy setup instructions,
- optionally create a Jourfold deep link containing repository and variant context.

Sharing a repository URL does not itself grant access.

Credentials must never be embedded in share links.

## 46. Validation

Validation should distinguish:
- informational notices,
- warnings,
- repository-invalid errors.

Travel-plan quality issues should normally be warnings rather than blockers.

Blocking errors are reserved for cases where the repository cannot be interpreted safely or would become structurally invalid.

## 47. Repair

A reference implementation should provide repair tools for common schema, reference, and Git-state problems.

Recommended behavior:
1. validate,
2. explain,
3. attempt non-destructive repair,
4. offer rollback where a safe Git state is available.

Repair must never silently destroy unknown data.

## 48. Migration

Schema version is stored in `travel.yaml`.

Migrations should:
- be explicit,
- be deterministic,
- preserve unknown compatible data,
- create a recoverable Git version or backup before destructive transformation.

## 49. CLI direction

A reference CLI should eventually provide commands such as:

```text
travelrepo validate
travelrepo diff
travelrepo migrate
travelrepo repair
travelrepo export
travelrepo inspect
```

## 50. Reference library direction

The TravelRepo library should expose domain-level operations rather than UI concepts.

Example direction:

```csharp
var trip = await TravelRepository.OpenAsync(path);

var variants = await trip.Variants.ListAsync();

var comparison = await trip.Variants.CompareAsync(
    "main",
    "variants/kyoto-first");

await trip.Schedule.AddAsync(item);
await trip.Versions.CreateAsync(...);
```

TravelRepo core libraries must not depend on Jourfold, Avalonia, or any UI framework.

## 51. Jourfold identity model

Jourfold may maintain local default user identity settings.

When used in a trip, those defaults create or map to a normal TravelRepo `Person`.

There is no central Jourfold account requirement.

## 52. AI and automation readiness

The core API should consist of clear domain commands and queries so that future clients may expose the same capabilities through:
- CLI,
- MCP,
- agent tooling,
- skill files,
- automation,
- alternative GUIs.

AI-specific runtime behavior is not required by TravelRepo v1.

Convention: the reference implementation ships an agent guide at `skills/travelrepo/SKILL.md` and an optional local MCP server (`travelrepo mcp`, library `TravelRepo.Mcp`) that adapts the domain commands without bypassing validation. Neither is part of the file format; a repository is valid without them.

## 53. Non-goals for TravelRepo v1

Not required for the first complete implementation:
- live multi-user editing,
- recurring schedule rules,
- full OCR pipeline,
- complete encrypted multi-user key management,
- mandatory Git LFS,
- provider-specific integrations beyond initial GitHub support,
- full Splitwise-style settlement engine,
- voting and reactions,
- automatic itinerary generation,
- full web-browser generic SSH Git support.

The schema should avoid architectural decisions that make these features impossible later.

## 54. Open items for v0.2

The following should be specified next:
- exact JSON Schema or equivalent validation artifacts for YAML entities,
- formal component registry,
- canonical semantic diff format,
- domain-aware merge rules,
- exact inheritance resolution algorithm,
- route and stop schema,
- accommodation component schema,
- booking status vocabulary,
- task status vocabulary,
- budget schema,
- document encryption profile,
- Git ref discovery rules for non-standard branch layouts,
- compatibility and migration policy,
- package and namespace naming,
- conformance test suite.
