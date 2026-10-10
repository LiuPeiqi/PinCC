import AppKit
import PinCCCore
import PinCCCli

/// The card library is an ordinary auxiliary window. Desktop cards stay independent panels.
@MainActor
final class LibraryWindow: NSWindowController, NSTableViewDataSource, NSTableViewDelegate, NSSearchFieldDelegate {
    private let store: Store
    private weak var desktop: DesktopController?
    private let search = NSSearchField()
    private let filter = NSSegmentedControl(labels: ["活动", "已完成", "已删除", "历史", "待恢复"], trackingMode: .selectOne, target: nil, action: nil)
    private let table = NSTableView()
    private let detail = NSTextView()
    private let countLabel = NSTextField(labelWithString: "")
    private let titleLabel = NSTextField(labelWithString: "选择一张卡片")
    private let factsLabel = NSTextField(wrappingLabelWithString: "")
    private let primaryButton = NSButton(title: "显示在桌面", target: nil, action: nil)
    private let deleteButton = NSButton(title: "删除", target: nil, action: nil)
    private var cards: [Card] = []
    private var operations: [Op] = []
    private var visibleCards: [Card] = []
    private var visibleOperations: [Op] = []
    private var visibleDrafts: [PendingDraft] = []

    init(store: Store, desktop: DesktopController) {
        self.store = store
        self.desktop = desktop
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 820, height: 620),
                              styleMask: [.titled, .closable, .miniaturizable, .resizable],
                              backing: .buffered, defer: false)
        window.title = "PinCC · 卡片库"
        window.isReleasedWhenClosed = false
        window.contentMinSize = NSSize(width: 640, height: 420)
        super.init(window: window)
        buildInterface()
        window.center()
        refresh()
    }

    required init?(coder: NSCoder) { return nil }

    func show() {
        refresh()
        NSApp.activate(ignoringOtherApps: true)
        showWindow(nil)
        window?.makeKeyAndOrderFront(nil)
        window?.makeFirstResponder(search)
    }

    func refresh() {
        let selectedID = selectedCard?.id
        let selectedOperation = selectedOp?.id
        let selectedDraftPath = selectedDraft?.path
        do {
            cards = try store.all().sorted(by: Card.compare)
            operations = isHistory ? try store.ops(limit: 200) : []
            let query = search.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
            let matches = cards.filter { card in
                query.isEmpty || [card.title, card.body, card.summary ?? ""].contains { matchesQuery($0, query) }
            }
            let groups: [[Card]] = [matches.filter { $0.active },
                                    matches.filter { !$0.active && $0.doneAt != nil && $0.deletedAt == nil },
                                    matches.filter { $0.deletedAt != nil }]
            for index in 0..<3 { filter.setLabel(["活动", "已完成", "已删除"][index] + " \(groups[index].count)", forSegment: index) }
            visibleCards = isHistory || isDrafts ? [] : groups[max(0, filter.selectedSegment)]
            visibleDrafts = isDrafts ? try (desktop?.pendingDrafts() ?? []).filter { pending in
                query.isEmpty || pending.drafts.contains { draft in
                    matchesQuery(draft.card.title + " " + (draft.editorText ?? draft.card.body) + " " + (draft.addText ?? ""), query)
                }
            } : []
            visibleOperations = operations.filter { op in
                query.isEmpty || matchesQuery("\(CLI.opName(op.name)) \(op.cardId) \(previousBody(op))", query)
            }
            table.reloadData()
            let row: Int?
            if isHistory { row = visibleOperations.firstIndex { $0.id == selectedOperation } }
            else if isDrafts { row = visibleDrafts.firstIndex { $0.path == selectedDraftPath } }
            else { row = visibleCards.firstIndex { $0.id == selectedID } }
            if let row { table.selectRowIndexes(IndexSet(integer: row), byExtendingSelection: false) }
            else { table.deselectAll(nil) }
            countLabel.stringValue = isHistory ? "最近 \(visibleOperations.count) 条操作记录" : isDrafts ? "\(visibleDrafts.count) 组待恢复编辑" : "\(visibleCards.count) 张卡片"
            updateDetail()
        } catch { desktop?.reportError(error) }
    }

    private var isHistory: Bool { filter.selectedSegment == 3 }
    private var isDrafts: Bool { filter.selectedSegment == 4 }
    private var selectedDraft: PendingDraft? { isDrafts && visibleDrafts.indices.contains(table.selectedRow) ? visibleDrafts[table.selectedRow] : nil }
    private var selectedCard: Card? { visibleCards.indices.contains(table.selectedRow) ? visibleCards[table.selectedRow] : nil }
    private var selectedOp: Op? { isHistory && visibleOperations.indices.contains(table.selectedRow) ? visibleOperations[table.selectedRow] : nil }

    private func buildInterface() {
        guard let content = window?.contentView else { return }
        search.placeholderString = "搜索标题、正文、摘要"
        search.delegate = self
        search.sendsSearchStringImmediately = true
        filter.selectedSegment = 0
        filter.target = self
        filter.action = #selector(filterChanged)
        filter.segmentStyle = .rounded
        countLabel.textColor = .secondaryLabelColor
        countLabel.font = .systemFont(ofSize: 11)
        let top = NSStackView(views: [search, filter, countLabel])
        top.orientation = .vertical
        top.alignment = .leading
        top.spacing = 10
        search.widthAnchor.constraint(equalTo: top.widthAnchor).isActive = true

        let column = NSTableColumn(identifier: NSUserInterfaceItemIdentifier("card"))
        column.title = "卡片"
        table.addTableColumn(column)
        table.headerView = nil
        table.columnAutoresizingStyle = .uniformColumnAutoresizingStyle
        table.delegate = self
        table.dataSource = self
        table.rowHeight = 54
        table.usesAlternatingRowBackgroundColors = true
        table.allowsEmptySelection = true
        table.target = self
        table.doubleAction = #selector(primaryAction)
        let listScroll = NSScrollView()
        listScroll.hasVerticalScroller = true
        listScroll.documentView = table
        listScroll.autohidesScrollers = true

        titleLabel.font = .systemFont(ofSize: 17, weight: .semibold)
        titleLabel.lineBreakMode = .byTruncatingTail
        factsLabel.font = .systemFont(ofSize: 11)
        factsLabel.textColor = .secondaryLabelColor
        primaryButton.bezelStyle = .rounded
        primaryButton.target = self
        primaryButton.action = #selector(primaryAction)
        deleteButton.bezelStyle = .rounded
        deleteButton.target = self
        deleteButton.action = #selector(deleteAction)
        let actions = NSStackView(views: [primaryButton, deleteButton])
        actions.orientation = .horizontal
        actions.spacing = 8

        detail.frame = NSRect(x: 0, y: 0, width: 420, height: 420)
        detail.minSize = .zero
        detail.maxSize = NSSize(width: .greatestFiniteMagnitude, height: .greatestFiniteMagnitude)
        detail.isEditable = false
        detail.isSelectable = true
        detail.isRichText = false
        detail.font = .systemFont(ofSize: 13)
        detail.textColor = .labelColor
        detail.backgroundColor = .textBackgroundColor
        detail.textContainerInset = NSSize(width: 10, height: 10)
        detail.isVerticallyResizable = true
        detail.isHorizontallyResizable = false
        detail.autoresizingMask = [.width]
        detail.textContainer?.widthTracksTextView = true
        let detailScroll = NSScrollView()
        detailScroll.hasVerticalScroller = true
        detailScroll.autohidesScrollers = true
        detailScroll.borderType = .bezelBorder
        detailScroll.documentView = detail
        let detailStack = NSStackView(views: [titleLabel, factsLabel, detailScroll, actions])
        detailStack.orientation = .vertical
        detailStack.alignment = .leading
        detailStack.spacing = 10
        for view in [titleLabel, factsLabel, detailScroll] { view.widthAnchor.constraint(equalTo: detailStack.widthAnchor).isActive = true }
        let detailContainer = NSView()
        detailContainer.addSubview(detailStack)
        detailStack.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            detailStack.leadingAnchor.constraint(equalTo: detailContainer.leadingAnchor, constant: 16),
            detailStack.trailingAnchor.constraint(equalTo: detailContainer.trailingAnchor),
            detailStack.topAnchor.constraint(equalTo: detailContainer.topAnchor),
            detailStack.bottomAnchor.constraint(equalTo: detailContainer.bottomAnchor)
        ])
        let split = NSSplitView()
        split.isVertical = true
        split.dividerStyle = .thin
        split.addArrangedSubview(listScroll)
        split.addArrangedSubview(detailContainer)
        let root = NSStackView(views: [top, split])
        root.orientation = .vertical
        root.alignment = .leading
        root.spacing = 12
        content.addSubview(root)
        root.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            root.leadingAnchor.constraint(equalTo: content.leadingAnchor, constant: 16),
            root.trailingAnchor.constraint(equalTo: content.trailingAnchor, constant: -16),
            root.topAnchor.constraint(equalTo: content.topAnchor, constant: 16),
            root.bottomAnchor.constraint(equalTo: content.bottomAnchor, constant: -16),
            top.widthAnchor.constraint(equalTo: root.widthAnchor),
            split.widthAnchor.constraint(equalTo: root.widthAnchor),
            listScroll.widthAnchor.constraint(greaterThanOrEqualToConstant: 220),
            detailContainer.widthAnchor.constraint(greaterThanOrEqualToConstant: 300)
        ])
        split.setPosition(290, ofDividerAt: 0)
    }

    func numberOfRows(in tableView: NSTableView) -> Int { isHistory ? visibleOperations.count : isDrafts ? visibleDrafts.count : visibleCards.count }

    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
        let cell = NSTableCellView()
        let title: String
        let subtitle: String
        if isDrafts {
            let pending = visibleDrafts[row]
            title = pending.drafts.first?.card.title ?? "待恢复编辑"
            subtitle = "\(pending.drafts.count) 张卡片 · 保留原卡片内容"
        } else if isHistory {
            let op = visibleOperations[row]
            title = CLI.opName(op.name)
            subtitle = "卡片 #\(op.cardId) · \(dateString(op.at))"
        } else {
            let card = visibleCards[row]
            title = card.title.isEmpty ? "（空白卡片）" : card.title
            subtitle = "\(kindName(card.kind)) · #\(card.id)"
        }
        let name = NSTextField(labelWithString: title)
        name.font = .systemFont(ofSize: 13, weight: .medium)
        name.lineBreakMode = .byTruncatingTail
        let facts = NSTextField(labelWithString: subtitle)
        facts.font = .systemFont(ofSize: 11)
        facts.textColor = .secondaryLabelColor
        facts.lineBreakMode = .byTruncatingTail
        let stack = NSStackView(views: [name, facts])
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 3
        cell.addSubview(stack)
        stack.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: cell.leadingAnchor, constant: 8),
            stack.trailingAnchor.constraint(equalTo: cell.trailingAnchor, constant: -8),
            stack.centerYAnchor.constraint(equalTo: cell.centerYAnchor),
            name.widthAnchor.constraint(equalTo: stack.widthAnchor),
            facts.widthAnchor.constraint(equalTo: stack.widthAnchor)
        ])
        cell.textField = name
        return cell
    }

    func tableViewSelectionDidChange(_ notification: Notification) { updateDetail() }
    func controlTextDidChange(_ notification: Notification) { refresh() }
    @objc private func filterChanged() {
        search.placeholderString = isDrafts ? "搜索待恢复的编辑内容" : isHistory ? "搜索操作或修改前的内容" : "搜索标题、正文、摘要"
        refresh()
    }

    private func updateDetail() {
        if let pending = selectedDraft {
            titleLabel.stringValue = "待恢复编辑"
            factsLabel.stringValue = "\(pending.drafts.count) 张卡片；恢复时另建卡片，原内容继续保留。"
            do {
                detail.string = try pending.drafts.map { draft in
                    let current = try store.get(draft.card.id)
                    var snapshot = draft.editorText ?? draft.card.body
                    if let addition = draft.addText, !addition.isEmpty { snapshot += "\n\n未提交清单项：\n" + addition }
                    return "卡片 #\(draft.card.id)\n\n当前卡片：\n\(current?.body ?? "原卡片已不存在。")\n\n待恢复编辑：\n\(snapshot)"
                }.joined(separator: "\n\n────────────\n\n")
            } catch { desktop?.reportError(error) }
            primaryButton.title = "另建卡片恢复"
            primaryButton.isEnabled = true
            deleteButton.isHidden = true
        } else if let op = selectedOp {
            titleLabel.stringValue = CLI.opName(op.name)
            factsLabel.stringValue = "卡片 #\(op.cardId) · \(dateString(op.at)) · 操作组 #\(op.grp)"
            detail.string = "恢复时，同一操作组内的卡片改动会一起撤销。\n\n修改前：\n\n" + (op.before == nil ? "这张卡片尚未创建。" : previousBody(op))
            primaryButton.title = "恢复这组操作"
            primaryButton.isEnabled = true
            deleteButton.isHidden = true
        } else if let card = selectedCard {
            titleLabel.stringValue = card.title.isEmpty ? "（空白卡片）" : card.title
            var facts = [kindName(card.kind), "#\(card.id)"]
            if let at = card.deletedAt { facts.append("删除于 \(dateString(at))") }
            else if let at = card.doneAt { facts.append("完成于 \(dateString(at))") }
            if let source = card.src, !source.isEmpty { facts.append(source) }
            factsLabel.stringValue = facts.joined(separator: " · ")
            detail.string = card.body
            if let summary = card.summary, !summary.isEmpty { detail.string += "\n\n摘要：\n" + summary }
            primaryButton.title = card.active ? "显示在桌面" : "恢复并显示"
            primaryButton.isEnabled = true
            deleteButton.title = card.deletedAt == nil ? "删除" : "永久删除…"
            deleteButton.isHidden = false
            deleteButton.isEnabled = true
        } else {
            titleLabel.stringValue = isHistory ? "选择一条操作记录" : isDrafts ? "选择一组待恢复编辑" : "选择一张卡片"
            factsLabel.stringValue = ""
            detail.string = (isHistory ? visibleOperations.isEmpty : isDrafts ? visibleDrafts.isEmpty : visibleCards.isEmpty) ? "这里还没有记录。" : ""
            primaryButton.isEnabled = false
            deleteButton.isEnabled = false
            deleteButton.isHidden = isHistory || isDrafts
        }
        detail.scrollRangeToVisible(NSRange(location: 0, length: 0))
    }

    @objc private func primaryAction() {
        if let pending = selectedDraft { desktop?.libraryRestoreDraft(pending) }
        else if let op = selectedOp { desktop?.libraryUndo(group: op.grp) }
        else if let card = selectedCard {
            if card.active { desktop?.libraryShowCard(id: card.id) }
            else { desktop?.libraryRestoreCard(id: card.id) }
        }
        refresh()
    }
    @objc private func deleteAction() {
        guard let card = selectedCard else { return }
        guard card.deletedAt != nil else {
            desktop?.libraryDeleteCard(id: card.id, permanent: false)
            refresh()
            return
        }
        guard let window else { return }
        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = "永久删除这张卡片？"
        alert.informativeText = "“\(card.title.isEmpty ? "空白卡片" : card.title)”及其操作记录会被永久移除。此操作无法撤销。"
        alert.addButton(withTitle: "取消")
        alert.addButton(withTitle: "永久删除")
        alert.buttons.last?.hasDestructiveAction = true
        alert.beginSheetModal(for: window) { [weak self] response in
            guard response == .alertSecondButtonReturn, let self else { return }
            self.desktop?.libraryDeleteCard(id: card.id, permanent: true)
            self.refresh()
        }
    }

    private func matchesQuery(_ text: String, _ query: String) -> Bool {
        text.range(of: query, options: .caseInsensitive, locale: Locale(identifier: "en_US_POSIX")) != nil
    }

    private func previousBody(_ operation: Op) -> String {
        guard let before = operation.before,
              let card = try? Card.fromJSON(before) else { return "无法读取修改前的内容。" }
        return card.body
    }

    private func kindName(_ kind: String) -> String {
        ["link": "链接", "file": "文档", "image": "图片", "task": "清单"][kind] ?? "笔记"
    }

    private func dateString(_ milliseconds: Int64) -> String {
        Date(timeIntervalSince1970: TimeInterval(milliseconds) / 1_000).formatted(date: .abbreviated, time: .shortened)
    }
}
