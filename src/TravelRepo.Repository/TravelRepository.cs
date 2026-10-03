using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TravelRepo.Core;
using TravelRepo.Serialization;

namespace TravelRepo.Repository;

public sealed record StoredEntity(string Path, string Hash, Entity Entity);
public sealed record RepositoryState(TripSnapshot Trip, IReadOnlyDictionary<Guid, StoredEntity> Files, IReadOnlyList<Diagnostic> Diagnostics);
public sealed record EntityEdit(Guid Id, Entity? Value);
public sealed record ResourceEdit(string Path, byte[]? Bytes);

/// <summary>Filesystem adapter with optimistic concurrency, recoverable transactions and lossless YAML writes.</summary>
public sealed partial class TravelRepository
{
    public string Root { get; }
    public string RecoveryRoot { get; }
    private readonly SemaphoreSlim gate = new(1, 1);
    public static readonly IReadOnlyDictionary<string, string> Folders = new Dictionary<string, string>
    { ["person"] = "people", ["place"] = "places", ["schedule_item"] = "schedule", ["booking"] = "bookings", ["task"] = "tasks", ["expense"] = "expenses", ["budget"] = "budgets", ["collection"] = "collections", ["comment"] = "comments", ["document"] = "documents", ["note"] = "documents" };
    public TravelRepository(string root, string? recoveryRoot = null)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (new DirectoryInfo(Root).LinkTarget is not null) throw new DomainException("path.symlink", "Open the actual repository directory, not a symbolic link.");
        RecoveryRoot = recoveryRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TravelRepo", "recovery", Hash(Encoding.UTF8.GetBytes(Root)));
    }
    public static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
    public string SafePath(string relative)
    {
        var full = Path.GetFullPath(Path.Combine(Root, relative));
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || relative.Split('/', '\\').Any(x => x == ".git")) throw new DomainException("path.escape", "Path is outside trip content.");
        var cursor = full;
        while (cursor.Length > Root.Length)
        {
            if (File.Exists(cursor) || Directory.Exists(cursor))
                if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) throw new DomainException("path.symlink", "Symbolic links are not accepted for writable trip content.");
            cursor = Path.GetDirectoryName(cursor)!;
        }
        return full;
    }
    public async Task<RepositoryState> ReadAsync(CancellationToken cancellationToken = default)
    {
        var files = new Dictionary<Guid, StoredEntity>(); var diagnostics = new List<Diagnostic>(); Entity? manifest = null;
        var paths = new List<string> { "travel.yaml" };
        foreach (var folder in Folders.Values.Distinct().Append(".travelrepo"))
        {
            var directory = SafePath(folder);
            if (Directory.Exists(directory)) paths.AddRange(Directory.EnumerateFiles(directory, "*.yaml", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).Select(p => Path.GetRelativePath(Root, p)));
        }
        foreach (var path in paths.Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var bytes = await File.ReadAllBytesAsync(SafePath(path), cancellationToken); var entity = YamlCodec.Read(new UTF8Encoding(false, true).GetString(bytes));
                diagnostics.AddRange(SchemaValidation.Validate(entity));
                if (!Guid.TryParse(entity.Data["id"]?.ToString(), out var id)) continue;
                if (!files.TryAdd(id, new(path, Hash(bytes), entity))) diagnostics.Add(new("id.duplicate", Severity.Error, path, "An entity ID occurs more than once."));
                if (path == "travel.yaml") manifest = entity;
            }
            catch (Exception ex) when (ex is IOException or DomainException or YamlDotNet.Core.YamlException or DecoderFallbackException)
            { diagnostics.Add(new("read.invalid", Severity.Error, path, ex.Message)); }
        }
        if (manifest is null) throw new DomainException("manifest.invalid", string.Join("\n", diagnostics.Select(d => d.Path + ": " + d.Message)));
        var resources = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var folder in new[] { "documents", "assets" })
        {
            var directory = SafePath(folder);
            if (!Directory.Exists(directory)) continue;
            foreach (var file in Directory.EnumerateFiles(directory, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                if (file.EndsWith(".yaml", StringComparison.Ordinal)) continue;
                var relative = Path.GetRelativePath(Root, file).Replace('\\', '/');
                resources[relative] = await File.ReadAllBytesAsync(SafePath(relative), cancellationToken);
            }
        }
        var trip = new TripSnapshot(manifest, files.Where(p => p.Key != manifest.Id).ToDictionary(p => p.Key, p => p.Value.Entity)) { Resources = resources };
        if (!diagnostics.Any(d => d.Severity == Severity.Error)) diagnostics.AddRange(SemanticValidation.Validate(trip));
        return new(trip, files, diagnostics);
    }
    public async Task<RepositoryState> ApplyAsync(RepositoryState expected, IReadOnlyList<EntityEdit> edits, CancellationToken cancellationToken = default, IReadOnlyList<ResourceEdit>? resourceEdits = null)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(RecoveryRoot);
            await using var ownership = new FileStream(Path.Combine(RecoveryRoot, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var current = await ReadAsync(cancellationToken);
            if (!SameFiles(expected, current)) throw new DomainException("repository.changed", "The trip changed outside this operation. Reload before editing; undo history must be cleared.");
            if (current.Diagnostics.Any(d => d.Severity == Severity.Error)) throw new DomainException("repository.invalid", "Repair the invalid repository before editing.");
            var entities = current.Trip.Entities.ToDictionary(p => p.Key, p => p.Value.Copy()); var manifest = current.Trip.Manifest.Copy();
            foreach (var edit in edits)
            {
                if (edit.Value is not null && edit.Id != edit.Value.Id) throw new DomainException("id.change", "Entity identity cannot change during an edit.");
                if (edit.Id == manifest.Id) manifest = edit.Value?.Copy() ?? throw new DomainException("manifest.delete", "Cannot delete the manifest.");
                else if (edit.Value is null) entities.Remove(edit.Id); else entities[edit.Id] = edit.Value.Copy();
            }
            var resources = current.Trip.Resources.ToDictionary(p => p.Key, p => p.Value);
            foreach (var edit in resourceEdits ?? [])
            {
                SafePath(edit.Path);
                if (!(edit.Path.StartsWith("documents/", StringComparison.Ordinal) || edit.Path.StartsWith("assets/sha256/", StringComparison.Ordinal)) || edit.Path.EndsWith(".yaml", StringComparison.Ordinal)) throw new DomainException("resource.path", "Resource edits must target document content or hashed assets.");
                if (edit.Bytes is null) resources.Remove(edit.Path); else resources[edit.Path] = edit.Bytes;
            }
            var trip = new TripSnapshot(manifest, entities) { Resources = resources };
            var issues = trip.All.SelectMany(SchemaValidation.Validate).ToList();
            if (!issues.Any()) issues.AddRange(SemanticValidation.Validate(trip));
            if (issues.Any(d => d.Severity == Severity.Error)) throw new DomainException("command.invalid", string.Join("\n", issues.Where(d => d.Severity == Severity.Error).Select(d => d.Code + ": " + d.Message)));
            var changes = edits.DistinctBy(e => e.Id).Select(e => new FileChange(current.Files.GetValueOrDefault(e.Id)?.Path ?? EntityPath(e.Value!), e.Value is null ? null : Encoding.UTF8.GetBytes(YamlCodec.Write(e.Value)))).ToArray();
            await WriteTransactionAsync(changes.Concat((resourceEdits ?? []).Select(e => new FileChange(e.Path, e.Bytes))).ToArray(), cancellationToken);
            return await ReadAsync(cancellationToken);
        }
        finally { gate.Release(); }
    }
    public static bool SameFiles(RepositoryState a, RepositoryState b) => a.Trip.Resources.Count == b.Trip.Resources.Count && a.Trip.Resources.All(p => b.Trip.Resources.TryGetValue(p.Key, out var bytes) && Equal(p.Value, bytes)) && a.Files.Count == b.Files.Count && a.Files.All(p => b.Files.TryGetValue(p.Key, out var f) && p.Value.Hash == f.Hash && p.Value.Path == f.Path);
    private static string EntityPath(Entity e) => e.Type == "trip" ? "travel.yaml" : (Folders.GetValueOrDefault(e.Type) ?? ".travelrepo") + "/" + e.Id + ".yaml";
    private sealed record FileChange(string Path, byte[]? Bytes);
    private sealed record Backup(string Path, byte[]? Before, byte[]? After);
    private async Task WriteTransactionAsync(IReadOnlyList<FileChange> changes, CancellationToken cancellationToken)
    {
        var backups = new List<Backup>();
        foreach (var c in changes) { var p = SafePath(c.Path); backups.Add(new(c.Path, File.Exists(p) ? await File.ReadAllBytesAsync(p, cancellationToken) : null, c.Bytes)); }
        var journal = Path.Combine(RecoveryRoot, Guid.NewGuid() + ".json");
        await ReplaceAsync(journal, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(backups)));
        try
        {
            foreach (var c in changes) { cancellationToken.ThrowIfCancellationRequested(); await ReplaceAsync(SafePath(c.Path), c.Bytes); }
            File.Move(journal, journal + ".complete");
        }
        catch
        {
            foreach (var b in backups) await ReplaceAsync(SafePath(b.Path), b.Before);
            File.Move(journal, journal + ".rolledback"); throw;
        }
    }
    /// <summary>Restore interrupted writes only when files still match a before/after image.</summary>
    public async Task RecoverAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(RecoveryRoot)) return;
        foreach (var file in Directory.EnumerateFiles(RecoveryRoot, "*.json"))
        {
            var backups = JsonSerializer.Deserialize<List<Backup>>(await File.ReadAllTextAsync(file, cancellationToken))!;
            foreach (var b in backups)
            {
                var p = SafePath(b.Path); var bytes = File.Exists(p) ? await File.ReadAllBytesAsync(p, cancellationToken) : null;
                if (!Equal(bytes, b.Before) && !Equal(bytes, b.After)) throw new DomainException("recovery.external", "Recovery needs review because an external edit changed a transaction file.");
            }
            foreach (var b in backups) await ReplaceAsync(SafePath(b.Path), b.Before);
            File.Move(file, file + ".recovered");
        }
    }
    private static bool Equal(byte[]? a, byte[]? b) => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);
    public static async Task ReplaceAsync(string path, byte[]? bytes)
    {
        if (bytes is null) { File.Delete(path); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temp = path + "." + Guid.NewGuid() + ".tmp";
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { await file.WriteAsync(bytes); file.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public async Task InitializeAsync(Entity manifest, CancellationToken cancellationToken = default)
    {
        if (Directory.Exists(Root) && Directory.EnumerateFileSystemEntries(Root).Any(p => Path.GetFileName(p) != ".git")) throw new DomainException("repository.exists", "Choose an empty directory.");
        var diagnostics = SchemaValidation.Validate(manifest);
        if (diagnostics.Count > 0) throw new DomainException("manifest.invalid", diagnostics[0].Message);
        Directory.CreateDirectory(Root); await ReplaceAsync(SafePath("travel.yaml"), Encoding.UTF8.GetBytes(YamlCodec.Write(manifest)));
        await ReplaceAsync(SafePath("README.md"), Encoding.UTF8.GetBytes("# " + manifest.Title + "\n\nA TravelRepo trip. Open with any compatible client.\n"));
    }
    public async Task<Entity> ImportDocumentAsync(string path, string mediaType, bool allowLarge = false, CancellationToken cancellationToken = default)
    {
        if (new FileInfo(path).Length > 25 * 1024 * 1024 && !allowLarge) throw new DomainException("asset.large", "This file exceeds 25 MB. Confirm before adding it to trip history.");
        var state = await ReadAsync(cancellationToken); var import = await PrepareDocumentAsync(path, mediaType, allowLarge, cancellationToken);
        await ApplyAsync(state, [new(import.Document.Id, import.Document)], cancellationToken, [import.Resource]); return import.Document;
    }
    /// <summary>Prepare an import without changing the repository, for a client's atomic undoable command.</summary>
    public static async Task<(Entity Document, ResourceEdit Resource)> PrepareDocumentAsync(string path, string mediaType, bool allowLarge = false, CancellationToken ct = default)
    {
        if (new FileInfo(path).Length > 25 * 1024 * 1024 && !allowLarge) throw new DomainException("asset.large", "This file exceeds 25 MB. Confirm before adding it to trip history.");
        var bytes = await File.ReadAllBytesAsync(path, ct); var hash = Hash(bytes);
        var document = Entity.Create("document", Path.GetFileName(path)); document.Data["media_type"] = mediaType; document.Data["blob"] = new JsonObject { ["algorithm"] = "sha256", ["hash"] = hash };
        return (document, new("assets/sha256/" + hash[..2] + "/" + hash, bytes));
    }
    public string DocumentPath(Entity doc)
    {
        var hash = doc.Data["blob"]?["hash"]?.ToString() ?? "";
        if (hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c))) throw new DomainException("asset.hash", "Invalid blob hash.");
        return SafePath("assets/sha256/" + hash[..2] + "/" + hash);
    }
}
