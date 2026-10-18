using System.Globalization;
using System.Text.Json.Nodes;
using NodaTime;
using NodaTime.Text;

namespace TravelRepo.Core;

/// <summary>What is missing or inconsistent about a stay. Several can apply.</summary>
[Flags]
public enum StayIssues
{
    None = 0,
    /// <summary>The stay has a check-out but no check-in.</summary>
    MissingCheckIn = 1,
    /// <summary>The stay has a check-in but no check-out.</summary>
    MissingCheckOut = 2,
    /// <summary>The item's general time does not match check-in and check-out. Check-in and check-out win.</summary>
    TimeMismatch = 4
}

/// <summary>
/// A schedule item with an accommodation component, resolved to the nights it covers. Night <c>D</c> is the night
/// that starts on the evening of local date <c>D</c> at the stay. <see cref="Nights"/> is 0 for a day room (check-in
/// and check-out on the same day) and the stay has no <see cref="FirstNight"/> when it is not scheduled.
/// </summary>
public sealed record Stay(Entity Item, LocalDate? FirstNight, int Nights, ZonedTime? CheckIn, ZonedTime? CheckOut, string Zone, StayIssues Issues)
{
    public bool IsScheduled => FirstNight is not null;
    public LocalDate? LastNight => FirstNight is { } first && Nights > 0 ? first.PlusDays(Nights - 1) : null;
    /// <summary>The local date of the check-out morning.</summary>
    public LocalDate? DepartureDay => FirstNight?.PlusDays(Math.Max(Nights, 0));
    public bool Covers(LocalDate night) => FirstNight is { } first && night >= first && night < first.PlusDays(Nights);
    public IEnumerable<LocalDate> NightDates => FirstNight is { } first ? Enumerable.Range(0, Nights).Select(first.PlusDays) : [];
    public Guid? Place => Guid.TryParse(Item.Data["components"]?["accommodation"]?["place"]?.ToString(), out var id) ? id : null;
}

/// <summary>Stays that should be one stay over several nights, for example one item per night at the same place.</summary>
public sealed record StayMergeCandidate(IReadOnlyList<Stay> Stays, LocalDate FirstNight, int Nights)
{
    public Stay Keep => Stays[0];
}

/// <summary>Merging stays: the kept stay with its new values, and the stays it replaces.</summary>
public sealed record StayMerge(Entity Kept, IReadOnlyList<Guid> Removed, IReadOnlyList<Entity> Redirected);

/// <summary>
/// Rules for stays (schedule items with an accommodation component). Check-in and check-out are authoritative; the
/// item's <c>time</c> mirrors them so that clients which only read <c>time</c> keep working. All clients use these
/// rules instead of interpreting the fields themselves.
/// </summary>
public static class Stays
{
    /// <summary>Check-in time assumed when a stay's check-in has no time yet. A display default, not stored data.</summary>
    public static readonly LocalTime DefaultCheckIn = new(15, 0);
    /// <summary>Check-out time assumed when a stay's check-out has no time yet. A display default, not stored data.</summary>
    public static readonly LocalTime DefaultCheckOut = new(10, 0);

    public static bool IsStay(Entity item) => item.Type == "schedule_item" && item.Data["components"]?["accommodation"] is JsonObject;

    /// <summary>All stays of the trip, resolved, in order of their first night (unscheduled ones last).</summary>
    public static IReadOnlyList<Stay> All(TripSnapshot trip) =>
        trip.Entities.Values.Where(IsStay).Select(e => Resolve(trip, e)!).OrderBy(s => s.FirstNight ?? LocalDate.MaxIsoValue).ThenBy(s => s.Item.Title, StringComparer.Ordinal).ToArray();

    /// <summary>Resolves a stay, or returns <c>null</c> when the item is not a stay.</summary>
    public static Stay? Resolve(TripSnapshot trip, Entity item)
    {
        if (!IsStay(item)) return null;
        var accommodation = (JsonObject)item.Data["components"]!["accommodation"]!;
        var checkIn = Zoned(accommodation["check_in"]); var checkOut = Zoned(accommodation["check_out"]);
        var time = item.Data["time"] as JsonObject;
        var zone = checkIn?.Timezone ?? checkOut?.Timezone ?? time?["start"]?["timezone"]?.ToString() ?? time?["earliest"]?["timezone"]?.ToString() ?? time?["timezone"]?.ToString() ?? ScheduleQueries.TripZone(trip).Id;
        var (timeFirst, timeNights) = FromTime(time);
        var issues = StayIssues.None;
        LocalDate? first; int nights;
        if (checkIn is not null && checkOut is not null)
        {
            first = Day(checkIn); nights = Math.Max(0, Period.DaysBetween(Day(checkIn), Day(checkOut)));
            if (time is not null && (timeFirst != first || timeNights != nights || TimeDiffers(time, checkIn, checkOut))) issues |= StayIssues.TimeMismatch;
        }
        else if (checkIn is not null)
        {
            issues |= StayIssues.MissingCheckOut; first = Day(checkIn);
            var end = timeFirst is { } tf ? tf.PlusDays(timeNights) : (LocalDate?)null;
            nights = end is { } e && e > first ? Period.DaysBetween(first.Value, e) : 1;
        }
        else if (checkOut is not null)
        {
            issues |= StayIssues.MissingCheckIn; var departure = Day(checkOut);
            first = timeFirst is { } tf && tf < departure ? tf : departure.PlusDays(-1);
            nights = Period.DaysBetween(first.Value, departure);
        }
        else { first = timeFirst; nights = timeNights; }
        return new(item, first, nights, checkIn, checkOut, zone, issues);
    }

    /// <summary>The nights described by a general <c>time</c> value.</summary>
    private static (LocalDate? First, int Nights) FromTime(JsonObject? time)
    {
        if (time is null) return (null, 0);
        LocalDate? Local(string key) => time[key]?["local"]?.ToString() is { } text && LocalDateTimePattern.ExtendedIso.Parse(text).TryGetValue(default, out var value) ? value.Date : null;
        switch (time["precision"]?.ToString())
        {
            case "exact" or "approximate" or "window" or null:
                var start = Local("start") ?? Local("earliest"); var end = Local("end") ?? Local("latest");
                if (start is null) return (null, 0);
                return (start, end is { } e && e > start ? Period.DaysBetween(start.Value, e) : 1);
            case "day_part" or "all_day":
                return time["date"]?.ToString() is { } date && LocalDatePattern.Iso.Parse(date).TryGetValue(default, out var day) ? (day, 1) : (null, 0);
            default:
                return (null, 0);
        }
    }

    private static bool TimeDiffers(JsonObject time, ZonedTime checkIn, ZonedTime checkOut)
    {
        if (time["precision"]?.ToString() is not ("exact" or "approximate")) return false;
        var start = Zoned(time["start"]); var end = Zoned(time["end"]);
        return start is null || end is null || !SameMoment(start, checkIn) || !SameMoment(end, checkOut);
    }

    private static bool SameMoment(ZonedTime a, ZonedTime b)
    {
        try { return a.ToInstant() == b.ToInstant(); } catch (DomainException) { return a.Local == b.Local && a.Timezone == b.Timezone; }
    }

    private static ZonedTime? Zoned(JsonNode? node)
    {
        if (node is not JsonObject o || o["local"] is null || o["timezone"] is null) return null;
        try { return ZonedTime.From(o); } catch (Exception ex) when (ex is DomainException or InvalidOperationException) { return null; }
    }

    private static LocalDate Day(ZonedTime value) => LocalDateTimePattern.ExtendedIso.Parse(value.Local).TryGetValue(default, out var local) ? local.Date : throw new DomainException("time.local", "Invalid local date and time.");

    /// <summary>A local check-in or check-out on <paramref name="day"/> at <paramref name="time"/> in <paramref name="zone"/>.</summary>
    public static ZonedTime At(LocalDate day, LocalTime time, string zone) => new(day.At(time).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture), zone);

    /// <summary>
    /// Returns a copy of the stay with the given check-in and check-out, and its <c>time</c> set to the same span, so
    /// clients that only read <c>time</c> see the same stay.
    /// </summary>
    public static Entity WithSpan(Entity item, ZonedTime checkIn, ZonedTime checkOut)
    {
        if (!IsStay(item)) throw new DomainException("stay.type", "Only a stay has check-in and check-out.");
        if (checkOut.ToInstant() <= checkIn.ToInstant()) throw new DomainException("time.order", "Check-out must be after check-in.");
        var copy = item.Copy(); var accommodation = (JsonObject)copy.Data["components"]!["accommodation"]!;
        accommodation["check_in"] = checkIn.ToJson(); accommodation["check_out"] = checkOut.ToJson();
        copy.Data["time"] = new JsonObject { ["precision"] = "exact", ["start"] = checkIn.ToJson(), ["end"] = checkOut.ToJson() };
        return copy;
    }

    /// <summary>
    /// Returns a copy covering <paramref name="nights"/> nights from <paramref name="firstNight"/>, keeping the
    /// current check-in and check-out times or using the defaults.
    /// </summary>
    public static Entity WithNights(TripSnapshot trip, Entity item, LocalDate firstNight, int nights)
    {
        if (nights < 1) throw new DomainException("stay.nights", "A stay needs at least one night.");
        var stay = Resolve(trip, item) ?? throw new DomainException("stay.type", "Only a stay has nights.");
        var arrive = stay.CheckIn is { } ci ? TimeOf(ci) : DefaultCheckIn; var leave = stay.CheckOut is { } co ? TimeOf(co) : DefaultCheckOut;
        return WithSpan(item, At(firstNight, arrive, stay.Zone), At(firstNight.PlusDays(nights), leave, stay.Zone));
    }

    private static LocalTime TimeOf(ZonedTime value) => LocalDateTimePattern.ExtendedIso.Parse(value.Local).Value.TimeOfDay;

    /// <summary>
    /// Returns a copy whose check-in, check-out and <c>time</c> agree, filling a missing check-in or check-out from the
    /// resolved nights and the default times. Returns the item unchanged when it is not a scheduled stay.
    /// </summary>
    public static Entity Align(TripSnapshot trip, Entity item)
    {
        if (Resolve(trip, item) is not { FirstNight: { } first } stay) return item;
        if (stay.CheckIn is { } ci && stay.CheckOut is { } co && co.ToInstant() > ci.ToInstant()) return WithSpan(item, ci, co);
        return WithNights(trip, item, first, Math.Max(1, stay.Nights));
    }

    /// <summary>
    /// Nights between the trip's first and last day that have no stay and no other item running through most of the
    /// night (22:00 to 06:00), such as a night flight or a party. The night is measured in the trip's timezone and in
    /// the item's own departure and arrival timezones, so a night flight from a stopover counts where it starts.
    /// </summary>
    public static IReadOnlyList<LocalDate> MissingNights(TripSnapshot trip)
    {
        var (start, end) = TripQueries.EffectiveDates(trip);
        if (start is not { } from || end is not { } to || to <= from) return [];
        var stays = All(trip);
        var zone = ScheduleQueries.TripZone(trip);
        var others = trip.Entities.Values.Where(e => e.Type == "schedule_item" && !IsStay(e) && e.Data["children"] is not JsonArray { Count: > 0 })
            .Select(e => ScheduleQueries.Span(trip, e)).Where(s => s.IsPlaced && s.Precision is not TimePrecision.AllDay).ToArray();
        DateTimeZone[] Zones(ScheduleSpan span) => new[] { span.StartZone, span.EndZone }.Select(id => id is null ? null : DateTimeZoneProviders.Tzdb.GetZoneOrNull(id)).OfType<DateTimeZone>().Append(zone).Distinct().ToArray();
        bool ThroughNight(ScheduleSpan span, LocalDate night) => Zones(span).Any(z =>
            Overlap(span.Start!.Value, span.End!.Value, z.AtLeniently(night.At(new LocalTime(22, 0))).ToInstant(), z.AtLeniently(night.PlusDays(1).At(new LocalTime(6, 0))).ToInstant()) >= Duration.FromHours(4));
        var missing = new List<LocalDate>();
        for (var night = from; night < to; night = night.PlusDays(1))
        {
            if (stays.Any(s => s.Covers(night))) continue;
            if (others.Any(s => ThroughNight(s, night))) continue;
            missing.Add(night);
        }
        return missing;
    }

    private static Duration Overlap(Instant a, Instant b, Instant c, Instant d)
    {
        var start = a > c ? a : c; var end = b < d ? b : d;
        return end > start ? end - start : Duration.Zero;
    }

    /// <summary>
    /// Stays at the same place whose nights follow each other or overlap, for example one item per night. Places are
    /// compared by ID; stays without a place are compared by title.
    /// </summary>
    public static IReadOnlyList<StayMergeCandidate> MergeCandidates(TripSnapshot trip)
    {
        var result = new List<StayMergeCandidate>();
        var scheduled = All(trip).Where(s => s.FirstNight is not null && s.Nights > 0 && s.Item.Data["status"]?.ToString() != "cancelled").ToArray();
        foreach (var group in scheduled.GroupBy(s => s.Place?.ToString() ?? "title:" + s.Item.Title.Trim().ToLowerInvariant()))
        {
            var run = new List<Stay>(); LocalDate runEnd = default;
            void Flush() { if (run.Count > 1) result.Add(new(run.ToArray(), run[0].FirstNight!.Value, Period.DaysBetween(run[0].FirstNight!.Value, runEnd))); run.Clear(); }
            foreach (var stay in group.OrderBy(s => s.FirstNight).ThenByDescending(s => s.Nights))
            {
                var first = stay.FirstNight!.Value; var end = first.PlusDays(stay.Nights);
                if (run.Count > 0 && first > runEnd) Flush();
                if (run.Count == 0) runEnd = end;
                run.Add(stay); if (end > runEnd) runEnd = end;
            }
            Flush();
        }
        return result.OrderBy(c => c.FirstNight).ToArray();
    }

    private static readonly string[] StatusOrder = ["idea", "planned", "reserved", "confirmed", "completed"];

    /// <summary>
    /// Merges a candidate into its first stay: it covers all nights, keeps the earliest check-in and latest check-out
    /// times, the most advanced status, all content and people, and references to the other stays point to it.
    /// </summary>
    public static StayMerge Merge(TripSnapshot trip, StayMergeCandidate candidate)
    {
        var keep = candidate.Keep; var others = candidate.Stays.Skip(1).ToArray();
        var arrive = candidate.Stays.Where(s => s.CheckIn is not null && s.FirstNight == candidate.FirstNight).Select(s => TimeOf(s.CheckIn!)).DefaultIfEmpty(DefaultCheckIn).Min();
        var departure = candidate.FirstNight.PlusDays(candidate.Nights);
        var leave = candidate.Stays.Where(s => s.CheckOut is not null && s.DepartureDay == departure).Select(s => TimeOf(s.CheckOut!)).DefaultIfEmpty(DefaultCheckOut).Max();
        var merged = WithSpan(keep.Item, At(candidate.FirstNight, arrive, keep.Zone), At(departure, leave, keep.Zone));
        var statuses = candidate.Stays.Select(s => s.Item.Data["status"]?.ToString() ?? "idea").ToArray();
        merged.Data["status"] = statuses.Where(s => s != "cancelled").OrderByDescending(s => Array.IndexOf(StatusOrder, s)).FirstOrDefault() ?? "cancelled";
        void Union(string key)
        {
            var values = candidate.Stays.SelectMany(s => s.Item.Data[key] as JsonArray ?? []).Where(n => n is not null).ToList();
            var distinct = new JsonArray(); foreach (var value in values) if (!distinct.Any(d => JsonNode.DeepEquals(d, value))) distinct.Add(value!.DeepClone());
            if (distinct.Count > 0) merged.Data[key] = distinct;
        }
        Union("content"); Union("tags");
        foreach (var other in others.Select(s => s.Item))
        {
            // Fields only another night has, such as a booking link or people, are taken over.
            foreach (var (key, value) in other.Data)
                if (key is not ("id" or "time" or "status" or "title" or "content" or "tags") && merged.Data[key] is null && value is not null) merged.Data[key] = value.DeepClone();
            if (other.Data["components"] is JsonObject components)
                foreach (var (key, value) in components)
                    if (merged.Data["components"]![key] is null && value is not null) merged.Data["components"]![key] = value.DeepClone();
                    else if (key == "accommodation" && value is JsonObject accommodation)
                        foreach (var (field, fieldValue) in accommodation)
                            if (field is not ("check_in" or "check_out") && merged.Data["components"]!["accommodation"]![field] is null && fieldValue is not null) merged.Data["components"]!["accommodation"]![field] = fieldValue.DeepClone();
        }
        var removed = others.Select(s => s.Item.Id).ToHashSet();
        var redirected = trip.All.Where(e => !removed.Contains(e.Id) && e.Id != keep.Item.Id && TripReferences.From(e).Any(r => removed.Contains(r.Target)))
            .Select(e => TripReferences.Redirect(e, removed, keep.Item.Id)).ToArray();
        return new(merged, removed.ToArray(), redirected);
    }
}
