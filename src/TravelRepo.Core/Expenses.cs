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
    /// <summary>The part of <paramref name="person"/>, or zero.</summary>
    public decimal ShareOf(Guid person) => Shares.Where(s => s.Person == person).Sum(s => s.Amount);
}

/// <summary>Where one person stands in one currency: positive when others owe them, negative when they owe others.</summary>
public sealed record ExpenseBalance(Guid Person, string Currency, decimal Net);

/// <summary>One payment: <see cref="From"/> pays <see cref="To"/> an amount in a currency.</summary>
public sealed record Transfer(Guid From, Guid To, string Currency, decimal Amount);

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
            if (index >= 0) debts[index] = debts[index] with { Settled = debts[index].Settled + settled.Value, SettledOn = settled.On ?? debts[index].SettledOn };
            else if (issues == SplitIssues.None) debts.Add(new(expense.Id, settled.From, settled.To, currency, 0, settled.Value, settled.On));
        }
        return new(expense, money, method, payers, shares, debts, issues);
    }

    /// <summary>Every debt between people over all expenses, settled or not.</summary>
    public static IReadOnlyList<ExpenseDebt> Debts(TripSnapshot trip) => All(trip).SelectMany(s => s.Debts).ToArray();

    /// <summary>The debts that are not fully paid, optionally only those that concern <paramref name="person"/>.</summary>
    public static IReadOnlyList<ExpenseDebt> Open(TripSnapshot trip, Guid? person = null) =>
        Debts(trip).Where(d => !d.IsSettled && (person is not { } p || d.From == p || d.To == p)).ToArray();

    /// <summary>Where everyone stands per currency over the open debts. People who are even are left out.</summary>
    public static IReadOnlyList<ExpenseBalance> Balances(TripSnapshot trip)
    {
        var net = new Dictionary<(Guid, string), decimal>();
        foreach (var d in Open(trip))
        {
            net[(d.To, d.Currency)] = net.GetValueOrDefault((d.To, d.Currency)) + d.Open;
            net[(d.From, d.Currency)] = net.GetValueOrDefault((d.From, d.Currency)) - d.Open;
        }
        return net.Where(p => p.Value != 0).Select(p => new ExpenseBalance(p.Key.Item1, p.Key.Item2, p.Value))
            .OrderBy(b => b.Currency, StringComparer.Ordinal).ThenByDescending(b => b.Net).ThenBy(b => b.Person).ToArray();
    }

    /// <summary>
    /// Payments that settle every open debt, per currency, with as few payments as this finds: people who owe exactly
    /// what someone else is owed pay each other first, then whoever owes most pays whoever is owed most. Nobody pays
    /// more than they owe in all, and nobody receives more than they are owed.
    /// </summary>
    public static IReadOnlyList<Transfer> SettleUp(TripSnapshot trip) => Minimize(Balances(trip));

    /// <summary>The fewest payments this finds that bring the given balances to zero, per currency.</summary>
    public static IReadOnlyList<Transfer> Minimize(IEnumerable<ExpenseBalance> balances)
    {
        var result = new List<Transfer>();
        foreach (var group in balances.GroupBy(b => b.Currency, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var debtors = group.Where(b => b.Net < 0).Select(b => (b.Person, Amount: -b.Net)).OrderByDescending(x => x.Amount).ThenBy(x => x.Person).ToList();
            var creditors = group.Where(b => b.Net > 0).Select(b => (b.Person, Amount: b.Net)).OrderByDescending(x => x.Amount).ThenBy(x => x.Person).ToList();
            for (var i = 0; i < debtors.Count; i++)
            {
                var match = creditors.FindIndex(c => c.Amount == debtors[i].Amount);
                if (match < 0) continue;
                result.Add(new(debtors[i].Person, creditors[match].Person, group.Key, debtors[i].Amount));
                creditors.RemoveAt(match); debtors.RemoveAt(i--);
            }
            while (debtors.Count > 0 && creditors.Count > 0)
            {
                var d = debtors[0]; var c = creditors[0]; var amount = Math.Min(d.Amount, c.Amount);
                result.Add(new(d.Person, c.Person, group.Key, amount));
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
    public static PaymentPlan Pay(TripSnapshot trip, Guid person, IEnumerable<DebtKey> chosen)
    {
        var keys = chosen.ToHashSet();
        var items = Open(trip, person).Where(d => keys.Contains(d.Key)).ToArray();
        var payments = items.GroupBy(d => (Other: d.From == person ? d.To : d.From, d.Currency))
            .Select(g => (g.Key.Other, g.Key.Currency, Amount: g.Sum(d => d.From == person ? d.Open : -d.Open)))
            .Where(x => x.Amount != 0)
            .Select(x => x.Amount > 0 ? new Transfer(person, x.Other, x.Currency, x.Amount) : new Transfer(x.Other, person, x.Currency, -x.Amount))
            .OrderBy(t => t.Currency, StringComparer.Ordinal).ThenBy(t => t.From == person ? 0 : 1)
            .ThenBy(t => trip.Find(t.From == person ? t.To : t.From)?.Title, StringComparer.Ordinal).ThenBy(t => t.From == person ? t.To : t.From).ToArray();
        return new(person, items, payments);
    }

    /// <summary>
    /// Marks the given debts as paid in full on <paramref name="on"/> and returns the changed expenses. A debt that is
    /// owed back because more was paid than is owed counts as paid back. Other fields of the expense are kept.
    /// </summary>
    public static IReadOnlyList<Entity> Settle(TripSnapshot trip, IEnumerable<DebtKey> keys, LocalDate? on = null)
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
            }
            result.Add(copy);
        }
        return result;
    }

    /// <summary>Marks everything owed for an expense as paid, or, with <paramref name="from"/>, only what that person owes.</summary>
    public static Entity Settle(TripSnapshot trip, Entity expense, Guid? from = null, LocalDate? on = null)
    {
        var keys = Split(trip, expense).Debts.Where(d => from is not { } p || d.From == p).Select(d => d.Key).ToArray();
        return keys.Length == 0 ? expense.Copy() : Settle(trip, keys, on).Single();
    }

    /// <summary>Marks every open debt of the trip as paid, for example after everyone paid what <see cref="SettleUp"/> says.</summary>
    public static IReadOnlyList<Entity> SettleAll(TripSnapshot trip, LocalDate? on = null) => Settle(trip, Open(trip).Select(d => d.Key), on);

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

    /// <summary>Semantic checks for splits and paid marks; references to missing people are reported by <see cref="SemanticValidation"/>.</summary>
    public static IEnumerable<Diagnostic> Validate(TripSnapshot trip)
    {
        foreach (var expense in trip.Entities.Values.Where(e => e.Type == "expense"))
        {
            var path = expense.Id.ToString();
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
    private sealed record SettledMark(Guid From, Guid To, decimal Value, LocalDate? On);

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
                yield return new(from, to, value, entry["date"] is JsonValue d && LocalDatePattern.Iso.Parse(d.ToString()).TryGetValue(default, out var day) ? day : null);
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
