using System.Text;
using MimeKit;
using MimeKit.Text;

namespace TravelRepo.Documents;

public sealed record MailAddress(string? Name, string Address)
{
    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? Address : Name + " <" + Address + ">";
}

/// <summary>A file attached to an e-mail. A forwarded message is an attachment of type <c>message/rfc822</c>.</summary>
public sealed record MailAttachment(string FileName, string MediaType, byte[] Content);

/// <summary>
/// An e-mail read for display: headers, the readable text and the attachments. <see cref="Text"/> comes from the
/// plain-text part, or from the HTML part with markup removed. Nothing is fetched from the network; HTML images that
/// would be loaded from elsewhere are only counted in <see cref="RemoteImages"/>.
/// </summary>
public sealed record MailMessage(
    string? Subject,
    IReadOnlyList<MailAddress> From,
    IReadOnlyList<MailAddress> To,
    IReadOnlyList<MailAddress> Cc,
    DateTimeOffset? Date,
    string Text,
    bool TextFromHtml,
    int RemoteImages,
    IReadOnlyList<MailAttachment> Attachments);

/// <summary>Reads e-mail messages saved as <c>.eml</c> files (RFC 5322 / MIME).</summary>
public static class Mail
{
    public const string MediaType = "message/rfc822";

    public static bool IsMail(string? mediaType) => string.Equals(mediaType, MediaType, StringComparison.OrdinalIgnoreCase);

    public static MailMessage Read(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return Read(stream);
    }

    public static MailMessage Read(Stream stream)
    {
        MimeMessage message;
        try { message = MimeMessage.Load(stream); }
        catch (FormatException ex) { throw new TravelRepo.Core.DomainException("mail.invalid", "This file is not a readable e-mail: " + ex.Message); }
        IReadOnlyList<MailAddress> Addresses(InternetAddressList list) => list.Mailboxes.Select(m => new MailAddress(string.IsNullOrWhiteSpace(m.Name) ? null : m.Name, m.Address)).ToArray();
        var html = message.HtmlBody; var plain = message.TextBody;
        var (text, images) = plain is { Length: > 0 } && !LooksEmpty(plain) ? (Normalize(plain), html is null ? 0 : HtmlText.Convert(html).RemoteImages) : html is not null ? HtmlText.Convert(html) : ("", 0);
        return new(message.Subject, Addresses(message.From), Addresses(message.To), Addresses(message.Cc), message.Date == DateTimeOffset.MinValue ? null : message.Date,
            text, plain is not { Length: > 0 } || LooksEmpty(plain), images, Attachments(message));
    }

    private static bool LooksEmpty(string text) => string.IsNullOrWhiteSpace(text);

    private static IReadOnlyList<MailAttachment> Attachments(MimeMessage message)
    {
        var result = new List<MailAttachment>();
        foreach (var entity in message.BodyParts)
        {
            using var buffer = new MemoryStream();
            switch (entity)
            {
                // A forwarded message, attached or inline.
                case MessagePart forwarded when forwarded.Message is { } inner:
                    inner.WriteTo(buffer);
                    result.Add(new(SafeName((inner.Subject ?? "message") + ".eml"), MediaType, buffer.ToArray()));
                    break;
                // Attachments, and PDFs that booking tools put inline instead of attaching them.
                case MimePart part when part.Content is not null && (part.IsAttachment || part.ContentType.IsMimeType("application", "pdf")):
                    part.Content.DecodeTo(buffer);
                    var type = part.ContentType.MimeType.ToLowerInvariant();
                    result.Add(new(SafeName(part.FileName ?? part.ContentType.Name ?? "attachment" + MediaTypes.Extension(type)), type, buffer.ToArray()));
                    break;
            }
        }
        return result;
    }

    /// <summary>A file name without folders or characters that file systems reject.</summary>
    public static string SafeName(string name)
    {
        var file = Path.GetFileName(name.Replace('\\', '/').Split('/').Last());
        var invalid = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '|', '?', '*']).ToHashSet();
        var clean = new string(file.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim(' ', '.');
        return clean.Length == 0 ? "attachment" : clean.Length > 120 ? clean[..120] : clean;
    }

    private static string Normalize(string text) => string.Join('\n', text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(l => l.TrimEnd())).Trim();
}

/// <summary>Turns HTML into readable plain text without loading anything. Scripts, styles and hidden heads are dropped.</summary>
public static class HtmlText
{
    private static readonly HashSet<string> Blocks = new(StringComparer.OrdinalIgnoreCase) { "p", "div", "br", "tr", "li", "h1", "h2", "h3", "h4", "h5", "h6", "table", "section", "article", "header", "footer", "blockquote", "hr", "ul", "ol", "dl", "dt", "dd", "pre", "address" };
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase) { "script", "style", "head", "title", "noscript", "template", "svg" };

    public static (string Text, int RemoteImages) Convert(string html)
    {
        var tokenizer = new HtmlTokenizer(new StringReader(html));
        var text = new StringBuilder(); var skipping = 0; var images = 0; string? link = null; var linkText = new StringBuilder();
        void Break() { while (text.Length > 0 && text[^1] == ' ') text.Length--; if (text.Length > 0 && !text.ToString().EndsWith("\n\n", StringComparison.Ordinal)) text.Append('\n'); }
        while (tokenizer.ReadNextToken(out var token))
        {
            switch (token)
            {
                case HtmlTagToken tag when Skipped.Contains(tag.Name):
                    if (!tag.IsEmptyElement) skipping += tag.IsEndTag ? -1 : 1;
                    skipping = Math.Max(0, skipping);
                    break;
                case HtmlTagToken tag when skipping == 0:
                    if (tag.Id == HtmlTagId.Image && !tag.IsEndTag && tag.Attributes.FirstOrDefault(a => a.Id == HtmlAttributeId.Src)?.Value is { } source && source.StartsWith("http", StringComparison.OrdinalIgnoreCase)) images++;
                    if (tag.Id == HtmlTagId.A && !tag.IsEndTag) { link = tag.Attributes.FirstOrDefault(a => a.Id == HtmlAttributeId.Href)?.Value; linkText.Clear(); }
                    else if (tag.Id == HtmlTagId.A && tag.IsEndTag)
                    {
                        // A link keeps its address when the visible text does not show it, so booking links stay usable.
                        if (link is not null && link.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !linkText.ToString().Contains(link, StringComparison.OrdinalIgnoreCase)) text.Append(" (" + link + ")");
                        link = null;
                    }
                    if (Blocks.Contains(tag.Name)) Break();
                    if (tag.Id is HtmlTagId.TD or HtmlTagId.TH && !tag.IsEndTag && text.Length > 0 && text[^1] != '\n') text.Append("  ");
                    if (tag.Id == HtmlTagId.LI && !tag.IsEndTag) text.Append("• ");
                    break;
                case HtmlDataToken data when skipping == 0:
                    var value = System.Text.RegularExpressions.Regex.Replace(data.Data, @"\s+", " ");
                    if (text.Length == 0 || text[^1] == '\n') value = value.TrimStart();
                    text.Append(value); if (link is not null) linkText.Append(value);
                    break;
            }
        }
        var lines = text.ToString().Split('\n').Select(l => l.Trim());
        var compact = new List<string>();
        foreach (var line in lines) if (line.Length > 0 || compact.Count > 0 && compact[^1].Length > 0) compact.Add(line);
        return (string.Join('\n', compact).Trim(), images);
    }
}
