using System.Text;
using System.Text.Json.Nodes;
using NodaTime;
using TravelRepo.Core;
using TravelRepo.Repository;
using Xunit;
namespace TravelRepo.Tests;

public sealed class SearchTests
{
    private static string Samples()
    { var d = new DirectoryInfo(AppContext.BaseDirectory); while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "samples"))) d = d.Parent; return Path.Combine(d!.FullName, "samples"); }
    private static async Task<TripSnapshot> Sample(string name) => (await new TravelRepository(Path.Combine(Samples(), name)).ReadAsync()).Trip;

    private static JsonObject Exact(string start, string zone = "Asia/Tokyo") => new() { ["precision"] = "exact", ["start"] = new ZonedTime(start, zone).ToJson(), ["end"] = null };

    /// <summary>A small Tokyo trip with a temple visit, a flight with a booking, costs, tasks, packing, a note and a file.</summary>
    private static (TripSnapshot Trip, Dictionary<string, Entity> Named) Tokyo()
    {
        var all = new Dictionary<string, Entity>();
        Entity Add(string key, Entity e) { all[key] = e; return e; }
        var manifest = Entity.CreateTrip("Tokyo", "en", "Asia/Tokyo");
        var alex = Add("alex", Entity.Create("person", "Alex")); var mira = Add("mira", Entity.Create("person", "Mira"));
        manifest.Data["participants"] = new JsonArray(alex.Id.ToString(), mira.Id.ToString());
        var sensoji = Add("sensoji", Entity.Create("place", "Sensō-ji")); sensoji.Data["local_name"] = "浅草寺"; sensoji.Data["address"] = new JsonObject { ["formatted"] = "2-3-1 Asakusa, Taito City, Tokyo" };
        var haneda = Add("haneda", Entity.Create("place", "Tokyo Haneda")); var frankfurt = Add("frankfurt", Entity.Create("place", "Frankfurt Airport"));
        var temple = Add("temple", Entity.Create("schedule_item", "Tempel und Altstadt")); temple.Data["time"] = Exact("2027-05-14T09:30:00"); temple.Data["default_place"] = sensoji.Id.ToString(); temple.Data["category"] = "culture";
        temple.Data["participants"] = new JsonObject { ["inherit"] = false, ["values"] = new JsonArray(alex.Id.ToString()) };
        temple.Data["body"] = "Go early.\n\n- Bring **coins** for the fortune slips, the omikuji, which cost a hundred yen each and are worth it even when the fortune is bad\n";
        var flight = Add("flight", Entity.Create("schedule_item", "Flight to Tokyo")); flight.Data["time"] = Exact("2027-05-12T13:20:00", "Europe/Berlin");
        flight.Data["components"] = new JsonObject { ["transport"] = new JsonObject { ["type"] = "flight", ["carrier"] = "ANA", ["flight_number"] = "NH204", ["departure"] = new JsonObject { ["place"] = frankfurt.Id.ToString() }, ["arrival"] = new JsonObject { ["place"] = haneda.Id.ToString() } } };
        var dinner = Add("dinner", Entity.Create("schedule_item", "Ramen in Shinjuku")); dinner.Data["time"] = Exact("2027-05-14T19:00:00"); dinner.Data["category"] = "food";
        var idea = Add("idea", Entity.Create("schedule_item", "Temple market"));
        var booking = Add("booking", Entity.Create("booking", "Flights")); booking.Data["provider"] = new JsonObject { ["name"] = "All Nippon Airways" }; booking.Data["reference"] = "GR-1187"; booking.Data["items"] = new JsonArray(flight.Id.ToString());
        var cost = Add("cost", Entity.Create("expense", "Tickets")); cost.Data["amount"] = new JsonObject { ["value"] = "1749.80", ["currency"] = "EUR" }; cost.Data["paid_by"] = new JsonArray(mira.Id.ToString());
        cost.Data["related"] = new JsonArray(new JsonObject { ["type"] = "booking", ["id"] = booking.Id.ToString() });
        var task = Add("task", Entity.Create("task", "Renew passport")); task.Data["due"] = new JsonObject { ["date"] = "2027-04-01" }; task.Data["assigned_to"] = new JsonArray(mira.Id.ToString());
        var list = Add("list", PackingLists.CreateList("Hand luggage"));
        var (adapter, updated) = PackingLists.AddItem(list, "Travel adapter", group: "Electronics"); all["list"] = updated; Add("adapter", adapter);
        var note = Add("note", Entity.Create("note", "Useful phrases")); note.Data["content"] = new JsonArray(new JsonObject { ["type"] = "markdown", ["file"] = "notes/phrases.md" });
        var file = Add("file", Entity.Create("document", "boarding-pass.pdf"));
        var trip = new TripSnapshot(manifest, all.Values.ToDictionary(e => e.Id)) { Resources = new Dictionary<string, byte[]> { ["notes/phrases.md"] = Encoding.UTF8.GetBytes("# Phrases\n\nSumimasen means excuse me.\n") } };
        return (trip, all);
    }

    private static readonly SearchOptions WithNotes = new() { Notes = true };

    [Fact]
    public void TheTempleVisitIsFoundWithItsDay()
    {
        var (trip, named) = Tokyo();
        var hits = TripSearch.Find(trip, "temp");
        Assert.Equal(named["temple"].Id, hits[0].Entity.Id);
        Assert.Equal(SearchKind.Plan, hits[0].Kind);
        Assert.Equal(new LocalDate(2027, 5, 14), hits[0].Day);
        Assert.Equal("Asia/Tokyo", hits[0].Zone);
        Assert.Equal(Instant.FromUtc(2027, 5, 14, 0, 30), hits[0].Start);
        // The part that was typed is marked in the title.
        Assert.Equal([new TextRange(0, 4)], hits[0].TitleRanges);
        Assert.Contains(hits, h => h.Entity.Id == named["idea"].Id && h.Day is null);
    }

    [Fact]
    public void MatchingIgnoresCaseAndAccentsAndAllowsOneTypoInLongerWords()
    {
        var (trip, named) = Tokyo();
        // A letter left out; the word that really starts like this comes first.
        Assert.Equal([named["idea"].Id, named["temple"].Id], TripSearch.Find(trip, "Templ").Select(h => h.Entity.Id));
        Assert.Equal(named["temple"].Id, TripSearch.Find(trip, "TEMPLE und")[0].Entity.Id);    // one typo, and every word
        Assert.Contains(TripSearch.Find(trip, "pasport"), h => h.Entity.Id == named["task"].Id);
        Assert.Contains(TripSearch.Find(trip, "passprot"), h => h.Entity.Id == named["task"].Id); // two neighbours swapped
        Assert.Empty(TripSearch.Find(trip, "pxsprot"));
        // Short words are not guessed at.
        Assert.DoesNotContain(TripSearch.Find(trip, "rxmen"), h => h.Entity.Id == named["temple"].Id);
        Assert.Empty(TripSearch.Find(trip, "tmp"));
        var place = Assert.Single(TripSearch.Find(trip, "senso ji", new SearchOptions { Kinds = new HashSet<SearchKind> { SearchKind.Place } }));
        Assert.Equal(named["sensoji"].Id, place.Entity.Id);
        Assert.Contains(TripSearch.Find(trip, "浅草"), h => h.Entity.Id == named["sensoji"].Id);
    }

    [Fact]
    public void EveryWordMustBeFoundAndFieldsSayWhere()
    {
        var (trip, named) = Tokyo();
        Assert.Empty(TripSearch.Find(trip, "temple zebra"));
        Assert.Empty(TripSearch.Find(trip, "   "));
        Assert.Empty(TripSearch.Find(trip, null));
        // A plan is found by its place, and says so.
        var byPlace = TripSearch.Find(trip, "sensoji asakusa").Single(h => h.Kind == SearchKind.Plan);
        Assert.Equal(named["temple"].Id, byPlace.Entity.Id);
        Assert.Empty(byPlace.TitleRanges);
        Assert.Equal(SearchField.Place, byPlace.Elsewhere!.Field);
        Assert.Equal("Sensō-ji", byPlace.Elsewhere.Text);
        // The booking reference finds the booking and the flight it is for; the booking is first, since it is its own number.
        var byReference = TripSearch.Find(trip, "GR-1187");
        Assert.Equal([named["booking"].Id, named["flight"].Id], byReference.Select(h => h.Entity.Id));
        Assert.Equal(SearchField.Reference, byReference[0].Elsewhere!.Field);
        Assert.Equal([new TextRange(0, 2), new TextRange(3, 4)], byReference[0].Elsewhere!.Ranges);
        Assert.Equal(new LocalDate(2027, 5, 12), byReference[0].Day);
        Assert.Equal(SearchField.Number, TripSearch.Find(trip, "nh204").Single().Elsewhere!.Field);
        Assert.Equal(SearchField.From, TripSearch.Find(trip, "frankfurt").Single(h => h.Kind == SearchKind.Plan).Elsewhere!.Field);
        // Costs: by amount, by who paid and by what they belong to.
        Assert.Equal(named["cost"].Id, TripSearch.Find(trip, "1749").Single().Entity.Id);
        Assert.Contains(TripSearch.Find(trip, "mira"), h => h.Kind == SearchKind.Cost && h.Elsewhere!.Field == SearchField.People);
        Assert.Contains(TripSearch.Find(trip, "flights"), h => h.Kind == SearchKind.Cost && h.Elsewhere!.Field == SearchField.BelongsTo);
        // Packing: by list and group. Files by name.
        Assert.Equal(SearchField.Group, TripSearch.Find(trip, "electronics").Single().Elsewhere!.Field);
        Assert.Equal([SearchKind.Packing, SearchKind.Packing], TripSearch.Find(trip, "luggage").Select(h => h.Kind));
        Assert.Equal(SearchKind.File, TripSearch.Find(trip, "boarding").Single().Kind);
        Assert.Equal(new LocalDate(2027, 4, 1), TripSearch.Find(trip, "passport").Single().Day);
    }

    [Fact]
    public void TitlesComeBeforePlacesAndNumbersBeforeNotes()
    {
        var (trip, named) = Tokyo();
        var hits = TripSearch.Find(trip, "tokyo", WithNotes);
        // Title first, then a place of its own, then plans at that place or address.
        Assert.Equal(named["flight"].Id, hits[0].Entity.Id);
        Assert.True(hits.Select(h => h.Score).SequenceEqual(hits.Select(h => h.Score).OrderDescending()));
        var address = hits.Single(h => h.Entity.Id == named["temple"].Id);
        Assert.True(address.Score < hits.Single(h => h.Entity.Id == named["haneda"].Id).Score);
    }

    [Fact]
    public void EqualMatchesAreOrderedByDayThenKindSoTheOrderIsStable()
    {
        // A stay, its booking and its place share a title; whichever was created first, the plan leads.
        for (var round = 0; round < 2; round++)
        {
            var plan = Entity.Create("schedule_item", "Hotel Gracery"); plan.Data["time"] = Exact("2027-05-15T15:00:00");
            var booking = Entity.Create("booking", "Hotel Gracery"); booking.Data["items"] = new JsonArray(plan.Id.ToString());
            var place = Entity.Create("place", "Hotel Gracery");
            Entity[] all = round == 0 ? [plan, booking, place] : [place, booking, plan];
            var trip = new TripSnapshot(Entity.CreateTrip("Tokyo", "en", "Asia/Tokyo"), all.ToDictionary(e => e.Id));
            Assert.Equal([SearchKind.Plan, SearchKind.Booking, SearchKind.Place], TripSearch.Find(trip, "gracery").Select(h => h.Kind));
        }
    }

    [Fact]
    public void NoteTextsAreOnlySearchedWhenAsked()
    {
        var (trip, named) = Tokyo();
        Assert.Empty(TripSearch.Find(trip, "omikuji"));
        Assert.Empty(TripSearch.Find(trip, "sumimasen"));
        // The title of a note is always searched.
        Assert.Equal(named["note"].Id, TripSearch.Find(trip, "phrases").Single().Entity.Id);
        var inFile = TripSearch.Find(trip, "sumimasen", WithNotes).Single();
        Assert.Equal(SearchKind.Note, inFile.Kind);
        Assert.Equal("Sumimasen means excuse me.", inFile.Elsewhere!.Text);
        // A long line is shortened around the word, and the mark still points at it.
        var inBody = TripSearch.Find(trip, "omikuji", WithNotes).Single();
        Assert.Equal(named["temple"].Id, inBody.Entity.Id);
        var match = inBody.Elsewhere!; var range = Assert.Single(match.Ranges);
        Assert.Equal(SearchField.Notes, match.Field);
        Assert.StartsWith("…", match.Text); Assert.EndsWith("…", match.Text);
        Assert.Equal("omikuji", match.Text.Substring(range.Start, range.Length));
        Assert.DoesNotContain("**", TripSearch.Find(trip, "coins", WithNotes).Single().Elsewhere!.Text);
    }

    [Fact]
    public void CategoriesAreSearchedInTheClientsLanguage()
    {
        var (trip, named) = Tokyo();
        Assert.Contains(TripSearch.Find(trip, "culture"), h => h.Entity.Id == named["temple"].Id);
        var german = new SearchLabels(new Dictionary<ScheduleKind, string> { [ScheduleKind.Culture] = "Kultur", [ScheduleKind.Food] = "Essen" }, new Dictionary<string, string> { [ExpenseCategories.Transport] = "Transport", [ExpenseCategories.Food] = "Essen" });
        Assert.Equal(named["temple"].Id, TripSearch.Find(trip, "Kultur", labels: german).Single().Entity.Id);
        Assert.Equal(named["dinner"].Id, TripSearch.Find(trip, "essen", labels: german).Single().Entity.Id);
        // The expense for the flights takes its category from the booking's flight.
        Assert.Contains(TripSearch.Find(trip, "transport", labels: german), h => h.Entity.Id == named["cost"].Id && h.Elsewhere!.Field == SearchField.Category);
    }

    [Fact]
    public void TheIndexIsKeptPerSnapshotAndLabels()
    {
        var (trip, _) = Tokyo();
        var labels = new SearchLabels(new Dictionary<ScheduleKind, string>(), new Dictionary<string, string>());
        Assert.Same(TripSearch.For(trip), TripSearch.For(trip));
        Assert.NotSame(TripSearch.For(trip), TripSearch.For(trip, labels));
        Assert.Same(TripSearch.For(trip, labels), TripSearch.For(trip, labels));
        var other = new TripSnapshot(trip.Manifest, trip.Entities);
        Assert.NotSame(TripSearch.For(trip, labels), TripSearch.For(other, labels));
    }

    [Fact]
    public void DuringTheTripTodayComesFirstThenTheComingDaysThenThePast()
    {
        var manifest = Entity.CreateTrip("Tokyo", "en", "Asia/Tokyo");
        var items = Enumerable.Range(12, 5).Select(day => { var e = Entity.Create("schedule_item", "Walk " + day); e.Data["time"] = Exact("2027-05-" + day + "T10:00:00"); return e; }).ToList();
        items.Add(Entity.Create("schedule_item", "Walk someday"));
        var trip = new TripSnapshot(manifest, items.ToDictionary(e => e.Id));
        var hits = TripSearch.Find(trip, "walk");
        LocalDate? Day(int d) => new LocalDate(2027, 5, d);
        Assert.Equal([Day(12), Day(13), Day(14), Day(15), Day(16), null], TripSearch.ByDay(trip, hits).Select(g => g.Day));
        Assert.Equal([Day(14), Day(15), Day(16), Day(12), Day(13), null], TripSearch.ByDay(trip, hits, new LocalDate(2027, 5, 14)).Select(g => g.Day));
        // Before and after the trip the days are in order.
        Assert.Equal([Day(12), Day(13), Day(14), Day(15), Day(16), null], TripSearch.ByDay(trip, hits, new LocalDate(2027, 6, 1)).Select(g => g.Day));
        Assert.Equal("Walk someday", TripSearch.ByDay(trip, hits)[^1].Hits.Single().Entity.Title);
    }

    [Fact]
    public async Task TheConformanceFixturesAreSearchable()
    {
        var tokyo = await Sample("tokyo");
        var flight = TripSearch.Find(tokyo, "flight nh204").Single();
        Assert.Equal(new LocalDate(2027, 5, 12), flight.Day);
        Assert.Equal("Europe/Berlin", flight.Zone);
        Assert.Contains(TripSearch.Find(await Sample("hamburg"), "hambrg"), h => h.Kind == SearchKind.Plan);
        var zealand = await Sample("new-zealand");
        // Macrons: typed without, found with.
        Assert.Equal("Ōtautahi", TripSearch.Find(zealand, "Otautahi").Single().Entity.Title);
        Assert.Equal("Pōneke", TripSearch.Find(zealand, "poneke").Single().Entity.Title);
        Assert.Equal([new TextRange(0, 6)], TripSearch.Find(zealand, "PONEKE").Single().TitleRanges);
        Assert.Contains(TripSearch.Find(zealand, "coastal"), h => h.Day is null);
        // Every fixture can be indexed, the ones with unknown extensions and nested items included.
        foreach (var manifest in Directory.EnumerateFiles(Samples(), "travel.yaml", SearchOption.AllDirectories))
        {
            var trip = (await new TravelRepository(Path.GetDirectoryName(manifest)!).ReadAsync()).Trip;
            Assert.NotNull(TripSearch.Find(trip, "a", WithNotes));
        }
    }

    [Fact]
    public void UnknownFieldsAndOddShapesAreIgnored()
    {
        var item = Entity.Create("schedule_item", "Harbour walk");
        item.Data["extensions"] = new JsonObject { ["org.example"] = new JsonObject { ["secret"] = "lighthouse" } };
        item.Data["future_field"] = new JsonArray("lighthouse");
        item.Data["time"] = new JsonObject { ["precision"] = "exact", ["start"] = "not a time" };
        item.Data["components"] = new JsonObject { ["transport"] = new JsonObject { ["carrier"] = new JsonArray("x"), ["flight_number"] = 204 } };
        var expense = Entity.Create("expense", "Harbour tour"); expense.Data["amount"] = "a lot"; expense.Data["paid_by"] = "nobody"; expense.Data["related"] = new JsonObject();
        var trip = new TripSnapshot(Entity.CreateTrip("Odd"), new[] { item, expense }.ToDictionary(e => e.Id));
        Assert.Empty(TripSearch.Find(trip, "lighthouse", WithNotes));
        Assert.Equal(2, TripSearch.Find(trip, "harbour").Count);
        Assert.Equal(item.Id, TripSearch.Find(trip, "204").Single().Entity.Id);
    }

    [Fact]
    public void NormalizedWordsAreStableAndFindWhatTheyCameFrom()
    {
        Assert.Equal("otautahi", TripSearch.Normalize("Ōtautahi"));
        Assert.Equal("strasse", TripSearch.Normalize("Straße"));
        Assert.Equal("smorrebrod", TripSearch.Normalize("Smørrebrød"));
        Assert.Equal(["senso", "ji", "2", "3", "1"], TripSearch.Words("Sensō-ji, 2-3-1"));
        var random = new Random(20261116); const string alphabet = "aāeéöüßøłZK 東京寺-_.,:'’/()0123456789́";
        for (var i = 0; i < 400; i++)
        {
            var text = new string(Enumerable.Range(0, random.Next(1, 20)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            var once = TripSearch.Normalize(text);
            // Normalizing again changes nothing, and nothing upper case or accented is left.
            Assert.Equal(once, TripSearch.Normalize(once));
            Assert.Equal(once, once.ToLowerInvariant());
            // A title is found by its own words, and every range lies inside the title.
            var words = TripSearch.Words(text);
            var place = Entity.Create("place", text);
            var trip = new TripSnapshot(Entity.CreateTrip("Random"), new[] { place }.ToDictionary(e => e.Id));
            if (words.Count == 0) { Assert.Empty(TripSearch.Find(trip, text)); continue; }
            var hit = Assert.Single(TripSearch.Find(trip, text));
            Assert.All(hit.TitleRanges, r => Assert.True(r.Start >= 0 && r.Length > 0 && r.Start + r.Length <= text.Length));
            Assert.Single(TripSearch.Find(trip, string.Join(" ", words.Reverse())));
        }
    }

    [Theory]
    [InlineData("Souvenirs", "souvenirs")]
    [InlineData("suveniers für Oma", "souvenirs")]
    [InlineData("Taxifahrt zum Hotel", "transport")]
    [InlineData("Fahrt nach Kyoto", "transport")]
    [InlineData("Zugfahrt", "transport")]
    [InlineData("U-Bahn", "transport")]
    [InlineData("Restaurant Gonpachi", "food")]
    [InlineData("Frühstück im Hotel", "food")]
    [InlineData("Abendessen", "food")]
    [InlineData("Hotel Gracery", "accommodation")]
    [InlineData("Übernachtung Ryokan", "accommodation")]
    [InlineData("Eintritt teamLab", "activity")]
    [InlineData("Museum", "activity")]
    [InlineData("Bargeld", null)]
    [InlineData("Business lounge", null)]
    [InlineData("Eisenbahnmuseum", "activity")]
    [InlineData("Sonstiges", null)]
    [InlineData("", null)]
    public void TheTitleOfAnExpenseSuggestsItsCategory(string title, string? expected) => Assert.Equal(expected, ExpenseCategories.SuggestFromTitle(title));

    [Fact]
    public void AnEarlierExpenseWithTheSameTitleSuggestsItsCategoryFirst()
    {
        var earlier = Entity.Create("expense", "Konbini"); earlier.Data["category"] = "Snacks unterwegs";
        var other = Entity.Create("expense", "Taxi");
        var trip = new TripSnapshot(Entity.CreateTrip("Tokyo"), new[] { earlier, other }.ToDictionary(e => e.Id));
        Assert.Equal("Snacks unterwegs", ExpenseCategories.SuggestFromTitle("konbini", trip));
        Assert.Equal("food", ExpenseCategories.SuggestFromTitle("konbini"));
        // An expense without a stored category suggests nothing by itself; the words still do.
        Assert.Equal("transport", ExpenseCategories.SuggestFromTitle("Taxi", trip));
        Assert.Null(ExpenseCategories.SuggestFromTitle(null, trip));
    }
}
