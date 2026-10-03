using System.Security.Cryptography;
using TravelRepo.Core;

namespace TravelRepo.Git;

public sealed partial class GitRepository
{
    /// <summary>
    /// Copies a standalone working repository, including uncommitted files, Git history, all refs,
    /// local configuration and remotes, into an empty destination. The source is not changed.
    /// Linked worktrees, symbolic links and shared object stores require a separate Git clone.
    /// A concurrent source change aborts publication of the copy.
    /// </summary>
    public Task CopyWorkingTreeAsync(string destination, CancellationToken ct = default) => Mutate(async () =>
    {
        destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (destination.Equals(Repository.Root, comparison) || destination.StartsWith(Repository.Root + Path.DirectorySeparatorChar, comparison))
            throw new DomainException("copy.nested", "Choose a folder outside the current trip.");
        if (File.Exists(destination) || Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            throw new DomainException("folder.not_empty", "Choose an empty destination folder.");
        if (Directory.Exists(destination) && new DirectoryInfo(destination).LinkTarget is not null)
            throw new DomainException("path.symlink", "Choose an actual directory, not a symbolic link.");
        if (!Directory.Exists(Path.Combine(Repository.Root, ".git")) || File.Exists(Path.Combine(Repository.Root, ".git", "objects", "info", "alternates")))
            throw new DomainException("copy.linked", "This repository uses an external Git store. Use a standalone clone before copying it.");
        var parent = Path.GetDirectoryName(destination)!; Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".jourfold-copy-" + Guid.NewGuid());
        Directory.CreateDirectory(staging);
        try
        {
            string[] Files()
            {
                var files = new List<string>();
                void Visit(string folder)
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
                    {
                        ct.ThrowIfCancellationRequested(); var attributes = File.GetAttributes(entry);
                        if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new DomainException("path.symlink", "Repository copies cannot contain symbolic links.");
                        if (attributes.HasFlag(FileAttributes.Directory)) Visit(entry);
                        else { if (entry.StartsWith(Path.Combine(Repository.Root, ".git") + Path.DirectorySeparatorChar, comparison) && entry.EndsWith(".lock", StringComparison.Ordinal)) throw new DomainException("copy.busy", "Wait for the current Git operation to finish."); files.Add(Path.GetRelativePath(Repository.Root, entry)); }
                    }
                }
                Visit(Repository.Root); return files.Order(StringComparer.Ordinal).ToArray();
            }
            var files = Files(); var hashes = new Dictionary<string, byte[]>();
            foreach (var relative in files)
            {
                var source = Path.Combine(Repository.Root, relative); var target = Path.Combine(staging, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var input = File.OpenRead(source); await using var output = File.Create(target);
                await input.CopyToAsync(output, ct); await output.FlushAsync(ct); input.Position = 0;
                hashes[relative] = await SHA256.HashDataAsync(input, ct);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, File.GetUnixFileMode(source));
            }
            if (!files.SequenceEqual(Files())) throw new DomainException("copy.changed", "The source changed while it was being copied. Try again.");
            foreach (var relative in files)
            {
                await using var source = File.OpenRead(Path.Combine(Repository.Root, relative));
                await using var copy = File.OpenRead(Path.Combine(staging, relative));
                var sourceHash = await SHA256.HashDataAsync(source, ct); var copyHash = await SHA256.HashDataAsync(copy, ct);
                if (!hashes[relative].SequenceEqual(sourceHash) || !hashes[relative].SequenceEqual(copyHash))
                    throw new DomainException("copy.changed", "The source changed while it was being copied. Try again.");
            }
            if (Directory.Exists(destination)) Directory.Delete(destination); // Succeeds only if still empty.
            Directory.Move(staging, destination);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }, ct);
}
