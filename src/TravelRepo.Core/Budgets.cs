using System.Text.Json.Nodes;

namespace TravelRepo.Core;

/// <summary>
/// Where a budget stands (specification section 36): what the expenses it covers came to in its currency,
/// <see cref="Spent"/> for paid ones and <see cref="Planned"/> for estimates. <see cref="Approximate"/> when reference
/// rates were used; <see cref="NotIncluded"/> are amounts that could not be converted at all.
/// </summary>
public sealed record BudgetStatus(Entity Budget, string Scope, string? Target, Money Amount, decimal Spent, decimal Planned, bool Approximate, IReadOnlyList<Money> NotIncluded)
{
    public decimal Used => Spent + Planned;
    /// <summary>What is left of the budget; negative when it is exceeded.</summary>
    public decimal Left => Amount.Amount - Used;
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

    /// <summary>A budget's scope; a budget without one, or with one this version does not know, is for the whole trip.</summary>
    public static string ScopeOf(Entity budget) => budget.Data["scope"]?.ToString() is { } scope && Scopes.Contains(scope) ? scope : Trip;

    /// <summary>What a scoped budget is for: the category, or the person's or collection's id; null for the whole trip.</summary>
    public static string? TargetOf(Entity budget) => ScopeOf(budget) switch
    {
        Category => budget.Data["category"]?.ToString() is { Length: > 0 } c ? c : null,
        Participant => budget.Data["person"]?.ToString(),
        Collection => budget.Data["collection"]?.ToString(),
        _ => null
    };

    /// <summary>
    /// A copy of the budget for <paramref name="scope"/>: the whole trip, a category (a standard one or the
    /// travellers' own), a person's id or a collection's id. Fields of other scopes are removed; unknown fields are kept.
    /// </summary>
    public static Entity WithScope(TripSnapshot trip, Entity budget, string scope, string? target = null)
    {
        if (!Scopes.Contains(scope)) throw new DomainException("budget.scope", "A budget is for the trip, a category, a person or a collection.");
        var copy = budget.Copy();
        copy.Data["scope"] = scope;
        foreach (var key in new[] { "category", "person", "collection" }) copy.Data.Remove(key);
        switch (scope)
        {
            case Category when target is { Length: > 0 }:
                copy.Data["category"] = ExpenseCategories.Stored(ExpenseCategories.With(trip, Entity.Create("expense", ""), target));
                break;
            case Participant when Guid.TryParse(target, out var person):
                if (trip.Find(person) is not { Type: "person" }) throw new DomainException("budget.person", "Choose a person of the trip.");
                copy.Data["person"] = person.ToString();
                break;
            case Collection when Guid.TryParse(target, out var collection):
                if (trip.Find(collection) is not { Type: "collection" }) throw new DomainException("budget.collection", "Choose a collection of the trip.");
                copy.Data["collection"] = collection.ToString();
                break;
        }
        return copy;
    }

    /// <summary>Every budget of the trip with where it stands, whole-trip budgets first, then in order of title.</summary>
    public static IReadOnlyList<BudgetStatus> All(TripSnapshot trip, ExchangeRates? rates = null) => trip.Entities.Values.Where(e => e.Type == "budget")
        .Select(b => Status(trip, b, rates)).OfType<BudgetStatus>()
        .OrderBy(s => Scopes.ToList().IndexOf(s.Scope)).ThenBy(s => s.Budget.Title, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// What the expenses a budget covers come to, or null when the budget has no amount. A person's budget counts
    /// that person's part of each expense; a collection's budget the expenses for its plans, bookings and places,
    /// and expenses in the collection themselves. A scoped budget without a target covers nothing.
    /// </summary>
    public static BudgetStatus? Status(TripSnapshot trip, Entity budget, ExchangeRates? rates = null)
    {
        if (TripSummaries.Money(budget.Data["amount"]) is not { } amount) return null;
        var scope = ScopeOf(budget); var target = TargetOf(budget);
        decimal spent = 0, planned = 0; var approximate = false; var missing = new List<Money>();
        var inCollection = scope == Collection && Guid.TryParse(target, out var collectionId) && trip.Find(collectionId) is { Type: "collection" } collection
            ? (collection.Data["items"] as JsonArray ?? []).Select(n => Guid.TryParse(n?.ToString(), out var id) ? id : (Guid?)null).OfType<Guid>().ToHashSet() : [];
        foreach (var expense in trip.Entities.Values.Where(e => e.Type == "expense"))
        {
            if (TripSummaries.Money(expense.Data["amount"]) is not { } money) continue;
            decimal? part = scope switch
            {
                Trip => money.Amount,
                Category => target is not null && string.Equals(ExpenseCategories.Of(trip, expense), target, StringComparison.CurrentCultureIgnoreCase) ? money.Amount : null,
                Participant => Guid.TryParse(target, out var person) && Expenses.Split(trip, expense) is var split && split.People.Contains(person) ? split.ShareOf(person) : null,
                Collection => inCollection.Contains(expense.Id) || Related(trip, expense).Any(inCollection.Contains) ? money.Amount : null,
                _ => null
            };
            if (part is not { } value || value == 0) continue;
            var (converted, code, how) = Expenses.Convert(expense, value, money.Currency, amount.Currency, rates);
            if (code != amount.Currency) { missing.Add(new Money(Expenses.Format(value, money.Currency), money.Currency)); continue; }
            if (how == Conversion.Reference) approximate = true;
            if (expense.Data["estimated"] is JsonValue flag && flag.TryGetValue<bool>(out var estimated) && estimated) planned += converted; else spent += converted;
        }
        var notIncluded = missing.GroupBy(m => m.Currency, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new Money(Expenses.Format(g.Sum(m => m.Amount), g.Key), g.Key)).ToArray();
        return new(budget, scope, target, amount, spent, planned, approximate, notIncluded);
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
