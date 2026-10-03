# Provider architecture

Capabilities live in `TravelRepo.Providers.Abstractions`. A provider implements only supported capabilities: authentication, discovery, creation, privacy, collaboration, search or sharing. Git operations remain in `TravelRepo.Git`; local trip editing never requires a provider.

`GitHubProvider` implements GitHub App device authorization. Register an App with device flow enabled, supply its public client ID, and install it for the desired repositories. Do not embed an App private key or client secret in the desktop client. The default path requests no classic OAuth `repo` scope.

Discovery enumerates user installations and granted repositories, paginates results, and reads `travel.yaml` to determine compatibility. Repository creation sends `private: true` and leaves initialization to the local trip. Privacy is exposed as data for clients to warn appropriately. Collaborator invitations request push access and may fail when account/App permissions are insufficient.

Tokens use the supplied `ISecretStore`. Jourfold uses Windows Credential Manager or Linux Secret Service and otherwise keeps session-only credentials. `ICredentialBroker` supplies HTTPS Git credentials through scoped child-process configuration in environment variables; they are not included in command arguments or repository config. Other hosts retain system helpers and normal SSH behavior.

Inject `HttpClient` to test network boundaries. Live tests require a configured App and consenting disposable test accounts. HTTP seam tests do not prove GitHub has granted the required permissions.

GitHub documentation consulted: [GitHub App user tokens and device flow](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-a-user-access-token-for-a-github-app).
