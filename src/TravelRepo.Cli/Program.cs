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
        Console.WriteLine("travelrepo validate [path]\ntravelrepo inspect [path]\ntravelrepo diff <path> <revision>\ntravelrepo repair <path> [--apply]\ntravelrepo export <path> <output.ics|output.html>\ntravelrepo init <path> <title>\ntravelrepo format [path]\ntravelrepo version <path> <message>\ntravelrepo mcp [path] [--read-only]   serve the trip to AI assistants (MCP over stdio)"); return 0;
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
            var output = args.ElementAtOrDefault(2) ?? throw new DomainException("cli.output", "Provide an output filename.");
            var contents = Path.GetExtension(output).ToLowerInvariant() switch { ".ics" => TripExport.Ics(state.Trip), ".html" => TripExport.Html(state.Trip), _ => throw new DomainException("export.format", "Use .ics or .html.") };
            await File.WriteAllTextAsync(output, contents); return 0;
        case "version": await new GitRepository(repo, new GitCliBackend()).CreateVersionAsync(args.ElementAtOrDefault(2) ?? "Update trip"); return 0;
        default: Console.Error.WriteLine("Unknown command. Run travelrepo --help."); return 2;
    }
}
catch (Exception ex) when (ex is DomainException or IOException or UnauthorizedAccessException)
{ Console.Error.WriteLine(ex is DomainException d ? d.Code + ": " + d.Message : "I/O operation failed: " + ex.Message); return 2; }
