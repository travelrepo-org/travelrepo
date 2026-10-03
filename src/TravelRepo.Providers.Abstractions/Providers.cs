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
public interface IRepositoryDiscoveryProvider { Task<IReadOnlyList<RemoteRepository>> DiscoverAsync(CancellationToken ct = default); }
public interface IRepositoryCreationProvider { Task<RemoteRepository> CreateAsync(string name, CancellationToken ct = default); }
public interface IRepositoryPrivacyProvider { Task<RemoteRepository> InspectAsync(string fullName, CancellationToken ct = default); }
public interface ICollaboratorProvider { Task InviteAsync(string fullName, string username, CancellationToken ct = default); }
public interface IUserSearchProvider { Task<IReadOnlyList<string>> SearchUsersAsync(string query, CancellationToken ct = default); }
public interface IShareProvider { Uri ShareUri(RemoteRepository repository, string? branch = null); }
