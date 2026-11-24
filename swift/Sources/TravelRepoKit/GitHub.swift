import Foundation

/// GitHub through TravelRepo: device-code sign-in, the account, trip repositories and invitations. The app stores the
/// token (for example in the Keychain) and hands it over with `setToken` on every start. Git uses it for github.com
/// unless the app set a credential provider of its own.
public enum GitHub {
    public struct Account: Decodable, Sendable {
        public let login: String; public let name: String?; public let avatarUrl: String?; public let profileUrl: String?
        enum CodingKeys: String, CodingKey { case login, name, avatarUrl = "avatar_url", profileUrl = "profile_url" }
    }
    public struct Authorization: Codable, Sendable {
        public let deviceCode: String; public let userCode: String; public let verificationUri: String; public let expiresIn: Int; public let interval: Int
        enum CodingKeys: String, CodingKey { case deviceCode = "device_code", userCode = "user_code", verificationUri = "verification_uri", expiresIn = "expires_in", interval }
    }
    public enum SignIn: Sendable, Equatable { case pending, slowDown, connected(token: String) }
    public struct Repository: Decodable, Sendable {
        public let fullName: String; public let cloneUrl: String; public let webUrl: String; public let isPrivate: Bool
        enum CodingKeys: String, CodingKey { case fullName = "full_name", cloneUrl = "clone_url", webUrl = "web_url", isPrivate = "private" }
    }

    /// The GitHub App the sign-in uses: its client id and, for the installation page, its slug.
    public static func configure(clientId: String, appSlug: String? = nil) async throws {
        var arguments: [String: JSONValue] = ["client_id": .string(clientId)]
        if let appSlug { arguments["app_slug"] = .string(appSlug) }
        _ = try await TravelRepo.call("github_configure", arguments, as: JSONValue.self)
    }

    /// The token from an earlier sign-in, or nil to forget it.
    public static func setToken(_ token: String?) async throws {
        _ = try await TravelRepo.call("github_set_token", ["token": token.map(JSONValue.string) ?? .null], as: JSONValue.self)
    }

    /// The signed-in account, or nil.
    public static func account() async throws -> Account? {
        struct Status: Decodable { let account: Account? }
        return try await TravelRepo.call("github_status", as: Status.self).account
    }

    /// Starts a sign-in: show `userCode` and open `verificationUri`, then call `poll` every `interval` seconds.
    public static func beginSignIn() async throws -> Authorization { try await TravelRepo.call("github_begin_sign_in") }

    /// Asks GitHub once whether the user entered the code. On `.connected`, store the token.
    public static func poll(_ authorization: Authorization) async throws -> SignIn {
        let arguments = try JSONDecoder().decode([String: JSONValue].self, from: JSONEncoder().encode(authorization))
        struct Polled: Decodable { let state: String; let token: String? }
        let polled = try await TravelRepo.call("github_poll_sign_in", arguments, as: Polled.self)
        switch polled.state {
        case "connected": return .connected(token: polled.token ?? "")
        case "slow_down": return .slowDown
        default: return .pending
        }
    }

    public static func signOut() async throws { _ = try await TravelRepo.call("github_sign_out", as: JSONValue.self) }

    /// The repositories the GitHub App may see that hold a trip.
    public static func tripRepositories() async throws -> [Repository] {
        struct Found: Decodable { let repositories: [Repository] }
        return try await TravelRepo.call("github_repositories", as: Found.self).repositories
    }

    /// Creates a private repository for a trip.
    public static func createRepository(named name: String) async throws -> Repository {
        try await TravelRepo.call("github_create_repository", ["name": .string(name)])
    }

    public static func invite(_ username: String, to fullName: String) async throws {
        _ = try await TravelRepo.call("github_invite", ["full_name": .string(fullName), "username": .string(username)], as: JSONValue.self)
    }

    public static func searchUsers(_ query: String) async throws -> [String] {
        struct Found: Decodable { let logins: [String] }
        return try await TravelRepo.call("github_search_users", ["query": .string(query)], as: Found.self).logins
    }
}
