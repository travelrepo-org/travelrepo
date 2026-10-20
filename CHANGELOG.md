# Changelog

All notable changes to TravelRepo. Generated from `docs/releases` by `python eng/changelog.py`; do not edit by hand.

## 0.5.0 – 2026-10-20

### Changes

- The new TravelRepo.Providers.Weather package gives clients weather for a trip without writing anything into it. Built-in services are Open-Meteo (default and fallback), MET Norway, the US National Weather Service and Visual Crossing with a personal key. Requests contain only coordinates rounded to two decimals.
- WeatherService works out the places and dates a trip needs, asks the chosen service first and falls back to Open-Meteo. Beyond the forecasts it uses climate averages from the last ten years. WeatherCache keeps everything on the device.
- WeatherAdvice decides when weather matters for an item and turns concerns within three days into WeatherWarning reminders.
- The optional, inheritable `outdoor` field states whether an item happens outdoors (specification section 25.2). Without it, OutdoorEstimates estimates it from the transport type, the category and English and German title words, and says why.
- MCP: get_weather returns the weather a client has already cached, never live data, through WeatherService.CacheOnly, which cannot download. get_schedule shows the outdoor estimate.
- MailHtml makes e-mail HTML safe to show in its original layout, and MailMessage carries the HTML part and its inline images.

### Fixes

- Running Git in a trip folder that was moved or deleted reports git.folder_missing instead of failing with a platform error.

## 0.4.0 – 2026-10-19

### Changes

- TripReferences and TripDeletion plan deletions that remove references instead of failing, delete dependent comments, list unused places, documents, notes and bookings, and free unused blobs and notes.
- Stays resolve to the nights they cover, with check-in and check-out taking precedence and documented fallbacks (specification section 24). Writers mirror them into time; a disagreeing time is the warning stay.time_mismatch.
- Optional stay details (check-in and check-out windows, access, approximate hours, cancellation, breakfast, parking, arrival notes), booking cancellation and place contact. All additive in format 1.0.
- TripReminders derives reminders for cancellation deadlines, tasks due, late arrivals, nights without a stay and unconfirmed bookings.
- The new TravelRepo.Documents package reads e-mails (.eml) and the text of PDFs, and TripCommands attaches documents to entities.
- Git failures throw GitCommandException with a recognised cause and redacted details; DiagnoseAsync and safe repairs support clients.
- Variant management - an overview of local and remote variants, deletion with a recycle bin, cleanup of variants deleted on the remote, local-only copies, syncing variants that are not open, and merged_into.
- MCP: delete_entity removes references, and list_stays, merge_stays, read_document, import_document and list_reminders are new.

### Fixes

- Sync follows the tracked upstream branch instead of pushing a copy under the local branch name.
- Git status and staging only consider trip content, so unrelated files no longer block versions.
- Schema validation of one schema runs on one thread at a time; parallel validation could skip rules.
- System.Security.Cryptography.Pkcs 10.0.13 replaces a vulnerable version pulled in by MimeKit.

## 0.3.0 – 2026-10-04

### Changes

- A local MCP server and an agent guide let AI assistants read and edit a trip through validated TravelRepo commands.

## 0.2.0 – 2026-10-04

### Changes

- Providers can report the signed-in account and forget the sign-in.
- Releases become full releases once their packages are attached.

## 0.1.0 – 2026-10-04

### Changes

- First release of the TravelRepo format 1.0, SDK, Git and merge layers, CLI, provider and plugin abstractions and the conformance suite.
