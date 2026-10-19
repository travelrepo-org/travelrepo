using System.Globalization;
using System.Text.Json.Nodes;
using TravelRepo.Core;
using TravelRepo.Serialization;

namespace TravelRepo.Git;

/// <summary>
/// One variant as people see it: the local branch and its copy on the remote combined, with where it lives and how the
/// two differ. Counts come from the last fetch; nothing contacts the network.
/// </summary>
public sealed record VariantOverview(
    string Title,
    string? VariantId,
    string? LocalBranch,
    string? RemoteBranch,
    bool Current,
    bool Primary,
    bool Archived,
    bool Merged,
    bool LocalOnly,
    bool UpstreamGone,
    int Ahead,
    int Behind,
    int OnlyHere,
    int BehindPrimary,
    string? LastAuthor,
    DateTimeOffset? LastChanged,
    JsonObject Manifest)
{
    /// <summary>The branch to switch to: the local one, or the remote one that has no local copy yet.</summary>
    public string Branch => LocalBranch ?? RemoteBranch!;
    public bool OnRemote => RemoteBranch is not null;
    public bool RemoteOnly => LocalBranch is null;
    public bool Diverged => Ahead > 0 && Behind > 0;
}

/// <summary>A variant in the local recycle bin: the branch name it had and the moment it was deleted.</summary>
public sealed record DeletedVariant(string Reference, string Branch, string Title, DateTimeOffset DeletedAt, string Commit);

public sealed partial class GitRepository
{
    /// <summary>Local references that keep deleted variants restorable. They are never pushed.</summary>
    public const string DeletedPrefix = "refs/travelrepo/deleted/";
    private const string LocalOnlyKey = "travelrepoLocalOnly";
    /// <summary>Local references to where remote branches were before they were deleted on the remote. Never pushed.</summary>
    public const string GonePrefix = "refs/travelrepo/gone/";

    /// <summary>True when a branch was kept "only on this computer" after its copy on the remote was deleted.</summary>
    public async Task<bool> IsLocalOnlyAsync(string branch, CancellationToken ct = default) =>
        (await backend.ExecuteAsync(Repository.Root, ["config", "--bool", "--get", "branch." + branch + "." + LocalOnlyKey], cancellationToken: ct)).Output.Trim() == "true";

    /// <summary>All variants, local and on <paramref name="remote"/>, combined per variant and in a stable order: the primary plan first.</summary>
    public async Task<IReadOnlyList<VariantOverview>> VariantOverviewAsync(string? remote, CancellationToken ct = default)
    {
        var current = (await backend.ExecuteAsync(Repository.Root, ["symbolic-ref", "--quiet", "--short", "HEAD"], cancellationToken: ct)).Output.Trim();
        var rows = (await Run(ct, "for-each-ref", "--format=%(refname)%00%(objectname)%00%(upstream)%00%(upstream:track,nobracket)%00%(authorname)%00%(committerdate:iso-strict)", "refs/heads", "refs/remotes")).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(r => r.Split('\0')).Where(p => p.Length >= 6 && !p[0].EndsWith("/HEAD", StringComparison.Ordinal)).ToArray();
        var manifests = new Dictionary<string, JsonObject>();
        foreach (var row in rows)
        {
            var content = await backend.ExecuteAsync(Repository.Root, ["show", row[1] + ":travel.yaml"], cancellationToken: ct);
            if (content.ExitCode != 0) continue;
            try { var manifest = YamlCodec.Read(content.Output); if (SchemaValidation.Validate(manifest).Count == 0) manifests[row[0]] = manifest.Data; }
            catch (Exception ex) when (ex is DomainException or YamlDotNet.Core.YamlException) { }
        }
        string Short(string reference) => reference.StartsWith("refs/heads/", StringComparison.Ordinal) ? reference["refs/heads/".Length..] : reference.StartsWith("refs/remotes/", StringComparison.Ordinal) ? reference["refs/remotes/".Length..] : reference;
        var locals = rows.Where(r => r[0].StartsWith("refs/heads/", StringComparison.Ordinal) && manifests.ContainsKey(r[0])).ToArray();
        var remotes = remote is null ? [] : rows.Where(r => r[0].StartsWith("refs/remotes/" + remote + "/", StringComparison.Ordinal) && manifests.ContainsKey(r[0])).ToArray();
        var primary = locals.FirstOrDefault(r => manifests[r[0]]["variant"]?["role"]?.ToString() == "primary" && Short(r[0]) == "main") ?? locals.FirstOrDefault(r => manifests[r[0]]["variant"]?["role"]?.ToString() == "primary");
        var result = new List<VariantOverview>(); var claimed = new HashSet<string>();
        async Task<int> Count(string range) => int.Parse((await Run(ct, "rev-list", "--count", range)).Trim(), CultureInfo.InvariantCulture);
        foreach (var local in locals)
        {
            var branch = Short(local[0]); var localOnly = await IsLocalOnlyAsync(branch, ct);
            string? remoteRef = null;
            if (remote is not null && !localOnly)
            {
                var upstream = local[2].StartsWith("refs/remotes/" + remote + "/", StringComparison.Ordinal) ? local[2] : null;
                remoteRef = upstream ?? "refs/remotes/" + remote + "/" + branch;
            }
            var remoteRow = remoteRef is null ? null : remotes.FirstOrDefault(r => r[0] == remoteRef);
            var gone = local[3].Contains("gone", StringComparison.Ordinal) && !localOnly;
            if (remoteRow is not null) claimed.Add(remoteRow[0]);
            int ahead = 0, behind = 0;
            if (remoteRow is not null) { ahead = await Count(remoteRow[1] + ".." + local[1]); behind = await Count(local[1] + ".." + remoteRow[1]); }
            var onlyHere = int.Parse((await Run(ct, "rev-list", "--count", local[1], "--not", "--exclude=" + branch, "--branches", "--remotes", "--glob=" + GonePrefix + "*")).Trim(), CultureInfo.InvariantCulture);
            var behindPrimary = primary is not null && primary[0] != local[0] ? await Count(local[1] + ".." + primary[1]) : 0;
            result.Add(Overview(manifests[local[0]], branch, remoteRow is null ? null : Short(remoteRow[0]), branch == current, localOnly, gone, ahead, behind, onlyHere, behindPrimary, local[4], local[5]));
        }
        // Copies on the remote that are not downloaded yet. Two copies of the same variant at the same commit appear once.
        var seen = new HashSet<(string?, string)>();
        foreach (var row in remotes.Where(r => !claimed.Contains(r[0])))
        {
            var id = manifests[row[0]]["variant"]?["id"]?.ToString();
            if (!seen.Add((id, row[1])) || result.Any(v => v.VariantId == id && v.RemoteBranch is not null && rows.First(r => r[0] == "refs/remotes/" + v.RemoteBranch)[1] == row[1])) continue;
            var behindPrimary = primary is not null ? await Count(row[1] + ".." + primary[1]) : 0;
            result.Add(Overview(manifests[row[0]], null, Short(row[0]), false, false, false, 0, 0, 0, behindPrimary, row[4], row[5]));
        }
        return result.OrderByDescending(v => v.Primary).ThenBy(v => v.Archived).ThenByDescending(v => v.Current).ThenBy(v => v.Title, StringComparer.CurrentCulture).ToArray();
    }

    private static VariantOverview Overview(JsonObject manifest, string? local, string? remote, bool current, bool localOnly, bool gone, int ahead, int behind, int onlyHere, int behindPrimary, string author, string date)
    {
        var variant = manifest["variant"];
        return new(variant?["title"]?.ToString() ?? local ?? remote ?? "", variant?["id"]?.ToString(), local, remote, current, variant?["role"]?.ToString() == "primary",
            variant?["state"]?.ToString() == "archived", variant?["merged_into"] is JsonObject, localOnly, gone, ahead, behind, onlyHere, variant?["role"]?.ToString() == "primary" ? 0 : behindPrimary,
            author.Length > 0 ? author : null, DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null, manifest);
    }

    /// <summary>
    /// Deletes a variant branch. The primary plan and the open variant cannot be deleted. The branch is first kept under
    /// <see cref="DeletedPrefix"/> so it can be restored. With <paramref name="deleteRemote"/> its copy on
    /// <paramref name="remote"/> is deleted too.
    /// </summary>
    public Task<DeletedVariant> DeleteVariantAsync(string branch, bool deleteRemote, string? remote, CancellationToken ct = default) => Mutate(async () =>
    {
        var current = (await backend.ExecuteAsync(Repository.Root, ["symbolic-ref", "--quiet", "--short", "HEAD"], cancellationToken: ct)).Output.Trim();
        if (branch == current) throw new DomainException("variant.current", "Open another variant before deleting this one.");
        var commit = await Run(ct, "rev-parse", "--verify", "refs/heads/" + branch + "^{commit}");
        var manifest = YamlCodec.Read(await Run(ct, "show", commit + ":travel.yaml"));
        if (manifest.Data["variant"]?["role"]?.ToString() == "primary") throw new DomainException("variant.primary", "The main plan cannot be deleted.");
        var remoteBranch = remote is null ? null : await RemoteBranchAsync(remote, branch, ct);
        var now = DateTimeOffset.UtcNow;
        var reference = DeletedPrefix + now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + "/" + branch;
        await Run(ct, "update-ref", reference, commit);
        await Run(ct, "branch", "-D", "--", branch);
        if (remote is not null && remoteBranch is not null) await backend.ExecuteAsync(Repository.Root, ["update-ref", "-d", GonePrefix + remote + "/" + remoteBranch], cancellationToken: ct);
        if (deleteRemote && remote is not null && remoteBranch is not null)
        {
            var exists = await backend.ExecuteAsync(Repository.Root, ["rev-parse", "--verify", "--quiet", "refs/remotes/" + remote + "/" + remoteBranch], cancellationToken: ct);
            if (exists.ExitCode == 0) await Run(ct, "push", remote, "--delete", "--", remoteBranch);
        }
        return new DeletedVariant(reference, branch, manifest.Data["variant"]?["title"]?.ToString() ?? branch, now, commit);
    }, ct);

    /// <summary>Variants in the local recycle bin, newest first.</summary>
    public async Task<IReadOnlyList<DeletedVariant>> DeletedVariantsAsync(CancellationToken ct = default)
    {
        var result = new List<DeletedVariant>();
        foreach (var row in (await Run(ct, "for-each-ref", "--format=%(refname)%00%(objectname)", DeletedPrefix)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(r => r.Split('\0')))
        {
            var rest = row[0][DeletedPrefix.Length..]; var slash = rest.IndexOf('/'); if (slash < 0) continue;
            if (!DateTimeOffset.TryParseExact(rest[..slash], "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)) continue;
            string title;
            try { title = YamlCodec.Read(await Run(ct, "show", row[1] + ":travel.yaml")).Data["variant"]?["title"]?.ToString() ?? rest[(slash + 1)..]; }
            catch (Exception ex) when (ex is DomainException or YamlDotNet.Core.YamlException) { title = rest[(slash + 1)..]; }
            result.Add(new(row[0], rest[(slash + 1)..], title, at, row[1]));
        }
        return result.OrderByDescending(d => d.DeletedAt).ToArray();
    }

    /// <summary>Restores a deleted variant as a local branch, under its old name or with a number when that is taken.</summary>
    public Task<string> RestoreVariantAsync(DeletedVariant deleted, CancellationToken ct = default) => Mutate(async () =>
    {
        var name = deleted.Branch;
        for (var i = 2; (await backend.ExecuteAsync(Repository.Root, ["rev-parse", "--verify", "--quiet", "refs/heads/" + name], cancellationToken: ct)).ExitCode == 0; i++) name = deleted.Branch + "-" + i;
        await Run(ct, "branch", "--", name, deleted.Commit);
        // A restored variant stays on this computer until it is shared again, so the next sync does not upload it unasked.
        await Run(ct, "config", "--local", "--bool", "branch." + name + "." + LocalOnlyKey, "true");
        await Run(ct, "update-ref", "-d", deleted.Reference);
        return name;
    }, ct);

    /// <summary>Removes deleted variants older than <paramref name="age"/> from the recycle bin. Returns how many were removed.</summary>
    public async Task<int> PurgeDeletedVariantsAsync(TimeSpan age, CancellationToken ct = default)
    {
        var removed = 0;
        foreach (var deleted in (await DeletedVariantsAsync(ct)).Where(d => DateTimeOffset.UtcNow - d.DeletedAt > age))
        { await Mutate(() => Run(ct, "update-ref", "-d", deleted.Reference), ct); removed++; }
        return removed;
    }

    /// <summary>
    /// Keeps a variant whose copy on the remote was deleted only on this computer: its tracking is removed and sync no
    /// longer uploads it, so it does not come back for the people who deleted it.
    /// </summary>
    public Task KeepLocalOnlyAsync(string branch, CancellationToken ct = default) => Mutate(async () =>
    {
        var remote = (await backend.ExecuteAsync(Repository.Root, ["config", "--get", "branch." + branch + ".remote"], cancellationToken: ct)).Output.Trim();
        if (remote.Length > 0) await backend.ExecuteAsync(Repository.Root, ["update-ref", "-d", GonePrefix + remote + "/" + await RemoteBranchAsync(remote, branch, ct)], cancellationToken: ct);
        await backend.ExecuteAsync(Repository.Root, ["branch", "--unset-upstream", "--", branch], cancellationToken: ct);
        await Run(ct, "config", "--local", "--bool", "branch." + branch + "." + LocalOnlyKey, "true");
    }, ct);

    /// <summary>Uploads a variant that was kept only on this computer, and syncs it again from now on.</summary>
    public Task ShareVariantAsync(string branch, string remote, CancellationToken ct = default) => Mutate(async () =>
    {
        await backend.ExecuteAsync(Repository.Root, ["config", "--local", "--unset", "branch." + branch + "." + LocalOnlyKey], cancellationToken: ct);
        await Run(ct, "push", "--set-upstream", "--", remote, "refs/heads/" + branch + ":refs/heads/" + branch);
    }, ct);

    /// <summary>
    /// Brings a variant that is not open in line with its copy on the remote without switching to it: newer versions
    /// from the remote are taken over when nothing local is in the way, and local versions are uploaded. A diverged
    /// variant is left alone and reported, because it needs a merge.
    /// </summary>
    public Task<SyncState> SyncVariantAsync(string branch, string remote, CancellationToken ct = default) => Mutate(async () =>
    {
        var current = (await backend.ExecuteAsync(Repository.Root, ["symbolic-ref", "--quiet", "--short", "HEAD"], cancellationToken: ct)).Output.Trim();
        if (branch == current) throw new DomainException("variant.current", "Use sync for the open variant.");
        if (await IsLocalOnlyAsync(branch, ct)) return SyncState.LocalOnly;
        var remoteBranch = await RemoteBranchAsync(remote, branch, ct); var target = "refs/remotes/" + remote + "/" + remoteBranch;
        var local = await Run(ct, "rev-parse", "refs/heads/" + branch);
        if ((await backend.ExecuteAsync(Repository.Root, ["rev-parse", "--verify", "--quiet", target], cancellationToken: ct)).ExitCode != 0)
        { await Run(ct, "push", "--set-upstream", "--", remote, "refs/heads/" + branch + ":refs/heads/" + remoteBranch); return SyncState.Synced; }
        var theirs = await Run(ct, "rev-parse", target);
        if (theirs == local) return SyncState.Synced;
        if ((await backend.ExecuteAsync(Repository.Root, ["merge-base", "--is-ancestor", local, theirs], cancellationToken: ct)).ExitCode == 0)
        { await Run(ct, "update-ref", "refs/heads/" + branch, theirs, local); return SyncState.Synced; }
        if ((await backend.ExecuteAsync(Repository.Root, ["merge-base", "--is-ancestor", theirs, local], cancellationToken: ct)).ExitCode == 0)
        { await Run(ct, "push", "--", remote, "refs/heads/" + branch + ":refs/heads/" + remoteBranch); return SyncState.Synced; }
        return SyncState.Diverged;
    }, ct);

    /// <summary>
    /// Marks the open variant as merged into another variant: it is archived and records which variant and version took
    /// it over, so every client can show it as taken over. Creates a small administrative version.
    /// </summary>
    public Task MarkMergedAsync(string intoVariantId, string intoCommit, CancellationToken ct = default) => Mutate(async () =>
    {
        var state = await Repository.ReadAsync(ct); var manifest = state.Trip.Manifest.Copy();
        manifest.Data["variant"]!["state"] = "archived";
        manifest.Data["variant"]!["merged_into"] = new JsonObject { ["variant_id"] = intoVariantId, ["commit"] = intoCommit, ["at"] = NodaTime.SystemClock.Instance.GetCurrentInstant().ToString() };
        await Repository.ApplyAsync(state, [new(manifest.Id, manifest)], ct);
        await CreateVersionCoreAsync("Mark variant as merged", "variant-metadata", ct);
    }, ct);
}
