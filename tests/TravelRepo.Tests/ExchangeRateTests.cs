using TravelRepo.Core;
using Xunit;
namespace TravelRepo.Tests;

public class ExchangeRateTests
{
    [Theory]
    [InlineData("12.50", "12.50")]
    [InlineData("12,50", "12.50")]
    [InlineData("1,234.50", "1234.50")]
    [InlineData("1.234,50", "1234.50")]
    [InlineData("1.234", "1234")]
    [InlineData("-3,5", "-3.5")]
    [InlineData(" 48 ", "48")]
    public void AmountsAreReadAsPeopleTypeThem(string typed, string value) => Assert.Equal(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), ExchangeRates.Amount(typed));

    [Theory]
    [InlineData("nzd", "NZD")]
    [InlineData(" EUR ", "EUR")]
    [InlineData("euro", null)]
    [InlineData("E1R", null)]
    public void CurrencyCodesAreThreeLetters(string typed, string? code) => Assert.Equal(code, ExchangeRates.Code(typed));

    [Fact]
    public void ConversionGoesThroughTheEuroAndRatesSurviveTheDevice()
    {
        var xml = """<gesmes:Envelope xmlns:gesmes="http://www.gesmes.org/xml/2002-08-01" xmlns="http://www.ecb.int/vocabulary/2002-08-01/eurofxref"><Cube><Cube time="2027-03-01"><Cube currency="USD" rate="1.10"/><Cube currency="JPY" rate="160"/></Cube></Cube></gesmes:Envelope>""";
        var rates = ExchangeRates.Parse(xml);
        Assert.Equal(new DateOnly(2027, 3, 1), rates.Date);
        Assert.Equal(1600m, rates.Convert(11m, "USD", "JPY"));
        Assert.Null(rates.Convert(1m, "XXX", "EUR"));
        var path = Path.Combine(Path.GetTempPath(), "rates-" + Guid.NewGuid() + ".json");
        try { rates.Save(path); Assert.Equal(160m, ExchangeRates.Load(path)!.PerEuro["JPY"]); }
        finally { File.Delete(path); }
    }
}
