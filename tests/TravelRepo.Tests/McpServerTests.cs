using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TravelRepo.Core;
using TravelRepo.Git;
using TravelRepo.Mcp;
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
    private async Task<McpClient> Connect(string path, bool readOnly = false)
    {
        var toServer = new Pipe(); var toClient = new Pipe();
        var server = TripServer.Create(new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream()), path, new(ReadOnly: readOnly, RecoveryRoot: Path.Combine(root, "recovery")));
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

        // A place still used by an item is not deleted; the item and its note go away cleanly.
        Assert.Contains("Fushimi Inari", await Call(client, "delete_entity", """{"id":"@placeId"}""".Replace("@placeId", placeId), expectError: true));
        var changes = await Call(client, "compare", """{"revision":"HEAD"}""");
        Assert.Contains(changes["changes"]!.AsArray(), c => c!["title"]!.ToString() == "Fushimi Inari" && c["change"]!.ToString() == "added");
        await Call(client, "create_version", """{"message":"Plan Fushimi Inari"}""");
        Assert.Equal("Plan Fushimi Inari", (await Call(client, "list_versions"))["versions"]![0]!["message"]!.ToString());
        Assert.False((await Call(client, "get_trip"))["unsaved_changes"]!.GetValue<bool>());

        await Call(client, "delete_entity", """{"id":"@itemId"}""".Replace("@itemId", itemId)); await Call(client, "delete_entity", """{"id":"@placeId"}""".Replace("@placeId", placeId));
        var after = await repo.ReadAsync();
        Assert.Null(after.Trip.Find(Guid.Parse(itemId))); Assert.Null(after.Trip.Find(Guid.Parse(placeId))); Assert.DoesNotContain(after.Trip.Resources.Keys, k => k.EndsWith(".md", StringComparison.Ordinal));
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
}
