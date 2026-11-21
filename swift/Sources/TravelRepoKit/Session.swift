import CTravelRepo
import Foundation

/// An open trip. Commands of the MCP server work through `call`, with the same names and arguments.
public final class TravelRepoSession: Sendable {
    public let id: UInt64

    private init(id: UInt64) { self.id = id }
    deinit { tr_close(id) }

    /// Opens the trip in `root`. `culture` (such as `de-DE`) sets the language of sorting and exports.
    /// `exchangeRates` is the file `fetchExchangeRates` saves to and `costs` converts with; `weatherCache` the folder
    /// `fetchWeather` fills.
    public static func open(root: URL, recoveryRoot: URL? = nil, culture: String? = nil, readOnly: Bool = false, clientName: String? = nil,
                            exchangeRates: URL? = nil, weatherCache: URL? = nil, weatherProvider: String? = nil) async throws -> TravelRepoSession {
        var arguments: [String: JSONValue] = ["root": .string(root.path), "read_only": .bool(readOnly)]
        if let exchangeRates { arguments["exchange_rates"] = .string(exchangeRates.path) }
        if let weatherCache { arguments["weather_cache"] = .string(weatherCache.path) }
        if let weatherProvider { arguments["weather_provider"] = .string(weatherProvider) }
        if let recoveryRoot { arguments["recovery_root"] = .string(recoveryRoot.path) }
        if let culture { arguments["culture"] = .string(culture) }
        if let clientName { arguments["client_name"] = .string(clientName) }
        struct Opened: Decodable { let session: UInt64 }
        return TravelRepoSession(id: try await TravelRepo.call("open", arguments, as: Opened.self).session)
    }

    /// Creates a trip in an empty folder, with Git history when a name and e-mail are given.
    @discardableResult
    public static func createTrip(root: URL, title: String, language: String = "en", timezone: String? = nil, gitName: String? = nil, gitEmail: String? = nil) async throws -> String {
        var arguments: [String: JSONValue] = ["root": .string(root.path), "title": .string(title), "language": .string(language)]
        if let timezone { arguments["timezone"] = .string(timezone) }
        if let gitName, let gitEmail { arguments["git_name"] = .string(gitName); arguments["git_email"] = .string(gitEmail) }
        struct Created: Decodable { let id: String }
        return try await TravelRepo.call("create_trip", arguments, as: Created.self).id
    }

    /// Runs any command of this session and decodes its result.
    public func call<Result: Decodable>(_ command: String, _ arguments: [String: JSONValue] = [:], as type: Result.Type = Result.self) async throws -> Result {
        try await TravelRepo.call(command, session: id, arguments, as: type)
    }

    /// Runs any command of this session and returns its result as open JSON.
    @discardableResult
    public func call(_ command: String, _ arguments: [String: JSONValue] = [:]) async throws -> JSONValue {
        try await TravelRepo.call(command, session: id, arguments, as: JSONValue.self)
    }

    public func snapshot() async throws -> Snapshot { try await call("snapshot") }
    public func revision() async throws -> String { try await call("revision", as: Revision.self).revision }

    /// Writes entity edits (whole data, nil to delete) and resources (nil to delete) as one transaction, based on
    /// `revision`. Throws `repository.changed` when the trip changed since.
    @discardableResult
    public func apply(revision: String, edits: [String: [String: JSONValue]?] = [:], resources: [String: Data?] = [:]) async throws -> Applied {
        let entityEdits: [JSONValue] = edits.sorted { $0.key < $1.key }.map { id, data in
            .object(["id": .string(id), "data": data.map(JSONValue.object) ?? .null])
        }
        let resourceEdits: [JSONValue] = resources.sorted { $0.key < $1.key }.map { path, bytes in
            .object(["path": .string(path), "base64": bytes.map { .string($0.base64EncodedString()) } ?? .null])
        }
        return try await call("apply", ["revision": .string(revision), "edits": .array(entityEdits), "resources": .array(resourceEdits)])
    }

    public func readResource(_ path: String) async throws -> Data {
        struct Resource: Decodable { let base64: String }
        return Data(base64Encoded: try await call("read_resource", ["path": .string(path)], as: Resource.self).base64) ?? Data()
    }

    public func agenda() async throws -> Agenda { try await call("agenda") }
    public func find(_ query: String, kinds: [String]? = nil, notes: Bool = false, limit: Int = 200) async throws -> [SearchHit] {
        var arguments: [String: JSONValue] = ["query": .string(query), "notes": .bool(notes), "limit": .number(Double(limit))]
        if let kinds { arguments["kinds"] = .array(kinds.map(JSONValue.string)) }
        struct Found: Decodable { let hits: [SearchHit] }
        return try await call("find", arguments, as: Found.self).hits
    }

    /// The Git author of versions made in this trip, for example after cloning it.
    public func setIdentity(name: String, email: String) async throws {
        try await call("set_identity", ["name": .string(name), "email": .string(email)])
    }

    public func syncStatus(remote: String? = nil) async throws -> SyncStatus {
        try await call("sync_status", remote.map { ["remote": .string($0)] } ?? [:])
    }
    public func sync(remote: String = "origin") async throws -> String {
        struct Synced: Decodable { let state: String }
        return try await call("sync", ["remote": .string(remote)], as: Synced.self).state
    }
    /// Watches the trip folder and sends `trip_changed` events when its revision changes (see `TravelRepo.setEventHandler`).
    @discardableResult
    public func watch(every seconds: Int = 5, enabled: Bool = true) async throws -> Bool {
        struct Watching: Decodable { let watching: Bool }
        return try await call("watch", ["enabled": .bool(enabled), "interval_seconds": .number(Double(seconds))], as: Watching.self).watching
    }

    /// The merge of `branch` into the current variant. `keepBoth` lists entities whose conflicts keep the current
    /// entity and add the incoming one as a copy.
    public func mergePreview(branch: String, resolutions: [String: JSONValue] = [:], keepBoth: [String] = []) async throws -> MergePreview {
        try await call("merge_preview", ["branch": .string(branch), "resolutions": .object(resolutions), "keep_both": .array(keepBoth.map(JSONValue.string))])
    }
    @discardableResult
    public func mergeApply(branch: String, resolutions: [String: JSONValue], keepBoth: [String] = [], message: String? = nil) async throws -> String {
        var arguments: [String: JSONValue] = ["branch": .string(branch), "resolutions": .object(resolutions), "keep_both": .array(keepBoth.map(JSONValue.string))]
        if let message { arguments["message"] = .string(message) }
        struct Merged: Decodable { let commit: String }
        return try await call("merge_apply", arguments, as: Merged.self).commit
    }
}
