using System.Text.Json.Nodes;
namespace TravelRepo.Plugins;

/// <summary>Version 1 plugin contract. Hosts exchange JSON-RPC 2.0 messages over standard input/output.</summary>
public interface ITravelPlugin
{
    PluginDescription Describe();
    Task<JsonNode?> InvokeAsync(string command, JsonNode? arguments, CancellationToken ct = default);
}
public sealed record PluginManifest(string Id, string Version, int MinimumApiVersion, string EntryAssembly, string[] Capabilities, string[] Permissions);
public sealed record EditorField(string Key, string Label, string Kind, bool Required = false, string[]? Choices = null);
public sealed record ComponentDefinition(string Type, string Title, JsonObject Schema, EditorField[] Fields);
public sealed record PluginDescription(int ApiVersion, string Id, ComponentDefinition[] Components, string[] Commands);
public sealed record RpcRequest(string Jsonrpc, int Id, string Method, JsonNode? Params);
public sealed record RpcError(int Code, string Message);
