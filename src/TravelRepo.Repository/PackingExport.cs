using System.Globalization;
using System.Text;
using TravelRepo.Core;

namespace TravelRepo.Repository;

/// <summary>Which packing lists and items an export contains.</summary>
public sealed record PackingExportOptions
{
    /// <summary>The lists to export, by collection ID. Null exports every list.</summary>
    public IReadOnlyList<Guid>? Lists { get; init; }
    /// <summary>When set, only the items this person brings, with this person's packed state.</summary>
    public Guid? Person { get; init; }
    /// <summary>Only items nobody brings yet.</summary>
    public bool UnassignedOnly { get; init; }
}

/// <summary>
/// Packing list exports: sections for PDF and HTML renderers, plain text with check boxes for messengers and mail,
/// and CSV for spreadsheets. One person's export shows that person's items and whether that person has packed them;
/// the full export shows who brings each item and whether it is done.
/// </summary>
public static class PackingExport
{
    public static IReadOnlyList<PackingList> Lists(TripSnapshot trip, PackingExportOptions options) =>
        PackingLists.All(trip).Where(l => options.Lists is null || options.Lists.Contains(l.Collection.Id)).ToArray();

    /// <summary>The items of a list that the export contains.</summary>
    public static IReadOnlyList<PackingItem> Items(PackingList list, PackingExportOptions options) =>
        options.UnassignedOnly ? PackingLists.Unassigned(list) : options.Person is { } person ? PackingLists.For(list, person) : list.Items;

    /// <summary>One section per list that has items, with a heading row per group and a check row per item.</summary>
    public static IReadOnlyList<ExportSection> Sections(TripSnapshot trip, PackingExportOptions options, ExportLabels? labels = null)
    {
        labels ??= new();
        var sections = new List<ExportSection>();
        foreach (var list in Lists(trip, options))
        {
            var items = Items(list, options);
            if (items.Count == 0) continue;
            var rows = new List<ExportRow>();
            var groups = PackingLists.Groups(items);
            foreach (var (group, groupItems) in groups)
            {
                if (group is not null || groups.Count > 1) rows.Add(new(group ?? labels.OtherItems, Kind: ExportRowKind.Heading));
                rows.AddRange(groupItems.Select(i => new ExportRow(i.Task.Title, i.Quantity ?? "", Who(i, options, labels), Kind: ExportRowKind.Check, Checked: options.Person is { } p ? i.IsPackedBy(p) : i.Done)));
            }
            sections.Add(new(ExportParts.Packing, list.Collection.Title, rows, labels.PackingLists));
        }
        return sections;
    }

    /// <summary>A packing list document for print, PDF or text.</summary>
    public static ExportDocument Document(TripSnapshot trip, PackingExportOptions? options = null, ExportLabels? labels = null)
    {
        options ??= new(); labels ??= new();
        var subtitle = options.UnassignedOnly ? labels.NobodyYet : options.Person is { } p && trip.Find(p) is { } person ? string.Format(CultureInfo.CurrentCulture, labels.PackingFor, person.Title) : labels.PackingLists;
        return new(trip.Manifest.Title, [subtitle], Sections(trip, options, labels), null, [], labels.GeneratedBy, trip.Manifest.Data["language"]?.ToString() ?? "en", new ExportOptions { Parts = ExportParts.Packing, Person = options.Person }, labels);
    }

    public static string Text(TripSnapshot trip, PackingExportOptions? options = null, ExportLabels? labels = null) => TripExport.Text(Document(trip, options, labels));

    /// <summary>
    /// One row per item. <paramref name="separator"/> defaults to the culture's list separator, so spreadsheets open
    /// the file directly (for example a semicolon in German). Cells that a spreadsheet would read as a formula are
    /// prefixed with an apostrophe.
    /// </summary>
    public static string Csv(TripSnapshot trip, PackingExportOptions? options = null, ExportLabels? labels = null, CultureInfo? culture = null, string? separator = null)
    {
        options ??= new(); labels ??= new(); culture ??= CultureInfo.CurrentCulture;
        separator ??= culture.TextInfo.ListSeparator is { Length: > 0 } s ? s : ",";
        var csv = new StringBuilder();
        void Row(params string?[] cells) => csv.Append(string.Join(separator, cells.Select(c => Cell(c ?? "", separator)))).Append("\r\n");
        var person = options.Person;
        if (person is null) Row(labels.List, labels.Group, labels.Item, labels.Quantity, labels.BroughtBy, labels.Packed, labels.Done);
        else Row(labels.List, labels.Group, labels.Item, labels.Quantity, labels.BroughtBy, labels.Packed);
        foreach (var list in Lists(trip, options))
            foreach (var item in Items(list, options))
            {
                var brought = item.Everyone ? labels.Everyone : string.Join(", ", item.Assignees.Select(a => a.Title));
                if (person is { } p) Row(list.Collection.Title, item.Group, item.Task.Title, item.Quantity, brought, item.IsPackedBy(p) ? labels.Yes : labels.No);
                else Row(list.Collection.Title, item.Group, item.Task.Title, item.Quantity, brought, string.Join(", ", item.Assignees.Where(a => item.IsPackedBy(a.Id)).Select(a => a.Title)), item.Done ? labels.Yes : labels.No);
            }
        return csv.ToString();
    }

    /// <summary>The people who bring at least one item of the chosen lists, in traveller order.</summary>
    public static IReadOnlyList<Entity> People(TripSnapshot trip, PackingExportOptions? options = null)
    {
        var lists = Lists(trip, options ?? new());
        var travellers = TripPeople.Travelers(trip).Concat(trip.Entities.Values.Where(e => e.Type == "person").OrderBy(e => e.Title, StringComparer.CurrentCulture)).DistinctBy(e => e.Id);
        return travellers.Where(p => lists.Any(l => PackingLists.For(l, p.Id).Count > 0)).ToArray();
    }

    /// <summary>A file name such as <c>Lake weekend - Ana.pdf</c>, with characters that are not allowed in file names replaced.</summary>
    public static string FileName(string title, Entity? person, string extension)
    {
        var name = person is null ? title : title + " - " + person.Title;
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var safe = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return (safe.Length == 0 ? "export" : safe) + "." + extension.TrimStart('.');
    }

    private static string? Who(PackingItem item, PackingExportOptions options, ExportLabels labels)
    {
        if (item.Unassigned) return options.UnassignedOnly ? null : labels.Nobody;
        if (options.Person is { } person)
        {
            if (item.Everyone) return labels.Everyone;
            var others = item.Assignees.Where(a => a.Id != person).Select(a => a.Title).ToArray();
            return others.Length > 0 ? string.Format(CultureInfo.CurrentCulture, labels.With, string.Join(", ", others)) : null;
        }
        var who = item.Everyone ? labels.Everyone : string.Join(", ", item.Assignees.Select(a => a.Title));
        var packed = item.Assignees.Where(a => item.IsPackedBy(a.Id)).Select(a => a.Title).ToArray();
        return packed.Length == 0 || (!item.Everyone && item.Assignees.Count == 1) ? who : who + " · " + string.Format(CultureInfo.CurrentCulture, labels.PackedBy, string.Join(", ", packed));
    }

    private static string Cell(string value, string separator)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return value.Contains(separator, StringComparison.Ordinal) || value.Contains('"') || value.Contains('\n') || value.Contains('\r') ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
