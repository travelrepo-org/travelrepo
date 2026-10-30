using System.Text.Json.Nodes;
using TravelRepo.Core;
using Xunit;
namespace TravelRepo.Tests;

public class NestingTests
{
    private static TripSnapshot Trip(params Entity[] entities) => new(Entity.CreateTrip("Test", "en", "Europe/Berlin"), entities.ToDictionary(e => e.Id));

    private static TripSnapshot Apply(TripSnapshot trip, IEnumerable<Entity> changed)
    {
        var all = trip.Entities.ToDictionary(p => p.Key, p => p.Value);
        foreach (var e in changed) all[e.Id] = e;
        return trip with { Entities = all };
    }

    [Fact]
    public void AStepMovesFromOneGroupToAnotherAndTakesTheGroupsPeople()
    {
        var tour = Entity.Create("schedule_item", "Day tour"); var walk = Entity.Create("schedule_item", "Old town"); var lunch = Entity.Create("schedule_item", "Lunch");
        var trip = Trip(tour, walk, lunch);
        trip = Apply(trip, TripCommands.Nest(trip, tour.Id, lunch.Id));
        Assert.Equal(tour.Id, TripQueries.Parent(trip, lunch.Id)!.Id);
        Assert.Equal("true", trip.Find(lunch.Id)!.Data["participants"]!["inherit"]!.ToString());

        trip = Apply(trip, TripCommands.Nest(trip, walk.Id, lunch.Id));
        Assert.Equal(walk.Id, TripQueries.Parent(trip, lunch.Id)!.Id);
        Assert.Empty((JsonArray)trip.Find(tour.Id)!.Data["children"]!);
        Assert.DoesNotContain(SemanticValidation.Validate(trip), d => d.Code.StartsWith("nest.", StringComparison.Ordinal));

        trip = Apply(trip, TripCommands.Unnest(trip, lunch.Id));
        Assert.Null(TripQueries.Parent(trip, lunch.Id));
    }

    [Fact]
    public void AGroupCannotBecomeAStepOfItsOwnStep()
    {
        var tour = Entity.Create("schedule_item", "Day tour"); var walk = Entity.Create("schedule_item", "Old town");
        var trip = Trip(tour, walk);
        trip = Apply(trip, TripCommands.Nest(trip, tour.Id, walk.Id));
        Assert.Equal("nest.cycle", Assert.Throws<DomainException>(() => TripCommands.Nest(trip, walk.Id, tour.Id)).Code);
        Assert.Equal("nest.cycle", Assert.Throws<DomainException>(() => TripCommands.Nest(trip, tour.Id, tour.Id)).Code);
    }

    [Fact]
    public void LinkingABookingPointsBothWaysAndTakesItemsFromTheirOldBooking()
    {
        var old = Entity.Create("booking", "Old"); var booking = Entity.Create("booking", "Rail pass");
        var train = Entity.Create("schedule_item", "Train"); var bus = Entity.Create("schedule_item", "Bus");
        var hotel = Entity.Create("schedule_item", "Hotel"); hotel.Data["components"] = new JsonObject { ["accommodation"] = new JsonObject { ["booking"] = old.Id.ToString() } };
        old.Data["items"] = new JsonArray(hotel.Id.ToString());
        var trip = Trip(old, booking, train, bus, hotel);

        trip = Apply(trip, TripCommands.LinkBooking(trip, booking.Id, [train.Id, hotel.Id]));
        Assert.Equal(booking.Id, TripCommands.BookingOf(trip.Find(train.Id)!));
        Assert.Equal(booking.Id.ToString(), trip.Find(hotel.Id)!.Data["components"]!["accommodation"]!["booking"]!.ToString());
        Assert.Empty((JsonArray)trip.Find(old.Id)!.Data["items"]!);
        Assert.Equal([train.Id.ToString(), hotel.Id.ToString()], ((JsonArray)trip.Find(booking.Id)!.Data["items"]!).Select(n => n!.ToString()));

        trip = Apply(trip, TripCommands.LinkBooking(trip, booking.Id, [bus.Id]));
        Assert.Null(TripCommands.BookingOf(trip.Find(train.Id)!));
        Assert.Null(TripCommands.BookingOf(trip.Find(hotel.Id)!));
        Assert.Equal(booking.Id, TripCommands.BookingOf(trip.Find(bus.Id)!));
        Assert.Equal([bus.Id.ToString()], ((JsonArray)trip.Find(booking.Id)!.Data["items"]!).Select(n => n!.ToString()));
        Assert.DoesNotContain(SemanticValidation.Validate(trip), d => d.Severity == Severity.Error);
    }
}
