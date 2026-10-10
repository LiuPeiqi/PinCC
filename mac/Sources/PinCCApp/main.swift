import AppKit
import Darwin
import PinCCCore
import PinCCCli

// CLI arguments reach the headless library before NSApplication is initialized.
var arguments = Array(CommandLine.arguments.dropFirst())
// Launch Services and AppKit may append process/defaults arguments on GUI launches.
if arguments.first?.hasPrefix("-") == true {
    arguments.removeAll { $0.hasPrefix("-psn_") }
    for flag in ["-NSDocumentRevisionsDebugMode", "-ApplePersistenceIgnoreState", "-AppleLanguages", "-AppleLocale", "-NSQuitAlwaysKeepsWindows"] {
        while let index = arguments.firstIndex(of: flag) {
            let end = arguments.indices.contains(index + 1) ? index + 1 : index
            arguments.removeSubrange(index...end)
        }
    }
}
var guiArguments = arguments
var explicitHome: String?
if let index = guiArguments.firstIndex(of: "--home"), guiArguments.indices.contains(index + 1) {
    explicitHome = guiArguments[index + 1]
    guiArguments.removeSubrange(index...(index + 1))
}
if !guiArguments.allSatisfy({ $0 == "--startup" }) {
    exit(await CLI.run(arguments))
}
let application = NSApplication.shared
application.setActivationPolicy(.accessory)
let delegate = AppDelegate(home: Home.resolve(explicitHome))
application.delegate = delegate
withExtendedLifetime(delegate) { application.run() }
