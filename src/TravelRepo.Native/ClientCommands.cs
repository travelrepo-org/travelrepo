using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using NodaTime;
using NodaTime.Text;
using TravelRepo.Core;
using TravelRepo.Git;
using TravelRepo.Merge;
using TravelRepo.Repository;

namespace TravelRepo.Native;

/// <summary>
/// The commands an app needs beyond the MCP tools: whole files with a revision, atomic edits, resources, the plan by
/// day, search with highlights, variants and merging. Results keep TravelRepo's interpretation and use plain values:
/// instants as ISO 8601 in UTC, local dates as yyyy-MM-dd, time zones as IANA ids, enums in snake_case.
/// </summary>
internal static class ClientCommands
{
    public static readonly IReadOnlyList<string> Names =
        ["snapshot", "revision", "apply", "read_resource", "agenda", "find", "variants", "create_variant", "switch_variant", "delete_variant", "rollback",
         "merge_preview", "merge_apply", "initialize_git", "sync_status", "sync", "add_remote"];

    public static readonly IReadOnlyList<string> ReadOnly = ["snapshot", "revision", "read_resource", "agenda", "find", "variants", "merge_preview", "sync_status"];

    /// <summary>Runs <paramref name="command"/>, or returns false when it is not one of these.</summary>
    public static async Task<(bool Found, JsonNode? Result)> RunAsync(NativeSession session, string command, Arguments args, CancellationToken ct)
    {
        if (session.ReadOnly && Names.Contains(command) && !ReadOnly.Contains(command))
            throw new DomainException("session.read_only", "This session is read-only.");
        switch (command)
        {
            case "snapshot":
                {
                    var state = await session.ReadAsync(ct);
                    return (true, new JsonObject
                    {
                        ["revision"] = session.Remember(state),
                        ["entities"] = new JsonArray(state.Files.Values.OrderBy(f => f.Path, StringComparer.Ordinal)
                            .Select(f => (JsonNode)new JsonObject { ["path"] = f.Path, ["hash"] = f.Hash, ["data"] = f.Entity.Data.DeepClone() }).ToArray()),
                        ["resources"] = new JsonArray(state.Trip.Resources.OrderBy(r => r.Key, StringComparer.Ordinal)
                            .Select(r => (JsonNode)new JsonObject { ["path"] = r.Key, ["size"] = r.Value.Length, ["hash"] = TravelRepository.Hash(r.Value) }).ToArray()),
                        ["diagnostics"] = Diagnostics(state.Diagnostics)
                    });
                }
            case "revision":
                return (true, new JsonObject { ["revision"] = session.Remember(await session.ReadAsync(ct)) });
            case "apply":
                return (true, await ApplyAsync(session, args, ct));
            case "read_resource":
                {
                    var path = args.Required("path"); var state = await session.ReadAsync(ct);
                    if (!state.Trip.Resources.TryGetValue(path, out var bytes)) throw new DomainException("resource.missing", "There is no file " + path + " in this trip.");
                    return (true, new JsonObject { ["path"] = path, ["size"] = bytes.Length, ["base64"] = Convert.ToBase64String(bytes) });
                }
            case "agenda":
                return (true, Agenda(TripAgenda.Build((await session.ReadAsync(ct)).Trip)));
            case "find":
                return (true, await FindAsync(session, args, ct));
            case "variants":
                return (true, new JsonObject
                {
                    ["variants"] = new JsonArray((await session.Git.VariantOverviewAsync(args.String("remote"), ct)).Select(v => (JsonNode)new JsonObject
                    {
                        ["branch"] = v.Branch,
                        ["title"] = v.Title,
                        ["variant_id"] = v.VariantId,
                        ["local_branch"] = v.LocalBranch,
                        ["remote_branch"] = v.RemoteBranch,
                        ["current"] = v.Current,
                        ["primary"] = v.Primary,
                        ["archived"] = v.Archived,
                        ["merged"] = v.Merged,
                        ["local_only"] = v.LocalOnly,
                        ["upstream_gone"] = v.UpstreamGone,
                        ["ahead"] = v.Ahead,
                        ["behind"] = v.Behind,
                        ["only_here"] = v.OnlyHere,
                        ["behind_primary"] = v.BehindPrimary,
                        ["last_author"] = v.LastAuthor,
                        ["last_changed"] = v.LastChanged?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture)
                    }).ToArray())
                });
            case "create_variant":
                await session.Git.CreateVariantAsync(args.Required("branch"), args.Required("title"), ct);
                return (true, new JsonObject());
            case "switch_variant":
                await session.Git.SwitchAsync(args.Required("branch"), ct);
                return (true, new JsonObject());
            case "delete_variant":
                {
                    var deleted = await session.Git.DeleteVariantAsync(args.Required("branch"), args.Bool("delete_remote", false), args.String("remote"), ct);
                    return (true, new JsonObject { ["branch"] = deleted.Branch, ["title"] = deleted.Title, ["commit"] = deleted.Commit });
                }
            case "rollback":
                await session.Git.RollbackAsync(args.Required("revision"), ct);
                return (true, new JsonObject());
            case "merge_preview":
                {
                    var (plan, _) = await PlanAsync(session, args, ct);
                    var current = (await session.ReadAsync(ct)).Trip;
                    return (true, new JsonObject
                    {
                        ["can_apply"] = plan.CanApply,
                        ["conflicts"] = new JsonArray(plan.Conflicts.Select(c => (JsonNode)new JsonObject
                        {
                            ["key"] = c.Entity + c.Path,
                            ["entity"] = c.Entity.ToString(),
                            ["path"] = c.Path,
                            ["kind"] = Snake(c.Kind.ToString()),
                            ["base"] = c.Base?.DeepClone(),
                            ["current"] = c.Current?.DeepClone(),
                            ["incoming"] = c.Incoming?.DeepClone()
                        }).ToArray()),
                        ["changes"] = new JsonArray(ChangeSummary.Between(current, plan.Result).Entities.Select(e => (JsonNode)new JsonObject
                        {
                            ["entity"] = e.Entity.ToString(),
                            ["type"] = e.Type,
                            ["title"] = e.Title,
                            ["kind"] = Snake(e.Kind.ToString()),
                            ["fields"] = new JsonArray(e.Fields.Select(f => (JsonNode)f).ToArray())
                        }).ToArray()),
                        ["diagnostics"] = Diagnostics(plan.Diagnostics)
                    });
                }
            case "merge_apply":
                {
                    if (!string.IsNullOrEmpty(await session.Git.StatusAsync(ct))) throw new DomainException("git.dirty", "Create a version before merging.");
                    var (plan, other) = await PlanAsync(session, args, ct);
                    if (!plan.CanApply) throw new DomainException("merge.unresolved", "Answer every conflict before merging; " + plan.Conflicts.Count + " remain.");
                    var state = await session.ReadAsync(ct);
                    await session.Repository.ApplyAsync(state, plan.EditsAgainst(state.Trip), ct, plan.ResourcesAgainst(state.Trip));
                    var commit = await session.Git.RecordMergeAsync(other.Commit, args.String("message") ?? "Merge " + (other.Manifest.Data["variant"]?["title"]?.ToString() ?? other.Branch), ct);
                    return (true, new JsonObject { ["commit"] = commit });
                }
        }
        return (false, null);
    }

    /// <summary>The common ancestor, the other side and the plan, with the answers given in <c>resolutions</c>.</summary>
    private static async Task<(MergePlan Plan, Variant Other)> PlanAsync(NativeSession session, Arguments args, CancellationToken ct)
    {
        var branch = args.Required("branch");
        var other = (await session.Git.VariantsAsync(ct)).FirstOrDefault(v => v.Branch == branch || "refs/remotes/" + v.Branch == branch)
            ?? throw new DomainException("variant.missing", "There is no variant " + branch + ".");
        var baseline = await session.Git.SnapshotAsync(await session.Git.MergeBaseAsync(other.Commit, ct), ct);
        var incoming = await session.Git.SnapshotAsync(other.Commit, ct);
        var resolutions = args.Object("resolutions")?.ToDictionary(p => p.Key, p => p.Value?.DeepClone());
        return (SemanticMerge.Plan(baseline, (await session.ReadAsync(ct)).Trip, incoming, resolutions), other);
    }

    private static async Task<JsonNode> ApplyAsync(NativeSession session, Arguments args, CancellationToken ct)
    {
        var revision = args.Required("revision");
        var state = session.Recall(revision);
        if (state is null)
        {
            state = await session.ReadAsync(ct);
            if (Revision(state) != revision) throw new DomainException("repository.changed", "The trip changed since that revision. Read it again and repeat the edit.");
        }
        var edits = new List<EntityEdit>();
        foreach (var node in args.Array("edits") ?? [])
        {
            if (node is not JsonObject edit || !Guid.TryParse(edit["id"]?.GetValue<string>(), out var id)) throw new DomainException("argument.invalid", "Every edit needs an id.");
            var data = edit["data"];
            if (data is not null and not JsonObject) throw new DomainException("argument.invalid", "The data of an edit must be an object, or null to delete.");
            edits.Add(new(id, data is JsonObject value ? new Entity((JsonObject)value.DeepClone()) : null));
        }
        var resources = new List<ResourceEdit>();
        foreach (var node in args.Array("resources") ?? [])
        {
            if (node is not JsonObject resource || resource["path"]?.GetValue<string>() is not { Length: > 0 } path) throw new DomainException("argument.invalid", "Every resource needs a path.");
            byte[]? bytes;
            try { bytes = resource["base64"]?.GetValue<string>() is { } text ? Convert.FromBase64String(text) : null; }
            catch (FormatException) { throw new DomainException("argument.invalid", "The base64 of " + path + " is not valid."); }
            resources.Add(new(path, bytes));
        }
        if (edits.Count == 0 && resources.Count == 0) throw new DomainException("argument.missing", "Give at least one edit or resource.");
        var saved = await session.Repository.ApplyAsync(state, edits, ct, resources);
        return new JsonObject { ["revision"] = session.Remember(saved), ["diagnostics"] = Diagnostics(saved.Diagnostics) };
    }

    private static async Task<JsonNode> FindAsync(NativeSession session, Arguments args, CancellationToken ct)
    {
        HashSet<SearchKind>? kinds = null;
        foreach (var name in args.OptionalStrings("kinds") ?? [])
            (kinds ??= []).Add(Enum.TryParse<SearchKind>(name.Replace("_", ""), true, out var kind) ? kind : throw new DomainException("argument.invalid", "Unknown kind " + name + "."));
        var trip = (await session.ReadAsync(ct)).Trip;
        var hits = TripSearch.Find(trip, args.Required("query"), new SearchOptions { Notes = args.Bool("notes", false), Kinds = kinds, Limit = args.Int("limit", 200) });
        return new JsonObject
        {
            ["hits"] = new JsonArray(hits.Select(h => (JsonNode)new JsonObject
            {
                ["id"] = h.Entity.Id.ToString(),
                ["type"] = h.Entity.Type,
                ["title"] = h.Entity.Title,
                ["kind"] = Snake(h.Kind.ToString()),
                ["score"] = h.Score,
                ["day"] = Date(h.Day),
                ["start"] = Time(h.Start),
                ["zone"] = h.Zone,
                ["matches"] = new JsonArray(h.Matches.Select(m => (JsonNode)new JsonObject
                {
                    ["field"] = Snake(m.Field.ToString()),
                    ["text"] = m.Text,
                    ["ranges"] = new JsonArray(m.Ranges.Select(r => (JsonNode)new JsonObject { ["start"] = r.Start, ["length"] = r.Length }).ToArray())
                }).ToArray())
            }).ToArray())
        };
    }

    private static JsonObject Agenda(Agenda agenda) => new()
    {
        ["days"] = new JsonArray(agenda.Days.Select(d => (JsonNode)new JsonObject
        {
            ["date"] = Date(d.Date),
            ["number"] = d.Number,
            ["entries"] = new JsonArray(d.Entries.Select(Entry).ToArray()),
            ["nights"] = new JsonArray(d.Nights.Select(n => (JsonNode)new JsonObject
            {
                ["stay"] = n.Run.Main.Item.Id.ToString(),
                ["title"] = n.Run.Main.Item.Title,
                ["night"] = n.Number,
                ["nights"] = n.Run.Nights,
                ["first_night"] = Date(n.Run.FirstNight),
                ["zone"] = n.Run.Main.Zone
            }).ToArray())
        }).ToArray()),
        ["unscheduled"] = new JsonArray(agenda.Unscheduled.Select(Entry).ToArray())
    };

    private static JsonNode Entry(AgendaEntry e) => new JsonObject
    {
        ["id"] = e.Item.Id.ToString(),
        ["title"] = e.Item.Title,
        ["status"] = e.Status,
        ["role"] = Snake(e.Role.ToString()),
        ["kind"] = Snake(e.Kind.ToString()),
        ["precision"] = Snake(e.Precision.ToString()),
        ["start"] = Time(e.Start),
        ["end"] = Time(e.End),
        ["zone"] = e.Zone,
        ["end_zone"] = e.EndZone,
        ["day"] = Date(e.Day),
        ["timed"] = e.IsTimed,
        ["idea"] = e.IsIdea,
        ["steps"] = new JsonArray(e.Steps.Select(Entry).ToArray())
    };

    /// <summary>A short, stable fingerprint of every file of the trip, entities and resources.</summary>
    public static string Revision(RepositoryState state)
    {
        var text = new StringBuilder();
        foreach (var file in state.Files.Values.OrderBy(f => f.Path, StringComparer.Ordinal)) text.Append(file.Path).Append('\0').Append(file.Hash).Append('\n');
        foreach (var (path, bytes) in state.Trip.Resources.OrderBy(r => r.Key, StringComparer.Ordinal)) text.Append(path).Append('\0').Append(TravelRepository.Hash(bytes)).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16];
    }

    public static JsonArray Diagnostics(IEnumerable<Diagnostic> diagnostics) => new(diagnostics.Select(d => (JsonNode)new JsonObject
    {
        ["code"] = d.Code,
        ["severity"] = d.Severity.ToString().ToLowerInvariant(),
        ["path"] = d.Path,
        ["message"] = d.Message
    }).ToArray());

    private static string? Date(LocalDate? date) => date is { } d ? LocalDatePattern.Iso.Format(d) : null;
    private static string? Time(Instant? instant) => instant is { } i ? InstantPattern.General.Format(i) : null;

    /// <summary>PartlyCloudy → partly_cloudy.</summary>
    public static string Snake(string name)
    {
        var text = new StringBuilder();
        foreach (var c in name) { if (char.IsUpper(c) && text.Length > 0) text.Append('_'); text.Append(char.ToLowerInvariant(c)); }
        return text.ToString();
    }
}
