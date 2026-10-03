using TravelRepo.Core;
namespace TravelRepo.Git;

/// <summary>Credential-free desktop link carrying a clone URL and an optional variant branch.</summary>
public sealed record ShareLink(string Remote, string? Branch)
{
    public string ToUri()
    {
        Validate(Remote);
        return "jourfold://open?remote=" + Uri.EscapeDataString(Remote) + (Branch is null ? "" : "&variant=" + Uri.EscapeDataString(Branch));
    }
    public static ShareLink Parse(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "jourfold" || uri.Host != "open") throw new DomainException("share.invalid", "This is not a supported trip link.");
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Split('=', 2)).Where(p => p.Length == 2).ToArray();
        string? Get(string key) => query.FirstOrDefault(p => p[0] == key) is { } pair ? Uri.UnescapeDataString(pair[1]) : null;
        var remote = Get("remote") ?? throw new DomainException("share.invalid", "The trip link has no repository URL."); Validate(remote);
        var branch = Get("variant"); if (branch?.StartsWith('-') == true || branch?.Contains('\n') == true) throw new DomainException("share.invalid", "Invalid variant context.");
        return new(remote, branch);
    }
    public static void Validate(string remote)
    {
        GitRepository.ValidateRemote(remote);
        if (Uri.TryCreate(remote, UriKind.Absolute, out var uri) && (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || uri.UserInfo.Contains(':'))) throw new DomainException("share.credentials", "Sharing links cannot include credentials or URL tokens.");
    }
}
