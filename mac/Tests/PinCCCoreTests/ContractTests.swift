import Foundation
import CryptoKit
import XCTest
@testable import PinCCCore

/// Fixtures are produced by compiled production C#, not a second implementation.
/// #filePath works both in SwiftPM and in the checked-in Xcode project.
enum ContractFixture {
    static var directory: URL {
        URL(fileURLWithPath: #filePath).deletingLastPathComponent()
            .deletingLastPathComponent().deletingLastPathComponent()
            .appendingPathComponent("contracts", isDirectory: true)
    }
    static func records(_ family: String) throws -> [Data] {
        let text = try String(contentsOf: directory.appendingPathComponent("vectors.jsonl"), encoding: .utf8)
        return try text.split(separator: "\n").compactMap { line in
            let data = Data(line.utf8)
            let object = try JSONSerialization.jsonObject(with: data) as? [String: Any]
            return object?["type"] as? String == family ? data : nil
        }
    }
    static func decode<T: Decodable>(_ type: T.Type, family: String) throws -> [T] {
        try records(family).map { try JSONDecoder().decode(type, from: $0) }
    }
}

final class ContractTests: XCTestCase {
    private struct Fingerprint: Decodable {
        let schema: Int
        let generatedBy: String
        let sources: [String: String]
        let vectors: String
        let vectorsSha256: String
        let fixtureFamilies: [String: Int]
    }
    func testFixtureMatchesCurrentBaseline() throws {
        let fingerprint = try JSONDecoder().decode(Fingerprint.self, from: Data(contentsOf: ContractFixture.directory.appendingPathComponent("source-fingerprint.json")))
        XCTAssertEqual(fingerprint.schema, 1)
        XCTAssertEqual(fingerprint.generatedBy, "oracle/mac-contracts")
        XCTAssertGreaterThanOrEqual(fingerprint.sources.count, 20)
        let repository = ContractFixture.directory.deletingLastPathComponent().deletingLastPathComponent()
        for (path, expected) in fingerprint.sources {
            let original = [UInt8](try Data(contentsOf: repository.appendingPathComponent(path)))
            var canonical: [UInt8] = []
            var index = 0
            while index < original.count {
                if original[index] == 13 && index + 1 < original.count && original[index + 1] == 10 {
                    canonical.append(10); index += 2
                } else { canonical.append(original[index]); index += 1 }
            }
            let actual = SHA256.hash(data: Data(canonical)).map { String(format: "%02x", $0) }.joined()
            XCTAssertEqual(actual, expected, "Stale C# oracle: \(path); run mac/scripts/update-contracts.ps1 on Windows")
        }
        let data = try Data(contentsOf: ContractFixture.directory.appendingPathComponent(fingerprint.vectors))
        let actual = SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
        XCTAssertEqual(actual, fingerprint.vectorsSha256, "Contract vectors were changed without regenerating the C# oracle")
        for (family, count) in fingerprint.fixtureFamilies {
            XCTAssertEqual(try ContractFixture.records(family).count, count, family)
        }
    }
    private struct StorageEqualityVector: Decodable {
        let left: String, right: String
        let equal: Bool
        let leftHash: String, rightHash: String
    }
    func testStorageTextEqualityUsesCSharpOrdinalUTF16Rules() throws {
        let vectors = try ContractFixture.decode(StorageEqualityVector.self, family: "storageEquality")
        XCTAssertEqual(vectors.count, 6)
        for vector in vectors {
            XCTAssertEqual(SQLiteValue.text(vector.left) == SQLiteValue.text(vector.right), vector.equal)
            XCTAssertEqual(Card.hash(vector.left), vector.leftHash)
            XCTAssertEqual(Card.hash(vector.right), vector.rightHash)
        }
    }

    private struct CardVector: Decodable {
        var name: String
        var input: Card
        var expected: Derived
        struct Derived: Decodable {
            var active: Bool
            var firstLine: String
            var src: String?
            var isLink: Bool
            var isFile: Bool
            var imageOnly: Bool
            var note: String
            var noteLine: Int
            var kind: String
            var title: String
            var nextTask: String?
            var titleIsTask: Bool
            var tag: String?
            var done: Int
            var total: Int
            var inputHash: String?
            var summaryStale: Bool
            var hash: String
            var rank: Double
            var colors: [Int]
        }
    }

    func testCardDerivationsMatchProductionCSharp() throws {
        let vectors = try ContractFixture.decode(CardVector.self, family: "card")
        XCTAssertEqual(vectors.count, 360)
        for vector in vectors {
            let card = vector.input
            let expected = vector.expected
            let label = vector.name
            XCTAssertEqual(card.active, expected.active, label)
            XCTAssertEqual(card.firstLine, expected.firstLine, label)
            XCTAssertEqual(card.src, expected.src, label)
            XCTAssertEqual(card.isLink, expected.isLink, label)
            XCTAssertEqual(card.isFile, expected.isFile, label)
            XCTAssertEqual(card.imageOnly, expected.imageOnly, label)
            XCTAssertEqual(card.note, expected.note, label)
            XCTAssertEqual(card.noteLine, expected.noteLine, label)
            XCTAssertEqual(card.kind, expected.kind, label)
            XCTAssertEqual(card.title, expected.title, label)
            XCTAssertEqual(card.nextTask, expected.nextTask, label)
            XCTAssertEqual(card.titleIsTask, expected.titleIsTask, label)
            XCTAssertEqual(card.tag, expected.tag, label)
            XCTAssertEqual(card.progress.done, expected.done, label)
            XCTAssertEqual(card.progress.total, expected.total, label)
            XCTAssertEqual(card.inputHash, expected.inputHash, label)
            XCTAssertEqual(card.summaryStale, expected.summaryStale, label)
            XCTAssertEqual(Card.hash(card.body), expected.hash, label)
            XCTAssertEqual(card.rank, expected.rank, label)
            XCTAssertEqual(["off", "kind", "tag", "cycle"].map { card.shownColor(mode: $0) }, expected.colors, label)
            XCTAssertEqual(try Card.fromJSON(card.toJSON()), card, label)
        }
    }

    private struct MarkdownVector: Decodable {
        let name: String
        let input: String
        let expected: Result
        struct Result: Decodable {
            let blocks: [Block]
            let inlines: [Inline]
            let plain: String
            let taskLines: [Int]
            let foldDone: [Int]?
            let toggles: [Toggle]
            let added: String
            let continued: [String?]
        }
        struct Block: Decodable {
            let kind: String, text: String, marker: String
            let line: Int, level: Int, indent: Int
            let checked: Bool
        }
        struct Inline: Decodable { let text: String; let fmt: Int; let url: String? }
        struct Toggle: Decodable { let line: Int; let toggle: String, done: String, open: String }
    }

    func testMarkdownRenderingAndSourceEditsMatchProductionCSharp() throws {
        let vectors = try ContractFixture.decode(MarkdownVector.self, family: "markdown")
        XCTAssertGreaterThanOrEqual(vectors.count, 40)
        for vector in vectors {
            let expected = vector.expected
            let blocks = Markdown.blocks(vector.input)
            XCTAssertEqual(blocks.count, expected.blocks.count, vector.name)
            for (actual, wanted) in zip(blocks, expected.blocks) {
                XCTAssertEqual(actual.kind.rawValue, wanted.kind, vector.name)
                XCTAssertEqual(actual.text, wanted.text, vector.name)
                XCTAssertEqual(actual.line, wanted.line, vector.name)
                XCTAssertEqual(actual.level, wanted.level, vector.name)
                XCTAssertEqual(actual.indent, wanted.indent, vector.name)
                XCTAssertEqual(actual.checked, wanted.checked, vector.name)
                XCTAssertEqual(actual.marker, wanted.marker, vector.name)
            }
            let inlines = Markdown.inlines(vector.input)
            XCTAssertEqual(inlines.count, expected.inlines.count, vector.name)
            for (actual, wanted) in zip(inlines, expected.inlines) {
                XCTAssertEqual(actual.text, wanted.text, vector.name)
                XCTAssertEqual(actual.fmt.rawValue, wanted.fmt, vector.name)
                XCTAssertEqual(actual.url, wanted.url, vector.name)
            }
            XCTAssertEqual(Markdown.plain(vector.input), expected.plain, vector.name)
            XCTAssertEqual(Markdown.taskLines(vector.input), expected.taskLines, vector.name)
            XCTAssertEqual(Markdown.foldDone(blocks)?.sorted(), expected.foldDone, vector.name)
            for wanted in expected.toggles {
                XCTAssertEqual(Markdown.toggleTask(vector.input, line: wanted.line), wanted.toggle, vector.name)
                XCTAssertEqual(Markdown.setTask(vector.input, line: wanted.line, done: true), wanted.done, vector.name)
                XCTAssertEqual(Markdown.setTask(vector.input, line: wanted.line, done: false), wanted.open, vector.name)
            }
            XCTAssertEqual(Markdown.addTasks(vector.input, text: "- typed\r\n- [x] already\r\n中文😀\n\n[]"), expected.added, vector.name)
            XCTAssertEqual(vector.input.components(separatedBy: "\n").map(Markdown.continueList), expected.continued, vector.name)
        }
        XCTAssertTrue(Markdown.isTaskShortcut("[]"))
        XCTAssertTrue(Markdown.isTaskShortcut("[ ]"))
        XCTAssertFalse(Markdown.isTaskShortcut(" []"))
        XCTAssertFalse(Markdown.isTaskShortcut("[x]"))
    }

    private struct WhenVector: Decodable {
        let input: String
        let now: Int64
        let expected: Result?
        struct Result: Decodable { let at: Int64; let formatted: String; let past: Bool }
    }
    private struct PresetVector: Decodable {
        let now: Int64
        let expected: [Preset]
        struct Preset: Decodable { let label: String; let at: Int64 }
    }
    private var utc: Calendar {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(secondsFromGMT: 0)!
        return calendar
    }

    func testFixedCivilTimeGrammarAndPresetsMatchProductionCSharp() throws {
        let vectors = try ContractFixture.decode(WhenVector.self, family: "when")
        XCTAssertGreaterThanOrEqual(vectors.count, 200)
        for vector in vectors {
            let now = When.fromMs(vector.now)
            let actual = When.parse(vector.input, now: now, calendar: utc)
            XCTAssertEqual(actual, vector.expected?.at, "\(vector.now): \(vector.input)")
            if let expected = vector.expected {
                XCTAssertEqual(When.format(expected.at, now: now, calendar: utc), expected.formatted, vector.input)
                XCTAssertEqual(expected.at <= vector.now, expected.past, vector.input)
                XCTAssertEqual(When.toMs(When.fromMs(expected.at)), expected.at)
            }
        }
        for vector in try ContractFixture.decode(PresetVector.self, family: "presets") {
            let actual = When.presets(now: When.fromMs(vector.now), calendar: utc)
            XCTAssertEqual(actual.map(\.label), vector.expected.map(\.label))
            XCTAssertEqual(actual.map(\.at), vector.expected.map(\.at))
        }
    }

    private struct OrderVector: Decodable { let input: [Card]; let sortedIds: [Int64]; let ranks: [Double] }
    func testOrderingAndRankAllocationMatchProductionCSharp() throws {
        let vector = try XCTUnwrap(ContractFixture.decode(OrderVector.self, family: "order").first)
        XCTAssertEqual(vector.input.sorted(by: Card.compare).map(\.id), vector.sortedIds)
        XCTAssertEqual(Card.ranks(vector.input), vector.ranks)
    }

    private struct PaletteVector: Decodable {
        let rotation: [Int]
        let swatches: [SwatchVector]
        let overdue: String, uiAccent: String
        struct SwatchVector: Decodable { let name: String, back: String, strip: String, accent: String, ink: String }
    }
    func testPaletteStorageIDsMatchProductionCSharp() throws {
        let vector = try XCTUnwrap(ContractFixture.decode(PaletteVector.self, family: "palette").first)
        XCTAssertEqual(Card.rotation, vector.rotation)
        XCTAssertEqual(Palette.all.count, vector.swatches.count)
        for (actual, expected) in zip(Palette.all, vector.swatches) {
            XCTAssertEqual(actual.name, expected.name)
            XCTAssertEqual(actual.back.hex, expected.back)
            XCTAssertEqual(actual.strip.hex, expected.strip)
            XCTAssertEqual(actual.accent.hex, expected.accent)
            XCTAssertEqual(actual.ink.hex, expected.ink)
        }
        XCTAssertEqual(Palette.overdue.hex, vector.overdue)
        XCTAssertEqual(Palette.uiAccent.hex, vector.uiAccent)
        XCTAssertEqual(Palette.get(-1), Palette.all.last)
    }

    func testNativeMacSourceAdaptationDoesNotRewriteStoredBody() {
        var card = Card()
        card.body = "file:///Users/alice/a%20b.txt\n- [ ] review"
        XCTAssertEqual(card.src, "/Users/alice/a b.txt")
        XCTAssertEqual(card.note, "- [ ] review")
        XCTAssertEqual(card.body, "file:///Users/alice/a%20b.txt\n- [ ] review")
        card.body = "/Users/alice/report.md"
        XCTAssertTrue(card.isFile)
        card.body = "/api/items"
        XCTAssertFalse(card.isFile)
    }
}



