import AppKit
import PinCCCore

/// AppKit owns composition, selection, clipboard text and editing undo.
@MainActor
final class CardEditor: NSScrollView, NSTextViewDelegate {
    let textView = CardTextView(frame: .zero)
    var changed: ((String) -> Void)?
    var committed: (() -> Void)?
    var submittedLine: (() -> Void)?
    var singleLine = false { didSet { textView.allowsMarkdownCommands = !singleLine } }
    var commandScrolled: ((NSEvent) -> Void)?
    var ingestPasteboard: ((NSPasteboard) -> Void)?

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        drawsBackground = false
        hasVerticalScroller = false
        hasHorizontalScroller = false
        autohidesScrollers = true
        borderType = .noBorder
        textView.delegate = self
        textView.isRichText = false
        textView.importsGraphics = false
        textView.allowsUndo = true
        textView.drawsBackground = false
        textView.isVerticallyResizable = true
        textView.isHorizontallyResizable = false
        textView.minSize = .zero
        textView.maxSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
        textView.autoresizingMask = [.width]
        textView.textContainerInset = NSSize(width: 0, height: 2)
        textView.textContainer?.lineFragmentPadding = 0
        textView.textContainer?.widthTracksTextView = true
        textView.isAutomaticQuoteSubstitutionEnabled = false
        textView.isAutomaticDashSubstitutionEnabled = false
        textView.isAutomaticTextReplacementEnabled = false
        textView.isAutomaticSpellingCorrectionEnabled = false
        textView.isContinuousSpellCheckingEnabled = false
        textView.isGrammarCheckingEnabled = false
        textView.isAutomaticTextCompletionEnabled = false
        textView.smartInsertDeleteEnabled = false
        textView.isAutomaticLinkDetectionEnabled = false
        textView.isAutomaticDataDetectionEnabled = false
        textView.usesFindBar = false
        textView.font = .systemFont(ofSize: 14)
        let paragraph = NSMutableParagraphStyle()
        paragraph.minimumLineHeight = 21
        paragraph.maximumLineHeight = 21
        textView.defaultParagraphStyle = paragraph
        textView.formattingCommand = { [weak self] event in self?.formattingCommand(event) ?? false }
        textView.afterSpace = { [weak self] in self?.rewriteTaskShortcut() }
        textView.ingestPasteboard = { [weak self] board in self?.ingestPasteboard?(board) }
        documentView = textView
        setAccessibilityLabel("卡片正文编辑器")
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) is unavailable") }

    func begin(body: String, selection: NSRange? = nil, ink: NSColor, accent: NSColor, textSize: CGFloat) {
        textView.string = body
        textView.font = .systemFont(ofSize: textSize)
        let paragraph = NSMutableParagraphStyle()
        paragraph.minimumLineHeight = (textSize * 1.5).rounded()
        paragraph.maximumLineHeight = paragraph.minimumLineHeight
        textView.defaultParagraphStyle = paragraph
        textView.textColor = ink
        textView.insertionPointColor = accent
        textView.selectedTextAttributes = [.backgroundColor: accent.withAlphaComponent(0.25)]
        textView.undoManager?.removeAllActions()
        let length = (body as NSString).length
        let requested = selection ?? NSRange(location: length, length: 0)
        let position = min(requested.location, length)
        textView.setSelectedRange(NSRange(location: position, length: min(requested.length, length - position)))
        window?.makeFirstResponder(textView)
        textView.scrollRangeToVisible(textView.selectedRange())
    }

    var contentHeight: CGFloat {
        guard let manager = textView.layoutManager, let container = textView.textContainer else { return 21 }
        manager.ensureLayout(for: container)
        return ceil(manager.usedRect(for: container).height) + textView.textContainerInset.height * 2
    }

    override func scrollWheel(with event: NSEvent) {
        if event.modifierFlags.contains(.command) { commandScrolled?(event) }
        else { super.scrollWheel(with: event) }
    }

    func textDidChange(_ notification: Notification) { changed?(textView.string) }

    func textView(_ textView: NSTextView, doCommandBy commandSelector: Selector) -> Bool {
        // Candidate confirmation and cancellation belong to the input method.
        guard !textView.hasMarkedText() else { return false }
        if commandSelector == #selector(NSResponder.cancelOperation(_:)) {
            committed?()
            return true
        }
        if singleLine && commandSelector == #selector(NSResponder.insertNewline(_:)) {
            submittedLine?()
            return true
        }
        if commandSelector == #selector(NSResponder.insertTab(_:)) || commandSelector == #selector(NSResponder.insertBacktab(_:)) {
            let source = textView.string as NSString
            let selection = textView.selectedRange()
            let lineRange = source.lineRange(for: NSRange(location: selection.location, length: 0))
            let line = source.substring(with: lineRange)
            guard let block = Markdown.blocks(line).first, [BlockKind.task, .bullet, .ordered].contains(block.kind) else { return false }
            let backward = commandSelector == #selector(NSResponder.insertBacktab(_:))
            let count = backward ? (line.hasPrefix("\t") ? 1 : line.hasPrefix("  ") ? 2 : line.hasPrefix(" ") ? 1 : 0) : 0
            if backward && count == 0 { return true }
            let replacement = backward ? "" : "  "
            let target = NSRange(location: lineRange.location, length: count)
            guard textView.shouldChangeText(in: target, replacementString: replacement) else { return true }
            textView.textStorage?.replaceCharacters(in: target, with: replacement)
            let delta = (replacement as NSString).length - count
            let nextLocation = max(lineRange.location, selection.location + delta)
            let remaining = (textView.string as NSString).length - nextLocation
            textView.setSelectedRange(NSRange(location: nextLocation, length: min(selection.length, max(0, remaining))))
            textView.didChangeText()
            return true
        }
        if commandSelector == #selector(NSResponder.insertNewline(_:)) {
            let source = textView.string as NSString
            let selection = textView.selectedRange()
            guard selection.length == 0 else { return false }
            let range = source.lineRange(for: selection)
            var line = source.substring(with: range)
            while line.last == "\n" || line.last == "\r" { line.removeLast() }
            guard selection.location == range.location + (line as NSString).length else { return false }
            guard let prefix = Markdown.continueList(line) else { return false }
            let replacement = prefix.isEmpty ? "" : "\n" + prefix
            let target = prefix.isEmpty ? NSRange(location: range.location, length: selection.location - range.location) : selection
            guard textView.shouldChangeText(in: target, replacementString: replacement) else { return true }
            textView.textStorage?.replaceCharacters(in: target, with: replacement)
            textView.setSelectedRange(NSRange(location: target.location + (replacement as NSString).length, length: 0))
            textView.didChangeText()
            return true
        }
        return false
    }

    private func formattingCommand(_ event: NSEvent) -> Bool {
        guard !singleLine, !textView.hasMarkedText() else { return false }
        let flags = event.modifierFlags.intersection([.command, .shift, .option, .control])
        let key = event.charactersIgnoringModifiers?.lowercased() ?? ""
        let mark: String?
        switch (flags, key) {
        case (.command, "b"): mark = "**"
        case (.command, "i"): mark = "*"
        case ([.command, .shift], "x"): mark = "~~"
        case ([.command, .option], "c"): mark = "`"
        case ([.command, .shift], "l"): togglePrefix(task: true); return true
        case ([.command, .shift], "8"), ([.command, .shift], "*"): togglePrefix(task: false); return true
        default: return false
        }
        guard let mark else { return false }
        let selected = textView.selectedRange()
        let value = (textView.string as NSString).substring(with: selected)
        let replacement = mark + value + mark
        let next = selected.length == 0 ? NSRange(location: selected.location + mark.utf16.count, length: 0) : NSRange(location: selected.location, length: replacement.utf16.count)
        replace(selected, with: replacement, selection: next)
        return true
    }

    private func togglePrefix(task: Bool) {
        let source = textView.string as NSString
        let caret = textView.selectedRange().location
        var line = source.lineRange(for: NSRange(location: caret, length: 0))
        var value = source.substring(with: line)
        while value.last == "\n" || value.last == "\r" { value.removeLast() }
        line.length = value.utf16.count
        let trimmed = String(value.drop(while: { $0.isWhitespace }))
        let indent = value.utf16.count - trimmed.utf16.count
        let taskPrefix = trimmed.range(of: #"^[-*+]\s+\[[ xX]\]\s?"#, options: .regularExpression)
        let bulletPrefix = trimmed.range(of: #"^[-*+]\s+"#, options: .regularExpression)
        let existing = taskPrefix ?? bulletPrefix
        let rest = existing.map { String(trimmed[$0.upperBound...]) } ?? trimmed
        let hadKind = task ? taskPrefix != nil : bulletPrefix != nil && taskPrefix == nil
        let replacement = hadKind ? rest : (task ? "- [ ] " : "- ") + rest
        let delta = replacement.utf16.count - trimmed.utf16.count
        replace(NSRange(location: line.location + indent, length: trimmed.utf16.count), with: replacement, selection: NSRange(location: max(line.location, min(source.length + delta, caret + delta)), length: 0))
    }

    private func rewriteTaskShortcut() {
        guard !singleLine, !textView.hasMarkedText() else { return }
        let source = textView.string as NSString
        let caret = textView.selectedRange().location
        let line = source.lineRange(for: NSRange(location: caret, length: 0))
        let before = source.substring(with: NSRange(location: line.location, length: caret - line.location))
        let trimmed = String(before.drop(while: { $0.isWhitespace }))
        guard trimmed.hasSuffix(" "), Markdown.isTaskShortcut(String(trimmed.dropLast())) else { return }
        let indent = before.utf16.count - trimmed.utf16.count
        let range = NSRange(location: line.location + indent, length: trimmed.utf16.count)
        replace(range, with: "- [ ] ", selection: NSRange(location: range.location + 6, length: 0))
    }

    private func replace(_ range: NSRange, with value: String, selection: NSRange) {
        guard textView.shouldChangeText(in: range, replacementString: value) else { return }
        textView.textStorage?.replaceCharacters(in: range, with: value)
        textView.setSelectedRange(selection)
        textView.didChangeText()
    }
}

@MainActor
final class CardTextView: NSTextView {
    var ingestPasteboard: ((NSPasteboard) -> Void)?
    var allowsMarkdownCommands = true
    var formattingCommand: ((NSEvent) -> Bool)?
    var afterSpace: (() -> Void)?
    override var needsPanelToBecomeKey: Bool { true }
    override var readablePasteboardTypes: [NSPasteboard.PasteboardType] {
        [.fileURL, .png, .tiff] + super.readablePasteboardTypes
    }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func performKeyEquivalent(with event: NSEvent) -> Bool {
        if allowsMarkdownCommands && formattingCommand?(event) == true { return true }
        return super.performKeyEquivalent(with: event)
    }

    override func keyDown(with event: NSEvent) {
        if !allowsMarkdownCommands || formattingCommand?(event) != true { super.keyDown(with: event) }
    }

    override func insertText(_ insertString: Any, replacementRange: NSRange) {
        if let text = insertString as? String {
            let normalized = text.replacingOccurrences(of: "\u{2028}", with: "\n").replacingOccurrences(of: "\u{2029}", with: "\n")
            super.insertText(normalized, replacementRange: replacementRange)
            if text == " " && allowsMarkdownCommands { afterSpace?() }
        } else {
            super.insertText(insertString, replacementRange: replacementRange)
        }
    }

    override func paste(_ sender: Any?) {
        let board = NSPasteboard.general
        let hasFiles = board.canReadObject(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true])
        if hasFiles || (board.string(forType: .string) == nil && board.canReadObject(forClasses: [NSImage.self], options: nil)) {
            ingestPasteboard?(board)
        } else {
            super.pasteAsPlainText(sender)
        }
    }

    override func pasteAsPlainText(_ sender: Any?) { paste(sender) }

    override func validateUserInterfaceItem(_ item: any NSValidatedUserInterfaceItem) -> Bool {
        if item.action == #selector(paste(_:)) || item.action == #selector(pasteAsPlainText(_:)) { return true }
        return super.validateUserInterfaceItem(item)
    }
}






