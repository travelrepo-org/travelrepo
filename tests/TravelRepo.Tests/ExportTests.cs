using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using NodaTime;
using TravelRepo.Core;
using TravelRepo.Repository;
using Xunit;
namespace TravelRepo.Tests;

public class ExportTests
{
    private const string Zone = "Asia/Tokyo";
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly Entity Mia = Entity.Create("person", "Mia"), Noah = Entity.Create("person", "Noah");

    private static Entity Item(string title, string start, string end, params Entity[] people)
    {
        var item = Entity.Create("schedule_item", title);
        item.Data["time"] = new JsonObject { ["precision"] = "exact", ["start"] = new ZonedTime(start, Zone).ToJson(), ["end"] = new ZonedTime(end, Zone).ToJson() };
        if (people.Length > 0) item.Data["participants"] = new JsonObject { ["inherit"] = false, ["values"] = new JsonArray(people.Select(p => (JsonNode)p.Id.ToString()).ToArray()) };
        return item;
    }

    private static TripSnapshot Trip(params Entity[] entities)
    {
        var manifest = Entity.CreateTrip("Kansai", "en", Zone); manifest.Data["dates"] = new JsonObject { ["start"] = "2027-04-01", ["end"] = "2027-04-05" };
        return new(manifest, entities.Concat([Mia, Noah]).DistinctBy(e => e.Id).ToDictionary(e => e.Id));
    }

    [Fact]
    public void ACalendarShowsStaysAsCheckInAndCheckOut()
    {
        Entity Stay(string title, string checkIn, string checkOut, bool approximateIn = false, bool approximateOut = false)
        {
            var item = Entity.Create("schedule_item", title); item.Data["components"] = new JsonObject { ["accommodation"] = new JsonObject() };
            return Stays.WithSpan(item, new ZonedTime(checkIn, Zone), new ZonedTime(checkOut, Zone), approximateIn, approximateOut);
        }
        // A fixed check-in and a check-out window; check-in hours without a fixed arrival and no departure hour.
        var ryokan = Stay("Ryokan", "2027-04-01T16:00:00", "2027-04-03T10:00:00", approximateOut: true);
        ((JsonObject)ryokan.Data["components"]!["accommodation"]!)["check_out_window"] = new JsonObject { ["until"] = "11:00" };
        var hotel = Stay("Hotel", "2027-04-03T15:00:00", "2027-04-05T10:00:00", approximateIn: true, approximateOut: true);
        ((JsonObject)hotel.Data["components"]!["accommodation"]!)["check_in_window"] = new JsonObject { ["from"] = "15:00", ["until"] = "19:00" };
        var hike = Item("Hike", "2027-04-02T09:00:00", "2027-04-02T15:00:00");
        var trip = Trip(ryokan, hotel, hike);
        Instant At(string local) => new ZonedTime(local, Zone).ToInstant();

        var entries = TripCalendar.Entries(trip, staysAsCheckInOut: true).ToDictionary(e => e.Uid);
        Assert.Equal(5, entries.Count);
        Assert.Equal(CalendarEntryRole.Plan, entries[hike.Id.ToString()].Role);
        var arrive = entries[ryokan.Id + "-check-in"];
        Assert.Equal((CalendarEntryRole.CheckIn, At("2027-04-01T16:00:00"), At("2027-04-01T16:00:00")), (arrive.Role, arrive.Start!.Value, arrive.End!.Value));
        var leave = entries[ryokan.Id + "-check-out"];
        Assert.Equal((At("2027-04-03T11:00:00"), At("2027-04-03T11:00:00")), (leave.Start!.Value, leave.End!.Value));
        var window = entries[hotel.Id + "-check-in"];
        Assert.Equal((At("2027-04-03T15:00:00"), At("2027-04-03T19:00:00")), (window.Start!.Value, window.End!.Value));
        var unknown = entries[hotel.Id + "-check-out"];
        Assert.True(unknown.AllDay); Assert.Equal(new LocalDate(2027, 4, 5), unknown.Date);
        Assert.All(entries.Values.Where(e => e.Role != CalendarEntryRole.Plan), e => Assert.Equal(ScheduleKind.Accommodation, e.Kind));

        // The iCalendar file keeps one entry across the nights.
        var ics = TripExport.Ics(trip);
        Assert.Contains("UID:" + ryokan.Id + "@travelrepo", ics); Assert.DoesNotContain("-check-in@", ics);
    }

    /// <summary>A shared breakfast, Mia's museum, Noah's hike on the next day, and an idea without a time.</summary>
    private static (TripSnapshot Trip, Entity Breakfast, Entity Museum, Entity Hike, Entity Idea) Plan()
    {
        var breakfast = Item("Breakfast", "2027-04-01T08:00:00", "2027-04-01T09:00:00");
        var museum = Item("Museum", "2027-04-01T10:00:00", "2027-04-01T12:00:00", Mia);
        var hike = Item("Hike", "2027-04-02T09:00:00", "2027-04-02T15:00:00", Noah);
        var idea = Entity.Create("schedule_item", "Tea ceremony");
        return (Trip(breakfast, museum, hike, idea), breakfast, museum, hike, idea);
    }

    [Fact]
    public void OnePersonsExportKeepsSharedItemsAndDropsOtherPeoplesItems()
    {
        var (trip, _, _, _, _) = Plan();
        var it = TripItinerary.Build(trip, culture: Invariant, options: new ExportOptions { Person = Mia.Id });
        Assert.Equal(["Breakfast", "Museum"], it.Days.SelectMany(d => d.Entries).Select(e => e.Item.Title));
        Assert.Equal("Tea ceremony", Assert.Single(it.Unscheduled).Item.Title);
        var document = TripExport.Document(trip, new ExportOptions { Person = Mia.Id }, culture: Invariant);
        Assert.Contains("Plan for Mia", document.Meta);
    }

    [Fact]
    public void DateRangeKeepsDayNumbersAndLeavesOutUnscheduledItems()
    {
        var (trip, _, _, _, _) = Plan();
        var it = TripItinerary.Build(trip, culture: Invariant, options: new ExportOptions { From = new LocalDate(2027, 4, 2), To = new LocalDate(2027, 4, 2) });
        var day = Assert.Single(it.Days);
        Assert.Equal(2, day.Number); Assert.Equal("Hike", Assert.Single(day.Entries).Item.Title);
        Assert.Empty(it.Unscheduled);
        var ics = TripExport.Ics(trip, new ExportOptions { From = new LocalDate(2027, 4, 2) });
        Assert.Contains("SUMMARY:Hike", ics); Assert.DoesNotContain("SUMMARY:Museum", ics);
        Assert.Contains("2 Apr – 5 Apr", TripExport.Document(trip, new ExportOptions { From = new LocalDate(2027, 4, 2) }, culture: Invariant).Meta);
        var undated = trip with { Manifest = Entity.CreateTrip("Kansai", "en", Zone), Entities = trip.Entities.Values.Where(e => e.Type == "person").ToDictionary(e => e.Id) };
        Assert.Contains("from 2 Apr", TripExport.Document(undated, new ExportOptions { From = new LocalDate(2027, 4, 2) }, culture: Invariant).Meta);
    }

    [Fact]
    public void ItemsThatStartBeforeTheRangeAppearOnItsFirstDay()
    {
        var train = Item("Night train", "2027-04-01T22:00:00", "2027-04-02T07:00:00");
        var it = TripItinerary.Build(Trip(train), culture: Invariant, options: new ExportOptions { From = new LocalDate(2027, 4, 2) });
        Assert.Equal(new LocalDate(2027, 4, 2), Assert.Single(it.Days).Date);
    }

    [Fact]
    public void BookingsFollowTheirTravellersAndScheduleItems()
    {
        var (trip, _, museum, hike, _) = Plan();
        var tickets = Entity.Create("booking", "Museum tickets"); tickets.Data["items"] = new JsonArray(museum.Id.ToString());
        var guide = Entity.Create("booking", "Mountain guide"); guide.Data["travelers"] = new JsonArray(Noah.Id.ToString()); guide.Data["items"] = new JsonArray(hike.Id.ToString());
        var insurance = Entity.Create("booking", "Travel insurance");
        trip = trip with { Entities = trip.Entities.Values.Concat([tickets, guide, insurance]).ToDictionary(e => e.Id) };
        Assert.Equal(["Museum tickets", "Travel insurance"], TripItinerary.Build(trip, culture: Invariant, options: new ExportOptions { Person = Mia.Id }).Bookings.Select(b => b.Title));
        Assert.Equal(["Mountain guide"], TripItinerary.Build(trip, culture: Invariant, options: new ExportOptions { From = new LocalDate(2027, 4, 2) }).Bookings.Select(b => b.Title));
    }

    [Fact]
    public void PartsAndDetailDecideWhatTheDocumentShows()
    {
        var (trip, breakfast, museum, _, _) = Plan();
        var cafe = Entity.Create("place", "Kissa Hana"); cafe.Data["contact"] = new JsonObject { ["phone"] = "+81 75 000 0000" };
        breakfast = breakfast.Copy(); breakfast.Data["default_place"] = cafe.Id.ToString(); breakfast.Data["status"] = "confirmed";
        var task = Entity.Create("task", "Buy rail passes"); task.Data["due"] = new JsonObject { ["date"] = "2027-03-20" };
        var note = Entity.Create("note", "Phrases"); note.Data["body"] = "Bow when greeting."; note.Data["content"] = new JsonArray(new JsonObject { ["type"] = "markdown", ["file"] = "documents/phrases.md" });
        trip = trip with { Entities = trip.Entities.Values.Concat([cafe, breakfast, task, note]).Reverse().DistinctBy(e => e.Id).ToDictionary(e => e.Id), Resources = new Dictionary<string, byte[]> { ["documents/phrases.md"] = Encoding.UTF8.GetBytes("# Phrases\n\n- **Arigatou**: thank you\n- [Map](https://example.org)\n") } };

        var full = TripExport.Document(trip, new ExportOptions { Parts = ExportParts.All & ~ExportParts.Map }, culture: Invariant);
        Assert.Equal([ExportParts.Schedule, ExportParts.Contacts, ExportParts.Tasks, ExportParts.Notes], full.Sections.Select(s => s.Part).Distinct());
        Assert.Equal("Day 1", full.Sections[0].Overline);
        var row = full.Sections[0].Rows.Single(r => r.Title == "Breakfast");
        Assert.Equal("08:00–09:00", row.Lead); Assert.Equal("Kissa Hana", row.Summary); Assert.Equal("confirmed", row.Status);
        Assert.Equal("+81 75 000 0000", full.Sections.Single(s => s.Part == ExportParts.Contacts).Rows.Single().Summary);
        var taskRow = full.Sections.Single(s => s.Part == ExportParts.Tasks).Rows.Single();
        Assert.Equal(ExportRowKind.Check, taskRow.Kind); Assert.Equal("Due 20 Mar", taskRow.Lead);
        Assert.Equal(["Bow when greeting.", "Phrases", "• Arigatou: thank you", "• Map"], full.Sections.Single(s => s.Part == ExportParts.Notes).Rows.Single().DetailLines);

        var compact = TripExport.Document(trip, new ExportOptions { Parts = ExportParts.Schedule, Detail = ExportDetail.Compact }, culture: Invariant);
        Assert.All(compact.Sections, s => Assert.Equal(ExportParts.Schedule, s.Part));
        Assert.Null(compact.Sections[0].Rows.Single(r => r.Title == "Breakfast").Status);
        Assert.DoesNotContain(compact.Sections[0].Rows, r => r.Title == "Museum" && r.Summary is not null);
    }

    [Fact]
    public void StaysListNightsCheckInAndHouseRules()
    {
        var hotel = Entity.Create("place", "Hotel Kamo"); hotel.Data["address"] = new JsonObject { ["formatted"] = "Kyoto" };
        var stay = Entity.Create("schedule_item", "Hotel Kamo"); stay.Data["status"] = "confirmed";
        stay.Data["components"]!["accommodation"] = new JsonObject
        {
            ["place"] = hotel.Id.ToString(),
            ["check_in"] = new ZonedTime("2027-04-01T15:00:00", Zone).ToJson(),
            ["check_out"] = new ZonedTime("2027-04-03T10:00:00", Zone).ToJson(),
            ["check_in_window"] = new JsonObject { ["from"] = "15:00", ["until"] = "22:00" },
            ["breakfast"] = "included",
            ["access"] = "self"
        };
        var document = TripExport.Document(Trip(hotel, stay), new ExportOptions { Parts = ExportParts.Stays }, culture: Invariant);
        var row = Assert.Single(Assert.Single(document.Sections).Rows);
        Assert.Equal("1 Apr – 3 Apr", row.Lead); Assert.Equal("2 nights", row.Summary);
        Assert.Contains("Check-in Thu 1 Apr, 15:00 · Check-out Sat 3 Apr, 10:00", row.DetailLines);
        Assert.Contains("Check-in hours 15:00 to 22:00", row.DetailLines); Assert.Contains("Breakfast: included", row.DetailLines); Assert.Contains("Self check-in", row.DetailLines); Assert.Contains("Kyoto", row.DetailLines);
        Assert.Empty(TripExport.Document(Trip(hotel, stay), new ExportOptions { Parts = ExportParts.Stays, From = new LocalDate(2027, 4, 4) }, culture: Invariant).Sections);
    }

    [Fact]
    public void AttachmentsAreLabelledOnceAndOnlyWhenWanted()
    {
        var (trip, _, museum, _, _) = Plan();
        var ticket = Entity.Create("document", "ticket.pdf"); ticket.Data["media_type"] = "application/pdf";
        museum = museum.Copy(); museum.Data["content"] = new JsonArray(TripCommands.ContentEntry(ticket));
        var booking = Entity.Create("booking", "Museum tickets"); booking.Data["items"] = new JsonArray(museum.Id.ToString()); booking.Data["documents"] = new JsonArray(ticket.Id.ToString());
        trip = trip with { Entities = trip.Entities.Values.Concat([ticket, museum, booking]).Reverse().DistinctBy(e => e.Id).ToDictionary(e => e.Id) };

        var document = TripExport.Document(trip, new ExportOptions { Parts = ExportParts.Standard | ExportParts.Attachments }, culture: Invariant);
        var attachment = Assert.Single(document.Attachments);
        Assert.Equal("A1", attachment.Label); Assert.Equal(["Museum", "Museum tickets"], attachment.UsedBy);
        Assert.Equal(["A1"], document.Sections.SelectMany(s => s.Rows).Single(r => r.Title == "Museum").AttachmentLabels);
        Assert.Contains("Attachment A1", TripExport.Text(document));
        Assert.Empty(TripExport.Document(trip, new ExportOptions(), culture: Invariant).Attachments);
    }

    [Fact]
    public void MapDataHasThePlacesAndRoutesOfTheExport()
    {
        static Entity Place(string name, double lat, double lon) { var p = Entity.Create("place", name); p.Data["location"] = new JsonObject { ["latitude"] = lat, ["longitude"] = lon }; return p; }
        var kyoto = Place("Kyoto", 35.0, 135.76); var nara = Place("Nara", 34.68, 135.8); var osaka = Place("Osaka", 34.69, 135.5);
        var train = Item("Train", "2027-04-02T09:00:00", "2027-04-02T10:00:00"); train.Data["components"]!["transport"] = new JsonObject { ["type"] = "train", ["departure"] = new JsonObject { ["place"] = kyoto.Id.ToString() }, ["arrival"] = new JsonObject { ["place"] = nara.Id.ToString() } };
        var dinner = Item("Dinner", "2027-04-04T19:00:00", "2027-04-04T21:00:00"); dinner.Data["default_place"] = osaka.Id.ToString();
        var trip = Trip(kyoto, nara, osaka, train, dinner);
        var map = TripExport.Document(trip, new ExportOptions { Parts = ExportParts.Map }, culture: Invariant).Map!;
        Assert.Equal(["Kyoto", "Nara", "Osaka"], map.Places.Select(p => p.Title)); Assert.Single(map.Routes);
        Assert.Equal(["Kyoto", "Nara"], TripExport.Document(trip, new ExportOptions { Parts = ExportParts.Map, To = new LocalDate(2027, 4, 3) }, culture: Invariant).Map!.Places.Select(p => p.Title));
        Assert.Null(TripExport.Document(trip, new ExportOptions(), culture: Invariant).Map);
    }

    [Fact]
    public void PersonalCostsShowThatPersonsShareAndWhatIsOpen()
    {
        Entity Expense(string title, string value, Entity payer, bool estimated = false) { var e = Entity.Create("expense", title); e.Data["amount"] = new JsonObject { ["value"] = value, ["currency"] = "JPY" }; e.Data["estimated"] = estimated; e.Data["paid_by"] = new JsonArray(payer.Id.ToString()); return e; }
        var budget = Entity.Create("budget", "Food"); budget.Data["amount"] = new JsonObject { ["value"] = "50000", ["currency"] = "JPY" };
        var ramen = Expense("Ramen", "2400", Mia); var taxi = Expense("Taxi", "3000", Noah);
        var snacks = Expense("Snacks", "1000", Noah, estimated: true);
        var trip = Trip(ramen, taxi, snacks, budget);
        taxi = Expenses.Settle(trip, taxi, Mia.Id, new LocalDate(2027, 3, 2));
        // Noah's sake is his alone, so it does not concern Mia.
        var sake = Expenses.WithSplit(Expense("Sake", "1800", Noah), SplitMethod.Equal, [(Noah.Id, null)]);
        trip = Trip(ramen, taxi, snacks, sake, budget);

        Assert.Equal(7200m, TripItinerary.Build(trip, culture: Invariant).Costs.Single().Paid);
        // Mia's half of the ramen and the taxi, and of the snacks still estimated; budgets are for the whole trip.
        var mine = TripItinerary.Build(trip, culture: Invariant, options: new ExportOptions { Person = Mia.Id }).Costs.Single();
        Assert.Equal((2700m, 500m, 0m), (mine.Paid, mine.Estimated, mine.Budget));

        var costs = TripExport.Document(trip, new ExportOptions { Person = Mia.Id, Parts = ExportParts.Costs }, culture: Invariant).Sections.Single();
        Assert.Equal("2,700.00 share · 500.00 estimated", costs.Rows[0].Title);
        Assert.Equal(["Ramen", "Snacks", "Taxi"], costs.Rows.Skip(1).Where(r => r.Kind == ExportRowKind.Item && !r.Title.Contains("pays", StringComparison.Ordinal)).Select(r => r.Title));
        var ramenRow = costs.Rows.Single(r => r.Title == "Ramen");
        Assert.Equal("paid by Mia · share 1,200.00 JPY", ramenRow.Summary);
        Assert.Equal(["Noah owes Mia 1,200.00 JPY"], ramenRow.Details);
        Assert.Equal(["Mia paid back Noah 1,500.00 JPY"], costs.Rows.Single(r => r.Title == "Taxi").Details);
        Assert.Equal("To settle up", costs.Rows.Single(r => r.Kind == ExportRowKind.Heading).Title);
        Assert.Equal(("Noah pays Mia", "1,200.00 JPY"), (costs.Rows[^1].Title, costs.Rows[^1].Lead));

        // The whole trip's export says what is open per expense and how to settle up.
        var all = TripExport.Document(trip, new ExportOptions { Parts = ExportParts.Costs }, culture: Invariant).Sections.Single();
        Assert.Equal("paid by Mia · 1,200.00 JPY open", all.Rows.Single(r => r.Title == "Ramen").Summary);
        Assert.Equal("paid by Noah · settled", all.Rows.Single(r => r.Title == "Taxi").Summary);
        Assert.Contains(all.Rows, r => r.Title == "Sake");
        Assert.Contains("Noah pays Mia", TripExport.Text(TripExport.Document(trip, new ExportOptions { Parts = ExportParts.Costs }, culture: Invariant)));
    }

    [Fact]
    public void HtmlEscapesAndDrawsCheckBoxes()
    {
        var list = PackingLists.CreateList("Day pack <b>");
        (var water, list) = PackingLists.AddItem(list, "Water", everyone: true);
        var trip = Trip(list, PackingLists.SetPacked(Trip(list, water), water, Mia.Id, true));
        var html = TripExport.Html(PackingExport.Document(trip));
        Assert.Contains("Day pack &lt;b&gt;", html); Assert.Contains("&#9744;", html); Assert.DoesNotContain("<b>", html);
    }

    [Fact]
    public void OptionsReadAndWriteTheirTextForms()
    {
        Assert.Equal("schedule,map,attachments", ExportOptions.FormatParts(ExportParts.Schedule | ExportParts.Map | ExportParts.Attachments));
        Assert.Equal(ExportParts.Standard, ExportOptions.ParseParts(ExportOptions.FormatParts(ExportParts.Standard)));
        Assert.Throws<DomainException>(() => ExportOptions.ParseParts("schedule,weather"));
        Assert.Equal(new LocalDate(2027, 4, 2), ExportOptions.ParseDate("2027-04-02")); Assert.Null(ExportOptions.ParseDate(" "));
        Assert.Equal(PaperSize.Letter, ExportOptions.PaperFor(new CultureInfo("en-US"))); Assert.Equal(PaperSize.A4, ExportOptions.PaperFor(new CultureInfo("de-DE")));
    }

    [Fact]
    public void StaysShowCheckInCheckOutAndEachNightInsteadOfASpanningItem()
    {
        // One item per night at the same hotel, the first carrying check-in and check-out for the whole stay.
        var hotel = Entity.Create("place", "Hotel Kamo");
        Entity Night(string date, bool first)
        {
            var e = Entity.Create("schedule_item", "Hotel Kamo"); e.Data["status"] = "confirmed";
            e.Data["time"] = new JsonObject { ["precision"] = "day_part", ["date"] = date, ["day_part"] = "night", ["timezone"] = Zone };
            var accommodation = new JsonObject { ["place"] = hotel.Id.ToString() };
            if (first) { accommodation["check_in"] = new ZonedTime("2027-04-01T15:00:00", Zone).ToJson(); accommodation["check_out"] = new ZonedTime("2027-04-04T10:00:00", Zone).ToJson(); }
            e.Data["components"]!["accommodation"] = accommodation;
            return e;
        }
        var dinner = Item("Dinner", "2027-04-01T19:00:00", "2027-04-01T21:00:00");
        var trip = Trip(hotel, Night("2027-04-01", true), Night("2027-04-02", false), Night("2027-04-03", false), dinner);

        var run = Assert.Single(Stays.Runs(trip)); Assert.Equal(3, run.Nights); Assert.Equal(3, run.Stays.Count);
        var document = TripExport.Document(trip, new ExportOptions { Parts = ExportParts.Schedule | ExportParts.Stays }, culture: Invariant);
        var days = document.Sections.Where(s => s.Part == ExportParts.Schedule).ToArray();
        Assert.Equal(["Thursday, 1 April", "Friday, 2 April", "Saturday, 3 April", "Sunday, 4 April"], days.Select(d => d.Title));
        Assert.Equal(["Check-in · Hotel Kamo", "Dinner", "Night: Hotel Kamo"], days[0].Rows.Select(r => r.Title));
        Assert.Equal("15:00", days[0].Rows[0].Lead); Assert.Equal("accommodation", days[0].Rows[0].Tone);
        Assert.Equal("night 1 of 3", days[0].Rows[2].Summary); Assert.Equal(ExportRowKind.Night, days[0].Rows[2].Kind);
        Assert.Equal("night 3 of 3", days[2].Rows.Single().Summary);
        Assert.Equal(["Check-out · Hotel Kamo"], days[3].Rows.Select(r => r.Title)); Assert.Equal("10:00", days[3].Rows[0].Lead);
        // The stays section shows the run once.
        var stay = Assert.Single(document.Sections.Single(s => s.Part == ExportParts.Stays).Rows);
        Assert.Equal("1 Apr – 4 Apr", stay.Lead); Assert.Contains("3 nights", stay.Summary);
        // Nights appear on the time line too, without the stay as a block.
        Assert.Equal(["Hotel Kamo"], days[0].Timeline!.Nights); Assert.Equal(["Dinner"], days[0].Timeline!.Blocks.Select(b => b.Title));
    }

    [Fact]
    public void TimesInAnotherZoneCarryTheirShortNameAtBothEnds()
    {
        var flight = Entity.Create("schedule_item", "Flight to Osaka");
        flight.Data["time"] = new JsonObject { ["precision"] = "exact", ["start"] = new ZonedTime("2027-03-31T13:20:00", "Europe/Berlin").ToJson(), ["end"] = new ZonedTime("2027-04-01T08:35:00", Zone).ToJson() };
        var walk = Item("Walk", "2027-04-01T10:00:00", "2027-04-01T11:00:00");
        var document = TripExport.Document(Trip(flight, walk), new ExportOptions { Parts = ExportParts.Schedule }, culture: Invariant);
        var rows = document.Sections.SelectMany(s => s.Rows).ToArray();
        Assert.Equal("13:20 CEST – 1 Apr 08:35 JST", rows.Single(r => r.Title == "Flight to Osaka").Lead);
        Assert.Equal("10:00–11:00", rows.Single(r => r.Title == "Walk").Lead);
        Assert.Equal("Time zone: Tokyo (JST, UTC+9)", document.ZoneNote); Assert.Contains(document.ZoneNote, document.Meta);
    }

    [Fact]
    public void ZoneNamesFallBackToOffsetsAndShowSummerTime()
    {
        Assert.Equal("Berlin (CET/CEST, UTC+1/+2)", ExportTimes.Describe(DateTimeZoneProviders.Tzdb["Europe/Berlin"], new LocalDate(2027, 3, 26), new LocalDate(2027, 3, 30)));
        Assert.Equal("Sao Paulo (UTC-3)", ExportTimes.Describe(DateTimeZoneProviders.Tzdb["America/Sao_Paulo"], new LocalDate(2027, 3, 26), null));
        Assert.Equal("UTC+5:30", ExportTimes.Abbreviation(DateTimeZoneProviders.Tzdb["Asia/Colombo"], Instant.FromUtc(2027, 1, 1, 0, 0)));
    }

    [Fact]
    public void OverlappingPlansShareTheTimeLine()
    {
        var blocks = ExportTimeline.Arrange([(600, 720, "Museum", "sightseeing"), (660, 780, "Call", "activity"), (690, 700, "Coffee", "food"), (900, 960, "Train", "transport"), (100, 100, "Empty", "activity")]);
        Assert.Equal([(0, 3), (1, 3), (2, 3), (0, 1)], blocks.Select(b => (b.Lane, b.Lanes)));
        var night = Item("Night bus", "2027-04-01T22:00:00", "2027-04-02T06:00:00");
        var days = TripExport.Document(Trip(night), new ExportOptions { Parts = ExportParts.Schedule }, culture: Invariant).Sections;
        var block = Assert.Single(days[0].Timeline!.Blocks); Assert.Equal((1320, 1440), (block.Start, block.End));
        Assert.Null(TripExport.Document(Trip(night), new ExportOptions { Parts = ExportParts.Schedule, Timeline = false }, culture: Invariant).Sections[0].Timeline);
    }

    internal static Entity Comment(Entity on, Entity? author, string at, string body)
    {
        var c = Entity.Create("comment", "Comment"); c.Data.Remove("title");
        c.Data["target"] = new JsonObject { ["type"] = on.Type, ["id"] = on.Id.ToString() };
        if (author is not null) c.Data["author"] = author.Id.ToString();
        c.Data["created_at"] = at; c.Data["body"] = body;
        return c;
    }

    [Fact]
    public void AnExplicitTimeZoneShowsEveryTimeAndDayInIt()
    {
        var early = Item("Early train", "2027-04-02T06:00:00", "2027-04-02T06:30:00");
        var flight = Entity.Create("schedule_item", "Flight home");
        flight.Data["time"] = new JsonObject { ["precision"] = "exact", ["start"] = new ZonedTime("2027-04-03T10:00:00", Zone).ToJson(), ["end"] = new ZonedTime("2027-04-03T15:00:00", "Europe/Berlin").ToJson() };
        var inn = Entity.Create("schedule_item", "Inn"); inn.Data["components"] = new JsonObject { ["accommodation"] = new JsonObject() };
        inn = Stays.WithSpan(inn, new ZonedTime("2027-04-01T16:00:00", Zone), new ZonedTime("2027-04-02T10:00:00", Zone));
        var trip = Trip(early, flight, inn, Comment(early, Mia, "2027-03-01T20:00:00Z", "Book seats"));
        ItineraryEntry Entry(Itinerary it, string title, out LocalDate day)
        {
            var found = it.Days.SelectMany(d => d.Entries.Select(e => (d.Date, e))).First(x => x.e.Item.Title == title);
            day = found.Date; return found.e;
        }

        // Without a time zone, times follow the trip (Tokyo), and a time planned elsewhere names its zone.
        var trips = TripItinerary.Build(trip, culture: Invariant);
        Assert.Equal(Zone, trips.Zone);
        Assert.Equal("06:00–06:30", Entry(trips, "Early train", out var tripDay).Time); Assert.Equal(new LocalDate(2027, 4, 2), tripDay);
        Assert.Equal("10:00 JST – 15:00 CEST", Entry(trips, "Flight home", out _).Time);

        // In Berlin the early train runs the evening before, and the flight needs no zone names.
        var options = new ExportOptions { TimeZone = "Europe/Berlin", Comments = true, Parts = ExportParts.Schedule | ExportParts.Stays };
        var berlin = TripItinerary.Build(trip, culture: Invariant, options: options);
        Assert.Equal("Europe/Berlin", berlin.Zone);
        Assert.Equal("23:00–23:30", Entry(berlin, "Early train", out var berlinDay).Time); Assert.Equal(new LocalDate(2027, 4, 1), berlinDay);
        Assert.Equal("03:00–15:00", Entry(berlin, "Flight home", out _).Time);
        Assert.Equal("09:00", berlin.Days.SelectMany(d => d.Entries).Single(e => e.Role == ItineraryRole.CheckIn).Time);

        // The date range counts in the same zone.
        Assert.Contains(TripItinerary.Build(trip, culture: Invariant, options: options with { From = new LocalDate(2027, 4, 1), To = new LocalDate(2027, 4, 1) }).Days.SelectMany(d => d.Entries), e => e.Item.Title == "Early train");
        Assert.DoesNotContain(TripItinerary.Build(trip, culture: Invariant, options: new ExportOptions { From = new LocalDate(2027, 4, 1), To = new LocalDate(2027, 4, 1) }).Days.SelectMany(d => d.Entries), e => e.Item.Title == "Early train");

        var document = TripExport.Document(trip, options, culture: Invariant);
        Assert.Contains(document.Meta, m => m.Contains("Berlin (CEST, UTC+2)", StringComparison.Ordinal));
        Assert.Contains("Berlin (CEST, UTC+2)", document.ZoneNote, StringComparison.Ordinal);
        var rows = document.Sections.SelectMany(s => s.Rows).ToArray();
        Assert.Equal("1 Mar 2027", rows.Single(r => r.Title == "Early train").Comments.Single().When);
        Assert.Contains("Thu 1 Apr, 09:00", string.Join(" | ", rows.Select(r => r.Summary ?? "").Concat(rows.SelectMany(r => r.DetailLines))), StringComparison.Ordinal);
        Assert.Equal("2 Mar 2027", TripExport.Document(trip, options with { TimeZone = null }, culture: Invariant).Sections.SelectMany(s => s.Rows).Single(r => r.Title == "Early train").Comments.Single().When);

        Assert.Equal("Pacific/Auckland", ExportOptions.ParseZone(" Pacific/Auckland "));
        Assert.Null(ExportOptions.ParseZone(""));
        Assert.Equal("export.timezone", Assert.Throws<DomainException>(() => ExportOptions.ParseZone("Middle/Earth")).Code);
        Assert.Throws<DomainException>(() => TripItinerary.Build(trip, options: new ExportOptions { TimeZone = "Middle/Earth" }));
    }

    [Fact]
    public void ReadFromWestOfTheTripCalendarDaysStayAndExactTimesMove()
    {
        // A Tokyo trip read in Los Angeles (UTC-7 in April), where every exact time falls on the day before or the same day.
        var early = Item("Early train", "2027-04-01T06:00:00", "2027-04-01T06:30:00");
        var museum = Entity.Create("schedule_item", "Museum"); museum.Data["time"] = new JsonObject { ["precision"] = "all_day", ["date"] = "2027-04-02" };
        var market = Entity.Create("schedule_item", "Market"); market.Data["time"] = new JsonObject { ["precision"] = "day_part", ["date"] = "2027-04-03", ["day_part"] = "morning", ["timezone"] = Zone };
        var inn = Entity.Create("schedule_item", "Inn"); inn.Data["components"] = new JsonObject { ["accommodation"] = new JsonObject() };
        inn = Stays.WithSpan(inn, new ZonedTime("2027-04-01T16:00:00", Zone), new ZonedTime("2027-04-03T10:00:00", Zone));
        var trip = Trip(early, museum, market, inn);
        var options = new ExportOptions { TimeZone = "America/Los_Angeles", Parts = ExportParts.Schedule | ExportParts.Stays };
        var it = TripItinerary.Build(trip, culture: Invariant, options: options);
        (LocalDate Day, ItineraryEntry Entry) Find(Func<ItineraryEntry, bool> match) => it.Days.SelectMany(d => d.Entries.Select(e => (d.Date, e))).Single(x => match(x.e));

        // All-day and day-part plans keep their planned day.
        Assert.Equal(new LocalDate(2027, 4, 2), Find(e => e.Item.Title == "Museum").Day);
        Assert.Equal(new LocalDate(2027, 4, 3), Find(e => e.Item.Title == "Market").Day);
        // Exact times move to their day in Los Angeles, check-in and check-out included.
        Assert.Equal((new LocalDate(2027, 3, 31), "14:00–14:30"), (Find(e => e.Item.Title == "Early train").Day, Find(e => e.Item.Title == "Early train").Entry.Time));
        Assert.Equal((new LocalDate(2027, 4, 1), "00:00"), (Find(e => e.Role == ItineraryRole.CheckIn).Day, Find(e => e.Role == ItineraryRole.CheckIn).Entry.Time));
        Assert.Equal((new LocalDate(2027, 4, 2), "18:00"), (Find(e => e.Role == ItineraryRole.CheckOut).Day, Find(e => e.Role == ItineraryRole.CheckOut).Entry.Time));
        // The dates in the header widen to the day before the trip, which becomes day 1.
        Assert.Equal((new LocalDate(2027, 3, 31), new LocalDate(2027, 4, 5)), (it.Start, it.End));
        Assert.Equal(1, it.Days[0].Number);
        var document = TripExport.Document(trip, options, culture: Invariant);
        Assert.Equal("31 March 2027 – 5 April 2027", document.Meta[0]);
        // The market keeps its day, so it is not drawn on that day's time line in hours that belong to the day before.
        Assert.DoesNotContain(document.Sections.Where(s => s.Timeline is not null).SelectMany(s => s.Timeline!.Blocks), b => b.Title == "Market");

        // A calendar file ignores the zone, also for its date range.
        var day = new ExportOptions { From = new LocalDate(2027, 4, 1), To = new LocalDate(2027, 4, 1) };
        Assert.Equal(TripExport.Ics(trip, day), TripExport.Ics(trip, day with { TimeZone = "America/Los_Angeles", OtherTimeZones = ["Europe/Berlin"] }));
    }

    [Fact]
    public void ClockChangesAndRepeatedHoursConvertToTheExportZone()
    {
        Entity Berlin(string title, string start, string end, string? offset = null)
        {
            var item = Entity.Create("schedule_item", title);
            item.Data["time"] = new JsonObject { ["precision"] = "exact", ["start"] = new ZonedTime(start, "Europe/Berlin", offset).ToJson(), ["end"] = new ZonedTime(end, "Europe/Berlin", offset).ToJson() };
            return item;
        }
        // The night of the spring clock change (27 to 28 March 2027) and the repeated hour in autumn (31 October 2027).
        var before = Berlin("Before the change", "2027-03-27T09:00:00", "2027-03-27T10:00:00");
        var after = Berlin("After the change", "2027-03-28T03:30:00", "2027-03-28T04:30:00");
        var repeated = Berlin("Second 02:30", "2027-10-31T02:30:00", "2027-10-31T02:45:00", "+01:00");
        var trip = Trip(before, after, repeated);
        var it = TripItinerary.Build(trip, culture: Invariant, options: new ExportOptions { TimeZone = "UTC", OtherTimeZones = ["Europe/Berlin"] });
        ItineraryEntry Entry(string title) => it.Days.SelectMany(d => d.Entries).Single(e => e.Item.Title == title);

        Assert.Equal("08:00–09:00", Entry("Before the change").Time); Assert.Equal(["Berlin 09:00–10:00"], Entry("Before the change").OtherTimes);
        Assert.Equal("01:30–02:30", Entry("After the change").Time); Assert.Equal(["Berlin 03:30–04:30"], Entry("After the change").OtherTimes);
        Assert.Equal("01:30–01:45", Entry("Second 02:30").Time); Assert.Equal(["Berlin 02:30–02:45"], Entry("Second 02:30").OtherTimes);
        var document = TripExport.Document(trip, new ExportOptions { TimeZone = "Europe/Berlin", From = new LocalDate(2027, 3, 27), To = new LocalDate(2027, 3, 28) }, culture: Invariant);
        Assert.Contains("Berlin (CET/CEST, UTC+1/+2)", document.ZoneNote, StringComparison.Ordinal);
    }

    [Fact]
    public void MoreTimeZonesShowEachTimeBesideTheMainOne()
    {
        var early = Item("Early train", "2027-04-02T06:00:00", "2027-04-02T06:30:00");
        var hike = Item("Hike", "2027-04-02T09:00:00", "2027-04-02T15:00:00");
        var flight = Entity.Create("schedule_item", "Flight home");
        flight.Data["time"] = new JsonObject { ["precision"] = "exact", ["start"] = new ZonedTime("2027-04-03T10:00:00", Zone).ToJson(), ["end"] = new ZonedTime("2027-04-03T15:00:00", "Europe/Berlin").ToJson() };
        var inn = Entity.Create("schedule_item", "Inn"); inn.Data["components"] = new JsonObject { ["accommodation"] = new JsonObject() };
        inn = Stays.WithSpan(inn, new ZonedTime("2027-04-01T16:00:00", Zone), new ZonedTime("2027-04-02T10:00:00", Zone));
        var trip = Trip(early, hike, flight, inn);
        IReadOnlyList<string> Others(Itinerary it, string title) => it.Days.SelectMany(d => d.Entries).First(e => e.Item.Title == title).OtherTimes;

        // The trip's own zone is left out of the others, and days stay those of Tokyo.
        var options = new ExportOptions { OtherTimeZones = ["Europe/Berlin", "UTC", Zone], Parts = ExportParts.Schedule | ExportParts.Stays };
        var it = TripItinerary.Build(trip, culture: Invariant, options: options);
        Assert.Equal(["Berlin 1 Apr 23:00–23:30", "UTC 1 Apr 21:00–21:30"], Others(it, "Early train"));
        Assert.Equal(["Berlin 02:00–08:00", "UTC 00:00–06:00"], Others(it, "Hike"));
        Assert.Equal("10:00 JST – 15:00 CEST", it.Days.SelectMany(d => d.Entries).Single(e => e.Item.Title == "Flight home").Time);
        Assert.Equal(["Berlin 03:00–15:00", "UTC 01:00–13:00"], Others(it, "Flight home"));
        Assert.Equal(["Berlin 09:00", "UTC 07:00"], it.Days.SelectMany(d => d.Entries).Single(e => e.Role == ItineraryRole.CheckIn).OtherTimes);
        Assert.Equal(new LocalDate(2027, 4, 2), it.Days.Single(d => d.Entries.Any(e => e.Item.Title == "Early train")).Date);

        var document = TripExport.Document(trip, options, culture: Invariant);
        Assert.Equal("Time zones: Tokyo (JST, UTC+9) · Berlin (CEST, UTC+2) · UTC (UTC+0)", document.ZoneNote);
        Assert.Equal(["Berlin 1 Apr 23:00–23:30", "UTC 1 Apr 21:00–21:30"], document.Sections.SelectMany(s => s.Rows).Single(r => r.Title == "Early train").OtherTimes);
        Assert.Contains("(Berlin Thu 1 Apr, 09:00; UTC Thu 1 Apr, 07:00)", string.Join(" | ", document.Sections.SelectMany(s => s.Rows).SelectMany(r => r.DetailLines.Prepend(r.Summary ?? ""))));
        var text = TripExport.Text(document);
        Assert.Contains("06:00–06:30  Early train [idea]\n        Berlin 1 Apr 23:00–23:30\n        UTC 1 Apr 21:00–21:30\n", text);
        Assert.Contains("<div class=\"also\">Berlin 1 Apr 23:00–23:30</div>", TripExport.Html(document));

        // With Berlin as the main zone, Tokyo becomes the other one.
        var berlin = TripItinerary.Build(trip, culture: Invariant, options: options with { TimeZone = "Europe/Berlin", OtherTimeZones = [Zone] });
        Assert.Equal("23:00–23:30", berlin.Days.SelectMany(d => d.Entries).Single(e => e.Item.Title == "Early train").Time);
        Assert.Equal(["Tokyo 2 Apr 06:00–06:30"], Others(berlin, "Early train"));

        // Without other zones nothing is added.
        Assert.All(TripItinerary.Build(trip, culture: Invariant).Days.SelectMany(d => d.Entries), e => Assert.Empty(e.OtherTimes));
        Assert.Equal(["Europe/Berlin", "UTC"], ExportOptions.ParseZones("Europe/Berlin, UTC,Europe/Berlin"));
        Assert.Empty(ExportOptions.ParseZones(null));
        Assert.Throws<DomainException>(() => TripItinerary.Build(trip, options: new ExportOptions { OtherTimeZones = ["Middle/Earth"] }));
    }

    [Fact]
    public void GermanDatesHaveAFullStopAfterTheDayAndEnglishOnesDoNot()
    {
        var hike = Item("Hike", "2027-04-02T09:00:00", "2027-04-02T15:00:00");
        var night = Item("Night train", "2027-04-02T22:00:00", "2027-04-03T07:00:00");
        var task = Entity.Create("task", "Buy tickets"); task.Data["due"] = new JsonObject { ["date"] = "2027-03-20" };
        var inn = Entity.Create("schedule_item", "Inn"); inn.Data["components"] = new JsonObject { ["accommodation"] = new JsonObject() };
        inn = Stays.WithSpan(inn, new ZonedTime("2027-04-02T16:00:00", Zone), new ZonedTime("2027-04-04T10:00:00", Zone));
        var trip = Trip(hike, night, inn, task, Comment(hike, Mia, "2027-03-02T09:00:00Z", "Start early?"));
        var options = new ExportOptions { Parts = ExportParts.All & ~ExportParts.Map, Comments = true };
        ExportRow[] Rows(ExportDocument d) => d.Sections.SelectMany(s => s.Rows).ToArray();

        var german = TripExport.Document(trip, options, culture: new CultureInfo("de-DE"));
        Assert.Equal("1. April 2027 – 5. April 2027", german.Meta[0]);
        Assert.Contains(german.Sections, s => s.Title == "Freitag, 2. April");
        Assert.StartsWith("2. M", Rows(german).Single(r => r.Title == "Hike").Comments.Single().When, StringComparison.Ordinal);
        Assert.Contains("20. M", Rows(german).Single(r => r.Title == "Buy tickets").Lead, StringComparison.Ordinal);
        Assert.Contains("3. Apr", Rows(german).Single(r => r.Title == "Night train").Lead, StringComparison.Ordinal);
        Assert.Contains(", 2. Apr", string.Join(" | ", Rows(german).SelectMany(r => new[] { r.Lead, r.Title, r.Summary }.Concat(r.DetailLines))), StringComparison.Ordinal);

        var english = TripExport.Document(trip, options, culture: new CultureInfo("en-GB"));
        Assert.Equal("1 April 2027 – 5 April 2027", english.Meta[0]);
        Assert.Contains(english.Sections, s => s.Title == "Friday, 2 April");
        Assert.Equal("2 Mar 2027", Rows(english).Single(r => r.Title == "Hike").Comments.Single().When);
    }

    [Fact]
    public void CommentsGoBelowWhatTheyAreAboutWhenAsked()
    {
        var hike = Item("Hike", "2027-04-02T09:00:00", "2027-04-02T15:00:00");
        var inn = Entity.Create("schedule_item", "Inn"); inn.Data["components"] = new JsonObject { ["accommodation"] = new JsonObject() };
        inn = Stays.WithSpan(inn, new ZonedTime("2027-04-02T16:00:00", Zone), new ZonedTime("2027-04-04T10:00:00", Zone));
        var task = Entity.Create("task", "Buy tickets");
        var later = Comment(hike, Noah, "2027-03-02T09:00:00Z", "I can carry **the stove**.");
        var earlier = Comment(hike, Mia, "2027-03-01T23:30:00Z", "Start early?\n- before 9");
        var stranger = Comment(task, null, "2027-03-03T10:00:00Z", "Done <today>");
        var trip = Trip(hike, inn, task, later, earlier, stranger, Comment(inn, Mia, "2027-03-04T10:00:00Z", "Quiet room, please"), Comment(task, Mia, "2027-03-05T10:00:00Z", "  "));

        var without = TripExport.Document(trip, new ExportOptions { Parts = ExportParts.All }, culture: Invariant);
        Assert.All(without.Sections.SelectMany(s => s.Rows), r => Assert.Empty(r.Comments));

        var plan = TripExport.Document(trip, new ExportOptions { Parts = ExportParts.Schedule | ExportParts.Tasks, Comments = true }, culture: Invariant);
        var rows = plan.Sections.SelectMany(s => s.Rows).ToArray();
        // Oldest first, dated in the trip's time zone, Markdown removed.
        Assert.Equal([new("Mia", "2 Mar 2027", ["Start early?", "• before 9"]), new ExportComment("Noah", "2 Mar 2027", ["I can carry the stove."])], rows.Single(r => r.Title == "Hike").Comments, new CommentComparer());
        // Without a stays section, the stay's comments go with its check-in; empty comments are left out.
        Assert.Equal(["Quiet room, please"], rows.Single(r => r.Title.StartsWith("Check-in", StringComparison.Ordinal)).Comments.SelectMany(c => c.Lines));
        Assert.Empty(rows.Single(r => r.Title.StartsWith("Check-out", StringComparison.Ordinal)).Comments);
        Assert.Equal("Someone", rows.Single(r => r.Title == "Buy tickets").Comments.Single().Author);

        var stays = TripExport.Document(trip, new ExportOptions { Parts = ExportParts.Schedule | ExportParts.Stays, Comments = true }, culture: Invariant);
        Assert.Empty(stays.Sections.SelectMany(s => s.Rows).Single(r => r.Title.StartsWith("Check-in", StringComparison.Ordinal)).Comments);
        Assert.Single(stays.Sections.Single(s => s.Part == ExportParts.Stays).Rows.Single().Comments);

        var text = TripExport.Text(plan);
        Assert.Contains("» Mia, 2 Mar 2027: Start early?\n", text); Assert.Contains("  • before 9\n", text);
        var html = TripExport.Html(plan);
        Assert.Contains("<span class=\"who\">Someone</span>", html); Assert.Contains("Done &lt;today&gt;", html);
    }

    private sealed class CommentComparer : IEqualityComparer<ExportComment>
    {
        public bool Equals(ExportComment? a, ExportComment? b) => a is not null && b is not null && a.Author == b.Author && a.When == b.When && a.Lines.SequenceEqual(b.Lines);
        public int GetHashCode(ExportComment c) => c.Author.GetHashCode(StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownBecomesPlainLines()
    {
        Assert.Equal(["Title", "• one", "  • nested", "bold and italic", "snake_case_name stays"], ExportText.Plain("## Title\n\n- one\n  * nested\n**bold** and _italic_\n---\nsnake_case_name stays\n"));
    }
}

public class PackingExportTests
{
    private static readonly CultureInfo English = new("en-GB");
    private static readonly Entity Ana = Entity.Create("person", "Ana"), Ben = Entity.Create("person", "Ben"), Cleo = Entity.Create("person", "Cleo/Ä");

    /// <summary>Grill by Ana (packed), bread shared by Ben and Cleo, drinks by everyone (Ben packed), plates by nobody.</summary>
    private static (TripSnapshot Trip, Entity Camping) Trip()
    {
        var list = PackingLists.CreateList("Barbecue");
        (var grill, list) = PackingLists.AddItem(list, "Grill", "Cooking", "1", [Ana.Id]);
        (var bread, list) = PackingLists.AddItem(list, "Bread", "Food", null, [Ben.Id, Cleo.Id]);
        (var drinks, list) = PackingLists.AddItem(list, "Drinks, cold", "Food", "2 l each", everyone: true);
        (var plates, list) = PackingLists.AddItem(list, "=Plates");
        var camping = PackingLists.CreateList("Camping");
        (var tent, camping) = PackingLists.AddItem(camping, "Tent", assignees: [Ben.Id]);
        var trip = new TripSnapshot(Entity.CreateTrip("Lake weekend", "en", "Europe/Berlin"), new[] { Ana, Ben, Cleo, list, grill, bread, drinks, plates, camping, tent }.ToDictionary(e => e.Id));
        grill = PackingLists.SetPacked(trip, grill, Ana.Id, true); drinks = PackingLists.SetPacked(trip, drinks, Ben.Id, true);
        return (trip with { Entities = trip.Entities.Values.Select(e => e.Id == grill.Id ? grill : e.Id == drinks.Id ? drinks : e).ToDictionary(e => e.Id) }, camping);
    }

    [Fact]
    public void SectionsShowWhoBringsWhatAndWhatIsDone()
    {
        var (trip, _) = Trip();
        var sections = PackingExport.Sections(trip, new());
        Assert.Equal(["Barbecue", "Camping"], sections.Select(s => s.Title));
        var rows = sections[0].Rows;
        Assert.Equal(["Cooking", "Grill", "Food", "Bread", "Drinks, cold", "Other", "=Plates"], rows.Select(r => r.Title));
        Assert.True(rows[1].Checked); Assert.Equal("Ana", rows[1].Summary);
        Assert.Equal("Everyone · packed by Ben", rows[4].Summary); Assert.False(rows[4].Checked);
        Assert.Equal("Nobody yet", rows[6].Summary);
    }

    [Fact]
    public void OnePersonsListHasTheirItemsAndTheirPackedState()
    {
        var (trip, _) = Trip();
        var ben = PackingExport.Sections(trip, new() { Person = Ben.Id });
        Assert.Equal(["Bread", "Drinks, cold", "Tent"], ben.SelectMany(s => s.Rows).Where(r => r.Kind == ExportRowKind.Check).Select(r => r.Title));
        Assert.Equal("with Cleo/Ä", ben[0].Rows.Single(r => r.Title == "Bread").Summary);
        Assert.True(ben[0].Rows.Single(r => r.Title == "Drinks, cold").Checked);
        Assert.Equal(["=Plates"], PackingExport.Sections(trip, new() { UnassignedOnly = true }).Single().Rows.Select(r => r.Title));
        Assert.Equal(["Ana", "Ben", "Cleo/Ä"], PackingExport.People(trip).Select(p => p.Title));
    }

    [Fact]
    public void TextHasCheckBoxesForMessengers()
    {
        var (trip, camping) = Trip();
        var text = PackingExport.Text(trip, new() { Person = Ana.Id });
        Assert.StartsWith("Lake weekend\nPacking list for Ana\n", text);
        Assert.Contains("☑ Grill (1)\n", text); Assert.Contains("☐ Drinks, cold (2 l each) · Everyone\n", text);
        Assert.DoesNotContain("Camping", text);
        Assert.Contains("☐ Tent · Ben", PackingExport.Text(trip, new() { Lists = [camping.Id] }));
    }

    [Fact]
    public void CsvUsesTheCultureSeparatorQuotesAndGuardsFormulas()
    {
        var (trip, _) = Trip();
        var lines = PackingExport.Csv(trip, culture: English).Split("\r\n");
        Assert.Equal("List,Group,Item,Quantity,Brought by,Packed,Done", lines[0]);
        Assert.Equal("Barbecue,Food,\"Drinks, cold\",2 l each,Everyone,Ben,no", lines[3]);
        Assert.Equal("Barbecue,,'=Plates,,,,no", lines[4]);
        var german = PackingExport.Csv(trip, new() { Person = Cleo.Id }, culture: new CultureInfo("de-DE")).Split("\r\n");
        Assert.Equal("List;Group;Item;Quantity;Brought by;Packed", german[0]);
        Assert.Equal("Barbecue;Food;Bread;;Ben, Cleo/Ä;no", german[1]);
    }

    [Fact]
    public void ListsAndItemsCarryTheirCommentsWhenAsked()
    {
        var (trip, camping) = Trip();
        var tent = PackingLists.All(trip).Single(l => l.Collection.Id == camping.Id).Items.Single().Task;
        trip = trip with { Entities = trip.Entities.Values.Concat([ExportTests.Comment(tent, Ana, "2027-03-01T10:00:00Z", "Mine has a broken pole"), ExportTests.Comment(camping, Ben, "2027-03-02T10:00:00Z", "Pack by Friday")]).ToDictionary(e => e.Id) };
        Assert.All(PackingExport.Sections(trip, new()), s => { Assert.Empty(s.Comments); Assert.All(s.Rows, r => Assert.Empty(r.Comments)); });
        var section = PackingExport.Sections(trip, new() { Comments = true }, culture: English).Single(s => s.Title == "Camping");
        Assert.Equal(("Ben", "Pack by Friday"), (section.Comments.Single().Author, section.Comments.Single().Lines.Single()));
        Assert.Equal("Ana", section.Rows.Single().Comments.Single().Author);
        var text = PackingExport.Text(trip, new() { Lists = [camping.Id], Comments = true }, culture: English);
        Assert.Contains("Camping\n» Ben, 2 Mar 2027: Pack by Friday\n☐ Tent · Ben\n  » Ana, 1 Mar 2027: Mine has a broken pole\n", text);
    }

    [Fact]
    public void FileNamesAreSafeForEverySystem()
    {
        Assert.Equal("Lake weekend - Cleo_Ä.pdf", PackingExport.FileName("Lake weekend", Cleo, "pdf"));
        Assert.Equal("Packing_ day.csv", PackingExport.FileName("Packing: day.", null, ".csv"));
        Assert.Equal("export.txt", PackingExport.FileName("...", null, "txt"));
    }
}
