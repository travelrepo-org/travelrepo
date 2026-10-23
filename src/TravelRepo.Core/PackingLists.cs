using System.Text.Json.Nodes;

namespace TravelRepo.Core;

/// <summary>
/// One thing to bring. <see cref="Assignees"/> are the people who bring it: the assigned people, or every traveller
/// when <see cref="Everyone"/> is set, which includes people who join the trip later. With <see cref="Everyone"/>
/// each person brings their own; assigned people share the item, so one of them packing it is enough.
/// </summary>
public sealed record PackingItem(Entity Task, string? Group, string? Quantity, bool Everyone, IReadOnlyList<Entity> Assignees, IReadOnlyList<Guid> PackedBy)
{
    public bool Unassigned => !Everyone && Assignees.Count == 0;
    public bool IsPackedBy(Guid person) => PackedBy.Contains(person);
    public bool IsFor(Guid person) => Assignees.Any(a => a.Id == person);
    /// <summary>Everyone has packed their own, or one of the people sharing it has packed it.</summary>
    public bool Done => Assignees.Count > 0 && (Everyone ? Assignees.All(a => IsPackedBy(a.Id)) : Assignees.Any(a => IsPackedBy(a.Id)));
}

/// <summary>A packing list with its items in order.</summary>
public sealed record PackingList(Entity Collection, IReadOnlyList<PackingItem> Items)
{
    public int DoneCount => Items.Count(i => i.Done);
}

/// <summary>
/// Packing lists (specification section 37.1): a collection with <c>purpose: packing</c> whose items are tasks with
/// a <c>packing</c> component. Using the existing types keeps the format at 1.0 and lets older clients show the list
/// as a collection of tasks. Who has packed an item is a set of person IDs, so two people packing at the same time
/// merge without a conflict.
/// </summary>
public static class PackingLists
{
    public const string Purpose = "packing";

    public static bool IsList(Entity e) => e.Type == "collection" && e.Data["purpose"]?.ToString() == Purpose;
    public static bool IsItem(Entity e) => e.Type == "task" && e.Data["components"]?["packing"] is JsonObject;

    public static IReadOnlyList<PackingList> All(TripSnapshot trip) =>
        trip.Entities.Values.Where(IsList).OrderBy(e => e.Title, StringComparer.CurrentCulture).Select(l => Resolve(trip, l)).ToArray();

    public static PackingList Resolve(TripSnapshot trip, Entity list) =>
        new(list, (list.Data["items"] as JsonArray ?? []).Select(n => Guid.TryParse(n?.ToString(), out var id) ? trip.Find(id) : null).OfType<Entity>().Where(IsItem).Select(t => Item(trip, t)).ToArray());

    public static PackingItem Item(TripSnapshot trip, Entity task)
    {
        var packing = task.Data["components"]?["packing"];
        var everyone = packing?["everyone"] is JsonValue v && v.TryGetValue<bool>(out var all) && all;
        var assignees = everyone ? TripPeople.Travelers(trip)
            : (task.Data["assigned_to"] as JsonArray ?? []).Select(n => Guid.TryParse(n?.ToString(), out var id) ? trip.Find(id) : null).OfType<Entity>().Where(e => e.Type == "person").ToArray();
        var packed = (packing?["packed_by"] as JsonArray ?? []).Select(n => Guid.TryParse(n?.ToString(), out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).ToArray();
        return new(task, Text(packing?["group"]), Text(packing?["quantity"]), everyone, assignees, packed);
    }

    /// <summary>The items a person brings, across all lists or one list.</summary>
    public static IReadOnlyList<PackingItem> For(PackingList list, Guid person) => list.Items.Where(i => i.IsFor(person)).ToArray();

    public static IReadOnlyList<PackingItem> Unassigned(PackingList list) => list.Items.Where(i => i.Unassigned).ToArray();

    /// <summary>Items grouped by their group, in order of first appearance; items without a group come last.</summary>
    public static IReadOnlyList<(string? Group, IReadOnlyList<PackingItem> Items)> Groups(IEnumerable<PackingItem> items) =>
        items.GroupBy(i => i.Group ?? "").OrderBy(g => g.Key.Length == 0 ? 1 : 0).Select(g => (g.Key.Length == 0 ? null : g.Key, (IReadOnlyList<PackingItem>)g.ToArray())).ToArray();

    public static Entity CreateList(string title)
    {
        var list = Entity.Create("collection", title); list.Data["purpose"] = Purpose; list.Data["items"] = new JsonArray(); return list;
    }

    /// <summary>A new item at the end of the list. Returns the item and the updated list, to be saved together.</summary>
    public static (Entity Item, Entity List) AddItem(Entity list, string title, string? group = null, string? quantity = null, IReadOnlyList<Guid>? assignees = null, bool everyone = false)
    {
        var task = Entity.Create("task", title); task.Data["status"] = "open";
        var packing = new JsonObject(); if (group is { Length: > 0 }) packing["group"] = group.Trim(); if (quantity is { Length: > 0 }) packing["quantity"] = quantity.Trim();
        if (task.Data["components"] is not JsonObject components) task.Data["components"] = components = new JsonObject();
        components["packing"] = packing;
        var withPeople = Assign(task, assignees ?? [], everyone);
        var copy = list.Copy(); if (copy.Data["items"] is not JsonArray items) copy.Data["items"] = items = new JsonArray();
        items.Add(withPeople.Id.ToString());
        return (withPeople, copy);
    }

    /// <summary>Who brings the item: certain people, everyone, or nobody yet. Packed marks of people no longer assigned are kept.</summary>
    public static Entity Assign(Entity task, IReadOnlyList<Guid> people, bool everyone)
    {
        var copy = task.Copy(); var packing = Component(copy);
        if (everyone) { packing["everyone"] = true; copy.Data.Remove("assigned_to"); }
        else { packing.Remove("everyone"); if (people.Count > 0) copy.Data["assigned_to"] = new JsonArray(people.Distinct().Select(p => (JsonNode)p.ToString()).ToArray()); else copy.Data.Remove("assigned_to"); }
        return copy;
    }

    /// <summary>
    /// Marks the item as packed or not for one person. The task's status follows, so clients that do not know
    /// packing lists still see what is done.
    /// </summary>
    public static Entity SetPacked(TripSnapshot trip, Entity task, Guid person, bool packed)
    {
        var copy = task.Copy(); var packing = Component(copy);
        var ids = (packing["packed_by"] as JsonArray ?? []).Select(n => n?.ToString()).OfType<string>().ToList();
        ids.Remove(person.ToString()); if (packed) ids.Add(person.ToString());
        if (ids.Count > 0) packing["packed_by"] = new JsonArray(ids.Order(StringComparer.Ordinal).Select(i => (JsonNode)i).ToArray()); else packing.Remove("packed_by");
        copy.Data["status"] = Item(trip, copy).Done ? "completed" : "open";
        return copy;
    }

    /// <summary>The item's group and quantity; empty values remove them.</summary>
    public static Entity Describe(Entity task, string? group, string? quantity)
    {
        var copy = task.Copy(); var packing = Component(copy);
        if (group is { Length: > 0 }) packing["group"] = group.Trim(); else packing.Remove("group");
        if (quantity is { Length: > 0 }) packing["quantity"] = quantity.Trim(); else packing.Remove("quantity");
        return copy;
    }

    private static JsonObject Component(Entity task)
    {
        if (task.Data["components"] is not JsonObject components) task.Data["components"] = components = new JsonObject();
        if (components["packing"] is not JsonObject packing) components["packing"] = packing = new JsonObject();
        return packing;
    }

    private static string? Text(JsonNode? node) => node is JsonValue v && v.ToString() is { Length: > 0 } s ? s : null;
}
