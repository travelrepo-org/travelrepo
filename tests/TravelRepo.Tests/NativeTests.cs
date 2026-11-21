using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using TravelRepo.Git;
using TravelRepo.Native;
using Xunit;
namespace TravelRepo.Tests;

/// <summary>The C ABI in TravelRepo.Native, called through its exported functions as a native client would.</summary>
public sealed class NativeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "travelrepo-native-" + Guid.NewGuid().ToString("N"));
    private static readonly ConcurrentDictionary<nint, TaskCompletionSource<string>> Answers = new();
    private static long lastContext;

    public void Dispose() => TestFiles.Delete(root);

    [Fact]
    public void EveryMcpToolIsACommandWithTheSameArguments()
    {
        var methods = Type.GetType("TravelRepo.Mcp.TripTools, TravelRepo.Mcp", true)!.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Select(m => (Method: m, Attribute: m.GetCustomAttribute<McpServerToolAttribute>())).Where(m => m.Attribute is not null).ToArray();
        Assert.Equal(methods.Select(m => m.Attribute!.Name).Order(StringComparer.Ordinal), Tools.All.Select(t => t.Name).Order(StringComparer.Ordinal));
        foreach (var (method, attribute) in methods)
        {
            var tool = Tools.ByName[attribute!.Name!];
            Assert.True(attribute.ReadOnly == tool.ReadOnly, tool.Name + " read-only");
            var read = new List<(string Name, string Type, object? Default, bool Required)>();
            Assert.Throws<NullReferenceException>(() => { _ = tool.Run(null!, new Arguments([]) { Recorder = read }, default); });
            var expected = method.GetParameters().Where(p => p.ParameterType != typeof(CancellationToken))
                .Select(p => (p.Name!, Kind(p.ParameterType), p.HasDefaultValue ? p.DefaultValue : null, !p.HasDefaultValue)).ToList();
            Assert.Equal(expected, read);
        }
    }

    private static string Kind(Type type) => type == typeof(string) ? "string" : type == typeof(int) ? "int" : type == typeof(bool) ? "bool" : type == typeof(string[]) ? "string[]" : type == typeof(JsonElement) ? "json" : type.Name;

    [Fact]
    public async Task ClientsOpenATripReadItAndEditIt()
    {
        var info = await Ok(0, "info");
        Assert.Equal(TravelRepo.Core.TravelRepoInfo.FormatVersion, info["format_version"]!.GetValue<string>());
        Assert.Contains("get_schedule", info["commands"]!["session"]!.AsArray().Select(c => c!.GetValue<string>()));

        var session = (await Ok(0, "open", new JsonObject { ["root"] = Trip(), ["recovery_root"] = Path.Combine(root, "recovery") }))["session"]!.GetValue<ulong>();
        Assert.Equal("Tokyo", (await Ok(session, "get_trip"))["title"]!.GetValue<string>());
        var snapshot = await Ok(session, "snapshot");
        Assert.Contains(snapshot["entities"]!.AsArray(), e => e!["path"]!.GetValue<string>() == "travel.yaml");
        Assert.Empty(snapshot["diagnostics"]!.AsArray());

        var created = await Ok(session, "create_entity", new JsonObject { ["type"] = "person", ["data"] = new JsonObject { ["title"] = "Sam" } });
        var id = created["id"]!.GetValue<string>();
        Assert.Equal("Sam", (await Ok(session, "get_entity", new JsonObject { ["id"] = id }))["entity"]!["title"]!.GetValue<string>());
        Assert.Equal(JsonValueKind.String, (await Ok(session, "read_guide")).GetValueKind());

        Assert.Equal("command.unknown", await Failure(session, "fly_to_the_moon"));
        Assert.Equal("argument.missing", await Failure(session, "get_entity"));
        Assert.Equal("argument.invalid", await Failure(session, "list_entities", new JsonObject { ["limit"] = "many" }));
        Assert.Equal("command.unknown", await Failure(0, "get_trip"));
        Assert.Equal("session.not_trip", await Failure(0, "open", new JsonObject { ["root"] = root }));

        Exports.Close(session);
        Assert.Equal("session.unknown", await Failure(session, "get_trip"));
    }

    [Fact]
    public async Task ReadOnlySessionsRefuseEdits()
    {
        var session = (await Ok(0, "open", new JsonObject { ["root"] = Trip(), ["recovery_root"] = Path.Combine(root, "recovery"), ["read_only"] = true }))["session"]!.GetValue<ulong>();
        Assert.Equal("session.read_only", await Failure(session, "create_entity", new JsonObject { ["type"] = "person", ["data"] = new JsonObject { ["title"] = "Sam" } }));
    }

    [Fact]
    public async Task ExportsFollowTheSessionCulture()
    {
        var trip = Trip();
        async Task<string> Plan(string culture)
        {
            var session = (await Ok(0, "open", new JsonObject { ["root"] = trip, ["recovery_root"] = Path.Combine(root, "recovery"), ["culture"] = culture }))["session"]!.GetValue<ulong>();
            return (await Ok(session, "export_text"))["text"]!.GetValue<string>();
        }
        Assert.Contains("Mai", await Plan("de-DE"));
        Assert.Contains("May", await Plan("en-NZ"));
        Assert.Equal("argument.invalid", await Failure(0, "open", new JsonObject { ["root"] = trip, ["culture"] = "xx-not-a-culture" }));
    }

    [Fact]
    public async Task VersionsAndSyncStateWorkThroughLibGit2()
    {
        var session = (await Ok(0, "open", new JsonObject { ["root"] = Trip(), ["recovery_root"] = Path.Combine(root, "recovery") }))["session"]!.GetValue<ulong>();
        await Ok(session, "initialize_git", new JsonObject { ["name"] = "Alex", ["email"] = "alex@example.invalid" });
        await Ok(session, "create_entity", new JsonObject { ["type"] = "person", ["data"] = new JsonObject { ["title"] = "Sam" } });
        await Ok(session, "create_version", new JsonObject { ["message"] = "Add Sam" });
        Assert.Contains((await Ok(session, "list_versions"))["versions"]!.AsArray(), v => v!["message"]!.GetValue<string>() == "Add Sam");
        var status = await Ok(session, "sync_status");
        Assert.Equal("local_only", status["state"]!.GetValue<string>()); Assert.False(status["has_local_changes"]!.GetValue<bool>(), status.ToJsonString());
    }

    [Fact]
    public async Task CredentialsComeFromTheHostAndCanBeCancelled()
    {
        var credentials = new HostCredentials();
        Assert.Null(await credentials.GetAsync(new Uri("https://github.com/alex-travels/trip.git")));

        credentials.Request = (request, origin) => credentials.Reply(request, origin == "https://github.com" ? """{"username":"x-access-token","password":"test-token"}""" : null);
        Assert.Equal(new GitCredential("x-access-token", "test-token"), await credentials.GetAsync(new Uri("https://github.com/alex-travels/trip.git")));
        Assert.Null(await credentials.GetAsync(new Uri("https://example.org/trip.git")));

        credentials.Request = (_, _) => { };
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => credentials.GetAsync(new Uri("https://github.com/alex-travels/trip.git"), cancel.Token));
    }

    [Fact]
    public async Task AppsEditWithRevisionsAndGetConflictsWhenTheTripChanged()
    {
        var created = await Ok(0, "create_trip", new JsonObject { ["root"] = Path.Combine(root, "spring"), ["recovery_root"] = Path.Combine(root, "recovery"), ["title"] = "Spring in Japan", ["timezone"] = "Asia/Tokyo" });
        Assert.Equal("trip.exists", await Failure(0, "create_trip", new JsonObject { ["root"] = Path.Combine(root, "spring"), ["title"] = "Again" }));
        var session = (await Ok(0, "open", new JsonObject { ["root"] = created["root"]!.GetValue<string>(), ["recovery_root"] = Path.Combine(root, "recovery") }))["session"]!.GetValue<ulong>();
        var revision = (await Ok(session, "snapshot"))["revision"]!.GetValue<string>();

        var alex = TravelRepo.Core.Entity.Create("person", "Alex"); var sam = TravelRepo.Core.Entity.Create("person", "Sam");
        var applied = await Ok(session, "apply", new JsonObject
        {
            ["revision"] = revision,
            ["edits"] = new JsonArray(new JsonObject { ["id"] = alex.Id.ToString(), ["data"] = alex.Data.DeepClone() }, new JsonObject { ["id"] = sam.Id.ToString(), ["data"] = sam.Data.DeepClone() }),
            ["resources"] = new JsonArray(new JsonObject { ["path"] = "documents/" + alex.Id + ".md", ["base64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("Window seat to Kyoto.")) })
        });
        var after = applied["revision"]!.GetValue<string>();
        Assert.NotEqual(revision, after);
        Assert.Equal(after, (await Ok(session, "revision"))["revision"]!.GetValue<string>());
        Assert.Equal("Window seat to Kyoto.", Encoding.UTF8.GetString(Convert.FromBase64String((await Ok(session, "read_resource", new JsonObject { ["path"] = "documents/" + alex.Id + ".md" }))["base64"]!.GetValue<string>())));
        Assert.Equal("resource.missing", await Failure(session, "read_resource", new JsonObject { ["path"] = "documents/none.md" }));

        // An edit based on the old revision must not overwrite what happened since.
        Assert.Equal("repository.changed", await Failure(session, "apply", new JsonObject { ["revision"] = revision, ["edits"] = new JsonArray(new JsonObject { ["id"] = sam.Id.ToString(), ["data"] = null }) }));
        Assert.Equal("repository.changed", await Failure(session, "apply", new JsonObject { ["revision"] = "0000000000000000", ["edits"] = new JsonArray(new JsonObject { ["id"] = sam.Id.ToString(), ["data"] = null }) }));
        await Ok(session, "apply", new JsonObject { ["revision"] = after, ["edits"] = new JsonArray(new JsonObject { ["id"] = sam.Id.ToString(), ["data"] = null }) });
        Assert.DoesNotContain((await Ok(session, "snapshot"))["entities"]!.AsArray(), e => e!["data"]!["id"]!.GetValue<string>() == sam.Id.ToString());

        var readOnly = (await Ok(0, "open", new JsonObject { ["root"] = created["root"]!.GetValue<string>(), ["recovery_root"] = Path.Combine(root, "recovery"), ["read_only"] = true }))["session"]!.GetValue<ulong>();
        Assert.Equal("session.read_only", await Failure(readOnly, "apply", new JsonObject { ["revision"] = after, ["edits"] = new JsonArray() }));
    }

    [Fact]
    public async Task AgendaAndSearchGiveStructuredResults()
    {
        var session = (await Ok(0, "open", new JsonObject { ["root"] = Trip(), ["recovery_root"] = Path.Combine(root, "recovery") }))["session"]!.GetValue<ulong>();
        var agenda = await Ok(session, "agenda");
        var flight = agenda["days"]!.AsArray().SelectMany(d => d!["entries"]!.AsArray()).First(e => e!["title"]!.GetValue<string>() == "Flight to Tokyo")!;
        Assert.Equal("2027-05-12T11:20:00Z", flight["start"]!.GetValue<string>());
        Assert.Equal("Europe/Berlin", flight["zone"]!.GetValue<string>());
        Assert.Equal("Asia/Tokyo", flight["end_zone"]!.GetValue<string>());
        Assert.Equal("transport", flight["kind"]!.GetValue<string>());

        var hit = Assert.Single((await Ok(session, "find", new JsonObject { ["query"] = "fligh tok", ["kinds"] = new JsonArray("plan") }))["hits"]!.AsArray())!;
        Assert.Equal("plan", hit["kind"]!.GetValue<string>());
        var match = hit["matches"]![0]!;
        Assert.Equal("title", match["field"]!.GetValue<string>());
        Assert.Equal("Fligh", match["text"]!.GetValue<string>().Substring(match["ranges"]![0]!["start"]!.GetValue<int>(), match["ranges"]![0]!["length"]!.GetValue<int>()));
        Assert.Equal("argument.invalid", await Failure(session, "find", new JsonObject { ["query"] = "x", ["kinds"] = new JsonArray("spaceship") }));
    }

    [Fact]
    public async Task VariantsAreMergedWithAnsweredConflicts()
    {
        var created = await Ok(0, "create_trip", new JsonObject { ["root"] = Path.Combine(root, "kyoto"), ["recovery_root"] = Path.Combine(root, "recovery"), ["title"] = "Kyoto", ["git_name"] = "Alex", ["git_email"] = "alex@example.invalid" });
        var session = (await Ok(0, "open", new JsonObject { ["root"] = created["root"]!.GetValue<string>(), ["recovery_root"] = Path.Combine(root, "recovery") }))["session"]!.GetValue<ulong>();
        var temple = (await Ok(session, "create_entity", new JsonObject { ["type"] = "place", ["data"] = new JsonObject { ["title"] = "Kiyomizu-dera" } }))["id"]!.GetValue<string>();
        await Ok(session, "create_version", new JsonObject { ["message"] = "Add the temple" });

        async Task Rename(string title, string message)
        {
            var entity = (await Ok(session, "get_entity", new JsonObject { ["id"] = temple }))["entity"]!.AsObject(); entity["title"] = title;
            await Ok(session, "apply", new JsonObject { ["revision"] = (await Ok(session, "revision"))["revision"]!.DeepClone(), ["edits"] = new JsonArray(new JsonObject { ["id"] = temple, ["data"] = entity.DeepClone() }) });
            await Ok(session, "create_version", new JsonObject { ["message"] = message });
        }
        await Ok(session, "create_variant", new JsonObject { ["branch"] = "variants/early-start", ["title"] = "Early start" });
        await Rename("Kiyomizu-dera at dawn", "Go early");
        var variants = (await Ok(session, "variants"))["variants"]!.AsArray();
        Assert.Contains(variants, v => v!["branch"]!.GetValue<string>() == "variants/early-start" && v["current"]!.GetValue<bool>());
        var primary = variants.First(v => v!["primary"]!.GetValue<bool>())!["branch"]!.GetValue<string>();
        await Ok(session, "switch_variant", new JsonObject { ["branch"] = primary });
        await Rename("Kiyomizu-dera at sunset", "Go late");

        var preview = await Ok(session, "merge_preview", new JsonObject { ["branch"] = "variants/early-start" });
        Assert.False(preview["can_apply"]!.GetValue<bool>());
        var conflict = Assert.Single(preview["conflicts"]!.AsArray())!;
        Assert.Equal("Kiyomizu-dera at sunset", conflict["current"]!.GetValue<string>());
        Assert.Equal("Kiyomizu-dera at dawn", conflict["incoming"]!.GetValue<string>());
        Assert.Equal("merge.unresolved", await Failure(session, "merge_apply", new JsonObject { ["branch"] = "variants/early-start" }));

        var resolutions = new JsonObject { [conflict["key"]!.GetValue<string>()] = "Kiyomizu-dera at dawn" };
        Assert.True((await Ok(session, "merge_preview", new JsonObject { ["branch"] = "variants/early-start", ["resolutions"] = resolutions.DeepClone() }))["can_apply"]!.GetValue<bool>());
        Assert.NotEmpty((await Ok(session, "merge_apply", new JsonObject { ["branch"] = "variants/early-start", ["resolutions"] = resolutions.DeepClone() }))["commit"]!.GetValue<string>());
        Assert.Equal("Kiyomizu-dera at dawn", (await Ok(session, "get_entity", new JsonObject { ["id"] = temple }))["entity"]!["title"]!.GetValue<string>());
        Assert.Contains((await Ok(session, "list_versions"))["versions"]!.AsArray(), v => v!["message"]!.GetValue<string>().StartsWith("Merge Early start", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GitHubSignInAndRepositoriesWorkThroughTheInterface()
    {
        var yaml = TravelRepo.Serialization.YamlCodec.Write(TravelRepo.Core.Entity.CreateTrip("Spring in Japan"));
        var polls = 0; var requests = new System.Collections.Concurrent.ConcurrentBag<string>();
        static HttpResponseMessage Json(string json) => new(System.Net.HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        GitHubCommands.Http = new HttpClient(new FakeGitHub(r =>
        {
            requests.Add(r.Method + " " + r.RequestUri!.AbsolutePath);
            return r.RequestUri!.AbsolutePath switch
            {
                "/login/device/code" => Json("""{"device_code":"device","user_code":"WDJB-MJHT","verification_uri":"https://github.com/login/device","expires_in":900,"interval":5}"""),
                "/login/oauth/access_token" => Json(Interlocked.Increment(ref polls) == 1 ? """{"error":"authorization_pending"}""" : """{"access_token":"test-token"}"""),
                "/user" => Json("""{"login":"alex-travels","name":"Alex","avatar_url":"https://example.org/alex.png","html_url":"https://github.com/alex-travels"}"""),
                "/user/installations" => Json("""{"installations":[{"id":7}]}"""),
                "/user/installations/7/repositories" => Json("""{"repositories":[{"full_name":"alex-travels/spring","clone_url":"https://github.com/alex-travels/spring.git","html_url":"https://github.com/alex-travels/spring","private":true,"size":3}]}"""),
                "/repos/alex-travels/spring/contents/travel.yaml" => Json("{\"content\":\"" + Convert.ToBase64String(Encoding.UTF8.GetBytes(yaml)) + "\"}"),
                "/user/repos" => Json("""{"full_name":"alex-travels/kyoto","clone_url":"https://github.com/alex-travels/kyoto.git","html_url":"https://github.com/alex-travels/kyoto","private":true}"""),
                "/search/users" => Json("""{"items":[{"login":"sam-travels"}]}"""),
                _ => Json("{}")
            };
        }));
        GitHubCommands.Reset();
        try
        {
            await Ok(0, "github_configure", new JsonObject { ["client_id"] = "" });
            Assert.Equal("github.setup", await Failure(0, "github_begin_sign_in"));
            await Ok(0, "github_configure", new JsonObject { ["client_id"] = "test-client", ["app_slug"] = "travelrepo-test" });
            Assert.False((await Ok(0, "github_status"))["connected"]!.GetValue<bool>());

            var authorization = await Ok(0, "github_begin_sign_in");
            Assert.Equal("WDJB-MJHT", authorization["user_code"]!.GetValue<string>());
            Assert.Equal("pending", (await Ok(0, "github_poll_sign_in", authorization.AsObject().DeepClone().AsObject()))["state"]!.GetValue<string>());
            var connected = await Ok(0, "github_poll_sign_in", authorization.AsObject().DeepClone().AsObject());
            Assert.Equal("connected", connected["state"]!.GetValue<string>());
            Assert.Equal("test-token", connected["token"]!.GetValue<string>());
            Assert.Equal("alex-travels", (await Ok(0, "github_status"))["account"]!["login"]!.GetValue<string>());

            // Git uses the sign-in for github.com when the app has no credential handler of its own.
            Assert.Equal(new GitCredential("x-access-token", "test-token"), await NativeHost.Credentials.GetAsync(new Uri("https://github.com/alex-travels/spring.git")));
            Assert.Null(await NativeHost.Credentials.GetAsync(new Uri("https://example.org/spring.git")));

            Assert.Equal("alex-travels/spring", Assert.Single((await Ok(0, "github_repositories"))["repositories"]!.AsArray())!["full_name"]!.GetValue<string>());
            Assert.True((await Ok(0, "github_create_repository", new JsonObject { ["name"] = "kyoto" }))["private"]!.GetValue<bool>());
            await Ok(0, "github_invite", new JsonObject { ["full_name"] = "alex-travels/kyoto", ["username"] = "sam-travels" });
            Assert.Contains("PUT /repos/alex-travels/kyoto/collaborators/sam-travels", requests);
            Assert.Equal("sam-travels", (await Ok(0, "github_search_users", new JsonObject { ["query"] = "sam" }))["logins"]![0]!.GetValue<string>());

            await Ok(0, "github_sign_out");
            Assert.False((await Ok(0, "github_status"))["connected"]!.GetValue<bool>());
            await Ok(0, "github_set_token", new JsonObject { ["token"] = "stored-token" });
            Assert.True((await Ok(0, "github_status"))["connected"]!.GetValue<bool>());
        }
        finally
        {
            await Ok(0, "github_set_token", new JsonObject { ["token"] = null });
            GitHubCommands.Http = new HttpClient(); GitHubCommands.Reset();
        }
    }

    [Fact]
    public async Task AppsReadCostsPackingStaysRemindersAndExports()
    {
        var trip = Trip();
        var session = (await Ok(0, "open", new JsonObject
        {
            ["root"] = trip,
            ["recovery_root"] = Path.Combine(root, "recovery"),
            ["culture"] = "de-DE",
            ["exchange_rates"] = Path.Combine(root, "rates.json"),
            ["weather_cache"] = Path.Combine(root, "weather")
        }))["session"]!.GetValue<ulong>();
        await Ok(session, "add_expense", new JsonObject { ["title"] = "Sushi at Tsukiji", ["amount"] = "6000", ["currency"] = "JPY", ["paid_by"] = new JsonArray("Alex") });
        var list = (await Ok(session, "create_packing_list", new JsonObject { ["title"] = "Kyoto" }))["id"]!.GetValue<string>();
        await Ok(session, "add_packing_item", new JsonObject { ["list"] = list, ["title"] = "Rail pass", ["everyone"] = true });

        var costs = await Ok(session, "costs");
        var sushi = costs["expenses"]!.AsArray().Single(e => e!["title"]!.GetValue<string>() == "Sushi at Tsukiji")!;
        Assert.Equal("6000", sushi["amount"]!["value"]!.GetValue<string>());
        Assert.Contains(costs["open_debts"]!.AsArray(), d => d!["currency"]!.GetValue<string>() == "JPY" && d["open"]!.GetValue<string>() == "3000");
        Assert.Contains(costs["totals"]!.AsArray(), t => t!["currency"]!.GetValue<string>() == "JPY");
        var transfer = Assert.Single(costs["settle_up"]!.AsArray())!;
        Assert.Equal("3000", transfer["amount"]!.GetValue<string>());
        Assert.Equal("c3416734-e021-7185-91a7-0d5e9bf6aa7c", transfer["to"]!.GetValue<string>()); // Alex paid, so Alex is paid back.

        var packing = (await Ok(session, "packing"))["lists"]!.AsArray().Single(l => l!["id"]!.GetValue<string>() == list)!;
        Assert.True(Assert.Single(packing["items"]!.AsArray())!["everyone"]!.GetValue<bool>());
        Assert.NotNull((await Ok(session, "bookings"))["bookings"]);
        Assert.NotNull((await Ok(session, "stays"))["stays"]);
        Assert.NotNull((await Ok(session, "reminders", new JsonObject { ["now"] = "2027-05-01T08:00:00Z" }))["reminders"]);
        Assert.Equal("argument.invalid", await Failure(session, "reminders", new JsonObject { ["now"] = "tomorrow" }));

        Assert.Contains("Mai", (await Ok(session, "export"))["text"]!.GetValue<string>());
        Assert.Contains("<html", (await Ok(session, "export", new JsonObject { ["format"] = "html" }))["text"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("BEGIN:VCALENDAR", (await Ok(session, "export", new JsonObject { ["format"] = "ics" }))["text"]!.GetValue<string>());
        Assert.Contains("Sushi at Tsukiji", (await Ok(session, "export", new JsonObject { ["kind"] = "costs", ["format"] = "csv" }))["text"]!.GetValue<string>());
        Assert.Contains("Rail pass", (await Ok(session, "export", new JsonObject { ["kind"] = "packing" }))["text"]!.GetValue<string>());
        Assert.Equal("argument.invalid", await Failure(session, "export", new JsonObject { ["kind"] = "packing", ["format"] = "ics" }));

        // A document from bytes, as the share sheet hands it over, attached to the flight.
        const string flight = "669afb3f-38b4-7df8-9f47-910f4e1b17d5";
        var imported = await Ok(session, "import_document_data", new JsonObject { ["name"] = "seat.txt", ["base64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("Window seat 23A")), ["attach_to"] = flight });
        var document = imported["id"]!.GetValue<string>();
        Assert.Contains(document, (await Ok(session, "get_entity", new JsonObject { ["id"] = flight }))["entity"]!["content"]!.ToJsonString());
        Assert.Equal("argument.invalid", await Failure(session, "import_document_data", new JsonObject { ["name"] = "seat.txt", ["base64"] = "not base64!" }));
    }

    [Fact]
    public async Task ExchangeRatesAndWeatherAreFetchedThroughTheInterface()
    {
        var ecb = """<gesmes:Envelope xmlns:gesmes="http://www.gesmes.org/xml/2002-08-01" xmlns="http://www.ecb.int/vocabulary/2002-08-01/eurofxref"><Cube><Cube time="2027-05-10"><Cube currency="JPY" rate="160"/><Cube currency="NZD" rate="1.85"/></Cube></Cube></gesmes:Envelope>""";
        AppCommands.Http = new HttpClient(new FakeGitHub(r => r.RequestUri!.Host.Contains("ecb.europa.eu")
            ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(ecb, Encoding.UTF8, "application/xml") }
            : new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)));
        try
        {
            var trip = Trip(); var rates = Path.Combine(root, "rates.json");
            var plain = (await Ok(0, "open", new JsonObject { ["root"] = trip, ["recovery_root"] = Path.Combine(root, "recovery") }))["session"]!.GetValue<ulong>();
            Assert.Equal("weather.cache", await Failure(plain, "fetch_weather"));
            var session = (await Ok(0, "open", new JsonObject { ["root"] = trip, ["recovery_root"] = Path.Combine(root, "recovery"), ["exchange_rates"] = rates, ["weather_cache"] = Path.Combine(root, "weather") }))["session"]!.GetValue<ulong>();

            var fetched = await Ok(session, "fetch_exchange_rates");
            Assert.Equal("2027-05-10", fetched["date"]!.GetValue<string>());
            Assert.True(fetched["saved"]!.GetValue<bool>());
            Assert.Equal(160m, TravelRepo.Core.ExchangeRates.Load(rates)!.PerEuro["JPY"]);

            // Every weather service is down here: the result says so instead of failing the call.
            var weather = await Ok(session, "fetch_weather");
            Assert.NotNull(weather["days"]);
            Assert.NotNull(weather["problems"]);
        }
        finally { AppCommands.Http = new HttpClient(); }
    }

    [Fact]
    public async Task EventsReportCloneProgressAndTripChanges()
    {
        var events = new ConcurrentQueue<JsonNode>();
        NativeHost.Events = text => events.Enqueue(JsonNode.Parse(text)!);
        try
        {
            // A trip with Git history on a bare remote, cloned through the interface.
            var remote = Path.Combine(root, "remote.git");
            using (var bare = new LibGit2Sharp.Repository(LibGit2Sharp.Repository.Init(remote, isBare: true))) bare.Refs.UpdateTarget("HEAD", "refs/heads/main");
            var created = await Ok(0, "create_trip", new JsonObject { ["root"] = Path.Combine(root, "alex"), ["recovery_root"] = Path.Combine(root, "recovery"), ["title"] = "Spring in Japan", ["git_name"] = "Alex", ["git_email"] = "alex@example.invalid" });
            var alex = (await Ok(0, "open", new JsonObject { ["root"] = created["root"]!.DeepClone(), ["recovery_root"] = Path.Combine(root, "recovery") }))["session"]!.GetValue<ulong>();
            await Ok(alex, "add_remote", new JsonObject { ["name"] = "origin", ["url"] = remote });
            Assert.Equal("synced", (await Ok(alex, "sync"))["state"]!.GetValue<string>());
            await Ok(0, "clone", new JsonObject { ["url"] = remote, ["destination"] = Path.Combine(root, "sam") });
            var progress = events.Where(e => e["type"]!.GetValue<string>() == "clone_progress").ToArray();
            Assert.NotEmpty(progress);
            // A local clone copies the objects without a transfer; its checkout is still reported, up to the last file.
            Assert.All(progress, e => Assert.Equal(progress[0]["call"]!.GetValue<ulong>(), e["call"]!.GetValue<ulong>()));
            Assert.True(progress[^1]["checkout_total"]!.GetValue<int>() > 0 && progress[^1]["checkout_completed"]!.GetValue<int>() == progress[^1]["checkout_total"]!.GetValue<int>(), progress[^1].ToJsonString());

            // An edit by someone else, here a second session on the clone, reaches a session that watches.
            var sam = (await Ok(0, "open", new JsonObject { ["root"] = Path.Combine(root, "sam"), ["recovery_root"] = Path.Combine(root, "recovery-sam") }))["session"]!.GetValue<ulong>();
            // A clone has no Git identity of its own until the app sets one.
            await Ok(sam, "set_identity", new JsonObject { ["name"] = "Sam", ["email"] = "sam@example.invalid" });
            Assert.True((await Ok(sam, "watch", new JsonObject { ["interval_seconds"] = 1 }))["watching"]!.GetValue<bool>());
            var editor = new TravelRepo.Repository.TravelRepository(Path.Combine(root, "sam"), Path.Combine(root, "recovery-editor"));
            var nara = TravelRepo.Core.Entity.Create("place", "Nara Park");
            await editor.ApplyAsync(await editor.ReadAsync(), [new(nara.Id, nara)]);
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline && !events.Any(e => e["type"]!.GetValue<string>() == "trip_changed" && e["session"]!.GetValue<ulong>() == sam)) await Task.Delay(100);
            var changed = Assert.Single(events, e => e["type"]!.GetValue<string>() == "trip_changed" && e["session"]!.GetValue<ulong>() == sam)!;
            Assert.Equal((await Ok(sam, "revision"))["revision"]!.GetValue<string>(), changed["revision"]!.GetValue<string>());
            Assert.False((await Ok(sam, "watch", new JsonObject { ["enabled"] = false }))["watching"]!.GetValue<bool>());
            await Ok(sam, "create_version", new JsonObject { ["message"] = "Add Nara Park" });
            Assert.Equal("Sam", (await Ok(sam, "list_versions"))["versions"]![0]!["author"]!.GetValue<string>());
        }
        finally { NativeHost.Events = null; }
    }

    [Fact]
    public async Task KeepBothKeepsTheCurrentEntityAndAddsTheIncomingOne()
    {
        var created = await Ok(0, "create_trip", new JsonObject { ["root"] = Path.Combine(root, "nara"), ["recovery_root"] = Path.Combine(root, "recovery"), ["title"] = "Nara", ["git_name"] = "Alex", ["git_email"] = "alex@example.invalid" });
        var session = (await Ok(0, "open", new JsonObject { ["root"] = created["root"]!.DeepClone(), ["recovery_root"] = Path.Combine(root, "recovery") }))["session"]!.GetValue<ulong>();
        var lunch = (await Ok(session, "create_entity", new JsonObject { ["type"] = "schedule_item", ["data"] = new JsonObject { ["title"] = "Lunch" } }))["id"]!.GetValue<string>();
        await Ok(session, "create_version", new JsonObject { ["message"] = "Add lunch" });
        async Task Rename(string title)
        {
            var entity = (await Ok(session, "get_entity", new JsonObject { ["id"] = lunch }))["entity"]!.AsObject(); entity["title"] = title;
            await Ok(session, "apply", new JsonObject { ["revision"] = (await Ok(session, "revision"))["revision"]!.DeepClone(), ["edits"] = new JsonArray(new JsonObject { ["id"] = lunch, ["data"] = entity.DeepClone() }) });
            await Ok(session, "create_version", new JsonObject { ["message"] = "Lunch: " + title });
        }
        await Ok(session, "create_variant", new JsonObject { ["branch"] = "variants/deer-park", ["title"] = "Deer park" });
        await Rename("Lunch by the deer park");
        var primary = (await Ok(session, "variants"))["variants"]!.AsArray().First(v => v!["primary"]!.GetValue<bool>())!["branch"]!.GetValue<string>();
        await Ok(session, "switch_variant", new JsonObject { ["branch"] = primary });
        await Rename("Lunch at Kasuga Taisha");

        Assert.True((await Ok(session, "merge_preview", new JsonObject { ["branch"] = "variants/deer-park", ["keep_both"] = new JsonArray(lunch) }))["can_apply"]!.GetValue<bool>());
        await Ok(session, "merge_apply", new JsonObject { ["branch"] = "variants/deer-park", ["keep_both"] = new JsonArray(lunch) });
        var titles = (await Ok(session, "snapshot"))["entities"]!.AsArray().Select(e => e!["data"]!["title"]?.GetValue<string>()).ToArray();
        Assert.Contains("Lunch at Kasuga Taisha", titles);
        Assert.Contains("Lunch by the deer park", titles);
    }

    [Fact]
    public async Task ThePersonUsingTheAppIsFoundAndClaimed()
    {
        var session = (await Ok(0, "open", new JsonObject { ["root"] = Trip(), ["recovery_root"] = Path.Combine(root, "recovery") }))["session"]!.GetValue<ulong>();
        var nobody = await Ok(session, "me", new JsonObject { ["github_login"] = "sam-travels" });
        Assert.Null(nobody["person"]);
        var sam = nobody["travelers"]!.AsArray().Single(t => t!["name"]!.GetValue<string>() == "Sam")!["id"]!.GetValue<string>();
        await Ok(session, "claim_person", new JsonObject { ["person"] = sam, ["github_login"] = "sam-travels", ["git_name"] = "Sam", ["git_email"] = "sam@example.invalid" });
        var me = await Ok(session, "me", new JsonObject { ["github_login"] = "sam-travels" });
        Assert.Equal(sam, me["person"]!.GetValue<string>());
        Assert.Equal("github", me["source"]!.GetValue<string>());
        Assert.Equal(sam, (await Ok(session, "me", new JsonObject { ["git_email"] = "sam@example.invalid" }))["person"]!.GetValue<string>());
        // Someone else signing in sees that Sam is already taken by another account.
        var other = await Ok(session, "me", new JsonObject { ["github_login"] = "alex-travels" });
        Assert.True(other["travelers"]!.AsArray().Single(t => t!["id"]!.GetValue<string>() == sam)!["claimed_by_someone_else"]!.GetValue<bool>());
    }

    private sealed class FakeGitHub(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(answer(request));
    }

    private string Trip()
    {
        var trip = Path.Combine(root, "trip-" + Guid.NewGuid().ToString("N")[..8]);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "tokyo"), "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(trip, Path.GetRelativePath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "tokyo"), file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target);
        }
        return trip;
    }

    private static async Task<JsonNode> Ok(ulong session, string command, JsonObject? arguments = null)
    {
        var response = await Call(session, command, arguments);
        Assert.True(response["ok"]!.GetValue<bool>(), command + ": " + response.ToJsonString());
        return response["result"]!;
    }

    private static async Task<string> Failure(ulong session, string command, JsonObject? arguments = null)
    {
        var response = await Call(session, command, arguments);
        Assert.False(response["ok"]!.GetValue<bool>(), command + ": " + response.ToJsonString());
        Assert.NotEmpty(response["error"]!["message"]!.GetValue<string>());
        return response["error"]!["code"]!.GetValue<string>();
    }

    private static async Task<JsonNode> Call(ulong session, string command, JsonObject? arguments)
    {
        var context = (nint)Interlocked.Increment(ref lastContext);
        var answer = Answers[context] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.NotEqual(0ul, Exports.Call(session, command, arguments?.ToJsonString(), context));
        var text = await answer.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Answers.TryRemove(context, out _);
        return JsonNode.Parse(text)!;
    }

    /// <summary>Calls the exported functions through function pointers, as native code does.</summary>
    private static unsafe class Exports
    {
        public static ulong Call(ulong session, string command, string? arguments, nint context)
        {
            var name = Encoding.UTF8.GetBytes(command + "\0"); var json = arguments is null ? null : Encoding.UTF8.GetBytes(arguments + "\0");
            delegate* unmanaged<ulong, byte*, byte*, delegate* unmanaged<nint, byte*, void>, nint, ulong> call = &NativeApi.Call;
            fixed (byte* n = name) fixed (byte* a = json) return call(session, n, a, &Respond, context);
        }

        public static void Close(ulong session)
        {
            delegate* unmanaged<ulong, void> close = &NativeApi.Close;
            close(session);
        }

        [UnmanagedCallersOnly]
        private static void Respond(nint context, byte* response) => Answers[context].SetResult(Marshal.PtrToStringUTF8((nint)response)!);
    }
}
