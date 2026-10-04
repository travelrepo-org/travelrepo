using System.Text.Json.Nodes;
using NodaTime.Text;

namespace TravelRepo.Core;

/// <summary>Cross-entity and temporal validation, independent of serialization and schema validation.</summary>
public sealed record TravelGapEstimate(Guid FromPlace, Guid ToPlace, NodaTime.Duration Duration);

public static class SemanticValidation
{
    public static IReadOnlyList<Diagnostic> Validate(TripSnapshot trip, IEnumerable<TravelGapEstimate>? travelEstimates = null)
    {
        var result = new List<Diagnostic>();
        void Error(string code, string path, string message) => result.Add(new(code, Severity.Error, path, message));
        var ids = trip.All.Select(e => e.Id.ToString()).ToHashSet();
        var scalarRefs = new HashSet<string> { "place", "default_place", "document", "entity", "author", "ref", "item", "booking" };
        var listRefs = new HashSet<string> { "children", "travelers", "items", "documents", "assigned_to", "paid_by", "mentions", "guests" };
        var targetTypes = new Dictionary<string, string> { ["place"] = "place", ["default_place"] = "place", ["document"] = "document", ["author"] = "person", ["item"] = "schedule_item", ["booking"] = "booking", ["ref"] = "booking", ["children"] = "schedule_item", ["travelers"] = "person", ["documents"] = "document", ["assigned_to"] = "person", ["paid_by"] = "person", ["mentions"] = "person", ["guests"] = "person", ["participants"] = "person", ["values"] = "person" };
        void TargetType(JsonNode? value, string key, string path)
        {
            if (Guid.TryParse(value?.ToString(), out var id) && targetTypes.TryGetValue(key, out var type) && trip.Find(id) is { } target && target.Type != type) Error("reference.type", path, "The linked item has the wrong entity type.");
        }
        void References(JsonNode? n, string path, string key = "")
        {
            if (n is JsonObject obj)
            {
                foreach (var (k, v) in obj)
                {
                    if (k is "extensions" or "variant" or "external_ids" or "identities" || k.Contains('.')) continue;
                    if (key == "" && k is not ("cover" or "avatar" or "participants" or "children" or "components" or "content" or "default_place" or "travelers" or "items" or "documents" or "assigned_to" or "paid_by" or "mentions" or "target" or "related" or "author" or "constraints")) continue;
                    if (key == "components" && k is not ("transport" or "accommodation" or "booking")) continue;
                    if (k == "id" && key is "target" or "related" && v is not null && !ids.Contains(v.ToString())) Error("reference.missing", path + "/id", "The referenced entity does not exist.");
                    if (v is JsonValue) TargetType(v, k, path + "/" + k);
                    References(v, path + "/" + k, k);
                }
            }
            else if (n is JsonArray a)
            {
                foreach (var v in a)
                {
                    if ((listRefs.Contains(key) || key is "participants" or "values") && v is JsonValue && Guid.TryParse(v.ToString(), out _) && !ids.Contains(v.ToString())) Error("reference.missing", path, "The referenced entity does not exist.");
                    if (v is JsonValue) TargetType(v, key, path);
                    References(v, path, key);
                }
            }
            else if (n is not null && scalarRefs.Contains(key) && Guid.TryParse(n.ToString(), out _) && !ids.Contains(n.ToString())) Error("reference.missing", path, "The referenced entity does not exist.");
        }
        if (trip.Manifest.Data["default_timezone"] is JsonValue defaultZone && NodaTime.DateTimeZoneProviders.Tzdb.GetZoneOrNull(defaultZone.ToString()) is null) Error("time.zone", "default_timezone", "Unknown IANA timezone.");
        foreach (var key in new[] { "start", "end" }) if (trip.Manifest.Data["dates"]?[key] is JsonValue date && !LocalDatePattern.Iso.Parse(date.ToString()).Success) Error("date.invalid", "dates/" + key, "Invalid calendar date.");
        if (trip.Manifest.Data["dates"]?["start"] is { } ds && trip.Manifest.Data["dates"]?["end"] is { } de && LocalDatePattern.Iso.Parse(ds.ToString()).TryGetValue(default, out var firstDate) && LocalDatePattern.Iso.Parse(de.ToString()).TryGetValue(default, out var lastDate) && lastDate < firstDate) Error("date.order", "dates", "Trip end precedes its start.");
        var parents = new Dictionary<Guid, Guid>();
        foreach (var e in trip.All)
        {
            References(e.Data, e.Id.ToString());
            if (e.Type == "document" && e.Data["blob"]?["hash"] is { } hash)
            {
                var h = hash.ToString(); if (h.Length != 64 || h.Any(c => !Uri.IsHexDigit(c))) { Error("asset.hash", e.Id.ToString(), "Invalid asset hash."); continue; }
                var path = "assets/sha256/" + h[..2] + "/" + h;
                if (!trip.Resources.TryGetValue(path, out var bytes)) Error("asset.missing", e.Id.ToString(), "The document blob is missing.");
                else if (Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)) != h) Error("asset.corrupt", e.Id.ToString(), "The document blob does not match its hash.");
            }
            if (e.Data["content"] is JsonArray content)
                foreach (var entry in content.OfType<JsonObject>()) if (entry["file"] is { } file && !trip.Resources.ContainsKey(file.ToString())) Error("content.missing", e.Id.ToString(), "Referenced content file is missing.");
            if (e.Data["timezone"] is JsonValue zone && NodaTime.DateTimeZoneProviders.Tzdb.GetZoneOrNull(zone.ToString()) is null) Error("time.zone", e.Id.ToString(), "Unknown IANA timezone.");
            if (e.Data["due"]?["date"] is { } due && !LocalDatePattern.Iso.Parse(due.ToString()).Success) Error("date.invalid", e.Id + "/due", "Invalid due date.");
            if (e.Data["time"] is JsonObject t)
            {
                if (t["date"] is { } date && !LocalDatePattern.Iso.Parse(date.ToString()).Success) Error("date.invalid", e.Id + "/time/date", "Invalid calendar date.");
                if (t["timezone"] is { } tz && NodaTime.DateTimeZoneProviders.Tzdb.GetZoneOrNull(tz.ToString()) is null) Error("time.zone", e.Id + "/time/timezone", "Unknown IANA timezone.");
                foreach (var key in new[] { "start", "end", "earliest", "latest" })
                    if (t[key] is JsonObject value)
                    {
                        try { ZonedTime.From(value).ToInstant(); } catch (DomainException ex) { if (t["precision"]?.ToString() == "exact" || ex.Code != "time.gap") Error(ex.Code, e.Id + "/time/" + key, ex.Message); }
                    }
                try
                {
                    var start = t["start"] ?? t["earliest"]; var end = t["end"] ?? t["latest"];
                    if (start is not null && end is not null && ZonedTime.From(end).ToInstant() < ZonedTime.From(start).ToInstant()) Error("time.order", e.Id.ToString(), "End precedes start.");
                }
                catch (DomainException) { }
            }
            if (e.Data["components"]?["accommodation"] is JsonObject accommodation)
            {
                foreach (var key in new[] { "check_in", "check_out" }) if (accommodation[key] is { } value) { try { ZonedTime.From(value).ToInstant(); } catch (DomainException ex) { Error(ex.Code, e.Id + "/components/accommodation/" + key, ex.Message); } }
                try { if (accommodation["check_in"] is { } checkIn && accommodation["check_out"] is { } checkOut && ZonedTime.From(checkOut).ToInstant() < ZonedTime.From(checkIn).ToInstant()) Error("time.order", e.Id.ToString(), "Check-out precedes check-in."); } catch (DomainException) { }
            }
            if (e.Data["children"] is JsonArray children)
                foreach (var c in children)
                {
                    if (!Guid.TryParse(c?.ToString(), out var id)) continue;
                    if (!parents.TryAdd(id, e.Id)) Error("nest.parents", e.Id.ToString(), "An item cannot have multiple parents.");
                    if (trip.Find(id) is { Type: not "schedule_item" }) Error("nest.type", e.Id.ToString(), "Only schedule items can be nested.");
                }
        }
        foreach (var id in parents.Keys)
        {
            var seen = new HashSet<Guid> { id }; var current = id;
            while (parents.TryGetValue(current, out current)) if (!seen.Add(current)) { Error("nest.cycle", id.ToString(), "Schedule nesting contains a cycle."); break; }
        }
        if (trip.Manifest.Data["cover"]?["document"] is JsonValue cover && Guid.TryParse(cover.ToString(), out var coverId) && trip.Find(coverId) is { } doc && !((string?)doc.Data["media_type"] ?? "").StartsWith("image/", StringComparison.Ordinal)) Error("cover.type", "cover", "The cover must reference an image document.");
        var schedule = trip.Entities.Values.Where(e => e.Type == "schedule_item").ToArray();
        if (!result.Any(d => d.Severity == Severity.Error))
        {
            for (var i = 0; i < schedule.Length; i++)
                for (var j = i + 1; j < schedule.Length; j++)
                {
                    bool Ancestor(Guid parent, Guid child) { while (parents.TryGetValue(child, out child)) if (child == parent) return true; return false; }
                    if (Ancestor(schedule[i].Id, schedule[j].Id) || Ancestor(schedule[j].Id, schedule[i].Id)) continue;
                    // A stay runs in the background of everything else; being at a hotel never conflicts with an activity.
                    if (schedule[i].Data["components"]?["accommodation"] is JsonObject || schedule[j].Data["components"]?["accommodation"] is JsonObject) continue;
                    var a = TripQueries.Range(trip, schedule[i]); var b = TripQueries.Range(trip, schedule[j]);
                    var p = TripQueries.Resolve(trip, schedule[i].Id, "participants").Value as JsonArray;
                    var q = TripQueries.Resolve(trip, schedule[j].Id, "participants").Value as JsonArray;
                    if (a.Start < b.End && b.Start < a.End && p is not null && q is not null && p.Any(x => q.Any(y => JsonNode.DeepEquals(x, y)))) result.Add(new("plan.overlap", Severity.Warning, schedule[i].Id.ToString(), "A participant has overlapping activities."));
                }
            // Provider estimates are optional, derived input. They never make repository data invalid.
            var estimates = (travelEstimates ?? []).Where(x => x.Duration >= NodaTime.Duration.Zero).ToArray();
            foreach (var person in trip.Entities.Values.Where(e => e.Type == "person"))
            {
                var activities = schedule.Where(e => e.Data["children"] is not JsonArray a || a.Count == 0).Where(e => (TripQueries.Resolve(trip, e.Id, "participants").Value as JsonArray)?.Any(p => p?.ToString() == person.Id.ToString()) == true).Select(e => (Item: e, Range: TripQueries.Range(trip, e))).Where(x => x.Range.Start is not null && x.Range.End is not null).OrderBy(x => x.Range.Start).ToArray();
                for (var i = 1; i < activities.Length; i++)
                {
                    var before = activities[i - 1]; var after = activities[i];
                    if (!Guid.TryParse((before.Item.Data["components"]?["transport"]?["arrival"]?["place"] ?? TripQueries.Resolve(trip, before.Item.Id, "default_place").Value)?.ToString(), out var from) || !Guid.TryParse((after.Item.Data["components"]?["transport"]?["departure"]?["place"] ?? TripQueries.Resolve(trip, after.Item.Id, "default_place").Value)?.ToString(), out var to)) continue;
                    var estimate = estimates.FirstOrDefault(x => x.FromPlace == from && x.ToPlace == to);
                    if (estimate is not null && after.Range.Start - before.Range.End < estimate.Duration) result.Add(new("plan.travel_gap", Severity.Warning, after.Item.Id.ToString(), "The available gap is shorter than the estimated travel time."));
                }
            }
            foreach (var e in schedule)
            {
                if (e.Data["constraints"] is not JsonArray constraints) continue;
                foreach (var c in constraints.OfType<JsonObject>())
                {
                    var range = TripQueries.Range(trip, e); var type = c["type"]?.ToString();
                    if (type == "earliest_start" && e.Data["time"]?["start"]?["local"] is JsonValue local && LocalDateTimePattern.ExtendedIso.Parse(local.ToString()).TryGetValue(default, out var date) && LocalTimePattern.ExtendedIso.Parse(c["local_time"]?.ToString() ?? "").TryGetValue(default, out var earliest) && date.TimeOfDay < earliest)
                        result.Add(new("plan.constraint", Severity.Warning, e.Id.ToString(), "Activity starts before its earliest preferred time."));
                    if (Guid.TryParse(c["item"]?.ToString(), out var id) && trip.Find(id) is { } other)
                    {
                        var r = TripQueries.Range(trip, other); var offset = PeriodPattern.NormalizingIso.Parse(c["offset"]?.ToString() ?? "PT0S");
                        if (!offset.Success) Error("constraint.offset", e.Id.ToString(), "Enter an ISO duration offset.");
                        if (offset.Success)
                        {
                            try
                            {
                                var delta = offset.Value.ToDuration();
                                if (type == "after" && range.Start < r.End + delta || type == "arrive_before" && range.End > r.Start - delta) result.Add(new("plan.constraint", Severity.Warning, e.Id.ToString(), "The planned time violates a temporal constraint."));
                            }
                            catch (InvalidOperationException) { Error("constraint.offset", e.Id.ToString(), "Offsets must be fixed durations."); }
                        }
                    }
                }
            }
        }
        return result;
    }
}
