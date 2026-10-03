using System.Text;
using TravelRepo.Core;
using TravelRepo.Merge;
using Xunit;
namespace TravelRepo.Tests;

public class ResourceMergeTests
{
    [Fact]
    public void IndependentMarkdownLinesMergeAndAssetsSurvive()
    {
        var m = Entity.CreateTrip("Trip"); TripSnapshot Snapshot(string text, bool asset = false) => new(m, new Dictionary<Guid, Entity>()) { Resources = asset ? new Dictionary<string, byte[]> { ["documents/note.md"] = Encoding.UTF8.GetBytes(text), ["assets/sha256/00/blob"] = [0, 255, 17] } : new Dictionary<string, byte[]> { ["documents/note.md"] = Encoding.UTF8.GetBytes(text) } };
        var plan = SemanticMerge.Plan(Snapshot("one\ntwo\nthree"), Snapshot("ONE\ntwo\nthree"), Snapshot("one\ntwo\nTHREE", true)); Assert.True(plan.CanApply); Assert.Equal("ONE\ntwo\nTHREE", Encoding.UTF8.GetString(plan.Result.Resources["documents/note.md"])); Assert.Equal(new byte[] { 0, 255, 17 }, plan.Result.Resources["assets/sha256/00/blob"]);
    }
    [Fact]
    public void ConflictingMarkdownIsTypedAndCannotBeApplied()
    {
        var m = Entity.CreateTrip("Trip"); TripSnapshot S(string text) => new(m, new Dictionary<Guid, Entity>()) { Resources = new Dictionary<string, byte[]> { ["documents/note.md"] = Encoding.UTF8.GetBytes(text) } };
        var plan = SemanticMerge.Plan(S("one"), S("ours"), S("theirs")); Assert.Equal(ConflictKind.Markdown, Assert.Single(plan.Conflicts).Kind); Assert.Throws<DomainException>(() => plan.ResourcesAgainst(S("ours")));
    }
}
