using System.Text.Json.Nodes;

namespace TravelRepo.Core;

/// <summary>One place in an entity's data that refers to another entity, with the JSON path of the value.</summary>
public sealed record EntityReference(Entity Source, Guid Target, string Path);

/// <summary>
/// Finds and removes references between entities. A reference is any string value that equals another entity's ID,
/// outside the entity's own <c>id</c> and outside <c>extensions</c>, which belong to their plugins.
/// </summary>
public static class TripReferences
{
    /// <summary>Every reference from <paramref name="source"/> to another entity, including references inside extensions.</summary>
    public static IReadOnlyList<EntityReference> From(Entity source, bool includeExtensions = true)
    {
        var result = new List<EntityReference>();
        void Visit(JsonNode? node, string path, bool root)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var (key, value) in o)
                    {
                        if (root && key == "id") continue;
                        if (key == "extensions" && !includeExtensions) continue;
                        Visit(value, path + "/" + key, false);
                    }
                    break;
                case JsonArray a:
                    for (var i = 0; i < a.Count; i++) Visit(a[i], path + "/" + i, false);
                    break;
                case JsonValue v when v.TryGetValue<string>(out var text) && Guid.TryParse(text, out var id) && id != source.Id:
                    result.Add(new(source, id, path.Length == 0 ? "/" : path));
                    break;
            }
        }
        Visit(source.Data, "", true);
        return result;
    }

    /// <summary>Every reference to <paramref name="target"/> from other entities in the trip.</summary>
    public static IReadOnlyList<EntityReference> To(TripSnapshot trip, Guid target, bool includeExtensions = true) =>
        trip.All.Where(e => e.Id != target).SelectMany(e => From(e, includeExtensions)).Where(r => r.Target == target).ToArray();

    /// <summary>
    /// Returns a copy of <paramref name="source"/> without references to <paramref name="targets"/>. List members are
    /// removed; a tagged list entry such as a content entry, a related item or a constraint is removed as a whole;
    /// any other field is removed, and an object left empty by that is removed too, except an accommodation or
    /// transport component. Extensions are not changed.
    /// </summary>
    public static Entity Detach(Entity source, IReadOnlySet<Guid> targets)
    {
        var copy = source.Copy();
        bool IsTarget(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && Guid.TryParse(s, out var id) && targets.Contains(id);
        // Removes references below the node. Returns whether anything changed. A component that says what kind of item
        // this is stays even when it becomes empty: an accommodation without a place is still a stay.
        bool Clean(JsonNode? node, bool root, bool components = false)
        {
            var changed = false;
            if (node is JsonObject o)
            {
                foreach (var key in o.Select(p => p.Key).ToArray())
                {
                    if (root && key == "id" || key == "extensions") continue;
                    if (IsTarget(o[key])) { o.Remove(key); changed = true; continue; }
                    // An object that only held the removed reference, such as a booking component or a cover, goes too.
                    if (Clean(o[key], false, root && key == "components")) { changed = true; if (o[key] is JsonObject { Count: 0 } && !(root && key == "components") && !(components && key is "accommodation" or "transport")) o.Remove(key); }
                }
            }
            else if (node is JsonArray a)
            {
                for (var i = a.Count - 1; i >= 0; i--)
                {
                    var item = a[i];
                    if (IsTarget(item)) { a.RemoveAt(i); changed = true; continue; }
                    // An entry that names its kind and points at the deleted entity has no meaning without it.
                    if (item is JsonObject entry && entry.ContainsKey("type") && entry.Any(p => IsTarget(p.Value))) { a.RemoveAt(i); changed = true; continue; }
                    if (Clean(item, false)) { changed = true; if (item is JsonObject { Count: 0 }) a.RemoveAt(i); }
                }
            }
            return changed;
        }
        Clean(copy.Data, true);
        return copy;
    }

    /// <summary>
    /// Returns a copy of <paramref name="source"/> in which references to any of <paramref name="from"/> point to
    /// <paramref name="to"/> instead. A list that would then name <paramref name="to"/> twice keeps it once.
    /// </summary>
    public static Entity Redirect(Entity source, IReadOnlySet<Guid> from, Guid to)
    {
        var copy = source.Copy(); var target = to.ToString();
        void Visit(JsonNode? node, bool root)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var key in o.Select(p => p.Key).ToArray())
                    {
                        if (root && key == "id") continue;
                        if (Matches(o[key])) o[key] = target; else Visit(o[key], false);
                    }
                    break;
                case JsonArray a:
                    for (var i = 0; i < a.Count; i++) if (Matches(a[i])) a[i] = target; else Visit(a[i], false);
                    var seen = false;
                    for (var i = 0; i < a.Count; i++)
                        if (a[i] is JsonValue v && v.TryGetValue<string>(out var s) && s == target) { if (seen) a.RemoveAt(i--); seen = true; }
                    break;
            }
        }
        bool Matches(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && Guid.TryParse(s, out var id) && from.Contains(id);
        Visit(copy.Data, true);
        return copy;
    }
}
