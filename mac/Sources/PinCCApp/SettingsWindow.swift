import AppKit
import PinCCCore
import PinCCMacPlatform
import ServiceManagement

/// Native auxiliary settings; values are committed through Store and applied by the desktop owner.
@MainActor
final class SettingsWindow: NSWindowController, NSTextFieldDelegate, NSTextViewDelegate, NSWindowDelegate {
    private let store: Store
    private weak var desktop: DesktopController?
    private var controls: [String: NSControl] = [:]
    private var defaults: [String: String] = [:]
    private var catalog: SkillsCatalog?
    private let loginToggle = NSButton(checkboxWithTitle: "登录时启动 PinCC", target: nil, action: nil)
    private let loginStatus = NSTextField(wrappingLabelWithString: "")
    private let loginSettings = NSButton(title: "打开系统登录项设置…", target: nil, action: nil)
    private let recorder = ShortcutRecorder()
    private let shortcutStatus = NSTextField(wrappingLabelWithString: "")
    private let result = NSTextField(wrappingLabelWithString: "")
    private let skillsText = NSTextView()
    private let prompt = NSTextView()
    private let detectButton = NSButton(title: "检测工具", target: nil, action: nil)
    private let testButton = NSButton(title: "测试当前执行器", target: nil, action: nil)

    init(store: Store, desktop: DesktopController) {
        self.store = store
        self.desktop = desktop
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 640, height: 620),
                              styleMask: [.titled, .closable, .miniaturizable, .resizable],
                              backing: .buffered, defer: false)
        window.title = "PinCC · 设置"
        window.isReleasedWhenClosed = false
        window.contentMinSize = NSSize(width: 560, height: 620)
        super.init(window: window)
        window.delegate = self
        buildInterface()
        window.center()
        refresh()
    }

    required init?(coder: NSCoder) { return nil }

    func show() {
        refresh()
        loadCatalog()
        NSApp.activate(ignoringOtherApps: true)
        showWindow(nil)
        window?.makeKeyAndOrderFront(nil)
    }

    func refresh() {
        do {
            for (key, control) in controls {
                if let field = control as? NSTextField, field.currentEditor() != nil { continue }
                let value = try store.setting(key, fallback: defaults[key] ?? "")
                if let popup = control as? NSPopUpButton {
                    if let item = popup.itemArray.first(where: { ($0.representedObject as? String) == value }) { popup.select(item) }
                    else {
                        popup.addItem(withTitle: value.isEmpty ? "关闭" : value)
                        popup.lastItem?.representedObject = value
                        popup.select(popup.lastItem)
                    }
                } else if let button = control as? NSButton { button.state = value == "1" ? .on : .off }
                else if let field = control as? NSTextField { field.stringValue = value }
            }
            if window?.firstResponder !== prompt {
                let savedPrompt = try store.setting("llm.prompt")
                prompt.string = savedPrompt.isEmpty ? defaultPrompt : savedPrompt
            }
            recorder.setting = try store.setting("mac.hk.new", fallback: Hotkeys.defaultSetting)
            shortcutStatus.stringValue = desktop?.hotkeyProblem ?? (recorder.setting.isEmpty ? "未绑定全局快捷键。" : "已注册。")
            shortcutStatus.textColor = desktop?.hotkeyProblem == nil ? .secondaryLabelColor : .systemRed
            updateCustomAvailability()
            updateLoginStatus()
        } catch { desktop?.reportError(error) }
    }

    func windowDidBecomeKey(_ notification: Notification) { updateLoginStatus() }
    func windowDidResignKey(_ notification: Notification) { recorder.cancelRecording() }
    func windowWillClose(_ notification: Notification) {
        window?.makeFirstResponder(nil)
        recorder.cancelRecording()
    }

    private func buildInterface() {
        guard let content = window?.contentView else { return }
        let tabs = NSTabView()
        tabs.tabViewType = .topTabsBezelBorder
        content.addSubview(tabs)
        tabs.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            tabs.leadingAnchor.constraint(equalTo: content.leadingAnchor, constant: 12),
            tabs.trailingAnchor.constraint(equalTo: content.trailingAnchor, constant: -12),
            tabs.topAnchor.constraint(equalTo: content.topAnchor, constant: 12),
            tabs.bottomAnchor.constraint(equalTo: content.bottomAnchor, constant: -12)
        ])

        let general = section(tabs, label: "通用")
        addHeading("通用", to: general)
        loginToggle.target = self
        loginToggle.action = #selector(setLoginItem)
        general.addArrangedSubview(loginToggle)
        styleNote(loginStatus)
        general.addArrangedSubview(loginStatus)
        loginSettings.bezelStyle = .rounded
        loginSettings.target = self
        loginSettings.action = #selector(openLoginSettings)
        general.addArrangedSubview(loginSettings)
        general.addArrangedSubview(toggle("sound", title: "提醒时播放提示音", fallback: "1"))
        addNote("PinCC 的卡片是独立悬浮窗口。关闭设置或卡片库后，桌面卡片继续显示。", to: general)

        let appearance = section(tabs, label: "外观")
        addHeading("外观与桌面", to: appearance)
        addRow("新卡配色", popup("color.mode", options: [("off","关"),("rotate","轮换"),("kind","按类型"),("tag","按 #标签")], fallback: "rotate"), to: appearance)
        addRow("文字大小", popup("text.size", options: [("13","小"),("14","中"),("16","大"),("18","特大")], fallback: "14"), to: appearance)
        addRow("平铺起点", popup("tile.corner", options: corners, fallback: "tr"), to: appearance)
        addRow("卡片间距", popup("tile.gap", options: [("8","紧凑"),("12","适中"),("18","宽松")], fallback: "12"), to: appearance)
        addRow("+ 按钮位置", popup("hub.corner", options: corners, fallback: "br"), to: appearance)
        addNote("也可以把屏幕角落的 + 拖到另一个角。卡片的置顶、透明度和固定尺寸由每张卡片自行保存。", to: appearance)
        addNote("选择后立即保存并更新桌面。", to: appearance)

        let shortcuts = section(tabs, label: "快捷键")
        addHeading("全局快捷键", to: shortcuts)
        recorder.bezelStyle = .rounded
        recorder.onCapture = { [weak self] value in self?.save("mac.hk.new", value) }
        recorder.onRecordingChanged = { [weak self] active in self?.desktop?.setShortcutRecording(active) }
        addRow("新建卡片", recorder, to: shortcuts)
        let reset = NSButton(title: "恢复默认", target: self, action: #selector(resetShortcut))
        reset.bezelStyle = .rounded
        shortcuts.addArrangedSubview(reset)
        styleNote(shortcutStatus)
        shortcuts.addArrangedSubview(shortcutStatus)
        addNote("点组合键按钮后按下快捷键。需要包含 ⌘ 或 ⌃；Delete 清除绑定，Esc 取消录制。录制只接收此窗口内的按键。", to: shortcuts)
        addNote("卡片内：Return 编辑；Esc 保存退出；⌘N 新建；⌘F 卡片库；⌘, 设置；⌘⌫ 删除；⌘Z 撤销；⇧⌘Z 重做。", to: shortcuts)

        let ai = section(tabs, label: "AI 摘要")
        addHeading("本机 CLI 执行器", to: ai)
        addRow("工具", popup("mac.llm.tool", options: [("","关闭"),("custom","自定义")], fallback: ""), to: ai)
        addRow("模型", field("mac.llm.model", placeholder: "留空使用执行器默认模型"), to: ai)
        addRow("自定义参数", field("mac.llm.custom", placeholder: "[\"ollama\",\"run\",\"模型名\",\"--nowordwrap\"]"), to: ai)
        addNote("自定义参数填写 JSON 字符串数组；第一项是程序名或绝对路径。提示词从标准输入传入，结果从标准输出读取。", to: ai)
        detectButton.bezelStyle = .rounded
        detectButton.target = self
        detectButton.action = #selector(detectTools)
        testButton.bezelStyle = .rounded
        testButton.target = self
        testButton.action = #selector(testRunner)
        let runnerActions = NSStackView(views: [detectButton, testButton])
        runnerActions.orientation = .horizontal
        runnerActions.spacing = 10
        ai.addArrangedSubview(runnerActions)
        ai.addArrangedSubview(toggle("llm.auto", title: "新建链接或文档卡片时自动读取并摘要", fallback: "1"))
        addRow("任务总时限", field("llm.run_timeout", placeholder: "留空由各项任务计算预算"), to: ai)
        addNote("笔记仅在点 ✨ 后摘要。命令、模型与环境配置保存在这台 Mac 的设置中。", to: ai)
        styleNote(result)
        result.isSelectable = true
        ai.addArrangedSubview(result)
        addHeading("提示词模板", to: ai)
        prompt.frame = NSRect(x: 0, y: 0, width: 520, height: 150)
        prompt.minSize = .zero
        prompt.maxSize = NSSize(width: .greatestFiniteMagnitude, height: .greatestFiniteMagnitude)
        prompt.isRichText = false
        prompt.font = .monospacedSystemFont(ofSize: 11, weight: .regular)
        prompt.delegate = self
        prompt.isHorizontallyResizable = false
        prompt.isVerticallyResizable = true
        prompt.autoresizingMask = [.width]
        prompt.textContainer?.widthTracksTextView = true
        let promptScroll = NSScrollView()
        promptScroll.hasVerticalScroller = true
        promptScroll.autohidesScrollers = true
        promptScroll.borderType = .bezelBorder
        promptScroll.documentView = prompt
        promptScroll.heightAnchor.constraint(equalToConstant: 150).isActive = true
        ai.addArrangedSubview(promptScroll)
        let resetPrompt = NSButton(title: "恢复默认模板", target: self, action: #selector(resetPromptTemplate))
        resetPrompt.bezelStyle = .rounded
        ai.addArrangedSubview(resetPrompt)
        addNote("可用变量：{what}、{title}、{source}、{budget}、{content}。离开文本框后保存；留空使用 summary 技能的模板。", to: ai)

        let data = section(tabs, label: "数据与技能")
        addHeading("本地数据", to: data)
        let home = NSTextField(wrappingLabelWithString: store.home)
        home.isSelectable = true
        home.font = .monospacedSystemFont(ofSize: 11, weight: .regular)
        data.addArrangedSubview(home)
        let folderActions = NSStackView(views: [
            NSButton(title: "打开数据目录", target: self, action: #selector(openHome)),
            NSButton(title: "打开技能目录", target: self, action: #selector(openSkills)),
            NSButton(title: "打开执行器目录", target: self, action: #selector(openAgents))
        ])
        folderActions.orientation = .horizontal
        folderActions.spacing = 8
        for case let button as NSButton in folderActions.arrangedSubviews { button.bezelStyle = .rounded }
        data.addArrangedSubview(folderActions)
        addRow("记录保留", popup("retention.days", options: [("7","7 天"),("30","30 天"),("90","90 天"),("365","1 年")], fallback: "30"), to: data)
        addNote("保留期用于已删除卡片与操作记录；已完成卡片一直保留。", to: data)
        addHeading("生效的技能与执行器", to: data)
        skillsText.frame = NSRect(x: 0, y: 0, width: 520, height: 185)
        skillsText.minSize = .zero
        skillsText.maxSize = NSSize(width: .greatestFiniteMagnitude, height: .greatestFiniteMagnitude)
        skillsText.isEditable = false
        skillsText.isSelectable = true
        skillsText.isRichText = false
        skillsText.font = .systemFont(ofSize: 11)
        skillsText.textContainerInset = NSSize(width: 8, height: 8)
        skillsText.isHorizontallyResizable = false
        skillsText.isVerticallyResizable = true
        skillsText.autoresizingMask = [.width]
        skillsText.textContainer?.widthTracksTextView = true
        let skillsScroll = NSScrollView()
        skillsScroll.hasVerticalScroller = true
        skillsScroll.autohidesScrollers = true
        skillsScroll.borderType = .bezelBorder
        skillsScroll.documentView = skillsText
        skillsScroll.heightAnchor.constraint(equalToConstant: 185).isActive = true
        data.addArrangedSubview(skillsScroll)
        let reload = NSButton(title: "刷新文件列表", target: self, action: #selector(reloadCatalog))
        reload.bezelStyle = .rounded
        data.addArrangedSubview(reload)
        for stack in [general, appearance, shortcuts, ai, data] {
            for view in stack.arrangedSubviews {
                if view is NSTextField || view is NSScrollView {
                    view.widthAnchor.constraint(equalTo: stack.widthAnchor).isActive = true
                }
            }
        }
    }

    private var defaultPrompt: String { catalog?.find("summary")?.body ?? "" }
    private var corners: [(String, String)] { [("tr","右上"),("tl","左上"),("br","右下"),("bl","左下")] }

    private func section(_ tabs: NSTabView, label: String) -> NSStackView {
        let scroll = NSScrollView()
        scroll.hasVerticalScroller = true
        scroll.autohidesScrollers = true
        scroll.drawsBackground = false
        let document = SettingsDocumentView()
        document.translatesAutoresizingMaskIntoConstraints = false
        scroll.documentView = document
        let stack = NSStackView()
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 14
        document.addSubview(stack)
        stack.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            document.leadingAnchor.constraint(equalTo: scroll.contentView.leadingAnchor),
            document.topAnchor.constraint(equalTo: scroll.contentView.topAnchor),
            document.widthAnchor.constraint(equalTo: scroll.contentView.widthAnchor),
            stack.leadingAnchor.constraint(equalTo: document.leadingAnchor, constant: 22),
            stack.trailingAnchor.constraint(equalTo: document.trailingAnchor, constant: -22),
            stack.topAnchor.constraint(equalTo: document.topAnchor, constant: 22),
            stack.bottomAnchor.constraint(equalTo: document.bottomAnchor, constant: -22)
        ])
        let item = NSTabViewItem(identifier: label)
        item.label = label
        item.view = scroll
        tabs.addTabViewItem(item)
        return stack
    }
    private func addHeading(_ title: String, to stack: NSStackView) {
        let label = NSTextField(labelWithString: title)
        label.font = .systemFont(ofSize: 15, weight: .semibold)
        stack.addArrangedSubview(label)
    }

    private func styleNote(_ note: NSTextField) {
        note.font = .systemFont(ofSize: 11)
        note.textColor = .secondaryLabelColor
    }

    private func addNote(_ text: String, to stack: NSStackView) {
        let label = NSTextField(wrappingLabelWithString: text)
        styleNote(label)
        stack.addArrangedSubview(label)
    }

    private func addRow(_ title: String, _ control: NSView, to stack: NSStackView) {
        let label = NSTextField(labelWithString: title)
        label.widthAnchor.constraint(equalToConstant: 108).isActive = true
        let row = NSStackView(views: [label, control])
        row.orientation = .horizontal
        row.alignment = .centerY
        row.spacing = 12
        stack.addArrangedSubview(row)
        row.widthAnchor.constraint(equalTo: stack.widthAnchor).isActive = true
        control.setContentHuggingPriority(.defaultLow, for: .horizontal)
        control.widthAnchor.constraint(greaterThanOrEqualToConstant: 180).isActive = true
    }

    private func popup(_ key: String, options: [(String, String)], fallback: String) -> NSPopUpButton {
        let control = NSPopUpButton()
        control.identifier = NSUserInterfaceItemIdentifier(key)
        for (value, label) in options {
            control.addItem(withTitle: label)
            control.lastItem?.representedObject = value
        }
        control.target = self
        control.action = #selector(popupChanged)
        controls[key] = control
        defaults[key] = fallback
        return control
    }

    private func field(_ key: String, placeholder: String, fallback: String = "") -> NSTextField {
        let field = NSTextField()
        field.identifier = NSUserInterfaceItemIdentifier(key)
        field.placeholderString = placeholder
        field.delegate = self
        controls[key] = field
        defaults[key] = fallback
        return field
    }

    private func toggle(_ key: String, title: String, fallback: String) -> NSButton {
        let button = NSButton(checkboxWithTitle: title, target: self, action: #selector(toggleChanged))
        button.identifier = NSUserInterfaceItemIdentifier(key)
        controls[key] = button
        defaults[key] = fallback
        return button
    }

    @objc private func popupChanged(_ sender: NSPopUpButton) {
        guard let key = sender.identifier?.rawValue, let value = sender.selectedItem?.representedObject as? String else { return }
        save(key, value)
    }

    @objc private func toggleChanged(_ sender: NSButton) {
        guard let key = sender.identifier?.rawValue else { return }
        save(key, sender.state == .on ? "1" : "0")
    }

    func controlTextDidEndEditing(_ notification: Notification) {
        guard let field = notification.object as? NSTextField, let key = field.identifier?.rawValue else { return }
        let value = field.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        do {
            if key == "mac.llm.custom", !value.isEmpty { _ = try SummaryService.customArguments(value) }
            if key == "mac.llm.model", !SummaryService.validModel(value) { throw PlatformError.message("模型名无效。") }
            if key == "llm.run_timeout", !value.isEmpty {
                guard let seconds = Int(value), (1...86_400).contains(seconds) else { throw PlatformError.message("任务总时限填写 1–86400 秒的整数。") }
            }
            save(key, value.isEmpty ? nil : value)
        } catch {
            result.stringValue = error.localizedDescription
            desktop?.reportError(error)
            refresh()
        }
    }

    func textDidEndEditing(_ notification: Notification) {
        guard let textView = notification.object as? NSTextView, textView === prompt else { return }
        let value = prompt.string
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        save("llm.prompt", trimmed.isEmpty || trimmed == defaultPrompt.trimmingCharacters(in: .whitespacesAndNewlines) ? nil : value)
    }

    @objc private func resetPromptTemplate() {
        window?.makeFirstResponder(nil)
        save("llm.prompt", nil)
    }

    private func save(_ key: String, _ value: String?) {
        do {
            try store.setSetting(key, value)
            desktop?.applySettings()
            refresh()
        } catch {
            result.stringValue = error.localizedDescription
            desktop?.reportError(error)
            refresh()
        }
    }

    private func updateCustomAvailability() {
        let selected = (controls["mac.llm.tool"] as? NSPopUpButton)?.selectedItem?.representedObject as? String
        controls["mac.llm.custom"]?.isEnabled = selected == "custom"
    }

    @objc private func resetShortcut() { save("mac.hk.new", nil) }

    private func updateLoginStatus() {
        switch SMAppService.mainApp.status {
        case .enabled:
            loginToggle.state = .on
            loginToggle.isEnabled = true
            loginStatus.stringValue = "已启用：登录后由 macOS 启动 PinCC。"
        case .requiresApproval:
            loginToggle.state = .on
            loginToggle.isEnabled = true
            loginStatus.stringValue = "已注册，等待你在系统登录项设置中允许。"
        case .notRegistered:
            loginToggle.state = .off
            loginToggle.isEnabled = true
            loginStatus.stringValue = "未启用登录启动。"
        case .notFound:
            loginToggle.state = .off
            loginToggle.isEnabled = false
            loginStatus.stringValue = "系统未找到应用服务。请从完整的 PinCC.app 中运行。"
        @unknown default:
            loginToggle.state = .off
            loginToggle.isEnabled = false
            loginStatus.stringValue = "系统返回了未知登录项状态。"
        }
    }

    @objc private func setLoginItem() {
        do {
            if loginToggle.state == .on { try SMAppService.mainApp.register() }
            else { try SMAppService.mainApp.unregister() }
            updateLoginStatus()
        } catch {
            updateLoginStatus()
            desktop?.reportError(error)
        }
    }

    @objc private func openLoginSettings() { SMAppService.openSystemSettingsLoginItems() }
    @objc private func openHome() { openFolder("") }
    @objc private func openSkills() { openFolder("skills") }
    @objc private func openAgents() { openFolder("agents") }

    private func openFolder(_ child: String) {
        let home = URL(fileURLWithPath: store.home, isDirectory: true)
        let folder = child.isEmpty ? home : home.appendingPathComponent(child, isDirectory: true)
        do {
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            if !NSWorkspace.shared.open(folder) { throw PlatformError.message("无法打开 \(folder.path)") }
        } catch { desktop?.reportError(error) }
    }

    @objc private func reloadCatalog() { loadCatalog() }

    private func loadCatalog() {
        skillsText.string = "技能目录正在加载…"
        desktop?.refreshSkillsCatalog()
    }

    func applySkillsCatalog(_ loaded: SkillsCatalog?, error: String?) {
        catalog = loaded
        guard let loaded else {
            skillsText.string = error ?? "技能目录未能读取"
            return
        }
        populateRunners(loaded.runners)
        skillsText.string = loaded.skills.map { skill in
            let origin = skill.builtin ? "内置" : skill.origin
            let grants = skill.grants.isEmpty ? "无需额外工具授权" : "运行时申请：" + skill.grants.joined(separator: "、")
            return "\(skill.label) · \(origin)\n\(skill.description)\n\(grants)"
        }.joined(separator: "\n\n")
        if !loaded.errors.isEmpty {
            skillsText.string += "\n\n文件错误：\n" + loaded.errors.joined(separator: "\n")
        }
        refresh()
    }

    private func populateRunners(_ runners: [RunnerProfile]) {
        guard let popup = controls["mac.llm.tool"] as? NSPopUpButton else { return }
        popup.removeAllItems()
        popup.addItem(withTitle: "关闭")
        popup.lastItem?.representedObject = ""
        for runner in runners {
            popup.addItem(withTitle: runner.label)
            popup.lastItem?.representedObject = runner.name
        }
        popup.addItem(withTitle: "自定义")
        popup.lastItem?.representedObject = "custom"
    }

    @objc private func detectTools() {
        guard let desktop else { return }
        setTesting(true, message: "正在检测本机工具…")
        Task { [weak self] in
            do {
                let tools = try await desktop.detectTools()
                guard let self else { return }
                self.setTesting(false, message: tools.isEmpty ? "没有检测到可用工具。" : "检测到：" + tools.joined(separator: "、"))
                self.loadCatalog()
            } catch {
                self?.setTesting(false, message: error.localizedDescription)
                desktop.reportError(error)
            }
        }
    }

    @objc private func testRunner() {
        window?.makeFirstResponder(nil)
        guard let desktop else { return }
        setTesting(true, message: "正在测试当前执行器…")
        Task { [weak self] in
            do {
                let answer = try await desktop.testRunner()
                self?.setTesting(false, message: answer)
            } catch {
                self?.setTesting(false, message: error.localizedDescription)
                desktop.reportError(error)
            }
        }
    }

    private func setTesting(_ busy: Bool, message: String) {
        detectButton.isEnabled = !busy
        testButton.isEnabled = !busy
        result.stringValue = message
    }
}

/// Focused local recording uses AppKit's normal responder chain.
@MainActor
private final class ShortcutRecorder: NSButton {
    var setting = "" { didSet { if !recording { updateTitle() } } }
    var onCapture: ((String) -> Void)?
    var onRecordingChanged: ((Bool) -> Void)?
    private var recording = false {
        didSet { if oldValue != recording { onRecordingChanged?(recording) } }
    }
    override var acceptsFirstResponder: Bool { true }

    init() {
        super.init(frame: .zero)
        target = self
        action = #selector(startRecording)
        setAccessibilityLabel("录制新建卡片的全局快捷键")
        updateTitle()
    }
    required init?(coder: NSCoder) { return nil }

    @objc private func startRecording() {
        window?.makeFirstResponder(self)
        recording = true
        title = "请按组合键…"
    }

    override func performKeyEquivalent(with event: NSEvent) -> Bool {
        guard recording, window?.firstResponder === self else { return super.performKeyEquivalent(with: event) }
        handleKey(event)
        return true
    }

    override func keyDown(with event: NSEvent) {
        guard recording else { super.keyDown(with: event); return }
        handleKey(event)
    }

    func cancelRecording() {
        recording = false
        updateTitle()
    }

    private func handleKey(_ event: NSEvent) {
        if event.keyCode == 53 { recording = false; updateTitle(); return }
        if event.keyCode == 51 || event.keyCode == 117 { capture(""); return }
        if let value = Hotkeys.setting(from: event) { capture(value) }
        else { NSSound.beep() }
    }

    override func resignFirstResponder() -> Bool {
        recording = false
        updateTitle()
        return super.resignFirstResponder()
    }

    private func capture(_ value: String) {
        recording = false
        setting = value
        onCapture?(value)
        updateTitle()
    }

    private func updateTitle() { title = setting.isEmpty ? "未绑定（点此录制）" : setting }
}

@MainActor
private final class SettingsDocumentView: NSView {
    override var isFlipped: Bool { true }
}