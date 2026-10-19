using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NodaTime;
using TravelRepo.Core;
using TravelRepo.Git;
using TravelRepo.Mcp;
using TravelRepo.Providers.Weather;
using TravelRepo.Repository;
using Xunit;
namespace TravelRepo.Tests;

public sealed class McpServerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "travelrepo-mcp-" + Guid.NewGuid());
    private readonly CancellationTokenSource stop = new();
    public void Dispose() { stop.Cancel(); TestFiles.Delete(root); }

    private async Task<(TravelRepository Repo, GitRepository Git)> Trip()
    {
        var repo = new TravelRepository(Path.Combine(root, "trip"), Path.Combine(root, "recovery"));
        await repo.InitializeAsync(Entity.CreateTrip("Kyoto", "en", "Asia/Tokyo"));
        var git = new GitRepository(repo, new GitCliBackend()); await git.InitializeAsync("Test", "test@example.invalid");
        return (repo, git);
    }

    /// <summary>A real MCP client talking to the server through in-memory pipes, as an assistant would over stdio.</summary>
    private async Task<McpClient> Connect(string path, bool readOnly = false, Func<CachedWeatherSource?>? weather = null)
    {
        var toServer = new Pipe(); var toClient = new Pipe();
        var server = TripServer.Create(new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream()), path, new(ReadOnly: readOnly, ClientName: "Jourfold", RecoveryRoot: Path.Combine(root, "recovery"), Weather: weather));
        _ = server.RunAsync(stop.Token);
        return await McpClient.CreateAsync(new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()), cancellationToken: stop.Token);
    }

    private static async Task<JsonNode> Call(McpClient client, string tool, string arguments = "{}")
    {
        var result = await Call(client, tool, arguments, expectError: false);
        return JsonNode.Parse(result)!;
    }
    private static async Task<string> Call(McpClient client, string tool, string arguments, bool expectError)
    {
        var args = JsonDocument.Parse(arguments).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
        var result = await client.CallToolAsync(tool, args);
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(t => t.Text));
        Assert.True(expectError == (result.IsError == true), tool + ": " + text);
        return text;
    }

    [Fact]
    public async Task AssistantPlansADayThroughValidatedEdits()
    {
        var (repo, git) = await Trip(); var client = await Connect(repo.Root);
        Assert.Contains("read_guide", client.ServerInstructions);
        var tools = (await client.ListToolsAsync()).Select(t => t.Name).ToArray();
        Assert.Contains("create_entity", tools); Assert.Contains("get_schedule", tools);
        Assert.Contains("## Time", (await Call(client, "read_guide", "{}", expectError: false)));

        var station = await Call(client, "create_entity", """{"type":"place","data":{"name":"Fushimi Inari Taisha","timezone":"Asia/Tokyo","location":{"latitude":34.9671,"longitude":135.7727}}}""");
        var placeId = station["id"]!.ToString();
        var shrine = await Call(client, "create_entity", """{"type":"schedule_item","data":{"title":"Fushimi Inari","status":"planned","category":"sightseeing","default_place":"@placeId","time":{"precision":"exact","start":{"local":"2027-05-14T09:00:00","timezone":"Asia/Tokyo"},"end":{"local":"2027-05-14T11:00:00","timezone":"Asia/Tokyo"}}}}""".Replace("@placeId", placeId));
        var itemId = shrine["id"]!.ToString();
        // Some models send objects as JSON text.
        await Call(client, "create_entity", """{"type":"schedule_item","data":"{\"title\":\"Sake tasting\",\"duration\":\"PT1H30M\",\"category\":\"food\"}"}""");

        var schedule = await Call(client, "get_schedule", """{"from":"2027-05-14","to":"2027-05-14"}""");
        var entry = schedule["days"]![0]!["items"]![0]!;
        Assert.Equal("Fushimi Inari", entry["title"]!.ToString()); Assert.Equal("2027-05-14 Fri 09:00 to 11:00 Asia/Tokyo", entry["when"]!.ToString()); Assert.Equal("Fushimi Inari Taisha", entry["place"]!.ToString());
        Assert.Equal("Sake tasting", schedule["not_scheduled"]![0]!["title"]!.ToString()); Assert.Equal("idea", schedule["not_scheduled"]![0]!["status"]!.ToString());

        // Invalid changes are refused with a readable reason and leave the files untouched.
        var refused = await Call(client, "update_entity", """{"id":"@itemId","changes":{"status":"maybe"}}""".Replace("@itemId", itemId), expectError: true);
        Assert.Contains("command.invalid", refused); Assert.Contains("status", refused); Assert.DoesNotContain("\"details\"", refused);
        Assert.Equal("planned", (await repo.ReadAsync()).Trip.Find(Guid.Parse(itemId))!.Data["status"]!.ToString());

        // A merge patch changes only the given fields; the item moves an hour later and keeps its place.
        await Call(client, "update_entity", """{"id":"@itemId","changes":{"time":{"start":{"local":"2027-05-14T10:00:00"},"end":{"local":"2027-05-14T12:00:00"}}}}""".Replace("@itemId", itemId));
        var updated = (await repo.ReadAsync()).Trip.Find(Guid.Parse(itemId))!;
        Assert.Equal("2027-05-14T10:00:00", updated.Data["time"]!["start"]!["local"]!.ToString()); Assert.Equal("Asia/Tokyo", updated.Data["time"]!["start"]!["timezone"]!.ToString()); Assert.Equal(placeId, updated.Data["default_place"]!.ToString());

        await Call(client, "add_note", """{"id":"@itemId","markdown":"Go early, the upper paths get busy after 10."}""".Replace("@itemId", itemId));
        var details = await Call(client, "get_entity", """{"id":"@itemId"}""".Replace("@itemId", itemId));
        Assert.Contains("Go early", details["notes"]![0]!["markdown"]!.ToString());

        var alex = await Call(client, "create_entity", """{"type":"person","data":{"display_name":"Alex"}}""");
        Assert.Contains(alex["id"]!.ToString(), (await repo.ReadAsync()).Trip.Manifest.Data["participants"]!.AsArray().Select(n => n!.ToString()));

        var changes = await Call(client, "compare", """{"revision":"HEAD"}""");
        Assert.Contains(changes["changes"]!.AsArray(), c => c!["title"]!.ToString() == "Fushimi Inari" && c["change"]!.ToString() == "added");
        await Call(client, "create_version", """{"message":"Plan Fushimi Inari"}""");
        Assert.Equal("Plan Fushimi Inari", (await Call(client, "list_versions"))["versions"]![0]!["message"]!.ToString());
        Assert.False((await Call(client, "get_trip"))["unsaved_changes"]!.GetValue<bool>());

        // Deleting the item keeps its place and reports it as unused; the item's note goes away with it.
        var deleted = await Call(client, "delete_entity", """{"id":"@itemId"}""".Replace("@itemId", itemId));
        Assert.Contains(deleted["unused"]!.AsArray(), e => e!["id"]!.ToString() == placeId);
        Assert.NotNull((await repo.ReadAsync()).Trip.Find(Guid.Parse(placeId)));
        await Call(client, "delete_entity", """{"id":"@placeId"}""".Replace("@placeId", placeId));
        var after = await repo.ReadAsync();
        Assert.Null(after.Trip.Find(Guid.Parse(itemId))); Assert.Null(after.Trip.Find(Guid.Parse(placeId))); Assert.DoesNotContain(after.Trip.Resources.Keys, k => k.EndsWith(".md", StringComparison.Ordinal));
        Assert.True((await Call(client, "validate"))["valid"]!.GetValue<bool>());
    }

    [Fact]
    public async Task StaysAreListedMovedAndMergedAsOneStay()
    {
        var (repo, _) = await Trip(); var client = await Connect(repo.Root);
        var ryokan = (await Call(client, "create_entity", """{"type":"place","data":{"name":"Ryokan"}}"""))["id"]!.ToString();
        string Night(string date) => "{\"type\":\"schedule_item\",\"data\":{\"title\":\"Ryokan\",\"status\":\"reserved\",\"time\":{\"precision\":\"day_part\",\"date\":\"" + date + "\",\"day_part\":\"night\",\"timezone\":\"Asia/Tokyo\"},\"components\":{\"accommodation\":{\"place\":\"" + ryokan + "\"}}}}";
        var first = (await Call(client, "create_entity", Night("2027-05-14")))["id"]!.ToString();
        var second = (await Call(client, "create_entity", Night("2027-05-15")))["id"]!.ToString();

        var stays = await Call(client, "list_stays");
        var suggestion = Assert.Single(stays["merge_suggestions"]!.AsArray())!;
        Assert.Equal(2, suggestion["nights"]!.GetValue<int>());
        var merged = await Call(client, "merge_stays", "{\"ids\":[\"" + first + "\",\"" + second + "\"]}");
        Assert.Equal(first, merged["kept"]!.ToString()); Assert.Equal(2, merged["stay"]!["nights"]!.GetValue<int>());
        Assert.Null((await repo.ReadAsync()).Trip.Find(Guid.Parse(second)));

        // Moving the check-out moves the stay's general time with it.
        await Call(client, "update_entity", "{\"id\":\"" + first + "\",\"changes\":{\"components\":{\"accommodation\":{\"check_out\":{\"local\":\"2027-05-17T10:00:00\",\"timezone\":\"Asia/Tokyo\"}}}}}");
        var details = await Call(client, "get_entity", "{\"id\":\"" + first + "\"}");
        Assert.Equal(3, details["stay"]!["nights"]!.GetValue<int>()); Assert.Empty(details["stay"]!["issues"]!.AsArray());
        Assert.Equal("2027-05-17T10:00:00", details["entity"]!["time"]!["end"]!["local"]!.ToString());
        Assert.True((await Call(client, "validate"))["valid"]!.GetValue<bool>());

        // Hours of the accommodation and a reminder for a task due today.
        await Call(client, "update_entity", "{\"id\":\"" + first + "\",\"changes\":{\"components\":{\"accommodation\":{\"check_in_window\":{\"from\":\"15:00\",\"until\":\"20:00\"},\"access\":\"reception\"}}}}");
        Assert.Equal("20:00", (await Call(client, "get_entity", "{\"id\":\"" + first + "\"}"))["stay"]!["check_in_hours"]!["until"]!.ToString());
        var today = NodaTime.SystemClock.Instance.GetCurrentInstant().InZone(NodaTime.DateTimeZoneProviders.Tzdb["Asia/Tokyo"]).Date.ToString("yyyy-MM-dd", null);
        await Call(client, "create_entity", "{\"type\":\"task\",\"data\":{\"title\":\"Pay the ryokan\",\"due\":{\"date\":\"" + today + "\"}}}");
        Assert.Contains((await Call(client, "list_reminders"))["reminders"]!.AsArray(), r => r!["kind"]!.ToString() == "TaskDue" && r["urgent"]!.GetValue<bool>());
    }

    [Fact]
    public async Task AConfirmationMailIsAttachedAndRead()
    {
        var (repo, _) = await Trip(); var client = await Connect(repo.Root);
        var booking = (await Call(client, "create_entity", """{"type":"booking","data":{"title":"Lakeview Lodge"}}"""))["id"]!.ToString();
        var file = Path.Combine(root, "confirmation.eml"); await File.WriteAllBytesAsync(file, DocumentTests.Confirmation());
        var imported = await Call(client, "import_document", JsonSerializer.Serialize(new { path = file, attach_to = booking, split_attachments = true }));
        var documents = imported["documents"]!.AsArray();
        Assert.Equal(["confirmation.eml", "ticket.pdf", "Earlier offer.eml"], documents.Select(d => d!["name"]!.ToString()).ToArray());
        var trip = (await repo.ReadAsync()).Trip;
        Assert.Equal(3, TripCommands.AttachedDocuments(trip, trip.Find(Guid.Parse(booking))!).Count);

        var mail = await Call(client, "read_document", JsonSerializer.Serialize(new { id = documents[0]!["id"]!.ToString() }));
        Assert.Equal("Your booking 4711 is confirmed", mail["mail"]!["subject"]!.ToString());
        Assert.Contains("Check-in from 15:00", mail["text"]!.ToString());
        var ticket = await Call(client, "read_document", JsonSerializer.Serialize(new { id = documents[1]!["id"]!.ToString() }));
        Assert.Contains("Booking 4711", ticket["text"]!.ToString());
        Assert.Contains("absolute path", await Call(client, "import_document", JsonSerializer.Serialize(new { path = "relative.eml" }), expectError: true));
        Assert.True((await Call(client, "validate"))["valid"]!.GetValue<bool>());
    }

    [Fact]
    public async Task EditsFromOtherProgramsAreSeenAndNotOverwritten()
    {
        var (repo, _) = await Trip(); var client = await Connect(repo.Root);
        var created = await Call(client, "create_entity", """{"type":"task","data":{"title":"Book the ryokan"}}""");
        var id = Guid.Parse(created["id"]!.ToString());
        // Jourfold (or a person with an editor) changes the same task between two assistant calls.
        var state = await repo.ReadAsync(); var task = state.Trip.Find(id)!.Copy(); task.Data["due"] = new JsonObject { ["date"] = "2027-04-01" };
        await repo.ApplyAsync(state, [new(id, task)]);
        await Call(client, "update_entity", """{"id":"@id","changes":{"status":"completed"}}""".Replace("@id", id.ToString()));
        var saved = (await repo.ReadAsync()).Trip.Find(id)!;
        Assert.Equal("completed", saved.Data["status"]!.ToString()); Assert.Equal("2027-04-01", saved.Data["due"]!["date"]!.ToString());
    }

    [Fact]
    public void SchemaProblemsAreExplainedByTheMostSpecificRule()
    {
        var item = Entity.Create("schedule_item", "Kinkaku-ji");
        item.Data["time"] = new JsonObject { ["precision"] = "exact", ["start"] = new JsonObject { ["local"] = "2027-05-15T09:00", ["timezone"] = "Asia/Tokyo" } };
        var problem = Assert.Single(TravelRepo.Serialization.SchemaValidation.Validate(item));
        Assert.Equal(["time.start.local: The string value is not a match for the indicated regular expression"], TravelRepo.Serialization.SchemaValidation.Explain(problem.Message));
    }

    [Fact]
    public async Task ReadOnlyServerOffersNoChanges()
    {
        var (repo, _) = await Trip(); var client = await Connect(repo.Root, readOnly: true);
        var tools = (await client.ListToolsAsync()).Select(t => t.Name).ToArray();
        Assert.Contains("get_trip", tools); Assert.Contains("compare", tools);
        Assert.DoesNotContain("create_entity", tools); Assert.DoesNotContain("create_version", tools); Assert.DoesNotContain("delete_entity", tools);
        Assert.Contains("read-only", client.ServerInstructions);
        Assert.Throws<DomainException>(() => TripServer.Options(root, new()));
    }

    [Fact]
    public async Task AssistantsGetOnlyCachedWeatherAndAreToldSo()
    {
        var (repo, _) = await Trip();
        var now = SystemClock.Instance.GetCurrentInstant(); var zone = DateTimeZoneProviders.Tzdb["Asia/Tokyo"]; var tomorrow = now.InZone(zone).Date.PlusDays(1);
        var inari = Entity.Create("place", "Fushimi Inari Taisha"); inari.Data["location"] = new JsonObject { ["latitude"] = 34.9671, ["longitude"] = 135.7727 };
        var walk = Entity.Create("schedule_item", "Fushimi Inari hike"); walk.Data["status"] = "planned"; walk.Data["category"] = "nature"; walk.Data["default_place"] = inari.Id.ToString();
        string At(int hour) => tomorrow.At(new LocalTime(hour, 0)).ToString("yyyy-MM-dd'T'HH:mm:ss", null);
        walk.Data["time"] = new JsonObject { ["precision"] = "exact", ["start"] = new ZonedTime(At(9), "Asia/Tokyo").ToJson(), ["end"] = new ZonedTime(At(12), "Asia/Tokyo").ToJson() };
        await repo.ApplyAsync(await repo.ReadAsync(), [new(inari.Id, inari), new(walk.Id, walk)]);

        // Weather off: the assistant learns that, and how it could change.
        var client = await Connect(repo.Root, weather: () => null);
        var off = await Call(client, "get_weather");
        Assert.False(off["available"]!.GetValue<bool>()); Assert.Contains("turned off in Jourfold", off["note"]!.ToString());
        Assert.Contains("never fetches live weather", client.ServerInstructions);

        // Weather on: what Jourfold cached three hours ago, with its age, and rain on the hike.
        var cacheDirectory = Path.Combine(root, "weather"); var cache = new WeatherCache(cacheDirectory); var fetched = now - Duration.FromHours(3);
        var start = zone.AtStartOfDay(tomorrow.PlusDays(-1)).ToInstant();
        cache.Store(new WeatherForecast("open-meteo", new GeoPoint(34.9671, 135.7727).Rounded(2), fetched, now + Duration.FromHours(1),
            Enumerable.Range(0, 72).Select(i => new WeatherHour(start + Duration.FromHours(i), 1, 21, 85, 2, 10, 25, WeatherCondition.Rain)).ToArray()));
        client = await Connect(repo.Root, weather: () => new CachedWeatherSource(cacheDirectory, "open-meteo"));
        var on = await Call(client, "get_weather");
        Assert.True(on["available"]!.GetValue<bool>()); Assert.Contains("not live data", on["note"]!.ToString());
        var item = on["items"]!.AsArray().Single()!;
        Assert.Equal("outdoor (estimated)", item["outdoor"]!.ToString());
        var outlook = item["outlooks"]![0]!;
        Assert.Equal("forecast", outlook["kind"]!.ToString()); Assert.Equal(3.0, outlook["age_hours"]!.GetValue<double>(), 1); Assert.Equal("Rain", outlook["condition"]!.ToString());
        Assert.Equal("rain", item["warnings"]![0]!["kind"]!.ToString());
        Assert.Equal(At(0)[..10], on["days"]!.AsArray().First()!["date"]!.ToString());
        Assert.Contains("Open-Meteo", on["attribution"]!.ToJsonString());

        // The cache-only service cannot download, whatever is asked of it.
        var snapshot = (await repo.ReadAsync()).Trip;
        await Assert.ThrowsAsync<InvalidOperationException>(() => WeatherService.CacheOnly(cache).UpdateAsync(snapshot));
    }
}
