# Changelog

All notable changes to TravelRepo. Generated from `docs/releases` by `python eng/changelog.py`; do not edit by hand.

## 0.11.0 – 2026-11-12

### Changes

- ProviderAccount says the account's number (Id) and the email it shows publicly (PublicEmail); GitHubProvider reads both from /user. They are init properties, so code that creates a ProviderAccount keeps working.
- GitHubProvider.NoReplyEmail builds GitHub's private address for commits, ID+login@users.noreply.github.com, which GitHub links to the account. Without the account's number it gives the older login@users.noreply.github.com, which GitHub links only for accounts made before July 2017. Clients use it to save versions under the account when the user has given no email of their own.

## 0.10.0 – 2026-11-12

### Changes

- TravelRepo.Git.LibGit2 runs on iPhone and iPad. eng/build-libgit2-ios.sh builds the libgit2 commit LibGit2Sharp loads (libgit2 1.8.6) as git2.xcframework for devices and the simulator, with the system's zlib and, as on Android, without HTTPS and SSH in libgit2, because ManagedHttpTransport uses .NET's HttpClient and with it the system's TLS. iOS loads libraries only from frameworks, so an app passes the path of git2.framework/git2 to LibGit2Library.Use before its first Git command.
- LibGit2Library.Use loads libgit2 at once and says why it cannot (DllNotFoundException), instead of failing at the first Git command. Calling it again with the same path does nothing; another path is refused. A test checks that LibGit2Library.Name is still the name LibGit2Sharp loads, so a LibGit2Sharp update that changes it is noticed here and not in an app.
- A device build for iOS needs the Mono interpreter (UseInterpreter), because LibGit2Sharp passes delegates to libgit2 without MonoPInvokeCallback; docs/spike-git-without-cli.md has the iOS notes.

## 0.9.0 – 2026-11-11

### Changes

- Costs have one trip currency, stored as `currency` in travel.yaml (TripCurrency). Totals, the trip's budget and who owes whom are in it. An amount charged on a card is kept with its rate and day (`converted`, Expenses.WithCharge), so it never moves with the rates; everything else is estimated with reference rates and marked as such.
- A booking has no price of its own any more: what it costs is its payments, the expenses that belong to it, paid or still to pay with an optional due date (`due`). Payments for ideas and cancelled bookings say what they would cost but are not counted. Reminders mention payments still to pay that are due within a week or overdue.
- Every expense names who shares it, so a traveller who joins later does not share earlier expenses by accident. Splits are equal, by amounts, by shares or in percent (ExpenseSplit). Marks for what was paid back keep the amount transferred in the trip currency (`settled[].converted`), and Expenses.Debts, the balances and PaymentPlan give who owes whom and the fewest payments that settle it.
- An expense can have a `date`, the day it was paid. Without one, the day of the plan it belongs to or the day of the card charge counts (Expenses.DateOf).
- One budget for the whole trip, plus budgets limited to a category, a person, a collection or several of them together (Budgets). A budget that cannot be converted to the trip currency yet stays in its own currency, and Budgets.ConvertTo lets clients offer to convert budgets when the trip currency changes.
- CostExport writes the costs as CSV with a comma, semicolon, tab or pipe, or as an ExportDocument for PDF and text, for everyone or for the part of chosen people. Plan exports show a booking's payments and who owes whom.
- CostUpgrade.Plan brings costs written by older versions into the current shape: booking prices become payments still to pay, expenses name who shares them, and the trip gets a currency. Nothing that was paid, owed or paid back changes, and clients save the plan only after the travellers agree.
- All of this is additive in format 1.0. Older data reads as before, and a booking's `price` is kept until the upgrade. Semantic merge keeps each person's entries apart, such as their part of an expense or what they paid back, and deleting a person or collection offers to delete budgets only for them (specification sections 34 to 36 and 54).
- MCP tools for costs: get_costs, add_expense, split_expense, set_charged, set_trip_currency, set_budget, record_payment, mark_paid and export_costs. They refuse values that would mislead, such as a negative charged amount, a budget of zero or marking a payment still to pay as paid back.

### Fixes

- Attached files such as e-mails keep their line endings whatever Git is set to convert. With core.autocrlf=input, as on many Macs, the git program stored an e-mail with other line endings than its hash names, and every other copy of the trip reported asset.corrupt and refused all changes. Every trip now carries a .gitattributes with `* -text`, and GitCliBackend runs Git with core.autocrlf=false. A trip that was already affected is repaired by its next version on the device that still has the original file.

## 0.8.0 – 2026-11-10

### Changes

- Exports can show every time in one chosen time zone, for example to read a plan from home: ExportOptions.TimeZone, --timezone in the CLI and timezone in the MCP tool export_text. Exact times, the days the schedule is grouped by, the date range, the time line and the days of comments follow that zone. All-day and day-part plans and approximate check-in and check-out times keep their planned day, and calendar files are unaffected (specification section 45.1).
- Exports can also show each time in more time zones below it, named by city and with the date when the day there is another: ExportOptions.OtherTimeZones, --also-timezones in the CLI and also_timezones in export_text.
- WeatherService.UpdateAsync(trip, refresh: true) fetches forecasts again before they expire, for when someone asks for fresh weather. Forecasts fetched in the last five minutes are left as they are, and so are those of services that set their own expiry, as MET Norway and the National Weather Service do (IWeatherProvider.ServiceSetsExpiry).

### Fixes

- German exports write the full stop after the day, such as "4. Juni 2027", "Freitag, 4. Juni" and "Fr, 4. Jun".

## 0.7.0 – 2026-11-05

### Changes

- The new TravelRepo.Git.LibGit2 package runs Git through libgit2 where there is no git program, such as on Android. LibGit2Backend answers GitRepository's commands with Git's output and messages, so errors are classified as before, and ManagedHttpTransport speaks Git's smart HTTP protocol through HttpClient, so the platform's TLS and certificates are used. TRAVELREPO_TEST_GIT=libgit2 runs the test suite through it. eng/build-libgit2-android.sh builds libgit2 for Android with 16 KB page alignment.
- GitRepository.PullAsync takes the remote's versions only as a fast-forward, for updates nobody watches. It never sends and never combines, and leaves a trip with changes or versions of its own as it was.
- TripAgenda lays out the plan day by day as data, with schedule items and their spans, groups with their steps, check-ins, check-outs and nights. A line belongs to the day it starts on in its own time zone; items past midnight continue on every day they touch.
- TripCalendar.Entries turns a trip's placed plans into calendar entries for device calendars. A stay can become its check-in and its check-out, and the iCalendar export uses the same entries.
- Connections.In finds changes between journeys with their layover, also between nearby places such as a city's second airport. A night back at a stay the travellers had already checked into ends the journey instead. Places.Distance measures between two positions.
- Places can carry `local_name` and `address.local`, their name and address in the local language and script (specification section 13), read through Places.LocalName and Places.LocalAddress. Format 1.0 is unchanged.
- WalletPass.Read reads Wallet passes (.pkpass) without a wallet app, including fields, colours and the barcode, so clients can show a boarding pass offline. DocumentText reads passes for search.
- TripCommands.Nest and Unnest move plans in and out of groups without cycles, LinkBooking links a booking to its plans in both directions, and BookedItems lists what a booking is for.
- PackingLists gains Rename, DeleteList, which deletes the items no other list holds, and MoveItem.
- ExportOptions.Comments and PackingExportOptions.Comments put comments into exports, oldest first with author and day. TripComments reads comments by target. The CLI takes --comments.
- ExchangeRates moves into TravelRepo.Core with Amount and Code for typed input.
- TripSummaries.Destinations lists the towns of a trip's stays, and TripSummaries.Town reads a place's town from its address.
- GitHub: IRepositoryAccessProvider tells when a GitHub App may use no repository and links to its installation page. Discovery reads eight repositories at a time, skips empty ones and reports its progress. Device sign-in keeps polling when a request is lost to a sleeping phone or a network change.
- MCP: list_connections, add_to_group, remove_from_group, get_bookings, link_booking, get_costs, add_expense and convert_currency are new, as are rename_packing_list, delete_packing_list and update_packing_item. The server stays offline: convert_currency uses only the rates a client downloaded.
- global.json accepts any .NET 10 SDK from 10.0.100 on. CI and releases still pin 10.0.401 through eng/sdk.json.

## 0.6.0 – 2026-10-24

### Changes

- Packing lists (specification section 37.1) use existing types, so the format stays at 1.0 and older clients see a collection of tasks. A list is a collection with `purpose: packing`; an item is a task with a `packing` component for its group, quantity, the people who bring it, `everyone` and `packed_by`.
- PackingLists works out who brings an item, who has packed it, what nobody has taken yet and how items are grouped, and keeps the task status in step. `packed_by` merges as a set, so people packing at the same time do not conflict.
- TripPeople suggests which person in a trip is the local user, by GitHub login first and then by Git e-mail, and only when exactly one person matches (specification section 51). ReminderKind.WhoAmI asks when nothing matches.
- ExportOptions and ExportFilter choose the parts, a date range, one person, detailed or compact and the paper size, with the rules in specification section 45.1. TripExport.Document describes the export without a renderer; HTML and plain text render it, and ICS follows the date range and person.
- Places.Location and ContactDetails read place data, which the weather now uses too. Notes are read from an item's `body` as well as from Markdown content entries.
- Stays.Runs joins consecutive nights at the same place. Exports show check-in, check-out and each night, name the trip's time zone, and show both local times when an item starts or ends in another zone. ExportTimeline places each day's plans on a time line.
- PackingExport writes packing lists for everyone, one person or what nobody brings yet, as text with check boxes or as CSV with the culture's separator and protection against formula cells.
- CLI: `export` takes `--from`, `--to`, `--person`, `--parts`, `--compact` and writes `.txt`. The new `export-lists` writes packing lists as `.txt` or `.csv`.
- MCP: get_packing_lists, create_packing_list, add_packing_item, assign_packing_item and set_packed work with packing lists, and export_text returns the plan or the packing lists as text.

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
