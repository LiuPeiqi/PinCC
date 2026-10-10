import Foundation
import CSQLite

public enum SQLiteValue: Equatable, Sendable {
    case null, integer(Int64), real(Double), text(String), blob(Data)

    /// Swift String equality normalizes canonical Unicode equivalents, while
    /// the shared C# store compares UTF-16 units. Preserve that write boundary.
    public static func == (lhs: SQLiteValue, rhs: SQLiteValue) -> Bool {
        switch (lhs, rhs) {
        case (.null, .null): return true
        case (.integer(let a), .integer(let b)): return a == b
        case (.real(let a), .real(let b)): return a == b || (a.isNaN && b.isNaN)
        case (.text(let a), .text(let b)): return a.utf16.elementsEqual(b.utf16)
        case (.blob(let a), .blob(let b)): return a == b
        default: return false
        }
    }

    public var int64: Int64? {
        switch self {
        case .integer(let value): return value
        case .real(let value) where value.isFinite && value >= Double(Int64.min) && value < Double(Int64.max): return Int64(value)
        default: return nil
        }
    }
    public var double: Double? {
        switch self {
        case .integer(let value): return Double(value)
        case .real(let value): return value
        default: return nil
        }
    }
    public var string: String? { if case .text(let value) = self { return value }; return nil }
}

public struct SQLiteError: Error, LocalizedError, Sendable {
    public let code: Int32
    public let message: String
    public var errorDescription: String? { "SQLite: \(message) (\(code))" }
}

public enum DatabaseBudgetError: Error, LocalizedError {
    case expired
    public var errorDescription: String? { "数据库维护超时" }
}

/// A small SQLite boundary: UTF-8 paths, UTF-16 text like the C# baseline. The recursive lock keeps a full transaction
/// together; FULLMUTEX also protects SQLite's connection internals.
public final class Db: @unchecked Sendable {
    public typealias Row = [String: SQLiteValue]
    public let path: String
    private var handle: OpaquePointer?
    private let gate = NSRecursiveLock()
    private let busyMs: Int32

    public init(path: String, busyMs: Int = 5_000, configureWAL: Bool = true) throws {
        self.path = path
        self.busyMs = Int32(clamping: max(0, busyMs))
        let rc = sqlite3_open_v2(path, &handle, SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE | SQLITE_OPEN_FULLMUTEX, nil)
        guard rc == SQLITE_OK else {
            let error = makeError(rc)
            if let handle { sqlite3_close_v2(handle) }
            handle = nil
            throw error
        }
        do {
            try check(sqlite3_busy_timeout(handle, self.busyMs))
            if configureWAL { try enableWAL() }
        } catch {
            close()
            throw error
        }
    }

    deinit { close() }

    /// Store checks user_version first, so a future schema remains untouched.
    public func enableWAL() throws {
        try locked {
            if try scalar("PRAGMA journal_mode").string?.lowercased() != "wal" {
                try script("PRAGMA journal_mode=WAL")
            }
            try script("PRAGMA synchronous=NORMAL")
        }
    }

    public func close() {
        locked {
            if let handle { sqlite3_close_v2(handle) }
            handle = nil
        }
    }

    @discardableResult
    public func exec(_ sql: String, _ args: [SQLiteValue] = []) throws -> Int {
        try locked {
            try run(sql, args) { _ in }
            return Int(sqlite3_changes(handle))
        }
    }

    public func insert(_ sql: String, _ args: [SQLiteValue] = []) throws -> Int64 {
        try locked {
            try run(sql, args) { _ in }
            return sqlite3_last_insert_rowid(handle)
        }
    }

    public func query(_ sql: String, _ args: [SQLiteValue] = []) throws -> [Row] {
        try locked {
            var rows: [Row] = []
            try run(sql, args) { statement in
                var row: Row = [:]
                for index in 0..<sqlite3_column_count(statement) {
                    guard let name = sqlite3_column_name(statement, index) else { continue }
                    row[String(cString: name)] = readColumn(statement, index)
                }
                rows.append(row)
            }
            return rows
        }
    }

    public func scalar(_ sql: String, _ args: [SQLiteValue] = []) throws -> SQLiteValue {
        try locked {
            var value: SQLiteValue = .null
            try run(sql, args) { statement in
                if case .null = value { value = readColumn(statement, 0) }
            }
            return value
        }
    }

    public func script(_ sql: String) throws {
        try locked {
            guard handle != nil else { throw SQLiteError(code: SQLITE_MISUSE, message: "连接已关闭") }
            var message: UnsafeMutablePointer<CChar>?
            let rc = sqlite3_exec(handle, sql, nil, nil, &message)
            let detail = message.map { String(cString: $0) }
            if let message { sqlite3_free(message) }
            if rc != SQLITE_OK { throw SQLiteError(code: rc, message: detail ?? makeError(rc).message) }
        }
    }

    /// BEGIN IMMEDIATE excludes a writer between read and revision check.
    public func transaction<T>(_ operation: () throws -> T) throws -> T {
        try locked {
            guard handle != nil else { throw SQLiteError(code: SQLITE_MISUSE, message: "连接已关闭") }
            guard sqlite3_get_autocommit(handle) != 0 else {
                throw SQLiteError(code: SQLITE_MISUSE, message: "不允许嵌套数据库事务")
            }
            try script("BEGIN IMMEDIATE")
            do {
                let result = try operation()
                try script("COMMIT")
                return result
            } catch {
                if let handle, sqlite3_get_autocommit(handle) == 0 { try? script("ROLLBACK") }
                throw error
            }
        }
    }

    public var dataVersion: Int64 { get throws { try scalar("PRAGMA data_version").int64 ?? 0 } }

    /// Use a dedicated maintenance connection. Progress bounds VM work, while a
    /// shortened busy timeout bounds waits for concurrent writers.
    public func withBudget<T>(seconds: TimeInterval, cancelled: @escaping @Sendable () -> Bool = { false }, operation: () throws -> T) throws -> T {
        try locked {
            guard handle != nil else { throw SQLiteError(code: SQLITE_MISUSE, message: "连接已关闭") }
            guard seconds > 0 else { throw DatabaseBudgetError.expired }
            let budget = Budget(seconds: seconds, cancelled: cancelled)
            if cancelled() { throw CancellationError() }
            guard handle != nil else { throw SQLiteError(code: SQLITE_MISUSE, message: "连接已关闭") }
            let context = Unmanaged.passUnretained(budget).toOpaque()
            sqlite3_progress_handler(handle, 1_000, { context in
                guard let context else { return 0 }
                return Unmanaged<Budget>.fromOpaque(context).takeUnretainedValue().stopped ? 1 : 0
            }, context)
            sqlite3_busy_timeout(handle, min(busyMs, Int32(clamping: max(1, Int(seconds * 1_000)))))
            defer {
                if let handle {
                    sqlite3_progress_handler(handle, 0, nil, nil)
                    sqlite3_busy_timeout(handle, busyMs)
                }
            }
            do {
                let result = try operation()
                if cancelled() { throw CancellationError() }
                if budget.stopped { throw DatabaseBudgetError.expired }
                return result
            } catch {
                if cancelled() { throw CancellationError() }
                if budget.stopped { throw DatabaseBudgetError.expired }
                throw error
            }
        }
    }

    private final class Budget {
        let deadline: UInt64
        let cancelled: @Sendable () -> Bool
        init(seconds: TimeInterval, cancelled: @escaping @Sendable () -> Bool) {
            deadline = DispatchTime.now().uptimeNanoseconds + UInt64(min(seconds, 86_400) * 1_000_000_000)
            self.cancelled = cancelled
        }
        var stopped: Bool { cancelled() || DispatchTime.now().uptimeNanoseconds >= deadline }
    }

    private func locked<T>(_ operation: () throws -> T) rethrows -> T {
        gate.lock()
        defer { gate.unlock() }
        return try operation()
    }

    private func run(_ sql: String, _ args: [SQLiteValue], row: (OpaquePointer) throws -> Void) throws {
        guard let handle else { throw SQLiteError(code: SQLITE_MISUSE, message: "连接已关闭") }
        var statement: OpaquePointer?
        try check(sqlite3_prepare_v2(handle, sql, -1, &statement, nil))
        guard let statement else { throw SQLiteError(code: SQLITE_MISUSE, message: "SQL 语句为空") }
        defer { sqlite3_finalize(statement) }
        guard sqlite3_bind_parameter_count(statement) == Int32(args.count) else {
            throw SQLiteError(code: SQLITE_MISUSE, message: "SQL 参数数量不匹配")
        }
        for (offset, value) in args.enumerated() { try bind(statement, Int32(offset + 1), value) }
        while true {
            let rc = sqlite3_step(statement)
            if rc == SQLITE_ROW { try row(statement) }
            else if rc == SQLITE_DONE { return }
            else { throw makeError(rc) }
        }
    }

    private func bind(_ statement: OpaquePointer, _ index: Int32, _ value: SQLiteValue) throws {
        let rc: Int32
        switch value {
        case .null: rc = sqlite3_bind_null(statement, index)
        case .integer(let number): rc = sqlite3_bind_int64(statement, index, number)
        case .real(let number): rc = sqlite3_bind_double(statement, index, number)
        case .text(let text):
            let count = text.utf16.count
            guard count <= Int(Int32.max) / 2 else { throw SQLiteError(code: SQLITE_TOOBIG, message: "文本过大") }
            // Count is explicit, so embedded NULs remain data. A trailing zero
            // keeps the pointer non-null for the empty string.
            let units = Array(text.utf16) + [UInt16(0)]
            rc = units.withUnsafeBytes { pincc_bind_text16(statement, index, $0.baseAddress, Int32(count * 2)) }
        case .blob(let bytes):
            guard bytes.count <= Int(Int32.max) else { throw SQLiteError(code: SQLITE_TOOBIG, message: "数据过大") }
            if bytes.isEmpty { rc = sqlite3_bind_zeroblob(statement, index, 0) }
            else { rc = bytes.withUnsafeBytes { pincc_bind_blob(statement, index, $0.baseAddress, Int32(bytes.count)) } }
        }
        try check(rc)
    }

    private func readColumn(_ statement: OpaquePointer, _ index: Int32) -> SQLiteValue {
        switch sqlite3_column_type(statement, index) {
        case SQLITE_INTEGER: return .integer(sqlite3_column_int64(statement, index))
        case SQLITE_FLOAT: return .real(sqlite3_column_double(statement, index))
        case SQLITE_TEXT:
            guard let bytes = sqlite3_column_text16(statement, index) else { return .text("") }
            let units = bytes.assumingMemoryBound(to: UInt16.self)
            return .text(String(decoding: UnsafeBufferPointer(start: units, count: Int(sqlite3_column_bytes16(statement, index)) / 2), as: UTF16.self))
        case SQLITE_BLOB:
            let count = Int(sqlite3_column_bytes(statement, index))
            guard count > 0, let bytes = sqlite3_column_blob(statement, index) else { return .blob(Data()) }
            return .blob(Data(bytes: bytes, count: count))
        default: return .null
        }
    }

    private func check(_ code: Int32) throws { if code != SQLITE_OK { throw makeError(code) } }
    private func makeError(_ code: Int32) -> SQLiteError {
        SQLiteError(code: code, message: handle.flatMap { sqlite3_errmsg($0) }.map { String(cString: $0) } ?? "无法打开数据库")
    }
}
