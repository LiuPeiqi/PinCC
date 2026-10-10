import Foundation
import XCTest
@testable import PinCCDesk

final class LayoutTests: XCTestCase {
    private struct TileVector: Decodable {
        let name: String
        let area: LayoutRect
        let hub: LayoutRect
        let corner: String
        let gap: Double
        let items: [Item]
        let expected: [Placement]
        struct Item: Decodable { let id: Int64; let width: Double, height: Double, pillHeight: Double }
        struct Placement: Decodable { let id: Int64; let rect: LayoutRect; let collapsed: Bool, hidden: Bool }
    }
    private struct GeometryVector: Decodable {
        let name: String
        let area: LayoutRect
        let rect: LayoutRect
        let obstacles: [LayoutRect]
        let gap: Double
        let clamped: LayoutRect
        let pushed: LayoutRect
    }
    private func vectors<T: Decodable>(_ type: T.Type, family: String) throws -> [T] {
        let file = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent()
            .deletingLastPathComponent().appendingPathComponent("contracts/vectors.jsonl")
        return try String(contentsOf: file, encoding: .utf8).split(separator: "\n").compactMap { line in
            let data = Data(line.utf8)
            let object = try JSONSerialization.jsonObject(with: data) as? [String: Any]
            guard object?["type"] as? String == family else { return nil }
            return try JSONDecoder().decode(type, from: data)
        }
    }

    func testAllCornersOverflowAndObstaclesMatchProductionCSharp() throws {
        let cases = try vectors(TileVector.self, family: "layout")
        XCTAssertEqual(cases.count, 80)
        for vector in cases {
            let items = vector.items.map { LayoutItem(id: $0.id, width: $0.width, height: $0.height, pillHeight: $0.pillHeight) }
            let actual = Layout.compute(items: items, area: vector.area, hub: vector.hub, corner: vector.corner, gap: vector.gap)
            XCTAssertEqual(actual.count, vector.expected.count, vector.name)
            for (placement, expected) in zip(actual, vector.expected) {
                XCTAssertEqual(placement.id, expected.id, vector.name)
                XCTAssertEqual(placement.rect, expected.rect, vector.name)
                XCTAssertEqual(placement.collapsed, expected.collapsed, vector.name)
                XCTAssertEqual(placement.hidden, expected.hidden, vector.name)
            }
        }
    }

    func testClampingAndPushOutMatchProductionCSharp() throws {
        let cases = try vectors(GeometryVector.self, family: "geometry")
        XCTAssertEqual(cases.count, 80)
        for vector in cases {
            XCTAssertEqual(vector.rect.clamped(into: vector.area), vector.clamped, vector.name)
            XCTAssertEqual(Layout.pushOut(vector.rect, obstacles: vector.obstacles, area: vector.area, gap: vector.gap), vector.pushed, vector.name)
        }
    }

    func testFreePositionsAndTemporaryCapsulesDoNotMutateInputs() {
        let area = LayoutRect(x: -1000, y: 50, width: 420, height: 300)
        let free = LayoutItem(id: 1, width: 180, height: 120, free: true, x: -1200, y: 500)
        let items = [free] + (2...12).map { LayoutItem(id: Int64($0), width: 220, height: 180) }
        let result = Layout.compute(items: items, area: area, hub: LayoutRect(x: -650, y: 62, width: 44, height: 44))
        XCTAssertEqual(items[0], free)
        XCTAssertEqual(result[0].rect, LayoutRect(x: -1000, y: 230, width: 180, height: 120))
        XCTAssertFalse(result[0].hidden)
        XCTAssertTrue(result.dropFirst().contains { $0.collapsed || $0.hidden })
        XCTAssertTrue(items.dropFirst().allSatisfy { !$0.collapsed })
    }
}
