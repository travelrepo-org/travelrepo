using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using TravelRepo.Core;
using TravelRepo.Serialization;

namespace TravelRepo.Providers.GitHub;

/// <summary>GitHub App device authorization and capability-based hosting integration.</summary>
/// <param name="http">Sends the requests to GitHub.</param>
/// <param name="secrets">Keeps the user token.</param>
/// <param name="clientId">The public client ID of the GitHub App.</param>
/// <param name="appSlug">The App's name in its GitHub address (<c>github.com/apps/&lt;slug&gt;</c>), for the page where
/// users install it on their repositories. Signing in does not install the App, so without an installation it sees no
/// repository.</param>
public sealed class GitHubProvider(HttpClient http, ISecretStore secrets, string clientId, string? appSlug = null) : IProviderAuthentication, IProviderAccount, IRepositoryDiscoveryProvider, IRepositoryAccessProvider, IRepositoryCreationProvider, IRepositoryPrivacyProvider, ICollaboratorProvider, IUserSearchProvider, IShareProvider
{
    private const string TokenKey = "github.user-token";
    private async Task<JsonNode> Send(HttpMethod method, string uri, object? body = null, bool authenticated = true, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(method, uri.StartsWith("https://", StringComparison.Ordinal) ? uri : "https://api.github.com/" + uri);
        request.Headers.UserAgent.ParseAdd("TravelRepo/" + TravelRepoInfo.Version); request.Headers.Accept.ParseAdd("application/json"); request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (authenticated)
        {
            var token = await secrets.ReadAsync(TokenKey, ct) ?? throw new DomainException("github.authentication", "Connect GitHub first.");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new DomainException("github.http." + (int)response.StatusCode, "GitHub request failed. Check authorization and repository permissions.");
        var text = await response.Content.ReadAsStringAsync(ct); return string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text)!;
    }
    /// <summary>Whether a user token is stored. Does not contact GitHub.</summary>
    public async Task<bool> IsConnectedAsync(CancellationToken ct = default) => await secrets.ReadAsync(TokenKey, ct) is not null;
    public async Task<ProviderAccount?> AccountAsync(CancellationToken ct = default)
    {
        if (!await IsConnectedAsync(ct)) return null;
        JsonNode user;
        try { user = await Send(HttpMethod.Get, "user", ct: ct); }
        catch (DomainException ex) when (ex.Code == "github.http.401") { return null; }
        static Uri? Link(JsonNode? value) => Uri.TryCreate(value?.ToString(), UriKind.Absolute, out var uri) && uri.Scheme == "https" ? uri : null;
        var name = user["name"]?.ToString(); var email = user["email"]?.ToString();
        return new(user["login"]!.ToString(), string.IsNullOrWhiteSpace(name) ? null : name, Link(user["avatar_url"]), Link(user["html_url"]))
        {
            Id = user["id"] is JsonValue id && id.TryGetValue<long>(out var number) ? number : null,
            PublicEmail = string.IsNullOrWhiteSpace(email) ? null : email.Trim()
        };
    }

    /// <summary>
    /// The private address GitHub gives every account for commits, <c>ID+login@users.noreply.github.com</c>, which
    /// GitHub links to the account. Without the account's number, the older <c>login@users.noreply.github.com</c>,
    /// which GitHub links only for accounts made before July 2017.
    /// </summary>
    public static string NoReplyEmail(string login, long? id) => (id is { } number ? number.ToString(System.Globalization.CultureInfo.InvariantCulture) + "+" : "") + login + "@users.noreply.github.com";
    public Task SignOutAsync(CancellationToken ct = default) => secrets.DeleteAsync(TokenKey, ct);
    public async Task<DeviceAuthorization> BeginAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientId)) throw new DomainException("github.setup", "A GitHub App client ID must be configured. See provider setup documentation.");
        var n = await Send(HttpMethod.Post, "https://github.com/login/device/code", new { client_id = clientId }, false, ct);
        return new(n["device_code"]!.ToString(), n["user_code"]!.ToString(), n["verification_uri"]!.ToString(), (int)n["expires_in"]!, (int)n["interval"]!);
    }
    /// <summary>
    /// Waits until the user has entered the code, polling at GitHub's interval. A request lost to a phone falling
    /// asleep or changing networks does not end the sign-in; the code stays valid, so the next poll asks again.
    /// </summary>
    public async Task CompleteAsync(DeviceAuthorization authorization, CancellationToken ct = default)
    {
        var end = DateTimeOffset.UtcNow.AddSeconds(authorization.ExpiresIn); var interval = Math.Max(5, authorization.Interval);
        while (DateTimeOffset.UtcNow < end)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);
            try
            {
                switch (await PollAsync(authorization, ct))
                {
                    case DeviceSignIn.Connected: return;
                    case DeviceSignIn.SlowDown: interval += 5; break;
                }
            }
            catch (Exception ex) when (IsTransient(ex, ct)) { }
        }
        throw new DomainException("github.expired", "The authorization code expired.");
    }

    /// <summary>
    /// Asks GitHub once whether the user has entered the code, and stores the token when they have. For clients that
    /// pace the polling themselves, such as a phone app that asks again as soon as it is back in the foreground.
    /// Callers must wait at least the authorization's interval between calls, and longer after
    /// <see cref="DeviceSignIn.SlowDown"/>.
    /// </summary>
    /// <exception cref="DomainException">The user declined, or the code expired.</exception>
    public async Task<DeviceSignIn> PollAsync(DeviceAuthorization authorization, CancellationToken ct = default)
    {
        var n = await Send(HttpMethod.Post, "https://github.com/login/oauth/access_token", new { client_id = clientId, device_code = authorization.DeviceCode, grant_type = "urn:ietf:params:oauth:grant-type:device_code" }, false, ct);
        if (n["access_token"] is { } token) { await secrets.WriteAsync(TokenKey, token.ToString(), ct); return DeviceSignIn.Connected; }
        return n["error"]?.ToString() switch
        {
            "authorization_pending" => DeviceSignIn.Pending,
            "slow_down" => DeviceSignIn.SlowDown,
            "expired_token" => throw new DomainException("github.expired", "The authorization code expired."),
            _ => throw new DomainException("github.authorization", "Authorization was declined or expired. Connect again to retry.")
        };
    }

    /// <summary>
    /// Whether a failed request is worth repeating: no network, a dropped connection, a timeout, or a GitHub server
    /// error. Declined sign-ins and missing permissions are not.
    /// </summary>
    public static bool IsTransient(Exception ex, CancellationToken ct = default) => ex switch
    {
        HttpRequestException or IOException => true,
        TaskCanceledException => !ct.IsCancellationRequested,
        DomainException { Code: var code } => code.StartsWith("github.http.5", StringComparison.Ordinal),
        _ => false
    };

    private async Task<List<JsonNode>> Pages(string endpoint, string? key, CancellationToken ct)
    {
        var result = new List<JsonNode>();
        for (var page = 1; ; page++)
        {
            var n = await Send(HttpMethod.Get, endpoint + (endpoint.Contains('?') ? "&" : "?") + "per_page=100&page=" + page, ct: ct);
            var a = (key is null ? n : n[key]) as JsonArray ?? throw new DomainException("github.response", "Unexpected GitHub response.");
            result.AddRange(a.OfType<JsonNode>()); if (a.Count < 100) return result;
        }
    }
    /// <summary>Whether the App is installed on at least one account the user can reach, their own or an organisation's.</summary>
    public async Task<bool> HasRepositoryAccessAsync(CancellationToken ct = default) =>
        (int?)(await Send(HttpMethod.Get, "user/installations?per_page=1", ct: ct))["total_count"] is > 0;

    public Uri? RepositoryAccessUri => string.IsNullOrWhiteSpace(appSlug) ? null : new("https://github.com/apps/" + Uri.EscapeDataString(appSlug.Trim()) + "/installations/new");

    /// <summary>
    /// Lists every repository the App's installations can reach, then reads their travel.yaml a few at a time. Empty
    /// repositories are skipped without a request.
    /// </summary>
    public async Task<IReadOnlyList<RemoteRepository>> DiscoverAsync(IProgress<DiscoveryProgress>? progress = null, CancellationToken ct = default)
    {
        var repos = new List<JsonNode>();
        foreach (var installation in await Pages("user/installations", "installations", ct))
            repos.AddRange(await Pages("user/installations/" + installation["id"] + "/repositories", "repositories", ct));
        repos = repos.DistinctBy(r => r["full_name"]!.ToString()).ToList();
        progress?.Report(new(0, repos.Count));
        var found = new bool[repos.Count]; var done = 0;
        await Parallel.ForEachAsync(Enumerable.Range(0, repos.Count), new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (i, token) =>
        {
            found[i] = (int?)repos[i]["size"] != 0 && await HoldsTripAsync(repos[i]["full_name"]!.ToString(), token);
            progress?.Report(new(Interlocked.Increment(ref done), repos.Count));
        });
        return repos.Where((_, i) => found[i]).Select(Parse).ToArray();
    }

    private async Task<bool> HoldsTripAsync(string name, CancellationToken ct)
    {
        try
        {
            var content = await Send(HttpMethod.Get, "repos/" + EscapeName(name) + "/contents/travel.yaml", ct: ct);
            var manifest = YamlCodec.Read(Encoding.UTF8.GetString(Convert.FromBase64String(content["content"]!.ToString())));
            return SchemaValidation.Validate(manifest).Count == 0 && manifest.Type == "trip";
        }
        catch (DomainException ex) when (ex.Code == "github.http.404" || ex.Code.StartsWith("yaml.", StringComparison.Ordinal)) { return false; }
    }
    public async Task<RemoteRepository> CreateAsync(string name, CancellationToken ct = default) => Parse(await Send(HttpMethod.Post, "user/repos", new { name, @private = true, auto_init = false }, ct: ct));
    public async Task<RemoteRepository> InspectAsync(string fullName, CancellationToken ct = default) => Parse(await Send(HttpMethod.Get, "repos/" + EscapeName(fullName), authenticated: await secrets.ReadAsync(TokenKey, ct) is not null, ct: ct));
    public async Task InviteAsync(string fullName, string username, CancellationToken ct = default) => await Send(HttpMethod.Put, "repos/" + EscapeName(fullName) + "/collaborators/" + Uri.EscapeDataString(username), new { permission = "push" }, ct: ct);
    public async Task<IReadOnlyList<string>> SearchUsersAsync(string query, CancellationToken ct = default)
    { var n = await Send(HttpMethod.Get, "search/users?q=" + Uri.EscapeDataString(query), ct: ct); return n["items"]!.AsArray().Select(x => x!["login"]!.ToString()).ToArray(); }
    public Uri ShareUri(RemoteRepository repository, string? branch = null) => new(repository.WebUrl + (branch is null ? "" : "/tree/" + Uri.EscapeDataString(branch)));
    private static string EscapeName(string name) => string.Join('/', name.Split('/').Select(Uri.EscapeDataString));
    private static RemoteRepository Parse(JsonNode n) => new(n["full_name"]!.ToString(), n["clone_url"]!.ToString(), n["html_url"]!.ToString(), (bool)n["private"]!);
}
