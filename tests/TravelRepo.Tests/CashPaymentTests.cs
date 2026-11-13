using System.Text.Json.Nodes;
using NodaTime;
using TravelRepo.Core;
using TravelRepo.Serialization;
using Xunit;
namespace TravelRepo.Tests;

/// <summary>Payments in cash bought before, which no card will ever charge (specification section 35).</summary>
public class CashPaymentTests
{
    private static readonly Entity Ana = Entity.Create("person", "Ana"), Ben = Entity.Create("person", "Ben");

    private static TripSnapshot Trip(params Entity[] entities)
    {
        var manifest = TripCurrency.With(Entity.CreateTrip("Kyoto", "en", "Asia/Tokyo"), "EUR");
        manifest.Data["participants"] = new JsonArray(Ana.Id.ToString(), Ben.Id.ToString());
        return new(manifest, entities.Concat([Ana, Ben]).DistinctBy(e => e.Id).ToDictionary(e => e.Id));
    }

    private static Entity Expense(string title, string value, string day)
    {
        var e = Entity.Create("expense", title);
        e.Data["amount"] = new JsonObject { ["value"] = value, ["currency"] = "JPY" };
        e.Data["estimated"] = false; e.Data["date"] = day;
        e.Data["paid_by"] = new JsonArray(Ana.Id.ToString());
        return e;
    }

    private static readonly ExchangeRates Rates = new(new DateOnly(2027, 5, 14), new Dictionary<string, decimal> { ["JPY"] = 160m });

    [Fact]
    public void CashCountsExactlyAtTheRateItCost()
    {
        // 10,000 yen from the cash machine cost 62.50 euros: 0.00625 euros per yen.
        var ramen = Expenses.WithCash(Expense("Ramen", "2400", "2027-05-15"), "EUR", 0.00625m, new LocalDate(2027, 5, 15));
        Assert.True(Expenses.IsCash(ramen));
        var charge = Assert.IsType<Charge>(Expenses.ChargeOf(ramen));
        Assert.Equal((15.00m, "EUR", 0.00625m, Expenses.Cash), (charge.Value, charge.Currency, charge.Rate, charge.Source));
        Assert.Equal((15.00m, "EUR", Conversion.Charged), Expenses.Convert(ramen, 2400m, "JPY", "EUR", Rates));
        // In the totals it is exact, not estimated, and needs no charged amount.
        var total = Expenses.Total(Trip(ramen), "EUR", Rates);
        Assert.Equal((15.00m, 0m, 0), (total.Exact, total.Estimated, total.WithoutCharge));
        // Corrected later, the amount in euros follows the rate, not the other way round.
        var corrected = ramen.Copy(); corrected.Data["amount"]!["value"] = "3200";
        Assert.Equal(20.00m, Expenses.ChargeOf(corrected)!.Value);
    }

    [Fact]
    public void CashIsValidTripDataAndACardChargeReplacesIt()
    {
        var market = Expenses.WithCash(Expense("Market", "1600", "2027-05-15"), "EUR", 0.00625m, new LocalDate(2027, 5, 15));
        Assert.Empty(SchemaValidation.Validate(market));
        // Paid by card after all: the charge replaces the cash mark.
        var card = Expenses.WithCharge(market, 10.20m, "EUR", new LocalDate(2027, 5, 16));
        Assert.False(Expenses.IsCash(card)); Assert.Equal("card", Expenses.ChargeOf(card)!.Source);
        Assert.Empty(SchemaValidation.Validate(card));
        // And back: the cash mark replaces the charge.
        Assert.True(Expenses.IsCash(Expenses.WithCash(card, "EUR", 0.0064m, new LocalDate(2027, 5, 16))));
        Assert.Throws<DomainException>(() => Expenses.WithCash(market, "JPY", 1m, new LocalDate(2027, 5, 15)));
        Assert.Throws<DomainException>(() => Expenses.WithCash(market, "EUR", 0m, new LocalDate(2027, 5, 15)));
    }

    [Fact]
    public void TheNextCashPaymentIsOfferedTheRateOfTheLastAndPayingBackKeepsItsSource()
    {
        var first = Expenses.WithCash(Expense("Temple", "800", "2027-05-14"), "EUR", 0.0063m, new LocalDate(2027, 5, 14));
        var second = Expenses.WithCash(Expense("Ramen", "2400", "2027-05-15"), "EUR", 0.00625m, new LocalDate(2027, 5, 15));
        var card = Expenses.WithCharge(Expense("Taxi", "3000", "2027-05-16"), 19m, "EUR");
        var trip = Trip(first, second, card);
        Assert.Equal(0.00625m, Expenses.LastCashRate(trip, "JPY", "EUR"));
        Assert.Null(Expenses.LastCashRate(trip, "USD", "EUR"));

        // Ben pays Ana back his half in euros: the transfer follows the cash rate and says so.
        var shared = second.Copy(); shared.Data["shared_by"] = new JsonArray(Ana.Id.ToString(), Ben.Id.ToString());
        trip = Trip(first, shared, card);
        var debt = Assert.Single(Expenses.Split(trip, shared).Debts);
        var settled = Expenses.Settle(trip, [new DebtKey(shared.Id, debt.From, debt.To)], new LocalDate(2027, 5, 20), "EUR", Rates).Single();
        var transfer = settled.Data["settled"]!.AsArray().OfType<JsonObject>().Single()["converted"]!;
        Assert.Equal(("7.50", "cash"), (transfer["value"]!.ToString(), transfer["source"]!.ToString()));
    }
}
