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
    public string CheckInAt { get; init; } = "Check-in · {0}";
    public string CheckOutAt { get; init; } = "Check-out · {0}";
    public string NightAt { get; init; } = "Night: {0}";
    public string NightOf { get; init; } = "night {0} of {1}";
    /// <summary>Names the time zone of all times, for example "Time zone: Tokyo (JST, UTC+9)".</summary>
    public string TimeZone { get; init; } = "Time zone: {0}";
    public IReadOnlyDictionary<string, string> DayParts { get; init; } = new Dictionary<string, string> { ["morning"] = "Morning", ["afternoon"] = "Afternoon", ["evening"] = "Evening", ["night"] = "Night" };
    public IReadOnlyDictionary<string, string> Statuses { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> Access { get; init; } = new Dictionary<string, string> { ["reception"] = "Reception", ["self"] = "Self check-in", ["host"] = "The host lets you in" };
    public IReadOnlyDictionary<string, string> BreakfastValues { get; init; } = new Dictionary<string, string> { ["included"] = "included", ["available"] = "available", ["none"] = "none" };

    public string NightCount(int nights) => nights == 1 ? OneNight : string.Format(CultureInfo.CurrentCulture, Nights, nights);
    public string Status(string? status) => status is null ? "" : Statuses.GetValueOrDefault(status) ?? status;
}

/// <summary>What an itinerary entry stands for: a schedule item, or the arrival or departure of a stay.</summary>
public enum ItineraryRole { Item, CheckIn, CheckOut }

/// <summary>
/// One line of a day: a schedule item, or a stay's check-in or check-out (then <c>Item</c> is the stay). <c>Notes</c>
/// holds the item's Markdown notes as plain text lines.
/// </summary>
public sealed record ItineraryEntry(Entity Item, ScheduleKind Kind, string Time, string? Where, IReadOnlyList<string> People, string? Status, string? BookingReference, string? Group, IReadOnlyList<string>? Notes = null, ItineraryRole Role = ItineraryRole.Item);

/// <summary>A night of a stay run: the run and which of its nights this is, starting at 1.</summary>
public sealed record ItineraryNight(StayRun Run, int Number);

public sealed record ItineraryDay(LocalDate Date, int Number, IReadOnlyList<ItineraryEntry> Entries)
{
    /// <summary>Where the travellers sleep after this day.</summary>
    public IReadOnlyList<ItineraryNight> Nights { get; init; } = [];
}

public sealed record Itinerary(string Title, LocalDate? Start, LocalDate? End, IReadOnlyList<string> Travellers, IReadOnlyList<ItineraryDay> Days, IReadOnlyList<ItineraryEntry> Unscheduled, IReadOnlyList<Entity> Bookings, IReadOnlyList<CurrencyTotal> Costs, string Zone)
{
    /// <summary>The stay runs the export contains, in date order.</summary>
    public IReadOnlyList<StayRun> Stays { get; init; } = [];
}

/// <summary>
/// A day-by-day reading of the plan for print, PDF and calendar exports. Stays do not appear as items spanning
/// several days: their check-in and check-out are lines on the days they happen, and every day lists where the
/// travellers sleep. Times are shown in the trip's time zone; a time in another zone carries its zone's short name,
/// and when either end of an item is in another zone, both ends show their own local time and short name.
/// </summary>
public static class TripItinerary
{
    public static Itinerary Build(TripSnapshot trip, ExportLabels? labels = null, CultureInfo? culture = null, ExportOptions? options = null)
    {
        labels ??= new(); culture ??= CultureInfo.CurrentCulture; options ??= new();
        var filter = new ExportFilter(trip, options);
        var zone = ScheduleQueries.TripZone(trip);
        static string Clock(ZonedDateTime t) => t.TimeOfDay.ToString("HH:mm", CultureInfo.InvariantCulture);
        string Short(Instant i, DateTimeZone z) => Clock(i.InZone(z)) + " " + ExportTimes.Abbreviation(z, i);
        var parents = trip.Entities.Values.Where(e => e.Data["children"] is JsonArray { Count: > 0 }).ToArray();
        var runs = Stays.Runs(trip).Where(r => filter.Includes(r.Main) && filter.InRange(r.FirstNight, r.DepartureDay)).ToArray();
        var inRuns = runs.SelectMany(r => r.Stays).Select(s => s.Item.Id).ToHashSet();
        var entries = new List<(Instant? Start, LocalDate? Day, ItineraryEntry Entry)>();
        string? Reference(Entity item) => (Guid.TryParse((item.Data["components"]?["booking"]?["ref"] ?? item.Data["components"]?["accommodation"]?["booking"])?.ToString(), out var id) ? trip.Find(id) : null)?.Data["reference"]?.ToString();
        foreach (var item in trip.Entities.Values.Where(e => e.Type == "schedule_item" && e.Data["children"] is not JsonArray { Count: > 0 } && !inRuns.Contains(e.Id) && filter.Includes(e)))
        {
            var span = ScheduleQueries.Span(trip, item);
            var startZone = span.StartZone is { } sz ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(sz) ?? zone : zone;
            var endZone = span.EndZone is { } ez ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(ez) ?? startZone : startZone;
            // When either end is in another zone, both ends show their local time with its short name.
            var foreign = startZone.Id != zone.Id || endZone.Id != zone.Id;
            string At(Instant i, DateTimeZone z) => foreign ? Short(i, z) : Clock(i.InZone(zone));
            var time = span.Precision switch
            {
                TimePrecision.AllDay => labels.AllDay,
                TimePrecision.DayPart => labels.DayParts.GetValueOrDefault(span.DayPart ?? "") ?? span.DayPart ?? "",
                TimePrecision.Window when span.IsPlaced => string.Format(culture, labels.Between, At(span.Start!.Value, startZone), At(span.End!.Value, endZone)),
                TimePrecision.Approximate when span.IsPlaced => "~" + At(span.Start!.Value, startZone),
                TimePrecision.Exact when span.IsPlaced => At(span.Start!.Value, startZone) + (foreign ? " – " : "–")
                    + (span.End!.Value.InZone(endZone).Date != span.Start.Value.InZone(startZone).Date ? span.End.Value.InZone(endZone).Date.ToString("d MMM", culture) + " " : "") + At(span.End.Value, endZone),
                _ => ""
            };
            var route = ScheduleQueries.Route(trip, item);
            var where = route.From is not null && route.To is not null ? route.From.Title + " → " + route.To.Title : ScheduleQueries.PrimaryPlace(trip, item)?.Title;
            var transport = item.Data["components"]?["transport"];
            var details = string.Join(" ", new[] { transport?["carrier"]?.ToString(), transport?["flight_number"]?.ToString() }.Where(x => !string.IsNullOrWhiteSpace(x)));
            if (details.Length > 0) where = where is null ? details : where + " · " + details;
            var status = item.Data["status"]?.ToString();
            var parent = parents.FirstOrDefault(p => p.Data["children"]!.AsArray().Any(c => c?.ToString() == item.Id.ToString()));
            var entry = new ItineraryEntry(item, ScheduleCategories.Classify(trip, item), time, where, ScheduleQueries.Participants(trip, item).Select(p => p.Title).ToArray(),
                status is null ? null : labels.Status(status), Reference(item), parent?.Title, ExportText.Notes(trip, item));
            // An item that starts before the range is listed on the range's first day.
            var day = filter.Days(item)?.First is { } first && options.From is { } from && first < from ? from : filter.Days(item)?.First;
            entries.Add((span.IsPlaced ? span.Start : null, day, entry));
        }

        // A stay run adds its check-in and check-out to the days they happen on.
        foreach (var run in runs)
        {
            var main = run.Main; var place = main.Place is { } p ? trip.Find(p) : null;
            var stayZone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(main.Zone) ?? zone;
            void Add(ItineraryRole role, LocalDate day, Stay? source, LocalTime fallback)
            {
                if (!filter.InRange(day, day)) return;
                var value = role == ItineraryRole.CheckIn ? source?.CheckIn : source?.CheckOut;
                var approximate = role == ItineraryRole.CheckIn ? source?.CheckInApproximate ?? true : source?.CheckOutApproximate ?? true;
                Instant? at = null;
                try { at = value?.ToInstant(); } catch (DomainException) { }
                var sort = at ?? day.At(fallback).InZoneLeniently(stayZone).ToInstant();
                var time = at is { } exact && !approximate ? (stayZone.Id == zone.Id ? Clock(exact.InZone(zone)) : Short(exact, stayZone)) : "";
                var item = (source ?? main).Item;
                entries.Add((sort, day, new ItineraryEntry(item, ScheduleKind.Accommodation, time, place?.Title != item.Title ? place?.Title : null, ScheduleQueries.Participants(trip, item).Select(x => x.Title).ToArray(),
                    labels.Status(item.Data["status"]?.ToString()) is { Length: > 0 } st ? st : null, run.Stays.Select(x => Reference(x.Item)).FirstOrDefault(r => r is not null), null, null, role)));
            }
            Add(ItineraryRole.CheckIn, run.FirstNight, run.CheckInStay, Stays.DefaultCheckIn);
            Add(ItineraryRole.CheckOut, run.DepartureDay, run.CheckOutStay, Stays.DefaultCheckOut);
        }

        var (start, end) = TripQueries.EffectiveDates(trip);
        var nightDays = runs.SelectMany(r => Enumerable.Range(0, r.Nights).Select(r.FirstNight.PlusDays)).Where(d => filter.InRange(d, d));
        var allDays = entries.Where(e => e.Day is not null).Select(e => e.Day!.Value).Concat(nightDays).Distinct().Order().ToArray();
        var firstDay = start ?? allDays.DefaultIfEmpty(LocalDate.MinIsoValue).Min();
        var days = allDays.Select(date => new ItineraryDay(date, firstDay == LocalDate.MinIsoValue ? 0 : Period.Between(firstDay, date, PeriodUnits.Days).Days + 1,
            entries.Where(e => e.Day == date).OrderBy(e => e.Start).Select(e => e.Entry).ToArray())
        { Nights = runs.Where(r => r.Covers(date)).Select(r => new ItineraryNight(r, r.NightNumber(date))).ToArray() }).ToArray();
        var unscheduled = entries.Where(e => e.Day is null).Select(e => e.Entry).OrderBy(e => e.Item.Title, StringComparer.Create(culture, false)).ToArray();
        var travellers = (trip.Manifest.Data["participants"] as JsonArray ?? []).Select(n => Guid.TryParse(n?.ToString(), out var id) ? trip.Find(id)?.Title : null).OfType<string>().ToArray();
        if (travellers.Length == 0) travellers = trip.Entities.Values.Where(e => e.Type == "person").Select(e => e.Title).ToArray();
        var bookings = trip.Entities.Values.Where(e => e.Type == "booking" && filter.IncludesBooking(e)).OrderBy(e => e.Title, StringComparer.Create(culture, false)).ToArray();
        // Budgets belong to the whole trip, so one person's export shows only what that person paid.
        var costs = options.Person is null ? TripSummaries.Costs(trip)
            : TripSummaries.Costs(trip with { Entities = trip.Entities.Values.Where(e => e.Type == "expense" && filter.IncludesExpense(e)).ToDictionary(e => e.Id) });
        return new(trip.Manifest.Title, start, end, travellers, days, unscheduled, bookings, costs, zone.Id) { Stays = runs };
    }
}

/// <summary>Time zone names for exports.</summary>
public static class ExportTimes
{
    /// <summary>
    /// The zone's short name at an instant, such as JST or CEST. Where the time zone database has only a number,
    /// as for many zones, the offset is shown instead, such as UTC-3.
    /// </summary>
    public static string Abbreviation(DateTimeZone zone, Instant at)
    {
        var interval = zone.GetZoneInterval(at);
        return interval.Name is { Length: > 1 } name && name.All(char.IsLetter) ? name : Offset(interval.WallOffset);
    }

    /// <summary>An offset such as UTC+9, UTC-3 or UTC+5:30.</summary>
    public static string Offset(Offset offset)
    {
        var minutes = offset.Seconds / 60; var sign = minutes < 0 ? "-" : "+"; minutes = Math.Abs(minutes);
        return "UTC" + sign + (minutes / 60).ToString(CultureInfo.InvariantCulture) + (minutes % 60 == 0 ? "" : ":" + (minutes % 60).ToString("00", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The trip's zone for a page header, such as "Tokyo (JST, UTC+9)". Over a change to or from summer time both
    /// names and offsets appear, such as "Berlin (CET/CEST, UTC+1/+2)".
    /// </summary>
    public static string Describe(DateTimeZone zone, LocalDate? first, LocalDate? last)
    {
        var from = (first ?? last ?? SystemClock.Instance.GetCurrentInstant().InZone(zone).Date).AtStartOfDayInZone(zone).ToInstant();
        var to = (last ?? first)?.PlusDays(1).AtStartOfDayInZone(zone).ToInstant() ?? from;
        var intervals = zone.GetZoneIntervals(from, to > from ? to : from + Duration.FromTicks(1)).DistinctBy(i => (i.Name, i.WallOffset)).ToArray();
        var names = intervals.Select(i => Abbreviation(zone, i.HasStart && i.Start > from ? i.Start : from)).Where(n => !n.StartsWith("UTC", StringComparison.Ordinal)).Distinct().ToArray();
        var offsets = intervals.Select(i => Offset(i.WallOffset)).Distinct().ToArray();
        var city = zone.Id is "UTC" or "Etc/UTC" ? "UTC" : zone.Id.Split('/')[^1].Replace('_', ' ');
        var offset = offsets[0] + string.Concat(offsets.Skip(1).Select(o => "/" + o[3..]));
        return city + " (" + (names.Length > 0 ? string.Join("/", names) + ", " : "") + offset + ")";
    }
}
