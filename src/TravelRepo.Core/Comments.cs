using NodaTime;
using NodaTime.Text;

namespace TravelRepo.Core;

/// <summary>Comments on trip entities. v1 comments are linear, so they are read oldest first.</summary>
public static class TripComments
{
    /// <summary>The comments on one entity, oldest first.</summary>
    public static IReadOnlyList<Entity> On(TripSnapshot trip, Guid target) => [.. Sorted(trip.Entities.Values.Where(e => e.Type == "comment" && Target(e) == target))];

    /// <summary>Every comment by the entity it is on, oldest first, for callers that look up many entities.</summary>
    public static ILookup<Guid, Entity> ByTarget(TripSnapshot trip) =>
        Sorted(trip.Entities.Values.Where(e => e.Type == "comment" && Target(e) is not null)).ToLookup(e => Target(e)!.Value);

    /// <summary>The entity a comment is on, or null when its target is missing.</summary>
    public static Guid? Target(Entity comment) => Guid.TryParse(comment.Data["target"]?["id"]?.ToString(), out var id) ? id : null;

    /// <summary>The person who wrote the comment, when it names one that is in the trip.</summary>
    public static Entity? Author(TripSnapshot trip, Entity comment) => Guid.TryParse(comment.Data["author"]?.ToString(), out var id) ? trip.Find(id) : null;

    public static Instant? CreatedAt(Entity comment) =>
        comment.Data["created_at"]?.ToString() is { } text && InstantPattern.ExtendedIso.Parse(text) is { Success: true } r ? r.Value
        : DateTimeOffset.TryParse(comment.Data["created_at"]?.ToString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at) ? Instant.FromDateTimeOffset(at) : null;

    private static IEnumerable<Entity> Sorted(IEnumerable<Entity> comments) => comments.OrderBy(c => CreatedAt(c) ?? Instant.MaxValue).ThenBy(c => c.Id);
}
