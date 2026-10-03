using System.Globalization;
using System.Text.Json.Nodes;
using NodaTime;
using NodaTime.Text;

namespace TravelRepo.Core;

/// <summary>A lossless entity envelope. Modify a copy and submit it through a repository command.</summary>
public sealed record Entity(JsonObject Data)
{
    public Guid Id => Guid.Parse(Data["id"]!.GetValue<string>());
    public string Type => Data["type"]?.GetValue<string>() ?? "trip";
    public string Title => Data["title"]?.ToString() ?? Data["name"]?.ToString() ?? Data["display_name"]?.ToString() ?? Type;
    public Entity Copy() => new((JsonObject)Data.DeepClone());
    public static Entity Create(string type, string title)
    {
        var data = new JsonObject { ["id"] = Guid.CreateVersion7().ToString(), ["type"] = type, ["extensions"] = new JsonObject() };
        data[type == "person" ? "display_name" : type is "place" or "document" ? "name" : "title"] = title;
        if (type == "schedule_item") { data["status"] = "idea"; data["time"] = null; data["components"] = new JsonObject(); data["children"] = new JsonArray(); }
        if (type == "task") data["status"] = "open";
        return new Entity(data);
    }
    public static Entity CreateTrip(string title, string language = "en", string? timezone = null) => new(new JsonObject
    {
        ["schema"] = new JsonObject { ["name"] = "TravelRepo", ["version"] = "1.0" },
        ["id"] = Guid.CreateVersion7().ToString(),
        ["title"] = title,
        ["language"] = language,
        ["default_timezone"] = timezone,
        ["participants"] = new JsonArray(),
        ["extensions"] = new JsonObject(),
        ["variant"] = new JsonObject { ["id"] = Guid.CreateVersion7().ToString(), ["title"] = "Current Plan", ["role"] = "primary", ["state"] = "active", ["parent"] = null, ["created_at"] = SystemClock.Instance.GetCurrentInstant().ToString() }
    });
}

public enum Severity { Info, Warning, Error }
public sealed record Diagnostic(string Code, Severity Severity, string Path, string Message);
public sealed record TripSnapshot(Entity Manifest, IReadOnlyDictionary<Guid, Entity> Entities)
{
    public IReadOnlyDictionary<string, byte[]> Resources { get; init; } = new Dictionary<string, byte[]>();
    public IEnumerable<Entity> All => new[] { Manifest }.Concat(Entities.Values);
    public Entity? Find(Guid id) => id == Manifest.Id ? Manifest : Entities.GetValueOrDefault(id);
}
public sealed class DomainException(string code, string message) : Exception(message) { public string Code { get; } = code; }

/// <summary>Exact wall time with explicit offset required at an ambiguous DST transition.</summary>
public sealed record ZonedTime(string Local, string Timezone, string? Offset = null)
{
    public Instant ToInstant()
    {
        var local = LocalDateTimePattern.ExtendedIso.Parse(Local);
        if (!local.Success) throw new DomainException("time.local", "Invalid local date and time.");
        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(Timezone) ?? throw new DomainException("time.zone", "Unknown IANA timezone.");
        var map = zone.MapLocal(local.Value);
        if (map.Count == 0) throw new DomainException("time.gap", "This local time does not exist because clocks move forward.");
        if (Offset is null)
        {
            if (map.Count == 2) throw new DomainException("time.ambiguous", "Choose an offset for this repeated local time.");
            return map.Single().ToInstant();
        }
        var offset = OffsetPattern.GeneralInvariant.Parse(Offset);
        if (!offset.Success) throw new DomainException("time.offset", "Invalid UTC offset.");
        var instant = local.Value.WithOffset(offset.Value).ToInstant();
        if (zone.GetUtcOffset(instant) != offset.Value) throw new DomainException("time.offset", "The offset does not match this timezone and local time.");
        return instant;
    }
    public static ZonedTime From(JsonNode n) => new(n["local"]!.ToString(), n["timezone"]!.ToString(), n["offset"]?.ToString());
    public JsonObject ToJson() => new() { ["local"] = Local, ["timezone"] = Timezone, ["offset"] = Offset };
    public static ZonedTime At(Instant instant, string zone)
    {
        var z = instant.InZone(DateTimeZoneProviders.Tzdb[zone]);
        return new(LocalDateTimePattern.ExtendedIso.Format(z.LocalDateTime), zone, OffsetPattern.GeneralInvariant.Format(z.Offset));
    }
}
public sealed record Money(string Value, string Currency)
{
    public decimal Amount => decimal.Parse(Value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
}
public sealed record InheritedValue(JsonNode? Value, Guid Source, bool Inherited);

public static class TripQueries
{
    private static readonly HashSet<string> Inheritable = ["participants", "tags", "default_place", "display", "category"];
    public static Entity? Parent(TripSnapshot trip, Guid child) => trip.Entities.Values.FirstOrDefault(e => e.Data["children"] is JsonArray a && a.Any(x => x?.ToString() == child.ToString()));
    public static InheritedValue Resolve(TripSnapshot trip, Guid id, string field)
    {
        if (!Inheritable.Contains(field)) throw new DomainException("inherit.field", "This field cannot be inherited.");
        var original = id; var visited = new HashSet<Guid>();
        while (visited.Add(id))
        {
            var e = trip.Find(id) ?? throw new DomainException("reference.missing", "Entity not found.");
            var value = e.Data[field];
            if (value is JsonObject o && o["inherit"]?.ToString() == "true")
            {
                var p = Parent(trip, id);
                if (p is not null) { id = p.Id; continue; }
                value = o["values"];
            }
            else if (value is JsonObject own && own.ContainsKey("values")) value = own["values"];
            if (value is not null) return new(value.DeepClone(), id, id != original);
            var parent = Parent(trip, id);
            if (parent is null) return new(null, id, id != original);
            id = parent.Id;
        }
        throw new DomainException("nest.cycle", "Schedule nesting contains a cycle.");
    }
    public static (Instant? Start, Instant? End) Range(TripSnapshot trip, Entity entity)
    {
        var all = new List<Instant>(); var visited = new HashSet<Guid>();
        void Visit(Entity e)
        {
            if (!visited.Add(e.Id)) return;
            foreach (var k in new[] { "start", "end", "earliest", "latest" })
                if (e.Data["time"]?[k] is JsonObject t) { try { all.Add(ZonedTime.From(t).ToInstant()); } catch (DomainException) { } }
            if (e.Data["children"] is JsonArray children)
                foreach (var c in children) if (Guid.TryParse(c?.ToString(), out var id) && trip.Find(id) is { } child) Visit(child);
        }
        Visit(entity); return all.Count == 0 ? (null, null) : (all.Min(), all.Max());
    }
    public static (LocalDate? Start, LocalDate? End) EffectiveDates(TripSnapshot trip)
    {
        var dates = new List<LocalDate>();
        foreach (var entity in trip.Entities.Values.Where(e => e.Type == "schedule_item"))
        {
            var time = entity.Data["time"]; if (time is null) continue;
            if (time["date"] is { } date && LocalDatePattern.Iso.Parse(date.ToString()).TryGetValue(default, out var day)) dates.Add(day);
            foreach (var key in new[] { "start", "end", "earliest", "latest" }) if (time[key]?["local"] is { } local && LocalDateTimePattern.ExtendedIso.Parse(local.ToString()).TryGetValue(default, out var value)) dates.Add(value.Date);
        }
        LocalDate? Explicit(string key) => trip.Manifest.Data["dates"]?[key] is { } value && LocalDatePattern.Iso.Parse(value.ToString()).TryGetValue(default, out var day) ? day : null;
        return (Explicit("start") ?? (dates.Count > 0 ? dates.Min() : null), Explicit("end") ?? (dates.Count > 0 ? dates.Max() : null));
    }
    public static IEnumerable<Entity> Search(TripSnapshot trip, string query) => trip.All.Where(e => e.Data.ToJsonString().Contains(query, StringComparison.OrdinalIgnoreCase));
}
