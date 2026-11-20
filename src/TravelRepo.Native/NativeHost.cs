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
    private static readonly ConcurrentDictionary<ulong, Session> Sessions = new();
    private static readonly ConcurrentDictionary<ulong, CancellationTokenSource> Calls = new();
    private static readonly Lock GitGate = new();
    private static long lastSession, lastCall;
    private static IGitBackend? git;

    /// <summary>Asks the host for Git credentials. Set through <see cref="NativeApi"/>.</summary>
    public static HostCredentials Credentials { get; } = new();

    /// <summary>The commands that need no session (session 0).</summary>
    public static readonly IReadOnlyList<string> GlobalCommands = ["info", "configure", "open", "clone"];

    /// <summary>The commands of a session in addition to <see cref="Tools"/>.</summary>
    public static readonly IReadOnlyList<string> SessionCommands = ["snapshot", "initialize_git", "sync_status", "sync", "add_remote"];

    private sealed record Session(TravelRepository Repository, GitRepository Git, TripTools Tools, CultureInfo? Culture);

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
                Session? open = null;
                if (session != 0 && !Sessions.TryGetValue(session, out open)) throw new DomainException("session.unknown", "There is no open session " + session + ".");
                if (open?.Culture is { } culture) { CultureInfo.CurrentCulture = culture; CultureInfo.CurrentUICulture = culture; }
                response = Ok(await (open is null ? Global(command, args, cancellation.Token) : Run(open, command, args, cancellation.Token)));
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { response = Error("call.cancelled", "The call was cancelled."); }
            catch (TripToolException ex) { response = Error(ex.Code, ex.Message); }
            catch (McpException ex) { response = Error("command.invalid", ex.Message); }
            catch (DomainException ex) { response = Error(ex.Code, ex.Message); }
            catch (Exception ex) { response = Error("internal", ex.GetType().Name + ": " + ex.Message); }
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
    public static void Close(ulong session) => Sessions.TryRemove(session, out _);

    private static IGitBackend Git
    {
        get { lock (GitGate) return git ??= new LibGit2Backend(Credentials); }
    }

    private static async Task<JsonNode?> Global(string command, Arguments args, CancellationToken ct)
    {
        switch (command)
        {
            case "info":
                AppContext.TryGetSwitch("System.Globalization.Invariant", out var invariant);
                return new JsonObject
                {
                    ["version"] = TravelRepoInfo.Version,
                    ["format_version"] = TravelRepoInfo.FormatVersion,
                    ["invariant_globalization"] = invariant,
                    ["commands"] = new JsonObject
                    {
                        ["global"] = new JsonArray(GlobalCommands.Select(c => (JsonNode)c).ToArray()),
                        ["session"] = new JsonArray(SessionCommands.Concat(Tools.All.Select(t => t.Name)).Select(c => (JsonNode)c).ToArray()),
                        ["read_only"] = new JsonArray(Tools.All.Where(t => t.ReadOnly).Select(t => (JsonNode)t.Name).Concat(["snapshot", "sync_status"]).ToArray())
                    }
                };
            case "configure":
                if (args.String("libgit2") is { Length: > 0 } library)
                {
                    try { LibGit2Library.Use(library); }
                    catch (Exception ex) when (ex is DllNotFoundException or InvalidOperationException) { throw new DomainException("git.library", ex.Message); }
                }
                return new JsonObject();
            case "open":
                return new JsonObject { ["session"] = Open(args) };
            case "clone":
                {
                    var url = args.Required("url"); var destination = args.Required("destination");
                    await GitRepository.CloneAsync(Git, url, destination, ct);
                    return new JsonObject { ["destination"] = destination };
                }
            default:
                throw Unknown(command, Tools.ByName.ContainsKey(command) || SessionCommands.Contains(command) ? "It needs the session that open returns." : null);
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
        var tools = new TripTools(repository, Git, args.Bool("read_only", false),
            weatherCache is null ? null : () => new CachedWeatherSource(weatherCache, weatherProvider), args.String("client_name"), rates is null ? null : () => rates);
        var id = (ulong)Interlocked.Increment(ref lastSession);
        Sessions[id] = new(repository, new GitRepository(repository, Git), tools, culture);
        return id;
    }

    private static async Task<JsonNode?> Run(Session session, string command, Arguments args, CancellationToken ct)
    {
        switch (command)
        {
            case "snapshot":
                {
                    var state = await session.Repository.ReadAsync(ct);
                    return new JsonObject
                    {
                        ["entities"] = new JsonArray(state.Files.Values.OrderBy(f => f.Path, StringComparer.Ordinal)
                            .Select(f => (JsonNode)new JsonObject { ["path"] = f.Path, ["hash"] = f.Hash, ["data"] = f.Entity.Data.DeepClone() }).ToArray()),
                        ["diagnostics"] = Diagnostics(state.Diagnostics)
                    };
                }
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
                        ["state"] = State(status.State)
                    };
                }
            case "sync":
                return new JsonObject { ["state"] = State(await session.Git.SyncAsync(args.String("remote", "origin"), ct)) };
            case "initialize_git":
                await session.Git.InitializeAsync(args.Required("name"), args.Required("email"), ct);
                return new JsonObject();
            case "add_remote":
                await session.Git.AddRemoteAsync(args.Required("name"), args.Required("url"), ct);
                return new JsonObject();
        }
        if (!Tools.ByName.TryGetValue(command, out var tool)) throw Unknown(command, null);
        var text = await tool.Run(session.Tools, args, ct);
        return tool.Name == "read_guide" ? JsonValue.Create(text) : JsonNode.Parse(text);
    }

    private static JsonArray Diagnostics(IEnumerable<Diagnostic> diagnostics) => new(diagnostics.Select(d => (JsonNode)new JsonObject
    {
        ["code"] = d.Code,
        ["severity"] = d.Severity.ToString().ToLowerInvariant(),
        ["path"] = d.Path,
        ["message"] = d.Message
    }).ToArray());

    private static string State(SyncState state) => state switch
    {
        SyncState.LocalOnly => "local_only",
        SyncState.Synced => "synced",
        SyncState.LocalChanges => "local_changes",
        SyncState.Diverged => "diverged",
        SyncState.RemoteChanges => "remote_changes",
        _ => state.ToString()
    };

    private static DomainException Unknown(string command, string? hint) =>
        new("command.unknown", "There is no command " + command + " here." + (hint is null ? "" : " " + hint));

    private static string Ok(JsonNode? result) => new JsonObject { ["ok"] = true, ["result"] = result }.ToJsonString(Output);

    private static string Error(string code, string message) =>
        new JsonObject { ["ok"] = false, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }.ToJsonString(Output);
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
        if (Request is not { } request) return null;
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
