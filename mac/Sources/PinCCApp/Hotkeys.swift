import AppKit
import Carbon

/// Registers a shortcut with the system; recording stays inside the settings window.
@MainActor
final class Hotkeys {
    static let defaultSetting = "⌃⌥⌘N"

    private var handler: EventHandlerRef?
    private var hotKey: EventHotKeyRef?
    private var binding: Binding?
    private var action: (@MainActor () -> Void)?
    private var identifier: UInt32 = 0
    private static let signature: OSType = 0x50494343 // PICC

    init() {}

    /// An empty setting disables the shortcut. A failed replacement keeps the old binding.
    func registerNew(setting: String, action: @escaping @MainActor () -> Void) -> String? {
        if setting.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            shutdown()
            return nil
        }
        guard let next = Self.parse(setting) else {
            return "快捷键需要一个按键，并包含 ⌘ 或 ⌃。"
        }
        if next == binding {
            self.action = action
            return nil
        }
        if let conflict = Self.conflict(for: next) { return conflict }
        if handler == nil {
            var eventType = EventTypeSpec(eventClass: OSType(kEventClassKeyboard),
                                          eventKind: UInt32(kEventHotKeyPressed))
            let status = InstallEventHandler(GetApplicationEventTarget(), pinCCHotKeyHandler, 1,
                                             &eventType, Unmanaged.passUnretained(self).toOpaque(), &handler)
            guard status == noErr else {
                return "无法安装全局快捷键（系统错误 \(status)）。"
            }
        }
        let nextID = identifier &+ 1
        var candidate: EventHotKeyRef?
        let status = RegisterEventHotKey(next.keyCode, next.modifiers,
                                         EventHotKeyID(signature: Self.signature, id: nextID),
                                         GetApplicationEventTarget(), OptionBits(kEventHotKeyExclusive), &candidate)
        guard status == noErr, let candidate else {
            return status == OSStatus(eventHotKeyExistsErr)
                ? "快捷键已被占用，请选择其他组合。"
                : "无法注册快捷键（系统错误 \(status)）。"
        }
        if let hotKey { UnregisterEventHotKey(hotKey) }
        hotKey = candidate
        binding = next
        identifier = nextID
        self.action = action
        return nil
    }

    func shutdown() {
        if let hotKey { UnregisterEventHotKey(hotKey) }
        if let handler { RemoveEventHandler(handler) }
        hotKey = nil
        handler = nil
        binding = nil
        action = nil
    }

    fileprivate func receive(_ identifier: EventHotKeyID) -> OSStatus {
        guard identifier.signature == Self.signature, identifier.id == self.identifier,
              hotKey != nil else { return OSStatus(eventNotHandledErr) }
        action?()
        return noErr
    }

    /// Names describe Carbon virtual keys, so recording round-trips to the same physical key.
    static func setting(from event: NSEvent) -> String? {
        guard event.type == .keyDown, !event.isARepeat,
              let key = keys.first(where: { $0.code == UInt32(event.keyCode) }) else { return nil }
        let flags = event.modifierFlags.intersection([.control, .option, .shift, .command])
        guard flags.contains(.command) || flags.contains(.control) else { return nil }
        return (flags.contains(.control) ? "⌃" : "")
            + (flags.contains(.option) ? "⌥" : "")
            + (flags.contains(.shift) ? "⇧" : "")
            + (flags.contains(.command) ? "⌘" : "") + key.name
    }

    private static func conflict(for binding: Binding) -> String? {
        let menuSettings = ["⌘N", "⌘F", "⌘,", "⌘Q", "⌘W", "⌘C", "⌘V", "⌘X", "⌘A",
                            "⌘Z", "⇧⌘Z", "⌘H", "⌘M", "⌘`", "⌘Delete"]
        if menuSettings.compactMap(parse).contains(binding) {
            return "该组合用于应用菜单或系统文本操作，请选择其他快捷键。"
        }
        var value: Unmanaged<CFArray>?
        if CopySymbolicHotKeys(&value) == noErr,
           let systemKeys = value?.takeRetainedValue() as? [[String: Any]],
           systemKeys.contains(where: { key in
               (key[kHISymbolicHotKeyEnabled as String] as? NSNumber)?.boolValue == true
                   && (key[kHISymbolicHotKeyCode as String] as? NSNumber)?.uint32Value == binding.keyCode
                   && (key[kHISymbolicHotKeyModifiers as String] as? NSNumber)?.uint32Value == binding.modifiers
           }) {
            return "该组合已用于系统快捷键，请选择其他组合。"
        }
        let knownSettings = ["⌘Space", "⌥⌘Space", "⌃Space", "⇧⌘3", "⇧⌘4", "⇧⌘5", "⌃⌘A"]
        return knownSettings.compactMap(parse).contains(binding)
            ? "该组合常用于系统功能，请选择其他快捷键。" : nil
    }

    private struct Binding: Equatable {
        let keyCode: UInt32
        let modifiers: UInt32
    }

    private static func parse(_ setting: String) -> Binding? {
        var text = setting.uppercased().trimmingCharacters(in: .whitespacesAndNewlines)
        for (glyph, name) in [("⌃", "CTRL+"), ("⌥", "ALT+"), ("⇧", "SHIFT+"), ("⌘", "CMD+")] {
            text = text.replacingOccurrences(of: glyph, with: name)
        }
        let tokens = text.split(separator: "+", omittingEmptySubsequences: false)
            .map { String($0).trimmingCharacters(in: .whitespaces) }
        guard let keyName = tokens.last, !keyName.isEmpty else { return nil }
        var modifiers: UInt32 = 0
        for token in tokens.dropLast() {
            switch token {
            case "CTRL", "CONTROL": modifiers |= UInt32(controlKey)
            case "ALT", "OPTION", "OPT": modifiers |= UInt32(optionKey)
            case "SHIFT": modifiers |= UInt32(shiftKey)
            case "CMD", "COMMAND": modifiers |= UInt32(cmdKey)
            default: return nil
            }
        }
        guard modifiers & UInt32(controlKey | cmdKey) != 0 else { return nil }
        let aliases = ["ESCAPE": "ESC", "ENTER": "RETURN", "BACKSPACE": "DELETE",
                       "DEL": "DELETE", "SPACEBAR": "SPACE", "PGUP": "PAGEUP", "PGDN": "PAGEDOWN"]
        guard let key = keys.first(where: { $0.name.uppercased() == (aliases[keyName] ?? keyName) }) else { return nil }
        return Binding(keyCode: key.code, modifiers: modifiers)
    }

    private static let keys: [(name: String, code: UInt32)] = [
        ("A", UInt32(kVK_ANSI_A)), ("B", UInt32(kVK_ANSI_B)), ("C", UInt32(kVK_ANSI_C)),
        ("D", UInt32(kVK_ANSI_D)), ("E", UInt32(kVK_ANSI_E)), ("F", UInt32(kVK_ANSI_F)),
        ("G", UInt32(kVK_ANSI_G)), ("H", UInt32(kVK_ANSI_H)), ("I", UInt32(kVK_ANSI_I)),
        ("J", UInt32(kVK_ANSI_J)), ("K", UInt32(kVK_ANSI_K)), ("L", UInt32(kVK_ANSI_L)),
        ("M", UInt32(kVK_ANSI_M)), ("N", UInt32(kVK_ANSI_N)), ("O", UInt32(kVK_ANSI_O)),
        ("P", UInt32(kVK_ANSI_P)), ("Q", UInt32(kVK_ANSI_Q)), ("R", UInt32(kVK_ANSI_R)),
        ("S", UInt32(kVK_ANSI_S)), ("T", UInt32(kVK_ANSI_T)), ("U", UInt32(kVK_ANSI_U)),
        ("V", UInt32(kVK_ANSI_V)), ("W", UInt32(kVK_ANSI_W)), ("X", UInt32(kVK_ANSI_X)),
        ("Y", UInt32(kVK_ANSI_Y)), ("Z", UInt32(kVK_ANSI_Z)),
        ("0", UInt32(kVK_ANSI_0)), ("1", UInt32(kVK_ANSI_1)), ("2", UInt32(kVK_ANSI_2)),
        ("3", UInt32(kVK_ANSI_3)), ("4", UInt32(kVK_ANSI_4)), ("5", UInt32(kVK_ANSI_5)),
        ("6", UInt32(kVK_ANSI_6)), ("7", UInt32(kVK_ANSI_7)), ("8", UInt32(kVK_ANSI_8)),
        ("9", UInt32(kVK_ANSI_9)), ("-", UInt32(kVK_ANSI_Minus)), ("=", UInt32(kVK_ANSI_Equal)),
        ("[", UInt32(kVK_ANSI_LeftBracket)), ("]", UInt32(kVK_ANSI_RightBracket)),
        (";", UInt32(kVK_ANSI_Semicolon)), ("'", UInt32(kVK_ANSI_Quote)),
        (",", UInt32(kVK_ANSI_Comma)), (".", UInt32(kVK_ANSI_Period)),
        ("/", UInt32(kVK_ANSI_Slash)), ("\\", UInt32(kVK_ANSI_Backslash)),
        ("`", UInt32(kVK_ANSI_Grave)), ("Space", UInt32(kVK_Space)),
        ("Return", UInt32(kVK_Return)), ("Tab", UInt32(kVK_Tab)), ("Esc", UInt32(kVK_Escape)),
        ("Delete", UInt32(kVK_Delete)), ("ForwardDelete", UInt32(kVK_ForwardDelete)),
        ("Left", UInt32(kVK_LeftArrow)), ("Right", UInt32(kVK_RightArrow)),
        ("Up", UInt32(kVK_UpArrow)), ("Down", UInt32(kVK_DownArrow)),
        ("Home", UInt32(kVK_Home)), ("End", UInt32(kVK_End)),
        ("PageUp", UInt32(kVK_PageUp)), ("PageDown", UInt32(kVK_PageDown)),
        ("F1", UInt32(kVK_F1)), ("F2", UInt32(kVK_F2)), ("F3", UInt32(kVK_F3)),
        ("F4", UInt32(kVK_F4)), ("F5", UInt32(kVK_F5)), ("F6", UInt32(kVK_F6)),
        ("F7", UInt32(kVK_F7)), ("F8", UInt32(kVK_F8)), ("F9", UInt32(kVK_F9)),
        ("F10", UInt32(kVK_F10)), ("F11", UInt32(kVK_F11)), ("F12", UInt32(kVK_F12)),
        ("F13", UInt32(kVK_F13)), ("F14", UInt32(kVK_F14)), ("F15", UInt32(kVK_F15)),
        ("F16", UInt32(kVK_F16)), ("F17", UInt32(kVK_F17)), ("F18", UInt32(kVK_F18)),
        ("F19", UInt32(kVK_F19)), ("F20", UInt32(kVK_F20))
    ]
}

private func pinCCHotKeyHandler(_ next: EventHandlerCallRef?, _ event: EventRef?,
                              _ userData: UnsafeMutableRawPointer?) -> OSStatus {
    guard let event, let userData else { return OSStatus(eventNotHandledErr) }
    var identifier = EventHotKeyID()
    let status = GetEventParameter(event, EventParamName(kEventParamDirectObject),
                                   EventParamType(typeEventHotKeyID), nil,
                                   ByteCount(MemoryLayout<EventHotKeyID>.size), nil, &identifier)
    guard status == noErr else { return status }
    let owner = Unmanaged<Hotkeys>.fromOpaque(userData).takeUnretainedValue()
    // GetApplicationEventTarget dispatches from AppKit's main event loop.
    return MainActor.assumeIsolated { owner.receive(identifier) }
}
