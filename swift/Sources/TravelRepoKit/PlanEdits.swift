import Foundation

// Edits and read models of the plan beyond whole-entity edits (docs/native.md, "Plan edits").

public struct Vocabulary: Decodable, Sendable {
    public let scheduleCategories: [String]; public let transportTypes: [String]; public let statuses: [String]
    public let expenseCategories: [String]; public let customExpenseCategories: [String]
    enum CodingKeys: String, CodingKey {
        case scheduleCategories = "schedule_categories", transportTypes = "transport_types", statuses
        case expenseCategories = "expense_categories", customExpenseCategories = "custom_expense_categories"
    }
}

public struct EntitySummary: Decodable, Sendable, Hashable { public let id: String; public let type: String; public let title: String }

public struct DeletionPreview: Decodable, Sendable {
    public let deleted: [EntitySummary]
    /// Deleted too, because they cannot exist without a deleted entity, such as comments on it.
    public let dependents: [EntitySummary]
    /// Changed to drop their references.
    public let updated: [EntitySummary]
    public let removedResources: [String]
    /// Only the deleted entities used these; they stay unless their ids are deleted as well.
    public let unused: [EntitySummary]
    enum CodingKeys: String, CodingKey { case deleted, dependents, updated, removedResources = "removed_resources", unused }
}

public struct Comment: Decodable, Sendable {
    public let id: String; public let author: String?; public let authorName: String?; public let createdAt: String?; public let body: String?
    enum CodingKeys: String, CodingKey { case id, author, authorName = "author_name", createdAt = "created_at", body }
}

public struct CalendarEntry: Decodable, Sendable {
    public let uid: String; public let item: String; public let title: String; public let role: String; public let kind: String; public let allDay: Bool
    public let start: String?; public let end: String?; public let date: String?; public let zone: String?
    public let location: String?; public let description: String?; public let status: String?
    enum CodingKeys: String, CodingKey { case uid, item, title, role, kind, allDay = "all_day", start, end, date, zone, location, description, status }
}

public struct CostUpgradePreview: Decodable, Sendable {
    public struct Change: Decodable, Sendable { public let kind: String; public let entity: String; public let title: String; public let amount: Money?; public let other: Money? }
    public let needed: Bool
    public let changes: [Change]
}

public struct MailView: Decodable, Sendable {
    public struct Address: Decodable, Sendable { public let name: String?; public let address: String }
    public struct InlineImage: Decodable, Sendable {
        public let contentId: String; public let mediaType: String; public let base64: String
        enum CodingKeys: String, CodingKey { case contentId = "content_id", mediaType = "media_type", base64 }
    }
    public struct Attachment: Decodable, Sendable {
        public let name: String; public let mediaType: String; public let size: Int; public let base64: String
        enum CodingKeys: String, CodingKey { case name, mediaType = "media_type", size, base64 }
    }
    public let subject: String?; public let from: [Address]; public let to: [Address]; public let cc: [Address]; public let date: String?
    public let text: String; public let textFromHtml: Bool
    /// Cleaned for display: no scripts, frames or forms; remote images only when allowed.
    public let html: String?
    public let remoteImages: Int; public let remoteImagesAllowed: Bool
    public let inlineImages: [InlineImage]; public let attachments: [Attachment]
    enum CodingKeys: String, CodingKey {
        case subject, from, to, cc, date, text, textFromHtml = "text_from_html", html, remoteImages = "remote_images"
        case remoteImagesAllowed = "remote_images_allowed", inlineImages = "inline_images", attachments
    }
}

private struct Saved: Decodable { let revision: String; let id: String? }

extension TravelRepoSession {
    public func vocabulary() async throws -> Vocabulary { try await call("vocabulary") }
    public func suggestCategory(title: String) async throws -> String? {
        struct Suggested: Decodable { let category: String? }
        return try await call("suggest_category", ["title": .string(title)], as: Suggested.self).category
    }

    public func deletePreview(_ ids: [String]) async throws -> DeletionPreview { try await call("delete_preview", ["ids": .array(ids.map(JSONValue.string))]) }
    /// Deletes `ids` with their dependents and removes references to them, in one transaction.
    @discardableResult
    public func deleteEntities(_ ids: [String]) async throws -> Applied { try await call("delete_entities", ["ids": .array(ids.map(JSONValue.string))]) }

    /// Copies an item with everything nested in it and returns the copy's id.
    public func duplicate(_ id: String, title: String? = nil) async throws -> String {
        var arguments: [String: JSONValue] = ["id": .string(id)]
        if let title { arguments["title"] = .string(title) }
        return try await call("duplicate", arguments, as: Saved.self).id ?? ""
    }
    /// Gives an item an exact time. Throws `time.gap` for a skipped local time and `time.ambiguous` for a repeated one
    /// without `offset` (such as "+01").
    @discardableResult
    public func schedule(_ id: String, local: String, timezone: String, minutes: Int = 60, offset: String? = nil) async throws -> Applied {
        var arguments: [String: JSONValue] = ["id": .string(id), "local": .string(local), "timezone": .string(timezone), "minutes": .number(Double(minutes))]
        if let offset { arguments["offset"] = .string(offset) }
        return try await call("schedule", arguments)
    }
    /// Moves an item to `day` (yyyy-MM-dd) at the same local time and length.
    @discardableResult
    public func moveToDay(_ id: String, day: String, offset: String? = nil) async throws -> Applied {
        var arguments: [String: JSONValue] = ["id": .string(id), "day": .string(day)]
        if let offset { arguments["offset"] = .string(offset) }
        return try await call("move_to_day", arguments)
    }

    public func comments(on target: String) async throws -> [Comment] {
        struct Comments: Decodable { let comments: [Comment] }
        return try await call("comments", ["target": .string(target)], as: Comments.self).comments
    }
    @discardableResult
    public func addComment(on target: String, author: String, body: String) async throws -> String {
        try await call("add_comment", ["target": .string(target), "author": .string(author), "body": .string(body)], as: Saved.self).id ?? ""
    }

    public func calendar(staysAsCheckInOut: Bool = false) async throws -> [CalendarEntry] {
        struct Entries: Decodable { let entries: [CalendarEntry] }
        return try await call("calendar", ["stays_as_check_in_out": .bool(staysAsCheckInOut)], as: Entries.self).entries
    }

    public func costUpgradePreview(fallbackCurrency: String? = nil) async throws -> CostUpgradePreview {
        try await call("cost_upgrade_preview", fallbackCurrency.map { ["fallback_currency": .string($0)] } ?? [:])
    }
    /// Saves the upgrade of older cost data; it becomes a version of its own when nothing else is unsaved.
    public func costUpgradeApply(fallbackCurrency: String? = nil, message: String = "Upgrade costs") async throws {
        var arguments: [String: JSONValue] = ["message": .string(message)]
        if let fallbackCurrency { arguments["fallback_currency"] = .string(fallbackCurrency) }
        try await call("cost_upgrade_apply", arguments)
    }

    public func readMail(_ document: String, allowRemoteImages: Bool = false) async throws -> MailView {
        try await call("read_mail", ["id": .string(document), "allow_remote_images": .bool(allowRemoteImages)])
    }
    /// Replaces a document's file with the file at `source`.
    public func replaceDocument(_ id: String, source: URL, mediaType: String, allowLarge: Bool = false) async throws {
        try await call("replace_document", ["id": .string(id), "source": .string(source.path), "media_type": .string(mediaType), "allow_large": .bool(allowLarge)])
    }
}
