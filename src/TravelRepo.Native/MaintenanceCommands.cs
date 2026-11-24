using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TravelRepo.Core;
using TravelRepo.Git;
using TravelRepo.Repository;

namespace TravelRepo.Native;

/// <summary>
/// Looking after a trip folder and its history: Git health and repair, restoring an older version, the version list,
/// remotes, variants that are not open (renaming, sharing, the recycle bin) and copying the trip elsewhere.
/// </summary>
internal static class MaintenanceCommands
{
    public static readonly IReadOnlyList<string> Names =
        ["git_health", "recover", "remove_stale_lock", "abort_operation", "move_aside", "restore_preview", "restore_apply", "history", "remotes", "pull",
         "update_variant", "deleted_variants", "restore_variant", "purge_deleted_variants", "keep_local_only", "share_variant", "sync_variant", "mark_merged", "copy_trip"];

    public static readonly IReadOnlyList<string> ReadOnly = ["git_health", "restore_preview", "history", "remotes", "deleted_variants", "copy_trip"];

    public static async Task<(bool Found, JsonNode? Result)> RunAsync(NativeSession session, string command, Arguments args, CancellationToken ct)
    {
        if (!Names.Contains(command)) return (false, null);
        if (session.ReadOnly && !ReadOnly.Contains(command)) throw new DomainException("session.read_only", "This session is read-only.");
        var git = session.Git;
        switch (command)
        {
            case "git_health":
                {
                    var health = await git.DiagnoseAsync(args.String("remote"), ct);
                    return (true, new JsonObject
                    {
                        ["git_version"] = health.GitVersion,
                        ["branch"] = health.Branch,
                        ["detached"] = health.Detached,
                        ["operation"] = health.Operation,
                        ["stale_lock"] = health.StaleLock,
                        ["lock_age_seconds"] = health.LockAge is { } age ? (int)age.TotalSeconds : null,
                        ["trip_changes"] = health.TripChanges,
                        ["other_files"] = health.OtherFiles,
                        ["remote"] = health.Remote,
                        ["remote_branch"] = health.RemoteBranch,
                        ["remote_branch_known"] = health.RemoteBranchKnown,
                        ["ahead"] = health.Ahead,
                        ["behind"] = health.Behind,
                        ["summary"] = health.Summary()
                    });
                }
            case "recover":
                await session.Repository.RecoverAsync(ct);
                return (true, new JsonObject { ["revision"] = session.Remember(await session.ReadAsync(ct)) });
            case "remove_stale_lock":
                return (true, new JsonObject { ["removed"] = await git.RemoveStaleLockAsync(TimeSpan.FromSeconds(Math.Max(0, args.Int("minimum_age_seconds", 60))), ct) });
            case "abort_operation":
                await git.AbortOperationAsync(ct);
                return (true, new JsonObject());
            case "move_aside":
                return (true, new JsonObject { ["folder"] = await git.MoveAsideAsync(args.Strings("paths"), ct) });
            case "restore_preview":
                {
                    var plan = await session.Repository.PreviewRestoreAsync(await git.SnapshotAsync(args.Required("commit"), ct), ct);
                    return (true, new JsonObject
                    {
                        ["plan"] = Fingerprint(plan),
                        ["changes"] = new JsonArray(plan.Changes.OrderBy(c => c.Path, StringComparer.Ordinal).Select(c => (JsonNode)new JsonObject
                        {
                            ["path"] = c.Path,
                            ["kind"] = c.CurrentHash is null ? "added" : c.Replacement is null ? "removed" : "changed",
                            ["size"] = c.Replacement?.Length
                        }).ToArray())
                    });
                }
            case "restore_apply":
                {
                    // The plan answers what the person saw in restore_preview; a trip that changed since is not restored.
                    var plan = await session.Repository.PreviewRestoreAsync(await git.SnapshotAsync(args.Required("commit"), ct), ct);
                    if (Fingerprint(plan) != args.Required("plan")) throw new DomainException("repair.changed", "The trip changed since this restore was previewed. Preview it again.");
                    var state = await session.Repository.ApplyRestoreAsync(plan, ct);
                    return (true, new JsonObject { ["revision"] = session.Remember(state), ["diagnostics"] = ClientCommands.Diagnostics(state.Diagnostics) });
                }
            case "history":
                return (true, new JsonObject
                {
                    ["versions"] = new JsonArray((await git.HistoryAsync(ct)).Take(Math.Clamp(args.Int("limit", 100), 1, 100)).Select(v => (JsonNode)new JsonObject
                    {
                        ["commit"] = v.Commit,
                        ["parents"] = new JsonArray(v.Parents.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(p => (JsonNode)p).ToArray()),
                        ["author"] = v.Author,
                        ["email"] = v.Email,
                        ["committer"] = v.Committer,
                        ["committer_email"] = v.CommitterEmail,
                        ["at"] = DateTimeOffset.TryParse(v.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
                            ? at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) : v.Timestamp,
                        ["message"] = v.Message.TrimEnd('\n'),
                        ["title"] = v.Message.Split('\n')[0],
                        ["application_generated"] = v.ApplicationGenerated,
                        ["refs"] = new JsonArray(v.Refs.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(r => (JsonNode)r).ToArray())
                    }).ToArray())
                });
            case "remotes":
                // Remote addresses never carry credentials (add_remote refuses them), so they can be shown.
                return (true, new JsonObject
                {
                    ["remotes"] = new JsonArray((await git.RemotesAsync(ct)).OrderBy(r => r.Key, StringComparer.Ordinal)
                        .Select(r => (JsonNode)new JsonObject { ["name"] = r.Key, ["url"] = r.Value }).ToArray())
                });
            case "pull":
                return (true, new JsonObject { ["state"] = ClientCommands.Snake((await git.PullAsync(args.String("remote", "origin"), ct)).ToString()) });
            case "update_variant":
                await git.UpdateVariantAsync(args.String("title"), args.Bool("archive", false), ct);
                return (true, new JsonObject());
            case "deleted_variants":
                return (true, new JsonObject
                {
                    ["variants"] = new JsonArray((await git.DeletedVariantsAsync(ct)).Select(d => (JsonNode)new JsonObject
                    {
                        ["reference"] = d.Reference,
                        ["branch"] = d.Branch,
                        ["title"] = d.Title,
                        ["deleted_at"] = d.DeletedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                        ["commit"] = d.Commit
                    }).ToArray())
                });
            case "restore_variant":
                {
                    var reference = args.Required("reference");
                    var deleted = (await git.DeletedVariantsAsync(ct)).FirstOrDefault(d => d.Reference == reference)
                        ?? throw new DomainException("variant.missing", "There is no deleted variant " + reference + ".");
                    return (true, new JsonObject { ["branch"] = await git.RestoreVariantAsync(deleted, ct) });
                }
            case "purge_deleted_variants":
                return (true, new JsonObject { ["removed"] = await git.PurgeDeletedVariantsAsync(TimeSpan.FromDays(Math.Max(0, args.Int("older_than_days", 30))), ct) });
            case "keep_local_only":
                await git.KeepLocalOnlyAsync(args.Required("branch"), ct);
                return (true, new JsonObject());
            case "share_variant":
                await git.ShareVariantAsync(args.Required("branch"), args.String("remote", "origin"), ct);
                return (true, new JsonObject());
            case "sync_variant":
                return (true, new JsonObject { ["state"] = ClientCommands.Snake((await git.SyncVariantAsync(args.Required("branch"), args.String("remote", "origin"), ct)).ToString()) });
            case "mark_merged":
                await git.MarkMergedAsync(args.Required("into_variant_id"), args.Required("into_commit"), ct);
                return (true, new JsonObject());
            case "copy_trip":
                {
                    var destination = Path.GetFullPath(args.Required("destination"));
                    await git.CopyWorkingTreeAsync(destination, ct);
                    return (true, new JsonObject { ["root"] = destination });
                }
        }
        return (false, null);
    }

    /// <summary>A fingerprint of a restore: every file it touches, as it is now and as it becomes.</summary>
    private static string Fingerprint(RepairPlan plan)
    {
        var text = new StringBuilder();
        foreach (var change in plan.Changes.OrderBy(c => c.Path, StringComparer.Ordinal))
            text.Append(change.Path).Append('\0').Append(change.CurrentHash).Append('\0').Append(change.Replacement is null ? "-" : TravelRepository.Hash(change.Replacement)).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16];
    }
}
