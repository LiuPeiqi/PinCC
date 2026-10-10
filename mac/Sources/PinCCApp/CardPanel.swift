import AppKit
import QuartzCore
import PinCCCore
import PinCCMacPlatform

/// UI intent only; the desktop controller owns Store operations and undo groups.
@MainActor
enum CardAction {
    case draftChanged(String)
    case bodyChanged(String)
    case editingBegan
    case editingEnded
    case complete
    case delete
    case move
    case dragging
    case settled
    case resize
    case autoHeight
    case summarize
    case cancelSummary
    case runSkill(String)
    case undo
    case redo
    case openSource
    case setPinned(Bool)
    case setOpacity(Double)
    case setFree(Bool)
    case setCollapsed(Bool)
    case setColor(Int)
    case setAutoColor(Bool)
    case setReminder(Int64?)
    case copy
    case paste
    case drop(NSPasteboard)
    case nextCard
    case previousCard
}

@MainActor
protocol CardPanelDelegate: AnyObject {
    var skillsCatalog: SkillsCatalog? { get }
    var skillsCatalogNotice: String? { get }
    func cardPanel(_ panel: CardPanel, perform action: CardAction)
}

/// Each card is its own nonactivating desktop panel, never a dashboard child.
@MainActor
final class CardPanel: NSPanel, NSWindowDelegate, NSDraggingSource {
    private(set) var card: Card
    var cardID: Int64 { card.id }
    private(set) var isEditing = false
    private(set) var isAdding = false
    var isHovering = false { didSet { updateOpacity(); if !isHovering { settleIfInactive() } } }
    private(set) var isDragging = false
    private(set) var lastUserStartFrame = NSRect.zero
    private var isResizing = false
    private var isSystemDragging = false
    private var wheelRemainder: CGFloat = 0
    private var wheelOpacityMode = false
    private var opacityPreviewUntil = Date.distantPast
    private var reminderPanel: CardReminderPanel?
    private var systemDragTask: Task<Void, Never>?
    private let store: Store
    weak var cardDelegate: (any CardPanelDelegate)?
    let cardView = CardView(frame: .zero)
    private var saveTask: Task<Void, Never>?
    private var saveSucceeded = true
    private var needsSaveConfirmation = false
    private var lastSavedBody: String
    private var undoVisible = false
    private var temporaryCollapsed: Bool?
    private var temporaryWidth: CGFloat?
    private var zoomScale: CGFloat = 1
    private var actualPixelsMode = false
    private var colorMode = "rotate"
    private var textSize: CGFloat = 14
    private var suppressAutoHeight = false
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { false }

    init(card: Card, store: Store, delegate: any CardPanelDelegate) {
        self.card = card
        self.store = store
        cardDelegate = delegate
        lastSavedBody = card.body
        super.init(contentRect: NSRect(x: 0, y: 0, width: max(160, card.w), height: max(64, card.h ?? 120)), styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        self.delegate = self
        contentView = cardView
        cardView.panel = self
        cardView.autoresizingMask = [.width, .height]
        isOpaque = false
        backgroundColor = .clear
        hasShadow = true
        hidesOnDeactivate = false
        becomesKeyOnlyIfNeeded = false
        isExcludedFromWindowsMenu = true
        isReleasedWhenClosed = false
        isRestorable = false
        animationBehavior = .none
        acceptsMouseMovedEvents = true
        // Ordinary Spaces only. fullScreenAuxiliary would invite cards into
        // another application's exclusive full-screen Space.
        collectionBehavior = [.canJoinAllSpaces, .ignoresCycle]
        minSize = NSSize(width: 160, height: 64)
        cardView.editor.changed = { [weak self] body in self?.editorChanged(body) }
        cardView.editor.submittedLine = { [weak self] in self?.submitAddedLine() }
        cardView.editor.commandScrolled = { [weak self] event in self?.handleWheel(event, opacity: true) }
        cardView.editor.committed = { [weak self] in self?.commitEditingAndReleaseFocus() }
        cardView.editor.ingestPasteboard = { [weak self] board in self?.perform(.drop(board)) }
        apply(card: card)
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) is unavailable") }

    func apply(card: Card) {
        self.card = card
        if !isEditing { lastSavedBody = card.body }
        level = card.topmost || card.overdue ? .floating : .normal
        if card.overdue { collectionBehavior.insert(.stationary) }
        else { collectionBehavior.remove(.stationary) }
        updateOpacity()
        var displayed = card
        if let temporaryCollapsed { displayed.collapsed = temporaryCollapsed }
        if card.imageOnly || (displayed.collapsed && !isEditing && !card.overdue) { clearTemporaryWidth() }
        colorMode = (try? store.setting("color.mode", fallback: "rotate")) ?? "rotate"
        textSize = CGFloat(max(12, min(20, Double((try? store.setting("text.size", fallback: "14")) ?? "14") ?? 14)))
        let firstImage = card.images.first.map { store.resolveImage($0) }
        cardView.apply(card: displayed, colorMode: colorMode, imagePath: firstImage, imagePaths: card.images.map { store.resolveImage($0) }, textSize: textSize)
        cardView.setEditing(isEditing, adding: isAdding)
        minSize = displayed.imageOnly ? NSSize(width: 48, height: 48) : NSSize(width: 160, height: displayed.collapsed && !isEditing ? 36 : 64)
        invalidateShadow()
        settleIfInactive()
    }

    func refreshDisplayOpacity() { updateOpacity() }

    private func updateOpacity() {
        let full = !card.topmost || card.overdue || isEditing || NSWorkspace.shared.accessibilityDisplayShouldReduceTransparency || ((isHovering || isKeyWindow) && Date() > opacityPreviewUntil)
        alphaValue = full ? 1 : CGFloat(max(0.2, min(1, card.opacity)))
    }

    func applyTemporaryCollapsed(_ collapsed: Bool) {
        temporaryCollapsed = collapsed
        apply(card: card)
    }

    var isPill: Bool { (temporaryCollapsed ?? card.collapsed) && !isEditing && !card.overdue && !undoVisible }
    var hasTemporaryWidth: Bool { temporaryWidth != nil }
    var layoutWidth: Double { card.imageOnly && isPill ? 48 : hasTemporaryWidth ? Double(frame.width) : card.w }

    func holdTemporaryWidth() {
        guard !card.topmost, !card.imageOnly, !isPill else { return }
        temporaryWidth = temporaryWidth ?? CGFloat(card.w)
        cardView.needsLayout = true
        cardView.layoutSubtreeIfNeeded()
    }

    func clearTemporaryWidth(restore: Bool = false) {
        guard temporaryWidth != nil else { return }
        temporaryWidth = nil
        guard restore else { return }
        var rect = frame
        rect.size.width = max(160, CGFloat(card.w))
        setFrame(rect, display: true)
        fitAutomaticHeight()
    }

    func settleIfInactive() {
        guard !card.topmost, !isKeyWindow, !isHovering, !isEditing, !isDragging, reminderPanel?.isVisible != true else { return }
        cardView.scrollView.contentView.scroll(to: .zero)
        cardView.scrollView.reflectScrolledClipView(cardView.scrollView.contentView)
        let widened = hasTemporaryWidth
        clearTemporaryWidth(restore: true)
        if widened { perform(.settled) }
    }

    var layoutHeight: Double {
        if undoVisible { return 36 }
        if isEditing && !isAdding { return Double(editorHeight) }
        if (temporaryCollapsed ?? card.collapsed) && !isEditing { return card.imageOnly ? 48 : 36 }
        if card.imageOnly, let pixels = cardView.imagePixelSize { return Double(frame.width * pixels.height / pixels.width) }
        return card.h ?? Double(cardView.automaticHeight)
    }

    func perform(_ action: CardAction) {
        switch action {
        case .complete, .delete, .setCollapsed:
            guard commitEditing() else { return }
        default: break
        }
        cardDelegate?.cardPanel(self, perform: action)
    }

    func beginEditing() { beginEditing(line: nil, character: 0) }

    func beginEditing(line: Int?, character: Int) {
        guard !undoVisible else { return }
        if isEditing && !isAdding { makeKey(); makeFirstResponder(cardView.editor.textView); return }
        guard commitEditing() else { return }
        isEditing = true
        isAdding = false
        cardView.editor.singleLine = false
        saveSucceeded = true
        cardDelegate?.cardPanel(self, perform: .editingBegan)
        var selection: NSRange?
        if let line {
            let lines = card.body.components(separatedBy: "\n")
            if line < lines.count {
                let start = lines.prefix(line).reduce(0) { $0 + ($1 as NSString).length + 1 }
                selection = NSRange(location: start + min(character, (lines[line] as NSString).length), length: 0)
            }
        }
        temporaryCollapsed = nil
        // A wider editor is a temporary presentation change for ordinary cards.
        // Keep the same temporary width marker used by an ordinary wide card.
        if !card.topmost && frame.width < 320 {
            holdTemporaryWidth()
            var rect = frame
            rect.size.width = min(320, screen?.visibleFrame.width ?? 320)
            if let visible = screen?.visibleFrame { rect.origin.x = min(max(rect.minX, visible.minX), visible.maxX - rect.width) }
            setFrame(rect, display: true)
        }
        apply(card: card)
        fitAutomaticHeight()
        makeKey()
        cardView.layoutSubtreeIfNeeded()
        cardView.editor.begin(body: card.body, selection: selection, ink: Palette.get(card.shownColor(mode: colorMode)).ink.nsColor, accent: Palette.get(card.shownColor(mode: colorMode)).accent.nsColor, textSize: textSize)
        fitAutomaticHeight()
    }

    /// Persist the current buffer while keeping this native editing session open.
    @discardableResult
    func flushEditing() -> Bool {
        guard isEditing else { return true }
        guard !cardView.editor.textView.hasMarkedText() else { return false }
        saveTask?.cancel()
        if isAdding { return saveAddedInput() }
        saveBody(normalizedCommit(cardView.editor.textView.string))
        return saveSucceeded
    }

    /// A paste/drop initiated by the user joins NSTextView's selection and undo.
    @discardableResult
    func insertEditorText(_ text: String) -> Bool {
        guard isEditing, !cardView.editor.textView.hasMarkedText() else { return false }
        let textView = cardView.editor.textView
        textView.insertText(text, replacementRange: textView.selectedRange())
        return true
    }

    @discardableResult
    func commitEditing() -> Bool {
        guard isEditing else { return true }
        guard !cardView.editor.textView.hasMarkedText() else { return false }
        saveTask?.cancel()
        if isAdding {
            guard saveAddedInput() else { return false }
        } else {
            let body = normalizedCommit(cardView.editor.textView.string)
            saveBody(body)
        }
        guard saveSucceeded else { return false }
        isEditing = false
        isAdding = false
        cardView.editor.singleLine = false
        cardDelegate?.cardPanel(self, perform: .editingEnded)
        makeFirstResponder(cardView)
        settleIfInactive()
        apply(card: card)
        fitAutomaticHeight()
        return true
    }

    private func commitEditingAndReleaseFocus() {
        guard commitEditing(), isVisible else { return }
        // NSPanel has no public "give up key" action. Reordering relinquishes
        // key through WindowServer without calling the resignKey notification.
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        orderOut(nil)
        orderFrontRegardless()
        CATransaction.commit()
    }

    func showReminder() {
        if let reminderPanel { reminderPanel.makeKeyAndOrderFront(nil); return }
        let picker = CardReminderPanel(parent: self)
        reminderPanel = picker
        addChildWindow(picker, ordered: .above)
        var rect = picker.frame
        rect.origin = NSPoint(x: frame.minX, y: frame.minY - rect.height - 8)
        if let work = screen?.visibleFrame {
            if rect.minY < work.minY { rect.origin.y = min(work.maxY - rect.height, frame.maxY + 8) }
            rect.origin.x = max(work.minX, min(rect.minX, work.maxX - rect.width))
        }
        picker.setFrame(rect, display: true)
        picker.makeKeyAndOrderFront(nil)
        picker.focusInput()
    }

    func dismissReminder(resume: Bool) {
        guard let picker = reminderPanel else { return }
        reminderPanel = nil
        removeChildWindow(picker)
        picker.close()
        if resume {
            makeKey()
            makeFirstResponder(isEditing ? cardView.editor.textView : cardView)
        } else { _ = commitEditing() }
    }

    func beginAdding() {
        guard !undoVisible, cardID > 0 else { return }
        if isAdding { makeKey(); makeFirstResponder(cardView.editor.textView); return }
        guard commitEditing() else { return }
        isEditing = true
        isAdding = true
        saveSucceeded = true
        cardView.editor.singleLine = true
        temporaryCollapsed = nil
        cardDelegate?.cardPanel(self, perform: .editingBegan)
        apply(card: card)
        makeKey()
        cardView.layoutSubtreeIfNeeded()
        let swatch = Palette.get(card.shownColor(mode: colorMode))
        cardView.editor.begin(body: "", selection: nil, ink: swatch.ink.nsColor, accent: swatch.accent.nsColor, textSize: textSize)
    }

    private func submitAddedLine() {
        guard isAdding, !cardView.editor.textView.hasMarkedText() else { return }
        if cardView.editor.textView.string.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            commitEditingAndReleaseFocus()
        } else { _ = saveAddedInput() }
    }

    @discardableResult
    private func saveAddedInput() -> Bool {
        guard isAdding, !cardView.editor.textView.hasMarkedText() else { return false }
        let input = cardView.editor.textView.string
        if input.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            saveBody(card.body)
            return saveSucceeded
        }
        saveBody(Markdown.addTasks(card.body, text: input))
        guard saveSucceeded else { return false }
        cardView.editor.textView.string = ""
        cardView.editor.textView.undoManager?.removeAllActions()
        fitAutomaticHeight()
        return true
    }

    func handleWheel(_ event: NSEvent, opacity: Bool) {
        if wheelOpacityMode != opacity { wheelRemainder = 0; wheelOpacityMode = opacity }
        let delta = event.scrollingDeltaY
        guard delta != 0 else {
            if event.phase == .ended || event.phase == .cancelled { wheelRemainder = 0 }
            return
        }
        if event.hasPreciseScrollingDeltas {
            wheelRemainder += delta
            let threshold: CGFloat = 20
            while abs(wheelRemainder) >= threshold {
                let direction: CGFloat = wheelRemainder > 0 ? 1 : -1
                wheelRemainder -= threshold * direction
                applyWheelStep(direction, opacity: opacity)
            }
            if event.phase == .ended || event.phase == .cancelled { wheelRemainder = 0 }
        } else { applyWheelStep(delta > 0 ? 1 : -1, opacity: opacity) }
    }

    private func applyWheelStep(_ direction: CGFloat, opacity: Bool) {
        if opacity {
            guard card.topmost else { cardView.showTransient("置顶后可调透明度"); return }
            let value = (max(0.2, min(1, card.opacity + Double(direction) * 0.1)) * 10).rounded() / 10
            opacityPreviewUntil = Date().addingTimeInterval(1.2)
            perform(.setOpacity(value))
            cardView.showTransient("透明度 \(Int((value * 100).rounded()))%")
            updateOpacity()
        } else if card.imageOnly && !(temporaryCollapsed ?? card.collapsed) {
            zoomImage(by: direction > 0 ? 1.1 : 1 / 1.1)
        }
    }

    private func normalizedCommit(_ body: String) -> String {
        var result = body.replacingOccurrences(of: "\r\n", with: "\n")
        while result.last?.isWhitespace == true { result.removeLast() }
        return result
    }

    private func editorChanged(_ body: String) {
        needsSaveConfirmation = true
        if isAdding {
            cardView.needsLayout = true
            cardDelegate?.cardPanel(self, perform: .draftChanged(Markdown.addTasks(card.body, text: body)))
            if body.contains("\n") { _ = saveAddedInput() }
            return
        }
        fitAutomaticHeight()
        cardDelegate?.cardPanel(self, perform: .draftChanged(body))
        saveTask?.cancel()
        saveTask = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(500))
            guard !Task.isCancelled, let self, self.isEditing, !self.cardView.editor.textView.hasMarkedText() else { return }
            self.saveBody(self.cardView.editor.textView.string)
        }
    }

    private func saveBody(_ body: String) {
        // An editing buffer may have returned to the last saved text after a failed save.
        // Confirm its revision and clear only its draft through the normal save path.
        guard needsSaveConfirmation || !saveSucceeded || !body.utf16.elementsEqual(lastSavedBody.utf16) else { return }
        saveSucceeded = true
        cardDelegate?.cardPanel(self, perform: .bodyChanged(body))
        if saveSucceeded { lastSavedBody = body; needsSaveConfirmation = false }
    }

    func markSaveResult(success: Bool, message: String? = nil) {
        saveSucceeded = success
        cardView.setProblem(success ? nil : (message ?? "请重试，草稿已保留"))
    }

    var skillsCatalogNotice: String? { cardDelegate?.skillsCatalogNotice }

    var matchingSkills: [Skill] {
        guard let catalog = cardDelegate?.skillsCatalog else { return [] }
        let best = catalog.best(for: card)
        let askName = best?.on.contains("ask") == true ? best?.name : nil
        return catalog.skills.filter { $0.scope == "card" && $0.matches(card) && $0.name != askName }.sorted {
            if $0.specificity != $1.specificity { return $0.specificity > $1.specificity }
            if $0.builtin != $1.builtin { return !$0.builtin }
            return $0.name < $1.name
        }
    }

    func setSummaryStatus(_ status: String?) { cardView.setSummaryStatus(status) }

    func showUndo(_ title: String) {
        undoVisible = true
        minSize = NSSize(width: 160, height: 36)
        cardView.setUndo(title)
        var rect = frame
        rect.origin.y = rect.maxY - 36
        rect.size.height = 36
        setFrame(rect, display: true)
        invalidateShadow()
    }

    func clearUndo() {
        undoVisible = false
        cardView.setUndo(nil)
        apply(card: card)
        fitAutomaticHeight()
    }

    private var editorHeight: CGFloat {
        let cap = min(360, (screen?.visibleFrame.height ?? 900) * 0.4)
        return max(120, min(cap, cardView.editor.contentHeight + 45))
    }

    func fitAutomaticHeight() {
        guard !suppressAutoHeight, !undoVisible, !card.imageOnly else { return }
        let height: CGFloat
        if (temporaryCollapsed ?? card.collapsed) && !isEditing { height = 36 }
        else if let storedHeight = card.h, !isEditing || isAdding { height = CGFloat(storedHeight) }
        else { height = isEditing && !isAdding ? editorHeight : cardView.automaticHeight }
        guard abs(height - frame.height) > 0.5 else { return }
        var rect = frame
        rect.origin.y = rect.maxY - height
        rect.size.height = height
        setFrame(rect, display: true)
        invalidateShadow()
    }

    override func sendEvent(_ event: NSEvent) {
        if event.type == .leftMouseDown && !isKeyWindow && !event.modifierFlags.contains(.command) { makeKey() }
        super.sendEvent(event)
    }

    override func performClose(_ sender: Any?) {}
    override func performMiniaturize(_ sender: Any?) {}

    override func keyDown(with event: NSEvent) { handleKey(event) }

    func handleKey(_ event: NSEvent) {
        if isEditing { super.keyDown(with: event); return }
        let modifiers = event.modifierFlags.intersection(.deviceIndependentFlagsMask)
        let characters = event.charactersIgnoringModifiers?.lowercased() ?? ""
        if modifiers.contains(.command) {
            switch characters {
            case "c": perform(.copy)
            case "v": perform(.paste)
            case "z": perform(modifiers.contains(.shift) ? .redo : .undo)
            case "0": actualImagePixels()
            case "`", "~": perform(modifiers.contains(.shift) ? .previousCard : .nextCard)
            case "w": break
            case "\u{7f}", "\u{8}": perform(.delete)
            default: super.keyDown(with: event)
            }
        } else if event.keyCode == 36 || event.keyCode == 76 { beginEditing() }
        else { super.keyDown(with: event) }
    }

    func windowDidResignKey(_ notification: Notification) {
        if reminderPanel?.isVisible == true { return }
        // Native child panels are part of the card's focus scope.
        if let key = NSApp.keyWindow, childWindows?.contains(key) == true { return }
        _ = commitEditing()
        apply(card: card)
        cardView.refreshFocus()
        settleIfInactive()
    }
    func windowDidBecomeKey(_ notification: Notification) { apply(card: card); cardView.refreshFocus() }
    func windowDidMove(_ notification: Notification) {
        if isSystemDragging && isDragging { cardDelegate?.cardPanel(self, perform: .dragging) }
    }
    func windowDidChangeBackingProperties(_ notification: Notification) {
        cardView.needsDisplay = true
        if card.imageOnly && actualPixelsMode { actualImagePixels() }
    }

    /// Geometry tracking does not change activation or synthesize keyboard events.
    /// The final action is emitted once per completed drag/resize.
    func trackMouse(_ event: NSEvent) {
        if card.imageOnly && event.modifierFlags.contains(.option) { dragImageOut(event); return }
        if event.modifierFlags.contains(.command) {
            let original = frame
            lastUserStartFrame = original
            isResizing = false
            isSystemDragging = true
            isDragging = true
            performDrag(with: event)
            systemDragTask?.cancel()
            systemDragTask = Task { [weak self] in
                while NSEvent.pressedMouseButtons & 1 != 0 {
                    try? await Task.sleep(for: .milliseconds(50))
                    if Task.isCancelled { return }
                }
                guard let self else { return }
                self.isDragging = false
                self.isSystemDragging = false
                if self.frame != original { self.perform(.move) }
            }
            return
        }
        let local = cardView.convert(event.locationInWindow, from: nil)
        let initial = frame
        lastUserStartFrame = initial
        let start = convertPoint(toScreen: event.locationInWindow)
        let left = local.x <= 6
        let right = local.x >= initial.width - 6
        let top = local.y <= 6
        let bottom = local.y >= initial.height - 6
        let resizing = !undoVisible && (left || right || top || bottom)
        isResizing = resizing
        isSystemDragging = false
        var changed = false
        suppressAutoHeight = true
        isDragging = true
        defer { suppressAutoHeight = false; isDragging = false }
        while let next = nextEvent(matching: [.leftMouseDragged, .leftMouseUp], until: .distantFuture, inMode: .eventTracking, dequeue: true) {
            if next.type == .leftMouseUp { break }
            let cursor = convertPoint(toScreen: next.locationInWindow)
            let dx = cursor.x - start.x
            let dy = cursor.y - start.y
            guard changed || hypot(dx, dy) >= 3 else { continue }
            var rect = initial
            if resizing {
                if left { rect.origin.x += dx; rect.size.width -= dx }
                if right { rect.size.width += dx }
                if bottom { rect.origin.y += dy; rect.size.height -= dy }
                if top { rect.size.height += dy }
                let minimum: CGFloat = card.imageOnly ? 48 : 160
                if rect.width < minimum { if left { rect.origin.x = initial.maxX - minimum }; rect.size.width = minimum }
                let minimumHeight: CGFloat = card.imageOnly ? 48 : (card.collapsed ? 36 : 64)
                if rect.height < minimumHeight { if bottom { rect.origin.y = initial.maxY - minimumHeight }; rect.size.height = minimumHeight }
                if card.imageOnly, let pixels = cardView.imagePixelSize {
                    rect.size.height = rect.width * pixels.height / pixels.width
                    if bottom { rect.origin.y = initial.maxY - rect.height }
                }
            } else { rect.origin.x += dx; rect.origin.y += dy }
            setFrame(rect, display: true)
            if !resizing { perform(.dragging) }
            changed = true
        }
        if changed {
            isDragging = false
            suppressAutoHeight = false
            perform(resizing ? .resize : .move)
            invalidateShadow()
        }
    }

    func actualImagePixels() {
        guard card.imageOnly, let pixels = cardView.imagePixelSize else { return }
        actualPixelsMode = true
        zoomScale = 1
        resizeImage(size: NSSize(width: pixels.width / backingScaleFactor, height: pixels.height / backingScaleFactor))
    }

    func fitImage() {
        guard card.imageOnly, let pixels = cardView.imagePixelSize else { return }
        actualPixelsMode = false
        let room = screen?.visibleFrame.size ?? NSSize(width: 1000, height: 800)
        let factor = min(room.width * 0.6 / pixels.width, room.height * 0.6 / pixels.height)
        zoomScale = factor * backingScaleFactor
        resizeImage(size: NSSize(width: pixels.width * factor, height: pixels.height * factor))
    }

    func zoomImage(by factor: CGFloat) {
        guard card.imageOnly, factor > 0 else { return }
        actualPixelsMode = false
        zoomScale *= factor
        resizeImage(size: NSSize(width: max(48, frame.width * factor), height: max(48, frame.height * factor)))
    }

    private func resizeImage(size: NSSize) {
        lastUserStartFrame = frame
        var rect = frame
        rect.origin.y = rect.maxY - size.height
        rect.size = size
        if let visible = screen?.visibleFrame {
            rect.origin.x = min(max(rect.minX, visible.minX), visible.maxX - min(rect.width, visible.width))
            rect.origin.y = min(max(rect.minY, visible.minY), visible.maxY - min(rect.height, visible.height))
        }
        setFrame(rect, display: true)
        perform(.resize)
        if let pixels = cardView.imagePixelSize {
            cardView.showTransient("比例 \(Int((frame.width * backingScaleFactor / pixels.width * 100).rounded()))%")
        }
    }

    private func dragImageOut(_ event: NSEvent) {
        guard let path = card.images.first else { return }
        let url = URL(fileURLWithPath: store.resolveImage(path))
        guard FileManager.default.fileExists(atPath: url.path) else { return }
        let item = NSDraggingItem(pasteboardWriter: url as NSURL)
        let image = NSImage(contentsOf: url) ?? NSImage(size: NSSize(width: 48, height: 48))
        item.setDraggingFrame(cardView.bounds, contents: image)
        cardView.beginDraggingSession(with: [item], event: event, source: self)
    }

    func draggingSession(_ session: NSDraggingSession, sourceOperationMaskFor context: NSDraggingContext) -> NSDragOperation { .copy }
}














/// A native child panel shares the card's focus scope. Its text system uses
/// exactly the same IME-safe editor as the card; When remains the only parser.
@MainActor
private final class CardReminderPanel: NSPanel, NSWindowDelegate {
    private weak var parentCard: CardPanel?
    private let input = CardEditor(frame: .zero)
    private let preview = NSTextField(wrappingLabelWithString: "")
    private let applyButton = CardButton(title: "设定")
    private var candidate: Int64?
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { false }

    init(parent: CardPanel) {
        parentCard = parent
        super.init(contentRect: NSRect(x: 0, y: 0, width: 300, height: 248), styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        delegate = self
        isReleasedWhenClosed = false
        hidesOnDeactivate = false
        becomesKeyOnlyIfNeeded = false
        isOpaque = false
        backgroundColor = .clear
        hasShadow = true
        level = .floating
        animationBehavior = .none
        collectionBehavior = [.canJoinAllSpaces, .ignoresCycle]
        let view = CardReminderView(frame: NSRect(x: 0, y: 0, width: 300, height: 248))
        view.wantsLayer = true
        view.layer?.cornerRadius = 8
        view.layer?.cornerCurve = .continuous
        view.layer?.backgroundColor = NSColor.windowBackgroundColor.cgColor
        view.layer?.borderColor = NSColor.separatorColor.cgColor
        view.layer?.borderWidth = 1
        contentView = view
        let title = NSTextField(labelWithString: "提醒")
        title.font = .systemFont(ofSize: 14, weight: .semibold)
        title.frame = NSRect(x: 14, y: 214, width: 230, height: 20)
        view.addSubview(title)
        let close = CardButton(symbol: "xmark", label: "关闭提醒选择器")
        close.frame = NSRect(x: 260, y: 212, width: 26, height: 26)
        close.handler = { [weak self] in self?.parentCard?.dismissReminder(resume: true) }
        view.addSubview(close)
        input.frame = NSRect(x: 14, y: 176, width: 272, height: 28)
        input.singleLine = true
        input.changed = { [weak self] value in self?.interpret(value) }
        input.submittedLine = { [weak self] in self?.applyReminder() }
        input.committed = { [weak self] in self?.parentCard?.dismissReminder(resume: true) }
        input.ingestPasteboard = nil
        input.setAccessibilityLabel("提醒时间，例如明天上午9点")
        view.addSubview(input)
        preview.font = .systemFont(ofSize: 12)
        preview.textColor = .secondaryLabelColor
        preview.frame = NSRect(x: 14, y: 122, width: 272, height: 44)
        view.addSubview(preview)
        for (index, preset) in When.presets().prefix(4).enumerated() {
            let button = CardButton(title: preset.label)
            button.frame = NSRect(x: 14 + CGFloat(index % 2) * 138, y: 86 - CGFloat(index / 2) * 32, width: 132, height: 27)
            button.handler = { [weak self] in
                guard let self else { return }
                self.parentCard?.perform(.setReminder(preset.at))
                self.parentCard?.dismissReminder(resume: true)
            }
            view.addSubview(button)
        }
        let clear = CardButton(title: "取消提醒")
        clear.frame = NSRect(x: 14, y: 14, width: 90, height: 28)
        clear.handler = { [weak self] in
            self?.parentCard?.perform(.setReminder(nil))
            self?.parentCard?.dismissReminder(resume: true)
        }
        view.addSubview(clear)
        applyButton.frame = NSRect(x: 204, y: 14, width: 82, height: 28)
        applyButton.handler = { [weak self] in self?.applyReminder() }
        view.addSubview(applyButton)
        setAccessibilityLabel("设置卡片提醒")
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) is unavailable") }

    func focusInput() {
        var initial = ""
        if let at = parentCard?.card.remindAt {
            let formatter = DateFormatter()
            formatter.locale = Locale(identifier: "en_US_POSIX")
            formatter.dateFormat = "yyyy-MM-dd HH:mm"
            initial = formatter.string(from: Date(timeIntervalSince1970: Double(at) / 1000))
        }
        input.layoutSubtreeIfNeeded()
        input.begin(body: initial, selection: NSRange(location: 0, length: (initial as NSString).length), ink: .labelColor, accent: .controlAccentColor, textSize: 14)
        interpret(initial)
    }

    private func interpret(_ value: String) {
        guard !input.textView.hasMarkedText() else { return }
        candidate = When.parse(value.replacingOccurrences(of: "\n", with: " "))
        applyButton.isEnabled = candidate != nil
        if let candidate {
            let formatter = DateFormatter()
            formatter.dateStyle = .full
            formatter.timeStyle = .short
            preview.stringValue = formatter.string(from: Date(timeIntervalSince1970: Double(candidate) / 1000)) + "\n" + TimeZone.current.identifier
            preview.textColor = .secondaryLabelColor
        } else {
            preview.stringValue = value.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? "输入时间，例如：明天 9:00、2 小时后。" : "暂时无法识别；请输入日期与时间。"
            preview.textColor = value.isEmpty ? .secondaryLabelColor : .systemRed
        }
    }

    private func applyReminder() {
        guard !input.textView.hasMarkedText(), let candidate else { return }
        parentCard?.perform(.setReminder(candidate))
        parentCard?.dismissReminder(resume: true)
    }

    func windowDidResignKey(_ notification: Notification) {
        guard let parentCard else { return }
        parentCard.dismissReminder(resume: parentCard.isKeyWindow)
    }
}

@MainActor
private final class CardReminderView: NSView {
    override var needsPanelToBecomeKey: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
}

