import CTravelRepo
import Foundation

/// A failed TravelRepo call: the TravelRepo error code (such as `repository.changed`) and its English message.
public struct TravelRepoError: Error, Equatable, CustomStringConvertible {
    public let code: String
    public let message: String
    public var description: String { "\(code): \(message)" }
}

/// TravelRepo through its C interface. Every call runs on a TravelRepo thread and never blocks the caller;
/// cancelling the Swift task cancels the call.
public enum TravelRepo {
    /// The ABI version this package was written for.
    public static let abiVersion = Int(TRAVELREPO_ABI_VERSION)

    /// Runs `command` in `session` (0 for commands without one) and returns the `result` as JSON data.
    public static func callData(_ command: String, session: UInt64 = 0, arguments: Data? = nil) async throws -> Data {
        let box = CallBox()
        let response: String = try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (done: CheckedContinuation<String, Error>) in
                box.start(done)
                let context = Unmanaged.passRetained(box).toOpaque()
                let callback: tr_callback = { context, response in
                    Unmanaged<CallBox>.fromOpaque(context!).takeRetainedValue().finish(String(cString: response!))
                }
                let json = arguments.map { String(decoding: $0, as: UTF8.self) }
                let id: UInt64 = command.withCString { name in
                    if let json { return json.withCString { tr_call(session, name, $0, callback, context) } }
                    return tr_call(session, name, nil, callback, context)
                }
                box.started(id)
            }
        } onCancel: { box.cancel() }
        let envelope = try JSONSerialization.jsonObject(with: Data(response.utf8)) as? [String: Any] ?? [:]
        if envelope["ok"] as? Bool == true {
            return try JSONSerialization.data(withJSONObject: envelope["result"] ?? NSNull(), options: [.fragmentsAllowed])
        }
        let error = envelope["error"] as? [String: Any]
        throw TravelRepoError(code: error?["code"] as? String ?? "internal", message: error?["message"] as? String ?? response)
    }

    /// Runs `command` with encodable arguments and decodes its result.
    public static func call<Result: Decodable>(_ command: String, session: UInt64 = 0, _ arguments: [String: JSONValue] = [:], as type: Result.Type = Result.self) async throws -> Result {
        let data = try await callData(command, session: session, arguments: arguments.isEmpty ? nil : try JSONEncoder().encode(arguments))
        return try JSONDecoder().decode(Result.self, from: data)
    }

    /// Version, format version and commands.
    public static func info() async throws -> Info { try await call("info") }

    /// Tells Git where libgit2 is, for example `Bundle.main.privateFrameworksPath! + "/git2.framework/git2"`.
    public static func configure(libgit2: String) async throws {
        _ = try await callData("configure", arguments: try JSONEncoder().encode(["libgit2": libgit2]))
    }

    /// Answers Git's requests for credentials, for example with a token from the Keychain. Nil removes it.
    public static func setCredentialProvider(_ provider: (@Sendable (URL) async -> (username: String, password: String)?)?) {
        Credentials.provider = provider
        if provider == nil { tr_set_credential_handler(nil, nil); return }
        tr_set_credential_handler({ _, request, origin in
            let url = URL(string: String(cString: origin!))
            Task.detached {
                let credential: (username: String, password: String)? = if let url, let provider = Credentials.provider { await provider(url) } else { nil }
                if let credential, let json = try? JSONEncoder().encode(["username": credential.username, "password": credential.password]) {
                    String(decoding: json, as: UTF8.self).withCString { tr_credential_reply(request, $0) }
                } else {
                    tr_credential_reply(request, nil)
                }
            }
        }, nil)
    }
}

extension TravelRepo {
    /// Receives TravelRepo's events: `clone_progress` while cloning (with the call's objects and checked-out files)
    /// and `trip_changed` for sessions that `watch` (with the session id and new revision). Called on a TravelRepo
    /// thread; hand the event over to the main actor before touching the UI. Nil stops it.
    public static func setEventHandler(_ handler: (@Sendable (JSONValue) -> Void)?) {
        Events.handler = handler
        if handler == nil { tr_set_event_handler(nil, nil); return }
        tr_set_event_handler({ _, event in
            guard let event, let value = try? JSONDecoder().decode(JSONValue.self, from: Data(String(cString: event).utf8)) else { return }
            Events.handler?(value)
        }, nil)
    }
}

private enum Events {
    nonisolated(unsafe) static var handler: (@Sendable (JSONValue) -> Void)?
}

private enum Credentials {
    nonisolated(unsafe) static var provider: (@Sendable (URL) async -> (username: String, password: String)?)?
}

/// One call in flight: its continuation and its id, for cancellation.
private final class CallBox: @unchecked Sendable {
    private let lock = NSLock()
    private var done: CheckedContinuation<String, Error>?
    private var id: UInt64 = 0
    private var cancelled = false

    func start(_ done: CheckedContinuation<String, Error>) { lock.withLock { self.done = done } }
    func started(_ id: UInt64) {
        let cancel = lock.withLock { self.id = id; return cancelled }
        if cancel { tr_cancel(id) }
    }
    func cancel() {
        let id = lock.withLock { cancelled = true; return self.id }
        if id != 0 { tr_cancel(id) }
    }
    func finish(_ response: String) {
        let done = lock.withLock { let d = self.done; self.done = nil; return d }
        done?.resume(returning: response)
    }
}
