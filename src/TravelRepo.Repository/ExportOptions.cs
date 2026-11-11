using System.Globalization;
using NodaTime;
using NodaTime.Text;
using TravelRepo.Core;

namespace TravelRepo.Repository;

/// <summary>Parts of a trip export. Clients offer them as check boxes.</summary>
[Flags]
public enum ExportParts
{
    None = 0,
    Schedule = 1 << 0,
    Stays = 1 << 1,
    Bookings = 1 << 2,
    Contacts = 1 << 3,
    Costs = 1 << 4,
    Tasks = 1 << 5,
    Notes = 1 << 6,
    Packing = 1 << 7,
    /// <summary>A map of the exported places and routes. Drawn by the client; only the data comes from TravelRepo.</summary>
    Map = 1 << 8,
    /// <summary>Attached confirmations as appendix pages. Only PDF renderers can include the files themselves.</summary>
    Attachments = 1 << 9,
    Standard = Schedule | Stays | Bookings | Contacts | Costs,
    All = Standard | Tasks | Notes | Packing | Map | Attachments
}

public enum ExportDetail { Detailed, Compact }

public enum PaperSize { A4, Letter }

/// <summary>
/// What a trip export contains. Rules that decide what belongs to a date range or to one person live in
/// <see cref="ExportFilter"/>, so every client and the CLI export the same things.
/// </summary>
public sealed record ExportOptions
{
    public ExportParts Parts { get; init; } = ExportParts.Standard;
    /// <summary>First day to include, inclusive. Null starts at the beginning of the trip.</summary>
    public LocalDate? From { get; init; }
    /// <summary>Last day to include, inclusive. Null runs to the end of the trip.</summary>
    public LocalDate? To { get; init; }
    /// <summary>When set, the export shows only what concerns this person.</summary>
    public Guid? Person { get; init; }
    public ExportDetail Detail { get; init; } = ExportDetail.Detailed;
    /// <summary>Paper size for renderers that produce pages.</summary>
    public PaperSize Paper { get; init; } = PaperSize.A4;
    /// <summary>Whether renderers with pages draw each day's plans on a time line beside the list.</summary>
    public bool Timeline { get; init; } = true;
    /// <summary>Whether rows carry the comments on what they stand for.</summary>
    public bool Comments { get; init; }
    /// <summary>
    /// A time zone from the time zone database, such as <c>Pacific/Auckland</c>, that every time, day and date range
    /// is shown in. Null uses the trip's default time zone and shows a time in another zone in that zone's local time.
    /// </summary>
    public string? TimeZone { get; init; }
    /// <summary>
    /// More time zones whose local times are shown beside each time, such as home. The days and the date range still
    /// follow <see cref="TimeZone"/> or the trip's zone.
    /// </summary>
    public IReadOnlyList<string> OtherTimeZones { get; init; } = [];

    public bool Includes(ExportParts part) => (Parts & part) == part;
    public bool Compact => Detail == ExportDetail.Compact;
    public bool HasRange => From is not null || To is not null;

    /// <summary>The zone the export shows times in: <see cref="TimeZone"/>, or else the trip's default time zone.</summary>
    public DateTimeZone Zone(TripSnapshot trip) => TimeZone is { } id
        ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(id) ?? throw new DomainException("export.timezone", "Unknown time zone: " + id)
        : ScheduleQueries.TripZone(trip);

    /// <summary>The zones of <see cref="OtherTimeZones"/> without the export's own zone and repeats.</summary>
    public IReadOnlyList<DateTimeZone> OtherZones(TripSnapshot trip)
    {
        var main = Zone(trip).Id;
        return [.. OtherTimeZones.Select(id => DateTimeZoneProviders.Tzdb.GetZoneOrNull(id) ?? throw new DomainException("export.timezone", "Unknown time zone: " + id))
            .Where(z => z.Id != main).DistinctBy(z => z.Id)];
    }

    /// <summary>Letter in the countries that use it, A4 everywhere else.</summary>
    public static PaperSize PaperFor(CultureInfo culture)
    {
        try { return new RegionInfo(culture.Name).TwoLetterISORegionName is "US" or "CA" or "MX" or "PH" or "CL" or "CO" or "VE" or "GT" or "CR" or "PR" ? PaperSize.Letter : PaperSize.A4; }
        catch (ArgumentException) { return PaperSize.A4; }
    }

    /// <summary>Parts as a comma-separated list such as <c>schedule,stays,map</c>, for the command line and saved settings.</summary>
    public static string FormatParts(ExportParts parts) =>
        string.Join(",", Enum.GetValues<ExportParts>().Where(p => p is not (ExportParts.None or ExportParts.Standard or ExportParts.All) && (parts & p) == p).Select(p => p.ToString().ToLowerInvariant()));

    public static ExportParts ParseParts(string text)
    {
        var parts = ExportParts.None;
        foreach (var name in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            parts |= Enum.TryParse<ExportParts>(name, true, out var part) && part != ExportParts.None ? part : throw new DomainException("export.part", "Unknown export part: " + name);
        return parts;
    }

    /// <summary>A time zone id such as <c>Pacific/Auckland</c> or <c>UTC</c> in its canonical spelling, or null for none.</summary>
    public static string? ParseZone(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : DateTimeZoneProviders.Tzdb.GetZoneOrNull(text.Trim())?.Id ?? throw new DomainException("export.timezone", "Use a time zone such as Pacific/Auckland or UTC.");

    /// <summary>Comma-separated time zones such as <c>Europe/Berlin,UTC</c>, in their canonical spelling.</summary>
    public static IReadOnlyList<string> ParseZones(string? text) =>
        [.. (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(z => ParseZone(z)!).Distinct()];

    public static LocalDate? ParseDate(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : LocalDatePattern.Iso.Parse(text.Trim()) is { Success: true } r ? r.Value : throw new DomainException("export.date", "Use dates like 2027-05-14.");
}

/// <summary>Decides what belongs to an export's date range and person.</summary>
/// <remarks>
/// Things without people (no participants, no travellers, nobody assigned) concern everyone, so they stay in every
/// person's export. Items without a time stay only in exports without a date range.
/// </remarks>
public sealed class ExportFilter(TripSnapshot trip, ExportOptions options)
{
    private readonly DateTimeZone zone = options.Zone(trip);

    /// <summary>The first and last day an item touches, in the export's time zone, or null when it has no time.</summary>
    public (LocalDate First, LocalDate Last)? Days(Entity item)
    {
        var span = ScheduleQueries.Span(trip, item);
        if (!span.IsPlaced || span.Start is not { } start) return null;
        // All-day and day-part plans belong to a calendar day, not to a moment that could move to another day.
        if (options.TimeZone is not null && span.Precision is TimePrecision.AllDay or TimePrecision.DayPart && span.Date is { } planned) return (planned, planned);
        var first = start.InZone(zone).Date;
        var last = span.End is { } end && end > start ? end.Minus(Duration.FromTicks(1)).InZone(zone).Date : first;
        return (first, last < first ? first : last);
    }

    public bool InRange(LocalDate first, LocalDate last) => (options.From is not { } from || last >= from) && (options.To is not { } to || first <= to);

    /// <summary>Whether a schedule item belongs to the export: in range (or unscheduled without a range) and for the person.</summary>
    public bool Includes(Entity item)
    {
        if (Days(item) is { } days ? !InRange(days.First, days.Last) : options.HasRange) return false;
        return ForPerson(ScheduleQueries.Participants(trip, item).Select(p => p.Id).Concat(Ids(item.Data["components"]?["accommodation"]?["guests"])));
    }

    public bool Includes(Stay stay)
    {
        if (stay.FirstNight is { } first ? !InRange(first, stay.LastNight ?? first) : options.HasRange) return false;
        return ForPerson(ScheduleQueries.Participants(trip, stay.Item).Select(p => p.Id).Concat(Ids(stay.Item.Data["components"]?["accommodation"]?["guests"])));
    }

    /// <summary>
    /// A booking belongs when its travellers include the person (or it lists none) and, with a date range, when one of
    /// its schedule items is in the range. Bookings without schedule items appear only without a date range.
    /// </summary>
    public bool IncludesBooking(Entity booking)
    {
        var items = LinkedItems(booking).ToArray();
        var travellers = Ids(booking.Data["travelers"]).ToArray();
        if (items.Length == 0) return !options.HasRange && ForPerson(travellers);
        // Listed travellers decide who the booking is for; otherwise its schedule items do.
        return travellers.Length > 0 ? ForPerson(travellers) && items.Any(i => Days(i) is { } d ? InRange(d.First, d.Last) : !options.HasRange) : items.Any(Includes);
    }

    /// <summary>Tasks assigned to the person or to nobody, due in the range or without a due date. Packing items are not tasks here.</summary>
    public bool IncludesTask(Entity task)
    {
        if (PackingLists.IsItem(task)) return false;
        if (options.HasRange && LocalDatePattern.Iso.Parse(task.Data["due"]?["date"]?.ToString() ?? "") is { Success: true } due && !InRange(due.Value, due.Value)) return false;
        return ForPerson(Ids(task.Data["assigned_to"]));
    }

    /// <summary>In a person's export, the expenses that person paid, shares or owes or is owed money for; otherwise all.</summary>
    public bool IncludesExpense(Entity expense)
    {
        if (options.Person is not { } person) return true;
        if (Ids(expense.Data["paid_by"]).Contains(person)) return true;
        var split = Expenses.Split(trip, expense);
        return split.People.Contains(person) || split.Debts.Any(d => d.From == person || d.To == person);
    }

    /// <summary>The schedule items a booking covers: its <c>items</c> and items that point to it.</summary>
    public IEnumerable<Entity> LinkedItems(Entity booking) =>
        Ids(booking.Data["items"]).Select(trip.Find).OfType<Entity>()
            .Concat(trip.Entities.Values.Where(e => e.Type == "schedule_item" && (e.Data["components"]?["booking"]?["ref"] ?? e.Data["components"]?["accommodation"]?["booking"])?.ToString() == booking.Id.ToString()))
            .Where(e => e.Type == "schedule_item").DistinctBy(e => e.Id);

    private bool ForPerson(IEnumerable<Guid> people)
    {
        if (options.Person is not { } person) return true;
        var list = people.ToArray();
        return list.Length == 0 || list.Contains(person);
    }

    private static IEnumerable<Guid> Ids(System.Text.Json.Nodes.JsonNode? node) =>
        (node as System.Text.Json.Nodes.JsonArray ?? []).Select(n => Guid.TryParse(n?.ToString(), out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty);
}
