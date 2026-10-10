import Foundation
import CoreGraphics
import ImageIO
import UniformTypeIdentifiers

/// A validated raster image. Standard formats retain their original bytes and metadata.
public struct MacImagePayload: Sendable {
    public let data: Data
    public let ext: String
    public let width: Int
    public let height: Int

    public static let maximumBytes = 50_000_000
    public static let maximumDecodedPixels = 80_000_000

    /// Converts HEIC/HEIF/AVIF to PNG with orientation in the pixels.
    /// Pasteboard TIFF can request PNG without applying an EXIF transform.
    public static func decode(_ data: Data, fileExtension: String? = nil,
                              asPNG: Bool = false) -> MacImagePayload? {
        guard !data.isEmpty, data.count <= maximumBytes,
              let source = CGImageSourceCreateWithData(data as CFData,
                    [kCGImageSourceShouldCache: false] as CFDictionary),
              let sourceType = CGImageSourceGetType(source),
              UTType(sourceType as String)?.conforms(to: .image) == true else { return nil }
        let identifier = sourceType as String
        let needsTransform = ["public.heic", "public.heif", "public.avif",
                              "public.heics", "public.heifs"].contains(identifier)
        guard let originalExtension = extensionFor(data, type: identifier) ?? (needsTransform ? ".png" : nil) else {
            return nil
        }
        let index = CGImageSourceGetPrimaryImageIndex(source)
        guard let size = safePixelSize(source, index: index) else { return nil }
        let image: CGImage
        if needsTransform {
            guard let transformed = CGImageSourceCreateThumbnailAtIndex(source, index, [
                    kCGImageSourceCreateThumbnailFromImageAlways: true,
                    kCGImageSourceCreateThumbnailWithTransform: true,
                    kCGImageSourceThumbnailMaxPixelSize: max(size.width, size.height)
                  ] as CFDictionary) else { return nil }
            image = transformed
        } else {
            guard let decoded = CGImageSourceCreateImageAtIndex(source, index, nil) else { return nil }
            image = decoded
        }
        if needsTransform || (asPNG && originalExtension != ".png") {
            let output = NSMutableData()
            guard let destination = CGImageDestinationCreateWithData(output as CFMutableData,
                    UTType.png.identifier as CFString, 1, nil) else { return nil }
            // Passing no source properties prevents an already-applied orientation being reapplied.
            CGImageDestinationAddImage(destination, image, nil)
            guard CGImageDestinationFinalize(destination) else { return nil }
            return MacImagePayload(data: output as Data, ext: ".png",
                                   width: image.width, height: image.height)
        }
        let hint = fileExtension?.lowercased().trimmingCharacters(in: CharacterSet(charactersIn: "."))
        let ext = originalExtension == ".tiff" && hint == "tif" ? ".tif" : originalExtension
        return MacImagePayload(data: data, ext: ext, width: image.width, height: image.height)
    }

    /// Checks metadata before decoding pixels, including images already present in a Home.
    public static func safePixelSize(_ source: CGImageSource, index: Int = 0) -> (width: Int, height: Int)? {
        guard index >= 0, index < CGImageSourceGetCount(source),
              let properties = CGImageSourceCopyPropertiesAtIndex(source, index, nil) as? [CFString: Any],
              let width = (properties[kCGImagePropertyPixelWidth] as? NSNumber)?.doubleValue,
              let height = (properties[kCGImagePropertyPixelHeight] as? NSNumber)?.doubleValue,
              width.isFinite, height.isFinite, width > 0, height > 0,
              width.rounded(.towardZero) == width, height.rounded(.towardZero) == height,
              width * height <= Double(maximumDecodedPixels) else { return nil }
        return (Int(width), Int(height))
    }

    public static func read(_ url: URL) throws -> MacImagePayload? {
        guard url.isFileURL,
              url.host == nil || url.host == "" || url.host?.lowercased() == "localhost",
              !url.path.hasPrefix("//"), !url.path.hasPrefix("/\\"),
              url.path.range(of: #"^/[A-Za-z]:[\\/]"#, options: .regularExpression) == nil else { return nil }
        let values = try url.resourceValues(forKeys: [.isRegularFileKey, .fileSizeKey])
        guard values.isRegularFile == true, let size = values.fileSize,
              size > 0, size <= maximumBytes else { return nil }
        return decode(try Data(contentsOf: url, options: .mappedIfSafe), fileExtension: url.pathExtension)
    }

    private static func extensionFor(_ data: Data, type: String) -> String? {
        let bytes = Array(data.prefix(16))
        if bytes.starts(with: [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]) { return ".png" }
        if bytes.starts(with: [0xFF, 0xD8, 0xFF]) { return ".jpg" }
        if bytes.starts(with: Array("GIF87a".utf8)) || bytes.starts(with: Array("GIF89a".utf8)) { return ".gif" }
        if bytes.starts(with: [0x42, 0x4D]) { return ".bmp" }
        if bytes.count >= 12, Array(bytes[0..<4]) == Array("RIFF".utf8),
           Array(bytes[8..<12]) == Array("WEBP".utf8) { return ".webp" }
        if type == UTType.tiff.identifier { return ".tiff" }
        if type == UTType.ico.identifier { return ".ico" }
        return nil
    }
}
