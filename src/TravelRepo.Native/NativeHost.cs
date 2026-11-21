using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using TravelRepo.Core;
using TravelRepo.Git;
using TravelRepo.Git.LibGit2;
using TravelRepo.Mcp;
using TravelRepo.Providers.Weather;
using TravelRepo.Repository;

namespace TravelRepo.Native;

/// <summary>
/// The managed side of the C ABI (<see cref="NativeApi"/>): sessions, calls, cancellation and Git credentials.
/// Every call answers exactly once, on a thread pool thread, with <c>{"ok":true,"result":…}</c> or
/// <c>{"ok":false,"error":{"code":…,"message":…}}</c>.
/// </summary>
internal static class NativeHost
{
    private static readonly JsonSerializerOptions Output = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly ConcurrentDictionary<ulong, NativeSession> Sessions = new();
    private static readonly ConcurrentDictionary<ulong, CancellationTokenSource> Calls = new();
    private static readonly Lock GitGate = new();
    private static long lastSession, lastCall;
    private static IGitBackend? git;

    /// <summary>Receives events (clone progress, trip changes) as JSON. Set through <see cref="NativeApi"/>.</summary>
    public static Action<string>? Events { get; set; }

    /// <summary>Sends an event to the host, if it listens. A failing handler never affects TravelRepo.</summary>
    public static void Emit(JsonObject @event)
    {
        if (Events is not { } handler) return;
        try { handler(@event.ToJsonString(Output)); } catch { }
    }

    /// <summary>Asks the host for Git credentials. Set through <see cref="NativeApi"/>.</summary>
    public static HostCredentials Credentials { get; } = new();

    /// <summary>The version of the C ABI and of the JSON it carries. It grows when something changes incompatibly.</summary>
    public const int AbiVersion = 1;

    /// <summary>The commands that need no session (session 0).</summary>
    public static readonly IReadOnlyList<string> GlobalCommands = ["info", "configure", "create_trip", "open", "clone"];

    /// <summary>Starts a call and returns its id for <see cref="Cancel"/>. <paramref name="respond"/> runs exactly once.</summary>
    public static ulong Call(ulong session, string command, string? arguments, Action<string> respond)
    {
        var id = (ulong)Interlocked.Increment(ref lastCall);
        var cancellation = new CancellationTokenSource();
        Calls[id] = cancellation;
        _ = Task.Run(async () =>
        {
            string response;
            try
            {
                var args = Arguments.Parse(arguments);
                NativeSession? open = null;
                if (session != 0 && !Sessions.TryGetValue(session, out open)) throw new DomainException("session.unknown", "There is no open session " + session + ".");
                if (open?.Culture is { } culture) { CultureInfo.CurrentCulture = culture; CultureInfo.CurrentUICulture = culture; }
                response = Ok(await (open is null ? Global(command, args, id, cancellation.Token) : Run(open, command, args, cancellation.Token)));
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { response = Error("call.cancelled", "The call was cancelled."); }
            catch (TripToolException ex) { response = Error(ex.Code, ex.Message); }
            catch (McpException ex) { response = Error("command.invalid", ex.Message); }
            catch (DomainException ex) { response = Error(ex.Code, ex.Message); }
            catch (Exception ex) { response = Error("internal", ex.GetType().Name + ": " + ex.Message + (ex.InnerException is { } inner ? " (" + inner.GetType().Name + ": " + inner.Message + ")" : "")); }
            finally { Calls.TryRemove(id, out _); cancellation.Dispose(); }
            try { respond(response); } catch { /* The host's callback must not take the runtime down. */ }
        });
        return id;
    }

    /// <summary>Cancels a running call. Unknown or finished calls are ignored.</summary>
    public static void Cancel(ulong call)
    {
        if (!Calls.TryGetValue(call, out var cancellation)) return;
        try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Closes a session. Calls already running finish.</summary>
    public static void Close(ulong session)
    {
        if (Sessions.TryRemove(session, out var closed)) closed.StopWatching();
    }

    /// <summary>
    /// Git for every session. libgit2 is loaded on the first Git command, not when a trip opens: a failed load would
    /// stay failed for the whole process, so an app that calls configure after opening a trip still gets working Git.
    /// </summary>
    private static readonly IGitBackend Git = new DeferredGit();

    private sealed class DeferredGit : IGitBackend
    {
        public Task<GitResult> ExecuteAsync(string directory, IReadOnlyList<string> arguments, string? input = null, CancellationToken cancellationToken = default)
        {
            IGitBackend backend;
            lock (GitGate)
            {
                try { backend = git ??= new LibGit2Backend(Credentials); }
                catch (TypeInitializationException ex) when (ex.InnerException is DllNotFoundException or BadImageFormatException)
                { throw new DomainException("git.library", "libgit2 could not be loaded; call configure with its path before the first Git command. " + ex.InnerException.Message); }
            }
            return backend.ExecuteAsync(directory, arguments, input, cancellationToken);
        }
    }

    private static async Task<JsonNode?> Global(string command, Arguments args, ulong call, CancellationToken ct)
    {
        switch (command)
        {
            case "info":
                AppContext.TryGetSwitch("System.Globalization.Invariant", out var invariant);
                return new JsonObject
                {
                    ["abi_version"] = AbiVersion,
                    ["version"] = TravelRepoInfo.Version,
                    ["format_version"] = TravelRepoInfo.FormatVersion,
                    ["invariant_globalization"] = invariant,
                    ["commands"] = new JsonObject
                    {
                        ["global"] = new JsonArray(GlobalCommands.Concat(GitHubCommands.Names).Select(c => (JsonNode)c).ToArray()),
                        ["session"] = new JsonArray(ClientCommands.Names.Concat(AppCommands.Names).Concat(MaintenanceCommands.Names).Concat(PlanCommands.Names).Concat(Tools.All.Select(t => t.Name)).Select(c => (JsonNode)c).ToArray()),
                        ["read_only"] = new JsonArray(ClientCommands.ReadOnly.Concat(AppCommands.ReadOnly).Concat(MaintenanceCommands.ReadOnly).Concat(PlanCommands.ReadOnly).Concat(Tools.All.Where(t => t.ReadOnly).Select(t => t.Name)).Select(c => (JsonNode)c).ToArray())
                    }
                };
            case "configure":
                if (args.String("libgit2") is { Length: > 0 } library)
                {
                    try { LibGit2Library.Use(library); }
                    catch (Exception ex) when (ex is DllNotFoundException or InvalidOperationException) { throw new DomainException("git.library", ex.Message); }
                }
                return new JsonObject();
            case "create_trip":
                {
                    var root = Path.GetFullPath(args.Required("root"));
                    if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) throw new DomainException("trip.exists", "The folder " + root + " is not empty.");
                    var repository = new TravelRepository(root, args.String("recovery_root"));
                    var manifest = Entity.CreateTrip(args.Required("title"), args.String("language", "en"), args.String("timezone"));
                    await repository.InitializeAsync(manifest, ct);
                    if (args.String("git_name") is { } name) await new GitRepository(repository, Git).InitializeAsync(name, args.Required("git_email"), ct);
                    return new JsonObject { ["root"] = root, ["id"] = manifest.Id.ToString() };
                }
            case "open":
                return new JsonObject { ["session"] = Open(args) };
            case "clone":
                {
                    var url = args.Required("url"); var destination = args.Required("destination");
                    var last = DateTime.MinValue;
                    using (LibGit2Backend.ReportProgress(p =>
                    {
                        // About ten reports a second, and always the last one of each phase.
                        var now = DateTime.UtcNow;
                        var final = p.CheckoutTotal > 0 ? p.CheckoutCompleted == p.CheckoutTotal : p.TotalObjects > 0 && p.ReceivedObjects == p.TotalObjects;
                        if (!final && now - last < TimeSpan.FromMilliseconds(100)) return;
                        last = now;
                        Emit(new JsonObject
                        {
                            ["type"] = "clone_progress",
                            ["call"] = call,
                            ["total_objects"] = p.TotalObjects,
                            ["received_objects"] = p.ReceivedObjects,
                            ["indexed_objects"] = p.IndexedObjects,
                            ["received_bytes"] = p.ReceivedBytes,
                            ["checkout_completed"] = p.CheckoutCompleted,
                            ["checkout_total"] = p.CheckoutTotal
                        });
                    }))
                        await GitRepository.CloneAsync(Git, url, destination, ct);
                    return new JsonObject { ["destination"] = destination };
                }
            default:
                var (found, result) = await GitHubCommands.RunAsync(command, args, ct);
                if (found) return result;
                throw Unknown(command, Tools.ByName.ContainsKey(command) || ClientCommands.Names.Contains(command) || AppCommands.Names.Contains(command) ? "It needs the session that open returns." : null);
        }
    }

    private static ulong Open(Arguments args)
    {
        var root = Path.GetFullPath(args.Required("root"));
        if (!File.Exists(Path.Combine(root, "travel.yaml"))) throw new DomainException("session.not_trip", "This folder is not a TravelRepo trip: travel.yaml is missing.");
        CultureInfo? culture = null;
        if (args.String("culture") is { Length: > 0 } name)
        {
            try { culture = CultureInfo.GetCultureInfo(name, predefinedOnly: true); }
            catch (CultureNotFoundException) { throw new DomainException("argument.invalid", "The culture " + name + " is not available here."); }
        }
        var repository = new TravelRepository(root, args.String("recovery_root"));
        var weatherCache = args.String("weather_cache"); var weatherProvider = args.String("weather_provider"); var rates = args.String("exchange_rates");
        var readOnly = args.Bool("read_only", false);
        var tools = new TripTools(repository, Git, readOnly,
            weatherCache is null ? null : () => new CachedWeatherSource(weatherCache, weatherProvider), args.String("client_name"), rates is null ? null : () => rates);
        var id = (ulong)Interlocked.Increment(ref lastSession);
        Sessions[id] = new(repository, new GitRepository(repository, Git), tools, culture, readOnly) { Id = id, ExchangeRatesPath = rates, WeatherCache = weatherCache, WeatherProvider = weatherProvider };
        return id;
    }

    private static async Task<JsonNode?> Run(NativeSession session, string command, Arguments args, CancellationToken ct)
    {
        switch (command)
        {
            case "sync_status":
                {
                    var status = await session.Git.SyncStatusAsync(args.String("remote"), ct);
                    return new JsonObject
                    {
                        ["branch"] = status.Branch,
                        ["remote"] = status.Remote,
                        ["has_local_changes"] = status.HasLocalChanges,
                        ["remote_branch_exists"] = status.RemoteBranchExists,
                        ["ahead"] = status.Ahead,
                        ["behind"] = status.Behind,
                        ["state"] = ClientCommands.Snake(status.State.ToString())
                    };
                }
            case "sync":
                if (session.ReadOnly) throw new DomainException("session.read_only", "This session is read-only.");
                return new JsonObject { ["state"] = ClientCommands.Snake((await session.Git.SyncAsync(args.String("remote", "origin"), ct)).ToString()) };
            case "set_identity":
                if (session.ReadOnly) throw new DomainException("session.read_only", "This session is read-only.");
                await session.Git.SetIdentityAsync(args.Required("name"), args.Required("email"), ct);
                return new JsonObject();
            case "initialize_git":
                if (session.ReadOnly) throw new DomainException("session.read_only", "This session is read-only.");
                await session.Git.InitializeAsync(args.Required("name"), args.Required("email"), ct);
                return new JsonObject();
            case "watch":
                if (args.Bool("enabled", true)) await session.WatchAsync(TimeSpan.FromSeconds(Math.Clamp(args.Int("interval_seconds", 5), 1, 3600)), session.Id, ct);
                else session.StopWatching();
                return new JsonObject { ["watching"] = session.IsWatching };
            case "add_remote":
                if (session.ReadOnly) throw new DomainException("session.read_only", "This session is read-only.");
                await session.Git.AddRemoteAsync(args.Required("name"), args.Required("url"), ct);
                return new JsonObject();
        }
        var (found, result) = await ClientCommands.RunAsync(session, command, args, ct);
        if (found) return result;
        (found, result) = await AppCommands.RunAsync(session, command, args, ct);
        if (found) return result;
        (found, result) = await MaintenanceCommands.RunAsync(session, command, args, ct);
        if (found) return result;
        (found, result) = await PlanCommands.RunAsync(session, command, args, ct);
        if (found) return result;
        if (!Tools.ByName.TryGetValue(command, out var tool)) throw Unknown(command, null);
        var text = await tool.Run(session.Tools, args, ct);
        return tool.Name == "read_guide" ? JsonValue.Create(text) : JsonNode.Parse(text);
    }

    private static DomainException Unknown(string command, string? hint) =>
        new("command.unknown", "There is no command " + command + " here." + (hint is null ? "" : " " + hint));

    private static string Ok(JsonNode? result) => new JsonObject { ["ok"] = true, ["result"] = result }.ToJsonString(Output);

    private static string Error(string code, string message) =>
        new JsonObject { ["ok"] = false, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }.ToJsonString(Output);
}

/// <summary>An open trip: its repository and Git, the MCP tools, and the last states read, by revision.</summary>
internal sealed class NativeSession(TravelRepository repository, GitRepository git, TripTools tools, CultureInfo? culture, bool readOnly)
{
    private readonly Lock gate = new();
    private readonly List<(string Revision, RepositoryState State)> recent = [];

    public TravelRepository Repository { get; } = repository;
    public GitRepository Git { get; } = git;
    public TripTools Tools { get; } = tools;
    public CultureInfo? Culture { get; } = culture;
    public bool ReadOnly { get; } = readOnly;
    public ulong Id { get; init; }
    private CancellationTokenSource? watching;

    public bool IsWatching { get { lock (gate) return watching is not null; } }

    /// <summary>
    /// Checks the trip folder every <paramref name="interval"/> and sends trip_changed when its revision changed, for
    /// example after a sync or an edit in another app. Comparing file sizes and times first keeps it cheap; the trip is
    /// only read again when one of them changed.
    /// </summary>
    /// <remarks>
    /// The starting point is taken before this returns, so a change made right after <c>watch</c> answers is always
    /// reported, never mistaken for the starting point.
    /// </remarks>
    public async Task WatchAsync(TimeSpan interval, ulong session, CancellationToken ct)
    {
        var fingerprint = Fingerprint(); string? revision = null;
        try { revision = ClientCommands.Revision(await ReadAsync(ct)); } catch (Exception ex) when (ex is not OperationCanceledException) { }
        CancellationToken token;
        lock (gate) { watching?.Cancel(); watching = new(); token = watching.Token; }
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, token);
                    var now = Fingerprint();
                    if (now == fingerprint) continue;
                    fingerprint = now;
                    var state = await ReadAsync(token); var current = ClientCommands.Revision(state);
                    if (current == revision) continue;
                    revision = current; Remember(state);
                    NativeHost.Emit(new JsonObject { ["type"] = "trip_changed", ["session"] = session, ["revision"] = current });
                }
                catch (OperationCanceledException) { break; }
                catch (Exception) { /* A half-written file or a moved folder: try again at the next tick. */ }
            }
        }, token);
    }

    public void StopWatching() { lock (gate) { watching?.Cancel(); watching = null; } }

    private string Fingerprint()
    {
        var text = new System.Text.StringBuilder();
        try
        {
            foreach (var file in new DirectoryInfo(Repository.Root).EnumerateFiles("*", SearchOption.AllDirectories)
                .Where(f => !f.FullName.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal)).OrderBy(f => f.FullName, StringComparer.Ordinal))
                text.Append(file.FullName).Append('|').Append(file.Length).Append('|').Append(file.LastWriteTimeUtc.Ticks).Append('\n');
        }
        catch (IOException) { }
        return text.ToString();
    }
    public string? ExchangeRatesPath { get; init; }
    public string? WeatherCache { get; init; }
    public string? WeatherProvider { get; init; }

    /// <summary>The exchange rates saved at <see cref="ExchangeRatesPath"/>, or null.</summary>
    public ExchangeRates? ExchangeRates() => ExchangeRatesPath is null ? null : TravelRepo.Core.ExchangeRates.Load(ExchangeRatesPath);

    public Task<RepositoryState> ReadAsync(CancellationToken ct) => Repository.ReadAsync(ct);

    /// <summary>Keeps <paramref name="state"/> for a later <c>apply</c> and returns its revision.</summary>
    public string Remember(RepositoryState state)
    {
        var revision = ClientCommands.Revision(state);
        lock (gate)
        {
            recent.RemoveAll(r => r.Revision == revision);
            recent.Add((revision, state));
            if (recent.Count > 8) recent.RemoveAt(0);
        }
        return revision;
    }

    public RepositoryState? Recall(string revision) { lock (gate) return recent.LastOrDefault(r => r.Revision == revision).State; }
}

/// <summary>Git credentials from the host: each request is answered with <see cref="Reply"/>, or cancelled.</summary>
internal sealed class HostCredentials : ICredentialBroker
{
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<GitCredential?>> pending = new();
    private long lastRequest;

    /// <summary>Called with a request id and the origin, for example <c>https://github.com</c>. Without it Git gets no credentials.</summary>
    public Action<ulong, string>? Request { get; set; }

    public async Task<GitCredential?> GetAsync(Uri origin, CancellationToken ct = default)
    {
        // Without a handler of its own, the app's GitHub sign-in is used for github.com.
        if (Request is not { } request)
            return origin.Host == "github.com" && await GitHubCommands.Secrets.ReadAsync(GitHubCommands.TokenKey, ct) is { } token ? new GitCredential("x-access-token", token) : null;
        var id = (ulong)Interlocked.Increment(ref lastRequest);
        var answer = new TaskCompletionSource<GitCredential?>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = answer;
        try
        {
            using var registration = ct.Register(() => answer.TrySetCanceled(ct));
            request(id, origin.GetLeftPart(UriPartial.Authority));
            return await answer.Task;
        }
        finally { pending.TryRemove(id, out _); }
    }

    /// <summary>Answers a request with <c>{"username":…,"password":…}</c>, or with null for none.</summary>
    public void Reply(ulong request, string? json)
    {
        if (!pending.TryGetValue(request, out var answer)) return;
        try
        {
            var node = json is null ? null : JsonNode.Parse(json);
            answer.TrySetResult(node?["username"]?.GetValue<string>() is { } user && node["password"]?.GetValue<string>() is { } password ? new GitCredential(user, password) : null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { answer.TrySetResult(null); }
    }
}
