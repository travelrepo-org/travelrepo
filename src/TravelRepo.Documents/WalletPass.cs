using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TravelRepo.Documents;

/// <summary>The code on a pass, to scan at a gate or door. <see cref="Format"/> is qr, pdf417, aztec or code128.</summary>
public sealed record WalletBarcode(string Message, string Format, string? AltText, string Encoding);

/// <summary>A labelled value on a pass, such as "Gate: B44". <see cref="Group"/> is primary, secondary, auxiliary, header or back.</summary>
public sealed record WalletField(string Group, string? Label, string Value);

/// <summary>
/// A Wallet pass (.pkpass), such as a boarding pass or a museum ticket, read without the platform's wallet: what it is,
/// its fields and its barcode, so a client can show it offline. Labels are translated with the pass's own strings,
/// <paramref name="Style"/> is boardingPass, eventTicket, coupon, storeCard or generic.
/// </summary>
public sealed record WalletPass(string? Description, string? Organization, string? Style, WalletBarcode? Barcode, IReadOnlyList<WalletField> Fields, DateTimeOffset? Relevant, string? Background, string? Foreground)
{
    public const string MediaType = "application/vnd.apple.pkpass";

    /// <summary>Reads a .pkpass file; null when it is not one. <paramref name="language"/> picks the pass's translations.</summary>
    public static WalletPass? Read(byte[] bytes, string language = "en")
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            if (zip.GetEntry("pass.json") is not { } entry) return null;
            using var json = JsonDocument.Parse(ReadAll(entry));
            var pass = json.RootElement;
            var strings = Strings(zip, language);
            string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number
                ? Translate(v.ToString(), strings) : null;

            var style = new[] { "boardingPass", "eventTicket", "coupon", "storeCard", "generic" }.FirstOrDefault(s => pass.TryGetProperty(s, out _));
            var fields = new List<WalletField>();
            if (style is not null && pass.GetProperty(style) is { ValueKind: JsonValueKind.Object } body)
                foreach (var group in new[] { "header", "primary", "secondary", "auxiliary", "back" })
                    if (body.TryGetProperty(group + "Fields", out var list) && list.ValueKind == JsonValueKind.Array)
                        foreach (var field in list.EnumerateArray())
                            if (Text(field, "value") is { Length: > 0 } value) fields.Add(new(group, Text(field, "label"), value));

            // Newer passes list barcodes; older ones have one.
            var code = pass.TryGetProperty("barcodes", out var codes) && codes.ValueKind == JsonValueKind.Array && codes.GetArrayLength() > 0 ? codes[0]
                : pass.TryGetProperty("barcode", out var single) && single.ValueKind == JsonValueKind.Object ? single : (JsonElement?)null;
            WalletBarcode? barcode = null;
            if (code is { } c && c.TryGetProperty("message", out var message) && message.GetString() is { Length: > 0 } text)
            {
                var format = c.TryGetProperty("format", out var f) ? f.GetString() switch
                {
                    "PKBarcodeFormatQR" => "qr", "PKBarcodeFormatPDF417" => "pdf417", "PKBarcodeFormatAztec" => "aztec", "PKBarcodeFormatCode128" => "code128", _ => null
                } : null;
                if (format is not null) barcode = new(text, format, Text(c, "altText"), c.TryGetProperty("messageEncoding", out var enc) ? enc.GetString() ?? "iso-8859-1" : "iso-8859-1");
            }
            DateTimeOffset? relevant = pass.TryGetProperty("relevantDate", out var r) && DateTimeOffset.TryParse(r.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var at) ? at : null;
            return new(Text(pass, "description"), Text(pass, "organizationName"), style, barcode, fields, relevant, Text(pass, "backgroundColor"), Text(pass, "foregroundColor"));
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or InvalidOperationException or KeyNotFoundException or IOException) { return null; }
    }

    /// <summary>The pass as text, for search and assistants.</summary>
    public string Text() => string.Join('\n', new[] { Organization, Description }.Concat(Fields.Select(f => (f.Label is null ? "" : f.Label + ": ") + f.Value)).Where(t => !string.IsNullOrWhiteSpace(t)));

    private static string ReadAll(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>The pass's strings for a language, falling back to English and then to any language it has.</summary>
    private static Dictionary<string, string> Strings(ZipArchive zip, string language)
    {
        var entry = zip.GetEntry(language + ".lproj/pass.strings") ?? zip.GetEntry("en.lproj/pass.strings") ?? zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".lproj/pass.strings", StringComparison.Ordinal));
        if (entry is null) return [];
        // pass.strings is usually UTF-16; the reader detects it from the byte order mark.
        var text = ReadAll(entry);
        return Regex.Matches(text, "\"((?:[^\"\\\\]|\\\\.)*)\"\\s*=\\s*\"((?:[^\"\\\\]|\\\\.)*)\"\\s*;").ToDictionary(m => Regex.Unescape(m.Groups[1].Value), m => Regex.Unescape(m.Groups[2].Value));
    }

    private static string Translate(string value, Dictionary<string, string> strings) => strings.GetValueOrDefault(value) ?? value;
}
