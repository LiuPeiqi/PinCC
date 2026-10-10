import AppKit
import PinCCDesk

@MainActor
final class HubPanel: NSPanel {
    let screenID: String
    private let hubView: HubView
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }

    init(screen: MacScreen, desktop: DesktopController) {
        screenID = screen.id
        hubView = HubView(desktop: desktop, screenID: screen.id)
        super.init(contentRect: NSRect(x: 0, y: 0, width: 40, height: 154),
                   styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        isFloatingPanel = true
        level = .floating
        hidesOnDeactivate = false
        isOpaque = false
        backgroundColor = .clear
        hasShadow = false
        isReleasedWhenClosed = false
        collectionBehavior = [.canJoinAllSpaces, .fullScreenNone, .ignoresCycle]
        contentView = hubView
        reposition(screen: screen, corner: desktop.hubCorner)
    }

    func reposition(screen: MacScreen, corner: String) {
        let left = corner.hasSuffix("l"), bottom = corner.hasPrefix("b")
        let work = screen.work
        setFrame(NSRect(x: left ? work.minX + 16 : work.maxX - 56,
                        y: bottom ? work.minY + 16 : work.maxY - 170,
                        width: 40, height: 154), display: true)
        hubView.bottom = bottom
        hubView.layoutButtons()
    }

    var obstacle: LayoutRect {
        guard let screen = MacScreens.resolve(screenID) else { return LayoutRect() }
        // The shared layout reserves the speed dial even while it is closed.
        return screen.local(frame)
    }

    func refresh(hidden: Int, due: Int, hotkeyProblem: String?) {
        hubView.refresh(hidden: hidden, due: due, hotkeyProblem: hotkeyProblem)
    }
}

@MainActor
private final class HubView: NSView {
    weak var desktop: DesktopController?
    let screenID: String
    var bottom = true
    private let plus = HubButton(title: "+", target: nil, action: nil)
    private let badge = NSTextField(labelWithString: "")
    private var dial: [NSButton] = []
    private var hoverIn: Timer?
    private var hoverOut: Timer?
    private var tracking: NSTrackingArea?

    init(desktop: DesktopController, screenID: String) {
        self.desktop = desktop
        self.screenID = screenID
        super.init(frame: NSRect(x: 0, y: 0, width: 40, height: 154))
        plus.font = .systemFont(ofSize: 24)
        plus.isBordered = false
        plus.wantsLayer = true
        plus.layer?.cornerRadius = 20
        plus.layer?.backgroundColor = NSColor.controlBackgroundColor.withAlphaComponent(0.95).cgColor
        plus.layer?.borderColor = NSColor.separatorColor.cgColor
        plus.layer?.borderWidth = 1
        plus.target = self
        plus.action = #selector(create)
        plus.contextMenu = { [weak self] in self?.desktop?.menu(screenID: self?.screenID) }
        plus.dragEnded = { [weak self] point in self?.desktop?.moveHub(to: point) }
        plus.setAccessibilityLabel("新建卡片")
        addSubview(plus)
        let specs: [(String, Selector, String)] = [
            ("▦", #selector(tile), "平铺本屏卡片"),
            ("↕", #selector(collapse), "折叠或展开本屏卡片"),
            ("−", #selector(hide), "隐藏或显示本屏普通卡片")
        ]
        for (title, action, tip) in specs {
            let button = NSButton(title: title, target: self, action: action)
            button.isBordered = false
            button.wantsLayer = true
            button.layer?.cornerRadius = 16
            button.layer?.backgroundColor = NSColor.controlBackgroundColor.cgColor
            button.toolTip = tip
            button.setAccessibilityLabel(tip)
            button.isHidden = true
            dial.append(button)
            addSubview(button)
        }
        badge.font = .systemFont(ofSize: 10, weight: .semibold)
        badge.alignment = .center
        badge.textColor = .white
        badge.wantsLayer = true
        badge.layer?.cornerRadius = 8
        badge.layer?.backgroundColor = NSColor.controlAccentColor.cgColor
        badge.isHidden = true
        addSubview(badge)
        registerForDraggedTypes(Clipboard.acceptedTypes + NSFilePromiseReceiver.readableDraggedTypes.map(NSPasteboard.PasteboardType.init))
        layoutButtons()
    }

    required init?(coder: NSCoder) { return nil }

    func layoutButtons() {
        plus.frame = NSRect(x: 0, y: bottom ? 0 : 114, width: 40, height: 40)
        badge.frame = NSRect(x: 0, y: plus.frame.maxY - 13, width: 27, height: 16)
        for (index, button) in dial.enumerated() {
            button.frame = NSRect(x: 4, y: bottom ? 44 + CGFloat(index) * 38 : 76 - CGFloat(index) * 38,
                                  width: 32, height: 32)
        }
    }

    func refresh(hidden: Int, due: Int, hotkeyProblem: String?) {
        badge.stringValue = hotkeyProblem == nil ? (hidden > 0 ? "+\(hidden)" : "") : "!"
        badge.isHidden = badge.stringValue.isEmpty
        plus.contentTintColor = due > 0 ? .systemRed : .labelColor
        plus.toolTip = "新建卡片；新复制的图片只采集一次\n悬停：本屏平铺、折叠、隐藏；右键：更多"
            + (hidden > 0 ? "\n\(hidden) 张卡片暂时隐藏，可在卡片库查看" : "")
            + (hotkeyProblem.map { "\n快捷键：\($0)" } ?? "")
    }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let tracking { removeTrackingArea(tracking) }
        tracking = NSTrackingArea(rect: bounds, options: [.mouseEnteredAndExited, .activeAlways], owner: self)
        if let tracking { addTrackingArea(tracking) }
    }

    override func mouseEntered(with event: NSEvent) {
        hoverOut?.invalidate()
        hoverIn?.invalidate()
        hoverIn = Timer(timeInterval: 0.3, target: self, selector: #selector(expand), userInfo: nil, repeats: false)
        if let hoverIn { RunLoop.main.add(hoverIn, forMode: .common) }
    }

    override func mouseExited(with event: NSEvent) {
        hoverIn?.invalidate()
        hoverOut?.invalidate()
        hoverOut = Timer(timeInterval: 0.35, target: self, selector: #selector(shrink), userInfo: nil, repeats: false)
        if let hoverOut { RunLoop.main.add(hoverOut, forMode: .common) }
    }

    @objc private func expand() { dial.forEach { $0.isHidden = false } }
    @objc private func shrink() { dial.forEach { $0.isHidden = true } }
    @objc private func create() { desktop?.newCard(screenID: screenID, collectImage: true) }
    @objc private func tile() { desktop?.tile(screenID: screenID) }
    @objc private func collapse() { desktop?.collapse(screenID: screenID) }
    @objc private func hide() { desktop?.toggleHidden(screenID: screenID) }

    override func rightMouseDown(with event: NSEvent) {
        guard let menu = desktop?.menu(screenID: screenID) else { return }
        NSMenu.popUpContextMenu(menu, with: event, for: self)
    }
    override func draggingEntered(_ sender: any NSDraggingInfo) -> NSDragOperation { .copy }
    override func prepareForDragOperation(_ sender: any NSDraggingInfo) -> Bool { true }
    override func performDragOperation(_ sender: any NSDraggingInfo) -> Bool {
        desktop?.ingest(sender.draggingPasteboard, screenID: screenID)
        return true
    }
}

@MainActor
private final class HubButton: NSButton {
    var contextMenu: (() -> NSMenu?)?
    var dragEnded: ((NSPoint) -> Void)?
    override func rightMouseDown(with event: NSEvent) {
        if let menu = contextMenu?() { NSMenu.popUpContextMenu(menu, with: event, for: self) }
    }
    override func mouseDown(with event: NSEvent) {
        guard let window else { return }
        let start = NSEvent.mouseLocation, origin = window.frame.origin
        var moved = false
        while let next = NSApp.nextEvent(matching: [.leftMouseDragged, .leftMouseUp],
                                         until: .distantFuture, inMode: .eventTracking, dequeue: true) {
            let point = NSEvent.mouseLocation
            if next.type == .leftMouseDragged {
                let dx = point.x - start.x, dy = point.y - start.y
                if abs(dx) + abs(dy) > 4 { moved = true }
                if moved { window.setFrameOrigin(NSPoint(x: origin.x + dx, y: origin.y + dy)) }
            } else {
                if moved { dragEnded?(point) } else { _ = sendAction(action, to: target) }
                return
            }
        }
    }
}
