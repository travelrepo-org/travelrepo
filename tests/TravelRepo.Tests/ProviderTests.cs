using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using TravelRepo.Core;
using TravelRepo.Providers;
using TravelRepo.Providers.GitHub;
using TravelRepo.Serialization;
using Xunit;
namespace TravelRepo.Tests;

public class ProviderTests
{
    private sealed class Secrets : ISecretStore
    { public Task<string?> ReadAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>("test-token"); public Task WriteAsync(string key, string value, CancellationToken ct = default) => Task.CompletedTask; public Task DeleteAsync(string key, CancellationToken ct = default) => Task.CompletedTask; }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> action) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => action(request); }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    private sealed class MemorySecrets : ISecretStore
    {
        public Dictionary<string, string> Values { get; } = [];
        public Task<string?> ReadAsync(string key, CancellationToken ct = default) => Task.FromResult(Values.GetValueOrDefault(key));
        public Task WriteAsync(string key, string value, CancellationToken ct = default) { Values[key] = value; return Task.CompletedTask; }
        public Task DeleteAsync(string key, CancellationToken ct = default) { Values.Remove(key); return Task.CompletedTask; }
    }
    [Fact]
    public async Task AccountShowsTheSignedInUserAndSignOutForgetsTheToken()
    {
        var secrets = new MemorySecrets(); var status = HttpStatusCode.OK;
        var provider = new GitHubProvider(new HttpClient(new Handler(r =>
        {
            Assert.Equal("/user", r.RequestUri!.AbsolutePath); Assert.StartsWith("TravelRepo/" + TravelRepoInfo.Version, r.Headers.UserAgent.ToString());
            return Task.FromResult(status == HttpStatusCode.OK ? Json(new { login = "alex", name = "Alex Example", avatar_url = "https://avatars.githubusercontent.com/u/1", html_url = "https://github.com/alex" }) : new HttpResponseMessage(status));
        })), secrets, "test-client");
        Assert.Null(await provider.AccountAsync());
        secrets.Values["github.user-token"] = "token";
        var account = await provider.AccountAsync();
        Assert.Equal(new ProviderAccount("alex", "Alex Example", new Uri("https://avatars.githubusercontent.com/u/1"), new Uri("https://github.com/alex")), account);
        status = HttpStatusCode.Unauthorized; Assert.Null(await provider.AccountAsync());
        status = HttpStatusCode.InternalServerError; await Assert.ThrowsAsync<DomainException>(() => provider.AccountAsync());
        await provider.SignOutAsync(); Assert.False(await provider.IsConnectedAsync()); Assert.Empty(secrets.Values);
    }
    [Fact]
    public async Task PublishingDefaultsPrivateAndPrivacyIsExposed()
    {
        var requests = new List<string>(); var handler = new Handler(async r =>
        {
            requests.Add(r.RequestUri!.AbsolutePath); Assert.Equal("Bearer", r.Headers.Authorization!.Scheme);
            if (r.Method == HttpMethod.Post) { var body = JsonNode.Parse(await r.Content!.ReadAsStringAsync())!; Assert.True((bool)body["private"]!); Assert.False((bool)body["auto_init"]!); }
            return Json(new { full_name = "alex/trip", clone_url = "https://github.com/alex/trip.git", html_url = "https://github.com/alex/trip", @private = r.Method == HttpMethod.Post });
        });
        var provider = new GitHubProvider(new HttpClient(handler), new Secrets(), "test-client"); Assert.True((await provider.CreateAsync("trip")).IsPrivate); Assert.False((await provider.InspectAsync("alex/trip")).IsPrivate); await provider.InviteAsync("alex/trip", "carla"); Assert.Contains("/repos/alex/trip/collaborators/carla", requests);
    }
    [Fact]
    public async Task DiscoveryInspectsGrantedInstallationManifest()
    {
        var yaml = YamlCodec.Write(Entity.CreateTrip("Trip")); var paths = new List<string>(); var provider = new GitHubProvider(new HttpClient(new Handler(r =>
        {
            var path = r.RequestUri!.AbsolutePath; paths.Add(path);
            return Task.FromResult(path switch
            {
                "/user/installations" => Json(new { installations = new[] { new { id = 123 } } }),
                "/user/installations/123/repositories" => Json(new { repositories = new[] { new { full_name = "alex/trip", clone_url = "https://github.com/alex/trip.git", html_url = "https://github.com/alex/trip", @private = true } } }),
                _ => Json(new { content = Convert.ToBase64String(Encoding.UTF8.GetBytes(yaml)) })
            });
        })), new Secrets(), "client");
        Assert.Single(await provider.DiscoverAsync()); Assert.Contains("/repos/alex/trip/contents/travel.yaml", paths);
    }
    [Fact]
    public async Task DeviceFlowDoesNotRequestBroadOAuthScope()
    {
        var provider = new GitHubProvider(new HttpClient(new Handler(async r =>
        {
            Assert.Equal("/login/device/code", r.RequestUri!.AbsolutePath); Assert.Null(r.Headers.Authorization); var body = await r.Content!.ReadAsStringAsync(); Assert.DoesNotContain("repo", body); return Json(new { device_code = "device", user_code = "USER", verification_uri = "https://github.com/login/device", expires_in = 600, interval = 5 });
        })), new Secrets(), "client"); Assert.Equal("USER", (await provider.BeginAsync()).UserCode);
    }
}
