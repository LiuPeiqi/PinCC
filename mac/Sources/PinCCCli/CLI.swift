import Foundation
import PinCCCore
import PinCCMacPlatform
#if canImport(Darwin)
import Darwin
#endif

/// The text interface shares the GUI's store and journal. One command is one undo group.
public enum CLI {
    public static let ok: Int32 = 0, usageError: Int32 = 1, notFound: Int32 = 2, conflict: Int32 = 3, busy: Int32 = 4
    private static let editOptions = ["summary", "remind", "done", "undone", "color", "top", "normal", "image", "add-task", "json"]
    private static let options: [String: [String]] = [
        "list": ["all", "json", "md"], "show": ["json"], "add": editOptions,
        "set": editOptions + ["append", "check", "uncheck", "if-rev", "if-input"],
        "rm": ["json"], "log": ["json"], "restore": ["json"], "skills": ["json"], "run": ["json"]
    ]
    private static let valueOptions: Set<String> = ["summary", "remind", "color", "image", "add-task", "check", "uncheck", "if-rev", "if-input"]

    /// `args` excludes the executable name. Standard input is read only for an explicit `-`.
    @MainActor
    public static func run(
        _ args: [String], home: String? = nil,
        output: (String) -> Void = { print($0) },
        readStdin: () -> String = { String(data: FileHandle.standardInput.readDataToEndOfFile(), encoding: .utf8) ?? "" }
    ) async -> Int32 {
        let verb = args.first?.lowercased() ?? "help"
        do {
            if ["help", "--help", "-h", "/?"].contains(verb) {
                output(args.dropFirst().contains("--skill") ? agentSkill() : help)
                return ok
            }
            guard let allowed = options[verb] else {
                throw CLIError.usage("未知命令「\(args.first ?? verb)」。可用：list show add set rm log restore skills run help")
            }
            let (pos, flags) = try split(Array(args.dropFirst()))
            if let bad = flags.keys.sorted().first(where: { !allowed.contains($0) }) {
                throw CLIError.usage("\(verb) 不支持 --\(bad)。可用选项：\(allowed.map { "--" + $0 }.joined(separator: " "))")
            }
            let json = flags["json"] != nil
            let store = try Store(home: home)
            defer { store.close() }
            let mode = try store.setting("color.mode", fallback: "rotate")
            switch verb {
            case "list":
                let cards = try (flags["all"] != nil ? store.all() : store.active()).sorted(by: Card.compare)
                if json { output(try jsonText(cards.map { try dto($0, store: store, mode: mode) })) }
                else if flags["md"] != nil { output(try deskMarkdown(store, cards: cards)) }
                else if cards.isEmpty { output("（没有卡片）") }
                else { cards.forEach { output(line($0)) } }
            case "show":
                let card = try need(store, pos)
                output(try json ? jsonText(dto(card, store: store, mode: mode)) : markdown(card, store: store))
            case "add":
                let body = try text(pos, offset: 0, flags: flags, readStdin: readStdin) ?? ""
                guard !body.isEmpty || flags["image"] != nil || flags["add-task"] != nil else {
                    throw CLIError.usage("add 需要内容：pincc add \"文字\"、pincc add -（标准输入）、--add-task 任务或 --image 图片")
                }
                let change = try changes(store, flags: flags, readStdin: readStdin, text: nil)
                var card = Card()
                card.body = body
                try change(&card)
                card = try store.insert(begin(store), card)
                output(try json ? jsonText(dto(card, store: store, mode: mode)) : "已添加 #\(card.id)")
            case "set":
                let old = try need(store, pos)
                let body = try text(pos, offset: 1, flags: flags, readStdin: readStdin)
                let ifRev: Int64?
                if let value = flags["if-rev"] {
                    guard let number = Int64(value.trimmingCharacters(in: .whitespacesAndNewlines)) else { throw CLIError.usage("--if-rev 需要有效的版本编号") }
                    ifRev = number
                } else { ifRev = nil }
                let change = try changes(store, flags: flags, readStdin: readStdin, text: body)
                let group = try begin(store)
                guard let card = try store.mutate(group, id: old.id, op: opFor(flags, text: body), ifRev: ifRev, change: { card in
                    if let expected = flags["if-input"], card.inputHash?.caseInsensitiveCompare(expected) != .orderedSame {
                        throw CLIError.inputChanged(card.inputHash)
                    }
                    try change(&card)
                }) else { throw CLIError.missing("找不到卡片 #\(old.id)") }
                output(try json ? jsonText(dto(card, store: store, mode: mode)) : group.empty ? "#\(card.id) 没有变化" : "已更新 #\(card.id)（撤销：pincc restore \(group.number)）")
            case "rm":
                let old = try need(store, pos)
                let group = try begin(store)
                guard let card = try store.mutate(group, id: old.id, op: "delete", change: { card in
                    if card.deletedAt == nil { card.deletedAt = When.nowMs }
                }) else { throw CLIError.missing("找不到卡片 #\(old.id)") }
                if json { output(try jsonText(dto(card, store: store, mode: mode))) }
                else if group.empty, let deleted = card.deletedAt { output("#\(card.id) 已经是删除状态（\(When.format(deleted)) 删除），未改动") }
                else { output("已删除 #\(card.id)（撤销：pincc restore \(group.number)）") }
            case "log":
                let limit: Int
                if let value = pos.first {
                    guard let n = Int32(value.trimmingCharacters(in: .whitespacesAndNewlines)) else { throw CLIError.usage("log 的条数需要整数") }
                    limit = Int(n)
                } else { limit = 30 }
                let ops = try store.ops(limit: limit)
                if json {
                    output(try jsonText(ops.map { ["grp": $0.grp, "op_id": $0.id, "at": iso($0.at), "card": $0.cardId, "op": $0.name] as [String: Any] }))
                } else {
                    for op in ops { output("\(pad(String(op.grp), width: 6, left: true))  \(dateString(op.at, pattern: "MM-dd HH:mm:ss"))  #\(pad(String(op.cardId), width: 5)) \(opName(op.name))") }
                }
            case "restore":
                guard let value = pos.first, let number = Int64(value.trimmingCharacters(in: .whitespacesAndNewlines)) else {
                    throw CLIError.usage("restore 需要操作组编号（见 pincc log 第一列）")
                }
                let ids = try store.restore(group: number)
                guard !ids.isEmpty else { throw CLIError.missing("找不到操作组 \(number)") }
                if json { output(try jsonText(["restored_grp": number, "cards": ids] as [String: Any])) }
                else { output("已恢复 \(ids.map { "#\($0)" }.joined(separator: "、")) 到操作组 \(number) 之前的状态") }
            case "skills":
                try printSkills(store, json: json, output: output)
            case "run":
                try await runSkill(store, pos: pos, json: json, output: output)
            default: break
            }
            return ok
        } catch let error as CLIError {
            return fail(error.code, error.localizedDescription, output: output)
        } catch let error as RevConflictError {
            return fail(conflict, error.localizedDescription + "，未写入。", output: output)
        } catch {
            let code = errorCode(error)
            let message = error is SQLiteError && code == busy ? "数据库正忙，请稍后重试" : error.localizedDescription
            return fail(code, message, output: output)
        }
    }

    private enum CLIError: LocalizedError {
        case usage(String), missing(String), inputChanged(String?)
        var code: Int32 {
            switch self { case .usage: return CLI.usageError; case .missing: return CLI.notFound; case .inputChanged: return CLI.conflict }
        }
        var errorDescription: String? {
            switch self {
            case .usage(let message), .missing(let message): return message
            case .inputChanged(let current):
                return (current.map { "卡片内容已改变（当前 input_hash=\($0)）" } ?? "卡片的来源还没有读取，没有 input_hash") + "，未写入。"
            }
        }
    }

    private static func split(_ args: [String]) throws -> ([String], [String: String]) {
        var positional: [String] = [], flags: [String: String] = [:], i = 0
        while i < args.count {
            let arg = args[i]
            if arg.hasPrefix("--"), arg.count > 2 {
                let name = String(arg.dropFirst(2)).lowercased()
                if valueOptions.contains(name) {
                    guard i + 1 < args.count else { throw CLIError.usage("--\(name) 需要一个值") }
                    i += 1
                    flags[name] = args[i]
                } else { flags[name] = "1" }
            } else { positional.append(arg) }
            i += 1
        }
        return (positional, flags)
    }

    private static func need(_ store: Store, _ pos: [String]) throws -> Card {
        guard let first = pos.first, let id = Int64(first.drop(while: { $0 == "#" }).trimmingCharacters(in: .whitespacesAndNewlines)) else {
            throw CLIError.usage("需要卡片编号，例如 pincc show 12")
        }
        guard let card = try store.get(id) else { throw CLIError.missing("找不到卡片 #\(id)") }
        return card
    }

    private static func begin(_ store: Store) throws -> Group {
        if let value = ProcessInfo.processInfo.environment["PINCC_GROUP"], let number = Int64(value.trimmingCharacters(in: .whitespacesAndNewlines)), let group = try store.join(number) { return group }
        return store.begin()
    }

    private static func text(_ pos: [String], offset: Int, flags: [String: String], readStdin: () -> String) throws -> String? {
        guard pos.count > offset else { return nil }
        guard pos[offset] == "-" else { return pos.dropFirst(offset).joined(separator: " ") }
        guard flags["summary"] != "-", flags["add-task"] != "-" else {
            throw CLIError.usage("标准输入只能读一次：正文、--summary、--add-task 只能有一个是 -")
        }
        return try stdin(readStdin, what: "正文")
    }

    private static func stdin(_ read: () -> String, what: String) throws -> String {
        let value = trimEnd(read())
        guard !value.isEmpty else {
            throw CLIError.usage("\(what)要从标准输入读取，但标准输入是空的，未写入" + (what == "摘要" ? "（清空摘要请用 --summary \"\"）" : ""))
        }
        return value
    }

    private static func changes(_ store: Store, flags: [String: String], readStdin: () -> String, text: String?) throws -> (inout Card) throws -> Void {
        guard flags["summary"] != "-" || flags["add-task"] != "-" else {
            throw CLIError.usage("标准输入只能读一次：--summary 和 --add-task 不能都是 -")
        }
        let summary = try flags["summary"].map { $0 == "-" ? try stdin(readStdin, what: "摘要") : $0 }
        let addTask = try flags["add-task"].map { $0 == "-" ? try stdin(readStdin, what: "任务") : $0 }
        if let addTask, addTask.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { throw CLIError.usage("--add-task 需要任务文字") }
        let check = try taskNumbers(flags, name: "check"), uncheck = try taskNumbers(flags, name: "uncheck")
        var remind: Int64?
        if let value = flags["remind"], !["off", "clear", "none", "清除"].contains(value) {
            guard let parsed = When.parse(value) else { throw CLIError.usage("看不懂时间「\(value)」，例如 \"+30m\"、\"18:30\"、\"明天 9点\"、\"今晚\"、\"9/25 14:00\"") }
            remind = parsed
        }
        var color: Int?
        if let value = flags["color"], value != "auto" {
            guard !value.isEmpty, value.utf8.allSatisfy({ $0 >= 48 && $0 <= 57 }), let number = Int(value), (0...8).contains(number) else {
                throw CLIError.usage("--color 取 0-8 或 auto")
            }
            color = number
        }
        var image: String?
        if let path = flags["image"] {
            guard !isWindowsPath(path) else { throw CLIError.usage("图片路径「\(path)」属于 Windows；请使用当前 Mac 上的文件路径") }
            let url = URL(fileURLWithPath: (path as NSString).expandingTildeInPath).standardizedFileURL
            guard FileManager.default.fileExists(atPath: url.path) else { throw CLIError.usage("找不到图片文件「\(path)」") }
            guard let payload = try MacImagePayload.read(url) else { throw CLIError.usage("「\(path)」不是可读取的图片，或超过 50 MB（支持 PNG、JPEG、GIF、BMP、WebP、TIFF、HEIC/HEIF）") }
            image = try store.saveImage(payload.data, ext: payload.ext)
        }
        return { card in
            if let text { card.body = flags["append"] != nil && !card.body.isEmpty ? trimEnd(card.body) + "\n" + text : text }
            if let image { card.images.append(image) }
            if !check.isEmpty || !uncheck.isEmpty {
                let lines = Markdown.taskLines(card.body)
                if let bad = (check + uncheck).first(where: { $0 > lines.count }) {
                    throw CLIError.usage(lines.isEmpty ? "#\(card.id) 没有任务" : "#\(card.id) 只有 \(lines.count) 个任务，没有第 \(bad) 个")
                }
                for n in check { card.body = Markdown.setTask(card.body, line: lines[n - 1], done: true) }
                for n in uncheck { card.body = Markdown.setTask(card.body, line: lines[n - 1], done: false) }
            }
            if let addTask { card.body = Markdown.addTasks(card.body, text: addTask) }
            if let summary {
                card.summary = summary.isEmpty ? nil : summary
                var meta = card.info ?? CardMeta()
                meta.sumHash = card.summary == nil ? nil : card.inputHash
                card.meta = try meta.toJSON()
            }
            if flags["remind"] != nil { card.remindAt = remind }
            if flags["done"] != nil, card.doneAt == nil { card.doneAt = When.nowMs }
            if flags["undone"] != nil { card.doneAt = nil; card.deletedAt = nil }
            if flags["color"] != nil { card.color = color }
            if flags["top"] != nil { card.topmost = true }
            if flags["normal"] != nil { card.topmost = false }
        }
    }

    private static func taskNumbers(_ flags: [String: String], name: String) throws -> [Int] {
        guard let value = flags[name] else { return [] }
        let parts = value.split(separator: ",", omittingEmptySubsequences: true).map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
        let numbers = try parts.map { part -> Int in
            guard !part.isEmpty, part.utf8.allSatisfy({ $0 >= 48 && $0 <= 57 }), let number = Int(part), number >= 1 else {
                throw CLIError.usage("--\(name) 取任务序号（从 1 起），例如 --\(name) 2 或 --\(name) 1,3")
            }
            return number
        }
        guard !numbers.isEmpty else { throw CLIError.usage("--\(name) 需要任务序号") }
        return numbers
    }

    private static func opFor(_ flags: [String: String], text: String?) -> String {
        let actions = flags.keys.filter { !["json", "if-rev", "if-input"].contains($0) }
        guard text == nil, actions.count == 1, let action = actions.first else { return "edit" }
        switch action { case "summary", "remind", "done", "color": return action; case "top", "normal": return "view"; default: return "edit" }
    }

    public static func deskMarkdown(_ store: Store, cards: [Card]) throws -> String {
        var result = "# PinCC 卡片（\(cards.count) 张，\(dateString(When.nowMs, pattern: "yyyy-MM-dd HH:mm"))）\n"
        for card in cards { result += "\n" + (try markdown(card, store: store)) + "\n" }
        return trimEnd(result)
    }

    public static func markdown(_ card: Card, store: Store) throws -> String {
        var lines = ["## #\(card.id) \(card.title)"]
        let info = card.srcInfo
        if let remind = card.remindAt { lines.append("- 提醒：\(dateString(remind, pattern: "yyyy-MM-dd HH:mm"))\(card.overdue ? "（已到期）" : "")") }
        if let src = card.src { lines.append("- 来源：\(src)") }
        if let site = info?.site { lines.append("- 站点：\(site)") }
        if card.progress.total > 0 { lines.append("- 任务：\(card.progress.done)/\(card.progress.total)") }
        if let done = card.doneAt { lines.append("- 已完成：\(dateString(done, pattern: "yyyy-MM-dd HH:mm"))") }
        lines.append("- 创建：\(dateString(card.createdAt, pattern: "yyyy-MM-dd HH:mm")) · rev \(card.rev)")
        if let desc = info?.desc, !desc.isEmpty, isBlank(card.summary) { lines.append("- 描述：\(desc)") }
        if !isBlank(card.note) { lines += ["", card.note.trimmingCharacters(in: .whitespacesAndNewlines)] }
        for image in card.images { lines.append("![](\(store.resolveImage(image)))") }
        if let summary = card.summary, !isBlank(summary) {
            lines += ["", card.summaryStale ? "> 摘要（原文已改动，可能过时）：" : "> 摘要："]
            lines += summary.trimmingCharacters(in: .whitespacesAndNewlines).components(separatedBy: "\n").map { "> " + $0 }
        }
        return trimEnd(lines.joined(separator: "\n"))
    }

    private static func line(_ card: Card) -> String {
        var value = "#" + pad(String(card.id), width: 4)
        if card.doneAt != nil { value += " [已完成]" }
        if card.deletedAt != nil { value += " [已删除]" }
        if let remind = card.remindAt { value += (card.overdue ? " ⏰已到 " : " ⏰") + When.format(remind) }
        if card.progress.total > 0 { value += " [\(card.progress.done)/\(card.progress.total)]" }
        if ["link", "file", "image"].contains(card.kind) { value += " (\(card.kind))" }
        value += "  " + (card.title.count > 60 ? String(card.title.prefix(60)) + "…" : card.title)
        return value
    }

    private static func dto(_ card: Card, store: Store, mode: String) throws -> [String: Any] {
        let tasks: [[String: Any]] = Markdown.blocks(card.body).filter { $0.kind == .task }.enumerated().map {
            ["n": $0.offset + 1, "done": $0.element.checked, "text": $0.element.text]
        }
        let meta: Any
        if let info = card.srcInfo {
            meta = ["Src": nullable(info.src), "Title": nullable(info.title), "Site": nullable(info.site), "Desc": nullable(info.desc),
                    "Date": nullable(info.date), "FType": nullable(info.fType), "Size": nullable(info.size), "Mtime": nullable(info.mtime),
                    "TextHash": nullable(info.textHash), "SumHash": nullable(info.sumHash), "State": nullable(info.state), "Error": nullable(info.error)]
        }
        else { meta = NSNull() }
        return [
            "id": card.id, "rev": card.rev, "kind": card.kind, "title": card.title,
            "src": nullable(card.src), "body": card.body, "images": card.images.map(store.resolveImage),
            "summary": nullable(card.summary), "summary_stale": card.summaryStale, "input_hash": nullable(card.inputHash), "meta": meta,
            "color": ["index": card.shownColor(mode: mode), "auto": card.color == nil] as [String: Any],
            "topmost": card.topmost, "opacity": card.opacity, "collapsed": card.collapsed, "free": card.free, "overdue": card.overdue,
            "progress": ["done": card.progress.done, "total": card.progress.total], "tasks": tasks,
            "remind_at": nullable(card.remindAt.map(iso)), "done_at": nullable(card.doneAt.map(iso)), "deleted_at": nullable(card.deletedAt.map(iso)),
            "created_at": iso(card.createdAt), "updated_at": iso(card.updatedAt)
        ]
    }

    private static func nullable<T>(_ value: T?) -> Any { value.map { $0 as Any } ?? NSNull() }
    private static func isBlank(_ value: String?) -> Bool { value?.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ?? true }
    private static func trimEnd(_ value: String) -> String { String(value.reversed().drop(while: { $0.isWhitespace }).reversed()) }
    private static func pad(_ value: String, width: Int, left: Bool = false) -> String {
        let spaces = String(repeating: " ", count: max(0, width - value.count))
        return left ? spaces + value : value + spaces
    }
    private static func isWindowsPath(_ value: String) -> Bool {
        if value.hasPrefix("\\\\") { return true }
        let chars = Array(value.utf8.prefix(3))
        return chars.count == 3 && ((65...90).contains(chars[0]) || (97...122).contains(chars[0])) && chars[1] == 58 && [47, 92].contains(chars[2])
    }
    private static func jsonText(_ value: Any) throws -> String {
        String(decoding: try JSONSerialization.data(withJSONObject: value, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]), as: UTF8.self)
    }
    private static func dateString(_ ms: Int64, pattern: String) -> String {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.calendar = Calendar(identifier: .gregorian)
        formatter.timeZone = .current
        formatter.dateFormat = pattern
        return formatter.string(from: When.fromMs(ms))
    }
    private static func iso(_ ms: Int64) -> String { dateString(ms, pattern: "yyyy-MM-dd'T'HH:mm:ssxxx") }
    private static func fail(_ code: Int32, _ message: String, output: (String) -> Void) -> Int32 {
        output("错误：" + message)
        if code == usageError { output("用法：pincc help") }
        return code
    }
    private static func errorCode(_ error: Error) -> Int32 {
        if error is MaintenanceBusyError { return busy }
        if let sqlite = error as? SQLiteError, [5, 6].contains(sqlite.code & 0xff) { return busy }
        return usageError
    }

    public static func opName(_ op: String) -> String {
        ["create": "新建", "edit": "编辑", "done": "完成", "delete": "删除", "restore": "恢复", "remind": "提醒", "summary": "摘要", "meta": "读取来源", "move": "移动", "resize": "调整大小", "view": "显示设置", "color": "颜色", "tile": "平铺", "run": "运行技能"][op] ?? op
    }

    public static func agentSkill() -> String {
        let exe = CommandLine.arguments.first ?? "pincc"
        return """
        ---
        name: pincc
        description: 用 PinCC（Pin Carbon Copy，macOS 桌面悬浮卡片）读写用户的待办卡片：钉住文件/网址/笔记、加清单项、写摘要、设提醒。用户说“钉起来 / pin 一下 / 记到卡片 / 桌面待办”时使用。
        ---
        PinCC 的命令行就是它的接口，桌面程序会在 1 秒内反映改动。

        - 程序：`\(exe)`（安装命令行工具后可直接用 `pincc`）。
        - 先运行 `pincc help` 看完整用法。常用：
          - `pincc list --md`：读全部卡片
          - `pincc add "<网址或文件路径>"`：钉一个网址/文档（摘要由 PinCC 自己生成，不要手写）
          - `pincc add - --remind "明天 9点" < note.md`：新建带提醒的笔记
          - `pincc set <id> --add-task "…"`、`--check N`：清单逐项修改
          - `pincc show <id> --json`，再 `set <id> --summary - --if-input <input_hash>`：写摘要
        - 文件路径给当前 Mac 上的绝对路径；不同设备的文件路径需要重新选择。
        - 未经用户要求不要 `rm` 或 `--done`；每次写入都可以 `pincc log` 找到组号后 `restore` 撤销。
        """
    }

    public static let help = """
    PinCC（Pin Carbon Copy）— macOS 桌面悬浮卡片（每张卡片 = 一件待办：备忘 / 网址 / 文档 / 图片）。命令行即文本接口，供人和 CLI LLM 工具读写。

    pincc list [--all] [--json|--md]        列出卡片（提醒最近的在前，其余按新建时间由近及远）；--md 为整篇 Markdown
    pincc show <id> [--json]                查看一张卡片
    pincc add <文字|-> [选项]                新建卡片；正文第一行是网址或绝对文件路径时即为链接/文档卡片
    pincc set <id> [文字|-] [--append] [选项] [--check N] [--uncheck N] [--if-rev N] [--if-input H]
                                            修改卡片。--if-rev：仅当版本仍为 N 时写入；--if-input：仅当摘要的输入
                                            （笔记正文或网页/文档正文，见 input_hash）仍为 H 时写入
                                            --check / --uncheck：勾选 / 取消第 N 个任务（从 1 起，可写 1,3；序号见 show --json 的 tasks）
    pincc rm <id> [--json]                  删除（可恢复）
    pincc log [N] [--json]                  最近的操作（第一列为操作组编号）
    pincc restore <组编号> [--json]          撤销整组操作：该组改过的内容回到之前，其他改动不受影响
    pincc skills [--json]                   列出技能（数据目录 skills/<名字>/SKILL.md）和执行器（agents/<名字>.json）
    pincc run <技能> [<id>] [--json]         运行一个技能（桌面范围的技能不需要 id），按技能的 output 写回
    pincc help [--skill]                    本说明；--skill 输出一份教 agent 使用 PinCC 的 SKILL.md

    选项：--summary <文字|->  --remind <时间|off>  --done | --undone  --color <0-8|auto>  --top | --normal  --image <图片文件>  --json
          --add-task <文字|->  加任务：每行一项，加在最后一个任务之后（没有任务时加在末尾），不用改写整段正文
    时间：+30m  2h  18:30  下午3点  今晚  明早  明天 9点  后天晚上  周五 10:00  下周一  9/25 14:00  2026-10-01 08:00
    "-" 表示从标准输入读取（UTF-8，一次命令只能读一次）；标准输入为空时报错、不写入（清空摘要用 --summary ""）。
    退出码：0 成功 · 1 用法错误（含未知选项）· 2 找不到 · 3 冲突（--if-rev / --if-input 不符，未写入）· 4 数据库忙

    给 LLM 工具的约定：
    - 读全部：pincc list --md（或 --json，含 rev / kind / src / input_hash / summary_stale / progress 等字段）。
    - 写摘要：先 pincc show <id> --json 取 input_hash，总结后
        pincc set <id> --summary - --if-input <input_hash> < summary.md
      期间内容变了则退出码 3、不写入（重新读取再总结）；写入后内容再改动，摘要会标为过时。
      摘要格式：第一行 **一句话结论**，然后 3–6 条 "- " 要点，需要行动的事项用 "- [ ] "。
    - 新建：pincc add - --remind "明天 9点" < note.md
    - 清单：pincc set <id> --add-task "买牛奶"；pincc set <id> --check 2（只改那一行，不会覆盖别处的改动）
    - 未经用户要求不要 rm 或 --done；每次写入都可以用 log / restore 撤销。
    - 由 PinCC 技能启动时（环境变量 PINCC_GROUP 已设置），你的所有写入归入同一个操作组，可一起撤销。
    - 文件路径使用当前 Mac 的绝对路径；不要直接打开 pincc.db。
    - 首次运行有程序执行或额外授权的用户技能时，需要在交互终端或桌面中确认。

    数据目录：~/Library/Application Support/PinCC（环境变量 PINCC_HOME 可覆盖）。
    """
}

extension CLI {
    private static func isApproved(_ skill: Skill, store: Store) throws -> Bool {
        if !skill.needsApproval { return true }
        return try store.setting("mac.skill.ok." + skill.name) == skill.hash
    }
    private static func printSkills(_ store: Store, json: Bool, output: (String) -> Void) throws {
        let catalog = try SkillsCatalog.load(home: store.home)
        if json {
            let skills: [[String: Any]] = try catalog.skills.map { skill in
                let approved = try isApproved(skill, store: store)
                return [
                    "name": skill.name, "label": skill.label, "description": skill.description,
                    "origin": skill.builtin ? "builtin" : skill.origin, "scope": skill.scope, "on": skill.on,
                    "src": skill.sources, "kind": skill.kinds, "ext": skill.extensions, "tag": nullable(skill.tag),
                    "grants": skill.grants, "runner": nullable(skill.runner), "output": skill.output,
                    "context": skill.context.map { step -> [String: Any] in
                        if let run = step.run { return ["run": run, "where": step.location, "timeout": step.timeout] }
                        return ["builtin": nullable(step.builtin)]
                    },
                    "approved": approved
                ]
            }
            let runners: [[String: Any]] = catalog.runners.map {
                ["name": $0.name, "label": $0.label, "origin": $0.origin, "command": $0.program, "grants": Array($0.grants.keys).sorted()]
            }
            output(try jsonText(["skills": skills, "runners": runners, "errors": catalog.errors] as [String: Any]))
            return
        }
        output("技能（\(URL(fileURLWithPath: store.home).appendingPathComponent("skills").path)）：")
        for skill in catalog.skills {
            var when: [String] = []
            if skill.scope == "desk" { when.append("桌面") }
            if !skill.sources.isEmpty { when.append("src=" + skill.sources.joined(separator: ",")) }
            if !skill.kinds.isEmpty { when.append("kind=" + skill.kinds.joined(separator: ",")) }
            if !skill.extensions.isEmpty { when.append("ext=" + skill.extensions.joined(separator: ",")) }
            if let tag = skill.tag { when.append("#" + tag) }
            let state = skill.needsApproval ? ((try isApproved(skill, store: store)) ? " · 已确认" : " · 待确认（首次运行时）") : ""
            var value = "  \(pad(skill.name, width: 16)) \(skill.label)（\(skill.builtin ? "内置" : "用户")）· \(skill.on.joined(separator: "/"))"
            if !when.isEmpty { value += " · " + when.joined(separator: " ") }
            value += " · 输出 " + skill.output
            if !skill.grants.isEmpty { value += " · 授权 " + skill.grants.joined(separator: ",") }
            if let runner = skill.runner { value += " · 执行器 " + runner }
            output(value + state)
        }
        output("执行器（\(URL(fileURLWithPath: store.home).appendingPathComponent("agents").path)）：")
        for runner in catalog.runners {
            let grants = ["none"] + runner.grants.keys.filter { $0 != "none" }.sorted()
            output("  \(pad(runner.name, width: 16)) \(runner.label)（\(runner.origin == "builtin" ? "内置" : "用户")）· 授权 \(grants.joined(separator: ","))")
        }
        for error in catalog.errors { output("  ✗ " + error) }
    }

    @MainActor
    private static func runSkill(_ store: Store, pos: [String], json: Bool, output: (String) -> Void) async throws {
        guard let name = pos.first else { throw CLIError.usage("run 需要技能名：pincc run <技能> [卡片编号]（pincc skills 列出全部）") }
        let catalog = try SkillsCatalog.load(home: store.home)
        guard let skill = catalog.find(name) else { throw CLIError.missing("找不到技能「\(name)」（pincc skills 列出全部）") }
        let card = skill.scope == "card" ? try need(store, Array(pos.dropFirst())) : nil
        let group = try begin(store)
        let desk: String?
        if skill.scope == "desk" || skill.context.contains(where: { $0.builtin == "desk" }) {
            desk = try deskMarkdown(store, cards: store.active())
        } else { desk = nil }
        let service = SummaryService(store: store)
        let authorize: @MainActor @Sendable (String) async -> Bool = { permission in await confirmPermission(permission) }
        let status: @MainActor @Sendable (String) -> Void = { _ in }
        let result: SkillRunResult?
        if let card { result = try await service.run(cardID: card.id, skillName: name, userAsked: true, group: group, desk: desk, authorize: authorize, status: status) }
        else { result = try await service.runDesk(skillName: name, group: group, desk: desk, authorize: authorize, status: status) }
        guard let result else { output("（本次任务无需运行）"); return }
        if json {
            output(try jsonText([
                "skill": result.skill, "card": nullable(result.cardID), "output": result.output,
                "written": result.written, "grp": nullable(result.group), "result": result.text
            ] as [String: Any]))
            return
        }
        output(result.text)
        output("")
        let undo = result.group.map { "（撤销：pincc restore \($0)）" } ?? ""
        if ["summary", "append"].contains(result.output), !result.written { output("（卡片在运行期间改动了，结果未写入）"); return }
        switch result.output {

        case "summary": output("已写入 #\(card!.id) 的摘要" + undo)
        case "append": output("已追加到 #\(card!.id)" + undo)
        case "cards" where result.written: output("已新建卡片" + undo)
        default:
            output(result.group.map { "本次运行的写入可一起撤销：pincc restore \($0)" } ?? "（没有写入）")
        }
    }

    @MainActor
    private static func confirmPermission(_ message: String) async -> Bool {
        #if canImport(Darwin)
        guard isatty(STDIN_FILENO) != 0 else { return false }
        let prompt = message + "\n允许此技能？输入 yes 确认，其他输入取消："
        FileHandle.standardError.write(Data(prompt.utf8))
        return await Task.detached {
            readLine()?.trimmingCharacters(in: .whitespacesAndNewlines).lowercased() == "yes"
        }.value
        #else
        return false
        #endif
    }
}
