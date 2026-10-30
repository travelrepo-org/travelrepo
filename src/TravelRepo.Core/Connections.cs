using NodaTime;

namespace TravelRepo.Core;

/// <summary>
/// A change between two journeys: <see cref="Arriving"/> ends where <see cref="Departing"/> starts, soon enough to be one
/// way, such as a layover between two flights. <see cref="Place"/> is where the change happens, when known.
/// </summary>
public sealed record Connection(Entity Arriving, Entity Departing, Instant Arrival, Instant Departure, Entity? Place)
{
    /// <summary>How long the traveller has between arriving and leaving again.</summary>
    public Duration Layover => Departure - Arrival;
}

/// <summary>Finds connections between the journeys of a trip.</summary>
public static class Connections
{
    /// <summary>The longest gap that still counts as a connection, such as an overnight layover.</summary>
    public static readonly Duration Longest = Duration.FromHours(24);

    /// <summary>
    /// Places this far apart still count as one place to change at, such as two airports of a city or a station
    /// next to the airport.
    /// </summary>
    public const double SamePlaceMetres = 100_000;

    /// <summary>
    /// The connections in a trip, in time order. Two journeys connect when the second is the next to leave after the
    /// first arrives, within <see cref="Longest"/>, from the place the first arrives at (the same place, or one within
    /// <see cref="SamePlaceMetres"/>). Cancelled journeys and journeys without a time do not count.
    /// </summary>
    public static IReadOnlyList<Connection> In(TripSnapshot trip)
    {
        var journeys = trip.Entities.Values
            .Where(e => e.Type == "schedule_item" && e.Data["components"]?["transport"] is not null && e.Data["status"]?.ToString() != "cancelled"
                && e.Data["children"] is not System.Text.Json.Nodes.JsonArray { Count: > 0 })
            .Select(e => (Item: e, Span: ScheduleQueries.Span(trip, e)))
            .Where(j => j.Span.IsPlaced && j.Span.Precision is not (TimePrecision.AllDay or TimePrecision.DayPart))
            .OrderBy(j => j.Span.Start).ToArray();
        var found = new List<Connection>();
        for (var i = 0; i < journeys.Length; i++)
        {
            var (arriving, span) = journeys[i];
            var next = journeys.Skip(i + 1).FirstOrDefault(j => j.Span.Start >= span.End);
            if (next.Item is null || next.Span.Start - span.End > Longest) continue;
            var at = ScheduleQueries.Route(trip, arriving).To; var from = ScheduleQueries.Route(trip, next.Item).From;
            if (!SamePlace(at, from)) continue;
            found.Add(new(arriving, next.Item, span.End!.Value, next.Span.Start!.Value, at ?? from));
        }
        return found;
    }

    private static bool SamePlace(Entity? a, Entity? b)
    {
        if (a is null || b is null) return false;
        if (a.Id == b.Id) return true;
        return Places.Location(a) is { } x && Places.Location(b) is { } y && Places.Distance(x, y) <= SamePlaceMetres;
    }
}
