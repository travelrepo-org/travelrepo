using System.Diagnostics;
using TravelRepo.Core;
using TravelRepo.Git;
using TravelRepo.Repository;
using Xunit;
namespace TravelRepo.Tests;

/// <summary>Attached e-mails keep their CRLF line endings in history, whatever the user's Git is set to convert.</summary>
public sealed class LineEndingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "travelrepo-eol-" + Guid.NewGuid());
    private readonly IGitBackend backend = TestGit.Backend();
    public void Dispose() => TestFiles.Delete(root);

    private const string Mail = "From: hotel@example.invalid\r\nSubject: Booking Pinewood Lodge\r\n\r\nConfirmed for two nights in Wanaka.\r\n";

    private async Task<(TravelRepository, GitRepository)> Trip()
    {
        var repo = new TravelRepository(Path.Combine(root, "trip"), Path.Combine(root, "recovery")); await repo.InitializeAsync(Entity.CreateTrip("New Zealand"));
        var git = new GitRepository(repo, backend); await git.InitializeAsync("First", "first@example.invalid");
        return (repo, git);
    }

    /// <summary>Pushes the trip to a bare remote and returns the errors a fresh copy of it reports.</summary>
    private async Task<IReadOnlyList<Diagnostic>> ErrorsOfACopy(GitRepository git)
    {
        var remote = Path.Combine(root, "remote.git"); Directory.CreateDirectory(remote);
        Assert.Equal(0, (await backend.ExecuteAsync(remote, ["init", "--bare", "-b", "main"])).ExitCode);
        await git.AddRemoteAsync("origin", remote); await git.PushAsync("origin");
        var copy = Path.Combine(root, "copy"); await GitRepository.CloneAsync(backend, remote, copy);
        return (await new TravelRepository(copy, Path.Combine(root, "recovery-copy")).ReadAsync()).Diagnostics.Where(d => d.Severity == Severity.Error).ToArray();
    }

    private async Task AttachMail(TravelRepository repo)
    {
        var file = Path.Combine(root, "booking.eml"); await File.WriteAllTextAsync(file, Mail);
        await repo.ImportDocumentAsync(file, "message/rfc822");
    }

    [Fact]
    public async Task EveryTripStoresItsFilesByteForByte()
    {
        var (repo, git) = await Trip();
        Assert.Contains(GitRepository.AttributesRule, await File.ReadAllLinesAsync(Path.Combine(repo.Root, ".gitattributes")));
        Assert.Empty(await git.StatusAsync());
        // The traveller's Git converts line endings, as core.autocrlf=input does on many Macs.
        Assert.Equal(0, (await backend.ExecuteAsync(repo.Root, ["config", "--local", "core.autocrlf", "input"])).ExitCode);
        await AttachMail(repo); await git.CreateVersionAsync("Attach booking");
        Assert.Empty(await ErrorsOfACopy(git));
    }

    [Fact]
    public async Task ATripWhoseMailsWereConvertedIsRepairedByItsNextVersion()
    {
        if (TestGit.UsesLibGit2) return; // The broken history is made with the git program.
        var (repo, git) = await Trip();
        // A trip from before the rule, whose e-mail Git stored with LF while the folder keeps the CRLF original.
        File.Delete(Path.Combine(repo.Root, ".gitattributes"));
        await AttachMail(repo);
        RawGit(repo.Root, "-c", "core.autocrlf=input", "add", "-A");
        RawGit(repo.Root, "commit", "-q", "-m", "Attach booking");
        Assert.NotEmpty(await ErrorsOfACopy(git));
        TestFiles.Delete(Path.Combine(root, "copy")); TestFiles.Delete(Path.Combine(root, "remote.git")); RawGit(repo.Root, "remote", "remove", "origin");

        var item = Entity.Create("schedule_item", "Wanaka"); await repo.ApplyAsync(await repo.ReadAsync(), [new(item.Id, item)]);
        await git.CreateVersionAsync("Add Wanaka");
        Assert.Contains(GitRepository.AttributesRule, await File.ReadAllLinesAsync(Path.Combine(repo.Root, ".gitattributes")));
        Assert.Empty(await git.StatusAsync());
        Assert.Empty(await ErrorsOfACopy(git));
    }

    private static void RawGit(string directory, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) start.ArgumentList.Add(a);
        using var process = Process.Start(start)!; process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }
}
