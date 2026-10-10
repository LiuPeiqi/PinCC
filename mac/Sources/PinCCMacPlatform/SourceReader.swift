import Foundation
import PDFKit
import Darwin
import CryptoKit
import ImageIO
import WebKit
import PinCCCore

public struct SourceContent: Sendable {
    public var text: String
    public var title: String?
    public var description: String?
    public var source: String?
    public var documentURL: URL?
    public var site: String? = nil
    public var date: String? = nil
    public var imageData: Data? = nil
    public var imageExt: String? = nil
    public var fType: String? = nil
    public var size: Int64? = nil
    public var mtime: Int64? = nil
    public var state: String? = nil
    public var thin: Bool { text.utf16.count < 300 }
}

public enum SourceReader {
    public static let maxText = 22_000
    private static let maxPage = 2_000_000, maxImage = 4_000_000, maxDocument = 25_000_000
    private static let fileSlots = DispatchSemaphore(value: 2)
    private static let dnsSlots = DispatchSemaphore(value: 2)

    public static func read(card: Card, userAsked: Bool = true, home: String? = nil) async throws -> SourceContent {
        guard let src = card.src else { return SourceContent(text: card.body, title: card.title) }
        if card.isLink { return try await web(src, automatic: !userAsked, home: home) }
        guard let url = localFileURL(src) else { throw PlatformError.message("来自 Windows 的文件：请在 Mac 上重新关联本机文件") }
        return try await file(url)
    }
    /// Resolves only this platform's absolute path grammar; no stat or file read occurs here.
    public static func localFileURL(_ path: String) -> URL? {
        guard path.hasPrefix("/"), !path.hasPrefix("//"), !path.hasPrefix("/\\") else { return nil }
        let url = URL(fileURLWithPath: path)
        return validLocalURL(url) ? url : nil
    }

    /// Static requests never execute scripts or fetch page subresources. The caller may explicitly
    /// request render() for a thin page after reporting the rendering phase to the user.
    public static func web(_ address: String, automatic: Bool = false, home: String? = nil) async throws -> SourceContent {
        guard var current = URL(string: address), validWebURL(current) else { throw PlatformError.message("来源不是有效的 HTTP(S) 地址") }
        let began = ProcessInfo.processInfo.systemUptime
        for hop in 0...8 {
            try Task.checkCancellation()
            if automatic {
                let safe = await safeToReadAutomatically(current)
                try Task.checkCancellation()
                guard safe else { throw PlatformError.message("含凭据、令牌或非公网地址，未自动读取") }
            }
            let remaining = 60 - (ProcessInfo.processInfo.systemUptime - began)
            guard remaining > 0 else { throw PlatformError.message("读取来源超时") }
            let configuration = URLSessionConfiguration.ephemeral
            configuration.httpCookieStorage = nil
            configuration.urlCredentialStorage = nil
            configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
            configuration.timeoutIntervalForRequest = min(15, remaining)
            configuration.timeoutIntervalForResource = remaining
            let session = URLSession(configuration: configuration, delegate: NoRedirect(), delegateQueue: nil)
            defer { session.invalidateAndCancel() }
            var request = URLRequest(url: current)
            request.setValue("PinCC/1.0 (macOS)", forHTTPHeaderField: "User-Agent")
            request.setValue("text/html,application/xhtml+xml,text/plain,application/pdf,image/*,*/*;q=0.8", forHTTPHeaderField: "Accept")
            let (bytes, response) = try await session.bytes(for: request)
            guard let http = response as? HTTPURLResponse else { throw PlatformError.message("来源未返回 HTTP 响应") }
            if [301,302,303,307,308].contains(http.statusCode), let location = http.value(forHTTPHeaderField: "Location") {
                guard hop < 8, let next = URL(string: location, relativeTo: current)?.absoluteURL, validWebURL(next) else { throw PlatformError.message("来源跳转无效或次数过多") }
                current = next // An automatic request revalidates every hop before its next GET.
                continue
            }
            if [401,403,429,503].contains(http.statusCode) { return SourceContent(text: "", title: current.lastPathComponent, source: current.absoluteString) }
            guard (200..<300).contains(http.statusCode) else { throw PlatformError.message("网页返回 HTTP \(http.statusCode)") }
            let type = http.mimeType?.lowercased() ?? ""
            let ext = documentExtension(type, current.pathExtension.lowercased())
            var cap = ext == nil ? (type.hasPrefix("image/") ? maxImage : maxPage) : maxDocument
            guard response.expectedContentLength <= maxDocument else { throw PlatformError.message("来源超过 25 MB 读取上限") }
            if (ext != nil || type.hasPrefix("image/")) && response.expectedContentLength > Int64(cap) { throw PlatformError.message("来源超过 \(cap / 1_000_000) MB 读取上限") }
            var data = Data()
            data.reserveCapacity(min(cap, 65_536))
            for try await byte in bytes {
                try Task.checkCancellation()
                guard ProcessInfo.processInfo.systemUptime - began < 60 else { throw PlatformError.message("读取来源超时") }
                // Magic bytes also recognize an image served as application/octet-stream.
                if data.count == 16 && ext == nil && imageExtension(data) != nil { cap = maxImage }
                guard data.count < cap else { throw PlatformError.message("来源超过读取字节上限") }
                data.append(byte)
            }
            let frozenData = data, source = current.absoluteString, title = current.lastPathComponent
            let charset = response.textEncodingName
            if let ext {
                guard ext == "pdf" ? frozenData.starts(with: Data("%PDF-".utf8)) : frozenData.starts(with: [0x50,0x4b,0x03,0x04]) else { throw PlatformError.message("下载内容不是有效的 PDF / Office 文档") }
                let saved = try await worker(seconds: min(15, remaining)) { stopped in
                    if stopped() { throw CancellationError() }
                    return try saveDownload(frozenData, ext: ext, home: home)
                }
                var content = try await file(saved)
                content.source = source
                content.title = title
                content.mtime = nil
                return content
            }
            if frozenData.count <= maxImage, let imageExt = try await worker(seconds: min(15, remaining), { _ in validatedImageExtension(frozenData) }) {
                return SourceContent(text: "", title: title, source: source, imageData: frozenData, imageExt: imageExt, fType: imageExt.dropFirst().uppercased(), size: Int64(frozenData.count))
            }
            if type.hasPrefix("image/") { throw PlatformError.message("来源图片无法解码或超过 4 MB") }
            if type.contains("html") || type.isEmpty {
                return try await worker(seconds: min(15, remaining)) { _ in html(decode(frozenData, charset: charset, sniffHTML: true), source: source) }
            }
            if type.hasPrefix("text/") || type.contains("json") || type.contains("xml") || type.contains("javascript") {
                return SourceContent(text: capText(decode(frozenData, charset: charset)), title: title, source: source)
            }
            return SourceContent(text: "", title: title, source: source)
        }
        throw PlatformError.message("来源跳转次数过多")
    }

    /// Manual-only script fallback. A nonpersistent WKWebView has no visible window. WebKit owns
    /// subresource fetching; its navigation delegate cannot enforce a public-address policy for
    /// every subresource, so automatic reading must never call this API.
    public static func render(_ address: String) async throws -> SourceContent {
        guard let url = URL(string: address), validWebURL(url) else { throw PlatformError.message("来源不是有效的 HTTP(S) 地址") }
        return try await WebRenderer.read(url)
    }

    private struct LocalRead: Sendable {
        var content: SourceContent
        var data: Data?
    }

    public static func file(_ url: URL) async throws -> SourceContent {
        guard validLocalURL(url) else { throw PlatformError.message("需要本机绝对文件路径；来自其他平台的文件请重新关联") }
        let snapshot = try await worker(seconds: 60) { stopped -> LocalRead in
            var content = SourceContent(text: "", title: url.lastPathComponent, source: url.path, documentURL: url)
            do {
                let facts = try FileManager.default.attributesOfItem(atPath: url.resolvingSymlinksInPath().path)
                let ext = url.pathExtension.lowercased()
                let directory = facts[.type] as? FileAttributeType == .typeDirectory
                content.fType = directory ? "文件夹" : ext.uppercased()
                content.mtime = (facts[.modificationDate] as? Date).map { Int64($0.timeIntervalSince1970 * 1000) }
                if directory {
                    guard let handle = opendir(url.path) else { throw NSError(domain: NSPOSIXErrorDomain, code: Int(errno)) }
                    defer { closedir(handle) }
                    var names: [String] = []
                    while names.count < 40 {
                        if stopped() { throw CancellationError() }
                        errno = 0
                        guard let item = readdir(handle) else {
                            if errno != 0 { throw NSError(domain: NSPOSIXErrorDomain, code: Int(errno)) }
                            break
                        }
                        let capacity = MemoryLayout.size(ofValue: item.pointee.d_name)
                        let name = withUnsafePointer(to: &item.pointee.d_name) { pointer in
                            pointer.withMemoryRebound(to: CChar.self, capacity: capacity) { String(cString: $0) }
                        }
                        if name != "." && name != ".." { names.append(name) }
                    }
                    content.text = names.joined(separator: "\n")
                    content.description = "\(names.count)\(names.count == 40 ? "+" : "") 项：" + names.prefix(8).joined(separator: "、")
                    return LocalRead(content: content)
                }
                content.size = (facts[.size] as? NSNumber)?.int64Value
                guard (content.size ?? 0) <= maxDocument else { throw PlatformError.message("文档超过 25 MB 读取上限") }
                let handle = try FileHandle(forReadingFrom: url)
                defer { try? handle.close() }
                var data = Data()
                while let chunk = try handle.read(upToCount: 65_536), !chunk.isEmpty {
                    if stopped() { throw CancellationError() }
                    guard data.count + chunk.count <= maxDocument else { throw PlatformError.message("文档超过 25 MB 读取上限") }
                    data.append(chunk)
                }
                content.size = Int64(data.count)
                return LocalRead(content: content, data: data)
            } catch let error as NSError {
                if missing(error) { content.state = "missing"; return LocalRead(content: content) }
                if denied(error) { throw PlatformError.message("需要文件访问授权：\(url.path)") }
                throw error
            }
        }
        try Task.checkCancellation()
        var content = snapshot.content
        guard let data = snapshot.data else { return content }
        let ext = url.pathExtension.lowercased()
        if let imageExt = try await worker(seconds: 15, { _ in validatedImageExtension(data) }) {
            content.imageData = data
            content.imageExt = imageExt
            if content.fType?.isEmpty != false { content.fType = imageExt.dropFirst().uppercased() }
            return content
        }
        if ext == "pdf" {
            content.text = try await worker(seconds: 60) { stopped -> String in
                guard data.starts(with: Data("%PDF-".utf8)), let pdf = PDFDocument(data: data), !pdf.isLocked else { throw PlatformError.message("PDF 无法读取或需要密码") }
                var result = ""
                for index in 0..<min(pdf.pageCount, 512) {
                    if stopped() { throw CancellationError() }
                    result += (pdf.page(at: index)?.string ?? "") + "\n"
                    if result.utf16.count >= maxText { break }
                }
                return capText(result)
            }
        } else if ["docx","pptx","xlsx"].contains(ext) {
            do { content.text = try await office(data: data, ext: ext) }
            catch let error as DocumentParseError { content.description = error.localizedDescription }
        } else {
            let knownText = ["txt","md","markdown","csv","tsv","json","xml","html","htm","log","swift","cs","py","js","ts","css","yaml","yml","toml","ini","sh"]
            let textBOM = data.starts(with: [0xff,0xfe]) || data.starts(with: [0xfe,0xff])
            if knownText.contains(ext) || textBOM || !data.contains(0) && String(data: data, encoding: .utf8) != nil {
                content.text = try await worker(seconds: 15) { _ in
                    let decoded = decode(data, sniffHTML: ["html","htm"].contains(ext))
                    return ["html","htm"].contains(ext) ? html(decoded, source: url.path).text : capText(decoded)
                }
            }
        }
        if !content.text.isEmpty { content.description = prefixText(content.text, units: 300).split(whereSeparator: { $0.isWhitespace }).joined(separator: " ") }
        return content
    }

    private static func validLocalURL(_ url: URL) -> Bool {
        let path = url.path
        let host = url.host?.lowercased() ?? ""
        return url.isFileURL && (host.isEmpty || host == "localhost") && path.hasPrefix("/") &&
            !path.hasPrefix("//") && !path.hasPrefix("/\\") && !path.utf8.contains(0) &&
            path.range(of: #"^/[A-Za-z]:[\\/]"#, options: .regularExpression) == nil
    }
    private static func missing(_ error: NSError) -> Bool {
        error.domain == NSCocoaErrorDomain && error.code == NSFileReadNoSuchFileError ||
        error.domain == NSPOSIXErrorDomain && [Int(ENOENT), Int(ENOTDIR)].contains(error.code)
    }
    private static func denied(_ error: NSError) -> Bool {
        error.domain == NSCocoaErrorDomain && error.code == NSFileReadNoPermissionError ||
        error.domain == NSPOSIXErrorDomain && [Int(EACCES), Int(EPERM)].contains(error.code)
    }
    private static func validWebURL(_ url: URL) -> Bool {
        ["http","https"].contains(url.scheme?.lowercased() ?? "") && url.host?.isEmpty == false && url.user == nil && url.password == nil
    }

    /// Uses the shared Fetch.cs query policy exactly. DNS is validated before URLSession resolves
    /// again; system proxies and DNS rebinding remain a documented platform acceptance condition.
    public static func safeToReadAutomatically(_ url: URL) async -> Bool {
        guard validWebURL(url), let rawHost = url.host else { return false }
        let host = rawHost.trimmingCharacters(in: CharacterSet(charactersIn: ".[]")).lowercased()
        if let query = URLComponents(url: url, resolvingAgainstBaseURL: false)?.percentEncodedQuery {
            for part in query.components(separatedBy: CharacterSet(charactersIn: "&;")) {
                let rawKey = part.components(separatedBy: "=").first?.replacingOccurrences(of: "+", with: " ") ?? ""
                guard let key = rawKey.removingPercentEncoding else { return false }
                let cleaned = key.replacingOccurrences(of: "keywords?|author", with: "", options: [.regularExpression, .caseInsensitive])
                if cleaned.range(of: "token|sig|credential|secret|key|auth|code|otp|ticket|session|password|pwd", options: [.regularExpression, .caseInsensitive]) != nil { return false }
            }
        }
        let local = [".localhost",".local",".lan",".internal",".intranet",".corp",".home.arpa",".localdomain",".ts.net"]
        guard host != "localhost", !local.contains(where: { host.hasSuffix($0) }), host.contains(".") || host.contains(":") else { return false }
        return (try? await worker(seconds: 4, slots: dnsSlots) { stopped in
            if stopped() { return false }
            var hints = addrinfo(), list: UnsafeMutablePointer<addrinfo>?
            hints.ai_family = AF_UNSPEC
            hints.ai_socktype = SOCK_STREAM
            guard getaddrinfo(host, nil, &hints, &list) == 0, let first = list else { return false }
            defer { freeaddrinfo(first) }
            var current: UnsafeMutablePointer<addrinfo>? = first, found = false
            while let entry = current {
                if stopped() { return false }
                found = true
                if !publicAddress(entry.pointee.ai_addr) { return false }
                current = entry.pointee.ai_next
            }
            return found
        }) ?? false
    }

    private static func publicAddress(_ pointer: UnsafeMutablePointer<sockaddr>?) -> Bool {
        guard let pointer else { return false }
        if Int32(pointer.pointee.sa_family) == AF_INET {
            let addr = UnsafeRawPointer(pointer).assumingMemoryBound(to: sockaddr_in.self).pointee.sin_addr
            return withUnsafeBytes(of: addr) { publicIPv4(Array($0)) }
        }
        if Int32(pointer.pointee.sa_family) == AF_INET6 {
            let addr = UnsafeRawPointer(pointer).assumingMemoryBound(to: sockaddr_in6.self).pointee.sin6_addr
            let b = withUnsafeBytes(of: addr) { Array($0) }
            let mapped = b.prefix(10).allSatisfy { $0 == 0 } && b[10] == 255 && b[11] == 255
            let compatible = b.prefix(12).allSatisfy { $0 == 0 }
            let nat64 = Array(b.prefix(12)) == [0,100,255,155,0,0,0,0,0,0,0,0]
            if mapped || compatible || nat64 { return publicIPv4(Array(b.suffix(4))) }
            return b[0] != 255 && b[0] & 0xfe != 0xfc && !(b[0] == 0xfe && b[1] & 0xc0 == 0x80) && !(b[0] == 0xfe && b[1] & 0xc0 == 0xc0)
        }
        return false
    }
    private static func publicIPv4(_ b: [UInt8]) -> Bool {
        guard b.count == 4 else { return false }
        return !([0,10,127].contains(b[0]) || b[0] >= 224 || b[0] == 100 && b[1] & 0xc0 == 64 ||
                 b[0] == 169 && b[1] == 254 || b[0] == 172 && b[1] & 0xf0 == 16 ||
                 b[0] == 192 && (b[1] == 168 || b[1] == 0 && b[2] == 0))
    }
    public static func capText(_ text: String) -> String {
        let units = text.utf16
        guard units.count > maxText else { return text }
        var tail = Array(units.suffix(5_000))
        if let first = tail.first, (0xdc00...0xdfff).contains(first) { tail.removeFirst() }
        return prefixText(text, units: 16_500) + "\n…（正文已截断）…\n" + String(decoding: tail, as: UTF16.self)
    }
    static func prefixText(_ text: String, units: Int) -> String {
        var prefix = Array(text.utf16.prefix(units))
        if let last = prefix.last, (0xd800...0xdbff).contains(last) { prefix.removeLast() }
        return String(decoding: prefix, as: UTF16.self)
    }
    static func documentExtension(_ type: String, _ ext: String) -> String? {
        switch type {
        case "application/pdf": return "pdf"
        case "application/vnd.openxmlformats-officedocument.wordprocessingml.document": return "docx"
        case "application/vnd.openxmlformats-officedocument.presentationml.presentation": return "pptx"
        case "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet": return "xlsx"
        default: break
        }
        guard ["", "application/octet-stream", "binary/octet-stream", "application/x-download", "application/force-download", "application/download"].contains(type) else { return nil }
        return ["pdf","docx","pptx","xlsx"].contains(ext) ? ext : nil
    }
    private static func imageExtension(_ data: Data) -> String? {
        if data.starts(with: [0x89,0x50,0x4e,0x47,0x0d,0x0a,0x1a,0x0a]) { return ".png" }
        if data.starts(with: [0xff,0xd8,0xff]) { return ".jpg" }
        if data.starts(with: Data("GIF87a".utf8)) || data.starts(with: Data("GIF89a".utf8)) { return ".gif" }
        if data.starts(with: Data("BM".utf8)) { return ".bmp" }
        if data.count >= 12 && data.starts(with: Data("RIFF".utf8)) && data.dropFirst(8).starts(with: Data("WEBP".utf8)) { return ".webp" }
        return nil
    }
    private static func validatedImageExtension(_ data: Data) -> String? {
        guard let ext = imageExtension(data), let source = CGImageSourceCreateWithData(data as CFData, [kCGImageSourceShouldCache: false] as CFDictionary),
              CGImageSourceGetStatus(source) == .statusComplete, CGImageSourceGetCount(source) > 0 else { return nil }
        let options = [kCGImageSourceCreateThumbnailFromImageAlways: true, kCGImageSourceThumbnailMaxPixelSize: 512, kCGImageSourceShouldCache: false] as CFDictionary
        guard CGImageSourceCreateThumbnailAtIndex(source, 0, options) != nil else { return nil }
        return ext
    }
    private static func saveDownload(_ data: Data, ext: String, home: String?) throws -> URL {
        let root: URL
        if let home {
            guard home.hasPrefix("/"), !home.utf8.contains(0) else { throw PlatformError.message("PinCC Home 需要本机绝对路径") }
            root = URL(fileURLWithPath: home, isDirectory: true).appendingPathComponent("files", isDirectory: true)
        } else {
            root = FileManager.default.temporaryDirectory.appendingPathComponent("PinCCSources", isDirectory: true)
        }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let hash = String(Insecure.SHA1.hash(data: data).map { String(format: "%02x", $0) }.joined().prefix(20))
        let saved = root.appendingPathComponent(hash).appendingPathExtension(ext)
        try data.write(to: saved, options: .atomic)
        return saved
    }
    private static func decode(_ data: Data, charset: String? = nil, sniffHTML: Bool = false) -> String {
        if data.starts(with: [0xff,0xfe,0x00,0x00]) { return String(data: data, encoding: .utf32LittleEndian) ?? "" }
        if data.starts(with: [0x00,0x00,0xfe,0xff]) { return String(data: data, encoding: .utf32BigEndian) ?? "" }
        if data.starts(with: [0xff,0xfe]) { return String(data: data, encoding: .utf16LittleEndian) ?? "" }
        if data.starts(with: [0xfe,0xff]) { return String(data: data, encoding: .utf16BigEndian) ?? "" }
        var encoding = charset?.trimmingCharacters(in: CharacterSet(charactersIn: "\"' "))
        if encoding?.isEmpty != false && sniffHTML {
            let head = String(decoding: data.prefix(4096), as: UTF8.self)
            encoding = attribute("charset", head)
        }
        if let encoding {
            let normalized = ["gb2312","gbk","x-gbk","gb_2312-80"].contains(encoding.lowercased()) ? "gb18030" : encoding
            let cf = CFStringConvertIANACharSetNameToEncoding(normalized as CFString)
            if cf != kCFStringEncodingInvalidId, let text = String(data: data, encoding: String.Encoding(rawValue: CFStringConvertEncodingToNSStringEncoding(cf))) { return text }
        }
        if let utf8 = String(data: data, encoding: .utf8) { return utf8.trimmingCharacters(in: CharacterSet(charactersIn: "\u{feff}")) }
        let gb = String.Encoding(rawValue: CFStringConvertEncodingToNSStringEncoding(CFStringEncoding(CFStringEncodings.GB_18030_2000.rawValue)))
        return String(data: data, encoding: gb) ?? String(decoding: data, as: UTF8.self)
    }
    // A small bounded HTML tokenizer extracts readable text without executing page scripts or fetching subresources.
    private static func html(_ html: String, source: String) -> SourceContent {
        var body = "", title = "", description: String?, inTitle = false, ignored: [String] = []
        var preferredTitle: String?, site: String?, date: String?
        let chars = Array(html.prefix(maxPage))
        var i = 0
        while i < chars.count {
            // HTML script/style are raw-text elements: a '<' in JavaScript or CSS is not a tag.
            if let rawText = ignored.last, ["script","style"].contains(rawText) {
                let tag = Array(rawText)
                var close = i, found = false
                while close + tag.count + 2 < chars.count {
                    if chars[close] == "<", chars[close + 1] == "/",
                       tag.indices.allSatisfy({ chars[close + 2 + $0].lowercased() == String(tag[$0]) }) {
                        let boundary = chars[close + 2 + tag.count]
                        if boundary == ">" || boundary.isWhitespace { found = true; break }
                    }
                    close += 1
                }
                guard found else { break }
                i = close
            }
            if chars[i] == "<" {
                var end = i + 1, quote: Character?
                while end < chars.count {
                    let ch = chars[end]
                    if let q = quote { if ch == q { quote = nil } }
                    else if ch == "\"" || ch == "'" { quote = ch }
                    else if ch == ">" { break }
                    end += 1
                }
                guard end < chars.count else { break }
                let raw = String(chars[(i + 1)..<end]), lower = raw.lowercased()
                let closing = lower.hasPrefix("/")
                let tag = lower.trimmingCharacters(in: CharacterSet(charactersIn: "/ \t\n")).split(whereSeparator: { $0.isWhitespace }).first.map(String.init) ?? ""
                if ["script","style","noscript","svg","nav","footer"].contains(tag) {
                    if closing {
                        if let index = ignored.lastIndex(of: tag) { ignored.removeSubrange(index...) }
                    } else if tag != "svg" || !lower.trimmingCharacters(in: .whitespacesAndNewlines).hasSuffix("/") {
                        ignored.append(tag)
                    }
                }
                if tag == "title" { inTitle = !closing }
                if tag == "meta", let content = attribute("content", raw) {
                    let key = (attribute("property", raw) ?? attribute("name", raw) ?? attribute("itemprop", raw))?.lowercased()
                    if key == "og:title" { preferredTitle = content }
                    if key == "og:site_name" { site = content }
                    if key == "article:published_time" || date == nil && ["og:updated_time","date","datepublished","pubdate"].contains(key ?? "") { date = content }
                    if ["description","og:description"].contains(key ?? "") { description = content }
                }
                if ["p","div","br","li","h1","h2","h3","article","section","tr"].contains(tag) && ignored.isEmpty { body += "\n" }
                i = end + 1
            } else {
                if ignored.isEmpty { if inTitle { title.append(chars[i]) } else { body.append(chars[i]) } }
                i += 1
            }
        }
        let text = entities(body).components(separatedBy: .newlines).map { $0.split(whereSeparator: { $0.isWhitespace }).joined(separator: " ") }.filter { !$0.isEmpty }.joined(separator: "\n")
        return SourceContent(text: capText(text), title: entities(preferredTitle ?? title).trimmingCharacters(in: .whitespacesAndNewlines), description: description.map(entities), source: source, site: site.flatMap { let value = entities($0).trimmingCharacters(in: .whitespacesAndNewlines); return value.isEmpty ? nil : value }, date: date.flatMap { $0.utf16.count >= 10 ? prefixText($0, units: 10) : nil })
    }
    private static func attribute(_ name: String, _ tag: String) -> String? {
        let pattern = "(?i)(?:^|\\s)" + name + "\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s>]+))"
        guard let regex = try? NSRegularExpression(pattern: pattern), let match = regex.firstMatch(in: tag, range: NSRange(tag.startIndex..., in: tag)) else { return nil }
        for index in 1...3 { if let range = Range(match.range(at: index), in: tag) { return String(tag[range]) } }; return nil
    }
    private static func entities(_ text: String) -> String {
        var result = text
        for (name, value) in [("&nbsp;"," "),("&quot;","\""),("&apos;","'"),("&lt;","<"),("&gt;",">"),("&amp;","&")] { result = result.replacingOccurrences(of: name, with: value) }
        guard let regex = try? NSRegularExpression(pattern: "&#(x[0-9a-fA-F]+|[0-9]+);") else { return result }
        let ns = result as NSString
        for match in regex.matches(in: result, range: NSRange(location: 0, length: ns.length)).reversed() {
            let raw = ns.substring(with: match.range(at: 1))
            let value = raw.hasPrefix("x") ? UInt32(raw.dropFirst(), radix: 16) : UInt32(raw)
            if let value, let scalar = UnicodeScalar(value), let range = Range(match.range, in: result) { result.replaceSubrange(range, with: String(scalar)) }
        }
        return result
    }

    private enum DocumentParseError: LocalizedError, Sendable {
        case message(String)
        var errorDescription: String? { if case let .message(text) = self { return text }; return nil }
    }
    private static func office(data: Data, ext: String) async throws -> String {
        let entries = try await worker(seconds: 15) { stopped in
            if stopped() { throw CancellationError() }
            return try zipEntries(data).filter {
                ext == "docx" ? $0.hasPrefix("word/") && $0.hasSuffix(".xml") && ($0.contains("document") || $0.contains("header") || $0.contains("footer") || $0.contains("footnotes") || $0.contains("endnotes")) :
                ext == "pptx" ? $0.hasPrefix("ppt/slides/slide") && $0.hasSuffix(".xml") :
                $0 == "xl/sharedStrings.xml" || $0.hasPrefix("xl/worksheets/sheet") && $0.hasSuffix(".xml")
            }.sorted { $0.compare($1, options: .numeric) == .orderedAscending }
        }
        guard entries.count <= 256 else { throw DocumentParseError.message("Office 文档超过 256 个解析部件") }
        // unzip reads this immutable snapshot, not a document another app may be changing.
        let snapshot = try await worker(seconds: 15) { stopped in
            if stopped() { throw CancellationError() }
            let root = FileManager.default.temporaryDirectory.appendingPathComponent("PinCCOffice", isDirectory: true)
            try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
            let url = root.appendingPathComponent(UUID().uuidString).appendingPathExtension("zip")
            try data.write(to: url, options: .atomic)
            return url
        }
        defer { try? FileManager.default.removeItem(at: snapshot) }
        var result = "", shared: [String] = []
        let began = ProcessInfo.processInfo.systemUptime
        for entry in entries {
            try Task.checkCancellation()
            let remaining = 60 - (ProcessInfo.processInfo.systemUptime - began)
            guard remaining > 0 else { throw PlatformError.message("Office 读取超时") }
            let run = try await ProcessRunner.run("/usr/bin/unzip", arguments: ["-p", snapshot.path, entry], timeout: remaining, maxOutput: 60_000_000)
            guard run.code == 0, !run.outputTruncated else { throw DocumentParseError.message("Office 部件损坏或超过解析上限") }
            let frozenXML = run.outputData, frozenShared = shared
            let parsed = try await worker(seconds: min(15, remaining)) { stopped -> (String, [String]) in
                let declarations = String(decoding: frozenXML, as: UTF8.self).replacingOccurrences(of: "\0", with: "")
                guard declarations.range(of: "<!DOCTYPE", options: .caseInsensitive) == nil, declarations.range(of: "<!ENTITY", options: .caseInsensitive) == nil else { throw DocumentParseError.message("Office XML 不允许 DTD 或实体声明") }
                let reader = OfficeXML(mode: ext, shared: frozenShared, stopped: stopped)
                let parser = XMLParser(data: frozenXML)
                parser.shouldResolveExternalEntities = false
                parser.delegate = reader
                guard parser.parse() || reader.reachedLimit else {
                    if stopped() { throw CancellationError() }
                    throw DocumentParseError.message("Office XML 部件损坏")
                }
                return (reader.text, reader.items)
            }
            if entry == "xl/sharedStrings.xml" { shared = parsed.1 } else { result += parsed.0 + "\n" }
            if result.utf16.count >= maxText { break }
        }
        return capText(result)
    }
    private static func zipEntries(_ data: Data) throws -> [String] {
        let bytes = [UInt8](data)
        func value(_ offset: Int, _ count: Int) throws -> UInt64 {
            guard offset >= 0, count >= 0, offset <= bytes.count - count else { throw DocumentParseError.message("ZIP 目录越界") }
            return (0..<count).reduce(0) { $0 | UInt64(bytes[offset + $1]) << ($1 * 8) }
        }
        guard bytes.count >= 22 else { throw DocumentParseError.message("Office 不是 ZIP 文件") }
        var end: Int?
        for offset in stride(from: bytes.count - 22, through: max(0, bytes.count - 65_557), by: -1) {
            if try value(offset, 4) == 0x06054b50, try value(offset + 20, 2) == UInt64(bytes.count - offset - 22) { end = offset; break }
        }
        guard let end, try value(end + 4, 4) == 0, try value(end + 8, 2) == value(end + 10, 2) else { throw DocumentParseError.message("不支持跨卷 Office ZIP") }
        let count = Int(try value(end + 10, 2)), start = Int(try value(end + 16, 4)), directorySize = try value(end + 12, 4)
        guard count <= 4096, count != 65535, directorySize != 0xffffffff, start != Int(UInt32.max), start <= end,
              directorySize <= UInt64(end - start) else { throw DocumentParseError.message("ZIP64 或过多 ZIP 条目不受支持") }
        let directoryEnd = start + Int(directorySize)
        var offset = start, total: UInt64 = 0, names: [String] = [], seen: Set<String> = []
        for _ in 0..<count {
            guard offset <= directoryEnd - 46, try value(offset, 4) == 0x02014b50 else { throw DocumentParseError.message("ZIP 中央目录损坏") }
            let flags = try value(offset + 8, 2), method = try value(offset + 10, 2)
            let compressed = try value(offset + 20, 4), size = try value(offset + 24, 4), localOffset = try value(offset + 42, 4)
            let length = Int(try value(offset + 28, 2)), extra = Int(try value(offset + 30, 2)), comment = Int(try value(offset + 32, 2))
            guard flags & 0x41 == 0, [UInt64(0),8].contains(method), size != 0xffffffff, compressed != 0xffffffff,
                  localOffset != 0xffffffff, try value(offset + 34, 2) == 0, offset + 46 + length + extra + comment <= directoryEnd else { throw DocumentParseError.message("加密、ZIP64 或无效 Office 部件") }
            var extraOffset = offset + 46 + length
            let extraEnd = extraOffset + extra
            while extraOffset < extraEnd {
                guard extraEnd - extraOffset >= 4 else { throw DocumentParseError.message("ZIP 附加字段损坏") }
                let tag = try value(extraOffset, 2), size = Int(try value(extraOffset + 2, 2))
                guard tag != 1, size <= extraEnd - extraOffset - 4 else { throw DocumentParseError.message("ZIP64 或无效附加字段") }
                extraOffset += 4 + size
            }
            total += size
            guard total <= 60_000_000 else { throw DocumentParseError.message("Office 解压文本超过 60 MB") }
            let name = String(decoding: bytes[(offset + 46)..<(offset + 46 + length)], as: UTF8.self)
            guard !name.hasPrefix("/"), !name.contains(".."), !name.contains("\\"), !name.contains("*"), !name.contains("?"), !name.contains("["), !name.utf8.contains(0), seen.insert(name).inserted else { throw DocumentParseError.message("Office 部件名无效或重复") }
            names.append(name)
            offset += 46 + length + extra + comment
        }
        guard offset == directoryEnd else { throw DocumentParseError.message("ZIP 中央目录长度不匹配") }
        return names
    }

    private final class NoRedirect: NSObject, URLSessionTaskDelegate, @unchecked Sendable {
        func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest, completionHandler: @escaping @Sendable (URLRequest?) -> Void) { completionHandler(nil) }
    }
    private final class OfficeXML: NSObject, XMLParserDelegate {
        let mode: String, shared: [String]
        let stopped: @Sendable () -> Bool
        var text = "", items: [String] = [], current = "", cell = "", cellType = "", stack: [String] = []
        var inItem = false, inCell = false, reachedLimit = false
        private var textLength = 0
        init(mode: String, shared: [String], stopped: @escaping @Sendable () -> Bool) {
            self.mode = mode; self.shared = shared; self.stopped = stopped
        }
        func parser(_ parser: XMLParser, didStartElement name: String, namespaceURI: String?, qualifiedName: String?, attributes: [String: String]) {
            if stopped() { parser.abortParsing(); return }
            let tag = name.components(separatedBy: ":").last ?? name
            stack.append(tag)
            if tag == "si" { inItem = true; current = "" }
            if tag == "c" { inCell = true; cell = ""; cellType = attributes["t"] ?? "" }
        }
        func parser(_ parser: XMLParser, foundCharacters string: String) {
            if stopped() { parser.abortParsing(); return }
            guard let tag = stack.last, tag == "t" || tag == "v", !stack.contains("rPh") else { return }
            if inItem { current += string }
            else if mode == "xlsx" && inCell { cell += string }
            else if mode != "xlsx" && tag == "t" { append(string, parser: parser) }
        }
        func parser(_ parser: XMLParser, didEndElement name: String, namespaceURI: String?, qualifiedName: String?) {
            let tag = name.components(separatedBy: ":").last ?? name
            if tag == "si" { items.append(current); inItem = false }
            if tag == "c" {
                if cellType == "s", let index = Int(cell), shared.indices.contains(index) { append(shared[index] + "\t", parser: parser) }
                else { append(cell + "\t", parser: parser) }
                inCell = false
            }
            if tag == "p" || tag == "row" { append("\n", parser: parser) }
            if !stack.isEmpty { stack.removeLast() }
        }
        private func append(_ string: String, parser: XMLParser) {
            text += string
            textLength += string.utf16.count
            if textLength >= SourceReader.maxText { reachedLimit = true; parser.abortParsing() }
        }
        func parser(_ parser: XMLParser, resolveExternalEntityName name: String, systemID: String?) -> Data? { nil }
    }

    @MainActor private final class WebRenderer: NSObject, WKNavigationDelegate {
        private var view: WKWebView?
        private var continuation: CheckedContinuation<SourceContent, Error>?
        private var deadline: Task<Void, Never>?
        private var sampleDelay: Task<Void, Never>?
        private var samples = 0, redirects = 0
        private var previousText: String?
        private var finished = false

        static func read(_ url: URL) async throws -> SourceContent {
            let renderer = WebRenderer()
            return try await renderer.start(url)
        }
        private func start(_ url: URL) async throws -> SourceContent {
            try await withTaskCancellationHandler {
                try Task.checkCancellation()
                return try await withCheckedThrowingContinuation { continuation in
                    self.continuation = continuation
                    let configuration = WKWebViewConfiguration()
                    configuration.websiteDataStore = .nonPersistent()
                    configuration.preferences.javaScriptCanOpenWindowsAutomatically = false
                    configuration.defaultWebpagePreferences.allowsContentJavaScript = true
                    let view = WKWebView(frame: CGRect(x: 0, y: 0, width: 1280, height: 900), configuration: configuration)
                    view.navigationDelegate = self
                    self.view = view
                    deadline = Task { [weak self] in
                        do { try await Task.sleep(nanoseconds: 30_000_000_000) } catch { return }
                        self?.finish(.failure(PlatformError.message("渲染网页超过 30 秒")))
                    }
                    view.load(URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 30))
                }
            } onCancel: {
                Task { @MainActor in self.finish(.failure(CancellationError())) }
            }
        }
        func webView(_ webView: WKWebView, decidePolicyFor action: WKNavigationAction, decisionHandler: @escaping @MainActor @Sendable (WKNavigationActionPolicy) -> Void) {
            guard !finished, action.targetFrame != nil, let url = action.request.url, SourceReader.validWebURL(url), !action.shouldPerformDownload else {
                decisionHandler(.cancel)
                return
            }
            decisionHandler(.allow)
        }
        func webView(_ webView: WKWebView, decidePolicyFor response: WKNavigationResponse, decisionHandler: @escaping @MainActor @Sendable (WKNavigationResponsePolicy) -> Void) {
            guard !finished, let url = response.response.url, SourceReader.validWebURL(url), response.canShowMIMEType else { decisionHandler(.cancel); return }
            decisionHandler(.allow)
        }
        func webView(_ webView: WKWebView, didReceiveServerRedirectForProvisionalNavigation navigation: WKNavigation!) {
            redirects += 1
            guard redirects <= 8, let url = webView.url, SourceReader.validWebURL(url) else {
                finish(.failure(PlatformError.message("网页跳转无效或次数过多")))
                return
            }
        }
        func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) { sample() }
        func webView(_ webView: WKWebView, didFail navigation: WKNavigation!, withError error: Error) { finish(.failure(error)) }
        func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) { finish(.failure(error)) }
        func webViewWebContentProcessDidTerminate(_ webView: WKWebView) { finish(.failure(PlatformError.message("网页渲染进程已退出"))) }
        func webView(_ webView: WKWebView, didReceive challenge: URLAuthenticationChallenge, completionHandler: @escaping @MainActor @Sendable (URLSession.AuthChallengeDisposition, URLCredential?) -> Void) {
            completionHandler(challenge.protectionSpace.authenticationMethod == NSURLAuthenticationMethodServerTrust ? .performDefaultHandling : .cancelAuthenticationChallenge, nil)
        }
        private struct DOMContent: Decodable {
            var text: String
            var title: String?
            var description: String?
            var source: String?
            var site: String?
            var date: String?
        }
        private func sample() {
            guard !finished, let view else { return }
            samples += 1
            // Return a bounded string so no WebKit-owned objects cross actor boundaries.
            let script = #"""
            (() => {
                const meta = (name) => document.querySelector('meta[property="' + name + '"],meta[name="' + name + '"]')?.content || null;
                const article = document.querySelector('article');
                const main = document.querySelector('main');
                const root = article && (article.innerText || '').length >= 300 ? article : main || document.body;
                return JSON.stringify({ text: (root?.innerText || '').slice(0, 80000), title: (meta('og:title') || document.title || '').slice(0, 1000), description: (meta('og:description') || meta('description') || '').slice(0, 2000), site: (meta('og:site_name') || '').slice(0, 1000), date: (meta('article:published_time') || meta('og:updated_time') || meta('date') || meta('datePublished') || meta('pubdate') || '').slice(0, 64), source: location.href });
            })()
            """#
            view.evaluateJavaScript(script) { [weak self] value, error in
                guard let self, !self.finished else { return }
                if let error { self.finish(.failure(error)); return }
                guard let json = value as? String, let data = json.data(using: .utf8), let dom = try? JSONDecoder().decode(DOMContent.self, from: data),
                      let source = dom.source, let url = URL(string: source), SourceReader.validWebURL(url) else {
                    self.finish(.failure(PlatformError.message("网页渲染未返回可读正文")))
                    return
                }
                let content = SourceContent(text: SourceReader.capText(dom.text), title: dom.title, description: dom.description, source: source, site: dom.site?.isEmpty == false ? dom.site : nil, date: dom.date.flatMap { $0.utf16.count >= 10 ? SourceReader.prefixText($0, units: 10) : nil })
                if self.samples >= 8 || !content.thin && self.previousText == content.text { self.finish(.success(content)); return }
                self.previousText = content.text
                self.sampleDelay?.cancel()
                self.sampleDelay = Task { [weak self] in
                    do { try await Task.sleep(nanoseconds: 750_000_000) } catch { return }
                    self?.sample()
                }
            }
        }
        private func finish(_ result: Result<SourceContent, Error>) {
            guard !finished else { return }
            finished = true
            deadline?.cancel()
            sampleDelay?.cancel()
            view?.navigationDelegate = nil
            view?.stopLoading()
            view = nil
            let continuation = self.continuation
            self.continuation = nil
            continuation?.resume(with: result)
        }
    }
    private final class WorkState<T: Sendable>: @unchecked Sendable {
        private let lock = NSLock()
        private var continuation: CheckedContinuation<T, Error>?
        private var terminal: Result<T, Error>?
        private var stop = false
        func install(_ continuation: CheckedContinuation<T, Error>) {
            lock.lock(); if let terminal { lock.unlock(); continuation.resume(with: terminal) } else { self.continuation = continuation; lock.unlock() }
        }
        func finish(_ result: Result<T, Error>, stopped: Bool = false) {
            lock.lock(); guard terminal == nil else { lock.unlock(); return }
            terminal = result; stop = stopped; let continuation = self.continuation; self.continuation = nil; lock.unlock()
            continuation?.resume(with: result)
        }
        var cancelled: Bool { lock.lock(); defer { lock.unlock() }; return stop }
    }
    private static func worker<T: Sendable>(seconds: TimeInterval, slots: DispatchSemaphore = fileSlots, _ work: @escaping @Sendable (@escaping @Sendable () -> Bool) throws -> T) async throws -> T {
        let state = WorkState<T>()
        return try await withTaskCancellationHandler {
            try Task.checkCancellation()
            return try await withCheckedThrowingContinuation { continuation in
                state.install(continuation)
                DispatchQueue.global(qos: .userInitiated).async {
                    guard slots.wait(timeout: .now() + seconds) == .success else { state.finish(.failure(PlatformError.message("读取队列超时")), stopped: true); return }
                    defer { slots.signal() } // late blocked PDF/DNS work retains its slot until it actually returns
                    if state.cancelled { return }
                    do { state.finish(.success(try work { state.cancelled })) } catch { state.finish(.failure(error)) }
                }
                DispatchQueue.global(qos: .utility).asyncAfter(deadline: .now() + seconds) { state.finish(.failure(PlatformError.message("读取来源超时")), stopped: true) }
            }
        } onCancel: { state.finish(.failure(CancellationError()), stopped: true) }
    }
}
