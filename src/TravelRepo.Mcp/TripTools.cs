using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using NodaTime;
using NodaTime.Text;
using TravelRepo.Core;
using TravelRepo.Git;
using TravelRepo.Merge;
using TravelRepo.Repository;
using TravelRepo.Serialization;

namespace TravelRepo.Mcp;

/// <summary>
/// The tools of the trip server. Each call reads the current files, so edits made in Jourfold or elsewhere are
/// always seen. Results are compact JSON; errors are returned to the assistant as readable messages.
/// </summary>
internal sealed class TripTools(TravelRepository repository, IGitBackend git, bool readOnly)
{
    private static readonly JsonSerializerOptions Output = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly string[] Creatable = ["schedule_item", "place", "person", "booking", "task", "expense", "budget", "collection", "note"];
    private readonly SemaphoreSlim gate = new(1, 1);

    [McpServerTool(Name = "read_guide", Title = "Read the trip format guide", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Explains how a trip is structured (entity types, fields, times, notes) and the rules for editing it. Read it once before making changes.")]
    public string ReadGuide() => TripServer.Guide;

    [McpServerTool(Name = "get_trip", Title = "Trip overview", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Overview of the trip: title, dates, default timezone, people, variants, how many entities of each type exist, validation problems and whether there are unsaved changes.")]
    public Task<string> GetTrip(CancellationToken ct = default) => Run(async () =>
    {
        var state = await repository.ReadAsync(ct); var trip = state.Trip; var manifest = trip.Manifest.Data;
        var (start, end) = TripQueries.EffectiveDates(trip);
        var result = new JsonObject
        {
            ["id"] = trip.Manifest.Id.ToString(),
            ["title"] = trip.Manifest.Title,
            ["language"] = manifest["language"]?.DeepClone(),
            ["default_timezone"] = ScheduleQueries.TripZone(trip).Id,
            ["dates"] = new JsonObject { ["start"] = Date(start), ["end"] = Date(end), ["derived_from_schedule"] = manifest["dates"]?["start"] is null || manifest["dates"]?["end"] is null },
            ["variant"] = manifest["variant"]?["title"]?.DeepClone(),
            ["people"] = new JsonArray(trip.Entities.Values.Where(e => e.Type == "person").OrderBy(e => e.Title, StringComparer.Ordinal).Select(p => (JsonNode)new JsonObject { ["id"] = p.Id.ToString(), ["name"] = p.Title }).ToArray()),
            ["counts"] = new JsonObject(trip.Entities.Values.GroupBy(e => e.Type).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => KeyValuePair.Create(g.Key, (JsonNode?)g.Count()))),
            ["problems"] = new JsonObject { ["errors"] = state.Diagnostics.Count(d => d.Severity == Severity.Error), ["warnings"] = state.Diagnostics.Count(d => d.Severity == Severity.Warning) },
            ["read_only"] = readOnly
        };
        try
        {
            var repo = new GitRepository(repository, git);
            result["unsaved_changes"] = (await repo.StatusAsync(ct)).Length > 0;
            var current = await repo.CurrentBranchAsync(ct);
            result["variants"] = new JsonArray((await repo.VariantsAsync(ct)).Where(v => !v.IsRemote).Select(v => (JsonNode)new JsonObject
            {
                ["branch"] = v.Branch,
                ["title"] = v.Manifest.Data["variant"]?["title"]?.DeepClone(),
                ["state"] = v.Manifest.Data["variant"]?["state"]?.DeepClone(),
                ["current"] = v.Branch == current
            }).ToArray());
        }
        catch (DomainException) { result["git"] = "This trip folder has no Git history, so versions and variants are not available."; }
        return result;
    });

    [McpServerTool(Name = "list_entities", Title = "List entities", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists entities with their id, title and, for schedule items, status, time and place. Filter by type and/or by text that appears anywhere in the entity.")]
    public Task<string> ListEntities(
        [Description("Optional entity type, for example schedule_item, place, person, booking, task, expense, note.")] string? type = null,
        [Description("Optional search text, matched case-insensitively against all fields.")] string? text = null,
        [Description("Maximum number of results (default 200).")] int limit = 200,
        CancellationToken ct = default) => Run(async () =>
    {
        var trip = (await repository.ReadAsync(ct)).Trip;
        IEnumerable<Entity> found = text is { Length: > 0 } ? TripQueries.Search(trip, text) : trip.All;
        if (type is { Length: > 0 }) found = found.Where(e => e.Type == type);
        var list = found.OrderBy(e => e.Type, StringComparer.Ordinal).ThenBy(e => ScheduleQueries.Span(trip, e).Start ?? Instant.MaxValue).ThenBy(e => e.Title, StringComparer.Ordinal).ToArray();
        return new JsonObject { ["total"] = list.Length, ["entities"] = new JsonArray(list.Take(Math.Clamp(limit, 1, 1000)).Select(e => (JsonNode)Summary(trip, e)).ToArray()) };
    });

    [McpServerTool(Name = "get_entity", Title = "Read an entity", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Returns all fields of one entity, the text of its notes, its parent and the entities that refer to it.")]
    public Task<string> GetEntity([Description("The entity id.")] string id, CancellationToken ct = default) => Run(async () =>
    {
        var trip = (await repository.ReadAsync(ct)).Trip; var entity = Find(trip, id);
        var result = new JsonObject { ["entity"] = entity.Data.DeepClone() };
        if (entity.Type == "schedule_item") { result["when"] = When(trip, entity); result["place"] = ScheduleQueries.PrimaryPlace(trip, entity)?.Title; }
        var notes = (entity.Data["content"] as JsonArray ?? []).OfType<JsonObject>().Where(c => c["type"]?.ToString() == "markdown" && c["file"] is not null)
            .Select(c => (JsonNode)new JsonObject { ["file"] = c["file"]!.ToString(), ["markdown"] = trip.Resources.TryGetValue(c["file"]!.ToString(), out var bytes) ? Encoding.UTF8.GetString(bytes) : null }).ToArray();
        if (notes.Length > 0) result["notes"] = new JsonArray(notes);
        if (TripQueries.Parent(trip, entity.Id) is { } parent) result["parent"] = Summary(trip, parent);
        result["referenced_by"] = new JsonArray(TripSummaries.ReferencesTo(trip, entity.Id).Select(e => (JsonNode)Summary(trip, e)).ToArray());
        return result;
    });

    [McpServerTool(Name = "get_schedule", Title = "Read the schedule", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The schedule day by day in the trip's timezone, with each item's time, status, category, place and people, followed by the items that are not scheduled yet (ideas). Without dates it covers the whole trip, at most 62 days.")]
    public Task<string> GetSchedule(
        [Description("Optional first day, YYYY-MM-DD.")] string? from = null,
        [Description("Optional last day, YYYY-MM-DD.")] string? to = null,
        CancellationToken ct = default) => Run(async () =>
    {
        var trip = (await repository.ReadAsync(ct)).Trip; var zone = ScheduleQueries.TripZone(trip);
        var items = trip.Entities.Values.Where(e => e.Type == "schedule_item").Select(e => (Item: e, Span: ScheduleQueries.Span(trip, e))).Where(x => x.Span.IsPlaced)
            .OrderBy(x => x.Span.Start).ThenBy(x => x.Item.Title, StringComparer.Ordinal).ToArray();
        var (tripStart, tripEnd) = TripQueries.EffectiveDates(trip);
        var first = ParseDate(from, "from") ?? tripStart ?? items.Select(x => x.Span.Start!.Value.InZone(zone).Date).DefaultIfEmpty(SystemClock.Instance.GetCurrentInstant().InZone(zone).Date).Min();
        var last = ParseDate(to, "to") ?? tripEnd ?? items.Select(x => x.Span.End!.Value.InZone(zone).Date).DefaultIfEmpty(first).Max();
        if (last < first) throw new McpException("The last day is before the first day.");
        if (Period.Between(first, last, PeriodUnits.Days).Days > 61) last = first.PlusDays(61);
        var days = new JsonArray();
        for (var day = first; day <= last; day = day.PlusDays(1))
        {
            var dayStart = zone.AtStartOfDay(day).ToInstant(); var dayEnd = zone.AtStartOfDay(day.PlusDays(1)).ToInstant();
            // Items are listed on the day they start; on the first listed day also those still running from before.
            var entries = items.Where(x => x.Span.Start >= dayStart && x.Span.Start < dayEnd || day == first && x.Span.Start < dayStart && x.Span.End > dayStart)
                .Select(x => (JsonNode)Summary(trip, x.Item, withParent: true)).ToArray();
            days.Add(new JsonObject { ["date"] = Date(day), ["weekday"] = day.DayOfWeek.ToString(), ["items"] = new JsonArray(entries) });
        }
        var ideas = trip.Entities.Values.Where(e => ScheduleQueries.IsUnscheduled(trip, e)).OrderBy(e => e.Title, StringComparer.Ordinal)
            .Select(e => (JsonNode)Summary(trip, e).Also(o => o["duration"] = e.Data["duration"]?.DeepClone())).ToArray();
        return new JsonObject { ["timezone"] = zone.Id, ["days"] = days, ["not_scheduled"] = new JsonArray(ideas) };
    });

    [McpServerTool(Name = "get_schema", Title = "Read an entity schema", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The JSON Schema that entities of one type must satisfy. Useful when a change is rejected as invalid.")]
    public string GetSchema([Description("Entity type, for example schedule_item or place.")] string type) =>
        SchemaValidation.SchemaText(type) ?? throw new McpException("Unknown type. Known types: " + string.Join(", ", SchemaValidation.Types) + ".");

    [McpServerTool(Name = "validate", Title = "Check the trip", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Checks all files of the trip and lists errors and warnings, for example unknown timezones, broken references or an end before the start.")]
    public Task<string> Validate(CancellationToken ct = default) => Run(async () =>
    {
        var state = await repository.ReadAsync(ct);
        var problems = state.Diagnostics.OrderByDescending(d => d.Severity).Select(d => (JsonNode)new JsonObject
        {
            ["severity"] = d.Severity.ToString().ToLowerInvariant(),
            ["code"] = d.Code,
            ["path"] = d.Path,
            ["message"] = d.Code == "schema.invalid" && SchemaValidation.Explain(d.Message) is { Count: > 0 } lines ? string.Join("; ", lines) : d.Message
        }).ToArray();
        return new JsonObject { ["valid"] = !state.Diagnostics.Any(d => d.Severity == Severity.Error), ["problems"] = new JsonArray(problems) };
    });

    [McpServerTool(Name = "list_versions", Title = "List versions", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The saved versions of the trip (Git commits), newest first.")]
    public Task<string> ListVersions([Description("Maximum number of versions (default 20).")] int limit = 20, CancellationToken ct = default) => Run(async () =>
    {
        var history = await new GitRepository(repository, git).HistoryAsync(ct);
        return new JsonObject
        {
            ["versions"] = new JsonArray(history.Take(Math.Clamp(limit, 1, 100)).Select(v => (JsonNode)new JsonObject
            {
                ["commit"] = v.Commit[..Math.Min(12, v.Commit.Length)],
                ["date"] = v.Timestamp,
                ["author"] = v.Author,
                ["message"] = v.Message.Split('\n')[0].Trim()
            }).ToArray())
        };
    });

    [McpServerTool(Name = "compare", Title = "Compare with a version or variant", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists which entities differ between a saved version or a variant and the current files: added, updated (with the changed fields) or removed. Use \"HEAD\" for the unsaved changes since the last version.")]
    public Task<string> Compare([Description("A commit from list_versions, a variant branch from get_trip, or HEAD.")] string revision = "HEAD", CancellationToken ct = default) => Run(async () =>
    {
        if (revision.Length == 0 || revision.StartsWith('-') || revision.Any(c => char.IsWhiteSpace(c) || c == ':')) throw new McpException("Give a commit id, a branch name or HEAD.");
        var before = await new GitRepository(repository, git).SnapshotAsync(revision, ct); var after = (await repository.ReadAsync(ct)).Trip;
        var summary = ChangeSummary.Between(before, after);
        return new JsonObject
        {
            ["changes"] = new JsonArray(summary.Entities.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Entity.ToString(),
                ["type"] = c.Type,
                ["title"] = c.Title,
                ["change"] = c.Kind.ToString().ToLowerInvariant(),
                ["fields"] = new JsonArray(c.Fields.Select(f => (JsonNode)f).ToArray())
            }).ToArray()),
            ["files"] = new JsonArray(summary.Resources.Select(r => (JsonNode)(r.Kind.ToString().ToLowerInvariant() + ": " + r.Path)).ToArray())
        };
    });

    [McpServerTool(Name = "create_entity", Title = "Create an entity", Destructive = false, OpenWorld = false)]
    [Description("Creates a schedule item, place, person, booking, task, expense, budget, collection or note. The id is created for you. A new person is also added to the trip's participants. Returns the saved entity.")]
    public Task<string> CreateEntity(
        [Description("Entity type: schedule_item, place, person, booking, task, expense, budget, collection or note.")] string type,
        [Description("The fields as a JSON object, for example {\"title\": \"Fushimi Inari\", \"status\": \"idea\", \"category\": \"sightseeing\", \"duration\": \"PT2H\"}. Places use \"name\", people \"display_name\". See read_guide.")] JsonElement data,
        CancellationToken ct = default) => Run(async () =>
    {
        if (!Creatable.Contains(type)) throw new McpException("Use one of these types: " + string.Join(", ", Creatable) + ".");
        var fields = ObjectOf(data, "data");
        var title = (fields["title"] ?? fields["name"] ?? fields["display_name"])?.ToString();
        if (string.IsNullOrWhiteSpace(title)) throw new McpException("Give the new " + type + " a " + (type == "person" ? "display_name" : type == "place" ? "name" : "title") + ".");
        var entity = Entity.Create(type, title.Trim());
        if (type == "collection") entity.Data["items"] = new JsonArray();
        foreach (var (key, value) in fields) if (key is not ("id" or "type")) entity.Data[key] = value?.DeepClone();
        var saved = await Write(state =>
        {
            var edits = new List<EntityEdit> { new(entity.Id, entity) };
            if (type == "person")
            {
                var manifest = state.Trip.Manifest.Copy(); var participants = manifest.Data["participants"] as JsonArray ?? [];
                manifest.Data["participants"] = participants; participants.Add(entity.Id.ToString()); edits.Add(new(manifest.Id, manifest));
            }
            return (edits, null);
        }, ct);
        return new JsonObject { ["id"] = entity.Id.ToString(), ["entity"] = saved.Trip.Find(entity.Id)!.Data.DeepClone() };
    });

    [McpServerTool(Name = "update_entity", Title = "Change an entity", Destructive = false, OpenWorld = false)]
    [Description("Changes fields of an entity, including the trip itself (use get_trip for its id). `changes` is a JSON merge patch: given fields replace the old values, nested objects are merged, and null removes a field. Lists are replaced as a whole. Returns the saved entity.")]
    public Task<string> UpdateEntity(
        [Description("The entity id.")] string id,
        [Description("The fields to change as a JSON object, for example {\"status\": \"planned\", \"time\": {\"precision\": \"exact\", \"start\": {\"local\": \"2027-05-14T09:00:00\", \"timezone\": \"Asia/Tokyo\"}, \"end\": {\"local\": \"2027-05-14T11:00:00\", \"timezone\": \"Asia/Tokyo\"}}}.")] JsonElement changes,
        CancellationToken ct = default) => Run(async () =>
    {
        var patch = ObjectOf(changes, "changes");
        foreach (var key in new[] { "id", "type", "schema", "variant" }) if (patch.ContainsKey(key)) throw new McpException("The field \"" + key + "\" cannot be changed.");
        Guid target = default;
        var saved = await Write(state =>
        {
            var entity = Find(state.Trip, id).Copy(); target = entity.Id;
            Merge(entity.Data, patch);
            return ([new(entity.Id, entity)], null);
        }, ct);
        return new JsonObject { ["id"] = target.ToString(), ["entity"] = saved.Trip.Find(target)!.Data.DeepClone() };
    });

    [McpServerTool(Name = "delete_entity", Title = "Delete an entity", Destructive = true, OpenWorld = false)]
    [Description("Deletes an entity and its notes. It is also removed from the trip's participants and from its parent's children. If other entities still refer to it, nothing is deleted and they are listed, so you can change them first.")]
    public Task<string> DeleteEntity([Description("The entity id.")] string id, CancellationToken ct = default) => Run(async () =>
    {
        string title = "";
        await Write(state =>
        {
            var trip = state.Trip; var entity = Find(trip, id); title = entity.Title;
            if (entity.Type == "trip") throw new McpException("The trip itself cannot be deleted.");
            var key = entity.Id.ToString(); var edits = new List<EntityEdit> { new(entity.Id, null) }; var blocking = new List<Entity>();
            foreach (var other in TripSummaries.ReferencesTo(trip, entity.Id))
            {
                var copy = other.Copy();
                var list = other.Type == "trip" ? copy.Data["participants"] as JsonArray : copy.Data["children"] as JsonArray;
                foreach (var node in list?.Where(n => n?.ToString() == key).ToArray() ?? []) list!.Remove(node);
                // Membership lists are cleaned up here; any other reference has to be changed deliberately first.
                if (copy.Data.ToJsonString().Contains(key, StringComparison.Ordinal)) blocking.Add(other); else edits.Add(new(copy.Id, copy));
            }
            if (blocking.Count > 0) throw new McpException("Not deleted: still referenced by " + string.Join(", ", blocking.Select(b => b.Type + " \"" + b.Title + "\" (" + b.Id + ")")) + ". Remove those references with update_entity first.");
            var notes = (entity.Data["content"] as JsonArray ?? []).OfType<JsonObject>().Where(c => c["type"]?.ToString() == "markdown").Select(c => c["file"]?.ToString()).OfType<string>()
                .Where(file => trip.Resources.ContainsKey(file) && !trip.All.Any(e => e.Id != entity.Id && e.Data.ToJsonString().Contains(file, StringComparison.Ordinal)))
                .Select(file => new ResourceEdit(file, null)).ToArray();
            return (edits, notes);
        }, ct);
        return new JsonObject { ["deleted"] = id, ["title"] = title };
    });

    [McpServerTool(Name = "add_note", Title = "Add a note", Destructive = false, OpenWorld = false)]
    [Description("Adds a Markdown note to an entity, for example opening hours, tips or the reasons for a suggestion. The note appears under Notes in Jourfold.")]
    public Task<string> AddNote([Description("The entity id.")] string id, [Description("The note text in Markdown.")] string markdown, CancellationToken ct = default) => Run(async () =>
    {
        if (string.IsNullOrWhiteSpace(markdown)) throw new McpException("The note is empty.");
        var file = "documents/" + Guid.CreateVersion7() + ".md"; Guid target = default;
        await Write(state =>
        {
            var entity = Find(state.Trip, id).Copy(); target = entity.Id;
            var content = entity.Data["content"] as JsonArray ?? []; entity.Data["content"] = content;
            content.Add(new JsonObject { ["type"] = "markdown", ["file"] = file });
            return ([new(entity.Id, entity)], [new(file, Encoding.UTF8.GetBytes(markdown.Trim() + "\n"))]);
        }, ct);
        return new JsonObject { ["id"] = target.ToString(), ["file"] = file };
    });

    [McpServerTool(Name = "create_version", Title = "Save a version", Destructive = false, OpenWorld = false)]
    [Description("Saves all current changes as a new version of the trip (a Git commit) with a short description. Only do this when the user asks for it.")]
    public Task<string> CreateVersion([Description("What changed, in one line, for example \"Plan the Kyoto day trip\".")] string message, CancellationToken ct = default) => Run(async () =>
    {
        if (string.IsNullOrWhiteSpace(message)) throw new McpException("Describe the version in one line.");
        var commit = await new GitRepository(repository, git).CreateVersionAsync(message.Split('\n')[0].Trim(), "assistant-version", ct);
        return new JsonObject { ["commit"] = commit[..Math.Min(12, commit.Length)], ["message"] = message.Split('\n')[0].Trim() };
    });

    // Helpers -------------------------------------------------------------------------------------------

    /// <summary>Runs one tool call at a time and turns domain errors into messages the assistant can act on.</summary>
    private async Task<string> Run(Func<Task<JsonNode>> action)
    {
        await gate.WaitAsync();
        try { return (await action()).ToJsonString(Output); }
        catch (DomainException ex) { throw new McpException(Explain(ex)); }
        catch (IOException ex) { throw new McpException("The trip files could not be read or written: " + ex.Message); }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Reads the current files, plans the edits and applies them as one validated transaction. When another program
    /// (such as Jourfold) wrote in between, the plan is made again on the new files.
    /// </summary>
    private async Task<RepositoryState> Write(Func<RepositoryState, (IReadOnlyList<EntityEdit> Entities, IReadOnlyList<ResourceEdit>? Resources)> plan, CancellationToken ct)
    {
        if (readOnly) throw new McpException("This server is read-only.");
        for (var attempt = 0; ; attempt++)
        {
            var state = await repository.ReadAsync(ct);
            var (entities, resources) = plan(state);
            try { return await repository.ApplyAsync(state, entities, ct, resources); }
            catch (DomainException ex) when (ex.Code == "repository.changed" && attempt < 2) { }
        }
    }

    private static string Explain(DomainException ex)
    {
        var lines = ex.Message.Split('\n').SelectMany(line => line.StartsWith("schema.invalid: ", StringComparison.Ordinal) && SchemaValidation.Explain(line["schema.invalid: ".Length..]) is { Count: > 0 } rules
            ? rules.Select(rule => "schema: " + rule) : [line]);
        var hint = ex.Code switch
        {
            "repository.invalid" => "\nCall validate to see the problems.",
            "command.invalid" => "\nNothing was saved. Fix the values (see read_guide or get_schema) and try again.",
            _ => ""
        };
        return ex.Code + ": " + string.Join("\n", lines) + hint;
    }

    private static Entity Find(TripSnapshot trip, string id) =>
        Guid.TryParse(id?.Trim(), out var guid) ? trip.Find(guid) ?? throw new McpException("No entity has the id " + id + ". Use list_entities to find ids.")
        : throw new McpException("\"" + id + "\" is not an entity id. Ids look like 0195f390-2231-75f5-9500-c660bc001001.");

    /// <summary>A JSON object argument. Some models send objects as JSON text, so a string holding an object is accepted too.</summary>
    private static JsonObject ObjectOf(JsonElement element, string name)
    {
        try
        {
            var node = element.ValueKind == JsonValueKind.String ? JsonNode.Parse(element.GetString()!) : JsonNode.Parse(element.GetRawText());
            return node as JsonObject ?? throw new McpException("\"" + name + "\" must be a JSON object.");
        }
        catch (JsonException) { throw new McpException("\"" + name + "\" must be a JSON object."); }
    }

    /// <summary>JSON merge patch (RFC 7396).</summary>
    private static void Merge(JsonObject target, JsonObject patch)
    {
        foreach (var (key, value) in patch)
        {
            if (value is null) target.Remove(key);
            else if (value is JsonObject inner && target[key] is JsonObject existing) Merge(existing, inner);
            else target[key] = value.DeepClone();
        }
    }

    private static JsonObject Summary(TripSnapshot trip, Entity e, bool withParent = false)
    {
        var result = new JsonObject { ["id"] = e.Id.ToString(), ["type"] = e.Type, ["title"] = e.Title };
        if (e.Data["status"] is { } status) result["status"] = status.DeepClone();
        if (e.Type != "schedule_item") return result;
        result["category"] = Category(trip, e);
        result["when"] = When(trip, e);
        if (ScheduleQueries.PrimaryPlace(trip, e) is { } place) result["place"] = place.Title;
        if (ScheduleQueries.Participants(trip, e) is { Count: > 0 } people) result["people"] = new JsonArray(people.Select(p => (JsonNode)p.Title).ToArray());
        if (withParent && TripQueries.Parent(trip, e.Id) is { } parent) result["part_of"] = parent.Title;
        return result;
    }

    private static string Category(TripSnapshot trip, Entity item) =>
        item.Data["category"]?.ToString() is { Length: > 0 } category ? category
        : ScheduleCategories.Classify(trip, item) switch { ScheduleKind.FreeTime => "free_time", var kind => kind.ToString().ToLowerInvariant() };

    /// <summary>The time of a schedule item as readable local times, for example "2027-05-12 Wed 13:20 Europe/Berlin to 2027-05-13 Thu 08:35 Asia/Tokyo".</summary>
    private static string? When(TripSnapshot trip, Entity item)
    {
        var span = ScheduleQueries.Span(trip, item); var tripZone = ScheduleQueries.TripZone(trip).Id;
        if (!span.IsPlaced) return null;
        string At(Instant instant, string? zone, bool withDate)
        {
            var local = instant.InZone(DateTimeZoneProviders.Tzdb.GetZoneOrNull(zone ?? tripZone) ?? DateTimeZone.Utc).LocalDateTime;
            return (withDate ? LocalDatePattern.Iso.Format(local.Date) + " " + local.DayOfWeek.ToString()[..3] + " " : "") + local.TimeOfDay.ToString("HH:mm", CultureInfo.InvariantCulture);
        }
        var startZone = span.StartZone ?? tripZone; var endZone = span.EndZone ?? startZone;
        var start = span.Start!.Value; var end = span.End!.Value;
        var sameDay = startZone == endZone && start.InZone(DateTimeZoneProviders.Tzdb[startZone]).Date == end.Minus(Duration.FromTicks(1)).InZone(DateTimeZoneProviders.Tzdb[startZone]).Date;
        var range = sameDay ? At(start, startZone, true) + " to " + At(end, endZone, false) + " " + startZone
            : At(start, startZone, true) + (startZone == endZone ? "" : " " + startZone) + " to " + At(end, endZone, true) + " " + endZone;
        return span.Precision switch
        {
            TimePrecision.Approximate => "about " + range,
            TimePrecision.Window => "sometime between " + range,
            TimePrecision.DayPart => Date(span.Date) + " " + span.DayPart + " (" + startZone + ")",
            TimePrecision.AllDay => Date(span.Date) + " all day",
            _ when span.Derived => range + " (from its parts)",
            _ => range
        };
    }

    private static string? Date(LocalDate? date) => date is { } d ? LocalDatePattern.Iso.Format(d) : null;

    private static LocalDate? ParseDate(string? text, string name) =>
        string.IsNullOrWhiteSpace(text) ? null : LocalDatePattern.Iso.Parse(text.Trim()) is { Success: true } result ? result.Value : throw new McpException("\"" + name + "\" must be a date like 2027-05-12.");
}

internal static class JsonNodeExtensions
{
    public static T Also<T>(this T value, Action<T> action) { action(value); return value; }
}
