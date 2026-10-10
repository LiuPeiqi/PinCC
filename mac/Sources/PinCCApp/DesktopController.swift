import AppKit
import Dispatch
import PinCCCore
import PinCCCli
import PinCCDesk
import PinCCMacPlatform

/// The sole GUI owner of operation groups, reminder delivery and card layout.
@MainActor
final class DesktopController: NSResponder, CardPanelDelegate, NSMenuDelegate {
    let store: Store
    private let clipboard: Clipboard
    private let hotkeys = Hotkeys()
    private let summaries: SummaryService
    private var panels: [Int64: CardPanel] = [:]
    private var hubs: [String: HubPanel] = [:]
    private var library: LibraryWindow?
    private var settings: SettingsWindow?
    private var timer: Timer?
    private var checkpointTimer: Timer?
    private var layoutTimer: Timer?
    private var version: Int64 = 0
    private var nextTemporaryID: Int64 = -1
    private var hiddenScreens = Set<String>()
    private var rung = Set<String>()
    private var undoGroups: [Int64] = []
    private var redoGroups: [Int64] = []
    private var sessions: [Int64: EditSession] = [:]
    private var drafts: [Int64: EditDraft] = [:]
    private var checkpointPath: String?
    private var strips: [Int64: UndoStrip] = [:]
    private var stopping = false
    private var shortcutRecording = false
    private var lastMinute: Int64 = -1
    private var previewDragID: Int64?
    private var peeks: [Int64: Date] = [:]
    private var coveredScreens = Set<String>()
    private var presentedDue = Set<String>()
    private var soundSnooze: Int64 = 0
    private var ticks = 0
    private var sourceSignatures: [Int64: String] = [:]
    private var sourceTasks: [Int64: Task<Void, Never>] = [:]
    private var pendingSources: [Int64: Bool] = [:]
    private var fileRefreshTask: Task<Void, Never>?
    private var maintenanceTask: Task<Void, Error>?
    private var maintenanceDay: String?
    private(set) var skillsCatalog: SkillsCatalog?
    private var skillsCatalogTask: Task<Void, Never>?
    private var skillsCatalogGeneration: UInt64 = 0
    private var skillsCatalogLoading = true
    private var skillsCatalogError: String?
    private nonisolated static let skillsCatalogQueue = DispatchQueue(label: "PinCC.skills-catalog", qos: .utility)
    private var defaultPin: [String: Bool] = [:]
    private var gestureGroup: Group?
    private var gestureAt: Date = .distantPast
    private var lastError: String?
    private var recoveryCount = 0
    private var recoveryFailureCount = 0
    private(set) var hubCorner = "br"
    private var tileCorner = "TopRight"
    private var gap: Double = 12
    private var sound = true
    private var autoSummary = true
    private var hasRunner = false
    private var deskRunning = false
    private var deskStatus: String?
    private(set) var hotkeyProblem: String?

    private struct EditSession { let group: Group; let key: String; let title: String; let isNew: Bool }
    private struct UndoStrip { let group: Int64; let frame: NSRect; var deadline: Date }

    init(store: Store) throws {
        self.store = store
        clipboard = Clipboard(store: store)
        summaries = SummaryService(store: store)
        super.init()
        try loadSettings()
    }
    required init?(coder: NSCoder) { return nil }

    func start() {
        refreshSkillsCatalog()
        do { version = try store.dataVersion; try reload() } catch { reportError(error) }
        rebuildHubs(); registerHotkey(); relayout()
        timer = Timer(timeInterval: 1, target: self, selector: #selector(tick), userInfo: nil, repeats: true)
        timer?.tolerance = 0.05
        if let timer { RunLoop.main.add(timer, forMode: .common) }
        NotificationCenter.default.addObserver(self, selector: #selector(screensChanged), name: NSApplication.didChangeScreenParametersNotification, object: nil)
        NSWorkspace.shared.notificationCenter.addObserver(self, selector: #selector(woke), name: NSWorkspace.didWakeNotification, object: nil)
        NotificationCenter.default.addObserver(self, selector: #selector(woke), name: NSNotification.Name.NSSystemClockDidChange, object: nil)
        NotificationCenter.default.addObserver(self, selector: #selector(woke), name: NSNotification.Name.NSSystemTimeZoneDidChange, object: nil)
        NSWorkspace.shared.notificationCenter.addObserver(self, selector: #selector(woke), name: NSWorkspace.activeSpaceDidChangeNotification, object: nil)
        NSWorkspace.shared.notificationCenter.addObserver(self, selector: #selector(woke), name: NSWorkspace.didActivateApplicationNotification, object: nil)
        detectFirstRunner()
        maintenance()
        tick()
    }

    func prepareToTerminate() -> Bool {
        _ = settings?.window?.makeFirstResponder(nil)
        let saved = Array(panels.values).map { $0.commitEditing() }.allSatisfy { $0 }
        do { try persistDrafts() } catch { reportError(error); return false }
        return saved
    }

    func stop() {
        stopping = true
        timer?.invalidate(); checkpointTimer?.invalidate(); layoutTimer?.invalidate()
        hotkeys.shutdown()
        NotificationCenter.default.removeObserver(self)
        NSWorkspace.shared.notificationCenter.removeObserver(self)
        sourceTasks.values.forEach { $0.cancel() }; fileRefreshTask?.cancel(); maintenanceTask?.cancel(); skillsCatalogTask?.cancel()
        summaries.cancelAll()
        panels.values.forEach { $0.close() }; hubs.values.forEach { $0.close() }
        store.close()
    }

    private func loadSettings() throws {
        gap = min(40, max(4, Double(try store.setting("tile.gap", fallback: "12")) ?? 12))
        hubCorner = try store.setting("hub.corner", fallback: "br")
        let corner = try store.setting("tile.corner", fallback: "tr")
        tileCorner = ["tl": "TopLeft", "tr": "TopRight", "bl": "BottomLeft", "br": "BottomRight"][corner] ?? "TopRight"
        sound = try store.settingBool("sound", fallback: true)
        autoSummary = try store.settingBool("llm.auto", fallback: true)
        hasRunner = !(try store.setting("mac.llm.tool")).isEmpty
        soundSnooze = Int64(try store.setting("mac.sound.snooze_until")) ?? 0
        for screen in MacScreens.all { defaultPin[screen.id] = try store.settingBool("mac.top:" + screen.id, fallback: false) }
        _ = try store.setting("color.mode", fallback: "rotate")
        _ = try store.setting("text.size", fallback: "14")
    }

    func applySettings() {
        do {
            try loadSettings(); registerHotkey(); rebuildHubs()
            refreshSkillsCatalog()
            panels.values.forEach { $0.apply(card: $0.card) }
            relayout()
        } catch { reportError(error) }
    }

    private func registerHotkey() {
        guard !shortcutRecording else { return }
        do {
            hotkeyProblem = hotkeys.registerNew(setting: try store.setting("mac.hk.new", fallback: Hotkeys.defaultSetting)) { [weak self] in
                self?.newCard(screenID: MacScreens.current?.id, collectImage: true)
            }
        } catch { hotkeyProblem = error.localizedDescription; reportError(error) }
    }

    private func adopt(_ card: Card) -> CardPanel {
        let panel = CardPanel(card: card, store: store, delegate: self)
        panels[card.id] = panel
        return panel
    }
    private func remove(_ id: Int64) {
        guard let panel = panels.removeValue(forKey: id) else { return }
        summaries.cancel(cardID: id); sourceTasks[id]?.cancel(); pendingSources.removeValue(forKey: id); sourceSignatures.removeValue(forKey: id)
        strips.removeValue(forKey: id); sessions.removeValue(forKey: id)
        panel.close()
    }

    private func reload() throws {
        let active = try store.active()
        let ids = Set(active.map(\.id))
        for card in active {
            if let panel = panels[card.id] {
                // External CLI writes must not overwrite a live editor buffer.
                if !panel.isEditing { panel.apply(card: card) }
            } else { _ = adopt(card) }
            if card.src != nil && !panels[card.id]!.isEditing { scheduleSource(card) }
        }
        for (id, panel) in panels where id > 0 && !ids.contains(id) && strips[id] == nil && !panel.isEditing { remove(id) }
        library?.refresh()
    }

    @objc private func tick() {
        guard !stopping else { return }
        var needsLayout = false
        do {
            let latest = try store.dataVersion
            if latest != version { version = latest; try reload(); try loadSettings(); registerHotkey(); needsLayout = true }
        } catch { reportError(error) }
        var expired: [Int64] = []
        for (id, entry) in strips {
            var strip = entry
            if panels[id]?.isHovering == true { strip.deadline = Date().addingTimeInterval(4); strips[id] = strip }
            else if strip.deadline <= Date() { expired.append(id) }
        }
        expired.forEach(remove)
        let expiredPeeks = peeks.filter { $0.value <= Date() && panels[$0.key]?.isHovering != true && panels[$0.key]?.isKeyWindow != true }
        expiredPeeks.keys.forEach { peeks.removeValue(forKey: $0) }
        needsLayout = needsLayout || !expiredPeeks.isEmpty
        needsLayout = needsLayout || !expired.isEmpty
        let covered = MacScreens.fullscreenScreenIDs()
        if covered != coveredScreens {
            coveredScreens = covered; needsLayout = true
            for panel in panels.values {
                panel.apply(card: panel.card)
                if covered.contains(MacScreens.resolve(panel.card.mon)?.id ?? "") { panel.level = .normal }
            }
            for (id, hub) in hubs { if covered.contains(id) { hub.orderOut(nil) } else { hub.orderFrontRegardless() } }
        }
        var newDue = false, ring = false
        for panel in panels.values where panel.card.active && panel.card.overdue {
            guard let at = panel.card.remindAt else { continue }
            let key = "\(panel.cardID):\(at)", covered = coveredScreens.contains(MacScreens.resolve(panel.card.mon)?.id ?? "")
            if presentedDue.insert(key).inserted { newDue = true; panel.apply(card: panel.card); if !covered { panel.orderFrontRegardless() } }
            if covered { panel.level = .normal }
            if !covered && soundSnooze <= When.nowMs && rung.insert(key).inserted { ring = true }
        }
        if ring && sound { NSSound.beep() }
        let minute = When.nowMs / 60_000
        if minute != lastMinute {
            lastMinute = minute
            for panel in panels.values where panel.card.remindAt != nil {
                panel.apply(card: panel.card)
                if coveredScreens.contains(MacScreens.resolve(panel.card.mon)?.id ?? "") { panel.level = .normal }
            }
        }
        if needsLayout || newDue { relayout() }
        ticks += 1
        if ticks % 15 == 0 { refreshFileSources() }
        if ticks % 30 == 0 && skillsCatalogTask == nil { refreshSkillsCatalog() }
        if ticks % 60 == 0 { maintenance() }
    }
    @objc private func woke(_ notification: Notification) { tick() }
    @objc private func screensChanged(_ notification: Notification) { rebuildHubs(); relayout() }

    private func rebuildHubs() {
        let screens = MacScreens.all
        let ids = Set(screens.map(\.id))
        for (id, hub) in hubs where !ids.contains(id) { hub.close(); hubs.removeValue(forKey: id) }
        for screen in screens {
            if let hub = hubs[screen.id] { hub.reposition(screen: screen, corner: hubCorner) }
            else { hubs[screen.id] = HubPanel(screen: screen, desktop: self) }
            if !coveredScreens.contains(screen.id) { hubs[screen.id]?.orderFrontRegardless() }
        }
    }
    private func requestLayout() {
        layoutTimer?.invalidate()
        layoutTimer = Timer(timeInterval: 0.1, target: self, selector: #selector(layoutTick), userInfo: nil, repeats: false)
        if let layoutTimer { RunLoop.main.add(layoutTimer, forMode: .common) }
    }
    @objc private func layoutTick() {
        layoutTimer = nil
        if let id = previewDragID, let panel = panels[id], panel.isDragging { previewDrag(panel) }
        else { relayout() }
    }

    private func relayout() {
        guard !stopping else { return }
        for screen in MacScreens.all {
            let members = panels.values.filter { MacScreens.resolve($0.card.mon)?.id == screen.id }.sorted { Card.compare($0.card, $1.card) }
            let hub = hubs[screen.id]?.obstacle ?? LayoutRect()
            var freeRects: [Int64: LayoutRect] = [:], freeObstacles = [hub]
            for panel in members where panel.card.free && !panel.isEditing && !panel.isDragging && !panel.hasTemporaryWidth && strips[panel.cardID] == nil {
                panel.applyTemporaryCollapsed(panel.card.collapsed && !panel.card.overdue)
                let saved = LayoutRect(x: panel.card.x, y: panel.card.y, width: panel.layoutWidth, height: panel.layoutHeight)
                let displayed = Layout.pushOut(saved, obstacles: freeObstacles, area: screen.area, gap: gap)
                freeRects[panel.cardID] = displayed; freeObstacles.append(displayed)
            }
            let items = members.map { panel -> LayoutItem in
                if let strip = strips[panel.cardID] {
                    let rect = screen.local(strip.frame)
                    return LayoutItem(id: panel.cardID, width: rect.width, height: rect.height, protected: true, free: true, x: rect.x, y: rect.y)
                }
                if panel.isEditing || panel.isDragging || panel.hasTemporaryWidth || peeks[panel.cardID] != nil {
                    let rect = screen.local(panel.frame)
                    return LayoutItem(id: panel.cardID, width: rect.width, height: rect.height, protected: true, free: true, x: rect.x, y: rect.y)
                }
                if let rect = freeRects[panel.cardID] {
                    return LayoutItem(id: panel.cardID, width: rect.width, height: rect.height,
                        pillHeight: panel.card.imageOnly ? 48 : 36, collapsed: panel.card.collapsed && !panel.card.overdue,
                        protected: panel.card.overdue, free: true, x: rect.x, y: rect.y)
                }
                let protected = panel.card.overdue || peeks[panel.cardID] != nil
                panel.applyTemporaryCollapsed(panel.card.collapsed && !protected)
                return LayoutItem(id: panel.cardID, width: panel.layoutWidth, height: panel.layoutHeight,
                    pillHeight: panel.card.imageOnly ? 48 : 36, collapsed: panel.card.collapsed && !protected,
                    protected: protected, free: panel.card.free, x: panel.card.x, y: panel.card.y)
            }
            let placements = Layout.compute(items: items, area: screen.area, hub: hubs[screen.id]?.obstacle ?? LayoutRect(), corner: tileCorner, gap: gap)
            var count = 0
            for placement in placements {
                guard let panel = panels[placement.id] else { continue }
                let protected = panel.card.overdue || panel.isEditing || panel.isDragging || panel.hasTemporaryWidth || strips[placement.id] != nil || peeks[placement.id] != nil
                if !protected && (placement.hidden || hiddenScreens.contains(screen.id)) { panel.orderOut(nil); count += 1; continue }
                if strips[placement.id] == nil && !panel.isDragging {
                    panel.applyTemporaryCollapsed(placement.collapsed && !protected)
                    let rect: LayoutRect
                    if placement.hidden {
                        let occupied = placements.filter { $0.id != placement.id && !$0.hidden }.map(\.rect) + [hub]
                        var current = screen.local(panel.frame); current.height = panel.layoutHeight
                        rect = Layout.pushOut(current, obstacles: occupied, area: screen.area, gap: gap)
                    } else { rect = placement.rect }
                    let target = screen.appKit(rect)
                    if panel.frame != target { panel.setFrame(target, display: true) }
                }
                if coveredScreens.contains(screen.id) { panel.level = .normal }
                if !panel.isVisible && !coveredScreens.contains(screen.id) { panel.orderFrontRegardless() }
            }
            hubs[screen.id]?.refresh(hidden: count, due: members.filter { $0.card.active && $0.card.overdue }.count, hotkeyProblem: hotkeyProblem)
        }
    }
    func moveHub(to point: NSPoint) {
        guard let screen = MacScreens.all.first(where: { $0.screen.frame.contains(point) }) ?? MacScreens.current else { return }
        let left = point.x < screen.work.midX, bottom = point.y < screen.work.midY
        do { try store.setSetting("hub.corner", (bottom ? "b" : "t") + (left ? "l" : "r")); applySettings() }
        catch { reportError(error); rebuildHubs() }
    }

    func newCard(screenID: String?, collectImage: Bool = false) {
        guard !stopping, let screen = MacScreens.resolve(screenID) ?? MacScreens.current else { return }
        if collectImage {
            do { let collected = try clipboard.collect(.general, onlyNewImage: true); if !collected.isEmpty { create(collected, screen: screen); return } }
            catch { reportError(error) }
        }
        var card = Card()
        card.id = nextTemporaryID; nextTemporaryID -= 1; card.createdAt = When.nowMs; card.mon = screen.id; card.topmost = defaultPin[screen.id] ?? false
        let panel = adopt(card)
        hiddenScreens.remove(screen.id); relayout(); panel.orderFrontRegardless(); panel.beginEditing()
    }

    private func create(_ collected: [ClipboardCard], screen: MacScreen) {
        guard !collected.isEmpty else { return }
        let group = store.begin()
        do {
            var cards = collected.map { item -> Card in
                var card = Card()
                card.body = item.body; card.images = item.images; card.mon = screen.id; card.createdAt = When.nowMs; card.topmost = defaultPin[screen.id] ?? false
                if item.images.count == 1 && item.body.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                    if let width = item.width { card.w = max(48, width / screen.scale) }
                    if let height = item.height { card.h = max(48, height / screen.scale) }
                    let factor = min(1, screen.work.width * 0.6 / card.w, screen.work.height * 0.6 / (card.h ?? card.w))
                    card.w *= factor; card.h = card.h.map { $0 * factor }
                }
                return card
            }
            cards = try store.insertMany(group, cards)
            cards.forEach { _ = adopt($0) }; push(group); hiddenScreens.remove(screen.id); relayout()
            for card in cards where card.src != nil { scheduleSource(card) }
            library?.refresh()
        } catch { reportError(error) }
    }

    func ingest(_ board: NSPasteboard, screenID: String?, into cardID: Int64? = nil) {
        guard !stopping else { return }
        if let promises = board.readObjects(forClasses: [NSFilePromiseReceiver.self], options: nil) as? [NSFilePromiseReceiver], !promises.isEmpty {
            receive(promises, screenID: screenID, into: cardID); return
        }
        do {
            let collected = try clipboard.collect(board)
            if let cardID, let panel = panels[cardID] {
                let bodies = collected.map(\.body).filter { !$0.isEmpty }, images = collected.flatMap(\.images)
                let fileDrop = board.types?.contains(.fileURL) == true
                if panel.isEditing && images.isEmpty && !fileDrop {
                    _ = panel.insertEditorText(bodies.joined(separator: "\n"))
                    return
                }
                guard panel.flushEditing(), panels[panel.cardID] === panel, panel.card.active else { return }
                let editing = panel.isEditing
                if editing && !bodies.isEmpty {
                    guard panel.insertEditorText(bodies.joined(separator: "\n")), panel.flushEditing() else { return }
                }
                if !images.isEmpty || (!editing && !bodies.isEmpty) {
                    change(panel, op: "paste", group: sessions[panel.cardID]?.group) { card in
                        if !editing && !bodies.isEmpty { card.body += (card.body.isEmpty ? "" : "\n") + bodies.joined(separator: "\n") }
                        card.images.append(contentsOf: images)
                    }
                }
                if panel.cardID > 0 && panel.card.src != nil { scheduleSource(panel.card) }
            } else if let screen = MacScreens.resolve(screenID) ?? MacScreens.current { create(collected, screen: screen) }
        } catch { reportError(error) }
    }

    private func receive(_ receivers: [NSFilePromiseReceiver], screenID: String?, into cardID: Int64?) {
        let directory = URL(fileURLWithPath: store.home, isDirectory: true).appendingPathComponent("files", isDirectory: true)
            .appendingPathComponent("import-" + UUID().uuidString, isDirectory: true)
        do { try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true) }
        catch { reportError(error); return }
        let queue = OperationQueue()
        let targetPanel = cardID.flatMap { panels[$0] }
        for receiver in receivers {
            targetPanel?.setSummaryStatus("正在导入…")
            receiver.receivePromisedFiles(atDestination: directory, options: [:], operationQueue: queue) { @Sendable [weak self, weak targetPanel] url, error in
                Task { @MainActor [weak self, weak targetPanel] in
                    guard let self, !self.stopping else {
                        if url.standardizedFileURL.path.hasPrefix(directory.standardizedFileURL.path + "/") { try? FileManager.default.removeItem(at: url) }
                        return
                    }
                    let destinationID: Int64?
                    if cardID != nil {
                        guard let targetPanel, self.panels[targetPanel.cardID] === targetPanel, targetPanel.card.active else {
                            if url.standardizedFileURL.path.hasPrefix(directory.standardizedFileURL.path + "/") { try? FileManager.default.removeItem(at: url) }
                            return
                        }
                        destinationID = targetPanel.cardID
                    } else { destinationID = nil }
                    targetPanel?.setSummaryStatus(nil)
                    if let error {
                        if url.standardizedFileURL.path.hasPrefix(directory.standardizedFileURL.path + "/") { try? FileManager.default.removeItem(at: url) }
                        self.reportError(error); return
                    }
                    let pasteboard = NSPasteboard.withUniqueName()
                    pasteboard.writeObjects([url as NSURL])
                    self.ingest(pasteboard, screenID: screenID, into: destinationID)
                    pasteboard.releaseGlobally()
                }
            }
        }
    }

    private func push(_ group: Group) { if !group.empty { push(groupNumber: group.number) } }
    private func push(groupNumber: Int64) {
        undoGroups.removeAll { $0 == groupNumber }; undoGroups.append(groupNumber); redoGroups.removeAll()
    }
    private func replaceTemporaryID(_ panel: CardPanel, with saved: Card) {
        let oldID = panel.cardID
        panels.removeValue(forKey: oldID); panels[saved.id] = panel
        if let session = sessions.removeValue(forKey: oldID) { sessions[saved.id] = session }
        drafts.removeValue(forKey: oldID)
    }

    private func change(_ panel: CardPanel, op: String, group: Group? = nil, ifRev: Int64? = nil,
                        update: (inout Card) throws -> Void) {
        do {
            let group = group ?? store.begin()
            let saved: Card
            if panel.cardID < 0 {
                var card = panel.card; try update(&card)
                if card.body.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && card.images.isEmpty {
                    panel.apply(card: card); requestLayout(); return
                }
                card.id = 0; saved = try store.insert(group, card)
                replaceTemporaryID(panel, with: saved)
            } else {
                guard let card = try store.mutate(group, id: panel.cardID, op: op, ifRev: ifRev, change: update) else { throw StoreError.missingCard }
                saved = card
            }
            panel.apply(card: saved)
            if sessions[panel.cardID]?.group !== group { push(group) }
            library?.refresh(); requestLayout()
        } catch { panel.markSaveResult(success: false, message: error.localizedDescription); reportError(error) }
    }

    func cardPanel(_ panel: CardPanel, perform action: CardAction) {
        guard !stopping else { return }
        switch action {
        case .editingBegan:
            sessions[panel.cardID] = EditSession(group: store.begin(), key: UUID().uuidString, title: panel.card.title, isNew: panel.cardID < 0)
        case .draftChanged(let body):
            guard let session = sessions[panel.cardID] else { return }
            var card = panel.card; card.body = body
            drafts[panel.cardID] = EditDraft(key: session.key, card: card, expectedRev: card.id > 0 ? card.rev : nil,
                groupNumber: session.group.number > 0 ? session.group.number : nil)
            checkpointTimer?.invalidate()
            checkpointTimer = Timer(timeInterval: 0.5, target: self, selector: #selector(checkpointTick), userInfo: nil, repeats: false)
            if let checkpointTimer { RunLoop.main.add(checkpointTimer, forMode: .common) }
        case .bodyChanged(let body): saveBody(panel, body: body)
        case .editingEnded:
            let session = sessions.removeValue(forKey: panel.cardID)
            if let session { push(session.group) }
            if panel.card.body.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && panel.card.images.isEmpty {
                drafts.removeValue(forKey: panel.cardID)
                do {
                    try persistDrafts()
                    if panel.cardID < 0 { remove(panel.cardID) }
                    else if session?.isNew == true {
                        try store.forget(panel.cardID)
                        if let number = session?.group.number { undoGroups.removeAll { $0 == number } }
                        remove(panel.cardID)
                    } else { finish(panel, deleting: true, group: session?.group, title: session?.title) }
                } catch { panel.markSaveResult(success: false, message: error.localizedDescription); reportError(error) }
            }
            if panel.cardID > 0 && panel.card.src != nil { scheduleSource(panel.card) }
            requestLayout()
        case .complete: finish(panel, deleting: false)
        case .delete: finish(panel, deleting: true)
        case .undo: undoCard(id: panel.cardID)
        case .redo: redo(nil)
        case .setPinned(let value): change(panel, op: "pin") { $0.topmost = value }
        case .setFree(let value): change(panel, op: "tile") { $0.free = value }
        case .setCollapsed(let value): change(panel, op: "collapse") { $0.collapsed = value }
        case .setColor(let value): change(panel, op: "color") { $0.color = value }
        case .setAutoColor(let value): if value { change(panel, op: "color") { $0.color = nil } }
        case .setReminder(let value): change(panel, op: "remind") { $0.remindAt = value }
        case .settled: requestLayout()
        case .dragging:
            previewDragID = panel.cardID
            if layoutTimer?.isValid != true { requestLayout() }
        case .move:
            previewDragID = nil; finishMove(panel)
        case .resize:
            previewDragID = nil; finishResize(panel)
        case .setOpacity(let value):
            change(panel, op: "opacity", group: continuousGroup()) { $0.opacity = min(1, max(0.2, value)) }
        case .autoHeight: change(panel, op: "resize") { $0.h = nil }
        case .summarize: requestSummary(id: panel.cardID)
        case .runSkill(let name): requestSummary(id: panel.cardID, skillName: name)
        case .cancelSummary: summaries.cancel(cardID: panel.cardID)
        case .openSource: openSource(panel.card)
        case .copy: copyPanel(panel)
        case .paste: ingest(.general, screenID: panel.card.mon, into: panel.cardID)
        case .drop(let board): ingest(board, screenID: panel.card.mon, into: panel.cardID)
        case .nextCard: focusCard(after: panel, backwards: false)
        case .previousCard: focusCard(after: panel, backwards: true)
        }
    }

    private func focusCard(after panel: CardPanel, backwards: Bool) {
        let ordered = panels.values.filter { $0.isVisible }.sorted { Card.compare($0.card, $1.card) }
        guard let index = ordered.firstIndex(where: { $0 === panel }), !ordered.isEmpty else { return }
        let next = (index + (backwards ? ordered.count - 1 : 1)) % ordered.count
        ordered[next].makeKeyAndOrderFront(nil)
    }

    private struct DragPlan {
        let screen: MacScreen
        let order: [CardPanel]
        let at: Int
        let placements: [LayoutPlacement]
    }

    private func dragPlan(_ panel: CardPanel) -> DragPlan? {
        guard !panel.card.free, !panel.card.overdue, !panel.hasTemporaryWidth, strips[panel.cardID] == nil,
              let screen = MacScreens.containing(panel.frame) else { return nil }
        let others = scoped(screen.id).filter { $0 !== panel }
        let order = others.filter { !$0.card.free && $0.card.active && !$0.card.overdue && !$0.isEditing && !$0.hasTemporaryWidth && strips[$0.cardID] == nil }
            .sorted { Card.compare($0.card, $1.card) }
        let orderedIDs = Set(order.map(\.cardID))
        let fixed = others.filter { !orderedIDs.contains($0.cardID) }.map { other -> LayoutItem in
            let rect = screen.local(strips[other.cardID]?.frame ?? other.frame)
            return LayoutItem(id: other.cardID, width: rect.width, height: rect.height, protected: true, free: true, x: rect.x, y: rect.y)
        }
        func flowItem(_ other: CardPanel) -> LayoutItem {
            other.applyTemporaryCollapsed(other.card.collapsed)
            return LayoutItem(id: other.cardID, width: other.layoutWidth, height: other.layoutHeight,
                pillHeight: other.card.imageOnly ? 48 : 36, collapsed: other.card.collapsed)
        }
        let flow = order.map(flowItem), moving = flowItem(panel), rect = screen.local(panel.frame)
        let hub = hubs[screen.id]?.obstacle ?? LayoutRect()
        let slots: [LayoutRect?] = (0...order.count).map { index in
            var trial = flow; trial.insert(moving, at: index)
            return Layout.compute(items: fixed + trial, area: screen.area, hub: hub, corner: tileCorner, gap: gap)
                .first { $0.id == panel.cardID && !$0.hidden }?.rect
        }
        guard slots.contains(where: { $0?.intersects(rect) == true }) else { return nil }
        let low = order.filter { $0.card.remindAt != nil && Card.compare($0.card, panel.card) }.count
        let high = panel.card.remindAt == nil ? order.count : low
        func distance(_ index: Int) -> Double {
            guard let slot = slots[index] else { return .infinity }
            return abs(slot.x + slot.width / 2 - rect.x - rect.width / 2) + abs(slot.y + slot.height / 2 - rect.y - rect.height / 2)
        }
        guard let at = (low...high).filter({ slots[$0] != nil }).min(by: {
            let a = distance($0), b = distance($1); return a == b ? $0 < $1 : a < b
        }) else { return nil }
        var trial = flow; trial.insert(moving, at: at)
        return DragPlan(screen: screen, order: order, at: at,
            placements: Layout.compute(items: fixed + trial, area: screen.area, hub: hub, corner: tileCorner, gap: gap))
    }

    private func previewDrag(_ panel: CardPanel) {
        guard let plan = dragPlan(panel) else { relayout(); return }
        for placement in plan.placements where placement.id != panel.cardID {
            guard let other = panels[placement.id], !other.isEditing, !other.isDragging, !other.hasTemporaryWidth, strips[other.cardID] == nil else { continue }
            if placement.hidden { other.orderOut(nil) }
            else {
                other.applyTemporaryCollapsed(placement.collapsed)
                other.setFrame(plan.screen.appKit(placement.rect), display: true)
                if !other.isVisible { other.orderFrontRegardless() }
            }
        }
    }

    private func finishMove(_ panel: CardPanel) {
        if let plan = dragPlan(panel) {
            var order = plan.order; order.insert(panel, at: min(plan.at, order.count))
            let ranked = order.filter { $0.card.remindAt == nil }, ranks = Card.ranks(order.filter { $0.card.remindAt == nil }.map(\.card))
            let group = store.begin()
            do {
                if panel.card.mon != plan.screen.id, panel.cardID > 0,
                   let saved = try store.mutate(group, id: panel.cardID, op: "move", change: { $0.mon = plan.screen.id }) { panel.apply(card: saved) }
                for (index, other) in ranked.enumerated() where other.cardID > 0 && other.card.rank != ranks[index] {
                    let rank = ranks[index]
                    if let saved = try store.mutate(group, id: other.cardID, op: "move", change: { $0.ord = rank }) { other.apply(card: saved) }
                }
                push(group); relayout(); library?.refresh()
            } catch { reportError(error) }
            return
        }
        guard let screen = MacScreens.containing(panel.frame) else { return }
        let obstacles = scoped(screen.id).filter { $0 !== panel && $0.isVisible && $0.card.free }.map { screen.local($0.frame) }
            + [hubs[screen.id]?.obstacle ?? LayoutRect()]
        let rect = Layout.pushOut(screen.local(panel.frame), obstacles: obstacles, area: screen.area, gap: gap)
        panel.setFrame(screen.appKit(rect), display: true)
        change(panel, op: "move") { $0.free = true; $0.mon = screen.id; $0.x = rect.x; $0.y = rect.y }
        relayout()
    }

    private func finishResize(_ panel: CardPanel) {
        guard let screen = MacScreens.containing(panel.frame) else { return }
        var rect = screen.local(panel.frame)
        let start = panel.lastUserStartFrame
        let widthChanged = abs(panel.frame.width - start.width) > 1
        let heightChanged = abs(panel.frame.height - start.height) > 1
        if (widthChanged || heightChanged) && rect.width > panel.card.w + 1 && !panel.card.topmost && !panel.isPill && !panel.card.imageOnly {
            panel.holdTemporaryWidth()
            if heightChanged { change(panel, op: "resize") { $0.h = rect.height.rounded() } }
            panel.fitAutomaticHeight()
            relayout(); return
        }
        panel.clearTemporaryWidth()
        if panel.card.free {
            let obstacles = scoped(screen.id).filter { $0 !== panel && $0.isVisible && $0.card.free }.map { screen.local($0.frame) }
                + [hubs[screen.id]?.obstacle ?? LayoutRect()]
            rect = Layout.trim(rect, start: screen.local(start), obstacles: obstacles, area: screen.area,
                minW: panel.card.imageOnly ? 48 : 160, minH: panel.card.imageOnly ? 48 : 64, gap: gap)
            panel.setFrame(screen.appKit(rect), display: true)
        }
        change(panel, op: "resize") { card in
            if widthChanged { card.w = rect.width.rounded() }
            if heightChanged || card.h != nil { card.h = rect.height.rounded() }
            card.mon = screen.id
            if card.free { card.x = rect.x; card.y = rect.y }
        }
        relayout()
    }

    private func saveBody(_ panel: CardPanel, body: String) {
        do {
            try persistDrafts()
            let oldID = panel.cardID
            let session = sessions[oldID] ?? EditSession(group: store.begin(), key: UUID().uuidString, title: panel.card.title, isNew: panel.cardID < 0)
            let saved: Card
            if oldID < 0 {
                var card = panel.card; card.body = body
                if body.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && card.images.isEmpty {
                    drafts.removeValue(forKey: oldID)
                    try persistDrafts()
                    panel.markSaveResult(success: true, message: nil)
                    return
                }
                card.id = 0; saved = try store.insert(session.group, card)
                replaceTemporaryID(panel, with: saved)
                sessions[saved.id] = session
            } else {
                guard let card = try store.mutate(session.group, id: oldID, op: "edit", ifRev: panel.card.rev, change: { $0.body = body }) else { throw StoreError.missingCard }
                saved = card
            }
            panel.apply(card: saved); panel.markSaveResult(success: true, message: nil)
            if !panel.isEditing { push(session.group) }
            drafts.removeValue(forKey: saved.id); try persistDrafts(); library?.refresh(); requestLayout()
        } catch { panel.markSaveResult(success: false, message: error.localizedDescription); reportError(error) }
    }

    @objc private func checkpointTick() { do { try persistDrafts() } catch { reportError(error) } }
    private func persistDrafts() throws {
        checkpointTimer?.invalidate()
        let old = checkpointPath
        if drafts.isEmpty {
            if let old { try DraftRecovery.discard(home: store.home, path: old); checkpointPath = nil }
        } else {
            let path = try DraftRecovery.write(home: store.home, drafts: Array(drafts.values))
            checkpointPath = path
            if let old, old != path { try DraftRecovery.discard(home: store.home, path: old) }
        }
    }

    private func finish(_ panel: CardPanel, deleting: Bool, group: Group? = nil, title: String? = nil) {
        guard panel.commitEditing(), panels[panel.cardID] === panel, panel.card.active else { return }
        if panel.cardID < 0 { remove(panel.cardID); requestLayout(); return }
        do {
            let group = group ?? store.begin(), frame = panel.frame, displayTitle = title ?? panel.card.title
            guard let saved = try store.mutate(group, id: panel.cardID, op: deleting ? "delete" : "done", change: { card in
                if deleting { card.deletedAt = When.nowMs } else { card.doneAt = When.nowMs }
            }) else { throw StoreError.missingCard }
            summaries.cancel(cardID: saved.id); panel.apply(card: saved); push(group)
            strips[saved.id] = UndoStrip(group: group.number, frame: frame, deadline: Date().addingTimeInterval(4))
            panel.showUndo((deleting ? "已删除 · " : "已完成 · ") + String(displayTitle.prefix(14)))
            library?.refresh()
        } catch { reportError(error) }
    }

    private func restore(_ group: Int64) throws -> Int64? {
        let inverse = store.begin()
        let ids = try store.restore(group: group, into: inverse)
        for id in ids { if let panel = panels[id], strips.removeValue(forKey: id) != nil { panel.clearUndo() } }
        try reload(); relayout()
        return inverse.empty ? nil : inverse.number
    }
    func undoCard(id: Int64) {
        do {
            let group = strips[id]?.group ?? (try store.ops(limit: 1, cardId: id).first?.grp)
            guard let group else { return }
            let inverse = try restore(group); undoGroups.removeAll { $0 == group }
            if let inverse { redoGroups.append(inverse) }
        } catch { reportError(error) }
    }
    @objc func undo(_ sender: Any?) {
        if let panel = NSApp.keyWindow as? CardPanel { undoCard(id: panel.cardID); return }
        guard let group = undoGroups.last else { return }
        do { let inverse = try restore(group); undoGroups.removeLast(); if let inverse { redoGroups.append(inverse) } }
        catch { reportError(error) }
    }
    @objc func redo(_ sender: Any?) {
        guard let group = redoGroups.last else { return }
        do { let inverse = try restore(group); redoGroups.removeLast(); if let inverse { undoGroups.append(inverse) } }
        catch { reportError(error) }
    }
    @objc func copy(_ sender: Any?) { if let panel = NSApp.keyWindow as? CardPanel { copyPanel(panel) } }
    @objc func paste(_ sender: Any?) { if let panel = NSApp.keyWindow as? CardPanel { ingest(.general, screenID: panel.card.mon, into: panel.cardID) } }

    private func copyPanel(_ panel: CardPanel) {
        let board = NSPasteboard.general
        board.clearContents()
        let wrote: Bool
        if panel.card.imageOnly, let name = panel.card.images.first,
           let image = NSImage(contentsOfFile: store.resolveImage(name)) {
            wrote = board.writeObjects([image])
        } else {
            let text = [panel.card.body, panel.card.summary ?? ""].filter { !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }.joined(separator: "\n\n")
            wrote = board.setString(text, forType: .string)
        }
        if wrote { clipboard.markUsed(board) }
    }

    private func openSource(_ card: Card) {
        guard let source = card.src else { return }
        if Card.isURL(source), let url = URL(string: source) { NSWorkspace.shared.open(url); return }
        guard source.hasPrefix("/") else { reportError(NSError(domain: "PinCC", code: 1, userInfo: [NSLocalizedDescriptionKey: "来自 Windows 的文件；请在原电脑打开。"])); return }
        let url = URL(fileURLWithPath: source)
        do { _ = try url.resourceValues(forKeys: [.isRegularFileKey, .isDirectoryKey]) } catch { reportError(error); return }
        NSWorkspace.shared.open(url)
    }

    private func scheduleSource(_ card: Card) {
        guard let source = card.src else { return }
        if let previous = sourceSignatures[card.id], previous.utf16.elementsEqual(source.utf16) { return }
        sourceSignatures[card.id] = source
        enrichSource(id: card.id, runAuto: true)
    }
    private func enrichSource(id: Int64, runAuto: Bool) {
        guard !stopping else { return }
        guard sourceTasks[id] == nil else { pendingSources[id] = (pendingSources[id] ?? false) || runAuto; return }
        sourceTasks[id] = Task { [weak self] in
            guard let self else { return }
            defer {
                self.sourceTasks.removeValue(forKey: id)
                if let auto = self.pendingSources.removeValue(forKey: id), !self.stopping { self.enrichSource(id: id, runAuto: auto) }
            }
            do {
                let card = try await self.summaries.enrich(cardID: id)
                guard !self.stopping, !Task.isCancelled else { return }
                if let card, let panel = self.panels[id], !panel.isEditing { panel.apply(card: card); self.requestLayout() }
                if runAuto, self.autoSummary, self.hasRunner, let card, (card.summary ?? "").isEmpty { self.requestSummary(id: id, userAsked: false) }
            } catch is CancellationError { }
            catch { if !self.stopping { self.panels[id]?.setSummaryStatus("来源未能读取：" + error.localizedDescription) } }
        }
    }
    private func refreshFileSources() {
        guard fileRefreshTask == nil else { return }
        let cards = panels.values.map(\.card).filter { $0.active && $0.isFile && $0.src?.hasPrefix("/") == true }
        fileRefreshTask = Task { [weak self] in
            let changed = await Task.detached(priority: .utility) {
                cards.filter { card in
                    guard let source = card.src,
                          let facts = try? URL(fileURLWithPath: source).resourceValues(forKeys: [.contentModificationDateKey]),
                          let date = facts.contentModificationDate else { return card.srcInfo?.state != "missing" }
                    return card.srcInfo?.mtime != When.toMs(date)
                }.map(\.id)
            }.value
            guard let self else { return }
            self.fileRefreshTask = nil
            guard !self.stopping, !Task.isCancelled else { return }
            for id in changed { self.enrichSource(id: id, runAuto: false) }
        }
    }
    private func detectFirstRunner() {
        do {
            guard !hasRunner, !(try store.settingBool("mac.llm.detected", fallback: false)) else { return }
            Task { [weak self] in
                guard let self else { return }
                do {
                    let found = try await self.summaries.detectTools()
                    guard !self.stopping else { return }
                    if let first = found.first, (try self.store.setting("mac.llm.tool")).isEmpty { try self.store.setSetting("mac.llm.tool", first) }
                    try self.store.setSetting("mac.llm.detected", true); self.applySettings()
                } catch { if !self.stopping { self.reportError(error) } }
            }
        } catch { reportError(error) }
    }
    private func maintenance() {
        guard !stopping, maintenanceTask == nil else { return }
        let formatter = DateFormatter()
        formatter.calendar = Calendar(identifier: .gregorian); formatter.locale = Locale(identifier: "en_US_POSIX"); formatter.dateFormat = "yyyyMMdd"
        let day = formatter.string(from: Date())
        guard maintenanceDay != day else { return }
        do {
            let days = try store.settingInt("retention.days", fallback: 30), home = store.home
            let task = Task.detached(priority: .utility) {
                let maintenance = try Store(home: home, busyMs: 250)
                defer { maintenance.close() }
                try maintenance.purge(days: days, cancelled: { Task.isCancelled })
                try maintenance.backup(cancelled: { Task.isCancelled })
            }
            maintenanceTask = task
            Task { [weak self] in
                do { try await task.value; self?.maintenanceDay = day }
                catch is CancellationError { }
                catch { if let self, !self.stopping { self.lastError = error.localizedDescription } }
                self?.maintenanceTask = nil
            }
        } catch { reportError(error) }
    }

    func requestSummary(id: Int64, userAsked: Bool = true, skillName: String? = nil) {
        let panel = panels[id]
        if userAsked, let panel, !panel.flushEditing() { return }
        let cardID = panel?.cardID ?? id
        guard cardID > 0, panels[cardID]?.card.active == true else { return }
        Task { [weak self] in
            guard let self else { return }
            do {
                let desk = try CLI.deskMarkdown(self.store, cards: self.store.active())
                let result = try await self.summaries.run(cardID: cardID, skillName: skillName, userAsked: userAsked, budget: self.summaryBudget(cardID), desk: desk,
                    authorize: { [weak self] description in await self?.authorize(description, cardID: cardID) ?? false },
                    status: { [weak self] status in self?.panels[cardID]?.setSummaryStatus(status) })
                guard !self.stopping else { return }
                self.panels[cardID]?.setSummaryStatus(nil)
                if userAsked, let result, let group = result.group { self.push(groupNumber: group) }
                try self.reload(); self.relayout()
            } catch is CancellationError { self.panels[cardID]?.setSummaryStatus(nil) }
            catch { self.panels[cardID]?.setSummaryStatus(error.localizedDescription); if userAsked { self.reportError(error) } }
        }
    }
    @objc private func performRunDeskSkill(_ sender: NSMenuItem) {
        guard let name = sender.representedObject as? String, !deskRunning, !stopping else { return }
        deskRunning = true; deskStatus = "正在准备…"
        Task { [weak self] in
            guard let self else { return }
            defer { self.deskRunning = false; self.deskStatus = nil }
            do {
                let desk = try CLI.deskMarkdown(self.store, cards: self.store.active())
                let result = try await self.summaries.runDesk(skillName: name, desk: desk,
                    authorize: { [weak self] description in
                        guard let self else { return false }
                        if NSApp.keyWindow == nil { self.performLibrary(nil) }
                        return await self.authorize(description, cardID: 0)
                    },
                    status: { [weak self] value in self?.deskStatus = value })
                guard !self.stopping else { return }
                if let group = result?.group { self.push(groupNumber: group) }
                try self.reload(); self.relayout()
            } catch is CancellationError { }
            catch { if !self.stopping { self.reportError(error) } }
        }
    }
    @objc private func performCancelDeskSkill(_ sender: Any?) {
        summaries.cancel(cardID: 0); deskStatus = "正在取消…"
    }

    private func authorize(_ description: String, cardID: Int64) async -> Bool {
        guard let window = (panels[cardID] as NSWindow?) ?? NSApp.keyWindow, !Task.isCancelled else { return false }
        let alert = NSAlert()
        alert.messageText = "允许运行这项技能？"; alert.informativeText = description
        alert.addButton(withTitle: "允许本次技能版本"); alert.addButton(withTitle: "取消")
        return await withTaskCancellationHandler {
            await withCheckedContinuation { continuation in
                guard !Task.isCancelled else { continuation.resume(returning: false); return }
                alert.beginSheetModal(for: window) { response in continuation.resume(returning: response == .alertFirstButtonReturn) }
            }
        } onCancel: {
            Task { @MainActor in
                if alert.window.sheetParent != nil { NSApp.endSheet(alert.window, returnCode: .cancel) }
            }
        }
    }
    private func summaryBudget(_ id: Int64) -> Int {
        guard let card = panels[id]?.card, let screen = MacScreens.resolve(card.mon) else { return 300 }
        let size: Double
        do { size = min(20, max(12, Double(try store.setting("text.size", fallback: "14")) ?? 14)) }
        catch { reportError(error); return 300 }
        let header = card.isLink ? 72.0 : card.isFile ? 52.0 : 0
        let height = (card.h ?? min(360, screen.work.height * 0.4)) - 31 - 10 - header
        let lines = max(3, height / (size * 1.5).rounded(.toNearestOrEven)), perLine = max(8, (card.w - 26) / size)
        return Int(min(500, max(60, lines * perLine * 0.7)))
    }
    private func continuousGroup() -> Group {
        if gestureGroup == nil || Date().timeIntervalSince(gestureAt) > 0.6 { gestureGroup = store.begin() }
        gestureAt = Date()
        return gestureGroup!
    }
    func detectTools() async throws -> [String] { try await summaries.detectTools() }
    func testRunner() async throws -> String { try await summaries.testRunner() }
    func setShortcutRecording(_ active: Bool) { shortcutRecording = active; if active { hotkeys.shutdown() } else { registerHotkey() } }

    func tile(screenID: String?) {
        let group = store.begin()
        do {
            for panel in scoped(screenID) where panel.cardID > 0 && panel.card.active {
                guard panel.commitEditing() else { return }
                panel.clearTemporaryWidth()
                if let saved = try store.mutate(group, id: panel.cardID, op: "tile", change: { $0.free = false }) { panel.apply(card: saved) }
            }
            push(group); showCards(screenID: screenID); library?.refresh()
        } catch { reportError(error) }
    }
    func collapse(screenID: String?) {
        let selected = scoped(screenID).filter { $0.card.active && !$0.card.overdue }
        let value = selected.contains { !$0.card.collapsed }, group = store.begin()
        do {
            for panel in selected {
                guard panel.commitEditing() else { return }
                if panel.cardID > 0, let saved = try store.mutate(group, id: panel.cardID, op: "collapse", change: { $0.collapsed = value }) { panel.apply(card: saved) }
            }
            push(group); relayout(); library?.refresh()
        } catch { reportError(error) }
    }
    func toggleHidden(screenID: String?) {
        let ids = screenID.map { [$0] } ?? MacScreens.all.map(\.id)
        if ids.allSatisfy({ hiddenScreens.contains($0) }) { ids.forEach { hiddenScreens.remove($0) } }
        else { ids.forEach { hiddenScreens.insert($0) } }
        relayout()
    }
    func showCards(screenID: String?) {
        if let screenID { hiddenScreens.remove(screenID) } else { hiddenScreens.removeAll() }
        relayout()
        scoped(screenID).filter { $0.isVisible }.forEach { $0.orderFrontRegardless() }
    }
    private func scoped(_ screenID: String?) -> [CardPanel] { panels.values.filter { screenID == nil || MacScreens.resolve($0.card.mon)?.id == screenID } }

    func libraryShowCard(id: Int64) {
        guard let panel = panels[id] else { return }
        hiddenScreens.remove(MacScreens.resolve(panel.card.mon)?.id ?? "")
        peeks[id] = Date().addingTimeInterval(4); panel.applyTemporaryCollapsed(false); panel.fitAutomaticHeight(); relayout(); panel.makeKeyAndOrderFront(nil)
    }
    func libraryRestoreCard(id: Int64) {
        do {
            let group = store.begin()
            _ = try store.mutate(group, id: id, op: "restore", change: { $0.doneAt = nil; $0.deletedAt = nil })
            push(group); try reload(); relayout(); libraryShowCard(id: id)
        } catch { reportError(error) }
    }
    func libraryDeleteCard(id: Int64, permanent: Bool) {
        do {
            if permanent { try store.forget(id); remove(id); try reload(); relayout() }
            else if let panel = panels[id] { finish(panel, deleting: true) }
            else { let group = store.begin(); _ = try store.mutate(group, id: id, op: "delete", change: { $0.deletedAt = When.nowMs }); push(group) }
            library?.refresh()
        } catch { reportError(error) }
    }
    func libraryUndo(group: Int64) { do { if let inverse = try restore(group) { push(groupNumber: inverse) } } catch { reportError(error) } }
    func pendingDrafts() throws -> [PendingDraft] { try DraftRecovery.pending(home: store.home) }
    func libraryRestoreDraft(_ pending: PendingDraft) {
        do { _ = try DraftRecovery.restoreAsNew(home: store.home, pending: pending); try reload(); relayout() }
        catch { reportError(error) }
    }

    func menuItem(title: String, action: Selector, key: String = "", screenID: String? = nil) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: action, keyEquivalent: key)
        item.target = self; item.representedObject = screenID
        return item
    }
    var skillsCatalogNotice: String? {
        if skillsCatalogLoading { return "技能目录正在加载…" }
        return skillsCatalogError
    }

    /// Menus read only this cache. A running scan coalesces later refreshes into one next scan.
    func refreshSkillsCatalog() {
        guard !stopping else { return }
        skillsCatalogGeneration &+= 1
        skillsCatalogError = nil
        skillsCatalogLoading = true
        guard skillsCatalogTask == nil else { return }
        beginSkillsCatalogLoad()
    }

    private func beginSkillsCatalogLoad() {
        let home = store.home, generation = skillsCatalogGeneration
        skillsCatalogTask = Task { [weak self] in
            let result = await withCheckedContinuation { (continuation: CheckedContinuation<Result<SkillsCatalog, Error>, Never>) in
                Self.skillsCatalogQueue.async {
                    do { continuation.resume(returning: .success(try SkillsCatalog.load(home: home))) }
                    catch { continuation.resume(returning: .failure(error)) }
                }
            }
            guard let self else { return }
            self.skillsCatalogTask = nil
            guard !self.stopping, !Task.isCancelled else { return }
            guard generation == self.skillsCatalogGeneration else {
                self.beginSkillsCatalogLoad()
                return
            }
            self.skillsCatalogLoading = false
            switch result {
            case .success(let catalog):
                self.skillsCatalog = catalog
                if !catalog.errors.isEmpty { self.skillsCatalogError = "部分技能配置未能读取：" + catalog.errors.joined(separator: "\n") }
            case .failure(let error):
                self.skillsCatalogError = "技能目录未能读取：" + error.localizedDescription
            }
            self.settings?.applySkillsCatalog(self.skillsCatalog, error: self.skillsCatalogError)
        }
    }

    private func fillDeskSkills(_ menu: NSMenu) {
        menu.removeAllItems(); menu.autoenablesItems = false
        let skills = skillsCatalog?.skills.filter { $0.scope == "desk" } ?? []
        for skill in skills {
            let item = NSMenuItem(title: skill.label, action: #selector(performRunDeskSkill(_:)), keyEquivalent: "")
            item.target = self; item.representedObject = skill.name; item.toolTip = skill.description
            item.isEnabled = !deskRunning; menu.addItem(item)
        }
        if let notice = skillsCatalogNotice {
            let item = NSMenuItem(title: skillsCatalogLoading ? notice : "技能配置未能读取", action: nil, keyEquivalent: "")
            item.toolTip = notice; item.isEnabled = false; menu.addItem(item)
        } else if skills.isEmpty {
            let item = NSMenuItem(title: "暂无桌面技能", action: nil, keyEquivalent: "")
            item.isEnabled = false; menu.addItem(item)
        }
        if deskRunning {
            menu.addItem(.separator())
            let status = NSMenuItem(title: deskStatus ?? "正在运行…", action: nil, keyEquivalent: "")
            status.isEnabled = false; menu.addItem(status)
            let cancel = NSMenuItem(title: "取消桌面技能", action: #selector(performCancelDeskSkill(_:)), keyEquivalent: "")
            cancel.target = self; menu.addItem(cancel)
        }
    }

    func menu(screenID: String?) -> NSMenu {
        let menu = NSMenu(title: "PinCC"), scope = screenID == nil ? "全部" : "本屏"
        menu.delegate = self
        menu.addItem(menuItem(title: "新建卡片", action: #selector(performNew(_:)), screenID: screenID))
        menu.addItem(menuItem(title: "粘贴为新卡片", action: #selector(performPasteNew(_:)), screenID: screenID))
        menu.addItem(.separator())
        menu.addItem(menuItem(title: "显示\(scope)卡片", action: #selector(performShow(_:)), screenID: screenID))
        menu.addItem(menuItem(title: "平铺\(scope)卡片", action: #selector(performTile(_:)), screenID: screenID))
        menu.addItem(menuItem(title: "折叠 / 展开\(scope)卡片", action: #selector(performCollapse(_:)), screenID: screenID))
        menu.addItem(menuItem(title: "隐藏 / 显示\(scope)普通卡片", action: #selector(performHide(_:)), screenID: screenID))
        menu.addItem(menuItem(title: "置顶\(scope)卡片", action: #selector(performPin(_:)), screenID: screenID))
        menu.addItem(menuItem(title: "暂缓提醒声 1 小时", action: #selector(performSnoozeSound(_:))))
        menu.addItem(.separator())
        menu.addItem(menuItem(title: "卡片库…", action: #selector(performLibrary(_:))))
        menu.addItem(menuItem(title: "设置…", action: #selector(performSettings(_:))))
        let skillsItem = NSMenuItem(title: "桌面技能", action: nil, keyEquivalent: "")
        skillsItem.representedObject = "pincc.desk.skills"
        let skillsMenu = NSMenu(title: "桌面技能"); fillDeskSkills(skillsMenu); skillsItem.submenu = skillsMenu
        menu.addItem(skillsItem)
        menu.addItem(.separator())
        menu.addItem(NSMenuItem(title: "退出 PinCC", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q"))
        return menu
    }
    func menuNeedsUpdate(_ menu: NSMenu) {
        for item in menu.items {
            if item.representedObject as? String == "pincc.desk.skills", let skills = item.submenu { fillDeskSkills(skills); continue }
            let screenID = item.representedObject as? String
            let selected = scoped(screenID).filter { $0.card.active }
            if item.action == #selector(performPin(_:)) {
                let pinned = !selected.isEmpty ? selected.allSatisfy { $0.card.topmost } : (screenID.flatMap { defaultPin[$0] } ?? false)
                item.state = pinned ? .on : .off
            } else if item.action == #selector(performCollapse(_:)) { item.state = !selected.isEmpty && selected.allSatisfy { $0.card.collapsed } ? .on : .off }
            else if item.action == #selector(performSnoozeSound(_:)) { item.state = soundSnooze > When.nowMs ? .on : .off }
        }
    }
    @objc func performPin(_ sender: NSMenuItem) {
        let screenID = sender.representedObject as? String, selected = scoped(sender.representedObject as? String).filter { $0.card.active }
        let value = selected.isEmpty ? !(screenID.flatMap { defaultPin[$0] } ?? false) : !selected.allSatisfy { $0.card.topmost }
        let ids = screenID.map { [$0] } ?? MacScreens.all.map(\.id), group = store.begin()
        do {
            for id in ids { try store.setSetting("mac.top:" + id, value) }
            for panel in selected where panel.cardID > 0 {
                if let saved = try store.mutate(group, id: panel.cardID, op: "pin", change: { $0.topmost = value }) { panel.apply(card: saved) }
            }
            push(group); applySettings(); library?.refresh()
        } catch { reportError(error) }
    }
    @objc func performSnoozeSound(_ sender: Any?) {
        do { soundSnooze = soundSnooze > When.nowMs ? 0 : When.nowMs + 3_600_000; try store.setSetting("mac.sound.snooze_until", String(soundSnooze)) }
        catch { reportError(error) }
    }
    @objc func performNew(_ sender: NSMenuItem) { newCard(screenID: sender.representedObject as? String ?? MacScreens.current?.id, collectImage: true) }
    @objc func performPasteNew(_ sender: NSMenuItem) { ingest(.general, screenID: sender.representedObject as? String ?? MacScreens.current?.id) }
    @objc func performShow(_ sender: NSMenuItem) { showCards(screenID: sender.representedObject as? String) }
    @objc func performTile(_ sender: NSMenuItem) { tile(screenID: sender.representedObject as? String) }
    @objc func performCollapse(_ sender: NSMenuItem) { collapse(screenID: sender.representedObject as? String) }
    @objc func performHide(_ sender: NSMenuItem) { toggleHidden(screenID: sender.representedObject as? String) }
    @objc func performLibrary(_ sender: Any?) {
        if library == nil { library = LibraryWindow(store: store, desktop: self) }
        library?.show()
        if recoveryFailureCount > 0 { library?.window?.title = "卡片库 · \(recoveryFailureCount) 份草稿需恢复" }
        else if recoveryCount > 0 { library?.window?.title = "卡片库 · 已恢复 \(recoveryCount) 份编辑" }
    }
    @objc func performSettings(_ sender: Any?) {
        if settings == nil { settings = SettingsWindow(store: store, desktop: self) }
        settings?.show()
    }
    func refreshFromDisk() { guard !stopping else { return }; do { try reload(); relayout() } catch { reportError(error) } }
    func showRecoveryNotice(count: Int) { recoveryCount = count }
    func showRecoveryFailure(count: Int) { recoveryFailureCount = count }
    func reportError(_ error: Error) {
        let message = error.localizedDescription
        guard !stopping, message != lastError else { return }
        lastError = message
        let alert = NSAlert(error: error)
        alert.messageText = "PinCC 操作未完成"
        if let window = NSApp.keyWindow, window.attachedSheet == nil { alert.beginSheetModal(for: window) }
        else { alert.runModal() }
    }
}
