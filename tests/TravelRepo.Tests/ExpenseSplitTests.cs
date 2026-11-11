using System.Text.Json.Nodes;
using NodaTime;
using TravelRepo.Core;
using TravelRepo.Merge;
using TravelRepo.Repository;
using TravelRepo.Serialization;
using Xunit;
namespace TravelRepo.Tests;

public class ExpenseSplitTests
{
    private static readonly Entity Ana = Entity.Create("person", "Ana"), Ben = Entity.Create("person", "Ben"), Cleo = Entity.Create("person", "Cleo");

    private static TripSnapshot Trip(params Entity[] entities)
    {
        var manifest = Entity.CreateTrip("Aachen weekend", "en", "Europe/Berlin");
        manifest.Data["participants"] = new JsonArray(Ana.Id.ToString(), Ben.Id.ToString(), Cleo.Id.ToString());
        return new(manifest, entities.Concat([Ana, Ben, Cleo]).DistinctBy(e => e.Id).ToDictionary(e => e.Id));
    }

    private static TripSnapshot With(TripSnapshot trip, params Entity[] changed) =>
        trip with { Entities = trip.Entities.Values.Where(e => changed.All(c => c.Id != e.Id)).Concat(changed).ToDictionary(e => e.Id) };

    private static Entity Expense(string title, string value, string currency, params Entity[] payers)
    {
        var e = Entity.Create("expense", title);
        e.Data["amount"] = new JsonObject { ["value"] = value, ["currency"] = currency };
        e.Data["estimated"] = false;
        e.Data["paid_by"] = new JsonArray(payers.Select(p => (JsonNode)p.Id.ToString()).ToArray());
        return e;
    }

    [Fact]
    public void AnExpenseIsDividedEquallyAmongAllTravellersByDefault()
    {
        var dinner = Expense("Dinner", "100.00", "EUR", Ana);
        var trip = Trip(dinner);
        var split = Expenses.Split(trip, dinner);
        Assert.True(split.Divides); Assert.Equal(SplitMethod.Equal, split.Method);
        // The cent left over goes to the first traveller, so the parts add up to the expense.
        Assert.Equal([33.34m, 33.33m, 33.33m], split.Shares.Select(s => s.Amount));
        Assert.Equal([Ana.Id, Ben.Id, Cleo.Id], split.Shares.Select(s => s.Person));
        Assert.Equal([(Ben.Id, Ana.Id, 33.33m), (Cleo.Id, Ana.Id, 33.33m)], split.Debts.Select(d => (d.From, d.To, d.Amount)));
        Assert.Equal(66.66m, split.Open); Assert.False(split.IsSettled);
        Assert.Empty(SchemaValidation.Validate(dinner));

        // A traveller who joins later shares the expenses that have no split of their own.
        var dana = Entity.Create("person", "Dana");
        var manifest = trip.Manifest.Copy(); ((JsonArray)manifest.Data["participants"]!).Add(dana.Id.ToString());
        var later = new TripSnapshot(manifest, trip.Entities.Values.Append(dana).ToDictionary(e => e.Id));
        Assert.Equal([25m, 25m, 25m, 25m], Expenses.Split(later, dinner).Shares.Select(s => s.Amount));
    }

    [Fact]
    public void SplitsByAmountShareAndPercentDivideExactly()
    {
        var hotel = Expense("Hotel", "300.00", "EUR", Ben);
        var trip = Trip(hotel);
        var byAmount = Expenses.WithSplit(hotel, SplitMethod.Amount, [(Ana.Id, 150m), (Ben.Id, 100m), (Cleo.Id, 50m)]);
        Assert.Equal("amount", byAmount.Data["split"]!["method"]!.ToString());
        Assert.Equal("150.00", byAmount.Data["split"]!["parts"]![0]!["value"]!.ToString());
        Assert.Empty(SchemaValidation.Validate(byAmount));
        var split = Expenses.Split(With(trip, byAmount), byAmount);
        Assert.Equal([150m, 100m, 50m], split.Shares.Select(s => s.Amount));
        Assert.Equal([(Ana.Id, 150m), (Cleo.Id, 50m)], split.Debts.Select(d => (d.From, d.Amount)));

        // A couple counts twice.
        var byShare = Expenses.WithSplit(hotel, SplitMethod.Share, [(Ana.Id, 2m), (Ben.Id, 1m)]);
        Assert.Equal([200m, 100m], Expenses.Split(With(trip, byShare), byShare).Shares.Select(s => s.Amount));
        Assert.Null(Expenses.Split(With(trip, byShare), byShare).Shares.FirstOrDefault(s => s.Person == Cleo.Id));

        var byPercent = Expenses.WithSplit(hotel, SplitMethod.Percent, [(Ana.Id, 50m), (Ben.Id, 25m), (Cleo.Id, 25m)]);
        Assert.Equal([150m, 75m, 75m], Expenses.Split(With(trip, byPercent), byPercent).Shares.Select(s => s.Amount));

        // Equally among some people only.
        var some = Expenses.WithSplit(hotel, SplitMethod.Equal, [(Ana.Id, null), (Ben.Id, null)]);
        Assert.Null(some.Data["split"]!["parts"]![0]!["value"]);
        Assert.Equal([150m, 150m], Expenses.Split(With(trip, some), some).Shares.Select(s => s.Amount));
        // Back to everyone equally: the default needs no split at all.
        Assert.Null(Expenses.WithSplit(some, SplitMethod.Equal).Data["split"]);
    }

    [Fact]
    public void CurrenciesRoundToTheirOwnSmallestUnit()
    {
        var ramen = Expense("Ramen", "1000", "JPY", Ana);
        var split = Expenses.Split(Trip(ramen), ramen);
        Assert.Equal([334m, 333m, 333m], split.Shares.Select(s => s.Amount));
        Assert.Equal("1000", Expenses.Format(1000m, "JPY")); Assert.Equal("12.50", Expenses.Format(12.5m, "EUR")); Assert.Equal("1.250", Expenses.Format(1.25m, "KWD"));
        Assert.Equal(0, Expenses.MinorDigits("JPY")); Assert.Equal(2, Expenses.MinorDigits("NZD"));

        // An amount typed with more digits than the currency has still adds up.
        var fuel = Expense("Fuel", "10.005", "EUR", Ana);
        Assert.Equal(10.005m, Expenses.Split(Trip(fuel), fuel).Shares.Sum(s => s.Amount));
    }

    [Fact]
    public void SplitsThatDoNotAddUpAreWarningsAndDivideNothing()
    {
        var taxi = Expense("Taxi", "30.00", "EUR", Ana);
        var trip = Trip(taxi);
        var tooLittle = Expenses.WithSplit(taxi, SplitMethod.Amount, [(Ben.Id, 10m), (Cleo.Id, 10m)]);
        var split = Expenses.Split(With(trip, tooLittle), tooLittle);
        Assert.True(split.Issues.HasFlag(SplitIssues.SumMismatch)); Assert.False(split.Divides); Assert.Empty(split.Debts);
        Assert.Contains(SemanticValidation.Validate(With(trip, tooLittle)), d => d.Code == "expense.split_sum" && d.Severity == Severity.Warning);

        var percent = Expenses.WithSplit(taxi, SplitMethod.Percent, [(Ben.Id, 50m), (Cleo.Id, 40m)]);
        Assert.Contains(SemanticValidation.Validate(With(trip, percent)), d => d.Code == "expense.split_sum");
        var negative = Expenses.WithSplit(taxi, SplitMethod.Share, [(Ben.Id, -1m), (Cleo.Id, 2m)]);
        Assert.Contains(SemanticValidation.Validate(With(trip, negative)), d => d.Code == "expense.split_value");
        Assert.Throws<DomainException>(() => Expenses.WithSplit(taxi, SplitMethod.Amount, [(Ben.Id, null)]));

        // Estimates and expenses nobody paid yet are not divided.
        var estimate = taxi.Copy(); estimate.Data["estimated"] = true;
        Assert.True(Expenses.Split(trip, estimate).Issues.HasFlag(SplitIssues.Estimated)); Assert.Empty(Expenses.Split(trip, estimate).Debts);
        var unpaid = taxi.Copy(); unpaid.Data.Remove("paid_by");
        Assert.True(Expenses.Split(trip, unpaid).Issues.HasFlag(SplitIssues.NoPayer));
        Assert.Equal(10m, Expenses.Split(trip, unpaid).ShareOf(Ben.Id));
    }

    [Fact]
    public void AnUnknownMethodIsKeptAndOnlyNoted()
    {
        var taxi = Expense("Taxi", "30.00", "EUR", Ana);
        taxi.Data["split"] = new JsonObject { ["method"] = "org.example.by_distance", ["parts"] = new JsonArray(new JsonObject { ["person"] = Ben.Id.ToString(), ["km"] = 12 }), ["note"] = "by km" };
        var trip = Trip(taxi);
        Assert.Empty(SchemaValidation.Validate(taxi));
        var split = Expenses.Split(trip, taxi);
        Assert.True(split.Issues.HasFlag(SplitIssues.UnknownMethod)); Assert.Null(split.Method); Assert.Empty(split.Debts);
        Assert.Contains(SemanticValidation.Validate(trip), d => d.Code == "expense.split_method" && d.Severity == Severity.Info);
        var back = YamlCodec.Read(YamlCodec.Write(taxi));
        Assert.True(JsonNode.DeepEquals(taxi.Data, back.Data));

        // Changing the split keeps what this version does not know about it.
        var changed = Expenses.WithSplit(taxi, SplitMethod.Share, [(Ben.Id, 1m), (Cleo.Id, 1m)]);
        Assert.Equal("by km", changed.Data["split"]!["note"]!.ToString());
        Assert.Equal(12, changed.Data["split"]!["parts"]![0]!["km"]!.GetValue<int>());
    }

    [Fact]
    public void SeveralPayersAreOwedInProportionToWhatTheyPaidAboveTheirPart()
    {
        // Ana and Ben paid 60.00 each; everyone's part is 40.00, so Cleo owes each of them 20.00.
        var car = Expense("Rental car", "120.00", "EUR", Ana, Ben);
        var split = Expenses.Split(Trip(car), car);
        Assert.Equal([Ana.Id, Ben.Id], split.Payers);
        Assert.Equal([(Cleo.Id, Ana.Id, 20m), (Cleo.Id, Ben.Id, 20m)], split.Debts.Select(d => (d.From, d.To, d.Amount)));
    }

    [Fact]
    public void SharesAndWholeExpensesCanBeMarkedAsPaid()
    {
        var dinner = Expense("Dinner", "90.00", "EUR", Ana);
        var trip = Trip(dinner);
        var day = new LocalDate(2027, 6, 12);

        var benPaid = Expenses.Settle(trip, dinner, Ben.Id, day);
        Assert.Empty(SchemaValidation.Validate(benPaid));
        var entry = Assert.Single((JsonArray)benPaid.Data["settled"]!)!;
        Assert.Equal(Ben.Id.ToString(), entry["person"]!.ToString()); Assert.Equal(Ana.Id.ToString(), entry["to"]!.ToString());
        Assert.Equal("30.00", entry["value"]!.ToString()); Assert.Equal("2027-06-12", entry["date"]!.ToString());
        var split = Expenses.Split(With(trip, benPaid), benPaid);
        Assert.True(split.Debts.Single(d => d.From == Ben.Id).IsSettled); Assert.Equal(day, split.Debts.Single(d => d.From == Ben.Id).SettledOn);
        Assert.Equal(30m, split.Open); Assert.False(split.IsSettled);

        var allPaid = Expenses.Settle(With(trip, benPaid), benPaid);
        Assert.True(Expenses.Split(With(trip, allPaid), allPaid).IsSettled);
        Assert.Empty(Expenses.Open(With(trip, allPaid)));

        var undone = Expenses.Unsettle(allPaid, Cleo.Id);
        Assert.Equal([Cleo.Id], Expenses.Open(With(trip, undone)).Select(d => d.From));
        Assert.Null(Expenses.Unsettle(undone).Data["settled"]);
        Assert.Throws<DomainException>(() => Expenses.Settle(trip, [new DebtKey(dinner.Id, Ana.Id, Ben.Id)]));
    }

    [Fact]
    public void ChangingTheSplitAfterPayingShowsWhatIsStillOwedOrOwedBack()
    {
        var dinner = Expense("Dinner", "90.00", "EUR", Ana);
        var trip = Trip(dinner);
        var paid = Expenses.Settle(trip, dinner);
        // Ben had paid 30.00 back, but now his part is 50.00 and Cleo's only 10.00.
        var changed = Expenses.WithSplit(paid, SplitMethod.Amount, [(Ana.Id, 30m), (Ben.Id, 50m), (Cleo.Id, 10m)]);
        var split = Expenses.Split(With(trip, changed), changed);
        Assert.Equal(20m, split.Debts.Single(d => d.From == Ben.Id).Open);
        Assert.Equal(-20m, split.Debts.Single(d => d.From == Cleo.Id).Open);
        var balances = Expenses.Balances(With(trip, changed));
        Assert.Equal(0, balances.Where(b => b.Person == Ana.Id).Sum(b => b.Net));
        Assert.Equal([new Transfer(Ben.Id, Cleo.Id, "EUR", 20m)], Expenses.SettleUp(With(trip, changed)));

        // Leaving Cleo out entirely: what she paid is owed back to her, and paying it back clears the mark.
        var without = Expenses.WithSplit(paid, SplitMethod.Equal, [(Ana.Id, null), (Ben.Id, null)]);
        var refund = Expenses.Split(With(trip, without), without).Debts.Single(d => d.From == Cleo.Id);
        Assert.Equal((0m, 30m, -30m), (refund.Amount, refund.Settled, refund.Open));
        var refunded = Expenses.Settle(With(trip, without), [refund.Key]).Single();
        Assert.DoesNotContain(Expenses.Split(With(trip, refunded), refunded).Debts, d => d.From == Cleo.Id);
    }

    [Fact]
    public void DebtsAreSetOffIntoTheFewestPayments()
    {
        // Ana paid 90 for all, Ben 60 for all, Cleo nothing: Cleo owes 50, Ana is owed 40 and Ben 10.
        var a = Expense("Dinner", "90.00", "EUR", Ana); var b = Expense("Museum", "60.00", "EUR", Ben);
        var trip = Trip(a, b);
        Assert.Equal([(Ana.Id, 40m), (Ben.Id, 10m), (Cleo.Id, -50m)], Expenses.Balances(trip).Select(x => (x.Person, x.Net)));
        Assert.Equal([new Transfer(Cleo.Id, Ana.Id, "EUR", 40m), new Transfer(Cleo.Id, Ben.Id, "EUR", 10m)], Expenses.SettleUp(trip));

        // A chain Ana -> Ben -> Cleo is one payment instead of two.
        var c1 = Expenses.WithSplit(Expense("Tickets", "20.00", "EUR", Ben), SplitMethod.Amount, [(Ana.Id, 20m)]);
        var c2 = Expenses.WithSplit(Expense("Snacks", "20.00", "EUR", Cleo), SplitMethod.Amount, [(Ben.Id, 20m)]);
        Assert.Equal([new Transfer(Ana.Id, Cleo.Id, "EUR", 20m)], Expenses.SettleUp(Trip(c1, c2)));

        // Each currency is settled on its own; nothing is converted.
        var yen = Expense("Ramen", "3000", "JPY", Cleo);
        Assert.Contains(Expenses.SettleUp(Trip(a, yen)), t => t.Currency == "JPY" && t.To == Cleo.Id && t.Amount == 1000m);

        // Exact matches pay each other directly: 30 to 30 and 10 to 10 instead of three payments.
        var balances = new[] { new ExpenseBalance(Ana.Id, "EUR", 30m), new ExpenseBalance(Ben.Id, "EUR", 10m), new ExpenseBalance(Cleo.Id, "EUR", -10m), new ExpenseBalance(Guid.Parse("0195f3e0-0000-7000-8000-000000000001"), "EUR", -30m) };
        Assert.Equal(2, Expenses.Minimize(balances).Count);

        // Once everyone has paid, everything can be marked as paid at once.
        Assert.Empty(Expenses.SettleUp(With(trip, Expenses.SettleAll(trip).ToArray())));
    }

    [Fact]
    public void SettlingUpAlwaysBalancesEveryoneWithFewerPaymentsThanPeople()
    {
        var random = new Random(20270612);
        var people = Enumerable.Range(0, 7).Select(i => Entity.Create("person", "P" + i)).ToArray();
        for (var round = 0; round < 200; round++)
        {
            var expenses = Enumerable.Range(0, random.Next(1, 12)).Select(i =>
            {
                var payer = people[random.Next(people.Length)];
                var e = Expense("E" + i, (random.Next(1, 50000) / 100m).ToString("F2", System.Globalization.CultureInfo.InvariantCulture), random.Next(4) == 0 ? "JPY" : "EUR", payer);
                if (e.Data["amount"]!["currency"]!.ToString() == "JPY") e.Data["amount"]!["value"] = random.Next(100, 90000).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var among = people.Where(_ => random.Next(2) == 0).ToArray();
                return random.Next(3) switch
                {
                    0 => e,
                    1 when among.Length > 0 => Expenses.WithSplit(e, SplitMethod.Share, among.Select(p => (p.Id, (decimal?)random.Next(1, 4)))),
                    _ when among.Length > 0 => Expenses.WithSplit(e, SplitMethod.Equal, among.Select(p => (p.Id, (decimal?)null))),
                    _ => e
                };
            }).ToArray();
            var trip = new TripSnapshot(Entity.CreateTrip("Random"), expenses.Concat(people).ToDictionary(e => e.Id));
            foreach (var split in Expenses.All(trip)) Assert.Equal(split.Amount!.Amount, split.Shares.Sum(s => s.Amount));
            var balances = Expenses.Balances(trip);
            foreach (var currency in balances.GroupBy(b => b.Currency)) Assert.Equal(0, currency.Sum(b => b.Net));
            var transfers = Expenses.SettleUp(trip);
            foreach (var currency in balances.Select(b => b.Currency).Distinct())
            {
                var involved = balances.Count(b => b.Currency == currency);
                Assert.True(transfers.Count(t => t.Currency == currency) <= Math.Max(0, involved - 1));
            }
            foreach (var b in balances) Assert.Equal(b.Net, transfers.Where(t => t.Currency == b.Currency && t.To == b.Person).Sum(t => t.Amount) - transfers.Where(t => t.Currency == b.Currency && t.From == b.Person).Sum(t => t.Amount));
            Assert.All(transfers, t => Assert.True(t.Amount > 0));
        }
    }

    [Fact]
    public void ThePayFunctionAddsUpOnlyTheChosenOpenItems()
    {
        var dinner = Expense("Dinner", "90.00", "EUR", Ana);   // Ben owes Ana 30
        var taxi = Expense("Taxi", "30.00", "EUR", Ben);       // Ana owes Ben 10
        var museum = Expense("Museum", "60.00", "EUR", Cleo);  // Ben owes Cleo 20
        var ferry = Expense("Ferry", "45.00", "NZD", Ana);     // Ben owes Ana 15 NZD
        var trip = Trip(dinner, taxi, museum, ferry);

        var items = Expenses.Open(trip, Ben.Id);
        Assert.Equal(5, items.Count);
        Assert.DoesNotContain(items, d => d.From != Ben.Id && d.To != Ben.Id);

        // Ben settles the dinner and the taxi with Ana now; they set off into one payment. The museum waits.
        var chosen = items.Where(d => d.Expense == dinner.Id || d.Expense == taxi.Id && d.From == Ana.Id).Select(d => d.Key).ToArray();
        var plan = Expenses.Pay(trip, Ben.Id, chosen);
        Assert.Equal([new Transfer(Ben.Id, Ana.Id, "EUR", 20m)], plan.Payments);
        Assert.Equal(20m, plan.Totals["EUR"]); Assert.Equal(2, plan.Items.Count);

        // Everything at once: one payment per person and currency.
        var all = Expenses.Pay(trip, Ben.Id, items.Select(d => d.Key));
        Assert.Equal([new Transfer(Ben.Id, Ana.Id, "EUR", 20m), new Transfer(Ben.Id, Cleo.Id, "EUR", 10m), new Transfer(Ben.Id, Ana.Id, "NZD", 15m)], all.Payments);
        Assert.Equal(30m, all.Totals["EUR"]); Assert.Equal(15m, all.Totals["NZD"]);

        // When the others owe more, they pay: the total is negative.
        var owedToBen = Expenses.Pay(trip, Ben.Id, items.Where(d => d.To == Ben.Id).Select(d => d.Key));
        Assert.Equal([new Transfer(Ana.Id, Ben.Id, "EUR", 10m), new Transfer(Cleo.Id, Ben.Id, "EUR", 10m)], owedToBen.Payments);
        Assert.Equal(-20m, owedToBen.Totals["EUR"]);

        // Items of other people and items already paid are ignored.
        var cleoOwesAna = Expenses.Open(trip, Cleo.Id).First(d => d.To == Ana.Id).Key;
        Assert.Empty(Expenses.Pay(trip, Ben.Id, [cleoOwesAna]).Items);
        var settled = With(trip, Expenses.Settle(trip, plan.Items.Select(i => i.Key), new LocalDate(2027, 6, 12)).ToArray());
        Assert.Empty(Expenses.Pay(settled, Ben.Id, chosen).Payments);
        Assert.Equal(3, Expenses.Open(settled, Ben.Id).Count);
        // What stays open is what was not chosen.
        Assert.Equal([new Transfer(Ben.Id, Cleo.Id, "EUR", 10m), new Transfer(Ben.Id, Ana.Id, "NZD", 15m)], Expenses.Pay(settled, Ben.Id, Expenses.Open(settled, Ben.Id).Select(d => d.Key)).Payments);
    }

    [Fact]
    public void PeoplePayingBackAtTheSameTimeMergeWithoutConflict()
    {
        var dinner = Expense("Dinner", "90.00", "EUR", Ana);
        var trip = Trip(dinner);
        var ours = Expenses.Settle(trip, dinner, Ben.Id); var theirs = Expenses.Settle(trip, dinner, Cleo.Id);
        var plan = SemanticMerge.Plan(trip, With(trip, ours), With(trip, theirs));
        Assert.Empty(plan.Conflicts);
        Assert.True(Expenses.Split(plan.Result, plan.Result.Find(dinner.Id)!).IsSettled);

        // Two different amounts for the same payment need a decision.
        var a = ours.Copy(); a.Data["settled"]![0]!["value"] = "10.00";
        var conflict = Assert.Single(SemanticMerge.Plan(With(trip, ours), With(trip, a), With(trip, Expenses.Unsettle(ours))).Conflicts);
        Assert.Equal(ConflictKind.DeleteEdit, conflict.Kind);

        // Parts edited for different people on two devices merge too.
        var split = Expenses.WithSplit(dinner, SplitMethod.Share, [(Ana.Id, 1m), (Ben.Id, 1m), (Cleo.Id, 1m)]);
        var anaTwo = Expenses.WithSplit(split, SplitMethod.Share, [(Ana.Id, 2m), (Ben.Id, 1m), (Cleo.Id, 1m)]);
        var cleoOut = Expenses.WithSplit(split, SplitMethod.Share, [(Ana.Id, 1m), (Ben.Id, 1m)]);
        var merged = SemanticMerge.Plan(With(trip, split), With(trip, anaTwo), With(trip, cleoOut));
        Assert.Empty(merged.Conflicts);
        Assert.Equal([60m, 30m], Expenses.Split(merged.Result, merged.Result.Find(dinner.Id)!).Shares.Select(s => s.Amount));
    }

    [Fact]
    public void DeletingAPersonRemovesTheirPartsAndPayments()
    {
        var raw = Expenses.WithSplit(Expense("Dinner", "90.00", "EUR", Ana), SplitMethod.Share, [(Ana.Id, 1m), (Ben.Id, 1m), (Cleo.Id, 1m)]);
        var dinner = Expenses.Settle(Trip(raw), raw, Cleo.Id);
        var trip = Trip(dinner);
        Assert.DoesNotContain(SemanticValidation.Validate(trip), d => d.Severity == Severity.Error);
        var deletion = TripDeletion.Plan(trip, [Cleo.Id]);
        var updated = Assert.Single(deletion.Updated, e => e.Id == dinner.Id);
        Assert.Equal([Ana.Id.ToString(), Ben.Id.ToString()], ((JsonArray)updated.Data["split"]!["parts"]!).Select(p => p!["person"]!.ToString()));
        Assert.Empty(updated.Data["settled"] as JsonArray ?? []);
        Assert.Empty(SchemaValidation.Validate(updated));

        // A part that names nobody who exists is a broken reference.
        var broken = trip with { Entities = trip.Entities.Values.Where(e => e.Id != Cleo.Id).ToDictionary(e => e.Id) };
        Assert.Contains(SemanticValidation.Validate(broken), d => d.Code == "reference.missing" && d.Path.StartsWith(dinner.Id + "/split", StringComparison.Ordinal));
        Assert.Contains(SemanticValidation.Validate(broken), d => d.Code == "reference.missing" && d.Path.StartsWith(dinner.Id + "/settled", StringComparison.Ordinal));
        Assert.Contains(TripSummaries.ReferencesTo(trip, Cleo.Id), e => e.Id == dinner.Id);
    }

    [Fact]
    public async Task TheMultiPersonFixtureShowsWhoOwesWhom()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory); while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "samples"))) d = d.Parent;
        var state = await new TravelRepository(Path.Combine(d!.FullName, "samples", "multi-person")).ReadAsync();
        Assert.DoesNotContain(state.Diagnostics, x => x.Severity == Severity.Error || x.Code.StartsWith("expense", StringComparison.Ordinal));
        var trip = state.Trip;
        var alex = trip.Entities.Values.Single(e => e.Title == "Alex").Id; var carla = trip.Entities.Values.Single(e => e.Title == "Carla").Id;
        var boat = Expenses.All(trip).Single(s => s.Expense.Title == "Boat hire");
        Assert.True(boat.IsSettled); Assert.Equal(new LocalDate(2027, 6, 12), boat.Debts.Single().SettledOn);
        Assert.Equal([new Transfer(carla, alex, "EUR", 45m)], Expenses.SettleUp(trip));
        foreach (var expense in trip.Entities.Values.Where(e => e.Type == "expense")) Assert.Equal(YamlCodec.Write(expense), YamlCodec.Write(YamlCodec.Read(YamlCodec.Write(expense))));
    }
    [Fact]
    public void ChangingTheMethodKeepsTheCurrentDivision()
    {
        var hotel = Expense("Hotel", "100.00", "EUR", Ben);
        var trip = Trip(hotel);
        var byAmount = Expenses.ChangeMethod(trip, hotel, SplitMethod.Amount);
        Assert.Equal(["33.34", "33.33", "33.33"], ((JsonArray)byAmount.Data["split"]!["parts"]!).Select(p => p!["value"]!.ToString()));
        var byPercent = Expenses.ChangeMethod(With(trip, byAmount), byAmount, SplitMethod.Percent);
        Assert.Equal(["33.34", "33.33", "33.33"], ((JsonArray)byPercent.Data["split"]!["parts"]!).Select(p => p!["value"]!.ToString()));
        Assert.Equal(100m, Expenses.Split(With(trip, byPercent), byPercent).Assigned);
        var byShare = Expenses.ChangeMethod(trip, byPercent, SplitMethod.Share);
        Assert.Equal(["1", "1", "1"], ((JsonArray)byShare.Data["split"]!["parts"]!).Select(p => p!["value"]!.ToString()));
        // Back to equal, the three stay named, so a traveller added later does not join.
        var equal = Expenses.ChangeMethod(With(trip, byShare), byShare, SplitMethod.Equal);
        Assert.Equal(("equal", 3), (equal.Data["split"]!["method"]!.ToString(), ((JsonArray)equal.Data["split"]!["parts"]!).Count));

        // Leaving someone out, then giving an amount: the parts tell how much is still to divide.
        var twoOnly = Expenses.WithPerson(trip, hotel, Cleo.Id, false);
        Assert.Equal([Ana.Id, Ben.Id], Expenses.Split(With(trip, twoOnly), twoOnly).People);
        Assert.Equal([Ana.Id, Ben.Id, Cleo.Id], Expenses.Split(trip, Expenses.WithPerson(With(trip, twoOnly), twoOnly, Cleo.Id, true)).People);
        var amounts = Expenses.ChangeMethod(With(trip, twoOnly), twoOnly, SplitMethod.Amount);
        var changed = Expenses.WithValue(With(trip, amounts), amounts, Ana.Id, 70m);
        var split = Expenses.Split(With(trip, changed), changed);
        Assert.Equal((120m, 100m), (split.Assigned, split.Target!.Value)); Assert.True(split.Issues.HasFlag(SplitIssues.SumMismatch));
        Assert.Equal([Ana.Id, Ben.Id], split.People);
        var withCleo = Expenses.WithPerson(With(trip, changed), changed, Cleo.Id, true);
        Assert.Equal("0.00", ((JsonArray)withCleo.Data["split"]!["parts"]!)[2]!["value"]!.ToString());
        Assert.Throws<DomainException>(() => Expenses.WithValue(trip, hotel, Ana.Id, 10m));
    }
    [Fact]
    public void TheAmountChargedOnACardSettlesInEuros()
    {
        // Ana paid 340 NZD on her card, which charged 190.40 EUR; Ben paid 81 NZD for dinner without a statement yet.
        var tour = Expense("Hobbiton", "340.00", "NZD", Ana); var dinner = Expense("Dinner", "81.00", "NZD", Ben);
        var trip = Trip(tour, dinner);
        var charged = Expenses.WithCharge(tour, 190.40m, "eur", new LocalDate(2026, 11, 26));
        Assert.Empty(SchemaValidation.Validate(charged));
        var converted = (JsonObject)charged.Data["converted"]!;
        Assert.Equal(("190.40", "EUR", "0.56", "2026-11-26", "card"), (converted["value"]!.ToString(), converted["currency"]!.ToString(), converted["rate"]!.ToString(), converted["rate_date"]!.ToString(), converted["source"]!.ToString()));
        Assert.Equal(new Charge(190.40m, "EUR", 0.56m, new LocalDate(2026, 11, 26), "card"), Expenses.ChargeOf(charged));
        Assert.Throws<DomainException>(() => Expenses.WithCharge(tour, 190m, "NZD"));
        trip = With(trip, charged);

        // A third of the tour in euros is exact; without rates the dinner stays in NZD.
        Assert.Equal((63.46m, "EUR", Conversion.Charged), Expenses.Convert(charged, 113.33m, "NZD", "EUR"));
        var plain = Expenses.SettleUp(trip, "EUR");
        Assert.Contains(plain, t => t.Currency == "EUR" && !t.Estimated);
        Assert.Contains(plain, t => t.Currency == "NZD");
        var total = Expenses.Total(trip, "EUR");
        Assert.Equal((190.40m, 0m, 1), (total.Exact, total.Estimated, total.WithoutCharge));
        Assert.Equal([new Money("81.00", "NZD")], total.NotConverted);

        // With reference rates the rest is an estimate and says so.
        var rates = new ExchangeRates(new DateOnly(2026, 11, 20), new Dictionary<string, decimal> { ["NZD"] = 1.80m });
        var all = Expenses.SettleUp(trip, "EUR", rates);
        Assert.All(all, t => Assert.Equal("EUR", t.Currency));
        Assert.All(all, t => Assert.True(t.Estimated));
        var withRates = Expenses.Total(trip, "EUR", rates);
        Assert.Equal((190.40m, 45.00m, 1, 0), (withRates.Exact, withRates.Estimated, withRates.WithoutCharge, withRates.NotConverted.Count));
        foreach (var currency in Expenses.Balances(trip, "EUR", rates).GroupBy(b => b.Currency)) Assert.Equal(0, currency.Sum(b => b.Net));

        // Once the dinner is charged too, everything is exact and the pay function works in euros.
        trip = With(trip, Expenses.WithCharge(dinner, 45.36m, "EUR"));
        Assert.All(Expenses.SettleUp(trip, "EUR", rates), t => Assert.False(t.Estimated));
        var pay = Expenses.Pay(trip, Cleo.Id, Expenses.Open(trip, Cleo.Id).Select(d => d.Key), "EUR");
        Assert.Equal([new Transfer(Cleo.Id, Ana.Id, "EUR", 63.46m), new Transfer(Cleo.Id, Ben.Id, "EUR", 15.12m)], pay.Payments);
        Assert.Equal(78.58m, pay.Totals["EUR"]);
        Assert.Null(Expenses.WithoutCharge(charged).Data["converted"]);

        // Fields of converted this version does not know are kept.
        var extended = charged.Copy(); extended.Data["converted"]!["org.example.card"] = "Visa";
        Assert.Equal("Visa", Expenses.WithCharge(extended, 191m, "EUR").Data["converted"]!["org.example.card"]!.ToString());
    }
    [Fact]
    public void ABookingsPaymentsSayWhatItCostsAndWhoSharesThem()
    {
        var hotel = Entity.Create("booking", "Lakeside lodge"); hotel.Data["status"] = "confirmed";
        hotel.Data["travelers"] = new JsonArray(Ana.Id.ToString(), Ben.Id.ToString());
        var trip = Trip(hotel);
        Assert.Equal(BookingCoverage.None, Expenses.Payment(trip, hotel).Coverage);

        // A deposit is paid now, the rest is still to pay on site; both are shared by the booking's travellers only.
        var deposit = Expenses.FromBooking(trip, hotel, new Money("100.00", "NZD"), payers: [Ana.Id]);
        var rest = Expenses.FromBooking(trip, hotel, new Money("220.00", "NZD"), planned: true);
        Assert.Empty(SchemaValidation.Validate(deposit)); Assert.Empty(SchemaValidation.Validate(rest));
        trip = With(trip, deposit, rest);
        Assert.Equal([Ana.Id, Ben.Id], Expenses.Split(trip, deposit).People);
        Assert.Equal([(Ben.Id, Ana.Id, 50m)], Expenses.Split(trip, deposit).Debts.Select(d => (d.From, d.To, d.Amount)));
        var payment = Expenses.Payment(trip, hotel);
        Assert.Equal((BookingCoverage.Planned, 1, 1), (payment.Coverage, payment.Paid.Count, payment.Planned.Count));
        // What is still to pay is planned: no debts yet, and the costs list it apart from what is paid.
        Assert.Empty(Expenses.Split(trip, rest).Debts);
        Assert.Equal((100m, 220.00m), (TripSummaries.Costs(trip).Single().Paid, TripSummaries.Costs(trip).Single().Estimated));

        trip = With(trip, Expenses.MarkPaid(rest, [Ben.Id]));
        Assert.Equal(BookingCoverage.Paid, Expenses.Payment(trip, hotel).Coverage);
        Assert.Equal([Ben.Id], Expenses.Split(trip, trip.Find(rest.Id)!).Payers);
        Assert.Throws<DomainException>(() => Expenses.FromBooking(trip, hotel));
    }

    [Fact]
    public void ANewExpenseNamesItsPeopleSoALaterTravellerDoesNotJoinIt()
    {
        var walk = Entity.Create("schedule_item", "Glow worm walk"); walk.Data["participants"] = new JsonArray(Ben.Id.ToString(), Cleo.Id.ToString());
        var trip = Trip(walk);
        var guide = Expenses.Create(trip, "Guide", new Money("60.00", "NZD"), [Ben.Id], walk.Id);
        var dinner = Expenses.Create(trip, "Dinner", new Money("90.00", "NZD"), [Ana.Id]);
        Assert.Equal([Ben.Id, Cleo.Id], Expenses.Split(Trip(walk, guide), guide).People);
        Assert.Equal([Ana.Id, Ben.Id, Cleo.Id], Expenses.Split(Trip(dinner), dinner).People);
        Assert.False(Expenses.SharesWithEveryone(dinner));
        // A JPY amount has no decimals, also when it is created from a number with them.
        Assert.Equal("1000", Expenses.Create(trip, "Ramen", new Money("1000.00", "JPY"), [Ana.Id]).Data["amount"]!["value"]!.ToString());

        // An expense from an older client names nobody; before Dana joins it is fixed to the three, so what Ben and
        // Cleo paid back stays exactly what they owed.
        var old = Expense("Day 1 dinner", "90.00", "EUR", Ana);
        trip = Trip(old, dinner); old = Expenses.SettleAll(trip).Single(e => e.Id == old.Id); trip = With(trip, old);
        var named = Expenses.FixShares(trip);
        Assert.Equal([old.Id], named.Select(e => e.Id));
        var dana = Entity.Create("person", "Dana");
        var manifest = trip.Manifest.Copy(); ((JsonArray)manifest.Data["participants"]!).Add(dana.Id.ToString());
        var later = new TripSnapshot(manifest, trip.Entities.Values.Where(e => e.Id != old.Id).Concat(named).Append(dana).ToDictionary(e => e.Id));
        Assert.True(Expenses.Split(later, later.Find(old.Id)!).IsSettled);
        Assert.DoesNotContain(Expenses.Open(later), d => d.Expense == old.Id || d.From == dana.Id);
        Assert.Empty(Expenses.FixShares(later));

        // Changing the method or who shares it keeps the people named; the last one cannot leave.
        var two = Expenses.WithPerson(later, later.Find(dinner.Id)!, Cleo.Id, false);
        Assert.Equal([Ana.Id, Ben.Id], Expenses.Split(later, two).People);
        Assert.Equal([Ana.Id, Ben.Id], Expenses.Split(later, Expenses.ChangeMethod(later, Expenses.ChangeMethod(later, two, SplitMethod.Share), SplitMethod.Equal)).People);
        var alone = Expenses.WithPerson(later, two, Ben.Id, false);
        Assert.Throws<DomainException>(() => Expenses.WithPerson(later, alone, Ana.Id, false));
    }

    [Fact]
    public void ExpensesWhoseSplitNeedsAttentionAreNamed()
    {
        var boat = Expenses.WithSplit(Expense("Boat", "60.00", "EUR", Ana), SplitMethod.Amount, [(Ana.Id, 20m), (Ben.Id, 20m), (Cleo.Id, 20m)]);
        var trip = Trip(boat);
        Assert.Empty(Expenses.Problems(trip));
        var corrected = boat.Copy(); corrected.Data["amount"]!["value"] = "75.00";
        Assert.Equal([boat.Id], Expenses.Problems(With(trip, corrected)).Select(p => p.Expense.Id));
        // A planned one is not divided yet, so it is no problem yet.
        corrected.Data["estimated"] = true;
        Assert.Empty(Expenses.Problems(With(trip, corrected)));
    }

    [Fact]
    public void ExpensesHaveStandardOrOwnCategoriesOrFollowTheirPlans()
    {
        var stay = Entity.Create("schedule_item", "Lakeside Lodge"); stay.Data["components"] = new JsonObject { ["accommodation"] = new JsonObject() };
        var hotel = Entity.Create("booking", "Lakeside Lodge"); hotel.Data["items"] = new JsonArray(stay.Id.ToString());
        var lodging = Expense("Lodge", "320.00", "NZD", Ana); lodging.Data["related"] = new JsonArray(new JsonObject { ["type"] = "booking", ["id"] = hotel.Id.ToString() });
        var kiwi = Expense("Kiwi plush", "35.00", "NZD", Ben);
        var trip = Trip(stay, hotel, lodging, kiwi);

        // Without a category, the booking's stay suggests one; otherwise it is other.
        Assert.Equal("accommodation", ExpenseCategories.Of(trip, lodging));
        Assert.Equal("other", ExpenseCategories.Of(trip, kiwi));
        var souvenir = ExpenseCategories.With(trip, kiwi, "SOUVENIRS");
        Assert.Equal("souvenirs", souvenir.Data["category"]!.ToString());
        Assert.Empty(SchemaValidation.Validate(souvenir));

        // A category of their own is shared through the trip and reused regardless of case.
        var fees = ExpenseCategories.With(trip, Expense("Park entry", "20.00", "NZD", Ana), " Eintritte ");
        trip = With(trip, souvenir, fees);
        Assert.Equal(["Eintritte"], ExpenseCategories.Custom(trip));
        Assert.Equal("Eintritte", ExpenseCategories.With(trip, Expense("Museum", "25.00", "NZD", Ben), "eintritte").Data["category"]!.ToString());
        Assert.Null(ExpenseCategories.With(trip, souvenir, "").Data["category"]);

        Assert.Equal([("accommodation", 320.00m), ("souvenirs", 35m), ("Eintritte", 20m)], ExpenseCategories.Totals(trip).Select(t => (t.Category, t.Amount)));
        var rates = new ExchangeRates(new DateOnly(2026, 11, 20), new Dictionary<string, decimal> { ["NZD"] = 2m });
        var inEuros = ExpenseCategories.Totals(With(trip, Expenses.WithCharge(lodging, 190m, "EUR")), "EUR", rates);
        Assert.Equal((190m, false), (inEuros.Single(t => t.Category == "accommodation").Amount, inEuros.Single(t => t.Category == "accommodation").Estimated));
        Assert.True(inEuros.Single(t => t.Category == "souvenirs").Estimated);
    }

    [Fact]
    public void BudgetsCoverTheTripACategoryAPersonOrACollection()
    {
        Entity Budget(string title, string value, string currency) { var b = Entity.Create("budget", title); b.Data["amount"] = new JsonObject { ["value"] = value, ["currency"] = currency }; return b; }
        var dinner = ExpenseCategories.With(Trip(), Expense("Dinner", "90.00", "EUR", Ana), "food");
        var ferry = ExpenseCategories.With(Trip(), Expense("Ferry", "200.00", "NZD", Ben), "transport");
        ferry = Expenses.WithCharge(ferry, 112m, "EUR");
        var snacks = ExpenseCategories.With(Trip(), Expense("Snacks", "30.00", "NZD", Cleo), "food"); snacks.Data["estimated"] = true;
        var whole = Budget("Trip", "1000.00", "EUR"); var food = Budget("Eating out", "100.00", "EUR"); var ana = Budget("Ana's money", "100.00", "EUR"); var day = Budget("Day trip", "150.00", "EUR");
        var collection = Entity.Create("collection", "Day trip"); collection.Data["items"] = new JsonArray(ferry.Id.ToString());
        var trip = Trip(dinner, ferry, snacks, whole, food, ana, day, collection);
        food = Budgets.WithFilters(trip, food, new BudgetFilters("FOOD", null, null));
        ana = Budgets.WithFilters(trip, ana, new BudgetFilters(null, Ana.Id, null));
        day = Budgets.WithFilters(trip, day, new BudgetFilters(null, null, collection.Id));
        trip = With(trip, food, ana, day);
        Assert.All(new[] { food, ana, day }, b => Assert.Empty(SchemaValidation.Validate(b)));
        Assert.Equal(("category", "food"), (Budgets.ScopeOf(food), food.Data["category"]!.ToString()));
        Assert.Null(food.Data["scope"]);

        // Without rates the snacks in NZD cannot count in euros and are named; the ferry counts as charged.
        var status = Budgets.All(trip).ToDictionary(b => b.Budget.Title);
        Assert.Equal((202m, 0m, false), (status["Trip"].Spent, status["Trip"].Planned, status["Trip"].Approximate));
        Assert.Equal([new Money("30.00", "NZD")], status["Trip"].NotIncluded);
        Assert.Equal((90m, 10m), (status["Eating out"].Spent, status["Eating out"].Left));
        // Ana's third of the dinner, and of the ferry 66.67 NZD (she gets the cent left over) = 37.34 EUR as charged.
        Assert.Equal(30m + 37.34m, status["Ana's money"].Spent);
        Assert.Equal(112m, status["Day trip"].Spent);

        // With rates, the estimated snacks are planned spending, as an estimate.
        var rates = new ExchangeRates(new DateOnly(2026, 11, 20), new Dictionary<string, decimal> { ["NZD"] = 2m });
        var withRates = Budgets.Status(trip, food, rates)!;
        Assert.Equal((90m, 15m, true, -5m), (withRates.Spent, withRates.Planned, withRates.Approximate, withRates.Left));

        // The trip's one budget is the budget without limits, in the trip's currency; limited ones are listed apart.
        var tripBudget = Budgets.ForTrip(trip)!;
        Assert.Equal(("EUR", 1000m, 202m, 1), (tripBudget.Currency, tripBudget.Amount, tripBudget.Paid.Total, tripBudget.Budgets.Count));
        Assert.Equal(["Ana's money", "Day trip", "Eating out"], Budgets.Limited(trip).Select(b => b.Budget.Title).Order());
        Assert.All(TripSummaries.Costs(trip), c => Assert.Equal(0m, c.Budget));
        Assert.Throws<DomainException>(() => Budgets.WithFilters(trip, ana, new BudgetFilters(null, collection.Id, null)));
        // Deleting Ana leaves her budget without a person; it then covers the whole trip.
        var deletion = TripDeletion.Plan(trip, [Ana.Id]);
        var orphan = deletion.Updated.Single(e => e.Id == ana.Id);
        Assert.Null(orphan.Data["person"]);
        Assert.Contains(SemanticValidation.Validate(trip with { Entities = trip.Entities.Values.Where(e => e.Id != Ana.Id).ToDictionary(e => e.Id) }), d => d.Code == "reference.missing" && d.Path == ana.Id + "/person");
    }
    [Fact]
    public void ABudgetCanCombineACategoryAndAPerson()
    {
        var budget = Entity.Create("budget", "Ben's food"); budget.Data["amount"] = new JsonObject { ["value"] = "50.00", ["currency"] = "EUR" };
        var dinner = ExpenseCategories.With(Trip(), Expense("Dinner", "90.00", "EUR", Ana), "food");
        var lunch = Expenses.WithSplit(ExpenseCategories.With(Trip(), Expense("Lunch", "40.00", "EUR", Ben), "food"), SplitMethod.Equal, [(Ana.Id, null), (Cleo.Id, null)]);
        var taxi = ExpenseCategories.With(Trip(), Expense("Taxi", "30.00", "EUR", Ben), "transport");
        var trip = Trip(budget, dinner, lunch, taxi);
        var both = Budgets.WithFilters(trip, budget, new BudgetFilters("food", Ben.Id, null));
        Assert.Empty(SchemaValidation.Validate(both));
        // The limits say it all; no scope repeats them.
        Assert.Equal(("participant", "food", Ben.Id.ToString()), (Budgets.ScopeOf(both), both.Data["category"]!.ToString(), both.Data["person"]!.ToString()));
        Assert.Null(both.Data["scope"]);
        trip = With(trip, both);
        // Ben's third of the dinner; not the lunch he paid but does not share, not the taxi.
        var status = Budgets.Status(trip, both)!;
        Assert.Equal((30m, 20m), (status.Spent, status.Left));
        Assert.Equal(new BudgetFilters("food", Ben.Id, null), status.Filters);

        // Removing the person leaves the category alone; removing both is the whole trip again.
        var food = Budgets.WithFilters(trip, both, Budgets.FiltersOf(both) with { Person = null });
        Assert.Equal(("category", 130m), (Budgets.ScopeOf(food), Budgets.Status(With(trip, food), food)!.Spent));
        var whole = Budgets.WithFilters(trip, both, BudgetFilters.None);
        Assert.Equal("trip", Budgets.ScopeOf(whole));
        Assert.Equal(50m, Budgets.ForTrip(With(trip, whole))!.Amount);
        Assert.Null(Budgets.ForTrip(trip));
        Assert.Throws<DomainException>(() => Budgets.WithFilters(trip, budget, new BudgetFilters(null, dinner.Id, null)));
    }
}
