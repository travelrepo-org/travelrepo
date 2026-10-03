using System.Text.Json.Nodes;
using TravelRepo.Core;

namespace TravelRepo.Merge;

public enum ChangeKind { Added, Updated, Removed }

/// <summary>One changed entity. <see cref="Fields"/> lists changed top-level fields, with component names expanded.</summary>
public sealed record EntityChange(Guid Entity, string Type, string Title, ChangeKind Kind, IReadOnlyList<string> Fields);

/// <summary>A changed repository resource that no entity's content references.</summary>
public sealed record ResourceChange(string Path, ChangeKind Kind);

/// <summary>
/// A human-oriented summary of a semantic diff. Clients localize and present it; the structure is
/// independent of any UI. Markdown content changes are attributed to the entity that references the file.
/// </summary>
public sealed record ChangeSummary(IReadOnlyList<EntityChange> Entities, IReadOnlyList<ResourceChange> Resources)
{
    public bool IsEmpty => Entities.Count == 0 && Resources.Count == 0;
    public int Count => Entities.Count + Resources.Count;

    public static ChangeSummary Between(TripSnapshot before, TripSnapshot after)
    {
        var diff = SemanticMerge.Diff(before, after);
        var byEntity = new Dictionary<Guid, (ChangeKind Kind, SortedSet<string> Fields)>();
        var resources = new List<ResourceChange>();
        foreach (var change in diff)
        {
            if (change.Entity == Guid.Empty)
            {
                var path = change.Path["/resources/".Length..];
                var kind = change.Before is null ? ChangeKind.Added : change.After is null ? ChangeKind.Removed : ChangeKind.Updated;
                var owner = Owner(after, path) ?? Owner(before, path);
                if (owner is not null) Note(owner.Value, ChangeKind.Updated, "content");
                else if (!path.StartsWith("assets/", StringComparison.Ordinal)) resources.Add(new(path, kind));
                continue;
            }
            var existedBefore = before.Find(change.Entity) is not null; var existsAfter = after.Find(change.Entity) is not null;
            var entityKind = !existedBefore ? ChangeKind.Added : !existsAfter ? ChangeKind.Removed : ChangeKind.Updated;
            Note(change.Entity, entityKind, FieldName(change.Path));
        }
        void Note(Guid id, ChangeKind kind, string? field)
        {
            if (!byEntity.TryGetValue(id, out var entry)) byEntity[id] = entry = (kind, new SortedSet<string>(StringComparer.Ordinal));
            if (kind != ChangeKind.Updated && entry.Kind == ChangeKind.Updated) byEntity[id] = entry = (kind, entry.Fields);
            if (field is { Length: > 0 } && entry.Kind == ChangeKind.Updated) entry.Fields.Add(field);
        }
        var entities = byEntity.Select(p =>
        {
            var entity = after.Find(p.Key) ?? before.Find(p.Key)!;
            return new EntityChange(p.Key, entity.Type, entity.Title, p.Value.Kind, p.Value.Fields.ToArray());
        })
        .Where(c => !(c.Type == "trip" && c.Kind == ChangeKind.Updated && c.Fields.Count == 0))
        .OrderBy(c => c.Kind).ThenBy(c => Order(c.Type)).ThenBy(c => c.Title, StringComparer.CurrentCulture).ToArray();
        return new(entities, resources);
    }

    private static string? FieldName(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        return parts[0] == "components" && parts.Length > 1 ? "components/" + parts[1] : parts[0];
    }

    private static Guid? Owner(TripSnapshot trip, string resource)
    {
        foreach (var e in trip.All)
            if (e.Data["content"] is JsonArray content && content.Any(c => c?["file"]?.ToString() == resource)) return e.Id;
        return null;
    }

    private static int Order(string type) => type switch
    {
        "trip" => 0, "schedule_item" => 1, "booking" => 2, "place" => 3, "person" => 4, "task" => 5, "expense" => 6, "budget" => 7, "document" => 8, "note" => 9, "comment" => 10, "collection" => 11, _ => 12
    };
}
