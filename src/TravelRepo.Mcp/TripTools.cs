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
using TravelRepo.Providers.Weather;
using TravelRepo.Repository;
using TravelRepo.Serialization;

namespace TravelRepo.Mcp;

/// <summary>
/// The tools of the trip server. Each call reads the current files, so edits made in Jourfold or elsewhere are
/// always seen. Results are compact JSON; errors are returned to the assistant as readable messages.
/// </summary>
internal sealed class TripTools(TravelRepository repository, IGitBackend git, bool readOnly, Func<CachedWeatherSource?>? weather = null, string? clientName = null)
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
        if (Stays.Resolve(trip, entity) is { } stay) result["stay"] = StayJson(stay);
        var notes = (entity.Data["content"] as JsonArray ?? []).OfType<JsonObject>().Where(c => c["type"]?.ToString() == "markdown" && c["file"] is not null)
            .Select(c => (JsonNode)new JsonObject { ["file"] = c["file"]!.ToString(), ["markdown"] = trip.Resources.TryGetValue(c["file"]!.ToString(), out var bytes) ? Encoding.UTF8.GetString(bytes) : null }).ToArray();
        if (notes.Length > 0) result["notes"] = new JsonArray(notes);
        if (TripQueries.Parent(trip, entity.Id) is { } parent) result["parent"] = Summary(trip, parent);
        result["referenced_by"] = new JsonArray(TripSummaries.ReferencesTo(trip, entity.Id).Select(e => (JsonNode)Summary(trip, e)).ToArray());
        return result;
    });

    [McpServerTool(Name = "get_schedule", Title = "Read the schedule", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The schedule day by day in the trip's timezone, with each item's time, status, category, outdoor estimate, place and people, followed by the items that are not scheduled yet (ideas). Without dates it covers the whole trip, at most 62 days.")]
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

    [McpServerTool(Name = "list_stays", Title = "List stays and nights", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists the stays (items with an accommodation component) with the nights they cover, nights between the trip's first and last day without a stay or an overnight item, and stays that should be one stay, for example one item per night at the same place.")]
    public Task<string> ListStays(CancellationToken ct = default) => Run(async () =>
    {
        var trip = (await repository.ReadAsync(ct)).Trip;
        return new JsonObject
        {
            ["stays"] = new JsonArray(Stays.All(trip).Select(s => (JsonNode)StayJson(s).Also(o => { o["id"] = s.Item.Id.ToString(); o["title"] = s.Item.Title; o["place"] = s.Place is { } p ? trip.Find(p)?.Title : null; })).ToArray()),
            ["nights_without_stay"] = new JsonArray(Stays.MissingNights(trip).Select(d => (JsonNode)Date(d)!).ToArray()),
            ["merge_suggestions"] = new JsonArray(Stays.MergeCandidates(trip).Select(c => (JsonNode)new JsonObject { ["ids"] = new JsonArray(c.Stays.Select(s => (JsonNode)s.Item.Id.ToString()).ToArray()), ["first_night"] = Date(c.FirstNight), ["nights"] = c.Nights }).ToArray())
        };
    });

    [McpServerTool(Name = "merge_stays", Title = "Merge stays into one", Destructive = true, OpenWorld = false)]
    [Description("Merges stays that list_stays suggests merging into the first of them: it covers all their nights, keeps all notes, the most advanced status and the links to it; the others are deleted. Pass the ids of one suggestion.")]
    public Task<string> MergeStays([Description("The ids of one merge suggestion from list_stays.")] string[] ids, CancellationToken ct = default) => Run(async () =>
    {
        StayMerge? done = null;
        await Write(state =>
        {
            var wanted = ids.Select(id => Find(state.Trip, id).Id).ToHashSet();
            var candidate = Stays.MergeCandidates(state.Trip).FirstOrDefault(c => c.Stays.Select(s => s.Item.Id).ToHashSet().SetEquals(wanted))
                ?? throw new McpException("These stays are not a merge suggestion. Call list_stays and pass the ids of one suggestion.");
            done = Stays.Merge(state.Trip, candidate);
            return (done.Removed.Select(id => new EntityEdit(id, null)).Append(new EntityEdit(done.Kept.Id, done.Kept)).Concat(done.Redirected.Select(e => new EntityEdit(e.Id, e))).ToArray(), null);
        }, ct);
        return new JsonObject { ["kept"] = done!.Kept.Id.ToString(), ["removed"] = new JsonArray(done.Removed.Select(id => (JsonNode)id.ToString()).ToArray()), ["stay"] = StayJson(Stays.Resolve((await repository.ReadAsync(ct)).Trip, done.Kept)!) };
    });

    private static JsonObject StayJson(Stay stay) => new()
    {
        ["first_night"] = stay.FirstNight is { } first ? Date(first) : null,
        ["nights"] = stay.Nights,
        ["check_in"] = stay.CheckIn?.ToJson(),
        ["check_out"] = stay.CheckOut?.ToJson(),
        ["check_in_hours"] = stay.CheckInWindow?.ToJson(),
        ["check_out_hours"] = stay.CheckOutWindow?.ToJson(),
        ["hours_not_fixed"] = stay.CheckInApproximate || stay.CheckOutApproximate,
        ["access"] = stay.Access,
        ["free_cancellation_until"] = stay.FreeCancellationUntil?.ToJson(),
        ["issues"] = new JsonArray(Enum.GetValues<StayIssues>().Where(i => i != StayIssues.None && stay.Issues.HasFlag(i)).Select(i => (JsonNode)i.ToString()).ToArray())
    };

    [McpServerTool(Name = "get_packing_lists", Title = "Read packing lists", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The trip's packing lists with their items in order: group, quantity, who brings each item (\"everyone\" means every traveller brings their own, including people added later; several people share an item), who has packed it, whether it is done, and which items nobody brings yet. Optionally only the items of one person.")]
    public Task<string> GetPackingLists([Description("Optional person id or name: only the items this person brings.")] string? person = null, CancellationToken ct = default) => Run(async () =>
    {
        var trip = (await repository.ReadAsync(ct)).Trip; var only = person is null ? null : Person(trip, person);
        return new JsonObject
        {
            ["lists"] = new JsonArray(PackingLists.All(trip).Select(list => (JsonNode)new JsonObject
            {
                ["id"] = list.Collection.Id.ToString(),
                ["title"] = list.Collection.Title,
                ["done"] = list.DoneCount,
                ["total"] = list.Items.Count,
                ["items"] = new JsonArray((only is null ? list.Items : PackingLists.For(list, only.Id)).Select(i => (JsonNode)PackingItemJson(i)).ToArray()),
                ["unassigned"] = new JsonArray(PackingLists.Unassigned(list).Select(i => (JsonNode)i.Task.Title).ToArray())
            }).ToArray())
        };
    });

    [McpServerTool(Name = "export_text", Title = "Export as text", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The trip plan or the packing lists as plain text, using the same rules as the app's exports: for example one person's plan for a few days, or one person's packing list to paste into a message. Packing items start with a ballot box, ticked when done.")]
    public Task<string> ExportText(
        [Description("\"plan\" for the itinerary or \"packing\" for the packing lists.")] string kind = "plan",
        [Description("Optional person id or name: only what concerns this person.")] string? person = null,
        [Description("Optional first day, yyyy-mm-dd (plan only).")] string? from = null,
        [Description("Optional last day, yyyy-mm-dd (plan only).")] string? to = null,
        [Description("Optional parts for the plan, comma-separated: schedule, stays, bookings, contacts, costs, tasks, notes, packing. Default: schedule, stays, bookings, contacts, costs.")] string? parts = null,
        [Description("True for times, titles, places and booking references only.")] bool compact = false,
        CancellationToken ct = default) => Run(async () =>
    {
        var trip = (await repository.ReadAsync(ct)).Trip; var who = person is null ? (Guid?)null : Person(trip, person).Id;
        try
        {
            var text = kind switch
            {
                "packing" => PackingExport.Text(trip, new PackingExportOptions { Person = who }),
                "plan" => TripExport.Text(TripExport.Document(trip, new ExportOptions { Person = who, From = ExportOptions.ParseDate(from), To = ExportOptions.ParseDate(to), Parts = parts is null ? ExportParts.Standard : ExportOptions.ParseParts(parts) & ~(ExportParts.Map | ExportParts.Attachments), Detail = compact ? ExportDetail.Compact : ExportDetail.Detailed })),
                _ => throw new McpException("Use kind \"plan\" or \"packing\".")
            };
            return new JsonObject { ["text"] = text };
        }
        catch (DomainException e) { throw new McpException(e.Message); }
    });

    [McpServerTool(Name = "create_packing_list", Title = "Create a packing list", Destructive = false, OpenWorld = false)]
    [Description("Creates an empty packing list, for example \"Barbecue\" or \"Camping gear\". Add items with add_packing_item.")]
    public Task<string> CreatePackingList([Description("The list's title.")] string title, CancellationToken ct = default) => Run(async () =>
    {
        if (string.IsNullOrWhiteSpace(title)) throw new McpException("A packing list needs a title.");
        var list = PackingLists.CreateList(title.Trim());
        await Write(_ => ([new EntityEdit(list.Id, list)], null), ct);
        return new JsonObject { ["id"] = list.Id.ToString(), ["title"] = list.Title };
    });

    [McpServerTool(Name = "add_packing_item", Title = "Add a packing item", Destructive = false, OpenWorld = false)]
    [Description("Adds an item to the end of a packing list. People are ids or names; leave them out for an item nobody brings yet, or set everyone for an item every traveller brings their own of.")]
    public Task<string> AddPackingItem(
        [Description("The packing list's id.")] string list,
        [Description("What to bring, for example \"Grill\".")] string title,
        [Description("Optional group, for example \"Cooking\".")] string? group = null,
        [Description("Optional quantity, for example \"2 packs\".")] string? quantity = null,
        [Description("Optional people who bring it together (ids or names).")] string[]? people = null,
        [Description("True when every traveller brings their own.")] bool everyone = false,
        CancellationToken ct = default) => Run(async () =>
    {
        if (string.IsNullOrWhiteSpace(title)) throw new McpException("An item needs a title.");
        Entity? added = null;
        await Write(state =>
        {
            var collection = Find(state.Trip, list); if (!PackingLists.IsList(collection)) throw new McpException("This is not a packing list. Use get_packing_lists to find their ids.");
            var (item, updated) = PackingLists.AddItem(collection, title.Trim(), group, quantity, (people ?? []).Select(p => Person(state.Trip, p).Id).ToArray(), everyone);
            added = item; return ([new EntityEdit(item.Id, item), new EntityEdit(updated.Id, updated)], null);
        }, ct);
        return PackingItemJson(PackingLists.Item((await repository.ReadAsync(ct)).Trip, added!));
    });

    [McpServerTool(Name = "assign_packing_item", Title = "Choose who brings an item", Destructive = false, OpenWorld = false)]
    [Description("Sets who brings a packing item: people (ids or names) who share it, everyone (each traveller brings their own), or nobody. Marks of who has packed it are kept.")]
    public Task<string> AssignPackingItem(
        [Description("The item's id.")] string item,
        [Description("People who bring it together (ids or names); empty for nobody.")] string[]? people = null,
        [Description("True when every traveller brings their own.")] bool everyone = false,
        CancellationToken ct = default) => Run(async () =>
    {
        await Write(state =>
        {
            var task = PackingTask(state.Trip, item);
            var updated = PackingLists.Assign(task, (people ?? []).Select(p => Person(state.Trip, p).Id).ToArray(), everyone);
            return ([new EntityEdit(updated.Id, updated)], null);
        }, ct);
        var trip = (await repository.ReadAsync(ct)).Trip; return PackingItemJson(PackingLists.Item(trip, PackingTask(trip, item)));
    });

    [McpServerTool(Name = "set_packed", Title = "Mark an item as packed", Destructive = false, OpenWorld = false)]
    [Description("Marks a packing item as packed, or not packed, for one person. Only do this when the person says so.")]
    public Task<string> SetPacked([Description("The item's id.")] string item, [Description("The person's id or name.")] string person, [Description("False to undo.")] bool packed = true, CancellationToken ct = default) => Run(async () =>
    {
        await Write(state => { var task = PackingTask(state.Trip, item); var updated = PackingLists.SetPacked(state.Trip, task, Person(state.Trip, person).Id, packed); return ([new EntityEdit(updated.Id, updated)], null); }, ct);
        var trip = (await repository.ReadAsync(ct)).Trip; return PackingItemJson(PackingLists.Item(trip, PackingTask(trip, item)));
    });

    private static JsonObject PackingItemJson(PackingItem i) => new()
    {
        ["id"] = i.Task.Id.ToString(),
        ["title"] = i.Task.Title,
        ["group"] = i.Group,
        ["quantity"] = i.Quantity,
        ["brought_by"] = i.Everyone ? "everyone" : i.Unassigned ? "nobody yet" : string.Join(", ", i.Assignees.Select(a => a.Title)),
        ["packed_by"] = new JsonArray(i.Assignees.Where(a => i.IsPackedBy(a.Id)).Select(a => (JsonNode)a.Title).ToArray()),
        ["done"] = i.Done
    };

    private static Entity PackingTask(TripSnapshot trip, string id) => Find(trip, id) is var e && PackingLists.IsItem(e) ? e : throw new McpException("This is not a packing item. Use get_packing_lists to find their ids.");

    /// <summary>A person by id or by name; a name must match exactly one person, ignoring case.</summary>
    private static Entity Person(TripSnapshot trip, string idOrName)
    {
        if (Guid.TryParse(idOrName.Trim(), out var id)) return trip.Find(id) is { Type: "person" } byId ? byId : throw new McpException("No person has the id " + idOrName + ".");
        var matches = trip.Entities.Values.Where(e => e.Type == "person" && string.Equals(e.Title, idOrName.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToArray();
        return matches.Length == 1 ? matches[0] : throw new McpException(matches.Length == 0 ? "Nobody in the trip is called \"" + idOrName + "\". Use list_entities with type person." : "Several people are called \"" + idOrName + "\". Use the person's id.");
    }

    [McpServerTool(Name = "list_reminders", Title = "List reminders", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Things the travellers should look at now: free cancellation deadlines within two weeks, tasks due, arrivals after a check-in window, nights without a place to stay, and bookings not confirmed shortly before they start. Urgent ones come first.")]
    public Task<string> ListReminders(CancellationToken ct = default) => Run(async () =>
    {
        var trip = (await repository.ReadAsync(ct)).Trip; var zone = ScheduleQueries.TripZone(trip);
        var reminders = TripReminders.For(trip, SystemClock.Instance.GetCurrentInstant());
        return new JsonObject
        {
            ["reminders"] = new JsonArray(reminders.Select(r => (JsonNode)new JsonObject
            {
                ["kind"] = r.Kind.ToString(),
                ["urgent"] = r.Urgent,
                ["entity"] = r.Entity?.ToString(),
                ["title"] = r.Title,
                ["when"] = r.Moment is { } moment ? LocalDateTimePattern.ExtendedIso.Format(moment.InZone(zone).LocalDateTime) : null,
                ["dates"] = new JsonArray(r.Dates.Select(d => (JsonNode)Date(d)!).ToArray())
            }).ToArray())
        };
    });

    [McpServerTool(Name = "get_weather", Title = "Read cached weather", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The weather the user's trip app last downloaded for this trip, read from its local cache. This tool never fetches live weather and cannot look up other places or dates; every entry says when it was fetched, so mention its age when it matters. Per day: the weather where most of the day happens. Per item: whether weather matters (outdoor estimate), the outlook at its place (or at departure and arrival), and warnings. Kinds: forecast (within a week), trend (up to about two weeks, can still change a lot), climate (averages of the last years, not a forecast). When weather is off in the app, the answer says so; only the user can turn it on.")]
    public Task<string> GetWeather(
        [Description("Optional first day, YYYY-MM-DD.")] string? from = null,
        [Description("Optional last day, YYYY-MM-DD.")] string? to = null,
        CancellationToken ct = default) => Run(async () =>
    {
        var app = clientName is { Length: > 0 } name ? name : "the trip app";
        if (weather?.Invoke() is not { } source)
            return new JsonObject { ["available"] = false, ["note"] = "Weather is turned off in " + app + ". The user can turn it on there (Settings, Weather). This server never downloads weather itself." };
        var trip = (await repository.ReadAsync(ct)).Trip; var zone = ScheduleQueries.TripZone(trip);
        var now = SystemClock.Instance.GetCurrentInstant();
        var service = WeatherService.CacheOnly(new WeatherCache(source.CacheDirectory), source.Provider);
        var result = service.Cached(trip);
        var first = ParseDate(from, "from"); var last = ParseDate(to, "to");
        bool InRange(LocalDate date) => (first is null || date >= first) && (last is null || date <= last);
        JsonObject Outlook(WeatherOutlook o)
        {
            var json = new JsonObject { ["kind"] = o.Kind.ToString().ToLowerInvariant(), ["place"] = trip.Find(o.Place)?.Title };
            if (o.Fetched is { } fetched) { json["fetched"] = InstantPattern.General.Format(fetched); json["age_hours"] = Math.Round((now - fetched).TotalHours, 1); }
            json["source"] = o.Kind == OutlookKind.Climate ? "climate averages" : service.Providers.FirstOrDefault(p => p.Id == o.Provider)?.Name ?? o.Provider;
            if (o.Climate is { } c) { json["avg_max_c"] = c.MaxC; json["avg_min_c"] = c.MinC; json["rain_day_share"] = c.RainDayShare; json["avg_precipitation_mm"] = c.PrecipitationMm; json["years"] = c.Years; }
            else if (o.Hours.Count > 0)
            {
                // The item's own hours, summarised.
                json["condition"] = o.Hours.Where(h => h.Condition >= WeatherCondition.Drizzle).Select(h => (WeatherCondition?)h.Condition).Max()?.ToString() ?? o.Hours.GroupBy(h => h.Condition).OrderByDescending(g => g.Count()).First().Key.ToString();
                json["min_c"] = o.Hours.Min(h => h.TemperatureC); json["max_c"] = o.Hours.Max(h => h.TemperatureC);
                json["precipitation_chance"] = o.Hours.Max(h => h.PrecipitationChance); json["precipitation_mm"] = Math.Round(o.Hours.Sum(h => h.PrecipitationMm ?? 0), 1); json["gust_kmh"] = o.Hours.Max(h => h.GustKmh);
            }
            else if (o.Day is { } d)
            {
                json["condition"] = d.Condition.ToString(); json["max_c"] = d.MaxC; json["min_c"] = d.MinC; json["precipitation_chance"] = d.PrecipitationChance; json["precipitation_mm"] = d.PrecipitationMm; json["gust_kmh"] = d.GustKmh;
            }
            var (sunrise, sunset) = (o.Day?.Sunrise ?? o.Climate?.Sunrise, o.Day?.Sunset ?? o.Climate?.Sunset);
            if (sunrise is { } rise) json["sunrise"] = LocalDateTimePattern.ExtendedIso.Format(rise.InZone(zone).LocalDateTime);
            if (sunset is { } set) json["sunset"] = LocalDateTimePattern.ExtendedIso.Format(set.InZone(zone).LocalDateTime);
            return json;
        }
        var days = result.Days.Values.Where(d => InRange(d.Date)).OrderBy(d => d.Date).Select(d => (JsonNode)new JsonObject { ["date"] = Date(d.Date) }.Also(j => { foreach (var p in Outlook(d.Outlook)) j[p.Key] = p.Value?.DeepClone(); })).ToArray();
        var items = result.Items.Values.Select(i => (Weather: i, Entity: trip.Find(i.Item))).Where(x => x.Entity is not null)
            .Select(x => (x.Weather, x.Entity, Start: ScheduleQueries.Span(trip, x.Entity!).Start)).Where(x => x.Start is { } s && InRange(s.InZone(zone).Date)).OrderBy(x => x.Start)
            .Select(x => (JsonNode)new JsonObject
            {
                ["id"] = x.Entity!.Id.ToString(),
                ["title"] = x.Entity.Title,
                ["when"] = When(trip, x.Entity),
                ["outdoor"] = x.Weather.Exposure.Exposure.ToString().ToLowerInvariant() + (x.Weather.Exposure.Source is ExposureSource.Explicit or ExposureSource.Inherited ? "" : " (estimated)"),
                ["outlooks"] = new JsonArray(x.Weather.Outlooks.Select(o => (JsonNode)Outlook(o)).ToArray()),
                ["warnings"] = new JsonArray(x.Weather.Concerns.Select(c => (JsonNode)new JsonObject { ["kind"] = c.Kind.ToString().ToLowerInvariant(), ["severe"] = c.Severe, ["value"] = c.Value }).ToArray())
            }).ToArray();
        return new JsonObject
        {
            ["available"] = true,
            ["note"] = "Cached weather from " + app + "'s last update, not live data. This server never contacts weather services; " + app + " refreshes the cache while the trip is open there with weather turned on. Forecasts older than 24 hours are left out. Temperatures in °C, precipitation in mm, wind in km/h, times in the trip's timezone.",
            ["timezone"] = zone.Id,
            ["days"] = new JsonArray(days),
            ["items"] = new JsonArray(items),
            ["attribution"] = new JsonArray(result.Sources.Select(a => (JsonNode)(a.Text + (a.License is null ? "" : " (" + a.License + ")") + ", " + a.Url)).ToArray())
        };
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
            // Changing check-in or check-out moves the stay; its general time follows so other tools see the same nights.
            if (patch["components"]?["accommodation"] is JsonObject changed && (changed.ContainsKey("check_in") || changed.ContainsKey("check_out")) && !patch.ContainsKey("time"))
                entity = Stays.Align(state.Trip, entity);
            return ([new(entity.Id, entity)], null);
        }, ct);
        return new JsonObject { ["id"] = target.ToString(), ["entity"] = saved.Trip.Find(target)!.Data.DeepClone() };
    });

    [McpServerTool(Name = "delete_entity", Title = "Delete an entity", Destructive = true, OpenWorld = false)]
    [Description("Deletes an entity and its notes. References to it are removed from other entities (for example from a booking's items, a parent's children or an activity's place); comments on it are deleted with it. Places, documents, notes and bookings that only this entity used are kept and listed as `unused`, unless `also_delete_unused` is true.")]
    public Task<string> DeleteEntity([Description("The entity id.")] string id, [Description("Also delete places, documents, notes and bookings that nothing else uses afterwards.")] bool also_delete_unused = false, CancellationToken ct = default) => Run(async () =>
    {
        DeletionPlan? done = null;
        await Write(state =>
        {
            var trip = state.Trip; var entity = Find(trip, id);
            if (entity.Type == "trip") throw new McpException("The trip itself cannot be deleted.");
            var plan = TripDeletion.Plan(trip, [entity.Id]);
            if (also_delete_unused && plan.Unused.Count > 0) plan = TripDeletion.Plan(trip, plan.Requested.Concat(plan.Unused.Select(e => e.Id)));
            done = plan;
            return (plan.Edits, plan.ResourceEdits);
        }, ct);
        JsonArray List(IEnumerable<Entity> entities) => new(entities.Select(e => (JsonNode)new JsonObject { ["id"] = e.Id.ToString(), ["type"] = e.Type, ["title"] = e.Title }).ToArray());
        return new JsonObject
        {
            ["deleted"] = List(done!.Deleted),
            ["references_removed_from"] = List(done.Updated),
            ["unused"] = List(also_delete_unused ? [] : done.Unused)
        };
    });

    [McpServerTool(Name = "read_document", Title = "Read an attached file", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Returns the text of an attached file (document entity): the text of a PDF, or the subject, sender, date, text and attachment names of an e-mail (.eml). Scanned PDFs without a text layer have no text. Use it to read booking confirmations.")]
    public Task<string> ReadDocument([Description("The document entity id.")] string id, [Description("At most this many characters of text.")] int max_characters = 20000, CancellationToken ct = default) => Run(async () =>
    {
        var state = await repository.ReadAsync(ct); var document = Find(state.Trip, id);
        if (document.Type != "document") throw new McpException("This entity is not a file. Files are entities of type document.");
        var media = document.Data["media_type"]?.ToString() ?? "application/octet-stream"; var hash = document.Data["blob"]?["hash"]?.ToString() ?? "";
        var bytes = hash.Length == 64 ? state.Trip.Resources.GetValueOrDefault("assets/sha256/" + hash[..2] + "/" + hash) : null;
        if (bytes is null) throw new McpException("The file's content is missing from the trip.");
        var limit = Math.Clamp(max_characters, 100, 200_000); var text = TravelRepo.Documents.DocumentText.Extract(bytes, media, limit + 1);
        var result = new JsonObject { ["id"] = document.Id.ToString(), ["name"] = document.Title, ["media_type"] = media, ["size"] = bytes.Length, ["text"] = text is null ? null : text.Length > limit ? text[..limit] : text, ["truncated"] = text?.Length > limit };
        if (TravelRepo.Documents.Mail.IsMail(media))
        {
            var mail = TravelRepo.Documents.Mail.Read(bytes);
            result["mail"] = new JsonObject { ["subject"] = mail.Subject, ["from"] = string.Join(", ", mail.From), ["date"] = mail.Date?.ToString("O", CultureInfo.InvariantCulture), ["attachments"] = new JsonArray(mail.Attachments.Select(a => (JsonNode)new JsonObject { ["name"] = a.FileName, ["media_type"] = a.MediaType, ["size"] = a.Content.Length }).ToArray()) };
        }
        if (text is null) result["note"] = "This kind of file has no readable text here.";
        return result;
    });

    [McpServerTool(Name = "import_document", Title = "Attach a file", Destructive = false, OpenWorld = false)]
    [Description("Copies a local file into the trip as a document, for example a booking confirmation saved as .eml or .pdf, and optionally attaches it to an entity such as a booking or a stay. With split_attachments an e-mail's attached files (tickets, invoices) are added as documents too. Files over 25 MB are refused.")]
    public Task<string> ImportDocument(
        [Description("Absolute path of the file on this computer.")] string path,
        [Description("Optional id of the entity to attach the file to.")] string? attach_to = null,
        [Description("For an e-mail: also add its attached files as separate documents.")] bool split_attachments = false,
        CancellationToken ct = default) => Run(async () =>
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) throw new McpException("No file at " + path + ". Pass an absolute path to an existing file.");
        if (new FileInfo(path).Length > 25 * 1024 * 1024) throw new McpException("The file is larger than 25 MB. Ask the travellers to add it themselves if it should be part of the trip.");
        var bytes = await File.ReadAllBytesAsync(path, ct); var media = TravelRepo.Documents.MediaTypes.FromFileName(path);
        var files = new List<(string Name, string Media, byte[] Bytes)> { (Path.GetFileName(path), media, bytes) };
        if (split_attachments && TravelRepo.Documents.Mail.IsMail(media))
            files.AddRange(TravelRepo.Documents.Mail.Read(bytes).Attachments.Where(a => a.Content.Length > 0 && a.Content.Length <= 25 * 1024 * 1024).Select(a => (a.FileName, a.MediaType, a.Content)));
        var created = new List<Entity>();
        await Write(state =>
        {
            created.Clear(); var edits = new List<EntityEdit>(); var resources = new List<ResourceEdit>();
            var owner = attach_to is null ? null : Find(state.Trip, attach_to);
            foreach (var (name, type, content) in files)
            {
                var (document, resource) = TravelRepository.PrepareDocument(content, name, type);
                created.Add(document); edits.Add(new(document.Id, document)); if (!resources.Any(r => r.Path == resource.Path)) resources.Add(resource);
                if (owner is not null) owner = TripCommands.AttachDocument(owner, document);
            }
            if (owner is not null) edits.Add(new(owner.Id, owner));
            return (edits, resources);
        }, ct);
        return new JsonObject { ["documents"] = new JsonArray(created.Select(d => (JsonNode)new JsonObject { ["id"] = d.Id.ToString(), ["name"] = d.Title, ["media_type"] = d.Data["media_type"]?.DeepClone() }).ToArray()), ["attached_to"] = attach_to };
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
        // How much weather matters: outdoor, possibly, travel or indoor, stated by the outdoor field or estimated.
        var exposure = OutdoorEstimates.For(trip, e);
        result["outdoor"] = exposure.Exposure.ToString().ToLowerInvariant() + (exposure.Source is ExposureSource.Explicit or ExposureSource.Inherited ? "" : " (estimated)");
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
