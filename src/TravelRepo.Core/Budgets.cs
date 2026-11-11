using System.Text.Json.Nodes;

namespace TravelRepo.Core;

/// <summary>
/// Where a budget stands (specification section 36): what the expenses it covers came to in its currency,
/// <see cref="Spent"/> for paid ones and <see cref="Planned"/> for estimates. <see cref="Approximate"/> when reference
/// rates were used; <see cref="NotIncluded"/> are amounts that could not be converted at all.
/// </summary>
public sealed record BudgetStatus(Entity Budget, string Scope, string? Target, Money Amount, decimal Spent, decimal Planned, bool Approximate, IReadOnlyList<Money> NotIncluded)
{
    /// <summary>Everything the budget is limited to; all of it applies together.</summary>
    public BudgetFilters Filters { get; init; } = BudgetFilters.None;
    public decimal Used => Spent + Planned;
    /// <summary>What is left of the budget; negative when it is exceeded.</summary>
    public decimal Left => Amount.Amount - Used;
}

/// <summary>
/// The trip's budget in the trip's currency: <see cref="Amount"/> from every budget without limits (approximate where
/// one is in another currency, <see cref="AmountNotConverted"/> where it cannot be converted), and the paid and planned
/// expenses converted the same way. When no budget converts to the trip's currency, for example after the currency
/// changed and before rates are known, <see cref="Currency"/> is the budget's own currency instead.
/// </summary>
public sealed record TripBudgetStatus(string Currency, decimal Amount, bool AmountApproximate, IReadOnlyList<Money> AmountNotConverted, IReadOnlyList<Entity> Budgets, ConvertedTotal Paid, ConvertedTotal Planned)
{
    /// <summary>What payments for ideas would cost; shown apart, not part of <see cref="Used"/>.</summary>
    public ConvertedTotal? Ideas { get; init; }
    public decimal Used => Paid.Total + Planned.Total;
    /// <summary>What is left; negative when the budget is exceeded.</summary>
    public decimal Left => Amount - Used;
    /// <summary>Whether reference rates were used anywhere, so the numbers are estimates.</summary>
    public bool Approximate => AmountApproximate || Paid.Estimated != 0 || Planned.Estimated != 0;
}

/// <summary>
/// What a budget is limited to: an expense category, a person's part, a collection, or several of them together, such
/// as one person's food. Null means not limited by that.
/// </summary>
public sealed record BudgetFilters(string? Category, Guid? Person, Guid? Collection)
{
    public static readonly BudgetFilters None = new(null, null, null);
    public bool IsWholeTrip => Category is null && Person is null && Collection is null;
}

/// <summary>
/// Budgets for the whole trip, one expense category, one person's part, or a collection of plans. A budget counts the
/// expenses it covers in its own currency: exactly where amounts are in it or were charged in it, otherwise with
/// reference rates as an estimate.
/// </summary>
public static class Budgets
{
    public const string Trip = "trip", Category = "category", Participant = "participant", Collection = "collection";

    /// <summary>The scopes in the order clients offer them.</summary>
    public static readonly IReadOnlyList<string> Scopes = [Trip, Category, Participant, Collection];

    /// <summary>
    /// The narrowest limit of a budget, for an icon or a heading: a person, otherwise a category, otherwise a
    /// collection, otherwise the whole trip. A stored <c>scope</c> only repeated the limits and is ignored.
    /// </summary>
    public static string ScopeOf(Entity budget) => FiltersOf(budget) switch
    {
        { Person: not null } => Participant,
        { Category: not null } => Category,
        { Collection: not null } => Collection,
        _ => Trip
    };

    /// <summary>What a limited budget is for: the person's or collection's id, or the category; null for the whole trip.</summary>
    public static string? TargetOf(Entity budget) => FiltersOf(budget) switch
    {
        { Person: { } p } => p.ToString(),
        { Category: { } c } => c,
        { Collection: { } k } => k.ToString(),
        _ => null
    };

    /// <summary>
    /// What a budget is limited to. Its <c>category</c>, <c>person</c> and <c>collection</c> apply together; without
    /// any of them it covers the whole trip.
    /// </summary>
    public static BudgetFilters FiltersOf(Entity budget) => new(
        budget.Data["category"]?.ToString() is { Length: > 0 } c ? c : null,
        Guid.TryParse(budget.Data["person"]?.ToString(), out var p) ? p : null,
        Guid.TryParse(budget.Data["collection"]?.ToString(), out var k) ? k : null);

    /// <summary>
    /// A copy of the budget limited to everything given together, such as one person's food; no limits is the whole
    /// trip. A <c>scope</c> left by older clients is removed, since the limits say it all. Unknown fields are kept.
    /// </summary>
    public static Entity WithFilters(TripSnapshot trip, Entity budget, BudgetFilters filters)
    {
        if (filters.Person is { } person && trip.Find(person) is not { Type: "person" }) throw new DomainException("budget.person", "Choose a person of the trip.");
        if (filters.Collection is { } collection && trip.Find(collection) is not { Type: "collection" }) throw new DomainException("budget.collection", "Choose a collection of the trip.");
        var copy = budget.Copy();
        foreach (var key in new[] { "category", "person", "collection", "scope" }) copy.Data.Remove(key);
        if (filters.Category is { Length: > 0 } category) copy.Data["category"] = ExpenseCategories.Stored(ExpenseCategories.With(trip, Entity.Create("expense", ""), category));
        if (filters.Person is { } p) copy.Data["person"] = p.ToString();
        if (filters.Collection is { } c) copy.Data["collection"] = c.ToString();
        return copy;
    }

    /// <summary>The budgets for the whole trip, which together make the trip's budget (see <see cref="ForTrip(TripSnapshot, ExchangeRates?)"/>).</summary>
    public static IReadOnlyList<Entity> TripBudgets(TripSnapshot trip) => trip.Entities.Values
        .Where(e => e.Type == "budget" && FiltersOf(e).IsWholeTrip && TripSummaries.Money(e.Data["amount"]) is not null).OrderBy(e => e.Title, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// The trip's one budget, in the trip's currency (see <see cref="TripCurrency"/>): every budget without limits
    /// added up, compared with every expense. Amounts in other currencies convert exactly where charged in it,
    /// otherwise with <paramref name="rates"/> as an estimate. Null without a budget for the whole trip.
    /// </summary>
    public static TripBudgetStatus? ForTrip(TripSnapshot trip, ExchangeRates? rates = null)
    {
        var budgets = TripBudgets(trip);
        if (budgets.Count == 0) return null;
        var status = ForTrip(trip, budgets, TripCurrency.Of(trip) ?? TripSummaries.Money(budgets[0].Data["amount"])!.Currency, rates);
        // A budget set before the trip's currency changed, with no rate to convert it yet, stays in its own currency
        // instead of reading as a budget of zero.
        if (status.Amount == 0 && status.AmountNotConverted.Count > 0) status = ForTrip(trip, budgets, status.AmountNotConverted[0].Currency, rates);
        return status;
    }

    private static TripBudgetStatus ForTrip(TripSnapshot trip, IReadOnlyList<Entity> budgets, string currency, ExchangeRates? rates)
    {
        decimal amount = 0; var approximate = false; var missing = new List<Money>();
        foreach (var budget in budgets)
        {
            var money = TripSummaries.Money(budget.Data["amount"])!;
            var (value, code, how) = Expenses.Convert(budget, money.Amount, money.Currency, currency, rates);
            if (code != currency) { missing.Add(money); continue; }
            amount += value; approximate |= how == Conversion.Reference;
        }
        var expenses = trip.Entities.Values.Where(e => e.Type == "expense").ToArray();
        var ideas = expenses.Where(e => Expenses.IsIdea(trip, e)).ToArray();
        return new(currency, amount, approximate, missing, budgets, Expenses.Total(expenses, currency, rates), Expenses.Total(expenses.Except(ideas), currency, rates, planned: true))
        { Ideas = ideas.Length > 0 ? Expenses.Total(ideas, currency, rates, planned: true) : null };
    }

    /// <summary>
    /// Budgets in another currency than <paramref name="currency"/>, such as after the trip's currency changed, each
    /// converted to it with <paramref name="rates"/> and rounded to whole units; budgets that cannot be converted are
    /// left out. Clients offer this when the trip's currency changes, showing old and new amounts, and save it only
    /// after the travellers agree.
    /// </summary>
    public static IReadOnlyList<(Entity Budget, Money Before, Money After)> ConvertTo(TripSnapshot trip, string currency, ExchangeRates? rates)
    {
        var result = new List<(Entity, Money, Money)>();
        foreach (var budget in trip.Entities.Values.Where(e => e.Type == "budget").OrderBy(e => e.Title, StringComparer.Ordinal))
        {
            if (TripSummaries.Money(budget.Data["amount"]) is not { } money || money.Currency == currency || rates?.Convert(money.Amount, money.Currency, currency) is not { } value) continue;
            var after = new Money(Expenses.Format(Math.Round(value, 0, MidpointRounding.AwayFromZero), currency), currency);
            var copy = budget.Copy();
            var amount = copy.Data["amount"] as JsonObject ?? new JsonObject();
            amount["value"] = after.Value; amount["currency"] = currency;
            copy.Data["amount"] = amount;
            result.Add((copy, money, after));
        }
        return result;
    }

    /// <summary>Budgets with a limit, such as a category or a person, with where they stand, in order of title.</summary>
    public static IReadOnlyList<BudgetStatus> Limited(TripSnapshot trip, ExchangeRates? rates = null) => All(trip, rates).Where(b => !b.Filters.IsWholeTrip).ToArray();

    /// <summary>Every budget of the trip with where it stands, whole-trip budgets first, then in order of title.</summary>
    public static IReadOnlyList<BudgetStatus> All(TripSnapshot trip, ExchangeRates? rates = null) => trip.Entities.Values.Where(e => e.Type == "budget")
        .Select(b => Status(trip, b, rates)).OfType<BudgetStatus>()
        .OrderBy(s => Scopes.ToList().IndexOf(s.Scope)).ThenBy(s => s.Budget.Title, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// What the expenses a budget covers come to, or null when the budget has no amount. A person's budget counts
    /// that person's part of each expense; a collection's budget the expenses for its plans, bookings and places,
    /// and expenses in the collection themselves.
    /// </summary>
    public static BudgetStatus? Status(TripSnapshot trip, Entity budget, ExchangeRates? rates = null)
    {
        if (TripSummaries.Money(budget.Data["amount"]) is not { } amount) return null;
        var scope = ScopeOf(budget); var target = TargetOf(budget); var filters = FiltersOf(budget);
        decimal spent = 0, planned = 0; var approximate = false; var missing = new List<Money>();
        var inCollection = filters.Collection is { } collectionId && trip.Find(collectionId) is { Type: "collection" } collection
            ? (collection.Data["items"] as JsonArray ?? []).Select(n => Guid.TryParse(n?.ToString(), out var id) ? id : (Guid?)null).OfType<Guid>().ToHashSet() : [];
        foreach (var expense in trip.Entities.Values.Where(e => e.Type == "expense"))
        {
            // Payments for ideas say what it would cost; they do not count until it is booked.
            if (TripSummaries.Money(expense.Data["amount"]) is not { } money || Expenses.IsIdea(trip, expense)) continue;
            if (filters.Category is { } wanted && !string.Equals(ExpenseCategories.Of(trip, expense), wanted, StringComparison.OrdinalIgnoreCase)) continue;
            if (filters.Collection is not null && !(inCollection.Contains(expense.Id) || Related(trip, expense).Any(inCollection.Contains))) continue;
            decimal? part = money.Amount;
            if (filters.Person is { } person) part = Expenses.Split(trip, expense) is var split && split.People.Contains(person) ? split.ShareOf(person) : null;
            if (part is not { } value || value == 0) continue;
            var (converted, code, how) = Expenses.Convert(expense, value, money.Currency, amount.Currency, rates);
            if (code != amount.Currency) { missing.Add(new Money(Expenses.Format(value, money.Currency), money.Currency)); continue; }
            if (how == Conversion.Reference) approximate = true;
            if (expense.Data["estimated"] is JsonValue flag && flag.TryGetValue<bool>(out var estimated) && estimated) planned += converted; else spent += converted;
        }
        var notIncluded = missing.GroupBy(m => m.Currency, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new Money(Expenses.Format(g.Sum(m => m.Amount), g.Key), g.Key)).ToArray();
        return new(budget, scope, target, amount, spent, planned, approximate, notIncluded) { Filters = filters };
    }

    /// <summary>
    /// What a collection budget can reach from an expense: what the expense belongs to, a booking's plans, and the
    /// places of those plans.
    /// </summary>
    private static IEnumerable<Guid> Related(TripSnapshot trip, Entity expense)
    {
        foreach (var id in (expense.Data["related"] as JsonArray ?? []).Select(r => Guid.TryParse(r?["id"]?.ToString(), out var x) ? x : (Guid?)null).OfType<Guid>())
        {
            yield return id;
            if (trip.Find(id) is not { } entity) continue;
            var items = entity.Type == "booking" ? TripCommands.BookedItems(trip, id) : entity.Type == "schedule_item" ? [id] : [];
            foreach (var item in items)
            {
                yield return item;
                if (trip.Find(item) is { } plan && ScheduleQueries.PrimaryPlace(trip, plan) is { } place) yield return place.Id;
            }
        }
    }

    /// <summary>Checks that a scoped budget names an existing person or collection.</summary>
    public static IEnumerable<Diagnostic> Validate(TripSnapshot trip)
    {
        foreach (var budget in trip.Entities.Values.Where(e => e.Type == "budget"))
            foreach (var (key, type) in new[] { ("person", "person"), ("collection", "collection") })
            {
                if (budget.Data[key]?.ToString() is not { } text) continue;
                if (!Guid.TryParse(text, out var id) || trip.Find(id) is not { } target) yield return new("reference.missing", Severity.Error, budget.Id + "/" + key, "The referenced entity does not exist.");
                else if (target.Type != type) yield return new("reference.type", Severity.Error, budget.Id + "/" + key, "The linked item has the wrong entity type.");
            }
    }
}
