using System.Text.Json.Nodes;
using TravelRepo.Core;
using Xunit;
namespace TravelRepo.Tests;

public class PeopleTests
{
    private static Entity Person(string name, string? email = null, string? github = null)
    {
        var person = Entity.Create("person", name); var identities = new JsonObject();
        if (email is not null) identities["git"] = new JsonArray(new JsonObject { ["name"] = name, ["email"] = email });
        if (github is not null) identities["github"] = new JsonArray(new JsonObject { ["username"] = github });
        if (identities.Count > 0) person.Data["identities"] = identities;
        return person;
    }
    private static TripSnapshot Trip(params Entity[] people) => new(Entity.CreateTrip("Weekend", "en", "Europe/Berlin"), people.ToDictionary(p => p.Id));

    [Fact]
    public void TheLocalUserIsMatchedByGitHubLoginFirstThenByEmail()
    {
        var alex = Person("Alex", "alex@example.org"); var sam = Person("Sam", "sam@example.org", "sam-travels"); var kim = Person("Kim");
        var trip = Trip(alex, sam, kim);
        Assert.Equal(new PersonMatch(sam, PersonMatchSource.GitHub), TripPeople.Match(trip, "SAM-travels", "alex@example.org"));
        Assert.Equal(new PersonMatch(alex, PersonMatchSource.Email), TripPeople.Match(trip, "someone-else", "Alex@Example.org"));
        Assert.Null(TripPeople.Match(trip, null, "nobody@example.org"));
        // Two people with the same e-mail: guessing would be wrong.
        Assert.Null(TripPeople.Match(Trip(alex, Person("Alex again", "alex@example.org")), null, "alex@example.org"));
    }

    [Fact]
    public void AnIdentityIsAddedWithoutDuplicatesAndOtherIdentitiesAreNoticed()
    {
        var kim = Person("Kim");
        Assert.False(TripPeople.HasOtherIdentity(kim, "kim-gh", "kim@example.org"));
        var claimed = TripPeople.WithIdentity(kim, "kim-gh", "Kim", "kim@example.org");
        Assert.Equal(["kim-gh"], TripPeople.GitHubLogins(claimed)); Assert.Equal(["kim@example.org"], TripPeople.Emails(claimed));
        Assert.Equal(claimed.Data.ToJsonString(), TripPeople.WithIdentity(claimed, "KIM-gh", "Kim", "KIM@example.org").Data.ToJsonString());
        Assert.False(TripPeople.HasOtherIdentity(claimed, "kim-gh", "kim@example.org"));
        Assert.True(TripPeople.HasOtherIdentity(claimed, "fred-gh", "fred@example.org"));
        Assert.Null(kim.Data["identities"]); // the original is unchanged
    }

    [Fact]
    public void TravelersAreTheParticipantsOrEveryone()
    {
        var alex = Person("Alex"); var sam = Person("Sam"); var trip = Trip(alex, sam);
        Assert.Equal(["Alex", "Sam"], TripPeople.Travelers(trip).Select(p => p.Title));
        trip.Manifest.Data["participants"] = new JsonArray(sam.Id.ToString());
        Assert.Equal(["Sam"], TripPeople.Travelers(trip).Select(p => p.Title));
    }
}
