using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NodaTime;
using NodaTime.Text;
using TravelRepo.Core;

namespace TravelRepo.Repository;

public enum ExportRowKind
{
    Item,
    /// <summary>A subheading inside a section, such as a packing list group.</summary>
    Heading,
    /// <summary>An item with a check box, such as a packing item or a task.</summary>
    Check
}

/// <summary>One line of an export section. Renderers draw every section the same way, so HTML, PDF and text agree.</summary>
/// <param name="Title">The main text.</param>
/// <param name="Lead">Short text in the left column: a time, a booking reference, a quantity.</param>
/// <param name="Summary">One line under the title.</param>
/// <param name="Details">Further lines, only in detailed exports.</param>
/// <param name="Status">A short tag such as Confirmed.</param>
/// <param name="Kind">An item, a subheading or an item with a check box.</param>
/// <param name="Checked">For check rows: whether the box is ticked.</param>
/// <param name="Attachments">Labels of appendix pages that belong to this row, such as A1.</param>
public sealed record ExportRow(string Title, string Lead = "", string? Summary = null, IReadOnlyList<string>? Details = null, string? Status = null, ExportRowKind Kind = ExportRowKind.Item, bool Checked = false, IReadOnlyList<string>? Attachments = null)
{
    public IReadOnlyList<string> DetailLines => Details ?? [];
    public IReadOnlyList<string> AttachmentLabels => Attachments ?? [];
}

public sealed record ExportSection(ExportParts Part, string Title, IReadOnlyList<ExportRow> Rows, string? Overline = null);

public sealed record ExportMapPlace(string Title, double Latitude, double Longitude);
public sealed record ExportMapRoute(double FromLatitude, double FromLongitude, double ToLatitude, double ToLongitude, bool Confirmed);

/// <summary>The places and routes of an export, in trip order. Clients draw them on a basemap or a plain grid.</summary>
public sealed record ExportMap(IReadOnlyList<ExportMapPlace> Places, IReadOnlyList<ExportMapRoute> Routes)
{
    public bool IsEmpty => Places.Count == 0 && Routes.Count == 0;
}

/// <summary>A document to append to the export, labelled A1, A2 and so on in order of first use.</summary>
public sealed record ExportAttachment(string Label, Entity Document, IReadOnlyList<string> UsedBy)
{
    public string Name => Document.Title;
    public string MediaType => Document.Data["media_type"]?.ToString() ?? "application/octet-stream";
}

/// <summary>
/// A renderer-neutral export: title, a few meta lines and sections of rows, plus map data and attachments for
/// renderers that can draw them.
/// </summary>
public sealed record ExportDocument(string Title, IReadOnlyList<string> Meta, IReadOnlyList<ExportSection> Sections, ExportMap? Map, IReadOnlyList<ExportAttachment> Attachments, string Footer, string Language, ExportOptions Options, ExportLabels Labels);

public static partial class TripExport
{
    /// <summary>The trip as an export document, following <paramref name="options"/>.</summary>
    public static ExportDocument Document(TripSnapshot trip, ExportOptions? options = null, ExportLabels? labels = null, CultureInfo? culture = null)
    {
        options ??= new(); labels ??= new(); culture ??= CultureInfo.CurrentCulture;
        var filter = new ExportFilter(trip, options);
        var it = TripItinerary.Build(trip, labels, culture, options);
        var compact = options.Compact;
        var sections = new List<ExportSection>();

        var attachments = new List<(Entity Document, List<string> UsedBy)>();
        IReadOnlyList<string> Attach(Entity owner, string usedBy)
        {
            if (!options.Includes(ExportParts.Attachments)) return [];
            var result = new List<string>();
            foreach (var document in TripCommands.AttachedDocuments(trip, owner))
            {
                var index = attachments.FindIndex(a => a.Document.Id == document.Id);
                if (index < 0) { attachments.Add((document, [])); index = attachments.Count - 1; }
                if (!attachments[index].UsedBy.Contains(usedBy)) attachments[index].UsedBy.Add(usedBy);
                result.Add("A" + (index + 1).ToString(CultureInfo.InvariantCulture));
            }
            return result;
        }
        string Date(LocalDate d) => d.ToString("d MMM", culture);
        string Range(LocalDate a, LocalDate b) => a == b ? Date(a) : Date(a) + " – " + Date(b);
        string Clock(ZonedTime t, bool approximate) => LocalDateTimePattern.ExtendedIso.Parse(t.Local) is { Success: true } r ? r.Value.Date.ToString("ddd d MMM", culture) + (approximate ? "" : ", " + r.Value.TimeOfDay.ToString("HH:mm", CultureInfo.InvariantCulture)) : t.Local;

        var meta = new List<string>();
        var dates = it.Start is { } start && it.End is { } end ? start.ToString("d MMMM yyyy", culture) + " – " + end.ToString("d MMMM yyyy", culture) : "";
        meta.Add(string.Join(" · ", new[] { dates, it.Zone }.Where(x => x.Length > 0)));
        if (options.Person is { } personId && trip.Find(personId) is { } person) meta.Add(string.Format(culture, labels.PlanFor, person.Title));
        else if (it.Travellers.Count > 0) meta.Add(labels.Travellers + ": " + string.Join(", ", it.Travellers));
        // An open end without known trip dates reads "from 4 Jun" or "until 6 Jun", never like a single day.
        if (options.HasRange)
            meta.Add((options.From ?? it.Start, options.To ?? it.End) switch
            {
                ({ } first, { } last) => Range(first, last),
                ({ } first, null) => string.Format(culture, labels.FromTime, Date(first)),
                (null, { } last) => string.Format(culture, labels.UntilTime, Date(last)),
                _ => ""
            });

        ExportRow Entry(ItineraryEntry e)
        {
            var sub = compact ? new[] { e.Where, e.BookingReference is null ? null : labels.Reference + " " + e.BookingReference }
                : new[] { e.Group, e.Where, e.People.Count > 0 ? string.Join(", ", e.People) : null, e.BookingReference is null ? null : labels.Reference + " " + e.BookingReference };
            return new(e.Item.Title, e.Time, Join(sub), compact ? null : e.Notes, compact ? null : e.Status, Attachments: Attach(e.Item, e.Item.Title));
        }
        if (options.Includes(ExportParts.Schedule))
        {
            foreach (var day in it.Days) sections.Add(new(ExportParts.Schedule, day.Date.ToString("dddd, d MMMM", culture), day.Entries.Select(Entry).ToArray(), day.Number > 0 ? string.Format(culture, labels.Day, day.Number) : null));
            if (it.Unscheduled.Count > 0) sections.Add(new(ExportParts.Schedule, labels.Unscheduled, it.Unscheduled.Select(Entry).ToArray()));
        }

        var stays = Stays.All(trip).Where(s => s.Item.Data["status"]?.ToString() != "cancelled" && filter.Includes(s)).ToArray();
        if (options.Includes(ExportParts.Stays) && stays.Length > 0)
        {
            string? Window(StayWindow? w) => w is null ? null : w.From is { } f && w.Until is { } u ? string.Format(culture, labels.Between, f.ToString("HH:mm", CultureInfo.InvariantCulture), u.ToString("HH:mm", CultureInfo.InvariantCulture))
                : w.From is { } from ? string.Format(culture, labels.FromTime, from.ToString("HH:mm", CultureInfo.InvariantCulture)) : w.Until is { } until ? string.Format(culture, labels.UntilTime, until.ToString("HH:mm", CultureInfo.InvariantCulture)) : null;
            var rows = new List<ExportRow>();
            foreach (var stay in stays)
            {
                var place = stay.Place is { } p ? trip.Find(p) : null;
                var accommodation = stay.Item.Data["components"]?["accommodation"];
                var booking = Guid.TryParse((accommodation?["booking"] ?? stay.Item.Data["components"]?["booking"]?["ref"])?.ToString(), out var b) ? trip.Find(b) : null;
                var checkIn = stay.CheckIn is { } ci ? string.Format(culture, labels.CheckIn, Clock(ci, stay.CheckInApproximate)) : null;
                var checkOut = stay.CheckOut is { } co ? string.Format(culture, labels.CheckOut, Clock(co, stay.CheckOutApproximate)) : null;
                var details = new List<string?>
                {
                    Join([checkIn, checkOut]),
                    Window(stay.CheckInWindow) is { } inHours ? string.Format(culture, labels.CheckInHours, inHours) : null,
                    Window(stay.CheckOutWindow) is { } outHours ? string.Format(culture, labels.CheckOutHours, outHours) : null,
                    stay.Access is { } access ? labels.Access.GetValueOrDefault(access) ?? access : null,
                    accommodation?["breakfast"]?.ToString() is { Length: > 0 } breakfast ? string.Format(culture, labels.Breakfast, labels.BreakfastValues.GetValueOrDefault(breakfast) ?? breakfast) : null,
                    Parking(accommodation?["parking"], labels),
                    accommodation?["arrival_notes"]?.ToString() is { Length: > 0 } notes ? notes : null,
                    Places.Address(place),
                    Contact(Places.Contact(place)),
                    stay.FreeCancellationUntil is { } free ? string.Format(culture, labels.FreeCancellation, Clock(free, false)) : null,
                };
                var lead = stay.FirstNight is { } first ? Range(first, stay.DepartureDay ?? first) : "";
                var summary = compact ? Join([stay.Nights > 0 ? labels.NightCount(stay.Nights) : null, checkIn, booking?.Data["reference"]?.ToString() is { } r1 ? labels.Reference + " " + r1 : null])
                    : Join([place?.Title != stay.Item.Title ? place?.Title : null, stay.Nights > 0 ? labels.NightCount(stay.Nights) : null, booking?.Data["reference"]?.ToString() is { } r2 ? labels.Reference + " " + r2 : null]);
                var docs = Attach(stay.Item, stay.Item.Title).Concat(booking is null ? [] : Attach(booking, stay.Item.Title)).Distinct().ToArray();
                rows.Add(new(stay.Item.Title, lead, summary, compact ? null : details.OfType<string>().Where(x => x.Length > 0).ToArray(), compact ? null : labels.Status(stay.Item.Data["status"]?.ToString()), Attachments: docs));
            }
            sections.Add(new(ExportParts.Stays, labels.Stays, rows));
        }

        if (options.Includes(ExportParts.Bookings) && it.Bookings.Count > 0)
            sections.Add(new(ExportParts.Bookings, labels.Bookings, it.Bookings.Select(booking =>
            {
                var price = TripSummaries.Money(booking.Data["price"]);
                var free = Stays.Zoned(booking.Data["cancellation"]?["free_until"]);
                var details = compact ? null : new[] { free is null ? null : string.Format(culture, labels.FreeCancellation, Clock(free, false)), Contact(ContactDetails.Read(booking.Data["provider"]?["contact"] ?? booking.Data["provider"])) }.OfType<string>().ToArray();
                return new ExportRow(booking.Title, booking.Data["reference"]?.ToString() ?? "",
                    Join([booking.Data["provider"]?["name"]?.ToString(), price is null ? null : price.Amount.ToString("N2", culture) + " " + price.Currency, compact ? null : labels.Status(booking.Data["status"]?.ToString())]), details, Attachments: Attach(booking, booking.Title));
            }).ToArray()));

        if (options.Includes(ExportParts.Contacts))
        {
            var used = new List<(string Name, ContactDetails? Contact, string? Address, string For)>();
            void Add(string name, ContactDetails? contact, string? address, string usedFor)
            {
                if (contact is null) return;
                var index = used.FindIndex(u => u.Name == name && u.Contact == contact);
                if (index < 0) used.Add((name, contact, address, usedFor));
                else if (!used[index].For.Split(", ").Contains(usedFor)) used[index] = used[index] with { For = used[index].For + ", " + usedFor };
            }
            foreach (var stay in stays) if (stay.Place is { } p && trip.Find(p) is { } place) Add(place.Title, Places.Contact(place), Places.Address(place), stay.Item.Title);
            foreach (var entry in it.Days.SelectMany(d => d.Entries).Concat(it.Unscheduled).Where(e => !Stays.IsStay(e.Item)))
                if (ScheduleQueries.PrimaryPlace(trip, entry.Item) is { } place) Add(place.Title, Places.Contact(place), Places.Address(place), entry.Item.Title);
            foreach (var booking in it.Bookings)
                if (booking.Data["provider"]?["name"]?.ToString() is { Length: > 0 } provider) Add(provider, ContactDetails.Read(booking.Data["provider"]?["contact"] ?? booking.Data["provider"]), null, booking.Title);
            if (used.Count > 0)
                sections.Add(new(ExportParts.Contacts, labels.Contacts, used.Select(u => new ExportRow(u.Name, "", Contact(u.Contact), compact ? null : new[] { u.Address, u.For }.OfType<string>().ToArray())).ToArray()));
        }

        if (options.Includes(ExportParts.Tasks))
        {
            var tasks = trip.Entities.Values.Where(e => e.Type == "task" && filter.IncludesTask(e)).OrderBy(e => e.Data["status"]?.ToString() == "completed" ? 1 : 0)
                .ThenBy(e => e.Data["due"]?["date"]?.ToString() ?? "9999").ThenBy(e => e.Title, StringComparer.Create(culture, false)).ToArray();
            if (tasks.Length > 0)
                sections.Add(new(ExportParts.Tasks, labels.Tasks, tasks.Select(t =>
                {
                    var due = LocalDatePattern.Iso.Parse(t.Data["due"]?["date"]?.ToString() ?? "") is { Success: true } d ? string.Format(culture, labels.Due, Date(d.Value)) : "";
                    var people = (t.Data["assigned_to"] as JsonArray ?? []).Select(n => Guid.TryParse(n?.ToString(), out var id) ? trip.Find(id)?.Title : null).OfType<string>().ToArray();
                    return new ExportRow(t.Title, due, people.Length > 0 ? string.Join(", ", people) : null, Kind: ExportRowKind.Check, Checked: t.Data["status"]?.ToString() == "completed", Attachments: Attach(t, t.Title));
                }).ToArray()));
        }

        if (options.Includes(ExportParts.Costs) && it.Costs.Count > 0)
        {
            var rows = it.Costs.Select(c => new ExportRow(c.Paid.ToString("N2", culture) + " " + labels.Paid + (c.Estimated > 0 ? " · " + c.Estimated.ToString("N2", culture) + " " + labels.Estimated : "") + (c.Budget > 0 ? " · " + c.Budget.ToString("N2", culture) + " " + labels.Budget : ""), c.Currency)).ToList();
            if (!compact)
                foreach (var expense in trip.Entities.Values.Where(e => e.Type == "expense" && filter.IncludesExpense(e)).OrderBy(e => e.Title, StringComparer.Create(culture, false)))
                {
                    if (TripSummaries.Money(expense.Data["amount"]) is not { } money) continue;
                    var payers = (expense.Data["paid_by"] as JsonArray ?? []).Select(n => Guid.TryParse(n?.ToString(), out var id) ? trip.Find(id)?.Title : null).OfType<string>().ToArray();
                    var estimated = expense.Data["estimated"] is JsonValue v && v.TryGetValue<bool>(out var flag) && flag;
                    rows.Add(new(expense.Title, money.Amount.ToString("N2", culture) + " " + money.Currency, Join([payers.Length > 0 ? string.Format(culture, labels.PaidBy, string.Join(", ", payers)) : null, estimated ? labels.Estimated : null]), Attachments: Attach(expense, expense.Title)));
                }
            sections.Add(new(ExportParts.Costs, labels.Costs, rows));
        }

        if (options.Includes(ExportParts.Notes))
        {
            var notes = trip.Entities.Values.Where(e => e.Type == "note").OrderBy(e => e.Title, StringComparer.Create(culture, false))
                .Select(n => new ExportRow(n.Title, "", null, ExportText.Notes(trip, n))).Where(r => r.DetailLines.Count > 0 || r.Title.Length > 0).ToArray();
            if (notes.Length > 0) sections.Add(new(ExportParts.Notes, labels.Notes, notes));
        }

        if (options.Includes(ExportParts.Packing))
            sections.AddRange(PackingExport.Sections(trip, new PackingExportOptions { Person = options.Person }, labels));

        ExportMap? map = null;
        if (options.Includes(ExportParts.Map))
        {
            var places = new List<ExportMapPlace>(); var routes = new List<ExportMapRoute>();
            void Place(Entity? place) { if (Places.Location(place) is { } at && !places.Any(p => p.Title == place!.Title && p.Latitude == at.Latitude && p.Longitude == at.Longitude)) places.Add(new(place!.Title, at.Latitude, at.Longitude)); }
            foreach (var entry in it.Days.SelectMany(d => d.Entries))
            {
                var route = ScheduleQueries.Route(trip, entry.Item);
                if (Places.Location(route.From) is { } a && Places.Location(route.To) is { } b) { routes.Add(new(a.Latitude, a.Longitude, b.Latitude, b.Longitude, entry.Item.Data["status"]?.ToString() is "confirmed" or "completed")); Place(route.From); Place(route.To); }
                else Place(ScheduleQueries.PrimaryPlace(trip, entry.Item));
            }
            foreach (var stay in stays) Place(stay.Place is { } p ? trip.Find(p) : null);
            map = new(places, routes);
            if (map.IsEmpty) map = null;
        }

        var exported = attachments.Select((a, i) => new ExportAttachment("A" + (i + 1).ToString(CultureInfo.InvariantCulture), a.Document, a.UsedBy)).ToArray();
        if (exported.Length > 0)
            sections.Add(new(ExportParts.Attachments, labels.Attachments, exported.Select(a => new ExportRow(a.Name, a.Label, string.Join(", ", a.UsedBy))).ToArray()));
        return new(it.Title, meta.Where(m => m.Length > 0).ToArray(), sections, map, exported, labels.GeneratedBy, trip.Manifest.Data["language"]?.ToString() ?? "en", options, labels);
    }

    private static string? Join(IEnumerable<string?> parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p))) is { Length: > 0 } s ? s : null;

    private static string? Contact(ContactDetails? contact) => contact is null ? null : Join([contact.Phone, contact.Email, contact.Website]);

    private static string? Parking(JsonNode? parking, ExportLabels labels)
    {
        if (parking is not JsonObject) return null;
        var included = parking["included"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        var note = parking["note"]?.ToString();
        return Join([included ? labels.ParkingIncluded : note is { Length: > 0 } ? labels.Parking : null, note]);
    }
}

/// <summary>Plain text from trip content, for exports.</summary>
public static partial class ExportText
{
    /// <summary>The Markdown notes of an entity, from its <c>body</c> and its Markdown content entries, as plain text lines without blank lines.</summary>
    public static IReadOnlyList<string> Notes(TripSnapshot trip, Entity entity)
    {
        var lines = new List<string>();
        if (entity.Data["body"] is JsonValue body && body.ToString() is { Length: > 0 } text) lines.AddRange(Plain(text));
        foreach (var entry in (entity.Data["content"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var markdown = entry["type"]?.ToString() == "markdown" && entry["file"]?.ToString() is { } file && trip.Resources.TryGetValue(file, out var bytes) ? Encoding.UTF8.GetString(bytes)
                : entry["type"]?.ToString() is "text" or "markdown" ? entry["text"]?.ToString() : null;
            if (markdown is not null) lines.AddRange(Plain(markdown));
        }
        return lines;
    }

    /// <summary>
    /// Markdown reduced to readable lines: headings, emphasis, code marks and link targets are removed, list markers
    /// become bullets. Exports print text, so nothing is interpreted as HTML.
    /// </summary>
    public static IReadOnlyList<string> Plain(string markdown)
    {
        var result = new List<string>();
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Trim().Length == 0 || line.Trim() is "---" or "***" or "```") continue;
            line = Heading().Replace(line, "");
            line = Bullet().Replace(line, m => m.Groups[1].Value + "• ");
            line = Link().Replace(line, "$1");
            line = Emphasis().Replace(line, "$2");
            result.Add(line.Replace("`", ""));
        }
        return result;
    }

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s+")] private static partial Regex Heading();
    [GeneratedRegex(@"^(\s*)(?:[-*+]|\d+[.)])\s+(?:\[[ xX]\]\s+)?")] private static partial Regex Bullet();
    [GeneratedRegex(@"!?\[([^\]]*)\]\([^)]*\)")] private static partial Regex Link();
    [GeneratedRegex(@"(?<!\w)(\*\*|__|\*|_)(\S(?:.*?\S)?)\1(?!\w)")] private static partial Regex Emphasis();
}
