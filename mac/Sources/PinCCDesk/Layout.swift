import Foundation

/// Work-area coordinates use a top-left origin and points. Only MacScreens converts AppKit coordinates.
public struct LayoutRect: Equatable, Sendable, Codable {
    public var x: Double
    public var y: Double
    public var width: Double
    public var height: Double
    public init(x: Double = 0, y: Double = 0, width: Double = 0, height: Double = 0) {
        self.x = x; self.y = y; self.width = width; self.height = height
    }
    public var right: Double { x + width }
    public var bottom: Double { y + height }
    public func intersects(_ other: LayoutRect) -> Bool {
        x < other.right && right > other.x && y < other.bottom && bottom > other.y
    }
    public func inflated(_ gap: Double) -> LayoutRect {
        LayoutRect(x: x - gap, y: y - gap, width: width + gap * 2, height: height + gap * 2)
    }
    public func clamped(into area: LayoutRect) -> LayoutRect {
        let width = min(width, area.width), height = min(height, area.height)
        return LayoutRect(x: min(max(x, area.x), area.right - width),
                          y: min(max(y, area.y), area.bottom - height), width: width, height: height)
    }
}

public struct LayoutItem: Equatable, Sendable {
    public var id: Int64
    public var width: Double
    public var height: Double
    public var pillHeight: Double
    public var collapsed: Bool
    public var `protected`: Bool
    public var free: Bool
    public var x: Double
    public var y: Double
    public init(id: Int64, width: Double, height: Double, pillHeight: Double = 36,
                collapsed: Bool = false, protected: Bool = false, free: Bool = false,
                x: Double = 0, y: Double = 0) {
        self.id = id; self.width = width; self.height = height; self.pillHeight = pillHeight
        self.collapsed = collapsed; self.protected = protected; self.free = free; self.x = x; self.y = y
    }
}

public struct LayoutPlacement: Equatable, Sendable {
    public var id: Int64
    public var rect: LayoutRect
    public var collapsed: Bool
    public var hidden: Bool
    public init(id: Int64, rect: LayoutRect, collapsed: Bool, hidden: Bool) {
        self.id = id; self.rect = rect; self.collapsed = collapsed; self.hidden = hidden
    }
}

/// Port of src/UI/Layout.cs. It never persists computed positions or temporary capsule state.
public enum Layout {
    public static func compute(items: [LayoutItem], area: LayoutRect, hub: LayoutRect,
                               corner: String = "TopRight", gap: Double = 12) -> [LayoutPlacement] {
        let obstacles = [hub] + items.filter { $0.free }.map {
            LayoutRect(x: $0.x, y: $0.y, width: $0.width, height: $0.collapsed ? $0.pillHeight : $0.height).clamped(into: area)
        }
        let tiled = items.filter { !$0.free }
        var expanded = tiled.count
        var slots: [LayoutRect?] = []
        while true {
            let sizes = tiled.enumerated().map { index, item in
                (item.width, item.collapsed || (index >= expanded && !item.protected) ? min(item.height, item.pillHeight) : item.height)
            }
            slots = place(area: area, sizes: sizes, obstacles: obstacles, gap: gap, inset: 16, corner: corner)
            if expanded == 0 || slots.allSatisfy({ $0 != nil }) { break }
            expanded -= 1
        }
        var result: [LayoutPlacement] = []
        var index = 0
        for item in items {
            if item.free {
                result.append(LayoutPlacement(id: item.id,
                    rect: LayoutRect(x: item.x, y: item.y, width: item.width,
                                     height: item.collapsed ? item.pillHeight : item.height).clamped(into: area),
                    collapsed: item.collapsed, hidden: false))
            } else {
                result.append(LayoutPlacement(id: item.id, rect: slots[index] ?? LayoutRect(),
                    collapsed: item.collapsed || (index >= expanded && !item.protected && item.height > item.pillHeight),
                    hidden: slots[index] == nil))
                index += 1
            }
        }
        return result
    }

    private static func place(area: LayoutRect, sizes: [(Double, Double)], obstacles: [LayoutRect],
                              gap: Double, inset: Double, corner: String) -> [LayoutRect?] {
        let normalized = corner.lowercased().replacingOccurrences(of: "-", with: "")
        let flipX = normalized == "topright" || normalized == "bottomright"
        let flipY = normalized == "bottomleft" || normalized == "bottomright"
        func mirror(_ rect: LayoutRect) -> LayoutRect {
            LayoutRect(x: flipX ? area.x + area.right - rect.right : rect.x,
                       y: flipY ? area.y + area.bottom - rect.bottom : rect.y,
                       width: rect.width, height: rect.height)
        }
        let obstacles = obstacles.map { mirror($0).inflated(gap) }
        let top = area.y + inset, bottom = area.bottom - inset, right = area.right - inset
        var x = area.x + inset, y = top, columnWidth = 0.0
        var full = false
        return sizes.map { width, height in
            var placed: LayoutRect?
            while !full {
                if y > top && y + height > bottom {
                    x += (columnWidth > 0 ? columnWidth : width) + gap; y = top; columnWidth = 0
                }
                if x + width > right && x > area.x + inset { full = true; break }
                let candidate = LayoutRect(x: x, y: y, width: width, height: height)
                if let hit = obstacles.filter({ $0.intersects(candidate) }).min(by: { $0.bottom < $1.bottom }) {
                    y = max(y + 1, hit.bottom)
                    if y + height > bottom {
                        x += max(columnWidth, width) + gap; y = top; columnWidth = 0
                    }
                    continue
                }
                placed = candidate; y += height + gap; columnWidth = max(columnWidth, width)
                break
            }
            return placed.map(mirror)
        }
    }

    /// Pull back only the edges moved by a free-card resize; src/UI/Desk.cs Trim.
    public static func trim(_ rect: LayoutRect, start: LayoutRect, obstacles: [LayoutRect],
                            area: LayoutRect, minW: Double = 160, minH: Double = 64,
                            gap: Double = 12) -> LayoutRect {
        var left = rect.x, top = rect.y, right = rect.right, bottom = rect.bottom
        for obstacle in obstacles.map({ $0.inflated(gap) }) {
            guard LayoutRect(x: left, y: top, width: right - left, height: bottom - top).intersects(obstacle) else { continue }
            if right != start.right && obstacle.x >= start.right - 1 {
                right = max(left + minW, obstacle.x)
            } else if bottom != start.bottom && obstacle.y >= start.bottom - 1 {
                bottom = max(top + minH, obstacle.y)
            } else if left != start.x && obstacle.right <= start.x + 1 {
                left = min(right - minW, obstacle.right)
            } else if top != start.y && obstacle.bottom <= start.y + 1 {
                top = min(bottom - minH, obstacle.bottom)
            }
        }
        let trimmed = LayoutRect(x: left, y: top, width: right - left, height: bottom - top)
        return obstacles.contains { $0.inflated(gap).intersects(trimmed) }
            ? pushOut(trimmed, obstacles: obstacles, area: area, gap: gap) : trimmed
    }

    public static func pushOut(_ rect: LayoutRect, obstacles: [LayoutRect], area: LayoutRect, gap: Double = 12) -> LayoutRect {
        var rect = rect.clamped(into: area)
        for _ in 0..<12 {
            guard let hit = obstacles.map({ $0.inflated(gap) }).first(where: { $0.intersects(rect) }) else { break }
            let moves = [(rect.right - hit.x, hit.x - rect.right, 0.0),
                         (hit.right - rect.x, hit.right - rect.x, 0.0),
                         (rect.bottom - hit.y, 0.0, hit.y - rect.bottom),
                         (hit.bottom - rect.y, 0.0, hit.bottom - rect.y)]
            let ranked = moves.enumerated().sorted { lhs, rhs in
                func fits(_ move: (Double, Double, Double)) -> Bool {
                    rect.x + move.1 >= area.x && rect.y + move.2 >= area.y &&
                    rect.right + move.1 <= area.right && rect.bottom + move.2 <= area.bottom
                }
                let a = fits(lhs.element), b = fits(rhs.element)
                if a != b { return a }
                if lhs.element.0 != rhs.element.0 { return lhs.element.0 < rhs.element.0 }
                return lhs.offset < rhs.offset
            }
            guard let move = ranked.first?.element else { break }
            rect.x += move.1; rect.y += move.2; rect = rect.clamped(into: area)
        }
        return rect
    }
}
