using System.Globalization;
using System.Text.Json.Nodes;
using NodaTime;
using NodaTime.Text;

namespace TravelRepo.Core;

/// <summary>How an expense is divided between people (specification section 34.1).</summary>
public enum SplitMethod
{
    /// <summary>Equal parts, among every traveller unless the split names people.</summary>
    Equal,
    /// <summary>Each person's part is an amount in the expense's currency.</summary>
    Amount,
    /// <summary>Each person's part is a weight, such as 2 for a couple and 1 for a single traveller.</summary>
    Share,
    /// <summary>Each person's part is a percentage; together they make 100.</summary>
    Percent
}

/// <summary>Why an expense does not create debts between people, or why its split needs attention.</summary>
[Flags]
public enum SplitIssues
{
    None = 0,
    /// <summary>The amount is missing or not a number.</summary>
    NoAmount = 1,
    /// <summary>Nobody paid it yet, so nobody owes anyone.</summary>
    NoPayer = 2,
    /// <summary>It is only an estimate and is not divided until it is paid.</summary>
    Estimated = 4,
    /// <summary>Nobody shares it: no travellers and no people in the split.</summary>
    NoPeople = 8,
    /// <summary>A method this version does not know. The data is kept, but nothing is divided.</summary>
    UnknownMethod = 16,
    /// <summary>The amounts do not add up to the expense, or the percentages not to 100.</summary>
    SumMismatch = 32,
    /// <summary>A part is not a number, or a weight or percentage is negative.</summary>
    InvalidValue = 64
}

/// <summary>One person's part of an expense. <see cref="Value"/> is the part as stored: an amount, weight or percentage.</summary>
public sealed record ExpenseShare(Guid Person, decimal Amount, decimal? Value);

/// <summary>Identifies what one person owes another for one expense, for example to choose it in the pay function.</summary>
public readonly record struct DebtKey(Guid Expense, Guid From, Guid To);

/// <summary>
/// What <see cref="From"/> owes <see cref="To"/> for one expense, and how much of it is marked as paid. <see cref="Open"/>
/// is negative when more was paid than is owed, for example after the split changed: then To owes From the difference.
/// </summary>
public sealed record ExpenseDebt(Guid Expense, Guid From, Guid To, string Currency, decimal Amount, decimal Settled, LocalDate? SettledOn = null)
{
    /// <summary>
    /// What was transferred in another currency when it was paid back, such as euros for a debt in NZD, with the rate
    /// used then; null when it was paid in the debt's own currency or before this was recorded.
    /// </summary>
    public Transferred? PaidIn { get; init; }
    public DebtKey Key => new(Expense, From, To);
    public decimal Open => Amount - Settled;
    public bool IsSettled => Open == 0;
}

/// <summary>An expense divided between people: who paid, everyone's part, and the debts that follow.</summary>
public sealed record ExpenseSplit(Entity Expense, Money? Amount, SplitMethod? Method, IReadOnlyList<Guid> Payers, IReadOnlyList<ExpenseShare> Shares, IReadOnlyList<ExpenseDebt> Debts, SplitIssues Issues)
{
    /// <summary>Whether the expense creates debts: it has an amount, a payer and a valid split, and is not an estimate.</summary>
    public bool Divides => Issues == SplitIssues.None;
    /// <summary>What is still owed for this expense, in its currency.</summary>
    public decimal Open => Debts.Sum(d => Math.Abs(d.Open));
    /// <summary>Whether everything owed for this expense is marked as paid. An expense without debts counts as settled.</summary>
    public bool IsSettled => Debts.All(d => d.IsSettled);
    /// <summary>
    /// What the parts add up to as stored: amounts for <see cref="SplitMethod.Amount"/>, percentages for
    /// <see cref="SplitMethod.Percent"/>, weights for <see cref="SplitMethod.Share"/>, the amount for an equal split.
    /// Compare it with <see cref="Target"/> to say how much is still to divide.
    /// </summary>
    public decimal Assigned { get; init; }
    /// <summary>What <see cref="Assigned"/> must reach: the amount, or 100 for percentages; null for weights.</summary>
    public decimal? Target => Method switch { SplitMethod.Percent => 100m, SplitMethod.Amount or SplitMethod.Equal => Amount?.Amount, _ => null };
    /// <summary>The people who share the expense, in the order of the split, also when its parts do not add up.</summary>
    public IReadOnlyList<Guid> People { get; init; } = [];
    /// <summary>The part of <paramref name="person"/>, or zero.</summary>
    public decimal ShareOf(Guid person) => Shares.Where(s => s.Person == person).Sum(s => s.Amount);
}

/// <summary>
/// What was transferred to pay back a debt in another currency (specification section 34.1): the amount, its currency,
/// units of it per unit of the debt's currency, the day of the rate, and whether the rate was a reference rate
/// (an estimate) rather than what a card charged.
/// </summary>
public sealed record Transferred(decimal Value, string Currency, decimal Rate, LocalDate? RateDate, bool Reference);

/// <summary>Where one person stands in one currency: positive when others owe them, negative when they owe others.</summary>
public sealed record ExpenseBalance(Guid Person, string Currency, decimal Net)
{
    /// <summary>Whether part of it was converted with reference rates because no charged amount was entered.</summary>
    public bool Estimated { get; init; }
}

/// <summary>One payment: <see cref="From"/> pays <see cref="To"/> an amount in a currency.</summary>
public sealed record Transfer(Guid From, Guid To, string Currency, decimal Amount)
{
    /// <summary>Whether part of it was converted with reference rates because no charged amount was entered.</summary>
    public bool Estimated { get; init; }
}

/// <summary>
/// What an expense really cost in another currency, for example the euros a card was charged for a payment in NZD
/// (specification section 35). <see cref="Rate"/> is units of <see cref="Currency"/> per unit of the expense's currency.
/// </summary>
public sealed record Charge(decimal Value, string Currency, decimal Rate, LocalDate? Date, string? Source);

/// <summary>Where a booking stands with its payments (specification section 34.3).</summary>
public enum BookingCoverage
{
    /// <summary>No payment belongs to it: it is free, or nothing is recorded yet.</summary>
    None,
    /// <summary>At least one payment is still to pay, such as the rest paid on site.</summary>
    Planned,
    /// <summary>Every payment that belongs to it is paid.</summary>
    Paid
}

/// <summary>
/// The payments that belong to a booking: what is paid and what is still to pay. <see cref="LegacyPrice"/> is a
/// <c>price</c> stored by older clients, which <see cref="CostUpgrade"/> turns into payments.
/// </summary>
public sealed record BookingPayment(Entity Booking, IReadOnlyList<Entity> Expenses, BookingCoverage Coverage)
{
    public IReadOnlyList<Entity> Paid => Expenses.Where(e => !TravelRepo.Core.Expenses.IsPlanned(e)).ToArray();
    public IReadOnlyList<Entity> Planned => Expenses.Where(TravelRepo.Core.Expenses.IsPlanned).ToArray();
    /// <summary>The price an older client stored on the booking, or null.</summary>
    public Money? LegacyPrice => TripSummaries.Money(Booking.Data["price"]);
}

/// <summary>How an amount came to be in the requested currency.</summary>
public enum Conversion
{
    /// <summary>It already was in that currency.</summary>
    Same,
    /// <summary>With the amount actually charged, so it is exact.</summary>
    Charged,
    /// <summary>With reference rates, so it is an estimate.</summary>
    Reference,
    /// <summary>It could not be converted and stays in its own currency.</summary>
    None
}

/// <summary>
/// The paid expenses in one currency: <see cref="Exact"/> from amounts in that currency or charged in it,
/// <see cref="Estimated"/> converted with reference rates, and the expenses that could not be converted at all.
/// </summary>
public sealed record ConvertedTotal(string Currency, decimal Exact, decimal Estimated, int WithoutCharge, IReadOnlyList<Money> NotConverted)
{
    public decimal Total => Exact + Estimated;
}

/// <summary>
/// What paying the chosen open items comes to for one person: one payment per other person and currency, after
/// what they owe each other has been set off. <see cref="Items"/> are the chosen items that concern the person.
/// </summary>
public sealed record PaymentPlan(Guid Person, IReadOnlyList<ExpenseDebt> Items, IReadOnlyList<Transfer> Payments)
{
    /// <summary>What the person pays in all, per currency; negative when the others pay them more than they pay.</summary>
    public IReadOnlyDictionary<string, decimal> Totals => Payments.GroupBy(p => p.Currency, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.Sum(p => p.From == Person ? p.Amount : -p.Amount), StringComparer.Ordinal);
}

/// <summary>
/// Divides expenses between the trip's people, tracks what has been paid back, and works out who pays whom
/// (specification section 34.1). Amounts stay in the expense's currency; nothing is converted.
/// </summary>
public static class Expenses
{
    private static readonly HashSet<string> NoMinorUnit = ["BIF", "CLP", "DJF", "GNF", "ISK", "JPY", "KMF", "KRW", "PYG", "RWF", "UGX", "UYI", "VND", "VUV", "XAF", "XOF", "XPF"];
    private static readonly HashSet<string> ThreeDecimals = ["BHD", "IQD", "JOD", "KWD", "LYD", "OMR", "TND"];

    /// <summary>The decimal places of a currency's smallest unit (ISO 4217): 0 for JPY, 3 for KWD, otherwise 2.</summary>
    public static int MinorDigits(string currency) => NoMinorUnit.Contains(currency) ? 0 : ThreeDecimals.Contains(currency) ? 3 : 2;

    /// <summary>An amount as stored in the format: invariant digits with the currency's decimal places, such as "12.50".</summary>
    public static string Format(decimal amount, string currency)
    {
        var digits = MinorDigits(currency);
        return amount == Math.Round(amount, digits) ? amount.ToString("F" + digits, CultureInfo.InvariantCulture) : amount.ToString("0." + new string('0', digits) + "##########", CultureInfo.InvariantCulture);
    }

    /// <summary>The stored name of a method: equal, amount, share or percent.</summary>
    public static string Name(SplitMethod method) => method.ToString().ToLowerInvariant();

    /// <summary>Every expense of the trip divided, in title order.</summary>
    public static IReadOnlyList<ExpenseSplit> All(TripSnapshot trip) => trip.Entities.Values.Where(e => e.Type == "expense")
        .OrderBy(e => e.Title, StringComparer.Ordinal).ThenBy(e => e.Id).Select(e => Split(trip, e)).ToArray();

    /// <summary>
    /// Divides one expense. Without a <c>split</c>, it is divided equally among the trip's travellers. Every payer
    /// paid an equal part. A person who paid more than their part is owed the difference by those who paid less,
    /// in proportion. Amounts are rounded to the currency's smallest unit and the cents left over go, one each, to
    /// the parts that lost most in rounding, earlier people first, so the parts always add up to the expense.
    /// </summary>
    public static ExpenseSplit Split(TripSnapshot trip, Entity expense)
    {
        var issues = SplitIssues.None;
        var money = TripSummaries.Money(expense.Data["amount"]);
        if (money is null) issues |= SplitIssues.NoAmount;
        var estimated = expense.Data["estimated"] is JsonValue flag && flag.TryGetValue<bool>(out var e) && e;
        if (estimated) issues |= SplitIssues.Estimated;
        var payers = PersonIds(trip, expense.Data["paid_by"]).Distinct().ToArray();
        if (payers.Length == 0) issues |= SplitIssues.NoPayer;
        var (method, parts, splitIssues) = ReadSplit(trip, expense);
        issues |= splitIssues;
        var shares = new List<ExpenseShare>();
        var debts = new List<ExpenseDebt>();
        if (money is not null && method is not null && (splitIssues & (SplitIssues.UnknownMethod | SplitIssues.InvalidValue | SplitIssues.NoPeople)) == 0)
        {
            var total = money.Amount; var digits = Digits(money.Value, money.Currency);
            switch (method)
            {
                case SplitMethod.Equal:
                    shares.AddRange(Allocate(total, digits, parts.Select(p => (p.Person, 1m)).ToArray()).Select(a => new ExpenseShare(a.Key, a.Amount, null)));
                    break;
                case SplitMethod.Amount:
                    shares.AddRange(parts.Select(p => new ExpenseShare(p.Person, p.Value!.Value, p.Value)));
                    if (shares.Sum(s => s.Amount) != total) issues |= SplitIssues.SumMismatch;
                    break;
                case SplitMethod.Share or SplitMethod.Percent:
                    if (method == SplitMethod.Percent && parts.Sum(p => p.Value!.Value) != 100m) { issues |= SplitIssues.SumMismatch; break; }
                    if (parts.Sum(p => p.Value!.Value) <= 0) { issues |= SplitIssues.InvalidValue; break; }
                    var values = parts.ToDictionary(p => p.Person, p => p.Value);
                    shares.AddRange(Allocate(total, digits, parts.Select(p => (p.Person, p.Value!.Value)).ToArray()).Select(a => new ExpenseShare(a.Key, a.Amount, values[a.Key])));
                    break;
            }
        }
        if (issues == SplitIssues.None)
        {
            var digits = Digits(money!.Value, money.Currency);
            var paid = Allocate(money.Amount, digits, payers.Select(p => (p, 1m)).ToArray()).ToDictionary(a => a.Key, a => a.Amount);
            var people = payers.Concat(shares.Select(s => s.Person)).Distinct().ToArray();
            var net = people.Select(p => (Person: p, Net: paid.GetValueOrDefault(p) - shares.Where(s => s.Person == p).Sum(s => s.Amount))).ToArray();
            var creditors = net.Where(n => n.Net > 0).Select(n => (n.Person, n.Net)).ToArray();
            foreach (var debtor in net.Where(n => n.Net < 0))
                foreach (var part in Allocate(-debtor.Net, digits, creditors))
                    if (part.Amount != 0) debts.Add(new(expense.Id, debtor.Person, part.Key, money.Currency, part.Amount, 0));
        }
        // What was marked as paid is kept even when the split no longer asks for it, so a changed split shows as money owed back.
        var currency = money?.Currency ?? "";
        foreach (var settled in ReadSettled(expense))
        {
            var index = debts.FindIndex(d => d.From == settled.From && d.To == settled.To);
            if (index >= 0) debts[index] = debts[index] with { Settled = debts[index].Settled + settled.Value, SettledOn = settled.On ?? debts[index].SettledOn, PaidIn = settled.PaidIn ?? debts[index].PaidIn };
            else if (issues == SplitIssues.None) debts.Add(new(expense.Id, settled.From, settled.To, currency, 0, settled.Value, settled.On) { PaidIn = settled.PaidIn });
        }
        var assigned = method switch { SplitMethod.Equal => money?.Amount ?? 0, _ => parts.Sum(p => p.Value ?? 0) };
        return new(expense, money, method, payers, shares, debts, issues) { Assigned = assigned, People = parts.Select(p => p.Person).ToArray() };
    }

    /// <summary>Every debt between people over all expenses, settled or not.</summary>
    public static IReadOnlyList<ExpenseDebt> Debts(TripSnapshot trip) => All(trip).SelectMany(s => s.Debts).ToArray();

    /// <summary>The debts that are not fully paid, optionally only those that concern <paramref name="person"/>.</summary>
    public static IReadOnlyList<ExpenseDebt> Open(TripSnapshot trip, Guid? person = null) =>
        Debts(trip).Where(d => !d.IsSettled && (person is not { } p || d.From == p || d.To == p)).ToArray();

    /// <summary>Where everyone stands per currency over the open debts. People who are even are left out.</summary>
    /// <remarks>
    /// With <paramref name="currency"/>, each debt is converted first, see <see cref="Convert"/>: exactly with the amount
    /// charged for its expense, otherwise with <paramref name="rates"/> as an estimate; a debt that cannot be converted
    /// stays in its own currency.
    /// </remarks>
    public static IReadOnlyList<ExpenseBalance> Balances(TripSnapshot trip, string? currency = null, ExchangeRates? rates = null)
    {
        var net = new Dictionary<(Guid, string), decimal>(); var estimated = new HashSet<(Guid, string)>();
        foreach (var d in Open(trip))
        {
            var (open, code, how) = currency is null || trip.Find(d.Expense) is not { } expense ? (d.Open, d.Currency, Conversion.Same) : Convert(expense, d.Open, d.Currency, currency, rates);
            net[(d.To, code)] = net.GetValueOrDefault((d.To, code)) + open;
            net[(d.From, code)] = net.GetValueOrDefault((d.From, code)) - open;
            if (how == Conversion.Reference) { estimated.Add((d.To, code)); estimated.Add((d.From, code)); }
        }
        return net.Where(p => p.Value != 0).Select(p => new ExpenseBalance(p.Key.Item1, p.Key.Item2, p.Value) { Estimated = estimated.Contains(p.Key) })
            .OrderBy(b => b.Currency, StringComparer.Ordinal).ThenByDescending(b => b.Net).ThenBy(b => b.Person).ToArray();
    }

    /// <summary>
    /// The amount actually charged for an expense in another currency, from its <c>converted</c> field, or null when
    /// none was entered.
    /// </summary>
    public static Charge? ChargeOf(Entity expense)
    {
        if (expense.Data["converted"] is not JsonObject c || ExchangeRates.Code(c["currency"]?.ToString()) is not { } code) return null;
        // Cash bought before: the rate it cost is what was entered, and the amount in the other currency follows from it.
        if (c["source"]?.ToString() == Cash)
        {
            if (!TryDecimal(c["rate"], out var cashRate) || cashRate <= 0 || TripSummaries.Money(expense.Data["amount"]) is not { } paid) return null;
            LocalDate? cashDay = c["rate_date"] is JsonValue cd && LocalDatePattern.Iso.Parse(cd.ToString()).TryGetValue(default, out var cashDate) ? cashDate : null;
            return new(Math.Round(paid.Amount * cashRate, MinorDigits(code), MidpointRounding.AwayFromZero), code, cashRate, cashDay, Cash);
        }
        if (!TryDecimal(c["value"], out var value)) return null;
        // Both amounts are what was entered; the rate follows from them, so it stays right when the amount is corrected later.
        var rate = TripSummaries.Money(expense.Data["amount"]) is { Amount: not 0 } money ? Math.Round(value / money.Amount, 6, MidpointRounding.AwayFromZero) : TryDecimal(c["rate"], out var stored) ? stored : 0;
        LocalDate? date = c["rate_date"] is JsonValue d && LocalDatePattern.Iso.Parse(d.ToString()).TryGetValue(default, out var day) ? day : null;
        return new(value, code, rate, date, c["source"]?.ToString());
    }

    /// <summary>
    /// A copy of the expense with the amount actually charged in another currency, such as what a card statement shows
    /// in euros for a payment in NZD. The rate is worked out from both amounts and kept with the day and the source
    /// <c>card</c>, so the conversion stays as it was (specification section 35). Other fields of <c>converted</c> are kept.
    /// </summary>
    public static Entity WithCharge(Entity expense, decimal value, string currency, LocalDate? on = null, string source = "card")
    {
        var code = ExchangeRates.Code(currency) ?? throw new DomainException("expense.charge_currency", "A currency is a code with three letters, such as EUR.");
        var money = TripSummaries.Money(expense.Data["amount"]) ?? throw new DomainException("expense.amount", "The expense needs an amount first.");
        if (code == money.Currency) throw new DomainException("expense.charge_currency", "The charged amount is for another currency than the expense's.");
        if (money.Amount == 0) throw new DomainException("expense.amount", "The expense needs an amount first.");
        // A rate below zero would turn what is owed around; a refund is charged as a refund.
        if (value == 0 || Math.Sign(value) != Math.Sign(money.Amount)) throw new DomainException("expense.charge_value", money.Amount < 0 ? "The amount charged for a refund is below zero, like the refund." : "The charged amount must be above zero.");
        var copy = expense.Copy();
        var converted = copy.Data["converted"] as JsonObject ?? new JsonObject();
        converted["value"] = Format(value, code);
        converted["currency"] = code;
        converted["rate"] = Math.Round(value / money.Amount, 6, MidpointRounding.AwayFromZero).ToString("0.######", CultureInfo.InvariantCulture);
        converted["rate_date"] = on is { } day ? LocalDatePattern.Iso.Format(day) : converted["rate_date"]?.DeepClone() ?? null;
        if (converted["rate_date"] is null) converted.Remove("rate_date");
        converted["source"] = source;
        copy.Data["converted"] = converted;
        return copy;
    }

    /// <summary>The <c>source</c> of an expense's <c>converted</c> when it was paid with cash bought before (specification section 35).</summary>
    public const string Cash = "cash";

    /// <summary>Whether an expense was paid with cash in its own currency, so no card will ever charge it.</summary>
    public static bool IsCash(Entity expense) => expense.Data["converted"] is JsonObject c && c["source"]?.ToString() == Cash;

    /// <summary>
    /// A copy of the expense marked as paid with cash in its own currency, bought before, such as yen from a cash
    /// machine. No card will charge it, so what it comes to in <paramref name="currency"/> follows from
    /// <paramref name="rate"/>, the units of <paramref name="currency"/> one unit of the expense's currency cost: from
    /// the cash machine's receipt, or a reference rate the traveller chose to keep. It is kept with the day of that rate
    /// and the source <c>cash</c>, so it stays as it was (specification section 35).
    /// </summary>
    public static Entity WithCash(Entity expense, string currency, decimal rate, LocalDate on)
    {
        var code = ExchangeRates.Code(currency) ?? throw new DomainException("expense.charge_currency", "A currency is a code with three letters, such as EUR.");
        var money = TripSummaries.Money(expense.Data["amount"]) ?? throw new DomainException("expense.amount", "The expense needs an amount first.");
        if (code == money.Currency) throw new DomainException("expense.charge_currency", "Cash is counted in another currency than the expense's.");
        if (rate <= 0) throw new DomainException("expense.cash_rate", "The rate of the cash must be above zero.");
        var copy = expense.Copy();
        copy.Data["converted"] = new JsonObject
        {
            ["value"] = Format(Math.Round(money.Amount * rate, MinorDigits(code), MidpointRounding.AwayFromZero), code),
            ["currency"] = code,
            ["rate"] = Math.Round(rate, 8, MidpointRounding.AwayFromZero).ToString("0.########", CultureInfo.InvariantCulture),
            ["rate_date"] = LocalDatePattern.Iso.Format(on),
            ["source"] = Cash
        };
        return copy;
    }

    /// <summary>
    /// The rate cash in <paramref name="from"/> cost in <paramref name="to"/> the last time it was used in the trip, from
    /// the latest expense paid with such cash, to offer for the next one: it usually comes from the same withdrawal.
    /// </summary>
    public static decimal? LastCashRate(TripSnapshot trip, string from, string to) =>
        trip.Entities.Values.Where(e => e.Type == "expense" && IsCash(e) && TripSummaries.Money(e.Data["amount"])?.Currency == from)
            .Select(e => (Expense: e, Charge: ChargeOf(e))).Where(x => x.Charge is { } c && c.Currency == to)
            .OrderByDescending(x => x.Charge!.Date ?? DateOf(trip, x.Expense) ?? default).ThenByDescending(x => x.Expense.Id.ToString(), StringComparer.Ordinal)
            .Select(x => (decimal?)x.Charge!.Rate).FirstOrDefault();

    /// <summary>A copy of the expense without its charged amount.</summary>
    public static Entity WithoutCharge(Entity expense) { var copy = expense.Copy(); copy.Data.Remove("converted"); return copy; }

    /// <summary>
    /// <paramref name="amount"/> of <paramref name="expense"/>, in <paramref name="from"/>, as <paramref name="to"/>:
    /// unchanged when the currencies match, exactly with the amount charged when one in <paramref name="to"/> was
    /// entered, otherwise with reference rates as an estimate, and unchanged in <paramref name="from"/> when neither works.
    /// Results are rounded to the target currency's smallest unit.
    /// </summary>
    public static (decimal Amount, string Currency, Conversion How) Convert(Entity expense, decimal amount, string from, string to, ExchangeRates? rates = null)
    {
        if (from == to) return (amount, from, Conversion.Same);
        if (ChargeOf(expense) is { } charge && charge.Currency == to && TripSummaries.Money(expense.Data["amount"]) is { Amount: not 0 } money && money.Currency == from)
            return (Math.Round(amount * charge.Value / money.Amount, MinorDigits(to), MidpointRounding.AwayFromZero), to, Conversion.Charged);
        if (rates?.Convert(amount, from, to) is { } reference) return (Math.Round(reference, MinorDigits(to), MidpointRounding.AwayFromZero), to, Conversion.Reference);
        return (amount, from, Conversion.None);
    }

    /// <summary>
    /// What the paid expenses come to in <paramref name="currency"/>: exactly where an amount is in it or was charged
    /// in it, otherwise estimated with <paramref name="rates"/>. Estimates and budgets are left out.
    /// </summary>
    public static ConvertedTotal Total(TripSnapshot trip, string currency, ExchangeRates? rates = null) =>
        Total(trip.Entities.Values.Where(e => e.Type == "expense"), currency, rates);

    /// <summary>
    /// What <paramref name="expenses"/> come to in <paramref name="currency"/>, as <see cref="Total(TripSnapshot, string, ExchangeRates?)"/>:
    /// the paid ones, or with <paramref name="planned"/> the ones still to pay.
    /// </summary>
    public static ConvertedTotal Total(IEnumerable<Entity> expenses, string currency, ExchangeRates? rates = null, bool planned = false)
    {
        decimal exact = 0, estimated = 0; var withoutCharge = 0; var missing = new List<Money>();
        foreach (var expense in expenses)
        {
            if (TripSummaries.Money(expense.Data["amount"]) is not { } money || IsPlanned(expense) != planned) continue;
            var (value, _, how) = Convert(expense, money.Amount, money.Currency, currency, rates);
            if (how is Conversion.Reference or Conversion.None) withoutCharge++;
            if (how == Conversion.None) missing.Add(money);
            else if (how == Conversion.Reference) estimated += value;
            else exact += value;
        }
        var notConverted = missing.GroupBy(m => m.Currency, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new Money(Format(g.Sum(m => m.Amount), g.Key), g.Key)).ToArray();
        return new(currency, exact, estimated, withoutCharge, notConverted);
    }

    /// <summary>
    /// Payments that settle every open debt, per currency, with as few payments as this finds: people who owe exactly
    /// what someone else is owed pay each other first, then whoever owes most pays whoever is owed most. Nobody pays
    /// more than they owe in all, and nobody receives more than they are owed.
    /// </summary>
    public static IReadOnlyList<Transfer> SettleUp(TripSnapshot trip, string? currency = null, ExchangeRates? rates = null) => Minimize(Balances(trip, currency, rates));

    /// <summary>The fewest payments this finds that bring the given balances to zero, per currency.</summary>
    public static IReadOnlyList<Transfer> Minimize(IEnumerable<ExpenseBalance> balances)
    {
        var result = new List<Transfer>();
        foreach (var group in balances.GroupBy(b => b.Currency, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var estimated = group.Any(b => b.Estimated);
            var debtors = group.Where(b => b.Net < 0).Select(b => (b.Person, Amount: -b.Net)).OrderByDescending(x => x.Amount).ThenBy(x => x.Person).ToList();
            var creditors = group.Where(b => b.Net > 0).Select(b => (b.Person, Amount: b.Net)).OrderByDescending(x => x.Amount).ThenBy(x => x.Person).ToList();
            for (var i = 0; i < debtors.Count; i++)
            {
                var match = creditors.FindIndex(c => c.Amount == debtors[i].Amount);
                if (match < 0) continue;
                result.Add(new(debtors[i].Person, creditors[match].Person, group.Key, debtors[i].Amount) { Estimated = estimated });
                creditors.RemoveAt(match); debtors.RemoveAt(i--);
            }
            while (debtors.Count > 0 && creditors.Count > 0)
            {
                var d = debtors[0]; var c = creditors[0]; var amount = Math.Min(d.Amount, c.Amount);
                result.Add(new(d.Person, c.Person, group.Key, amount) { Estimated = estimated });
                debtors.RemoveAt(0); creditors.RemoveAt(0);
                if (d.Amount > amount) Insert(debtors, (d.Person, d.Amount - amount));
                if (c.Amount > amount) Insert(creditors, (c.Person, c.Amount - amount));
            }
        }
        return result;

        static void Insert(List<(Guid Person, decimal Amount)> list, (Guid Person, decimal Amount) item)
        {
            var at = list.FindIndex(x => x.Amount < item.Amount || x.Amount == item.Amount && x.Person.CompareTo(item.Person) > 0);
            list.Insert(at < 0 ? list.Count : at, item);
        }
    }

    /// <summary>
    /// The pay function: what <paramref name="person"/> pays, or receives, when the chosen open items are settled now.
    /// Items that do not concern the person are left out, as are items already paid. Per other person and currency,
    /// what they owe each other is set off into one payment. Payments the person makes come first, by name.
    /// </summary>
    /// <remarks>With <paramref name="currency"/>, the items are converted first as in <see cref="Balances"/>.</remarks>
    public static PaymentPlan Pay(TripSnapshot trip, Guid person, IEnumerable<DebtKey> chosen, string? currency = null, ExchangeRates? rates = null)
    {
        var keys = chosen.ToHashSet();
        var items = Open(trip, person).Where(d => keys.Contains(d.Key)).ToArray();
        var converted = items.Select(d => (Debt: d, Value: currency is null || trip.Find(d.Expense) is not { } e ? (d.Open, d.Currency, Conversion.Same) : Convert(e, d.Open, d.Currency, currency, rates))).ToArray();
        var payments = converted.GroupBy(x => (Other: x.Debt.From == person ? x.Debt.To : x.Debt.From, Currency: x.Value.Item2))
            .Select(g => (g.Key.Other, g.Key.Currency, Amount: g.Sum(x => x.Debt.From == person ? x.Value.Item1 : -x.Value.Item1), Estimated: g.Any(x => x.Value.Item3 == Conversion.Reference)))
            .Where(x => x.Amount != 0)
            .Select(x => (x.Amount > 0 ? new Transfer(person, x.Other, x.Currency, x.Amount) : new Transfer(x.Other, person, x.Currency, -x.Amount)) with { Estimated = x.Estimated })
            .OrderBy(t => t.Currency, StringComparer.Ordinal).ThenBy(t => t.From == person ? 0 : 1)
            .ThenBy(t => trip.Find(t.From == person ? t.To : t.From)?.Title, StringComparer.Ordinal).ThenBy(t => t.From == person ? t.To : t.From).ToArray();
        return new(person, items, payments);
    }

    /// <summary>
    /// Marks the given debts as paid in full on <paramref name="on"/> and returns the changed expenses. A debt that is
    /// owed back because more was paid than is owed counts as paid back. Other fields of the expense are kept.
    /// </summary>
    /// <remarks>
    /// With <paramref name="currency"/>, the currency the travellers paid back in, such as the trip's: what is
    /// transferred now for a debt in another currency is kept with the paid mark, converted as in <see cref="Convert"/>
    /// (exactly with what a card charged, otherwise with <paramref name="rates"/>), so the history shows what was
    /// really paid and that amount no longer changes with the rates.
    /// </remarks>
    public static IReadOnlyList<Entity> Settle(TripSnapshot trip, IEnumerable<DebtKey> keys, LocalDate? on = null, string? currency = null, ExchangeRates? rates = null)
    {
        var result = new List<Entity>();
        foreach (var group in keys.Distinct().GroupBy(k => k.Expense))
        {
            var expense = trip.Find(group.Key) ?? throw new DomainException("entity.missing", "Entity not found.");
            if (expense.Type != "expense") throw new DomainException("expense.type", "Only expenses can be marked as paid.");
            var split = Split(trip, expense);
            var copy = expense.Copy();
            foreach (var key in group)
            {
                var debt = split.Debts.FirstOrDefault(d => d.Key == key) ?? throw new DomainException("expense.debt", "This person owes nothing for this expense.");
                SetSettled(copy, debt.From, debt.To, debt.Amount == 0 ? null : Format(debt.Amount, debt.Currency), on);
                if (debt.Amount != 0 && debt.Open > 0 && currency is not null && currency != debt.Currency) RecordTransfer(copy, expense, debt, currency, rates, on);
            }
            result.Add(copy);
        }
        return result;
    }

    /// <summary>Marks everything owed for an expense as paid, or, with <paramref name="from"/>, only what that person owes.</summary>
    public static Entity Settle(TripSnapshot trip, Entity expense, Guid? from = null, LocalDate? on = null, string? currency = null, ExchangeRates? rates = null)
    {
        var keys = Split(trip, expense).Debts.Where(d => from is not { } p || d.From == p).Select(d => d.Key).ToArray();
        return keys.Length == 0 ? expense.Copy() : Settle(trip, keys, on, currency, rates).Single();
    }

    /// <summary>Marks every open debt of the trip as paid, for example after everyone paid what <see cref="SettleUp"/> says.</summary>
    public static IReadOnlyList<Entity> SettleAll(TripSnapshot trip, LocalDate? on = null, string? currency = null, ExchangeRates? rates = null) => Settle(trip, Open(trip).Select(d => d.Key), on, currency, rates);

    /// <summary>
    /// Takes back the paid mark of an expense, or with <paramref name="from"/> only of what that person owed, or with
    /// <paramref name="to"/> as well only of what they owed that person.
    /// </summary>
    public static Entity Unsettle(Entity expense, Guid? from = null, Guid? to = null)
    {
        var copy = expense.Copy();
        if (copy.Data["settled"] is not JsonArray settled) return copy;
        for (var i = settled.Count - 1; i >= 0; i--)
            if (settled[i] is JsonObject entry && (from is not { } f || Id(entry["person"]) == f) && (to is not { } t || Id(entry["to"]) == t)) settled.RemoveAt(i);
        if (settled.Count == 0) copy.Data.Remove("settled");
        return copy;
    }

    /// <summary>
    /// A copy of the expense divided by <paramref name="method"/>. An equal split among every traveller removes the
    /// split, which is the default. Values are amounts, weights or percentages; for an equal split they are ignored.
    /// Fields of the split and of each part that this version does not know are kept.
    /// </summary>
    public static Entity WithSplit(Entity expense, SplitMethod method, IEnumerable<(Guid Person, decimal? Value)>? parts = null)
    {
        var copy = expense.Copy();
        var list = (parts ?? []).GroupBy(p => p.Person).Select(g => (Person: g.Key, Value: g.Select(p => p.Value).FirstOrDefault(v => v is not null))).ToArray();
        if (method == SplitMethod.Equal && list.Length == 0)
        {
            if (copy.Data["split"] is JsonObject old && old.Any(p => p.Key is not ("method" or "parts"))) { old.Remove("method"); old.Remove("parts"); }
            else copy.Data.Remove("split");
            return copy;
        }
        if (method != SplitMethod.Equal && list.Any(p => p.Value is null)) throw new DomainException("expense.split_value", "Every part needs a value.");
        var split = copy.Data["split"] as JsonObject ?? new JsonObject();
        var previous = (split["parts"] as JsonArray ?? []).OfType<JsonObject>().Where(p => Id(p["person"]) is not null).GroupBy(p => Id(p["person"])!.Value).ToDictionary(g => g.Key, g => g.First());
        var currency = TripSummaries.Money(expense.Data["amount"])?.Currency ?? "EUR";
        split["method"] = Name(method);
        split["parts"] = new JsonArray(list.Select(p =>
        {
            var entry = previous.TryGetValue(p.Person, out var old) ? (JsonObject)old.DeepClone() : new JsonObject();
            entry["person"] = p.Person.ToString();
            if (method == SplitMethod.Equal) entry.Remove("value");
            else entry["value"] = method == SplitMethod.Amount ? Format(p.Value!.Value, currency) : p.Value!.Value.ToString(CultureInfo.InvariantCulture);
            return (JsonNode)entry;
        }).ToArray());
        copy.Data["split"] = split;
        return copy;
    }

    /// <summary>
    /// A copy of the expense divided by another method, keeping the current division as far as the method allows:
    /// amounts and percentages start from everyone's current part, weights from 1 each. The people stay named.
    /// </summary>
    public static Entity ChangeMethod(TripSnapshot trip, Entity expense, SplitMethod method)
    {
        var split = Split(trip, expense);
        if (split.Method == method) return expense.Copy();
        var people = split.People;
        var amounts = split.Shares.Count == people.Count && people.Count > 0 ? split.Shares.Select(x => x.Amount).ToArray()
            : Split(trip, WithSplit(expense, SplitMethod.Equal, people.Select(p => (p, (decimal?)null)))).Shares.Select(x => x.Amount).ToArray();
        switch (method)
        {
            case SplitMethod.Equal:
                // The people stay named, so a traveller added later does not join this expense by accident.
                return WithSplit(expense, SplitMethod.Equal, people.Select(p => (p, (decimal?)null)));
            case SplitMethod.Amount:
                return WithSplit(expense, method, people.Select((p, i) => (p, (decimal?)(i < amounts.Length ? amounts[i] : 0))));
            case SplitMethod.Share:
                return WithSplit(expense, method, people.Select(p => (p, (decimal?)1m)));
            default:
                var total = amounts.Sum();
                var percent = total == 0 ? Allocate(100m, 2, people.Select(p => (p, 1m)).ToArray()) : Allocate(100m, 2, people.Select((p, i) => (p, i < amounts.Length ? Math.Abs(amounts[i]) : 0)).ToArray());
                return WithSplit(expense, method, percent.Select(x => (x.Key, (decimal?)x.Amount)));
        }
    }

    /// <summary>
    /// A copy of the expense with <paramref name="person"/> sharing it or not. Someone who joins an amount or a
    /// percentage split starts at zero, and a weight split at 1. The last person cannot leave.
    /// </summary>
    public static Entity WithPerson(TripSnapshot trip, Entity expense, Guid person, bool shares)
    {
        var split = Split(trip, expense);
        var method = split.Method ?? throw new DomainException("expense.split_method", "This split method is not known here.");
        var people = split.People.ToList();
        if (shares == people.Contains(person)) return expense.Copy();
        if (shares) people.Add(person); else people.Remove(person);
        // Nobody sharing it would read as "every traveller" in older data, so the last person stays.
        if (people.Count == 0) throw new DomainException("expense.split_people", "At least one person shares an expense.");
        if (method == SplitMethod.Equal) return WithSplit(expense, SplitMethod.Equal, people.Select(p => (p, (decimal?)null)));
        var values = ValuesOf(trip, expense);
        return WithSplit(expense, method, people.Select(p => (p, (decimal?)(values.TryGetValue(p, out var v) ? v : method == SplitMethod.Share ? 1m : 0m))));
    }

    /// <summary>A copy of the expense in which <paramref name="person"/>'s amount, weight or percentage is <paramref name="value"/>; the others stay.</summary>
    public static Entity WithValue(TripSnapshot trip, Entity expense, Guid person, decimal value)
    {
        var split = Split(trip, expense);
        if (split.Method is not ({ } method and not SplitMethod.Equal)) throw new DomainException("expense.split_method", "An equal split has no values.");
        var values = ValuesOf(trip, expense); values[person] = value;
        var people = split.People.Contains(person) ? split.People : [.. split.People, person];
        return WithSplit(expense, method, people.Select(p => (p, (decimal?)values.GetValueOrDefault(p))));
    }

    private static Dictionary<Guid, decimal> ValuesOf(TripSnapshot trip, Entity expense) =>
        ReadSplit(trip, expense).Parts.Where(p => p.Value is not null).ToDictionary(p => p.Person, p => p.Value!.Value);

    /// <summary>The expenses that belong to <paramref name="booking"/> through their <c>related</c> entries.</summary>
    public static IReadOnlyList<Entity> ForBooking(TripSnapshot trip, Guid booking) => trip.Entities.Values
        .Where(e => e.Type == "expense" && (e.Data["related"] as JsonArray ?? []).Any(r => Id(r?["id"]) == booking)).OrderBy(e => e.Title, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Bookings that still carry a <c>price</c> from an older client and have no payment that covers it, so the price
    /// is not counted anywhere yet. Cancelled bookings and ideas are left out. In order of title.
    /// </summary>
    public static IReadOnlyList<Entity> WithoutExpense(TripSnapshot trip) => Payments(trip)
        .Where(p => p.LegacyPrice is { Amount: not 0 } && p.Expenses.Count == 0 && p.Booking.Data["status"]?.ToString() is not ("cancelled" or "idea")).Select(p => p.Booking).ToArray();

    /// <summary>
    /// A booking's payments: the expenses that name it in <c>related</c>, paid or still to pay. What a booking costs is
    /// what its payments add up to; a booking has no amount of its own (specification section 34.3).
    /// </summary>
    public static BookingPayment Payment(TripSnapshot trip, Entity booking)
    {
        var expenses = ForBooking(trip, booking.Id);
        var coverage = expenses.Count == 0 ? BookingCoverage.None : expenses.Any(IsPlanned) ? BookingCoverage.Planned : BookingCoverage.Paid;
        return new(booking, expenses, coverage);
    }

    /// <summary>Every booking's <see cref="Payment"/>, in order of title.</summary>
    public static IReadOnlyList<BookingPayment> Payments(TripSnapshot trip) => trip.Entities.Values.Where(b => b.Type == "booking")
        .OrderBy(b => b.Title, StringComparer.Ordinal).ThenBy(b => b.Id).Select(b => Payment(trip, b)).ToArray();

    /// <summary>
    /// A new payment for a booking with its title and a link back to it, shared by the booking's travellers (see
    /// <see cref="PeopleFor"/>). Without <paramref name="amount"/> it takes a legacy price, or what of it is not paid yet.
    /// </summary>
    public static Entity FromBooking(TripSnapshot trip, Entity booking, Money? amount = null, bool planned = false, IEnumerable<Guid>? payers = null)
    {
        if (booking.Type != "booking") throw new DomainException("expense.booking", "Only a booking can have payments.");
        amount ??= TripSummaries.Money(booking.Data["price"]) is { } price ? (CostUpgrade.Rest(trip, booking, price) is { Amount: > 0 } rest ? rest : price) : null;
        return Create(trip, booking.Title, amount ?? throw new DomainException("expense.amount", "A payment needs an amount."), payers ?? [], booking.Id, planned);
    }

    /// <summary>The day a payment still to pay is due, such as the rest of a deposit, or null.</summary>
    public static NodaTime.LocalDate? DueOf(Entity expense) =>
        expense.Data["due"]?["date"] is JsonValue d && LocalDatePattern.Iso.Parse(d.ToString()).TryGetValue(default, out var day) ? day : null;

    /// <summary>A copy of the expense due on <paramref name="day"/>, or without a due day. Other fields of <c>due</c> are kept.</summary>
    public static Entity WithDue(Entity expense, NodaTime.LocalDate? day)
    {
        var copy = expense.Copy();
        if (day is not { } d) { copy.Data.Remove("due"); return copy; }
        var due = copy.Data["due"] as JsonObject ?? new JsonObject();
        due["date"] = LocalDatePattern.Iso.Format(d);
        copy.Data["due"] = due;
        return copy;
    }

    /// <summary>
    /// The day an expense was paid: its own <c>date</c>, otherwise the first day of the plan it belongs to (see
    /// <see cref="DayOf"/>), otherwise the day an amount was charged. Null when none of them is known.
    /// </summary>
    public static NodaTime.LocalDate? DateOf(TripSnapshot trip, Entity expense) =>
        (expense.Data["date"] is JsonValue d && LocalDatePattern.Iso.Parse(d.ToString()).TryGetValue(default, out var day) ? day : (NodaTime.LocalDate?)null)
        ?? DayOf(trip, expense) ?? ChargeOf(expense)?.Date;

    /// <summary>A copy of the expense paid on <paramref name="day"/>, or without a day of its own.</summary>
    public static Entity WithDate(Entity expense, NodaTime.LocalDate? day)
    {
        var copy = expense.Copy();
        if (day is { } d) copy.Data["date"] = LocalDatePattern.Iso.Format(d); else copy.Data.Remove("date");
        return copy;
    }

    /// <summary>
    /// Whether a payment still to pay belongs to an idea or something cancelled: a booking or plan with the status
    /// <c>idea</c> or <c>cancelled</c>. Such payments say what it would cost; they do not count as still to pay or in
    /// budgets until what they belong to is booked. What was paid always counts.
    /// </summary>
    public static bool IsIdea(TripSnapshot trip, Entity expense) => IsPlanned(expense)
        && (expense.Data["related"] as JsonArray ?? []).Select(r => Id(r?["id"])).OfType<Guid>().Select(trip.Find).OfType<Entity>()
            .Any(e => e.Data["status"]?.ToString() is "idea" or "cancelled");

    /// <summary>Whether an expense is still to pay: an estimate, or an amount that is booked but not paid yet.</summary>
    public static bool IsPlanned(Entity expense) => expense.Data["estimated"] is JsonValue flag && flag.TryGetValue<bool>(out var planned) && planned;

    /// <summary>
    /// A new expense that always names who shares it: the people of what it belongs to (see <see cref="PeopleFor"/>),
    /// divided equally. A planned one is still to pay and creates no debts until it is paid.
    /// </summary>
    public static Entity Create(TripSnapshot trip, string title, Money amount, IEnumerable<Guid> payers, Guid? related = null, bool planned = false, string? category = null, NodaTime.LocalDate? on = null)
    {
        var expense = Entity.Create("expense", title.Trim());
        if (on is { } day) expense.Data["date"] = LocalDatePattern.Iso.Format(day);
        var code = ExchangeRates.Code(amount.Currency) ?? throw new DomainException("expense.currency", "A currency is a code with three letters, such as EUR.");
        expense.Data["amount"] = new JsonObject { ["value"] = Format(amount.Amount, code), ["currency"] = code };
        expense.Data["estimated"] = planned;
        var who = payers.Distinct().ToArray();
        if (who.Length > 0) expense.Data["paid_by"] = new JsonArray(who.Select(p => (JsonNode)p.ToString()).ToArray());
        if (related is { } id && trip.Find(id) is { } target) expense.Data["related"] = new JsonArray(new JsonObject { ["type"] = target.Type, ["id"] = target.Id.ToString() });
        var people = PeopleFor(trip, related);
        if (people.Count > 0) expense = WithSplit(expense, SplitMethod.Equal, people.Select(p => (p, (decimal?)null)));
        if (category is { Length: > 0 }) expense = ExpenseCategories.With(trip, expense, category);
        return expense;
    }

    /// <summary>
    /// Who shares an expense by default: a booking's travellers, otherwise the people of the plans it belongs to,
    /// otherwise the trip's travellers.
    /// </summary>
    public static IReadOnlyList<Guid> PeopleFor(TripSnapshot trip, Guid? related)
    {
        if (related is { } id && trip.Find(id) is { } target)
        {
            var plans = target.Type == "booking" ? TripCommands.BookedItems(trip, id).Select(trip.Find).OfType<Entity>().ToArray() : target.Type == "schedule_item" ? [target] : [];
            if (target.Type == "booking" && PersonIds(trip, target.Data["travelers"]).Distinct().ToArray() is { Length: > 0 } travelers) return travelers;
            var people = plans.SelectMany(p => ScheduleQueries.Participants(trip, p)).Select(p => p.Id).Distinct().ToArray();
            if (people.Length > 0) return people;
        }
        return TripPeople.Travelers(trip).Select(p => p.Id).ToArray();
    }

    /// <summary>
    /// Expenses that are divided among "every traveller" because they name nobody (written by older clients), each
    /// fixed to the people who travel now. Clients apply this before they add a traveller, so a new traveller never
    /// shares, or changes what was paid back for, earlier expenses by accident.
    /// </summary>
    public static IReadOnlyList<Entity> FixShares(TripSnapshot trip)
    {
        var travellers = TripPeople.Travelers(trip).Select(p => p.Id).ToArray();
        if (travellers.Length == 0) return [];
        return trip.Entities.Values.Where(e => e.Type == "expense" && SharesWithEveryone(e)).OrderBy(e => e.Title, StringComparer.Ordinal)
            .Select(e => WithSplit(e, SplitMethod.Equal, travellers.Select(p => (p, (decimal?)null)))).ToArray();
    }

    /// <summary>Whether an expense names nobody and so is shared by every traveller, including travellers added later.</summary>
    public static bool SharesWithEveryone(Entity expense) =>
        expense.Data["split"] is not JsonObject split || (split["method"]?.ToString() ?? "equal") == "equal" && (split["parts"] as JsonArray ?? []).Count == 0;

    /// <summary>
    /// A copy of a planned expense marked as paid, by <paramref name="payers"/> when it names nobody yet, on
    /// <paramref name="on"/> when it has no day of its own.
    /// </summary>
    public static Entity MarkPaid(Entity expense, IEnumerable<Guid>? payers = null, NodaTime.LocalDate? on = null)
    {
        var copy = expense.Copy();
        copy.Data["estimated"] = false;
        // Paid on that day, unless the expense already says when.
        if (on is { } day && copy.Data["date"] is null) copy.Data["date"] = LocalDatePattern.Iso.Format(day);
        // Paid now, so it is no longer due.
        copy.Data.Remove("due");
        if ((copy.Data["paid_by"] as JsonArray ?? []).Count == 0 && payers?.Distinct().ToArray() is { Length: > 0 } who) copy.Data["paid_by"] = new JsonArray(who.Select(p => (JsonNode)p.ToString()).ToArray());
        return copy;
    }

    /// <summary>
    /// The day an expense happened as far as the trip knows it: the first day of the plan it belongs to, directly or
    /// through a booking. Clients use it as the default day of an amount charged.
    /// </summary>
    public static NodaTime.LocalDate? DayOf(TripSnapshot trip, Entity expense)
    {
        foreach (var id in (expense.Data["related"] as JsonArray ?? []).Select(r => Id(r?["id"])).OfType<Guid>())
        {
            if (trip.Find(id) is not { } target) continue;
            var plans = target.Type == "booking" ? TripCommands.BookedItems(trip, id).Select(trip.Find).OfType<Entity>() : target.Type == "schedule_item" ? [target] : [];
            var days = plans.Select(p => ScheduleQueries.Span(trip, p)).Select(span => span.Start is { } start
                ? start.InZone(NodaTime.DateTimeZoneProviders.Tzdb.GetZoneOrNull(span.StartZone ?? "") ?? ScheduleQueries.TripZone(trip)).Date : span.Date).OfType<NodaTime.LocalDate>().Order().ToArray();
            if (days.Length > 0) return days[0];
        }
        return null;
    }

    /// <summary>
    /// Expenses that are paid and should be divided but are not, because their split needs attention: parts that do
    /// not add up, values that are not numbers, nobody who shares it, or a method this version does not know. They are
    /// left out of who owes whom until they are fixed, so clients name them there.
    /// </summary>
    public static IReadOnlyList<ExpenseSplit> Problems(TripSnapshot trip) => All(trip)
        .Where(s => (s.Issues & (SplitIssues.SumMismatch | SplitIssues.InvalidValue | SplitIssues.NoPeople | SplitIssues.UnknownMethod)) != 0 && (s.Issues & (SplitIssues.Estimated | SplitIssues.NoAmount)) == 0).ToArray();

    /// <summary>Semantic checks for splits and paid marks; references to missing people are reported by <see cref="SemanticValidation"/>.</summary>
    public static IEnumerable<Diagnostic> Validate(TripSnapshot trip)
    {
        foreach (var booking in trip.Entities.Values.Where(e => e.Type == "booking" && e.Data["price"] is not null))
            yield return new("booking.price", Severity.Info, booking.Id + "/price", "A booking's price is from an older version; its payments say what it costs. Upgrading the trip's costs turns it into payments.");
        foreach (var expense in trip.Entities.Values.Where(e => e.Type == "expense"))
        {
            var path = expense.Id.ToString();
            if (expense.Data["due"]?["date"] is JsonValue dueDate && !LocalDatePattern.Iso.Parse(dueDate.ToString()).Success) yield return new("date.invalid", Severity.Error, path + "/due", "Invalid calendar date.");
            if (expense.Data["date"] is JsonValue paidOn && !LocalDatePattern.Iso.Parse(paidOn.ToString()).Success) yield return new("date.invalid", Severity.Error, path + "/date", "Invalid calendar date.");
            foreach (var (field, key) in new[] { ("split/parts", "person"), ("settled", "person"), ("settled", "to") })
            {
                var list = field == "settled" ? expense.Data["settled"] : expense.Data["split"]?["parts"];
                foreach (var entry in (list as JsonArray ?? []).OfType<JsonObject>())
                {
                    if (entry[key]?.ToString() is not { } text) { yield return new("expense.person", Severity.Warning, path + "/" + field, "An entry names no person."); continue; }
                    if (!Guid.TryParse(text, out var id) || trip.Find(id) is not { } target) yield return new("reference.missing", Severity.Error, path + "/" + field, "The referenced entity does not exist.");
                    else if (target.Type != "person") yield return new("reference.type", Severity.Error, path + "/" + field, "The linked item has the wrong entity type.");
                }
            }
            foreach (var entry in (expense.Data["settled"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (!TryDecimal(entry["value"], out _)) yield return new("expense.settled_value", Severity.Warning, path + "/settled", "A paid mark needs an amount.");
                if (entry["date"] is JsonValue date && !LocalDatePattern.Iso.Parse(date.ToString()).Success) yield return new("date.invalid", Severity.Error, path + "/settled", "Invalid calendar date.");
            }
            if (expense.Data["split"] is null) continue;
            var split = Split(trip, expense);
            if (split.Issues.HasFlag(SplitIssues.UnknownMethod)) yield return new("expense.split_method", Severity.Info, path + "/split", "This split method is not known here; the expense is not divided.");
            if (split.Issues.HasFlag(SplitIssues.InvalidValue)) yield return new("expense.split_value", Severity.Warning, path + "/split", "A part of the split is not a valid number.");
            if (split.Issues.HasFlag(SplitIssues.SumMismatch)) yield return new("expense.split_sum", Severity.Warning, path + "/split", split.Method == SplitMethod.Percent ? "The percentages do not add up to 100." : "The parts do not add up to the amount.");
            if (split.Issues.HasFlag(SplitIssues.NoPeople)) yield return new("expense.split_people", Severity.Warning, path + "/split", "Nobody shares this expense.");
        }
    }

    private sealed record Part(Guid Person, decimal? Value);
    private sealed record SettledMark(Guid From, Guid To, decimal Value, LocalDate? On, Transferred? PaidIn);

    /// <summary>
    /// Keeps with a paid mark what was transferred now in <paramref name="currency"/> for the open part of
    /// <paramref name="debt"/>, added to what an earlier part was transferred for in the same currency.
    /// </summary>
    private static void RecordTransfer(Entity copy, Entity expense, ExpenseDebt debt, string currency, ExchangeRates? rates, LocalDate? on)
    {
        var (value, code, how) = Convert(expense, debt.Open, debt.Currency, currency, rates);
        if (code != currency || how == Conversion.None) return;
        var entry = (copy.Data["settled"] as JsonArray ?? []).OfType<JsonObject>().First(e => Id(e["person"]) == debt.From && Id(e["to"]) == debt.To);
        var total = value + (debt.PaidIn is { } earlier && earlier.Currency == currency ? earlier.Value : 0);
        var basis = debt.Open + (debt.PaidIn is { } before && before.Currency == currency ? debt.Settled : 0);
        LocalDate? rateDay = how == Conversion.Reference ? rates is { } r ? LocalDate.FromDateOnly(r.Date) : null : ChargeOf(expense)?.Date ?? on;
        var converted = new JsonObject
        {
            ["value"] = Format(total, currency),
            ["currency"] = currency,
            ["rate"] = Math.Round(total / basis, 6, MidpointRounding.AwayFromZero).ToString("0.######", CultureInfo.InvariantCulture),
            ["source"] = how == Conversion.Reference ? "reference" : ChargeOf(expense)?.Source == Cash ? Cash : "card"
        };
        if (rateDay is { } d) converted["rate_date"] = LocalDatePattern.Iso.Format(d);
        entry["converted"] = converted;
    }

    private static (SplitMethod? Method, IReadOnlyList<Part> Parts, SplitIssues Issues) ReadSplit(TripSnapshot trip, Entity expense)
    {
        var split = expense.Data["split"] as JsonObject;
        var name = split?["method"]?.ToString() ?? "equal";
        SplitMethod? method = name switch { "equal" => SplitMethod.Equal, "amount" => SplitMethod.Amount, "share" => SplitMethod.Share, "percent" => SplitMethod.Percent, _ => null };
        if (method is null) return (null, [], SplitIssues.UnknownMethod);
        var issues = SplitIssues.None; var parts = new List<Part>();
        foreach (var entry in (split?["parts"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (Id(entry["person"]) is not { } person || trip.Find(person) is not { Type: "person" }) continue;
            decimal? value = null;
            if (method != SplitMethod.Equal)
            {
                if (!TryDecimal(entry["value"], out var v) || method != SplitMethod.Amount && v < 0) { issues |= SplitIssues.InvalidValue; continue; }
                value = v;
            }
            var at = parts.FindIndex(p => p.Person == person);
            // The same person twice, for example after two people were merged into one, holds both parts.
            if (at >= 0) parts[at] = parts[at] with { Value = parts[at].Value + value };
            else parts.Add(new(person, value));
        }
        if (method == SplitMethod.Equal && parts.Count == 0) parts.AddRange(TripPeople.Travelers(trip).Select(p => new Part(p.Id, null)));
        if (parts.Count == 0) issues |= SplitIssues.NoPeople;
        return (method, parts, issues);
    }

    private static IEnumerable<SettledMark> ReadSettled(Entity expense)
    {
        foreach (var entry in (expense.Data["settled"] as JsonArray ?? []).OfType<JsonObject>())
            if (Id(entry["person"]) is { } from && Id(entry["to"]) is { } to && TryDecimal(entry["value"], out var value))
                yield return new(from, to, value, entry["date"] is JsonValue d && LocalDatePattern.Iso.Parse(d.ToString()).TryGetValue(default, out var day) ? day : null,
                    entry["converted"] is JsonObject c && ExchangeRates.Code(c["currency"]?.ToString()) is { } code && TryDecimal(c["value"], out var paid)
                        ? new Transferred(paid, code, TryDecimal(c["rate"], out var rate) ? rate : value == 0 ? 0 : paid / value, c["rate_date"] is JsonValue rd && LocalDatePattern.Iso.Parse(rd.ToString()).TryGetValue(default, out var rateDay) ? rateDay : null, c["source"]?.ToString() == "reference")
                        : null);
    }

    private static void SetSettled(Entity expense, Guid from, Guid to, string? value, LocalDate? on)
    {
        var settled = expense.Data["settled"] as JsonArray ?? new JsonArray();
        var existing = settled.OfType<JsonObject>().Where(e => Id(e["person"]) == from && Id(e["to"]) == to).ToArray();
        var entry = existing.FirstOrDefault();
        foreach (var extra in existing.Skip(1)) settled.Remove(extra);
        if (value is null) { if (entry is not null) settled.Remove(entry); }
        else
        {
            if (entry is null) { entry = new JsonObject { ["person"] = from.ToString(), ["to"] = to.ToString() }; settled.Add(entry); }
            // A transfer in another currency is recorded again by the caller when there is one; a stale one would mislead.
            if (entry["value"]?.ToString() != value) entry.Remove("converted");
            entry["value"] = value;
            if (on is { } day) entry["date"] = LocalDatePattern.Iso.Format(day);
        }
        if (settled.Count == 0) expense.Data.Remove("settled"); else expense.Data["settled"] = settled;
    }

    /// <summary>
    /// Divides <paramref name="total"/> in proportion to the weights, in units of 10^-digits. The units left over by
    /// rounding down go to the largest remainders; equal remainders go to the earlier entry.
    /// </summary>
    private static IReadOnlyList<(Guid Key, decimal Amount)> Allocate(decimal total, int digits, IReadOnlyList<(Guid Key, decimal Weight)> weights)
    {
        var sum = weights.Sum(w => w.Weight);
        if (weights.Count == 0 || sum <= 0) return [];
        var unit = 1m / Pow10(digits);
        var units = decimal.Truncate(Math.Abs(total) / unit);
        var sign = total < 0 ? -1 : 1;
        var raw = weights.Select((w, i) => (w.Key, Index: i, Exact: units * w.Weight / sum)).ToArray();
        var floor = raw.Select(r => decimal.Floor(r.Exact)).ToArray();
        var left = units - floor.Sum();
        foreach (var r in raw.OrderByDescending(r => r.Exact - decimal.Floor(r.Exact)).ThenBy(r => r.Index))
        {
            if (left <= 0) break;
            floor[r.Index]++; left--;
        }
        // An amount with more digits than the currency keeps its extra fraction on the first part, so the parts still add up.
        var rest = Math.Abs(total) - units * unit;
        return raw.Select(r => (r.Key, sign * (floor[r.Index] * unit + (r.Index == 0 ? rest : 0)))).ToArray();
    }

    private static decimal Pow10(int digits) { var p = 1m; for (var i = 0; i < digits; i++) p *= 10; return p; }

    private static int Digits(string value, string currency)
    {
        var dot = value.IndexOf('.');
        return Math.Max(MinorDigits(currency), dot < 0 ? 0 : value.Length - dot - 1);
    }

    private static bool TryDecimal(JsonNode? node, out decimal value)
    {
        value = 0;
        return node is JsonValue v && (v.TryGetValue<string>(out var s) ? decimal.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value) : v.TryGetValue(out value));
    }

    private static Guid? Id(JsonNode? node) => Guid.TryParse(node?.ToString(), out var id) ? id : null;

    private static IEnumerable<Guid> PersonIds(TripSnapshot trip, JsonNode? ids) =>
        (ids as JsonArray ?? []).Select(Id).OfType<Guid>().Where(id => trip.Find(id) is { Type: "person" });
}
