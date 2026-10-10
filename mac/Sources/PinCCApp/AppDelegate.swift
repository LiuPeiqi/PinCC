import AppKit
import PinCCCore

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private let home: String
    private var instanceLease: HomeLease?
    private var desktop: DesktopController?
    private var statusItem: NSStatusItem?
    private var recoveryTask: Task<Void, Never>?
    private var introduction: NSPanel?
    private var reopenName: Notification.Name { Notification.Name("PinCC.show." + Card.hash(home)) }

    init(home: String) { self.home = home; super.init() }

    func applicationDidFinishLaunching(_ notification: Notification) {
        do {
            guard let lease = try HomeLease.tryEnter(home: home, purpose: "Instance") else {
                DistributedNotificationCenter.default().postNotificationName(reopenName, object: nil,
                    userInfo: nil, deliverImmediately: true)
                NSApp.terminate(nil)
                return
            }
            instanceLease = lease
            let store = try Store(home: home)
            let controller = try DesktopController(store: store)
            desktop = controller
            NSApp.nextResponder = controller
            installMainMenu(controller)
            let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
            item.button?.image = NSImage(systemSymbolName: "square.text.square", accessibilityDescription: "Pin Carbon Copy")
            item.button?.sendAction(on: [.leftMouseUp, .rightMouseUp])
            // Both buttons use this one menu; do not infer an action from event type.
            item.menu = controller.menu(screenID: nil)
            statusItem = item
            DistributedNotificationCenter.default().addObserver(self, selector: #selector(reopened),
                                                                 name: reopenName, object: nil)
            controller.start()
            showIntroduction(store: store)
            recoveryTask = Task { [weak self] in
                let report = await Task.detached(priority: .utility) { [home] in
                    var failures: [String] = []
                    do {
                        let recovered = try DraftRecovery.recover(home: home, onFailure: { _, error in failures.append(error.localizedDescription) })
                        return (recovered.count, failures)
                    } catch { failures.append(error.localizedDescription); return (0, failures) }
                }.value
                guard let self, !Task.isCancelled else { return }
                controller.refreshFromDisk()
                controller.showRecoveryNotice(count: report.0)
                if !report.1.isEmpty {
                    self.statusItem?.button?.toolTip = "有草稿未能自动恢复，请打开卡片库的待恢复页。" + report.1.prefix(1).joined()
                    controller.showRecoveryFailure(count: report.1.count)
                }
            }
        } catch {
            let alert = NSAlert(error: error)
            alert.messageText = "PinCC 无法启动"
            alert.runModal()
            NSApp.terminate(nil)
        }
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        desktop?.prepareToTerminate() == false ? .terminateCancel : .terminateNow
    }

    func applicationWillTerminate(_ notification: Notification) {
        recoveryTask?.cancel()
        desktop?.stop()
        DistributedNotificationCenter.default().removeObserver(self)
        if let statusItem { NSStatusBar.system.removeStatusItem(statusItem) }
        instanceLease?.close()
        instanceLease = nil
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        desktop?.showCards(screenID: nil)
        return true
    }
    @objc private func reopened(_ notification: Notification) { desktop?.showCards(screenID: nil) }

    private func showIntroduction(store: Store) {
        do {
            guard !(try store.settingBool("mac.onboarding.seen", fallback: false)), MacScreens.fullscreenScreenIDs().isEmpty else { return }
            let panel = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 350, height: 156),
                styleMask: [.titled, .closable, .nonactivatingPanel], backing: .buffered, defer: false)
            panel.title = "Pin Carbon Copy"
            panel.isReleasedWhenClosed = false
            panel.hidesOnDeactivate = false
            panel.collectionBehavior = [.moveToActiveSpace, .fullScreenNone, .ignoresCycle]
            let text = NSTextField(wrappingLabelWithString: "点屏幕角落的 + 记一张卡片。\n⌃⌥⌘N 可随时新建；复制图片后点 + 采集一次。\n输入会自动保存，Esc 保存并退出编辑。")
            text.font = .systemFont(ofSize: 13)
            text.frame = NSRect(x: 20, y: 50, width: 310, height: 82)
            let button = NSButton(title: "知道了", target: self, action: #selector(dismissIntroduction(_:)))
            button.frame = NSRect(x: 250, y: 14, width: 80, height: 28)
            panel.contentView?.addSubview(text); panel.contentView?.addSubview(button)
            panel.center(); panel.orderFrontRegardless(); introduction = panel
            try store.setSetting("mac.onboarding.seen", true)
        } catch { desktop?.reportError(error) }
    }
    @objc private func dismissIntroduction(_ sender: Any?) { introduction?.close(); introduction = nil }

    private func installMainMenu(_ desktop: DesktopController) {
        let main = NSMenu()
        let appItem = NSMenuItem()
        let appMenu = NSMenu(title: "PinCC")
        let about = NSMenuItem(title: "关于 Pin Carbon Copy", action: #selector(NSApplication.orderFrontStandardAboutPanel(_:)), keyEquivalent: "")
        appMenu.addItem(about)
        appMenu.addItem(.separator())
        appMenu.addItem(desktop.menuItem(title: "设置…", action: #selector(DesktopController.performSettings(_:)), key: ","))
        appMenu.addItem(.separator())
        appMenu.addItem(NSMenuItem(title: "退出 PinCC", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q"))
        appItem.submenu = appMenu
        main.addItem(appItem)

        let fileItem = NSMenuItem(), file = NSMenu(title: "文件")
        file.addItem(desktop.menuItem(title: "新建卡片", action: #selector(DesktopController.performNew(_:)), key: "n"))
        file.addItem(desktop.menuItem(title: "粘贴为新卡片", action: #selector(DesktopController.performPasteNew(_:)), key: ""))
        file.addItem(desktop.menuItem(title: "卡片库…", action: #selector(DesktopController.performLibrary(_:)), key: "f"))
        file.addItem(.separator())
        file.addItem(NSMenuItem(title: "关闭窗口", action: #selector(NSWindow.performClose(_:)), keyEquivalent: "w"))
        fileItem.submenu = file
        main.addItem(fileItem)

        let editItem = NSMenuItem(), edit = NSMenu(title: "编辑")
        for (title, selector, key, modifiers) in [
            ("撤销", "undo:", "z", NSEvent.ModifierFlags.command),
            ("重做", "redo:", "z", NSEvent.ModifierFlags([.command, .shift])),
            ("剪切", "cut:", "x", NSEvent.ModifierFlags.command),
            ("复制", "copy:", "c", NSEvent.ModifierFlags.command),
            ("粘贴", "paste:", "v", NSEvent.ModifierFlags.command),
            ("全选", "selectAll:", "a", NSEvent.ModifierFlags.command)
        ] {
            let item = NSMenuItem(title: title, action: Selector(selector), keyEquivalent: key)
            item.keyEquivalentModifierMask = modifiers
            edit.addItem(item) // nil target preserves NSTextView's responder and undo routing.
        }
        editItem.submenu = edit
        main.addItem(editItem)
        let viewItem = NSMenuItem()
        viewItem.submenu = desktop.menu(screenID: nil)
        viewItem.submenu?.title = "桌面"
        main.addItem(viewItem)
        let windowItem = NSMenuItem(), windowMenu = NSMenu(title: "窗口")
        windowMenu.addItem(NSMenuItem(title: "最小化", action: #selector(NSWindow.performMiniaturize(_:)), keyEquivalent: "m"))
        windowItem.submenu = windowMenu
        main.addItem(windowItem)
        NSApp.windowsMenu = windowMenu
        NSApp.mainMenu = main
    }
}
