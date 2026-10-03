using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using TravelRepo.Core;
using TravelRepo.Serialization;

namespace TravelRepo.Providers.GitHub;

/// <summary>GitHub App device authorization and capability-based hosting integration.</summary>
public sealed class GitHubProvider(HttpClient http, ISecretStore secrets, string clientId) : IProviderAuthentication, IRepositoryDiscoveryProvider, IRepositoryCreationProvider, IRepositoryPrivacyProvider, ICollaboratorProvider, IUserSearchProvider, IShareProvider
{
    private const string TokenKey = "github.user-token";
    private async Task<JsonNode> Send(HttpMethod method, string uri, object? body = null, bool authenticated = true, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(method, uri.StartsWith("https://", StringComparison.Ordinal) ? uri : "https://api.github.com/" + uri);
        request.Headers.UserAgent.ParseAdd("TravelRepo/0.1.0"); request.Headers.Accept.ParseAdd("application/json"); request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
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
    public async Task<DeviceAuthorization> BeginAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientId)) throw new DomainException("github.setup", "A GitHub App client ID must be configured. See provider setup documentation.");
        var n = await Send(HttpMethod.Post, "https://github.com/login/device/code", new { client_id = clientId }, false, ct);
        return new(n["device_code"]!.ToString(), n["user_code"]!.ToString(), n["verification_uri"]!.ToString(), (int)n["expires_in"]!, (int)n["interval"]!);
    }
    public async Task CompleteAsync(DeviceAuthorization authorization, CancellationToken ct = default)
    {
        var end = DateTimeOffset.UtcNow.AddSeconds(authorization.ExpiresIn); var interval = Math.Max(5, authorization.Interval);
        while (DateTimeOffset.UtcNow < end)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);
            var n = await Send(HttpMethod.Post, "https://github.com/login/oauth/access_token", new { client_id = clientId, device_code = authorization.DeviceCode, grant_type = "urn:ietf:params:oauth:grant-type:device_code" }, false, ct);
            if (n["access_token"] is { } token) { await secrets.WriteAsync(TokenKey, token.ToString(), ct); return; }
            switch (n["error"]?.ToString())
            {
                case "authorization_pending": continue;
                case "slow_down": interval += 5; continue;
                default: throw new DomainException("github.authorization", "Authorization was declined or expired. Connect again to retry.");
            }
        }
        throw new DomainException("github.expired", "The authorization code expired.");
    }
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
    public async Task<IReadOnlyList<RemoteRepository>> DiscoverAsync(CancellationToken ct = default)
    {
        var result = new List<RemoteRepository>();
        foreach (var installation in await Pages("user/installations", "installations", ct))
            foreach (var repo in await Pages("user/installations/" + installation["id"] + "/repositories", "repositories", ct))
            {
                var name = repo["full_name"]!.ToString();
                try
                {
                    var content = await Send(HttpMethod.Get, "repos/" + EscapeName(name) + "/contents/travel.yaml", ct: ct);
                    var yaml = Encoding.UTF8.GetString(Convert.FromBase64String(content["content"]!.ToString()));
                    var manifest = YamlCodec.Read(yaml); if (SchemaValidation.Validate(manifest).Count == 0 && manifest.Type == "trip") result.Add(Parse(repo));
                }
                catch (DomainException ex) when (ex.Code == "github.http.404" || ex.Code.StartsWith("yaml.", StringComparison.Ordinal)) { }
            }
        return result.DistinctBy(r => r.FullName).ToArray();
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
