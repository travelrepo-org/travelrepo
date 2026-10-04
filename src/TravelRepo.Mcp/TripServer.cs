using System.Reflection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TravelRepo.Core;
using TravelRepo.Git;
using TravelRepo.Repository;

namespace TravelRepo.Mcp;

/// <summary>Settings for a trip MCP server.</summary>
/// <param name="ReadOnly">Offer only the tools that read the trip; nothing can be changed.</param>
/// <param name="Git">Git backend for versions and comparisons. Defaults to the Git found on this computer.</param>
/// <param name="ClientName">Name of the application that starts the server, reported to the assistant (for example "Jourfold").</param>
/// <param name="RecoveryRoot">Recovery journal folder; defaults to the one every TravelRepo client uses for this trip.</param>
public sealed record TripServerOptions(bool ReadOnly = false, IGitBackend? Git = null, string? ClientName = null, string? RecoveryRoot = null);

/// <summary>
/// A Model Context Protocol server for one TravelRepo trip. AI assistants (MCP clients) start it as a local process
/// and talk to it over standard input and output. Every change goes through <see cref="TravelRepository.ApplyAsync"/>,
/// so it is validated, written as one transaction and refused when the files changed in the meantime.
/// The server has no AI logic of its own and never contacts the network.
/// </summary>
public static class TripServer
{
    /// <summary>The agent guide (SKILL.md) that explains the trip format to assistants.</summary>
    public static string Guide { get; } = ReadGuide();

    /// <summary>Serves the trip over standard input and output until the client disconnects.</summary>
    public static async Task RunStdioAsync(string tripPath, TripServerOptions? options = null, CancellationToken ct = default)
    {
        var serverOptions = Options(tripPath, options ?? new());
        await using var server = McpServer.Create(new StdioServerTransport(serverOptions), serverOptions);
        await server.RunAsync(ct);
    }

    /// <summary>Creates a server on an arbitrary transport, for embedding and tests. Call <c>RunAsync</c> on the result.</summary>
    public static McpServer Create(ITransport transport, string tripPath, TripServerOptions? options = null) => McpServer.Create(transport, Options(tripPath, options ?? new()));

    /// <summary>Server name, instructions and tools for one trip. Fails early when the folder is not a trip.</summary>
    public static McpServerOptions Options(string tripPath, TripServerOptions options)
    {
        var root = Path.GetFullPath(tripPath);
        if (!File.Exists(Path.Combine(root, "travel.yaml"))) throw new DomainException("mcp.not_trip", "This folder is not a TravelRepo trip: travel.yaml is missing.");
        var tools = new TripTools(new TravelRepository(root, options.RecoveryRoot), options.Git ?? new GitCliBackend(), options.ReadOnly);
        var collection = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var method in typeof(TripTools).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
        {
            if (method.GetCustomAttribute<McpServerToolAttribute>() is not { } attribute) continue;
            if (options.ReadOnly && !attribute.ReadOnly) continue;
            collection.Add(McpServerTool.Create(method, tools));
        }
        return new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "travelrepo", Title = "TravelRepo trip", Version = TravelRepoInfo.Version },
            ServerInstructions = Instructions(options),
            ToolCollection = collection
        };
    }

    private static string Instructions(TripServerOptions options) =>
        "These tools work on one travel plan (a TravelRepo trip)" + (options.ClientName is { Length: > 0 } client ? " that the user also has open in " + client : "") + ". "
        + "Call read_guide once before your first change; it explains entities, times and the rules for editing. "
        + "Start with get_trip and get_schedule to see what exists. "
        + (options.ReadOnly
            ? "This server is read-only: you can look at the trip but not change it."
            : "Changes are validated and saved immediately and show up for the user as unsaved changes. Create a version only when the user asks for it.");

    private static string ReadGuide()
    {
        using var stream = typeof(TripServer).Assembly.GetManifestResourceStream("TravelRepo.Mcp.SKILL.md")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
