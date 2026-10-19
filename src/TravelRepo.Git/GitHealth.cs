using System.Globalization;
using TravelRepo.Core;
using TravelRepo.Repository;

namespace TravelRepo.Git;

/// <summary>
/// The Git state of a trip folder as far as it matters for saving and syncing. <see cref="Summary"/> is plain text
/// for a bug report and contains no remote addresses or credentials.
/// </summary>
public sealed record GitHealth(
    string GitVersion,
    string? Branch,
    string? Operation,
    string? StaleLock,
    TimeSpan? LockAge,
    int TripChanges,
    int OtherFiles,
    string? Remote,
    string? RemoteBranch,
    bool RemoteBranchKnown,
    int Ahead,
    int Behind)
{
    public bool Detached => Branch is null;
    public string Summary() => string.Join('\n', new[]
    {
        "git: " + GitVersion,
        "branch: " + (Branch ?? "(detached)"),
        Operation is null ? null : "unfinished operation: " + Operation,
        StaleLock is null ? null : "lock file: " + StaleLock + " (" + (LockAge?.TotalSeconds ?? 0).ToString("0", CultureInfo.InvariantCulture) + " s old)",
        "changed trip files: " + TripChanges + ", other changed files: " + OtherFiles,
        Remote is null ? "remote: none" : "remote: " + Remote + "/" + RemoteBranch + (RemoteBranchKnown ? ", ahead " + Ahead + ", behind " + Behind : ", not fetched yet")
    }.Where(l => l is not null));
}

public sealed partial class GitRepository
{
    private static readonly string[] Locks = ["index.lock", "HEAD.lock", "config.lock", "shallow.lock"];
    private static readonly (string Marker, string Name, string Abort)[] Operations =
        [("MERGE_HEAD", "merge", "merge"), ("rebase-merge", "rebase", "rebase"), ("rebase-apply", "rebase", "rebase"), ("CHERRY_PICK_HEAD", "cherry-pick", "cherry-pick"), ("REVERT_HEAD", "revert", "revert")];

    private async Task<string> GitDirAsync(CancellationToken ct) => await Run(ct, "rev-parse", "--absolute-git-dir");

    /// <summary>Reads the local Git state without contacting the network.</summary>
    public async Task<GitHealth> DiagnoseAsync(string? remote, CancellationToken ct = default)
    {
        var version = (await backend.ExecuteAsync(Repository.Root, ["--version"], cancellationToken: ct)).Output.Trim().Replace("git version ", "", StringComparison.Ordinal);
        var gitDir = await GitDirAsync(ct);
        var head = await backend.ExecuteAsync(Repository.Root, ["symbolic-ref", "--quiet", "--short", "HEAD"], cancellationToken: ct);
        var branch = head.ExitCode == 0 ? head.Output.Trim() : null;
        var operation = Operations.FirstOrDefault(o => Path.Exists(Path.Combine(gitDir, o.Marker))).Name;
        var (lockPath, lockAge) = FindLock(gitDir);
        var trip = (await StatusAsync(ct)).Split('\0', StringSplitOptions.RemoveEmptyEntries).Length;
        var all = (await Run(ct, "status", "--porcelain=v1", "-z")).Split('\0', StringSplitOptions.RemoveEmptyEntries).Length;
        string? remoteBranch = null; var known = false; int ahead = 0, behind = 0;
        if (remote is not null && branch is not null)
        {
            remoteBranch = await RemoteBranchAsync(remote, branch, ct);
            var target = "refs/remotes/" + remote + "/" + remoteBranch;
            known = (await backend.ExecuteAsync(Repository.Root, ["rev-parse", "--verify", "--quiet", target], cancellationToken: ct)).ExitCode == 0;
            if (known)
            {
                var counts = (await Run(ct, "rev-list", "--left-right", "--count", "HEAD..." + target)).Split('\t', ' ');
                ahead = int.Parse(counts[0], CultureInfo.InvariantCulture); behind = int.Parse(counts[^1], CultureInfo.InvariantCulture);
            }
        }
        return new(version, branch, operation, lockPath is null ? null : ".git/" + Path.GetRelativePath(gitDir, lockPath).Replace('\\', '/'), lockAge, trip, Math.Max(0, all - trip), remote, remoteBranch, known, ahead, behind);
    }

    private static (string? Path, TimeSpan? Age) FindLock(string gitDir)
    {
        foreach (var name in Locks)
        {
            var path = Path.Combine(gitDir, name);
            if (File.Exists(path)) return (path, DateTime.UtcNow - File.GetLastWriteTimeUtc(path));
        }
        return (null, null);
    }

    /// <summary>
    /// Removes a lock file that a stopped Git process left behind. A lock younger than <paramref name="minimumAge"/>
    /// may belong to a running process and is kept. Returns whether a lock was removed.
    /// </summary>
    public Task<bool> RemoveStaleLockAsync(TimeSpan minimumAge, CancellationToken ct = default) => Mutate(async () =>
    {
        var (path, age) = FindLock(await GitDirAsync(ct));
        if (path is null || age < minimumAge) return false;
        File.Delete(path); return true;
    }, ct);

    /// <summary>
    /// Cancels a merge, rebase, cherry-pick or revert that was started outside the client, restoring the state before
    /// it. Changes made during that operation are lost, so clients ask first.
    /// </summary>
    public Task AbortOperationAsync(CancellationToken ct = default) => Mutate(async () =>
    {
        var gitDir = await GitDirAsync(ct);
        var operation = Operations.FirstOrDefault(o => Path.Exists(Path.Combine(gitDir, o.Marker)));
        if (operation.Name is null) return;
        await Run(ct, operation.Abort, "--abort");
    }, ct);

    /// <summary>
    /// Moves files that block incoming versions out of the trip folder into a dated folder below the recovery
    /// directory, keeping their relative paths. Returns that folder. Nothing is deleted.
    /// </summary>
    public Task<string> MoveAsideAsync(IReadOnlyList<string> paths, CancellationToken ct = default) => Mutate(() =>
    {
        var target = Path.Combine(Repository.RecoveryRoot, "moved-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        foreach (var relative in paths)
        {
            var source = Repository.SafePath(relative);
            if (!File.Exists(source)) continue;
            var destination = Path.Combine(target, relative); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(source, destination);
        }
        return Task.FromResult(target);
    }, ct);
}
