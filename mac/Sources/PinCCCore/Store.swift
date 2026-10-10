import Foundation
import CryptoKit
import Darwin

public struct Op: Codable, Sendable {
    public let id: Int64
    public let grp: Int64
    public let at: Int64
    public let cardId: Int64
    public let name: String
    public let before: String?
    public let cols: String?
}

public final class Group: @unchecked Sendable {
    fileprivate struct Entry { var rev: Int64; let op: Int64; var cols: String }
    fileprivate let gate = NSRecursiveLock()
    fileprivate var id: Int64 = 0
    fileprivate var cards: [Int64: Entry] = [:]
    public init() {}
    public var number: Int64 { gate.lock(); defer { gate.unlock() }; return id }
    public var empty: Bool { number == 0 }
}

public struct RevConflictError: Error, LocalizedError, Sendable {
    public let current: Int64
    public var errorDescription: String? { "卡片已被修改（当前 rev=\(current)）" }
}

public enum StoreError: Error, LocalizedError {
    case newerSchema, missingCard, invalidRecoveryKey, invalidImageExtension
    public var errorDescription: String? {
        switch self {
        case .newerSchema: return "数据库由更新版本的 PinCC 创建，当前版本只能读取。请更新 PinCC。"
        case .missingCard: return "草稿对应的卡片已不存在，草稿保持不变"
        case .invalidRecoveryKey: return "恢复草稿缺少编号"
        case .invalidImageExtension: return "图片扩展名无效"
        }
    }
}

/// Shared local persistence for the floating cards and CLI. Writes update only
/// changed columns, guarded by revision; each gesture owns one undo group.
public final class Store: @unchecked Sendable {
    public static let schemaVersion = 2
    public let home: String
    public let imgDir: String
    public let db: Db
    public let readOnly: Bool
    private let gate = NSRecursiveLock()

    private static let schema = """
        CREATE TABLE IF NOT EXISTS cards(
          id INTEGER PRIMARY KEY AUTOINCREMENT, rev INTEGER NOT NULL DEFAULT 0,
          body TEXT NOT NULL DEFAULT '', images TEXT, summary TEXT, meta TEXT,
          color INTEGER, topmost INTEGER NOT NULL DEFAULT 0, opacity REAL NOT NULL DEFAULT 1,
          collapsed INTEGER NOT NULL DEFAULT 0, free INTEGER NOT NULL DEFAULT 0,
          mon TEXT, x REAL NOT NULL DEFAULT 0, y REAL NOT NULL DEFAULT 0, w REAL NOT NULL DEFAULT 260, h REAL,
          remind_at INTEGER, done_at INTEGER, deleted_at INTEGER, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL, ord REAL);
        CREATE TABLE IF NOT EXISTS ops(
          id INTEGER PRIMARY KEY AUTOINCREMENT, grp INTEGER NOT NULL, at INTEGER NOT NULL, card_id INTEGER NOT NULL,
          op TEXT NOT NULL, before TEXT, cols TEXT);
        CREATE INDEX IF NOT EXISTS ops_grp ON ops(grp);
        CREATE INDEX IF NOT EXISTS ops_card ON ops(card_id,id);
        CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY,value TEXT);
        """

    public init(home: String? = nil, busyMs: Int = 5_000) throws {
        self.home = Home.resolve(home)
        imgDir = URL(fileURLWithPath: self.home, isDirectory: true).appendingPathComponent("img", isDirectory: true).path
        try FileManager.default.createDirectory(atPath: self.home, withIntermediateDirectories: true)
        db = try Db(path: URL(fileURLWithPath: self.home).appendingPathComponent("pincc.db").path, busyMs: busyMs, configureWAL: false)
        readOnly = (try db.scalar("PRAGMA user_version").int64 ?? 0) > Int64(Self.schemaVersion)
        if !readOnly {
            try FileManager.default.createDirectory(atPath: imgDir, withIntermediateDirectories: true)
            try db.enableWAL()
            if try userVersion() < Int64(Self.schemaVersion) || preRelease() { try migrate() }
        }
    }

    public func close() { locked { db.close() } }
    public var dataVersion: Int64 { get throws { try locked { try db.dataVersion } } }

    public func get(_ id: Int64) throws -> Card? {
        try locked { try db.query("SELECT * FROM cards WHERE id=?", [.integer(id)]).first.map { try Card(row: $0) } }
    }
    public func all() throws -> [Card] {
        try locked { try db.query("SELECT * FROM cards").map { try Card(row: $0) } }
    }
    public func active() throws -> [Card] {
        try locked {
            try db.query("SELECT * FROM cards WHERE done_at IS NULL AND deleted_at IS NULL")
                .map { try Card(row: $0) }.sorted(by: Card.compare)
        }
    }

    public func begin() -> Group { Group() }

    public func reserve(_ group: Group) throws {
        try locked {
            try writable()
            try inTransaction(group) {
                if group.id == 0 { try journal(group, cardId: 0, rev: 0, newRev: 0, op: "run", before: nil, columns: []) }
            }
        }
    }

    public func join(_ number: Int64) throws -> Group? {
        try locked {
            guard try db.scalar("SELECT count(*) FROM ops WHERE grp=? AND op='run' AND card_id=0 AND at>?",
                                [.integer(number), .integer(Self.nowMs - 3_600_000)]).int64 ?? 0 > 0 else { return nil }
            let group = Group()
            group.id = number
            return group
        }
    }

    public func release(_ group: Group) throws {
        try locked {
            group.gate.lock()
            defer { group.gate.unlock() }
            if group.id == 0 || readOnly { return }
            try db.transaction {
                if try db.scalar("SELECT count(*) FROM ops WHERE grp=? AND card_id<>0", [.integer(group.id)]).int64 == 0 {
                    try db.exec("DELETE FROM ops WHERE grp=? AND card_id=0 AND op='run'", [.integer(group.id)])
                }
            }
            if try db.scalar("SELECT count(*) FROM ops WHERE grp=?", [.integer(group.id)]).int64 == 0 {
                group.id = 0
                group.cards.removeAll()
            }
        }
    }

    public func insert(_ group: Group, _ card: Card) throws -> Card {
        try locked { try writable(); return try inTransaction(group) { try insertRow(group, card) } }
    }

    /// A computed multi-card result commits together, after its source guard.
    public func insertMany(_ group: Group, _ cards: [Card], canApply: (() throws -> Bool)? = nil,
                           cancelled: () -> Bool = { false }) throws -> [Card] {
        try locked {
            try writable()
            return try inTransaction(group) {
                if cancelled() { throw CancellationError() }
                if let canApply, try !canApply() { return [] }
                var saved: [Card] = []
                for card in cards {
                    if cancelled() { throw CancellationError() }
                    saved.append(try insertRow(group, card))
                }
                if cancelled() { throw CancellationError() }
                return saved
            }
        }
    }

    public func mutate(_ group: Group, id: Int64, op: String, ifRev: Int64? = nil, journal: Bool = true,
                       change: (inout Card) throws -> Void) throws -> Card? {
        try locked {
            try writable()
            return try inTransaction(group) {
                guard let current = try get(id) else { return nil }
                if let ifRev, current.rev != ifRev { throw RevConflictError(current: current.rev) }
                var next = current
                try change(&next)
                return try write(group, current: current, next: next, op: op, record: journal)
            }
        }
    }

    public func ops(limit: Int = 200, cardId: Int64? = nil) throws -> [Op] {
        try locked {
            let rows: [Db.Row]
            if let cardId {
                rows = try db.query("SELECT * FROM ops WHERE card_id=? ORDER BY id DESC LIMIT ?", [.integer(cardId), .integer(Int64(limit))])
            } else {
                rows = try db.query("SELECT * FROM ops ORDER BY id DESC LIMIT ?", [.integer(Int64(limit))])
            }
            return rows.map { row in
                Op(id: row["id"]?.int64 ?? 0, grp: row["grp"]?.int64 ?? 0, at: row["at"]?.int64 ?? 0,
                   cardId: row["card_id"]?.int64 ?? 0, name: row["op"]?.string ?? "",
                   before: row["before"]?.string, cols: row["cols"]?.string)
            }
        }
    }

    /// Only fields changed by the selected group are restored. Later changes to
    /// other fields survive. Restore itself is a new undoable group.
    public func restore(group number: Int64, into inverse: Group? = nil) throws -> [Int64] {
        try locked {
            try writable()
            // The caller keeps this exact inverse group for redo. Reading the
            // latest op after commit could pick an unrelated concurrent writer.
            let group = inverse ?? begin()
            return try inTransaction(group) {
                var ids: [Int64] = []
                for row in try db.query("SELECT * FROM ops WHERE grp=? ORDER BY id DESC", [.integer(number)]) {
                    let id = row["card_id"]?.int64 ?? 0
                    if id == 0 { continue }
                    if !ids.contains(id) { ids.append(id) }
                    let current = try get(id)
                    if let json = row["before"]?.string {
                        let target = try Card.fromJSON(json)
                        if let current {
                            let columns = row["cols"]?.string.map { $0.split(separator: ",").map(String.init) } ?? Card.columns
                            _ = try write(group, current: current, next: current.with(from: target, columns: columns), op: "restore", record: true)
                        } else {
                            try db.exec("INSERT INTO cards(id,rev,updated_at,\(Card.columns.joined(separator: ","))) VALUES(?,1,?,\(marks(Card.columns.count)))",
                                        [.integer(id), .integer(Self.nowMs)] + target.values())
                            try journal(group, cardId: id, rev: 0, newRev: 1, op: "restore", before: nil, columns: [])
                        }
                    } else if let current, current.deletedAt == nil {
                        var next = current
                        next.deletedAt = Self.nowMs
                        _ = try write(group, current: current, next: next, op: "restore", record: true)
                    }
                }
                return ids
            }
        }
    }

    /// Permanent deletion is called only by an explicit user action or for an
    /// empty abandoned new card. Ordinary removal uses deletedAt and a group.
    public func forget(_ id: Int64) throws {
        try locked {
            try writable()
            try db.transaction {
                try db.exec("DELETE FROM ops WHERE card_id=?", [.integer(id)])
                try db.exec("DELETE FROM cards WHERE id=?", [.integer(id)])
            }
        }
    }

    /// Recovery key, group number and content commit atomically. A stale draft
    /// remains on disk; it never replaces an externally edited body.
    public func saveRecovery(key: String, snapshot: Card, expectedRev: Int64?, groupNumber: Int64? = nil,
                             asNew: Bool = false) throws -> RecoveredDraft {
        try locked {
            try writable()
            guard !key.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { throw StoreError.invalidRecoveryKey }
            let group = begin()
            return try inTransaction(group) {
                if let savedID = Int64(try setting("recovery." + key)) {
                    guard let card = try get(savedID) else { throw StoreError.missingCard }
                    return RecoveredDraft(card: card, groupNumber: Int64(try setting("recovery-group." + key)) ?? 0)
                }
                if !asNew, snapshot.id > 0, let groupNumber, groupNumber > 0,
                   try db.scalar("SELECT EXISTS(SELECT 1 FROM ops WHERE grp=? AND card_id=? AND op IN ('edit','create'))",
                                 [.integer(groupNumber), .integer(snapshot.id)]).int64 != 0 {
                    group.id = groupNumber
                }
                let saved: Card
                if !asNew && snapshot.id > 0 {
                    guard let current = try get(snapshot.id) else { throw StoreError.missingCard }
                    if !current.body.utf16.elementsEqual(snapshot.body.utf16) && (expectedRev == nil || current.rev != expectedRev) {
                        throw RevConflictError(current: current.rev)
                    }
                    var next = current
                    next.body = snapshot.body
                    saved = try write(group, current: current, next: next, op: "edit", record: true)
                } else {
                    var fresh = snapshot
                    fresh.id = 0
                    fresh.rev = 0
                    if asNew {
                        fresh.createdAt = 0
                        fresh.doneAt = nil
                        fresh.deletedAt = nil
                        fresh.free = false
                        fresh.mon = nil
                        fresh.ord = nil
                    }
                    saved = try insertRow(group, fresh)
                }
                try setSetting("recovery." + key, String(saved.id))
                try setSetting("recovery-group." + key, String(group.id))
                return RecoveredDraft(card: saved, groupNumber: group.id)
            }
        }
    }

    public func setting(_ key: String, fallback: String = "") throws -> String {
        try locked { try db.scalar("SELECT value FROM settings WHERE key=?", [.text(key)]).string ?? fallback }
    }
    public func settingInt(_ key: String, fallback: Int) throws -> Int { Int(try setting(key)) ?? fallback }
    public func settingBool(_ key: String, fallback: Bool) throws -> Bool {
        let value = try setting(key)
        return value.isEmpty ? fallback : value == "1"
    }
    public func setSetting(_ key: String, _ value: String?) throws {
        try locked {
            try writable()
            if let value {
                try db.exec("INSERT INTO settings(key,value) VALUES(?,?) ON CONFLICT(key) DO UPDATE SET value=excluded.value", [.text(key), .text(value)])
            } else { try db.exec("DELETE FROM settings WHERE key=?", [.text(key)]) }
        }
    }
    public func setSetting(_ key: String, _ value: Bool) throws { try setSetting(key, value ? "1" : "0") }

    public func saveImage(_ data: Data, ext: String = ".png") throws -> String {
        try locked {
            try writable()
            let lease = try HomeLease.enterImages(home: home)
            defer { lease.close() }
            let suffix = ext.hasPrefix(".") ? String(ext.dropFirst()).lowercased() : ext.lowercased()
            guard (2...8).contains(suffix.count), suffix.allSatisfy({ $0.isASCII && ($0.isLetter || $0.isNumber) }) else {
                throw StoreError.invalidImageExtension
            }
            let hash = Insecure.SHA1.hash(data: data).map { String(format: "%02x", $0) }.joined()
            let name = String(hash.prefix(20)) + "." + suffix
            let url = URL(fileURLWithPath: imgDir, isDirectory: true).appendingPathComponent(name)
            if FileManager.default.fileExists(atPath: url.path) {
                try FileManager.default.setAttributes([.modificationDate: Date()], ofItemAtPath: url.path)
            } else { try LocalFiles.atomicWrite(data, to: url) }
            return "img/" + name
        }
    }

    public func resolveImage(_ source: String) -> String {
        guard source.lowercased().hasPrefix("img/") else { return source }
        let name = String(source.dropFirst(4))
        guard !name.isEmpty, name != ".", name != "..", !name.contains("/"), !name.contains("\\") else { return source }
        return URL(fileURLWithPath: imgDir, isDirectory: true).appendingPathComponent(name).path
    }

    /// Maintenance is synchronous and bounded. The app invokes it off MainActor
    /// with its own Store; no directory scan occupies the GUI connection.
    public func purge(days: Int, cancelled: @escaping @Sendable () -> Bool = { false }) throws {
        if readOnly { return }
        if cancelled() { throw CancellationError() }
        let cutoff = Self.nowMs - Int64(Int32(clamping: days)) * 86_400_000
        try db.withBudget(seconds: 2, cancelled: cancelled) {
            try db.transaction {
                try db.exec("DELETE FROM ops WHERE id IN (SELECT id FROM ops WHERE at<? LIMIT 2000)", [.integer(cutoff)])
                try db.exec("DELETE FROM cards WHERE id IN (SELECT id FROM cards WHERE deleted_at IS NOT NULL AND deleted_at<? LIMIT 2000)", [.integer(cutoff)])
            }
        }
        let started = DispatchTime.now().uptimeNanoseconds
        let old = Date().addingTimeInterval(-Double(max(1, days)) * 86_400)
        guard let iterator = FileManager.default.enumerator(at: URL(fileURLWithPath: imgDir), includingPropertiesForKeys: [.contentModificationDateKey, .isRegularFileKey],
                                                            options: [.skipsHiddenFiles, .skipsSubdirectoryDescendants]) else { return }
        for case let url as URL in iterator {
            if cancelled() { throw CancellationError() }
            let elapsed = Double(DispatchTime.now().uptimeNanoseconds - started) / 1_000_000_000
            if elapsed >= 2 { break }
            let attributes = try url.resourceValues(forKeys: [.contentModificationDateKey, .isRegularFileKey])
            guard attributes.isRegularFile == true, let modified = attributes.contentModificationDate, modified < old else { continue }
            guard let lease = try HomeLease.tryEnter(home: home, purpose: "Images") else { break }
            defer { lease.close() }
            guard let renewed = try url.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate, renewed < old else { continue }
            do {
                let name = url.lastPathComponent.lowercased()
                let used = try db.withBudget(seconds: min(0.075, 2 - elapsed), cancelled: cancelled) {
                    try db.scalar("SELECT EXISTS(SELECT 1 FROM cards WHERE instr(lower(ifnull(images,'') || body),?)>0 UNION ALL SELECT 1 FROM ops WHERE instr(lower(ifnull(before,'')),?)>0)", [.text(name), .text(name)]).int64 != 0
                }
                if cancelled() { throw CancellationError() }
                if !used { try? FileManager.default.removeItem(at: url) }
            } catch DatabaseBudgetError.expired { break }
        }
    }

    /// VACUUM INTO includes committed WAL content in a consistent standalone
    /// database. Atomic promotion preserves yesterday's copy on failure.
    public func backup(cancelled: @escaping @Sendable () -> Bool = { false }) throws {
        if readOnly { return }
        if cancelled() { throw CancellationError() }
        guard let lease = try HomeLease.tryEnter(home: home, purpose: "Backup") else { return }
        defer { lease.close() }
        let formatter = DateFormatter()
        formatter.calendar = Calendar(identifier: .gregorian)
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyyMMdd"
        let today = formatter.string(from: Date())
        if try setting("backup.last") == today { return }
        let directory = URL(fileURLWithPath: home, isDirectory: true).appendingPathComponent("backup", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        // A process killed during VACUUM may leave its unique staging file.
        // Limit cleanup and match only this backup writer's own file names.
        let stagingFiles = try FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil)
            .filter { $0.lastPathComponent.range(of: #"^pincc-\d{8}\.db\.[0-9a-f]{32}\.tmp$"#, options: .regularExpression) != nil }
        for stale in stagingFiles.prefix(20) {
            if cancelled() { throw CancellationError() }
            try? FileManager.default.removeItem(at: stale)
        }
        let target = directory.appendingPathComponent("pincc-\(today).db")
        let temporary = directory.appendingPathComponent("pincc-\(today).db.\(UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()).tmp")
        defer { try? FileManager.default.removeItem(at: temporary) }
        let maintenance = try Db(path: db.path, busyMs: 250)
        defer { maintenance.close() }
        try maintenance.withBudget(seconds: 30, cancelled: cancelled) {
            try maintenance.script("VACUUM INTO '\(temporary.path.replacingOccurrences(of: "'", with: "''"))'")
        }
        if cancelled() { throw CancellationError() }
        try LocalFiles.promote(temporary, to: target)
        let backups = try FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil)
            .filter { $0.lastPathComponent.range(of: #"^pincc-\d{8}\.db$"#, options: .regularExpression) != nil }
            .sorted { $0.lastPathComponent > $1.lastPathComponent }
        for old in backups.dropFirst(7) { try? FileManager.default.removeItem(at: old) }
        try setSetting("backup.last", today)
    }

    private func insertRow(_ group: Group, _ original: Card) throws -> Card {
        var card = original
        let now = Self.nowMs
        if card.createdAt == 0 { card.createdAt = now }
        card.updatedAt = now
        card.rev = 1
        card.id = try db.insert("INSERT INTO cards(rev,updated_at,\(Card.columns.joined(separator: ","))) VALUES(1,?,\(marks(Card.columns.count)))",
                               [.integer(now)] + card.values())
        try journal(group, cardId: card.id, rev: 0, newRev: 1, op: "create", before: nil, columns: [])
        return card
    }

    private func write(_ group: Group, current: Card, next original: Card, op: String, record: Bool) throws -> Card {
        let before = current.values()
        let after = original.values()
        let changed = before.indices.filter { before[$0] != after[$0] }
        if changed.isEmpty { return current }
        let set = changed.map { Card.columns[$0] + "=?" }.joined(separator: ",")
        let now = max(Self.nowMs, current.updatedAt + 1)
        let count = try db.exec("UPDATE cards SET \(set),rev=rev+1,updated_at=? WHERE id=? AND rev=?",
                               changed.map { after[$0] } + [.integer(now), .integer(current.id), .integer(current.rev)])
        guard count == 1 else { throw RevConflictError(current: try get(current.id)?.rev ?? current.rev) }
        var next = original
        // Identity and revision are owned exclusively by Store.
        next.id = current.id
        next.rev = current.rev + 1
        next.updatedAt = now
        if record {
            try journal(group, cardId: current.id, rev: current.rev, newRev: next.rev, op: op,
                        before: try current.toJSON(), columns: changed.map { Card.columns[$0] })
        } else if var entry = group.cards[current.id], entry.rev == current.rev {
            entry.rev = next.rev
            group.cards[current.id] = entry
        }
        return next
    }

    private func journal(_ group: Group, cardId: Int64, rev: Int64, newRev: Int64, op: String, before: String?, columns: [String]) throws {
        if var entry = group.cards[cardId], entry.rev == rev {
            var merged = entry.cols.split(separator: ",").map(String.init)
            for column in columns where !merged.contains(column) { merged.append(column) }
            let list = merged.joined(separator: ",")
            if list != entry.cols { try db.exec("UPDATE ops SET cols=? WHERE id=?", [.text(list), .integer(entry.op)]) }
            entry.rev = newRev
            entry.cols = list
            group.cards[cardId] = entry
            return
        }
        let list = columns.joined(separator: ",")
        let id = try db.insert("INSERT INTO ops(grp,at,card_id,op,before,cols) VALUES(?,?,?,?,?,?)",
                               [.integer(group.id), .integer(Self.nowMs), .integer(cardId), .text(op), before.map(SQLiteValue.text) ?? .null, .text(list)])
        if group.id == 0 {
            group.id = id
            try db.exec("UPDATE ops SET grp=? WHERE id=?", [.integer(id), .integer(id)])
        }
        group.cards[cardId] = Group.Entry(rev: newRev, op: id, cols: list)
    }

    private func inTransaction<T>(_ group: Group, _ operation: () throws -> T) throws -> T {
        group.gate.lock()
        defer { group.gate.unlock() }
        let lease = try HomeLease.enterImages(home: home)
        defer { lease.close() }
        let previousID = group.id
        let previousCards = group.cards
        do { return try db.transaction(operation) }
        catch { group.id = previousID; group.cards = previousCards; throw error }
    }

    private func userVersion() throws -> Int64 { try db.scalar("PRAGMA user_version").int64 ?? 0 }
    private func hasColumn(_ table: String, _ column: String) throws -> Bool {
        try db.scalar("SELECT count(*) FROM pragma_table_info(?) WHERE name=?", [.text(table), .text(column)]).int64 ?? 0 > 0
    }
    private func preRelease() throws -> Bool {
        // Accept unversioned local databases with the legacy journal shape.
        // An empty new database has no ops table yet.
        let exists = try db.scalar("SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='ops')").int64 ?? 0
        return try exists != 0 && !hasColumn("ops", "cols")
    }
    private func migrate() throws {
        try db.transaction {
            let pre = try preRelease()
            if try userVersion() >= Int64(Self.schemaVersion) && !pre { return }
            if pre { try db.script("DROP INDEX IF EXISTS ops_grp; DROP INDEX IF EXISTS ops_card; ALTER TABLE ops RENAME TO ops_old;") }
            try db.script(Self.schema)
            if pre { try db.script("INSERT INTO ops(id,grp,at,card_id,op,before) SELECT id,grp,at,card_id,op,before FROM ops_old; DROP TABLE ops_old;") }
            if try !hasColumn("cards", "ord") { try db.script("ALTER TABLE cards ADD COLUMN ord REAL") }
            try db.script("PRAGMA user_version = \(Self.schemaVersion)")
        }
    }
    private func writable() throws { if readOnly { throw StoreError.newerSchema } }
    private func marks(_ count: Int) -> String { Array(repeating: "?", count: count).joined(separator: ",") }
    private static var nowMs: Int64 { Int64(Date().timeIntervalSince1970 * 1_000) }
    private func locked<T>(_ operation: () throws -> T) rethrows -> T {
        gate.lock()
        defer { gate.unlock() }
        return try operation()
    }
}
