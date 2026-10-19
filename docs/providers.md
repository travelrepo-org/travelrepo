# Provider architecture

Capabilities live in `TravelRepo.Providers.Abstractions`. A provider implements only supported capabilities: authentication, discovery, creation, privacy, collaboration, search or sharing. Git operations remain in `TravelRepo.Git`; local trip editing never requires a provider.

`GitHubProvider` implements GitHub App device authorization. Register an App with device flow enabled, supply its public client ID, and install it for the desired repositories. Do not embed an App private key or client secret in the desktop client. The default path requests no classic OAuth `repo` scope.

Discovery enumerates user installations and granted repositories, paginates results, and reads `travel.yaml` to determine compatibility. Repository creation sends `private: true` and leaves initialization to the local trip. Privacy is exposed as data for clients to warn appropriately. Collaborator invitations request push access and may fail when account/App permissions are insufficient.

Tokens use the supplied `ISecretStore`. Jourfold uses Windows Credential Manager or Linux Secret Service and otherwise keeps session-only credentials. `ICredentialBroker` supplies HTTPS Git credentials through scoped child-process configuration in environment variables; they are not included in command arguments or repository config. Other hosts retain system helpers and normal SSH behavior.

Inject `HttpClient` to test network boundaries. Live tests require a configured App and consenting disposable test accounts. HTTP seam tests do not prove GitHub has granted the required permissions.

GitHub documentation consulted: [GitHub App user tokens and device flow](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-a-user-access-token-for-a-github-app).

## Weather

`TravelRepo.Providers.Weather` adds weather to trips. Clients contact a weather service only after the person turns weather on, and only with coordinates rounded to two decimals (about one kilometre). Nothing else about the trip is sent, and nothing is written into it.

| Service | Key | Reach | Notes |
| --- | --- | --- | --- |
| Open-Meteo | none | 16 days, hourly | Default and fallback. Free for non-commercial use, CC BY 4.0. Also supplies climate averages from its historical archive. |
| MET Norway | none | about 9 days | Hourly for two to three days, then six-hour steps. CC BY 4.0. Strongest in the Nordic countries. |
| National Weather Service | none | about 6 days, hourly | United States only. Public domain. |
| Visual Crossing | personal | 15 days, hourly | The key is read from the `ISecretStore` entry `weather.visual-crossing.key`. |

`WeatherService` works out which places and dates a trip needs, asks the chosen service first and falls back to Open-Meteo for points the service does not cover, days beyond its reach, or when it fails. Open-Meteo answers for all points in one request. Beyond the forecasts it uses climate averages: the last ten complete years of the same calendar days, three days either side. Everything goes into `WeatherCache` on the device. Forecasts expire after two hours (MET Norway and the National Weather Service set their own expiry) and are not shown once they are more than a day old.

`WeatherService.CacheOnly` reads the cache without being able to download: its services sit on an HTTP client that refuses every request, and `UpdateAsync` throws. The MCP server uses it, so assistants see only what the client already fetched.

Each item gets an outlook per place: a forecast within seven days, a trend up to the forecast reach, climate beyond it. Each future trip day gets one at the place where most of its daytime is planned, or where that night is spent.

`OutdoorEstimates` in `TravelRepo.Core` decides how much weather matters for an item. The `outdoor` field wins (spec section 25.2). Otherwise walking and cycling count as outdoors, other journeys as travel, stays as indoors, and the category gives a first estimate that a short list of English and German title words can move ("Wanderung", "hike" towards outdoors; "museum", "caves" towards indoors). `WeatherAdvice` then warns about rain, wind, heat and cold for outdoor items, about strong weather for items that may be outdoors, and only about severe weather for journeys, with a lower wind limit on ferries. Concerns within three days become `WeatherWarning` reminders.
