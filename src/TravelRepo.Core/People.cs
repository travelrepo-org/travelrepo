using System.Text.Json.Nodes;

namespace TravelRepo.Core;

/// <summary>Why a person was recognised as the local user.</summary>
public enum PersonMatchSource { GitHub, Email }

/// <summary>A person of the trip that matches the local user's accounts.</summary>
public sealed record PersonMatch(Entity Person, PersonMatchSource Source);

/// <summary>
/// People of a trip and how clients recognise the local user among them. The mapping "in this trip I am X" itself is
/// local client state; these rules only suggest it from the identities stored on people (specification section 12).
/// </summary>
public static class TripPeople
{
    /// <summary>The trip's travellers: the manifest's participants, or every person when the manifest lists none.</summary>
    public static IReadOnlyList<Entity> Travelers(TripSnapshot trip)
    {
        var listed = (trip.Manifest.Data["participants"] as JsonArray ?? []).Select(n => Guid.TryParse(n?.ToString(), out var id) ? trip.Find(id) : null).OfType<Entity>().Where(e => e.Type == "person").ToArray();
        return listed.Length > 0 ? listed : trip.Entities.Values.Where(e => e.Type == "person").OrderBy(e => e.Title, StringComparer.CurrentCulture).ToArray();
    }

    /// <summary>
    /// The person that matches the local user: first by GitHub login, then by Git e-mail, both case-insensitive. Null
    /// when nobody matches, or when several people match the same way and choosing would be a guess.
    /// </summary>
    public static PersonMatch? Match(TripSnapshot trip, string? githubLogin, string? gitEmail)
    {
        var people = trip.Entities.Values.Where(e => e.Type == "person").ToArray();
        if (githubLogin is { Length: > 0 } && people.Where(p => GitHubLogins(p).Contains(githubLogin, StringComparer.OrdinalIgnoreCase)).ToArray() is [var byLogin]) return new(byLogin, PersonMatchSource.GitHub);
        if (gitEmail is { Length: > 0 } && people.Where(p => Emails(p).Contains(gitEmail, StringComparer.OrdinalIgnoreCase)).ToArray() is [var byEmail]) return new(byEmail, PersonMatchSource.Email);
        return null;
    }

    public static IReadOnlyList<string> GitHubLogins(Entity person) => (person.Data["identities"]?["github"] as JsonArray ?? []).Select(n => n?["username"]?.ToString()).OfType<string>().Where(s => s.Length > 0).ToArray();
    public static IReadOnlyList<string> Emails(Entity person) => (person.Data["identities"]?["git"] as JsonArray ?? []).Select(n => n?["email"]?.ToString()).OfType<string>().Where(s => s.Length > 0).ToArray();

    /// <summary>
    /// Whether the person already carries an identity that is not the local user's: another GitHub login or another
    /// e-mail. Clients warn before adding theirs, because the person may be someone else.
    /// </summary>
    public static bool HasOtherIdentity(Entity person, string? githubLogin, string? gitEmail) =>
        GitHubLogins(person).Any(l => !string.Equals(l, githubLogin, StringComparison.OrdinalIgnoreCase))
        || Emails(person).Any(e => !string.Equals(e, gitEmail, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A copy of the person with the local user's GitHub login and Git identity added, so history shows their
    /// versions under this person and their other devices recognise them. Existing identities are kept.
    /// </summary>
    public static Entity WithIdentity(Entity person, string? githubLogin, string? gitName, string? gitEmail)
    {
        var copy = person.Copy();
        if (copy.Data["identities"] is not JsonObject identities) copy.Data["identities"] = identities = new JsonObject();
        if (githubLogin is { Length: > 0 } && !GitHubLogins(copy).Contains(githubLogin, StringComparer.OrdinalIgnoreCase))
        {
            if (identities["github"] is not JsonArray github) identities["github"] = github = new JsonArray();
            github.Add(new JsonObject { ["username"] = githubLogin });
        }
        if (gitEmail is { Length: > 0 } && !Emails(copy).Contains(gitEmail, StringComparer.OrdinalIgnoreCase))
        {
            if (identities["git"] is not JsonArray git) identities["git"] = git = new JsonArray();
            git.Add(new JsonObject { ["name"] = gitName ?? person.Title, ["email"] = gitEmail });
        }
        return copy;
    }
}
