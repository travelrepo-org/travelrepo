using System.Globalization;
using NodaTime;
using TravelRepo.Core;

namespace TravelRepo.Providers.Weather;

/// <summary>A position on Earth in decimal degrees.</summary>
public readonly record struct GeoPoint(double Latitude, double Longitude)
{
    /// <summary>
    /// The point rounded to <paramref name="decimals"/> places. Weather requests only ever send points rounded to two
    /// decimals, about one kilometre. Coarser points would save requests but can land on a mountain: services correct
    /// temperatures for the height of the point they are asked for.
    /// </summary>
    public GeoPoint Rounded(int decimals) => new(Math.Round(Latitude, decimals), Math.Round(Longitude, decimals));

    /// <summary>A stable text form, used in cache keys and requests.</summary>
    public override string ToString() => Latitude.ToString("0.####", CultureInfo.InvariantCulture) + "," + Longitude.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>The location of a place entity, or null when it has none.</summary>
    public static GeoPoint? Of(Entity? place) => Places.Location(place) is { } p ? new GeoPoint(p.Latitude, p.Longitude) : null;
}

/// <summary>The weather of an hour or a day in a few classes, ordered from mild to severe.</summary>
public enum WeatherCondition { Clear, PartlyCloudy, Cloudy, Fog, Drizzle, Rain, HeavyRain, Snow, Thunderstorm }

/// <summary>
/// Forecast weather for a period starting at <see cref="Time"/> and lasting <see cref="Hours"/> hours (1, or more where
/// a service only forecasts in steps of several hours). Temperatures are °C, precipitation mm over the period, wind and
/// gusts km/h. Values a service does not forecast are null.
/// </summary>
public sealed record WeatherHour(Instant Time, int Hours, double? TemperatureC, int? PrecipitationChance, double? PrecipitationMm, double? WindKmh, double? GustKmh, WeatherCondition Condition)
{
    public Instant End => Time + Duration.FromHours(Hours);
}

/// <summary>The weather of one local day, aggregated from the hours of that day.</summary>
public sealed record WeatherDay(LocalDate Date, double? MaxC, double? MinC, int? PrecipitationChance, double? PrecipitationMm, double? GustKmh, WeatherCondition Condition, Instant? Sunrise, Instant? Sunset);

/// <summary>Averages for one calendar day over recent years: typical highs and lows, and how often it rained.</summary>
/// <param name="Date">The trip date the averages are for.</param>
/// <param name="MaxC">The average daily high.</param>
/// <param name="MinC">The average daily low.</param>
/// <param name="RainDayShare">The share of days with at least 1 mm of precipitation, from 0 to 1.</param>
/// <param name="PrecipitationMm">The average precipitation per day.</param>
/// <param name="Years">How many years the averages are made from.</param>
/// <param name="Sunrise">Sunrise on that date, when known.</param>
/// <param name="Sunset">Sunset on that date, when known.</param>
public sealed record ClimateDay(LocalDate Date, double MaxC, double MinC, double RainDayShare, double PrecipitationMm, int Years, Instant? Sunrise = null, Instant? Sunset = null);

/// <summary>The credit a weather service asks for.</summary>
public sealed record WeatherAttribution(string Text, string Url, string? License);

/// <summary>A forecast for one point, as fetched from a service.</summary>
public sealed record WeatherForecast(string Provider, GeoPoint Point, Instant Fetched, Instant Expires, IReadOnlyList<WeatherHour> Hours);

/// <summary>A request for climate averages at one point.</summary>
public sealed record ClimateRequest(GeoPoint Point, LocalDate From, LocalDate To);

/// <summary>
/// A weather forecast service. Services are only contacted when the person has turned weather on, and only with
/// rounded coordinates; nothing else about the trip is sent.
/// </summary>
public interface IWeatherProvider
{
    /// <summary>A stable identifier, stored in settings, such as <c>open-meteo</c>.</summary>
    string Id { get; }
    /// <summary>The service's name for display.</summary>
    string Name { get; }
    WeatherAttribution Attribution { get; }
    /// <summary>Whether the service needs a personal API key.</summary>
    bool RequiresKey { get; }
    /// <summary>How many days ahead the service forecasts.</summary>
    int HorizonDays { get; }
    /// <summary>Whether the service forecasts for this point at all, for example only within one country.</summary>
    bool Covers(GeoPoint point);
    /// <summary>
    /// Forecasts for the points, in the same order. An entry is null when the service does not cover that point.
    /// Throws <see cref="DomainException"/> with code <c>weather.key</c> when a key is missing or rejected, and
    /// <c>weather.unavailable</c> when the service cannot be reached or answers with an error.
    /// </summary>
    Task<IReadOnlyList<WeatherForecast?>> ForecastAsync(IReadOnlyList<GeoPoint> points, CancellationToken ct = default);
}

/// <summary>A source of climate averages for dates beyond the forecast.</summary>
public interface IClimateSource
{
    WeatherAttribution Attribution { get; }
    /// <summary>Averages for every date of each request, keyed by the request's point.</summary>
    Task<IReadOnlyDictionary<GeoPoint, IReadOnlyList<ClimateDay>>> ClimateAsync(IReadOnlyList<ClimateRequest> requests, CancellationToken ct = default);
}

/// <summary>Shared helpers for turning service data into hours and days.</summary>
public static class WeatherMath
{
    /// <summary>The condition of a WMO weather code, as used by Open-Meteo.</summary>
    public static WeatherCondition FromWmo(int code) => code switch
    {
        0 or 1 => WeatherCondition.Clear,
        2 => WeatherCondition.PartlyCloudy,
        3 => WeatherCondition.Cloudy,
        45 or 48 => WeatherCondition.Fog,
        >= 51 and <= 57 => WeatherCondition.Drizzle,
        65 or 82 => WeatherCondition.HeavyRain,
        >= 61 and <= 67 or 80 or 81 => WeatherCondition.Rain,
        >= 71 and <= 77 or 85 or 86 => WeatherCondition.Snow,
        >= 95 => WeatherCondition.Thunderstorm,
        _ => WeatherCondition.Cloudy
    };

    /// <summary>The condition of a short text such as an icon name or "Chance Rain Showers".</summary>
    public static WeatherCondition FromText(string? text)
    {
        var t = (text ?? "").ToLowerInvariant().Replace("-", "").Replace("_", "").Replace(" ", "");
        if (t.Contains("thunder") || t.Contains("tsra")) return WeatherCondition.Thunderstorm;
        if (t.Contains("snow") || t.Contains("sleet") || t.Contains("blizzard") || t.Contains("ice")) return WeatherCondition.Snow;
        if (t.Contains("heavyrain")) return WeatherCondition.HeavyRain;
        if (t.Contains("drizzle") || t.Contains("lightrain")) return WeatherCondition.Drizzle;
        if (t.Contains("rain") || t.Contains("shower")) return WeatherCondition.Rain;
        if (t.Contains("fog") || t.Contains("mist") || t.Contains("haze")) return WeatherCondition.Fog;
        if (t.Contains("partly") || t.Contains("fair") || t.Contains("mostlysunny") || t.Contains("mostlyclear") || t.Contains("sct")) return WeatherCondition.PartlyCloudy;
        if (t.Contains("cloud") || t.Contains("overcast") || t.Contains("bkn") || t.Contains("ovc")) return WeatherCondition.Cloudy;
        if (t.Contains("clear") || t.Contains("sun") || t.Contains("skc") || t.Contains("few")) return WeatherCondition.Clear;
        return WeatherCondition.Cloudy;
    }

    /// <summary>
    /// Aggregates the hours of one local day. The day's condition is the most severe kind of precipitation that lasts
    /// at least two hours (one for thunderstorms and snow), otherwise the most common sky between 7:00 and 21:00.
    /// </summary>
    public static WeatherDay? Day(IReadOnlyList<WeatherHour> hours, LocalDate date, DateTimeZone zone, GeoPoint point)
    {
        var start = zone.AtStartOfDay(date).ToInstant(); var end = zone.AtStartOfDay(date.PlusDays(1)).ToInstant();
        var day = hours.Where(h => h.End > start && h.Time < end).ToArray();
        if (day.Length == 0 || day.Sum(h => (Math.Min(h.End.ToUnixTimeSeconds(), end.ToUnixTimeSeconds()) - Math.Max(h.Time.ToUnixTimeSeconds(), start.ToUnixTimeSeconds())) / 3600.0) < 12) return null;
        var daytime = day.Where(h => h.Time.InZone(zone).Hour is >= 7 and < 21 || h.Hours > 1).ToArray(); if (daytime.Length == 0) daytime = day;
        var wet = daytime.Where(h => h.Condition >= WeatherCondition.Drizzle).GroupBy(h => h.Condition).Select(g => (g.Key, Hours: g.Sum(h => h.Hours)))
            .Where(g => g.Hours >= (g.Key >= WeatherCondition.Snow ? 1 : 2)).Select(g => (WeatherCondition?)g.Key).Max();
        var sky = daytime.Where(h => h.Condition < WeatherCondition.Drizzle).GroupBy(h => h.Condition).OrderByDescending(g => g.Sum(h => h.Hours)).ThenByDescending(g => g.Key).Select(g => (WeatherCondition?)g.Key).FirstOrDefault();
        double? Max(Func<WeatherHour, double?> f) => day.Select(f).Where(v => v is not null).Max();
        double? Min(Func<WeatherHour, double?> f) => day.Select(f).Where(v => v is not null).Min();
        var precipitation = day.Any(h => h.PrecipitationMm is not null) ? Math.Round(day.Sum(h => h.PrecipitationMm ?? 0), 1) : (double?)null;
        var (sunrise, sunset) = Sun.Times(point, date, zone);
        return new(date, Max(h => h.TemperatureC), Min(h => h.TemperatureC), day.Max(h => h.PrecipitationChance), precipitation, Max(h => h.GustKmh), wet ?? sky ?? WeatherCondition.Cloudy, sunrise, sunset);
    }
}

/// <summary>Sunrise and sunset from the NOAA solar calculation, accurate to a minute or two.</summary>
public static class Sun
{
    /// <summary>Sunrise and sunset on a local date; null during polar day or night.</summary>
    public static (Instant? Sunrise, Instant? Sunset) Times(GeoPoint point, LocalDate date, DateTimeZone zone)
    {
        var noonUtc = zone.AtLeniently(date.At(new LocalTime(12, 0))).ToInstant();
        var julianDay = noonUtc.ToUnixTimeSeconds() / 86400.0 + 2440587.5;
        var t = (julianDay - 2451545.0) / 36525.0;
        double Rad(double d) => d * Math.PI / 180; double Deg(double r) => r * 180 / Math.PI;
        var meanLongitude = (280.46646 + t * (36000.76983 + t * 0.0003032)) % 360;
        var meanAnomaly = 357.52911 + t * (35999.05029 - 0.0001537 * t);
        var eccentricity = 0.016708634 - t * (0.000042037 + 0.0000001267 * t);
        var center = Math.Sin(Rad(meanAnomaly)) * (1.914602 - t * (0.004817 + 0.000014 * t)) + Math.Sin(Rad(2 * meanAnomaly)) * (0.019993 - 0.000101 * t) + Math.Sin(Rad(3 * meanAnomaly)) * 0.000289;
        var apparent = meanLongitude + center - 0.00569 - 0.00478 * Math.Sin(Rad(125.04 - 1934.136 * t));
        var obliquity = 23 + (26 + (21.448 - t * (46.815 + t * (0.00059 - t * 0.001813))) / 60) / 60 + 0.00256 * Math.Cos(Rad(125.04 - 1934.136 * t));
        var declination = Deg(Math.Asin(Math.Sin(Rad(obliquity)) * Math.Sin(Rad(apparent))));
        var y = Math.Pow(Math.Tan(Rad(obliquity / 2)), 2);
        var equationOfTime = 4 * Deg(y * Math.Sin(2 * Rad(meanLongitude)) - 2 * eccentricity * Math.Sin(Rad(meanAnomaly)) + 4 * eccentricity * y * Math.Sin(Rad(meanAnomaly)) * Math.Cos(2 * Rad(meanLongitude))
            - 0.5 * y * y * Math.Sin(4 * Rad(meanLongitude)) - 1.25 * eccentricity * eccentricity * Math.Sin(2 * Rad(meanAnomaly)));
        var cosHourAngle = Math.Cos(Rad(90.833)) / (Math.Cos(Rad(point.Latitude)) * Math.Cos(Rad(declination))) - Math.Tan(Rad(point.Latitude)) * Math.Tan(Rad(declination));
        if (cosHourAngle is < -1 or > 1) return (null, null);
        var hourAngle = Deg(Math.Acos(cosHourAngle));
        var solarNoonMinutes = 720 - 4 * point.Longitude - equationOfTime; // minutes after UTC midnight of the UTC date at local noon
        var utcMidnight = Instant.FromUtc(noonUtc.InUtc().Year, noonUtc.InUtc().Month, noonUtc.InUtc().Day, 0, 0);
        var noon = utcMidnight + Duration.FromMinutes(solarNoonMinutes);
        // Keep the solar noon closest to local noon, since the UTC date can differ from the local date.
        while (noon - noonUtc > Duration.FromHours(12)) noon -= Duration.FromDays(1);
        while (noonUtc - noon > Duration.FromHours(12)) noon += Duration.FromDays(1);
        return (noon - Duration.FromMinutes(4 * hourAngle), noon + Duration.FromMinutes(4 * hourAngle));
    }
}
