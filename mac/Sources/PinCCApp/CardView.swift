import AppKit
import ImageIO
import PinCCCore
import PinCCMacPlatform

@MainActor
final class CardView: NSView {
    weak var panel: CardPanel?
    let scrollView = CardScrollView(frame: .zero)
    let document = CardDocumentView(frame: .zero)
    let editor = CardEditor(frame: .zero)
    private let status = CardStatusField(labelWithString: "")
    private let addPrefix = NSTextField(labelWithString: "□")
    private let addPlaceholder = NSTextField(labelWithString: "添加一项 · Enter 确认，Esc 结束")
    private var transientMessage: String?
    private var transientTask: Task<Void, Never>?
    private let undoTitle = NSTextField(labelWithString: "")
    private let undoButton = CardButton(title: "撤销")
    private let complete = CardButton(symbol: "circle", label: "完成卡片")
    private let pin = CardButton(symbol: "pin", label: "置顶")
    private let summarize = CardButton(symbol: "sparkles", label: "摘要")
    private let collapse = CardButton(symbol: "chevron.up", label: "折叠")
    private let more = CardButton(symbol: "ellipsis", label: "更多")
    private var area: NSTrackingArea?
    private var hoverTask: Task<Void, Never>?
    private var hovered = false
    private var undoMessage: String?
    private var summaryStatus: String?
    private var problem: String?
    private var image: NSImage?
    private var imagePath: String?
    private(set) var imagePixelSize: NSSize?
    private var palette = Palette.get(0)
    private var card = Card()

    override var isFlipped: Bool { true }
    override var acceptsFirstResponder: Bool { true }
    override var needsPanelToBecomeKey: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        wantsLayer = true
        layer?.cornerRadius = 8
        layer?.cornerCurve = .continuous
        layer?.masksToBounds = true
        scrollView.drawsBackground = false
        scrollView.hasVerticalScroller = false
        scrollView.hasHorizontalScroller = false
        scrollView.borderType = .noBorder
        scrollView.documentView = document
        addSubview(scrollView)
        addSubview(addPlaceholder)
        addSubview(addPrefix)
        addSubview(editor)
        editor.isHidden = true
        for view in [status, undoTitle, undoButton, complete, pin, summarize, collapse, more] { addSubview(view) }
        status.font = .systemFont(ofSize: 12)
        addPrefix.font = .systemFont(ofSize: 14)
        addPlaceholder.font = .systemFont(ofSize: 12)
        addPlaceholder.isHidden = true
        addPrefix.isHidden = true
        scrollView.intercept = { [weak self] event in
            guard event.modifierFlags.contains(.command) else { return false }
            self?.panel?.handleWheel(event, opacity: true)
            return true
        }
        status.lineBreakMode = .byTruncatingTail
        status.maximumNumberOfLines = 1
        undoTitle.font = .systemFont(ofSize: 12)
        undoTitle.lineBreakMode = .byTruncatingTail
        undoTitle.maximumNumberOfLines = 1
        undoTitle.isHidden = true
        undoButton.isHidden = true
        undoButton.handler = { [weak self] in self?.panel?.perform(.undo) }
        complete.handler = { [weak self] in self?.panel?.perform(.complete) }
        pin.handler = { [weak self] in
            guard let self else { return }
            self.panel?.perform(.setPinned(!self.card.topmost))
        }
        summarize.handler = { [weak self] in
            guard let self else { return }
            self.panel?.perform(self.summaryStatus == nil ? .summarize : .cancelSummary)
        }
        collapse.handler = { [weak self] in
            guard let self else { return }
            self.panel?.perform(.setCollapsed(!self.card.collapsed))
        }
        more.handler = { [weak self] in self?.showMenu() }
        document.edit = { [weak self] line, character in self?.panel?.beginEditing(line: line, character: character) }
        document.toggleTask = { [weak self] line in
            guard let self else { return }
            self.panel?.perform(.bodyChanged(Markdown.toggleTask(self.card.body, line: line)))
        }
        document.drag = { [weak self] event in self?.panel?.trackMouse(event) }
        document.source = { [weak self] in self?.panel?.perform(.openSource) }
        document.reflow = { [weak self] in
            guard let self else { return }
            self.needsLayout = true
            self.panel?.fitAutomaticHeight()
        }
        registerForDraggedTypes(Clipboard.acceptedTypes + NSFilePromiseReceiver.readableDraggedTypes.map(NSPasteboard.PasteboardType.init))
        setAccessibilityRole(.group)
        setAccessibilityLabel("悬浮卡片")
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) is unavailable") }

    func apply(card: Card, colorMode: String, imagePath: String?, imagePaths: [String], textSize: CGFloat) {
        self.card = card
        palette = Palette.get(card.shownColor(mode: colorMode))
        layer?.backgroundColor = palette.back.nsColor.cgColor
        appearance = NSAppearance(named: card.shownColor(mode: colorMode) == 8 ? .darkAqua : .aqua)
        status.textColor = card.overdue ? Palette.overdue.nsColor : palette.ink.nsColor.withAlphaComponent(0.6)
        undoTitle.textColor = palette.ink.nsColor
        document.configure(card: card, palette: palette, imagePaths: imagePaths, textSize: textSize)
        pin.image = NSImage(systemSymbolName: card.topmost ? "pin.fill" : "pin", accessibilityDescription: "置顶")
        collapse.image = NSImage(systemSymbolName: card.collapsed ? "chevron.down" : "chevron.up", accessibilityDescription: "折叠或展开")
        if self.imagePath != imagePath {
            self.imagePath = imagePath
            image = nil
            imagePixelSize = nil
            if let imagePath, let source = CGImageSourceCreateWithURL(URL(fileURLWithPath: imagePath) as CFURL, nil), MacImagePayload.safePixelSize(source) != nil,
               let cgImage = CGImageSourceCreateImageAtIndex(source, 0, nil) {
                // Raw pixel order: do not silently apply EXIF orientation to shared images.
                imagePixelSize = NSSize(width: CGFloat(cgImage.width), height: CGFloat(cgImage.height))
                image = NSImage(cgImage: cgImage, size: imagePixelSize!)
            }
        }
        setAccessibilityLabel(card.title.isEmpty ? "空白卡片" : card.title)
        needsLayout = true
        needsDisplay = true
    }

    func setUndo(_ message: String?) {
        undoMessage = message
        undoTitle.stringValue = message ?? ""
        undoTitle.isHidden = message == nil
        undoButton.isHidden = message == nil
        needsLayout = true
        needsDisplay = true
    }

    func setSummaryStatus(_ text: String?) {
        summaryStatus = text
        summarize.image = NSImage(systemSymbolName: text == nil ? "sparkles" : "xmark.circle", accessibilityDescription: text == nil ? "生成摘要" : "取消摘要")
        summarize.toolTip = text == nil ? "生成摘要" : "取消摘要"
        needsLayout = true
    }

    func setProblem(_ message: String?) { problem = message; needsLayout = true }
    func refreshFocus() { needsLayout = true; needsDisplay = true }

    func setEditing(_ editing: Bool, adding: Bool) {
        editor.isHidden = !editing
        scrollView.isHidden = editing && !adding
        needsLayout = true
    }

    var automaticHeight: CGFloat {
        let bodyHeight = document.measure(width: max(136, bounds.width - 24))
        let cap = min(360, (panel?.screen?.visibleFrame.height ?? 900) * 0.4)
        return max(64, min(cap, bodyHeight + 10 + 31))
    }

    override func layout() {
        super.layout()
        let adding = panel?.isAdding ?? false
        let busy = panel?.isEditing ?? false
        let editing = busy && !adding
        let strip = undoMessage != nil
        let pill = card.collapsed && !busy && !strip
        let imageOnly = card.imageOnly && !editing && !strip && !pill
        let controlsShown = hovered || (panel?.isKeyWindow ?? false) || editing
        let rowHeight: CGFloat = pill || strip ? 36 : 31
        let y = max(0, bounds.height - rowHeight)
        let ink = imageOnly ? NSColor.white : palette.ink.nsColor
        let footerVisible = !imageOnly || controlsShown
        for button in [complete, pin, summarize, collapse, more] { button.contentTintColor = ink }
        let footerX: CGFloat = 10
        let footerWidth = max(0, bounds.width - 20)
        let regularButtons: [CardButton] = card.imageOnly && bounds.width < 80 ? [more] : pill ? [complete, more] : bounds.width >= 250 ? [complete, pin, summarize, collapse, more] : bounds.width >= 200 ? [complete, pin, more] : [complete, more]
        let ready = card.progress.total > 0 && card.progress.done == card.progress.total
        for button in [complete, pin, summarize, collapse, more] { button.isHidden = true }
        var right = bounds.width - 8
        if !strip && footerVisible && !adding {
            for button in regularButtons.reversed() {
                right -= 24
                button.frame = NSRect(x: right, y: y + (rowHeight - 24) / 2, width: 24, height: 24)
                button.isHidden = !(controlsShown || (button === complete && ready))
            }
        }
        status.frame = NSRect(x: footerX, y: y + (rowHeight - 17) / 2, width: max(0, right - footerX - 5), height: 17)
        status.isHidden = strip || !footerVisible || adding
        status.textColor = imageOnly ? .white : card.overdue ? Palette.overdue.nsColor : palette.ink.nsColor.withAlphaComponent(0.65)
        var chips: [String] = []
        if let transientMessage { chips.append(transientMessage) }
        else if let problem { chips.append("保存失败 · " + problem) }
        else if let summaryStatus { chips.append(summaryStatus) }
        else {
            if let reminder = card.remindAt { chips.append((card.overdue ? "到期 " : "") + When.format(reminder)) }
            if card.progress.total > 0 { chips.append("\(card.progress.done)/\(card.progress.total)") }
            if card.topmost { chips.append("置顶") }
        }
        status.stringValue = chips.joined(separator: " · ")
        var statusDescription = status.stringValue
        if let at = card.remindAt {
            let formatter = DateFormatter()
            formatter.dateStyle = .full
            formatter.timeStyle = .short
            statusDescription = formatter.string(from: Date(timeIntervalSince1970: Double(at) / 1000)) + " · " + TimeZone.current.identifier + " · " + statusDescription
        }
        status.toolTip = statusDescription
        if card.progress.total > 0 && card.id > 0 && !pill && !strip {
            status.clicked = { [weak self] in self?.panel?.beginAdding() }
            statusDescription += " · 添加一项"
        } else if card.remindAt != nil && !pill && !strip {
            status.clicked = { [weak self] in self?.panel?.showReminder() }
            statusDescription += " · 设定提醒"
        } else { status.clicked = nil }
        status.setAccessibilityRole(status.clicked == nil ? .staticText : .button)
        status.setAccessibilityLabel(statusDescription)
        if strip {
            undoButton.frame = NSRect(x: bounds.width - 56, y: y + 6, width: 48, height: 24)
            undoTitle.frame = NSRect(x: 12, y: y + 10, width: max(0, footerWidth - 54), height: 18)
        }
        let contentFrame = NSRect(x: 12, y: 10, width: max(1, bounds.width - 24), height: max(1, y - 10))
        editor.frame = adding ? NSRect(x: 32, y: y + 3, width: max(1, bounds.width - 44), height: 25) : contentFrame
        editor.textView.frame.size.width = editor.frame.width
        addPrefix.isHidden = !adding
        addPrefix.frame = NSRect(x: 12, y: y + 5, width: 18, height: 22)
        addPrefix.textColor = palette.accent.nsColor
        addPlaceholder.isHidden = !adding || !editor.textView.string.isEmpty
        addPlaceholder.frame = NSRect(x: 33, y: y + 6, width: max(1, bounds.width - 45), height: 20)
        addPlaceholder.textColor = palette.ink.nsColor.withAlphaComponent(0.45)
        scrollView.frame = contentFrame
        scrollView.isHidden = editing || pill || strip || imageOnly
        if !scrollView.isHidden {
            let height = document.measure(width: contentFrame.width)
            document.frame = NSRect(x: 0, y: 0, width: contentFrame.width, height: max(height, contentFrame.height))
        }
        needsDisplay = true
    }

    override func draw(_ dirtyRect: NSRect) {
        super.draw(dirtyRect)
        if card.imageOnly && !(panel?.isEditing ?? false) && undoMessage == nil {
            if let image {
                let available = bounds
                let scale = min(available.width / image.size.width, available.height / image.size.height)
                let size = NSSize(width: image.size.width * scale, height: image.size.height * scale)
                image.draw(in: NSRect(x: (bounds.width - size.width) / 2, y: (bounds.height - size.height) / 2, width: size.width, height: size.height), from: .zero, operation: .sourceOver, fraction: 1, respectFlipped: true, hints: nil)
            } else {
                drawLabel("图片无法读取", rect: bounds.insetBy(dx: 12, dy: 10), color: palette.ink.nsColor.withAlphaComponent(0.6))
            }
            if !(more.isHidden && complete.isHidden) {
                NSColor.black.withAlphaComponent(0.62).setFill()
                NSBezierPath(roundedRect: NSRect(x: 5, y: bounds.height - 34, width: max(0, bounds.width - 10), height: 29), xRadius: 6, yRadius: 6).fill()
            }
        } else if card.collapsed && !(panel?.isEditing ?? false) && undoMessage == nil {
            let title = card.nextTask.map { card.titleIsTask ? $0 : card.title + " · " + $0 } ?? card.title
            drawLabel(title.isEmpty ? "（空）" : title, rect: NSRect(x: 12, y: 8, width: max(0, bounds.width - 75), height: 20), color: palette.ink.nsColor)
            status.isHidden = true
        }
        let border = NSBezierPath(roundedRect: bounds.insetBy(dx: 0.5, dy: 0.5), xRadius: 8, yRadius: 8)
        (card.overdue ? Palette.overdue.nsColor : (panel?.isKeyWindow ?? false) ? palette.accent.nsColor : palette.ink.nsColor.withAlphaComponent(0.15)).setStroke()
        border.lineWidth = card.overdue ? 2 : 1
        border.stroke()
    }

    private func drawLabel(_ text: String, rect: NSRect, color: NSColor) {
        let paragraph = NSMutableParagraphStyle()
        paragraph.lineBreakMode = .byTruncatingTail
        (text as NSString).draw(in: rect, withAttributes: [.font: NSFont.systemFont(ofSize: 14), .foregroundColor: color, .paragraphStyle: paragraph])
    }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let area { removeTrackingArea(area) }
        let next = NSTrackingArea(rect: bounds, options: [.mouseEnteredAndExited, .mouseMoved, .activeAlways, .inVisibleRect], owner: self)
        area = next
        addTrackingArea(next)
    }

    override func mouseEntered(with event: NSEvent) {
        hoverTask?.cancel()
        panel?.isHovering = true
        hoverTask = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(150))
            guard !Task.isCancelled, let self else { return }
            self.hovered = true
            self.needsLayout = true
        }
    }

    override func mouseExited(with event: NSEvent) {
        hoverTask?.cancel()
        panel?.isHovering = false
        hovered = false
        needsLayout = true
    }

    override func mouseMoved(with event: NSEvent) {
        let point = convert(event.locationInWindow, from: nil)
        if point.x < 6 || point.x > bounds.width - 6 { NSCursor.resizeLeftRight.set() }
        else if point.y < 6 || point.y > bounds.height - 6 { NSCursor.resizeUpDown.set() }
        else { NSCursor.arrow.set() }
    }

    override func hitTest(_ point: NSPoint) -> NSView? {
        let local = convert(point, from: superview)
        if local.x < 6 || local.x > bounds.width - 6 || local.y < 6 || local.y > bounds.height - 6 { return bounds.contains(local) ? self : nil }
        return super.hitTest(point)
    }

    override func mouseDown(with event: NSEvent) {
        if event.clickCount == 2 {
            let point = convert(event.locationInWindow, from: nil)
            if point.y > bounds.height - 6 && !card.imageOnly { panel?.perform(.autoHeight) }
            else { panel?.beginEditing() }
        } else { panel?.trackMouse(event) }
    }

    override func scrollWheel(with event: NSEvent) {
        let point = convert(event.locationInWindow, from: nil)
        if event.modifierFlags.contains(.command) || (!pin.isHidden && pin.frame.contains(point)) { panel?.handleWheel(event, opacity: true) }
        else if card.imageOnly { panel?.handleWheel(event, opacity: false) }
        else { scrollView.scrollWheel(with: event) }
    }

    override func smartMagnify(with event: NSEvent) { if card.imageOnly { panel?.actualImagePixels() } }
    override func otherMouseDown(with event: NSEvent) {
        if event.buttonNumber == 2 && card.imageOnly { panel?.actualImagePixels() }
        else { super.otherMouseDown(with: event) }
    }
    override func magnify(with event: NSEvent) { if card.imageOnly { panel?.zoomImage(by: 1 + event.magnification) } }
    override func keyDown(with event: NSEvent) { panel?.handleKey(event) }
    override func menu(for event: NSEvent) -> NSMenu? { buildMenu() }

    func showTransient(_ message: String) {
        transientTask?.cancel()
        transientMessage = message
        needsLayout = true
        transientTask = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(1200))
            guard !Task.isCancelled, let self else { return }
            self.transientMessage = nil
            self.needsLayout = true
            self.panel?.refreshDisplayOpacity()
        }
    }

    private func showMenu() {
        guard let event = NSApp.currentEvent else { return }
        NSMenu.popUpContextMenu(buildMenu(), with: event, for: more)
    }

    private func buildMenu() -> NSMenu {
        let menu = NSMenu()
        if card.overdue {
            for (title, minutes) in [("延后 10 分钟", Int64(10)), ("延后 1 小时", Int64(60))] {
                let item = CardMenuItem(title: title, action: #selector(menuAction(_:)), keyEquivalent: "")
                item.target = self
                item.cardAction = .setReminder(When.nowMs + minutes * 60_000)
                menu.addItem(item)
            }
        }
        let items: [(String, CardAction)] = [("编辑正文", .editingBegan), ("复制", .copy), ("粘贴", .paste), ("完成", .complete), ("删除", .delete), (card.topmost ? "取消置顶" : "置顶", .setPinned(!card.topmost)), (card.collapsed ? "展开" : "折叠", .setCollapsed(!card.collapsed)), (summaryStatus == nil ? "摘要 / 重新摘要" : "取消摘要", summaryStatus == nil ? .summarize : .cancelSummary), ("撤销", .undo)]
        for (title, action) in items {
            let item = CardMenuItem(title: title, action: #selector(menuAction(_:)), keyEquivalent: "")
            item.target = self
            item.cardAction = action
            menu.addItem(item)
        }
        if let matching = panel?.matchingSkills, !matching.isEmpty {
            let skills = NSMenu()
            for skill in matching {
                let item = CardMenuItem(title: skill.label, action: #selector(menuAction(_:)), keyEquivalent: "")
                item.target = self
                item.cardAction = .runSkill(skill.name)
                item.toolTip = skill.description
                skills.addItem(item)
            }
            let skillItem = NSMenuItem(title: "技能", action: nil, keyEquivalent: "")
            skillItem.submenu = skills
            menu.addItem(skillItem)
        }
        if let notice = panel?.skillsCatalogNotice {
            let item = NSMenuItem(title: "技能目录：" + notice.components(separatedBy: "\n")[0], action: nil, keyEquivalent: "")
            item.toolTip = notice
            item.isEnabled = false
            menu.addItem(item)
        }
        if card.progress.total > 0 && card.id > 0 {
            let add = NSMenuItem(title: "添加一项", action: #selector(addItem(_:)), keyEquivalent: "")
            add.target = self
            menu.addItem(add)
        }
        if card.src != nil {
            let item = CardMenuItem(title: card.isLink ? "打开来源" : "打开来源文件", action: #selector(menuAction(_:)), keyEquivalent: "")
            item.target = self
            item.cardAction = .openSource
            menu.addItem(item)
        }
        if card.free {
            let tile = CardMenuItem(title: "回到平铺", action: #selector(menuAction(_:)), keyEquivalent: "")
            tile.target = self
            tile.cardAction = .setFree(false)
            menu.addItem(tile)
        }
        menu.addItem(.separator())
        let colors = NSMenu()
        for (index, swatch) in Palette.all.enumerated() {
            let item = CardMenuItem(title: swatch.name, action: #selector(menuAction(_:)), keyEquivalent: "")
            item.target = self
            item.cardAction = .setColor(index)
            item.state = card.color == index ? .on : .off
            colors.addItem(item)
        }
        let auto = CardMenuItem(title: "自动配色", action: #selector(menuAction(_:)), keyEquivalent: "")
        auto.target = self
        auto.cardAction = .setAutoColor(true)
        auto.state = card.color == nil ? .on : .off
        colors.addItem(auto)
        let colorItem = NSMenuItem(title: "颜色", action: nil, keyEquivalent: "")
        colorItem.submenu = colors
        menu.addItem(colorItem)
        let opacityMenu = NSMenu()
        opacityMenu.autoenablesItems = false
        for value in [1.0, 0.9, 0.8, 0.7, 0.5] {
            let item = CardMenuItem(title: "\(Int((value * 100).rounded()))%", action: #selector(menuAction(_:)), keyEquivalent: "")
            item.target = self
            item.cardAction = .setOpacity(value)
            item.state = abs(card.opacity - value) < 0.001 ? .on : .off
            item.isEnabled = card.topmost
            opacityMenu.addItem(item)
        }
        let opacityItem = NSMenuItem(title: "透明度", action: nil, keyEquivalent: "")
        opacityItem.submenu = opacityMenu
        menu.addItem(opacityItem)
        let reminders = NSMenu()
        let reminderEditor = NSMenuItem(title: "设定提醒…", action: #selector(editReminder(_:)), keyEquivalent: "")
        reminderEditor.target = self
        reminders.addItem(reminderEditor)
        for preset in When.presets() {
            let item = CardMenuItem(title: preset.label, action: #selector(menuAction(_:)), keyEquivalent: "")
            item.target = self
            item.cardAction = .setReminder(preset.at)
            reminders.addItem(item)
        }
        let clear = CardMenuItem(title: "取消提醒", action: #selector(menuAction(_:)), keyEquivalent: "")
        clear.target = self
        clear.cardAction = .setReminder(nil)
        reminders.addItem(clear)
        let reminderItem = NSMenuItem(title: "提醒", action: nil, keyEquivalent: "")
        reminderItem.submenu = reminders
        menu.addItem(reminderItem)
        if card.imageOnly {
            menu.addItem(NSMenuItem(title: "实际像素", action: #selector(actualPixels(_:)), keyEquivalent: "0"))
            menu.items.last?.target = self
            menu.addItem(NSMenuItem(title: "适合屏幕", action: #selector(fitImage(_:)), keyEquivalent: ""))
            menu.items.last?.target = self
        }
        return menu
    }

    @objc private func menuAction(_ sender: CardMenuItem) {
        guard let action = sender.cardAction else { return }
        if case .editingBegan = action { panel?.beginEditing() }
        else { panel?.perform(action) }
    }
    @objc private func editReminder(_ sender: Any?) { panel?.showReminder() }
    @objc private func addItem(_ sender: Any?) { panel?.beginAdding() }
    @objc private func actualPixels(_ sender: Any?) { panel?.actualImagePixels() }
    @objc private func fitImage(_ sender: Any?) { panel?.fitImage() }

    override func draggingEntered(_ sender: any NSDraggingInfo) -> NSDragOperation { .copy }
    override func prepareForDragOperation(_ sender: any NSDraggingInfo) -> Bool { true }
    override func performDragOperation(_ sender: any NSDraggingInfo) -> Bool {
        panel?.perform(.drop(sender.draggingPasteboard))
        return true
    }
}

@MainActor
private final class CardMenuItem: NSMenuItem {
    var cardAction: CardAction?
}

@MainActor
final class CardButton: NSButton {
    var handler: (() -> Void)?
    init(title: String) {
        super.init(frame: .zero)
        self.title = title
        bezelStyle = .rounded
        target = self
        action = #selector(invoke(_:))
        setAccessibilityLabel(title)
    }
    convenience init(symbol: String, label: String) {
        self.init(title: "")
        image = NSImage(systemSymbolName: symbol, accessibilityDescription: label)
        imagePosition = .imageOnly
        isBordered = false
        toolTip = label
        setAccessibilityLabel(label)
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) is unavailable") }
    override var needsPanelToBecomeKey: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    @objc private func invoke(_ sender: Any?) { handler?() }
}

extension RGBColor {
    var nsColor: NSColor { NSColor(srgbRed: red, green: green, blue: blue, alpha: 1) }
}

/// Rendering only: source-line mapping remains attached to every body block.
@MainActor
final class CardDocumentView: NSView {
    var edit: ((Int, Int) -> Void)?
    var toggleTask: ((Int) -> Void)?
    var drag: ((NSEvent) -> Void)?
    var source: (() -> Void)?
    var reflow: (() -> Void)?
    private var card = Card()
    private var palette = Palette.get(0)
    private var showDone = false
    private var rows: [TextRow] = []
    private var checkboxes: [CardButton] = []
    private var foldedCount = 0
    private var foldedRect = NSRect.zero
    private var measuredWidth: CGFloat = -1
    private var imagePaths: [String] = []
    private var sourceLines: [String] = []
    private var textSize: CGFloat = 14
    private var gallery: [NSView] = []
    override var isFlipped: Bool { true }
    override var needsPanelToBecomeKey: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    func configure(card: Card, palette: Swatch, imagePaths: [String], textSize: CGFloat) {
        self.card = card
        self.palette = palette
        self.imagePaths = imagePaths
        self.textSize = textSize
        sourceLines = card.body.components(separatedBy: "\n")
        measuredWidth = -1
        needsDisplay = true
    }

    @discardableResult
    func measure(width: CGFloat) -> CGFloat {
        if measuredWidth == width { return rows.last?.rect.maxY ?? 21 }
        measuredWidth = width
        for box in checkboxes { box.removeFromSuperview() }
        for view in gallery { view.removeFromSuperview() }
        gallery.removeAll()
        checkboxes.removeAll()
        rows.removeAll()
        var y: CGFloat = 0
        func append(_ text: String, sourceLine: Int, kind: BlockKind = .paragraph, size: CGFloat? = nil, alpha: CGFloat = 1, inset: CGFloat = 0, summary: Bool = false) {
            let attributed = styled(text, size: size ?? textSize, kind: kind, alpha: alpha)
            let row = TextRow(attributed: attributed, line: sourceLine, kind: kind, summary: summary)
            if sourceLine >= 0 { row.mapSource(sourceLines, startingAt: sourceLine) }
            let height = row.measure(width: max(1, width - inset))
            row.rect = NSRect(x: inset, y: y, width: max(1, width - inset), height: height)
            rows.append(row)
            y += height + (kind == .task || kind == .bullet || kind == .ordered ? 2 : 8)
        }
        if !card.imageOnly && !imagePaths.isEmpty {
            let count = imagePaths.count == 1 ? 1 : min(imagePaths.count, max(1, Int((width + 6) / 102)))
            let itemWidth = (width - CGFloat(count - 1) * 6) / CGFloat(count)
            var imageHeight: CGFloat = imagePaths.count == 1 ? 150 : itemWidth
            for index in 0..<count {
                let imageView = NSImageView(frame: .zero)
                if let imageSource = CGImageSourceCreateWithURL(URL(fileURLWithPath: imagePaths[index]) as CFURL, nil), MacImagePayload.safePixelSize(imageSource) != nil, let pixels = CGImageSourceCreateImageAtIndex(imageSource, 0, nil) {
                    let imageSize = NSSize(width: CGFloat(pixels.width), height: CGFloat(pixels.height))
                    imageView.image = NSImage(cgImage: pixels, size: imageSize)
                    if count == 1 { imageHeight = min(150, itemWidth * imageSize.height / imageSize.width) }
                }
                imageView.imageScaling = .scaleProportionallyUpOrDown
                imageView.frame = NSRect(x: CGFloat(index) * (itemWidth + 6), y: y, width: itemWidth, height: imageHeight)
                imageView.setAccessibilityLabel("图片 \(index + 1)")
                addSubview(imageView)
                gallery.append(imageView)
                if index == count - 1 && imagePaths.count > count {
                    let badge = NSTextField(labelWithString: "+\(imagePaths.count - count)")
                    badge.textColor = .white
                    badge.drawsBackground = true
                    badge.backgroundColor = .black.withAlphaComponent(0.6)
                    badge.frame = NSRect(x: imageView.frame.maxX - 36, y: y + imageHeight - 22, width: 36, height: 22)
                    addSubview(badge)
                    gallery.append(badge)
                }
            }
            y += imageHeight + 8
        }
        if let src = card.src { append(card.title + "\n" + src, sourceLine: 0, size: 12, alpha: 0.7) }
        if let summary = card.summary, !summary.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            for block in Markdown.blocks(String(summary.prefix(12_000))) {
                append(block.text, sourceLine: -1, kind: block.kind, alpha: card.summaryStale ? 0.55 : 1, inset: 10, summary: true)
            }
        }
        if (card.summary?.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ?? true), let info = card.srcInfo {
            if info.state == "blocked" { append("正文不可获取 · 仅标题/描述", sourceLine: -1, size: 12, alpha: 0.65) }
            if let description = info.desc, !description.isEmpty {
                let preview = description.components(separatedBy: "\n").map { Markdown.plain($0) }.filter { !$0.isEmpty }.joined(separator: " ")
                append(String(preview.prefix(360)), sourceLine: -1, size: 13, alpha: 0.65)
            }
        }
        let blocks = Markdown.blocks(String(card.note.prefix(12_000)))
        let folded = Markdown.foldDone(blocks)
        foldedCount = folded == nil ? 0 : blocks.enumerated().filter { folded!.contains($0.offset) && $0.element.kind == .task && $0.element.checked }.count
        for (index, block) in blocks.enumerated() {
            if !showDone && (folded?.contains(index) ?? false) { continue }
            let line = block.line + card.noteLine + (block.kind == .code ? 1 : 0)
            var inset = CGFloat(block.indent) * 16
            var text = block.text
            var size: CGFloat = textSize
            switch block.kind {
            case .heading: size = block.level <= 1 ? textSize + 4 : block.level == 2 ? textSize + 2 : textSize
            case .bullet: text = (block.indent == 0 ? "• " : "◦ ") + text
            case .ordered: text = block.marker + " " + text
            case .task: inset += 22
            case .quote: inset += 10
            case .rule: text = "―"
            case .image: text = "图片：" + block.marker
            default: break
            }
            append(text, sourceLine: line, kind: block.kind, size: size, alpha: block.checked ? 0.55 : 1, inset: inset)
            if block.kind == .task, let row = rows.last {
                let box = CardButton(title: "")
                box.setButtonType(.switch)
                box.state = block.checked ? .on : .off
                box.frame = NSRect(x: inset - 22, y: row.rect.minY, width: 18, height: 21)
                box.setAccessibilityLabel(Markdown.plain(block.text))
                box.handler = { [weak self] in self?.toggleTask?(line) }
                addSubview(box)
                checkboxes.append(box)
            }
        }
        foldedRect = .zero
        if foldedCount > 0 {
            let text = showDone ? "⌄ 收起已完成的 \(foldedCount) 项" : "› 已完成 \(foldedCount) 项"
            append(text, sourceLine: -2, size: 12.5, alpha: 0.65)
            foldedRect = rows.last?.rect ?? .zero
        }
        if rows.isEmpty { append("双击输入…", sourceLine: 0, alpha: 0.55) }
        setAccessibilityRole(.group)
        setAccessibilityLabel(rows.map { $0.storage.string }.joined(separator: "\n"))
        needsDisplay = true
        return max(21, y - 8)
    }

    override func draw(_ dirtyRect: NSRect) {
        for row in rows where row.rect.intersects(dirtyRect) {
            if row.kind == .code {
                palette.ink.nsColor.withAlphaComponent(0.06).setFill()
                NSBezierPath(roundedRect: row.rect.insetBy(dx: -3, dy: -2), xRadius: 3, yRadius: 3).fill()
            }
            if row.kind == .quote || row.summary {
                palette.accent.nsColor.withAlphaComponent(row.summary ? 1 : 0.5).setFill()
                NSBezierPath(rect: NSRect(x: row.rect.minX - 8, y: row.rect.minY, width: 2, height: row.rect.height)).fill()
            }
            let range = row.manager.glyphRange(for: row.container)
            row.manager.drawBackground(forGlyphRange: range, at: row.rect.origin)
            row.manager.drawGlyphs(forGlyphRange: range, at: row.rect.origin)
        }
    }

    override func mouseDown(with event: NSEvent) {
        let point = convert(event.locationInWindow, from: nil)
        if foldedRect.contains(point) { showDone.toggle(); measuredWidth = -1; reflow?(); return }
        guard let row = rows.first(where: { $0.rect.contains(point) }) else { drag?(event); return }
        if event.clickCount == 2 && row.line >= 0 {
            let character = row.manager.characterIndex(for: NSPoint(x: point.x - row.rect.minX, y: point.y - row.rect.minY), in: row.container, fractionOfDistanceBetweenInsertionPoints: nil)
            let sourcePosition = row.sourcePosition(character: character)
            edit?(sourcePosition.line, sourcePosition.column)
        } else if row.line == 0 && card.src != nil && point.y < rows[0].rect.maxY {
            source?()
        } else {
            let character = row.manager.characterIndex(for: NSPoint(x: point.x - row.rect.minX, y: point.y - row.rect.minY), in: row.container, fractionOfDistanceBetweenInsertionPoints: nil)
            if character < row.storage.length, let link = row.storage.attribute(.link, at: character, effectiveRange: nil) as? String, let url = URL(string: link), ["http", "https", "mailto"].contains(url.scheme?.lowercased() ?? "") {
                NSWorkspace.shared.open(url)
            } else { drag?(event) }
        }
    }

    private func styled(_ text: String, size: CGFloat, kind: BlockKind, alpha: CGFloat) -> NSAttributedString {
        let result = NSMutableAttributedString(string: "")
        let paragraph = NSMutableParagraphStyle()
        paragraph.minimumLineHeight = (size * 1.5).rounded()
        paragraph.maximumLineHeight = (size * 1.5).rounded()
        for segment in (kind == .code ? [Seg(text: text)] : Markdown.inlines(text)) {
            var font = segment.fmt.contains(.code) || kind == .code ? NSFont.monospacedSystemFont(ofSize: size - 1, weight: .regular) : NSFont.systemFont(ofSize: size, weight: segment.fmt.contains(.bold) || kind == .heading ? .semibold : .regular)
            if segment.fmt.contains(.italic) { font = NSFontManager.shared.convert(font, toHaveTrait: .italicFontMask) }
            var attributes: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: palette.ink.nsColor.withAlphaComponent(alpha), .paragraphStyle: paragraph]
            if segment.fmt.contains(.strike) { attributes[.strikethroughStyle] = NSUnderlineStyle.single.rawValue }
            if segment.fmt.contains(.code) { attributes[.backgroundColor] = palette.ink.nsColor.withAlphaComponent(0.06) }
            if let url = segment.url {
                attributes[.foregroundColor] = palette.accent.nsColor.withAlphaComponent(alpha)
                if !url.hasPrefix("#") { attributes[.link] = url }
            }
            result.append(NSAttributedString(string: segment.text, attributes: attributes))
        }
        return result
    }
}

@MainActor
private final class TextRow {
    let storage: NSTextStorage
    let manager = NSLayoutManager()
    let container = NSTextContainer(size: NSSize(width: 1, height: CGFloat.greatestFiniteMagnitude))
    let line: Int
    let kind: BlockKind
    let summary: Bool
    var rect = NSRect.zero
    private var sourceMap: [(line: Int, column: Int)] = []
    init(attributed: NSAttributedString, line: Int, kind: BlockKind, summary: Bool) {
        storage = NSTextStorage(attributedString: attributed)
        self.line = line
        self.kind = kind
        self.summary = summary
        container.lineFragmentPadding = 0
        storage.addLayoutManager(manager)
        manager.addTextContainer(container)
    }
    func mapSource(_ lines: [String], startingAt line: Int) {
        guard line >= 0 && line < lines.count else { return }
        let neededLines = storage.string.components(separatedBy: "\n").count
        let source = Array(lines.dropFirst(line).prefix(neededLines).joined(separator: "\n").utf16)
        let displayed = Array(storage.string.utf16)
        var offset = 0
        var currentLine = line
        var column = 0
        sourceMap = []
        for unit in displayed {
            let match = source[offset...].firstIndex(of: unit) ?? offset
            while offset < match {
                if source[offset] == 10 { currentLine += 1; column = 0 }
                else { column += 1 }
                offset += 1
            }
            sourceMap.append((currentLine, column))
            if offset < source.count && source[offset] == unit {
                if unit == 10 { currentLine += 1; column = 0 }
                else { column += 1 }
                offset += 1
            }
        }
        sourceMap.append((currentLine, column))
    }
    func sourcePosition(character: Int) -> (line: Int, column: Int) {
        guard !sourceMap.isEmpty else { return (line, character) }
        return sourceMap[min(character, sourceMap.count - 1)]
    }
    func measure(width: CGFloat) -> CGFloat {
        container.containerSize.width = width
        manager.ensureLayout(for: container)
        return max(21, ceil(manager.usedRect(for: container).height))
    }
}









@MainActor
final class CardScrollView: NSScrollView {
    var intercept: ((NSEvent) -> Bool)?
    override func scrollWheel(with event: NSEvent) {
        if intercept?(event) != true { super.scrollWheel(with: event) }
    }
}

@MainActor
private final class CardStatusField: NSTextField {
    var clicked: (() -> Void)?
    override var needsPanelToBecomeKey: Bool { true }
    override var acceptsFirstResponder: Bool { clicked != nil }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func mouseDown(with event: NSEvent) {
        if let clicked { clicked() }
        else { super.mouseDown(with: event) }
    }
    override func keyDown(with event: NSEvent) {
        if event.keyCode == 36 || event.keyCode == 49 { clicked?() }
        else { super.keyDown(with: event) }
    }
}


