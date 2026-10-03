using System.Text;
using System.Text.Json.Nodes;
using NodaTime;
using TravelRepo.Core;
using TravelRepo.Git;
using TravelRepo.Merge;
using TravelRepo.Repository;
using TravelRepo.Serialization;
using Xunit;
namespace TravelRepo.Tests;

public sealed class SdkQueryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "travelrepo-sdk-" + Guid.NewGuid());
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    private static TripSnapshot Trip(params Entity[] entities)
    {
        var manifest = Entity.CreateTrip("Kyoto", "en", "Asia/Tokyo");
        return new(manifest, entities.ToDictionary(e => e.Id));
    }
    private static Entity Item(string title, JsonObject? time = null)
    {
        var item = Entity.Create("schedule_item", title); item.Data["time"] = time; return item;
    }
    private static JsonObject Exact(string start, string? end = null, string zone = "Asia/Tokyo") => new()
    {
        ["precision"] = "exact",
        ["start"] = new ZonedTime(start, zone).ToJson(),
        ["end"] = end is null ? null : new ZonedTime(end, zone).ToJson()
    };

    [Fact]
    public void WriterUsesPlainScalarsOnlyWhereEveryParserAgrees()
    {
        var entity = Entity.Create("place", "Café Zürich, Gion");
        entity.Data["extensions"]!["org.example"] = new JsonObject { ["yes"] = "yes", ["date"] = "2027-05-12", ["number"] = "0123", ["empty"] = "", ["colon"] = "a: b", ["hash"] = "a #b", ["spaces"] = "trailing " };
        var yaml = YamlCodec.Write(entity);
        Assert.Contains("name: Café Zürich, Gion\n", yaml);
        Assert.Contains("id: " + entity.Id + "\n", yaml);
        Assert.Contains("yes: \"yes\"", yaml);
        Assert.Contains("date: \"2027-05-12\"", yaml);
        Assert.Contains("number: \"0123\"", yaml);
        Assert.DoesNotContain("\n...", yaml);
        Assert.True(JsonNode.DeepEquals(entity.Data, YamlCodec.Read(yaml).Data));
    }

    [Fact]
    public void RandomStringsRoundTripThroughTheWriter()
    {
        var random = new Random(20261003); const string alphabet = "aZü東 -_.,:#'\"&/()+0123456789\\\ttrueyesnull~";
        for (var i = 0; i < 500; i++)
        {
            var text = new string(Enumerable.Range(0, random.Next(0, 24)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            var entity = Entity.Create("note", "x"); entity.Data["body"] = text;
            Assert.Equal(text, YamlCodec.Read(YamlCodec.Write(entity)).Data["body"]?.ToString() ?? "");
        }
    }

    [Fact]
    public void SpansCoverEveryPrecision()
    {
        var exact = Item("Temple", Exact("2027-05-14T09:00:00", "2027-05-14T11:30:00"));
        var open = Item("Lunch", Exact("2027-05-14T12:00:00")); open.Data["duration"] = "PT45M";
        var window = Item("Market", new JsonObject { ["precision"] = "window", ["earliest"] = new ZonedTime("2027-05-14T14:00:00", "Asia/Tokyo").ToJson(), ["latest"] = new ZonedTime("2027-05-14T18:00:00", "Asia/Tokyo").ToJson() });
        var dayPart = Item("Walk", new JsonObject { ["precision"] = "day_part", ["date"] = "2027-05-15", ["day_part"] = "evening", ["timezone"] = "Asia/Tokyo" });
        var allDay = Item("Nara", new JsonObject { ["precision"] = "all_day", ["date"] = "2027-05-16" });
        var idea = Item("Onsen");
        var trip = Trip(exact, open, window, dayPart, allDay, idea);
        Assert.Equal(Duration.FromMinutes(150), ScheduleQueries.Span(trip, exact).Length);
        Assert.Equal(Duration.FromMinutes(45), ScheduleQueries.Span(trip, open).Length);
        Assert.Equal(TimePrecision.Window, ScheduleQueries.Span(trip, window).Precision);
        var evening = ScheduleQueries.Span(trip, dayPart);
        Assert.Equal(new LocalTime(17, 0), evening.Start!.Value.InZone(DateTimeZoneProviders.Tzdb["Asia/Tokyo"]).TimeOfDay);
        Assert.Equal(Duration.FromHours(24), ScheduleQueries.Span(trip, allDay).Length);
        Assert.False(ScheduleQueries.Span(trip, idea).IsPlaced);
        Assert.True(ScheduleQueries.IsUnscheduled(trip, idea));
        Assert.False(ScheduleQueries.IsUnscheduled(trip, exact));
    }

    [Fact]
    public void UnscheduledParentsAggregateTheirChildren()
    {
        var child = Item("Day one", Exact("2027-05-14T09:00:00", "2027-05-14T17:00:00"));
        var parent = Item("Kyoto days"); parent.Data["children"] = new JsonArray(child.Id.ToString());
        var span = ScheduleQueries.Span(Trip(parent, child), parent);
        Assert.True(span.Derived); Assert.Equal(Duration.FromHours(8), span.Length);
    }

    [Theory]
    [InlineData(null, ScheduleKind.Activity)]
    [InlineData("food", ScheduleKind.Food)]
    [InlineData("Restaurant", ScheduleKind.Food)]
    [InlineData("museum", ScheduleKind.Culture)]
    [InlineData("com.example.custom", ScheduleKind.Other)]
    public void CategoriesClassifyItems(string? category, ScheduleKind expected)
    {
        var item = Item("Thing"); if (category is not null) item.Data["category"] = category;
        Assert.Equal(expected, ScheduleCategories.Classify(Trip(item), item));
    }

    [Fact]
    public void ComponentsWinOverCategoryAndCategoriesInherit()
    {
        var flight = Item("Flight"); flight.Data["category"] = "food"; flight.Data["components"]!["transport"] = new JsonObject { ["type"] = "flight" };
        var child = Item("Ramen"); var parent = Item("Food tour"); parent.Data["category"] = "food"; parent.Data["children"] = new JsonArray(child.Id.ToString());
        var trip = Trip(flight, parent, child);
        Assert.Equal(ScheduleKind.Transport, ScheduleCategories.Classify(trip, flight));
        Assert.Equal("flight", ScheduleCategories.TransportType(flight));
        Assert.Equal(ScheduleKind.Food, ScheduleCategories.Classify(trip, child));
    }

    [Fact]
    public void PlacesParticipantsAndCostsSummarizeCanonicalData()
    {
        var alex = Entity.Create("person", "Alex"); var station = Entity.Create("place", "Kyoto Station"); var hotel = Entity.Create("place", "Ryokan");
        var train = Item("Train", Exact("2027-05-14T09:00:00", "2027-05-14T11:00:00")); train.Data["components"]!["transport"] = new JsonObject { ["type"] = "train", ["arrival"] = new JsonObject { ["place"] = station.Id.ToString() } };
        train.Data["participants"] = new JsonObject { ["inherit"] = false, ["values"] = new JsonArray(alex.Id.ToString()) };
        var stay = Item("Stay", Exact("2027-05-14T15:00:00", "2027-05-15T10:00:00")); stay.Data["components"]!["accommodation"] = new JsonObject { ["place"] = hotel.Id.ToString() };
        Entity Expense(string value, string currency, bool estimated, string type = "expense") { var e = Entity.Create(type, "Cost"); e.Data["amount"] = new JsonObject { ["value"] = value, ["currency"] = currency }; if (type == "expense") e.Data["estimated"] = estimated; return e; }
        var trip = Trip(alex, station, hotel, train, stay, Expense("100.50", "EUR", false), Expense("20", "EUR", true), Expense("12000", "JPY", false), Expense("500", "EUR", false, "budget"));
        Assert.Equal(["Ryokan", "Kyoto Station"], TripSummaries.PlaceNames(trip));
        Assert.Equal("Alex", Assert.Single(ScheduleQueries.Participants(trip, train)).Title);
        Assert.Equal(hotel.Id, ScheduleQueries.PrimaryPlace(trip, stay)!.Id);
        var eur = TripSummaries.Costs(trip).Single(c => c.Currency == "EUR");
        Assert.Equal(100.50m, eur.Paid); Assert.Equal(20m, eur.Estimated); Assert.Equal(500m, eur.Budget);
        Assert.Contains(TripSummaries.Costs(trip), c => c.Currency == "JPY" && c.Paid == 12000m);
        Assert.Contains(TripSummaries.ReferencesTo(trip, station.Id), e => e.Id == train.Id);
    }

    [Fact]
    public void ChangeSummaryAttributesMarkdownToItsEntity()
    {
        var note = Entity.Create("note", "Packing"); note.Data["content"] = new JsonArray(new JsonObject { ["type"] = "markdown", ["file"] = "documents/packing.md" });
        var temple = Item("Temple"); var removed = Entity.Create("person", "Sam");
        var before = Trip(note, temple, removed) with { Resources = new Dictionary<string, byte[]> { ["documents/packing.md"] = Encoding.UTF8.GetBytes("- socks") } };
        var changedTemple = temple.Copy(); changedTemple.Data["status"] = "planned"; changedTemple.Data["components"]!["booking"] = new JsonObject { ["ref"] = Guid.CreateVersion7().ToString() };
        var added = Entity.Create("task", "Book train");
        var after = new TripSnapshot(before.Manifest, new Dictionary<Guid, Entity> { [note.Id] = note, [temple.Id] = changedTemple, [added.Id] = added }) { Resources = new Dictionary<string, byte[]> { ["documents/packing.md"] = Encoding.UTF8.GetBytes("- socks\n- adapter") } };
        var summary = ChangeSummary.Between(before, after);
        Assert.Equal(4, summary.Count);
        Assert.Contains(summary.Entities, c => c.Entity == added.Id && c.Kind == ChangeKind.Added);
        Assert.Contains(summary.Entities, c => c.Entity == removed.Id && c.Kind == ChangeKind.Removed);
        Assert.Equal(["components/booking", "status"], summary.Entities.Single(c => c.Entity == temple.Id).Fields);
        Assert.Equal(["content"], summary.Entities.Single(c => c.Entity == note.Id).Fields.Take(1));
        Assert.Empty(summary.Resources);
    }

    [Fact]
    public async Task NormalizeRewritesFormattingWithoutChangingData()
    {
        var repo = new TravelRepository(Path.Combine(root, "trip"), Path.Combine(root, "recovery"));
        await repo.InitializeAsync(Entity.CreateTrip("Format"));
        var place = Entity.Create("place", "Lake"); await repo.ApplyAsync(await repo.ReadAsync(), [new(place.Id, place)]);
        var path = (await repo.ReadAsync()).Files[place.Id].Path;
        await File.WriteAllTextAsync(repo.SafePath(path), place.Data.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        var before = await repo.ReadAsync();
        Assert.Equal(1, await repo.NormalizeAsync());
        var after = await repo.ReadAsync();
        Assert.Empty(SemanticMerge.Diff(before.Trip, after.Trip));
        Assert.Equal(0, await repo.NormalizeAsync());
    }

    [Fact]
    public void RecoveryRootIsAlwaysAbsolute()
    {
        Assert.True(Path.IsPathFullyQualified(TravelRepository.DefaultStateRoot()));
        Assert.True(Path.IsPathFullyQualified(new TravelRepository(Path.Combine(root, "x")).RecoveryRoot));
    }

    [Fact]
    public async Task SyncStatusCountsAheadAndBehindWithoutFetching()
    {
        var remotePath = Path.Combine(root, "remote.git"); Directory.CreateDirectory(remotePath);
        var backend = new GitCliBackend(); await backend.ExecuteAsync(remotePath, ["init", "--bare", "-b", "main"]);
        var repo = new TravelRepository(Path.Combine(root, "local"), Path.Combine(root, "recovery-local")); await repo.InitializeAsync(Entity.CreateTrip("Sync"));
        var git = new GitRepository(repo, backend); await git.InitializeAsync("Alex", "alex@example.invalid");
        Assert.Equal(SyncState.LocalOnly, (await git.SyncStatusAsync(null)).State);
        await git.AddRemoteAsync("origin", remotePath);
        Assert.Equal(SyncState.LocalChanges, (await git.SyncStatusAsync("origin")).State);
        await git.PushAsync("origin"); await git.FetchAsync("origin");
        Assert.Equal(SyncState.Synced, (await git.SyncStatusAsync("origin")).State);
        var task = Entity.Create("task", "Pack"); await repo.ApplyAsync(await repo.ReadAsync(), [new(task.Id, task)]);
        Assert.True((await git.SyncStatusAsync("origin")).HasLocalChanges);
        await git.CreateVersionAsync("Add task");
        var status = await git.SyncStatusAsync("origin");
        Assert.Equal((1, 0), (status.Ahead, status.Behind)); Assert.Equal(SyncState.LocalChanges, status.State);
    }

    [Fact]
    public void StaysDoNotProduceOverlapWarnings()
    {
        var alex = Entity.Create("person", "Alex"); JsonObject With() => new() { ["inherit"] = false, ["values"] = new JsonArray(alex.Id.ToString()) };
        var stay = Item("Hotel", Exact("2027-05-14T15:00:00", "2027-05-16T11:00:00")); stay.Data["components"]!["accommodation"] = new JsonObject(); stay.Data["participants"] = With();
        var dinner = Item("Dinner", Exact("2027-05-14T19:00:00", "2027-05-14T21:00:00")); dinner.Data["participants"] = With();
        var bar = Item("Bar", Exact("2027-05-14T20:00:00", "2027-05-14T22:00:00")); bar.Data["participants"] = With();
        var warnings = SemanticValidation.Validate(Trip(alex, stay, dinner, bar)).Where(d => d.Code == "plan.overlap").ToArray();
        Assert.Single(warnings);
        Assert.DoesNotContain(warnings, w => w.Path == stay.Id.ToString());
    }
}
