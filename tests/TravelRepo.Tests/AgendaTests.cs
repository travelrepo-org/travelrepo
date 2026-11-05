using System.Text.Json.Nodes;
using NodaTime;
using TravelRepo.Core;
using Xunit;
namespace TravelRepo.Tests;

public class AgendaTests
{
    private const string Zone = "Asia/Tokyo";

    private static Entity Item(string title, string start, string end, string zone = Zone, string? endZone = null)
    {
        var item = Entity.Create("schedule_item", title);
        item.Data["time"] = new JsonObject { ["precision"] = "exact", ["start"] = new ZonedTime(start, zone).ToJson(), ["end"] = new ZonedTime(end, endZone ?? zone).ToJson() };
        return item;
    }

    private static TripSnapshot Trip(params Entity[] entities)
    {
        var manifest = Entity.CreateTrip("Kansai", "en", Zone); manifest.Data["dates"] = new JsonObject { ["start"] = "2027-04-01", ["end"] = "2027-04-04" };
        return new(manifest, entities.ToDictionary(e => e.Id));
    }

    [Fact]
    public void EveryTripDayIsListedAndTimedLinesAreInOrder()
    {
        var late = Item("Dinner", "2027-04-02T19:00:00", "2027-04-02T21:00:00");
        var early = Item("Temple", "2027-04-02T09:00:00", "2027-04-02T11:00:00");
        var allDay = Entity.Create("schedule_item", "Nara"); allDay.Data["time"] = new JsonObject { ["precision"] = "all_day", ["date"] = "2027-04-02" };
        var dated = Entity.Create("schedule_item", "Laundry"); dated.Data["time"] = new JsonObject { ["date"] = "2027-04-02" };

        var agenda = TripAgenda.Build(Trip(late, early, allDay, dated));

        Assert.Equal([new LocalDate(2027, 4, 1), new LocalDate(2027, 4, 2), new LocalDate(2027, 4, 3), new LocalDate(2027, 4, 4)], agenda.Days.Select(d => d.Date));
        var day = agenda.Find(new LocalDate(2027, 4, 2))!;
        Assert.Equal(2, day.Number);
        Assert.Equal(["Nara", "Temple", "Dinner", "Laundry"], day.Entries.Select(e => e.Item.Title));
        Assert.True(day.Entries[1].IsTimed); Assert.False(day.Entries[0].IsTimed); Assert.False(day.Entries[3].IsTimed);
        Assert.Empty(agenda.Find(new LocalDate(2027, 4, 1))!.Entries);
    }

    [Fact]
    public void AGroupAppearsOnceWithItsStepsInOrder()
    {
        var train = Item("Train to Nikko", "2027-04-03T08:10:00", "2027-04-03T10:00:00");
        var falls = Item("Kegon Falls", "2027-04-03T13:30:00", "2027-04-03T15:00:00");
        var shrine = Item("Toshogu Shrine", "2027-04-03T10:30:00", "2027-04-03T12:30:00");
        var trip = Entity.Create("schedule_item", "Day trip to Nikko");
        trip.Data["children"] = new JsonArray(falls.Id.ToString(), train.Id.ToString(), shrine.Id.ToString());

        var day = TripAgenda.Build(Trip(train, falls, shrine, trip)).Find(new LocalDate(2027, 4, 3))!;

        var group = Assert.Single(day.Entries);
        Assert.Equal("Day trip to Nikko", group.Item.Title); Assert.True(group.IsGroup);
        Assert.Equal(["Train to Nikko", "Toshogu Shrine", "Kegon Falls"], group.Steps.Select(s => s.Item.Title));
        Assert.Equal(new ZonedTime("2027-04-03T08:10:00", Zone).ToInstant(), group.Start);
    }

    [Fact]
    public void StaysShowCheckInCheckOutAndTheNights()
    {
        var stay = Entity.Create("schedule_item", "Ryokan");
        stay.Data["components"]!["accommodation"] = new JsonObject
        {
            ["check_in"] = new ZonedTime("2027-04-01T15:00:00", Zone).ToJson(),
            ["check_out"] = new ZonedTime("2027-04-03T10:00:00", Zone).ToJson()
        };

        var agenda = TripAgenda.Build(Trip(stay));

        var checkIn = Assert.Single(agenda.Find(new LocalDate(2027, 4, 1))!.Entries);
        Assert.Equal(AgendaRole.CheckIn, checkIn.Role); Assert.Equal(ScheduleKind.Accommodation, checkIn.Kind);
        Assert.Equal(new ZonedTime("2027-04-01T15:00:00", Zone).ToInstant(), checkIn.Start);
        Assert.Equal(AgendaRole.CheckOut, Assert.Single(agenda.Find(new LocalDate(2027, 4, 3))!.Entries).Role);
        Assert.Equal(2, Assert.Single(agenda.Find(new LocalDate(2027, 4, 2))!.Nights).Number);
        Assert.Empty(agenda.Find(new LocalDate(2027, 4, 3))!.Nights);
    }

    [Fact]
    public void AJourneyBelongsToTheDayItStartsOnWhereItStarts()
    {
        // 20:00 in Berlin is already 03:00 the next morning in Tokyo.
        var flight = Item("Flight to Tokyo", "2027-03-31T20:00:00", "2027-04-01T15:00:00", "Europe/Berlin", Zone);

        var agenda = TripAgenda.Build(Trip(flight));

        var line = Assert.Single(agenda.Find(new LocalDate(2027, 3, 31))!.Entries);
        Assert.Equal("Europe/Berlin", line.Zone); Assert.Equal(Zone, line.EndZone);
        Assert.Equal(0, agenda.Find(new LocalDate(2027, 3, 31))!.Number);
        // On the day it lands it appears again, as the rest of the journey.
        var landing = Assert.Single(agenda.Find(new LocalDate(2027, 4, 1))!.Entries);
        Assert.Equal(AgendaRole.Continued, landing.Role); Assert.Equal(line.End, landing.End);
    }

    [Fact]
    public void AnItemPastMidnightAppearsOnEveryDayItTouches()
    {
        var hike = Item("Kumano Kodo", "2027-04-01T08:00:00", "2027-04-03T16:00:00");
        var party = Item("Hanami party", "2027-04-02T20:00:00", "2027-04-03T00:00:00");

        var agenda = TripAgenda.Build(Trip(hike, party));

        Assert.Equal(AgendaRole.Item, Assert.Single(agenda.Find(new LocalDate(2027, 4, 1))!.Entries).Role);
        Assert.Equal([AgendaRole.Continued, AgendaRole.Item], agenda.Find(new LocalDate(2027, 4, 2))!.Entries.Select(e => e.Role));
        // Ending at midnight is still the evening before.
        Assert.Equal(hike.Id, Assert.Single(agenda.Find(new LocalDate(2027, 4, 3))!.Entries).Item.Id);
    }

    [Fact]
    public void IdeasWithoutADayAreListedApartAndIdeasWithATimeStayInTheDay()
    {
        var untimed = Entity.Create("schedule_item", "Onsen evening?"); untimed.Data["status"] = "idea";
        var timed = Item("Dinner in Ginza", "2027-04-02T19:00:00", "2027-04-02T21:00:00"); timed.Data["status"] = "idea";

        var agenda = TripAgenda.Build(Trip(untimed, timed));

        Assert.True(Assert.Single(agenda.Unscheduled).IsIdea);
        Assert.True(Assert.Single(agenda.Find(new LocalDate(2027, 4, 2))!.Entries).IsIdea);
    }

    [Fact]
    public void DestinationsAreTheTownsOfTheStaysWithoutCountryOrPostalCode()
    {
        var entities = new List<Entity>();
        void Stay(string address, string from, string to)
        {
            var place = Entity.Create("place", "Hotel"); place.Data["address"] = new JsonObject { ["formatted"] = address };
            var stay = Entity.Create("schedule_item", "Hotel");
            stay.Data["components"]!["accommodation"] = new JsonObject { ["place"] = place.Id.ToString(), ["check_in"] = new ZonedTime(from, Zone).ToJson(), ["check_out"] = new ZonedTime(to, Zone).ToJson() };
            entities.Add(place); entities.Add(stay);
        }
        Stay("Gion, Higashiyama Ward, Kyoto, 605-0001", "2027-04-02T15:00:00", "2027-04-04T10:00:00");
        Stay("Kabukicho, Shinjuku City, Tokyo, Japan", "2027-04-01T15:00:00", "2027-04-02T10:00:00");

        Assert.Equal(["Tokyo", "Kyoto"], TripSummaries.Destinations(Trip([.. entities])));
        Assert.Empty(TripSummaries.Destinations(Trip()));
        Assert.Equal("Kyoto", TripSummaries.Town(entities[0]));
        Assert.Null(TripSummaries.Town(Entity.Create("place", "Somewhere")));
    }
}
