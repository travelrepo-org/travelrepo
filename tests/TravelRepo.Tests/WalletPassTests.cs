using System.IO.Compression;
using System.Text;
using TravelRepo.Documents;
using Xunit;
namespace TravelRepo.Tests;

public class WalletPassTests
{
    private static byte[] Pass(string json, string? germanStrings = null)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("pass.json").Open())) writer.Write(json);
            if (germanStrings is not null) using (var writer = new StreamWriter(zip.CreateEntry("de.lproj/pass.strings").Open(), Encoding.Unicode)) writer.Write(germanStrings);
        }
        return stream.ToArray();
    }

    [Fact]
    public void ABoardingPassIsReadWithItsFieldsAndBarcode()
    {
        var bytes = Pass("""
            { "formatVersion": 1, "description": "Boarding pass", "organizationName": "Example Air", "relevantDate": "2027-05-14T12:50+02:00",
              "backgroundColor": "rgb(16,59,92)",
              "barcodes": [ { "message": "M1EXAMPLE/ALEX E ABC123 FRAHND XA 0042", "format": "PKBarcodeFormatAztec", "messageEncoding": "iso-8859-1", "altText": "ABC123" } ],
              "boardingPass": { "transitType": "PKTransitTypeAir",
                "primaryFields": [ { "key": "from", "label": "FRA", "value": "Frankfurt" }, { "key": "to", "label": "HND", "value": "Tokyo" } ],
                "auxiliaryFields": [ { "key": "gate", "label": "gate_label", "value": "B44" }, { "key": "seat", "label": "Seat", "value": 32 } ] } }
            """, "\"gate_label\" = \"Flugsteig\";");
        var pass = WalletPass.Read(bytes, "de")!;
        Assert.Equal(("Example Air", "boardingPass"), (pass.Organization, pass.Style));
        Assert.Equal(new WalletBarcode("M1EXAMPLE/ALEX E ABC123 FRAHND XA 0042", "aztec", "ABC123", "iso-8859-1"), pass.Barcode);
        Assert.Contains(new WalletField("auxiliary", "Flugsteig", "B44"), pass.Fields);
        Assert.Contains(new WalletField("auxiliary", "Seat", "32"), pass.Fields);
        Assert.Equal(new DateTimeOffset(2027, 5, 14, 12, 50, 0, TimeSpan.FromHours(2)), pass.Relevant);
        Assert.Equal(WalletPass.MediaType, MediaTypes.FromFileName("Ticket.pkpass"));
        Assert.Contains("Flugsteig: B44", DocumentText.Extract(bytes, WalletPass.MediaType));
        Assert.Null(WalletPass.Read(Encoding.UTF8.GetBytes("not a zip")));
    }
}
