using TravelRepo.Git;
using TravelRepo.Git.LibGit2;

namespace TravelRepo.Tests;

/// <summary>The Git backend under test. Set TRAVELREPO_TEST_GIT=libgit2 to run the Git tests through libgit2 instead of the git program.</summary>
internal static class TestGit
{
    public static bool UsesLibGit2 => Environment.GetEnvironmentVariable("TRAVELREPO_TEST_GIT") == "libgit2";
    public static IGitBackend Backend() => UsesLibGit2 ? new LibGit2Backend() : new GitCliBackend();
}
