namespace TravelRepo.Providers;

public sealed record RemoteRepository(string FullName, string CloneUrl, string WebUrl, bool IsPrivate);
public sealed record DeviceAuthorization(string DeviceCode, string UserCode, string VerificationUri, int ExpiresIn, int Interval);
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
public sealed record ProviderAccount(string Login, string? Name, Uri? AvatarUrl, Uri? ProfileUrl);
public interface IProviderAccount
{
    /// <summary>The signed-in account, or <c>null</c> when there is no sign-in or the provider rejects it.</summary>
    Task<ProviderAccount?> AccountAsync(CancellationToken ct = default);
    /// <summary>Forgets the sign-in on this device. Access granted on the provider's side stays until the user revokes it there.</summary>
    Task SignOutAsync(CancellationToken ct = default);
}
public interface IRepositoryDiscoveryProvider { Task<IReadOnlyList<RemoteRepository>> DiscoverAsync(CancellationToken ct = default); }
public interface IRepositoryCreationProvider { Task<RemoteRepository> CreateAsync(string name, CancellationToken ct = default); }
public interface IRepositoryPrivacyProvider { Task<RemoteRepository> InspectAsync(string fullName, CancellationToken ct = default); }
public interface ICollaboratorProvider { Task InviteAsync(string fullName, string username, CancellationToken ct = default); }
public interface IUserSearchProvider { Task<IReadOnlyList<string>> SearchUsersAsync(string query, CancellationToken ct = default); }
public interface IShareProvider { Uri ShareUri(RemoteRepository repository, string? branch = null); }
