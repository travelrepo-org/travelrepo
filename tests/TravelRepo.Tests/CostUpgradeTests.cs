using System.Text.Json.Nodes;
using TravelRepo.Core;
using TravelRepo.Serialization;
using Xunit;
namespace TravelRepo.Tests;

/// <summary>
/// Upgrading costs written by older clients: every shape they could write keeps what it means, and nothing that was
/// paid, owed or paid back changes.
/// </summary>
public class CostUpgradeTests
{
    private static readonly Entity Mia = Entity.Create("person", "Mia"), Noah = Entity.Create("person", "Noah");

    private static Entity Booking(string title, string? price, string currency = "NZD", string status = "confirmed")
    {
        var b = Entity.Create("booking", title); b.Data["status"] = status;
        b.Data["travelers"] = new JsonArray(Mia.Id.ToString(), Noah.Id.ToString());
        if (price is not null) b.Data["price"] = new JsonObject { ["value"] = price, ["currency"] = currency };
        return b;
    }

    private static Entity Expense(string title, string value, string currency, Entity payer, Entity? booking = null)
    {
        var e = Entity.Create("expense", title);
        e.Data["amount"] = new JsonObject { ["value"] = value, ["currency"] = currency };
        e.Data["estimated"] = false;
        e.Data["paid_by"] = new JsonArray(payer.Id.ToString());
        if (booking is not null) e.Data["related"] = new JsonArray(new JsonObject { ["id"] = booking.Id.ToString(), ["type"] = "booking" });
        return e;
    }

    /// <summary>
    /// A two-person trip in the shapes an older client wrote: bookings whose price equals their expense, one paid in
    /// part, one not paid, one in another currency, an idea; expenses without a split, with an equal split for one
    /// person, with amounts and a card charge, with a paid-back mark; and a budget with a scope.
    /// </summary>
    private static TripSnapshot OldTrip()
    {
        var manifest = Entity.CreateTrip("South Island loop", "en", "Pacific/Auckland");
        manifest.Data["participants"] = new JsonArray(Mia.Id.ToString(), Noah.Id.ToString());
        var flights = Booking("Flights", "1200.00", "EUR"); var lodge = Booking("Lodge", "300.00"); var motel = Booking("Motel", "150.00");
        var tour = Booking("Tour", "600.00"); var ferry = Booking("Ferry", "190.00"); var maybe = Booking("Maybe glamping", "250.00", status: "idea");
        var flightCost = Expense("Flights for two", "1200.00", "EUR", Noah, flights);
        flightCost.Data["settled"] = new JsonArray(new JsonObject { ["person"] = Mia.Id.ToString(), ["to"] = Noah.Id.ToString(), ["value"] = "600.00", ["date"] = "2027-02-03" });
        var lodgeCost = Expense("Lodge", "300.00", "NZD", Mia, lodge);
        lodgeCost.Data["converted"] = new JsonObject { ["value"] = "150.00", ["currency"] = "EUR", ["rate"] = "0.5", ["rate_date"] = "2027-02-04", ["source"] = "card" };
        var tourDeposit = Expense("Tour deposit", "200.00", "NZD", Mia, tour);
        var ferryInEuros = Expense("Ferry", "98.00", "EUR", Noah, ferry);
        var pass = Expense("Park pass Mia", "50.00", "NZD", Mia);
        pass.Data["split"] = new JsonObject { ["method"] = "equal", ["parts"] = new JsonArray(new JsonObject { ["person"] = Mia.Id.ToString() }) };
        var food = Expense("Picnic", "80.00", "NZD", Noah);
        food.Data["split"] = new JsonObject { ["method"] = "amount", ["parts"] = new JsonArray(new JsonObject { ["person"] = Mia.Id.ToString(), ["value"] = "30.00" }, new JsonObject { ["person"] = Noah.Id.ToString(), ["value"] = "50.00" }) };
        food.Data["converted"] = new JsonObject { ["value"] = "40.00", ["currency"] = "EUR", ["rate"] = "0.5", ["rate_date"] = "2027-02-04", ["source"] = "card" };
        var snack = Expense("Snack", "12.00", "NZD", Noah);
        var budget = Entity.Create("budget", "Spending"); budget.Data["amount"] = new JsonObject { ["value"] = "3000.00", ["currency"] = "EUR" }; budget.Data["scope"] = "trip";
        var entities = new[] { Mia, Noah, flights, lodge, motel, tour, ferry, maybe, flightCost, lodgeCost, tourDeposit, ferryInEuros, pass, food, snack, budget };
        return new(manifest, entities.ToDictionary(e => e.Id));
    }

    private static TripSnapshot Apply(TripSnapshot trip, CostUpgradePlan plan)
    {
        var manifest = plan.Edits.FirstOrDefault(e => e.Id == trip.Manifest.Id) ?? trip.Manifest;
        var entities = trip.Entities.ToDictionary(p => p.Key, p => p.Value);
        foreach (var e in plan.Edits.Where(e => e.Id != trip.Manifest.Id)) entities[e.Id] = e;
        return new(manifest, entities);
    }

    [Fact]
    public void AnUpgradeKeepsEverythingPaidOwedAndPaidBack()
    {
        var before = OldTrip();
        var plan = CostUpgrade.Plan(before);
        var after = Apply(before, plan);
        string Title(Guid id) => before.Find(id)?.Title ?? after.Find(id)!.Title;

        // Who owes whom, what is paid back and what was paid do not change at all.
        Assert.Equal(Expenses.Debts(before).Select(d => (d.Expense, d.From, d.To, d.Amount, d.Settled)), Expenses.Debts(after).Select(d => (d.Expense, d.From, d.To, d.Amount, d.Settled)));
        Assert.Equal(Expenses.Balances(before), Expenses.Balances(after));
        Assert.Equal(Expenses.SettleUp(before, "EUR"), Expenses.SettleUp(after, "EUR"));
        Assert.Equal(TripSummaries.Costs(before).Select(c => (c.Currency, c.Paid)), TripSummaries.Costs(after).Select(c => (c.Currency, c.Paid)));
        Assert.Equal(Expenses.Total(before, "EUR").Total, Expenses.Total(after, "EUR").Total);

        var kinds = plan.Changes.GroupBy(c => c.Kind).ToDictionary(g => g.Key, g => g.Select(c => c.Title).Order().ToArray());
        // The trip counts in euros, the currency the cards charged in.
        Assert.Equal(["EUR"], kinds[CostChangeKind.TripCurrency]);
        Assert.Equal("EUR", TripCurrency.Stored(after));
        // Expenses that named nobody now name both travellers; the one for Mia alone stays hers.
        Assert.Equal(["Ferry", "Flights for two", "Lodge", "Snack", "Tour deposit"], kinds[CostChangeKind.SharesNamed]);
        Assert.All(after.Entities.Values.Where(e => e.Type == "expense"), e => Assert.False(Expenses.SharesWithEveryone(e)));
        Assert.Equal([Mia.Id], Expenses.Split(after, after.Find(before.Entities.Values.Single(e => e.Title == "Park pass Mia").Id)!).People);

        // Prices: covered ones go, the rest of the tour and the unpaid motel become payments still to pay, the ferry
        // paid in euros cannot be compared and keeps its price, the idea keeps its price.
        Assert.Equal(["Flights", "Lodge"], kinds[CostChangeKind.PriceCovered]);
        Assert.Equal(["Motel"], kinds[CostChangeKind.PriceToPlanned]);
        Assert.Equal(["Tour"], kinds[CostChangeKind.PriceRestPlanned]);
        Assert.Equal(["Ferry"], kinds[CostChangeKind.PriceKept]);
        Assert.Equal(new Money("400.00", "NZD"), plan.Changes.Single(c => c.Kind == CostChangeKind.PriceRestPlanned).Amount);
        var planned = after.Entities.Values.Where(e => e.Type == "expense" && Expenses.IsPlanned(e)).OrderBy(e => e.Title).ToArray();
        Assert.Equal([("Motel", "150.00"), ("Tour", "400.00")], planned.Select(e => (e.Title, e.Data["amount"]!["value"]!.ToString())));
        Assert.All(planned, e => Assert.Equal([Mia.Id, Noah.Id], Expenses.Split(after, e).People));
        Assert.All(planned, e => Assert.Empty(Expenses.Split(after, e).Debts));
        Assert.Equal(["Ferry", "Maybe glamping"], after.Entities.Values.Where(e => e.Type == "booking" && e.Data["price"] is not null).Select(e => e.Title).Order());
        // What was booked but not counted before is now planned spending.
        Assert.Equal(550m, TripSummaries.Costs(after).Single(c => c.Currency == "NZD").Estimated);

        Assert.Equal(["Spending"], kinds[CostChangeKind.BudgetScope]);
        Assert.Equal(3000m, Budgets.ForTrip(after)!.Amount);

        // Everything stays valid, and upgrading again finds nothing more to do.
        Assert.All(plan.Edits, e => Assert.Empty(SchemaValidation.Validate(e)));
        Assert.DoesNotContain(SemanticValidation.Validate(after), d => d.Severity == Severity.Error);
        Assert.True(CostUpgrade.Plan(after).IsEmpty);
        Assert.Equal(["Ferry"], CostUpgrade.Plan(after).Changes.Select(c => Title(c.Entity)));
    }

    [Fact]
    public void ATripWithoutCostsNeedsNoUpgradeAndOneWithACurrencyKeepsIt()
    {
        var manifest = Entity.CreateTrip("Empty", "en", "Europe/Berlin");
        Assert.True(CostUpgrade.Plan(new TripSnapshot(manifest, new Dictionary<Guid, Entity>()), "EUR").IsEmpty);
        var trip = OldTrip();
        trip = trip with { Manifest = TripCurrency.With(trip.Manifest, "NZD") };
        Assert.DoesNotContain(CostUpgrade.Plan(trip).Changes, c => c.Kind == CostChangeKind.TripCurrency);
        Assert.Equal("NZD", TripCurrency.Of(trip));
        Assert.Throws<DomainException>(() => TripCurrency.With(trip.Manifest, "euro"));
    }

    [Fact]
    public void TheTripBudgetAddsUpBudgetsWithoutLimitsInTheTripCurrency()
    {
        var old = OldTrip();
        var trip = Apply(old, CostUpgrade.Plan(old));
        var cash = Entity.Create("budget", "Cash"); cash.Data["amount"] = new JsonObject { ["value"] = "500.00", ["currency"] = "NZD" };
        trip = trip with { Entities = trip.Entities.Values.Append(cash).ToDictionary(e => e.Id) };
        // Without rates the NZD budget cannot be added to the euros and is named.
        var status = Budgets.ForTrip(trip)!;
        Assert.Equal(("EUR", 3000m), (status.Currency, status.Amount));
        Assert.Equal([new Money("500.00", "NZD")], status.AmountNotConverted);
        var rates = new ExchangeRates(new DateOnly(2026, 11, 20), new Dictionary<string, decimal> { ["NZD"] = 2m });
        status = Budgets.ForTrip(trip, rates)!;
        Assert.Equal((3250m, true), (status.Amount, status.Approximate));
        // Paid: the euros as paid or charged, the rest of the NZD with reference rates; planned: the motel and the tour rest.
        Assert.Equal(1200m + 98m + 150m + 40m, status.Paid.Exact);
        Assert.Equal(275m, status.Planned.Total);
    }
}
