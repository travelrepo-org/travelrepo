using System.Text.Json.Nodes;
using TravelRepo.Core;
using TravelRepo.Merge;
using TravelRepo.Serialization;
using Xunit;
namespace TravelRepo.Tests;

public class PackingListTests
{
    private static readonly Entity Ana = Entity.Create("person", "Ana"), Ben = Entity.Create("person", "Ben"), Cleo = Entity.Create("person", "Cleo");
    private static TripSnapshot Trip(params Entity[] entities) => new(Entity.CreateTrip("Lake weekend", "en", "Europe/Berlin"), entities.Concat([Ana, Ben, Cleo]).DistinctBy(e => e.Id).ToDictionary(e => e.Id));

    /// <summary>A barbecue list: grill by Ana, bread shared by Ben and Cleo, drinks by everyone, plates by nobody yet.</summary>
    private static (TripSnapshot Trip, Entity List, Entity Grill, Entity Bread, Entity Drinks, Entity Plates) Barbecue()
    {
        var list = PackingLists.CreateList("Barbecue");
        (var grill, list) = PackingLists.AddItem(list, "Grill", "Cooking", "1", [Ana.Id]);
        (var bread, list) = PackingLists.AddItem(list, "Bread", "Food", null, [Ben.Id, Cleo.Id]);
        (var drinks, list) = PackingLists.AddItem(list, "Drinks", "Food", "2 l each", everyone: true);
        (var plates, list) = PackingLists.AddItem(list, "Plates");
        return (Trip(list, grill, bread, drinks, plates), list, grill, bread, drinks, plates);
    }

    [Fact]
    public void ItemsKnowWhoBringsThemAndWhatIsUnassigned()
    {
        var (trip, list, _, _, _, _) = Barbecue();
        var resolved = Assert.Single(PackingLists.All(trip));
        Assert.Equal(["Grill", "Bread", "Drinks", "Plates"], resolved.Items.Select(i => i.Task.Title));
        Assert.Equal(["Plates"], PackingLists.Unassigned(resolved).Select(i => i.Task.Title));
        Assert.Equal(["Bread", "Drinks"], PackingLists.For(resolved, Cleo.Id).Select(i => i.Task.Title));
        Assert.Equal(["Cooking", "Food", null], PackingLists.Groups(resolved.Items).Select(g => g.Group));
        Assert.Equal("2 l each", resolved.Items[2].Quantity);
        Assert.Empty(SchemaValidation.Validate(list)); Assert.All(resolved.Items, i => Assert.Empty(SchemaValidation.Validate(i.Task)));
        Assert.DoesNotContain(SemanticValidation.Validate(trip), d => d.Severity == Severity.Error);

        // "Everyone" includes people who join later.
        var dana = Entity.Create("person", "Dana");
        var later = new TripSnapshot(trip.Manifest, trip.Entities.Values.Append(dana).ToDictionary(e => e.Id));
        Assert.Contains(PackingLists.Resolve(later, list).Items[2].Assignees, p => p.Id == dana.Id);
    }

    [Fact]
    public void SharedItemsAreDoneWhenOnePacksAndEveryoneItemsWhenAllHave()
    {
        var (trip, _, _, bread, drinks, _) = Barbecue();
        var packedBread = PackingLists.SetPacked(trip, bread, Ben.Id, true);
        Assert.True(PackingLists.Item(trip, packedBread).Done); Assert.Equal("completed", packedBread.Data["status"]!.ToString());

        var d = drinks;
        foreach (var person in new[] { Ana, Ben }) d = PackingLists.SetPacked(trip, d, person.Id, true);
        Assert.False(PackingLists.Item(trip, d).Done); Assert.Equal("open", d.Data["status"]!.ToString());
        d = PackingLists.SetPacked(trip, d, Cleo.Id, true);
        Assert.True(PackingLists.Item(trip, d).Done);
        d = PackingLists.SetPacked(trip, d, Ana.Id, false);
        Assert.Equal("open", d.Data["status"]!.ToString()); Assert.False(PackingLists.Item(trip, d).IsPackedBy(Ana.Id));
    }

    [Fact]
    public void PeoplePackingAtTheSameTimeMergeWithoutConflict()
    {
        var (trip, _, _, _, drinks, _) = Barbecue();
        var ours = PackingLists.SetPacked(trip, drinks, Ana.Id, true); var theirs = PackingLists.SetPacked(trip, drinks, Ben.Id, true);
        TripSnapshot With(Entity e) => new(trip.Manifest, trip.Entities.Values.Where(x => x.Id != e.Id).Append(e).ToDictionary(x => x.Id));
        var plan = SemanticMerge.Plan(trip, With(ours), With(theirs));
        Assert.Empty(plan.Conflicts);
        var merged = PackingLists.Item(plan.Result, plan.Result.Find(drinks.Id)!);
        Assert.True(merged.IsPackedBy(Ana.Id)); Assert.True(merged.IsPackedBy(Ben.Id));
    }

    [Fact]
    public void ReassigningAndDeletingAPersonKeepTheListConsistent()
    {
        var (trip, _, grill, _, _, plates) = Barbecue();
        var everyone = PackingLists.Assign(plates, [], everyone: true);
        Assert.Null(everyone.Data["assigned_to"]); Assert.Equal(3, PackingLists.Item(trip, everyone).Assignees.Count);
        var back = PackingLists.Assign(everyone, [Cleo.Id], everyone: false);
        Assert.Null(back.Data["components"]!["packing"]!["everyone"]); Assert.Equal([Cleo.Id], PackingLists.Item(trip, back).Assignees.Select(a => a.Id));

        // Deleting Ana removes her from the grill and from who has packed it.
        var packed = PackingLists.SetPacked(trip, grill, Ana.Id, true);
        var detached = TripReferences.Detach(packed, new HashSet<Guid> { Ana.Id });
        Assert.DoesNotContain(Ana.Id.ToString(), detached.Data.ToJsonString());
        var described = PackingLists.Describe(detached, "Gear", "");
        Assert.Equal("Gear", described.Data["components"]!["packing"]!["group"]!.ToString()); Assert.Null(described.Data["components"]!["packing"]!["quantity"]);

        // A packed mark for someone who is not in the trip is a broken reference.
        var stranger = PackingLists.SetPacked(trip, grill, Guid.CreateVersion7(), true);
        var broken = new TripSnapshot(trip.Manifest, trip.Entities.Values.Where(e => e.Id != grill.Id).Append(stranger).ToDictionary(e => e.Id));
        Assert.Contains(SemanticValidation.Validate(broken), d => d.Code == "reference.missing" && d.Path.Contains("packed_by"));
    }
}
