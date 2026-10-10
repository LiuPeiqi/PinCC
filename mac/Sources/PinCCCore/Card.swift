import CryptoKit
import Foundation

/// One persisted card. Presentation and source facts remain derived from its contents.
public struct Card: Codable, Equatable, Sendable {
    public var id: Int64 = 0
    public var rev: Int64 = 0
    public var body = ""
    public var images: [String] = []
    public var summary: String?
    public var meta: String?
    public var color: Int?
    public var topmost = false
    public var opacity: Double = 1
    public var collapsed = false
    public var free = false
    public var mon: String?
    public var x: Double = 0
    public var y: Double = 0
    public var w: Double = 260
    public var h: Double?
    public var remindAt: Int64?
    public var doneAt: Int64?
    public var deletedAt: Int64?
    public var createdAt: Int64 = 0
    public var updatedAt: Int64 = 0
    public var ord: Double?

    public init() {}

    /// Shared snapshots retain the C# property names, independently of SQLite columns.
    private enum CodingKeys: String, CodingKey {
        case id = "Id", rev = "Rev", body = "Body", images = "Images", summary = "Summary", meta = "Meta"
        case color = "Color", topmost = "Topmost", opacity = "Opacity", collapsed = "Collapsed", free = "Free"
        case mon = "Mon", x = "X", y = "Y", w = "W", h = "H", remindAt = "RemindAt", doneAt = "DoneAt"
        case deletedAt = "DeletedAt", createdAt = "CreatedAt", updatedAt = "UpdatedAt", ord = "Ord"
    }

    public init(from decoder: Decoder) throws {
        self.init()
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = try c.decodeIfPresent(Int64.self, forKey: .id) ?? 0
        rev = try c.decodeIfPresent(Int64.self, forKey: .rev) ?? 0
        body = try c.decodeIfPresent(String.self, forKey: .body) ?? ""
        images = try c.decodeIfPresent([String].self, forKey: .images) ?? []
        summary = try c.decodeIfPresent(String.self, forKey: .summary)
        meta = try c.decodeIfPresent(String.self, forKey: .meta)
        color = try c.decodeIfPresent(Int.self, forKey: .color)
        topmost = try c.decodeIfPresent(Bool.self, forKey: .topmost) ?? false
        opacity = try c.decodeIfPresent(Double.self, forKey: .opacity) ?? 1
        collapsed = try c.decodeIfPresent(Bool.self, forKey: .collapsed) ?? false
        free = try c.decodeIfPresent(Bool.self, forKey: .free) ?? false
        mon = try c.decodeIfPresent(String.self, forKey: .mon)
        x = try c.decodeIfPresent(Double.self, forKey: .x) ?? 0
        y = try c.decodeIfPresent(Double.self, forKey: .y) ?? 0
        w = try c.decodeIfPresent(Double.self, forKey: .w) ?? 260
        h = try c.decodeIfPresent(Double.self, forKey: .h)
        remindAt = try c.decodeIfPresent(Int64.self, forKey: .remindAt)
        doneAt = try c.decodeIfPresent(Int64.self, forKey: .doneAt)
        deletedAt = try c.decodeIfPresent(Int64.self, forKey: .deletedAt)
        createdAt = try c.decodeIfPresent(Int64.self, forKey: .createdAt) ?? 0
        updatedAt = try c.decodeIfPresent(Int64.self, forKey: .updatedAt) ?? 0
        ord = try c.decodeIfPresent(Double.self, forKey: .ord)
    }

    public func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encode(id, forKey: .id)
        try c.encode(rev, forKey: .rev)
        try c.encode(body, forKey: .body)
        try c.encode(images, forKey: .images)
        try c.encode(summary, forKey: .summary)
        try c.encode(meta, forKey: .meta)
        try c.encode(color, forKey: .color)
        try c.encode(topmost, forKey: .topmost)
        try c.encode(opacity, forKey: .opacity)
        try c.encode(collapsed, forKey: .collapsed)
        try c.encode(free, forKey: .free)
        try c.encode(mon, forKey: .mon)
        try c.encode(x, forKey: .x)
        try c.encode(y, forKey: .y)
        try c.encode(w, forKey: .w)
        try c.encode(h, forKey: .h)
        try c.encode(remindAt, forKey: .remindAt)
        try c.encode(doneAt, forKey: .doneAt)
        try c.encode(deletedAt, forKey: .deletedAt)
        try c.encode(createdAt, forKey: .createdAt)
        try c.encode(updatedAt, forKey: .updatedAt)
        try c.encode(ord, forKey: .ord)
    }

    public static let columns = [
        "body", "images", "summary", "meta", "color", "topmost", "opacity", "collapsed", "free", "mon", "x", "y", "w", "h",
        "remind_at", "done_at", "deleted_at", "created_at", "ord"
    ]

    public func values() -> [SQLiteValue] {
        // JSONEncoder cannot fail for an array containing only String values.
        let imageJSON = images.isEmpty ? nil : String(decoding: try! JSONEncoder().encode(images), as: UTF8.self)
        return [
            .text(body), imageJSON.map(SQLiteValue.text) ?? .null,
            summary.map(SQLiteValue.text) ?? .null, meta.map(SQLiteValue.text) ?? .null,
            color.map { .integer(Int64($0)) } ?? .null, .integer(topmost ? 1 : 0), .real(opacity),
            .integer(collapsed ? 1 : 0), .integer(free ? 1 : 0), mon.map(SQLiteValue.text) ?? .null,
            .real(x), .real(y), .real(w), h.map(SQLiteValue.real) ?? .null,
            remindAt.map(SQLiteValue.integer) ?? .null, doneAt.map(SQLiteValue.integer) ?? .null,
            deletedAt.map(SQLiteValue.integer) ?? .null, .integer(createdAt), ord.map(SQLiteValue.real) ?? .null
        ]
    }

    public init(row: [String: SQLiteValue]) throws {
        self.init()
        id = row["id"]?.int64 ?? 0
        rev = row["rev"]?.int64 ?? 0
        body = row["body"]?.string ?? ""
        if let json = row["images"]?.string, json.utf8.count > 2 {
            images = try JSONDecoder().decode([String].self, from: Data(json.utf8))
        }
        summary = row["summary"]?.string
        meta = row["meta"]?.string
        color = row["color"]?.int64.flatMap { Int(exactly: $0) }
        topmost = (row["topmost"]?.int64 ?? 0) != 0
        opacity = row["opacity"]?.double ?? 1
        collapsed = (row["collapsed"]?.int64 ?? 0) != 0
        free = (row["free"]?.int64 ?? 0) != 0
        mon = row["mon"]?.string
        x = row["x"]?.double ?? 0
        y = row["y"]?.double ?? 0
        w = row["w"]?.double ?? 260
        h = row["h"]?.double
        remindAt = row["remind_at"]?.int64
        doneAt = row["done_at"]?.int64
        deletedAt = row["deleted_at"]?.int64
        createdAt = row["created_at"]?.int64 ?? 0
        updatedAt = row["updated_at"]?.int64 ?? 0
        ord = row["ord"]?.double
    }

    /// Restores only fields owned by the operation; identity and revision stay with the receiver.
    public func with(from source: Card, columns: [String]) -> Card {
        var result = self
        for column in Set(columns.map { $0.lowercased() }) {
            switch column {
            case "body": result.body = source.body
            case "images": result.images = source.images
            case "summary": result.summary = source.summary
            case "meta": result.meta = source.meta
            case "color": result.color = source.color
            case "topmost": result.topmost = source.topmost
            case "opacity": result.opacity = source.opacity
            case "collapsed": result.collapsed = source.collapsed
            case "free": result.free = source.free
            case "mon": result.mon = source.mon
            case "x": result.x = source.x
            case "y": result.y = source.y
            case "w": result.w = source.w
            case "h": result.h = source.h
            case "remind_at": result.remindAt = source.remindAt
            case "done_at": result.doneAt = source.doneAt
            case "deleted_at": result.deletedAt = source.deletedAt
            case "created_at": result.createdAt = source.createdAt
            case "ord": result.ord = source.ord
            default: break
            }
        }
        return result
    }

    public func toJSON() throws -> String { String(decoding: try JSONEncoder().encode(self), as: UTF8.self) }
    public static func fromJSON(_ json: String) throws -> Card { try JSONDecoder().decode(Card.self, from: Data(json.utf8)) }

    public var active: Bool { doneAt == nil && deletedAt == nil }
    public var overdue: Bool { remindAt.map { $0 <= When.nowMs && doneAt == nil } ?? false }
    public var firstLine: String { body.components(separatedBy: "\n").map(Self.trim).first { !$0.isEmpty } ?? "" }

    public var src: String? {
        let line = firstLine.trimmingCharacters(in: CharacterSet(charactersIn: "\"<>"))
        if Self.isURL(line) { return line }
        if line.lowercased().hasPrefix("file://"), let url = URL(string: line), url.isFileURL {
            let path = url.path
            if let host = url.host, !host.isEmpty, host.lowercased() != "localhost" {
                return "\\\\" + host + path.replacingOccurrences(of: "/", with: "\\")
            }
            if path.hasPrefix("//") { return path.replacingOccurrences(of: "/", with: "\\") }
            if path.utf16.count >= 3, path.first == "/", let drive = path.dropFirst().first?.asciiValue,
               (65...90).contains(drive) || (97...122).contains(drive),
               path[path.index(path.startIndex, offsetBy: 2)] == ":" {
                return String(path.dropFirst()).replacingOccurrences(of: "/", with: "\\")
            }
            return path
        }
        if Self.windowsPath.firstMatch(in: line, range: NSRange(line.startIndex..., in: line)) != nil { return line }
        let roots = ["/Users", "/Volumes", "/Applications", "/Library", "/System", "/private", "/tmp", "/var", "/opt", "/usr"]
        return !line.contains("\n") && !line.contains("\r") && roots.contains(where: { line == $0 || line.hasPrefix($0 + "/") }) ? line : nil
    }

    public var isLink: Bool { src.map(Self.isURL) ?? false }
    public var isFile: Bool { src != nil && !isLink }
    public var imageOnly: Bool { images.count == 1 && Self.trim(body).isEmpty }
    public var noteLine: Int {
        src == nil ? 0 : (body.components(separatedBy: "\n").firstIndex { !Self.trim($0).isEmpty } ?? -1) + 1
    }
    public var note: String { src == nil ? body : body.components(separatedBy: "\n").dropFirst(noteLine).joined(separator: "\n") }
    public var kind: String {
        if !images.isEmpty && Self.trim(body).isEmpty { return "image" }
        return isLink ? "link" : isFile ? "file" : progress.total > 0 ? "task" : "note"
    }
    public var info: CardMeta? { meta.flatMap { try? JSONDecoder().decode(CardMeta.self, from: Data($0.utf8)) } }
    public var srcInfo: CardMeta? { guard let facts = info, Self.same(facts.src, src) else { return nil }; return facts }
    public var progress: (done: Int, total: Int) {
        let tasks = Markdown.blocks(body).filter { $0.kind == .task }
        return (tasks.filter(\.checked).count, tasks.count)
    }
    public var inputHash: String? { src == nil ? Self.hash(body) : srcInfo?.textHash }
    public var summaryStale: Bool {
        guard let summary, !Self.trim(summary).isEmpty, let hash = info?.sumHash else { return false }
        return !Self.same(hash, inputHash)
    }
    public var title: String {
        if let title = srcInfo?.title, !title.isEmpty { return title }
        for raw in note.components(separatedBy: "\n") {
            let title = Markdown.plain(raw)
            if !title.isEmpty { return title }
        }
        if let src {
            if isLink { return src }
            return src.trimmingCharacters(in: CharacterSet(charactersIn: "\\/")).components(separatedBy: CharacterSet(charactersIn: "\\/")).last ?? src
        }
        return images.isEmpty ? "" : "图片"
    }
    public var nextTask: String? {
        guard progress.total != progress.done else { return nil }
        return Markdown.blocks(note).filter { $0.kind == .task && !$0.checked }.map { Markdown.plain($0.text) }.first { !$0.isEmpty }
    }
    public var titleIsTask: Bool { progress.total > 0 && Markdown.blocks(note).contains { $0.kind == .task && Self.same(Markdown.plain($0.text), title) } }
    public var tag: String? {
        guard let match = Self.tagPattern.firstMatch(in: body, range: NSRange(body.startIndex..., in: body)),
              let range = Range(match.range(at: 1), in: body) else { return nil }
        return String(body[range]).lowercased()
    }

    public static let rotation = [0, 4, 2, 6, 3, 1, 5]
    public func shownColor(mode: String) -> Int { color ?? autoColor(mode: mode) }
    public func autoColor(mode: String) -> Int {
        switch mode {
        case "off": return 0
        case "kind": return ["link": 4, "file": 6, "image": 3, "task": 1][kind] ?? 0
        case "tag": return tag.map { Self.rotation[Int(Self.fnv($0) % 7)] } ?? 0
        default: return Self.rotation[Int(max(id, 0) % 7)]
        }
    }
    public var rank: Double { ord ?? Double(createdAt) }
    public static func compare(_ a: Card, _ b: Card) -> Bool {
        if let ra = a.remindAt, let rb = b.remindAt { return ra == rb ? a.rank > b.rank : ra < rb }
        if a.remindAt != nil { return true }
        if b.remindAt != nil { return false }
        return a.rank == b.rank ? a.id > b.id : a.rank > b.rank
    }
    public static func ranks(_ cards: [Card]) -> [Double] {
        var ranks = cards.map(\.rank).sorted(by: >)
        for i in ranks.indices.dropFirst() where ranks[i] >= ranks[i - 1] { ranks[i] = ranks[i - 1] - 1 }
        return ranks
    }
    public static func isURL(_ text: String) -> Bool {
        guard text.utf16.count > 8, !text.contains("\n"), !text.contains(" "), let url = URL(string: text),
              let scheme = url.scheme?.lowercased(), let host = url.host, !host.isEmpty else { return false }
        return scheme == "http" || scheme == "https"
    }
    /// Shared input fingerprint, not a security primitive. Keep the Windows SHA-1 prefix exactly.
    public static func hash(_ text: String) -> String {
        Insecure.SHA1.hash(data: Data(text.utf8)).prefix(8).map { String(format: "%02X", $0) }.joined()
    }
    private static func fnv(_ text: String) -> UInt32 {
        text.utf16.reduce(UInt32(2_166_136_261)) { ($0 ^ UInt32($1)) &* 16_777_619 }
    }
    private static func same(_ left: String?, _ right: String?) -> Bool {
        switch (left, right) {
        case (nil, nil): return true
        case let (left?, right?): return left.utf16.elementsEqual(right.utf16)
        default: return false
        }
    }
    private static func trim(_ text: String) -> String { text.trimmingCharacters(in: .whitespacesAndNewlines) }
    private static let windowsPath = try! NSRegularExpression(pattern: #"^(?:[A-Za-z]:\\|\\\\[^\\\s]+\\)[^<>|*?"\r\n]*$"#)
    private static let tagPattern = try! NSRegularExpression(pattern: #"(?:^|\s)#([[\p{L}\p{N}_\-]&&[\u0000-\uFFFF]]{1,32})"#)
}

/// Source metadata uses the same sparse JSON keys as the Windows implementation.
public struct CardMeta: Codable, Equatable, Sendable {
    public var src: String?
    public var title: String?
    public var site: String?
    public var desc: String?
    public var date: String?
    public var fType: String?
    public var size: Int64?
    public var mtime: Int64?
    public var textHash: String?
    public var sumHash: String?
    public var state: String?
    public var error: String?
    public init() {}
    private enum CodingKeys: String, CodingKey {
        case src = "Src", title = "Title", site = "Site", desc = "Desc", date = "Date", fType = "FType"
        case size = "Size", mtime = "Mtime", textHash = "TextHash", sumHash = "SumHash", state = "State", error = "Error"
    }
    public func toJSON() throws -> String { String(decoding: try JSONEncoder().encode(self), as: UTF8.self) }
}
