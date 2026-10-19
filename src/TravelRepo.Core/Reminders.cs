using System.Text.Json.Nodes;
using NodaTime;
using NodaTime.Text;

namespace TravelRepo.Core;

public enum ReminderKind
{
    /// <summary>A stay or booking can be cancelled free of charge only until a deadline.</summary>
    CancellationDeadline,
    /// <summary>A task is due today, tomorrow or overdue.</summary>
    TaskDue,
    /// <summary>The planned arrival, or a journey that ends that day, is after the accommodation's check-in hours.</summary>
    LateArrival,
    /// <summary>Nights between the trip's first and last day have no place to stay.</summary>
    NightsWithoutStay,
    /// <summary>A booking is not confirmed although the trip or the booked item is close.</summary>
    UnconfirmedBooking,
    /// <summary>
    /// Variants were deleted on the remote while copies remain here. Clients create this reminder from the Git state;
    /// <see cref="TripReminders.For"/> never returns it, because it is not trip data.
    /// </summary>
    VariantsDeletedOnline,
    /// <summary>
    /// The forecast for an item in the next days brings weather that matters for it, such as rain on a hike or a storm
    /// on a ferry crossing. Weather services create this reminder (see TravelRepo.Providers.Weather);
    /// <see cref="TripReminders.For"/> never returns it, because forecasts are not trip data.
    /// </summary>
    WeatherWarning
}

/// <summary>
/// Something the travellers should look at, derived from the trip at a given moment. <see cref="Key"/> stays the same
/// while the underlying data does, so a client can remember locally that a reminder was dismissed or already shown.
/// <see cref="Moment"/> is the deadline or event the reminder is about, when it has one.
/// </summary>
public sealed record Reminder(string Key, ReminderKind Kind, Guid? Entity, string Title, Instant? Moment, bool Urgent, IReadOnlyList<LocalDate> Dates);

/// <summary>Reminders for a trip. They are derived from canonical data and never stored in the repository.</summary>
public static class TripReminders
{
    /// <summary>How long before a deadline a cancellation reminder appears.</summary>
    public static readonly Duration CancellationLead = Duration.FromDays(14);
    /// <summary>How long before the trip or the booked item an unconfirmed booking is mentioned.</summary>
    public static readonly Duration ConfirmationLead = Duration.FromDays(14);

    public static IReadOnlyList<Reminder> For(TripSnapshot trip, Instant now)
    {
        var result = new List<Reminder>();
        var zone = ScheduleQueries.TripZone(trip);
        var today = now.InZone(zone).Date;

        // Free cancellation deadlines of stays and bookings that are still active.
        foreach (var entity in trip.Entities.Values)
        {
            if (entity.Data["status"]?.ToString() is "cancelled" or "completed") continue;
            var free = entity.Type == "booking" ? Stays.Zoned(entity.Data["cancellation"]?["free_until"]) : Stays.IsStay(entity) ? Stays.Zoned(entity.Data["components"]?["accommodation"]?["cancellation"]?["free_until"]) : null;
            if (free is null) continue;
            Instant deadline; try { deadline = free.ToInstant(); } catch (DomainException) { continue; }
            if (now < deadline - CancellationLead || now > deadline + Duration.FromDays(1)) continue;
            result.Add(new("cancellation:" + entity.Id + ":" + free.Local, ReminderKind.CancellationDeadline, entity.Id, entity.Title, deadline, deadline - now <= Duration.FromDays(2), []));
        }

        // Open tasks due today, tomorrow or earlier.
        foreach (var task in trip.Entities.Values.Where(e => e.Type == "task" && e.Data["status"]?.ToString() is not ("completed" or "cancelled")))
        {
            if (task.Data["due"]?["date"]?.ToString() is not { } text || !LocalDatePattern.Iso.Parse(text).TryGetValue(default, out var due) || due > today.PlusDays(1)) continue;
            result.Add(new("task:" + task.Id + ":" + text, ReminderKind.TaskDue, task.Id, task.Title, zone.AtStartOfDay(due).ToInstant(), due <= today, [due]));
        }

        // Arrivals after the accommodation's check-in hours: the planned check-in, or the last journey that ends on the arrival day.
        foreach (var stay in Stays.All(trip).Where(s => s.FirstNight is not null && s.CheckInWindow?.Until is not null && s.Item.Data["status"]?.ToString() != "cancelled"))
        {
            var until = stay.CheckInWindow!.Until!.Value; var day = stay.FirstNight!.Value;
            var stayZone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(stay.Zone) ?? zone;
            LocalDateTime? arrival = stay.CheckIn is { } ci && !stay.CheckInApproximate && NodaTime.Text.LocalDateTimePattern.ExtendedIso.Parse(ci.Local).TryGetValue(default, out var planned) ? planned : null;
            var journey = trip.Entities.Values.Where(e => e.Data["components"]?["transport"] is JsonObject).Select(e => ScheduleQueries.Span(trip, e)).Where(s => s.End is not null)
                .Select(s => s.End!.Value.InZone(stayZone).LocalDateTime).Where(end => end.Date == day).DefaultIfEmpty().Max();
            var latest = new[] { arrival, journey == default ? null : journey }.Where(t => t is not null && t.Value.Date == day).Max();
            if (latest is not { } at || at.TimeOfDay <= until) continue;
            var moment = stayZone.AtLeniently(at).ToInstant();
            if (moment < now) continue;
            result.Add(new("late:" + stay.Item.Id + ":" + LocalDateTimePattern.ExtendedIso.Format(at), ReminderKind.LateArrival, stay.Item.Id, stay.Item.Title, moment, moment - now <= Duration.FromDays(3), [day]));
        }

        // Nights without a stay, as one reminder.
        var missing = Stays.MissingNights(trip).Where(n => n >= today).ToArray();
        if (missing.Length > 0)
            result.Add(new("nights:" + string.Join(",", missing.Select(d => LocalDatePattern.Iso.Format(d))), ReminderKind.NightsWithoutStay, null, trip.Manifest.Title, zone.AtStartOfDay(missing[0]).ToInstant(), missing[0] <= today.PlusDays(7), missing));

        // Bookings still pending shortly before the trip or the booked item.
        var (tripStart, _) = TripQueries.EffectiveDates(trip);
        foreach (var booking in trip.Entities.Values.Where(e => e.Type == "booking" && e.Data["status"]?.ToString() is null or "idea" or "pending"))
        {
            var items = (booking.Data["items"] as JsonArray ?? []).Select(n => Guid.TryParse(n?.ToString(), out var id) ? trip.Find(id) : null).OfType<Entity>();
            var start = items.Select(i => ScheduleQueries.Span(trip, i).Start).Where(s => s is not null).DefaultIfEmpty(tripStart is { } d ? zone.AtStartOfDay(d).ToInstant() : null).Min();
            if (start is not { } begins || begins < now || begins - now > ConfirmationLead) continue;
            result.Add(new("unconfirmed:" + booking.Id, ReminderKind.UnconfirmedBooking, booking.Id, booking.Title, begins, begins - now <= Duration.FromDays(3), []));
        }

        return result.OrderByDescending(r => r.Urgent).ThenBy(r => r.Moment ?? Instant.MaxValue).ToArray();
    }
}
