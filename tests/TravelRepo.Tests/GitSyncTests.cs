using TravelRepo.Core;
using TravelRepo.Git;
using TravelRepo.Repository;
using Xunit;
namespace TravelRepo.Tests;

public sealed class GitSyncTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "travelrepo-sync-" + Guid.NewGuid());
    private readonly GitCliBackend backend = new();
    public void Dispose() => TestFiles.Delete(root);

    private string Remote => Path.Combine(root, "remote.git");

    /// <summary>A trip pushed to a bare remote, and a second clone of it, as two travellers would have.</summary>
    private async Task<(TravelRepository, GitRepository, TravelRepository, GitRepository)> TwoClones()
    {
        var repo = new TravelRepository(Path.Combine(root, "first"), Path.Combine(root, "recovery-first")); await repo.InitializeAsync(Entity.CreateTrip("New Zealand"));
        var git = new GitRepository(repo, backend); await git.InitializeAsync("First", "first@example.invalid");
        Directory.CreateDirectory(Remote); Assert.Equal(0, (await backend.ExecuteAsync(Remote, ["init", "--bare", "-b", "main"])).ExitCode);
        await git.AddRemoteAsync("origin", Remote); await git.PushAsync("origin");
        var second = Path.Combine(root, "second"); await GitRepository.CloneAsync(backend, Remote, second);
        var other = new TravelRepository(second, Path.Combine(root, "recovery-second")); var otherGit = new GitRepository(other, backend); await otherGit.SetIdentityAsync("Second", "second@example.invalid");
        return (repo, git, other, otherGit);
    }

    private static async Task Add(TravelRepository repo, GitRepository git, string title)
    {
        var item = Entity.Create("schedule_item", title); await repo.ApplyAsync(await repo.ReadAsync(), [new(item.Id, item)]); await git.CreateVersionAsync("Add " + title);
    }

    [Fact]
    public async Task FilesOutsideTheTripDoNotCountAsChanges()
    {
        var (repo, git, _, _) = await TwoClones();
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "packing.txt"), "socks");
        Assert.Empty(await git.StatusAsync());
        Assert.Equal(SyncState.Synced, await git.SyncAsync("origin"));
        var health = await git.DiagnoseAsync("origin");
        Assert.Equal(0, health.TripChanges); Assert.Equal(1, health.OtherFiles); Assert.Equal("main", health.RemoteBranch);
    }

    [Fact]
    public async Task ARemoteVariantKeepsItsNameAndSyncsBothWays()
    {
        var (repo, git, other, otherGit) = await TwoClones();
        await git.CreateVariantAsync("variants/road-trip", "Road trip"); await git.PushAsync("origin");
        await otherGit.FetchAsync("origin");
        await otherGit.SwitchAsync("origin/variants/road-trip");
        Assert.Equal("variants/road-trip", await otherGit.CurrentBranchAsync());

        await Add(repo, git, "Hot pools"); await git.PushAsync("origin");
        Assert.Equal(SyncState.Synced, await otherGit.SyncAsync("origin"));
        Assert.Contains((await other.ReadAsync()).Trip.Entities.Values, e => e.Title == "Hot pools");

        await Add(other, otherGit, "Glow worms");
        Assert.Equal(SyncState.Synced, await otherGit.SyncAsync("origin"));
        Assert.Equal(SyncState.Synced, await git.SyncAsync("origin"));
        Assert.Contains((await repo.ReadAsync()).Trip.Entities.Values, e => e.Title == "Glow worms");
        var branches = (await backend.ExecuteAsync(Remote, ["for-each-ref", "--format=%(refname:short)", "refs/heads"])).Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["main", "variants/road-trip"], branches.Order().ToArray());
    }

    [Fact]
    public async Task ABranchWithAnotherLocalNameSyncsWithTheBranchItTracks()
    {
        // Older clients created a local branch with a generated name when switching to a variant from the host.
        var (repo, git, other, otherGit) = await TwoClones();
        await git.CreateVariantAsync("variants/road-trip", "Road trip"); await git.PushAsync("origin");
        await otherGit.FetchAsync("origin");
        Assert.Equal(0, (await backend.ExecuteAsync(other.Root, ["switch", "--track", "-c", "variants/import-0001", "origin/variants/road-trip"])).ExitCode);
        await Add(other, otherGit, "Kayaking");
        Assert.Equal(SyncState.Synced, await otherGit.SyncAsync("origin"));
        Assert.Equal(SyncState.Synced, await git.SyncAsync("origin"));
        Assert.Contains((await repo.ReadAsync()).Trip.Entities.Values, e => e.Title == "Kayaking");
        var branches = (await backend.ExecuteAsync(Remote, ["for-each-ref", "--format=%(refname:short)", "refs/heads"])).Output;
        Assert.DoesNotContain("import", branches);
    }

    [Fact]
    public async Task FilesThatBlockIncomingVersionsCanBeMovedAside()
    {
        var (repo, git, other, otherGit) = await TwoClones();
        // Another tool committed a file outside the trip content; the second traveller has an unrelated file there.
        Directory.CreateDirectory(Path.Combine(repo.Root, "extra")); await File.WriteAllTextAsync(Path.Combine(repo.Root, "extra", "info.txt"), "from first");
        Assert.Equal(0, (await backend.ExecuteAsync(repo.Root, ["add", "extra/info.txt"])).ExitCode);
        Assert.Equal(0, (await backend.ExecuteAsync(repo.Root, ["commit", "-m", "Add info"])).ExitCode); await git.PushAsync("origin");
        Directory.CreateDirectory(Path.Combine(other.Root, "extra")); await File.WriteAllTextAsync(Path.Combine(other.Root, "extra", "info.txt"), "local");

        var error = await Assert.ThrowsAsync<GitCommandException>(() => otherGit.SyncAsync("origin"));
        Assert.Equal(GitFailure.WouldOverwrite, error.Failure); Assert.Equal("git.overwrite", error.Code);
        Assert.Equal(["extra/info.txt"], error.Paths.ToArray());
        Assert.StartsWith("git merge --ff-only", error.Details);

        var moved = await otherGit.MoveAsideAsync(error.Paths);
        Assert.Equal("local", await File.ReadAllTextAsync(Path.Combine(moved, "extra", "info.txt")));
        Assert.Equal(SyncState.Synced, await otherGit.SyncAsync("origin"));
        Assert.Equal("from first", await File.ReadAllTextAsync(Path.Combine(other.Root, "extra", "info.txt")));
    }

    [Fact]
    public async Task AStaleLockIsReportedAndCanBeRemoved()
    {
        var (repo, git, _, _) = await TwoClones();
        var lockFile = Path.Combine(repo.Root, ".git", "index.lock"); await File.WriteAllTextAsync(lockFile, "");
        var item = Entity.Create("place", "Rotorua"); await repo.ApplyAsync(await repo.ReadAsync(), [new(item.Id, item)]);
        var error = await Assert.ThrowsAsync<GitCommandException>(() => git.CreateVersionAsync("Add Rotorua"));
        Assert.Equal(GitFailure.Locked, error.Failure);
        Assert.Equal(".git/index.lock", (await git.DiagnoseAsync("origin")).StaleLock);
        Assert.False(await git.RemoveStaleLockAsync(TimeSpan.FromMinutes(5)));
        File.SetLastWriteTimeUtc(lockFile, DateTime.UtcNow.AddMinutes(-10));
        Assert.True(await git.RemoveStaleLockAsync(TimeSpan.FromMinutes(5)));
        await git.CreateVersionAsync("Add Rotorua");
    }

    [Fact]
    public async Task UnreachableRemotesExplainTheFailureWithoutCredentials()
    {
        var (_, git, _, _) = await TwoClones();
        await git.AddRemoteAsync("broken", Path.Combine(root, "missing.git"));
        var error = await Assert.ThrowsAsync<GitCommandException>(() => git.FetchAsync("broken"));
        Assert.Equal("git fetch --prune -- broken", error.Command); Assert.NotEqual(0, error.ExitCode); Assert.NotEmpty(error.Output);
    }

    [Theory]
    [InlineData("fatal: unable to access 'https://github.com/a/b.git/': Could not resolve host: github.com", GitFailure.Network)]
    [InlineData("fatal: could not read Username for 'https://github.com': terminal prompts disabled", GitFailure.Authentication)]
    [InlineData("git@github.com: Permission denied (publickey).", GitFailure.Authentication)]
    [InlineData(" ! [rejected]        main -> main (fetch first)\nerror: failed to push some refs", GitFailure.Rejected)]
    [InlineData("fatal: Unable to create '/trip/.git/index.lock': File exists.", GitFailure.Locked)]
    [InlineData("error: You have not concluded your merge (MERGE_HEAD exists).", GitFailure.OperationInProgress)]
    [InlineData("fatal: detected dubious ownership in repository at '/trip'", GitFailure.DubiousOwnership)]
    [InlineData("fatal: ref HEAD is not a symbolic ref", GitFailure.DetachedHead)]
    [InlineData("nothing to commit, working tree clean", GitFailure.NothingToCommit)]
    [InlineData("something else entirely", GitFailure.Unknown)]
    public void GitMessagesAreClassified(string message, GitFailure expected) => Assert.Equal(expected, GitErrorText.Classify(message));

    [Fact]
    public void RedactionRemovesCredentialsTokensAndHome()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var text = GitErrorText.Redact("fatal: https://alex:secret@github.com/a/b.git\nAuthorization: Basic YWxleDpzZWNyZXQ=\ntoken ghp_abcdefghijklmnopqrstuvwxyz0123\nat " + Path.Combine(home, "Trips", "nz"));
        Assert.DoesNotContain("secret", text); Assert.DoesNotContain("YWxleDpzZWNyZXQ", text); Assert.DoesNotContain("ghp_", text);
        Assert.Contains("https://***@github.com", text); Assert.Contains("~", text);
        if (home.Length > 1) Assert.DoesNotContain(home, text);
    }

    [Fact]
    public async Task TheOverviewCombinesLocalAndRemoteCopiesWithTheirState()
    {
        var (repo, git, other, otherGit) = await TwoClones();
        await git.CreateVariantAsync("variants/road-trip", "Road trip"); await git.PushAsync("origin");
        await otherGit.FetchAsync("origin");
        var before = await otherGit.VariantOverviewAsync("origin");
        var remoteOnly = Assert.Single(before, v => v.Title == "Road trip");
        Assert.True(remoteOnly.RemoteOnly); Assert.True(Assert.Single(before, v => v.Primary).Current);

        await otherGit.SwitchAsync("origin/variants/road-trip");
        await Add(repo, git, "Hot pools"); await git.PushAsync("origin"); await otherGit.FetchAsync("origin");
        var behind = Assert.Single(await otherGit.VariantOverviewAsync("origin"), v => v.Title == "Road trip");
        Assert.Equal(("variants/road-trip", "origin/variants/road-trip"), (behind.LocalBranch, behind.RemoteBranch));
        Assert.Equal((0, 1), (behind.Ahead, behind.Behind));
        await Add(other, otherGit, "Glow worms");
        var diverged = Assert.Single(await otherGit.VariantOverviewAsync("origin"), v => v.Title == "Road trip");
        Assert.True(diverged.Diverged); Assert.Equal(1, diverged.OnlyHere); Assert.True(diverged.Current);
    }

    [Fact]
    public async Task DeletingAVariantKeepsItRestorableAndCanDeleteTheRemoteCopy()
    {
        var (repo, git, _, _) = await TwoClones();
        await git.CreateVariantAsync("variants/road-trip", "Road trip"); await git.PushAsync("origin");
        await Assert.ThrowsAsync<DomainException>(() => git.DeleteVariantAsync("variants/road-trip", false, "origin"));
        await git.SwitchAsync("main");
        await Assert.ThrowsAsync<DomainException>(() => git.DeleteVariantAsync("main", false, "origin"));
        var deleted = await git.DeleteVariantAsync("variants/road-trip", true, "origin");
        Assert.Equal("Road trip", deleted.Title);
        Assert.DoesNotContain("road-trip", (await backend.ExecuteAsync(Remote, ["for-each-ref", "refs/heads"])).Output);
        var bin = Assert.Single(await git.DeletedVariantsAsync()); Assert.Equal("variants/road-trip", bin.Branch);

        var restored = await git.RestoreVariantAsync(bin);
        Assert.Equal("variants/road-trip", restored); Assert.Empty(await git.DeletedVariantsAsync());
        Assert.True(await git.IsLocalOnlyAsync(restored));
        Assert.Contains(await git.VariantOverviewAsync("origin"), v => v.LocalBranch == restored && v.LocalOnly && !v.OnRemote);

        await git.DeleteVariantAsync(restored, false, "origin");
        Assert.Equal(0, await git.PurgeDeletedVariantsAsync(TimeSpan.FromDays(30)));
        Assert.Equal(1, await git.PurgeDeletedVariantsAsync(TimeSpan.Zero));
    }

    [Fact]
    public async Task AVariantDeletedOnTheRemoteIsReportedAndCanBeKeptWithoutComingBack()
    {
        var (repo, git, other, otherGit) = await TwoClones();
        await git.CreateVariantAsync("variants/road-trip", "Road trip"); await git.CreateVariantAsync("variants/cheap", "Cheap"); await git.PushAsync("origin");
        await git.SwitchAsync("variants/road-trip"); await git.PushAsync("origin"); await git.SwitchAsync("main");
        await otherGit.FetchAsync("origin");
        await otherGit.SwitchAsync("origin/variants/road-trip"); await Add(other, otherGit, "Only mine");
        await otherGit.SwitchAsync("origin/variants/cheap"); await otherGit.SwitchAsync("main");

        await git.DeleteVariantAsync("variants/road-trip", true, "origin"); await git.DeleteVariantAsync("variants/cheap", true, "origin");
        await otherGit.SyncAsync("origin");
        var gone = (await otherGit.VariantOverviewAsync("origin")).Where(v => v.UpstreamGone).OrderBy(v => v.Title).ToArray();
        Assert.Equal(["Cheap", "Road trip"], gone.Select(v => v.Title).ToArray());
        Assert.Equal(0, gone[0].OnlyHere); Assert.Equal(1, gone[1].OnlyHere);

        await otherGit.KeepLocalOnlyAsync("variants/road-trip"); await otherGit.DeleteVariantAsync("variants/cheap", false, "origin");
        await otherGit.SwitchAsync("variants/road-trip");
        Assert.Equal(SyncState.LocalOnly, await otherGit.SyncAsync("origin"));
        Assert.Equal(SyncState.LocalOnly, (await otherGit.SyncStatusAsync("origin")).State);
        Assert.DoesNotContain("road-trip", (await backend.ExecuteAsync(Remote, ["for-each-ref", "refs/heads"])).Output);

        await otherGit.ShareVariantAsync("variants/road-trip", "origin");
        Assert.Contains("road-trip", (await backend.ExecuteAsync(Remote, ["for-each-ref", "refs/heads"])).Output);
        Assert.False(await otherGit.IsLocalOnlyAsync("variants/road-trip"));
    }

    [Fact]
    public async Task VariantsThatAreNotOpenSyncWithoutSwitching()
    {
        var (repo, git, other, otherGit) = await TwoClones();
        await git.CreateVariantAsync("variants/road-trip", "Road trip"); await git.PushAsync("origin"); await git.SwitchAsync("main");
        await otherGit.FetchAsync("origin"); await otherGit.SwitchAsync("origin/variants/road-trip"); await otherGit.SwitchAsync("main");
        await git.SwitchAsync("variants/road-trip"); await Add(repo, git, "Hot pools"); await git.PushAsync("origin"); await git.SwitchAsync("main");
        await otherGit.FetchAsync("origin");
        Assert.Equal(SyncState.Synced, await otherGit.SyncVariantAsync("variants/road-trip", "origin"));
        var synced = Assert.Single(await otherGit.VariantOverviewAsync("origin"), v => v.Title == "Road trip");
        Assert.Equal((0, 0), (synced.Ahead, synced.Behind));
        Assert.Equal("main", await otherGit.CurrentBranchAsync());

        // Taken over: the variant records which plan merged it.
        await git.SwitchAsync("variants/road-trip"); await git.MarkMergedAsync(Guid.CreateVersion7().ToString(), "abc"); await git.SwitchAsync("main");
        var merged = Assert.Single(await git.VariantOverviewAsync("origin"), v => v.LocalBranch == "variants/road-trip");
        Assert.True(merged.Merged); Assert.True(merged.Archived);
        Assert.Empty(TravelRepo.Serialization.SchemaValidation.Validate(new Entity(merged.Manifest)));
    }
}
