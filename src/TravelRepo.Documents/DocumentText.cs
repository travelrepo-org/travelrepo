using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace TravelRepo.Documents;

/// <summary>Media types of files people attach to a trip, from their file name.</summary>
public static class MediaTypes
{
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".eml"] = Mail.MediaType,
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".heic"] = "image/heic",
        [".svg"] = "image/svg+xml",
        [".txt"] = "text/plain",
        [".md"] = "text/markdown",
        [".html"] = "text/html",
        [".htm"] = "text/html",
        [".ics"] = "text/calendar",
        [".json"] = "application/json",
        [".gpx"] = "application/gpx+xml",
        [".kml"] = "application/vnd.google-earth.kml+xml",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".zip"] = "application/zip"
    };

    public static string FromFileName(string name) => ByExtension.GetValueOrDefault(Path.GetExtension(name)) ?? "application/octet-stream";

    /// <summary>The usual file extension for a media type, with a dot, or an empty string.</summary>
    public static string Extension(string mediaType) => ByExtension.FirstOrDefault(p => string.Equals(p.Value, mediaType, StringComparison.OrdinalIgnoreCase)).Key ?? "";
}

/// <summary>
/// The readable text of a document, for search and for assistants: the text of a PDF, the headers and text of an
/// e-mail, or a text file. Scanned PDFs without a text layer have no text; OCR is not part of TravelRepo.
/// </summary>
public static class DocumentText
{
    public static string? Extract(byte[] bytes, string mediaType, int maxCharacters = 200_000)
    {
        string? text;
        try
        {
            text = mediaType.ToLowerInvariant() switch
            {
                "application/pdf" => Pdf(bytes, maxCharacters),
                Mail.MediaType => MailText(Mail.Read(bytes)),
                var type when type.StartsWith("text/", StringComparison.Ordinal) && type != "text/html" => Encoding.UTF8.GetString(bytes),
                "text/html" => HtmlText.Convert(Encoding.UTF8.GetString(bytes)).Text,
                _ => null
            };
        }
        catch (Exception ex) when (ex is TravelRepo.Core.DomainException or FormatException or InvalidOperationException or ArgumentException or IOException or UglyToad.PdfPig.Core.PdfDocumentFormatException)
        { return null; }
        return text is null ? null : text.Length > maxCharacters ? text[..maxCharacters] : text;
    }

    /// <summary>An e-mail as plain text: subject, sender, recipients, date, body and attachment names.</summary>
    public static string MailText(MailMessage mail)
    {
        var text = new StringBuilder();
        if (mail.Subject is { Length: > 0 } subject) text.AppendLine("Subject: " + subject);
        if (mail.From.Count > 0) text.AppendLine("From: " + string.Join(", ", mail.From));
        if (mail.To.Count > 0) text.AppendLine("To: " + string.Join(", ", mail.To));
        if (mail.Date is { } date) text.AppendLine("Date: " + date.ToString("yyyy-MM-dd HH:mm zzz", System.Globalization.CultureInfo.InvariantCulture));
        if (mail.Attachments.Count > 0) text.AppendLine("Attachments: " + string.Join(", ", mail.Attachments.Select(a => a.FileName)));
        return text.AppendLine().Append(mail.Text).ToString();
    }

    private static string Pdf(byte[] bytes, int maxCharacters)
    {
        using var document = PdfDocument.Open(bytes);
        var text = new StringBuilder();
        foreach (var page in document.GetPages())
        {
            if (text.Length > maxCharacters) break;
            if (text.Length > 0) text.Append("\n\n");
            text.Append(ContentOrderTextExtractor.GetText(page).Trim());
        }
        return text.ToString();
    }
}
