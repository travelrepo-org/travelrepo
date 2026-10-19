using NodaTime;
using TravelRepo.Core;

namespace TravelRepo.Providers.Weather;

/// <summary>How far an outlook can be trusted.</summary>
public enum OutlookKind
{
    /// <summary>A forecast for the next week, hour by hour.</summary>
    Forecast,
    /// <summary>A forecast one to about two weeks ahead: a daily tendency that can still change a lot.</summary>
    Trend,
    /// <summary>Beyond the forecasts: what the weather usually is on these days.</summary>
    Climate
}

/// <summary>
/// The weather at one place for an item or a day. <see cref="Hours"/> covers the item's time (empty for trends and
/// climate), <see cref="Day"/> the whole local day, and <see cref="Climate"/> the averages beyond the forecast.
/// </summary>
public sealed record WeatherOutlook(OutlookKind Kind, Guid Place, GeoPoint Point, string Provider, Instant? Fetched, IReadOnlyList<WeatherHour> Hours, WeatherDay? Day, ClimateDay? Climate);

/// <summary>A kind of weather that matters for an item.</summary>
public enum ConcernKind { Thunderstorm, Snow, Wind, Rain, Heat, Cold }

/// <summary>
/// Weather that matters for an item: <see cref="Value"/> is the peak gust (km/h), the precipitation (mm), the highest
/// or lowest temperature (°C), or zero for thunderstorms and snow. <see cref="Severe"/> marks weather that can cancel
/// or endanger the plan.
/// </summary>
public sealed record WeatherConcern(ConcernKind Kind, bool Severe, double Value);

/// <summary>The weather for one schedule item: one outlook, or two for a journey (departure and arrival).</summary>
public sealed record ItemWeather(Guid Item, ExposureEstimate Exposure, IReadOnlyList<WeatherOutlook> Outlooks, IReadOnlyList<WeatherConcern> Concerns);

/// <summary>The weather of a trip day at the place where most of that day happens.</summary>
public sealed record DayWeather(LocalDate Date, Guid Place, WeatherOutlook Outlook);

/// <summary>Something that went wrong while updating, for a quiet hint in clients.</summary>
public sealed record WeatherProblem(string Provider, string Code, string Message);

/// <summary>The weather for a trip: per item, per day, the services it came from and any problems while updating.</summary>
public sealed record TripWeather(IReadOnlyDictionary<Guid, ItemWeather> Items, IReadOnlyDictionary<LocalDate, DayWeather> Days, IReadOnlyList<WeatherAttribution> Sources, IReadOnlyList<WeatherProblem> Problems)
{
    public static readonly TripWeather Empty = new(new Dictionary<Guid, ItemWeather>(), new Dictionary<LocalDate, DayWeather>(), [], []);
}

/// <summary>
/// Weather for a trip. It works out which places and dates matter, asks the chosen service for forecasts (falling back
/// to Open-Meteo where that service has no forecast), uses climate averages beyond the forecasts and keeps everything
/// in the local <see cref="WeatherCache"/>. Nothing is written into the trip.
/// </summary>
public sealed class WeatherService
{
    /// <summary>Outlooks up to this far ahead count as forecasts; later ones as trends.</summary>
    public static readonly Duration ForecastReach = Duration.FromDays(7);
    /// <summary>Forecasts older than this are not shown, even offline.</summary>
    public static readonly Duration MaximumAge = Duration.FromHours(24);
    private readonly Dictionary<string, IWeatherProvider> providers;
    private readonly IWeatherProvider fallback;
    private readonly IClimateSource? climate;
    private readonly IClock clock;
    public WeatherCache Cache { get; }
    /// <summary>The service to ask first, by <see cref="IWeatherProvider.Id"/>. Unknown values mean the fallback.</summary>
    public string Preferred { get; set; }

    /// <param name="providers">The available services. The first one is the fallback for points and days others do not cover.</param>
    /// <param name="climate">Where climate averages come from, or null to show nothing beyond the forecasts.</param>
    /// <param name="cache">The local cache.</param>
    /// <param name="preferred">The service to ask first.</param>
    /// <param name="clock">The current time; tests replace it.</param>
    public WeatherService(IReadOnlyList<IWeatherProvider> providers, IClimateSource? climate, WeatherCache cache, string? preferred = null, IClock? clock = null)
    {
        if (providers.Count == 0) throw new ArgumentException("At least one weather service is needed.", nameof(providers));
        this.providers = providers.ToDictionary(p => p.Id); fallback = providers[0]; this.climate = climate; Cache = cache; this.clock = clock ?? SystemClock.Instance;
        Preferred = preferred ?? fallback.Id;
    }

    /// <summary>The built-in services: Open-Meteo first, then MET Norway, the US National Weather Service and Visual Crossing.</summary>
    public static IReadOnlyList<IWeatherProvider> BuiltIn(HttpClient http, ISecretStore secrets, string? userAgent = null) =>
        [new OpenMeteo(http, userAgent), new MetNorway(http, userAgent), new NationalWeatherService(http, userAgent), new VisualCrossing(http, secrets, userAgent)];

    /// <summary>
    /// A service that only reads <paramref name="cache"/>: it knows the built-in services by name but cannot reach
    /// any of them, so callers such as AI assistants get what was already fetched and can never trigger a download.
    /// </summary>
    public static WeatherService CacheOnly(WeatherCache cache, string? preferred = null, IClock? clock = null)
    {
        var providers = BuiltIn(new HttpClient(new RefuseRequests()), new NoSecrets());
        return new(providers, (OpenMeteo)providers[0], cache, preferred, clock) { IsCacheOnly = true };
    }

    /// <summary>True for <see cref="CacheOnly"/> services; <see cref="UpdateAsync"/> then refuses to run.</summary>
    public bool IsCacheOnly { get; private init; }

    private sealed class RefuseRequests : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new InvalidOperationException("This weather service only reads the cache.");
    }
    private sealed class NoSecrets : ISecretStore
    {
        public Task<string?> ReadAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task WriteAsync(string key, string value, CancellationToken ct = default) => throw new InvalidOperationException("This weather service only reads the cache.");
        public Task DeleteAsync(string key, CancellationToken ct = default) => throw new InvalidOperationException("This weather service only reads the cache.");
    }

    public IReadOnlyList<IWeatherProvider> Providers => [.. providers.Values];
    private IWeatherProvider Primary => providers.GetValueOrDefault(Preferred) ?? fallback;

    /// <summary>One place and time the trip needs weather for.</summary>
    private sealed record Need(Guid Place, GeoPoint Point, DateTimeZone Zone, Instant Start, Instant End);

    /// <summary>The trip's weather from the cache alone, without contacting any service.</summary>
    public TripWeather Cached(TripSnapshot trip) => Build(trip, clock.GetCurrentInstant(), []);

    /// <summary>Updates what is missing or out of date, then returns the trip's weather. Failures become <see cref="TripWeather.Problems"/>.</summary>
    public async Task<TripWeather> UpdateAsync(TripSnapshot trip, CancellationToken ct = default)
    {
        if (IsCacheOnly) throw new InvalidOperationException("This weather service only reads the cache.");
        var now = clock.GetCurrentInstant(); var problems = new List<WeatherProblem>();
        var needs = Needs(trip, now).ToArray();
        var horizon = now + Duration.FromDays(fallback.HorizonDays - 1);
        var forecastCells = needs.Where(n => n.Start < horizon).GroupBy(n => n.Point.Rounded(2)).ToArray();
        var primary = Primary;
        // The chosen service first; Open-Meteo for what it does not cover, cannot reach, or forecasts further ahead.
        var missing = new List<GeoPoint>(); var farther = new List<GeoPoint>();
        var asked = forecastCells.Select(c => c.Key).Where(p => primary.Covers(p) && !Cache.Uncovered(primary.Id, p, now)).ToArray();
        missing.AddRange(forecastCells.Select(c => c.Key).Except(asked));
        try { missing.AddRange(await RefreshAsync(primary, asked, now, ct)); }
        catch (DomainException ex) { problems.Add(new(primary.Id, ex.Code, ex.Message)); missing.AddRange(asked); }
        if (primary != fallback)
        {
            farther.AddRange(forecastCells.Where(c => c.Max(n => n.End) > now + Duration.FromDays(primary.HorizonDays - 1)).Select(c => c.Key));
            try { await RefreshAsync(fallback, missing.Concat(farther).Distinct().ToArray(), now, ct); }
            catch (DomainException ex) { problems.Add(new(fallback.Id, ex.Code, ex.Message)); }
        }

        if (climate is not null)
        {
            var requests = new List<ClimateRequest>();
            foreach (var cell in needs.Where(n => n.End >= horizon).GroupBy(n => n.Point.Rounded(2)))
            {
                var stored = Cache.Climate(cell.Key);
                var dates = cell.SelectMany(n => Dates(n).Where(d => d > now.InZone(n.Zone).Date.PlusDays(fallback.HorizonDays - 2))).Where(d => !stored.ContainsKey(d)).Distinct().Order().ToArray();
                if (dates.Length > 0) requests.Add(new(cell.Key, dates[0], dates[^1]));
            }
            if (requests.Count > 0)
            {
                try { foreach (var (point, days) in await climate.ClimateAsync(requests, ct)) Cache.Store(point, days); }
                catch (DomainException ex) { problems.Add(new("climate", ex.Code, ex.Message)); }
            }
        }
        return Build(trip, now, problems);
    }

    /// <summary>Fetches the points whose cached forecast expired. Returns the points the service does not cover.</summary>
    private async Task<IReadOnlyList<GeoPoint>> RefreshAsync(IWeatherProvider provider, IReadOnlyList<GeoPoint> points, Instant now, CancellationToken ct)
    {
        var stale = points.Where(p => Cache.Forecast(provider.Id, p) is not { } f || f.Expires <= now).ToArray();
        if (stale.Length == 0) return [];
        var uncovered = new List<GeoPoint>();
        // Small batches keep single requests short; services without batching still get one point at a time.
        foreach (var batch in stale.Chunk(25))
        {
            var forecasts = await provider.ForecastAsync(batch, ct);
            for (var i = 0; i < batch.Length; i++)
                if (forecasts[i] is { } forecast) Cache.Store(forecast);
                else { Cache.MarkUncovered(provider.Id, batch[i]); uncovered.Add(batch[i]); }
        }
        return uncovered;
    }

    private static IEnumerable<LocalDate> Dates(Need need)
    {
        var first = need.Start.InZone(need.Zone).Date; var last = need.End.InZone(need.Zone).Date;
        for (var d = first; d <= last && d <= first.PlusDays(31); d = d.PlusDays(1)) yield return d;
    }

    /// <summary>The places and times of all future items and days, with the zone their days are counted in.</summary>
    private static IEnumerable<Need> Needs(TripSnapshot trip, Instant now)
    {
        foreach (var (_, outlooks) in ItemNeeds(trip, now)) foreach (var need in outlooks) yield return need;
        foreach (var (_, need) in DayNeeds(trip, now)) yield return need;
    }

    private static DateTimeZone ZoneOf(TripSnapshot trip, Entity place, string? fallbackZone) =>
        DateTimeZoneProviders.Tzdb.GetZoneOrNull(place.Data["timezone"]?.ToString() ?? "") ?? DateTimeZoneProviders.Tzdb.GetZoneOrNull(fallbackZone ?? "") ?? ScheduleQueries.TripZone(trip);

    private static IEnumerable<(Entity Item, Need[] Needs)> ItemNeeds(TripSnapshot trip, Instant now)
    {
        foreach (var item in trip.Entities.Values.Where(e => e.Type == "schedule_item" && !Stays.IsStay(e) && e.Data["status"]?.ToString() != "cancelled"))
        {
            if (item.Data["children"] is System.Text.Json.Nodes.JsonArray { Count: > 0 }) continue;
            var span = ScheduleQueries.Span(trip, item);
            if (span.Start is not { } start || span.End is not { } end || end < now) continue;
            if (item.Data["components"]?["transport"] is not null)
            {
                var (from, to) = ScheduleQueries.Route(trip, item); var list = new List<Need>();
                if (GeoPoint.Of(from) is { } a && start >= now) list.Add(new(from!.Id, a, ZoneOf(trip, from, span.StartZone), start, start + Duration.FromHours(1)));
                if (GeoPoint.Of(to) is { } b) list.Add(new(to!.Id, b, ZoneOf(trip, to, span.EndZone), end, end + Duration.FromHours(1)));
                if (list.Count > 0) yield return (item, list.ToArray());
                continue;
            }
            if (ScheduleQueries.PrimaryPlace(trip, item) is { } place && GeoPoint.Of(place) is { } point)
                yield return (item, [new(place.Id, point, ZoneOf(trip, place, span.StartZone), start < now ? now : start, end)]);
        }
    }

    /// <summary>
    /// The place of each future trip day: the place where the most daytime (8:00 to 20:00) is planned, not counting
    /// journeys and stays; without such items, where that night is spent, or the night before.
    /// </summary>
    private static IEnumerable<(LocalDate Date, Need Need)> DayNeeds(TripSnapshot trip, Instant now)
    {
        var zone = ScheduleQueries.TripZone(trip); var today = now.InZone(zone).Date;
        var (first, last) = TripQueries.EffectiveDates(trip);
        if (first is null || last is null || last < today) yield break;
        var items = trip.Entities.Values.Where(e => e.Type == "schedule_item" && !Stays.IsStay(e) && e.Data["components"]?["transport"] is null && e.Data["status"]?.ToString() != "cancelled" && e.Data["children"] is not System.Text.Json.Nodes.JsonArray { Count: > 0 })
            .Select(e => (Span: ScheduleQueries.Span(trip, e), Place: ScheduleQueries.PrimaryPlace(trip, e))).Where(x => x.Span.IsPlaced && GeoPoint.Of(x.Place) is not null).ToArray();
        var stays = Stays.All(trip).Where(s => s.FirstNight is not null && s.Item.Data["status"]?.ToString() != "cancelled" && s.Place is { } p && GeoPoint.Of(trip.Find(p)) is not null).ToArray();
        for (var date = first.Value < today ? today : first.Value; date <= last.Value && date <= today.PlusDays(400); date = date.PlusDays(1))
        {
            var from = zone.AtLeniently(date.At(new LocalTime(8, 0))).ToInstant(); var until = zone.AtLeniently(date.At(new LocalTime(20, 0))).ToInstant();
            var best = items.Select(x => (x.Place, Minutes: (Math.Min(x.Span.End!.Value.ToUnixTimeSeconds(), until.ToUnixTimeSeconds()) - Math.Max(x.Span.Start!.Value.ToUnixTimeSeconds(), from.ToUnixTimeSeconds())) / 60))
                .Where(x => x.Minutes > 0).GroupBy(x => x.Place!.Id).Select(g => (Place: g.First().Place!, Minutes: g.Sum(x => x.Minutes))).OrderByDescending(x => x.Minutes).Select(x => x.Place).FirstOrDefault();
            var place = best ?? stays.Where(s => s.Covers(date)).Concat(stays.Where(s => s.Covers(date.PlusDays(-1)))).Select(s => trip.Find(s.Place!.Value)).FirstOrDefault();
            if (place is null || GeoPoint.Of(place) is not { } point) continue;
            var placeZone = ZoneOf(trip, place, null);
            yield return (date, new(place.Id, point, placeZone, placeZone.AtStartOfDay(date).ToInstant(), placeZone.AtStartOfDay(date.PlusDays(1)).ToInstant()));
        }
    }

    private TripWeather Build(TripSnapshot trip, Instant now, IReadOnlyList<WeatherProblem> problems)
    {
        var sources = new HashSet<string>();
        WeatherOutlook? Outlook(Need need, bool forItem)
        {
            var date = need.Start.InZone(need.Zone).Date;
            // The chosen service first, unless its forecast is out of date and Open-Meteo's is not.
            var candidates = new[] { Primary, fallback }.Distinct().Select(p => Cache.Forecast(p.Id, need.Point.Rounded(2))).OfType<WeatherForecast>().Where(f => f.Fetched >= now - MaximumAge).ToList();
            if (candidates.Count == 2 && candidates[0].Expires <= now && candidates[1].Expires > now) candidates.Reverse();
            foreach (var forecast in candidates)
            {
                var provider = providers[forecast.Provider];
                var hours = forecast.Hours.Where(h => h.End > now).ToArray();
                if (hours.Length == 0 || hours[^1].End < need.Start + Duration.FromHours(1)) continue;
                var day = WeatherMath.Day(forecast.Hours, date, need.Zone, need.Point);
                if (day is null && !forItem) continue;
                sources.Add(provider.Id);
                var kind = need.Start - now <= ForecastReach ? OutlookKind.Forecast : OutlookKind.Trend;
                var during = kind == OutlookKind.Forecast && forItem ? Hours(hours, need) : [];
                return new(kind, need.Place, need.Point, provider.Id, forecast.Fetched, during, day, null);
            }
            var cell = need.Point.Rounded(2);
            if (Cache.Climate(cell).GetValueOrDefault(date) is { } average)
            {
                sources.Add("climate");
                var (sunrise, sunset) = Sun.Times(need.Point, date, need.Zone);
                return new(OutlookKind.Climate, need.Place, need.Point, OpenMeteo.ProviderId, null, [], null, average with { Sunrise = sunrise, Sunset = sunset });
            }
            return null;
        }
        var items = new Dictionary<Guid, ItemWeather>();
        foreach (var (item, needs) in ItemNeeds(trip, now))
        {
            var outlooks = needs.Select(n => Outlook(n, true)).OfType<WeatherOutlook>().ToArray();
            if (outlooks.Length == 0) continue;
            var exposure = OutdoorEstimates.For(trip, item);
            var concerns = WeatherAdvice.Concerns(exposure, outlooks.Where(o => o.Kind == OutlookKind.Forecast).SelectMany(o => o.Hours).ToArray());
            items[item.Id] = new(item.Id, exposure, outlooks, concerns);
        }
        var days = new Dictionary<LocalDate, DayWeather>();
        foreach (var (date, need) in DayNeeds(trip, now)) if (Outlook(need, false) is { } outlook) days[date] = new(date, need.Place, outlook);
        var attributions = sources.Select(id => id == "climate" ? climate?.Attribution : providers.GetValueOrDefault(id)?.Attribution).OfType<WeatherAttribution>().Distinct().ToArray();
        return new(items, days, attributions, problems);
    }

    /// <summary>
    /// The hours that cover an item. Items of half a day or longer only count the daytime hours (7:00 to 21:00); a
    /// short item gets at least the hour it starts in.
    /// </summary>
    private static WeatherHour[] Hours(IReadOnlyList<WeatherHour> hours, Need need)
    {
        var during = hours.Where(h => h.End > need.Start && h.Time < need.End).ToArray();
        if (need.End - need.Start >= Duration.FromHours(12)) during = during.Where(h => h.Hours > 1 || h.Time.InZone(need.Zone).Hour is >= 7 and < 21).ToArray();
        return during;
    }
}

/// <summary>
/// When weather matters for an item, depending on how exposed it is. Outdoor items are warned about rain, wind, heat
/// and cold; items that may be outdoors only about strong weather; journeys only about severe weather, with a lower
/// wind limit on the water; indoor items never.
/// </summary>
public static class WeatherAdvice
{
    /// <summary>How far ahead forecasts become reminders.</summary>
    public static readonly Duration ReminderReach = Duration.FromDays(3);

    public static IReadOnlyList<WeatherConcern> Concerns(ExposureEstimate exposure, IReadOnlyList<WeatherHour> hours)
    {
        if (exposure.Exposure == Exposure.Indoor || hours.Count == 0) return [];
        var result = new List<WeatherConcern>();
        var rain = Math.Round(hours.Sum(h => h.PrecipitationMm ?? 0), 1);
        // The heaviest hour; services with longer steps are spread evenly over their hours.
        var peak = hours.Select(h => (h.PrecipitationMm ?? 0) / h.Hours).DefaultIfEmpty().Max();
        // Rain counts when it is likely: a chance of at least 50 % (services without chances count as likely) and a
        // rainy condition or at least half a millimetre.
        var likely = hours.Any(h => (h.PrecipitationChance ?? 100) >= 50 && (h.Condition is >= WeatherCondition.Drizzle and not WeatherCondition.Snow || (h.PrecipitationMm ?? 0) >= 0.5));
        var gust = hours.Select(h => h.GustKmh ?? h.WindKmh * 1.4).Where(v => v is not null).DefaultIfEmpty().Max() ?? 0;
        var warmest = hours.Select(h => h.TemperatureC).Where(v => v is not null).DefaultIfEmpty().Max();
        var coldest = hours.Select(h => h.TemperatureC).Where(v => v is not null).DefaultIfEmpty().Min();
        if (hours.Any(h => h.Condition == WeatherCondition.Thunderstorm)) result.Add(new(ConcernKind.Thunderstorm, true, 0));
        var snow = hours.Any(h => h.Condition == WeatherCondition.Snow);
        var heavy = likely && (rain >= 10 || peak >= 4);
        switch (exposure.Exposure)
        {
            case Exposure.Outdoor:
                if (snow) result.Add(new(ConcernKind.Snow, false, 0));
                if (gust >= 50) result.Add(new(ConcernKind.Wind, gust >= 75, Math.Round(gust)));
                if (likely && (rain >= 1 || peak >= 0.5)) result.Add(new(ConcernKind.Rain, heavy, rain));
                if (warmest >= 30) result.Add(new(ConcernKind.Heat, warmest >= 35, warmest.Value));
                if (coldest <= 0) result.Add(new(ConcernKind.Cold, coldest <= -10, coldest.Value));
                break;
            case Exposure.Possibly:
                if (snow) result.Add(new(ConcernKind.Snow, false, 0));
                if (gust >= 75) result.Add(new(ConcernKind.Wind, true, Math.Round(gust)));
                if (heavy) result.Add(new(ConcernKind.Rain, true, rain));
                if (warmest >= 35) result.Add(new(ConcernKind.Heat, true, warmest.Value));
                if (coldest <= -10) result.Add(new(ConcernKind.Cold, true, coldest.Value));
                break;
            case Exposure.Travel:
                if (snow) result.Add(new(ConcernKind.Snow, true, 0));
                if (gust >= (exposure.OnWater ? 60 : 90)) result.Add(new(ConcernKind.Wind, true, Math.Round(gust)));
                if (likely && (rain >= 25 || peak >= 8)) result.Add(new(ConcernKind.Rain, true, rain));
                break;
        }
        return result.OrderByDescending(c => c.Severe).ThenBy(c => c.Kind).ToArray();
    }

    /// <summary>
    /// Reminders for items that start within <see cref="ReminderReach"/> and have weather concerns. The key changes when
    /// a new kind of weather appears, so a dismissed warning comes back only for something new. Severe weather within
    /// two days is urgent.
    /// </summary>
    public static IReadOnlyList<Reminder> Reminders(TripSnapshot trip, TripWeather weather, Instant now)
    {
        var result = new List<Reminder>();
        foreach (var item in weather.Items.Values.Where(i => i.Concerns.Count > 0))
        {
            if (trip.Find(item.Item) is not { } entity) continue;
            var span = ScheduleQueries.Span(trip, entity);
            if (span.Start is not { } start || start - now > ReminderReach || span.End < now) continue;
            var zone = ScheduleQueries.TripZone(trip); var date = start.InZone(zone).Date;
            var kinds = string.Join(",", item.Concerns.Select(c => c.Kind.ToString().ToLowerInvariant()).Order());
            result.Add(new("weather:" + entity.Id + ":" + NodaTime.Text.LocalDatePattern.Iso.Format(date) + ":" + kinds, ReminderKind.WeatherWarning, entity.Id, entity.Title, start, item.Concerns.Any(c => c.Severe) && start - now <= Duration.FromDays(2), [date]));
        }
        return result.OrderBy(r => r.Moment).ToArray();
    }
}
