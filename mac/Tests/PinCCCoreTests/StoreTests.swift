import Foundation
import XCTest
@testable import PinCCCore

final class StoreTests: XCTestCase {
    private enum TestFailure: Error { case planned }

    private func withHome(_ test: (String) throws -> Void) throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("PinCC-StoreTests-" + UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        try test(root.path)
    }
    private func card(_ body: String) -> Card { var result = Card(); result.body = body; return result }

    private struct SchemaVector: Decodable { let version: Int; let sql: String; let columns: [String] }
    func testSchemaMatchesOriginalCSharpSQLAndOpeningDoesNotWrite() throws {
        let schema = try XCTUnwrap(ContractFixture.decode(SchemaVector.self, family: "schema").first)
        try withHome { home in
            let store = try Store(home: home)
            defer { store.close() }
            let reference = try Db(path: URL(fileURLWithPath: home).appendingPathComponent("oracle-schema.db").path)
            defer { reference.close() }
            try reference.script(schema.sql)
            XCTAssertEqual(Store.schemaVersion, schema.version)
            XCTAssertEqual(Card.columns, schema.columns)
            XCTAssertEqual(try store.db.scalar("PRAGMA user_version").int64, Int64(schema.version))
            for table in ["cards", "ops", "settings"] {
                XCTAssertEqual(try store.db.query("PRAGMA table_info(\(table))"), try reference.query("PRAGMA table_info(\(table))"), table)
            }
            for index in ["ops_grp", "ops_card"] {
                XCTAssertEqual(try store.db.query("PRAGMA index_info(\(index))"), try reference.query("PRAGMA index_info(\(index))"), index)
            }
            let before = try store.dataVersion
            let reopened = try Store(home: home)
            reopened.close()
            XCTAssertEqual(try store.dataVersion, before)
        }
    }


    func testUnversionedLegacyJournalMigratesInPlaceAndFirstWriteIsUndoable() throws {
        let schema = try XCTUnwrap(ContractFixture.decode(SchemaVector.self, family: "schema").first)
        try withHome { home in
            let imageDirectory = URL(fileURLWithPath: home).appendingPathComponent("img", isDirectory: true)
            try FileManager.default.createDirectory(at: imageDirectory, withIntermediateDirectories: true)
            let imageURL = imageDirectory.appendingPathComponent("legacy.png")
            let imageBytes = Data([0x89, 0x50, 0x4e, 0x47])
            try imageBytes.write(to: imageURL)
            var snapshot = card("legacy before")
            snapshot.id = 41
            snapshot.rev = 3
            snapshot.images = ["img/legacy.png"]
            snapshot.color = 6
            snapshot.createdAt = 11
            snapshot.updatedAt = 12
            let legacy = try Db(path: URL(fileURLWithPath: home).appendingPathComponent("pincc.db").path)
            try legacy.script(schema.sql.replacingOccurrences(of: ", ord REAL", with: ""))
            try legacy.script("""
                DROP INDEX IF EXISTS ops_grp;
                DROP INDEX IF EXISTS ops_card;
                DROP TABLE ops;
                CREATE TABLE ops(id INTEGER PRIMARY KEY, grp INTEGER NOT NULL, at INTEGER NOT NULL,
                  card_id INTEGER NOT NULL, op TEXT NOT NULL, before TEXT);
                PRAGMA user_version = 0;
                """)
            try legacy.exec("INSERT INTO cards(id,rev,body,images,color,created_at,updated_at) VALUES(41,3,'saved body',?,6,11,12)",
                            [.text("[\"img/legacy.png\"]")])
            try legacy.exec("INSERT INTO settings(key,value) VALUES('color.mode','tag')")
            try legacy.exec("INSERT INTO ops(id,grp,at,card_id,op,before) VALUES(900,900,1,41,'edit',?)",
                            [.text(try snapshot.toJSON())])
            legacy.close()

            let store = try Store(home: home)
            defer { store.close() }
            XCTAssertFalse(store.readOnly)
            XCTAssertEqual(try store.db.scalar("PRAGMA user_version").int64, 2)
            XCTAssertEqual(try store.db.scalar("SELECT count(*) FROM pragma_table_info('ops') WHERE name='cols'").int64, 1)
            let stored = try XCTUnwrap(store.get(41))
            XCTAssertEqual(stored.body, "saved body")
            XCTAssertEqual(stored.images, ["img/legacy.png"])
            XCTAssertEqual(stored.color, 6)
            XCTAssertEqual(stored.createdAt, 11)
            XCTAssertNil(stored.ord)
            XCTAssertEqual(try store.setting("color.mode"), "tag")
            XCTAssertEqual(try Data(contentsOf: imageURL), imageBytes)
            let oldEntry = try XCTUnwrap(store.ops().first)
            XCTAssertEqual(oldEntry.id, 900)
            XCTAssertEqual(oldEntry.grp, 900)
            XCTAssertNil(oldEntry.cols)

            let change = store.begin()
            _ = try store.mutate(change, id: stored.id, op: "edit") { $0.body = "first write after migration" }
            XCTAssertGreaterThan(change.number, 900)
            XCTAssertEqual(try store.restore(group: change.number), [stored.id])
            XCTAssertEqual(try store.get(stored.id)?.body, "saved body")
            XCTAssertEqual(try store.get(stored.id)?.images, ["img/legacy.png"])
            XCTAssertEqual(try store.restore(group: 900), [stored.id])
            XCTAssertEqual(try store.get(stored.id)?.body, "legacy before")
            XCTAssertEqual(try store.get(stored.id)?.images, ["img/legacy.png"])
            XCTAssertEqual(try Data(contentsOf: imageURL), imageBytes)
            XCTAssertEqual(try store.setting("color.mode"), "tag")
        }
    }

    func testRevisionConflictAndNoOpDoNotJournal() throws {
        try withHome { home in
            let gui = try Store(home: home)
            let cli = try Store(home: home)
            defer { gui.close(); cli.close() }
            let saved = try gui.insert(gui.begin(), card("original"))
            let revision = saved.rev
            let guiVersion = try gui.dataVersion
            let edited = try XCTUnwrap(cli.mutate(cli.begin(), id: saved.id, op: "set", ifRev: revision) { $0.body = "external" })
            XCTAssertGreaterThan(try gui.dataVersion, guiVersion)
            let failed = gui.begin()
            XCTAssertThrowsError(try gui.mutate(failed, id: saved.id, op: "edit", ifRev: revision) { $0.body = "stale" }) { error in
                XCTAssertEqual((error as? RevConflictError)?.current, edited.rev)
            }
            XCTAssertTrue(failed.empty)
            XCTAssertEqual(try gui.get(saved.id)?.body, "external")
            let count = try gui.ops().count
            let noOp = gui.begin()
            let unchanged = try XCTUnwrap(gui.mutate(noOp, id: saved.id, op: "edit") { _ in })
            XCTAssertTrue(noOp.empty)
            XCTAssertEqual(unchanged.rev, edited.rev)
            XCTAssertEqual(try gui.ops().count, count)
        }
    }

    func testGroupUndoOnlyRestoresOwnedFieldsAcrossExternalWrites() throws {
        try withHome { home in
            let gui = try Store(home: home)
            let cli = try Store(home: home)
            defer { gui.close(); cli.close() }
            let original = try gui.insert(gui.begin(), card("before"))
            let session = gui.begin()
            _ = try gui.mutate(session, id: original.id, op: "edit") { $0.body = "first"; $0.free = true }
            _ = try cli.mutate(cli.begin(), id: original.id, op: "set") { $0.color = 4; $0.topmost = true }
            _ = try gui.mutate(session, id: original.id, op: "edit") { $0.body = "last"; $0.h = 180 }
            let entries = try gui.ops(cardId: original.id).filter { $0.grp == session.number }
            XCTAssertEqual(entries.count, 2, "An intervening writer needs its own before snapshot")
            XCTAssertEqual(try gui.restore(group: session.number), [original.id])
            let restored = try XCTUnwrap(gui.get(original.id))
            XCTAssertEqual(restored.body, "before")
            XCTAssertFalse(restored.free)
            XCTAssertNil(restored.h)
            XCTAssertEqual(restored.color, 4)
            XCTAssertTrue(restored.topmost)
            let undoGroup = try XCTUnwrap(gui.ops().first?.grp)
            _ = try gui.restore(group: undoGroup)
            let redone = try XCTUnwrap(gui.get(original.id))
            XCTAssertEqual(redone.body, "last")
            XCTAssertTrue(redone.free)
            XCTAssertEqual(redone.h, 180)
            XCTAssertEqual(redone.color, 4)
            XCTAssertTrue(redone.topmost)
        }
    }

    func testCanonicalEquivalentUnicodeStillCountsAsAnOrdinalBodyEdit() throws {
        try withHome { home in
            let store = try Store(home: home)
            defer { store.close() }
            let composed = "\u{00e9}"
            let decomposed = "e\u{0301}"
            let original = try store.insert(store.begin(), card(composed))
            var stale = original
            stale.body = decomposed
            _ = try store.mutate(store.begin(), id: original.id, op: "set") { $0.color = 4 }
            XCTAssertThrowsError(try store.saveRecovery(key: "ordinal-conflict", snapshot: stale, expectedRev: original.rev)) {
                XCTAssertTrue($0 is RevConflictError)
            }
            let edit = store.begin()
            let previous = try XCTUnwrap(store.get(original.id))
            let updated = try XCTUnwrap(store.mutate(edit, id: original.id, op: "edit") { $0.body = decomposed })
            XCTAssertEqual(updated.rev, previous.rev + 1)
            XCTAssertFalse(edit.empty)
            XCTAssertEqual(Array(try XCTUnwrap(store.get(original.id)).body.utf16), Array(decomposed.utf16))
            _ = try store.restore(group: edit.number)
            XCTAssertEqual(Array(try XCTUnwrap(store.get(original.id)).body.utf16), Array(composed.utf16))
            XCTAssertEqual(try store.get(original.id)?.color, 4)
        }
    }

    func testCreationUndoAndRestoreAreUndoable() throws {
        try withHome { home in
            let store = try Store(home: home)
            defer { store.close() }
            let create = store.begin()
            let saved = try store.insert(create, card("created"))
            XCTAssertEqual(try store.restore(group: create.number), [saved.id])
            XCTAssertNotNil(try store.get(saved.id)?.deletedAt)
            XCTAssertTrue(try store.active().isEmpty)
            let undo = try XCTUnwrap(store.ops().first?.grp)
            XCTAssertEqual(try store.restore(group: undo), [saved.id])
            XCTAssertNil(try store.get(saved.id)?.deletedAt)
            XCTAssertEqual(try store.active().map(\.id), [saved.id])
            XCTAssertEqual(try store.get(saved.id)?.body, "created")
        }
    }

    func testTransactionsAndGroupBookkeepingRollBackTogether() throws {
        try withHome { home in
            let store = try Store(home: home)
            defer { store.close() }
            let group = store.begin()
            var checkpoints = 0
            XCTAssertThrowsError(try store.insertMany(group, [card("one"), card("two")], cancelled: {
                checkpoints += 1
                return checkpoints >= 3
            })) { XCTAssertTrue($0 is CancellationError) }
            XCTAssertTrue(group.empty)
            XCTAssertTrue(try store.all().isEmpty)
            XCTAssertTrue(try store.ops().isEmpty)
            let saved = try store.insert(group, card("success"))
            XCTAssertGreaterThan(group.number, 0)
            XCTAssertEqual(try store.ops().count, 1)
            let failedChange = store.begin()
            XCTAssertThrowsError(try store.mutate(failedChange, id: saved.id, op: "edit") { row in
                row.body = "must not commit"
                throw TestFailure.planned
            })
            XCTAssertTrue(failedChange.empty)
            XCTAssertEqual(try store.get(saved.id)?.body, "success")
            XCTAssertThrowsError(try store.db.transaction {
                try store.db.exec("INSERT INTO settings(key,value) VALUES('rollback','partial')")
                throw TestFailure.planned
            })
            XCTAssertEqual(try store.setting("rollback"), "")
            XCTAssertNoThrow(try store.db.transaction { try store.db.exec("INSERT INTO settings(key,value) VALUES('after','ok')") })
        }
    }

    func testRecoveryKeysAreIdempotentAndConflictingBodiesRemainUntouched() throws {
        try withHome { home in
            let store = try Store(home: home)
            defer { store.close() }
            let first = try store.saveRecovery(key: "new-draft", snapshot: card("draft"), expectedRev: nil)
            let retry = try store.saveRecovery(key: "new-draft", snapshot: card("later retry"), expectedRev: nil)
            XCTAssertEqual(first.card.id, retry.card.id)
            XCTAssertEqual(first.groupNumber, retry.groupNumber)
            XCTAssertEqual(retry.card.body, "draft")
            XCTAssertEqual(try store.all().count, 1)
            XCTAssertEqual(try store.ops().count, 1)
            var draft = first.card
            draft.body = "typed but uncommitted"
            _ = try store.mutate(store.begin(), id: first.card.id, op: "set") { $0.body = "CLI body"; $0.color = 6 }
            XCTAssertThrowsError(try store.saveRecovery(key: "stale-draft", snapshot: draft, expectedRev: first.card.rev)) {
                XCTAssertTrue($0 is RevConflictError)
            }
            XCTAssertEqual(try store.get(first.card.id)?.body, "CLI body")
            XCTAssertEqual(try store.setting("recovery.stale-draft"), "")
            draft.body = "CLI body"
            let sameBody = try store.saveRecovery(key: "same-body", snapshot: draft, expectedRev: first.card.rev)
            XCTAssertEqual(sameBody.card.color, 6)
            XCTAssertEqual(sameBody.card.body, "CLI body")
        }
    }

    /// Put checkpoints only in the temporary test Home. The production writer's
    /// Application Support fallback is deliberately outside this test's scope.
    private func checkpoint(home: String, drafts: [EditDraft]) throws -> String {
        let directory = URL(fileURLWithPath: home).appendingPathComponent("recovery", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let path = directory.appendingPathComponent("edits-test-" + UUID().uuidString + ".json")
        try JSONEncoder().encode(drafts).write(to: path, options: .atomic)
        return path.path
    }

    func testConflictingCheckpointSurvivesAndExplicitRestoreAsNewPreservesExternalEdit() throws {
        try withHome { home in
            let store = try Store(home: home)
            defer { store.close() }
            let original = try store.insert(store.begin(), card("initial"))
            var draft = original
            draft.body = "typed draft"
            let path = try checkpoint(home: home, drafts: [EditDraft(key: "conflict", card: draft, expectedRev: original.rev)])
            _ = try store.mutate(store.begin(), id: original.id, op: "set") { $0.body = "CLI edit" }
            XCTAssertTrue(try DraftRecovery.recover(home: home).isEmpty)
            XCTAssertTrue(FileManager.default.fileExists(atPath: path))
            XCTAssertEqual(try store.get(original.id)?.body, "CLI edit")
            let pending = try XCTUnwrap(DraftRecovery.pending(home: home).first)
            let restored = try DraftRecovery.restoreAsNew(home: home, pending: pending)
            XCTAssertEqual(restored.count, 1)
            XCTAssertNotEqual(restored[0].id, original.id)
            XCTAssertEqual(restored[0].body, "typed draft")
            XCTAssertTrue(restored[0].active)
            XCTAssertEqual(try store.get(original.id)?.body, "CLI edit")
            XCTAssertFalse(FileManager.default.fileExists(atPath: path))
        }
    }

    func testDuplicateCheckpointsRecoverExactlyOnce() throws {
        try withHome { home in
            let draft = EditDraft(key: "duplicate", card: card("one durable draft"), expectedRev: nil)
            let first = try checkpoint(home: home, drafts: [draft])
            let second = try checkpoint(home: home, drafts: [draft])
            XCTAssertEqual(try DraftRecovery.recover(home: home).count, 1)
            XCTAssertFalse(FileManager.default.fileExists(atPath: first))
            XCTAssertFalse(FileManager.default.fileExists(atPath: second))
            XCTAssertTrue(try DraftRecovery.recover(home: home).isEmpty)
            let store = try Store(home: home)
            defer { store.close() }
            XCTAssertEqual(try store.all().count, 1)
            XCTAssertEqual(try store.ops().count, 1)
            XCTAssertEqual(try store.setting("recovery.duplicate"), "")
        }
    }

    func testCorruptCheckpointRemainsWithoutBlockingValidRecovery() throws {
        try withHome { home in
            let valid = try checkpoint(home: home, drafts: [EditDraft(key: "valid-neighbor", card: card("recoverable"), expectedRev: nil)])
            let invalid = URL(fileURLWithPath: home).appendingPathComponent("recovery/edits-test-corrupt.json")
            try Data("{invalid JSON".utf8).write(to: invalid)
            var failures: [String] = []
            let recovered = try DraftRecovery.recover(home: home) { path, _ in failures.append(path) }
            XCTAssertEqual(recovered.map { $0.card.body }, ["recoverable"])
            XCTAssertTrue(failures.contains(invalid.path))
            XCTAssertTrue(FileManager.default.fileExists(atPath: invalid.path))
            XCTAssertFalse(FileManager.default.fileExists(atPath: valid))
            let store = try Store(home: home)
            defer { store.close() }
            XCTAssertEqual(try store.all().count, 1)
        }
    }

    func testFutureSchemaReadsButRefusesAllStoreWrites() throws {
        try withHome { home in
            let current = try Store(home: home)
            let saved = try current.insert(current.begin(), card("future reader"))
            try current.db.script("PRAGMA user_version=99")
            current.close()
            let future = try Store(home: home)
            defer { future.close() }
            XCTAssertTrue(future.readOnly)
            XCTAssertEqual(try future.get(saved.id)?.body, "future reader")
            XCTAssertThrowsError(try future.insert(future.begin(), card("blocked")))
            XCTAssertThrowsError(try future.mutate(future.begin(), id: saved.id, op: "set") { $0.body = "blocked" })
            XCTAssertThrowsError(try future.setSetting("blocked", "value"))
            XCTAssertThrowsError(try future.forget(saved.id))
            XCTAssertThrowsError(try future.restore(group: 1))
            XCTAssertThrowsError(try future.saveImage(Data([1, 2, 3])))
            XCTAssertThrowsError(try future.saveRecovery(key: "blocked", snapshot: card("blocked"), expectedRev: nil))
            XCTAssertEqual(try future.db.scalar("PRAGMA user_version").int64, 99)
            XCTAssertEqual(try future.all().count, 1)
        }
    }

    func testImagesStayAliveWhileReferencedByCardOrUndoSnapshot() throws {
        try withHome { home in
            let store = try Store(home: home)
            defer { store.close() }
            let image = try store.saveImage(Data([0x89, 0x50, 0x4e, 0x47]))
            XCTAssertEqual(try store.saveImage(Data([0x89, 0x50, 0x4e, 0x47])), image)
            XCTAssertThrowsError(try store.saveImage(Data([1]), ext: "../png"))
            let file = store.resolveImage(image)
            try FileManager.default.setAttributes([.modificationDate: Date().addingTimeInterval(-30 * 86_400)], ofItemAtPath: file)
            var row = card("")
            row.images = [image]
            let saved = try store.insert(store.begin(), row)
            try store.purge(days: 7)
            XCTAssertTrue(FileManager.default.fileExists(atPath: file))
            _ = try store.mutate(store.begin(), id: saved.id, op: "edit") { $0.images = [] }
            try store.purge(days: 7)
            XCTAssertTrue(FileManager.default.fileExists(atPath: file), "Undo still owns the old image")
            try store.forget(saved.id)
            try store.purge(days: 7)
            XCTAssertFalse(FileManager.default.fileExists(atPath: file))
        }
    }

    private final class LeaseResult: @unchecked Sendable {
        private let lock = NSLock()
        private var value: Result<Bool, Error>?
        func set(_ result: Result<Bool, Error>) { lock.lock(); defer { lock.unlock() }; value = result }
        func get() -> Result<Bool, Error>? { lock.lock(); defer { lock.unlock() }; return value }
    }
    func testImageLeaseExcludesAnotherThreadAndSymlinkAlias() throws {
        try withHome { home in
            let alias = URL(fileURLWithPath: home).appendingPathComponent("alias")
            try FileManager.default.createSymbolicLink(atPath: alias.path, withDestinationPath: home)
            let lease = try XCTUnwrap(HomeLease.tryEnter(home: home, purpose: "Images"))
            defer { lease.close() }
            let nested = try XCTUnwrap(HomeLease.tryEnter(home: alias.path, purpose: "Images"))
            nested.close()
            let result = LeaseResult()
            let completed = DispatchSemaphore(value: 0)
            let aliasPath = alias.path
            Thread {
                do {
                    let other = try HomeLease.tryEnter(home: aliasPath, purpose: "Images", waitMs: 25)
                    result.set(.success(other != nil))
                    other?.close()
                } catch { result.set(.failure(error)) }
                completed.signal()
            }.start()
            XCTAssertEqual(completed.wait(timeout: .now() + 2), .success)
            XCTAssertFalse(try XCTUnwrap(result.get()).get())
        }
    }
}


