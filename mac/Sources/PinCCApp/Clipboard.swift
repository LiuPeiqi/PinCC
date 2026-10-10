import AppKit
import PinCCCore
import PinCCMacPlatform

struct ClipboardCard {
    var body: String
    var images: [String]
    var width: Double?
    var height: Double?

    init(body: String = "", images: [String] = [], width: Double? = nil, height: Double? = nil) {
        self.body = body
        self.images = images
        self.width = width
        self.height = height
    }
}

/// Reads only in response to a paste, drop, or new-card action; it never polls the clipboard.
@MainActor
final class Clipboard {
    static let acceptedTypes: [NSPasteboard.PasteboardType] = [.fileURL, .URL, .png, .tiff, .string]

    private let store: Store
    private var consumedChanges: [String: Int] = [:]
    private static let imageExtensions: Set<String> = [
        "png", "jpg", "jpeg", "gif", "bmp", "webp", "tif", "tiff", "ico", "heic", "heif", "avif"
    ]

    init(store: Store) { self.store = store }

    func markUsed(_ pasteboard: NSPasteboard = .general) {
        consumedChanges[pasteboard.name.rawValue] = pasteboard.changeCount
    }

    /// File URLs take priority over their filenames and Finder's accompanying icon bitmap.
    /// A new-card click consumes only an image, never text or a mixed file selection.
    func collect(_ pasteboard: NSPasteboard, onlyNewImage: Bool = false) throws -> [ClipboardCard] {
        let count = pasteboard.changeCount
        let name = pasteboard.name.rawValue
        if onlyNewImage && consumedChanges[name] == count { return [] }

        var cards: [ClipboardCard] = []
        if pasteboard.types?.contains(.fileURL) == true {
            guard let objects = pasteboard.readObjects(forClasses: [NSURL.self],
                              options: [.urlReadingFileURLsOnly: true]) as? [NSURL],
                  !objects.isEmpty else { return [] }
            let files = objects.map { $0 as URL }
            if onlyNewImage && !files.allSatisfy({ Self.imageExtensions.contains($0.pathExtension.lowercased()) }) {
                return []
            }
            for file in files.prefix(12) {
                if Self.imageExtensions.contains(file.pathExtension.lowercased()),
                   let image = try MacImagePayload.read(file) {
                    cards.append(try imageCard(image))
                } else if onlyNewImage {
                    return []
                } else {
                    cards.append(ClipboardCard(body: file.absoluteString))
                }
            }
        } else {
            let text = pasteboard.string(forType: .string)
            if let text, !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                if onlyNewImage { return [] }
                cards = [ClipboardCard(body: text)]
            } else if !onlyNewImage, let url = pasteboard.string(forType: .URL), !url.isEmpty {
                cards = [ClipboardCard(body: url)]
            } else {
                for type in [NSPasteboard.PasteboardType.png, .tiff, .init("public.heic"), .init("public.heif"), .init("public.avif")] {
                    guard let data = pasteboard.data(forType: type),
                          let image = MacImagePayload.decode(data, asPNG: true) else { continue }
                    cards = [try imageCard(image)]
                    break
                }
            }
        }
        // A denied read, malformed payload, or failed file write remains available for a retry.
        if !cards.isEmpty { consumedChanges[name] = count }
        return cards
    }

    private func imageCard(_ image: MacImagePayload) throws -> ClipboardCard {
        ClipboardCard(images: [try store.saveImage(image.data, ext: image.ext)],
                      width: Double(image.width), height: Double(image.height))
    }
}
