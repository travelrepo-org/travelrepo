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
