import Foundation

public enum BlockKind: String, Codable, Sendable {
    case paragraph, heading, bullet, ordered, task, quote, code, rule, image
}

/// The line is the zero-based source line, never a rendered-block index.
public struct MdBlock: Equatable, Sendable {
    public var kind: BlockKind
    public var text: String
    public var line: Int
    public var level: Int
    public var indent: Int
    public var checked: Bool
    public var marker: String
    public init(kind: BlockKind, text: String, line: Int, level: Int = 0, indent: Int = 0, checked: Bool = false, marker: String = "") {
        self.kind = kind; self.text = text; self.line = line; self.level = level
        self.indent = indent; self.checked = checked; self.marker = marker
    }
}

public struct Fmt: OptionSet, Equatable, Sendable {
    public let rawValue: Int
    public init(rawValue: Int) { self.rawValue = rawValue }
    public static let bold = Fmt(rawValue: 1)
    public static let italic = Fmt(rawValue: 2)
    public static let strike = Fmt(rawValue: 4)
    public static let code = Fmt(rawValue: 8)
}

public struct Seg: Equatable, Sendable {
    public let text: String
    public let fmt: Fmt
    public let url: String?
    public init(text: String, fmt: Fmt = [], url: String? = nil) { self.text = text; self.fmt = fmt; self.url = url }
}

/// The small Markdown subset used by cards. Newlines are explicit line breaks.
public enum Markdown {
    public static func blocks(_ text: String) -> [MdBlock] {
        let lines = text.replacingOccurrences(of: "\r\n", with: "\n").replacingOccurrences(of: "\r", with: "\n").components(separatedBy: "\n")
        var result: [MdBlock] = []
        var i = 0
        while i < lines.count {
            let line = lines[i]
            if trim(line).isEmpty { i += 1; continue }
            let start = i
            if line.trimmingCharacters(in: .whitespaces).hasPrefix("```") {
                var code = ""
                i += 1
                while i < lines.count && !lines[i].trimmingCharacters(in: .whitespaces).hasPrefix("```") {
                    code += (code.isEmpty ? "" : "\n") + lines[i]
                    i += 1
                }
                result.append(MdBlock(kind: .code, text: code, line: start))
            } else if let m = match(heading, line) {
                result.append(MdBlock(kind: .heading, text: trim(capture(m, 2, line)), line: i, level: capture(m, 1, line).count))
            } else if match(rule, line) != nil {
                result.append(MdBlock(kind: .rule, text: "", line: i))
            } else if let m = match(image, line) {
                result.append(MdBlock(kind: .image, text: capture(m, 2, line), line: i, marker: capture(m, 1, line)))
            } else if let m = match(task, line) {
                result.append(MdBlock(kind: .task, text: capture(m, 4, line), line: i, indent: indentation(capture(m, 1, line)), checked: capture(m, 3, line) != " "))
            } else if let m = match(bullet, line) {
                result.append(MdBlock(kind: .bullet, text: capture(m, 3, line), line: i, indent: indentation(capture(m, 1, line))))
            } else if let m = match(ordered, line) {
                result.append(MdBlock(kind: .ordered, text: capture(m, 3, line), line: i, indent: indentation(capture(m, 1, line)), marker: capture(m, 2, line)))
            } else if let m = match(quote, line) {
                var content = capture(m, 1, line)
                while i + 1 < lines.count, let next = match(quote, lines[i + 1]) {
                    i += 1
                    content += "\n" + capture(next, 1, lines[i])
                }
                result.append(MdBlock(kind: .quote, text: content, line: start))
            } else {
                var content = trim(line)
                while i + 1 < lines.count && isParagraph(lines[i + 1]) { i += 1; content += "\n" + trim(lines[i]) }
                result.append(MdBlock(kind: .paragraph, text: content, line: start))
            }
            i += 1
        }
        return result
    }

    public static func inlines(_ text: String) -> [Seg] {
        var result: [Seg] = []
        var buffer = ""
        var style: Fmt = []
        var i = text.startIndex
        func flush() {
            if !buffer.isEmpty { result.append(Seg(text: buffer, fmt: style)); buffer = "" }
        }
        while i < text.endIndex {
            let c = text[i]
            let next = text.index(after: i)
            if c == "\\", next < text.endIndex, "\\`*_~[]()#!".contains(text[next]) {
                buffer.append(text[next]); i = text.index(after: next); continue
            }
            if c == "`", let end = text[next...].firstIndex(of: "`") {
                flush()
                result.append(Seg(text: String(text[next..<end]), fmt: style.union(.code)))
                i = text.index(after: end); continue
            }
            if c == "[", let m = matchAt(link, text, i), let range = Range(m.range, in: text) {
                flush()
                let label = capture(m, 1, text), target = capture(m, 2, text)
                result.append(Seg(text: label.isEmpty ? target : label, fmt: style, url: target))
                i = range.upperBound; continue
            }
            let previous: Character? = i == text.startIndex ? nil : text[text.index(before: i)]
            if (c == "h" || c == "H"), previous.map({ !isASCIIAlphanumeric($0) }) ?? true,
               let m = matchAt(url, text, i) {
                flush()
                let target = trimURL(capture(m, 0, text))
                result.append(Seg(text: target, fmt: style, url: target))
                i = text.index(i, offsetBy: target.count); continue
            }
            if c == "#", previous.map(isWhitespace) ?? true, let m = matchAt(tag, text, i), let range = Range(m.range, in: text) {
                flush()
                let value = capture(m, 0, text)
                result.append(Seg(text: value, fmt: style, url: value))
                i = range.upperBound; continue
            }
            var matched = false
            for (delimiter, flag) in [("**", Fmt.bold), ("__", .bold), ("~~", .strike), ("*", .italic), ("_", .italic)] {
                if delimiter == "_", !isWordEdge(text, i) { continue }
                guard text[i...].hasPrefix(delimiter) else { continue }
                if delimiter.count == 1, next < text.endIndex, text[next] == c { continue }
                let after = text.index(i, offsetBy: delimiter.count)
                if !style.contains(flag) {
                    guard after < text.endIndex, !isWhitespace(text[after]), text.range(of: delimiter, range: after..<text.endIndex) != nil else { continue }
                }
                flush()
                style = Fmt(rawValue: style.rawValue ^ flag.rawValue)
                i = after; matched = true; break
            }
            if matched { continue }
            buffer.append(c); i = next
        }
        flush()
        return result
    }

    public static func plain(_ line: String) -> String {
        var text = trim(line)
        if text.hasPrefix("```") || match(rule, text) != nil { return "" }
        for (regex, group) in [(heading, 2), (task, 4), (bullet, 3), (ordered, 3), (quote, 1)] {
            if let m = match(regex, text) { text = capture(m, group, text) }
        }
        if match(image, text) != nil { return "" }
        return trim(inlines(text).map(\.text).joined())
    }

    /// Like the shared C# editor, a successful mutation normalizes CRLF to LF.
    /// Invalid targets and already-correct checks return the original source unchanged.
    public static func toggleTask(_ text: String, line: Int) -> String {
        var lines = text.replacingOccurrences(of: "\r\n", with: "\n").components(separatedBy: "\n")
        guard lines.indices.contains(line), let m = match(task, lines[line]),
              let range = Range(m.range(at: 3), in: lines[line]) else { return text }
        let replacement = capture(m, 3, lines[line]) == " " ? "x" : " "
        lines[line].replaceSubrange(range, with: replacement)
        return lines.joined(separator: "\n")
    }

    public static func setTask(_ text: String, line: Int, done: Bool) -> String {
        let lines = text.replacingOccurrences(of: "\r\n", with: "\n").components(separatedBy: "\n")
        guard lines.indices.contains(line), let m = match(task, lines[line]),
              (capture(m, 3, lines[line]) != " ") != done else { return text }
        return toggleTask(text, line: line)
    }

    public static func taskLines(_ text: String) -> [Int] { blocks(text).filter { $0.kind == .task }.map(\.line) }

    public static func addTasks(_ text: String, text additions: String) -> String {
        let items = additions.replacingOccurrences(of: "\r\n", with: "\n").components(separatedBy: "\n").map { line -> String in
            let value = trim(line)
            guard let m = match(itemPrefix, value), let range = Range(m.range, in: value) else { return value }
            return trim(String(value[range.upperBound...]))
        }.filter { !$0.isEmpty }
        guard !items.isEmpty else { return text }
        let normalized = text.replacingOccurrences(of: "\r\n", with: "\n")
        var lines = normalized.components(separatedBy: "\n")
        let tasks = taskLines(text)
        guard !tasks.isEmpty else {
            var body = normalized
            while let last = body.last, isWhitespace(last) { body.removeLast() }
            return (body.isEmpty ? "" : body + "\n") + items.map { "- [ ] " + $0 }.joined(separator: "\n")
        }
        let matches = tasks.compactMap { index -> (indent: String, marker: String)? in
            guard lines.indices.contains(index), let m = match(task, lines[index]) else { return nil }
            return (capture(m, 1, lines[index]), capture(m, 2, lines[index]))
        }
        guard let outer = matches.min(by: { $0.indent.utf16.count < $1.indent.utf16.count }),
              let last = tasks.last, lines.indices.contains(last) else { return text }
        var at = last + 1
        while at < lines.count && !trim(lines[at]).isEmpty && leadingWhitespace(lines[at]) > outer.indent.utf16.count { at += 1 }
        lines.insert(contentsOf: items.map { outer.indent + outer.marker + " [ ] " + $0 }, at: at)
        return lines.joined(separator: "\n")
    }

    /// Returns block indexes to hide. It never deletes completed tasks from the source.
    public static func foldDone(_ blocks: [MdBlock], min: Int = 3) -> Set<Int>? {
        guard blocks.filter({ $0.kind == .task && $0.checked }).count >= min,
              blocks.contains(where: { $0.kind == .task && !$0.checked }) else { return nil }
        var hidden: Set<Int> = []
        var i = 0
        while i < blocks.count {
            if blocks[i].kind == .task && blocks[i].checked {
                let indent = blocks[i].indent
                hidden.insert(i)
                while i + 1 < blocks.count && [BlockKind.bullet, .ordered, .task].contains(blocks[i + 1].kind) && blocks[i + 1].indent > indent {
                    i += 1; hidden.insert(i)
                }
            }
            i += 1
        }
        return hidden
    }

    /// nil means non-list; empty means remove the empty item's marker; otherwise insert the prefix.
    public static func continueList(_ line: String) -> String? {
        if let m = match(task, line) { return trim(capture(m, 4, line)).isEmpty ? "" : capture(m, 1, line) + capture(m, 2, line) + " [ ] " }
        if let m = match(bullet, line) { return trim(capture(m, 3, line)).isEmpty ? "" : capture(m, 1, line) + capture(m, 2, line) + " " }
        if let m = match(ordered, line) {
            return trim(capture(m, 3, line)).isEmpty ? "" : capture(m, 1, line) + String((Int(capture(m, 2, line)) ?? 0) + 1) + ". "
        }
        if let m = match(quote, line) { return trim(capture(m, 1, line)).isEmpty ? "" : "> " }
        return nil
    }
    public static func isTaskShortcut(_ lineBeforeCaret: String) -> Bool { lineBeforeCaret == "[]" || lineBeforeCaret == "[ ]" }

    private static func isParagraph(_ line: String) -> Bool {
        !trim(line).isEmpty && !line.trimmingCharacters(in: .whitespaces).hasPrefix("```") &&
            [heading, rule, image, task, bullet, ordered, quote].allSatisfy { match($0, line) == nil }
    }
    private static func trim(_ text: String) -> String { text.trimmingCharacters(in: .whitespacesAndNewlines) }
    private static func indentation(_ whitespace: String) -> Int { Swift.min(3, whitespace.replacingOccurrences(of: "\t", with: "  ").utf16.count / 2) }
    private static func leadingWhitespace(_ text: String) -> Int { text.prefix(while: isWhitespace).reduce(0) { $0 + String($1).utf16.count } }
    private static func isWhitespace(_ character: Character) -> Bool { character.unicodeScalars.allSatisfy { CharacterSet.whitespacesAndNewlines.contains($0) } }
    // The C# delimiter boundary uses char (one UTF-16 code unit), not a grapheme.
    private static func isAlphanumeric(_ codeUnit: UInt16) -> Bool {
        guard let scalar = UnicodeScalar(UInt32(codeUnit)) else { return false }
        return CharacterSet.letters.contains(scalar) || CharacterSet.decimalDigits.contains(scalar)
    }
    private static func isASCIIAlphanumeric(_ character: Character) -> Bool { character.unicodeScalars.allSatisfy { (48...57).contains($0.value) || (65...90).contains($0.value) || (97...122).contains($0.value) } }
    private static func isWordEdge(_ text: String, _ index: String.Index) -> Bool {
        let next = text.index(after: index)
        return index == text.startIndex || !isAlphanumeric(String(text[text.index(before: index)]).utf16.last!) || next == text.endIndex || !isAlphanumeric(String(text[next]).utf16.first!)
    }
    private static func trimURL(_ text: String) -> String {
        var value = text
        while let last = value.last {
            if ".,;:!?\"'。，；）".contains(last) || last == ")" && value.filter({ $0 == ")" }).count > value.filter({ $0 == "(" }).count { value.removeLast() }
            else { break }
        }
        return value
    }
    private static func match(_ regex: NSRegularExpression, _ text: String) -> NSTextCheckingResult? {
        regex.firstMatch(in: text, range: NSRange(text.startIndex..., in: text))
    }
    private static func matchAt(_ regex: NSRegularExpression, _ text: String, _ index: String.Index) -> NSTextCheckingResult? {
        let range = NSRange(index..<text.endIndex, in: text)
        guard let result = regex.firstMatch(in: text, range: range), result.range.location == range.location else { return nil }
        return result
    }
    private static func capture(_ match: NSTextCheckingResult, _ group: Int, _ text: String) -> String {
        guard group < match.numberOfRanges, let range = Range(match.range(at: group), in: text) else { return "" }
        return String(text[range])
    }
    private static let heading = try! NSRegularExpression(pattern: #"^\s{0,3}(#{1,6})\s+(.*)$"#)
    private static let rule = try! NSRegularExpression(pattern: #"^\s{0,3}([-*_])(\s*\1){2,}\s*$"#)
    private static let image = try! NSRegularExpression(pattern: #"^\s*!\[([^\]]*)\]\(([^)\s]+)\)\s*$"#)
    private static let task = try! NSRegularExpression(pattern: #"^(\s*)([-*+])\s+\[([ xX])\](?:\s+(.*))?$"#)
    private static let bullet = try! NSRegularExpression(pattern: #"^(\s*)([-*+])\s+(.*)$"#)
    private static let itemPrefix = try! NSRegularExpression(pattern: #"^(?:[-*+]\s+)?(?:\[[ xX]?\](?:\s+|$))?"#)
    private static let ordered = try! NSRegularExpression(pattern: #"^(\s*)(\d{1,3})[.)]\s+(.*)$"#)
    private static let quote = try! NSRegularExpression(pattern: #"^\s{0,3}>\s?(.*)$"#)
    private static let link = try! NSRegularExpression(pattern: #"\[([^\]]*)\]\(((?:[^()\s]|\([^()\s]*\))+)\)"#)
    private static let url = try! NSRegularExpression(pattern: #"https?://[^\s<>"'`，。；、）]+"#, options: .caseInsensitive)
    private static let tag = try! NSRegularExpression(pattern: #"#[[\p{L}\p{N}_\-]&&[\u0000-\uFFFF]]{1,32}"#)
}
