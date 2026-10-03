using System.Text;
using TravelRepo.Core;
using TravelRepo.Serialization;
namespace TravelRepo.Repository;

public sealed record RepairFile(string Path, string? CurrentHash, byte[]? Replacement);
public sealed record RepairPlan(IReadOnlyList<RepairFile> Changes, TripSnapshot Target);
public sealed partial class TravelRepository
{
    /// <summary>Preview a full canonical rollback, including malformed files. Before-images are retained outside the trip.</summary>
    public async Task<RepairPlan> PreviewRestoreAsync(TripSnapshot target, CancellationToken ct = default)
    {
        var issues = target.All.SelectMany(SchemaValidation.Validate).Concat(SemanticValidation.Validate(target)).Where(d => d.Severity == Severity.Error).ToArray();
        if (issues.Length > 0) throw new DomainException("repair.target", "The selected version is not a valid restore target.");
        var desired = target.All.ToDictionary(EntityPath, e => Encoding.UTF8.GetBytes(YamlCodec.Write(e)), StringComparer.Ordinal);
        foreach (var resource in target.Resources) desired[resource.Key] = resource.Value;
        var current = await CanonicalBytesAsync(ct); var changes = new List<RepairFile>();
        foreach (var path in current.Keys.Union(desired.Keys))
        {
            var before = current.GetValueOrDefault(path); var after = desired.GetValueOrDefault(path);
            if (!Equal(before, after)) changes.Add(new(path, before is null ? null : Hash(before), after));
        }
        return new(changes, target);
    }
    private async Task<Dictionary<string, byte[]>> CanonicalBytesAsync(CancellationToken ct)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (File.Exists(SafePath("travel.yaml"))) result["travel.yaml"] = await File.ReadAllBytesAsync(SafePath("travel.yaml"), ct);
        foreach (var folder in Folders.Values.Distinct().Concat(new[] { ".travelrepo", "assets" }))
        {
            var directory = SafePath(folder); if (!Directory.Exists(directory)) continue;
            foreach (var path in Directory.EnumerateFiles(directory, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                var relative = Path.GetRelativePath(Root, path).Replace('\\', '/');
                if (relative.EndsWith(".yaml", StringComparison.Ordinal) || folder is "assets" or "documents") result[relative] = await File.ReadAllBytesAsync(SafePath(relative), ct);
            }
        }
        return result;
    }
    public async Task<RepositoryState> ApplyRestoreAsync(RepairPlan reviewed, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(RecoveryRoot); await using var ownership = new FileStream(Path.Combine(RecoveryRoot, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var fresh = await PreviewRestoreAsync(reviewed.Target, ct);
            if (fresh.Changes.Count != reviewed.Changes.Count || fresh.Changes.Any(c => !reviewed.Changes.Any(r => r.Path == c.Path && r.CurrentHash == c.CurrentHash && Equal(r.Replacement, c.Replacement)))) throw new DomainException("repair.changed", "The repository changed since this repair was reviewed. Review it again.");
            await WriteTransactionAsync(fresh.Changes.Select(c => new FileChange(c.Path, c.Replacement)).ToArray(), ct); return await ReadAsync(ct);
        }
        finally { gate.Release(); }
    }
}
