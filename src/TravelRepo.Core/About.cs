using System.Reflection;
namespace TravelRepo.Core;

/// <summary>Identifies the TravelRepo SDK a client runs on, for about screens, diagnostics and bug reports.</summary>
public static class TravelRepoInfo
{
    /// <summary>The trip format version this SDK writes into <c>trip.yaml</c> (<c>schema.version</c>).</summary>
    public const string FormatVersion = "1.0";
    /// <summary>The SPDX license expression of the TravelRepo libraries, schemas and specification.</summary>
    public const string License = "Apache-2.0";

    private static readonly Assembly Assembly = typeof(TravelRepoInfo).Assembly;

    /// <summary>The SDK version without build metadata, for example <c>0.1.0</c>.</summary>
    public static string Version => InformationalVersion.Split('+')[0];

    /// <summary>The full build version. Builds from a Git checkout append <c>+</c> and the source commit.</summary>
    public static string InformationalVersion => Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>The public source repository, or <c>null</c> when the build does not name one.</summary>
    public static Uri? RepositoryUrl => Metadata(Assembly, "RepositoryUrl");

    /// <summary>Reads an absolute http(s) URL from an assembly's <see cref="AssemblyMetadataAttribute"/>.</summary>
    public static Uri? Metadata(Assembly assembly, string key)
    {
        var value = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? uri : null;
    }
}
