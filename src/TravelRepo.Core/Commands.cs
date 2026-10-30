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

    /// <summary>
    /// Makes <paramref name="step"/> a step of <paramref name="group"/>, at the end of its children. A step belongs to one
    /// group, so it leaves the group it was in. A step without people of its own takes the group's. Returns the changed
    /// entities.
    /// </summary>
    public static IReadOnlyList<Entity> Nest(TripSnapshot trip, Guid group, Guid step)
    {
        var parent = trip.Find(group) ?? throw new DomainException("entity.missing", "Entity does not exist.");
        var child = trip.Find(step) ?? throw new DomainException("entity.missing", "Entity does not exist.");
        if (parent.Type != "schedule_item" || child.Type != "schedule_item") throw new DomainException("nest.type", "Only schedule items can be nested.");
        // The group itself or a group above it cannot become its step.
        for (var above = parent; above is not null; above = TripQueries.Parent(trip, above.Id))
            if (above.Id == step) throw new DomainException("nest.cycle", "Schedule nesting contains a cycle.");
        var changed = new List<Entity>();
        if (TripQueries.Parent(trip, step) is { } previous)
        {
            if (previous.Id == group) return [];
            changed.Add(WithoutChild(previous, step));
        }
        var copy = parent.Copy(); var children = copy.Data["children"] as JsonArray ?? new JsonArray(); copy.Data["children"] = children;
        children.Add(step.ToString()); changed.Add(copy);
        if (child.Data["participants"] is null)
        {
            var inherits = child.Copy(); inherits.Data["participants"] = new JsonObject { ["inherit"] = true, ["values"] = new JsonArray() };
            changed.Add(inherits);
        }
        return changed;
    }

    /// <summary>Takes <paramref name="step"/> out of its group; it stays in the plan on its own. Returns the changed group.</summary>
    public static IReadOnlyList<Entity> Unnest(TripSnapshot trip, Guid step) =>
        TripQueries.Parent(trip, step) is { } parent ? [WithoutChild(parent, step)] : [];

    private static Entity WithoutChild(Entity parent, Guid step)
    {
        var copy = parent.Copy();
        if (copy.Data["children"] is JsonArray children)
            foreach (var node in children.Where(c => c?.ToString() == step.ToString()).ToArray()) children.Remove(node);
        return copy;
    }

    /// <summary>
    /// Links a booking to exactly <paramref name="items"/>: the booking lists them, and each points back to it. An item
    /// that pointed to another booking leaves that booking; an item no longer listed stops pointing here. A stay keeps
    /// its booking in its accommodation, other items in <c>components.booking.ref</c>. Returns the changed entities.
    /// </summary>
    public static IReadOnlyList<Entity> LinkBooking(TripSnapshot trip, Guid booking, IReadOnlyCollection<Guid> items)
    {
        var target = trip.Find(booking) ?? throw new DomainException("entity.missing", "Entity does not exist.");
        if (target.Type != "booking") throw new DomainException("booking.type", "Only a booking can be linked to plans.");
        var changed = new Dictionary<Guid, Entity>();
        Entity Changing(Entity e) => changed.TryGetValue(e.Id, out var copy) ? copy : changed[e.Id] = e.Copy();
        var id = booking.ToString();
        foreach (var item in trip.Entities.Values.Where(e => e.Type == "schedule_item"))
        {
            var points = BookingOf(item);
            var wanted = items.Contains(item.Id);
            if (wanted && points != booking)
            {
                if (points is { } other && trip.Find(other) is { Type: "booking" } previous) RemoveItem(Changing(previous), item.Id);
                var copy = Changing(item);
                if (copy.Data["components"]?["accommodation"] is JsonObject stay && stay.ContainsKey("booking")) stay["booking"] = id;
                else
                {
                    if (copy.Data["components"] is not JsonObject components) copy.Data["components"] = components = new JsonObject();
                    components["booking"] = new JsonObject { ["ref"] = id };
                }
            }
            else if (!wanted && points == booking)
            {
                var copy = Changing(item);
                if (copy.Data["components"]?["accommodation"] is JsonObject stay && stay["booking"]?.ToString() == id) stay.Remove("booking");
                if (copy.Data["components"] is JsonObject components && components["booking"]?["ref"]?.ToString() == id) components.Remove("booking");
            }
        }
        var listed = (target.Data["items"] as JsonArray ?? []).Select(n => n?.ToString()).ToArray();
        var order = listed.Where(n => Guid.TryParse(n, out var g) && items.Contains(g)).Concat(items.Select(i => i.ToString())).Distinct().ToArray();
        if (!order.SequenceEqual(listed)) Changing(target).Data["items"] = new JsonArray(order.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
        return changed.Values.ToArray();
    }

    /// <summary>The booking a schedule item points to, through its booking component or its stay.</summary>
    public static Guid? BookingOf(Entity item) =>
        Guid.TryParse((item.Data["components"]?["booking"]?["ref"] ?? item.Data["components"]?["accommodation"]?["booking"])?.ToString(), out var id) ? id : null;

    private static void RemoveItem(Entity booking, Guid item)
    {
        if (booking.Data["items"] is JsonArray list)
            foreach (var node in list.Where(n => n?.ToString() == item.ToString()).ToArray()) list.Remove(node);
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
