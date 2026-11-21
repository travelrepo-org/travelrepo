import Foundation

// Looking after a trip folder and its history (docs/native.md, "Repair, history and variants").

public struct GitHealth: Decodable, Sendable {
    public let gitVersion: String; public let branch: String?; public let detached: Bool; public let operation: String?
    public let staleLock: String?; public let lockAgeSeconds: Int?; public let tripChanges: Int; public let otherFiles: Int
    public let remote: String?; public let remoteBranch: String?; public let remoteBranchKnown: Bool; public let ahead: Int; public let behind: Int
    /// Plain text for a bug report, without remote addresses or credentials.
    public let summary: String
    enum CodingKeys: String, CodingKey {
        case gitVersion = "git_version", branch, detached, operation, staleLock = "stale_lock", lockAgeSeconds = "lock_age_seconds", tripChanges = "trip_changes"
        case otherFiles = "other_files", remote, remoteBranch = "remote_branch", remoteBranchKnown = "remote_branch_known", ahead, behind, summary
    }
}

public struct RestorePreview: Decodable, Sendable {
    public struct Change: Decodable, Sendable { public let path: String; public let kind: String; public let size: Int? }
    /// Pass back to `restoreApply`; the restore is refused when the trip changed since.
    public let plan: String
    public let changes: [Change]
}

public struct Version: Decodable, Sendable {
    public let commit: String; public let parents: [String]; public let author: String; public let email: String
    public let committer: String; public let committerEmail: String; public let at: String; public let message: String; public let title: String
    public let applicationGenerated: Bool; public let refs: [String]
    enum CodingKeys: String, CodingKey {
        case commit, parents, author, email, committer, committerEmail = "committer_email", at, message, title, applicationGenerated = "application_generated", refs
    }
}

public struct Remote: Decodable, Sendable, Hashable { public let name: String; public let url: String }

public struct DeletedVariant: Decodable, Sendable {
    public let reference: String; public let branch: String; public let title: String; public let deletedAt: String; public let commit: String
    enum CodingKeys: String, CodingKey { case reference, branch, title, deletedAt = "deleted_at", commit }
}

private struct State: Decodable { let state: String }

extension TravelRepoSession {
    public func gitHealth(remote: String? = nil) async throws -> GitHealth { try await call("git_health", remote.map { ["remote": .string($0)] } ?? [:]) }
    /// Finishes writes that were interrupted, for example when the app was stopped while saving.
    public func recover() async throws { try await call("recover") }
    /// Removes a Git lock file older than `minimumAge` seconds; returns whether there was one.
    public func removeStaleLock(minimumAge seconds: Int = 60) async throws -> Bool {
        struct Removed: Decodable { let removed: Bool }
        return try await call("remove_stale_lock", ["minimum_age_seconds": .number(Double(seconds))], as: Removed.self).removed
    }
    /// Cancels a merge or rebase started outside the app. Changes made during it are lost; ask first.
    public func abortOperation() async throws { try await call("abort_operation") }
    /// Moves files that block incoming versions into a folder below the recovery folder and returns that folder.
    public func moveAside(_ paths: [String]) async throws -> String {
        struct Moved: Decodable { let folder: String }
        return try await call("move_aside", ["paths": .array(paths.map(JSONValue.string))], as: Moved.self).folder
    }

    public func restorePreview(commit: String) async throws -> RestorePreview { try await call("restore_preview", ["commit": .string(commit)]) }
    @discardableResult
    public func restoreApply(commit: String, plan: String) async throws -> Applied { try await call("restore_apply", ["commit": .string(commit), "plan": .string(plan)]) }

    public func history(limit: Int = 100) async throws -> [Version] {
        struct Versions: Decodable { let versions: [Version] }
        return try await call("history", ["limit": .number(Double(limit))], as: Versions.self).versions
    }
    public func remotes() async throws -> [Remote] {
        struct Remotes: Decodable { let remotes: [Remote] }
        return try await call("remotes", as: Remotes.self).remotes
    }
    /// Takes the remote's versions only when that is a fast-forward, for updates in the background. Never sends.
    public func pull(remote: String = "origin") async throws -> String { try await call("pull", ["remote": .string(remote)], as: State.self).state }

    /// Renames or archives the open variant; this makes a small version.
    public func updateVariant(title: String? = nil, archive: Bool = false) async throws {
        var arguments: [String: JSONValue] = ["archive": .bool(archive)]
        if let title { arguments["title"] = .string(title) }
        try await call("update_variant", arguments)
    }
    public func deletedVariants() async throws -> [DeletedVariant] {
        struct Deleted: Decodable { let variants: [DeletedVariant] }
        return try await call("deleted_variants", as: Deleted.self).variants
    }
    /// Brings a deleted variant back as a local branch and returns its name.
    public func restoreVariant(_ deleted: DeletedVariant) async throws -> String {
        struct Restored: Decodable { let branch: String }
        return try await call("restore_variant", ["reference": .string(deleted.reference)], as: Restored.self).branch
    }
    @discardableResult
    public func purgeDeletedVariants(olderThanDays days: Int = 30) async throws -> Int {
        struct Removed: Decodable { let removed: Int }
        return try await call("purge_deleted_variants", ["older_than_days": .number(Double(days))], as: Removed.self).removed
    }
    public func keepLocalOnly(branch: String) async throws { try await call("keep_local_only", ["branch": .string(branch)]) }
    public func shareVariant(branch: String, remote: String = "origin") async throws { try await call("share_variant", ["branch": .string(branch), "remote": .string(remote)]) }
    public func syncVariant(branch: String, remote: String = "origin") async throws -> String {
        try await call("sync_variant", ["branch": .string(branch), "remote": .string(remote)], as: State.self).state
    }
    /// Archives the open variant as taken over by another variant's version.
    public func markMerged(intoVariantId: String, intoCommit: String) async throws {
        try await call("mark_merged", ["into_variant_id": .string(intoVariantId), "into_commit": .string(intoCommit)])
    }
    /// Copies the trip with its history and remotes into an empty folder and returns that folder.
    @discardableResult
    public func copyTrip(to destination: URL) async throws -> String {
        struct Copied: Decodable { let root: String }
        return try await call("copy_trip", ["destination": .string(destination.path)], as: Copied.self).root
    }
}
