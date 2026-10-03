using System.Text.Json.Nodes;
namespace TravelRepo.Core;

/// <summary>Pure domain commands that produce edits for the repository's validated transaction boundary.</summary>
public static class TripCommands
{
    public static IReadOnlyList<Entity> DuplicateSubtree(TripSnapshot trip, Guid root)
    {
        var ids = new Dictionary<Guid, Guid>();
        void Visit(Guid id)
        {
            if (ids.ContainsKey(id)) return; var e = trip.Find(id) ?? throw new DomainException("entity.missing", "Entity does not exist.");
            if (e.Type == "trip") throw new DomainException("trip.duplicate", "A trip manifest cannot be duplicated inside the same repository.");
            ids[id] = Guid.CreateVersion7(); if (e.Data["children"] is JsonArray children) foreach (var c in children) if (Guid.TryParse(c?.ToString(), out var child)) Visit(child);
        }
        Visit(root);
        JsonNode? Rewrite(JsonNode? node)
        {
            if (node is JsonObject obj) return new JsonObject(obj.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, p.Key == "extensions" ? p.Value?.DeepClone() : Rewrite(p.Value))));
            if (node is JsonArray array) return new JsonArray(array.Select(Rewrite).ToArray());
            if (node is JsonValue value && value.TryGetValue<string>(out var text) && Guid.TryParse(text, out var id) && ids.TryGetValue(id, out var replacement)) return JsonValue.Create(replacement.ToString());
            return node?.DeepClone();
        }
        return ids.Keys.Select(id => new Entity((JsonObject)Rewrite(trip.Find(id)!.Data)!)).ToArray();
    }
}
