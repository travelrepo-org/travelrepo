using TravelRepo.Mcp;

namespace TravelRepo.Native;

/// <summary>A trip command of the MCP server, called by name with JSON arguments.</summary>
internal sealed record Tool(string Name, bool ReadOnly, Func<TripTools, Arguments, CancellationToken, Task<string>> Run);

/// <summary>
/// The commands of <see cref="TripTools"/>, by the names and argument names the MCP server uses. Written out instead of
/// found by reflection so it works under NativeAOT; <c>NativeTests</c> checks it against the MCP tools.
/// </summary>
internal static class Tools
{
    public static readonly IReadOnlyList<Tool> All =
    [
        new("read_guide", true, (t, a, ct) => Task.FromResult(t.ReadGuide())),
        new("get_trip", true, (t, a, ct) => t.GetTrip(ct)),
        new("list_entities", true, (t, a, ct) => t.ListEntities(a.String("type"), a.String("text"), a.Int("limit", 200), ct)),
        new("search", true, (t, a, ct) => t.Search(a.Required("query"), a.String("kinds"), a.Bool("notes", false), a.Int("limit", 30), ct)),
        new("get_entity", true, (t, a, ct) => t.GetEntity(a.Required("id"), ct)),
        new("get_schedule", true, (t, a, ct) => t.GetSchedule(a.String("from"), a.String("to"), ct)),
        new("list_stays", true, (t, a, ct) => t.ListStays(ct)),
        new("merge_stays", false, (t, a, ct) => t.MergeStays(a.Strings("ids"), ct)),
        new("get_packing_lists", true, (t, a, ct) => t.GetPackingLists(a.String("person"), ct)),
        new("export_text", true, (t, a, ct) => t.ExportText(a.String("kind", "plan"), a.String("person"), a.String("from"), a.String("to"), a.String("parts"), a.Bool("compact", false), a.String("timezone"), a.String("also_timezones"), ct)),
        new("create_packing_list", false, (t, a, ct) => t.CreatePackingList(a.Required("title"), ct)),
        new("add_packing_item", false, (t, a, ct) => t.AddPackingItem(a.Required("list"), a.Required("title"), a.String("group"), a.String("quantity"), a.OptionalStrings("people"), a.Bool("everyone", false), ct)),
        new("assign_packing_item", false, (t, a, ct) => t.AssignPackingItem(a.Required("item"), a.OptionalStrings("people"), a.Bool("everyone", false), ct)),
        new("set_packed", false, (t, a, ct) => t.SetPacked(a.Required("item"), a.Required("person"), a.Bool("packed", true), ct)),
        new("rename_packing_list", false, (t, a, ct) => t.RenamePackingList(a.Required("list"), a.Required("title"), ct)),
        new("delete_packing_list", false, (t, a, ct) => t.DeletePackingList(a.Required("list"), ct)),
        new("update_packing_item", false, (t, a, ct) => t.UpdatePackingItem(a.Required("item"), a.String("title"), a.String("group"), a.String("quantity"), a.String("list"), ct)),
        new("list_reminders", true, (t, a, ct) => t.ListReminders(ct)),
        new("get_weather", true, (t, a, ct) => t.GetWeather(a.String("from"), a.String("to"), ct)),
        new("list_connections", true, (t, a, ct) => t.ListConnections(ct)),
        new("add_to_group", false, (t, a, ct) => t.AddToGroup(a.Required("group"), a.Required("item"), ct)),
        new("remove_from_group", false, (t, a, ct) => t.RemoveFromGroup(a.Required("item"), ct)),
        new("get_bookings", true, (t, a, ct) => t.GetBookings(ct)),
        new("link_booking", false, (t, a, ct) => t.LinkBooking(a.Required("booking"), a.Strings("items"), ct)),
        new("get_costs", true, (t, a, ct) => t.GetCosts(a.String("in"), a.OptionalStrings("for"), ct)),
        new("split_expense", false, (t, a, ct) => t.SplitExpense(a.Required("expense"), a.Required("method"), a.OptionalStrings("people"), a.OptionalStrings("values"), ct)),
        new("set_charged", false, (t, a, ct) => t.SetCharged(a.Required("expense"), a.String("amount"), a.String("currency"), a.String("date"), ct)),
        new("set_cash", false, (t, a, ct) => t.SetCash(a.Required("expense"), a.String("per_unit"), a.String("received"), a.String("debited"), a.String("date"), ct)),
        new("set_trip_currency", false, (t, a, ct) => t.SetTripCurrency(a.Required("currency"), a.Bool("convert_budgets", false), ct)),
        new("set_budget", false, (t, a, ct) => t.SetBudget(a.Required("title"), a.Required("amount"), a.String("id"), a.String("category"), a.String("person"), a.String("collection"), ct)),
        new("record_payment", false, (t, a, ct) => t.RecordPayment(a.Required("expense"), a.OptionalStrings("paid_by"), a.String("amount"), a.String("date"), ct)),
        new("export_costs", true, (t, a, ct) => t.ExportCosts(a.String("format", "csv"), a.OptionalStrings("people"), a.String("separator", "comma"), ct)),
        new("mark_paid", false, (t, a, ct) => t.MarkPaid(a.Required("expense"), a.String("person"), a.Bool("paid", true), a.String("date"), ct)),
        new("convert_currency", true, (t, a, ct) => t.ConvertCurrency(a.Required("amount"), a.Required("from"), a.Required("to"), ct)),
        new("add_expense", false, (t, a, ct) => t.AddExpense(a.Required("title"), a.Required("amount"), a.Required("currency"), a.OptionalStrings("paid_by"), a.String("belongs_to"), a.Bool("estimated", false), a.String("category"), a.String("due"), a.String("date"), ct)),
        new("get_schema", true, (t, a, ct) => Task.FromResult(t.GetSchema(a.Required("type")))),
        new("validate", true, (t, a, ct) => t.Validate(ct)),
        new("list_versions", true, (t, a, ct) => t.ListVersions(a.Int("limit", 20), ct)),
        new("compare", true, (t, a, ct) => t.Compare(a.String("revision", "HEAD"), ct)),
        new("create_entity", false, (t, a, ct) => t.CreateEntity(a.Required("type"), a.Json("data"), ct)),
        new("update_entity", false, (t, a, ct) => t.UpdateEntity(a.Required("id"), a.Json("changes"), ct)),
        new("delete_entity", false, (t, a, ct) => t.DeleteEntity(a.Required("id"), a.Bool("also_delete_unused", false), ct)),
        new("read_document", true, (t, a, ct) => t.ReadDocument(a.Required("id"), a.Int("max_characters", 20000), ct)),
        new("import_document", false, (t, a, ct) => t.ImportDocument(a.Required("path"), a.String("attach_to"), a.Bool("split_attachments", false), ct)),
        new("add_note", false, (t, a, ct) => t.AddNote(a.Required("id"), a.Required("markdown"), ct)),
        new("create_version", false, (t, a, ct) => t.CreateVersion(a.Required("message"), ct)),
    ];

    public static readonly IReadOnlyDictionary<string, Tool> ByName = All.ToDictionary(t => t.Name, StringComparer.Ordinal);
}
