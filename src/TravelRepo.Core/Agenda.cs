using System.Text.Json.Nodes;
using NodaTime;

namespace TravelRepo.Core;

/// <summary>
/// What an agenda line stands for: a schedule item, the arrival or departure of a stay run, or the rest of an item that
/// began on an earlier day, such as a night flight on the day it lands.
/// </summary>
public enum AgendaRole { Item, CheckIn, CheckOut, Continued }

/// <summary>
/// One line of a day plan. For <see cref="AgendaRole.Item"/> it is a schedule item with its resolved span; a group
/// (an item with children) lists its nested items as <see cref="Steps"/>, in time order. For check-in and check-out
/// the item is the stay, <see cref="Start"/> is the arrival or departure and <see cref="End"/> is null. A
/// <see cref="AgendaRole.Continued"/> line keeps the item's own start and end; it has no steps.
/// </summary>
/// <param name="Item">The schedule item, or the stay for a check-in or check-out.</param>
/// <param name="Role">Whether the line is the item itself or a stay's arrival or departure.</param>
/// <param name="Kind">The kind used for colour and icon.</param>
/// <param name="Precision">How exact the time is. A check-in without a fixed hour is <see cref="TimePrecision.Approximate"/>.</param>
/// <param name="Start">When the line starts; null for an item with only a date.</param>
/// <param name="End">When it ends; null for check-in, check-out and items with only a date.</param>
/// <param name="Zone">The time zone of <paramref name="Start"/>.</param>
/// <param name="EndZone">The time zone of <paramref name="End"/>; differs from <paramref name="Zone"/> for a journey across zones.</param>
/// <param name="Day">The local date the line belongs to, in the time zone where it starts; null when it has no day.</param>
/// <param name="Steps">The nested items of a group, in time order.</param>
public sealed record AgendaEntry(Entity Item, AgendaRole Role, ScheduleKind Kind, TimePrecision Precision, Instant? Start, Instant? End, string? Zone, string? EndZone, LocalDate? Day, IReadOnlyList<AgendaEntry> Steps)
{
    public string? Status => Item.Data["status"]?.ToString();
    /// <summary>An idea: not agreed yet, shown as tentative.</summary>
    public bool IsIdea => Status == "idea";
    public bool IsGroup => Steps.Count > 0;
    /// <summary>A line with a clock time, as opposed to all day, a part of the day or only a date.</summary>
    public bool IsTimed => Start is not null && Precision is TimePrecision.Exact or TimePrecision.Approximate or TimePrecision.Window;
}

/// <summary>A night of a stay run: the run and which of its nights this is, starting at 1.</summary>
public sealed record AgendaNight(StayRun Run, int Number);

/// <summary>One day of the plan. <see cref="Number"/> counts from the trip's first day, which is day 1.</summary>
public sealed record AgendaDay(LocalDate Date, int Number, IReadOnlyList<AgendaEntry> Entries, IReadOnlyList<AgendaNight> Nights);

/// <param name="Days">Every day from the trip's first to its last date, plus days outside that range that have lines.</param>
/// <param name="Unscheduled">Items without a day, such as ideas not placed yet, by title.</param>
public sealed record Agenda(IReadOnlyList<AgendaDay> Days, IReadOnlyList<AgendaEntry> Unscheduled)
{
    public AgendaDay? Find(LocalDate date) => Days.FirstOrDefault(d => d.Date == date);
}

/// <summary>
/// The plan read day by day, for clients that lay out days themselves. Stays do not span several days here: their
/// check-in and check-out are lines on the days they happen, and each day lists where the travellers sleep. Nested
/// items appear once, as steps of their group. Times stay as instants with their zones; clients choose how to show them.
/// </summary>
public static class TripAgenda
{
    public static Agenda Build(TripSnapshot trip)
    {
        var tripZone = ScheduleQueries.TripZone(trip);
        var runs = Stays.Runs(trip);
        var inRuns = runs.SelectMany(r => r.Stays).Select(s => s.Item.Id).ToHashSet();
        var children = trip.Entities.Values.Where(e => e.Data["children"] is JsonArray).SelectMany(e => ChildIds(e)).ToHashSet();
        var lines = trip.Entities.Values
            .Where(e => e.Type == "schedule_item" && !children.Contains(e.Id) && !inRuns.Contains(e.Id))
            .Select(e => Line(trip, e, tripZone, []))
            .ToList();
        lines.AddRange(lines.SelectMany(Continued).ToArray());
        foreach (var run in runs)
        {
            lines.Add(StayLine(run, AgendaRole.CheckIn, run.FirstNight, tripZone));
            lines.Add(StayLine(run, AgendaRole.CheckOut, run.DepartureDay, tripZone));
        }

        var (start, end) = TripQueries.EffectiveDates(trip);
        var dates = lines.Where(l => l.Day is not null).Select(l => l.Day!.Value)
            .Concat(runs.SelectMany(r => Enumerable.Range(0, r.Nights).Select(r.FirstNight.PlusDays)));
        if (start is { } first && end is { } last && last >= first && Period.DaysBetween(first, last) < 366)
            dates = dates.Concat(Enumerable.Range(0, Period.DaysBetween(first, last) + 1).Select(first.PlusDays));
        var ordered = dates.Distinct().Order().ToArray();
        var dayOne = start ?? (ordered.Length > 0 ? ordered[0] : (LocalDate?)null);
        var days = ordered.Select(date => new AgendaDay(date, dayOne is { } one ? Period.DaysBetween(one, date) + 1 : 0,
            Sort(lines.Where(l => l.Day == date)),
            runs.Where(r => r.Covers(date)).Select(r => new AgendaNight(r, r.NightNumber(date))).ToArray())).ToArray();
        var unscheduled = lines.Where(l => l.Day is null).OrderBy(l => l.Item.Title, StringComparer.CurrentCulture).ToArray();
        return new(days, unscheduled);
    }

    private static IEnumerable<Guid> ChildIds(Entity parent) =>
        (parent.Data["children"] as JsonArray ?? []).Select(n => Guid.TryParse(n?.ToString(), out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty);

    private static AgendaEntry Line(TripSnapshot trip, Entity item, DateTimeZone tripZone, HashSet<Guid> seen)
    {
        var span = ScheduleQueries.Span(trip, item);
        seen.Add(item.Id);
        // A group's steps are its children; a child listed twice or pointing back up is skipped.
        var steps = Sort(ChildIds(item).Where(id => !seen.Contains(id)).Select(trip.Find).OfType<Entity>()
            .Where(c => c.Type == "schedule_item").Select(c => Line(trip, c, tripZone, seen)));
        var startZone = span.StartZone is { } z ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(z) ?? tripZone : tripZone;
        var day = span.Date ?? (span.Start is { } at ? at.InZone(startZone).Date : null);
        return new(item, AgendaRole.Item, ScheduleCategories.Classify(trip, item), span.Precision, span.Start, span.End,
            span.StartZone ?? (span.Start is null ? null : tripZone.Id), span.EndZone, day, steps);
    }

    /// <summary>
    /// An item that runs past midnight also appears on the days after the one it starts on, up to the day it ends in
    /// the zone where it ends. An end at midnight belongs to the day before.
    /// </summary>
    private static IEnumerable<AgendaEntry> Continued(AgendaEntry line)
    {
        if (line.Role != AgendaRole.Item || line.Day is not { } first || line.Start is null || line.End is not { } end || end <= line.Start) yield break;
        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(line.EndZone ?? line.Zone ?? "") ?? DateTimeZone.Utc;
        var local = end.InZone(zone).LocalDateTime;
        var last = local.TimeOfDay == LocalTime.Midnight ? local.Date.PlusDays(-1) : local.Date;
        // A long line is shown on its first days only, so a mistyped year does not fill the plan.
        for (var day = first.PlusDays(1); day <= last && Period.DaysBetween(first, day) <= 31; day = day.PlusDays(1))
            yield return line with { Role = AgendaRole.Continued, Day = day, Steps = [] };
    }

    private static AgendaEntry StayLine(StayRun run, AgendaRole role, LocalDate day, DateTimeZone tripZone)
    {
        var source = role == AgendaRole.CheckIn ? run.CheckInStay : run.CheckOutStay;
        var value = role == AgendaRole.CheckIn ? source?.CheckIn : source?.CheckOut;
        var approximate = role == AgendaRole.CheckIn ? source?.CheckInApproximate ?? true : source?.CheckOutApproximate ?? true;
        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(value?.Timezone ?? run.Main.Zone) ?? tripZone;
        Instant? at = null;
        try { at = value?.ToInstant(); } catch (DomainException) { }
        // Without a time of its own, a check-in or check-out sits at the usual hour and is marked as approximate.
        at ??= zone.AtLeniently(day.At(role == AgendaRole.CheckIn ? Stays.DefaultCheckIn : Stays.DefaultCheckOut)).ToInstant();
        var item = (source ?? run.Main).Item;
        return new(item, role, ScheduleKind.Accommodation, approximate ? TimePrecision.Approximate : TimePrecision.Exact, at, null, zone.Id, null, day, []);
    }

    /// <summary>All-day lines first, then by start; lines with only a date come last.</summary>
    private static AgendaEntry[] Sort(IEnumerable<AgendaEntry> lines) => lines
        .OrderBy(l => l.Precision == TimePrecision.AllDay ? 0 : l.Start is null ? 2 : 1)
        .ThenBy(l => l.Start)
        .ThenBy(l => l.Role == AgendaRole.CheckOut ? 0 : 1)
        .ThenBy(l => l.Item.Title, StringComparer.CurrentCulture)
        .ToArray();
}
