import Foundation

// Results of the app commands of TravelRepo.Native (docs/native.md): costs, packing, bookings, stays, reminders.
// Amounts come as decimal strings, as money is written in trip files, and decode into exact Decimals (DecimalText):
// JSONDecoder would read a JSON number through Double and lose digits.

/// A Decimal that TravelRepo sends as a string, such as "198.92".
@propertyWrapper
public struct DecimalText: Decodable, Sendable, Hashable {
    public var wrappedValue: Decimal
    public init(wrappedValue: Decimal) { self.wrappedValue = wrappedValue }
    public init(from decoder: Decoder) throws {
        let text = try decoder.singleValueContainer().decode(String.self)
        guard let value = Decimal(string: text, locale: Locale(identifier: "en_US_POSIX")) else {
            throw DecodingError.dataCorrupted(.init(codingPath: decoder.codingPath, debugDescription: "Not a decimal: \(text)"))
        }
        wrappedValue = value
    }
}

/// An optional Decimal sent as a string or null.
@propertyWrapper
public struct OptionalDecimalText: Decodable, Sendable, Hashable {
    public var wrappedValue: Decimal?
    public init(wrappedValue: Decimal?) { self.wrappedValue = wrappedValue }
    public init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        wrappedValue = container.decodeNil() ? nil : try DecimalText(from: decoder).wrappedValue
    }
}

extension KeyedDecodingContainer {
    public func decode(_ type: OptionalDecimalText.Type, forKey key: Key) throws -> OptionalDecimalText {
        try decodeIfPresent(type, forKey: key) ?? OptionalDecimalText(wrappedValue: nil)
    }
}

public struct Money: Decodable, Sendable, Hashable {
    public let value: String; public let currency: String
    public init(value: String, currency: String) { self.value = value; self.currency = currency }
}

public struct Costs: Decodable, Sendable {
    public struct Share: Decodable, Sendable { public let person: String; @DecimalText public var amount: Decimal; @OptionalDecimalText public var value: Decimal? }
    public struct Debt: Decodable, Sendable {
        public let expense: String; public let from: String; public let to: String; public let currency: String
        @DecimalText public var amount: Decimal; @DecimalText public var settled: Decimal; @DecimalText public var open: Decimal; public let settledOn: String?
        enum CodingKeys: String, CodingKey { case expense, from, to, currency, amount, settled, open, settledOn = "settled_on" }
    }
    public struct Expense: Decodable, Sendable {
        public let id: String; public let title: String; public let amount: Money?; public let method: String?; public let planned: Bool; public let category: String
        public let payers: [String]; public let shares: [Share]; public let debts: [Debt]; public let issues: [String]
    }
    public struct Balance: Decodable, Sendable { public let person: String; public let currency: String; @DecimalText public var net: Decimal }
    /// A payment that settles open debts; together they settle everything with as few payments as TravelRepo finds.
    public struct Transfer: Decodable, Sendable { public let from: String; public let to: String; public let currency: String; @DecimalText public var amount: Decimal }
    public struct Total: Decodable, Sendable { public let currency: String; @DecimalText public var paid: Decimal; @DecimalText public var estimated: Decimal; @DecimalText public var budget: Decimal }
    public struct CategoryTotal: Decodable, Sendable { public let category: String; public let currency: String; @DecimalText public var amount: Decimal; public let estimated: Bool }
    public struct Budget: Decodable, Sendable {
        public let id: String; public let title: String; public let scope: String; public let target: String?; public let amount: Money
        @DecimalText public var spent: Decimal; @DecimalText public var planned: Decimal; public let approximate: Bool; public let notIncluded: [Money]
        enum CodingKeys: String, CodingKey { case id, title, scope, target, amount, spent, planned, approximate, notIncluded = "not_included" }
    }
    public let expenses: [Expense]; public let openDebts: [Debt]; public let balances: [Balance]; public let settleUp: [Transfer]; public let totals: [Total]
    public let categories: [CategoryTotal]; public let budgets: [Budget]
    enum CodingKeys: String, CodingKey { case expenses, openDebts = "open_debts", balances, settleUp = "settle_up", totals, categories, budgets }
}

public struct PackingList: Decodable, Sendable {
    public struct Item: Decodable, Sendable {
        public let id: String; public let title: String; public let group: String?; public let quantity: String?; public let everyone: Bool
        public let assignees: [String]; public let packedBy: [String]
        enum CodingKeys: String, CodingKey { case id, title, group, quantity, everyone, assignees, packedBy = "packed_by" }
    }
    public let id: String; public let title: String; public let items: [Item]
}

public struct BookingPayments: Decodable, Sendable {
    public let id: String; public let title: String; public let coverage: String; public let items: [String]; public let paid: [String]; public let planned: [String]
}

public struct StayRun: Decodable, Sendable {
    public let id: String; public let title: String; public let firstNight: String?; public let nights: Int; public let zone: String
    public let checkIn: JSONValue?; public let checkOut: JSONValue?; public let issues: [String]
    enum CodingKeys: String, CodingKey { case id, title, firstNight = "first_night", nights, zone, checkIn = "check_in", checkOut = "check_out", issues }
}

public struct Reminder: Decodable, Sendable {
    public let key: String; public let kind: String; public let entity: String?; public let title: String; public let moment: String?; public let urgent: Bool; public let dates: [String]
}

extension TravelRepoSession {
    /// Expenses with their splits and debts, open debts, balances per person (in `currency` when given, with the saved
    /// exchange rates), totals per currency and category, and budgets.
    public func costs(currency: String? = nil) async throws -> Costs { try await call("costs", currency.map { ["currency": .string($0)] } ?? [:]) }

    public func packingLists() async throws -> [PackingList] {
        struct Lists: Decodable { let lists: [PackingList] }
        return try await call("packing", as: Lists.self).lists
    }

    public func bookings() async throws -> [BookingPayments] {
        struct Found: Decodable { let bookings: [BookingPayments] }
        return try await call("bookings", as: Found.self).bookings
    }

    public func stays() async throws -> [StayRun] {
        struct Found: Decodable { let stays: [StayRun] }
        return try await call("stays", as: Found.self).stays
    }

    /// What is due: cancellation deadlines, payments, tasks and the like, as of `now`.
    public func reminders(now: Date = Date()) async throws -> [Reminder] {
        struct Found: Decodable { let reminders: [Reminder] }
        let iso = ISO8601DateFormatter(); iso.formatOptions = [.withInternetDateTime]
        return try await call("reminders", ["now": .string(iso.string(from: now))], as: Found.self).reminders
    }

    /// An export in the session's language: kind `plan` (text, html, ics), `costs` (text, csv) or `packing` (text, csv).
    public func export(_ kind: String = "plan", format: String = "text", person: String? = nil) async throws -> String {
        var arguments: [String: JSONValue] = ["kind": .string(kind), "format": .string(format)]
        if let person { arguments["person"] = .string(person) }
        struct Exported: Decodable { let text: String }
        return try await call("export", arguments, as: Exported.self).text
    }

    /// Adds a document from bytes, for example from the share sheet, and attaches it to `attachTo` when given.
    @discardableResult
    public func importDocument(_ data: Data, name: String, mediaType: String? = nil, attachTo: String? = nil) async throws -> String {
        var arguments: [String: JSONValue] = ["name": .string(name), "base64": .string(data.base64EncodedString())]
        if let mediaType { arguments["media_type"] = .string(mediaType) }
        if let attachTo { arguments["attach_to"] = .string(attachTo) }
        struct Imported: Decodable { let id: String }
        return try await call("import_document_data", arguments, as: Imported.self).id
    }

    /// Downloads the European Central Bank's reference rates and saves them where the session was opened with `exchange_rates`.
    public func fetchExchangeRates() async throws -> String {
        struct Fetched: Decodable { let date: String }
        return try await call("fetch_exchange_rates", as: Fetched.self).date
    }

    /// Updates the weather cache for the trip's places and dates and returns the weather by day and plan.
    public func fetchWeather(refresh: Bool = false) async throws -> JSONValue { try await call("fetch_weather", ["refresh": .bool(refresh)]) }
}
