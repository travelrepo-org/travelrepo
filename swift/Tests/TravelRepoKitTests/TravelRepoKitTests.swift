import Foundation
import TravelRepoKit
import XCTest

/// TravelRepo compiled with NativeAOT, called from Swift on iOS (simulator). Only made-up trip data.
final class TravelRepoKitTests: XCTestCase {
    private var root: URL!

    override func setUp() async throws {
        root = FileManager.default.temporaryDirectory.appendingPathComponent("travelrepo-kit-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    }

    override func tearDown() async throws { try? FileManager.default.removeItem(at: root) }

    private func openTrip(culture: String? = nil) async throws -> TravelRepoSession {
        try await TravelRepoSession.createTrip(root: root.appendingPathComponent("trip"), title: "Spring in Japan", timezone: "Asia/Tokyo")
        return try await TravelRepoSession.open(root: root.appendingPathComponent("trip"), recoveryRoot: root.appendingPathComponent("recovery"), culture: culture)
    }

    func testInfoMatchesThePackage() async throws {
        let info = try await TravelRepo.info()
        XCTAssertEqual(info.abiVersion, TravelRepo.abiVersion)
        XCTAssertEqual(info.formatVersion, "1.0")
        XCTAssertFalse(info.invariantGlobalization)
    }

    func testEditsAgendaAndSearch() async throws {
        let session = try await openTrip()
        let shinkansen = try await session.call("create_entity", ["type": "schedule_item", "data": ["title": "Shinkansen to Kyoto",
            "time": ["precision": "exact", "start": ["local": "2027-05-15T08:10:00", "timezone": "Asia/Tokyo"], "end": ["local": "2027-05-15T10:25:00", "timezone": "Asia/Tokyo"]]]])["id"]!.string!
        let alex = try await session.call("create_entity", ["type": "person", "data": ["title": "Alex"]])["id"]!.string!

        // Rename Alex through apply, based on the revision of the snapshot.
        let snapshot = try await session.snapshot()
        guard case .object(var person)? = snapshot.entities.first(where: { $0.data["id"]?.string == alex })?.data else { return XCTFail("Alex missing") }
        person["title"] = "Alex K."
        let applied = try await session.apply(revision: snapshot.revision, edits: [alex: person], resources: ["documents/\(alex).md": Data("Window seat.".utf8)])
        XCTAssertNotEqual(applied.revision, snapshot.revision)
        let note = try await session.readResource("documents/\(alex).md")
        XCTAssertEqual(String(decoding: note, as: UTF8.self), "Window seat.")

        do { try await session.apply(revision: snapshot.revision, edits: [alex: nil]); XCTFail("stale revision accepted") }
        catch let error as TravelRepoError { XCTAssertEqual(error.code, "repository.changed") }

        let entry = try await session.agenda().days.flatMap(\.entries).first { $0.id == shinkansen }
        XCTAssertEqual(entry?.start, "2027-05-14T23:10:00Z")
        XCTAssertEqual(entry?.zone, "Asia/Tokyo")
        XCTAssertEqual(entry?.kind, "activity")

        let hit = try await session.find("shinkan kyo").first
        XCTAssertEqual(hit?.id, shinkansen)
        XCTAssertEqual(hit?.matches.first?.ranges.first?.start, 0)
    }

    func testExportsInTheSessionLanguage() async throws {
        let session = try await openTrip(culture: "de-DE")
        try await session.call("create_entity", ["type": "schedule_item", "data": ["title": "Nara Park", "time": ["precision": "all_day", "date": "2027-05-16"]]])
        let text = try await session.call("export_text")["text"]?.string ?? ""
        XCTAssertTrue(text.contains("Mai"), text)
    }

    func testCostsPackingExportsAndDocuments() async throws {
        let session = try await openTrip(culture: "de-DE")
        try await session.call("create_entity", ["type": "person", "data": ["title": "Alex"]])
        try await session.call("create_entity", ["type": "person", "data": ["title": "Sam"]])
        try await session.call("add_expense", ["title": "Okonomiyaki in Osaka", "amount": "3000", "currency": "JPY", "paid_by": ["Alex"]])
        let costs = try await session.costs()
        XCTAssertEqual(costs.expenses.first?.amount, Money(value: "3000", currency: "JPY"))
        XCTAssertEqual(costs.openDebts.first?.open, 1500)

        let list = try await session.call("create_packing_list", ["title": "Osaka"])["id"]!.string!
        try await session.call("add_packing_item", ["list": .string(list), "title": "Umbrella", "everyone": true])
        let lists = try await session.packingLists()
        XCTAssertEqual(lists.first { $0.id == list }?.items.first?.title, "Umbrella")

        let csv = try await session.export("costs", format: "csv")
        XCTAssertTrue(csv.contains("Okonomiyaki in Osaka"), csv)
        let note = try await session.importDocument(Data("Seats 7 and 8".utf8), name: "seats.txt")
        let snapshot = try await session.snapshot()
        XCTAssertTrue(snapshot.entities.contains { $0.data["id"]?.string == note })
    }

    func testErrorsCarryTravelRepoCodes() async throws {
        let session = try await openTrip()
        do { try await session.call("fly_to_the_moon"); XCTFail("unknown command accepted") }
        catch let error as TravelRepoError { XCTAssertEqual(error.code, "command.unknown") }
        do { _ = try await TravelRepoSession.open(root: root); XCTFail("non-trip opened") }
        catch let error as TravelRepoError { XCTAssertEqual(error.code, "session.not_trip") }
    }

    /// libgit2 for the simulator, as git2.framework/git2 from eng/build-libgit2-ios.sh. Git tests are skipped without it.
    private func configureGit() async throws {
        guard let path = ProcessInfo.processInfo.environment["TRAVELREPO_LIBGIT2"], !path.isEmpty else { throw XCTSkip("TRAVELREPO_LIBGIT2 is not set") }
        try await TravelRepo.configure(libgit2: path)
    }

    func testVersionsThroughLibGit2() async throws {
        try await configureGit()
        try await TravelRepoSession.createTrip(root: root.appendingPathComponent("trip"), title: "Spring in Japan", gitName: "Alex", gitEmail: "alex@example.invalid")
        let session = try await TravelRepoSession.open(root: root.appendingPathComponent("trip"), recoveryRoot: root.appendingPathComponent("recovery"))
        try await session.call("create_entity", ["type": "place", "data": ["title": "Fushimi Inari"]])
        try await session.call("create_version", ["message": "Add Fushimi Inari"])
        let newest = try await session.call("list_versions")["versions"]?.array?.first?["message"]?.string
        XCTAssertEqual(newest, "Add Fushimi Inari")
        let state = try await session.syncStatus().state
        XCTAssertEqual(state, "local_only")
    }

    /// Real HTTPS on iOS: GitHub's API through .NET's HttpClient and a clone through libgit2's managed transport.
    /// Both only read libgit2's public test repository. Opt in with TRAVELREPO_NETWORK_TESTS=1.
    func testHttpsToGitHub() async throws {
        guard ProcessInfo.processInfo.environment["TRAVELREPO_NETWORK_TESTS"] == "1" else { throw XCTSkip("TRAVELREPO_NETWORK_TESTS is not 1") }
        let repository = try await TravelRepo.call("github_inspect_repository", ["full_name": "libgit2/TestGitRepository"], as: JSONValue.self)
        XCTAssertEqual(repository["private"]?.bool, false)

        try await configureGit()
        let destination = root.appendingPathComponent("clone")
        try await TravelRepo.call("clone", ["url": "https://github.com/libgit2/TestGitRepository.git", "destination": .string(destination.path)], as: JSONValue.self)
        XCTAssertTrue(FileManager.default.fileExists(atPath: destination.appendingPathComponent(".git").path))
    }

    func testGitHubWithoutSignIn() async throws {
        try await GitHub.configure(clientId: "")
        try await GitHub.setToken(nil)
        let account = try await GitHub.account()
        XCTAssertNil(account)
        do { _ = try await GitHub.beginSignIn(); XCTFail("sign-in without client id") }
        catch let error as TravelRepoError { XCTAssertEqual(error.code, "github.setup") }
    }

    func testCredentialsComeFromTheAppAndCallsCanBeCancelled() async throws {
        try await configureGit()
        let asked = expectation(description: "credentials asked")
        TravelRepo.setCredentialProvider { url in
            XCTAssertEqual(url.host, "github.com")
            asked.fulfill()
            try? await Task.sleep(for: .seconds(30))
            return nil
        }
        defer { TravelRepo.setCredentialProvider(nil) }
        let destination = root.appendingPathComponent("clone").path
        let clone = Task { try await TravelRepo.call("clone", ["url": "https://github.com/alex-travels/trip.git", "destination": .string(destination)], as: JSONValue.self) }
        await fulfillment(of: [asked], timeout: 10)
        clone.cancel()
        do { _ = try await clone.value; XCTFail("cancelled clone finished") }
        catch let error as TravelRepoError { XCTAssertEqual(error.code, "call.cancelled") }
    }
}
