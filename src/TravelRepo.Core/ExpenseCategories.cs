using System.Text.Json.Nodes;

namespace TravelRepo.Core;

/// <summary>What expenses in one category come to in one currency; <see cref="Estimated"/> when reference rates were used.</summary>
public sealed record CategoryTotal(string Category, string Currency, decimal Amount, bool Estimated);

/// <summary>
/// Categories of expenses (specification section 34.2): a few standard ones, and names the travellers choose
/// themselves. An expense without a category takes one from the plan or booking it belongs to.
/// </summary>
public static class ExpenseCategories
{
    public const string Transport = "transport", Accommodation = "accommodation", Food = "food", Activity = "activity", Souvenirs = "souvenirs", Other = "other";

    /// <summary>The standard categories in the order clients offer them. Clients translate them; other values are names as typed.</summary>
    public static readonly IReadOnlyList<string> Standard = [Transport, Accommodation, Food, Activity, Souvenirs, Other];

    public static bool IsStandard(string category) => Standard.Contains(category);

    /// <summary>The category stored on the expense, or null.</summary>
    public static string? Stored(Entity expense) => expense.Data["category"] is JsonValue v && v.ToString().Trim() is { Length: > 0 } text ? text : null;

    /// <summary>
    /// The category of an expense: the stored one, otherwise the one its plan or booking suggests (see
    /// <see cref="Suggested"/>), otherwise <see cref="Other"/>.
    /// </summary>
    public static string Of(TripSnapshot trip, Entity expense) => Stored(expense) ?? Suggested(trip, expense) ?? Other;

    /// <summary>
    /// The category the plans an expense belongs to suggest, directly or through a booking: a journey is transport, a
    /// stay accommodation, food is food, shopping souvenirs, and activities, sights, culture, nature, nightlife and free
    /// time are activities. Null when nothing suggests one.
    /// </summary>
    public static string? Suggested(TripSnapshot trip, Entity expense)
    {
        foreach (var related in (expense.Data["related"] as JsonArray ?? []).Select(r => Guid.TryParse(r?["id"]?.ToString(), out var id) ? trip.Find(id) : null).OfType<Entity>())
        {
            var items = related.Type == "booking" ? TripCommands.BookedItems(trip, related.Id).Select(trip.Find).OfType<Entity>() : related.Type == "schedule_item" ? [related] : [];
            foreach (var item in items)
                if (FromKind(ScheduleCategories.Classify(trip, item)) is { } category) return category;
        }
        return null;
    }

    /// <summary>
    /// Words in the title of an expense that say what it most likely is, in English and German, compared as the
    /// search compares words (<see cref="TripSearch.Normalize"/>). Words of up to three letters must be the whole
    /// word, longer ones may begin it, and those of five letters or more may be anywhere in it, as in "Zugfahrt".
    /// </summary>
    private static readonly (string Category, string[] Words)[] TitleWords =
    [
        (Transport, ["taxi", "uber", "lyft", "bus", "bahn", "zug", "train", "railway", "flug", "flight", "fahrt", "fahrkarte", "fahrschein", "metro", "subway", "tram", "strassenbahn", "faehre", "fahre", "ferry",
            "maut", "toll", "tanken", "tankstelle", "benzin", "diesel", "sprit", "fuel", "petrol", "gasoline", "parken", "parking", "parkhaus", "parkplatz", "parkgebuhr", "mietwagen", "rental", "leihwagen", "shuttle", "transfer",
            "shinkansen", "suica", "pasmo", "airline", "transport", "rikscha", "rickshaw", "tuktuk", "seilbahn", "gondel"]),
        (Accommodation, ["hotel", "hostel", "airbnb", "unterkunft", "ubernachtung", "ryokan", "apartment", "appartement", "ferienwohnung", "ferienhaus", "camping", "campingplatz", "campsite", "campground", "motel", "pension",
            "lodge", "accommodation", "zimmer", "room", "resort", "guesthouse", "gasthaus", "gasthof", "herberge", "minshuku", "kurtaxe"]),
        (Food, ["restaurant", "essen", "abendessen", "mittagessen", "fruhstuck", "breakfast", "lunch", "dinner", "brunch", "cafe", "kaffee", "coffee", "bar", "pub", "bier", "beer", "pizza", "pizzeria", "sushi", "ramen", "burger",
            "supermarkt", "supermarket", "groceries", "grocery", "lebensmittel", "backerei", "bakery", "snack", "snacks", "eis", "eiscreme", "icecream", "gelato", "drinks", "getranke", "wein", "wine", "imbiss", "doner", "kebab",
            "izakaya", "food", "meal", "picknick", "picnic", "tee", "tea", "kuchen", "cake", "nudeln", "noodles", "curry", "bistro", "kneipe", "cocktail", "cocktails", "wasser", "water", "konbini", "bento", "streetfood", "mensa", "kantine"]),
        (Activity, ["museum", "eintritt", "entrance", "admission", "tour", "fuhrung", "kino", "cinema", "theater", "theatre", "konzert", "concert", "zoo", "aquarium", "onsen", "spa", "sauna", "therme", "ski", "skipass",
            "tauchen", "diving", "schnorcheln", "snorkeling", "snorkelling", "kajak", "kayak", "kanu", "canoe", "wanderung", "hike", "hiking", "bootstour", "cruise", "tempel", "temple", "schrein", "shrine", "schloss", "castle", "burg",
            "show", "festival", "activity", "aktivitat", "ausflug", "excursion", "bungee", "bungy", "rafting", "surfen", "surfing", "klettern", "climbing", "schwimmbad", "freibad", "karaoke", "galerie", "gallery", "ausstellung",
            "exhibition", "nationalpark", "aussichtsplattform", "observatory", "massage", "minigolf", "bowling", "freizeitpark", "skydive", "jetboat", "rundflug", "whalewatching"]),
        (Souvenirs, ["souvenir", "souvenirs", "souvenier", "souveniers", "suvenir", "suvenirs", "suvenier", "suveniers", "mitbringsel", "geschenk", "geschenke", "gift", "gifts", "present", "presents", "andenken", "postkarte", "postkarten",
            "postcard", "postcards", "magnet", "shopping", "keramik", "ceramics"])
    ];

    /// <summary>
    /// The category the title of an expense suggests while it is typed, such as food for "Restaurant" or transport
    /// for "Taxifahrt": the category of an earlier expense of this trip with the same title, which includes the
    /// travellers' own categories, otherwise what a word of the title says in English or German. The first word that
    /// says something counts. Null when nothing does. It is a suggestion for clients to offer, never stored by itself.
    /// </summary>
    public static string? SuggestFromTitle(string? title, TripSnapshot? trip = null)
    {
        var words = TripSearch.Words(title);
        if (words.Count == 0) return null;
        if (trip is not null)
        {
            var same = trip.Entities.Values.Where(e => e.Type == "expense" && Stored(e) is not null && TripSearch.Words(e.Title).SequenceEqual(words, StringComparer.Ordinal))
                .GroupBy(e => Stored(e)!, StringComparer.Ordinal).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).FirstOrDefault();
            if (same is not null) return same.Key;
        }
        foreach (var word in words)
        {
            (string Category, int Length)? best = null;
            foreach (var (category, keys) in TitleWords)
                foreach (var key in keys)
                {
                    var found = key.Length <= 3 ? word == key : key.Length == 4 ? word.StartsWith(key, StringComparison.Ordinal) : word.Contains(key, StringComparison.Ordinal);
                    if (found && (best is null || key.Length > best.Value.Length)) best = (category, key.Length);
                }
            if (best is { } hit) return hit.Category;
        }
        return null;
    }

    /// <summary>The expense category for a kind of plan, or null for meetings and other plans.</summary>
    public static string? FromKind(ScheduleKind kind) => kind switch
    {
        ScheduleKind.Transport => Transport,
        ScheduleKind.Accommodation => Accommodation,
        ScheduleKind.Food => Food,
        ScheduleKind.Shopping => Souvenirs,
        ScheduleKind.Activity or ScheduleKind.Sightseeing or ScheduleKind.Culture or ScheduleKind.Nature or ScheduleKind.Nightlife or ScheduleKind.FreeTime => Activity,
        _ => null
    };

    /// <summary>The categories the travellers created in this trip: names used on expenses that are not standard, in alphabetical order.</summary>
    public static IReadOnlyList<string> Custom(TripSnapshot trip) => trip.Entities.Values.Where(e => e.Type == "expense").Select(Stored).OfType<string>()
        .Where(c => !IsStandard(c)).Distinct(StringComparer.CurrentCultureIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).ToArray();

    /// <summary>
    /// A copy of the expense in <paramref name="category"/>: a standard category or a name of the travellers' own,
    /// written like one already used in the trip when it differs only in case. Null or empty removes it, so the
    /// expense follows its plan or booking again.
    /// </summary>
    public static Entity With(TripSnapshot trip, Entity expense, string? category)
    {
        var copy = expense.Copy();
        var text = category?.Trim();
        if (string.IsNullOrEmpty(text)) { copy.Data.Remove("category"); return copy; }
        var standard = Standard.FirstOrDefault(s => string.Equals(s, text, StringComparison.OrdinalIgnoreCase));
        copy.Data["category"] = standard ?? Custom(trip).FirstOrDefault(c => string.Equals(c, text, StringComparison.CurrentCultureIgnoreCase)) ?? text;
        return copy;
    }

    /// <summary>
    /// What the paid expenses come to per category, largest first. With <paramref name="currency"/>, amounts are
    /// converted as in <see cref="Expenses.Convert"/>; what cannot be converted stays in its own currency. Estimates
    /// are left out. With <paramref name="people"/>, only their parts count, such as one traveller's or a couple's
    /// (section 34.1); otherwise every expense counts in full.
    /// </summary>
    public static IReadOnlyList<CategoryTotal> Totals(TripSnapshot trip, string? currency = null, ExchangeRates? rates = null, IReadOnlyCollection<Guid>? people = null)
    {
        var sums = new Dictionary<(string Category, string Currency), (decimal Amount, bool Estimated)>();
        foreach (var expense in trip.Entities.Values.Where(e => e.Type == "expense"))
        {
            if (TripSummaries.Money(expense.Data["amount"]) is not { } money || expense.Data["estimated"] is JsonValue flag && flag.TryGetValue<bool>(out var guess) && guess) continue;
            var value = money.Amount;
            if (people is { Count: > 0 })
            {
                var split = Expenses.Split(trip, expense);
                value = people.Distinct().Sum(split.ShareOf);
                if (value == 0) continue;
            }
            var (amount, code, how) = currency is null ? (value, money.Currency, Conversion.Same) : Expenses.Convert(expense, value, money.Currency, currency, rates);
            var key = (Of(trip, expense), code);
            var sum = sums.GetValueOrDefault(key);
            sums[key] = (sum.Amount + amount, sum.Estimated || how == Conversion.Reference);
        }
        return sums.Select(p => new CategoryTotal(p.Key.Category, p.Key.Currency, p.Value.Amount, p.Value.Estimated))
            .OrderBy(t => t.Currency, StringComparer.Ordinal).ThenByDescending(t => t.Amount).ThenBy(t => t.Category, StringComparer.Ordinal).ToArray();
    }
}
