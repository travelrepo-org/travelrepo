using System.Text;
using System.Text.Json.Nodes;
using TravelRepo.Core;
using TravelRepo.Git;
using TravelRepo.Merge;
using TravelRepo.Serialization;
using Xunit;
namespace TravelRepo.Tests;

public class ReleaseIntegrityTests
{
    [Fact]
    public void LargeExtensionNumbersAreNotRoundedOrConvertedToStrings()
    {
        var e = Entity.Create("person", "Alex"); e.Data["future"] = JsonNode.Parse("{\"huge\":1234567890123456789012345678901234567890,\"tiny\":1e-100,\"precise\":1.23456789012345678901234567890123456789}");
        var read = YamlCodec.Read(YamlCodec.Write(e)); Assert.Equal(e.Data["future"]!.ToJsonString(), read.Data["future"]!.ToJsonString());
    }
    [Fact]
    public void IndependentSequenceInsertionsPreserveBothChanges()
    {
        var manifest = Entity.CreateTrip("Trip");
        TripSnapshot S(string text) => new(manifest, new Dictionary<Guid, Entity>()) { Resources = new Dictionary<string, byte[]> { ["documents/notes.md"] = Encoding.UTF8.GetBytes(text) } };
        var plan = SemanticMerge.Plan(S("a\nb\nc"), S("first\na\nb\nc"), S("a\nb\nc\nlast")); Assert.True(plan.CanApply); Assert.Equal("first\na\nb\nc\nlast", Encoding.UTF8.GetString(plan.Result.Resources["documents/notes.md"]));
        Assert.False(SemanticMerge.Plan(S("a\nb"), S("a\nours\nb"), S("a\ntheirs\nb")).CanApply);
    }
    [Fact]
    public void OrderedChildrenMergeIndependentAdditions()
    {
        var m = Entity.CreateTrip("Trip"); var parent = Entity.Create("schedule_item", "Day"); var children = Enumerable.Range(0, 3).Select(i => Entity.Create("schedule_item", "Stop " + i)).ToArray();
        TripSnapshot S(params int[] order) { var p = parent.Copy(); p.Data["children"] = new JsonArray(order.Select(i => (JsonNode?)JsonValue.Create(children[i].Id.ToString())).ToArray()); return new(m, children.Append(p).ToDictionary(e => e.Id)); }
        var plan = SemanticMerge.Plan(S(1), S(0, 1), S(1, 2)); Assert.True(plan.CanApply); Assert.Equal(children.Select(e => e.Id.ToString()), ((JsonArray)plan.Result.Find(parent.Id)!.Data["children"]!).Select(n => n!.ToString()));
    }
    [Fact]
    public void StructurallyIndependentEditsCannotAutoMergeIntoDanglingReference()
    {
        var m = Entity.CreateTrip("Trip"); var person = Entity.Create("person", "Alex"); var activity = Entity.Create("schedule_item", "Walk");
        var baseline = new TripSnapshot(m, new[] { person, activity }.ToDictionary(e => e.Id)); var changed = activity.Copy(); changed.Data["participants"] = new JsonObject { ["inherit"] = false, ["values"] = new JsonArray(person.Id.ToString()) };
        var plan = SemanticMerge.Plan(baseline, new(m, new Dictionary<Guid, Entity> { [activity.Id] = activity }), new(m, new[] { person, changed }.ToDictionary(e => e.Id)));
        Assert.Empty(plan.Conflicts); Assert.False(plan.CanApply); Assert.Contains(plan.Diagnostics, d => d.Code == "reference.missing");
    }
    [Fact]
    public void SharingContextRoundTripsWithoutCredentials()
    {
        var link = new ShareLink("git@example.org:group/trip.git", "variants/museum first"); Assert.Equal(link, ShareLink.Parse(link.ToUri()));
        Assert.Throws<DomainException>(() => new ShareLink("https://token@example.org/trip", "main").ToUri()); Assert.Throws<DomainException>(() => new ShareLink("https://example.org/trip?token=private", "main").ToUri());
        Assert.True(GitCliBackend.IsCompatible(new GitCliBackend().Executable));
    }
    [Fact]
    public void GeneratedInstantRoundTripsAcrossTimezonesAndTransitions()
    {
        var random = new Random(2041); var epoch = NodaTime.Instant.FromUtc(2020, 1, 1, 0, 0);
        foreach (var zone in new[] { "Europe/Berlin", "Asia/Tokyo", "Pacific/Auckland", "America/New_York" })
            for (var i = 0; i < 250; i++) { var instant = epoch + NodaTime.Duration.FromSeconds(random.NextInt64(0, 20L * 365 * 86400)); Assert.Equal(instant, ZonedTime.At(instant, zone).ToInstant()); }
    }
    [Fact]
    public void CachedTravelGapWarnsWithoutBlockingUserPlan()
    {
        var m = Entity.CreateTrip("Trip"); var person = Entity.Create("person", "Alex"); var from = Entity.Create("place", "Museum"); var to = Entity.Create("place", "Station");
        Entity Item(string name, Entity place, string start, string end) { var e = Entity.Create("schedule_item", name); e.Data["default_place"] = place.Id.ToString(); e.Data["participants"] = new JsonObject { ["values"] = new JsonArray(person.Id.ToString()), ["inherit"] = false }; e.Data["time"] = new JsonObject { ["precision"] = "exact", ["start"] = new ZonedTime(start, "Europe/Berlin").ToJson(), ["end"] = new ZonedTime(end, "Europe/Berlin").ToJson() }; return e; }
        var first = Item("Museum", from, "2027-05-12T10:00:00", "2027-05-12T11:00:00"); var second = Item("Train", to, "2027-05-12T11:15:00", "2027-05-12T12:00:00");
        var trip = new TripSnapshot(m, new[] { person, from, to, first, second }.ToDictionary(e => e.Id));
        var warnings = SemanticValidation.Validate(trip, [new(from.Id, to.Id, NodaTime.Duration.FromMinutes(25))]); Assert.Contains(warnings, d => d.Code == "plan.travel_gap" && d.Severity == Severity.Warning); Assert.DoesNotContain(warnings, d => d.Severity == Severity.Error);
        Assert.DoesNotContain(SemanticValidation.Validate(trip), d => d.Code == "plan.travel_gap");
    }

}
