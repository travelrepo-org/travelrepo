using System.Globalization;
using System.Text.Json.Nodes;
using NodaTime;
using NodaTime.Text;

namespace TravelRepo.Core;

/// <summary>The broad kind of a schedule item, derived from its components and category.</summary>
public enum ScheduleKind { Activity, Transport, Accommodation, Food, Sightseeing, Culture, Nature, Shopping, Nightlife, Meeting, FreeTime, Other }

/// <summary>Time precision values defined by the format (spec section 16).</summary>
public enum TimePrecision { Unscheduled, Exact, Approximate, Window, DayPart, AllDay }

/// <summary>
/// A resolved time span for display, planning and export. <see cref="Derived"/> is true when the span
/// was aggregated from nested children instead of the item's own time.
/// </summary>
public sealed record ScheduleSpan(TimePrecision Precision, Instant? Start, Instant? End, LocalDate? Date, string? DayPart, string? StartZone, string? EndZone, bool Derived)
{
    public bool IsPlaced => Start is not null && End is not null;
    public Duration? Length => Start is { } a && End is { } b ? b - a : null;
}

/// <summary>Standard category vocabulary and schedule classification shared by TravelRepo clients.</summary>
public static class ScheduleCategories
{
    /// <summary>Recommended values for the inheritable <c>category</c> field. Namespaced custom values remain valid.</summary>
    public static readonly IReadOnlyList<string> Standard = ["activity", "sightseeing", "food", "culture", "nature", "shopping", "nightlife", "meeting", "free_time", "transport", "accommodation", "other"];

    /// <summary>Recommended transport component types (spec section 23).</summary>
    public static readonly IReadOnlyList<string> TransportTypes = ["flight", "train", "bus", "car", "taxi", "rideshare", "ferry", "ship", "bicycle", "walking", "other"];

    /// <summary>Core schedule status values (spec section 26).</summary>
    public static readonly IReadOnlyList<string> Statuses = ["idea", "planned", "reserved", "confirmed", "completed", "cancelled"];

    public static ScheduleKind Classify(TripSnapshot trip, Entity item)
    {
        if (item.Data["components"]?["transport"] is JsonObject) return ScheduleKind.Transport;
        if (item.Data["components"]?["accommodation"] is JsonObject) return ScheduleKind.Accommodation;
        string? category = null;
        try { category = ScalarText(TripQueries.Resolve(trip, item.Id, "category").Value); }
        catch (DomainException) { category = ScalarText(item.Data["category"]); }
        return FromCategory(category);
    }

    public static ScheduleKind FromCategory(string? category) => category?.Trim().ToLowerInvariant() switch
    {
        null or "" or "activity" => ScheduleKind.Activity,
        "transport" => ScheduleKind.Transport,
        "accommodation" or "hotel" or "lodging" => ScheduleKind.Accommodation,
        "food" or "restaurant" or "meal" or "breakfast" or "lunch" or "dinner" or "cafe" or "drinks" => ScheduleKind.Food,
        "sightseeing" or "sight" or "sights" or "landmark" => ScheduleKind.Sightseeing,
        "culture" or "museum" or "art" or "theatre" or "theater" or "music" => ScheduleKind.Culture,
        "nature" or "hiking" or "beach" or "park" or "outdoor" => ScheduleKind.Nature,
        "shopping" or "market" => ScheduleKind.Shopping,
        "nightlife" or "bar" or "party" => ScheduleKind.Nightlife,
        "meeting" or "business" or "work" => ScheduleKind.Meeting,
        "free_time" or "rest" or "break" => ScheduleKind.FreeTime,
        _ => ScheduleKind.Other
    };

    /// <summary>The transport type of a transport item, or null for other items.</summary>
    public static string? TransportType(Entity item) => item.Data["components"]?["transport"]?["type"]?.ToString();

    private static string? ScalarText(JsonNode? value) => value switch
    {
        JsonValue v => v.ToString(),
        JsonArray { Count: > 0 } a => a[0]?.ToString(),
        _ => null
    };
}

/// <summary>Read-only schedule queries. They never modify repository data.</summary>
public static class ScheduleQueries
{
    /// <summary>Recommended display ranges for day parts. They are presentation defaults, not stored data.</summary>
    public static (LocalTime Start, LocalTime End) DayPartRange(string? dayPart) => dayPart switch
    {
        "morning" => (new LocalTime(8, 0), new LocalTime(12, 0)),
        "afternoon" => (new LocalTime(12, 0), new LocalTime(17, 0)),
        "evening" => (new LocalTime(17, 0), new LocalTime(21, 0)),
        "night" => (new LocalTime(21, 0), new LocalTime(23, 59)),
        _ => (new LocalTime(9, 0), new LocalTime(17, 0))
    };

    public static DateTimeZone TripZone(TripSnapshot trip) => DateTimeZoneProviders.Tzdb.GetZoneOrNull(trip.Manifest.Data["default_timezone"]?.ToString() ?? "") ?? DateTimeZone.Utc;

    public static TimePrecision PrecisionOf(Entity item) => item.Data["time"] is not JsonObject time ? TimePrecision.Unscheduled : time["precision"]?.ToString() switch
    {
        "exact" => TimePrecision.Exact,
        "approximate" => TimePrecision.Approximate,
        "window" => TimePrecision.Window,
        "day_part" => TimePrecision.DayPart,
        "all_day" => TimePrecision.AllDay,
        "unscheduled" => TimePrecision.Unscheduled,
        _ => time["start"] is not null ? TimePrecision.Exact : TimePrecision.Unscheduled
    };

    /// <summary>The item's planned duration field, if it has a fixed ISO 8601 duration.</summary>
    public static Duration? PlannedDuration(Entity item)
    {
        if (item.Data["duration"]?.ToString() is not { Length: > 0 } text) return null;
        var parsed = PeriodPattern.NormalizingIso.Parse(text);
        if (!parsed.Success) return null;
        try { return parsed.Value.ToDuration(); } catch (InvalidOperationException) { return null; }
    }

    /// <summary>Resolve the span of a schedule item, falling back to nested children for unscheduled parents.</summary>
    public static ScheduleSpan Span(TripSnapshot trip, Entity item)
    {
        var precision = PrecisionOf(item); var time = item.Data["time"] as JsonObject;
        ZonedTime? Read(string key) { try { return time?[key] is JsonObject t ? ZonedTime.From(t) : null; } catch (Exception ex) when (ex is DomainException or InvalidOperationException) { return null; } }
        Instant? At(ZonedTime? value) { try { return value?.ToInstant(); } catch (DomainException) { return null; } }
        LocalDate? Date() => time?["date"] is { } d && LocalDatePattern.Iso.Parse(d.ToString()).TryGetValue(default, out var day) ? day : null;
        switch (precision)
        {
            case TimePrecision.Exact or TimePrecision.Approximate:
                {
                    var start = Read("start"); var end = Read("end"); var a = At(start);
                    var b = At(end) ?? (a is { } s ? s + (PlannedDuration(item) ?? Duration.FromHours(1)) : null);
                    return new(precision, a, b, null, null, start?.Timezone, end?.Timezone ?? start?.Timezone, false);
                }
            case TimePrecision.Window:
                {
                    var start = Read("earliest"); var end = Read("latest");
                    return new(precision, At(start), At(end), null, null, start?.Timezone, end?.Timezone ?? start?.Timezone, false);
                }
            case TimePrecision.DayPart when Date() is { } day:
                {
                    var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(time?["timezone"]?.ToString() ?? "") ?? TripZone(trip);
                    var (from, to) = DayPartRange(time?["day_part"]?.ToString());
                    return new(precision, zone.AtLeniently(day.At(from)).ToInstant(), zone.AtLeniently(day.At(to)).ToInstant(), day, time?["day_part"]?.ToString(), zone.Id, zone.Id, false);
                }
            case TimePrecision.AllDay when Date() is { } day:
                {
                    var zone = TripZone(trip);
                    return new(precision, zone.AtStartOfDay(day).ToInstant(), zone.AtStartOfDay(day.PlusDays(1)).ToInstant(), day, null, zone.Id, zone.Id, false);
                }
        }
        if (item.Data["children"] is JsonArray { Count: > 0 })
        {
            var range = TripQueries.Range(trip, item);
            if (range.Start is not null) return new(precision, range.Start, range.End ?? range.Start, null, null, null, null, true);
        }
        return new(TimePrecision.Unscheduled, null, null, Date(), null, null, null, false);
    }

    /// <summary>Schedule items that have no own time and no scheduled children: the client's Inbox candidates.</summary>
    public static bool IsUnscheduled(TripSnapshot trip, Entity item) => item.Type == "schedule_item" && !Span(trip, item).IsPlaced;

    /// <summary>People resolved from an item's own or inherited participants, in manifest order where possible.</summary>
    public static IReadOnlyList<Entity> Participants(TripSnapshot trip, Entity item)
    {
        JsonNode? value;
        try { value = TripQueries.Resolve(trip, item.Id, "participants").Value; } catch (DomainException) { value = null; }
        if (value is not JsonArray ids) return [];
        return ids.Select(n => Guid.TryParse(n?.ToString(), out var id) ? trip.Find(id) : null).OfType<Entity>().Where(e => e.Type == "person").ToArray();
    }

    /// <summary>The most relevant place for an item: accommodation place, transport arrival, default place.</summary>
    public static Entity? PrimaryPlace(TripSnapshot trip, Entity item)
    {
        foreach (var node in new[] { item.Data["components"]?["accommodation"]?["place"], item.Data["components"]?["transport"]?["arrival"]?["place"] })
            if (Guid.TryParse(node?.ToString(), out var id) && trip.Find(id) is { Type: "place" } place) return place;
        try { if (Guid.TryParse(TripQueries.Resolve(trip, item.Id, "default_place").Value?.ToString(), out var id) && trip.Find(id) is { Type: "place" } place) return place; }
        catch (DomainException) { }
        return null;
    }

    /// <summary>Departure and arrival places of a transport item.</summary>
    public static (Entity? From, Entity? To) Route(TripSnapshot trip, Entity item)
    {
        Entity? Find(JsonNode? node) => Guid.TryParse(node?.ToString(), out var id) && trip.Find(id) is { Type: "place" } place ? place : null;
        var transport = item.Data["components"]?["transport"];
        return (Find(transport?["departure"]?["place"]), Find(transport?["arrival"]?["place"]));
    }
}

/// <summary>A per-currency cost overview. Values stay decimal and are never converted silently.</summary>
public sealed record CurrencyTotal(string Currency, decimal Paid, decimal Estimated, decimal Budget)
{
    public decimal Total => Paid + Estimated;
}

/// <summary>Library and overview summaries derived from canonical data.</summary>
public static class TripSummaries
{
    /// <summary>Distinct place names in schedule order, for library cards and overviews.</summary>
    public static IReadOnlyList<string> PlaceNames(TripSnapshot trip, int max = 3)
    {
        // Stays and activity places describe a trip better than airports and stations, which come last.
        var items = trip.Entities.Values.Where(e => e.Type == "schedule_item").Select(e => (Item: e, Span: ScheduleQueries.Span(trip, e))).OrderBy(x => x.Span.Start ?? Instant.MaxValue).ToArray();
        var ordered = items.Where(x => x.Item.Data["components"]?["transport"] is null).Select(x => ScheduleQueries.PrimaryPlace(trip, x.Item)).OfType<Entity>()
            .Concat(items.Select(x => ScheduleQueries.PrimaryPlace(trip, x.Item)).OfType<Entity>())
            .Concat(trip.Entities.Values.Where(e => e.Type == "place").OrderBy(e => e.Title, StringComparer.CurrentCulture));
        return ordered.Select(p => p.Title).Where(t => t.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase).Take(max).ToArray();
    }

    public static IReadOnlyList<CurrencyTotal> Costs(TripSnapshot trip)
    {
        var totals = new Dictionary<string, (decimal Paid, decimal Estimated, decimal Budget)>(StringComparer.Ordinal);
        foreach (var e in trip.Entities.Values.Where(e => e.Type is "expense" or "budget"))
        {
            if (Money(e.Data["amount"]) is not { } money) continue;
            var current = totals.GetValueOrDefault(money.Currency);
            if (e.Type == "budget") current.Budget += money.Amount;
            else if (e.Data["estimated"] is JsonValue flag && flag.TryGetValue<bool>(out var estimated) && estimated) current.Estimated += money.Amount;
            else current.Paid += money.Amount;
            totals[money.Currency] = current;
        }
        return totals.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new CurrencyTotal(p.Key, p.Value.Paid, p.Value.Estimated, p.Value.Budget)).ToArray();
    }

    public static Money? Money(JsonNode? node)
    {
        if (node?["value"]?.ToString() is not { } value || node["currency"]?.ToString() is not { Length: 3 } currency) return null;
        return decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _) ? new Money(value, currency) : null;
    }

    /// <summary>Map a Git author email to the trip-local person that declares it.</summary>
    public static Entity? PersonForGitEmail(TripSnapshot trip, string email) => trip.Entities.Values.FirstOrDefault(p => p.Type == "person" && p.Data["identities"]?["git"] is JsonArray identities && identities.Any(i => string.Equals(i?["email"]?.ToString(), email, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Entities that reference the given entity ID anywhere in their canonical data.</summary>
    public static IReadOnlyList<Entity> ReferencesTo(TripSnapshot trip, Guid id)
    {
        var text = id.ToString();
        bool Contains(JsonNode? node) => node switch
        {
            JsonObject o => o.Any(p => p.Key != "id" && Contains(p.Value)),
            JsonArray a => a.Any(Contains),
            JsonValue v => v.TryGetValue<string>(out var s) && s == text,
            _ => false
        };
        return trip.All.Where(e => e.Id != id && Contains(e.Data)).ToArray();
    }
}
