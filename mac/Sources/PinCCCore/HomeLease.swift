import Foundation
import Darwin

public struct MaintenanceBusyError: Error, LocalizedError {
    public var errorDescription: String? { "图片维护正忙，请稍后重试" }
}

/// A local file lease. flock follows filesystem identity on APFS and symlink
/// aliases. Lock files stay in place; unlinking a held lock would split owners.
public final class HomeLease {
    private final class Entry {
        let gate = NSRecursiveLock()
        var descriptor: Int32 = -1
        var depth = 0
    }
    private final class Registry: @unchecked Sendable {
        let gate = NSLock()
        var entries: [String: Entry] = [:]
        func entry(_ path: String) -> Entry {
            gate.lock()
            defer { gate.unlock() }
            if let existing = entries[path] { return existing }
            let entry = Entry()
            entries[path] = entry
            return entry
        }
    }
    private static let registry = Registry()
    private var entry: Entry?

    private init(_ entry: Entry) { self.entry = entry }
    deinit { close() }

    public static func tryEnter(home: String, purpose: String, waitMs: Int = 0) throws -> HomeLease? {
        guard ["Instance", "Images", "Backup", "Recovery", "Log"].contains(purpose) else {
            throw CocoaError(.fileWriteInvalidFileName)
        }
        let homeURL = URL(fileURLWithPath: Home.canonical(home), isDirectory: true)
        try FileManager.default.createDirectory(at: homeURL, withIntermediateDirectories: true)
        let name = purpose == "Instance" ? ".instance" : ".lock-" + purpose.lowercased()
        let path = homeURL.appendingPathComponent(name).path
        let entry = registry.entry(path)
        let deadline = DispatchTime.now().uptimeNanoseconds + UInt64(max(0, min(waitMs, 60_000))) * 1_000_000
        while !entry.gate.try() {
            if DispatchTime.now().uptimeNanoseconds >= deadline { return nil }
            usleep(5_000)
        }
        if entry.depth > 0 { entry.depth += 1; return HomeLease(entry) }
        let descriptor = Darwin.open(path, O_CREAT | O_RDWR | O_CLOEXEC, mode_t(0o600))
        guard descriptor >= 0 else {
            let error = posixError()
            entry.gate.unlock()
            throw error
        }
        while Darwin.flock(descriptor, LOCK_EX | LOCK_NB) != 0 {
            let code = errno
            if code != EWOULDBLOCK && code != EAGAIN && code != EINTR {
                Darwin.close(descriptor)
                entry.gate.unlock()
                throw NSError(domain: NSPOSIXErrorDomain, code: Int(code))
            }
            if DispatchTime.now().uptimeNanoseconds >= deadline {
                Darwin.close(descriptor)
                entry.gate.unlock()
                return nil
            }
            usleep(5_000)
        }
        entry.descriptor = descriptor
        entry.depth = 1
        return HomeLease(entry)
    }

    public static func enterImages(home: String) throws -> HomeLease {
        guard let lease = try tryEnter(home: home, purpose: "Images", waitMs: 250) else { throw MaintenanceBusyError() }
        return lease
    }

    /// A lease is synchronous and must be released on the acquiring thread.
    public func close() {
        guard let entry else { return }
        self.entry = nil
        entry.depth -= 1
        if entry.depth == 0 {
            Darwin.flock(entry.descriptor, LOCK_UN)
            Darwin.close(entry.descriptor)
            entry.descriptor = -1
        }
        entry.gate.unlock()
    }

    private static func posixError() -> NSError { NSError(domain: NSPOSIXErrorDomain, code: Int(errno)) }
}
