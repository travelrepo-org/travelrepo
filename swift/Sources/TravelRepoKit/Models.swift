import Foundation

// Results of TravelRepo commands (docs/native.md). Instants are ISO 8601 strings in UTC, dates yyyy-MM-dd, zones IANA ids.

public struct Info: Decodable, Sendable {
    public let abiVersion: Int
    public let version: String
    public let formatVersion: String
    public let invariantGlobalization: Bool
    enum CodingKeys: String, CodingKey { case abiVersion = "abi_version", version, formatVersion = "format_version", invariantGlobalization = "invariant_globalization" }
}

public struct Diagnostic: Decodable, Sendable, Hashable { public let code, severity, path, message: String }

struct Revision: Decodable { let revision: String }

public struct Snapshot: Decodable, Sendable {
    public struct File: Decodable, Sendable { public let path: String; public let hash: String; public let data: JSONValue }
    public struct Resource: Decodable, Sendable { public let path: String; public let size: Int; public let hash: String }
    public let revision: String
    public let entities: [File]
    public let resources: [Resource]
    public let diagnostics: [Diagnostic]
}

public struct Applied: Decodable, Sendable { public let revision: String; public let diagnostics: [Diagnostic] }

public struct Agenda: Decodable, Sendable {
    public struct Day: Decodable, Sendable { public let date: String; public let number: Int; public let entries: [Entry]; public let nights: [Night] }
    public struct Night: Decodable, Sendable {
        public let stay: String; public let title: String; public let night: Int; public let nights: Int; public let firstNight: String?; public let zone: String
        enum CodingKeys: String, CodingKey { case stay, title, night, nights, firstNight = "first_night", zone }
    }
    public struct Entry: Decodable, Sendable {
        public let id: String; public let title: String; public let status: String?; public let role: String; public let kind: String; public let precision: String
        public let start: String?; public let end: String?; public let zone: String?; public let endZone: String?; public let day: String?
        public let timed: Bool; public let idea: Bool; public let steps: [Entry]
        enum CodingKeys: String, CodingKey { case id, title, status, role, kind, precision, start, end, zone, endZone = "end_zone", day, timed, idea, steps }
    }
    public let days: [Day]
    public let unscheduled: [Entry]
}

public struct SearchHit: Decodable, Sendable {
    public struct Match: Decodable, Sendable {
        public struct Range: Decodable, Sendable { public let start: Int; public let length: Int }
        public let field: String; public let text: String; public let ranges: [Range]
    }
    public let id: String; public let type: String; public let title: String; public let kind: String; public let score: Double
    public let day: String?; public let start: String?; public let zone: String?; public let matches: [Match]
}

public struct SyncStatus: Decodable, Sendable {
    public let branch: String; public let remote: String?; public let hasLocalChanges: Bool; public let remoteBranchExists: Bool
    public let ahead: Int; public let behind: Int; public let state: String
    enum CodingKeys: String, CodingKey { case branch, remote, hasLocalChanges = "has_local_changes", remoteBranchExists = "remote_branch_exists", ahead, behind, state }
}

public struct MergePreview: Decodable, Sendable {
    public struct Conflict: Decodable, Sendable {
        public let key: String; public let entity: String; public let path: String; public let kind: String
        public let base: JSONValue?; public let current: JSONValue?; public let incoming: JSONValue?
    }
    public struct Change: Decodable, Sendable { public let entity: String; public let type: String; public let title: String; public let kind: String; public let fields: [String] }
    public let canApply: Bool; public let conflicts: [Conflict]; public let changes: [Change]; public let diagnostics: [Diagnostic]
    enum CodingKeys: String, CodingKey { case canApply = "can_apply", conflicts, changes, diagnostics }
}
