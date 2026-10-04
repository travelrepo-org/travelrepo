using System.Text.Json;
using System.Text.Json.Nodes;
namespace TravelRepo.Plugins;

/// <summary>Version 1 plugin contract. Hosts exchange JSON-RPC 2.0 messages over standard input/output.</summary>
public interface ITravelPlugin
{
    PluginDescription Describe();
    Task<JsonNode?> InvokeAsync(string command, JsonNode? arguments, CancellationToken ct = default);
}
/// <summary>
/// The package manifest (<see cref="FileName"/>). The first six members are required. The descriptive members are
/// optional and let hosts show what a plugin is, who made it and under which license it is distributed.
/// </summary>
/// <param name="Id">Stable reverse-domain identifier, for example <c>org.example.walking</c>.</param>
/// <param name="Version">The plugin's own version.</param>
/// <param name="MinimumApiVersion">The lowest plugin API version the plugin needs.</param>
/// <param name="EntryAssembly">The assembly that contains the <see cref="ITravelPlugin"/> implementation, relative to the package.</param>
/// <param name="Capabilities">What the plugin provides, such as <c>components</c> or <c>commands</c>.</param>
/// <param name="Permissions">Declared permissions, shown to the user before loading. They are informational, not enforced.</param>
/// <param name="Name">Display name. Hosts fall back to <paramref name="Id"/>.</param>
/// <param name="Description">One or two sentences about what the plugin adds.</param>
/// <param name="Authors">Author or organisation names as one display string.</param>
/// <param name="License">SPDX license expression, for example <c>MIT</c> or <c>Apache-2.0</c>.</param>
/// <param name="Homepage">An http(s) address with documentation or source code.</param>
/// <param name="LicenseFile">Path of the full license text relative to the package directory, usually inside <c>LICENSES/</c>.</param>
public sealed record PluginManifest(string Id, string Version, int MinimumApiVersion, string EntryAssembly, string[] Capabilities, string[] Permissions,
    string? Name = null, string? Description = null, string? Authors = null, string? License = null, string? Homepage = null, string? LicenseFile = null)
{
    /// <summary>The manifest file name inside a plugin package directory.</summary>
    public const string FileName = "jourfold.plugin.json";

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    /// <summary>Parses manifest JSON and checks the required members.</summary>
    /// <exception cref="FormatException">The JSON is not a usable manifest.</exception>
    public static PluginManifest Parse(string json)
    {
        PluginManifest? manifest;
        try { manifest = JsonSerializer.Deserialize<PluginManifest>(json, Options); }
        catch (JsonException ex) { throw new FormatException("The plugin manifest is not valid JSON.", ex); }
        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Version) || string.IsNullOrWhiteSpace(manifest.EntryAssembly))
            throw new FormatException("The plugin manifest needs id, version and entryAssembly.");
        return manifest with { Capabilities = manifest.Capabilities ?? [], Permissions = manifest.Permissions ?? [] };
    }

    /// <summary>The display name, or the ID when the manifest has none.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name.Trim();

    /// <summary><see cref="Homepage"/> when it is an absolute http(s) address; otherwise <c>null</c>.</summary>
    public Uri? HomepageUri => Uri.TryCreate(Homepage, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? uri : null;

    /// <summary>
    /// The full path of the license text inside <paramref name="packageDirectory"/>: <see cref="LicenseFile"/> when given,
    /// otherwise the only file in <c>LICENSES/</c>. Paths that leave the package directory are ignored.
    /// </summary>
    public string? ResolveLicenseFile(string packageDirectory)
    {
        var root = Path.GetFullPath(packageDirectory) + Path.DirectorySeparatorChar;
        string? candidate = null;
        if (!string.IsNullOrWhiteSpace(LicenseFile)) candidate = Path.GetFullPath(Path.Combine(root, LicenseFile));
        else if (Directory.Exists(Path.Combine(root, "LICENSES")) && Directory.GetFiles(Path.Combine(root, "LICENSES")) is [var only]) candidate = only;
        return candidate is not null && candidate.StartsWith(root, StringComparison.Ordinal) && File.Exists(candidate) ? candidate : null;
    }
}
public sealed record EditorField(string Key, string Label, string Kind, bool Required = false, string[]? Choices = null);
public sealed record ComponentDefinition(string Type, string Title, JsonObject Schema, EditorField[] Fields);
public sealed record PluginDescription(int ApiVersion, string Id, ComponentDefinition[] Components, string[] Commands);
public sealed record RpcRequest(string Jsonrpc, int Id, string Method, JsonNode? Params);
public sealed record RpcError(int Code, string Message);
