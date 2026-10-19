using System.Text.RegularExpressions;
using TravelRepo.Core;

namespace TravelRepo.Git;

/// <summary>The kind of a failed Git command, recognized from Git's own message.</summary>
public enum GitFailure
{
    Unknown,
    /// <summary>The host rejected or asked for credentials.</summary>
    Authentication,
    /// <summary>The host could not be reached.</summary>
    Network,
    /// <summary>A lock file such as <c>.git/index.lock</c> exists, usually left behind by a Git process that stopped.</summary>
    Locked,
    /// <summary>The host refused the push because it has newer versions.</summary>
    Rejected,
    /// <summary>Files in the folder would be overwritten by incoming versions.</summary>
    WouldOverwrite,
    /// <summary>A merge, rebase or similar operation started outside the client is not finished.</summary>
    OperationInProgress,
    /// <summary>The folder is not on a branch.</summary>
    DetachedHead,
    /// <summary>Git refuses the folder because another user owns it.</summary>
    DubiousOwnership,
    /// <summary>The folder is not a Git repository.</summary>
    NotARepository,
    /// <summary>There was nothing to save as a version.</summary>
    NothingToCommit
}

/// <summary>
/// A Git command that failed. <see cref="Exception.Message"/> is a short public sentence; <see cref="Details"/> holds
/// the command, exit code and Git's message with credentials, tokens and the home folder removed, for a person to
/// copy into a bug report. Nothing is sent anywhere.
/// </summary>
public sealed class GitCommandException(string code, string message, GitFailure failure, string command, int exitCode, string output, IReadOnlyList<string> paths)
    : DomainException(code, message)
{
    public GitFailure Failure { get; } = failure;
    /// <summary>The command as run, for example <c>git fetch -- origin</c>.</summary>
    public string Command { get; } = command;
    public int ExitCode { get; } = exitCode;
    /// <summary>Git's error output, redacted.</summary>
    public string Output { get; } = output;
    /// <summary>Paths Git named, for example files that would be overwritten.</summary>
    public IReadOnlyList<string> Paths { get; } = paths;
    public string Details => Command + "\nexit " + ExitCode + (Output.Length > 0 ? "\n" + Output : "");

    internal static GitCommandException From(IReadOnlyList<string> arguments, GitResult result, string? code = null, string? message = null)
    {
        var raw = result.Error.Length > 0 ? result.Error : result.Output;
        var failure = GitErrorText.Classify(raw);
        var (defaultCode, defaultMessage) = Describe(failure, arguments.Count > 0 ? arguments[0] : "");
        // A recognized cause is more useful than the caller's generic code.
        var useOwn = failure is GitFailure.Unknown && code is not null;
        return new(useOwn ? code! : defaultCode, useOwn ? message ?? defaultMessage : defaultMessage, failure, "git " + GitErrorText.Redact(string.Join(' ', arguments)), result.ExitCode, GitErrorText.Redact(raw.Trim()), GitErrorText.Paths(raw));
    }

    private static (string Code, string Message) Describe(GitFailure failure, string command) => failure switch
    {
        GitFailure.Authentication => ("git.auth", "The host did not accept the sign-in."),
        GitFailure.Network => ("git.network", "The host could not be reached."),
        GitFailure.Locked => ("git.locked", "Another Git process is using the trip folder, or one stopped and left a lock file behind."),
        GitFailure.Rejected => ("git.rejected", "The host has newer versions. Get them first, then try again."),
        GitFailure.WouldOverwrite => ("git.overwrite", "Files in the trip folder would be overwritten by incoming versions."),
        GitFailure.OperationInProgress => ("git.in_progress", "A Git operation started outside this app is not finished."),
        GitFailure.DetachedHead => ("git.detached", "The trip folder is not on a plan variant."),
        GitFailure.DubiousOwnership => ("git.ownership", "Git does not trust the trip folder because another user owns it."),
        GitFailure.NotARepository => ("git.not_repository", "The trip folder is not a Git repository."),
        GitFailure.NothingToCommit => ("version.empty", "There are no changes to save as a version."),
        _ => ("git.failed", "Git " + command + " failed. Check repository state, network access, and credentials.")
    };
}

/// <summary>Reads and cleans Git's error messages. Git runs with <c>LC_ALL=C</c>, so messages are in English.</summary>
public static partial class GitErrorText
{
    public static GitFailure Classify(string text)
    {
        bool Has(params string[] parts) => parts.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase));
        if (Has("dubious ownership")) return GitFailure.DubiousOwnership;
        if (Has("not a git repository")) return GitFailure.NotARepository;
        if (Has(".lock': File exists", "index.lock", "Another git process seems to be running", "cannot lock ref", "Unable to create") && Has("lock")) return GitFailure.Locked;
        if (Has("You have not concluded your merge", "MERGE_HEAD exists", "rebase in progress", "rebase-merge", "rebase-apply", "cherry-pick is already in progress", "you need to resolve your current index first", "unmerged files", "needs merge")) return GitFailure.OperationInProgress;
        if (Has("would be overwritten by")) return GitFailure.WouldOverwrite;
        if (Has("[rejected]", "non-fast-forward", "Updates were rejected", "fetch first", "failed to push some refs")) return GitFailure.Rejected;
        if (Has("Authentication failed", "could not read Username", "could not read Password", "terminal prompts disabled", "Permission denied (publickey", "Invalid username or password", "returned error: 401", "returned error: 403", "Repository not found", "Host key verification failed")) return GitFailure.Authentication;
        if (Has("Could not resolve host", "Could not resolve hostname", "Connection timed out", "Connection refused", "Network is unreachable", "Operation timed out", "Failed to connect", "unable to access", "The remote end hung up", "early EOF", "Connection reset", "SSL", "TLS")) return GitFailure.Network;
        if (Has("is not a symbolic ref", "HEAD detached", "not currently on a branch")) return GitFailure.DetachedHead;
        if (Has("nothing to commit", "nothing added to commit", "no changes added to commit")) return GitFailure.NothingToCommit;
        return GitFailure.Unknown;
    }

    /// <summary>Removes credentials in URLs, authorization headers, access tokens and the home folder. Long output is shortened.</summary>
    public static string Redact(string text)
    {
        var result = UserInfo().Replace(text, "$1***@");
        result = Authorization().Replace(result, "$1[removed]");
        result = Token().Replace(result, "[token]");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
        if (home.Length > 1) result = result.Replace(home, "~", OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        return result.Length > 4000 ? result[..4000] + "\n[...]" : result;
    }

    /// <summary>Indented paths Git lists below a message, for example files that would be overwritten.</summary>
    public static IReadOnlyList<string> Paths(string text) =>
        text.Split('\n').Where(l => l.StartsWith('\t')).Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().ToArray();

    [GeneratedRegex(@"([a-z][a-z0-9+.-]*://)[^/@\s]+@", RegexOptions.IgnoreCase)] private static partial Regex UserInfo();
    [GeneratedRegex(@"((?:Authorization|Proxy-Authorization)\s*:\s*)[^\r\n]+", RegexOptions.IgnoreCase)] private static partial Regex Authorization();
    [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|glpat-[A-Za-z0-9_-]{20,})\b")] private static partial Regex Token();
}
