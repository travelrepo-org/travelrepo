using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using NodaTime;
using NodaTime.Text;

namespace TravelRepo.Core;

/// <summary>What a search hit is, as travellers think of it. A packing list and its items are <see cref="Packing"/>.</summary>
public enum SearchKind { Plan, Booking, Cost, Place, Task, Packing, Note, File }

/// <summary>The part of an entity a search word was found in.</summary>
public enum SearchField
{
    /// <summary>The title or name.</summary>
    Title,
    /// <summary>The name of a plan's place.</summary>
    Place,
    /// <summary>A place's name in its own language and script.</summary>
    LocalName,
    Address,
    /// <summary>Where a journey starts.</summary>
    From,
    /// <summary>Where a journey ends.</summary>
    To,
    /// <summary>A carrier or the provider of a booking.</summary>
    Provider,
    /// <summary>A connection number, such as a flight number.</summary>
    Number,
    /// <summary>A booking reference.</summary>
    Reference,
    /// <summary>Participants, payers, the people an expense is for, or assignees.</summary>
    People,
    Category,
    Amount,
    /// <summary>The plan or booking an expense belongs to.</summary>
    BelongsTo,
    /// <summary>The plans a booking is for.</summary>
    Plans,
    /// <summary>The packing list an item is on.</summary>
    List,
    /// <summary>The group of a packing item.</summary>
    Group,
    /// <summary>The caption of a file.</summary>
    Caption,
    /// <summary>Note text. Only searched with <see cref="SearchOptions.Notes"/>.</summary>
    Notes
}

/// <summary>A part of a text, in UTF-16 code units, for highlighting.</summary>
public readonly record struct TextRange(int Start, int Length);

/// <summary>
/// A field that search words were found in: its text as written and where the words are in it. For
/// <see cref="SearchField.Notes"/> the text is the line that matched, shortened around the match when it is long.
/// </summary>
public sealed record SearchMatch(SearchField Field, string Text, IReadOnlyList<TextRange> Ranges);

/// <summary>
/// One entity found by a search. <see cref="Matches"/> lists every field that matched, the most important first.
/// <see cref="Day"/> is the local day the entity belongs to, if it has one: a plan's day, the first day of a
/// booking's plans, the day an expense was paid, the day a task is due. <see cref="Start"/> and <see cref="Zone"/>
/// are set for plans with a time.
/// </summary>
public sealed record SearchHit(Entity Entity, SearchKind Kind, double Score, LocalDate? Day, Instant? Start, string? Zone, IReadOnlyList<SearchMatch> Matches)
{
    /// <summary>Where the search words are in the entity's title; empty when the title did not match.</summary>
    public IReadOnlyList<TextRange> TitleRanges => Matches.FirstOrDefault(m => m.Field == SearchField.Title)?.Ranges ?? [];

    /// <summary>The most important match outside the title, to say where something was found, such as "Place: Sensō-ji".</summary>
    public SearchMatch? Elsewhere => Matches.FirstOrDefault(m => m.Field != SearchField.Title);
}

/// <summary>
/// Names of categories in the client's language, so "Essen" finds food. Without them the standard English keys are
/// searched. Categories the travellers named themselves are always searched as written.
/// </summary>
/// <param name="Plans">Names of the kinds of plans.</param>
/// <param name="Expenses">Names of the standard expense categories, by key (<see cref="ExpenseCategories.Standard"/>).</param>
public sealed record SearchLabels(IReadOnlyDictionary<ScheduleKind, string> Plans, IReadOnlyDictionary<string, string> Expenses);

/// <summary>How to search.</summary>
public sealed record SearchOptions
{
    /// <summary>Also search note texts. Titles of notes are always searched.</summary>
    public bool Notes { get; init; }
    /// <summary>Only these kinds; null or empty for all.</summary>
    public IReadOnlySet<SearchKind>? Kinds { get; init; }
    /// <summary>The most hits to return.</summary>
    public int Limit { get; init; } = 200;
}

/// <summary>The hits of one day, or the hits without a day when <see cref="Day"/> is null.</summary>
public sealed record SearchDay(LocalDate? Day, IReadOnlyList<SearchHit> Hits);

/// <summary>
/// Full-text search over a trip (plans, bookings, costs, places, tasks, packing, notes and files). It reads the
/// content as written and never changes it.
/// <para>
/// Matching ignores case and accents ("Wanaka" finds "Wānaka"), takes each word as the beginning of a word ("temp"
/// finds "Tempel") and allows one typo in words of five letters or more ("Templ" finds "Tempel"). Every word of the
/// query must be found somewhere in the entity. Hits are ordered by relevance: the title counts most, then places and
/// numbers, then other fields, then notes.
/// </para>
/// <para>
/// The index is built once per <see cref="TripSnapshot"/> and kept as long as the snapshot is, so searching while
/// typing is cheap. Unknown fields and extensions are not searched and never cause an error.
/// </para>
/// </summary>
public sealed class TripSearch
{
    private sealed record Token(string Text, int Start, int Length);
    private sealed record Field(SearchField Kind, string Text, double Weight, Token[] Tokens);
    private sealed record Document(Entity Entity, SearchKind Kind, LocalDate? Day, Instant? Start, string? Zone, Field[] Fields);

    private static readonly ConditionalWeakTable<TripSnapshot, TripSearch> Cache = new();
    private readonly Document[] documents;
    private readonly SearchLabels? labels;

    private TripSearch(TripSnapshot trip, SearchLabels? labels)
    {
        this.labels = labels;
        documents = Build(trip, labels);
    }

    /// <summary>The search for a trip, from the cache when this snapshot was searched before with the same labels.</summary>
    public static TripSearch For(TripSnapshot trip, SearchLabels? labels = null)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(trip, out var cached) && ReferenceEquals(cached.labels, labels)) return cached;
            var search = new TripSearch(trip, labels);
            Cache.AddOrUpdate(trip, search);
            return search;
        }
    }

    /// <summary>Searches a trip once; the same as <see cref="For"/> and then searching it.</summary>
    public static IReadOnlyList<SearchHit> Find(TripSnapshot trip, string? query, SearchOptions? options = null, SearchLabels? labels = null) => For(trip, labels).Find(query, options);

    /// <summary>The entities that contain every word of <paramref name="query"/>, best first. Empty for an empty query.</summary>
    public IReadOnlyList<SearchHit> Find(string? query, SearchOptions? options = null)
    {
        options ??= new();
        var words = Tokens(query ?? "", joined: false).Select(t => t.Text).Distinct(StringComparer.Ordinal).ToArray();
        if (words.Length == 0) return [];
        var hits = new List<SearchHit>();
        foreach (var document in documents)
        {
            if (options.Kinds is { Count: > 0 } kinds && !kinds.Contains(document.Kind)) continue;
            if (Match(document, words, options.Notes) is { } hit) hits.Add(hit);
        }
        return hits.OrderByDescending(h => h.Score).ThenBy(h => h.Day ?? LocalDate.MaxIsoValue).ThenBy(h => h.Start ?? Instant.MaxValue)
            // Equal otherwise, a plan comes before its booking and its costs, so the order never depends on IDs alone.
            .ThenBy(h => (int)h.Kind).ThenBy(h => h.Entity.Title, StringComparer.OrdinalIgnoreCase).ThenBy(h => h.Entity.Id).Take(Math.Max(1, options.Limit)).ToArray();
    }

    /// <summary>
    /// Hits grouped by day, each group in the order given (best first). During the trip, today comes first, then the
    /// days after it, then the days before it; otherwise the days are in order. Hits without a day come last, as one
    /// group with a null day.
    /// </summary>
    /// <param name="trip">The trip the hits are from; its dates say whether today is during the trip.</param>
    /// <param name="hits">The hits to group.</param>
    /// <param name="today">The current local day, to put it first during the trip; null for plain order.</param>
    public static IReadOnlyList<SearchDay> ByDay(TripSnapshot trip, IEnumerable<SearchHit> hits, LocalDate? today = null)
    {
        var list = hits.ToArray();
        var (start, end) = TripQueries.EffectiveDates(trip);
        var during = today is { } now && start is { } first && end is { } last && now >= first && now <= last;
        var days = list.Where(h => h.Day is not null).GroupBy(h => h.Day!.Value)
            .OrderBy(g => during && g.Key < today!.Value ? 1 : 0).ThenBy(g => g.Key)
            .Select(g => new SearchDay(g.Key, g.ToArray())).ToList();
        if (list.Where(h => h.Day is null).ToArray() is { Length: > 0 } rest) days.Add(new(null, rest));
        return days;
    }

    /// <summary>
    /// A word as the search compares it: lower case, without accents and other marks, with ß as ss and letters such
    /// as ø and ł as their base letters.
    /// </summary>
    public static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormKD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark) continue;
            switch (char.ToLowerInvariant(c))
            {
                case 'ß': builder.Append("ss"); break;
                case 'ø': builder.Append('o'); break;
                case 'ł': builder.Append('l'); break;
                case 'đ' or 'ð': builder.Append('d'); break;
                case 'æ': builder.Append("ae"); break;
                case 'œ': builder.Append("oe"); break;
                case 'þ': builder.Append("th"); break;
                case 'ı': builder.Append('i'); break;
                case var lower: builder.Append(lower); break;
            }
        }
        return builder.ToString();
    }

    // Matching

    private const double Exact = 1, Prefix = 0.85, Inside = 0.6, Typo = 0.5;

    private static SearchHit? Match(Document document, string[] words, bool notes)
    {
        var found = new Dictionary<Field, List<TextRange>>();
        double score = 0;
        foreach (var word in words)
        {
            double best = 0; var any = false;
            foreach (var field in document.Fields)
            {
                if (field.Kind == SearchField.Notes && !notes) continue;
                foreach (var token in field.Tokens)
                {
                    var quality = Quality(word, token.Text);
                    if (quality == 0) continue;
                    any = true; best = Math.Max(best, quality * field.Weight);
                    if (!found.TryGetValue(field, out var ranges)) found[field] = ranges = [];
                    // The part of the word that was typed, for a beginning; the whole word for a typo.
                    var length = quality >= Prefix && token.Text.Length > 0 ? Math.Max(1, Math.Min(token.Length, (int)Math.Round((double)token.Length * word.Length / token.Text.Length))) : token.Length;
                    var range = new TextRange(token.Start, length);
                    if (!ranges.Contains(range)) ranges.Add(range);
                }
            }
            if (!any) return null;
            score += best;
        }
        var matches = found.OrderByDescending(p => p.Key.Weight).ThenBy(p => Array.IndexOf(document.Fields, p.Key))
            .Select(p => Snippet(p.Key, Merged(p.Value)))
            .ToList();
        // One line of notes says where; more lines would only crowd the result.
        var firstNote = matches.FindIndex(m => m.Field == SearchField.Notes);
        if (firstNote >= 0) matches.RemoveAll(m => m.Field == SearchField.Notes && !ReferenceEquals(m, matches[firstNote]));
        return new(document.Entity, document.Kind, score / words.Length, document.Day, document.Start, document.Zone, matches);
    }

    private static double Quality(string word, string token)
    {
        if (token.Length == 0) return 0;
        if (token == word) return Exact;
        if (token.StartsWith(word, StringComparison.Ordinal)) return Prefix;
        // Scripts written without spaces, such as Japanese, have no word beginnings to go by.
        if (word.Any(c => c >= '⺀') && token.Contains(word, StringComparison.Ordinal)) return Inside;
        if (word.Length < 5 || !word.All(char.IsLetter) || word[0] != token[0]) return 0;
        // One typo: against the word, and against its beginning while the word is still being typed.
        for (var length = word.Length - 1; length <= word.Length + 1; length++)
            if (length <= token.Length && Distance(word, token.AsSpan(0, length)) <= 1) return Typo;
        return 0;
    }

    /// <summary>The number of typos between two words: a letter added, left out or replaced, or two neighbours swapped.</summary>
    private static int Distance(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        if (Math.Abs(a.Length - b.Length) > 1) return 2;
        var before = new int[b.Length + 1]; var row = new int[b.Length + 1]; var next = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) row[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            next[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                next[j] = Math.Min(Math.Min(row[j] + 1, next[j - 1] + 1), row[j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) next[j] = Math.Min(next[j], before[j - 2] + 1);
            }
            (before, row, next) = (row, next, before);
        }
        return row[b.Length];
    }

    /// <summary>Ranges in order, with those that overlap as one.</summary>
    private static TextRange[] Merged(List<TextRange> ranges)
    {
        var result = new List<TextRange>();
        foreach (var range in ranges.OrderBy(r => r.Start).ThenByDescending(r => r.Length))
        {
            if (result.Count > 0 && result[^1] is var last && range.Start < last.Start + last.Length)
                result[^1] = new(last.Start, Math.Max(last.Start + last.Length, range.Start + range.Length) - last.Start);
            else result.Add(range);
        }
        return result.ToArray();
    }

    private const int SnippetLength = 96;

    /// <summary>The match as shown: the field's text, or for a long line of notes the part around the first word found.</summary>
    private static SearchMatch Snippet(Field field, TextRange[] ranges)
    {
        if (field.Kind != SearchField.Notes || field.Text.Length <= SnippetLength) return new(field.Kind, field.Text, ranges);
        var from = Math.Max(0, ranges[0].Start - SnippetLength / 3);
        // Start at a word, not inside one.
        while (from > 0 && from < ranges[0].Start && !char.IsWhiteSpace(field.Text[from - 1])) from++;
        if (char.IsLowSurrogate(field.Text[from]) && from > 0) from--;
        var to = Math.Min(field.Text.Length, from + SnippetLength);
        if (to < field.Text.Length && char.IsLowSurrogate(field.Text[to])) to--;
        var text = (from > 0 ? "…" : "") + field.Text[from..to].TrimEnd() + (to < field.Text.Length ? "…" : "");
        var shift = (from > 0 ? 1 : 0) - from;
        var limit = text.Length - (to < field.Text.Length ? 1 : 0);
        return new(field.Kind, text, ranges.Where(r => r.Start >= from && r.Start + r.Length + shift <= limit).Select(r => new TextRange(r.Start + shift, r.Length)).ToArray());
    }

    /// <summary>The words of a text as the search compares them: split at spaces and punctuation, each through <see cref="Normalize"/>.</summary>
    public static IReadOnlyList<string> Words(string? text) => Tokens(text ?? "", joined: false).Select(t => t.Text).ToArray();

    /// <summary>The words of a text, normalized, with where each is in the text as written.</summary>
    private static List<Token> Tokens(string text, bool joined = true)
    {
        var tokens = new List<Token>();
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var inside = i < text.Length && (char.IsLetterOrDigit(text[i]) || char.IsSurrogate(text[i]) && char.IsLetterOrDigit(text, char.IsLowSurrogate(text[i]) && i > 0 ? i - 1 : i)
                || start >= 0 && CharUnicodeInfo.GetUnicodeCategory(text[i]) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark);
            if (inside && start < 0) start = i;
            else if (!inside && start >= 0)
            {
                var word = Normalize(text[start..i]);
                if (word.Length > 0) tokens.Add(new(word, start, i - start));
                start = -1;
            }
        }
        // Words joined by a hyphen or an apostrophe are also found written as one: "sensoji" finds "Sensō-ji".
        for (int i = 1, count = joined ? tokens.Count : 0; i < count; i++)
        {
            var (left, right) = (tokens[i - 1], tokens[i]);
            if (right.Start == left.Start + left.Length + 1 && text[right.Start - 1] is '-' or '\'' or '’' or '‐' or '‑')
                tokens.Add(new(left.Text + right.Text, left.Start, right.Start + right.Length - left.Start));
        }
        return tokens;
    }

    // Index

    private static Document[] Build(TripSnapshot trip, SearchLabels? labels)
    {
        var tripZone = ScheduleQueries.TripZone(trip);
        var bookings = trip.Entities.Values.Where(e => e.Type == "booking").ToArray();
        var booked = bookings.ToDictionary(b => b.Id, b => TripCommands.BookedItems(trip, b.Id).Select(trip.Find).OfType<Entity>().ToArray());
        var bookingsOf = booked.SelectMany(p => p.Value.Select(item => (Item: item.Id, Booking: trip.Find(p.Key)!))).ToLookup(x => x.Item, x => x.Booking);
        var lists = PackingLists.All(trip);
        var listOf = lists.SelectMany(l => l.Items.Select(i => (Item: i.Task.Id, List: l.Collection))).ToLookup(x => x.Item, x => x.List);
        var documents = new List<Document>();

        (LocalDate? Day, Instant? Start, string? Zone) When(Entity item)
        {
            try
            {
                var span = ScheduleQueries.Span(trip, item);
                var zone = span.StartZone is { } id ? DateTimeZoneProviders.Tzdb.GetZoneOrNull(id) ?? tripZone : tripZone;
                var timed = span.Precision is TimePrecision.Exact or TimePrecision.Approximate or TimePrecision.Window;
                return (span.Date ?? span.Start?.InZone(zone).Date, timed ? span.Start : null, timed && span.Start is not null ? zone.Id : null);
            }
            catch (Exception ex) when (ex is DomainException or InvalidOperationException or FormatException or ArgumentException) { return (null, null, null); }
        }
        string Names(IEnumerable<Entity?> people) => string.Join(", ", people.OfType<Entity>().Select(p => p.Title).Distinct());
        IEnumerable<Entity?> Ids(JsonNode? node) => (node as JsonArray ?? []).Select(n => Guid.TryParse(n?.ToString(), out var id) ? trip.Find(id) : null);

        foreach (var entity in trip.Entities.Values)
        {
            var fields = new List<Field>();
            void Add(SearchField kind, string? text, double weight) { if (!string.IsNullOrWhiteSpace(text) && Tokens(text) is { Count: > 0 } tokens) fields.Add(new(kind, text, weight, tokens.ToArray())); }
            void Place(Entity? place, SearchField name, double weight)
            {
                if (place is null) return;
                Add(name, place.Title, weight); Add(SearchField.LocalName, Places.LocalName(place), weight);
                Add(SearchField.Address, Places.Address(place), weight * 0.8); Add(SearchField.Address, Places.LocalAddress(place), weight * 0.8);
            }
            void NoteText(string? markdown)
            {
                if (string.IsNullOrWhiteSpace(markdown)) return;
                foreach (var line in markdown.Replace("\r\n", "\n").Split('\n'))
                    if (PlainLine(line) is { Length: > 0 } plain) Add(SearchField.Notes, plain, 0.2);
            }
            void Notes(Entity e)
            {
                try
                {
                    if (e.Data["body"] is JsonValue body) NoteText(body.ToString());
                    if (e.Data["notes"] is JsonValue notes) NoteText(notes.ToString());
                    foreach (var entry in (e.Data["content"] as JsonArray ?? []).OfType<JsonObject>())
                    {
                        var type = entry["type"]?.ToString();
                        if (type == "markdown" && entry["file"]?.ToString() is { } file && trip.Resources.TryGetValue(file, out var bytes)) NoteText(Encoding.UTF8.GetString(bytes));
                        else if (type is "text" or "markdown" && entry["text"] is JsonValue text) NoteText(text.ToString());
                    }
                }
                catch (InvalidOperationException) { }
            }

            SearchKind kind; LocalDate? day = null; Instant? start = null; string? zone = null;
            try
            {
                switch (entity.Type)
                {
                    case "schedule_item":
                        {
                            kind = SearchKind.Plan; (day, start, zone) = When(entity);
                            Add(SearchField.Title, entity.Title, 1);
                            var transport = entity.Data["components"]?["transport"] as JsonObject;
                            var (from, to) = ScheduleQueries.Route(trip, entity);
                            Place(from, SearchField.From, 0.7); Place(to, SearchField.To, 0.7);
                            if (ScheduleQueries.PrimaryPlace(trip, entity) is { } place && place.Id != to?.Id && place.Id != from?.Id) Place(place, SearchField.Place, 0.7);
                            Add(SearchField.Provider, Scalar(transport?["carrier"]), 0.6);
                            foreach (var (key, value) in transport ?? [])
                                if (key == "number" || key.EndsWith("_number", StringComparison.Ordinal)) Add(SearchField.Number, Scalar(value), 0.7);
                            foreach (var booking in bookingsOf[entity.Id])
                            {
                                Add(SearchField.Reference, Scalar(booking.Data["reference"]), 0.7);
                                Add(SearchField.Provider, Scalar(booking.Data["provider"]?["name"]), 0.5);
                            }
                            Add(SearchField.People, Names(ScheduleQueries.Participants(trip, entity)), 0.4);
                            var plan = ScheduleCategories.Classify(trip, entity);
                            Add(SearchField.Category, labels?.Plans.GetValueOrDefault(plan) ?? PlanKey(plan), 0.5);
                            Add(SearchField.Notes, Scalar(entity.Data["components"]?["accommodation"]?["arrival_notes"]), 0.2);
                            break;
                        }
                    case "booking":
                        {
                            kind = SearchKind.Booking;
                            var items = booked.GetValueOrDefault(entity.Id) ?? [];
                            var first = items.Select(When).Where(w => w.Day is not null).OrderBy(w => w.Day).ThenBy(w => w.Start ?? Instant.MaxValue).FirstOrDefault();
                            (day, start, zone) = first;
                            Add(SearchField.Title, entity.Title, 1);
                            Add(SearchField.Provider, Scalar(entity.Data["provider"]?["name"]), 0.6);
                            Add(SearchField.Reference, Scalar(entity.Data["reference"]), 0.8);
                            Add(SearchField.Plans, string.Join(", ", items.Select(i => i.Title).Distinct()), 0.5);
                            break;
                        }
                    case "expense":
                        {
                            kind = SearchKind.Cost; day = Expenses.DateOf(trip, entity);
                            Add(SearchField.Title, entity.Title, 1);
                            var category = ExpenseCategories.Of(trip, entity);
                            Add(SearchField.Category, labels?.Expenses.GetValueOrDefault(category) ?? category, 0.5);
                            var split = Expenses.Split(trip, entity);
                            Add(SearchField.People, Names(Ids(entity.Data["paid_by"]).Concat(split.Shares.Select(s => trip.Find(s.Person)))), 0.4);
                            if (TripSummaries.Money(entity.Data["amount"]) is { } money) Add(SearchField.Amount, money.Value + " " + money.Currency, 0.5);
                            var related = (entity.Data["related"] as JsonArray ?? []).Select(r => Guid.TryParse(r?["id"]?.ToString(), out var id) ? trip.Find(id) : null).OfType<Entity>().ToArray();
                            Add(SearchField.BelongsTo, string.Join(", ", related.Select(r => r.Title).Distinct()), 0.5);
                            break;
                        }
                    case "place":
                        kind = SearchKind.Place;
                        Add(SearchField.Title, entity.Title, 1); Add(SearchField.LocalName, Places.LocalName(entity), 0.9);
                        Add(SearchField.Address, Places.Address(entity), 0.6); Add(SearchField.Address, Places.LocalAddress(entity), 0.6);
                        break;
                    case "task":
                        {
                            var packing = PackingLists.IsItem(entity);
                            kind = packing ? SearchKind.Packing : SearchKind.Task;
                            Add(SearchField.Title, entity.Title, 1);
                            if (packing)
                            {
                                var item = PackingLists.Item(trip, entity);
                                Add(SearchField.People, Names(item.Assignees), 0.4);
                                Add(SearchField.Group, item.Group, 0.5);
                                Add(SearchField.List, string.Join(", ", listOf[entity.Id].Select(l => l.Title).Distinct()), 0.5);
                            }
                            else
                            {
                                Add(SearchField.People, Names(Ids(entity.Data["assigned_to"])), 0.4);
                                if (entity.Data["due"]?["date"] is JsonValue due && LocalDatePattern.Iso.Parse(due.ToString()).TryGetValue(default, out var date)) day = date;
                            }
                            break;
                        }
                    case "collection" when PackingLists.IsList(entity):
                        kind = SearchKind.Packing; Add(SearchField.Title, entity.Title, 1);
                        break;
                    case "note":
                        kind = SearchKind.Note; Add(SearchField.Title, entity.Title, 1);
                        break;
                    case "document":
                        kind = SearchKind.File; Add(SearchField.Title, entity.Title, 1); Add(SearchField.Caption, Scalar(entity.Data["caption"]), 0.6);
                        break;
                    default: continue;
                }
                Notes(entity);
            }
            // Data another client wrote in a shape this one does not know is searched as far as it can be read.
            catch (Exception ex) when (ex is DomainException or InvalidOperationException or FormatException or ArgumentException or KeyNotFoundException)
            {
                if (fields.Count == 0) continue;
                kind = entity.Type switch { "schedule_item" => SearchKind.Plan, "booking" => SearchKind.Booking, "expense" => SearchKind.Cost, "place" => SearchKind.Place, "task" => SearchKind.Task, "note" => SearchKind.Note, "document" => SearchKind.File, _ => SearchKind.Packing };
            }
            if (fields.Count > 0) documents.Add(new(entity, kind, day, start, zone, fields.ToArray()));
        }
        return documents.ToArray();
    }

    private static string? Scalar(JsonNode? node) => node is JsonValue value ? value.ToString() : null;

    private static string PlanKey(ScheduleKind kind) => kind == ScheduleKind.FreeTime ? "free time" : kind.ToString().ToLowerInvariant();

    /// <summary>A line of Markdown as plain text: without heading, list and quote marks and without emphasis and code marks.</summary>
    private static string PlainLine(string line)
    {
        var text = line.Trim();
        if (text is "---" or "***" or "```") return "";
        text = text.TrimStart('#', '>', ' ');
        if (text.StartsWith("- ", StringComparison.Ordinal) || text.StartsWith("* ", StringComparison.Ordinal) || text.StartsWith("+ ", StringComparison.Ordinal)) text = text[2..];
        if (text.StartsWith("[ ] ", StringComparison.Ordinal) || text.StartsWith("[x] ", StringComparison.OrdinalIgnoreCase)) text = text[4..];
        return text.Replace("**", "").Replace("__", "").Replace("`", "").Trim();
    }
}
