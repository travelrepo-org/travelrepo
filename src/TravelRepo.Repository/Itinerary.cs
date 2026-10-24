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
    public string Stays { get; init; } = "Stays";
    public string Contacts { get; init; } = "Contacts";
    public string Tasks { get; init; } = "Tasks";
    public string Notes { get; init; } = "Notes";
    public string PackingLists { get; init; } = "Packing lists";
    public string Map { get; init; } = "Map";
    public string Attachments { get; init; } = "Attachments";
    /// <summary>Points from an entry to its appendix pages, for example "Attachment A1, A2".</summary>
    public string AttachedAs { get; init; } = "Attachment {0}";
    public string Nights { get; init; } = "{0} nights";
    public string OneNight { get; init; } = "1 night";
    public string CheckIn { get; init; } = "Check-in {0}";
    public string CheckOut { get; init; } = "Check-out {0}";
    public string CheckInHours { get; init; } = "Check-in hours {0}";
    public string CheckOutHours { get; init; } = "Check-out hours {0}";
    public string FromTime { get; init; } = "from {0}";
    public string UntilTime { get; init; } = "until {0}";
    public string FreeCancellation { get; init; } = "Free cancellation until {0}";
    public string Breakfast { get; init; } = "Breakfast: {0}";
    public string Parking { get; init; } = "Parking";
    public string ParkingIncluded { get; init; } = "Parking included";
    public string Due { get; init; } = "Due {0}";
    /// <summary>Shown under the title of one person's export.</summary>
    public string PlanFor { get; init; } = "Plan for {0}";
    public string PackingFor { get; init; } = "Packing list for {0}";
    public string NobodyYet { get; init; } = "Nobody brings these yet";
    public string Everyone { get; init; } = "Everyone";
    public string Nobody { get; init; } = "Nobody yet";
    public string With { get; init; } = "with {0}";
    public string PackedBy { get; init; } = "packed by {0}";
    public string PaidBy { get; init; } = "paid by {0}";
    public string Yes { get; init; } = "yes";
    public string No { get; init; } = "no";
    public string List { get; init; } = "List";
    public string Group { get; init; } = "Group";
    public string Item { get; init; } = "Item";
    public string Quantity { get; init; } = "Quantity";
    public string BroughtBy { get; init; } = "Brought by";
    public string Packed { get; init; } = "Packed";
    public string Done { get; init; } = "Done";
    /// <summary>Heading for packing items without a group when other items have one.</summary>
    public string OtherItems { get; init; } = "Other";
    public IReadOnlyDictionary<string, string> DayParts { get; init; } = new Dictionary<string, string> { ["morning"] = "Morning", ["afternoon"] = "Afternoon", ["evening"] = "Evening", ["night"] = "Night" };
    public IReadOnlyDictionary<string, string> Statuses { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> Access { get; init; } = new Dictionary<string, string> { ["reception"] = "Reception", ["self"] = "Self check-in", ["host"] = "The host lets you in" };
    public IReadOnlyDictionary<string, string> BreakfastValues { get; init; } = new Dictionary<string, string> { ["included"] = "included", ["available"] = "available", ["none"] = "none" };

    public string NightCount(int nights) => nights == 1 ? OneNight : string.Format(CultureInfo.CurrentCulture, Nights, nights);
    public string Status(string? status) => status is null ? "" : Statuses.GetValueOrDefault(status) ?? status;
}

/// <summary>One schedule item in an itinerary. <c>Notes</c> holds its Markdown notes as plain text lines.</summary>
public sealed record ItineraryEntry(Entity Item, ScheduleKind Kind, string Time, string? Where, IReadOnlyList<string> People, string? Status, string? BookingReference, string? Group, IReadOnlyList<string>? Notes = null);
public sealed record ItineraryDay(LocalDate Date, int Number, IReadOnlyList<ItineraryEntry> Entries);
public sealed record Itinerary(string Title, LocalDate? Start, LocalDate? End, IReadOnlyList<string> Travellers, IReadOnlyList<ItineraryDay> Days, IReadOnlyList<ItineraryEntry> Unscheduled, IReadOnlyList<Entity> Bookings, IReadOnlyList<CurrencyTotal> Costs, string Zone);

/// <summary>A day-by-day reading of the plan for print, PDF and calendar exports.</summary>
public static class TripItinerary
{
    public static Itinerary Build(TripSnapshot trip, ExportLabels? labels = null, CultureInfo? culture = null, ExportOptions? options = null)
    {
        labels ??= new(); culture ??= CultureInfo.CurrentCulture; options ??= new();
        var filter = new ExportFilter(trip, options);
        var zone = ScheduleQueries.TripZone(trip);
        string Clock(Instant i) => i.InZone(zone).TimeOfDay.ToString("HH:mm", CultureInfo.InvariantCulture);
        var parents = trip.Entities.Values.Where(e => e.Data["children"] is JsonArray { Count: > 0 }).ToArray();
        var entries = new List<(Instant? Start, LocalDate? Day, ItineraryEntry Entry)>();
        foreach (var item in trip.Entities.Values.Where(e => e.Type == "schedule_item" && e.Data["children"] is not JsonArray { Count: > 0 } && filter.Includes(e)))
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
                status is null ? null : labels.Status(status), booking?.Data["reference"]?.ToString(), parent?.Title, ExportText.Notes(trip, item));
            // An item that starts before the range is listed on the range's first day.
            var day = filter.Days(item)?.First is { } first && options.From is { } from && first < from ? from : filter.Days(item)?.First;
            entries.Add((span.IsPlaced ? span.Start : null, day, entry));
        }
        var (start, end) = TripQueries.EffectiveDates(trip);
        var firstDay = start ?? entries.Where(e => e.Day is not null).Select(e => e.Day!.Value).DefaultIfEmpty(LocalDate.MinIsoValue).Min();
        var days = entries.Where(e => e.Day is not null).OrderBy(e => e.Start).GroupBy(e => e.Day!.Value).OrderBy(g => g.Key)
            .Select(g => new ItineraryDay(g.Key, firstDay == LocalDate.MinIsoValue ? 0 : Period.Between(firstDay, g.Key, PeriodUnits.Days).Days + 1, g.Select(x => x.Entry).ToArray())).ToArray();
        var unscheduled = entries.Where(e => e.Day is null).Select(e => e.Entry).OrderBy(e => e.Item.Title, StringComparer.Create(culture, false)).ToArray();
        var travellers = (trip.Manifest.Data["participants"] as JsonArray ?? []).Select(n => Guid.TryParse(n?.ToString(), out var id) ? trip.Find(id)?.Title : null).OfType<string>().ToArray();
        if (travellers.Length == 0) travellers = trip.Entities.Values.Where(e => e.Type == "person").Select(e => e.Title).ToArray();
        var bookings = trip.Entities.Values.Where(e => e.Type == "booking" && filter.IncludesBooking(e)).OrderBy(e => e.Title, StringComparer.Create(culture, false)).ToArray();
        // Budgets belong to the whole trip, so one person's export shows only what that person paid.
        var costs = options.Person is null ? TripSummaries.Costs(trip)
            : TripSummaries.Costs(trip with { Entities = trip.Entities.Values.Where(e => e.Type == "expense" && filter.IncludesExpense(e)).ToDictionary(e => e.Id) });
        return new(trip.Manifest.Title, start, end, travellers, days, unscheduled, bookings, costs, zone.Id);
    }
}
