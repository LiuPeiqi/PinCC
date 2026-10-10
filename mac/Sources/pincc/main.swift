import Foundation
import PinCCCli
#if canImport(Darwin)
import Darwin
#elseif canImport(Glibc)
import Glibc
#endif

// A dedicated command-line process never starts NSApplication or creates a window.
let result = await CLI.run(Array(CommandLine.arguments.dropFirst()))
exit(result)
