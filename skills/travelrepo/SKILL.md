---
name: travelrepo
description: Read and edit TravelRepo trips (travel plans stored as YAML files in a Git repository, as used by Jourfold). Use when asked to plan, review or change a trip's schedule, places, people, bookings, tasks, notes or costs.
---

# Working on a TravelRepo trip

A TravelRepo trip is a folder with `travel.yaml` at its root, one YAML file per entity in folders such as `schedule/`, `places/` and `people/` (named `<id>.yaml`), and Git history. Jourfold and other clients read the same files, so every change you make is visible to the people planning the trip.

## How to make changes

Prefer the TravelRepo MCP tools when they are available (`get_trip`, `get_schedule`, `create_entity`, `update_entity`, ...). They validate every change and write it atomically, so the trip cannot end up half-written or invalid.

Without the MCP tools, edit the YAML files directly and run `travelrepo validate <trip folder>` afterwards. Exit code 2 means the trip is invalid; fix the reported problems before doing anything else.

Rules in both cases:

- Look before you write. Read the trip overview and the relevant entities first, and reuse existing places and people instead of creating duplicates.
- Change only what you were asked to change. Keep unknown fields and everything under `extensions` as they are.
- Never write credentials, tokens or other secrets into the trip. It may be shared with other people.
- Do not create versions (Git commits), switch variants or push unless the person asks for it. Jourfold shows your edits as unsaved changes, and the person decides when to keep them as a version.
- Keep the trip's language (`language` in `travel.yaml`) for titles and notes you write.
- When something is uncertain, such as opening hours, prices or travel times, say so in a note or leave the field empty rather than inventing a value.

## Entities

Every entity has `id` (a UUID, version 7), `type` and usually `extensions: {}`. References between entities always use IDs, never titles or file paths. With the MCP tools, IDs are created for you.

| type | stored in | required fields | common optional fields |
|---|---|---|---|
| `trip` | `travel.yaml` | `title`, `language`, `variant` | `dates.start`, `dates.end` (`YYYY-MM-DD`), `default_timezone`, `participants` (person IDs) |
| `schedule_item` | `schedule/` | `title`, `status` | `time`, `category`, `default_place`, `duration`, `participants`, `components`, `children`, `content`, `constraints`, `tags` |
| `place` | `places/` | `name` | `location.latitude`, `location.longitude`, `address.formatted`, `timezone` |
| `person` | `people/` | `display_name` | `roles` |
| `booking` | `bookings/` | | `provider.name`, `reference`, `status`, `travelers`, `items` (schedule item IDs), `price` |
| `task` | `tasks/` | `title`, `status` (`open`, `in_progress`, `completed`, `cancelled`) | `assigned_to`, `due.date`, `related` (`[{type, id}]`) |
| `expense` | `expenses/` | `title`, `amount`, `estimated` (true or false) | `paid_by`, `related` |
| `budget` | `budgets/` | `title`, `amount` | `scope` |
| `collection` | `collections/` | `title`, `items` (entity IDs) | |
| `note` | `documents/` | `title` | `body` (Markdown) |
| `comment` | `comments/` | `target`, `author` (person ID), `created_at`, `body` | |

Money is `{ value: "149.90", currency: EUR }`: the value is a decimal string and the currency an ISO 4217 code. Durations use ISO 8601, for example `PT1H30M`.

## Schedule items

`status` is one of `idea`, `planned`, `reserved`, `confirmed`, `completed`, `cancelled`. New suggestions that the travellers have not agreed on yet should be `idea`.

`category` is one of `activity`, `sightseeing`, `food`, `culture`, `nature`, `shopping`, `nightlife`, `meeting`, `free_time`, `transport`, `accommodation`, `other`.

`default_place` is the ID of a place entity. `participants` is `{ inherit: true }` to take the parent's people, or `{ values: [person IDs] }`.

Transport and stays are components:

```yaml
components:
  transport:
    type: flight        # flight, train, bus, car, taxi, rideshare, ferry, ship, bicycle, walking, other
    carrier: ANA
    flight_number: NH204
    departure: { place: <place ID> }
    arrival: { place: <place ID> }
  accommodation:
    place: <place ID>
```

A stay overlapping other plans is normal and not a conflict. Overlapping activities are allowed too; they are shown side by side.

`children` lists the IDs of nested schedule items (for example the stops of a day trip). Children are separate entities with their own files.

## Time

Times are local wall-clock time plus an IANA timezone, never UTC instants. Write `local` with seconds and without an offset (`2027-05-12T13:20:00`), dates as `YYYY-MM-DD`, and timezones as IANA names such as `Asia/Tokyo` or `Europe/Berlin`. Use the timezone of the place where the item happens; a flight departs in one timezone and arrives in another.

```yaml
time:
  precision: exact            # or approximate (shown with "~")
  start: { local: 2027-05-12T13:20:00, timezone: Europe/Berlin }
  end:   { local: 2027-05-13T08:35:00, timezone: Asia/Tokyo }
```

Other forms:

- `{ precision: window, earliest: {local, timezone}, latest: {local, timezone} }`: sometime in this range.
- `{ precision: day_part, date: 2027-05-12, day_part: afternoon, timezone: Asia/Tokyo }`: `morning`, `afternoon`, `evening` or `night`.
- `{ precision: all_day, date: 2027-05-12 }`.
- `time: null`: not scheduled yet. Jourfold lists these items in its Inbox; give them a `duration` when you know it.

Without an `end`, an exact item lasts its `duration` or one hour.

## Notes and links

Longer text belongs in `content`, a list of entries:

```yaml
content:
  - type: markdown
    file: documents/<UUID>.md     # the Markdown file next to the YAML files
  - type: link
    url: https://example.org/
```

With the MCP tools, use `add_note`; it creates the Markdown file and the entry together.

## Checking your work

After a series of changes, call `validate` (or run `travelrepo validate`) and `get_schedule` for the affected days. Then tell the person what you changed, with titles and times, so they can review it in Jourfold.
