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
    /// are left out.
    /// </summary>
    public static IReadOnlyList<CategoryTotal> Totals(TripSnapshot trip, string? currency = null, ExchangeRates? rates = null)
    {
        var sums = new Dictionary<(string Category, string Currency), (decimal Amount, bool Estimated)>();
        foreach (var expense in trip.Entities.Values.Where(e => e.Type == "expense"))
        {
            if (TripSummaries.Money(expense.Data["amount"]) is not { } money || expense.Data["estimated"] is JsonValue flag && flag.TryGetValue<bool>(out var guess) && guess) continue;
            var (amount, code, how) = currency is null ? (money.Amount, money.Currency, Conversion.Same) : Expenses.Convert(expense, money.Amount, money.Currency, currency, rates);
            var key = (Of(trip, expense), code);
            var sum = sums.GetValueOrDefault(key);
            sums[key] = (sum.Amount + amount, sum.Estimated || how == Conversion.Reference);
        }
        return sums.Select(p => new CategoryTotal(p.Key.Category, p.Key.Currency, p.Value.Amount, p.Value.Estimated))
            .OrderBy(t => t.Currency, StringComparer.Ordinal).ThenByDescending(t => t.Amount).ThenBy(t => t.Category, StringComparer.Ordinal).ToArray();
    }
}
