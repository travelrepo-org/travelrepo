namespace TravelRepo.Providers;

public sealed record RemoteRepository(string FullName, string CloneUrl, string WebUrl, bool IsPrivate);
public sealed record DeviceAuthorization(string DeviceCode, string UserCode, string VerificationUri, int ExpiresIn, int Interval);

/// <summary>What one poll of a device sign-in found: still waiting, asked to poll less often, or signed in.</summary>
public enum DeviceSignIn { Pending, SlowDown, Connected }
public interface ISecretStore
{
    Task<string?> ReadAsync(string key, CancellationToken ct = default);
    Task WriteAsync(string key, string value, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
}
public interface IProviderAuthentication
{
    Task<DeviceAuthorization> BeginAsync(CancellationToken ct = default);
    Task CompleteAsync(DeviceAuthorization authorization, CancellationToken ct = default);
}
/// <summary>The account a provider is signed in with, for showing who shares and publishes.</summary>
/// <param name="Login">The account handle, for example <c>octocat</c>.</param>
/// <param name="Name">The display name, if the account has one.</param>
/// <param name="AvatarUrl">The account picture.</param>
/// <param name="ProfileUrl">The account's public page.</param>
public sealed record ProviderAccount(string Login, string? Name, Uri? AvatarUrl, Uri? ProfileUrl)
{
    /// <summary>The provider's number for the account, which stays when the login is renamed. Null when it does not say.</summary>
    public long? Id { get; init; }
    /// <summary>The email the account shows on its public profile, or null when it shows none.</summary>
    public string? PublicEmail { get; init; }
}
public interface IProviderAccount
{
    /// <summary>The signed-in account, or <c>null</c> when there is no sign-in or the provider rejects it.</summary>
    Task<ProviderAccount?> AccountAsync(CancellationToken ct = default);
    /// <summary>Forgets the sign-in on this device. Access granted on the provider's side stays until the user revokes it there.</summary>
    Task SignOutAsync(CancellationToken ct = default);
}
/// <summary>How far a search for trips has come: repositories checked out of all the account can reach.</summary>
public sealed record DiscoveryProgress(int Checked, int Total);
public interface IRepositoryDiscoveryProvider
{
    /// <summary>The repositories the account can reach that hold a trip. <paramref name="progress"/> hears how many have been checked.</summary>
    Task<IReadOnlyList<RemoteRepository>> DiscoverAsync(IProgress<DiscoveryProgress>? progress = null, CancellationToken ct = default);
}
/// <summary>For providers where the user chooses which repositories an app may use, apart from signing in.</summary>
public interface IRepositoryAccessProvider
{
    /// <summary>Whether the app may use any repository yet. A new sign-in has none until the user has chosen some.</summary>
    Task<bool> HasRepositoryAccessAsync(CancellationToken ct = default);
    /// <summary>The page where the user chooses the repositories, or <c>null</c> when the provider does not know it.</summary>
    Uri? RepositoryAccessUri { get; }
}
public interface IRepositoryCreationProvider { Task<RemoteRepository> CreateAsync(string name, CancellationToken ct = default); }
public interface IRepositoryPrivacyProvider { Task<RemoteRepository> InspectAsync(string fullName, CancellationToken ct = default); }
public interface ICollaboratorProvider { Task InviteAsync(string fullName, string username, CancellationToken ct = default); }
public interface IUserSearchProvider { Task<IReadOnlyList<string>> SearchUsersAsync(string query, CancellationToken ct = default); }
public interface IShareProvider { Uri ShareUri(RemoteRepository repository, string? branch = null); }
