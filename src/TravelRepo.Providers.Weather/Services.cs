using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using NodaTime;
using NodaTime.Text;
using TravelRepo.Core;

namespace TravelRepo.Providers.Weather;

/// <summary>HTTP plumbing shared by the weather services: an identifying User-Agent and plain error reporting.</summary>
internal static class WeatherHttp
{
    public static string DefaultUserAgent => "TravelRepo/" + TravelRepoInfo.Version + " (+https://github.com/travelrepo-org/travelrepo)";

    public static async Task<(JsonNode? Body, HttpResponseMessage Response)> GetAsync(HttpClient http, string userAgent, string url, CancellationToken ct, string accept = "application/json")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(userAgent); request.Headers.Accept.ParseAdd(accept);
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, ct); }
        catch (HttpRequestException ex) { throw new DomainException("weather.unavailable", "The weather service could not be reached: " + ex.Message); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new DomainException("weather.unavailable", "The weather service did not answer in time."); }
        var text = await response.Content.ReadAsStringAsync(ct);
        JsonNode? body = null; try { body = text.Length > 0 ? JsonNode.Parse(text) : null; } catch (System.Text.Json.JsonException) { }
        return (body, response);
    }

    public static DomainException Failure(HttpResponseMessage response, JsonNode? body, string service, bool keyed = false)
    {
        if (keyed && response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return new DomainException("weather.key", service + " did not accept the API key.");
        var reason = body is JsonObject o ? (o["reason"] ?? o["detail"] ?? o["message"])?.ToString() : null;
        return new DomainException("weather.unavailable", service + " answered " + (int)response.StatusCode + (reason is { Length: > 0 } ? ": " + reason : "."));
    }

    public static double? Number(JsonNode? node) => node is JsonValue v && double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    public static Instant Expires(HttpResponseMessage response, Instant now, Duration fallback, Duration minimum)
    {
        var expires = response.Content.Headers.Expires is { } e ? Instant.FromDateTimeOffset(e) : now + fallback;
        return expires < now + minimum ? now + minimum : expires;
    }
}

/// <summary>
/// Open-Meteo (open-meteo.com): global forecasts for 16 days from national weather services' models, free without a
/// key for non-commercial use, and climate averages from its historical weather archive. Data is CC BY 4.0.
/// </summary>
public sealed class OpenMeteo(HttpClient http, string? userAgent = null, IClock? clock = null) : IWeatherProvider, IClimateSource
{
    public const string ProviderId = "open-meteo";
    /// <summary>How many past years the climate averages use.</summary>
    public const int ClimateYears = 10;
    private readonly string agent = userAgent ?? WeatherHttp.DefaultUserAgent;
    private readonly IClock time = clock ?? SystemClock.Instance;
    public string Id => ProviderId;
    public string Name => "Open-Meteo";
    public WeatherAttribution Attribution { get; } = new("Weather data by Open-Meteo.com", "https://open-meteo.com/", "CC BY 4.0");
    public bool RequiresKey => false;
    public int HorizonDays => 16;
    public bool Covers(GeoPoint point) => true;

    /// <summary>One request for all points.</summary>
    public async Task<IReadOnlyList<WeatherForecast?>> ForecastAsync(IReadOnlyList<GeoPoint> points, CancellationToken ct = default)
    {
        if (points.Count == 0) return [];
        var url = "https://api.open-meteo.com/v1/forecast?" + Coordinates(points)
            + "&hourly=temperature_2m,precipitation_probability,precipitation,weather_code,wind_speed_10m,wind_gusts_10m&timeformat=unixtime&timezone=GMT&forecast_days=" + HorizonDays;
        var (body, response) = await WeatherHttp.GetAsync(http, agent, url, ct);
        using (response) if (!response.IsSuccessStatusCode || body is null) throw WeatherHttp.Failure(response, body, Name);
        var now = time.GetCurrentInstant(); var results = Results(body, points.Count);
        return points.Select((p, i) =>
        {
            var hourly = results[i]?["hourly"];
            var times = hourly?["time"] as JsonArray ?? [];
            double? At(string key, int index) => WeatherHttp.Number((hourly?[key] as JsonArray)?[index]);
            var hours = times.Select((t, index) => WeatherHttp.Number(t) is { } seconds && At("weather_code", index) is { } code
                ? new WeatherHour(Instant.FromUnixTimeSeconds((long)seconds), 1, At("temperature_2m", index), (int?)At("precipitation_probability", index), At("precipitation", index), At("wind_speed_10m", index), At("wind_gusts_10m", index), WeatherMath.FromWmo((int)code))
                : null).OfType<WeatherHour>().ToArray();
            return (WeatherForecast?)new WeatherForecast(Id, p, now, now + Duration.FromHours(2), hours);
        }).ToArray();
    }

    /// <summary>
    /// Averages over the last <see cref="ClimateYears"/> complete years of the archive, from the same calendar days and
    /// three days either side. All points go into one request per year.
    /// </summary>
    public async Task<IReadOnlyDictionary<GeoPoint, IReadOnlyList<ClimateDay>>> ClimateAsync(IReadOnlyList<ClimateRequest> requests, CancellationToken ct = default)
    {
        var result = new Dictionary<GeoPoint, IReadOnlyList<ClimateDay>>();
        if (requests.Count == 0) return result;
        var from = requests.Min(r => r.From).PlusDays(-3); var to = requests.Max(r => r.To).PlusDays(3);
        var latest = time.GetCurrentInstant().InUtc().Date.PlusDays(-7); // the archive lags a few days behind
        var points = requests.Select(r => r.Point).Distinct().ToArray();
        var samples = points.ToDictionary(p => p, _ => new Dictionary<(int Month, int Day), List<(double Max, double Min, double Rain)>>());
        var years = 0;
        for (var back = 1; years < ClimateYears && back <= ClimateYears + 2; back++)
        {
            var start = from.PlusYears(-back); var end = start.PlusDays(Period.DaysBetween(from, to));
            if (end > latest) continue;
            var url = "https://archive-api.open-meteo.com/v1/archive?" + Coordinates(points) + "&start_date=" + LocalDatePattern.Iso.Format(start) + "&end_date=" + LocalDatePattern.Iso.Format(end)
                + "&daily=temperature_2m_max,temperature_2m_min,precipitation_sum&timezone=auto";
            var (body, response) = await WeatherHttp.GetAsync(http, agent, url, ct);
            using (response) if (!response.IsSuccessStatusCode || body is null) throw WeatherHttp.Failure(response, body, Name);
            var results = Results(body, points.Length);
            for (var i = 0; i < points.Length; i++)
            {
                var daily = results[i]?["daily"]; var dates = daily?["time"] as JsonArray ?? [];
                for (var d = 0; d < dates.Count; d++)
                {
                    if (!LocalDatePattern.Iso.Parse(dates[d]?.ToString() ?? "").TryGetValue(default, out var date)) continue;
                    if (WeatherHttp.Number((daily!["temperature_2m_max"] as JsonArray)?[d]) is not { } max || WeatherHttp.Number((daily["temperature_2m_min"] as JsonArray)?[d]) is not { } min) continue;
                    var rain = WeatherHttp.Number((daily["precipitation_sum"] as JsonArray)?[d]) ?? 0;
                    var key = (date.Month, date.Day); if (!samples[points[i]].TryGetValue(key, out var list)) samples[points[i]][key] = list = [];
                    list.Add((max, min, rain));
                }
            }
            years++;
        }
        foreach (var request in requests)
        {
            var days = new List<ClimateDay>();
            for (var date = request.From; date <= request.To; date = date.PlusDays(1))
            {
                // A week around the date: three days either side, in each year.
                var window = Enumerable.Range(-3, 7).Select(o => date.PlusDays(o)).SelectMany(d => samples[request.Point].GetValueOrDefault((d.Month, d.Day)) ?? []).ToArray();
                if (window.Length == 0) continue;
                days.Add(new ClimateDay(date, Math.Round(window.Average(s => s.Max), 1), Math.Round(window.Average(s => s.Min), 1), Math.Round(window.Count(s => s.Rain >= 1) / (double)window.Length, 2), Math.Round(window.Average(s => s.Rain), 1), years));
            }
            result[request.Point] = days;
        }
        return result;
    }

    private static string Coordinates(IReadOnlyCollection<GeoPoint> points) =>
        "latitude=" + string.Join(",", points.Select(p => p.Latitude.ToString("0.####", CultureInfo.InvariantCulture))) + "&longitude=" + string.Join(",", points.Select(p => p.Longitude.ToString("0.####", CultureInfo.InvariantCulture)));

    /// <summary>Open-Meteo answers with one object for one point and an array for several.</summary>
    private static JsonNode?[] Results(JsonNode body, int count) => body is JsonArray array ? [.. array] : count == 1 ? [body] : throw new DomainException("weather.unavailable", "Open-Meteo answered in an unexpected format.");
}

/// <summary>
/// MET Norway's Locationforecast (api.met.no): global forecasts for about nine days, strongest in the Nordic countries.
/// Hourly for the first two to three days, then in six-hour steps. Data is CC BY 4.0 (MET Norway); the terms ask for an
/// identifying User-Agent and for respecting the Expires header.
/// </summary>
public sealed class MetNorway(HttpClient http, string? userAgent = null, IClock? clock = null) : IWeatherProvider
{
    public const string ProviderId = "met-norway";
    private readonly string agent = userAgent ?? WeatherHttp.DefaultUserAgent;
    private readonly IClock time = clock ?? SystemClock.Instance;
    public string Id => ProviderId;
    public string Name => "MET Norway";
    public WeatherAttribution Attribution { get; } = new("Weather data from MET Norway", "https://api.met.no/", "CC BY 4.0");
    public bool RequiresKey => false;
    public int HorizonDays => 9;
    public bool Covers(GeoPoint point) => true;

    public async Task<IReadOnlyList<WeatherForecast?>> ForecastAsync(IReadOnlyList<GeoPoint> points, CancellationToken ct = default)
    {
        var result = new List<WeatherForecast?>();
        foreach (var point in points)
        {
            var url = "https://api.met.no/weatherapi/locationforecast/2.0/compact?lat=" + point.Latitude.ToString("0.####", CultureInfo.InvariantCulture) + "&lon=" + point.Longitude.ToString("0.####", CultureInfo.InvariantCulture);
            var (body, response) = await WeatherHttp.GetAsync(http, agent, url, ct);
            using (response)
            {
                if (!response.IsSuccessStatusCode || body is null) throw WeatherHttp.Failure(response, body, Name);
                var now = time.GetCurrentInstant(); var hours = new List<WeatherHour>();
                foreach (var entry in body["properties"]?["timeseries"] as JsonArray ?? [])
                {
                    if (!InstantPattern.ExtendedIso.Parse(entry?["time"]?.ToString() ?? "").TryGetValue(default, out var at)) continue;
                    var data = entry!["data"]; var details = data?["instant"]?["details"];
                    var (length, next) = data?["next_1_hours"] is { } one ? (1, one) : data?["next_6_hours"] is { } six ? (6, six) : (0, null);
                    if (next is null) continue;
                    var wind = WeatherHttp.Number(details?["wind_speed"]) is { } ms ? Math.Round(ms * 3.6, 1) : (double?)null;
                    hours.Add(new WeatherHour(at, length, WeatherHttp.Number(details?["air_temperature"]), null, WeatherHttp.Number(next["details"]?["precipitation_amount"]), wind, null, WeatherMath.FromText(next["summary"]?["symbol_code"]?.ToString())));
                }
                // Six-hour steps overlap the hourly ones at the switch; keep the hourly ones.
                var hourlyUntil = hours.Where(h => h.Hours == 1).Select(h => h.End).DefaultIfEmpty(Instant.MinValue).Max();
                result.Add(new WeatherForecast(Id, point, now, WeatherHttp.Expires(response, now, Duration.FromHours(1), Duration.FromMinutes(30)), hours.Where(h => h.Hours == 1 || h.Time >= hourlyUntil).ToArray()));
            }
        }
        return result;
    }
}

/// <summary>
/// The US National Weather Service (api.weather.gov): hourly forecasts for about a week, only for the United States.
/// The data is public domain; the service asks for an identifying User-Agent.
/// </summary>
public sealed class NationalWeatherService(HttpClient http, string? userAgent = null, IClock? clock = null) : IWeatherProvider
{
    public const string ProviderId = "nws";
    private readonly string agent = userAgent ?? WeatherHttp.DefaultUserAgent;
    private readonly IClock time = clock ?? SystemClock.Instance;
    public string Id => ProviderId;
    public string Name => "National Weather Service (US)";
    public WeatherAttribution Attribution { get; } = new("Forecast from the US National Weather Service", "https://www.weather.gov/", null);
    public bool RequiresKey => false;
    public int HorizonDays => 6;

    /// <summary>A rough box around the United States and its territories; the service itself has the final word.</summary>
    public bool Covers(GeoPoint p) => p.Latitude is >= 17 and <= 72 && (p.Longitude is >= -180 and <= -64 || p.Longitude >= 170) || p.Latitude is >= 13 and <= 21 && p.Longitude is >= 144 and <= 146.5;

    public async Task<IReadOnlyList<WeatherForecast?>> ForecastAsync(IReadOnlyList<GeoPoint> points, CancellationToken ct = default)
    {
        var result = new List<WeatherForecast?>();
        foreach (var point in points)
        {
            if (!Covers(point)) { result.Add(null); continue; }
            var (place, placeResponse) = await WeatherHttp.GetAsync(http, agent, "https://api.weather.gov/points/" + point.Latitude.ToString("0.####", CultureInfo.InvariantCulture) + "," + point.Longitude.ToString("0.####", CultureInfo.InvariantCulture), ct, "application/geo+json");
            using (placeResponse)
            {
                if (placeResponse.StatusCode == HttpStatusCode.NotFound) { result.Add(null); continue; }
                if (!placeResponse.IsSuccessStatusCode || place?["properties"]?["forecastHourly"]?.ToString() is not { } hourlyUrl) throw WeatherHttp.Failure(placeResponse, place, Name);
                var (body, response) = await WeatherHttp.GetAsync(http, agent, hourlyUrl + "?units=si", ct, "application/geo+json");
                using (response)
                {
                    if (!response.IsSuccessStatusCode || body is null) throw WeatherHttp.Failure(response, body, Name);
                    var now = time.GetCurrentInstant(); var hours = new List<WeatherHour>();
                    foreach (var period in body["properties"]?["periods"] as JsonArray ?? [])
                    {
                        if (period is null || !OffsetDateTimePattern.ExtendedIso.Parse(period["startTime"]?.ToString() ?? "").TryGetValue(default, out var start)) continue;
                        var temperature = WeatherHttp.Number(period["temperature"]); if (period["temperatureUnit"]?.ToString() == "F" && temperature is { } f) temperature = Math.Round((f - 32) / 1.8, 1);
                        var wind = period["windSpeed"]?.ToString() is { } w && double.TryParse(w.Split(' ')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var speed) ? (w.Contains("mph") ? Math.Round(speed * 1.609, 1) : speed) : (double?)null;
                        hours.Add(new WeatherHour(start.ToInstant(), 1, temperature, (int?)WeatherHttp.Number(period["probabilityOfPrecipitation"]?["value"]), null, wind, null, WeatherMath.FromText(period["shortForecast"]?.ToString())));
                    }
                    result.Add(new WeatherForecast(Id, point, now, WeatherHttp.Expires(response, now, Duration.FromHours(1), Duration.FromMinutes(30)), hours));
                }
            }
        }
        return result;
    }
}

/// <summary>
/// Visual Crossing (visualcrossing.com) with a personal API key: global forecasts for 15 days. The key stays in the
/// device's secret store under <see cref="KeyName"/> and is only sent to Visual Crossing.
/// </summary>
public sealed class VisualCrossing(HttpClient http, ISecretStore secrets, string? userAgent = null, IClock? clock = null) : IWeatherProvider
{
    public const string ProviderId = "visual-crossing";
    /// <summary>The secret store entry that holds the API key.</summary>
    public const string KeyName = "weather.visual-crossing.key";
    private readonly string agent = userAgent ?? WeatherHttp.DefaultUserAgent;
    private readonly IClock time = clock ?? SystemClock.Instance;
    public string Id => ProviderId;
    public string Name => "Visual Crossing";
    public WeatherAttribution Attribution { get; } = new("Weather data by Visual Crossing", "https://www.visualcrossing.com/", null);
    public bool RequiresKey => true;
    public int HorizonDays => 15;
    public bool Covers(GeoPoint point) => true;

    public async Task<IReadOnlyList<WeatherForecast?>> ForecastAsync(IReadOnlyList<GeoPoint> points, CancellationToken ct = default)
    {
        if (points.Count == 0) return [];
        var key = await secrets.ReadAsync(KeyName, ct);
        if (string.IsNullOrWhiteSpace(key)) throw new DomainException("weather.key", "Visual Crossing needs an API key.");
        var result = new List<WeatherForecast?>();
        foreach (var point in points)
        {
            var url = "https://weather.visualcrossing.com/VisualCrossingWebServices/rest/services/timeline/" + point + "/next" + HorizonDays + "days?unitGroup=metric&include=hours&contentType=json&iconSet=icons2&elements=datetimeEpoch,temp,precip,precipprob,windspeed,windgust,icon&key=" + Uri.EscapeDataString(key.Trim());
            var (body, response) = await WeatherHttp.GetAsync(http, agent, url, ct);
            using (response)
            {
                if (!response.IsSuccessStatusCode || body is null) throw WeatherHttp.Failure(response, body, Name, keyed: true);
                var now = time.GetCurrentInstant(); var hours = new List<WeatherHour>();
                foreach (var hour in (body["days"] as JsonArray ?? []).SelectMany(d => d?["hours"] as JsonArray ?? []))
                {
                    if (WeatherHttp.Number(hour?["datetimeEpoch"]) is not { } epoch) continue;
                    var precipitation = WeatherHttp.Number(hour!["precip"]); var condition = WeatherMath.FromText(hour["icon"]?.ToString());
                    if (condition == WeatherCondition.Rain && precipitation >= 4) condition = WeatherCondition.HeavyRain;
                    hours.Add(new WeatherHour(Instant.FromUnixTimeSeconds((long)epoch), 1, WeatherHttp.Number(hour["temp"]), (int?)WeatherHttp.Number(hour["precipprob"]), precipitation, WeatherHttp.Number(hour["windspeed"]), WeatherHttp.Number(hour["windgust"]), condition));
                }
                result.Add(new WeatherForecast(Id, point, now, now + Duration.FromHours(2), hours));
            }
        }
        return result;
    }
}
