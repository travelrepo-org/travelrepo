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
        var git = new GitRepository(repo, TestGit.Backend()); await git.InitializeAsync("Test", "test@example.invalid");
        return (repo, git);
    }

    /// <summary>A real MCP client talking to the server through in-memory pipes, as an assistant would over stdio.</summary>
    private async Task<McpClient> Connect(string path, bool readOnly = false, Func<CachedWeatherSource?>? weather = null, Func<string?>? rates = null)
    {
        var toServer = new Pipe(); var toClient = new Pipe();
        var server = TripServer.Create(new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream()), path, new(ReadOnly: readOnly, ClientName: "Jourfold", RecoveryRoot: Path.Combine(root, "recovery"), Weather: weather, ExchangeRates: rates));
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

    [Fact]
    public async Task AssistantsKeepPackingListsWithPeople()
    {
        var (repo, _) = await Trip(); var client = await Connect(repo.Root);
        var ana = Entity.Create("person", "Ana"); var ben = Entity.Create("person", "Ben");
        await repo.ApplyAsync(await repo.ReadAsync(), [new(ana.Id, ana), new(ben.Id, ben)]);
        var list = (await Call(client, "create_packing_list", """{"title":"Barbecue"}"""))["id"]!.ToString();
        var grill = (await Call(client, "add_packing_item", "{\"list\":\"" + list + "\",\"title\":\"Grill\",\"group\":\"Cooking\",\"people\":[\"ana\"]}"))["id"]!.ToString();
        var drinks = await Call(client, "add_packing_item", "{\"list\":\"" + list + "\",\"title\":\"Drinks\",\"everyone\":true}");
        Assert.Equal("everyone", drinks["brought_by"]!.ToString());
        var plates = (await Call(client, "add_packing_item", "{\"list\":\"" + list + "\",\"title\":\"Plates\"}"))["id"]!.ToString();
        var lists = await Call(client, "get_packing_lists");
        Assert.Equal(["Plates"], lists["lists"]![0]!["unassigned"]!.AsArray().Select(n => n!.ToString()));

        Assert.Equal("Ben", (await Call(client, "assign_packing_item", "{\"item\":\"" + plates + "\",\"people\":[\"Ben\"]}"))["brought_by"]!.ToString());
        var packed = await Call(client, "set_packed", "{\"item\":\"" + grill + "\",\"person\":\"Ana\"}");
        Assert.True(packed["done"]!.GetValue<bool>());
        var mine = await Call(client, "get_packing_lists", """{"person":"Ben"}""");
        Assert.Equal(["Drinks", "Plates"], mine["lists"]![0]!["items"]!.AsArray().Select(n => n!["title"]!.ToString()));
        var text = (await Call(client, "export_text", """{"kind":"packing","person":"Ben"}"""))["text"]!.ToString();
        Assert.Contains("\u2610 Plates", text); Assert.DoesNotContain("Grill", text);
        Assert.Contains("Use dates like", await Call(client, "export_text", """{"from":"next week"}""", expectError: true));
        Assert.Contains("Nobody in the trip", await Call(client, "set_packed", "{\"item\":\"" + grill + "\",\"person\":\"Zoe\"}", expectError: true));

        // A second list: items are changed, moved there, the first list renamed and then deleted with what is left on it.
        var camping = (await Call(client, "create_packing_list", """{"title":"Camping"}"""))["id"]!.ToString();
        var tent = (await Call(client, "add_packing_item", "{\"list\":\"" + camping + "\",\"title\":\"Tent\",\"people\":[\"Ben\"]}"))["id"]!.ToString();
        var changed = await Call(client, "update_packing_item", "{\"item\":\"" + grill + "\",\"title\":\"Small grill\",\"quantity\":\"1\",\"group\":\"\",\"list\":\"" + camping + "\"}");
        Assert.Equal(("Small grill", "1"), (changed["title"]!.ToString(), changed["quantity"]!.ToString())); Assert.Null(changed["group"]);
        Assert.Equal("Picnic", (await Call(client, "rename_packing_list", "{\"list\":\"" + list + "\",\"title\":\"Picnic\"}"))["title"]!.ToString());
        Assert.Equal(2, (await Call(client, "delete_packing_list", "{\"list\":\"" + list + "\"}"))["items_deleted"]!.GetValue<int>());
        await Call(client, "delete_entity", "{\"id\":\"" + tent + "\"}");
        var left = Assert.Single((await Call(client, "get_packing_lists"))["lists"]!.AsArray());
        Assert.Equal(("Camping", "Small grill"), (left!["title"]!.ToString(), Assert.Single(left["items"]!.AsArray())!["title"]!.ToString()));
    }

    private static Entity Journey(string title, Entity from, Entity to, string start, string startZone, string end, string endZone)
    {
        var item = Entity.Create("schedule_item", title); item.Data["status"] = "confirmed";
        item.Data["time"] = new JsonObject { ["precision"] = "exact", ["start"] = new ZonedTime(start, startZone).ToJson(), ["end"] = new ZonedTime(end, endZone).ToJson() };
        item.Data["components"] = new JsonObject { ["transport"] = new JsonObject { ["type"] = "flight", ["departure"] = new JsonObject { ["place"] = from.Id.ToString() }, ["arrival"] = new JsonObject { ["place"] = to.Id.ToString() } } };
        return item;
    }

    [Fact]
    public async Task AssistantsLinkBookingsGroupPlansAndSeeLayovers()
    {
        var (repo, _) = await Trip(); var client = await Connect(repo.Root);
        Entity Place(string name, double lat, double lon) { var p = Entity.Create("place", name); p.Data["location"] = new JsonObject { ["latitude"] = lat, ["longitude"] = lon }; return p; }
        var home = Place("Home airport", 50.03, 8.57); var hub = Place("Hub airport", 37.46, 126.44); var far = Place("Destination airport", 34.43, 135.23);
        var first = Journey("First flight", home, hub, "2027-03-01T13:00:00", "Europe/Berlin", "2027-03-02T07:00:00", "Asia/Seoul");
        var second = Journey("Second flight", hub, far, "2027-03-02T09:30:00", "Asia/Seoul", "2027-03-02T11:30:00", "Asia/Tokyo");
        var tour = Entity.Create("schedule_item", "Day tour"); var temple = Entity.Create("schedule_item", "Temple");
        var booking = Entity.Create("booking", "Flights");
        await repo.ApplyAsync(await repo.ReadAsync(), new[] { home, hub, far, first, second, tour, temple, booking }.Select(e => new EntityEdit(e.Id, e)).ToArray());

        var connection = (await Call(client, "list_connections"))["connections"]![0]!;
        Assert.Equal(("First flight", "Second flight", "Hub airport", 150L), (connection["arriving"]!["title"]!.ToString(), connection["departing"]!["title"]!.ToString(), connection["place"]!.ToString(), connection["layover_minutes"]!.GetValue<long>()));
        Assert.Equal("2 h 30 min", connection["layover"]!.ToString());

        var linked = await Call(client, "link_booking", "{\"booking\":\"" + booking.Id + "\",\"items\":[\"" + first.Id + "\",\"" + second.Id + "\"]}");
        Assert.Equal(["First flight", "Second flight"], linked["plans"]!.AsArray().Select(n => n!["title"]!.ToString()).Order());
        Assert.Equal(booking.Id, TripCommands.BookingOf((await repo.ReadAsync()).Trip.Find(second.Id)!));
        Assert.Contains("not a plan", await Call(client, "link_booking", "{\"booking\":\"" + booking.Id + "\",\"items\":[\"" + hub.Id + "\"]}", expectError: true));
        Assert.Equal("Flights", (await Call(client, "get_bookings"))["bookings"]![0]!["title"]!.ToString());

        var group = await Call(client, "add_to_group", "{\"group\":\"" + tour.Id + "\",\"item\":\"" + temple.Id + "\"}");
        Assert.Equal("Temple", group["steps"]![0]!["title"]!.ToString());
        Assert.Equal("Temple", (await Call(client, "get_entity", "{\"id\":\"" + tour.Id + "\"}"))["steps"]![0]!["title"]!.ToString());
        Assert.Contains("nest.cycle", await Call(client, "add_to_group", "{\"group\":\"" + temple.Id + "\",\"item\":\"" + tour.Id + "\"}", expectError: true));
        Assert.Empty((await Call(client, "remove_from_group", "{\"item\":\"" + temple.Id + "\"}"))["steps"]!.AsArray());
        Assert.DoesNotContain((await repo.ReadAsync()).Diagnostics, d => d.Severity == Severity.Error);
    }

    [Fact]
    public async Task AssistantsAddExpensesAsTypedAndConvertOnlyWithCachedRates()
    {
        var (repo, _) = await Trip();
        var ratesFile = Path.Combine(root, "rates.json"); string? rates = null;
        var client = await Connect(repo.Root, rates: () => rates);
        var ana = Entity.Create("person", "Ana"); var ferry = Entity.Create("schedule_item", "Ferry");
        await repo.ApplyAsync(await repo.ReadAsync(), [new(ana.Id, ana), new(ferry.Id, ferry)]);

        var added = await Call(client, "add_expense", "{\"title\":\"Ferry tickets\",\"amount\":\"1.234,50\",\"currency\":\"nzd\",\"paid_by\":[\"Ana\"],\"belongs_to\":\"" + ferry.Id + "\"}");
        Assert.Equal(("1234.50", "NZD"), (added["entity"]!["amount"]!["value"]!.ToString(), added["entity"]!["amount"]!["currency"]!.ToString()));
        Assert.Contains("three letters", await Call(client, "add_expense", """{"title":"Lunch","amount":"12","currency":"euro"}""", expectError: true));
        Assert.Contains("number", await Call(client, "add_expense", """{"title":"Lunch","amount":"twelve","currency":"EUR"}""", expectError: true));

        // Rates off in the app: nothing is converted, and the assistant is told why.
        Assert.False((await Call(client, "convert_currency", """{"amount":"10","from":"EUR","to":"NZD"}"""))["available"]!.GetValue<bool>());
        new ExchangeRates(new DateOnly(2027, 3, 1), new Dictionary<string, decimal> { ["NZD"] = 1.8m, ["JPY"] = 160m }).Save(ratesFile); rates = ratesFile;
        var converted = await Call(client, "convert_currency", """{"amount":"18","from":"NZD","to":"JPY"}""");
        Assert.Equal(1600m, converted["result"]!.GetValue<decimal>());
        Assert.Equal("2027-03-01", converted["rates"]!["date"]!.ToString());
        var costs = await Call(client, "get_costs", """{"in":"EUR"}""");
        Assert.Equal(("NZD", 685.83m), (costs["totals"]![0]!["currency"]!.ToString(), costs["totals"]![0]!["total_in"]!.GetValue<decimal>()));
        Assert.Equal(["Ana"], costs["expenses"]![0]!["paid_by"]!.AsArray().Select(n => n!.ToString()));
        Assert.Equal(["Ferry"], costs["expenses"]![0]!["belongs_to"]!.AsArray().Select(n => n!.ToString()));
    }
    [Fact]
    public async Task AssistantsDivideExpensesAndMarkThemPaid()
    {
        var (repo, _) = await Trip();
        var client = await Connect(repo.Root);
        var ana = Entity.Create("person", "Ana"); var ben = Entity.Create("person", "Ben");
        await repo.ApplyAsync(await repo.ReadAsync(), [new(ana.Id, ana), new(ben.Id, ben)]);
        var id = (await Call(client, "add_expense", """{"title":"Boat hire","amount":"60","currency":"EUR","paid_by":["Ana"]}"""))["id"]!.ToString();

        // Equally among everyone by default.
        var costs = await Call(client, "get_costs", "{}");
        Assert.Equal("equal", costs["expenses"]![0]!["split"]!["method"]!.ToString());
        Assert.Equal(30m, costs["expenses"]![0]!["split"]!["open"]!.GetValue<decimal>());
        Assert.Equal(("Ben", "Ana", 30m), (costs["settle_up"]![0]!["from"]!.ToString(), costs["settle_up"]![0]!["to"]!.ToString(), costs["settle_up"]![0]!["amount"]!.GetValue<decimal>()));

        var split = await Call(client, "split_expense", "{\"expense\":\"" + id + "\",\"method\":\"amount\",\"people\":[\"Ana\",\"Ben\"],\"values\":[\"20\",\"40,00\"]}");
        Assert.Equal(40m, split["open"]!.GetValue<decimal>());
        Assert.Contains("add up", await Call(client, "split_expense", "{\"expense\":\"" + id + "\",\"method\":\"percent\",\"people\":[\"Ana\",\"Ben\"],\"values\":[\"50\",\"40\"]}", expectError: true));

        var paid = await Call(client, "mark_paid", "{\"expense\":\"" + id + "\",\"person\":\"Ben\",\"date\":\"2027-05-03\"}");
        Assert.Equal(0m, paid["open"]!.GetValue<decimal>());
        Assert.Equal("2027-05-03", paid["debts"]![0]!["paid_on"]!.ToString());
        Assert.Empty((await Call(client, "get_costs", "{}"))["settle_up"]!.AsArray());
        Assert.Equal(40m, (await Call(client, "mark_paid", "{\"expense\":\"" + id + "\",\"paid\":false}"))["open"]!.GetValue<decimal>());

        // The card charged 33,60 EUR for 60 NZD. Ben owes 40 EUR and half the jet boat, 16,80 EUR, in one payment.
        var nzd = (await Call(client, "add_expense", """{"title":"Jet boat","amount":"60","currency":"NZD","paid_by":["Ana"]}"""))["id"]!.ToString();
        Assert.Equal("0.56", (await Call(client, "set_charged", "{\"expense\":\"" + nzd + "\",\"amount\":\"33,60\",\"currency\":\"eur\",\"date\":\"2027-05-03\"}"))["converted"]!["rate"]!.ToString());
        var inEuros = await Call(client, "get_costs", """{"in":"EUR"}""");
        Assert.Contains(inEuros["settle_up"]!.AsArray(), t => t!["currency"]!.ToString() == "EUR" && t["amount"]!.GetValue<decimal>() == 56.80m && t["estimated"] is null);
        Assert.Equal(33.60m + 60m, inEuros["paid_in"]!["exact"]!.GetValue<decimal>());
        Assert.DoesNotContain((await repo.ReadAsync()).Diagnostics, d => d.Severity == Severity.Error);
    }

    [Fact]
    public async Task AssistantsRecordBookingPaymentsBudgetsAndTheTripCurrency()
    {
        var (repo, _) = await Trip();
        var client = await Connect(repo.Root);
        var ana = Entity.Create("person", "Ana"); var ben = Entity.Create("person", "Ben"); var cleo = Entity.Create("person", "Cleo");
        var state = await repo.ReadAsync();
        var manifest = state.Trip.Manifest.Copy(); manifest.Data["participants"] = new JsonArray(ana.Id.ToString(), ben.Id.ToString(), cleo.Id.ToString());
        await repo.ApplyAsync(state, [new(ana.Id, ana), new(ben.Id, ben), new(cleo.Id, cleo), new(manifest.Id, manifest)]);
        Assert.Equal("EUR", (await Call(client, "set_trip_currency", """{"currency":"eur"}"""))["currency"]!.ToString());

        // A booking created with a price gets a payment still to pay instead, shared by its travellers.
        var booking = (await Call(client, "create_entity", "{\"type\":\"booking\",\"data\":{\"title\":\"Lodge\",\"status\":\"confirmed\",\"travelers\":[\"" + ana.Id + "\",\"" + ben.Id + "\"],\"price\":{\"value\":\"300.00\",\"currency\":\"EUR\"}}}"))["id"]!.ToString();
        var bookings = await Call(client, "get_bookings", "{}");
        var lodge = bookings["bookings"]![0]!;
        Assert.Null(lodge["legacy_price"]);
        Assert.Equal(300m, lodge["to_pay"]!["amount"]!.GetValue<decimal>());
        var payment = lodge["payments"]![0]!["id"]!.ToString();
        var paid = await Call(client, "record_payment", "{\"expense\":\"" + payment + "\",\"paid_by\":[\"Ana\"],\"amount\":\"310\"}");
        Assert.False(paid["entity"]!["estimated"]!.GetValue<bool>());
        var costs = await Call(client, "get_costs", "{}");
        Assert.Equal("EUR", costs["currency"]!.ToString());
        Assert.Equal(("Ben", "Ana", 155m), (costs["settle_up"]![0]!["from"]!.ToString(), costs["settle_up"]![0]!["to"]!.ToString(), costs["settle_up"]![0]!["amount"]!.GetValue<decimal>()));

        // A new traveller does not join what was shared before.
        var dinner = (await Call(client, "add_expense", """{"title":"Dinner","amount":"90","currency":"EUR","paid_by":["Cleo"]}"""))["id"]!.ToString();
        await Call(client, "create_entity", """{"type":"person","data":{"display_name":"Dana"}}""");
        costs = await Call(client, "get_costs", "{}");
        Assert.Equal(3, costs["expenses"]!.AsArray().Single(e => e!["id"]!.ToString() == dinner)!["split"]!["parts"]!.AsArray().Count);

        // One trip budget and one for Ben's food, both in the trip's currency.
        await Call(client, "set_budget", """{"title":"Trip","amount":"2000"}""");
        await Call(client, "set_budget", """{"title":"Ben's food","amount":"100","category":"food","person":"Ben"}""");
        costs = await Call(client, "get_costs", "{}");
        Assert.Equal((2000m, 400m), (costs["budget"]!["amount"]!.GetValue<decimal>(), costs["budget"]!["spent"]!.GetValue<decimal>()));
        Assert.Equal("Ben's food", costs["budgets"]![0]!["title"]!.ToString());
        Assert.Empty(costs["needs_attention"]!.AsArray());
        Assert.Equal(booking, bookings["bookings"]![0]!["id"]!.ToString());
        Assert.DoesNotContain((await repo.ReadAsync()).Diagnostics, d => d.Severity == Severity.Error);
    }
}
