using System.Text.RegularExpressions;
using MimeKit.Text;

namespace TravelRepo.Documents;

/// <summary>An image embedded in an e-mail and referenced from its HTML as <c>cid:</c>.</summary>
public sealed record MailInlineImage(string ContentId, string MediaType, byte[] Content);

/// <summary>
/// E-mail HTML made safe to show in its original layout. <see cref="RemoteImages"/> counts the images that would be
/// loaded from elsewhere; they are left out unless allowed, because loading them tells the sender that and when the
/// mail was opened.
/// </summary>
public sealed record SafeMailHtml(string Html, int RemoteImages, bool RemoteImagesAllowed);

/// <summary>
/// Cleans e-mail HTML for display with an allow list: no scripts, frames, forms, embedded objects, external style
/// sheets or event handlers, links only to web pages, e-mail and phone numbers, and images only from the message
/// itself (<c>cid:</c>), from <c>data:</c> or, when allowed, from the web. Nothing is fetched here.
/// </summary>
public static partial class MailHtml
{
    // Elements whose start and end tags and everything between them are dropped.
    private static readonly HashSet<string> Removed = new(StringComparer.OrdinalIgnoreCase)
    { "script", "noscript", "iframe", "frame", "frameset", "object", "embed", "applet", "template", "svg", "math", "audio", "video", "canvas", "select", "textarea", "button", "title" };
    // Elements whose tags are dropped but whose content stays.
    private static readonly HashSet<string> Unwrapped = new(StringComparer.OrdinalIgnoreCase) { "form", "link", "meta", "base", "input", "source", "track", "param", "portal" };
    private static readonly HashSet<string> UrlAttributes = new(StringComparer.OrdinalIgnoreCase) { "href", "src", "background", "action", "formaction", "poster", "srcset", "xlink:href", "lowsrc", "dynsrc", "longdesc", "cite", "data", "usemap" };

    [GeneratedRegex(@"@import[^;]*;?", RegexOptions.IgnoreCase)]
    private static partial Regex Import();
    [GeneratedRegex(@"url\s*\(\s*(['""]?)(.*?)\1\s*\)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex CssUrl();
    // "background: <colour>", which some renderers only understand as background-color.
    [GeneratedRegex(@"(?<![-\w])background\s*:\s*(#[0-9a-fA-F]{3,8}|[a-zA-Z]+|rgba?\([^)]*\))\s*(?=;|}|$)", RegexOptions.IgnoreCase)]
    private static partial Regex BackgroundColour();
    // A whole declaration that could run code, up to the next ";" or brace.
    [GeneratedRegex(@"[^;{}]*(expression\s*\(|behavior\s*:|-moz-binding|javascript:)[^;{}]*;?", RegexOptions.IgnoreCase)]
    private static partial Regex CssScript();

    public static SafeMailHtml Sanitize(string html, bool allowRemoteImages = false)
    {
        var tokenizer = new HtmlTokenizer(new StringReader(html));
        using var output = new StringWriter();
        var skipping = new Stack<string>(); var inStyle = false; var remote = 0;
        while (tokenizer.ReadNextToken(out var token))
        {
            switch (token)
            {
                case HtmlTagToken tag when Removed.Contains(tag.Name):
                    if (tag.IsEndTag) { if (skipping.Count > 0 && string.Equals(skipping.Peek(), tag.Name, StringComparison.OrdinalIgnoreCase)) skipping.Pop(); }
                    else if (!tag.IsEmptyElement) skipping.Push(tag.Name);
                    break;
                case var _ when skipping.Count > 0:
                    break;
                case HtmlTagToken tag when Unwrapped.Contains(tag.Name):
                    break;
                case HtmlTagToken { IsEndTag: true } end:
                    if (string.Equals(end.Name, "style", StringComparison.OrdinalIgnoreCase)) inStyle = false;
                    end.WriteTo(output);
                    break;
                case HtmlTagToken tag:
                    if (string.Equals(tag.Name, "style", StringComparison.OrdinalIgnoreCase)) inStyle = !tag.IsEmptyElement;
                    var attributes = new List<HtmlAttribute>();
                    foreach (var attribute in tag.Attributes)
                    {
                        var name = attribute.Name; var value = attribute.Value ?? "";
                        if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase)) continue;
                        if (string.Equals(name, "style", StringComparison.OrdinalIgnoreCase)) { attributes.Add(new(name, Css(value, allowRemoteImages, ref remote))); continue; }
                        if (UrlAttributes.Contains(name))
                        {
                            var isImage = string.Equals(name, "src", StringComparison.OrdinalIgnoreCase) && string.Equals(tag.Name, "img", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(name, "background", StringComparison.OrdinalIgnoreCase);
                            if (Url(value, isImage, allowRemoteImages, ref remote) is { } safe) attributes.Add(new(name, safe));
                            continue;
                        }
                        attributes.Add(new(name, value));
                    }
                    // An image whose source was removed would show as a broken frame; it is left out.
                    if (string.Equals(tag.Name, "img", StringComparison.OrdinalIgnoreCase) && !attributes.Any(a => string.Equals(a.Name, "src", StringComparison.OrdinalIgnoreCase))) break;
                    // Links open outside the viewer, never in a frame of the mail.
                    if (string.Equals(tag.Name, "a", StringComparison.OrdinalIgnoreCase)) attributes.RemoveAll(a => string.Equals(a.Name, "target", StringComparison.OrdinalIgnoreCase));
                    new HtmlTagToken(tag.Name, attributes, tag.IsEmptyElement).WriteTo(output);
                    break;
                case HtmlDataToken data when inStyle:
                    output.Write(Css(data.Data, allowRemoteImages, ref remote));
                    break;
                case HtmlCommentToken or HtmlScriptDataToken or HtmlCDataToken:
                    break;
                default:
                    token.WriteTo(output);
                    break;
            }
        }
        return new(output.ToString(), remote, allowRemoteImages);
    }

    /// <summary>Whether a link may be opened: web pages, e-mail addresses and phone numbers only.</summary>
    public static bool IsOpenableLink(string? link) =>
        Uri.TryCreate(link?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto" or "tel";

    private static string? Url(string value, bool image, bool allowRemote, ref int remote)
    {
        var url = value.Trim();
        if (url.StartsWith('#')) return image ? null : url;
        if (image)
        {
            if (url.StartsWith("cid:", StringComparison.OrdinalIgnoreCase)) return url;
            if (url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("data:image/svg", StringComparison.OrdinalIgnoreCase)) return url;
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("//", StringComparison.Ordinal))
            {
                remote++;
                return allowRemote ? (url.StartsWith("//", StringComparison.Ordinal) ? "https:" + url : url) : null;
            }
            return null;
        }
        return IsOpenableLink(url) ? url : null;
    }

    /// <summary>CSS without imports, scripts or remote images (unless allowed).</summary>
    private static string Css(string css, bool allowRemote, ref int remote)
    {
        css = CssScript().Replace(css, "");
        css = BackgroundColour().Replace(css, "background-color:$1");
        css = Import().Replace(css, "");
        var count = 0;
        css = CssUrl().Replace(css, m =>
        {
            var url = m.Groups[2].Value.Trim();
            if (url.StartsWith("cid:", StringComparison.OrdinalIgnoreCase) || url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("data:image/svg", StringComparison.OrdinalIgnoreCase)) return m.Value;
            if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase) || url.StartsWith("//", StringComparison.Ordinal)) { count++; return allowRemote ? m.Value : "none"; }
            return "none";
        });
        remote += count;
        return css;
    }
}
