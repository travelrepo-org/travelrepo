using System.Net;
using System.Text;
using NodaTime;
using NodaTime.Text;
using TravelRepo.Core;
namespace TravelRepo.Repository;

/// <summary>Portable exports from canonical domain data.</summary>
public static partial class TripExport
{
    /// <summary>A print-friendly, self-contained HTML itinerary. Text is escaped; no scripts or remote resources.</summary>
    public static string Html(TripSnapshot trip, ExportLabels? labels = null, System.Globalization.CultureInfo? culture = null) => Html(Document(trip, null, labels, culture));

    /// <summary>
    /// An export document as a self-contained HTML page. Text is escaped; no scripts or remote resources. Map data and
    /// attached files are not drawn; the attachments section lists them by name.
    /// </summary>
    public static string Html(ExportDocument document)
    {
        static string E(string? text) => WebUtility.HtmlEncode(text ?? "");
        var s = new StringBuilder("<!doctype html><html lang=\"").Append(E(document.Language)).Append("\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>").Append(E(document.Title)).Append("</title><style>")
            .Append("body{font:15px/1.5 system-ui,-apple-system,'Segoe UI',sans-serif;color:#0f2a3d;max-width:860px;margin:40px auto;padding:0 20px}")
            .Append("h1{font-size:30px;margin:0 0 4px}h2{font-size:19px;margin:32px 0 10px;padding-bottom:6px;border-bottom:2px solid #0f7b7a}h3{font-size:14px;margin:14px 0 2px;color:#4d6170}")
            .Append(".meta{color:#4d6170;margin:0 0 8px}.day{color:#0f7b7a;font-weight:600;font-size:13px;letter-spacing:.04em;text-transform:uppercase;margin-right:8px}")
            .Append("table{border-collapse:collapse;width:100%}td{padding:8px 6px;vertical-align:top;border-bottom:1px solid #e1e7e8}td.time{width:150px;color:#4d6170;white-space:nowrap}td.box{width:22px;font-size:17px;line-height:1.2}")
            .Append(".title{font-weight:600}.sub{color:#4d6170;font-size:13px}.detail{color:#4d6170;font-size:13px;margin-top:2px}.tag{display:inline-block;font-size:12px;padding:1px 8px;border-radius:10px;background:#eef2f2;color:#4d6170;margin-left:6px}")
            .Append(".done .title{color:#76889a;text-decoration:line-through}")
            .Append("footer{margin-top:40px;color:#76889a;font-size:12px}@media print{body{margin:0;max-width:none}h2{break-after:avoid}tr{break-inside:avoid}}")
            .Append("</style></head><body><h1>").Append(E(document.Title)).Append("</h1>");
        foreach (var line in document.Meta) s.Append("<p class=\"meta\">").Append(E(line)).Append("</p>");
        foreach (var section in document.Sections)
        {
            s.Append("<h2>");
            if (section.Overline is { } overline) s.Append("<span class=\"day\">").Append(E(overline)).Append("</span>");
            s.Append(E(section.Title)).Append("</h2><table>");
            foreach (var row in section.Rows)
            {
                if (row.Kind == ExportRowKind.Heading) { s.Append("</table><h3>").Append(E(row.Title)).Append("</h3><table>"); continue; }
                s.Append(row.Kind == ExportRowKind.Check && row.Checked ? "<tr class=\"done\">" : "<tr>");
                if (row.Kind == ExportRowKind.Check) s.Append("<td class=\"box\">").Append(row.Checked ? "&#9745;" : "&#9744;").Append("</td>");
                s.Append("<td class=\"time\">").Append(E(row.Lead)).Append("</td><td><div class=\"title\">").Append(E(row.Title));
                if (!string.IsNullOrEmpty(row.Status)) s.Append("<span class=\"tag\">").Append(E(row.Status)).Append("</span>");
                s.Append("</div>");
                if (row.Summary is { Length: > 0 } summary) s.Append("<div class=\"sub\">").Append(E(summary)).Append("</div>");
                foreach (var detail in row.DetailLines) s.Append("<div class=\"detail\">").Append(E(detail)).Append("</div>");
                if (row.AttachmentLabels.Count > 0) s.Append("<div class=\"detail\">").Append(E(AttachmentNote(document, row))).Append("</div>");
                s.Append("</td></tr>");
            }
            s.Append("</table>");
        }
        return s.Append("<footer>").Append(E(document.Footer)).Append("</footer></body></html>").ToString();
    }

    /// <summary>
    /// An export document as plain text, for messengers, mail and the clipboard. Check rows start with a ballot box,
    /// ticked when done.
    /// </summary>
    public static string Text(ExportDocument document)
    {
        var s = new StringBuilder().Append(document.Title).Append('\n');
        foreach (var line in document.Meta) s.Append(line).Append('\n');
        foreach (var section in document.Sections)
        {
            s.Append('\n').Append(section.Overline is { } overline && section.Part != ExportParts.Packing ? overline + " · " : "").Append(section.Title).Append('\n');
            foreach (var row in section.Rows)
            {
                if (row.Kind == ExportRowKind.Heading) { s.Append('\n').Append(row.Title).Append(":\n"); continue; }
                var lead = row.Kind == ExportRowKind.Check ? (row.Checked ? "\u2611 " : "\u2610 ") : row.Lead.Length > 0 ? row.Lead + "  " : "";
                s.Append(lead).Append(row.Title);
                if (row.Kind == ExportRowKind.Check && row.Lead.Length > 0) s.Append(" (").Append(row.Lead).Append(')');
                if (row.Kind == ExportRowKind.Check && row.Summary is { Length: > 0 } who) s.Append(" · ").Append(who);
                if (!string.IsNullOrEmpty(row.Status)) s.Append(" [").Append(row.Status).Append(']');
                s.Append('\n');
                var indent = row.Kind == ExportRowKind.Check ? "  " : new string(' ', Math.Min(lead.Length, 8));
                if (row.Kind != ExportRowKind.Check && row.Summary is { Length: > 0 } summary) s.Append(indent).Append(summary).Append('\n');
                foreach (var detail in row.DetailLines) s.Append(indent).Append(detail).Append('\n');
                if (row.AttachmentLabels.Count > 0) s.Append(indent).Append(AttachmentNote(document, row)).Append('\n');
            }
        }
        return s.ToString();
    }

    /// <summary>The note that points from a row to its appendix pages, such as "Attachment A1, A2".</summary>
    public static string AttachmentNote(ExportDocument document, ExportRow row) => string.Format(System.Globalization.CultureInfo.CurrentCulture, document.Labels.AttachedAs, string.Join(", ", row.AttachmentLabels));

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
    /// <summary>The scheduled items as an iCalendar file. <paramref name="options"/> limits the date range and person.</summary>
    public static string Ics(TripSnapshot trip, ExportOptions? options = null)
    {
        var filter = new ExportFilter(trip, options ?? new());
        var lines = new List<string> { "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//TravelRepo//TravelRepo 1.0//EN", "CALSCALE:GREGORIAN" };
        // Nested parents are omitted: their steps are exported individually.
        foreach (var e in trip.Entities.Values.Where(e => e.Type == "schedule_item" && e.Data["children"] is not System.Text.Json.Nodes.JsonArray { Count: > 0 } && filter.Includes(e)))
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
