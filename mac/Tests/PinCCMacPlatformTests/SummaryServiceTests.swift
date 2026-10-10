import Foundation
import XCTest
import PinCCCore
@testable import PinCCMacPlatform

final class SummaryServiceTests: XCTestCase {
    /// The fixture records requests under a lock because enrichment runs detached.
    private final class Reader: @unchecked Sendable {
        private let lock = NSLock()
        private var requests: [(Bool, String)] = []
        func read(_ card: Card, userAsked: Bool, home: String) -> SourceContent {
            lock.lock()
            requests.append((userAsked, home))
            lock.unlock()
            return SourceContent(text: String(repeating: "Document text. ", count: 40), title: "Fixture title", source: card.src)
        }
        var count: Int { lock.lock(); defer { lock.unlock() }; return requests.count }
        var asked: Bool? { lock.lock(); defer { lock.unlock() }; return requests.last?.0 }
    }
    private func temporaryHome() throws -> URL {
        let home = FileManager.default.temporaryDirectory.appendingPathComponent("PinCC-SummaryTests-" + UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: home, withIntermediateDirectories: true)
        return home
    }

    @MainActor
    func testDisabledAutomaticLinksKeepSnapshotAndManualReadingStillRuns() async throws {
        let home = try temporaryHome()
        defer { try? FileManager.default.removeItem(at: home) }
        let store = try Store(home: home.path)
        defer { store.close() }
        try store.setSetting("llm.auto", false)
        var card = Card()
        card.body = "https://example.invalid/document\nUser note"
        card.summary = "Existing summary"
        var meta = CardMeta()
        meta.src = card.src
        meta.title = "Existing title"
        meta.textHash = Card.hash("Existing source")
        meta.sumHash = meta.textHash
        card.meta = try meta.toJSON()
        let saved = try store.insert(store.begin(), card)
        let operationCount = try store.ops().count
        let reader = Reader()
        let service = SummaryService(store: store, readSource: { card, asked, home in
            reader.read(card, userAsked: asked, home: home)
        })

        _ = try await service.enrich(cardID: saved.id)
        XCTAssertEqual(reader.count, 0, "Closing the automatic-reading setting must stop source requests")
        XCTAssertEqual(try store.get(saved.id), saved)
        XCTAssertEqual(try store.get(saved.id)?.inputHash, saved.inputHash)
        XCTAssertEqual(try store.ops().count, operationCount)

        _ = try await service.enrich(cardID: saved.id, userAsked: true)
        XCTAssertEqual(reader.count, 1)
        XCTAssertEqual(reader.asked, true)
        let manual = try XCTUnwrap(store.get(saved.id))
        XCTAssertEqual(manual.body, saved.body)
        XCTAssertEqual(manual.summary, saved.summary)
        XCTAssertEqual(manual.srcInfo?.title, "Fixture title")
        XCTAssertEqual(manual.inputHash, Card.hash(String(repeating: "Document text. ", count: 40)))
    }

    @MainActor
    func testDisabledAutomaticLinksStillAllowLocalFileMetadata() async throws {
        let home = try temporaryHome()
        defer { try? FileManager.default.removeItem(at: home) }
        let store = try Store(home: home.path)
        defer { store.close() }
        try store.setSetting("llm.auto", false)
        var card = Card()
        card.body = home.appendingPathComponent("local.txt").path
        let saved = try store.insert(store.begin(), card)
        let reader = Reader()
        let service = SummaryService(store: store, readSource: { card, asked, home in
            reader.read(card, userAsked: asked, home: home)
        })

        _ = try await service.enrich(cardID: saved.id)
        XCTAssertEqual(reader.count, 1)
        XCTAssertEqual(reader.asked, false)
        XCTAssertEqual(try store.get(saved.id)?.srcInfo?.title, "Fixture title")
    }
}
