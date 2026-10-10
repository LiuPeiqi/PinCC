import Foundation

/// One home policy shared by the app, helper, recovery and maintenance.
public enum Home {
    #if DEBUG
    public static let development = true
    #else
    public static let development = false
    #endif

    public static func resolve(_ explicit: String? = nil, development: Bool = Home.development) -> String {
        if let explicit, !explicit.isEmpty { return canonical(explicit) }
        if let env = ProcessInfo.processInfo.environment["PINCC_HOME"], !env.isEmpty {
            return canonical(env)
        }
        return applicationSupport(development: development).path
    }

    public static func applicationSupport(development: Bool = Home.development) -> URL {
        let root = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support", isDirectory: true)
        return root.appendingPathComponent(development ? "PinCC.dev" : "PinCC", isDirectory: true)
    }

    public static func canonical(_ path: String) -> String {
        URL(fileURLWithPath: (path as NSString).expandingTildeInPath, isDirectory: true)
            .standardizedFileURL.resolvingSymlinksInPath().path
    }
}
