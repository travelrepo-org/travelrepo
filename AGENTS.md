# TravelRepo repository instructions

These rules apply to the TravelRepo repository in addition to the workspace-level `AGENTS.md`.

## Boundary

TravelRepo is a reusable open project.

It must not depend on:
- Jourfold,
- Avalonia,
- Jourfold view models,
- Jourfold design tokens,
- desktop-only UI concepts.

If a feature is useful to more than one client, prefer implementing the domain behavior here and exposing a stable API.

## Public API

Treat public API shape as a product surface.

Prefer:
- small interfaces,
- typed domain concepts,
- deterministic behavior,
- cancellation for I/O,
- testable abstractions at external boundaries.

Do not expose raw process output, UI types, or internal serialization details as public domain APIs unless the specification requires it.

## Format compatibility

Never silently drop unknown compatible data.

Every change to canonical TravelRepo behavior must consider:
- schema compatibility,
- serialization round trips,
- old/new client interaction,
- semantic diff,
- semantic merge,
- migration,
- conformance fixtures.

A schema or serialization change is incomplete without tests.

## Serialization

YAML is canonical for v1 structured repository data.

Keep schema validation separate from semantic validation.

Required semantic information must never exist only in YAML comments.

Avoid writer churn that makes Git diffs noisy.

## Git

Use real Git semantics behind the Git abstraction.

Do not modify global Git configuration.

Do not embed credentials in remote URLs or command-line arguments.

Git integration tests should use real temporary repositories.

## Merge

Entity identity is UUID-based, not filename-based.

Semantic diff and merge must preserve unknown fields and extension namespaces.

Do not reduce normal merge UX to line-based YAML conflicts merely because Git can do so.

## Time

Use Noda Time abstractions for travel-time semantics.

Tests must cover DST transitions, ambiguous local times, invalid local times, and multi-timezone journeys.

## Example data

Agents: read this file again before each task; it changes.

No real travel data ever appears in tests, fixtures, samples, examples, screenshots, comments, code, documentation, pull requests, reviews, commit messages or release notes. That covers real trips, people, places someone went to, bookings, amounts, card charges and dates.

Examples use only these places: the cities below and the places in and around them, such as their stations, airports, hotels, sights and day trips close by.

| Country | Cities | Time zone |
| --- | --- | --- |
| Germany | Hamburg, Berlin, Frankfurt | Europe/Berlin |
| France | Paris, Lyon, Nice | Europe/Paris |
| Japan | Tokyo, Kyoto, Osaka, Nikko, Nara | Asia/Tokyo |
| New Zealand | Auckland, Christchurch, Queenstown, Picton, Wellington | Pacific/Auckland |
| Canada | Montreal, Toronto, Ottawa | America/Toronto |
| Canada | Vancouver | America/Vancouver |
| Canada | St. John's | America/St_Johns |

Together they cover the cases that need a place: daylight saving time in Germany, France and Canada, the opposite season in New Zealand and none in Japan, with their ambiguous and invalid local times; the date line between New Zealand or Japan and Canada; several time zones in one country (Canada); an offset of half an hour (St. John's, UTC−3:30); and journeys by plane, train, car and ferry (Picton–Wellington for ferries). UTC, or an offset zone such as `Etc/GMT+3`, is fine where no place is meant, and so is a point that is not a destination, such as one near the pole for the midnight sun.

Currencies are EUR, JPY (no minor unit), NZD, CAD and USD (for example a booking paid online in dollars). KWD is used only where three decimals are needed, such as rounding tests. A test that needs more currencies than these, such as one that fills a list of recent currencies, uses ISO codes that belong to no country: XTS (reserved for testing), XXX, XAU and XAG.

People are made up: the sample trip's Alex and Sam, or other first names that belong to nobody in particular. Never a real person, account or e-mail address; e-mail addresses and web addresses use the reserved example domains (`example.com`, `example.org`, `.example`, `.invalid`). GitHub accounts in tests and examples are handles that do not exist on GitHub, such as `alex-travels` and `sam-travels`; plain first names such as `alex` are real accounts.

- The default example is the sample trip "Spring in Japan" (Jourfold's `src/Jourfold.Application/SampleTrip.cs`): Alex and Sam fly from Frankfurt to Tokyo and travel on to Nikko, Kyoto, Nara and Osaka, paying in EUR and JPY. An edge case it does not cover is set on that trip where it fits, otherwise in the places above.
- When someone describes a problem or a feature with an example from a real trip, an agent translates it into the matching case in the places above before writing anything down. It keeps what matters about the example, such as an odd amount, a rounding case or a time zone change, and replaces the names, places, amounts and dates.
- This applies to existing tests, fixtures and documentation too: anything outside these places, currencies and people is moved into them when it is touched.

## Testing

The conformance suite is part of the product.

Include fixtures for:
- minimal repositories,
- Tokyo,
- Hamburg,
- New Zealand,
- flexible time,
- nested items,
- multiple participants,
- unknown extensions,
- merge conflicts,
- deduplicated assets.

Use property-based tests for suitable serialization, time, ID, and merge invariants.

## Releases

A fix or feature pull request never changes the version and never adds release notes. The version, `docs/releases/<version>.yaml`, `CHANGELOG.md` and anything else labelled with the version go into a dedicated release pull request from `release/<version>`, which contains the fix or feature branches plus one release commit. Its description says which pull requests to merge first.

- Anything that can count as a patch stays a patch (0.x.y+1); a new minor version only when the change clearly is one, and only after asking.

## Documentation

Keep the format specification, public SDK documentation, CLI docs, provider docs, and plugin docs consistent with code.

README prose should stay plain, human, and practical.
