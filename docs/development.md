# Developer setup

Install .NET SDK 10.0.401 and Git. `global.json` pins the SDK. From this repository:

```sh
dotnet restore --locked-mode
dotnet build TravelRepo.sln -c Release --no-restore
dotnet test TravelRepo.sln -c Release --no-build
dotnet format TravelRepo.sln --verify-no-changes --no-restore
dotnet pack TravelRepo.sln -c Release -o artifacts/packages
```

Tests use temporary real repositories with local identities. They never modify global Git configuration or use live credentials. Conformance fixtures are under `samples`, including Tokyo, Aachen, New Zealand, flexible time, nested items, extensions, binary deduplication and merge conflicts. Generated invariant tests use fixed seeds.

The CLI and libraries target `net10.0`. Consumers may reference NuGet packages independently of Jourfold. To develop both projects locally, place this repository beside `jourfold`; Jourfold uses project references when that sibling exists and package references otherwise.

Canonical YAML is converted into a safe JSON-compatible tree, validated against embedded JSON Schema 2020-12, and then checked by `SemanticValidation`. YAML comments are non-semantic and are not preserved. Unknown fields, namespaces and compatible custom types are retained.

## Public API

```csharp
var repository = new TravelRepository(path);
var state = await repository.ReadAsync(cancellationToken);
var activity = Entity.Create("schedule_item", "Visit museum");
var updated = await repository.ApplyAsync(state,
    [new EntityEdit(activity.Id, activity)], cancellationToken);
var git = new GitRepository(repository, new GitCliBackend());
await git.CreateVersionAsync("Add museum", ct: cancellationToken);
```

Always submit a complete logical operation as one `ApplyAsync` call. It validates the resulting snapshot, checks expected hashes, records before/after images outside the repository, and atomically replaces individual files. A multi-file operation is recoverable through its journal; filesystems do not provide a universal multi-file atomic swap. `RecoverAsync` refuses to overwrite a file changed outside the recorded transaction.

`Entity.Copy()` is the edit boundary. Do not mutate snapshots shared with another caller. Expected failures use `DomainException.Code`; UI clients translate errors and may expose diagnostic detail separately. The Git service serializes write operations per repository. Clients must also hold ownership for their writable workspace.

`ZonedTime` resolves exact local time through Noda Time. Ambiguous local times require `Offset`; nonexistent exact times are rejected. Money is represented as decimal strings.

Use `SemanticMerge.Plan(base, current, incoming)` to obtain a merge plan. Unresolved plans cannot produce edits. Apply entity and resource edits together with `ApplyAsync`. A Git merge commit must record both parents after a successful merge.

## Read-only queries for clients

These helpers interpret canonical data the same way in every client. They never change the repository.

- `ScheduleQueries.Span(trip, item)` resolves a display span for every time precision. Day parts use the recommended ranges from the specification; unscheduled parents are aggregated from their children (`Derived`). `IsUnscheduled`, `Participants`, `PrimaryPlace` and `Route` cover the other common questions.
- `ScheduleCategories.Classify(trip, item)` returns a `ScheduleKind` from the transport or accommodation component, otherwise from the inheritable `category` and the standard vocabulary in `Standard`. `TransportTypes` and `Statuses` list the core values.
- `TripSummaries` provides place names for overviews, per-currency cost totals (`Costs`) without conversion, Git email to person mapping and reverse references.
- `ChangeSummary.Between(before, after)` turns a semantic diff into added, updated and removed entities with the changed top-level fields. Markdown changes are attributed to the entity that references the file.
- `TripItinerary.Build(trip, labels, culture)` produces a day-by-day reading of the plan. `TripExport.Html` and `TripExport.Ics` use it; clients pass translated `ExportLabels`.
- `GitRepository.SyncStatusAsync(remote)` compares the current branch with the last fetched remote-tracking ref without network access.
- `TravelRepository.NormalizeAsync()` rewrites files in the reference YAML style; the CLI exposes it as `travelrepo format`.

## Future automation adapters

A future `TravelRepo.Mcp` package can adapt the existing command/query APIs. It should call repository commands and validation rather than editing YAML independently. No AI runtime is loaded by these libraries.

## Integrity and concurrency

Git mutations are serialized for each repository path across service instances. Autosave uses optimistic content checks, a filesystem writer lock and fsynced before/after recovery journals outside the repository. Creating a version refuses unrelated files already staged by an external tool, leaving that index for the user to resolve.

`TravelRepository.PrepareDocumentAsync` prepares a content-addressed document and resource edit without writing. Clients can submit both through `ApplyAsync` in one undoable transaction. The binary hash is validated on reads and before commands.

A semantic merge is eligible for automatic application only when its field/resource conflicts are resolved and its result passes structural and semantic validation. Ordered sequences and Markdown support independent changes; overlapping edits remain explicit conflicts. A reviewed plan carries its input snapshots, and Jourfold rejects it if the incoming branch has changed.

`SemanticValidation.Validate` optionally accepts `TravelGapEstimate` values from a provider cache. Estimates are derived input, never mandatory canonical data. Insufficient travel time produces a warning, not an integrity error.

`ShareLink` provides credential-free `jourfold://open` context. Opening a link requires an explicit clone confirmation in Jourfold. URL parameters and embedded passwords cannot be included in shared remote URLs.

## Copying a working repository

`GitRepository.CopyWorkingTreeAsync(destination, cancellationToken)` copies a standalone repository into an empty folder, preserving local edits, all files, history, refs and repository-local configuration. This powers Jourfold's Save as workflow. The source remains unchanged. Copying is serialized with this SDK's Git mutations, staged in a temporary sibling directory, and checked for source changes before publication. Existing destination content, nested destinations, symbolic links, linked worktrees and shared object stores are rejected. The caller should prevent concurrent application edits while copying. This operation preserves remote configuration; it does not create a new server repository or grant access.
