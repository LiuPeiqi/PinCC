import Foundation
import Darwin

public enum PlatformError: LocalizedError, Sendable {
    case message(String)
    public var errorDescription: String? { if case let .message(text) = self { return text }; return nil }
}

/// Token arguments never pass through a shell. Spawn creates the process group before exec.
public enum ProcessRunner {
    public struct Result: Sendable {
        public let code: Int32
        public let output: String
        public let outputData: Data
        public let error: String
        public let outputTruncated: Bool
    }

    /// Finder inherits a small PATH. Capture the user's login PATH once with a constant system-shell program.
    /// Only this fixed script reaches the shell; prompts, cards and CLI arguments never do.
    public static func loginEnvironment() async -> [String: String] { await loginPath.environment() }
    private static let loginPath = LoginPath()
    private actor LoginPath {
        private var captured: Task<[String: String], Never>?
        func environment() async -> [String: String] {
            if let captured { return await captured.value }
            let task = Task.detached(priority: .utility) { () -> [String: String] in
                var environment = ProcessInfo.processInfo.environment
                do {
                    let result = try await ProcessRunner.run("/bin/zsh", arguments: ["-l", "-c", "printf '\\0PINCC_PATH\\0%s\\0' \"$PATH\""],
                        timeout: 5, maxOutput: 16_000)
                    let parts = result.outputData.split(separator: 0, omittingEmptySubsequences: false)
                    let marker = Array("PINCC_PATH".utf8)
                    let index = parts.indices.last { Array(parts[$0]) == marker && $0 + 1 < parts.endIndex }
                    let path = index.flatMap { String(data: Data(parts[$0 + 1]), encoding: .utf8) } ?? ""
                    if result.code == 0, !result.outputTruncated, !path.isEmpty,
                       !path.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }) { environment["PATH"] = path }
                } catch { } // unavailable/broken login configuration keeps the inherited PATH
                return environment
            }
            captured = task
            return await task.value
        }
    }

    public static func executable(_ program: String, environment: [String: String] = ProcessInfo.processInfo.environment) -> String? {
        guard !program.isEmpty, !program.utf8.contains(0) else { return nil }
        if program.contains("/") { let absolute = URL(fileURLWithPath: program).standardizedFileURL.path; return FileManager.default.isExecutableFile(atPath: absolute) ? absolute : nil }
        let path = environment["PATH"] ?? "/usr/bin:/bin:/usr/sbin:/sbin"
        for folder in path.split(separator: ":").map(String.init) + ["/opt/homebrew/bin", "/usr/local/bin", NSHomeDirectory() + "/.local/bin"] {
            let file = URL(fileURLWithPath: folder).appendingPathComponent(program).path
            if FileManager.default.isExecutableFile(atPath: file) { return file }
        }
        return nil
    }

    public static func run(_ executable: String, arguments: [String] = [], input: String? = nil,
                           timeout: TimeInterval, environment: [String: String] = [:],
                           directory: String? = nil, maxOutput: Int = 8_000_000) async throws -> Result {
        let stop = StopFlag()
        return try await withTaskCancellationHandler {
            try Task.checkCancellation()
            return try await withCheckedThrowingContinuation { continuation in
                DispatchQueue.global(qos: .userInitiated).async {
                    do { continuation.resume(returning: try execute(executable, arguments, input, timeout, environment, directory, maxOutput, stop)) }
                    catch { continuation.resume(throwing: error) }
                }
            }
        } onCancel: { stop.cancel() }
    }

    private final class StopFlag: @unchecked Sendable {
        private let lock = NSLock()
        private var stopped = false
        func cancel() { lock.lock(); stopped = true; lock.unlock() }
        var cancelled: Bool { lock.lock(); defer { lock.unlock() }; return stopped }
    }

    private static func execute(_ program: String, _ arguments: [String], _ input: String?, _ timeout: TimeInterval,
                                _ overrides: [String: String], _ directory: String?, _ maxOutput: Int, _ stop: StopFlag) throws -> Result {
        guard timeout > 0, maxOutput > 0 else { throw PlatformError.message("进程预算无效") }
        var environment = ProcessInfo.processInfo.environment
        environment.merge(overrides) { _, new in new }
        guard let path = executable(program, environment: environment) else { throw PlatformError.message("找不到本机程序：\(program)") }
        guard (arguments + environment.map { "\($0.key)=\($0.value)" }).allSatisfy({ !$0.utf8.contains(0) }) else {
            throw PlatformError.message("进程参数含有 NUL")
        }
        if stop.cancelled { throw CancellationError() }
        let began = ProcessInfo.processInfo.systemUptime
        var inputPipe: [Int32] = [0, 0], outPipe: [Int32] = [0, 0], errPipe: [Int32] = [0, 0]
        guard pipe(&inputPipe) == 0 else { throw posixError() }
        guard pipe(&outPipe) == 0 else { close(inputPipe[0]); close(inputPipe[1]); throw posixError() }
        guard pipe(&errPipe) == 0 else { for fd in inputPipe + outPipe { close(fd) }; throw posixError() }
        // Standard descriptors may be closed by a CLI caller: never let pipe ends alias fd 0, 1 or 2.
        var allPipes = [inputPipe, outPipe, errPipe]
        for index in allPipes.indices {
            for end in 0...1 where allPipes[index][end] < 3 {
                let old = allPipes[index][end]
                let duplicated = fcntl(old, F_DUPFD_CLOEXEC, 3)
                if duplicated < 0 { for pair in allPipes { for fd in pair { close(fd) } }; throw posixError() }
                close(old); allPipes[index][end] = duplicated
            }
        }
        inputPipe = allPipes[0]; outPipe = allPipes[1]; errPipe = allPipes[2]
        var openFDs = Set(inputPipe + outPipe + errPipe)
        func closeFD(_ fd: Int32) { if openFDs.remove(fd) != nil { close(fd) } }
        defer { for fd in openFDs { close(fd) } }
        for fd in openFDs { _ = fcntl(fd, F_SETFD, FD_CLOEXEC) }
        var actions: posix_spawn_file_actions_t?
        var attributes: posix_spawnattr_t?
        var rc = posix_spawn_file_actions_init(&actions)
        guard rc == 0 else { throw posixError(rc) }
        defer { posix_spawn_file_actions_destroy(&actions) }
        rc = posix_spawnattr_init(&attributes)
        guard rc == 0 else { throw posixError(rc) }
        defer { posix_spawnattr_destroy(&attributes) }
        func checked(_ code: Int32) throws { if code != 0 { throw posixError(code) } }
        try checked(posix_spawn_file_actions_adddup2(&actions, inputPipe[0], STDIN_FILENO))
        try checked(posix_spawn_file_actions_adddup2(&actions, outPipe[1], STDOUT_FILENO))
        try checked(posix_spawn_file_actions_adddup2(&actions, errPipe[1], STDERR_FILENO))
        for fd in openFDs { try checked(posix_spawn_file_actions_addclose(&actions, fd)) }
        if let directory { try checked(posix_spawn_file_actions_addchdir_np(&actions, directory)) }
        try checked(posix_spawnattr_setpgroup(&attributes, 0))
        try checked(posix_spawnattr_setflags(&attributes, Int16(POSIX_SPAWN_SETPGROUP | POSIX_SPAWN_CLOEXEC_DEFAULT)))
        var argv = ([path] + arguments).map { strdup($0) } + [nil]
        var envp = environment.sorted { $0.key < $1.key }.map { strdup("\($0.key)=\($0.value)") } + [nil]
        defer { for pointer in argv + envp { free(pointer) } }
        var pid: pid_t = 0
        rc = argv.withUnsafeMutableBufferPointer { av in
            envp.withUnsafeMutableBufferPointer { ev in
                posix_spawn(&pid, path, &actions, &attributes, av.baseAddress!, ev.baseAddress!)
            }
        }
        try checked(rc)
        closeFD(inputPipe[0]); closeFD(outPipe[1]); closeFD(errPipe[1])
        let inputBytes = Array((input ?? "").utf8)
        var inputOffset = 0
        if inputBytes.isEmpty { closeFD(inputPipe[1]) }
        for fd in [inputPipe[1], outPipe[0], errPipe[0]] where openFDs.contains(fd) {
            _ = fcntl(fd, F_SETFL, fcntl(fd, F_GETFL) | O_NONBLOCK)
        }
        // Per-descriptor suppression turns a broken child stdin into EPIPE instead of killing PinCC.
        _ = fcntl(inputPipe[1], F_SETNOSIGPIPE, 1)
        var output = Data(), error = Data(), truncated = false
        var status: Int32 = 0, exitedAt: TimeInterval?, stoppedAt: TimeInterval?
        var timedOut = false
        var buffer = [UInt8](repeating: 0, count: 65_536)
        while true {
            let now = ProcessInfo.processInfo.systemUptime
            if stoppedAt == nil && (stop.cancelled || now - began >= timeout) {
                timedOut = !stop.cancelled; stoppedAt = now
                _ = kill(-pid, SIGTERM); if exitedAt == nil { _ = kill(pid, SIGTERM) }; closeFD(inputPipe[1])
            }
            if let stoppedAt, now - stoppedAt >= 0.5 { _ = kill(-pid, SIGKILL); if exitedAt == nil { _ = kill(pid, SIGKILL) } }
            if exitedAt == nil {
                let waited = waitpid(pid, &status, WNOHANG)
                if waited == pid { exitedAt = now; closeFD(inputPipe[1]) }
                else if waited < 0 && errno != EINTR { _ = kill(-pid, SIGKILL); throw posixError() }
            }
            for (fd, cap) in [(outPipe[0], maxOutput), (errPipe[0], 1_000_000)] where openFDs.contains(fd) {
                // Bounded batches ensure a continuously writing child cannot starve deadline/cancellation checks.
                for _ in 0..<16 {
                    let count = Darwin.read(fd, &buffer, buffer.count)
                    if count > 0 {
                        if fd == outPipe[0] {
                            let keep = min(count, max(0, cap - output.count))
                            output.append(contentsOf: buffer.prefix(keep)); truncated = truncated || keep < count
                        } else { error.append(contentsOf: buffer.prefix(min(count, max(0, cap - error.count)))) }
                    } else {
                        if count == 0 || (errno != EAGAIN && errno != EWOULDBLOCK && errno != EINTR) { closeFD(fd) }
                        break
                    }
                }
            }
            if openFDs.contains(inputPipe[1]) {
                let count = inputBytes.withUnsafeBytes { raw in
                    Darwin.write(inputPipe[1], raw.baseAddress!.advanced(by: inputOffset), min(65_536, inputBytes.count - inputOffset))
                }
                if count > 0 { inputOffset += count }
                else if count < 0 && errno != EAGAIN && errno != EWOULDBLOCK && errno != EINTR { closeFD(inputPipe[1]) }
                if inputOffset == inputBytes.count { closeFD(inputPipe[1]) }
            }
            if let exitedAt, (!openFDs.contains(outPipe[0]) && !openFDs.contains(errPipe[0]) || now - exitedAt >= 3) {
                _ = kill(-pid, SIGKILL) // descendants may retain inherited pipes after their leader exits
                break
            }
            var descriptors = openFDs.map { pollfd(fd: $0, events: Int16($0 == inputPipe[1] ? POLLOUT : POLLIN), revents: 0) }
            _ = descriptors.withUnsafeMutableBufferPointer { poll($0.baseAddress, nfds_t($0.count), 50) }
        }
        if stop.cancelled { throw CancellationError() }
        if timedOut { throw PlatformError.message("超过 \(Int(timeout)) 秒未完成") }
        let code = (status & 0x7f) == 0 ? (status >> 8) & 0xff : 128 + (status & 0x7f)
        return Result(code: code, output: decode(output), outputData: output, error: decode(error), outputTruncated: truncated)
    }

    private static func posixError(_ code: Int32 = errno) -> NSError { NSError(domain: NSPOSIXErrorDomain, code: Int(code)) }
    private static func decode(_ data: Data) -> String {
        String(data: data, encoding: .utf8) ?? String(data: data, encoding: .utf16) ?? String(decoding: data, as: UTF8.self)
    }
}
