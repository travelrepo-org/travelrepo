using System.Text.Json.Nodes;
using TravelRepo.Core;
using TravelRepo.Serialization;

namespace TravelRepo.Repository;

/// <summary>What deleting entities changes: the entities removed, the entities updated to drop references, and files freed.</summary>
public sealed record DeletionPlan(
    IReadOnlyList<Guid> Requested,
    IReadOnlyList<Entity> Deleted,
    IReadOnlyList<Entity> Updated,
    IReadOnlyList<string> RemovedResources,
    IReadOnlyList<Entity> Unused)
{
    /// <summary>Entities that are deleted because they cannot exist without a requested one, such as comments on it.</summary>
    public IEnumerable<Entity> Dependents => Deleted.Where(e => !Requested.Contains(e.Id));
    /// <summary>The entity edits for <see cref="TravelRepository.ApplyAsync"/>: deletions first, then updates.</summary>
    public EntityEdit[] Edits => Deleted.Select(e => new EntityEdit(e.Id, null)).Concat(Updated.Select(e => new EntityEdit(e.Id, e))).ToArray();
    /// <summary>The resource edits that remove files nothing uses any more.</summary>
    public ResourceEdit[] ResourceEdits => RemovedResources.Select(p => new ResourceEdit(p, null)).ToArray();
}

public static class TripDeletion
{
    /// <summary>Entity types offered for deletion together with the last entity that used them.</summary>
    public static readonly IReadOnlySet<string> OwnedTypes = new HashSet<string> { "place", "document", "note", "booking" };

    /// <summary>
    /// Plans deleting <paramref name="ids"/>. References from other entities are removed. An entity that becomes invalid
    /// without the reference, such as a comment on a deleted item, is deleted too. <see cref="DeletionPlan.Unused"/>
    /// lists places, documents, notes and bookings that only the deleted entities used; they are not deleted unless the
    /// caller plans again with their IDs included. Markdown notes and document blobs that nothing uses any more are removed.
    /// </summary>
    public static DeletionPlan Plan(TripSnapshot trip, IEnumerable<Guid> ids)
    {
        var requested = ids.Distinct().ToArray();
        foreach (var id in requested)
        {
            var entity = trip.Find(id) ?? throw new DomainException("entity.missing", "Entity not found.");
            if (entity.Type == "trip") throw new DomainException("manifest.delete", "Cannot delete the manifest.");
        }
        var deleted = requested.ToHashSet();
        var updated = new Dictionary<Guid, Entity>();
        // Detaching can make an entity invalid, which deletes it and may in turn detach others.
        while (true)
        {
            updated.Clear(); var more = false;
            foreach (var entity in trip.All.Where(e => !deleted.Contains(e.Id)))
            {
                if (!From(entity).Any(r => deleted.Contains(r.Target))) continue;
                var copy = TripReferences.Detach(entity, deleted);
                if (entity.Type != "trip" && SchemaValidation.Validate(copy).Count > 0) { deleted.Add(entity.Id); more = true; continue; }
                updated[entity.Id] = copy;
            }
            if (!more) break;
        }
        var remaining = trip.All.Where(e => !deleted.Contains(e.Id)).Select(e => updated.GetValueOrDefault(e.Id) ?? e).ToArray();
        var stillUsed = remaining.SelectMany(e => TripReferences.From(e)).Select(r => r.Target).ToHashSet();
        var removedEntities = deleted.Select(id => trip.Find(id)!).ToArray();
        var unused = removedEntities.SelectMany(e => From(e)).Select(r => r.Target).Distinct()
            .Where(id => !deleted.Contains(id) && !stillUsed.Contains(id)).Select(trip.Find).OfType<Entity>()
            .Where(e => OwnedTypes.Contains(e.Type)).OrderBy(e => e.Type, StringComparer.Ordinal).ThenBy(e => e.Title, StringComparer.CurrentCulture).ToArray();
        var remainingText = remaining.Select(e => e.Data.ToJsonString()).ToArray();
        var resources = new List<string>();
        foreach (var e in removedEntities)
        {
            foreach (var file in (e.Data["content"] as JsonArray ?? []).OfType<JsonObject>().Select(c => c["file"]?.ToString()).OfType<string>())
                if (trip.Resources.ContainsKey(file) && !remainingText.Any(t => t.Contains(file, StringComparison.Ordinal)) && !resources.Contains(file)) resources.Add(file);
            if (e.Type == "document" && e.Data["blob"]?["hash"]?.ToString() is { Length: 64 } hash)
            {
                var path = "assets/sha256/" + hash[..2] + "/" + hash;
                if (trip.Resources.ContainsKey(path) && !remaining.Any(r => r.Data["blob"]?["hash"]?.ToString() == hash) && !resources.Contains(path)) resources.Add(path);
            }
        }
        return new(requested, removedEntities, updated.Values.ToArray(), resources, unused);
    }

    private static IEnumerable<EntityReference> From(Entity e) => TripReferences.From(e, includeExtensions: false);
}
