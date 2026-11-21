using System.Text.Json.Nodes;
using NodaTime;
using NodaTime.Text;
using TravelRepo.Core;
using TravelRepo.Providers.Weather;
using TravelRepo.Repository;

namespace TravelRepo.Native;

/// <summary>
/// Read models for the rest of an app's screens (costs, packing, bookings, stays, reminders), fetching weather and
/// exchange rates, exports in the session's language and importing documents from bytes. Like <see cref="ClientCommands"/>,
/// results carry TravelRepo's interpretation as plain values, not the text the MCP tools write for assistants.
/// </summary>
internal static class AppCommands
{
    public static readonly IReadOnlyList<string> Names =
        ["costs", "packing", "bookings", "stays", "reminders", "fetch_exchange_rates", "fetch_weather", "export", "import_document_data", "me", "claim_person"];

    public static readonly IReadOnlyList<string> ReadOnly = ["costs", "packing", "bookings", "stays", "reminders", "export", "me"];

    /// <summary>The HTTP client for weather services and exchange rates. Tests replace it.</summary>
    public static HttpClient Http { get; set; } = new();

    private static string UserAgent => "TravelRepo/" + TravelRepoInfo.Version;

    public static async Task<(bool Found, JsonNode? Result)> RunAsync(NativeSession session, string command, Arguments args, CancellationToken ct)
    {
        if (!Names.Contains(command)) return (false, null);
        if (session.ReadOnly && !ReadOnly.Contains(command) && command != "fetch_weather" && command != "fetch_exchange_rates")
            throw new DomainException("session.read_only", "This session is read-only.");
        var trip = (await session.ReadAsync(ct)).Trip;
        switch (command)
        {
            case "costs": return (true, Costs(trip, args.String("currency"), session.ExchangeRates()));
            case "packing": return (true, Packing(trip));
            case "bookings": return (true, Bookings(trip));
            case "stays": return (true, Stays(trip));
            case "reminders":
                {
                    var now = args.String("now") is { } text ? Instant(text) : SystemClock.Instance.GetCurrentInstant();
                    return (true, new JsonObject
                    {
                        ["reminders"] = Array(TripReminders.For(trip, now).Select(r => (JsonNode)new JsonObject
                        {
                            ["key"] = r.Key,
                            ["kind"] = ClientCommands.Snake(r.Kind.ToString()),
                            ["entity"] = r.Entity?.ToString(),
                            ["title"] = r.Title,
                            ["moment"] = Time(r.Moment),
                            ["urgent"] = r.Urgent,
                            ["dates"] = Array(r.Dates.Select(d => (JsonNode)Date(d)!))
                        }))
                    });
                }
            case "fetch_exchange_rates":
                {
                    var rates = await ExchangeRates.FetchAsync(Http, UserAgent, ct);
                    if (session.ExchangeRatesPath is { } path) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!); rates.Save(path); }
                    return (true, new JsonObject { ["date"] = rates.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), ["saved"] = session.ExchangeRatesPath is not null, ["currencies"] = Array(rates.Currencies.Select(c => (JsonNode)c)) });
                }
            case "fetch_weather":
                {
                    var folder = session.WeatherCache ?? throw new DomainException("weather.cache", "Open the trip with weather_cache to fetch weather.");
                    var service = new WeatherService(WeatherService.BuiltIn(Http, GitHubCommands.Secrets, UserAgent), new OpenMeteo(Http, UserAgent), new WeatherCache(folder), session.WeatherProvider);
                    return (true, Weather(await service.UpdateAsync(trip, args.Bool("refresh", false), ct)));
                }
            case "export": return (true, Export(trip, args));
            case "me":
                {
                    // Who the person using the app is in this trip, from their GitHub login or Git e-mail address.
                    var match = TripPeople.Match(trip, args.String("github_login"), args.String("git_email"));
                    return (true, new JsonObject
                    {
                        ["person"] = match?.Person.Id.ToString(),
                        ["name"] = match?.Person.Title,
                        ["source"] = match?.Source switch { PersonMatchSource.GitHub => "github", PersonMatchSource.Email => "email", _ => null },
                        ["travelers"] = Array(TripPeople.Travelers(trip).Select(p => (JsonNode)new JsonObject
                        {
                            ["id"] = p.Id.ToString(),
                            ["name"] = p.Title,
                            ["claimed_by_someone_else"] = TripPeople.HasOtherIdentity(p, args.String("github_login"), args.String("git_email"))
                        }))
                    });
                }
            case "claim_person":
                {
                    // "That's me": the person gets the user's GitHub login and Git identity, so the next match finds them.
                    var id = args.Required("person");
                    for (var attempt = 0; ; attempt++)
                    {
                        var state = await session.ReadAsync(ct);
                        var person = Guid.TryParse(id, out var guid) ? state.Trip.Find(guid) : null;
                        if (person is not { Type: "person" }) throw new DomainException("entity.missing", "There is no person " + id + ".");
                        var claimed = TripPeople.WithIdentity(person, args.String("github_login"), args.String("git_name"), args.String("git_email"));
                        try { session.Remember(await session.Repository.ApplyAsync(state, [new(person.Id, claimed)], ct)); return (true, new JsonObject { ["person"] = id }); }
                        catch (DomainException ex) when (ex.Code == "repository.changed" && attempt < 2) { }
                    }
                }
            case "import_document_data": return (true, await ImportAsync(session, args, ct));
        }
        return (false, null);
    }

    private static JsonObject Costs(TripSnapshot trip, string? currency, ExchangeRates? rates)
    {
        static JsonNode? Money(Money? m) => m is null ? null : new JsonObject { ["value"] = m.Value, ["currency"] = m.Currency };
        static JsonNode Debt(ExpenseDebt d) => new JsonObject
        {
            ["expense"] = d.Expense.ToString(),
            ["from"] = d.From.ToString(),
            ["to"] = d.To.ToString(),
            ["currency"] = d.Currency,
            ["amount"] = Dec(d.Amount),
            ["settled"] = Dec(d.Settled),
            ["open"] = Dec(d.Open),
            ["settled_on"] = Date(d.SettledOn)
        };
        return new JsonObject
        {
            ["expenses"] = Array(Expenses.All(trip).Select(s => (JsonNode)new JsonObject
            {
                ["id"] = s.Expense.Id.ToString(),
                ["title"] = s.Expense.Title,
                ["amount"] = Money(s.Amount),
                ["method"] = s.Method is { } m ? ClientCommands.Snake(m.ToString()) : null,
                ["planned"] = Expenses.IsPlanned(s.Expense),
                ["category"] = ExpenseCategories.Of(trip, s.Expense),
                ["payers"] = Array(s.Payers.Select(p => (JsonNode)p.ToString())),
                ["shares"] = Array(s.Shares.Select(x => (JsonNode)new JsonObject { ["person"] = x.Person.ToString(), ["amount"] = Dec(x.Amount), ["value"] = x.Value is { } v ? Dec(v) : null })),
                ["debts"] = Array(s.Debts.Select(Debt)),
                ["issues"] = Flags(s.Issues.ToString())
            })),
            ["open_debts"] = Array(Expenses.Open(trip).Select(Debt)),
            ["balances"] = Array(Expenses.Balances(trip, currency, rates).Select(b => (JsonNode)new JsonObject { ["person"] = b.Person.ToString(), ["currency"] = b.Currency, ["net"] = Dec(b.Net) })),
            ["settle_up"] = Array(Expenses.SettleUp(trip, currency, rates).Select(t => (JsonNode)new JsonObject { ["from"] = t.From.ToString(), ["to"] = t.To.ToString(), ["currency"] = t.Currency, ["amount"] = Dec(t.Amount) })),
            ["totals"] = Array(TripSummaries.Costs(trip).Select(t => (JsonNode)new JsonObject { ["currency"] = t.Currency, ["paid"] = Dec(t.Paid), ["estimated"] = Dec(t.Estimated), ["budget"] = Dec(t.Budget) })),
            ["categories"] = Array(ExpenseCategories.Totals(trip, currency, rates).Select(c => (JsonNode)new JsonObject { ["category"] = c.Category, ["currency"] = c.Currency, ["amount"] = Dec(c.Amount), ["estimated"] = c.Estimated })),
            ["budgets"] = Array(Budgets.All(trip, rates).Select(b => (JsonNode)new JsonObject
            {
                ["id"] = b.Budget.Id.ToString(),
                ["title"] = b.Budget.Title,
                ["scope"] = b.Scope,
                ["target"] = b.Target,
                ["amount"] = Money(b.Amount),
                ["spent"] = Dec(b.Spent),
                ["planned"] = Dec(b.Planned),
                ["approximate"] = b.Approximate,
                ["not_included"] = Array(b.NotIncluded.Select(n => Money(n)!))
            }))
        };
    }

    private static JsonObject Packing(TripSnapshot trip) => new()
    {
        ["lists"] = Array(PackingLists.All(trip).Select(l => (JsonNode)new JsonObject
        {
            ["id"] = l.Collection.Id.ToString(),
            ["title"] = l.Collection.Title,
            ["items"] = Array(l.Items.Select(i => (JsonNode)new JsonObject
            {
                ["id"] = i.Task.Id.ToString(),
                ["title"] = i.Task.Title,
                ["group"] = i.Group,
                ["quantity"] = i.Quantity,
                ["everyone"] = i.Everyone,
                ["assignees"] = Array(i.Assignees.Select(a => (JsonNode)a.Id.ToString())),
                ["packed_by"] = Array(i.PackedBy.Select(p => (JsonNode)p.ToString()))
            }))
        }))
    };

    private static JsonObject Bookings(TripSnapshot trip) => new()
    {
        ["bookings"] = Array(Expenses.Payments(trip).Select(p => (JsonNode)new JsonObject
        {
            ["id"] = p.Booking.Id.ToString(),
            ["title"] = p.Booking.Title,
            ["coverage"] = ClientCommands.Snake(p.Coverage.ToString()),
            ["items"] = Array(TripCommands.BookedItems(trip, p.Booking.Id).Select(i => (JsonNode)i.ToString())),
            ["paid"] = Array(p.Paid.Select(e => (JsonNode)e.Id.ToString())),
            ["planned"] = Array(p.Planned.Select(e => (JsonNode)e.Id.ToString()))
        }))
    };

    private static JsonObject Stays(TripSnapshot trip) => new()
    {
        ["stays"] = Array(TravelRepo.Core.Stays.All(trip).Select(s => (JsonNode)new JsonObject
        {
            ["id"] = s.Item.Id.ToString(),
            ["title"] = s.Item.Title,
            ["first_night"] = Date(s.FirstNight),
            ["nights"] = s.Nights,
            ["zone"] = s.Zone,
            ["check_in"] = s.CheckIn?.ToJson(),
            ["check_out"] = s.CheckOut?.ToJson(),
            ["issues"] = Flags(s.Issues.ToString())
        }))
    };

    private static JsonObject Weather(TripWeather weather)
    {
        static JsonNode Outlook(WeatherOutlook o) => new JsonObject
        {
            ["kind"] = ClientCommands.Snake(o.Kind.ToString()),
            ["place"] = o.Place.ToString(),
            ["provider"] = o.Provider,
            ["fetched"] = Time(o.Fetched),
            ["day"] = o.Day is not { } d ? null : new JsonObject
            {
                ["date"] = Date(d.Date),
                ["max_c"] = d.MaxC,
                ["min_c"] = d.MinC,
                ["precipitation_chance"] = d.PrecipitationChance,
                ["precipitation_mm"] = d.PrecipitationMm,
                ["gust_kmh"] = d.GustKmh,
                ["condition"] = ClientCommands.Snake(d.Condition.ToString()),
                ["sunrise"] = Time(d.Sunrise),
                ["sunset"] = Time(d.Sunset)
            },
            ["climate"] = o.Climate is not { } c ? null : new JsonObject { ["max_c"] = c.MaxC, ["min_c"] = c.MinC, ["rain_day_share"] = c.RainDayShare, ["precipitation_mm"] = c.PrecipitationMm, ["years"] = c.Years },
            ["hours"] = Array(o.Hours.Select(h => (JsonNode)new JsonObject
            {
                ["time"] = Time(h.Time),
                ["hours"] = h.Hours,
                ["temperature_c"] = h.TemperatureC,
                ["precipitation_chance"] = h.PrecipitationChance,
                ["precipitation_mm"] = h.PrecipitationMm,
                ["wind_kmh"] = h.WindKmh,
                ["gust_kmh"] = h.GustKmh,
                ["condition"] = ClientCommands.Snake(h.Condition.ToString())
            }))
        };
        return new JsonObject
        {
            ["days"] = Array(weather.Days.Values.OrderBy(d => d.Date).Select(d => (JsonNode)new JsonObject { ["date"] = Date(d.Date), ["place"] = d.Place.ToString(), ["outlook"] = Outlook(d.Outlook) })),
            ["items"] = Array(weather.Items.Values.Select(i => (JsonNode)new JsonObject
            {
                ["id"] = i.Item.ToString(),
                ["outlooks"] = Array(i.Outlooks.Select(Outlook)),
                ["concerns"] = Array(i.Concerns.Select(c => (JsonNode)new JsonObject { ["kind"] = ClientCommands.Snake(c.Kind.ToString()), ["severe"] = c.Severe, ["value"] = c.Value }))
            })),
            ["sources"] = Array(weather.Sources.Select(s => (JsonNode)new JsonObject { ["text"] = s.Text, ["url"] = s.Url, ["license"] = s.License })),
            ["problems"] = Array(weather.Problems.Select(p => (JsonNode)new JsonObject { ["provider"] = p.Provider, ["code"] = p.Code, ["message"] = p.Message }))
        };
    }

    /// <summary>Exports in the session's culture: kind plan (text, html, ics), costs (text, csv) or packing (text, csv).</summary>
    private static JsonObject Export(TripSnapshot trip, Arguments args)
    {
        var kind = args.String("kind", "plan"); var format = args.String("format", "text");
        Guid? person = args.String("person") is { } p ? Guid.TryParse(p, out var id) ? id : throw new DomainException("argument.invalid", "person must be an entity id.") : null;
        var text = (kind, format) switch
        {
            ("plan", "text") => TripExport.Text(TripExport.Document(trip, new ExportOptions { Person = person })),
            ("plan", "html") => TripExport.Html(TripExport.Document(trip, new ExportOptions { Person = person })),
            ("plan", "ics") => TripExport.Ics(trip, new ExportOptions { Person = person }),
            ("costs", "text") => TripExport.Text(CostExport.Document(trip, new CostExportOptions { People = person is { } a ? [a] : [] })),
            ("costs", "csv") => CostExport.Csv(trip, new CostExportOptions { People = person is { } b ? [b] : [] }),
            ("packing", "text") => PackingExport.Text(trip, new PackingExportOptions { Person = person }),
            ("packing", "csv") => PackingExport.Csv(trip, new PackingExportOptions { Person = person }),
            _ => throw new DomainException("argument.invalid", $"There is no {format} export of {kind}.")
        };
        return new JsonObject { ["kind"] = kind, ["format"] = format, ["text"] = text };
    }

    /// <summary>Adds a document from bytes, as the share sheet or a file picker hands it over, and optionally attaches it.</summary>
    private static async Task<JsonNode> ImportAsync(NativeSession session, Arguments args, CancellationToken ct)
    {
        var name = args.Required("name");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(args.Required("base64")); }
        catch (FormatException) { throw new DomainException("argument.invalid", "base64 is not valid."); }
        var (document, resource) = TravelRepository.PrepareDocument(bytes, name, args.String("media_type") ?? Documents.MediaTypes.FromFileName(name));
        for (var attempt = 0; ; attempt++)
        {
            var state = await session.ReadAsync(ct);
            var edits = new List<EntityEdit> { new(document.Id, document) };
            if (args.String("attach_to") is { } target)
            {
                var owner = Guid.TryParse(target, out var ownerId) ? state.Trip.Find(ownerId) : null;
                if (owner is null) throw new DomainException("entity.missing", "There is no entity " + target + " to attach the document to.");
                edits.Add(new(owner.Id, TripCommands.AttachDocument(owner, document)));
            }
            try
            {
                var saved = await session.Repository.ApplyAsync(state, edits, ct, [resource]);
                return new JsonObject { ["id"] = document.Id.ToString(), ["media_type"] = document.Data["media_type"]?.DeepClone(), ["revision"] = session.Remember(saved) };
            }
            catch (DomainException ex) when (ex.Code == "repository.changed" && attempt < 2) { }
        }
    }

    /// <summary>Amounts as decimal strings, as money is written in trip files, so no client loses digits to a double.</summary>
    private static JsonNode Dec(decimal value) => JsonValue.Create(value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static JsonArray Array(IEnumerable<JsonNode> items) => new(items.ToArray());
    private static JsonArray Flags(string flags) => flags == "None" ? [] : Array(flags.Split(", ").Select(f => (JsonNode)ClientCommands.Snake(f)));
    private static string? Date(LocalDate? date) => date is { } d ? LocalDatePattern.Iso.Format(d) : null;
    private static string? Time(Instant? instant) => instant is { } i ? InstantPattern.General.Format(i) : null;
    private static Instant Instant(string text) => InstantPattern.ExtendedIso.Parse(text).TryGetValue(default, out var value) ? value : throw new DomainException("argument.invalid", "now must be an ISO 8601 instant in UTC.");
}
