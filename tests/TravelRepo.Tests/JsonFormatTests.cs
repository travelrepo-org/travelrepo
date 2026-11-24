using System.Text;
using System.Text.Json.Nodes;
using NodaTime;
using TravelRepo.Core;
using TravelRepo.Plugins;
using TravelRepo.Providers.Weather;
using TravelRepo.Repository;
using TravelRepo.Serialization;
using Xunit;
namespace TravelRepo.Tests;

/// <summary>
/// Files TravelRepo writes as JSON keep their format, so files written by older versions stay readable and newer
/// versions write what older ones read. The JSON is source generated, which also keeps it working under NativeAOT.
/// </summary>
public sealed class JsonFormatTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "travelrepo-json-" + Guid.NewGuid().ToString("N"));
    public void Dispose() => TestFiles.Delete(root);

    [Fact]
    public void ExchangeRateFileKeepsItsFormat()
    {
        Directory.CreateDirectory(root); var path = Path.Combine(root, "rates.json");
        new ExchangeRates(new DateOnly(2026, 4, 1), new Dictionary<string, decimal> { ["JPY"] = 160.5m, ["NZD"] = 1.85m }).Save(path);
        Assert.Equal("""{"Date":"2026-04-01","PerEuro":{"JPY":160.5,"NZD":1.85}}""", File.ReadAllText(path));

        File.WriteAllText(path, """{"Date":"2026-03-30","PerEuro":{"CAD":1.49,"JPY":161.25}}""");
        var rates = ExchangeRates.Load(path)!;
        Assert.Equal(new DateOnly(2026, 3, 30), rates.Date); Assert.Equal(161.25m, rates.PerEuro["JPY"]); Assert.Equal(1.49m, rates.PerEuro["CAD"]);
    }

    [Fact]
    public async Task WriteJournalKeepsItsFormat()
    {
        var repo = new TravelRepository(Path.Combine(root, "trip"), Path.Combine(root, "recovery")); await repo.InitializeAsync(Entity.CreateTrip("Spring in Japan"));
        var person = Entity.Create("person", "Alex"); await repo.ApplyAsync(await repo.ReadAsync(), [new(person.Id, person)]);
        var journal = JsonNode.Parse(File.ReadAllText(Directory.EnumerateFiles(repo.RecoveryRoot, "*.complete").Last()))!.AsArray();
        var entry = Assert.Single(journal, e => e!["Path"]!.GetValue<string>() == "people/" + person.Id + ".yaml")!.AsObject();
        Assert.Equal(["Path", "Before", "After"], entry.Select(p => p.Key));
        Assert.Null(entry["Before"]); // A file that did not exist before; "" would mean an empty file.
        Assert.Equal(File.ReadAllBytes(repo.SafePath("people/" + person.Id + ".yaml")), Convert.FromBase64String(entry["After"]!.GetValue<string>()));
    }

    [Fact]
    public async Task JournalOfAnInterruptedWriteIsRecovered()
    {
        var repo = new TravelRepository(Path.Combine(root, "trip"), Path.Combine(root, "recovery")); await repo.InitializeAsync(Entity.CreateTrip("Spring in Japan"));
        var before = File.ReadAllBytes(repo.SafePath("travel.yaml")); var after = Encoding.UTF8.GetBytes("interrupted: true\n");
        File.WriteAllBytes(repo.SafePath("travel.yaml"), after);
        Directory.CreateDirectory(repo.RecoveryRoot); var journal = Path.Combine(repo.RecoveryRoot, Guid.NewGuid() + ".json");
        Directory.CreateDirectory(repo.SafePath("people")); File.WriteAllBytes(repo.SafePath("people/new.yaml"), after);
        File.WriteAllText(journal, $$"""[{"Path":"travel.yaml","Before":"{{Convert.ToBase64String(before)}}","After":"{{Convert.ToBase64String(after)}}"},{"Path":"people/new.yaml","Before":null,"After":"{{Convert.ToBase64String(after)}}"}]""");
        await repo.RecoverAsync();
        Assert.Equal(before, File.ReadAllBytes(repo.SafePath("travel.yaml"))); Assert.False(File.Exists(repo.SafePath("people/new.yaml")));
        Assert.True(File.Exists(journal + ".recovered"));
    }

    [Fact]
    public void WeatherCacheFilesKeepTheirFormat()
    {
        var cache = new WeatherCache(root); var tokyo = new GeoPoint(35.6812, 139.7671);
        var fetched = Instant.FromUtc(2026, 4, 2, 0, 0); var hour = Instant.FromUtc(2026, 4, 2, 3, 0);
        cache.Store(new WeatherForecast("open-meteo", tokyo, fetched, fetched + Duration.FromHours(6), [new WeatherHour(hour, 1, 14.5, 20, 0.2, 12, null, WeatherCondition.PartlyCloudy)]));
        Assert.Equal($$"""{"Fetched":{{fetched.ToUnixTimeSeconds()}},"Expires":{{(fetched + Duration.FromHours(6)).ToUnixTimeSeconds()}},"Hours":[{"T":{{hour.ToUnixTimeSeconds()}},"H":1,"C":14.5,"P":20,"Mm":0.2,"W":12,"G":null,"K":1}]}""",
            File.ReadAllText(Path.Combine(root, "forecast", "open-meteo", "35.6812_139.7671.json")));
        Assert.Equal(14.5, Assert.Single(cache.Forecast("open-meteo", tokyo)!.Hours).TemperatureC);

        cache.Store(tokyo, [new ClimateDay(new LocalDate(2026, 4, 2), 19, 9.5, 0.3, 3.1, 30)]);
        Assert.Equal("""[{"D":"2026-04-02","Max":19,"Min":9.5,"Rain":0.3,"Mm":3.1,"Y":30}]""", File.ReadAllText(Path.Combine(root, "climate", "35.6812_139.7671.json")));
        Assert.Equal(9.5, cache.Climate(tokyo)[new LocalDate(2026, 4, 2)].MinC);
    }

    [Fact]
    public void PluginManifestNamesIgnoreCase()
    {
        var manifest = PluginManifest.Parse("""{"Id":"org.example.walking","VERSION":"1.0.0","minimumApiVersion":1,"entryAssembly":"Walking.dll","Capabilities":["commands"],"permissions":["network"],"Name":"Walking"}""");
        Assert.Equal("org.example.walking", manifest.Id); Assert.Equal("1.0.0", manifest.Version); Assert.Equal(["commands"], manifest.Capabilities); Assert.Equal(["network"], manifest.Permissions); Assert.Equal("Walking", manifest.DisplayName);
    }

    [Fact]
    public void SchemaErrorsAreReportedAsEvaluationResults()
    {
        var entity = YamlCodec.Read("schema: travelrepo/1.0\ntype: person\nid: not-a-uuid\ntitle: Alex\n");
        var message = JsonNode.Parse(Assert.Single(SchemaValidation.Validate(entity)).Message)!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(SchemaErrorsReference), message), message.ToJsonString());
    }

    // Written by TravelRepo 0.13.1 with reflection-based System.Text.Json.
    private const string SchemaErrorsReference = """
        {"valid":false,"details":[{"valid":false,"evaluationPath":"","schemaLocation":"https://travelrepo.org/schema/1.0/person.json#","instanceLocation":"","errors":{"required":"Required properties [\u0022display_name\u0022] are not present"}},{"valid":false,"evaluationPath":"/properties/id","schemaLocation":"https://travelrepo.org/schema/1.0/person.json#/properties/id","instanceLocation":"/id","errors":{"pattern":"The string value is not a match for the indicated regular expression"}},{"valid":true,"evaluationPath":"/properties/type","schemaLocation":"https://travelrepo.org/schema/1.0/person.json#/properties/type","instanceLocation":"/type"},{"valid":true,"evaluationPath":"/additionalProperties","schemaLocation":"https://travelrepo.org/schema/1.0/person.json#/additionalProperties","instanceLocation":"/schema"},{"valid":true,"evaluationPath":"/additionalProperties","schemaLocation":"https://travelrepo.org/schema/1.0/person.json#/additionalProperties","instanceLocation":"/title"}]}
        """;
}
