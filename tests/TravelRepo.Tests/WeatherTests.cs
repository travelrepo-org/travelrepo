using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using NodaTime;
using TravelRepo.Core;
using TravelRepo.Providers;
using TravelRepo.Providers.Weather;
using Xunit;
namespace TravelRepo.Tests;

public sealed class WeatherTests : IDisposable
{
    private const string Zone = "Asia/Tokyo";
    private readonly string cacheRoot = Path.Combine(Path.GetTempPath(), "travelrepo-weather-" + Guid.NewGuid());
    public void Dispose() => TestFiles.Delete(cacheRoot);

    // 8:00 on 10 November 2026 in Japan.
    private static readonly Instant Now = Instant.FromUtc(2026, 11, 9, 23, 0);
    private sealed class Clock(Instant now) : IClock { public Instant Now { get; set; } = now; public Instant GetCurrentInstant() => Now; }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> action) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Urls.Add(request.RequestUri!.AbsoluteUri); return Task.FromResult(action(request)); }
    }
    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private sealed class Secrets(string? key) : ISecretStore
    {
        public Task<string?> ReadAsync(string k, CancellationToken ct = default) => Task.FromResult(key);
        public Task WriteAsync(string k, string value, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(string k, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static Entity Place(string name, double lat, double lon)
    {
        var place = Entity.Create("place", name); place.Data["location"] = new JsonObject { ["latitude"] = lat, ["longitude"] = lon }; return place;
    }
    private static Entity Item(string title, string start, string end, Entity? place = null, string? category = null)
    {
        var item = Entity.Create("schedule_item", title); item.Data["status"] = "planned";
        item.Data["time"] = new JsonObject { ["precision"] = "exact", ["start"] = new ZonedTime(start, Zone).ToJson(), ["end"] = new ZonedTime(end, Zone).ToJson() };
        if (place is not null) item.Data["default_place"] = place.Id.ToString();
        if (category is not null) item.Data["category"] = category;
        return item;
    }
    private static TripSnapshot Trip(params Entity[] entities)
    {
        var manifest = Entity.CreateTrip("Autumn in Japan", "en", Zone);
        manifest.Data["dates"] = new JsonObject { ["start"] = "2026-11-10", ["end"] = "2026-12-20" };
        return new(manifest, entities.ToDictionary(e => e.Id));
    }

    // Outdoor estimates ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Bootstour auf dem See", "activity", Exposure.Outdoor, ExposureSource.Title)]
    [InlineData("Schnorcheln und Schwimmen in der Bucht", "activity", Exposure.Outdoor, ExposureSource.Title)]
    [InlineData("Ice caves", "sightseeing", Exposure.Indoor, ExposureSource.Title)]
    [InlineData("Film studio tour", "culture", Exposure.Indoor, ExposureSource.Category)]
    [InlineData("Castle grounds", "culture", Exposure.Possibly, ExposureSource.Title)]
    [InlineData("Old town bridge", "sightseeing", Exposure.Outdoor, ExposureSource.Category)]
    [InlineData("Gletscherwanderung am Nordhang", null, Exposure.Outdoor, ExposureSource.Title)]
    [InlineData("Evening at the hot springs", "activity", Exposure.Possibly, ExposureSource.Category)]
    [InlineData("Night market", "food", Exposure.Indoor, ExposureSource.Category)]
    [InlineData("Rental car pick-up", "other", Exposure.Indoor, ExposureSource.Category)]
    [InlineData("Parkhaus am Flughafen", null, Exposure.Possibly, ExposureSource.Category)]
    [InlineData("City museum and botanic garden", null, Exposure.Possibly, ExposureSource.Title)]
    public void OutdoorIsEstimatedFromCategoryAndTitle(string title, string? category, Exposure expected, ExposureSource source)
    {
        var item = Entity.Create("schedule_item", title); if (category is not null) item.Data["category"] = category;
        var estimate = OutdoorEstimates.For(Trip(item), item);
        Assert.Equal(expected, estimate.Exposure); Assert.Equal(source, estimate.Source);
    }

    [Fact]
    public void TheOutdoorFieldWinsAndIsInherited()
    {
        var day = Entity.Create("schedule_item", "Museum day"); var child = Entity.Create("schedule_item", "City museum");
        day.Data["outdoor"] = true; day.Data["children"] = new JsonArray(child.Id.ToString());
        var trip = Trip(day, child);
        Assert.Equal(new ExposureEstimate(Exposure.Outdoor, ExposureSource.Explicit), OutdoorEstimates.For(trip, day));
        Assert.Equal(new ExposureEstimate(Exposure.Outdoor, ExposureSource.Inherited), OutdoorEstimates.For(trip, child));
        child.Data["outdoor"] = false;
        Assert.Equal(new ExposureEstimate(Exposure.Indoor, ExposureSource.Explicit), OutdoorEstimates.For(trip, child));
    }

    [Theory]
    [InlineData("walking", Exposure.Outdoor, false)]
    [InlineData("bicycle", Exposure.Outdoor, false)]
    [InlineData("ferry", Exposure.Travel, true)]
    [InlineData("car", Exposure.Travel, false)]
    [InlineData("flight", Exposure.Travel, false)]
    public void JourneysDependOnHowYouTravel(string mode, Exposure expected, bool onWater)
    {
        var item = Entity.Create("schedule_item", "Harbour crossing"); item.Data["components"]!["transport"] = new JsonObject { ["type"] = mode };
        var estimate = OutdoorEstimates.For(Trip(item), item);
        Assert.Equal(expected, estimate.Exposure); Assert.Equal(onWater, estimate.OnWater); Assert.Equal(ExposureSource.Transport, estimate.Source);
    }

    [Fact]
    public void StaysAreIndoors()
    {
        var stay = Entity.Create("schedule_item", "Mountain inn"); stay.Data["components"]!["accommodation"] = new JsonObject();
        Assert.Equal(Exposure.Indoor, OutdoorEstimates.For(Trip(stay), stay).Exposure);
    }

    // Conditions and sun --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, WeatherCondition.Clear)]
    [InlineData(2, WeatherCondition.PartlyCloudy)]
    [InlineData(45, WeatherCondition.Fog)]
    [InlineData(53, WeatherCondition.Drizzle)]
    [InlineData(63, WeatherCondition.Rain)]
    [InlineData(65, WeatherCondition.HeavyRain)]
    [InlineData(73, WeatherCondition.Snow)]
    [InlineData(95, WeatherCondition.Thunderstorm)]
    public void WmoCodesMapToConditions(int code, WeatherCondition expected) => Assert.Equal(expected, WeatherMath.FromWmo(code));

    [Theory]
    [InlineData("partlycloudy_night", WeatherCondition.PartlyCloudy)]
    [InlineData("heavyrainshowers_day", WeatherCondition.HeavyRain)]
    [InlineData("lightrain", WeatherCondition.Drizzle)]
    [InlineData("rainandthunder", WeatherCondition.Thunderstorm)]
    [InlineData("Chance Rain Showers", WeatherCondition.Rain)]
    [InlineData("Mostly Sunny", WeatherCondition.PartlyCloudy)]
    [InlineData("clear-day", WeatherCondition.Clear)]
    [InlineData("snow-showers-day", WeatherCondition.Snow)]
    [InlineData("fair_day", WeatherCondition.PartlyCloudy)]
    public void ServiceTextsMapToConditions(string text, WeatherCondition expected) => Assert.Equal(expected, WeatherMath.FromText(text));

    [Fact]
    public void SunriseAndSunsetMatchPublishedTimes()
    {
        var berlin = DateTimeZoneProviders.Tzdb["Europe/Berlin"];
        var (rise, set) = Sun.Times(new GeoPoint(52.52, 13.405), new LocalDate(2026, 6, 21), berlin);
        Assert.InRange((rise!.Value.InZone(berlin).TimeOfDay - new LocalTime(4, 43)).ToDuration().TotalMinutes, -3, 3);
        Assert.InRange((set!.Value.InZone(berlin).TimeOfDay - new LocalTime(21, 33)).ToDuration().TotalMinutes, -3, 3);
        var tokyo = DateTimeZoneProviders.Tzdb[Zone];
        (rise, set) = Sun.Times(new GeoPoint(35.7, 139.75), new LocalDate(2026, 10, 19), tokyo); // as Open-Meteo gives it
        Assert.InRange((rise!.Value.InZone(tokyo).TimeOfDay - new LocalTime(5, 50)).ToDuration().TotalMinutes, -3, 3);
        Assert.InRange((set!.Value.InZone(tokyo).TimeOfDay - new LocalTime(17, 1)).ToDuration().TotalMinutes, -3, 3);
        Assert.Equal((null, null), Sun.Times(new GeoPoint(69.65, 18.96), new LocalDate(2026, 6, 21), DateTimeZoneProviders.Tzdb["Europe/Oslo"]));
    }

    // Services ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task OpenMeteoReadsOneOrSeveralPointsInOneRequest()
    {
        static string Point(double lat, int code) => "{\"latitude\":" + lat.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"longitude\":168.68,\"hourly\":{\"time\":[1794250800,1794254400],\"temperature_2m\":[17.5,18.4],\"precipitation_probability\":[10,80],\"precipitation\":[0.0,2.5],\"weather_code\":[2," + code + "],\"wind_speed_10m\":[12.1,15.1],\"wind_gusts_10m\":[40.7,61.0]}}";
        var handler = new Handler(r => Json(r.RequestUri!.Query.Contains("latitude=36.74,35.68") ? "[" + Point(36.74, 63) + "," + Point(35.68, 95) + "]" : Point(36.74, 63)));
        var service = new OpenMeteo(new HttpClient(handler), "Test/1", new Clock(Now));
        var one = Assert.Single(await service.ForecastAsync([new GeoPoint(36.74, 139.5)]));
        Assert.Equal(2, one!.Hours.Count); Assert.Equal(WeatherCondition.Rain, one.Hours[1].Condition); Assert.Equal(80, one.Hours[1].PrecipitationChance); Assert.Equal(61.0, one.Hours[1].GustKmh);
        Assert.Equal(Now + Duration.FromHours(2), one.Expires);
        var two = await service.ForecastAsync([new GeoPoint(36.74, 139.5), new GeoPoint(35.68, 139.77)]);
        Assert.Equal(WeatherCondition.Thunderstorm, two[1]!.Hours[1].Condition);
        Assert.Equal(2, handler.Urls.Count); Assert.All(handler.Urls, u => Assert.Contains("timezone=GMT", u));
    }

    [Fact]
    public async Task OpenMeteoClimateAveragesTheLastTenYearsAroundEachDate()
    {
        var handler = new Handler(r =>
        {
            var query = System.Web.HttpUtility.ParseQueryString(r.RequestUri!.Query);
            var start = LocalDate.FromDateTime(DateTime.Parse(query["start_date"]!, System.Globalization.CultureInfo.InvariantCulture));
            var end = LocalDate.FromDateTime(DateTime.Parse(query["end_date"]!, System.Globalization.CultureInfo.InvariantCulture));
            var dates = new List<string>(); var max = new List<double>(); var min = new List<double>(); var rain = new List<double>();
            // Every second year rains every day; highs are 20 + (year - 2016).
            for (var d = start; d <= end; d = d.PlusDays(1)) { dates.Add("\"" + d.ToString("yyyy-MM-dd", null) + "\""); max.Add(20 + d.Year - 2016); min.Add(8); rain.Add(d.Year % 2 == 0 ? 5 : 0); }
            return Json("{\"daily\":{\"time\":[" + string.Join(",", dates) + "],\"temperature_2m_max\":[" + string.Join(",", max) + "],\"temperature_2m_min\":[" + string.Join(",", min) + "],\"precipitation_sum\":[" + string.Join(",", rain) + "]}}");
        });
        var service = new OpenMeteo(new HttpClient(handler), "Test/1", new Clock(Now));
        var point = new GeoPoint(36.7, 139.5);
        var result = await service.ClimateAsync([new ClimateRequest(point, new LocalDate(2026, 12, 18), new LocalDate(2026, 12, 20))]);
        var days = result[point];
        Assert.Equal(3, days.Count); Assert.All(days, d => Assert.Equal(10, d.Years));
        Assert.Equal(new LocalDate(2026, 12, 18), days[0].Date);
        Assert.Equal(24.5, days[0].MaxC); Assert.Equal(8, days[0].MinC); Assert.Equal(0.5, days[0].RainDayShare); Assert.Equal(2.5, days[0].PrecipitationMm);
        Assert.Equal(10, handler.Urls.Count);
        Assert.Contains("start_date=2016-12-15", handler.Urls[^1]); Assert.Contains("end_date=2016-12-23", handler.Urls[^1]);
    }

    [Fact]
    public async Task MetNorwayUsesHourlyStepsThenSixHourSteps()
    {
        const string body = """
        {"properties":{"timeseries":[
          {"time":"2026-11-09T19:00:00Z","data":{"instant":{"details":{"air_temperature":10.0,"wind_speed":5.0}},"next_1_hours":{"summary":{"symbol_code":"lightrain"},"details":{"precipitation_amount":0.4}},"next_6_hours":{"summary":{"symbol_code":"rain"},"details":{"precipitation_amount":3.0}}}},
          {"time":"2026-11-09T20:00:00Z","data":{"instant":{"details":{"air_temperature":9.5,"wind_speed":15.0}},"next_1_hours":{"summary":{"symbol_code":"heavyrain"},"details":{"precipitation_amount":4.2}}}},
          {"time":"2026-11-10T00:00:00Z","data":{"instant":{"details":{"air_temperature":8.0,"wind_speed":2.0}},"next_6_hours":{"summary":{"symbol_code":"cloudy"},"details":{"precipitation_amount":0.0}}}},
          {"time":"2026-11-18T00:00:00Z","data":{"instant":{"details":{"air_temperature":8.0,"wind_speed":2.0}}}}
        ]}}
        """;
        var handler = new Handler(r => { Assert.Equal("Test/1", r.Headers.UserAgent.ToString()); var response = Json(body); response.Content.Headers.Expires = Now.ToDateTimeOffset() + TimeSpan.FromMinutes(5); return response; });
        var forecast = Assert.Single(await new MetNorway(new HttpClient(handler), "Test/1", new Clock(Now)).ForecastAsync([new GeoPoint(36.74, 139.5)]))!;
        Assert.Equal([1, 1, 6], forecast.Hours.Select(h => h.Hours));
        Assert.Equal(54, forecast.Hours[1].WindKmh); Assert.Equal(WeatherCondition.HeavyRain, forecast.Hours[1].Condition); Assert.Null(forecast.Hours[0].PrecipitationChance);
        Assert.Equal(Now + Duration.FromMinutes(30), forecast.Expires); // at least half an hour, whatever Expires says
        Assert.Contains("lat=36.74&lon=139.5", handler.Urls[0]);
    }

    [Fact]
    public async Task TheNationalWeatherServiceOnlyAnswersForTheUnitedStates()
    {
        var handler = new Handler(r => r.RequestUri!.AbsolutePath.StartsWith("/points/", StringComparison.Ordinal)
            ? r.RequestUri.AbsolutePath.Contains("21.3") ? Json("{}", HttpStatusCode.NotFound) : Json("""{"properties":{"forecastHourly":"https://api.weather.gov/gridpoints/TOP/32,81/forecast/hourly"}}""")
            : Json("""{"properties":{"periods":[{"startTime":"2026-11-09T13:00:00-06:00","temperature":7,"temperatureUnit":"C","probabilityOfPrecipitation":{"value":60},"windSpeed":"20 km/h","shortForecast":"Chance Rain Showers"},{"startTime":"2026-11-09T14:00:00-06:00","temperature":50,"temperatureUnit":"F","probabilityOfPrecipitation":{"value":20},"windSpeed":"10 mph","shortForecast":"Mostly Sunny"}]}}"""));
        var service = new NationalWeatherService(new HttpClient(handler), "Test/1", new Clock(Now));
        var result = await service.ForecastAsync([new GeoPoint(39.75, -97.09), new GeoPoint(36.74, 139.5), new GeoPoint(21.3, -157.85)]);
        Assert.Equal(WeatherCondition.Rain, result[0]!.Hours[0].Condition); Assert.Equal(10, result[0]!.Hours[1].TemperatureC); Assert.Equal(16.1, result[0]!.Hours[1].WindKmh);
        Assert.Equal(Instant.FromUtc(2026, 11, 9, 19, 0), result[0]!.Hours[0].Time);
        Assert.Null(result[1]); Assert.Null(result[2]);
        Assert.Equal(3, handler.Urls.Count); // points far outside the US are not even asked
        Assert.EndsWith("?units=si", handler.Urls[1]);
    }

    [Fact]
    public async Task VisualCrossingNeedsAnAcceptedKey()
    {
        var status = HttpStatusCode.OK;
        var handler = new Handler(r => status == HttpStatusCode.OK
            ? Json("""{"days":[{"hours":[{"datetimeEpoch":1794250800,"temp":17.2,"precip":5.1,"precipprob":90,"windspeed":20,"windgust":45,"icon":"rain"}]}]}""")
            : Json("Invalid API key", status));
        var point = new GeoPoint(36.74, 139.5);
        var missing = await Assert.ThrowsAsync<DomainException>(() => new VisualCrossing(new HttpClient(handler), new Secrets(null)).ForecastAsync([point]));
        Assert.Equal("weather.key", missing.Code); Assert.Empty(handler.Urls);
        var service = new VisualCrossing(new HttpClient(handler), new Secrets("abc 123"), "Test/1", new Clock(Now));
        var hour = Assert.Single(Assert.Single(await service.ForecastAsync([point]))!.Hours);
        Assert.Equal(WeatherCondition.HeavyRain, hour.Condition); Assert.Equal(90, hour.PrecipitationChance);
        Assert.Contains("timeline/36.74,139.5/next15days", handler.Urls[0]); Assert.Contains("key=abc%20123", handler.Urls[0]);
        status = HttpStatusCode.Unauthorized;
        Assert.Equal("weather.key", (await Assert.ThrowsAsync<DomainException>(() => service.ForecastAsync([point]))).Code);
        status = HttpStatusCode.TooManyRequests;
        Assert.Equal("weather.unavailable", (await Assert.ThrowsAsync<DomainException>(() => service.ForecastAsync([point]))).Code);
    }

    // Advice --------------------------------------------------------------------------------------------------------

    private static WeatherHour Hour(int hour, double rain = 0, int chance = 0, double gust = 20, double temperature = 18, WeatherCondition condition = WeatherCondition.Clear) =>
        new(Now + Duration.FromHours(hour), 1, temperature, chance, rain, gust / 2, gust, condition);

    [Fact]
    public void ConcernsDependOnExposure()
    {
        WeatherHour[] showers = [Hour(0, 1.5, 70, 55, condition: WeatherCondition.Rain), Hour(1, 0.5, 60, 40, condition: WeatherCondition.Drizzle)];
        Assert.Equal([ConcernKind.Wind, ConcernKind.Rain], WeatherAdvice.Concerns(new(Exposure.Outdoor, ExposureSource.Category), showers).Select(c => c.Kind));
        Assert.Empty(WeatherAdvice.Concerns(new(Exposure.Possibly, ExposureSource.Category), showers));
        Assert.Empty(WeatherAdvice.Concerns(new(Exposure.Indoor, ExposureSource.Category), [Hour(0, 30, 100, 120, condition: WeatherCondition.Thunderstorm)]));
        // Unlikely rain is not a concern, even outdoors.
        Assert.Empty(WeatherAdvice.Concerns(new(Exposure.Outdoor, ExposureSource.Category), [Hour(0, 1.5, 20, condition: WeatherCondition.Rain)]));

        WeatherHour[] downpour = [Hour(0, 6, 90, condition: WeatherCondition.HeavyRain), Hour(1, 5, 90, condition: WeatherCondition.HeavyRain)];
        var rain = Assert.Single(WeatherAdvice.Concerns(new(Exposure.Possibly, ExposureSource.Category), downpour));
        Assert.True(rain.Severe); Assert.Equal(11, rain.Value);

        WeatherHour[] windy = [Hour(0, gust: 65)];
        Assert.Empty(WeatherAdvice.Concerns(new(Exposure.Travel, ExposureSource.Transport), windy));
        Assert.Equal(ConcernKind.Wind, Assert.Single(WeatherAdvice.Concerns(new(Exposure.Travel, ExposureSource.Transport, OnWater: true), windy)).Kind);
        Assert.Equal(ConcernKind.Thunderstorm, WeatherAdvice.Concerns(new(Exposure.Travel, ExposureSource.Transport), [Hour(0, condition: WeatherCondition.Thunderstorm)])[0].Kind);
        Assert.Equal([ConcernKind.Heat], WeatherAdvice.Concerns(new(Exposure.Outdoor, ExposureSource.Category), [Hour(0, temperature: 31)]).Select(c => c.Kind));
    }

    // The trip service ----------------------------------------------------------------------------------------------

    private sealed class FakeService(string id, int horizon, Func<GeoPoint, bool>? covers = null) : IWeatherProvider
    {
        public int Calls; public Exception? Failure; public Func<GeoPoint, Instant, WeatherHour[]> Hours { get; set; } = (_, from) => [.. Enumerable.Range(0, horizon * 24).Select(i => new WeatherHour(from + Duration.FromHours(i), 1, 15, 10, 0, 10, 20, WeatherCondition.PartlyCloudy))];
        public IClock Clock { get; set; } = new Clock(Now);
        public string Id => id; public string Name => id; public WeatherAttribution Attribution => new(id + " data", "https://example.invalid/" + id, null);
        public bool RequiresKey => false; public int HorizonDays => horizon;
        public bool Covers(GeoPoint point) => covers?.Invoke(point) ?? true;
        public Task<IReadOnlyList<WeatherForecast?>> ForecastAsync(IReadOnlyList<GeoPoint> points, CancellationToken ct = default)
        {
            Calls++; if (Failure is not null) throw Failure;
            var now = Clock.GetCurrentInstant(); var start = Instant.FromUnixTimeSeconds(now.ToUnixTimeSeconds() / 86400 * 86400) - Duration.FromHours(12);
            return Task.FromResult<IReadOnlyList<WeatherForecast?>>(points.Select(p => covers?.Invoke(p) == false ? null : new WeatherForecast(id, p, now, now + Duration.FromHours(2), Hours(p, start))).ToArray());
        }
    }
    private sealed class FakeClimate : IClimateSource
    {
        public List<ClimateRequest> Requests { get; } = [];
        public WeatherAttribution Attribution => new("climate data", "https://example.invalid/climate", null);
        public Task<IReadOnlyDictionary<GeoPoint, IReadOnlyList<ClimateDay>>> ClimateAsync(IReadOnlyList<ClimateRequest> requests, CancellationToken ct = default)
        {
            Requests.AddRange(requests);
            return Task.FromResult<IReadOnlyDictionary<GeoPoint, IReadOnlyList<ClimateDay>>>(requests.ToDictionary(r => r.Point, r => (IReadOnlyList<ClimateDay>)Enumerable.Range(0, Period.DaysBetween(r.From, r.To) + 1).Select(i => new ClimateDay(r.From.PlusDays(i), 21, 9, 0.3, 2.1, 10)).ToArray()));
        }
    }

    [Fact]
    public async Task TheTripGetsForecastsNearbyAndClimateFurtherAhead()
    {
        var village = Place("Mountain village", 36.738, 139.501); var pier = Place("Lake pier", 35.205, 139.025); var city = Place("City tower", 35.6586, 139.7454);
        var hike = Item("Ridge hike", "2026-11-11T09:00:00", "2026-11-11T15:00:00", village, "nature");
        var museum = Item("Museum", "2026-11-11T16:00:00", "2026-11-11T17:00:00", village, "culture");
        var cruise = Item("Bootstour auf dem See", "2026-11-12T11:00:00", "2026-11-12T13:00:00", pier, "activity");
        var later = Item("Tower view", "2026-12-18T10:00:00", "2026-12-18T12:00:00", city, "sightseeing");
        var openMeteo = new FakeService("open-meteo", 16); var climate = new FakeClimate();
        // Heavy rain in the mountains tomorrow from 10:00 to 13:00.
        openMeteo.Hours = (p, from) => [.. Enumerable.Range(0, 16 * 24).Select(i => { var at = from + Duration.FromHours(i); var wet = p.Latitude > 36.5 && at >= Instant.FromUtc(2026, 11, 11, 1, 0) && at < Instant.FromUtc(2026, 11, 11, 4, 0); return new WeatherHour(at, 1, 14, wet ? 90 : 5, wet ? 4.5 : 0, 15, 30, wet ? WeatherCondition.HeavyRain : WeatherCondition.Clear); })];
        var service = new WeatherService([openMeteo], climate, new WeatherCache(cacheRoot), clock: new Clock(Now));
        var trip = Trip(village, pier, city, hike, museum, cruise, later);

        var weather = await service.UpdateAsync(trip);
        Assert.Empty(weather.Problems);
        var hikeWeather = weather.Items[hike.Id];
        Assert.Equal(OutlookKind.Forecast, Assert.Single(hikeWeather.Outlooks).Kind);
        Assert.Equal(6, hikeWeather.Outlooks[0].Hours.Count);
        var rain = Assert.Single(hikeWeather.Concerns); Assert.Equal(ConcernKind.Rain, rain.Kind); Assert.True(rain.Severe); Assert.Equal(13.5, rain.Value);
        Assert.Empty(weather.Items[museum.Id].Concerns);
        Assert.Equal(OutlookKind.Forecast, weather.Items[cruise.Id].Outlooks[0].Kind);
        var climateOutlook = Assert.Single(weather.Items[later.Id].Outlooks);
        Assert.Equal(OutlookKind.Climate, climateOutlook.Kind); Assert.Equal(21, climateOutlook.Climate!.MaxC); Assert.NotNull(climateOutlook.Climate.Sunrise);
        // Only the far item needed climate averages, for its rounded point.
        Assert.Equal(new GeoPoint(35.66, 139.75), Assert.Single(climate.Requests, r => r.Point.Latitude is > 35.5 and < 35.8).Point);
        Assert.True(HeavyRainDay(weather.Days[new LocalDate(2026, 11, 11)]));
        Assert.Equal(village.Id, weather.Days[new LocalDate(2026, 11, 11)].Place);
        Assert.Equal(pier.Id, weather.Days[new LocalDate(2026, 11, 12)].Place);
        Assert.Contains(weather.Sources, s => s.Text == "open-meteo data"); Assert.Contains(weather.Sources, s => s.Text == "climate data");

        var reminder = Assert.Single(WeatherAdvice.Reminders(trip, weather, Now));
        Assert.Equal(ReminderKind.WeatherWarning, reminder.Kind); Assert.Equal(hike.Id, reminder.Entity); Assert.True(reminder.Urgent);
        Assert.Equal("weather:" + hike.Id + ":2026-11-11:rain", reminder.Key);

        // Within the cache's lifetime, nothing is fetched again; the cache alone gives the same answer.
        await service.UpdateAsync(trip); Assert.Equal(1, openMeteo.Calls);
        Assert.Equal(weather.Items.Keys.Order(), service.Cached(trip).Items.Keys.Order());
        Assert.Single(climate.Requests, r => r.Point.Latitude is > 35.5 and < 35.8);
    }

    private static bool HeavyRainDay(DayWeather day) => day.Outlook.Day!.Condition == WeatherCondition.HeavyRain;

    [Fact]
    public async Task TheChosenServiceFallsBackToOpenMeteoWhereItHasNoForecast()
    {
        var tokyo = Place("Tokyo", 35.68, 139.77); var honolulu = Place("Honolulu", 21.307, -157.858);
        var walk = Item("Park walk", "2026-11-11T09:00:00", "2026-11-11T11:00:00", tokyo, "nature");
        var beach = Item("Waikiki beach", "2026-11-11T12:00:00", "2026-11-11T14:00:00", honolulu, "nature");
        var tenDays = Item("Hike in ten days", "2026-11-20T09:00:00", "2026-11-20T12:00:00", honolulu, "nature");
        var openMeteo = new FakeService("open-meteo", 16); var nws = new FakeService("nws", 6, p => p.Longitude < 0);
        var clock = new Clock(Now);
        var service = new WeatherService([openMeteo, nws], null, new WeatherCache(cacheRoot), "nws", clock);
        var weather = await service.UpdateAsync(Trip(tokyo, honolulu, walk, beach, tenDays));
        Assert.Equal("nws", weather.Items[beach.Id].Outlooks[0].Provider);
        Assert.Equal("open-meteo", weather.Items[walk.Id].Outlooks[0].Provider);
        // Beyond the chosen service's six days, Open-Meteo's trend.
        Assert.Equal(("open-meteo", OutlookKind.Trend), (weather.Items[tenDays.Id].Outlooks[0].Provider, weather.Items[tenDays.Id].Outlooks[0].Kind));
        Assert.Empty(weather.Items[tenDays.Id].Outlooks[0].Hours);

        // When the chosen service fails, Open-Meteo steps in and the problem is reported.
        clock.Now = Now + Duration.FromHours(3); nws.Clock = clock; openMeteo.Clock = clock; nws.Failure = new DomainException("weather.unavailable", "down");
        weather = await service.UpdateAsync(Trip(tokyo, honolulu, walk, beach, tenDays));
        Assert.Equal("weather.unavailable", Assert.Single(weather.Problems).Code);
        Assert.Equal("open-meteo", weather.Items[beach.Id].Outlooks[0].Provider);
    }

    [Fact]
    public async Task OldForecastsAreNotShown()
    {
        var tokyo = Place("Tokyo", 35.68, 139.77); var walk = Item("Park walk", "2026-11-11T09:00:00", "2026-11-11T11:00:00", tokyo, "nature");
        var openMeteo = new FakeService("open-meteo", 16); var clock = new Clock(Now);
        var service = new WeatherService([openMeteo], null, new WeatherCache(cacheRoot), clock: clock);
        var trip = Trip(tokyo, walk);
        await service.UpdateAsync(trip);
        clock.Now = Now + Duration.FromHours(25);
        Assert.Empty(service.Cached(trip).Items);
        openMeteo.Failure = new DomainException("weather.unavailable", "offline");
        var weather = await service.UpdateAsync(trip);
        Assert.Empty(weather.Items); Assert.Single(weather.Problems);
    }
}
