using NodaTime;
using TravelRepo.Core;
namespace TravelRepo.Repository;

/// <summary>What a calendar entry stands for: a plan, or a stay's arrival or departure.</summary>
public enum CalendarEntryRole { Plan, CheckIn, CheckOut }

/// <summary>
/// One entry of a trip in a calendar. A timed entry has <see cref="Start"/> and <see cref="End"/>, which are equal for a
/// single moment such as a fixed check-in; an all-day entry has only <see cref="Date"/>. The title is the item's own;
/// a client words check-in and check-out in its language. <see cref="Uid"/> stays the same across versions: the
/// item's id, with "-check-in" or "-check-out" for a stay. <see cref="Zone"/> is the IANA zone the entry starts in,
/// <see cref="Status"/> the item's status, such as "idea" or "confirmed".
/// </summary>
public sealed record CalendarEntry(string Uid, Entity Item, CalendarEntryRole Role, ScheduleKind Kind, Instant? Start, Instant? End, LocalDate? Date, string? Zone, string? Location, string? Description, string? Status)
{
    public bool AllDay => Start is null;
}

/// <summary>A trip as calendar entries, for iCalendar files and for calendars on a device.</summary>
public static class TripCalendar
{
    /// <summary>
    /// The trip's placed plans as calendar entries; nested parents are left out, their steps come one by one.
    /// </summary>
    /// <param name="trip">The trip.</param>
    /// <param name="include">Which items to take, such as an export's date range; all when null.</param>
    /// <param name="staysAsCheckInOut">
    /// When true, a stay becomes its check-in and its check-out instead of one entry across its nights: each a single
    /// moment at a fixed time, a block for a time window, or all day when the time is not known.
    /// </param>
    public static IReadOnlyList<CalendarEntry> Entries(TripSnapshot trip, Func<Entity, bool>? include = null, bool staysAsCheckInOut = false)
    {
        include ??= _ => true;
        var entries = new List<CalendarEntry>();
        foreach (var e in trip.Entities.Values.Where(e => e.Type == "schedule_item" && e.Data["children"] is not System.Text.Json.Nodes.JsonArray { Count: > 0 } && include(e)))
        {
            if (staysAsCheckInOut && Stays.IsStay(e)) continue;
            var span = ScheduleQueries.Span(trip, e); if (!span.IsPlaced || span.Derived) continue;
            var allDay = span.Precision == TimePrecision.AllDay && span.Date is not null;
            entries.Add(new(e.Id.ToString(), e, CalendarEntryRole.Plan, ScheduleCategories.Classify(trip, e), allDay ? null : span.Start, allDay ? null : span.End, allDay ? span.Date : null,
                span.StartZone, Location(trip, e), Description(trip, e), e.Data["status"]?.ToString()));
        }
        if (!staysAsCheckInOut) return entries;
        foreach (var stay in Stays.All(trip).Where(s => s.FirstNight is not null && include(s.Item)))
        {
            var place = Location(trip, stay.Item); var description = Description(trip, stay.Item); var status = stay.Item.Data["status"]?.ToString();
            var (inStart, inEnd, inDate) = Moment(stay.CheckIn, stay.CheckInApproximate, stay.CheckInWindow, stay.FirstNight!.Value, stay.Zone, arrival: true);
            entries.Add(new(stay.Item.Id + "-check-in", stay.Item, CalendarEntryRole.CheckIn, ScheduleKind.Accommodation, inStart, inEnd, inDate, stay.Zone, place, description, status));
            var (outStart, outEnd, outDate) = Moment(stay.CheckOut, stay.CheckOutApproximate, stay.CheckOutWindow, stay.DepartureDay!.Value, stay.Zone, arrival: false);
            entries.Add(new(stay.Item.Id + "-check-out", stay.Item, CalendarEntryRole.CheckOut, ScheduleKind.Accommodation, outStart, outEnd, outDate, stay.Zone, place, description, status));
        }
        return entries;
    }

    /// <summary>
    /// When a check-in or check-out happens: a fixed time as one moment; otherwise the hours of its window as a block,
    /// or one end of the window (the earliest arrival, the latest departure) as a moment; otherwise all day. An
    /// approximate time is a guess, so without a window it counts as no time.
    /// </summary>
    private static (Instant? Start, Instant? End, LocalDate? Date) Moment(ZonedTime? at, bool approximate, StayWindow? window, LocalDate day, string zone, bool arrival)
    {
        if (at is not null && !approximate)
        {
            try { var instant = at.ToInstant(); return (instant, instant, null); }
            catch (DomainException) { }
        }
        if (DateTimeZoneProviders.Tzdb.GetZoneOrNull(zone) is { } tz && window is { } w)
        {
            Instant On(LocalTime time) => tz.AtLeniently(day.At(time)).ToInstant();
            if (w.From is { } from && w.Until is { } until && until > from) return (On(from), On(until), null);
            if ((arrival ? w.From ?? w.Until : w.Until ?? w.From) is { } one) return (On(one), On(one), null);
        }
        return (null, null, day);
    }

    private static string? Location(TripSnapshot trip, Entity e)
    {
        var route = ScheduleQueries.Route(trip, e); var place = ScheduleQueries.PrimaryPlace(trip, e);
        return route.From is not null && route.To is not null ? route.From.Title + " → " + route.To.Title
            : place is null ? null : place.Title + (place.Data["address"]?["formatted"]?.ToString() is { Length: > 0 } address ? ", " + address : "");
    }

    private static string? Description(TripSnapshot trip, Entity e)
    {
        var people = string.Join(", ", ScheduleQueries.Participants(trip, e).Select(p => p.Title));
        var booking = Guid.TryParse(e.Data["components"]?["booking"]?["ref"]?.ToString(), out var bookingId) ? trip.Find(bookingId)?.Data["reference"]?.ToString() : null;
        var description = string.Join("\n", new[] { people, booking is null ? null : "Ref. " + booking, e.Data["status"]?.ToString() }.Where(x => !string.IsNullOrEmpty(x)));
        return description.Length > 0 ? description : null;
    }
}
