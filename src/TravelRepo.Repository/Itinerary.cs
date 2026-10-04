using System.Globalization;
using System.Text.Json.Nodes;
using NodaTime;
using TravelRepo.Core;

namespace TravelRepo.Repository;

/// <summary>Wording used in exports. English defaults; clients pass translations.</summary>
public sealed record ExportLabels
{
    public string Day { get; init; } = "Day {0}";
    public string AllDay { get; init; } = "All day";
    public string Unscheduled { get; init; } = "Not scheduled yet";
    public string Bookings { get; init; } = "Bookings";
    public string Costs { get; init; } = "Costs";
    public string Travellers { get; init; } = "Travellers";
    public string Paid { get; init; } = "paid";
    public string Estimated { get; init; } = "estimated";
    public string Budget { get; init; } = "budget";
    public string Between { get; init; } = "{0} to {1}";
    public string Reference { get; init; } = "Ref.";
    public string GeneratedBy { get; init; } = "Exported from a TravelRepo trip";
    public IReadOnlyDictionary<string, string> DayParts { get; init; } = new Dictionary<string, string> { ["morning"] = "Morning", ["afternoon"] = "Afternoon", ["evening"] = "Evening", ["night"] = "Night" };
    public IReadOnlyDictionary<string, string> Statuses { get; init; } = new Dictionary<string, string>();
}

public sealed record ItineraryEntry(Entity Item, ScheduleKind Kind, string Time, string? Where, IReadOnlyList<string> People, string? Status, string? BookingReference, string? Group);
public sealed record ItineraryDay(LocalDate Date, int Number, IReadOnlyList<ItineraryEntry> Entries);
public sealed record Itinerary(string Title, LocalDate? Start, LocalDate? End, IReadOnlyList<string> Travellers, IReadOnlyList<ItineraryDay> Days, IReadOnlyList<ItineraryEntry> Unscheduled, IReadOnlyList<Entity> Bookings, IReadOnlyList<CurrencyTotal> Costs, string Zone);

/// <summary>A day-by-day reading of the plan for print, PDF and calendar exports.</summary>
public static class TripItinerary
{
    public static Itinerary Build(TripSnapshot trip, ExportLabels? labels = null, CultureInfo? culture = null)
    {
        labels ??= new(); culture ??= CultureInfo.CurrentCulture;
        var zone = ScheduleQueries.TripZone(trip);
        string Clock(Instant i) => i.InZone(zone).TimeOfDay.ToString("HH:mm", CultureInfo.InvariantCulture);
        var parents = trip.Entities.Values.Where(e => e.Data["children"] is JsonArray { Count: > 0 }).ToArray();
        var entries = new List<(Instant? Start, ItineraryEntry Entry)>();
        foreach (var item in trip.Entities.Values.Where(e => e.Type == "schedule_item" && e.Data["children"] is not JsonArray { Count: > 0 }))
        {
            var span = ScheduleQueries.Span(trip, item);
            var time = span.Precision switch
            {
                TimePrecision.AllDay => labels.AllDay,
                TimePrecision.DayPart => labels.DayParts.GetValueOrDefault(span.DayPart ?? "") ?? span.DayPart ?? "",
                TimePrecision.Window when span.IsPlaced => string.Format(culture, labels.Between, Clock(span.Start!.Value), Clock(span.End!.Value)),
                TimePrecision.Approximate when span.IsPlaced => "~" + Clock(span.Start!.Value),
                TimePrecision.Exact when span.IsPlaced => Clock(span.Start!.Value) + "–" + (span.End!.Value.InZone(zone).Date != span.Start.Value.InZone(zone).Date ? span.End.Value.InZone(zone).Date.ToString("d MMM", culture) + " " : "") + Clock(span.End.Value),
                _ => ""
            };
            if (span.StartZone is { } other && other != zone.Id && span.Start is { } s)
                time += " (" + s.InZone(DateTimeZoneProviders.Tzdb[other]).TimeOfDay.ToString("HH:mm", CultureInfo.InvariantCulture) + " " + other.Split('/')[^1].Replace('_', ' ') + ")";
            var route = ScheduleQueries.Route(trip, item);
            var where = route.From is not null && route.To is not null ? route.From.Title + " → " + route.To.Title : ScheduleQueries.PrimaryPlace(trip, item)?.Title;
            var transport = item.Data["components"]?["transport"];
            var details = string.Join(" ", new[] { transport?["carrier"]?.ToString(), transport?["flight_number"]?.ToString() }.Where(x => !string.IsNullOrWhiteSpace(x)));
            if (details.Length > 0) where = where is null ? details : where + " · " + details;
            var booking = Guid.TryParse(item.Data["components"]?["booking"]?["ref"]?.ToString(), out var bookingId) ? trip.Find(bookingId) : null;
            var status = item.Data["status"]?.ToString();
            var parent = parents.FirstOrDefault(p => p.Data["children"]!.AsArray().Any(c => c?.ToString() == item.Id.ToString()));
            var entry = new ItineraryEntry(item, ScheduleCategories.Classify(trip, item), time, where, ScheduleQueries.Participants(trip, item).Select(p => p.Title).ToArray(),
                status is null ? null : labels.Statuses.GetValueOrDefault(status) ?? status, booking?.Data["reference"]?.ToString(), parent?.Title);
            entries.Add((span.IsPlaced ? span.Start : null, entry));
        }
        var (start, end) = TripQueries.EffectiveDates(trip);
        var first = start ?? entries.Where(e => e.Start is not null).Select(e => e.Start!.Value.InZone(zone).Date).DefaultIfEmpty(LocalDate.MinIsoValue).Min();
        var days = entries.Where(e => e.Start is not null).OrderBy(e => e.Start).GroupBy(e => e.Start!.Value.InZone(zone).Date)
            .Select(g => new ItineraryDay(g.Key, first == LocalDate.MinIsoValue ? 0 : Period.Between(first, g.Key, PeriodUnits.Days).Days + 1, g.Select(x => x.Entry).ToArray())).ToArray();
        var unscheduled = entries.Where(e => e.Start is null).Select(e => e.Entry).OrderBy(e => e.Item.Title, StringComparer.Create(culture, false)).ToArray();
        var travellers = (trip.Manifest.Data["participants"] as JsonArray ?? []).Select(n => Guid.TryParse(n?.ToString(), out var id) ? trip.Find(id)?.Title : null).OfType<string>().ToArray();
        if (travellers.Length == 0) travellers = trip.Entities.Values.Where(e => e.Type == "person").Select(e => e.Title).ToArray();
        var bookings = trip.Entities.Values.Where(e => e.Type == "booking").OrderBy(e => e.Title, StringComparer.Create(culture, false)).ToArray();
        return new(trip.Manifest.Title, start, end, travellers, days, unscheduled, bookings, TripSummaries.Costs(trip), zone.Id);
    }
}
