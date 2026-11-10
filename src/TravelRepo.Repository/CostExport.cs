using System.Globalization;
using System.Text;
using TravelRepo.Core;

namespace TravelRepo.Repository;

/// <summary>
/// What a cost export covers: everyone, or the parts of some people, such as one traveller or a couple, in the trip's
/// currency unless another is given. <see cref="Rates"/> are reference rates for amounts not charged in that currency.
/// </summary>
public sealed record CostExportOptions
{
    /// <summary>Whose costs: empty for everyone; otherwise their parts and the payments that concern them.</summary>
    public IReadOnlyList<Guid> People { get; init; } = [];
    /// <summary>The currency totals are shown in; the trip's currency when null.</summary>
    public string? Currency { get; init; }
    public ExchangeRates? Rates { get; init; }
}

/// <summary>Words of a cost export. English by default; clients pass translations.</summary>
public sealed record CostExportLabels
{
    public string Title { get; init; } = "Costs";
    /// <summary>Who the export is for, for example "for Ana and Ben".</summary>
    public string For { get; init; } = "for {0}";
    /// <summary>The currency it counts in, for example "in EUR".</summary>
    public string In { get; init; } = "in {0}";
    public string Overview { get; init; } = "Overview";
    public string Paid { get; init; } = "Paid";
    public string ToPay { get; init; } = "Still to pay";
    public string Share { get; init; } = "Their part";
    public string Budget { get; init; } = "Budget";
    public string BudgetUsed { get; init; } = "{0} of {1}, {2} left";
    public string NotConverted { get; init; } = "Not converted, no charged amount or rate: {0}";
    public string Estimated { get; init; } = "≈ estimated with reference rates where no charged amount was entered";
    public string ByCategory { get; init; } = "By category";
    public string WhoOwesWhom { get; init; } = "Who owes whom";
    public string PaysTo { get; init; } = "{0} pays {1}";
    public string AllEven { get; init; } = "Everyone is even.";
    public string Payments { get; init; } = "Payments";
    public string PaidBy { get; init; } = "paid by {0}";
    public string SharedBy { get; init; } = "shared: {0}";
    public string Owes { get; init; } = "{0} owes {1} {2}";
    public string Charged { get; init; } = "charged {0}";
    public string StateToPay { get; init; } = "to pay";
    /// <summary>A payment still to pay for an idea or something cancelled, which does not count.</summary>
    public string StateIdea { get; init; } = "idea, not counted";
    /// <summary>What payments for ideas would cost, apart from what is still to pay.</summary>
    public string Ideas { get; init; } = "Ideas, not counted";
    /// <summary>A debt paid back, for example "Ben paid back Ana 30.00 NZD".</summary>
    public string PaidBack { get; init; } = "{0} paid back {1} {2}";
    /// <summary>What was transferred for it in another currency, for example "as 17.10 EUR".</summary>
    public string TransferredAs { get; init; } = "as {0}";
    public string StateSettled { get; init; } = "settled";
    public string StateOpen { get; init; } = "{0} open";
    public string DueOn { get; init; } = "due {0}";
    // Columns of the CSV file.
    public string Date { get; init; } = "Date";
    public string Payment { get; init; } = "Payment";
    public string Category { get; init; } = "Category";
    public string BelongsTo { get; init; } = "Belongs to";
    public string Amount { get; init; } = "Amount";
    public string Currency { get; init; } = "Currency";
    public string ChargedAmount { get; init; } = "Charged";
    public string ChargedCurrency { get; init; } = "Charged currency";
    public string InCurrency { get; init; } = "In {0}";
    public string PaidByColumn { get; init; } = "Paid by";
    public string State { get; init; } = "State";
    public string Open { get; init; } = "Open";
    public string Due { get; init; } = "Due";
    /// <summary>A column with one person's part, for example "Part Ana".</summary>
    public string PartOf { get; init; } = "Part {0}";
    /// <summary>The column with the chosen people's parts together in the export's currency.</summary>
    public string PartsIn { get; init; } = "Their part in {0}";
    public string GeneratedBy { get; init; } = "Exported from a TravelRepo trip";
    /// <summary>Names of the standard categories (section 34.2); others are shown as the travellers typed them.</summary>
    public IReadOnlyDictionary<string, string> Categories { get; init; } = new Dictionary<string, string>();
    public string CategoryName(string category) => Categories.GetValueOrDefault(category) ?? category;
}

/// <summary>One payment of a cost export, with the parts of the people in it.</summary>
public sealed record CostExportRow(Entity Expense, LocalDateOrNone Day, string Category, string? BelongsTo, Money Amount, Charge? Charge, (decimal Amount, Conversion How) InCurrency, IReadOnlyList<string> Payers, ExpenseSplit Split, bool Planned, NodaTime.LocalDate? Due, decimal ChosenParts, (decimal Amount, Conversion How) ChosenPartsInCurrency)
{
    /// <summary>A payment still to pay for an idea or something cancelled; it does not count.</summary>
    public bool Idea { get; init; }
}

/// <summary>A day or none, so rows sort with undated payments last.</summary>
public readonly record struct LocalDateOrNone(NodaTime.LocalDate? Value);

/// <summary>
/// Exports of a trip's costs (specification section 45.2): every payment with its parts, totals in the trip's
/// currency, by category, the budget and who owes whom; for everyone or the parts of some people. As a CSV file with a
/// chosen separator, or as an <see cref="ExportDocument"/> for PDF and text renderers.
/// </summary>
public static class CostExport
{
    /// <summary>
    /// The payments the export lists, by day and title: all of them, or those the chosen people paid, share, or owe or
    /// are owed money for.
    /// </summary>
    public static IReadOnlyList<CostExportRow> Rows(TripSnapshot trip, CostExportOptions? options = null)
    {
        options ??= new();
        var currency = options.Currency ?? TripCurrency.Of(trip) ?? "EUR";
        var chosen = options.People.Distinct().ToArray();
        var rows = new List<CostExportRow>();
        foreach (var expense in trip.Entities.Values.Where(e => e.Type == "expense"))
        {
            if (TripSummaries.Money(expense.Data["amount"]) is not { } money) continue;
            var split = Expenses.Split(trip, expense);
            var payers = split.Payers.ToArray();
            if (chosen.Length > 0 && !chosen.Any(p => payers.Contains(p) || split.People.Contains(p) || split.Debts.Any(d => d.From == p || d.To == p))) continue;
            var related = (expense.Data["related"] as System.Text.Json.Nodes.JsonArray ?? []).Select(r => Guid.TryParse(r?["id"]?.ToString(), out var id) ? trip.Find(id)?.Title : null).OfType<string>().FirstOrDefault();
            var full = Expenses.Convert(expense, money.Amount, money.Currency, currency, options.Rates);
            var parts = chosen.Sum(split.ShareOf);
            var partsIn = Expenses.Convert(expense, parts, money.Currency, currency, options.Rates);
            rows.Add(new(expense, new(Expenses.DayOf(trip, expense)), ExpenseCategories.Of(trip, expense), related, money, Expenses.ChargeOf(expense),
                (full.Amount, full.Currency == currency ? full.How : Conversion.None), payers.Select(p => trip.Find(p)?.Title ?? "").ToArray(), split,
                Expenses.IsPlanned(expense), Expenses.DueOf(expense), parts, (partsIn.Amount, partsIn.Currency == currency ? partsIn.How : Conversion.None))
            { Idea = Expenses.IsIdea(trip, expense) });
        }
        return rows.OrderBy(r => r.Day.Value is null ? 1 : 0).ThenBy(r => r.Day.Value).ThenBy(r => r.Expense.Title, StringComparer.CurrentCulture).ToArray();
    }

    /// <summary>The separators offered for CSV files, by name.</summary>
    public static readonly IReadOnlyDictionary<string, string> Separators = new Dictionary<string, string> { ["comma"] = ",", ["semicolon"] = ";", ["tab"] = "\t", ["pipe"] = "|" };

    /// <summary>
    /// One row per payment, for spreadsheets. Amounts are plain numbers with the culture's decimal separator, or a point
    /// when that is the column separator; no thousands separators. One column per person shows their part in the
    /// payment's currency; with chosen people, a last column adds up their parts in the export's currency. Cells a
    /// spreadsheet would read as a formula are prefixed with an apostrophe.
    /// </summary>
    public static string Csv(TripSnapshot trip, CostExportOptions? options = null, CostExportLabels? labels = null, CultureInfo? culture = null, string separator = ",")
    {
        options ??= new(); labels ??= new(); culture ??= CultureInfo.CurrentCulture;
        var currency = options.Currency ?? TripCurrency.Of(trip) ?? "EUR";
        var decimalPoint = culture.NumberFormat.NumberDecimalSeparator == separator ? "." : culture.NumberFormat.NumberDecimalSeparator;
        string Number(decimal value, string code) => Expenses.Format(value, code).Replace(".", decimalPoint, StringComparison.Ordinal);
        var people = options.People.Count > 0 ? options.People.Select(trip.Find).OfType<Entity>().ToArray() : TripPeople.Travelers(trip).ToArray();
        var csv = new StringBuilder();
        void Row(IEnumerable<string> cells) => csv.Append(string.Join(separator, cells)).Append("\r\n");
        string Text(string? value) => PackingExport.Cell(value ?? "", separator);
        // Numbers may start with a minus; they are quoted only when needed, never prefixed.
        string Num(string value) => value.Contains(separator, StringComparison.Ordinal) ? "\"" + value + "\"" : value;
        var header = new List<string> { labels.Date, labels.Payment, labels.Category, labels.BelongsTo, labels.Amount, labels.Currency, labels.ChargedAmount, labels.ChargedCurrency, string.Format(culture, labels.InCurrency, currency), labels.PaidByColumn, labels.State, labels.Open, labels.Due };
        header.AddRange(people.Select(p => string.Format(culture, labels.PartOf, p.Title)));
        if (options.People.Count > 0) header.Add(string.Format(culture, labels.PartsIn, currency));
        Row(header.Select(Text));
        foreach (var r in Rows(trip, options))
        {
            var code = r.Amount.Currency;
            var state = r.Idea ? labels.StateIdea : r.Planned ? labels.StateToPay : r.Split.Debts.Count == 0 ? "" : r.Split.IsSettled ? labels.StateSettled : string.Format(culture, labels.StateOpen, Number(r.Split.Open, code) + " " + code);
            var cells = new List<string>
            {
                Text(r.Day.Value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), Text(r.Expense.Title), Text(labels.CategoryName(r.Category)), Text(r.BelongsTo),
                Num(Number(r.Amount.Amount, code)), Text(code),
                r.Charge is { } c ? Num(Number(c.Value, c.Currency)) : "", Text(r.Charge?.Currency),
                r.InCurrency.How == Conversion.None ? "" : Num(Number(r.InCurrency.Amount, currency)),
                Text(string.Join(", ", r.Payers)), Text(state), r.Split.Debts.Count == 0 ? "" : Num(Number(r.Split.Open, code)),
                Text(r.Due?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            };
            cells.AddRange(people.Select(p => r.Split.People.Contains(p.Id) ? Num(Number(r.Split.ShareOf(p.Id), code)) : ""));
            if (options.People.Count > 0) cells.Add(r.ChosenPartsInCurrency.How == Conversion.None ? "" : Num(Number(r.ChosenPartsInCurrency.Amount, currency)));
            Row(cells);
        }
        return csv.ToString();
    }

    /// <summary>
    /// The export as a document: an overview in the export's currency (paid, still to pay, the budget, or the chosen
    /// people's parts), totals by category, who owes whom with the payments that settle it, and every payment with
    /// its parts and what is open. Renderers make PDF or text of it.
    /// </summary>
    /// <remarks><paramref name="page"/> are the words renderers use around the content, such as on every page.</remarks>
    public static ExportDocument Document(TripSnapshot trip, CostExportOptions? options = null, CostExportLabels? labels = null, CultureInfo? culture = null, ExportLabels? page = null)
    {
        options ??= new(); labels ??= new(); culture ??= CultureInfo.CurrentCulture;
        var currency = options.Currency ?? TripCurrency.Of(trip) ?? "EUR";
        string M(decimal value, string code) => value.ToString("N" + Expenses.MinorDigits(code), culture) + " " + code;
        string Name(Guid id) => trip.Find(id)?.Title ?? "";
        var chosen = options.People.Distinct().ToArray();
        var rows = Rows(trip, options);
        var approximate = false;
        var sections = new List<ExportSection>();

        // Overview: for everyone what was paid and is still to pay, and the budget; for some people their parts.
        var overview = new List<ExportRow>();
        string Sum(IEnumerable<(decimal Amount, Conversion How, Money Original)> items)
        {
            var list = items.ToArray();
            var inCurrency = list.Where(x => x.How != Conversion.None).ToArray();
            approximate |= inCurrency.Any(x => x.How == Conversion.Reference);
            var apart = list.Where(x => x.How == Conversion.None).GroupBy(x => x.Original.Currency).Select(g => M(g.Sum(x => x.Original.Amount), g.Key));
            // No zero in front of what could not be converted: "156,000 JPY", not "0.00 EUR + 156,000 JPY".
            var converted = inCurrency.Length > 0 && (inCurrency.Sum(x => x.Amount) != 0 || !apart.Any()) ? (inCurrency.Any(x => x.How == Conversion.Reference) ? "≈ " : "") + M(inCurrency.Sum(x => x.Amount), currency) : null;
            return string.Join(" + ", new[] { converted }.OfType<string>().Concat(apart));
        }
        if (chosen.Length == 0)
        {
            overview.Add(new(labels.Paid, Sum(rows.Where(r => !r.Planned).Select(r => (r.InCurrency.Amount, r.InCurrency.How, r.Amount)))));
            if (rows.Any(r => r.Planned && !r.Idea)) overview.Add(new(labels.ToPay, Sum(rows.Where(r => r.Planned && !r.Idea).Select(r => (r.InCurrency.Amount, r.InCurrency.How, r.Amount)))));
            if (rows.Any(r => r.Idea)) overview.Add(new(labels.Ideas, Sum(rows.Where(r => r.Idea).Select(r => (r.InCurrency.Amount, r.InCurrency.How, r.Amount)))));
            if (Budgets.ForTrip(trip, options.Rates) is { } budget)
                overview.Add(new(labels.Budget, M(budget.Amount, budget.Currency), string.Format(culture, labels.BudgetUsed, (budget.Approximate ? "≈ " : "") + M(budget.Used, budget.Currency), M(budget.Amount, budget.Currency), M(budget.Left, budget.Currency))));
        }
        else
        {
            overview.Add(new(labels.Share + " · " + labels.Paid, Sum(rows.Where(r => !r.Planned && r.ChosenParts != 0).Select(r => (r.ChosenPartsInCurrency.Amount, r.ChosenPartsInCurrency.How, new Money(Expenses.Format(r.ChosenParts, r.Amount.Currency), r.Amount.Currency))))));
            if (rows.Any(r => r.Planned && !r.Idea && r.ChosenParts != 0)) overview.Add(new(labels.Share + " · " + labels.ToPay, Sum(rows.Where(r => r.Planned && !r.Idea && r.ChosenParts != 0).Select(r => (r.ChosenPartsInCurrency.Amount, r.ChosenPartsInCurrency.How, new Money(Expenses.Format(r.ChosenParts, r.Amount.Currency), r.Amount.Currency))))));
            foreach (var b in Budgets.Limited(trip, options.Rates).Where(b => b.Filters.Person is { } p && chosen.Contains(p)))
                overview.Add(new(b.Budget.Title, M(b.Amount.Amount, b.Amount.Currency), string.Format(culture, labels.BudgetUsed, (b.Approximate ? "≈ " : "") + M(b.Used, b.Amount.Currency), M(b.Amount.Amount, b.Amount.Currency), M(b.Left, b.Amount.Currency))));
        }
        sections.Add(new(ExportParts.Costs, labels.Overview, overview));

        var categories = ExpenseCategories.Totals(trip, currency, options.Rates, chosen.Length > 0 ? chosen : null);
        if (categories.Count > 0)
        {
            approximate |= categories.Any(c => c.Estimated);
            sections.Add(new(ExportParts.Costs, labels.ByCategory, categories.Select(c => new ExportRow(labels.CategoryName(c.Category), (c.Estimated ? "≈ " : "") + M(c.Amount, c.Currency))).ToArray()));
        }

        var transfers = Expenses.SettleUp(trip, currency, options.Rates).Where(t => chosen.Length == 0 || chosen.Contains(t.From) || chosen.Contains(t.To)).ToArray();
        approximate |= transfers.Any(t => t.Estimated);
        sections.Add(new(ExportParts.Costs, labels.WhoOwesWhom, transfers.Length == 0 ? [new ExportRow(labels.AllEven)]
            : transfers.Select(t => new ExportRow(string.Format(culture, labels.PaysTo, Name(t.From), Name(t.To)), (t.Estimated ? "≈ " : "") + M(t.Amount, t.Currency))).ToArray()));

        var payments = rows.Select(r =>
        {
            var code = r.Amount.Currency;
            var lead = M(r.Amount.Amount, code) + (r.Charge is { } c ? " · " + string.Format(culture, labels.Charged, M(c.Value, c.Currency)) : "");
            var state = r.Idea ? labels.StateIdea : r.Planned ? labels.StateToPay + (r.Due is { } due ? " · " + string.Format(culture, labels.DueOn, due.ToString("d", culture)) : "")
                : r.Split.Debts.Count == 0 ? null : r.Split.IsSettled ? labels.StateSettled : string.Format(culture, labels.StateOpen, M(r.Split.Open, code));
            var summary = string.Join(" · ", new[] { r.Day.Value?.ToString("d", culture), labels.CategoryName(r.Category), r.BelongsTo, r.Payers.Count > 0 ? string.Format(culture, labels.PaidBy, string.Join(", ", r.Payers)) : null, state }.Where(x => !string.IsNullOrEmpty(x)));
            var details = new List<string>();
            if (r.Split.Shares.Count > 0) details.Add(string.Format(culture, labels.SharedBy, string.Join(", ", r.Split.Shares.Select(x => Name(x.Person) + " " + M(x.Amount, code)))));
            foreach (var d in r.Split.Debts.Where(d => chosen.Length == 0 || chosen.Contains(d.From) || chosen.Contains(d.To)))
            {
                // Paid back, with what was transferred in another currency, and what is still open.
                if (d.Settled > 0) details.Add(string.Format(culture, labels.PaidBack, Name(d.From), Name(d.To), M(Math.Min(d.Settled, d.Amount), d.Currency))
                    + (d.PaidIn is { } t ? " " + string.Format(culture, labels.TransferredAs, (t.Reference ? "≈ " : "") + M(t.Value, t.Currency)) : ""));
                if (!d.IsSettled) details.Add(d.Open > 0 ? string.Format(culture, labels.Owes, Name(d.From), Name(d.To), M(d.Open, d.Currency)) : string.Format(culture, labels.Owes, Name(d.To), Name(d.From), M(-d.Open, d.Currency)));
            }
            return new ExportRow(r.Expense.Title, lead, summary, details);
        }).ToArray();
        sections.Add(new(ExportParts.Costs, labels.Payments, payments));

        var meta = new List<string> { labels.Title + (chosen.Length > 0 ? " · " + string.Format(culture, labels.For, string.Join(", ", chosen.Select(Name))) : "") + " · " + string.Format(culture, labels.In, currency) };
        if (approximate) meta.Add(labels.Estimated);
        return new(trip.Manifest.Title, meta, sections, null, [], labels.GeneratedBy, trip.Manifest.Data["language"]?.ToString() ?? "en", new ExportOptions { Parts = ExportParts.Costs }, page ?? new ExportLabels());
    }

    /// <summary>A file name for the export, such as "Japan - Costs - Ana.csv".</summary>
    public static string FileName(TripSnapshot trip, CostExportOptions options, CostExportLabels labels, string extension)
    {
        var who = options.People.Count > 0 ? " - " + string.Join(", ", options.People.Select(p => trip.Find(p)?.Title).OfType<string>()) : "";
        var name = trip.Manifest.Title + " - " + labels.Title + who;
        foreach (var c in Path.GetInvalidFileNameChars().Append(':').Append('/')) name = name.Replace(c, '-');
        return name + "." + extension;
    }
}
