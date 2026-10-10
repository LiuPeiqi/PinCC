import AppKit
import ColorSync
import CoreGraphics
import PinCCDesk

/// Only this adapter converts AppKit bottom-left points to work-area top-left points.
@MainActor
struct MacScreen {
    let screen: NSScreen
    let id: String

    init(_ screen: NSScreen) {
        self.screen = screen
        let number = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber
        if let number, let uuid = CGDisplayCreateUUIDFromDisplayID(number.uint32Value) {
            id = "mac:" + (CFUUIDCreateString(nil, uuid.takeRetainedValue()) as String)
        } else {
            id = "mac:display:" + (number?.stringValue ?? screen.localizedName)
        }
    }

    var work: NSRect { screen.visibleFrame }
    var scale: Double { screen.backingScaleFactor }
    var area: LayoutRect { LayoutRect(x: 0, y: 0, width: work.width, height: work.height) }

    func appKit(_ rect: LayoutRect) -> NSRect {
        NSRect(x: work.minX + rect.x, y: work.maxY - rect.y - rect.height,
               width: rect.width, height: rect.height)
    }

    func local(_ rect: NSRect) -> LayoutRect {
        LayoutRect(x: rect.minX - work.minX, y: work.maxY - rect.maxY,
                   width: rect.width, height: rect.height)
    }
}

@MainActor
enum MacScreens {
    static var all: [MacScreen] { NSScreen.screens.map(MacScreen.init) }
    static var current: MacScreen? {
        let point = NSEvent.mouseLocation
        return all.first { $0.screen.frame.contains(point) }
            ?? NSScreen.main.map(MacScreen.init) ?? all.first
    }
    static func resolve(_ id: String?) -> MacScreen? { all.first { $0.id == id } ?? all.first }
    static func containing(_ frame: NSRect) -> MacScreen? {
        all.max { left, right in
            let a = left.work.intersection(frame), b = right.work.intersection(frame)
            return a.width * a.height < b.width * b.height
        }
    }
    /// Bounds are a best-effort fullscreen hint. No capture or event-tap permission is requested.
    static func fullscreenScreenIDs() -> Set<String> {
        guard let front = NSWorkspace.shared.frontmostApplication,
              front.processIdentifier != ProcessInfo.processInfo.processIdentifier,
              let primary = NSScreen.screens.first,
              let windows = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]] else { return [] }
        var covered = Set<String>()
        for info in windows {
            guard (info[kCGWindowOwnerPID as String] as? NSNumber)?.int32Value == front.processIdentifier,
                  (info[kCGWindowLayer as String] as? NSNumber)?.intValue == 0,
                  let data = info[kCGWindowBounds as String] as? [String: Any],
                  let bounds = CGRect(dictionaryRepresentation: data as CFDictionary) else { continue }
            for screen in all {
                let frame = screen.screen.frame
                let quartz = CGRect(x: frame.minX, y: primary.frame.maxY - frame.maxY, width: frame.width, height: frame.height)
                if bounds.minX <= quartz.minX + 2 && bounds.minY <= quartz.minY + 2 && bounds.maxX >= quartz.maxX - 2 && bounds.maxY >= quartz.maxY - 2 { covered.insert(screen.id) }
            }
        }
        return covered
    }

}
