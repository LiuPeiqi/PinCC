import Foundation
import XCTest
import PinCCCore
import PinCCCli

/// Serial command transcripts use isolated homes and the public CLI boundary.
/// Expected fields and task behavior follow src/Core/Cli.cs and tests/Program.cs.
final class CLITests: XCTestCase {
    private struct Transcript {
        let code: Int32
        let output: [String]
        let stdinReads: Int
    }
    private struct Progress: Decodable, Equatable { let done: Int; let total: Int }
    private struct TaskDTO: Decodable, Equatable { let n: Int; let done: Bool; let text: String }
    private struct ColorDTO: Decodable { let index: Int; let auto: Bool }
    private struct CardDTO: Decodable {
        let id: Int64
        let rev: Int64
        let kind: String
        let title: String
        let src: String?
        let body: String
        let images: [String]
        let summary: String?
        let summaryStale: Bool
        let inputHash: String?
        let meta: CardMeta?
        let color: ColorDTO
        let topmost: Bool
        let opacity: Double
        let collapsed: Bool
        let free: Bool
        let overdue: Bool
        let progress: Progress
        let tasks: [TaskDTO]
        let remindAt: String?
        let doneAt: String?
        let deletedAt: String?
        let createdAt: String
        let updatedAt: String
        private enum CodingKeys: String, CodingKey {
            case id, rev, kind, title, src, body, images, summary, meta, color, topmost, opacity, collapsed, free, overdue, progress, tasks
            case summaryStale = "summary_stale", inputHash = "input_hash", remindAt = "remind_at", doneAt = "done_at"
            case deletedAt = "deleted_at", createdAt = "created_at", updatedAt = "updated_at"
        }
    }
    private struct LogDTO: Decodable {
        let grp: Int64
        let opID: Int64
        let at: String
        let card: Int64
        let op: String
        private enum CodingKeys: String, CodingKey { case grp, at, card, op; case opID = "op_id" }
    }
    private struct RestoreDTO: Decodable {
        let restoredGroup: Int64
        let cards: [Int64]
        private enum CodingKeys: String, CodingKey { case cards; case restoredGroup = "restored_grp" }
    }
    private struct OracleCard: Decodable {
        let name: String
        let input: Card
        let expected: Expected
        struct Expected: Decodable {
            let title: String
            let kind: String
            let inputHash: String?
            let done: Int
            let total: Int
        }
    }

    private func temporaryHome() throws -> URL {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("PinCC-CLITests-" + UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }
    @MainActor
    private func command(_ args: [String], home: URL, stdin: String = "") async -> Transcript {
        var output: [String] = []
        var reads = 0
        let code = await CLI.run(args, home: home.path, output: { output.append($0) }, readStdin: { reads += 1; return stdin })
        return Transcript(code: code, output: output, stdinReads: reads)
    }
    private func json<T: Decodable>(_ type: T.Type, _ transcript: Transcript, file: StaticString = #filePath, line: UInt = #line) throws -> T {
        XCTAssertEqual(transcript.code, 0, transcript.output.joined(separator: "\n"), file: file, line: line)
        XCTAssertEqual(transcript.output.count, 1, file: file, line: line)
        let text = try XCTUnwrap(transcript.output.first, file: file, line: line)
        return try JSONDecoder().decode(type, from: Data(text.utf8))
    }
    private func oracleCard(_ name: String) throws -> OracleCard {
        let directory = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent()
            .deletingLastPathComponent().appendingPathComponent("contracts", isDirectory: true)
        let records = try String(contentsOf: directory.appendingPathComponent("vectors.jsonl"), encoding: .utf8)
        for line in records.split(separator: "\n") {
            let data = Data(line.utf8)
            let header = try JSONSerialization.jsonObject(with: data) as? [String: Any]
            if header?["type"] as? String == "card", header?["name"] as? String == name {
                return try JSONDecoder().decode(OracleCard.self, from: data)
            }
        }
        throw CocoaError(.fileReadCorruptFile)
    }

    @MainActor
    func testJSONShapeAndReadCommandsDoNotWrite() async throws {
        let home = try temporaryHome()
        defer { try? FileManager.default.removeItem(at: home) }
        let baseline = try oracleCard("card-002")
        let addedOutput = await command(["add", baseline.input.body, "--color", "4", "--top", "--json"], home: home)
        let added = try json(CardDTO.self, addedOutput)
        XCTAssertGreaterThan(added.id, 0)
        XCTAssertEqual(added.rev, 1)
        XCTAssertEqual(added.kind, baseline.expected.kind)
        XCTAssertEqual(added.title, baseline.expected.title)
        XCTAssertEqual(added.inputHash, baseline.expected.inputHash)
        XCTAssertEqual(added.progress, Progress(done: baseline.expected.done, total: baseline.expected.total))
        XCTAssertEqual(added.body, baseline.input.body)
        XCTAssertNil(added.src)
        XCTAssertTrue(added.images.isEmpty)
        XCTAssertNil(added.summary)
        XCTAssertFalse(added.summaryStale)
        XCTAssertNil(added.meta)
        XCTAssertEqual(added.color.index, 4)
        XCTAssertFalse(added.color.auto)
        XCTAssertTrue(added.topmost)
        XCTAssertEqual(added.opacity, 1)
        XCTAssertFalse(added.collapsed)
        XCTAssertFalse(added.free)
        XCTAssertFalse(added.overdue)
        XCTAssertNil(added.remindAt)
        XCTAssertNil(added.doneAt)
        XCTAssertNil(added.deletedAt)
        let expectedKeys: Set<String> = ["id", "rev", "kind", "title", "src", "body", "images", "summary", "summary_stale", "input_hash", "meta", "color", "topmost", "opacity", "collapsed", "free", "overdue", "progress", "tasks", "remind_at", "done_at", "deleted_at", "created_at", "updated_at"]
        let object = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(addedOutput.output[0].utf8)) as? [String: Any])
        XCTAssertEqual(Set(object.keys), expectedKeys)
        let iso = ISO8601DateFormatter()
        XCTAssertNotNil(iso.date(from: added.createdAt))
        XCTAssertNotNil(iso.date(from: added.updatedAt))

        let observer = try Store(home: home.path)
        defer { observer.close() }
        let version = try observer.dataVersion
        let listOutput = await command(["list", "--json"], home: home)
        XCTAssertEqual(try json([CardDTO].self, listOutput).map(\.id), [added.id])
        let shownOutput = await command(["show", "#" + String(added.id), "--json"], home: home)
        XCTAssertEqual(try json(CardDTO.self, shownOutput).body, baseline.input.body)
        let logOutput = await command(["log", "5", "--json"], home: home)
        let log = try json([LogDTO].self, logOutput)
        XCTAssertEqual(log.count, 1)
        XCTAssertEqual(log[0].card, added.id)
        XCTAssertEqual(log[0].op, "create")
        XCTAssertGreaterThan(log[0].grp, 0)
        XCTAssertGreaterThan(log[0].opID, 0)
        XCTAssertNotNil(iso.date(from: log[0].at))
        XCTAssertEqual(try observer.dataVersion, version)
        XCTAssertEqual(addedOutput.stdinReads + listOutput.stdinReads + shownOutput.stdinReads + logOutput.stdinReads, 0)
    }

    @MainActor
    func testRestoreKeepsUnrelatedExternalFieldsAndSettings() async throws {
        let home = try temporaryHome()
        defer { try? FileManager.default.removeItem(at: home) }
        let create = await command(["add", "before", "--json"], home: home)
        let original = try json(CardDTO.self, create)
        let id = String(original.id)
        let edit = await command(["set", id, "after", "--json"], home: home)
        XCTAssertEqual(try json(CardDTO.self, edit).body, "after")
        let logOutput = await command(["log", "1", "--json"], home: home)
        let editedGroup = try XCTUnwrap(json([LogDTO].self, logOutput).first)
        XCTAssertEqual(editedGroup.op, "edit")

        let external = try Store(home: home.path)
        defer { external.close() }
        _ = try external.mutate(external.begin(), id: original.id, op: "view") { card in
            card.color = 6
            card.topmost = true
            card.free = true
            card.h = 180
        }
        try external.setSetting("color.mode", "tag")
        let restoredOutput = await command(["restore", String(editedGroup.grp), "--json"], home: home)
        let restored = try json(RestoreDTO.self, restoredOutput)
        XCTAssertEqual(restored.restoredGroup, editedGroup.grp)
        XCTAssertEqual(restored.cards, [original.id])
        let shownOutput = await command(["show", id, "--json"], home: home)
        let shown = try json(CardDTO.self, shownOutput)
        XCTAssertEqual(shown.body, "before")
        XCTAssertEqual(shown.color.index, 6)
        XCTAssertFalse(shown.color.auto)
        XCTAssertTrue(shown.topmost)
        XCTAssertTrue(shown.free)
        XCTAssertEqual(try external.get(original.id)?.h, 180)
        XCTAssertEqual(try external.setting("color.mode"), "tag")

        let entries = try external.ops().count
        let version = try external.dataVersion
        let againOutput = await command(["restore", String(editedGroup.grp), "--json"], home: home)
        XCTAssertEqual(try json(RestoreDTO.self, againOutput).cards, [original.id])
        XCTAssertEqual(try external.ops().count, entries)
        XCTAssertEqual(try external.dataVersion, version)
        XCTAssertEqual(try external.get(original.id)?.rev, shown.rev)
    }

    @MainActor
    func testDeleteIsIdempotentAndRestoreReturnsCardToActiveList() async throws {
        let home = try temporaryHome()
        defer { try? FileManager.default.removeItem(at: home) }
        let create = await command(["add", "keep me", "--json"], home: home)
        let original = try json(CardDTO.self, create)
        let id = String(original.id)
        let removedOutput = await command(["rm", id, "--json"], home: home)
        let removed = try json(CardDTO.self, removedOutput)
        XCTAssertNotNil(removed.deletedAt)
        let logOutput = await command(["log", "1", "--json"], home: home)
        let deletion = try XCTUnwrap(json([LogDTO].self, logOutput).first)
        XCTAssertEqual(deletion.op, "delete")
        let observer = try Store(home: home.path)
        defer { observer.close() }
        let entries = try observer.ops().count
        let again = await command(["rm", id], home: home)
        XCTAssertEqual(again.code, 0)
        XCTAssertTrue(again.output.joined().contains("已经是删除状态"))
        XCTAssertFalse(again.output.joined().contains("restore"))
        XCTAssertEqual(try observer.ops().count, entries)
        XCTAssertEqual(try observer.get(original.id)?.rev, removed.rev)
        let activeOutput = await command(["list", "--json"], home: home)
        XCTAssertTrue(try json([CardDTO].self, activeOutput).isEmpty)
        let allOutput = await command(["list", "--all", "--json"], home: home)
        XCTAssertEqual(try json([CardDTO].self, allOutput).map(\.id), [original.id])
        let restored = await command(["restore", String(deletion.grp), "--json"], home: home)
        XCTAssertEqual(try json(RestoreDTO.self, restored).cards, [original.id])
        let visibleOutput = await command(["list", "--json"], home: home)
        XCTAssertEqual(try json([CardDTO].self, visibleOutput).map(\.id), [original.id])
        XCTAssertNil(try observer.get(original.id)?.deletedAt)
    }

    @MainActor
    func testNumberedTaskTranscriptAndFailedEditsAreAtomic() async throws {
        let home = try temporaryHome()
        defer { try? FileManager.default.removeItem(at: home) }
        let create = await command(["add", "## 周末", "--add-task", "洗车", "--json"], home: home)
        let original = try json(CardDTO.self, create)
        let id = String(original.id)
        let add = await command(["set", id, "--add-task", "-", "--json"], home: home, stdin: "买菜\n- [ ] 取快递\n")
        XCTAssertEqual(add.stdinReads, 1)
        XCTAssertEqual(try json(CardDTO.self, add).progress.total, 3)
        let check = await command(["set", id, "--check", "1,3", "--json"], home: home)
        XCTAssertEqual(try json(CardDTO.self, check).progress, Progress(done: 2, total: 3))
        let finish = await command(["set", id, "--uncheck", "3", "--add-task", "理发", "--json"], home: home)
        let card = try json(CardDTO.self, finish)
        XCTAssertEqual(card.body, "## 周末\n- [x] 洗车\n- [ ] 买菜\n- [ ] 取快递\n- [ ] 理发")
        XCTAssertEqual(card.progress, Progress(done: 1, total: 4))
        XCTAssertEqual(card.tasks, [TaskDTO(n: 1, done: true, text: "洗车"), TaskDTO(n: 2, done: false, text: "买菜"), TaskDTO(n: 3, done: false, text: "取快递"), TaskDTO(n: 4, done: false, text: "理发")])
        let observer = try Store(home: home.path)
        defer { observer.close() }
        let before = try XCTUnwrap(observer.get(original.id))
        let entries = try observer.ops().count
        let invalid: [[String]] = [
            ["set", id, "--check", "9"], ["set", id, "--check", "0"], ["set", id, "--check", "x"],
            ["set", id, "--add-task", " "], ["add", "x", "--check", "1"],
            ["set", id, "--summary", "-", "--add-task", "-"]
        ]
        for args in invalid {
            let result = await command(args, home: home, stdin: "must not be written")
            XCTAssertEqual(result.code, 1, args.joined(separator: " "))
            XCTAssertEqual(result.stdinReads, 0)
        }
        XCTAssertEqual(try observer.get(original.id)?.values(), before.values())
        XCTAssertEqual(try observer.get(original.id)?.rev, before.rev)
        XCTAssertEqual(try observer.ops().count, entries)
        XCTAssertEqual(try observer.all().count, 1)
    }

    @MainActor
    func testRevisionAndInputConflictsWriteNothing() async throws {
        let home = try temporaryHome()
        defer { try? FileManager.default.removeItem(at: home) }
        let create = await command(["add", "note body", "--summary", "S", "--json"], home: home)
        let original = try json(CardDTO.self, create)
        let id = String(original.id)
        let observer = try Store(home: home.path)
        defer { observer.close() }
        let before = try XCTUnwrap(observer.get(original.id))
        let version = try observer.dataVersion
        let entries = try observer.ops().count
        let staleRevision = await command(["set", id, "changed", "--if-rev", String(original.rev - 1), "--json"], home: home)
        XCTAssertEqual(staleRevision.code, 3)
        XCTAssertTrue(staleRevision.output.joined().contains("rev="))
        let staleInput = await command(["set", id, "--summary", "-", "--if-input", "ABC", "--json"], home: home, stdin: "stale summary")
        XCTAssertEqual(staleInput.code, 3)
        XCTAssertEqual(staleInput.stdinReads, 1)
        XCTAssertTrue(staleInput.output.joined().contains("input_hash="))
        XCTAssertEqual(try observer.get(original.id)?.values(), before.values())
        XCTAssertEqual(try observer.get(original.id)?.rev, before.rev)
        XCTAssertEqual(try observer.ops().count, entries)
        XCTAssertEqual(try observer.dataVersion, version)

        let hash = try XCTUnwrap(original.inputHash)
        let acceptedOutput = await command(["set", id, "--summary", "-", "--if-input", hash.lowercased(), "--if-rev", String(original.rev), "--json"], home: home, stdin: "fresh summary\n")
        let accepted = try json(CardDTO.self, acceptedOutput)
        XCTAssertEqual(accepted.summary, "fresh summary")
        XCTAssertFalse(accepted.summaryStale)
        XCTAssertEqual(accepted.meta?.sumHash, hash)
        let editOutput = await command(["set", id, "edited body", "--json"], home: home)
        let edited = try json(CardDTO.self, editOutput)
        XCTAssertEqual(edited.summary, "fresh summary")
        XCTAssertTrue(edited.summaryStale)
    }

    @MainActor
    func testStdinIsExplicitAndUsageOrMissingErrorsDoNotCreateCards() async throws {
        let home = try temporaryHome()
        defer { try? FileManager.default.removeItem(at: home) }
        let errors: [([String], Int32)] = [
            (["frobnicate"], 1), (["add", "hello", "--bogus"], 1), (["list", "--remind", "x"], 1),
            (["set", "1", "--summary"], 1), (["add"], 1), (["add", "x", "--remind", "whenever"], 1),
            (["add", "x", "--color", "12"], 1), (["show", "424242", "--json"], 2),
            (["set", "424242", "new"], 2), (["rm", "424242"], 2), (["restore", "999999", "--json"], 2)
        ]
        for (args, expected) in errors {
            let result = await command(args, home: home, stdin: "unrequested stdin")
            XCTAssertEqual(result.code, expected, args.joined(separator: " "))
            XCTAssertEqual(result.stdinReads, 0)
            XCTAssertTrue(result.output.first?.hasPrefix("错误：") == true)
        }
        let emptyInput = await command(["add", "-"], home: home, stdin: " \n")
        XCTAssertEqual(emptyInput.code, 1)
        XCTAssertEqual(emptyInput.stdinReads, 1)
        let ambiguousInput = await command(["add", "-", "--summary", "-"], home: home, stdin: "unused")
        XCTAssertEqual(ambiguousInput.code, 1)
        XCTAssertEqual(ambiguousInput.stdinReads, 0)
        let store = try Store(home: home.path)
        defer { store.close() }
        XCTAssertTrue(try store.all().isEmpty)
        XCTAssertTrue(try store.ops().isEmpty)
        let literalOutput = await command(["add", "literal", "--json"], home: home, stdin: "ignored")
        XCTAssertEqual(try json(CardDTO.self, literalOutput).body, "literal")
        XCTAssertEqual(literalOutput.stdinReads, 0)
        let explicitOutput = await command(["add", "-", "--json"], home: home, stdin: "  leading space\n\n")
        XCTAssertEqual(try json(CardDTO.self, explicitOutput).body, "  leading space")
        XCTAssertEqual(explicitOutput.stdinReads, 1)
    }
}
