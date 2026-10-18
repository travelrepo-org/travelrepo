using System.Text.Json.Nodes;
using NodaTime;
using TravelRepo.Core;
using TravelRepo.Serialization;
using Xunit;
namespace TravelRepo.Tests;

public class StayTests
{
    private const string Zone = "Pacific/Auckland";
    private static readonly Entity Lodge = Entity.Create("place", "Lakeview Lodge");

    private static TripSnapshot Trip(params Entity[] entities)
    {
        var manifest = Entity.CreateTrip("New Zealand", "en", Zone);
        manifest.Data["dates"] = new JsonObject { ["start"] = "2026-11-08", ["end"] = "2026-11-14" };
        return new(manifest, entities.Append(Lodge).DistinctBy(e => e.Id).ToDictionary(e => e.Id));
    }

    private static Entity Stay(string title = "Lakeview Lodge", string? checkIn = null, string? checkOut = null, JsonObject? time = null, Guid? place = null)
    {
        var e = Entity.Create("schedule_item", title); var accommodation = new JsonObject { ["place"] = (place ?? Lodge.Id).ToString() };
        if (checkIn is not null) accommodation["check_in"] = new ZonedTime(checkIn, Zone).ToJson();
        if (checkOut is not null) accommodation["check_out"] = new ZonedTime(checkOut, Zone).ToJson();
        e.Data["components"]!["accommodation"] = accommodation; e.Data["time"] = time; e.Data["status"] = "planned";
        return e;
    }

    private static JsonObject NightOf(string date) => new() { ["precision"] = "day_part", ["date"] = date, ["day_part"] = "night", ["timezone"] = Zone };
    private static JsonObject Exact(string start, string end) => new() { ["precision"] = "exact", ["start"] = new ZonedTime(start, Zone).ToJson(), ["end"] = new ZonedTime(end, Zone).ToJson() };

    [Theory]
    // Check-in and check-out: nights from the check-in day to the day before check-out.
    [InlineData("2026-11-09T15:00:00", "2026-11-11T10:00:00", null, "2026-11-09", 2, StayIssues.None)]
    // A day room: check-in and check-out on the same day.
    [InlineData("2026-11-09T09:00:00", "2026-11-09T17:00:00", null, "2026-11-09", 0, StayIssues.None)]
    // Only check-in: one night.
    [InlineData("2026-11-09T15:00:00", null, null, "2026-11-09", 1, StayIssues.MissingCheckOut)]
    // Only check-out: the night before.
    [InlineData(null, "2026-11-11T10:00:00", null, "2026-11-10", 1, StayIssues.MissingCheckIn)]
    // Only a night of a date, as entered per night.
    [InlineData(null, null, "night:2026-11-12", "2026-11-12", 1, StayIssues.None)]
    // Only a general time span.
    [InlineData(null, null, "exact:2026-11-09T15:00:00/2026-11-12T10:00:00", "2026-11-09", 3, StayIssues.None)]
    // Check-in and check-out win over a general time that says something else.
    [InlineData("2026-11-09T15:00:00", "2026-11-11T10:00:00", "night:2026-11-09", "2026-11-09", 2, StayIssues.TimeMismatch)]
    // Only check-in, with a general time that ends later: the time's end counts.
    [InlineData("2026-11-09T15:00:00", null, "exact:2026-11-09T15:00:00/2026-11-12T10:00:00", "2026-11-09", 3, StayIssues.MissingCheckOut)]
    public void NightsFollowTheRules(string? checkIn, string? checkOut, string? time, string firstNight, int nights, StayIssues issues)
    {
        JsonObject? value = time switch
        {
            null => null,
            _ when time.StartsWith("night:", StringComparison.Ordinal) => NightOf(time[6..]),
            _ => Exact(time[6..].Split('/')[0], time[6..].Split('/')[1])
        };
        var stay = Stays.Resolve(Trip(), Stay(checkIn: checkIn, checkOut: checkOut, time: value))!;
        Assert.Equal(LocalDate.FromDateTime(DateTime.Parse(firstNight, System.Globalization.CultureInfo.InvariantCulture)), stay.FirstNight);
        Assert.Equal(nights, stay.Nights); Assert.Equal(issues, stay.Issues);
    }

    [Fact]
    public void AStayWithoutAnyTimeIsUnscheduled()
    {
        var stay = Stays.Resolve(Trip(), Stay())!;
        Assert.False(stay.IsScheduled); Assert.True(ScheduleQueries.IsUnscheduled(Trip(), stay.Item));
        Assert.Null(Stays.Resolve(Trip(), Entity.Create("schedule_item", "Walk")));
    }

    [Fact]
    public void WritingAStayKeepsTimeAndCheckInOutTogether()
    {
        var item = Stays.WithNights(Trip(), Stay(), new LocalDate(2026, 11, 9), 3);
        Assert.Equal("2026-11-09T15:00:00", item.Data["components"]!["accommodation"]!["check_in"]!["local"]!.ToString());
        Assert.Equal("2026-11-12T10:00:00", item.Data["components"]!["accommodation"]!["check_out"]!["local"]!.ToString());
        Assert.Equal("2026-11-09T15:00:00", item.Data["time"]!["start"]!["local"]!.ToString());
        Assert.Equal(StayIssues.None, Stays.Resolve(Trip(item), item)!.Issues);
        Assert.Empty(SchemaValidation.Validate(item));

        // Moving it keeps the hours that were set.
        var later = Stays.WithSpan(item, new ZonedTime("2026-11-10T17:30:00", Zone), new ZonedTime("2026-11-11T09:00:00", Zone));
        var moved = Stays.WithNights(Trip(later), later, new LocalDate(2026, 11, 12), 2);
        Assert.Equal("2026-11-12T17:30:00", moved.Data["time"]!["start"]!["local"]!.ToString());
        Assert.Equal("2026-11-14T09:00:00", moved.Data["time"]!["end"]!["local"]!.ToString());
        Assert.Throws<DomainException>(() => Stays.WithNights(Trip(), Stay(), new LocalDate(2026, 11, 9), 0));
    }

    [Fact]
    public void AMismatchIsAWarningAndAlignFixesIt()
    {
        var item = Stay(checkIn: "2026-11-09T15:00:00", checkOut: "2026-11-11T10:00:00", time: NightOf("2026-11-09"));
        var trip = Trip(item);
        var warning = Assert.Single(SemanticValidation.Validate(trip), d => d.Code == "stay.time_mismatch");
        Assert.Equal(Severity.Warning, warning.Severity);
        var aligned = Stays.Align(trip, item);
        Assert.DoesNotContain(SemanticValidation.Validate(Trip(aligned)), d => d.Code == "stay.time_mismatch");
        Assert.Equal("2026-11-11T10:00:00", aligned.Data["time"]!["end"]!["local"]!.ToString());
        // The span used for exports and conflicts follows check-in and check-out.
        var span = ScheduleQueries.Span(trip, item);
        Assert.Equal(new ZonedTime("2026-11-11T10:00:00", Zone).ToInstant(), span.End);
    }

    [Fact]
    public void OneItemPerNightIsOfferedAsOneStayAndMerged()
    {
        // The pattern of a real trip: the first night carries check-in and check-out, later nights only a date.
        var first = Stay("Night at the lodge", "2026-11-09T15:00:00", "2026-11-11T10:00:00", NightOf("2026-11-09"));
        first.Data["content"] = new JsonArray(new JsonObject { ["type"] = "link", ["url"] = "https://example.com/lodge" });
        var second = Stay("Night at the lodge", time: NightOf("2026-11-10")); second.Data["status"] = "confirmed";
        second.Data["content"] = new JsonArray(new JsonObject { ["type"] = "link", ["url"] = "https://example.com/booking" });
        var other = Entity.Create("place", "Cabin"); var cabin = Stay("Cabin", time: NightOf("2026-11-11"), place: other.Id);
        var cabin2 = Stay("Cabin", time: NightOf("2026-11-12"), place: other.Id);
        var gap = Stay("Cabin", time: NightOf("2026-11-14"), place: other.Id);
        var booking = Entity.Create("booking", "Lodge booking"); booking.Data["items"] = new JsonArray(first.Id.ToString(), second.Id.ToString());
        var trip = Trip(first, second, other, cabin, cabin2, gap, booking);

        var candidates = Stays.MergeCandidates(trip);
        Assert.Equal(2, candidates.Count);
        var lodge = candidates[0];
        Assert.Equal([first.Id, second.Id], lodge.Stays.Select(s => s.Item.Id).ToArray());
        Assert.Equal(new LocalDate(2026, 11, 9), lodge.FirstNight); Assert.Equal(2, lodge.Nights);
        Assert.Equal(2, candidates[1].Nights);
        Assert.DoesNotContain(candidates[1].Stays, s => s.Item.Id == gap.Id);

        var merge = Stays.Merge(trip, lodge);
        Assert.Equal(first.Id, merge.Kept.Id); Assert.Equal([second.Id], merge.Removed.ToArray());
        Assert.Equal("confirmed", merge.Kept.Data["status"]!.ToString());
        Assert.Equal(2, merge.Kept.Data["content"]!.AsArray().Count);
        Assert.Equal("2026-11-11T10:00:00", merge.Kept.Data["time"]!["end"]!["local"]!.ToString());
        var redirected = Assert.Single(merge.Redirected);
        Assert.Equal([first.Id.ToString()], redirected.Data["items"]!.AsArray().Select(n => n!.ToString()).ToArray());

        var cabins = Stays.Merge(trip, candidates[1]);
        Assert.Equal("2026-11-11T15:00:00", cabins.Kept.Data["components"]!["accommodation"]!["check_in"]!["local"]!.ToString());
        Assert.Equal("2026-11-13T10:00:00", cabins.Kept.Data["components"]!["accommodation"]!["check_out"]!["local"]!.ToString());
    }

    [Fact]
    public void MissingNightsIgnoreNightsWithAnOvernightItem()
    {
        var stay = Stay(checkIn: "2026-11-08T15:00:00", checkOut: "2026-11-10T10:00:00");
        var flight = Entity.Create("schedule_item", "Night flight"); flight.Data["time"] = Exact("2026-11-11T21:30:00", "2026-11-12T07:10:00");
        var party = Entity.Create("schedule_item", "Party"); party.Data["time"] = Exact("2026-11-12T20:00:00", "2026-11-13T00:30:00");
        var trip = Trip(stay, flight, party);
        // Nights 8 and 9 have the stay, 11 has the flight; the party ends too early to cover night 12.
        Assert.Equal([new LocalDate(2026, 11, 10), new LocalDate(2026, 11, 12), new LocalDate(2026, 11, 13)], Stays.MissingNights(trip).ToArray());
    }
}
