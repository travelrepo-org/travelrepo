using System.Text.Json.Nodes;
using TravelRepo.Core;
using TravelRepo.Repository;

namespace TravelRepo.Merge;

public enum ConflictKind { Scalar, DeleteEdit, OrderedList, Markdown, Binary }
public sealed record FieldChange(Guid Entity, string Path, JsonNode? Before, JsonNode? After);
public sealed record MergeConflict(Guid Entity, string Path, ConflictKind Kind, JsonNode? Base, JsonNode? Current, JsonNode? Incoming);
public sealed record MergePlan(TripSnapshot Result, IReadOnlyList<MergeConflict> Conflicts)
{
    public TripSnapshot? ExpectedCurrent { get; init; }
    public TripSnapshot? ExpectedIncoming { get; init; }
    public IReadOnlyList<Diagnostic> Diagnostics
    {
        get { var schema = Result.All.SelectMany(TravelRepo.Serialization.SchemaValidation.Validate).ToArray(); return schema.Length > 0 ? schema : SemanticValidation.Validate(Result); }
    }
    public bool CanApply => Conflicts.Count == 0 && !Diagnostics.Any(d => d.Severity == Severity.Error);
    public IReadOnlyList<ResourceEdit> ResourcesAgainst(TripSnapshot current)
    {
        if (!CanApply) throw new DomainException("merge.unresolved", "Resolve all merge conflicts before applying.");
        return current.Resources.Keys.Union(Result.Resources.Keys).Select(p => new ResourceEdit(p, Result.Resources.GetValueOrDefault(p))).ToArray();
    }
    public IReadOnlyList<EntityEdit> EditsAgainst(TripSnapshot current)
    {
        if (!CanApply) throw new DomainException("merge.unresolved", "Resolve all merge conflicts before applying.");
        return current.All.Select(e => new EntityEdit(e.Id, Result.Find(e.Id))).Concat(Result.All.Where(e => current.Find(e.Id) is null).Select(e => new EntityEdit(e.Id, e))).ToArray();
    }
}
/// <summary>Identity-based three-way structural merge. Variant metadata remains owned by the destination branch.</summary>
public static class SemanticMerge
{
    public static IReadOnlyList<FieldChange> Diff(TripSnapshot before, TripSnapshot after)
    {
        var changes = new List<FieldChange>();
        void Walk(Guid id, string path, JsonNode? a, JsonNode? b)
        {
            if (JsonNode.DeepEquals(a, b)) return;
            if (a is JsonObject x && b is JsonObject y)
                foreach (var key in x.Select(p => p.Key).Union(y.Select(p => p.Key))) Walk(id, path + "/" + key, x[key], y[key]);
            else changes.Add(new(id, path, a?.DeepClone(), b?.DeepClone()));
        }
        foreach (var id in before.All.Select(e => e.Id).Union(after.All.Select(e => e.Id))) Walk(id, "", before.Find(id)?.Data, after.Find(id)?.Data);
        foreach (var path in before.Resources.Keys.Union(after.Resources.Keys))
        {
            var a = before.Resources.GetValueOrDefault(path); var b = after.Resources.GetValueOrDefault(path);
            if (!Equal(a, b)) changes.Add(new(Guid.Empty, "/resources/" + path, a is null ? null : JsonValue.Create(path.EndsWith(".md", StringComparison.Ordinal) ? System.Text.Encoding.UTF8.GetString(a) : TravelRepository.Hash(a)), b is null ? null : JsonValue.Create(path.EndsWith(".md", StringComparison.Ordinal) ? System.Text.Encoding.UTF8.GetString(b) : TravelRepository.Hash(b))));
        }
        return changes;
    }
    public static MergePlan Plan(TripSnapshot baseline, TripSnapshot current, TripSnapshot incoming, IReadOnlyDictionary<string, JsonNode?>? resolutions = null)
    {
        if (baseline.Manifest.Id != current.Manifest.Id || baseline.Manifest.Id != incoming.Manifest.Id) throw new DomainException("merge.trip", "Cannot merge different trips.");
        var conflicts = new List<MergeConflict>();
        JsonNode? Merge(Guid id, string path, JsonNode? b, JsonNode? o, JsonNode? t)
        {
            if (path == "/variant" && id == current.Manifest.Id) return o?.DeepClone();
            if (resolutions is not null && resolutions.TryGetValue(id + path, out var resolution)) return resolution?.DeepClone();
            if (JsonNode.DeepEquals(o, t)) return o?.DeepClone();
            if (JsonNode.DeepEquals(b, o)) return t?.DeepClone();
            if (JsonNode.DeepEquals(b, t)) return o?.DeepClone();
            if (o is JsonObject om && t is JsonObject tm && (b is JsonObject || b is null))
            {
                var bm = b as JsonObject ?? new JsonObject(); var result = new JsonObject();
                foreach (var key in bm.Select(p => p.Key).Union(om.Select(p => p.Key)).Union(tm.Select(p => p.Key)).Order(StringComparer.Ordinal))
                {
                    var merged = Merge(id, path + "/" + key, bm[key], om[key], tm[key]);
                    if (merged is not null || (om.ContainsKey(key) && tm.ContainsKey(key) || !bm.ContainsKey(key) && (om.ContainsKey(key) || tm.ContainsKey(key)))) result[key] = merged;
                }
                return result;
            }
            if (o is JsonArray oa && t is JsonArray ta && (b is JsonArray || b is null) && !path.EndsWith("/children", StringComparison.Ordinal) && IsIdSet(oa) && IsIdSet(ta) && IsIdSet(b as JsonArray ?? []))
            {
                var bs = (b as JsonArray ?? []).Select(n => n!.ToString()).ToHashSet(); var os = oa.Select(n => n!.ToString()).ToHashSet(); var ts = ta.Select(n => n!.ToString()).ToHashSet();
                return new JsonArray(os.Intersect(ts).Union(os.Except(bs)).Union(ts.Except(bs)).Order(StringComparer.Ordinal).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
            }
            if (b is JsonArray baseArray && o is JsonArray ours && t is JsonArray theirs && SequenceMerge.TryMerge(baseArray.Select(n => n?.ToJsonString() ?? "null").ToArray(), ours.Select(n => n?.ToJsonString() ?? "null").ToArray(), theirs.Select(n => n?.ToJsonString() ?? "null").ToArray(), out var ordered))
                return new JsonArray(ordered.Select(s => JsonNode.Parse(s)).ToArray());
            var kind = o is null || t is null ? ConflictKind.DeleteEdit : path.Contains("/blob", StringComparison.Ordinal) ? ConflictKind.Binary : path.EndsWith("/body", StringComparison.Ordinal) ? ConflictKind.Markdown : o is JsonArray ? ConflictKind.OrderedList : ConflictKind.Scalar;
            conflicts.Add(new(id, path, kind, b?.DeepClone(), o?.DeepClone(), t?.DeepClone())); return o?.DeepClone();
        }
        var entities = new Dictionary<Guid, Entity>(); Entity? manifest = null;
        foreach (var id in baseline.All.Select(e => e.Id).Union(current.All.Select(e => e.Id)).Union(incoming.All.Select(e => e.Id)))
        {
            var result = Merge(id, "", baseline.Find(id)?.Data, current.Find(id)?.Data, incoming.Find(id)?.Data);
            if (result is not JsonObject obj) continue;
            var entity = new Entity(obj); if (id == current.Manifest.Id) manifest = entity; else entities[id] = entity;
        }
        var resources = new Dictionary<string, byte[]>();
        foreach (var path in baseline.Resources.Keys.Union(current.Resources.Keys).Union(incoming.Resources.Keys))
        {
            var b = baseline.Resources.GetValueOrDefault(path); var o = current.Resources.GetValueOrDefault(path); var t = incoming.Resources.GetValueOrDefault(path); byte[]? result;
            if (Equal(o, t)) result = o;
            else if (Equal(b, o)) result = t;
            else if (Equal(b, t)) result = o;
            else if (resolutions is not null && resolutions.TryGetValue(Guid.Empty + "/resources/" + path, out var resolved)) result = resolved is null ? null : path.EndsWith(".md", StringComparison.Ordinal) ? System.Text.Encoding.UTF8.GetBytes(resolved.ToString()) : Convert.FromBase64String(resolved.ToString());
            else if (path.EndsWith(".md", StringComparison.Ordinal) && TryMergeLines(b, o, t, out var merged)) result = merged;
            else
            {
                JsonNode? Encode(byte[]? bytes) => bytes is null ? null : JsonValue.Create(path.EndsWith(".md", StringComparison.Ordinal) ? System.Text.Encoding.UTF8.GetString(bytes) : Convert.ToBase64String(bytes));
                conflicts.Add(new(Guid.Empty, "/resources/" + path, path.EndsWith(".md", StringComparison.Ordinal) ? ConflictKind.Markdown : ConflictKind.Binary, Encode(b), Encode(o), Encode(t))); result = o;
            }
            if (result is not null) resources[path] = result;
        }
        manifest!.Data["variant"] = current.Manifest.Data["variant"]?.DeepClone();
        var snapshot = new TripSnapshot(manifest!, entities) { Resources = resources };
        return new(snapshot, conflicts) { ExpectedCurrent = current, ExpectedIncoming = incoming };
    }
    private static bool Equal(byte[]? a, byte[]? b) => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);
    private static bool TryMergeLines(byte[]? b, byte[]? o, byte[]? t, out byte[]? result)
    {
        result = null; if (b is null || o is null || t is null) return false;
        if (!SequenceMerge.TryMerge(System.Text.Encoding.UTF8.GetString(b).Split('\n'), System.Text.Encoding.UTF8.GetString(o).Split('\n'), System.Text.Encoding.UTF8.GetString(t).Split('\n'), out var merged)) return false;
        result = System.Text.Encoding.UTF8.GetBytes(string.Join('\n', merged)); return true;
    }
    private static bool IsIdSet(JsonArray a) => a.All(n => n is JsonValue && Guid.TryParse(n.ToString(), out _));
}
