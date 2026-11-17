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

No real travel data ever appears in tests, fixtures, examples, samples, comments, code, documentation, pull requests, reviews, commit messages or release notes. That covers real trips, people, places someone went to, bookings, amounts, card charges and dates.

- Use the trip to Japan that Jourfold ships as its sample trip ("Spring in Japan", in Jourfold's `src/Jourfold.Application/SampleTrip.cs`): its people (Alex and Sam), its places (Frankfurt, Tokyo, Nikko, Kyoto, Nara and the others it has) and its currencies, EUR and JPY, with USD where a third currency is needed.
- An edge case that trip does not cover yet is set on the same trip to Japan: another place in Japan, another plan, booking or expense on it, or a USD payment for something on it.
- When someone describes a problem or a feature with an example from a real trip, an agent translates it into the matching case on the Japan trip before writing anything down. It keeps what matters about the example, such as an odd amount, a rounding case or a time zone change, and replaces the names, places, amounts and dates.
- The conformance fixtures listed under Testing that are older than this rule (Aachen, New Zealand and the others) stay as they are. New and changed tests and examples follow this rule.

## Testing

The conformance suite is part of the product.

Include fixtures for:
- minimal repositories,
- Tokyo,
- Aachen,
- New Zealand,
- flexible time,
- nested items,
- multiple participants,
- unknown extensions,
- merge conflicts,
- deduplicated assets.

Use property-based tests for suitable serialization, time, ID, and merge invariants.

## Documentation

Keep the format specification, public SDK documentation, CLI docs, provider docs, and plugin docs consistent with code.

README prose should stay plain, human, and practical.
