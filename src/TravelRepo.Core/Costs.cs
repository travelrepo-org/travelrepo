using System.Text.Json.Nodes;

namespace TravelRepo.Core;

/// <summary>
/// The currency a trip counts in (specification section 33.1): its budget, its totals and who owes whom are in it,
/// and amounts charged are entered in it. It is stored in the manifest and shared by all travellers.
/// </summary>
public static class TripCurrency
{
    /// <summary>The currency stored in the manifest, or null.</summary>
    public static string? Stored(TripSnapshot trip) => ExchangeRates.Code(trip.Manifest.Data["currency"]?.ToString());

    /// <summary>The stored currency, otherwise <see cref="Suggested"/>.</summary>
    public static string? Of(TripSnapshot trip) => Stored(trip) ?? Suggested(trip);

    /// <summary>
    /// The currency a trip without one most likely counts in: the one amounts were most often charged in, otherwise the
    /// one most expenses are in, otherwise the one of its budgets. Null for a trip without costs.
    /// </summary>
    public static string? Suggested(TripSnapshot trip)
    {
        var expenses = trip.Entities.Values.Where(e => e.Type == "expense").ToArray();
        static string? Most(IEnumerable<string?> codes) => codes.OfType<string>().GroupBy(c => c, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).FirstOrDefault()?.Key;
        return Most(expenses.Select(e => Expenses.ChargeOf(e)?.Currency))
            ?? Most(expenses.Select(e => TripSummaries.Money(e.Data["amount"])?.Currency))
            ?? Most(trip.Entities.Values.Where(e => e.Type == "budget").Select(b => TripSummaries.Money(b.Data["amount"])?.Currency));
    }

    /// <summary>A copy of the manifest that counts in <paramref name="currency"/>.</summary>
    public static Entity With(Entity manifest, string currency)
    {
        var code = ExchangeRates.Code(currency) ?? throw new DomainException("trip.currency", "A currency is a code with three letters, such as EUR.");
        var copy = manifest.Copy();
        copy.Data["currency"] = code;
        return copy;
    }
}

/// <summary>What an upgrade of a trip's costs changes, one entry per entity.</summary>
public enum CostChangeKind
{
    /// <summary>The trip gets the currency it counts in.</summary>
    TripCurrency,
    /// <summary>An expense that named nobody is fixed to the people who travel now, which it was divided among already.</summary>
    SharesNamed,
    /// <summary>A booking's price without any payment becomes a payment still to pay.</summary>
    PriceToPlanned,
    /// <summary>Part of a booking's price is paid; the rest becomes a payment still to pay.</summary>
    PriceRestPlanned,
    /// <summary>A booking's price is covered by its payments and is removed.</summary>
    PriceCovered,
    /// <summary>A booking's payments add up to more than its price, for example with fees; the price is removed.</summary>
    PriceExceeded,
    /// <summary>A booking's price cannot be compared with its payments, which are in another currency; it is kept for the travellers to decide.</summary>
    PriceKept,
    /// <summary>A budget's <c>scope</c>, which only repeated its limits, is removed.</summary>
    BudgetScope
}

/// <summary>
/// One change of an upgrade. <see cref="Amount"/> is the amount involved, such as the price that becomes a payment or
/// the rest still to pay; <see cref="Other"/> a second amount, such as what the payments add up to.
/// </summary>
public sealed record CostChange(CostChangeKind Kind, Guid Entity, string Title, Money? Amount = null, Money? Other = null);

/// <summary>An upgrade of a trip's costs: what changes, and the entities to save, new ones included.</summary>
public sealed record CostUpgradePlan(IReadOnlyList<CostChange> Changes, IReadOnlyList<Entity> Edits)
{
    /// <summary>Whether nothing needs saving. Changes that only report something, such as a price kept, do not count.</summary>
    public bool IsEmpty => Edits.Count == 0;
}

/// <summary>
/// Brings costs written by older clients into the current shape (specification sections 33.1, 34 and 36) without
/// changing what they mean: the trip gets a currency, expenses that named nobody name the travellers they were already
/// divided among, a booking's price becomes the payments that are still open, and budgets lose their repeated scope.
/// Clients show <see cref="CostUpgradePlan.Changes"/> and save <see cref="CostUpgradePlan.Edits"/> as one version.
/// Planning again on the result finds nothing more to do.
/// </summary>
public static class CostUpgrade
{
    /// <summary>
    /// What upgrading the trip changes. <paramref name="fallbackCurrency"/>, such as the device region's currency, is
    /// used when the trip has no currency and no costs suggest one.
    /// </summary>
    public static CostUpgradePlan Plan(TripSnapshot trip, string? fallbackCurrency = null)
    {
        var changes = new List<CostChange>(); var edits = new List<Entity>();
        if (TripCurrency.Stored(trip) is null && (TripCurrency.Suggested(trip) ?? ExchangeRates.Code(fallbackCurrency)) is { } currency
            && trip.Entities.Values.Any(e => e.Type is "expense" or "budget" || e.Type == "booking" && e.Data["price"] is not null))
        {
            edits.Add(TripCurrency.With(trip.Manifest, currency));
            changes.Add(new(CostChangeKind.TripCurrency, trip.Manifest.Id, currency));
        }
        foreach (var fixedShares in Expenses.FixShares(trip))
        {
            edits.Add(fixedShares);
            changes.Add(new(CostChangeKind.SharesNamed, fixedShares.Id, fixedShares.Title));
        }
        foreach (var booking in trip.Entities.Values.Where(e => e.Type == "booking" && e.Data["price"] is not null).OrderBy(e => e.Title, StringComparer.Ordinal))
        {
            var price = TripSummaries.Money(booking.Data["price"]);
            var withoutPrice = booking.Copy(); withoutPrice.Data.Remove("price");
            // Ideas and cancelled bookings were never paid; their price only says what they would have cost, so it stays.
            if (price is not { Amount: > 0 } || booking.Data["status"]?.ToString() is "cancelled" or "idea") continue;
            var payments = Expenses.ForBooking(trip, booking.Id);
            if (payments.Count == 0)
            {
                edits.Add(withoutPrice); edits.Add(Expenses.FromBooking(trip, booking, price, planned: true));
                changes.Add(new(CostChangeKind.PriceToPlanned, booking.Id, booking.Title, price));
                continue;
            }
            if (Recorded(trip, booking, price) is not { } recorded)
            {
                changes.Add(new(CostChangeKind.PriceKept, booking.Id, booking.Title, price));
                continue;
            }
            edits.Add(withoutPrice);
            var other = new Money(Expenses.Format(recorded, price.Currency), price.Currency);
            if (recorded < price.Amount)
            {
                var rest = new Money(Expenses.Format(price.Amount - recorded, price.Currency), price.Currency);
                edits.Add(Expenses.FromBooking(trip, booking, rest, planned: true));
                changes.Add(new(CostChangeKind.PriceRestPlanned, booking.Id, booking.Title, rest, other));
            }
            else changes.Add(new(recorded == price.Amount ? CostChangeKind.PriceCovered : CostChangeKind.PriceExceeded, booking.Id, booking.Title, price, other));
        }
        foreach (var budget in trip.Entities.Values.Where(e => e.Type == "budget" && e.Data["scope"] is not null).OrderBy(e => e.Title, StringComparer.Ordinal))
        {
            var copy = budget.Copy(); copy.Data.Remove("scope");
            edits.Add(copy);
            changes.Add(new(CostChangeKind.BudgetScope, budget.Id, budget.Title));
        }
        // An expense may be both named and, as a new payment, created; the last version of each entity wins.
        return new(changes, edits.GroupBy(e => e.Id).Select(g => g.Last()).ToArray());
    }

    /// <summary>What a booking's payments add up to in its price's currency, or null when one of them cannot be compared.</summary>
    private static decimal? Recorded(TripSnapshot trip, Entity booking, Money price)
    {
        decimal recorded = 0;
        foreach (var expense in Expenses.ForBooking(trip, booking.Id))
        {
            if (TripSummaries.Money(expense.Data["amount"]) is not { } money) continue;
            var (amount, code, _) = Expenses.Convert(expense, money.Amount, money.Currency, price.Currency);
            if (code != price.Currency) return null;
            recorded += amount;
        }
        return recorded;
    }

    /// <summary>What of a legacy price its payments do not cover, or null when they cannot be compared.</summary>
    public static Money? Rest(TripSnapshot trip, Entity booking, Money price) =>
        Recorded(trip, booking, price) is { } recorded ? new Money(Expenses.Format(price.Amount - recorded, price.Currency), price.Currency) : null;
}
