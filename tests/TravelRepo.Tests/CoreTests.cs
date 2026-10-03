using System.Text.Json.Nodes;
using TravelRepo.Core;
using TravelRepo.Serialization;
using TravelRepo.Merge;
using Xunit;
namespace TravelRepo.Tests;

public class CoreTests
{
    [Fact]
    public void TokyoFlightHasCorrectElapsedTime()
    { var start = new ZonedTime("2027-05-12T13:20:00", "Europe/Berlin").ToInstant(); var end = new ZonedTime("2027-05-13T08:35:00", "Asia/Tokyo").ToInstant(); Assert.Equal(12.25, (end - start).TotalHours); }
    [Theory]
    [InlineData("2027-03-28T02:30:00", null, "time.gap")]
    [InlineData("2027-10-31T02:30:00", null, "time.ambiguous")]
    [InlineData("2027-10-31T02:30:00", "+03:00", "time.offset")]
    public void DstRejectsInvalidTime(string local, string? offset, string code) => Assert.Equal(code, Assert.Throws<DomainException>(() => new ZonedTime(local, "Europe/Berlin", offset).ToInstant()).Code);
    [Fact] public void DstDisambiguatorSelectsDifferentInstants() => Assert.Equal(1, (new ZonedTime("2027-10-31T02:30:00", "Europe/Berlin", "+01:00").ToInstant() - new ZonedTime("2027-10-31T02:30:00", "Europe/Berlin", "+02:00").ToInstant()).TotalHours);
    [Fact]
    public void GeneratedRoundTripsPreserveUnknownData()
    {
        var random = new Random(7491);
        for (var i = 0; i < 250; i++)
        {
            var entity = Entity.Create("person", "name \" : \n ü " + random.Next());
            entity.Data["extensions"]!["org.example.test"] = new JsonObject { ["nested"] = new JsonArray(random.Next(), "001", "false", null, false, 1.23m) };
            entity.Data["future"] = new JsonObject { ["data"] = "0123" };
            var read = YamlCodec.Read(YamlCodec.Write(entity)); Assert.True(JsonNode.DeepEquals(entity.Data, read.Data)); Assert.Empty(SchemaValidation.Validate(read));
        }
    }
    [Theory]
    [InlineData("a: 1\na: 2")]
    [InlineData("a: &x 1\nb: *x")]
    [InlineData("a: !unsafe 1")]
    public void UnsafeYamlIsRejected(string yaml) => Assert.ThrowsAny<Exception>(() => YamlCodec.Read(yaml));
    [Fact]
    public void MergeIndependentFieldsAndUnknownExtensions()
    {
        var m = Entity.CreateTrip("Trip"); var e = Entity.Create("person", "Alex"); var b = new TripSnapshot(m, new Dictionary<Guid, Entity> { [e.Id] = e }); var o = e.Copy(); var t = e.Copy();
        o.Data["display_name"] = "Sam"; t.Data["extensions"]!["org.example.data"] = 7;
        var plan = SemanticMerge.Plan(b, new(m, new Dictionary<Guid, Entity> { [e.Id] = o }), new(m, new Dictionary<Guid, Entity> { [e.Id] = t }));
        Assert.True(plan.CanApply); Assert.Equal("Sam", plan.Result.Entities[e.Id].Title); Assert.Equal(7, (int)plan.Result.Entities[e.Id].Data["extensions"]!["org.example.data"]!);
    }
    [Fact]
    public void ConflictingDeleteIsNeverSilentlyApplied()
    {
        var m = Entity.CreateTrip("Trip"); var e = Entity.Create("person", "Alex"); var b = new TripSnapshot(m, new Dictionary<Guid, Entity> { [e.Id] = e }); var t = e.Copy(); t.Data["display_name"] = "Changed";
        var plan = SemanticMerge.Plan(b, new(m, new Dictionary<Guid, Entity>()), new(m, new Dictionary<Guid, Entity> { [e.Id] = t }));
        Assert.Equal(ConflictKind.DeleteEdit, Assert.Single(plan.Conflicts).Kind); Assert.Throws<DomainException>(() => plan.EditsAgainst(b));
    }
    [Fact]
    public void GeneratedMergeIdentityInvariants()
    {
        for (var i = 0; i < 100; i++) { var m = Entity.CreateTrip("Trip " + i); var b = new TripSnapshot(m, new Dictionary<Guid, Entity>()); var changed = m.Copy(); changed.Data["future"] = i; var t = new TripSnapshot(changed, b.Entities); Assert.True(JsonNode.DeepEquals(t.Manifest.Data, SemanticMerge.Plan(b, b, t).Result.Manifest.Data)); Assert.True(SemanticMerge.Plan(b, t, t).CanApply); }
    }
    [Fact]
    public void MergeAlwaysRetainsDestinationVariantIdentity()
    {
        var baseline = Entity.CreateTrip("Trip"); var incoming = baseline.Copy(); incoming.Data["variant"]!["id"] = Guid.CreateVersion7().ToString(); incoming.Data["variant"]!["title"] = "Alternative";
        var current = new TripSnapshot(baseline, new Dictionary<Guid, Entity>()); var plan = SemanticMerge.Plan(current, current, new(incoming, current.Entities));
        Assert.Equal(baseline.Data["variant"]!.ToJsonString(), plan.Result.Manifest.Data["variant"]!.ToJsonString());
    }

}
