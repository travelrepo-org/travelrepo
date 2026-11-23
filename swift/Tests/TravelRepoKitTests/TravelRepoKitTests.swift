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

    func testWatchedSessionsReportChanges() async throws {
        let session = try await openTrip()
        let changed = expectation(description: "trip_changed")
        let id = session.id
        TravelRepo.setEventHandler { event in
            if event["type"]?.string == "trip_changed", event["session"]?.number == Double(id) { changed.fulfill() }
        }
        defer { TravelRepo.setEventHandler(nil) }
        let watching = try await session.watch(every: 1)
        XCTAssertTrue(watching)
        // A change made outside this session, as a sync or another app would.
        let other = try await TravelRepoSession.open(root: root.appendingPathComponent("trip"), recoveryRoot: root.appendingPathComponent("recovery-other"))
        try await other.call("create_entity", ["type": "place", "data": ["title": "Fushimi Inari"]])
        await fulfillment(of: [changed], timeout: 15)
        try await session.watch(enabled: false)
    }

    func testMeAndClaimPerson() async throws {
        let session = try await openTrip()
        let sam = try await session.call("create_entity", ["type": "person", "data": ["title": "Sam"]])["id"]!.string!
        let before = try await session.me(githubLogin: "sam-travels")
        XCTAssertNil(before.person)
        try await session.claimPerson(sam, githubLogin: "sam-travels", gitName: "Sam", gitEmail: "sam@example.invalid")
        let after = try await session.me(githubLogin: "sam-travels")
        XCTAssertEqual(after.person, sam)
        XCTAssertEqual(after.source, "github")
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

    func testHistoryRestoreAndDeletedVariants() async throws {
        try await configureGit()
        try await TravelRepoSession.createTrip(root: root.appendingPathComponent("trip"), title: "Spring in Japan", gitName: "Alex", gitEmail: "alex@example.invalid")
        let session = try await TravelRepoSession.open(root: root.appendingPathComponent("trip"), recoveryRoot: root.appendingPathComponent("recovery"))
        let first = try await session.history()[0].commit
        try await session.call("create_entity", ["type": "place", "data": ["title": "Nara Park"]])
        try await session.call("create_version", ["message": "Add Nara Park"])
        let health = try await session.gitHealth()
        XCTAssertNil(health.staleLock)
        XCTAssertEqual(health.tripChanges, 0)
        let preview = try await session.restorePreview(commit: first)
        XCTAssertTrue(preview.changes.contains { $0.kind == "removed" && $0.path.hasPrefix("places/") })
        try await session.restoreApply(commit: first, plan: preview.plan)
        try await session.call("create_version", ["message": "Back to the start"])
        let newest = try await session.history().first?.title
        XCTAssertEqual(newest, "Back to the start")

        try await session.call("create_variant", ["branch": "variants/deer", "title": "Deer first"])
        let primary = try await session.call("variants")["variants"]?.array?.first { $0["primary"]?.bool == true }?["branch"]?.string
        try await session.call("switch_variant", ["branch": .string(primary ?? "main")])
        try await session.call("delete_variant", ["branch": "variants/deer"])
        let deleted = try await session.deletedVariants()
        XCTAssertEqual(deleted.first?.title, "Deer first")
        let restored = try await session.restoreVariant(deleted[0])
        XCTAssertEqual(restored, "variants/deer")
        let bin = try await session.deletedVariants()
        XCTAssertTrue(bin.isEmpty)
    }

    func testPlanEditsAcrossDaylightSavingTime() async throws {
        let session = try await openTrip()
        let walk = try await session.call("create_entity", ["type": "schedule_item", "data": ["title": "Walk around Nara Park"]])["id"]!.string!
        let sam = try await session.call("create_entity", ["type": "person", "data": ["title": "Sam"]])["id"]!.string!
        // Hamburg moves its clocks forward on 28 March 2027: same local time and length on the new day.
        try await session.schedule(walk, local: "2027-03-27T10:00:00", timezone: "Europe/Berlin", minutes: 100)
        try await session.moveToDay(walk, day: "2027-03-28")
        let entry = try await session.calendar().first { $0.item == walk }
        XCTAssertEqual(entry?.start, "2027-03-28T08:00:00Z")
        XCTAssertEqual(entry?.end, "2027-03-28T09:40:00Z")
        do { try await session.schedule(walk, local: "2027-03-28T02:30:00", timezone: "Europe/Berlin"); XCTFail("A skipped time was accepted") }
        catch let error as TravelRepoError { XCTAssertEqual(error.code, "time.gap") }

        let copy = try await session.duplicate(walk, title: "Walk around Nara Park again")
        let comment = try await session.addComment(on: copy, author: sam, body: "Deer crackers?")
        let comments = try await session.comments(on: copy)
        XCTAssertEqual(comments.first?.authorName, "Sam")
        let preview = try await session.deletePreview([copy])
        XCTAssertEqual(preview.dependents.map(\.id), [comment])
        try await session.deleteEntities([copy])
        let left = try await session.comments(on: copy)
        XCTAssertTrue(left.isEmpty)

        let idea = try await session.call("create_entity", ["type": "schedule_item", "data": ["title": "Deer park in Nara"]])["id"]!.string!
        try await session.planMaterial(idea, local: "2027-05-18T10:00:00", timezone: "Asia/Tokyo")
        let status = try await session.call("get_entity", ["id": .string(idea)])["entity"]?["status"]?.string
        XCTAssertEqual(status, "planned")
        try await session.addComment(on: idea, author: sam, body: "Bring crackers", mentions: [sam])
        let missing = try await session.missingNights()
        XCTAssertNotNil(missing)

        let vocabulary = try await session.vocabulary()
        XCTAssertTrue(vocabulary.expenseCategories.contains("food"))
        let category = try await session.suggestCategory(title: "Ryokan in Kyoto")
        XCTAssertEqual(category, "accommodation")
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
