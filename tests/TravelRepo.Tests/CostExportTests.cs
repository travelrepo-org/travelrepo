using System.Globalization;
using System.Text.Json.Nodes;
using NodaTime;
using TravelRepo.Core;
using TravelRepo.Repository;
using TravelRepo.Serialization;
using Xunit;
namespace TravelRepo.Tests;

/// <summary>Cost exports, categories per person, due payments and budgets of deleted people.</summary>
public class CostExportTests
{
    private static readonly Entity Ana = Entity.Create("person", "Ana"), Ben = Entity.Create("person", "Ben"), Cleo = Entity.Create("person", "Cleo");

    private static Entity Expense(string title, string value, string currency, Entity payer, string category, params Entity[] sharing)
    {
        var e = Entity.Create("expense", title);
        e.Data["amount"] = new JsonObject { ["value"] = value, ["currency"] = currency };
        e.Data["estimated"] = false;
        e.Data["paid_by"] = new JsonArray(payer.Id.ToString());
        e.Data["category"] = category;
        return Expenses.WithSplit(e, SplitMethod.Equal, (sharing.Length > 0 ? sharing : [Ana, Ben, Cleo]).Select(p => (p.Id, (decimal?)null)));
    }

    private static TripSnapshot Trip(params Entity[] entities)
    {
        var manifest = TripCurrency.With(Entity.CreateTrip("Lake trip", "en", "Europe/Berlin"), "EUR");
        manifest.Data["participants"] = new JsonArray(Ana.Id.ToString(), Ben.Id.ToString(), Cleo.Id.ToString());
        return new(manifest, entities.Concat([Ana, Ben, Cleo]).DistinctBy(e => e.Id).ToDictionary(e => e.Id));
    }

    [Fact]
    public void CategoriesCanCountOnePersonsOrACouplesParts()
    {
        var dinner = Expense("Dinner", "90.00", "EUR", Ana, "food");
        var kayak = Expense("Kayak", "40.00", "EUR", Ben, "activity", Ben, Cleo);
        var trip = Trip(dinner, kayak);
        Assert.Equal([("food", 90m), ("activity", 40m)], ExpenseCategories.Totals(trip, "EUR").Select(t => (t.Category, t.Amount)));
        Assert.Equal([("food", 30m)], ExpenseCategories.Totals(trip, "EUR", people: [Ana.Id]).Select(t => (t.Category, t.Amount)));
        // Ben and Cleo together: two thirds of the dinner and all of the kayak.
        Assert.Equal([("food", 60m), ("activity", 40m)], ExpenseCategories.Totals(trip, "EUR", people: [Ben.Id, Cleo.Id]).Select(t => (t.Category, t.Amount)));
    }

    [Fact]
    public void APaymentStillToPayCanBeDueAndIsRemindedOfAWeekBefore()
    {
        var rest = Expense("Lodge balance", "300.00", "EUR", Ana, "accommodation"); rest.Data["estimated"] = true;
        rest = Expenses.WithDue(rest, new LocalDate(2027, 4, 1));
        Assert.Empty(SchemaValidation.Validate(rest));
        var trip = Trip(rest);
        Instant At(int month, int day) => new LocalDate(2027, month, day).AtStartOfDayInZone(DateTimeZoneProviders.Tzdb["Europe/Berlin"]).ToInstant().Plus(Duration.FromHours(9));
        Assert.DoesNotContain(TripReminders.For(trip, At(3, 20)), r => r.Kind == ReminderKind.PaymentDue);
        var reminder = Assert.Single(TripReminders.For(trip, At(3, 26)), r => r.Kind == ReminderKind.PaymentDue);
        Assert.Equal((rest.Id, false), (reminder.Entity!.Value, reminder.Urgent));
        Assert.True(Assert.Single(TripReminders.For(trip, At(4, 3)), r => r.Kind == ReminderKind.PaymentDue).Urgent);
        // Paid: no longer due, no reminder.
        var paid = Expenses.MarkPaid(rest, [Ana.Id]);
        Assert.Null(Expenses.DueOf(paid));
        Assert.DoesNotContain(TripReminders.For(Trip(paid), At(3, 30)), r => r.Kind == ReminderKind.PaymentDue);
        var broken = rest.Copy(); broken.Data["due"]!["date"] = "2027-02-30";
        Assert.Contains(SemanticValidation.Validate(Trip(broken)), d => d.Code == "date.invalid");
    }

    [Fact]
    public void DeletingAPersonOffersTheirOwnBudget()
    {
        var food = Entity.Create("budget", "Ben's food"); food.Data["amount"] = new JsonObject { ["value"] = "100.00", ["currency"] = "EUR" };
        var trip = Trip(food);
        var bens = Budgets.WithFilters(trip, food, new BudgetFilters(null, Ben.Id, null));
        var both = Entity.Create("budget", "Ben's snacks"); both.Data["amount"] = new JsonObject { ["value"] = "20.00", ["currency"] = "EUR" };
        both = Budgets.WithFilters(trip, both, new BudgetFilters("food", Ben.Id, null));
        trip = Trip(bens, both);
        var plan = TripDeletion.Plan(trip, [Ben.Id]);
        // Only for Ben: offered. Ben's food: still limited to food, so it stays and keeps its category.
        Assert.Equal([bens.Id], plan.Unused.Select(e => e.Id));
        Assert.Equal("food", plan.Updated.Single(e => e.Id == both.Id).Data["category"]!.ToString());
        var withBudget = TripDeletion.Plan(trip, [Ben.Id, bens.Id]);
        Assert.Contains(withBudget.Deleted, e => e.Id == bens.Id);
    }

    [Fact]
    public void TheCsvListsEveryPaymentWithEachPersonsPartAndTheChosenSeparator()
    {
        var dinner = Expense("Dinner, lake view", "90.00", "EUR", Ana, "food");
        var ferry = Expense("Ferry", "60.00", "NZD", Ben, "transport", Ben, Cleo);
        ferry = Expenses.WithCharge(ferry, 33m, "EUR", new LocalDate(2027, 2, 3));
        var minus = Expense("-Refund", "12.00", "EUR", Cleo, "other");
        var trip = Trip(dinner, ferry, minus);
        var labels = new CostExportLabels { Categories = new Dictionary<string, string> { ["food"] = "Essen" } };

        var csv = CostExport.Csv(trip, new(), labels, CultureInfo.InvariantCulture, ",").Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Date,Payment,Category,Belongs to,Amount,Currency,Charged,Charged currency,In EUR,Paid by,State,Open,Due,Part Ana,Part Ben,Part Cleo", csv[0]);
        // A title with the separator is quoted; a title that looks like a formula gets an apostrophe; amounts are plain.
        Assert.Contains(csv, l => l.StartsWith(",\"Dinner, lake view\",Essen,,90.00,EUR,,,90.00,Ana,60.00 EUR open,60.00,,30.00,30.00,30.00", StringComparison.Ordinal));
        Assert.Contains(csv, l => l.StartsWith(",Ferry,transport,,60.00,NZD,33.00,EUR,33.00,Ben,", StringComparison.Ordinal));
        Assert.Contains(csv, l => l.StartsWith(",'-Refund,", StringComparison.Ordinal));

        // German with a semicolon: decimal commas. German with a comma: points, so columns stay apart.
        var german = CultureInfo.GetCultureInfo("de-DE");
        Assert.Contains("90,00;EUR", CostExport.Csv(trip, new(), labels, german, ";"), StringComparison.Ordinal);
        Assert.Contains("90.00,EUR", CostExport.Csv(trip, new(), labels, german, ","), StringComparison.Ordinal);
        Assert.Contains("90.00\tEUR", CostExport.Csv(trip, new(), labels, CultureInfo.InvariantCulture, "\t"), StringComparison.Ordinal);

        // Ben and Cleo only: the payments that concern them, their columns and their parts together in euros.
        var couple = CostExport.Csv(trip, new() { People = [Ben.Id, Cleo.Id] }, labels, CultureInfo.InvariantCulture, ",").Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.EndsWith("Part Ben,Part Cleo,Their part in EUR", couple[0], StringComparison.Ordinal);
        Assert.Contains(couple, l => l.Contains(",Ferry,", StringComparison.Ordinal) && l.EndsWith(",30.00,30.00,33.00", StringComparison.Ordinal));
        Assert.Contains(couple, l => l.Contains("Dinner", StringComparison.Ordinal) && l.EndsWith(",30.00,30.00,60.00", StringComparison.Ordinal));
    }

    [Fact]
    public void TheDocumentHasAnOverviewCategoriesWhoOwesWhomAndThePayments()
    {
        var dinner = Expense("Dinner", "90.00", "EUR", Ana, "food");
        var lodge = Expense("Lodge balance", "300.00", "EUR", Ana, "accommodation"); lodge.Data["estimated"] = true;
        lodge = Expenses.WithDue(lodge, new LocalDate(2027, 4, 1));
        var budget = Entity.Create("budget", "Trip"); budget.Data["amount"] = new JsonObject { ["value"] = "1000.00", ["currency"] = "EUR" };
        var trip = Trip(dinner, lodge, budget);
        var document = CostExport.Document(trip, culture: CultureInfo.InvariantCulture);
        Assert.Equal(["Overview", "By category", "Who owes whom", "Payments"], document.Sections.Select(s => s.Title));
        Assert.Equal([("Paid", "90.00 EUR"), ("Still to pay", "300.00 EUR"), ("Budget", "1,000.00 EUR")], document.Sections[0].Rows.Select(r => (r.Title, r.Lead)));
        Assert.Equal("390.00 EUR of 1,000.00 EUR, 610.00 EUR left", document.Sections[0].Rows[2].Summary);
        Assert.Equal(["Ben pays Ana", "Cleo pays Ana"], document.Sections[2].Rows.Select(r => r.Title).Order());
        Assert.Contains("to pay · due 04/01/2027", document.Sections[3].Rows.Single(r => r.Title == "Lodge balance").Summary, StringComparison.Ordinal);
        var text = TripExport.Text(document);
        Assert.Contains("Costs · in EUR", text, StringComparison.Ordinal);

        // For Ben: his part, and only the payment he makes.
        var bens = CostExport.Document(trip, new() { People = [Ben.Id] }, culture: CultureInfo.InvariantCulture);
        Assert.Equal("30.00 EUR", bens.Sections[0].Rows[0].Lead);
        Assert.Equal(["Ben pays Ana"], bens.Sections[2].Rows.Select(r => r.Title));
        Assert.Contains("for Ben", bens.Meta[0], StringComparison.Ordinal);
        Assert.Equal("Lake trip - Costs - Ben.csv", CostExport.FileName(trip, new() { People = [Ben.Id] }, new(), "csv"));
    }

    [Fact]
    public void PaymentsForIdeasAreListedApartAndCountOnceBooked()
    {
        var glamping = Entity.Create("booking", "Glamping"); glamping.Data["status"] = "idea";
        var maybe = Expense("Glamping", "400.00", "EUR", Ana, "accommodation"); maybe.Data["estimated"] = true; maybe.Data.Remove("paid_by");
        maybe.Data["related"] = new JsonArray(new JsonObject { ["type"] = "booking", ["id"] = glamping.Id.ToString() });
        var ferry = Expense("Ferry balance", "100.00", "EUR", Ana, "transport"); ferry.Data["estimated"] = true;
        var budget = Entity.Create("budget", "Trip"); budget.Data["amount"] = new JsonObject { ["value"] = "1000.00", ["currency"] = "EUR" };
        var food = Entity.Create("budget", "Stays"); food.Data["amount"] = new JsonObject { ["value"] = "300.00", ["currency"] = "EUR" }; food.Data["category"] = "accommodation";
        var trip = Trip(glamping, maybe, ferry, budget, food);
        Assert.True(Expenses.IsIdea(trip, maybe)); Assert.False(Expenses.IsIdea(trip, ferry));
        var costs = TripSummaries.Costs(trip).Single();
        Assert.Equal((100m, 400m), (costs.Estimated, costs.Ideas));
        var status = Budgets.ForTrip(trip)!;
        Assert.Equal((100m, 400m), (status.Planned.Total, status.Ideas!.Total));
        Assert.Equal(0m, Budgets.Status(trip, food)!.Planned);
        var document = CostExport.Document(trip, culture: CultureInfo.InvariantCulture);
        Assert.Equal([("Still to pay", "100.00 EUR"), ("Ideas, not counted", "400.00 EUR")], document.Sections[0].Rows.Where(r => r.Title is "Still to pay" or "Ideas, not counted").Select(r => (r.Title, r.Lead)));
        Assert.Contains("idea, not counted", document.Sections.Single(x => x.Title == "Payments").Rows.Single(r => r.Title == "Glamping").Summary, StringComparison.Ordinal);

        // Booked: it counts as still to pay. A paid payment of a cancelled booking always counts.
        var booked = glamping.Copy(); booked.Data["status"] = "confirmed";
        var counted = trip with { Entities = trip.Entities.Values.Where(e => e.Id != glamping.Id).Append(booked).ToDictionary(e => e.Id) };
        Assert.Equal(500m, Budgets.ForTrip(counted)!.Planned.Total);
        Assert.Null(Budgets.ForTrip(counted)!.Ideas);
        var cancelled = glamping.Copy(); cancelled.Data["status"] = "cancelled";
        var deposit = Expenses.MarkPaid(maybe, [Ana.Id]);
        var paidDeposit = trip with { Entities = trip.Entities.Values.Where(e => e.Id != glamping.Id && e.Id != maybe.Id).Append(cancelled).Append(deposit).ToDictionary(e => e.Id) };
        Assert.Equal(400m, TripSummaries.Costs(paidDeposit).Single().Paid);
    }

    [Fact]
    public void APaidMarkKeepsWhatWasTransferredInTheTripsCurrency()
    {
        var ferry = Expense("Ferry", "60.00", "NZD", Ana, "transport", Ana, Ben);
        var tour = Expense("Tour", "100.00", "NZD", Ana, "activity", Ana, Ben);
        ferry = Expenses.WithCharge(ferry, 33m, "EUR", new LocalDate(2027, 2, 3));
        var trip = Trip(ferry, tour);
        var rates = new ExchangeRates(new DateOnly(2027, 2, 10), new Dictionary<string, decimal> { ["NZD"] = 2m });
        var paid = Expenses.SettleAll(trip, new LocalDate(2027, 2, 12), "EUR", rates);
        trip = trip with { Entities = trip.Entities.Values.Where(e => paid.All(p => p.Id != e.Id)).Concat(paid).ToDictionary(e => e.Id) };
        // The ferry as the card charged it, the tour with the reference rate of that day; the debts stay in NZD.
        var ferryDebt = Expenses.Split(trip, trip.Find(ferry.Id)!).Debts.Single();
        Assert.Equal((30m, true), (ferryDebt.Settled, ferryDebt.IsSettled));
        Assert.Equal(new Transferred(16.50m, "EUR", 0.55m, new LocalDate(2027, 2, 3), false), ferryDebt.PaidIn);
        var tourDebt = Expenses.Split(trip, trip.Find(tour.Id)!).Debts.Single();
        Assert.Equal(new Transferred(25.00m, "EUR", 0.5m, new LocalDate(2027, 2, 10), true), tourDebt.PaidIn);
        Assert.Empty(SchemaValidation.Validate(trip.Find(tour.Id)!));
        // Later rates do not change it, and taking the mark back removes it.
        Assert.Equal(25.00m, Expenses.Split(trip, trip.Find(tour.Id)!).Debts.Single().PaidIn!.Value);
        Assert.Null(Expenses.Split(trip, Expenses.Unsettle(trip.Find(tour.Id)!)).Debts.Single().PaidIn);
        Assert.Contains("Ben paid back Ana 50.00 NZD as ≈ 25.00 EUR", TripExport.Text(CostExport.Document(trip, culture: CultureInfo.InvariantCulture)), StringComparison.Ordinal);
        // Paid back in its own currency: nothing more to record.
        var own = Expenses.Settle(Trip(ferry), ferry, null, new LocalDate(2027, 2, 12), "NZD");
        Assert.Null(own.Data["settled"]![0]!["converted"]);
    }
}
