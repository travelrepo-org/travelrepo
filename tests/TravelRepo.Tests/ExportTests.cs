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
    public void PersonalCostsShowWhatThatPersonPaid()
    {
        Entity Expense(string title, string value, Entity payer) { var e = Entity.Create("expense", title); e.Data["amount"] = new JsonObject { ["value"] = value, ["currency"] = "JPY" }; e.Data["paid_by"] = new JsonArray(payer.Id.ToString()); return e; }
        var budget = Entity.Create("budget", "Food"); budget.Data["amount"] = new JsonObject { ["value"] = "50000", ["currency"] = "JPY" };
        var trip = Trip(Expense("Ramen", "2400", Mia), Expense("Taxi", "3000", Noah), budget);
        Assert.Equal(5400m, TripItinerary.Build(trip, culture: Invariant).Costs.Single().Paid);
        var mine = TripItinerary.Build(trip, culture: Invariant, options: new ExportOptions { Person = Mia.Id }).Costs.Single();
        Assert.Equal(2400m, mine.Paid); Assert.Equal(0m, mine.Budget);
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
    public void FileNamesAreSafeForEverySystem()
    {
        Assert.Equal("Lake weekend - Cleo_Ä.pdf", PackingExport.FileName("Lake weekend", Cleo, "pdf"));
        Assert.Equal("Packing_ day.csv", PackingExport.FileName("Packing: day.", null, ".csv"));
        Assert.Equal("export.txt", PackingExport.FileName("...", null, "txt"));
    }
}
