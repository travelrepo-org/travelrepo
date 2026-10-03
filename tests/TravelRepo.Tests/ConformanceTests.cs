using TravelRepo.Repository;
using TravelRepo.Core;
using TravelRepo.Merge;
using Xunit;
namespace TravelRepo.Tests;

public class ConformanceTests
{
    private static string Samples()
    { var d = new DirectoryInfo(AppContext.BaseDirectory); while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "samples"))) d = d.Parent; return Path.Combine(d!.FullName, "samples"); }
    [Fact]
    public async Task EverySamplePassesSchemaAndSemanticValidation()
    {
        foreach (var manifest in Directory.EnumerateFiles(Samples(), "travel.yaml", SearchOption.AllDirectories)) { var state = await new TravelRepository(Path.GetDirectoryName(manifest)!).ReadAsync(); Assert.DoesNotContain(state.Diagnostics, d => d.Severity == Severity.Error); }
    }
    [Fact]
    public async Task MergeFixtureProducesResolvableFieldConflict()
    {
        async Task<TripSnapshot> Read(string name) => (await new TravelRepository(Path.Combine(Samples(), "merge-scenarios", name)).ReadAsync()).Trip;
        var b = await Read("base"); var o = await Read("current"); var t = await Read("incoming"); var plan = SemanticMerge.Plan(b, o, t); var conflict = Assert.Single(plan.Conflicts); Assert.Equal("/display_name", conflict.Path);
        var resolved = SemanticMerge.Plan(b, o, t, new Dictionary<string, System.Text.Json.Nodes.JsonNode?> { [conflict.Entity + conflict.Path] = conflict.Incoming }); Assert.True(resolved.CanApply); Assert.Equal("Carla", Assert.Single(resolved.Result.Entities.Values).Title);
    }
}
