import Foundation
import CryptoKit
import Darwin

public struct EditDraft: Codable, Sendable {
    public let key: String
    public let card: Card
    public let expectedRev: Int64?
    public let ref: Int64
    public let editorText: String?
    public let addText: String?
    public let groupNumber: Int64?

    public init(key: String, card: Card, expectedRev: Int64?, ref: Int64 = 0,
                editorText: String? = nil, addText: String? = nil, groupNumber: Int64? = nil) {
        self.key = key
        self.card = card
        self.expectedRev = expectedRev
        self.ref = ref
        self.editorText = editorText
        self.addText = addText
        self.groupNumber = groupNumber
    }

    private enum CodingKeys: String, CodingKey {
        case key = "Key", card = "Card", expectedRev = "ExpectedRev", ref = "Ref"
        case editorText = "EditorText", addText = "AddText", groupNumber = "GroupNumber"
    }
    public init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        key = try values.decode(String.self, forKey: .key)
        card = try values.decode(Card.self, forKey: .card)
        expectedRev = try values.decodeIfPresent(Int64.self, forKey: .expectedRev)
        ref = try values.decodeIfPresent(Int64.self, forKey: .ref) ?? 0
        editorText = try values.decodeIfPresent(String.self, forKey: .editorText)
        addText = try values.decodeIfPresent(String.self, forKey: .addText)
        groupNumber = try values.decodeIfPresent(Int64.self, forKey: .groupNumber)
    }
}

public struct RecoveredDraft: Sendable {
    public let card: Card
    public let groupNumber: Int64
    public init(card: Card, groupNumber: Int64) { self.card = card; self.groupNumber = groupNumber }
}
public struct PendingDraft: Sendable {
    public let path: String
    public let drafts: [EditDraft]
    public init(path: String, drafts: [EditDraft]) { self.path = path; self.drafts = drafts }
}

public enum DraftRecoveryError: Error, LocalizedError {
    case busy, invalidPath, oversized
    public var errorDescription: String? {
        switch self {
        case .busy: return "草稿恢复正忙，请稍后重试"
        case .invalidPath: return "草稿文件不在当前 Home 的恢复目录"
        case .oversized: return "草稿文件过大，保持原文件供人工恢复"
        }
    }
}

/// Durable checkpoints survive database write failure. Successful automatic
/// recovery is idempotent; conflicts stay available for explicit recovery as new.
public enum DraftRecovery {
    private static let maximumFileBytes = 16 * 1_024 * 1_024

    public static func fallbackDirectory(home: String) -> URL {
        let identity = SHA256.hash(data: Data(Home.canonical(home).utf8)).map { String(format: "%02x", $0) }.joined()
        return Home.applicationSupport().appendingPathComponent("recovery-fallback", isDirectory: true)
            .appendingPathComponent(String(identity.prefix(16)), isDirectory: true)
    }

    /// The returned path is a checkpoint owned by its caller. After a successful
    /// autosave, discard that checkpoint; a failed save keeps it visible.
    public static func write(home: String, drafts: [EditDraft]) throws -> String? {
        guard !drafts.isEmpty else { return nil }
        let data = try JSONEncoder().encode(drafts)
        guard data.count <= maximumFileBytes else { throw DraftRecoveryError.oversized }
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = TimeZone(secondsFromGMT: 0)
        formatter.dateFormat = "yyyyMMdd-HHmmssSSS"
        let name = "edits-\(formatter.string(from: Date()))-\(ProcessInfo.processInfo.processIdentifier)-\(UUID().uuidString.lowercased()).json"
        let fallback = fallbackDirectory(home: home).appendingPathComponent(name)
        do { try LocalFiles.atomicWrite(data, to: fallback); return fallback.path }
        catch {
            let primary = URL(fileURLWithPath: Home.canonical(home), isDirectory: true)
                .appendingPathComponent("recovery", isDirectory: true).appendingPathComponent(name)
            try LocalFiles.atomicWrite(data, to: primary)
            return primary.path
        }
    }

    /// Newest checkpoints are read first, so retries of a session never recover
    /// an older snapshot before the latest durable one.
    public static func pending(home: String, onFailure: (String, Error) -> Void = { _, _ in }) throws -> [PendingDraft] {
        // A malformed file stays on disk without preventing other checkpoints
        // from being recovered. The data/recovery page can expose the directory.
        try files(home: home).prefix(100).compactMap { url in
            let drafts: [EditDraft]
            do { drafts = try read(url) }
            catch { onFailure(url.path, error); return nil }
            return PendingDraft(path: url.path, drafts: drafts)
        }
    }

    public static func recover(home: String, onFailure: (String, Error) -> Void = { _, _ in }) throws -> [RecoveredDraft] {
        guard let lease = try HomeLease.tryEnter(home: home, purpose: "Recovery", waitMs: 250) else { return [] }
        defer { lease.close() }
        let store = try Store(home: home, busyMs: 250)
        defer { store.close() }
        guard !store.readOnly else { return [] }
        var recovered: [RecoveredDraft] = []
        for pending in try pending(home: home, onFailure: onFailure) {
            var complete = true
            for draft in pending.drafts {
                do {
                    let saved = try store.saveRecovery(key: draft.key, snapshot: draft.card,
                                                       expectedRev: draft.expectedRev, groupNumber: draft.groupNumber)
                    if !recovered.contains(where: { $0.card.id == saved.card.id }) { recovered.append(saved) }
                } catch { complete = false; onFailure(pending.path, error) }
            }
            if complete {
                do { try completeFile(home: home, pending: pending, store: store) }
                catch { onFailure(pending.path, error) }
            }
        }
        return recovered
    }

    /// User requested recovery creates a new active card. Existing bodies and
    /// their undo history remain untouched. Partial failures keep the file.
    public static func restoreAsNew(home: String, pending: PendingDraft) throws -> [Card] {
        guard let lease = try HomeLease.tryEnter(home: home, purpose: "Recovery", waitMs: 250) else { throw DraftRecoveryError.busy }
        defer { lease.close() }
        let url = try checkedURL(home: home, path: pending.path)
        let actual = PendingDraft(path: url.path, drafts: try read(url))
        let store = try Store(home: home, busyMs: 250)
        defer { store.close() }
        var saved: [Card] = []
        for draft in actual.drafts {
            saved.append(try store.saveRecovery(key: draft.key, snapshot: draft.card,
                                               expectedRev: draft.expectedRev, asNew: true).card)
        }
        try completeFile(home: home, pending: actual, store: store)
        return saved
    }

    /// Only the tracked file is discarded. File removal happens before marker
    /// cleanup, and a marker remains while any other checkpoint uses its key.
    public static func discard(home: String, path: String) throws {
        guard let lease = try HomeLease.tryEnter(home: home, purpose: "Recovery", waitMs: 250) else { throw DraftRecoveryError.busy }
        defer { lease.close() }
        let url = try checkedURL(home: home, path: path)
        if !FileManager.default.fileExists(atPath: url.path) { return }
        let pending = PendingDraft(path: url.path, drafts: try read(url))
        let store = try Store(home: home, busyMs: 250)
        defer { store.close() }
        try completeFile(home: home, pending: pending, store: store)
    }

    private static func completeFile(home: String, pending: PendingDraft, store: Store) throws {
        let url = try checkedURL(home: home, path: pending.path)
        try FileManager.default.removeItem(at: url)
        // All marker cleanup is after the durable file is successfully removed.
        let remainingFiles = try files(home: home)
        // Do not clear markers when a bounded scan cannot establish whether an
        // older or damaged checkpoint still holds the same recovery key.
        guard remainingFiles.count <= 100 else { return }
        var remainingKeys = Set<String>()
        for file in remainingFiles {
            guard let drafts = try? read(file) else { return }
            remainingKeys.formUnion(drafts.map(\.key))
        }
        for draft in pending.drafts where !remainingKeys.contains(draft.key) {
            try store.setSetting("recovery." + draft.key, nil)
            try store.setSetting("recovery-group." + draft.key, nil)
        }
    }

    private static func files(home: String) throws -> [URL] {
        var result: [URL] = []
        for directory in directories(home: home) where FileManager.default.fileExists(atPath: directory.path) {
            result += try FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: [.isRegularFileKey, .fileSizeKey])
                .filter { $0.lastPathComponent.hasPrefix("edits-") && $0.pathExtension == "json" }
        }
        return result.sorted { $0.lastPathComponent > $1.lastPathComponent }
    }

    private static func directories(home: String) -> [URL] {
        [URL(fileURLWithPath: Home.canonical(home), isDirectory: true).appendingPathComponent("recovery", isDirectory: true),
         fallbackDirectory(home: home)]
    }
    private static func checkedURL(home: String, path: String) throws -> URL {
        let url = URL(fileURLWithPath: path).standardizedFileURL.resolvingSymlinksInPath()
        guard url.lastPathComponent.hasPrefix("edits-"), url.pathExtension == "json",
              directories(home: home).contains(where: { $0.standardizedFileURL.resolvingSymlinksInPath().path == url.deletingLastPathComponent().path }) else {
            throw DraftRecoveryError.invalidPath
        }
        return url
    }
    private static func read(_ url: URL) throws -> [EditDraft] {
        let values = try url.resourceValues(forKeys: [.fileSizeKey, .isRegularFileKey])
        guard values.isRegularFile == true, (values.fileSize ?? 0) <= maximumFileBytes else { throw DraftRecoveryError.oversized }
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        let data = try handle.read(upToCount: maximumFileBytes + 1) ?? Data()
        guard data.count <= maximumFileBytes else { throw DraftRecoveryError.oversized }
        return try JSONDecoder().decode([EditDraft].self, from: data)
    }
}

/// The image and checkpoint writers share the same durable local file primitive.
enum LocalFiles {
    static func atomicWrite(_ data: Data, to destination: URL) throws {
        let directory = destination.deletingLastPathComponent()
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let temporary = directory.appendingPathComponent("." + destination.lastPathComponent + "." + UUID().uuidString + ".tmp")
        defer { try? FileManager.default.removeItem(at: temporary) }
        let descriptor = Darwin.open(temporary.path, O_CREAT | O_EXCL | O_WRONLY | O_CLOEXEC, mode_t(0o600))
        guard descriptor >= 0 else { throw NSError(domain: NSPOSIXErrorDomain, code: Int(errno)) }
        let handle = FileHandle(fileDescriptor: descriptor, closeOnDealloc: true)
        do {
            try handle.write(contentsOf: data)
            try handle.synchronize()
            try handle.close()
        } catch {
            try? handle.close()
            throw error
        }
        try promote(temporary, to: destination)
    }

    static func promote(_ temporary: URL, to destination: URL) throws {
        guard Darwin.rename(temporary.path, destination.path) == 0 else {
            throw NSError(domain: NSPOSIXErrorDomain, code: Int(errno))
        }
        let descriptor = Darwin.open(destination.deletingLastPathComponent().path, O_RDONLY | O_CLOEXEC)
        if descriptor >= 0 { _ = Darwin.fsync(descriptor); Darwin.close(descriptor) }
    }
}
