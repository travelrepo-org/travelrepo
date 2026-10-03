using System.Net;
using System.Text;
using NodaTime;
using NodaTime.Text;
using TravelRepo.Core;
namespace TravelRepo.Repository;

/// <summary>Portable exports from canonical domain data.</summary>
public static class TripExport
{
    public static string Html(TripSnapshot trip)
    {
        var s = new StringBuilder("<!doctype html><html><head><meta charset=\"utf-8\"><title>").Append(WebUtility.HtmlEncode(trip.Manifest.Title)).Append("</title><style>body{font:16px sans-serif;max-width:900px;margin:40px auto;color:#16364e}article{break-inside:avoid;border-bottom:1px solid #bbb;padding:12px}pre{white-space:pre-wrap}@media print{body{margin:0}}</style></head><body><h1>").Append(WebUtility.HtmlEncode(trip.Manifest.Title)).Append("</h1>");
        foreach (var e in trip.Entities.Values.OrderBy(e => e.Data["time"]?["start"]?["local"]?.ToString() ?? "~")) s.Append("<article><h2>").Append(WebUtility.HtmlEncode(e.Title)).Append("</h2><pre>").Append(WebUtility.HtmlEncode(Describe(e))).Append("</pre></article>");
        return s.Append("</body></html>").ToString();
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
        foreach (var e in trip.Entities.Values.Where(e => e.Type == "schedule_item"))
        {
            var time = e.Data["time"]; if (time is null) continue;
            var start = time["start"]; var allDay = time["precision"]?.ToString() == "all_day";
            if (start is null && !allDay) continue;
            lines.AddRange(["BEGIN:VEVENT", "UID:" + e.Id + "@travelrepo", "DTSTAMP:" + Format(SystemClock.Instance.GetCurrentInstant()), "SUMMARY:" + Escape(e.Title)]);
            if (allDay) { var date = LocalDatePattern.Iso.Parse(time["date"]!.ToString()).Value; lines.Add("DTSTART;VALUE=DATE:" + date.ToString("yyyyMMdd", null)); lines.Add("DTEND;VALUE=DATE:" + date.PlusDays(1).ToString("yyyyMMdd", null)); }
            else { lines.Add("DTSTART:" + Format(ZonedTime.From(start!).ToInstant())); if (time["end"] is { } end) lines.Add("DTEND:" + Format(ZonedTime.From(end).ToInstant())); }
            lines.Add("DESCRIPTION:" + Escape(Describe(e))); lines.Add("END:VEVENT");
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
