import Foundation
import CoreFoundation
import PinCCCore

public struct SkillStep: Sendable {
    public let run: [String]?
    public let builtin: String?
    public let location: String
    public let timeout: Int
}
public struct Skill: Sendable {
    public let name, label, description, body, text, origin: String
    public let runner: String?
    public let scope, output: String
    public let on, grants: [String]
    public let context: [SkillStep]
    public let sources, kinds, extensions: [String]
    public let tag: String?
    public var hash: String { Card.hash(text) }
    public var builtin: Bool { origin == "builtin" }
    public var needsApproval: Bool { !builtin && (context.contains { $0.run != nil } || !grants.isEmpty) }
    public var specificity: Int { (sources.isEmpty ? 0 : 4) + (extensions.isEmpty ? 0 : 3) + (tag == nil ? 0 : 2) + (kinds.isEmpty ? 0 : 1) }
    public func matches(_ card: Card) -> Bool {
        guard scope == "card" else { return false }
        if !sources.isEmpty {
            guard let src = card.src, sources.contains(where: {
                let pattern = "^" + NSRegularExpression.escapedPattern(for: $0).replacingOccurrences(of: "\\*", with: ".*") + "$"
                return src.range(of: pattern, options: [.regularExpression, .caseInsensitive]) != nil
            }) else { return false }
        }
        if !kinds.isEmpty && !kinds.contains(card.kind) { return false }
        if !extensions.isEmpty {
            guard let src = card.src else { return false }
            let path = card.isLink ? URL(string: src)?.path ?? "" : src
            let suffix = (path as NSString).pathExtension.lowercased(), ext = suffix.isEmpty ? "" : "." + suffix
            if !extensions.contains(where: { $0.lowercased() == ext }) { return false }
        }
        if let tag, card.body.range(of: "(?:^|\\s)#" + NSRegularExpression.escapedPattern(for: tag) + "(?![\\p{L}\\p{N}_-])", options: [.regularExpression, .caseInsensitive]) == nil { return false }
        return true
    }
    public static func parse(_ raw: String, name: String, origin: String) throws -> Skill {
        var text = raw.replacingOccurrences(of: "\r\n", with: "\n"); while text.first == "\u{feff}" { text.removeFirst() }
        let lines = text.components(separatedBy: "\n")
        guard lines.first == "---", let end = lines.dropFirst().firstIndex(of: "---") else { throw PlatformError.message("SKILL.md 缺少 --- 元数据") }
        var front: [String: String] = [:], current: String?
        for line in lines[1..<end] where !line.trimmingCharacters(in: .whitespaces).isEmpty && !line.trimmingCharacters(in: .whitespaces).hasPrefix("#") {
            if line.first?.isWhitespace == true, let current { front[current, default: ""] += "\n" + line; continue }
            guard let colon = line.firstIndex(of: ":") else { throw PlatformError.message("看不懂元数据：\(line)") }
            let key = String(line[..<colon])
            guard key.range(of: "^[A-Za-z][\\w-]*$", options: .regularExpression) != nil else { throw PlatformError.message("元数据键无效：\(key)") }
            front[key] = String(line[line.index(after: colon)...]).trimmingCharacters(in: .whitespaces); current = key
        }
        func scalar(_ key: String) throws -> String? {
            guard let source = front[key] else { return nil }; let raw = source.trimmingCharacters(in: .whitespacesAndNewlines)
            if raw.hasPrefix("\""), raw.hasSuffix("\""), raw.count >= 2 { return try JSONSerialization.jsonObject(with: Data(raw.utf8), options: [.fragmentsAllowed]) as? String }
            if raw.hasPrefix("'"), raw.hasSuffix("'"), raw.count >= 2 { return String(raw.dropFirst().dropLast()).replacingOccurrences(of: "''", with: "'") }
            return raw
        }
        guard try scalar("name") == name, validName(name), let description = try scalar("description") else { throw PlatformError.message("name 与目录名必须一致，description 必填") }
        let runner = try scalar("runner")
        if let runner, !validName(runner) { throw PlatformError.message("runner 名称无效") }
        var sources: [String] = [], kinds: [String] = [], exts: [String] = [], on: [String] = []
        var scope = "card", tag: String?
        if let rawWhen = front["when"] {
            guard let condition = try JSONText.object(rawWhen) as? [String: Any] else { throw PlatformError.message("when 必须是 JSON 对象") }
            for (key, value) in condition {
                let values = try strings(value, key)
                switch key {
                case "src": sources = values
                case "kind":
                    guard values.allSatisfy({ ["note","task","link","file","image"].contains($0) }) else { throw PlatformError.message("when.kind 无效") }
                    kinds = values
                case "ext": exts = values.map { $0.hasPrefix(".") ? $0 : "." + $0 }
                case "tag": tag = values.first.map { String($0.drop(while: { $0 == "#" })) }
                case "scope": scope = values.first ?? "card"
                case "on": on = values
                default: throw PlatformError.message("when 不认识 \(key)")
                }
            }
        }
        guard ["card","desk"].contains(scope), on.allSatisfy({ ["ask","create","menu"].contains($0) }) else { throw PlatformError.message("scope 或 on 无效") }
        if on.isEmpty { on = ["ask"] }
        var steps: [SkillStep] = []
        if let rawContext = front["context"] {
            guard let context = try JSONText.object(rawContext) as? [[String: Any]] else { throw PlatformError.message("context 必须是 JSON 对象数组") }
            for step in context {
                guard Set(step.keys).isSubset(of: ["run","builtin","where","timeout"]) else { throw PlatformError.message("context 含有未知字段") }
                let run = try step["run"].map { try strings($0, "context.run") }
                let builtin = try step["builtin"].map { try strings($0, "context.builtin").first ?? "" }
                let location = try step["where"].map { try strings($0, "context.where").first ?? "runner" } ?? "runner"
                let timeout: Int
                if let value = step["timeout"] {
                    guard let number = value as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID(), number.doubleValue == Double(number.intValue) else { throw PlatformError.message("context.timeout 必须是整数") }
                    timeout = number.intValue
                } else { timeout = 60 }
                guard (run == nil) != (builtin == nil), run == nil || run?.first?.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty == false,
                      builtin == nil || ["source","note","desk"].contains(builtin!),
                      ["runner","windows","wsl","mac"].contains(location), (1...600).contains(timeout) else { throw PlatformError.message("context 步骤无效") }
                steps.append(SkillStep(run: run, builtin: builtin, location: location, timeout: timeout))
            }
        }
        if steps.isEmpty { steps = [SkillStep(run: nil, builtin: scope == "desk" ? "desk" : "note", location: "runner", timeout: 60)] }
        var grants: [String] = []
        if let rawGrants = front["grants"] {
            grants = rawGrants.hasPrefix("[") ? try strings(JSONText.object(rawGrants), "grants") : [try scalar("grants") ?? ""]
            grants = grants.filter { !$0.isEmpty && $0 != "none" }
            guard grants.allSatisfy({ ["web","read","pincc"].contains($0) || ($0.hasPrefix("shell:") && validPrefix(String($0.dropFirst(6)).trimmingCharacters(in: .whitespacesAndNewlines))) }) else { throw PlatformError.message("grants 无效") }
        }
        let output = try scalar("output") ?? (scope == "desk" ? "cards" : "summary")
        guard ["summary","append","cards","none"].contains(output), !(scope == "desk" && ["summary","append"].contains(output)) else { throw PlatformError.message("output 无效") }
        let body = lines.dropFirst(end + 1).joined(separator: "\n").trimmingCharacters(in: .newlines)
        guard !body.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { throw PlatformError.message("技能提示词为空") }
        return Skill(name: name, label: try scalar("label").flatMap { $0.isEmpty ? nil : $0 } ?? name, description: description, body: body, text: text, origin: origin,
                     runner: runner, scope: scope, output: output, on: on, grants: grants, context: steps, sources: sources, kinds: kinds, extensions: exts, tag: tag)
    }
}
public struct RunnerProfile: Sendable {
    public let name, label, program, origin, text: String
    public var hash: String { Card.hash(text) }
    public let priority: Int
    public let args, model, listModels: [String]
    public let requiresModel: Bool
    public let grants: [String: [String]]
    public let promptEscapes: [String: String]
    public func supports(_ grant: String) -> Bool { grant == "none" || grants[grant.hasPrefix("shell:") ? "shell" : grant] != nil }
    public static func parse(_ text: String, name: String, origin: String) throws -> RunnerProfile {
        guard let value = try JSONText.object(text, foldRootKeys: true) as? [String: Any], value["name"] as? String == name,
              validName(name), name != "custom" else { throw PlatformError.message("执行器 name 与文件名必须一致") }
        for key in ["label","command"] {
            if let raw = value[key], !(raw is NSNull), !(raw is String) { throw PlatformError.message("\(key) 必须是字符串") }
        }
        if let raw = value["priority"] {
            guard let number = raw as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID(), number.doubleValue == Double(number.intValue) else { throw PlatformError.message("priority 必须是整数") }
        }
        if let raw = value["requiresmodel"] {
            guard let number = raw as? NSNumber, CFGetTypeID(number) == CFBooleanGetTypeID() else { throw PlatformError.message("requiresModel 必须是布尔值") }
        }
        func tokens(_ key: String) throws -> [String] {
            guard let raw = value[key] else { return [] }
            guard let array = raw as? [String] else { throw PlatformError.message("\(key) 必须是字符串数组") }; return array
        }
        let args = try tokens("args"), model = try tokens("model"), lists = try tokens("listmodels")
        let command = value["command"] as? String
        if let command, command.range(of: "^[\\w ./\\\\:+-]{1,260}$", options: .regularExpression) == nil { throw PlatformError.message("command 必须是本机程序名或路径") }
        let program = command?.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty == false ? command! : name
        guard !args.isEmpty || command != nil else { throw PlatformError.message("缺少 args") }
        for token in args where token.range(of: "\\{[a-z]+\\}", options: .regularExpression) != nil {
            guard ["{model}","{grants}"].contains(token) else { throw PlatformError.message("args 占位符必须占一个完整参数") }
        }
        guard args.filter({ $0 == "{model}" }).count <= 1, args.filter({ $0 == "{grants}" }).count <= 1 else { throw PlatformError.message("args 占位符重复") }
        for token in model where token.range(of: "\\{[a-z]+\\}", options: .regularExpression) != nil {
            guard token == "{value}" else { throw PlatformError.message("model 占位符必须是 {value}") }
        }
        let requires = value["requiresmodel"] as? Bool ?? false
        guard model.isEmpty || model.contains("{value}"), !requires || !model.isEmpty,
              requires || model.isEmpty || args.contains("{model}") else { throw PlatformError.message("model 参数无效") }
        var grants: [String: [String]] = [:]
        if let raw = value["grants"] {
            guard let map = raw as? [String: [String]], Set(map.keys).isSubset(of: ["none","web","read","shell"]) else { throw PlatformError.message("grants 必须是授权到参数数组的对象") }
            for (key, tokens) in map {
                for token in tokens {
                    guard !(key != "shell" && token.contains("{prefix}")), token.replacingOccurrences(of: "{prefix}", with: "").range(of: "\\{[a-z]+\\}", options: .regularExpression) == nil else { throw PlatformError.message("grant 占位符无效") }
                }
                guard key != "shell" || tokens.contains(where: { $0.contains("{prefix}") }) else { throw PlatformError.message("shell 必须限定 {prefix}") }
            }
            guard map.values.allSatisfy({ $0.isEmpty }) || args.contains("{grants}") else { throw PlatformError.message("args 缺少 {grants}") }
            grants = map
        }
        let escapes: [String: String]
        if let raw = value["promptescapes"] {
            guard let map = raw as? [String: String] else { throw PlatformError.message("promptEscapes 必须是字符串对象") }; escapes = map
        } else { escapes = [:] }
        return RunnerProfile(name: name, label: value["label"] as? String ?? name, program: program, origin: origin, text: text.replacingOccurrences(of: "\r\n", with: "\n"),
                             priority: value["priority"] as? Int ?? 100, args: args, model: model, listModels: lists,
                             requiresModel: requires, grants: grants, promptEscapes: escapes)
    }
    public func arguments(model chosen: String?, grants wanted: [String]) throws -> [String] {
        if requiresModel && (chosen ?? "").isEmpty { throw PlatformError.message("执行器 \(name) 需要模型名") }
        var granted: [String] = []
        if wanted.isEmpty { granted = grants["none"] ?? [] }
        else {
            for grant in wanted {
                guard supports(grant) else { throw PlatformError.message("执行器 \(name) 不支持 \(grant)") }
                let key = grant.hasPrefix("shell:") ? "shell" : grant
                let prefix = grant.hasPrefix("shell:") ? String(grant.dropFirst(6)) : ""
                granted += (grants[key] ?? []).map { $0.replacingOccurrences(of: "{prefix}", with: prefix) }
            }
        }
        let modelArgs = (chosen ?? "").isEmpty ? [] : model.map { $0.replacingOccurrences(of: "{value}", with: chosen!) }
        return args.flatMap { $0 == "{model}" ? modelArgs : $0 == "{grants}" ? granted : [$0] }
    }
}
public struct SkillsCatalog: Sendable {
    public let skills: [Skill]
    public let runners: [RunnerProfile]
    public let errors: [String]
    public func find(_ name: String) -> Skill? { skills.first { $0.name == name } }
    public func runner(_ name: String) -> RunnerProfile? { runners.first { $0.name == name } }
    public func best(for card: Card) -> Skill? {
        skills.filter { $0.matches(card) && $0.on.contains(where: { ["ask","create"].contains($0) }) }.sorted {
            if $0.specificity != $1.specificity { return $0.specificity > $1.specificity }
            if $0.builtin != $1.builtin { return !$0.builtin }; return $0.name < $1.name
        }.first
    }
    /// Shared shipped files stay at repository builtin/ and are copied intact into the app's Resources.
    public static func load(home: String, builtinRoot: URL? = nil) throws -> SkillsCatalog {
        let resources = Bundle.main.resourceURL?.appendingPathComponent("builtin", isDirectory: true)
        let helperResources = Bundle.main.executableURL?.resolvingSymlinksInPath().deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("Resources/builtin", isDirectory: true)
        let development = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("builtin", isDirectory: true)
        guard let root = ([builtinRoot, resources, helperResources, development].compactMap { $0 }).first(where: {
            FileManager.default.fileExists(atPath: $0.appendingPathComponent("skills/summary/SKILL.md").path)
        }) else { throw PlatformError.message("缺少随程序发布的 builtin 资源目录") }
        var skills: [String: Skill] = [:], runners: [String: RunnerProfile] = [:], errors: [String] = []
        for (base, origin) in [(root, "builtin"), (URL(fileURLWithPath: home), "user")] {
            let skillDir = base.appendingPathComponent("skills", isDirectory: true)
            do {
                if FileManager.default.fileExists(atPath: skillDir.path) {
                    for folder in try FileManager.default.contentsOfDirectory(at: skillDir, includingPropertiesForKeys: [.isDirectoryKey]).sorted(by: { $0.path < $1.path }) {
                        let file = folder.appendingPathComponent("SKILL.md")
                        guard FileManager.default.fileExists(atPath: file.path) else { continue }
                        do { skills[folder.lastPathComponent] = try Skill.parse(boundedText(file), name: folder.lastPathComponent, origin: origin == "builtin" ? origin : folder.path) }
                        catch { errors.append("\(file.path)：\(error.localizedDescription)") }
                    }
                }
            } catch { errors.append("\(skillDir.path)：\(error.localizedDescription)") }
            let agentDir = base.appendingPathComponent("agents", isDirectory: true)
            do {
                if FileManager.default.fileExists(atPath: agentDir.path) {
                    for file in try FileManager.default.contentsOfDirectory(at: agentDir, includingPropertiesForKeys: nil).filter({ $0.pathExtension == "json" }).sorted(by: { $0.path < $1.path }) {
                        do { runners[file.deletingPathExtension().lastPathComponent] = try RunnerProfile.parse(boundedText(file), name: file.deletingPathExtension().lastPathComponent, origin: origin == "builtin" ? origin : file.path) }
                        catch { errors.append("\(file.path)：\(error.localizedDescription)") }
                    }
                }
            } catch { errors.append("\(agentDir.path)：\(error.localizedDescription)") }
        }
        return SkillsCatalog(skills: skills.values.sorted { ($0.builtin ? 1 : 0, $0.name) < ($1.builtin ? 1 : 0, $1.name) },
                             runners: runners.values.sorted { ($0.priority, $0.name) < ($1.priority, $1.name) }, errors: errors)
    }
}
private func boundedText(_ file: URL) throws -> String {
    let handle = try FileHandle(forReadingFrom: file); defer { try? handle.close() }
    let data = try handle.read(upToCount: 1_000_001) ?? Data()
    guard data.count <= 1_000_000, let text = String(data: data, encoding: .utf8) else { throw PlatformError.message("配置文件须为 UTF-8，且不超过 1 MB") }
    return text
}
private func validName(_ value: String) -> Bool { value.range(of: "^[a-z0-9][a-z0-9._-]{0,31}$", options: .regularExpression) != nil }
private func validPrefix(_ value: String) -> Bool { value.range(of: "^[\\w .+/\\\\:=@,~-]{1,200}$", options: .regularExpression) != nil }
private func strings(_ value: Any, _ key: String) throws -> [String] {
    if let string = value as? String { return [string] }
    if let strings = value as? [String] { return strings }
    throw PlatformError.message("\(key) 必须是字符串或字符串数组")
}
/// JSON comments/trailing commas are relaxed; this deliberately does not create another YAML format.
private enum JSONText {
    static func object(_ text: String, foldRootKeys: Bool = false) throws -> Any {
        let chars = Array(text), count = chars.count
        var clean = "", index = 0, quoted = false, escaped = false
        while index < count {
            let ch = chars[index]
            if quoted {
                clean.append(ch)
                if escaped { escaped = false } else if ch == "\\" { escaped = true } else if ch == "\"" { quoted = false }
                index += 1; continue
            }
            if ch == "\"" { quoted = true; clean.append(ch); index += 1; continue }
            if ch == "/", index + 1 < count, chars[index + 1] == "/" {
                index += 2; while index < count && chars[index] != "\n" { index += 1 }; continue
            }
            if ch == "/", index + 1 < count, chars[index + 1] == "*" {
                index += 2
                while index + 1 < count && !(chars[index] == "*" && chars[index + 1] == "/") { index += 1 }
                guard index + 1 < count else { throw PlatformError.message("JSON 注释未结束") }
                index += 2; clean.append(" "); continue
            }
            clean.append(ch); index += 1
        }
        // Work after comments are removed so a comma followed by a comment and } is still a trailing comma.
        let plain = Array(clean)
        var output = "", cursor = 0, depth = 0, rootKey = false
        while cursor < plain.count {
            let ch = plain[cursor]
            if ch == "\"" {
                let start = cursor; cursor += 1; var escape = false
                while cursor < plain.count {
                    let value = plain[cursor]; cursor += 1
                    if escape { escape = false } else if value == "\\" { escape = true } else if value == "\"" { break }
                }
                let token = String(plain[start..<cursor])
                if foldRootKeys, depth == 1, rootKey,
                   let key = try JSONSerialization.jsonObject(with: Data(token.utf8), options: [.fragmentsAllowed]) as? String {
                    let encoded = try JSONSerialization.data(withJSONObject: key.lowercased(), options: [.fragmentsAllowed])
                    output += String(decoding: encoded, as: UTF8.self)
                } else { output += token }
                rootKey = false; continue
            }
            if ch == "{" || ch == "[" { depth += 1; if depth == 1 && ch == "{" { rootKey = true } }
            else if ch == "}" || ch == "]" { depth -= 1 }
            else if ch == ",", depth == 1 { rootKey = true }
            if ch == "," {
                var next = cursor + 1; while next < plain.count && plain[next].isWhitespace { next += 1 }
                if next < plain.count && (plain[next] == "]" || plain[next] == "}") { cursor += 1; continue }
            }
            output.append(ch); cursor += 1
        }
        return try JSONSerialization.jsonObject(with: Data(output.utf8), options: [.fragmentsAllowed])
    }
}
