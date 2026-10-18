using System.Text;
using System.Text.Json.Nodes;
using TravelRepo.Core;
using TravelRepo.Repository;
using TravelRepo.Serialization;
using Xunit;
namespace TravelRepo.Tests;

public class DeletionTests
{
    // Comments need an author; Sample() sets it and Trip() adds it. Tests in one class run one after another.
    private static Entity Author = null!;
    private static TripSnapshot Trip(Entity manifest, params Entity[] entities) => new(manifest, entities.Append(Author).DistinctBy(e => e.Id).ToDictionary(e => e.Id));

    private static (Entity Manifest, Entity Lodge, Entity Stay, Entity Booking, Entity Comment, Entity Dinner) Sample()
    {
        var manifest = Entity.CreateTrip("New Zealand");
        var sam = Entity.Create("person", "Sam");
        var lodge = Entity.Create("place", "Aroha Lodge");
        var stay = Entity.Create("schedule_item", "Night in Rotorua");
        stay.Data["default_place"] = lodge.Id.ToString();
        stay.Data["components"]!["accommodation"] = new JsonObject { ["place"] = lodge.Id.ToString(), ["rooms"] = new JsonArray("Loft") };
        var booking = Entity.Create("booking", "Booking 4711"); booking.Data["items"] = new JsonArray(stay.Id.ToString());
        stay.Data["components"]!["booking"] = new JsonObject { ["ref"] = booking.Id.ToString() };
        var comment = Entity.Create("comment", "Comment"); comment.Data.Remove("title");
        comment.Data["target"] = new JsonObject { ["type"] = "schedule_item", ["id"] = stay.Id.ToString() };
        comment.Data["author"] = sam.Id.ToString(); comment.Data["created_at"] = "2027-01-01T10:00:00Z"; comment.Data["body"] = "Hot tub!";
        Author = sam;
        var dinner = Entity.Create("schedule_item", "Dinner by the lake");
        dinner.Data["content"] = new JsonArray(new JsonObject { ["type"] = "reference", ["entity"] = stay.Id.ToString() }, new JsonObject { ["type"] = "link", ["url"] = "https://example.com" });
        dinner.Data["extensions"]!["org.example.plugin"] = new JsonObject { ["stay"] = stay.Id.ToString() };
        return (manifest, lodge, stay, booking, comment, dinner);
    }

    [Fact]
    public void DetachRemovesMembersEntriesAndEmptiedObjectsButKeepsExtensions()
    {
        var (_, lodge, stay, booking, _, dinner) = Sample();
        var targets = new HashSet<Guid> { stay.Id };
        Assert.Empty(TripReferences.Detach(booking, targets).Data["items"]!.AsArray());
        var cleanDinner = TripReferences.Detach(dinner, targets);
        Assert.Single(cleanDinner.Data["content"]!.AsArray());
        Assert.Equal(stay.Id.ToString(), cleanDinner.Data["extensions"]!["org.example.plugin"]!["stay"]!.ToString());
        var cleanStay = TripReferences.Detach(stay, new HashSet<Guid> { lodge.Id, booking.Id });
        Assert.Null(cleanStay.Data["default_place"]);
        Assert.Null(cleanStay.Data["components"]!["booking"]);
        Assert.NotNull(TripReferences.Detach(stay, new HashSet<Guid> { lodge.Id }).Data["components"]!["accommodation"]);
        Assert.Null(cleanStay.Data["components"]!["accommodation"]!["place"]);
        Assert.Equal("Loft", cleanStay.Data["components"]!["accommodation"]!["rooms"]![0]!.ToString());
        Assert.Empty(SchemaValidation.Validate(cleanStay));
    }

    [Fact]
    public void DeletingAStayDetachesReferencesDeletesCommentsAndOffersUnusedPlace()
    {
        var (manifest, lodge, stay, booking, comment, dinner) = Sample();
        var trip = Trip(manifest, lodge, stay, booking, comment, dinner);
        var plan = TripDeletion.Plan(trip, [stay.Id]);
        Assert.Equal(new[] { stay.Id, comment.Id }.Order(), plan.Deleted.Select(e => e.Id).Order());
        Assert.Single(plan.Dependents, e => e.Id == comment.Id);
        Assert.Contains(plan.Updated, e => e.Id == booking.Id);
        Assert.Contains(plan.Updated, e => e.Id == dinner.Id);
        Assert.Equal(new[] { booking.Id, lodge.Id }.Order(), plan.Unused.Select(e => e.Id).Order());

        var applied = Apply(trip, plan);
        Assert.DoesNotContain(SemanticValidation.Validate(applied), d => d.Severity == Severity.Error);
        var withUnused = TripDeletion.Plan(trip, plan.Requested.Concat(plan.Unused.Select(e => e.Id)));
        Assert.Empty(withUnused.Unused);
        Assert.DoesNotContain(SemanticValidation.Validate(Apply(trip, withUnused)), d => d.Severity == Severity.Error);
    }

    [Fact]
    public void DeletingAPlaceInUseDetachesItFromItems()
    {
        var (manifest, lodge, stay, booking, comment, dinner) = Sample();
        var trip = Trip(manifest, lodge, stay, booking, comment, dinner);
        var plan = TripDeletion.Plan(trip, [lodge.Id]);
        Assert.Equal([lodge.Id], plan.Deleted.Select(e => e.Id).ToArray());
        var updatedStay = Assert.Single(plan.Updated, e => e.Id == stay.Id);
        Assert.Null(updatedStay.Data["default_place"]);
        Assert.DoesNotContain(SemanticValidation.Validate(Apply(trip, plan)), d => d.Severity == Severity.Error);
    }

    [Fact]
    public void PlacesUsedElsewhereAreNotOfferedAndParticipantsAreDetached()
    {
        var (manifest, lodge, stay, _, _, _) = Sample();
        var walk = Entity.Create("schedule_item", "Walk"); walk.Data["default_place"] = lodge.Id.ToString();
        var alex = Entity.Create("person", "Alex"); manifest.Data["participants"]!.AsArray().Add(alex.Id.ToString());
        stay.Data["participants"] = new JsonObject { ["inherit"] = false, ["values"] = new JsonArray(alex.Id.ToString()) };
        var trip = Trip(manifest, lodge, stay, walk, alex);
        Assert.DoesNotContain(TripDeletion.Plan(trip, [stay.Id]).Unused, e => e.Id == lodge.Id);
        var plan = TripDeletion.Plan(trip, [alex.Id]);
        Assert.Empty(plan.Updated.Single(e => e.Id == manifest.Id).Data["participants"]!.AsArray());
        Assert.Empty(plan.Updated.Single(e => e.Id == stay.Id).Data["participants"]!["values"]!.AsArray());
    }

    [Fact]
    public void DocumentsFreeTheirBlobAndNotesWhenNothingElseUsesThem()
    {
        var manifest = Entity.CreateTrip("Trip");
        var bytes = Encoding.UTF8.GetBytes("%PDF-1.4"); var hash = TravelRepository.Hash(bytes); var blob = "assets/sha256/" + hash[..2] + "/" + hash;
        Entity Doc() { var d = Entity.Create("document", "ticket.pdf"); d.Data["media_type"] = "application/pdf"; d.Data["blob"] = new JsonObject { ["algorithm"] = "sha256", ["hash"] = hash }; return d; }
        var first = Doc(); var second = Doc();
        var item = Entity.Create("schedule_item", "Ferry"); item.Data["content"] = new JsonArray(new JsonObject { ["type"] = "markdown", ["file"] = "documents/ferry.md" }, new JsonObject { ["type"] = "pdf", ["document"] = first.Id.ToString() });
        var trip = new TripSnapshot(manifest, new[] { first, second, item }.ToDictionary(e => e.Id)) { Resources = new Dictionary<string, byte[]> { [blob] = bytes, ["documents/ferry.md"] = "Bring snacks"u8.ToArray() } };
        var itemPlan = TripDeletion.Plan(trip, [item.Id]);
        Assert.Equal(["documents/ferry.md"], itemPlan.RemovedResources.ToArray());
        Assert.Contains(itemPlan.Unused, e => e.Id == first.Id);
        Assert.Empty(TripDeletion.Plan(trip, [first.Id]).RemovedResources);
        Assert.Contains(blob, TripDeletion.Plan(trip, [first.Id, second.Id]).RemovedResources);
    }

    [Fact]
    public void TheManifestCannotBeDeleted()
    {
        var manifest = Entity.CreateTrip("Trip");
        Assert.Equal("manifest.delete", Assert.Throws<DomainException>(() => TripDeletion.Plan(Trip(manifest), [manifest.Id])).Code);
    }

    private static TripSnapshot Apply(TripSnapshot trip, DeletionPlan plan)
    {
        var entities = trip.Entities.ToDictionary(p => p.Key, p => p.Value); var manifest = trip.Manifest;
        foreach (var e in plan.Deleted) entities.Remove(e.Id);
        foreach (var e in plan.Updated) { if (e.Id == manifest.Id) manifest = e; else entities[e.Id] = e; }
        return new TripSnapshot(manifest, entities) { Resources = trip.Resources.Where(r => !plan.RemovedResources.Contains(r.Key)).ToDictionary() };
    }
}
