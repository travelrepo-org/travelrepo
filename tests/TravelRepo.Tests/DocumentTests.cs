using MimeKit;
using TravelRepo.Documents;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;
namespace TravelRepo.Tests;

public class DocumentTests
{
    /// <summary>A small PDF with real text, like a booking confirmation.</summary>
    public static byte[] Pdf(params string[] lines)
    {
        var builder = new PdfDocumentBuilder(); var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4); var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        for (var i = 0; i < lines.Length; i++) page.AddText(lines[i], 12, new UglyToad.PdfPig.Core.PdfPoint(60, 760 - i * 20), font);
        return builder.Build();
    }

    /// <summary>A booking confirmation as mail programs save it: HTML with a tracking image, a link and the ticket as PDF.</summary>
    public static byte[] Confirmation()
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Lakeview Lodge", "bookings@lodge.example"));
        message.To.Add(new MailboxAddress("Alex", "alex@example.invalid"));
        message.Subject = "Your booking 4711 is confirmed";
        message.Date = new DateTimeOffset(2026, 10, 2, 9, 30, 0, TimeSpan.FromHours(13));
        var body = new BodyBuilder
        {
            HtmlBody = "<html><head><style>p{color:red}</style><script>alert(1)</script></head><body><h1>Booking confirmed</h1><p>Check-in from <b>15:00</b>, check-out by 10:00.</p>"
                + "<p><a href=\"https://lodge.example/manage/4711\">Manage booking</a></p><img src=\"https://tracker.example/pixel.gif\"><ul><li>2 nights</li><li>Lake view</li></ul></body></html>"
        };
        body.Attachments.Add("ticket.pdf", Pdf("Booking 4711", "Lakeview Lodge"), new ContentType("application", "pdf"));
        var forwarded = new MimeMessage { Subject = "Earlier offer" }; forwarded.From.Add(new MailboxAddress("Lodge", "offers@lodge.example")); forwarded.Body = new TextPart("plain") { Text = "Our offer" };
        body.Attachments.Add(new MessagePart { Message = forwarded });
        message.Body = body.ToMessageBody();
        using var stream = new MemoryStream(); message.WriteTo(stream); return stream.ToArray();
    }

    [Fact]
    public void AnEmailIsReadWithoutLoadingAnything()
    {
        var mail = Mail.Read(Confirmation());
        Assert.Equal("Your booking 4711 is confirmed", mail.Subject);
        Assert.Equal("Lakeview Lodge <bookings@lodge.example>", mail.From.Single().ToString());
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 9, 30, 0, TimeSpan.FromHours(13)), mail.Date);
        Assert.True(mail.TextFromHtml); Assert.Equal(1, mail.RemoteImages);
        Assert.Contains("Booking confirmed", mail.Text); Assert.Contains("Check-in from 15:00, check-out by 10:00.", mail.Text);
        Assert.Contains("Manage booking (https://lodge.example/manage/4711)", mail.Text); Assert.Contains("• 2 nights", mail.Text);
        Assert.DoesNotContain("alert", mail.Text); Assert.DoesNotContain("color", mail.Text);
        Assert.Equal(["ticket.pdf", "Earlier offer.eml"], mail.Attachments.Select(a => a.FileName).ToArray());
        Assert.Equal(Mail.MediaType, mail.Attachments[1].MediaType);
        Assert.Equal("Earlier offer", Mail.Read(mail.Attachments[1].Content).Subject);
        Assert.Contains("Booking 4711", DocumentText.Extract(mail.Attachments[0].Content, mail.Attachments[0].MediaType));
    }

    [Fact]
    public void MailHtmlKeepsTheLayoutButNothingThatRunsOrTracks()
    {
        const string html = "<html><head><title>x</title><link rel=\"stylesheet\" href=\"https://cdn.example/a.css\"><style>@import url(https://cdn.example/b.css); td{background:url(https://tracker.example/bg.gif);color:#333} .x{width:expression(alert(1))}</style>"
            + "<script>alert(1)</script></head><body onload=\"steal()\"><table width=\"600\" style=\"background-image:url('https://tracker.example/t.png');border:1px solid #ccc\"><tr><td align=\"right\">Total &amp; fees</td></tr></table>"
            + "<img src=\"cid:logo@lodge\" alt=\"Logo\"><img src=\"https://tracker.example/pixel.gif\"><img src=\"data:image/png;base64,iVBORw0KGgo=\"><img src=\"data:image/svg+xml;base64,PHN2Zz4=\">"
            + "<a href=\"https://lodge.example/manage\" target=\"_blank\">Manage</a> <a href=\"javascript:steal()\">Bad</a> <a href=\"mailto:desk@lodge.example\">Mail</a>"
            + "<form action=\"https://evil.example\"><input name=\"card\"><button>Pay</button>Fill in</form><iframe src=\"https://evil.example\"></iframe><!-- secret --></body></html>";
        var safe = MailHtml.Sanitize(html);
        Assert.Equal(3, safe.RemoteImages); Assert.False(safe.RemoteImagesAllowed);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(safe.Html, "<img")); // the cid and PNG images; blocked ones leave no empty frame
        Assert.Contains("background-color:#7a2e2e", MailHtml.Sanitize("<td style=\"background:#7a2e2e;color:#fff\">x</td>").Html);
        Assert.Contains("background-color:red}", MailHtml.Sanitize("<style>td{background: red}</style>").Html);
        foreach (var gone in new[] { "<script", "alert", "steal", "onload", "<link", "@import", "tracker.example", "expression", "javascript:", "<iframe", "evil.example", "<input", "<button", "Pay", "secret", "<title", "image/svg", "target" })
            Assert.DoesNotContain(gone, safe.Html, StringComparison.OrdinalIgnoreCase);
        foreach (var kept in new[] { "<table width=\"600\"", "border:1px solid #ccc", "align=\"right\"", "Total &amp; fees", "src=\"cid:logo@lodge\"", "data:image/png", "href=\"https://lodge.example/manage\"", "href=\"mailto:desk@lodge.example\"", "Fill in", "color:#333" })
            Assert.Contains(kept, safe.Html);

        var allowed = MailHtml.Sanitize(html, allowRemoteImages: true);
        Assert.Contains("https://tracker.example/pixel.gif", allowed.Html); Assert.True(allowed.RemoteImagesAllowed);
        Assert.DoesNotContain("@import", allowed.Html); Assert.DoesNotContain("evil.example", allowed.Html);

        Assert.True(MailHtml.IsOpenableLink("https://lodge.example")); Assert.True(MailHtml.IsOpenableLink("tel:+4930123"));
        Assert.False(MailHtml.IsOpenableLink("javascript:alert(1)")); Assert.False(MailHtml.IsOpenableLink("file:///etc/passwd")); Assert.False(MailHtml.IsOpenableLink("/relative"));
    }

    [Fact]
    public void AnEmailKeepsItsHtmlAndInlineImages()
    {
        var message = new MimeMessage { Subject = "With logo" }; message.From.Add(new MailboxAddress("Lodge", "desk@lodge.example"));
        var body = new BodyBuilder { HtmlBody = "<p><img src=\"cid:logo@lodge\"> Welcome</p>" };
        var logo = body.LinkedResources.Add("logo.png", new byte[] { 137, 80, 78, 71 }, new ContentType("image", "png")); logo.ContentId = "logo@lodge";
        message.Body = body.ToMessageBody();
        using var stream = new MemoryStream(); message.WriteTo(stream);
        var mail = Mail.Read(stream.ToArray());
        Assert.Contains("cid:logo@lodge", mail.Html);
        var image = Assert.Single(mail.InlineImages!);
        Assert.Equal(("logo@lodge", "image/png", 4), (image.ContentId, image.MediaType, image.Content.Length));
        Assert.Empty(mail.Attachments);
    }

    [Fact]
    public void TextOfPdfsMailsAndTextFiles()
    {
        Assert.Contains("Lakeview Lodge", DocumentText.Extract(Pdf("Booking 4711", "Lakeview Lodge"), "application/pdf"));
        var mail = DocumentText.Extract(Confirmation(), Mail.MediaType)!;
        Assert.StartsWith("Subject: Your booking 4711 is confirmed", mail); Assert.Contains("Attachments: ticket.pdf", mail);
        Assert.Equal("socks", DocumentText.Extract("socks"u8.ToArray(), "text/plain"));
        Assert.Null(DocumentText.Extract([1, 2, 3], "application/pdf"));
        Assert.Null(DocumentText.Extract([1, 2, 3], "image/png"));
    }

    [Fact]
    public void FileNamesAndMediaTypes()
    {
        Assert.Equal("ticket.pdf", Mail.SafeName("../../etc/ticket.pdf")); Assert.Equal("a_b.pdf", Mail.SafeName("a:b.pdf")); Assert.Equal("attachment", Mail.SafeName(".."));
        Assert.Equal(Mail.MediaType, MediaTypes.FromFileName("Booking.EML")); Assert.Equal("application/pdf", MediaTypes.FromFileName("x.pdf"));
        Assert.Equal("application/octet-stream", MediaTypes.FromFileName("x.unknown")); Assert.Equal(".eml", MediaTypes.Extension(Mail.MediaType));
        Assert.Throws<TravelRepo.Core.DomainException>(() => Mail.Read("this is not mail"u8.ToArray()).Subject?.Length);
    }
}
