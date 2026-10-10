import Foundation

/// Platform-neutral sRGB values. Views convert these to their native color type.
public struct RGBColor: Equatable, Sendable {
    public let hex: String
    public let red: Double
    public let green: Double
    public let blue: Double
    public init(_ hex: String) {
        self.hex = hex
        let value = UInt32(hex.trimmingCharacters(in: CharacterSet(charactersIn: "#")), radix: 16) ?? 0
        red = Double((value >> 16) & 255) / 255
        green = Double((value >> 8) & 255) / 255
        blue = Double(value & 255) / 255
    }
}

public struct Swatch: Equatable, Sendable {
    public let name: String
    public let back: RGBColor
    public let strip: RGBColor
    public let accent: RGBColor
    public let ink: RGBColor
    public init(name: String, back: String, strip: String, accent: String, ink: String = "#1F2328") {
        self.name = name
        self.back = RGBColor(back)
        self.strip = RGBColor(strip)
        self.accent = RGBColor(accent)
        self.ink = RGBColor(ink)
    }
}

public enum Palette {
    /// Storage IDs are stable; Card.rotation supplies the separate automatic sequence.
    public static let all = [
        Swatch(name: "柠檬", back: "#FCF1BC", strip: "#F3E49D", accent: "#786914"),
        Swatch(name: "蜜桃", back: "#FFEBD1", strip: "#FFDCBB", accent: "#935A11"),
        Swatch(name: "蔷薇", back: "#FFE6F4", strip: "#FFD5E8", accent: "#974C72"),
        Swatch(name: "丁香", back: "#F6EAFF", strip: "#EBDBFF", accent: "#78579B"),
        Swatch(name: "天空", back: "#DFF3FF", strip: "#CDE6FF", accent: "#316CA5"),
        Swatch(name: "湖水", back: "#D6F8F7", strip: "#C1EDEC", accent: "#007C7C"),
        Swatch(name: "薄荷", back: "#DEF9DE", strip: "#CCEECC", accent: "#3B793F"),
        Swatch(name: "云灰", back: "#F0F0F0", strip: "#E3E3E3", accent: "#636363"),
        Swatch(name: "墨炭", back: "#2F3133", strip: "#3A3C3F", accent: "#8CC3FC", ink: "#ECECEC")
    ]
    public static let overdue = RGBColor("#D13438")
    public static let uiAccent = RGBColor("#316CA5")
    public static func get(_ index: Int) -> Swatch { all[(index % all.count + all.count) % all.count] }
}
