using System.Diagnostics;
using System.Text.Json.Nodes;
using TravelRepo.Core;
using TravelRepo.Repository;
using TravelRepo.Serialization;

namespace TravelRepo.Git;

public sealed record GitResult(int ExitCode, string Output, byte[]? Bytes = null);
public interface IGitBackend
{
    Task<GitResult> ExecuteAsync(string directory, IReadOnlyList<string> arguments, string? input = null, CancellationToken cancellationToken = default);
}
/// <summary>Real Git CLI adapter. Arguments never pass through a shell; configured SSH and credential helpers are reused.</summary>
public sealed record GitCredential(string Username, string Password);
public interface ICredentialBroker { Task<GitCredential?> GetAsync(Uri origin, CancellationToken ct = default); }
public sealed class GitCliBackend(string? executable = null, ICredentialBroker? credentialBroker = null) : IGitBackend
{
    public string Executable { get; } = executable ?? ResolveExecutable();
    private static string ResolveExecutable()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue; var p = Path.Combine(dir, OperatingSystem.IsWindows() ? "git.exe" : "git"); if (File.Exists(p) && IsCompatible(p)) return p;
        }
        var fallback = Path.Combine(AppContext.BaseDirectory, "git", "cmd", "git.exe");
        if (OperatingSystem.IsWindows() && File.Exists(fallback) && IsCompatible(fallback)) return fallback;
        throw new DomainException("git.missing", "Git is unavailable. Install the package's Git dependency or select a compatible Git executable.");
    }
    /// <summary>v1 needs Git 2.34 or later. Probe before choosing system Git over packaged MinGit.</summary>
    public static bool IsCompatible(string path)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path) { ArgumentList = { "--version" }, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
            if (process is null) return false;
            if (!process.WaitForExit(3000)) { process.Kill(true); return false; }
            var match = System.Text.RegularExpressions.Regex.Match(process.StandardOutput.ReadToEnd(), @"git version (\d+)\.(\d+)");
            return process.ExitCode == 0 && match.Success && new Version(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value)) >= new Version(2, 34);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }
    public async Task<GitResult> ExecuteAsync(string directory, IReadOnlyList<string> arguments, string? input = null, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var start = new ProcessStartInfo(Executable) { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
        start.Environment["GIT_TERMINAL_PROMPT"] = "0"; start.Environment["LC_ALL"] = "C"; start.Environment["GCM_INTERACTIVE"] = "never";
        if (credentialBroker is not null && await credentialBroker.GetAsync(new Uri("https://github.com"), timeout.Token) is { } credential)
        {
            start.Environment["GIT_CONFIG_COUNT"] = "1";
            start.Environment["GIT_CONFIG_KEY_0"] = "http.https://github.com/.extraHeader";
            start.Environment["GIT_CONFIG_VALUE_0"] = "Authorization: Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(credential.Username + ":" + credential.Password));
        }
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Unable to start Git.");
        using var registration = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        using var buffer = new MemoryStream(); var output = process.StandardOutput.BaseStream.CopyToAsync(buffer, timeout.Token); var error = process.StandardError.ReadToEndAsync(timeout.Token);
        if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), timeout.Token);
        process.StandardInput.Close(); await process.WaitForExitAsync(timeout.Token);
        // stderr can contain sensitive remote details. Expected failures use stable public errors.
        await error;
        await output; var bytes = buffer.ToArray(); return new(process.ExitCode, System.Text.Encoding.UTF8.GetString(bytes), bytes);
    }
}
public sealed record Variant(string Branch, string Commit, Entity Manifest, bool IsRemote = false);
public sealed record VersionEntry(string Commit, string Parents, string Author, string Email, string Timestamp, string Message, bool ApplicationGenerated, string Committer = "", string CommitterEmail = "", string Refs = "");
public enum SyncState { LocalOnly, Synced, LocalChanges, Diverged, RemoteChanges }

/// <summary>Git-backed versions, variants and safe synchronization. Mutations are serialized per service instance.</summary>
public sealed partial class GitRepository(TravelRepository repository, IGitBackend backend)
{
    public TravelRepository Repository { get; } = repository;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> Gates = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private SemaphoreSlim Gate => Gates.GetOrAdd(Repository.Root, _ => new SemaphoreSlim(1, 1));
    private async Task Mutate(Func<Task> operation, CancellationToken ct) { await Gate.WaitAsync(ct); try { await operation(); } finally { Gate.Release(); } }
    private async Task<T> Mutate<T>(Func<Task<T>> operation, CancellationToken ct) { await Gate.WaitAsync(ct); try { return await operation(); } finally { Gate.Release(); } }
    public Task InitializeAsync(string name, string email, CancellationToken ct = default) => Mutate(() => InitializeCoreAsync(name, email, ct), ct);
    public Task SetIdentityAsync(string name, string email, CancellationToken ct = default) => Mutate(() => SetIdentityCoreAsync(name, email, ct), ct);
    public Task<string> CreateVersionAsync(string message, string action = "user-version", CancellationToken ct = default) => Mutate(() => CreateVersionCoreAsync(message, action, ct), ct);
    public Task CreateVariantAsync(string branch, string title, CancellationToken ct = default) => Mutate(() => CreateVariantCoreAsync(branch, title, ct), ct);
    public Task SwitchAsync(string branch, CancellationToken ct = default) => Mutate(() => SwitchCoreAsync(branch, ct), ct);
    public Task UpdateVariantAsync(string? title = null, bool archive = false, CancellationToken ct = default) => Mutate(() => UpdateVariantCoreAsync(title, archive, ct), ct);
    public Task AddRemoteAsync(string name, string url, CancellationToken ct = default) => Mutate(() => AddRemoteCoreAsync(name, url, ct), ct);
    public Task FetchAsync(string remote, CancellationToken ct = default) => Mutate(() => FetchCoreAsync(remote, ct), ct);
    public Task PushAsync(string remote, CancellationToken ct = default) => Mutate(() => PushCoreAsync(remote, ct), ct);
    public Task<SyncState> SyncAsync(string remote, CancellationToken ct = default) => Mutate(() => SyncCoreAsync(remote, ct), ct);
    public Task<string> RecordMergeAsync(string otherCommit, string message, CancellationToken ct = default) => Mutate(() => RecordMergeCoreAsync(otherCommit, message, ct), ct);
    public Task RollbackAsync(string revision, CancellationToken ct = default) => Mutate(() => RollbackCoreAsync(revision, ct), ct);
    private async Task<string> Run(CancellationToken ct, params string[] args)
    {
        var r = await backend.ExecuteAsync(Repository.Root, args, cancellationToken: ct);
        if (r.ExitCode != 0) throw new DomainException("git.failed", "Git " + args[0] + " failed (exit " + r.ExitCode + "). Check repository state, network access, and credentials.");
        return r.Output.TrimEnd('\r', '\n');
    }
    private async Task InitializeCoreAsync(string name, string email, CancellationToken ct = default)
    {
        await Run(ct, "init", "-b", "main"); await SetIdentityCoreAsync(name, email, ct);
        await CreateVersionCoreAsync("Create trip", "initialize", ct);
    }
    private async Task SetIdentityCoreAsync(string name, string email, CancellationToken ct = default)
    { await Run(ct, "config", "--local", "user.name", name); await Run(ct, "config", "--local", "user.email", email); }
    public Task<string> StatusAsync(CancellationToken ct = default) => Run(ct, "status", "--porcelain=v1", "-z");
    public Task<string> CurrentBranchAsync(CancellationToken ct = default) => Run(ct, "symbolic-ref", "--short", "HEAD");
    public Task<string> HeadAsync(CancellationToken ct = default) => Run(ct, "rev-parse", "HEAD");
    private async Task<string> CreateVersionCoreAsync(string message, string action = "user-version", CancellationToken ct = default)
    {
        {
            var state = await Repository.ReadAsync(ct);
            if (state.Diagnostics.Any(d => d.Severity == Severity.Error)) throw new DomainException("version.invalid", "Repair validation errors before creating a version.");
            await StageAsync(ct);
            await CheckIndexAsync(ct);
            var result = await backend.ExecuteAsync(Repository.Root, ["commit", "-F", "-"], message.Trim() + "\n\nTravelRepo-Version: 1\nTravelRepo-Action: " + action.Replace("\n", "") + "\nTravelRepo-Client: TravelRepo/0.1.0\n", ct);
            if (result.ExitCode != 0) throw new DomainException("version.failed", "No version was created. Check that changes exist and a local identity is configured.");
            return await HeadAsync(ct);
        }
    }
    private async Task CheckIndexAsync(CancellationToken ct)
    {
        var staged = (await Run(ct, "diff", "--cached", "--name-only", "-z")).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var roots = TravelRepository.Folders.Values.Distinct().Concat(new[] { "assets", ".travelrepo" }).ToArray();
        if (staged.Any(p => p is not ("travel.yaml" or "README.md") && !roots.Any(root => p.StartsWith(root + "/", StringComparison.Ordinal)))) throw new DomainException("version.foreign_index", "Other files are already staged by an external tool. Commit or unstage those files before creating a trip version.");
    }
    private async Task StageAsync(CancellationToken ct)
    {
        var tracked = (await Run(ct, "ls-files", "-z")).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        foreach (var path in new[] { "travel.yaml", "README.md" }.Concat(TravelRepository.Folders.Values.Distinct()).Concat(new[] { "assets", ".travelrepo" }))
            if (File.Exists(Repository.SafePath(path)) || Directory.Exists(Repository.SafePath(path)) || tracked.Any(p => p == path || p.StartsWith(path + "/", StringComparison.Ordinal))) await Run(ct, "add", "-A", "--", path);
    }
    public async Task<IReadOnlyList<Variant>> VariantsAsync(CancellationToken ct = default)
    {
        var result = new List<Variant>(); var refs = await Run(ct, "for-each-ref", "--format=%(refname:short)%00%(objectname)%00%(refname)", "refs/heads", "refs/remotes");
        foreach (var row in refs.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = row.Split('\0'); var content = await backend.ExecuteAsync(Repository.Root, ["show", parts[1] + ":travel.yaml"], cancellationToken: ct);
            if (content.ExitCode != 0) continue;
            try { var manifest = YamlCodec.Read(content.Output); if (SchemaValidation.Validate(manifest).Count == 0) result.Add(new(parts[0], parts[1], manifest, parts[2].StartsWith("refs/remotes/", StringComparison.Ordinal))); } catch (Exception ex) when (ex is DomainException or YamlDotNet.Core.YamlException) { }
        }
        return result;
    }
    private async Task CreateVariantCoreAsync(string branch, string title, CancellationToken ct = default)
    {
        await RequireClean(ct); await Run(ct, "check-ref-format", "--branch", branch);
        var state = await Repository.ReadAsync(ct); var parentBranch = await CurrentBranchAsync(ct); var commit = await HeadAsync(ct); var parentId = state.Trip.Manifest.Data["variant"]!["id"]!.ToString();
        await Run(ct, "switch", "-c", branch); var manifest = state.Trip.Manifest.Copy();
        manifest.Data["variant"] = new JsonObject { ["id"] = Guid.CreateVersion7().ToString(), ["title"] = title, ["role"] = "variant", ["state"] = "active", ["parent"] = new JsonObject { ["variant_id"] = parentId, ["branch"] = parentBranch, ["commit"] = commit }, ["created_at"] = NodaTime.SystemClock.Instance.GetCurrentInstant().ToString() };
        await Repository.ApplyAsync(state, [new(manifest.Id, manifest)], ct); await CreateVersionCoreAsync("Create variant: " + title, "create-variant", ct);
    }
    private async Task SwitchCoreAsync(string branch, CancellationToken ct = default)
    {
        await RequireClean(ct); var variant = (await VariantsAsync(ct)).FirstOrDefault(v => v.Branch == branch);
        if (variant?.IsRemote == true) await Run(ct, "switch", "--track", "-c", "variants/import-" + Guid.CreateVersion7(), branch);
        else await Run(ct, "switch", "--", branch);
    }
    private async Task UpdateVariantCoreAsync(string? title = null, bool archive = false, CancellationToken ct = default)
    {
        var state = await Repository.ReadAsync(ct); var m = state.Trip.Manifest.Copy(); if (title is not null) m.Data["variant"]!["title"] = title; if (archive) m.Data["variant"]!["state"] = "archived";
        await Repository.ApplyAsync(state, [new(m.Id, m)], ct); await CreateVersionCoreAsync(archive ? "Archive variant" : "Rename variant", "variant-metadata", ct);
    }
    private async Task RequireClean(CancellationToken ct)
    { if (!string.IsNullOrEmpty(await StatusAsync(ct))) throw new DomainException("git.dirty", "Create a version or undo local changes before switching or merging variants."); }
    public async Task<IReadOnlyDictionary<string, string>> RemotesAsync(CancellationToken ct = default)
    {
        var result = new Dictionary<string, string>(); foreach (var name in (await Run(ct, "remote")).Split('\n', StringSplitOptions.RemoveEmptyEntries)) result[name] = await Run(ct, "remote", "get-url", name); return result;
    }
    public static void ValidateRemote(string url)
    {
        if (url.Contains("::", StringComparison.Ordinal) || url.StartsWith('-') || url.Contains('\n') || url.Contains('\r')) throw new DomainException("remote.invalid", "Invalid remote URL.");
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && !string.IsNullOrEmpty(uri.UserInfo)) throw new DomainException("remote.credentials", "Do not include credentials in remote URLs.");
    }
    private async Task AddRemoteCoreAsync(string name, string url, CancellationToken ct = default) { ValidateRemote(url); await Run(ct, "remote", "add", name, url); }
    private async Task FetchCoreAsync(string remote, CancellationToken ct = default) => await Run(ct, "fetch", "--", remote);
    private async Task PushCoreAsync(string remote, CancellationToken ct = default) => await Run(ct, "push", "--", remote, "HEAD");
    private async Task<SyncState> SyncCoreAsync(string remote, CancellationToken ct = default)
    {
        await FetchCoreAsync(remote, ct); if (!string.IsNullOrEmpty(await StatusAsync(ct))) return SyncState.LocalChanges;
        var branch = await CurrentBranchAsync(ct); var target = "refs/remotes/" + remote + "/" + branch;
        var exists = await backend.ExecuteAsync(Repository.Root, ["rev-parse", "--verify", target], cancellationToken: ct);
        if (exists.ExitCode != 0) { await PushCoreAsync(remote, ct); return SyncState.Synced; }
        var ancestor = await backend.ExecuteAsync(Repository.Root, ["merge-base", "--is-ancestor", "HEAD", target], cancellationToken: ct);
        if (ancestor.ExitCode == 0) { await Run(ct, "merge", "--ff-only", target); return SyncState.Synced; }
        ancestor = await backend.ExecuteAsync(Repository.Root, ["merge-base", "--is-ancestor", target, "HEAD"], cancellationToken: ct);
        if (ancestor.ExitCode == 0) { await PushCoreAsync(remote, ct); return SyncState.Synced; }
        return SyncState.Diverged;
    }
    public async Task<TripSnapshot> SnapshotAsync(string revision, CancellationToken ct = default)
    {
        var commit = await Run(ct, "rev-parse", "--verify", revision + "^{commit}");
        var paths = (await Run(ct, "ls-tree", "-r", "--name-only", "-z", commit)).Split('\0', StringSplitOptions.RemoveEmptyEntries); var entities = new Dictionary<Guid, Entity>(); Entity? manifest = null;
        foreach (var path in paths.Where(p => p.EndsWith(".yaml", StringComparison.Ordinal)))
        {
            var e = YamlCodec.Read(await Run(ct, "show", commit + ":" + path)); if (SchemaValidation.Validate(e).Count > 0) throw new DomainException("snapshot.invalid", "A version contains invalid data.");
            if (path == "travel.yaml") manifest = e; else entities.Add(e.Id, e);
        }
        var resources = new Dictionary<string, byte[]>();
        foreach (var path in paths.Where(p => !p.EndsWith(".yaml", StringComparison.Ordinal) && (p.StartsWith("documents/", StringComparison.Ordinal) || p.StartsWith("assets/", StringComparison.Ordinal))))
        {
            var r = await backend.ExecuteAsync(Repository.Root, ["show", commit + ":" + path], cancellationToken: ct);
            if (r.ExitCode != 0 || r.Bytes is null) throw new DomainException("snapshot.resource", "Cannot read a versioned resource.");
            resources[path] = r.Bytes;
        }
        return new(manifest ?? throw new DomainException("manifest.missing", "Version is not a TravelRepo."), entities) { Resources = resources };
    }
    public Task<string> MergeBaseAsync(string other, CancellationToken ct = default) => Run(ct, "merge-base", "HEAD", other);
    public async Task<IReadOnlyList<VersionEntry>> HistoryAsync(CancellationToken ct = default)
    {
        var text = await Run(ct, "log", "-100", "--format=%H%x00%P%x00%an%x00%ae%x00%aI%x00%B%x00%cn%x00%ce%x00%D%x00"); var fields = text.Split('\0'); var entries = new List<VersionEntry>();
        for (var i = 0; i + 8 < fields.Length; i += 9) entries.Add(new(fields[i].Trim(), fields[i + 1], fields[i + 2], fields[i + 3], fields[i + 4], fields[i + 5], fields[i + 5].Contains("\nTravelRepo-Version: 1\n", StringComparison.Ordinal), fields[i + 6], fields[i + 7], fields[i + 8]));
        return entries;
    }
    private async Task<string> RecordMergeCoreAsync(string otherCommit, string message, CancellationToken ct = default)
    {
        var old = await HeadAsync(ct); await StageAsync(ct); await CheckIndexAsync(ct); var tree = await Run(ct, "write-tree");
        var commit = await backend.ExecuteAsync(Repository.Root, ["commit-tree", tree, "-p", old, "-p", otherCommit, "-F", "-"], message + "\n\nTravelRepo-Version: 1\nTravelRepo-Action: semantic-merge\nTravelRepo-Client: TravelRepo/0.1.0\n", ct);
        if (commit.ExitCode != 0) throw new DomainException("merge.commit", "Unable to create merge version.");
        await Run(ct, "update-ref", "HEAD", commit.Output.Trim(), old); return commit.Output.Trim();
    }
    private async Task RollbackCoreAsync(string revision, CancellationToken ct = default)
    {
        var snapshot = await SnapshotAsync(revision, ct); var state = await Repository.ReadAsync(ct);
        var edits = state.Trip.All.Select(e => new EntityEdit(e.Id, snapshot.Find(e.Id))).Concat(snapshot.All.Where(e => state.Trip.Find(e.Id) is null).Select(e => new EntityEdit(e.Id, e))).ToArray();
        await Repository.ApplyAsync(state, edits, ct, state.Trip.Resources.Keys.Union(snapshot.Resources.Keys).Select(p => new ResourceEdit(p, snapshot.Resources.GetValueOrDefault(p))).ToArray());
    }
    public static async Task CloneAsync(IGitBackend backend, string url, string destination, CancellationToken ct = default)
    {
        ValidateRemote(url); var parent = Path.GetDirectoryName(Path.GetFullPath(destination))!; Directory.CreateDirectory(parent);
        var result = await backend.ExecuteAsync(parent, ["clone", "--", url, Path.GetFullPath(destination)], cancellationToken: ct);
        if (result.ExitCode != 0) throw new DomainException("clone.failed", "Clone failed. Check the address, network, and credentials.");
    }
}
