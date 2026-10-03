# Schema profile 1.0

Normative structural artifacts are `schemas/travelrepo/1.0/*.json`. They use JSON Schema 2020-12 and can be used by non-.NET implementations after parsing safe YAML into JSON-compatible values. The shipped specification is `TRAVELREPO_SPEC.md`.

`trip.json` describes the manifest. Each built-in entity has a separate schema. `custom.json` accepts namespaced custom types. All schemas retain additional properties for forward compatibility. Unknown properties are data, not permission to bypass recognized required fields.

IDs use UUIDv7. Monetary amounts are strings. Zoned times contain a local ISO date-time and IANA timezone, with an optional offset disambiguator. Semantic validation handles timezones, references and graph rules separately.

Booking states are `idea`, `pending`, `reserved`, `confirmed`, `cancelled`, and `completed`. Task states are `open`, `in_progress`, `completed`, and `cancelled`. These vocabularies resolve open specification items for this implementation profile.

Structured files are written with stable key order, UTF-8 and LF. Strings are explicitly quoted to protect values such as `001` and `false`. Duplicate keys, anchors, aliases, multiple documents and explicit tags are rejected. Formatting and comments may change; unknown values must not.

There is no implicit schema migration. Unsupported manifest versions produce validation errors. Future migrations must retain before-images and produce reviewable changes.
