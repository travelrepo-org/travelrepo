using System.Globalization;
using System.Text.Json;
using NodaTime;
using NodaTime.Text;

namespace TravelRepo.Providers.Weather;

/// <summary>
/// The local weather cache: forecasts per service and point, climate averages per point and date, and points a
/// service does not cover. It lives on this device only (spec section 43) and can be deleted at any time.
/// </summary>
public sealed class WeatherCache(string directory)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly object gate = new();
    public string Directory { get; } = directory;

    private sealed record HourDto(long T, int H, double? C, int? P, double? Mm, double? W, double? G, int K);
    private sealed record ForecastDto(long Fetched, long Expires, List<HourDto> Hours);
    private sealed record ClimateDto(string D, double Max, double Min, double Rain, double Mm, int Y);

    private static string Name(GeoPoint point) => point.Latitude.ToString("0.####", CultureInfo.InvariantCulture) + "_" + point.Longitude.ToString("0.####", CultureInfo.InvariantCulture);
    private string ForecastFile(string provider, GeoPoint point) => Path.Combine(Directory, "forecast", provider, Name(point) + ".json");
    private string ClimateFile(GeoPoint point) => Path.Combine(Directory, "climate", Name(point) + ".json");
    private string UncoveredFile(string provider, GeoPoint point) => Path.Combine(Directory, "uncovered", provider, Name(point));

    public WeatherForecast? Forecast(string provider, GeoPoint point)
    {
        var dto = Read<ForecastDto>(ForecastFile(provider, point)); if (dto is null) return null;
        return new(provider, point, Instant.FromUnixTimeSeconds(dto.Fetched), Instant.FromUnixTimeSeconds(dto.Expires),
            dto.Hours.Select(h => new WeatherHour(Instant.FromUnixTimeSeconds(h.T), h.H, h.C, h.P, h.Mm, h.W, h.G, (WeatherCondition)h.K)).ToArray());
    }

    public void Store(WeatherForecast forecast) =>
        Write(ForecastFile(forecast.Provider, forecast.Point), new ForecastDto(forecast.Fetched.ToUnixTimeSeconds(), forecast.Expires.ToUnixTimeSeconds(),
            forecast.Hours.Select(h => new HourDto(h.Time.ToUnixTimeSeconds(), h.Hours, h.TemperatureC, h.PrecipitationChance, h.PrecipitationMm, h.WindKmh, h.GustKmh, (int)h.Condition)).ToList()));

    /// <summary>Climate averages stored for a point, by date.</summary>
    public IReadOnlyDictionary<LocalDate, ClimateDay> Climate(GeoPoint point) =>
        (Read<List<ClimateDto>>(ClimateFile(point)) ?? []).Select(d => LocalDatePattern.Iso.Parse(d.D).TryGetValue(default, out var date) ? new ClimateDay(date, d.Max, d.Min, d.Rain, d.Mm, d.Y) : null)
            .OfType<ClimateDay>().GroupBy(d => d.Date).ToDictionary(g => g.Key, g => g.Last());

    public void Store(GeoPoint point, IEnumerable<ClimateDay> days)
    {
        lock (gate)
        {
            var all = Climate(point).ToDictionary(); foreach (var day in days) all[day.Date] = day;
            Write(ClimateFile(point), all.Values.OrderBy(d => d.Date).Select(d => new ClimateDto(LocalDatePattern.Iso.Format(d.Date), d.MaxC, d.MinC, d.RainDayShare, d.PrecipitationMm, d.Years)).ToList());
        }
    }

    /// <summary>Whether a service said recently that it has no forecasts for this point.</summary>
    public bool Uncovered(string provider, GeoPoint point, Instant now) => File.Exists(UncoveredFile(provider, point)) && Instant.FromDateTimeUtc(File.GetLastWriteTimeUtc(UncoveredFile(provider, point))) > now - Duration.FromDays(30);
    public void MarkUncovered(string provider, GeoPoint point) { var file = UncoveredFile(provider, point); System.IO.Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, ""); }

    /// <summary>Removes everything cached.</summary>
    public void Clear() { lock (gate) { parsed.Clear(); if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true); } }

    /// <summary>The bytes the cache takes on this device.</summary>
    public long Size => System.IO.Directory.Exists(Directory) ? new DirectoryInfo(Directory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;

    // Parsed files, kept while the file on disk is unchanged: clients rebuild the trip's weather after every edit.
    private readonly Dictionary<string, (DateTime Written, object? Value)> parsed = [];

    private T? Read<T>(string file) where T : class
    {
        lock (gate)
        {
            try
            {
                if (!File.Exists(file)) { parsed.Remove(file); return null; }
                var written = File.GetLastWriteTimeUtc(file);
                if (parsed.TryGetValue(file, out var known) && known.Written == written && known.Value is T value) return value;
                var result = JsonSerializer.Deserialize<T>(File.ReadAllText(file), Json); parsed[file] = (written, result);
                return result;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return null; }
        }
    }

    private void Write<T>(string file, T value)
    {
        lock (gate)
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temporary = file + ".tmp"; File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json)); File.Move(temporary, file, true);
            parsed.Remove(file);
        }
    }
}
