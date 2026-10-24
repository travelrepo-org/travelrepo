using System.Text.Json;
using TravelRepo.Core;
using TravelRepo.Git;
using TravelRepo.Merge;
using TravelRepo.Repository;
using TravelRepo.Serialization;

try
{
    if (args.Length == 0 || args[0] is "--help" or "help")
    {
        Console.WriteLine("travelrepo validate [path]\ntravelrepo inspect [path]\ntravelrepo diff <path> <revision>\ntravelrepo repair <path> [--apply]\ntravelrepo export <path> <output.html|output.txt|output.ics> [--from yyyy-mm-dd] [--to yyyy-mm-dd] [--person name|id] [--parts schedule,stays,bookings,contacts,costs,tasks,notes,packing] [--compact]\ntravelrepo export-lists <path> <output.txt|output.csv> [--person name|id] [--list name|id]... [--unassigned]\ntravelrepo init <path> <title>\ntravelrepo format [path]\ntravelrepo version <path> <message>\ntravelrepo mcp [path] [--read-only]   serve the trip to AI assistants (MCP over stdio)"); return 0;
    }
    if (args[0] == "mcp")
    {
        // Standard output carries the protocol; nothing else may be written to it.
        await TravelRepo.Mcp.TripServer.RunStdioAsync(args.Skip(1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? ".", new(ReadOnly: args.Contains("--read-only")));
        return 0;
    }
    var path = args.Length > 1 ? args[1] : "."; var repo = new TravelRepository(path);
    if (args[0] == "init") { await repo.InitializeAsync(Entity.CreateTrip(args.ElementAtOrDefault(2) ?? Path.GetFileName(Path.GetFullPath(path)))); await new GitRepository(repo, new GitCliBackend()).InitializeAsync("Traveler", "traveler@localhost"); return 0; }
    if (args[0] == "repair" && args.Contains("--restore"))
    {
        var index = Array.IndexOf(args, "--restore"); var revision = args.ElementAtOrDefault(index + 1) ?? "HEAD";
        var target = await new GitRepository(repo, new GitCliBackend()).SnapshotAsync(revision); var plan = await repo.PreviewRestoreAsync(target);
        foreach (var change in plan.Changes) Console.WriteLine((change.Replacement is null ? "Remove " : "Restore ") + change.Path);
        if (args.Contains("--apply")) await repo.ApplyRestoreAsync(plan);
        else Console.WriteLine("Review the file changes, then add --apply. Before-images are retained outside the trip.");
        return 0;
    }
    if (args[0] == "repair" && args.Contains("--apply")) await repo.RecoverAsync();
    if (args[0] == "format") { Console.WriteLine($"Formatted {await repo.NormalizeAsync()} file(s)."); return 0; }
    var state = await repo.ReadAsync();
    switch (args[0])
    {
        case "validate": Console.WriteLine(JsonSerializer.Serialize(state.Diagnostics, new JsonSerializerOptions { WriteIndented = true })); return state.Diagnostics.Any(d => d.Severity == Severity.Error) ? 2 : 0;
        case "inspect": Console.WriteLine(state.Trip.Manifest.Title); foreach (var group in state.Trip.Entities.Values.GroupBy(e => e.Type)) Console.WriteLine($"{group.Key}: {group.Count()}"); foreach (var d in state.Diagnostics) Console.WriteLine($"{d.Severity}: {d.Code} {d.Path} {d.Message}"); return 0;
        case "diff":
            var baseline = await new GitRepository(repo, new GitCliBackend()).SnapshotAsync(args.ElementAtOrDefault(2) ?? "HEAD");
            Console.WriteLine(JsonSerializer.Serialize(SemanticMerge.Diff(baseline, state.Trip), new JsonSerializerOptions { WriteIndented = true })); return 0;
        case "repair":
            Console.WriteLine("Recovery journal: " + repo.RecoveryRoot);
            foreach (var d in state.Diagnostics) Console.WriteLine(d.Code + ": " + d.Message);
            if (!args.Contains("--apply")) Console.WriteLine("Review diagnostics and recovery files first. --apply restores interrupted transactions only when no external edit would be overwritten.");
            return state.Diagnostics.Any(d => d.Severity == Severity.Error) ? 2 : 0;
        case "export":
            {
                var output = args.ElementAtOrDefault(2) ?? throw new DomainException("cli.output", "Provide an output filename.");
                var options = new ExportOptions
                {
                    From = ExportOptions.ParseDate(Option("--from")),
                    To = ExportOptions.ParseDate(Option("--to")),
                    Person = Person(state.Trip, Option("--person")),
                    Parts = Option("--parts") is { } parts ? ExportOptions.ParseParts(parts) : ExportParts.Standard,
                    Detail = args.Contains("--compact") ? ExportDetail.Compact : ExportDetail.Detailed
                };
                var contents = Path.GetExtension(output).ToLowerInvariant() switch
                {
                    ".ics" => TripExport.Ics(state.Trip, options),
                    ".html" => TripExport.Html(TripExport.Document(state.Trip, options)),
                    ".txt" => TripExport.Text(TripExport.Document(state.Trip, options)),
                    _ => throw new DomainException("export.format", "Use .html, .txt or .ics.")
                };
                await File.WriteAllTextAsync(output, contents); return 0;
            }
        case "export-lists":
            {
                var output = args.ElementAtOrDefault(2) ?? throw new DomainException("cli.output", "Provide an output filename.");
                var lists = PackingLists.All(state.Trip);
                var chosen = args.Select((a, i) => (a, i)).Where(x => x.a == "--list").Select(x => args.ElementAtOrDefault(x.i + 1) ?? "")
                    .Select(name => lists.FirstOrDefault(l => l.Collection.Id.ToString() == name || string.Equals(l.Collection.Title, name, StringComparison.CurrentCultureIgnoreCase))?.Collection.Id ?? throw new DomainException("export.list", "No packing list called " + name + ".")).ToArray();
                var options = new PackingExportOptions { Lists = chosen.Length > 0 ? chosen : null, Person = Person(state.Trip, Option("--person")), UnassignedOnly = args.Contains("--unassigned") };
                var contents = Path.GetExtension(output).ToLowerInvariant() switch
                {
                    ".txt" => PackingExport.Text(state.Trip, options),
                    ".csv" => PackingExport.Csv(state.Trip, options),
                    _ => throw new DomainException("export.format", "Use .txt or .csv.")
                };
                // Spreadsheet programs need the byte order mark to read UTF-8 CSV files.
                await File.WriteAllTextAsync(output, contents, new System.Text.UTF8Encoding(Path.GetExtension(output).Equals(".csv", StringComparison.OrdinalIgnoreCase))); return 0;
            }
        case "version": await new GitRepository(repo, new GitCliBackend()).CreateVersionAsync(args.ElementAtOrDefault(2) ?? "Update trip"); return 0;
        default: Console.Error.WriteLine("Unknown command. Run travelrepo --help."); return 2;
    }
}
catch (Exception ex) when (ex is DomainException or IOException or UnauthorizedAccessException)
{ Console.Error.WriteLine(ex is DomainException d ? d.Code + ": " + d.Message : "I/O operation failed: " + ex.Message); return 2; }

string? Option(string name) => Array.IndexOf(args, name) is >= 0 and var i && i + 1 < args.Length ? args[i + 1] : null;

static Guid? Person(TripSnapshot trip, string? nameOrId)
{
    if (nameOrId is null) return null;
    var people = trip.Entities.Values.Where(e => e.Type == "person").ToArray();
    return (people.FirstOrDefault(p => p.Id.ToString() == nameOrId) ?? (people.Where(p => string.Equals(p.Title, nameOrId, StringComparison.CurrentCultureIgnoreCase)).ToArray() is [var only] ? only : null)
        ?? throw new DomainException("export.person", "No single person called " + nameOrId + ".")).Id;
}
