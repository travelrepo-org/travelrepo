using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using TravelRepo.Core;
using TravelRepo.Providers;
using TravelRepo.Providers.GitHub;

namespace TravelRepo.Native;

/// <summary>
/// GitHub for native apps: sign-in with a device code, the account, finding and creating trip repositories, and
/// inviting people. The app keeps the token (for example in the Keychain) and hands it over with github_set_token;
/// a finished sign-in returns the new token for the app to store. TravelRepo holds it only in memory.
/// </summary>
internal static class GitHubCommands
{
    public const string TokenKey = "github.user-token";

    public static readonly IReadOnlyList<string> Names =
        ["github_configure", "github_set_token", "github_status", "github_begin_sign_in", "github_poll_sign_in", "github_sign_out",
         "github_repository_access", "github_repositories", "github_create_repository", "github_inspect_repository", "github_invite", "github_search_users"];

    private static readonly Lock Gate = new();
    private static string clientId = "", appSlug = "";
    private static GitHubProvider? provider;

    /// <summary>The token store, in memory only. Git also takes the token from here for github.com.</summary>
    public static MemorySecrets Secrets { get; } = new();

    /// <summary>The HTTP client for GitHub. Tests replace it.</summary>
    public static HttpClient Http { get; set; } = new();

    private static GitHubProvider Provider
    {
        get { lock (Gate) return provider ??= new GitHubProvider(Http, Secrets, clientId, appSlug.Length == 0 ? null : appSlug); }
    }

    /// <summary>Forgets the provider, so the next call uses the current settings and <see cref="Http"/>.</summary>
    public static void Reset() { lock (Gate) provider = null; }

    public static async Task<(bool Found, JsonNode? Result)> RunAsync(string command, Arguments args, CancellationToken ct)
    {
        switch (command)
        {
            case "github_configure":
                lock (Gate) { clientId = args.Required("client_id"); appSlug = args.String("app_slug") ?? ""; provider = null; }
                return (true, new JsonObject());
            case "github_set_token":
                if (args.String("token") is { Length: > 0 } token) await Secrets.WriteAsync(TokenKey, token, ct);
                else await Secrets.DeleteAsync(TokenKey, ct);
                return (true, new JsonObject());
            case "github_status":
                {
                    var account = await Provider.AccountAsync(ct);
                    return (true, new JsonObject
                    {
                        ["connected"] = account is not null,
                        ["account"] = account is null ? null : new JsonObject
                        {
                            ["login"] = account.Login,
                            ["name"] = account.Name,
                            ["avatar_url"] = account.AvatarUrl?.ToString(),
                            ["profile_url"] = account.ProfileUrl?.ToString()
                        }
                    });
                }
            case "github_begin_sign_in":
                {
                    var authorization = await Provider.BeginAsync(ct);
                    return (true, new JsonObject
                    {
                        ["device_code"] = authorization.DeviceCode,
                        ["user_code"] = authorization.UserCode,
                        ["verification_uri"] = authorization.VerificationUri,
                        ["expires_in"] = authorization.ExpiresIn,
                        ["interval"] = authorization.Interval
                    });
                }
            case "github_poll_sign_in":
                {
                    var authorization = new DeviceAuthorization(args.Required("device_code"), args.String("user_code", ""), args.String("verification_uri", ""), args.Int("expires_in", 900), args.Int("interval", 5));
                    var state = await Provider.PollAsync(authorization, ct);
                    var result = new JsonObject { ["state"] = ClientCommands.Snake(state.ToString()) };
                    if (state == DeviceSignIn.Connected) result["token"] = await Secrets.ReadAsync(TokenKey, ct);
                    return (true, result);
                }
            case "github_sign_out":
                await Provider.SignOutAsync(ct);
                return (true, new JsonObject());
            case "github_repository_access":
                return (true, new JsonObject { ["has_access"] = await Provider.HasRepositoryAccessAsync(ct), ["install_url"] = Provider.RepositoryAccessUri?.ToString() });
            case "github_repositories":
                return (true, new JsonObject { ["repositories"] = new JsonArray((await Provider.DiscoverAsync(null, ct)).Select(Repository).ToArray()) });
            case "github_create_repository":
                return (true, Repository(await Provider.CreateAsync(args.Required("name"), ct)));
            case "github_inspect_repository":
                return (true, Repository(await Provider.InspectAsync(args.Required("full_name"), ct)));
            case "github_invite":
                await Provider.InviteAsync(args.Required("full_name"), args.Required("username"), ct);
                return (true, new JsonObject());
            case "github_search_users":
                return (true, new JsonObject { ["logins"] = new JsonArray((await Provider.SearchUsersAsync(args.Required("query"), ct)).Select(l => (JsonNode)l).ToArray()) });
        }
        return (false, null);
    }

    private static JsonNode Repository(RemoteRepository r) => new JsonObject
    {
        ["full_name"] = r.FullName,
        ["clone_url"] = r.CloneUrl,
        ["web_url"] = r.WebUrl,
        ["private"] = r.IsPrivate
    };
}

/// <summary>Secrets in memory, for the lifetime of the process.</summary>
internal sealed class MemorySecrets : ISecretStore
{
    private readonly ConcurrentDictionary<string, string> values = new();
    public Task<string?> ReadAsync(string key, CancellationToken ct = default) => Task.FromResult(values.TryGetValue(key, out var value) ? value : null);
    public Task WriteAsync(string key, string value, CancellationToken ct = default) { values[key] = value; return Task.CompletedTask; }
    public Task DeleteAsync(string key, CancellationToken ct = default) { values.TryRemove(key, out _); return Task.CompletedTask; }
}
