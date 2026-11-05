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
    public async Task DiscoveryReportsProgressAndSkipsEmptyRepositories()
    {
        var yaml = YamlCodec.Write(Entity.CreateTrip("Trip")); var paths = new System.Collections.Concurrent.ConcurrentBag<string>();
        object Repo(string name, int size) => new { full_name = "alex/" + name, clone_url = "https://github.com/alex/" + name + ".git", html_url = "https://github.com/alex/" + name, @private = true, size };
        var provider = new GitHubProvider(new HttpClient(new Handler(r =>
        {
            var path = r.RequestUri!.AbsolutePath; paths.Add(path);
            return Task.FromResult(path switch
            {
                "/user/installations" => Json(new { installations = new[] { new { id = 1 }, new { id = 2 } } }),
                "/user/installations/1/repositories" => Json(new { repositories = new[] { Repo("trip", 12), Repo("empty", 0), Repo("code", 40) } }),
                "/user/installations/2/repositories" => Json(new { repositories = new[] { Repo("trip", 12), Repo("later", 8) } }),
                "/repos/alex/code/contents/travel.yaml" => new HttpResponseMessage(HttpStatusCode.NotFound),
                _ => Json(new { content = Convert.ToBase64String(Encoding.UTF8.GetBytes(yaml)) })
            });
        })), new Secrets(), "client");
        var reports = new System.Collections.Concurrent.ConcurrentQueue<DiscoveryProgress>();
        var trips = await provider.DiscoverAsync(new Listener(reports.Enqueue));
        Assert.Equal(["alex/trip", "alex/later"], trips.Select(t => t.FullName));
        Assert.DoesNotContain("/repos/alex/empty/contents/travel.yaml", paths);
        Assert.All(reports, p => Assert.Equal(4, p.Total));
        Assert.Equal(4, reports.Max(p => p.Checked));
    }
    [Fact]
    public async Task RepositoryAccessNeedsAnInstallation()
    {
        var installations = 0;
        var provider = new GitHubProvider(new HttpClient(new Handler(r =>
        {
            Assert.Equal("/user/installations", r.RequestUri!.AbsolutePath);
            return Task.FromResult(Json(new { total_count = installations, installations = Array.Empty<object>() }));
        })), new Secrets(), "client", "jourfold");
        Assert.False(await provider.HasRepositoryAccessAsync());
        installations = 1; Assert.True(await provider.HasRepositoryAccessAsync());
        Assert.Equal("https://github.com/apps/jourfold/installations/new", provider.RepositoryAccessUri!.AbsoluteUri);
        Assert.Null(new GitHubProvider(new HttpClient(), new Secrets(), "client").RepositoryAccessUri);
    }
    private sealed class Listener(Action<DiscoveryProgress> report) : IProgress<DiscoveryProgress> { public void Report(DiscoveryProgress value) => report(value); }
    [Fact]
    public async Task DeviceFlowDoesNotRequestBroadOAuthScope()
    {
        var provider = new GitHubProvider(new HttpClient(new Handler(async r =>
        {
            Assert.Equal("/login/device/code", r.RequestUri!.AbsolutePath); Assert.Null(r.Headers.Authorization); var body = await r.Content!.ReadAsStringAsync(); Assert.DoesNotContain("repo", body); return Json(new { device_code = "device", user_code = "USER", verification_uri = "https://github.com/login/device", expires_in = 600, interval = 5 });
        })), new Secrets(), "client"); Assert.Equal("USER", (await provider.BeginAsync()).UserCode);
    }
    [Fact]
    public async Task PollingADeviceSignInReportsEachAnswerAndKeepsTheToken()
    {
        var answers = new Queue<Func<HttpResponseMessage>>([
            () => Json(new { error = "authorization_pending" }), () => Json(new { error = "slow_down" }),
            () => throw new HttpRequestException("connection aborted"), () => new HttpResponseMessage(HttpStatusCode.BadGateway),
            () => Json(new { access_token = "token" }), () => Json(new { error = "access_denied" })]);
        var secrets = new MemorySecrets();
        var provider = new GitHubProvider(new HttpClient(new Handler(r => { Assert.Equal("/login/oauth/access_token", r.RequestUri!.AbsolutePath); return Task.FromResult(answers.Dequeue()()); })), secrets, "client");
        var code = new DeviceAuthorization("device", "USER", "https://github.com/login/device", 900, 5);

        Assert.Equal(DeviceSignIn.Pending, await provider.PollAsync(code));
        Assert.Equal(DeviceSignIn.SlowDown, await provider.PollAsync(code));
        // A phone coming back from the browser may lose a request; that is worth asking again, a refusal is not.
        var lost = await Assert.ThrowsAsync<HttpRequestException>(() => provider.PollAsync(code)); Assert.True(GitHubProvider.IsTransient(lost));
        var busy = await Assert.ThrowsAsync<DomainException>(() => provider.PollAsync(code)); Assert.True(GitHubProvider.IsTransient(busy));
        Assert.Equal(DeviceSignIn.Connected, await provider.PollAsync(code)); Assert.Equal("token", secrets.Values["github.user-token"]);
        var declined = await Assert.ThrowsAsync<DomainException>(() => provider.PollAsync(code)); Assert.False(GitHubProvider.IsTransient(declined));
    }
}
