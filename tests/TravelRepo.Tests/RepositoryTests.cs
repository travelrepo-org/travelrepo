using TravelRepo.Core;
using TravelRepo.Git;
using TravelRepo.Repository;
using Xunit;
namespace TravelRepo.Tests;

public sealed class RepositoryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "travelrepo-tests-" + Guid.NewGuid());
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private async Task<(TravelRepository, GitRepository)> Create(string folder = "trip")
    {
        var repo = new TravelRepository(Path.Combine(root, folder), Path.Combine(root, "recovery-" + folder)); await repo.InitializeAsync(Entity.CreateTrip("Aachen")); var git = new GitRepository(repo, new GitCliBackend()); await git.InitializeAsync("Test", "test@example.invalid"); return (repo, git);
    }
    [Fact]
    public async Task CopyPreservesBranchesHistoryDirtyFilesAndRemotesWithoutChangingSource()
    {
        var (repo, git) = await Create(); var person = Entity.Create("person", "Alex");
        await repo.ApplyAsync(await repo.ReadAsync(), [new(person.Id, person)]); await git.CreateVersionAsync("Add Alex");
        await git.CreateVariantAsync("alternative", "Alternative"); await git.SwitchAsync("main");
        await git.AddRemoteAsync("origin", "https://example.invalid/trip.git");
        person.Data["display_name"] = "Local edit"; await repo.ApplyAsync(await repo.ReadAsync(), [new(person.Id, person)]);
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "untracked.txt"), "keep me");
        var destination = Path.Combine(root, "copy"); await git.CopyWorkingTreeAsync(destination);
        var copy = new TravelRepository(destination); var copiedGit = new GitRepository(copy, new GitCliBackend());
        Assert.Equal(await git.HeadAsync(), await copiedGit.HeadAsync()); Assert.Equal(await git.StatusAsync(), await copiedGit.StatusAsync());
        Assert.Equal(2, (await copiedGit.VariantsAsync()).Count); Assert.Equal("Local edit", (await copy.ReadAsync()).Trip.Find(person.Id)!.Title);
        Assert.Equal("keep me", await File.ReadAllTextAsync(Path.Combine(destination, "untracked.txt"))); Assert.Equal("https://example.invalid/trip.git", (await copiedGit.RemotesAsync())["origin"]);
        await File.WriteAllTextAsync(Path.Combine(destination, "untracked.txt"), "independent"); Assert.Equal("keep me", await File.ReadAllTextAsync(Path.Combine(repo.Root, "untracked.txt")));
        await Assert.ThrowsAsync<DomainException>(() => git.CopyWorkingTreeAsync(destination)); await Assert.ThrowsAsync<DomainException>(() => git.CopyWorkingTreeAsync(Path.Combine(repo.Root, "nested")));
    }
    [Fact]
    public async Task CopyRejectsLinksAndDoesNotPublishPartialDestination()
    {
        if (OperatingSystem.IsWindows()) return;
        var (repo, git) = await Create(); var outside = Path.Combine(root, "outside.txt"); await File.WriteAllTextAsync(outside, "private");
        File.CreateSymbolicLink(Path.Combine(repo.Root, "link"), outside); var destination = Path.Combine(root, "copy");
        await Assert.ThrowsAsync<DomainException>(() => git.CopyWorkingTreeAsync(destination)); Assert.False(Directory.Exists(destination)); Assert.Equal("private", await File.ReadAllTextAsync(outside));
        Assert.Empty(Directory.EnumerateDirectories(root, ".jourfold-copy-*"));
    }
    [Fact]
    public async Task RealGitVariantsRemotesAndOfflineReopen()
    {
        var (repo, git) = await Create(); Assert.Equal("main", await git.CurrentBranchAsync()); var state = await repo.ReadAsync();
        var person = Entity.Create("person", "Alex"); await repo.ApplyAsync(state, [new(person.Id, person)]); Assert.NotEmpty(await git.StatusAsync()); Assert.Single(await git.HistoryAsync());
        await Assert.ThrowsAsync<DomainException>(() => git.CreateVariantAsync("idea", "Idea"));
        await git.CreateVersionAsync("Add Alex"); await git.CreateVariantAsync("idea", "Idea"); Assert.Equal(2, (await git.VariantsAsync()).Count);
        await git.SwitchAsync("main"); Assert.Equal(state.Trip.Manifest.Id, (await repo.ReadAsync()).Trip.Manifest.Id);
        Directory.CreateDirectory(Path.Combine(root, "remote.git")); var backend = new GitCliBackend(); Assert.Equal(0, (await backend.ExecuteAsync(Path.Combine(root, "remote.git"), ["init", "--bare"])).ExitCode);
        await git.AddRemoteAsync("one", Path.Combine(root, "remote.git")); await git.AddRemoteAsync("two", Path.Combine(root, "remote.git")); await git.PushAsync("one"); await git.FetchAsync("two"); Assert.Equal(2, (await git.RemotesAsync()).Count);
        Assert.True((await git.HistoryAsync()).All(h => h.ApplicationGenerated)); Assert.Single((await new TravelRepository(repo.Root).ReadAsync()).Trip.Entities);
    }
    [Fact]
    public async Task StaleWriteDoesNotDestroyExternalEdit()
    {
        var (repo, _) = await Create(); var state = await repo.ReadAsync(); var p = repo.SafePath("travel.yaml"); await File.WriteAllTextAsync(p, "external: preserved\n" + await File.ReadAllTextAsync(p));
        var changed = state.Trip.Manifest.Copy(); changed.Data["title"] = "Bad overwrite";
        Assert.Equal("repository.changed", (await Assert.ThrowsAsync<DomainException>(() => repo.ApplyAsync(state, [new(changed.Id, changed)]))).Code); Assert.Contains("external: preserved", await File.ReadAllTextAsync(p));
    }
    [Fact]
    public async Task BlobDeduplicationAndLargeFileGate()
    {
        var (repo, _) = await Create(); var file = Path.Combine(root, "image.png"); await File.WriteAllBytesAsync(file, [1, 2, 3, 4]);
        var a = await repo.ImportDocumentAsync(file, "image/png"); var b = await repo.ImportDocumentAsync(file, "image/png"); Assert.Equal(repo.DocumentPath(a), repo.DocumentPath(b)); Assert.NotEqual(a.Id, b.Id);
        Assert.Single(Directory.GetFiles(Path.Combine(repo.Root, "assets"), "*", SearchOption.AllDirectories));
        await using (var large = File.Create(Path.Combine(root, "large"))) large.SetLength(26 * 1024 * 1024);
        await Assert.ThrowsAsync<DomainException>(() => repo.ImportDocumentAsync(Path.Combine(root, "large"), "application/octet-stream"));
    }
    [Fact]
    public async Task ReferencesUseIdentityAfterFileRename()
    { var (repo, _) = await Create(); var state = await repo.ReadAsync(); var p = Entity.Create("person", "Alex"); await repo.ApplyAsync(state, [new(p.Id, p)]); File.Move(repo.SafePath("people/" + p.Id + ".yaml"), repo.SafePath("people/renamed.yaml")); Assert.Equal(p.Id, Assert.Single((await repo.ReadAsync()).Trip.Entities.Values).Id); }
    [Theory][InlineData("../outside")][InlineData(".git/config")] public void PathsAreContained(string path) => Assert.Throws<DomainException>(() => new TravelRepository(root).SafePath(path));
    [Fact]
    public async Task RepairRestoresMalformedYamlOnlyAfterReviewAndRetainsBackup()
    {
        var (repo, git) = await Create(); var target = await git.SnapshotAsync("HEAD");
        await File.WriteAllTextAsync(repo.SafePath("travel.yaml"), "not: [valid");
        var plan = await repo.PreviewRestoreAsync(target); Assert.Single(plan.Changes);
        Assert.Equal("not: [valid", await File.ReadAllTextAsync(repo.SafePath("travel.yaml")));
        var restored = await repo.ApplyRestoreAsync(plan); Assert.Empty(restored.Diagnostics);
        Assert.NotEmpty(Directory.EnumerateFiles(repo.RecoveryRoot, "*.complete"));
    }
    [Fact]
    public async Task ReviewedRepairRejectsSubsequentExternalChanges()
    {
        var (repo, git) = await Create(); var target = await git.SnapshotAsync("HEAD");
        await File.WriteAllTextAsync(repo.SafePath("travel.yaml"), "broken"); var plan = await repo.PreviewRestoreAsync(target);
        await File.WriteAllTextAsync(repo.SafePath("travel.yaml"), "new external content");
        Assert.Equal("repair.changed", (await Assert.ThrowsAsync<DomainException>(() => repo.ApplyRestoreAsync(plan))).Code);
    }

}
