using TravelRepo.Core;
using TravelRepo.Plugins;
using Xunit;
namespace TravelRepo.Tests;

public sealed class AboutTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "travelrepo-plugin-" + Guid.NewGuid());
    public void Dispose() => TestFiles.Delete(root);

    [Fact]
    public void SdkReportsItsVersionAndFormat()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+", TravelRepoInfo.Version);
        Assert.DoesNotContain('+', TravelRepoInfo.Version);
        Assert.StartsWith(TravelRepoInfo.Version, TravelRepoInfo.InformationalVersion);
        Assert.Equal(TravelRepoInfo.FormatVersion, Entity.CreateTrip("Test").Data["schema"]!["version"]!.GetValue<string>());
        Assert.Equal("Apache-2.0", TravelRepoInfo.License);
    }

    [Fact]
    public void ManifestWithoutDescriptiveFieldsStillParses()
    {
        var manifest = PluginManifest.Parse("""{"id":"org.example.a","version":"1.0.0","minimumApiVersion":1,"entryAssembly":"A.dll","capabilities":["components"],"permissions":[]}""");
        Assert.Equal("org.example.a", manifest.DisplayName);
        Assert.Null(manifest.License);
        Assert.Null(manifest.HomepageUri);
    }

    [Fact]
    public void ManifestDescribesPluginAndFindsItsLicense()
    {
        Directory.CreateDirectory(Path.Combine(root, "LICENSES"));
        File.WriteAllText(Path.Combine(root, "LICENSES", "MIT.txt"), "MIT License");
        var manifest = PluginManifest.Parse("""{"id":"org.example.b","version":"2.1.0","minimumApiVersion":1,"entryAssembly":"B.dll","name":"Walking","description":"Adds walks.","authors":"Example","license":"MIT","homepage":"https://example.org/b"}""");
        Assert.Equal("Walking", manifest.DisplayName);
        Assert.Equal(new Uri("https://example.org/b"), manifest.HomepageUri);
        Assert.Empty(manifest.Capabilities);
        Assert.Equal(Path.Combine(root, "LICENSES", "MIT.txt"), manifest.ResolveLicenseFile(root));
    }

    [Fact]
    public void ManifestRejectsUnsafeValues()
    {
        Assert.Throws<FormatException>(() => PluginManifest.Parse("""{"version":"1.0.0"}"""));
        Assert.Throws<FormatException>(() => PluginManifest.Parse("not json"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "outside-license.txt"), "x");
        var manifest = PluginManifest.Parse("""{"id":"c","version":"1","minimumApiVersion":1,"entryAssembly":"C.dll","homepage":"javascript:alert(1)","licenseFile":"../outside-license.txt"}""");
        Assert.Null(manifest.HomepageUri);
        Assert.Null(manifest.ResolveLicenseFile(root));
    }
}
