using System.Net;
using System.Text;
using NodaTime;
using NodaTime.Text;
using TravelRepo.Core;
namespace TravelRepo.Repository;

/// <summary>Portable exports from canonical domain data.</summary>
public static class TripExport
{
    /// <summary>A print-friendly, self-contained HTML itinerary. Text is escaped; no scripts or remote resources.</summary>
    public static string Html(TripSnapshot trip, ExportLabels? labels = null, System.Globalization.CultureInfo? culture = null)
    {
        labels ??= new(); culture ??= System.Globalization.CultureInfo.CurrentCulture;
        var it = TripItinerary.Build(trip, labels, culture);
        static string E(string? text) => WebUtility.HtmlEncode(text ?? "");
        var s = new StringBuilder("<!doctype html><html lang=\"").Append(E(trip.Manifest.Data["language"]?.ToString() ?? "en")).Append("\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>").Append(E(it.Title)).Append("</title><style>")
            .Append("body{font:15px/1.5 system-ui,-apple-system,'Segoe UI',sans-serif;color:#0f2a3d;max-width:860px;margin:40px auto;padding:0 20px}")
            .Append("h1{font-size:30px;margin:0 0 4px}h2{font-size:19px;margin:32px 0 10px;padding-bottom:6px;border-bottom:2px solid #0f7b7a}")
            .Append(".meta{color:#4d6170;margin:0 0 8px}.day{color:#0f7b7a;font-weight:600;font-size:13px;letter-spacing:.04em;text-transform:uppercase;margin-right:8px}")
            .Append("table{border-collapse:collapse;width:100%}td{padding:8px 6px;vertical-align:top;border-bottom:1px solid #e1e7e8}td.time{width:150px;color:#4d6170;white-space:nowrap}")
            .Append(".title{font-weight:600}.sub{color:#4d6170;font-size:13px}.tag{display:inline-block;font-size:12px;padding:1px 8px;border-radius:10px;background:#eef2f2;color:#4d6170;margin-left:6px}")
            .Append("footer{margin-top:40px;color:#76889a;font-size:12px}@media print{body{margin:0;max-width:none}h2{break-after:avoid}tr{break-inside:avoid}}")
            .Append("</style></head><body><h1>").Append(E(it.Title)).Append("</h1>");
        var dates = it.Start is { } a && it.End is { } b ? a.ToString("d MMMM yyyy", culture) + " – " + b.ToString("d MMMM yyyy", culture) : "";
        s.Append("<p class=\"meta\">").Append(E(string.Join(" · ", new[] { dates, it.Zone }.Where(x => x.Length > 0)))).Append("</p>");
        if (it.Travellers.Count > 0) s.Append("<p class=\"meta\">").Append(E(labels.Travellers)).Append(": ").Append(E(string.Join(", ", it.Travellers))).Append("</p>");
        void Rows(IEnumerable<ItineraryEntry> entries)
        {
            s.Append("<table>");
            foreach (var e in entries)
            {
                s.Append("<tr><td class=\"time\">").Append(E(e.Time)).Append("</td><td><div class=\"title\">").Append(E(e.Item.Title));
                if (e.Status is { } status) s.Append("<span class=\"tag\">").Append(E(status)).Append("</span>");
                s.Append("</div>");
                var sub = string.Join(" · ", new[] { e.Group, e.Where, e.People.Count > 0 ? string.Join(", ", e.People) : null, e.BookingReference is null ? null : labels.Reference + " " + e.BookingReference }.Where(x => !string.IsNullOrEmpty(x)));
                if (sub.Length > 0) s.Append("<div class=\"sub\">").Append(E(sub)).Append("</div>");
                s.Append("</td></tr>");
            }
            s.Append("</table>");
        }
        foreach (var day in it.Days)
        {
            s.Append("<h2>");
            if (day.Number > 0) s.Append("<span class=\"day\">").Append(E(string.Format(culture, labels.Day, day.Number))).Append("</span>");
            s.Append(E(day.Date.ToString("dddd, d MMMM", culture))).Append("</h2>"); Rows(day.Entries);
        }
        if (it.Unscheduled.Count > 0) { s.Append("<h2>").Append(E(labels.Unscheduled)).Append("</h2>"); Rows(it.Unscheduled); }
        if (it.Bookings.Count > 0)
        {
            s.Append("<h2>").Append(E(labels.Bookings)).Append("</h2><table>");
            foreach (var booking in it.Bookings)
            {
                var price = TripSummaries.Money(booking.Data["price"]);
                s.Append("<tr><td class=\"time\">").Append(E(booking.Data["reference"]?.ToString())).Append("</td><td><div class=\"title\">").Append(E(booking.Title)).Append("</div><div class=\"sub\">")
                    .Append(E(string.Join(" · ", new[] { booking.Data["provider"]?["name"]?.ToString(), price is null ? null : price.Amount.ToString("N2", culture) + " " + price.Currency, labels.Statuses.GetValueOrDefault(booking.Data["status"]?.ToString() ?? "") ?? booking.Data["status"]?.ToString() }.Where(x => !string.IsNullOrEmpty(x))))).Append("</div></td></tr>");
            }
            s.Append("</table>");
        }
        if (it.Costs.Count > 0)
        {
            s.Append("<h2>").Append(E(labels.Costs)).Append("</h2><table>");
            foreach (var c in it.Costs)
                s.Append("<tr><td class=\"time\">").Append(E(c.Currency)).Append("</td><td>").Append(E(c.Paid.ToString("N2", culture) + " " + labels.Paid + (c.Estimated > 0 ? " · " + c.Estimated.ToString("N2", culture) + " " + labels.Estimated : "") + (c.Budget > 0 ? " · " + c.Budget.ToString("N2", culture) + " " + labels.Budget : ""))).Append("</td></tr>");
            s.Append("</table>");
        }
        return s.Append("<footer>").Append(E(labels.GeneratedBy)).Append("</footer></body></html>").ToString();
    }
    public static string Describe(Entity e)
    {
        var lines = new List<string>();
        void Walk(System.Text.Json.Nodes.JsonNode? n, string path)
        {
            if (n is System.Text.Json.Nodes.JsonObject o) { foreach (var (k, v) in o) if (k is not "id" and not "extensions") Walk(v, path.Length == 0 ? k : path + " / " + k); }
            else if (n is System.Text.Json.Nodes.JsonArray a) { foreach (var v in a) Walk(v, path); }
            else if (n is not null) lines.Add(path + ": " + n);
        }
        Walk(e.Data, ""); return string.Join('\n', lines);
    }
    public static string Ics(TripSnapshot trip)
    {
        var lines = new List<string> { "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//TravelRepo//TravelRepo 1.0//EN", "CALSCALE:GREGORIAN" };
        // Nested parents are omitted: their steps are exported individually.
        foreach (var e in trip.Entities.Values.Where(e => e.Type == "schedule_item" && e.Data["children"] is not System.Text.Json.Nodes.JsonArray { Count: > 0 }))
        {
            var span = ScheduleQueries.Span(trip, e); if (!span.IsPlaced || span.Derived) continue;
            lines.AddRange(["BEGIN:VEVENT", "UID:" + e.Id + "@travelrepo", "DTSTAMP:" + Format(SystemClock.Instance.GetCurrentInstant()), "SUMMARY:" + Escape(e.Title)]);
            if (span.Precision == TimePrecision.AllDay && span.Date is { } date) { lines.Add("DTSTART;VALUE=DATE:" + date.ToString("yyyyMMdd", null)); lines.Add("DTEND;VALUE=DATE:" + date.PlusDays(1).ToString("yyyyMMdd", null)); }
            else { lines.Add("DTSTART:" + Format(span.Start!.Value)); lines.Add("DTEND:" + Format(span.End!.Value)); }
            var route = ScheduleQueries.Route(trip, e); var place = ScheduleQueries.PrimaryPlace(trip, e);
            var location = route.From is not null && route.To is not null ? route.From.Title + " → " + route.To.Title : place is null ? null : place.Title + (place.Data["address"]?["formatted"]?.ToString() is { Length: > 0 } address ? ", " + address : "");
            if (location is not null) lines.Add("LOCATION:" + Escape(location));
            var people = string.Join(", ", ScheduleQueries.Participants(trip, e).Select(p => p.Title));
            var booking = Guid.TryParse(e.Data["components"]?["booking"]?["ref"]?.ToString(), out var bookingId) ? trip.Find(bookingId)?.Data["reference"]?.ToString() : null;
            var description = string.Join("\n", new[] { people, booking is null ? null : "Ref. " + booking, e.Data["status"]?.ToString() }.Where(x => !string.IsNullOrEmpty(x)));
            if (description.Length > 0) lines.Add("DESCRIPTION:" + Escape(description));
            if (e.Data["status"]?.ToString() == "cancelled") lines.Add("STATUS:CANCELLED");
            else if (e.Data["status"]?.ToString() is "idea" or "planned") lines.Add("STATUS:TENTATIVE");
            else lines.Add("STATUS:CONFIRMED");
            lines.Add("END:VEVENT");
        }
        lines.Add("END:VCALENDAR"); return string.Join("\r\n", lines.Select(Fold)) + "\r\n";
    }
    private static string Format(Instant i) => i.InUtc().ToString("yyyyMMdd'T'HHmmss'Z'", null);
    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "").Replace(";", "\\;").Replace(",", "\\,");
    private static string Fold(string s)
    {
        var result = new StringBuilder(); var bytes = 0;
        foreach (var rune in s.EnumerateRunes()) { if (bytes + rune.Utf8SequenceLength > 75) { result.Append("\r\n "); bytes = 1; } result.Append(rune); bytes += rune.Utf8SequenceLength; }
        return result.ToString();
    }
}
