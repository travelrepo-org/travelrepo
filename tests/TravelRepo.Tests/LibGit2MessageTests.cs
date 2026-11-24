using LibGit2Sharp;
using TravelRepo.Git.LibGit2;
using Xunit;
namespace TravelRepo.Tests;

/// <summary>
/// LibGit2Backend cleans up commit messages itself, because libgit2's own cleanup returns nothing under NativeAOT.
/// Its result must match libgit2's, which works here (JIT).
/// </summary>
public sealed class LibGit2MessageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "travelrepo-message-" + Guid.NewGuid().ToString("N"));
    public void Dispose() => TestFiles.Delete(root);

    [Theory]
    [InlineData("Plan the Kyoto day trip")]
    [InlineData("Plan the Kyoto day trip\n")]
    [InlineData("\n\n  Plan the Kyoto day trip  \n\n\n\nTake the 8:10 train from Kyoto to Nara.\t \n\n")]
    [InlineData("Add Sam\r\n\r\nTravelRepo-Version: 1\r\nTravelRepo-Action: user-version\r\n")]
    [InlineData("Line one\n \n\t\nLine two")]
    [InlineData("# not a comment here\nShinkansen to Osaka")]
    [InlineData("")]
    [InlineData("  \n \n")]
    public void MessagesAreCleanedUpLikeLibGit2(string message)
    {
        using var repo = new LibGit2Sharp.Repository(LibGit2Sharp.Repository.Init(root, isBare: true));
        var signature = new Signature("Alex", "alex@example.invalid", new DateTimeOffset(2027, 5, 12, 9, 0, 0, TimeSpan.FromHours(2)));
        var tree = repo.ObjectDatabase.CreateTree(new TreeDefinition());
        var commit = repo.ObjectDatabase.CreateCommit(signature, signature, message, tree, [], prettifyMessage: true);
        Assert.Equal(commit.Message, LibGit2Backend.Prettify(message));
    }
}
