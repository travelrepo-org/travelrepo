using System.Globalization;
using System.Text.Json.Nodes;
using NodaTime;
using NodaTime.Text;
using TravelRepo.Core;
using TravelRepo.Documents;
using TravelRepo.Repository;

namespace TravelRepo.Native;

/// <summary>
/// Edits and read models of the plan that apps offer beyond whole-entity edits: the words a trip uses, deleting with
/// a preview, duplicating, scheduling and moving to another day, comments, calendar entries, the upgrade of older
/// cost data, e-mails as they are shown, and replacing a document's file. Every change is one transaction.
/// </summary>
internal static class PlanCommands
{
    public static readonly IReadOnlyList<string> Names =
        ["vocabulary", "suggest_category", "delete_preview", "delete_entities", "duplicate", "schedule", "move_to_day", "comments", "add_comment",
         "calendar", "cost_upgrade_preview", "cost_upgrade_apply", "read_mail", "replace_document"];

    public static readonly IReadOnlyList<string> ReadOnly = ["vocabulary", "suggest_category", "delete_preview", "comments", "calendar", "cost_upgrade_preview", "read_mail"];

    public static async Task<(bool Found, JsonNode? Result)> RunAsync(NativeSession session, string command, Arguments args, CancellationToken ct)
    {
        if (!Names.Contains(command)) return (false, null);
        if (session.ReadOnly && !ReadOnly.Contains(command)) throw new DomainException("session.read_only", "This session is read-only.");
        var state = await session.ReadAsync(ct);
        var trip = state.Trip;
        switch (command)
        {
            case "vocabulary":
                return (true, new JsonObject
                {
                    ["schedule_categories"] = Strings(ScheduleCategories.Standard),
                    ["transport_types"] = Strings(ScheduleCategories.TransportTypes),
                    ["statuses"] = Strings(ScheduleCategories.Statuses),
                    ["expense_categories"] = Strings(ExpenseCategories.Standard),
                    ["custom_expense_categories"] = Strings(ExpenseCategories.Custom(trip))
                });
            case "suggest_category":
                return (true, new JsonObject { ["category"] = ExpenseCategories.SuggestFromTitle(args.Required("title"), trip) });
            case "delete_preview":
                {
                    var plan = TripDeletion.Plan(trip, Ids(args.Strings("ids")));
                    return (true, new JsonObject
                    {
                        ["deleted"] = Summaries(plan.Deleted.Where(e => plan.Requested.Contains(e.Id))),
                        ["dependents"] = Summaries(plan.Dependents),
                        ["updated"] = Summaries(plan.Updated),
                        ["removed_resources"] = Strings(plan.RemovedResources),
                        ["unused"] = Summaries(plan.Unused)
                    });
                }
            case "delete_entities":
                {
                    // To delete what became unused as well, the app passes those ids from delete_preview too.
                    var plan = TripDeletion.Plan(trip, Ids(args.Strings("ids")));
                    return (true, await SaveAsync(session, state, plan.Edits, plan.ResourceEdits, ct));
                }
            case "duplicate":
                {
                    var copies = TripCommands.DuplicateSubtree(trip, Id(args.Required("id")));
                    if (args.String("title") is { Length: > 0 } title) copies[0].Data[copies[0].Data.ContainsKey("name") && !copies[0].Data.ContainsKey("title") ? "name" : "title"] = title;
                    var saved = await SaveAsync(session, state, copies.Select(c => new EntityEdit(c.Id, c)).ToArray(), [], ct);
                    saved["id"] = copies[0].Id.ToString();
                    return (true, saved);
                }
            case "schedule":
                {
                    var item = Item(trip, args.Required("id"));
                    var zone = args.Required("timezone");
                    if (DateTimeZoneProviders.Tzdb.GetZoneOrNull(zone) is null) throw new DomainException("argument.invalid", "Unknown time zone " + zone + ".");
                    var start = new ZonedTime(args.Required("local"), zone, args.String("offset"));
                    var minutes = args.Int("minutes", 60);
                    if (minutes < 0) throw new DomainException("argument.invalid", "minutes cannot be negative.");
                    return (true, await SaveAsync(session, state, [new(item.Id, Scheduled(item, start, Duration.FromMinutes(minutes)))], [], ct));
                }
            case "move_to_day":
                {
                    var item = Item(trip, args.Required("id"));
                    var day = LocalDatePattern.Iso.Parse(args.Required("day")).TryGetValue(default, out var parsed) ? parsed : throw new DomainException("argument.invalid", "day must be yyyy-MM-dd.");
                    return (true, await SaveAsync(session, state, [new(item.Id, MovedTo(trip, item, day, args.String("offset")))], [], ct));
                }
            case "comments":
                {
                    var target = Id(args.Required("target"));
                    return (true, new JsonObject
                    {
                        ["comments"] = new JsonArray(TripComments.On(trip, target).Select(c => (JsonNode)new JsonObject
                        {
                            ["id"] = c.Id.ToString(),
                            ["author"] = c.Data["author"]?.ToString(),
                            ["author_name"] = TripComments.Author(trip, c)?.Title,
                            ["created_at"] = TripComments.CreatedAt(c) is { } at ? InstantPattern.General.Format(at) : null,
                            ["body"] = c.Data["body"]?.ToString()
                        }).ToArray())
                    });
                }
            case "add_comment":
                {
                    var target = trip.Find(Id(args.Required("target"))) ?? throw new DomainException("entity.missing", "There is no entity " + args.Required("target") + ".");
                    var author = trip.Find(Id(args.Required("author")));
                    if (author is not { Type: "person" }) throw new DomainException("argument.invalid", "author must be a person of this trip.");
                    var body = args.Required("body").Trim();
                    if (body.Length == 0) throw new DomainException("argument.missing", "Write something to comment.");
                    var comment = Entity.Create("comment", "");
                    comment.Data.Remove("title");
                    comment.Data["target"] = new JsonObject { ["type"] = target.Type, ["id"] = target.Id.ToString() };
                    comment.Data["author"] = author.Id.ToString();
                    comment.Data["created_at"] = InstantPattern.General.Format(SystemClock.Instance.GetCurrentInstant());
                    comment.Data["body"] = body;
                    var saved = await SaveAsync(session, state, [new(comment.Id, comment)], [], ct);
                    saved["id"] = comment.Id.ToString();
                    return (true, saved);
                }
            case "calendar":
                return (true, new JsonObject
                {
                    ["entries"] = new JsonArray(TripCalendar.Entries(trip, staysAsCheckInOut: args.Bool("stays_as_check_in_out", false)).Select(e => (JsonNode)new JsonObject
                    {
                        ["uid"] = e.Uid,
                        ["item"] = e.Item.Id.ToString(),
                        ["title"] = e.Item.Title,
                        ["role"] = ClientCommands.Snake(e.Role.ToString()),
                        ["kind"] = ClientCommands.Snake(e.Kind.ToString()),
                        ["all_day"] = e.AllDay,
                        ["start"] = Time(e.Start),
                        ["end"] = Time(e.End),
                        ["date"] = e.Date is { } d ? LocalDatePattern.Iso.Format(d) : null,
                        ["zone"] = e.Zone,
                        ["location"] = e.Location,
                        ["description"] = e.Description,
                        ["status"] = e.Status
                    }).ToArray())
                });
            case "cost_upgrade_preview":
                {
                    var plan = CostUpgrade.Plan(trip, args.String("fallback_currency"));
                    return (true, new JsonObject
                    {
                        ["needed"] = !plan.IsEmpty,
                        ["changes"] = new JsonArray(plan.Changes.Select(c => (JsonNode)new JsonObject
                        {
                            ["kind"] = ClientCommands.Snake(c.Kind.ToString()),
                            ["entity"] = c.Entity.ToString(),
                            ["title"] = c.Title,
                            ["amount"] = Money(c.Amount),
                            ["other"] = Money(c.Other)
                        }).ToArray())
                    });
                }
            case "cost_upgrade_apply":
                {
                    // As in Jourfold: when nothing else is unsaved, the upgrade becomes a version of its own; otherwise
                    // it joins the next version, so the travellers' own changes are never filed under it.
                    var plan = CostUpgrade.Plan(trip, args.String("fallback_currency"));
                    if (plan.IsEmpty) return (true, new JsonObject { ["revision"] = session.Remember(state), ["version"] = null });
                    var clean = string.IsNullOrEmpty(await session.Git.StatusAsync(ct));
                    var saved = await SaveAsync(session, state, plan.Edits.Select(e => new EntityEdit(e.Id, e)).ToArray(), [], ct);
                    saved["version"] = clean ? await session.Git.CreateVersionAsync(args.String("message", "Upgrade costs"), "cost-upgrade", ct) : null;
                    return (true, saved);
                }
            case "read_mail":
                return (true, ReadMail(trip, args));
            case "replace_document":
                {
                    var entity = await session.Repository.ReplaceDocumentAsync(Id(args.Required("id")), args.Required("source"), args.Required("media_type"), args.Bool("allow_large", false), ct);
                    return (true, new JsonObject { ["id"] = entity.Id.ToString(), ["revision"] = session.Remember(await session.ReadAsync(ct)) });
                }
        }
        return (false, null);
    }

    /// <summary>
    /// A stored e-mail as an app shows it: headers, plain text, HTML cleaned for display (remote images only when
    /// allowed), inline images and attachments as base64.
    /// </summary>
    private static JsonObject ReadMail(TripSnapshot trip, Arguments args)
    {
        var document = trip.Find(Id(args.Required("id")));
        if (document is not { Type: "document" }) throw new DomainException("entity.missing", "There is no document " + args.Required("id") + ".");
        if (!Mail.IsMail(document.Data["media_type"]?.ToString())) throw new DomainException("document.not_mail", "This document is not an e-mail.");
        var hash = document.Data["blob"]?["hash"]?.ToString() ?? "";
        var bytes = hash.Length == 64 ? trip.Resources.GetValueOrDefault("assets/sha256/" + hash[..2] + "/" + hash) : null;
        if (bytes is null) throw new DomainException("resource.missing", "The e-mail's file is missing from the trip.");
        var mail = Mail.Read(bytes);
        var html = mail.Html is null ? null : MailHtml.Sanitize(mail.Html, args.Bool("allow_remote_images", false));
        static JsonArray Addresses(IEnumerable<MailAddress> list) => new(list.Select(a => (JsonNode)new JsonObject { ["name"] = a.Name, ["address"] = a.Address }).ToArray());
        return new JsonObject
        {
            ["subject"] = mail.Subject,
            ["from"] = Addresses(mail.From),
            ["to"] = Addresses(mail.To),
            ["cc"] = Addresses(mail.Cc),
            ["date"] = mail.Date?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["text"] = mail.Text,
            ["text_from_html"] = mail.TextFromHtml,
            ["html"] = html?.Html,
            ["remote_images"] = html?.RemoteImages ?? mail.RemoteImages,
            ["remote_images_allowed"] = html?.RemoteImagesAllowed ?? false,
            ["inline_images"] = new JsonArray((mail.InlineImages ?? []).Select(i => (JsonNode)new JsonObject
            {
                ["content_id"] = i.ContentId,
                ["media_type"] = i.MediaType,
                ["base64"] = Convert.ToBase64String(i.Content)
            }).ToArray()),
            ["attachments"] = new JsonArray(mail.Attachments.Select(a => (JsonNode)new JsonObject
            {
                ["name"] = Mail.SafeName(a.FileName),
                ["media_type"] = a.MediaType,
                ["size"] = a.Content.Length,
                ["base64"] = Convert.ToBase64String(a.Content)
            }).ToArray())
        };
    }

    /// <summary>
    /// An exact time from <paramref name="start"/>, saved with its offset; the end keeps the arrival time zone an item
    /// already had. A local time that does not exist (<c>time.gap</c>) or exists twice without an offset
    /// (<c>time.ambiguous</c>) is refused, so the app can ask which one is meant.
    /// </summary>
    private static Entity Scheduled(Entity item, ZonedTime start, Duration duration)
    {
        var copy = item.Copy();
        var arrivalZone = copy.Data["time"]?["end"]?["timezone"]?.ToString() ?? start.Timezone;
        var instant = start.ToInstant();
        copy.Data["time"] = new JsonObject { ["precision"] = "exact", ["start"] = ZonedTime.At(instant, start.Timezone).ToJson(), ["end"] = ZonedTime.At(instant + duration, arrivalZone).ToJson() };
        return copy;
    }

    /// <summary>
    /// Another day at the same local time and the same length; an item without a time keeps its kind of day. On a day
    /// when that local time is skipped or repeated, <see cref="Scheduled"/> refuses it unless <paramref name="offset"/> says which.
    /// </summary>
    private static Entity MovedTo(TripSnapshot trip, Entity item, LocalDate day, string? offset)
    {
        var entry = Flatten(TripAgenda.Build(trip)).FirstOrDefault(e => e.Item.Id == item.Id);
        if (entry is { IsTimed: true, Start: { } start, Zone: { } zoneId } && DateTimeZoneProviders.Tzdb.GetZoneOrNull(zoneId) is { } zone)
        {
            var length = entry.End is { } end && end > start ? end - start : Duration.FromHours(1);
            var local = day.At(start.InZone(zone).TimeOfDay).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
            return Scheduled(item, new ZonedTime(local, zoneId, offset), length);
        }
        var copy = item.Copy();
        var date = LocalDatePattern.Iso.Format(day);
        if (copy.Data["time"] is JsonObject time && time.ContainsKey("date")) time["date"] = date;
        else copy.Data["time"] = new JsonObject { ["precision"] = "all_day", ["date"] = date };
        return copy;
    }

    private static IEnumerable<AgendaEntry> Flatten(Agenda agenda)
    {
        static IEnumerable<AgendaEntry> Walk(AgendaEntry e) => new[] { e }.Concat(e.Steps.SelectMany(Walk));
        return agenda.Days.SelectMany(d => d.Entries).Concat(agenda.Unscheduled).SelectMany(Walk);
    }

    private static async Task<JsonObject> SaveAsync(NativeSession session, RepositoryState state, IReadOnlyList<EntityEdit> edits, IReadOnlyList<ResourceEdit> resources, CancellationToken ct)
    {
        var saved = await session.Repository.ApplyAsync(state, edits, ct, resources);
        return new JsonObject { ["revision"] = session.Remember(saved), ["diagnostics"] = ClientCommands.Diagnostics(saved.Diagnostics) };
    }

    private static Entity Item(TripSnapshot trip, string id) =>
        trip.Find(Id(id)) is { Type: "schedule_item" } item ? item : throw new DomainException("entity.missing", "There is no plan item " + id + ".");

    private static Guid Id(string text) => Guid.TryParse(text, out var id) ? id : throw new DomainException("argument.invalid", text + " is not an entity id.");
    private static Guid[] Ids(IEnumerable<string> texts) => texts.Select(Id).ToArray();
    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode)v).ToArray());
    private static JsonArray Summaries(IEnumerable<Entity> entities) =>
        new(entities.Select(e => (JsonNode)new JsonObject { ["id"] = e.Id.ToString(), ["type"] = e.Type, ["title"] = e.Title }).ToArray());
    private static string? Time(Instant? instant) => instant is { } i ? InstantPattern.General.Format(i) : null;
    private static JsonNode? Money(Money? m) => m is null ? null : new JsonObject { ["value"] = m.Value.ToString(CultureInfo.InvariantCulture), ["currency"] = m.Currency };
}
