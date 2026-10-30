using System.Text.Json.Nodes;
using NodaTime;
using TravelRepo.Core;
using Xunit;
namespace TravelRepo.Tests;

public class ConnectionTests
{
    private static Entity Place(string name, double lat, double lon)
    {
        var place = Entity.Create("place", name); place.Data["location"] = new JsonObject { ["latitude"] = lat, ["longitude"] = lon }; return place;
    }

    private static Entity Journey(string title, Entity from, Entity to, string start, string startZone, string end, string endZone, string type = "flight")
    {
        var item = Entity.Create("schedule_item", title);
        item.Data["time"] = new JsonObject { ["precision"] = "exact", ["start"] = new ZonedTime(start, startZone).ToJson(), ["end"] = new ZonedTime(end, endZone).ToJson() };
        item.Data["components"] = new JsonObject { ["transport"] = new JsonObject { ["type"] = type, ["departure"] = new JsonObject { ["place"] = from.Id.ToString() }, ["arrival"] = new JsonObject { ["place"] = to.Id.ToString() } } };
        return item;
    }

    [Fact]
    public void ALayoverConnectsTwoFlightsAndAJourneyDaysLaterDoesNot()
    {
        var home = Place("Home airport", 50.03, 8.57); var hub = Place("Hub airport", 37.46, 126.44); var hubSecond = Place("Hub second airport", 37.56, 126.80);
        var far = Place("Destination", -33.94, 151.18); var city = Place("City station", -33.88, 151.21);
        var first = Journey("First flight", home, hub, "2027-03-01T13:00:00", "Europe/Berlin", "2027-03-02T06:00:00", "Asia/Seoul");
        var second = Journey("Second flight", hubSecond, far, "2027-03-02T14:20:00", "Asia/Seoul", "2027-03-03T05:00:00", "Australia/Sydney");
        var train = Journey("Train", city, far, "2027-03-05T09:00:00", "Australia/Sydney", "2027-03-05T10:00:00", "Australia/Sydney", "train");
        var cancelled = Journey("Old flight", hub, far, "2027-03-02T09:00:00", "Asia/Seoul", "2027-03-03T01:00:00", "Australia/Sydney"); cancelled.Data["status"] = "cancelled";
        var trip = new TripSnapshot(Entity.CreateTrip("Test", "en", "Europe/Berlin"), new[] { home, hub, hubSecond, far, city, first, second, train, cancelled }.ToDictionary(e => e.Id));

        var connection = Assert.Single(Connections.In(trip));
        Assert.Equal((first.Id, second.Id), (connection.Arriving.Id, connection.Departing.Id));
        // The second airport of the city counts as the same place to change at; the layover is in real time.
        Assert.Equal(Duration.FromMinutes(8 * 60 + 20), connection.Layover);
        Assert.Equal(hub.Id, connection.Place!.Id);
    }
}
