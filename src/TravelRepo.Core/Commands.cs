using System.Text.Json.Nodes;
namespace TravelRepo.Core;

/// <summary>Pure domain commands that produce edits for the repository's validated transaction boundary.</summary>
public static class TripCommands
{
    /// <summary>
    /// Returns a copy of <paramref name="owner"/> with the document attached as a content entry: an image or PDF entry
    /// for those types, a reference otherwise. Attaching the same document twice keeps one entry.
    /// </summary>
    public static Entity AttachDocument(Entity owner, Entity document)
    {
        if (document.Type != "document") throw new DomainException("document.type", "Only a document can be attached.");
        var copy = owner.Copy(); var content = copy.Data["content"] as JsonArray ?? new JsonArray(); copy.Data["content"] = content;
        var id = document.Id.ToString();
        if (!content.OfType<JsonObject>().Any(c => c["document"]?.ToString() == id || c["entity"]?.ToString() == id)) content.Add(ContentEntry(document));
        return copy;
    }

    /// <summary>The content entry that shows a document: <c>image</c>, <c>pdf</c> or a <c>reference</c> for other files.</summary>
    public static JsonObject ContentEntry(Entity document)
    {
        var media = document.Data["media_type"]?.ToString() ?? "";
        return media.StartsWith("image/", StringComparison.Ordinal) ? new JsonObject { ["type"] = "image", ["document"] = document.Id.ToString() }
            : media == "application/pdf" ? new JsonObject { ["type"] = "pdf", ["document"] = document.Id.ToString() }
            : new JsonObject { ["type"] = "reference", ["entity"] = document.Id.ToString() };
    }

    /// <summary>Documents attached to an entity: through content entries, a booking's <c>documents</c> or a stay's documents.</summary>
    public static IReadOnlyList<Entity> AttachedDocuments(TripSnapshot trip, Entity owner)
    {
        var ids = (owner.Data["content"] as JsonArray ?? []).OfType<JsonObject>().Select(c => (c["document"] ?? c["entity"])?.ToString())
            .Concat((owner.Data["documents"] as JsonArray ?? []).Select(n => n?.ToString()))
            .Concat((owner.Data["components"]?["accommodation"]?["documents"] as JsonArray ?? []).Select(n => n?.ToString()));
        return ids.Select(id => Guid.TryParse(id, out var g) ? trip.Find(g) : null).OfType<Entity>().Where(e => e.Type == "document").DistinctBy(e => e.Id).ToArray();
    }

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
