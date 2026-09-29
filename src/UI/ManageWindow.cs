using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using PinCC.Core;

namespace PinCC.UI;

/// <summary>
/// The one auxiliary window. 卡片: every card (on the desk, done, deleted) with search, per-card history and restore —
/// the safety net. 设置: everything applies immediately (no OK/Cancel). Topmost while open so cards never cover it.
/// </summary>
public sealed class ManageWindow : AuxWindow
{
    readonly Store _s;
    readonly TabControl _tabs = new();
    readonly TextBox _search;
    readonly StackPanel _list = new();
    readonly StackPanel _filters = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 6) };
    string _filter = "desk";
    long? _expanded;

    public ManageWindow(Desk desk) : base(desk, "PinCC", 640, 720)
    {
        _s = desk.Store;
        Topmost = true;
        _search = Field();
        _search.TextChanged += (_, _) => Refresh();
        var top = new StackPanel { Margin = new Thickness(18, 14, 18, 0) };
        top.Children.Add(_search);
        top.Children.Add(_filters);
        top.Children.Add(Muted($"已删除的卡片和操作记录保留 {_s.SettingInt("retention.days", 30)} 天；已完成的卡片一直保留。"));
        var cards = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        cards.Children.Add(top);
        cards.Children.Add(new ScrollViewer { Content = _list, Padding = new Thickness(18, 8, 18, 16), VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        _tabs.Items.Add(new TabItem { Header = "卡片", Content = cards, Tag = "cards" });
        _tabs.Items.Add(new TabItem { Header = "设置", Content = new ScrollViewer { Content = Settings(), VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, Tag = "settings" });
        _tabs.SetResourceReference(StyleProperty, "Ui.Tabs");
        Content = _tabs;
        Loaded += (_, _) => _search.Focus();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && Keyboard.FocusedElement is not TextBox { Tag: "hotkey" }) Close(); };
        Refresh();
    }

    public void Select(string tab) => _tabs.SelectedItem = _tabs.Items.Cast<TabItem>().FirstOrDefault(t => (string)t.Tag == tab) ?? _tabs.SelectedItem;

    // ================= 卡片 =================

    public void Refresh()
    {
        var all = _s.All();
        all.Sort(Card.Compare);
        var q = _search.Text.Trim();
        bool Match(Card c) => q.Length == 0 || new[] { c.Title, c.Body, c.Summary }.Any(t => t?.Contains(q, StringComparison.OrdinalIgnoreCase) == true);
        bool In(Card c, string f) => f switch { "desk" => c.Active, "done" => c.DoneAt != null && c.DeletedAt == null, _ => c.DeletedAt != null };

        _filters.Children.Clear();
        _filters.Children.Add(Segments("f", [("desk", $"活动 {all.Count(c => In(c, "desk") && Match(c))}"), ("done", $"已完成 {all.Count(c => In(c, "done") && Match(c))}"), ("deleted", $"已删除 {all.Count(c => In(c, "deleted") && Match(c))}")],
            _filter, v => { _filter = v; Dispatcher.BeginInvoke(Refresh); }));

        _list.Children.Clear();
        foreach (var c in all.Where(c => In(c, _filter) && Match(c)))
        {
            var card = c;
            var actions = new List<(string, Action)>();
            if (_filter == "desk") actions.Add(("显示", () => Desk.Locate(card.Id)));
            else actions.Add(("恢复", () => { _s.Mutate(_s.Begin(), card.Id, x => { x.DoneAt = null; x.DeletedAt = null; }, "restore"); Desk.Reload(); }));
            if (_filter == "deleted") actions.Add(("彻底删除", () => { _s.Forget(card.Id); Refresh(); }));
            _list.Children.Add(Row(card, actions));
            if (_expanded == card.Id) _list.Children.Add(History(card));
        }
        if (_list.Children.Count == 0) _list.Children.Add(Muted(q.Length > 0 ? "没有匹配的卡片" : "这里还是空的", 13));
    }

    FrameworkElement Row(Card c, List<(string Label, Action Act)> actions)
    {
        var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var sw = Palette.Get(c.ShownColor(Desk.ColorMode));
        g.Children.Add(new Ellipse { Width = 12, Height = 12, Fill = Palette.Brush(sw.Back), Stroke = Palette.Brush(sw.Accent), StrokeThickness = 1, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 0, 0) });
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = c.Title.Length > 0 ? c.Title : "（空）", FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(Muted(Facts(c)));
        Grid.SetColumn(text, 1);
        g.Children.Add(text);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (label, act) in actions)
        {
            var b = Btn(label, act);
            b.Padding = new Thickness(10, 3, 10, 3);
            b.Margin = new Thickness(8, 0, 0, 0);
            buttons.Children.Add(b);
        }
        Grid.SetColumn(buttons, 2);
        g.Children.Add(buttons);
        var border = new Border { Child = g, Padding = new Thickness(8, 6, 8, 6), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = "点击查看这张卡片的修改记录" };
        border.MouseEnter += (_, _) => border.SetResourceReference(Border.BackgroundProperty, "Ui.Hover");
        border.MouseLeave += (_, _) => border.Background = Brushes.Transparent;
        border.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && FindButton(d)) return;
            _expanded = _expanded == c.Id ? null : c.Id;
            Refresh();
        };
        return border;
    }

    static bool FindButton(DependencyObject d)
    {
        for (var x = d; x != null; x = x is Visual ? VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
            if (x is Button) return true;
        return false;
    }

    /// <summary>The card's change history: each step can be undone (restores the card to just before it).</summary>
    FrameworkElement History(Card c)
    {
        var panel = new StackPanel { Margin = new Thickness(30, 0, 8, 8) };
        var ops = _s.Ops(40, c.Id);
        if (ops.Count == 0) panel.Children.Add(Muted("没有记录"));
        foreach (var op in ops)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var grp = op.Grp;
            var undo = Btn("恢复到此前", () => { _s.Restore(grp); Desk.Reload(); Desk.Notify("已恢复"); });
            undo.Padding = new Thickness(8, 2, 8, 2);
            DockPanel.SetDock(undo, Dock.Right);
            row.Children.Add(undo);
            var before = op.Before is { } json ? Card.FromJson(json) : null;
            var preview = before == null ? "（新建之前：不存在）" : Short(Markdown.Plain(before.Body.Replace('\n', ' ')), 40);
            row.Children.Add(new TextBlock { Text = $"{When.FromMs(op.At):MM-dd HH:mm}  {Cli.OpName(op.Name)}   之前：{preview}", VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 });
            panel.Children.Add(row);
        }
        return panel;
    }

    static string Short(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    static string Facts(Card c)
    {
        var now = DateTime.Now;
        var parts = new List<string> { c.Kind switch { "link" => "链接", "file" => "文档", "image" => "图片", "task" => "清单", _ => "笔记" } };
        if (c.RemindAt is long t) parts.Add((c.Overdue ? "⏰ 已到 " : "⏰ ") + When.Format(When.FromMs(t), now));
        if (c.Progress is { Total: > 0 } k) parts.Add($"☑ {k.Done}/{k.Total}");
        if (c.Collapsed) parts.Add("已折叠");
        if (c.Topmost) parts.Add("置顶");
        if (c.DoneAt is long d) parts.Add("完成于 " + When.Format(When.FromMs(d), now));
        else if (c.DeletedAt is long x) parts.Add("删除于 " + When.Format(When.FromMs(x), now));
        else parts.Add("创建于 " + When.Format(When.FromMs(c.CreatedAt), now));
        return string.Join(" · ", parts);
    }

    // ================= 设置 =================

    FrameworkElement Settings()
    {
        var root = new StackPanel { Margin = new Thickness(24, 4, 24, 24) };

        root.Children.Add(Heading("全局快捷键"));
        foreach (var (key, label, def) in Hotkeys.Actions) root.Children.Add(Row(label, HotkeyRecorder(key, def)));
        root.Children.Add(Muted("点击输入框后按下组合键；Backspace 清除（不绑定）。“已注册”表示没有程序用同样方式占用；截图软件等使用键盘钩子的快捷键无法检测，已知的会提示。"));

        root.Children.Add(Heading("AI 摘要（本地 CLI LLM）"));
        root.Children.Add(LlmSettings());
        root.Children.Add(Switch("新建链接或文档卡片时自动读取并摘要（笔记只在点 ✨ 时摘要）", _s.SettingBool("llm.auto", true), v => { _s.SetSetting("llm.auto", v); Desk.ApplySettings(); }));
        var prompt = Field(_s.Setting("llm.prompt") ?? Llm.DefaultTemplate);
        prompt.AcceptsReturn = true;
        prompt.TextWrapping = TextWrapping.Wrap;
        prompt.Height = 150;
        prompt.FontSize = 12;
        prompt.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        prompt.LostFocus += (_, _) => _s.SetSetting("llm.prompt", prompt.Text.Trim() == Llm.DefaultTemplate.Trim() ? null : prompt.Text);
        var reset = Btn("恢复默认", () => { prompt.Text = Llm.DefaultTemplate; _s.SetSetting("llm.prompt", null); });
        reset.HorizontalAlignment = HorizontalAlignment.Left;
        reset.Margin = new Thickness(0, 6, 0, 0);
        root.Children.Add(new Expander
        {
            Header = "提示词模板（{what} {title} {source} {budget} {content}）", Margin = new Thickness(0, 6, 0, 0),
            Content = new StackPanel { Margin = new Thickness(0, 6, 0, 0), Children = { prompt, reset } },
        });

        root.Children.Add(Heading("技能与执行器"));
        root.Children.Add(SkillsPanel());

        root.Children.Add(Heading("外观与布局"));
        root.Children.Add(Row("新卡配色", Segments("color", [("off", "关"), ("rotate", "轮换"), ("kind", "按类型"), ("tag", "按 #标签")], _s.Setting("color.mode", "rotate"), v => Set("color.mode", v))));
        root.Children.Add(Row("文字大小", Segments("size", [("13", "小"), ("14", "中"), ("16", "大"), ("18", "特大")], _s.Setting("text.size", "14"), v => Set("text.size", v))));
        root.Children.Add(Row("平铺起点", Segments("corner", [("tr", "右上"), ("tl", "左上"), ("br", "右下"), ("bl", "左下")], _s.Setting("tile.corner", "tr"), v => Set("tile.corner", v))));
        root.Children.Add(Row("卡片间距", Segments("gap", [("8", "紧凑"), ("12", "适中"), ("18", "宽松")], _s.Setting("tile.gap", "12"), v => Set("tile.gap", v))));
        root.Children.Add(Row("+ 按钮位置", Segments("hub", [("br", "右下"), ("bl", "左下"), ("tr", "右上"), ("tl", "左上")], _s.Setting("hub.corner", "br"), v => Set("hub.corner", v))));
        root.Children.Add(Muted("也可以直接把 + 按钮拖到屏幕的另一个角。"));

        root.Children.Add(Heading("其他"));
        root.Children.Add(Switch("开机自动启动", Desk.AutoStart, Desk.SetAutoStart));
        root.Children.Add(Switch("提醒时播放提示音", _s.SettingBool("sound", true), v => _s.SetSetting("sound", v)));
        root.Children.Add(Switch("把 PinCC 加入 PATH（WSL 与终端中可直接运行 PinCC.exe）", InPath, v => InPath = v));
        root.Children.Add(Row("操作记录保留", Segments("retain", [("7", "7 天"), ("30", "30 天"), ("90", "90 天"), ("365", "1 年")], _s.Setting("retention.days", "30"), v => _s.SetSetting("retention.days", v))));
        var data = new StackPanel { Orientation = Orientation.Horizontal };
        data.Children.Add(Btn("打开数据目录", () => Process.Start("explorer.exe", $"\"{_s.Home}\"")));
        data.Children.Add(Btn("复制 CLI 说明", () => { try { Clipboard.SetText(Cli.Help); Desk.Notify("已复制，可以粘贴给 CLI LLM 工具"); } catch { } }));
        root.Children.Add(Row("数据与接口", data));
        root.Children.Add(Muted($"数据：{_s.Home}\\pincc.db（SQLite；每天自动备份到 backup\\，保留 7 份）\n命令行：\"{Environment.ProcessPath}\" help"));
        return root;
    }

    /// <summary>
    /// Skills (skills\&lt;name&gt;\SKILL.md) and runner profiles (agents\&lt;name&gt;.json) are files: what is in effect,
    /// what is broken and why, and the folders to edit. Changes are picked up by themselves within seconds.
    /// </summary>
    FrameworkElement SkillsPanel()
    {
        var panel = new StackPanel();
        void Fill()
        {
            panel.Children.Clear();
            Skills.Refresh(_s.Home);
            Runners.Refresh(_s.Home);
            foreach (var k in Skills.All)
            {
                var when = string.Join(" ", new[]
                {
                    k.Scope == "desk" ? "整个桌面（+ 的右键菜单）" : null, k.Src.Count > 0 ? "来源 " + string.Join(" ", k.Src) : null,
                    k.Kinds.Count > 0 ? "类型 " + string.Join(",", k.Kinds) : null, k.Exts.Count > 0 ? "扩展名 " + string.Join(",", k.Exts) : null, k.Tag != null ? "#" + k.Tag : null,
                }.OfType<string>());
                var on = string.Join("、", k.On.Select(o => o switch { "ask" => "✨", "create" => "自动", _ => "菜单" }));
                var state = !k.NeedsApproval ? "" : _s.Setting("skill.ok." + k.Name) == k.Hash ? " · 已确认" : " · 第一次运行时确认";
                var line = Muted($"{k.Label}（{k.Name} · {(k.Builtin ? "内置" : "用户")}）：{k.Description}\n    {on}{(when.Length > 0 ? " · " + when : "")} · 输出 {k.Output}"
                    + (k.Grants.Count > 0 ? " · 授权 " + string.Join(",", k.Grants) : "") + (k.Runner != null ? " · 执行器 " + k.Runner : "") + state);
                line.TextWrapping = TextWrapping.Wrap;
                line.Margin = new Thickness(0, 2, 0, 2);
                panel.Children.Add(line);
            }
            panel.Children.Add(Muted("执行器：" + string.Join("、", Runners.All.Select(p => p.Name + (p.Origin == "内置" ? "" : "（用户）")))));
            foreach (var e in Skills.Errors.Concat(Runners.Errors))
                panel.Children.Add(new TextBlock { Text = "✗ " + e, Foreground = Palette.Brush(Palette.Overdue), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 4) };
            buttons.Children.Add(Btn("打开技能文件夹", () => OpenFolder(Skills.Dir(_s.Home))));
            buttons.Children.Add(Btn("打开执行器文件夹", () => OpenFolder(Runners.Dir(_s.Home))));
            buttons.Children.Add(Btn("重新加载", () => { Skills.Reload(_s.Home); Runners.Reload(_s.Home); Fill(); }));
            buttons.Children.Add(Btn("复制给 agent 的 SKILL.md", () => { try { Clipboard.SetText(Cli.AgentSkill()); Desk.Notify("已复制：放进 agent 的技能目录，它就会用 PinCC"); } catch { } }));
            foreach (var b in buttons.Children.OfType<FrameworkElement>().Skip(1)) b.Margin = new Thickness(8, 0, 0, 0);
            panel.Children.Add(buttons);
            panel.Children.Add(Muted("技能 = skills\\<名字>\\SKILL.md（何时用、先取什么数据、允许模型做什么、结果写到哪里；正文是提示词）；执行器 = agents\\<名字>.json（怎样调用一个 CLI 模型）。同名文件覆盖内置的。示例：docs\\examples\\skills。"));
        }
        Fill();
        return panel;
    }

    static void OpenFolder(string dir)
    {
        Directory.CreateDirectory(dir);
        Process.Start("explorer.exe", $"\"{dir}\"");
    }

    /// <summary>
    /// Where the tool runs (Windows / WSL + distribution), which tool (or a custom command, or off), its model; 检测
    /// marks the tools installed in the chosen environment, 测试 runs the configured one. Every change applies at once.
    /// </summary>
    FrameworkElement LlmSettings()
    {
        var panel = new StackPanel();
        var scans = new Dictionary<string, Llm.ScanResult>(); // per environment + distribution
        bool Wsl() => _s.Setting("llm.where") == "wsl";
        string Distro() => Llm.ValidDistro(_s.Setting("llm.distro")) ?? "";
        string ScanKey() => Wsl() ? "wsl:" + Distro() : "windows";
        string ToolValue() => _s.Setting("llm.tool") ?? (_s.Setting("llm.custom") is { Length: > 0 } ? "custom" : "");

        var status = Muted("");
        var current = Muted("", 12);
        current.FontFamily = MarkdownView.Mono;
        var toolHost = new ContentControl();
        var distro = Field(_s.Setting("llm.distro") ?? "");
        distro.Width = 200;
        var distroRow = Row("WSL 发行版", Inline(distro, Muted("留空：默认发行版（wsl -l -v 可查看）")));
        var model = Field(_s.Setting("llm.model") ?? "");
        var custom = Field(_s.Setting("llm.custom") ?? "");
        custom.FontFamily = MarkdownView.Mono;
        custom.FontSize = 12;
        var customHint = Muted("");
        customHint.Margin = new Thickness(118, 2, 0, 0);

        FrameworkElement modelRow = null!, customRow = null!;
        void Refresh()
        {
            bool wsl = Wsl(), isCustom = ToolValue() == "custom", known = ToolValue() is { Length: > 0 } t && !isCustom;
            static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
            distroRow.Visibility = Show(wsl);
            modelRow.Visibility = Show(known);   // only what applies to the chosen tool is shown
            customRow.Visibility = customHint.Visibility = Show(isCustom);
            customHint.Text = (wsl ? "由 WSL 的 bash 执行（登录 shell 的 PATH）" : "由 cmd.exe 执行") + "；从标准输入读取提示词，把回答输出到标准输出。只用于文字摘要。";
            current.Text = Desk.Llm?.Describe() ?? "未启用：" + Llm.Problem(_s);
            scans.TryGetValue(ScanKey(), out var scan);
            var options = new List<(string, string)> { ("", "关闭") };
            options.AddRange(Llm.Tools.Select(t => (t, scan == null ? t : scan.Tools.Contains(t) ? t + " ✓" : t)));
            options.Add(("custom", "自定义"));
            toolHost.Content = Segments("tool", options.ToArray(), ToolValue(), v => { _s.SetSetting("llm.tool", v); Apply(); });
        }
        void Apply()
        {
            Desk.ApplySettings();
            Dispatcher.BeginInvoke(Refresh);
        }
        async Task Scan()
        {
            bool wsl = Wsl();
            var key = ScanKey();
            status.Text = wsl ? "正在检测 WSL 中的工具（首次可能要十几秒）…" : "正在检测 Windows 中的工具…";
            var r = await Llm.Scan(wsl, Distro());
            scans[key] = r;
            if (key != ScanKey()) return; // switched meanwhile: that scan's own call reports
            if (wsl && r.Env.Count > 0) _s.SetSetting("llm.env", System.Text.Json.JsonSerializer.Serialize(r.Env)); // PATH/proxies of this distribution
            status.Text = r.Error != null ? "✗ " + r.Error
                : r.Tools.Count == 0 ? $"{(wsl ? "WSL" : "Windows")} 中没有找到 {string.Join(" / ", Runners.All.Select(p => p.Program))}；可以用“自定义”，或在执行器文件夹里添加"
                : $"{(wsl ? "WSL" : "Windows")} 中找到：{string.Join("、", r.Tools)}（标 ✓）";
            Apply();
        }

        panel.Children.Add(Row("运行环境", Segments("where", [("windows", "Windows"), ("wsl", "WSL")], Wsl() ? "wsl" : "windows", v =>
        {
            _s.SetSetting("llm.where", v);
            Apply();
            if (!scans.ContainsKey(ScanKey())) _ = Scan();
        })));
        panel.Children.Add(distroRow);
        distro.LostFocus += (_, _) =>
        {
            var d = distro.Text.Trim();
            if (d == (_s.Setting("llm.distro") ?? "")) return;
            if (d.Length > 0 && Llm.ValidDistro(d) == null) { status.Text = "✗ 发行版名称只能包含字母、数字、. _ -"; return; }
            _s.SetSetting("llm.distro", d.Length == 0 ? null : d);
            _s.SetSetting("llm.env", null); // another distribution has its own PATH: captured again by the scan
            Apply();
            _ = Scan();
        };
        panel.Children.Add(Row("工具", toolHost));
        model.LostFocus += (_, _) => { _s.SetSetting("llm.model", model.Text.Trim().Length == 0 ? null : model.Text.Trim()); Apply(); };
        panel.Children.Add(modelRow = Row("模型（可选）", model));
        custom.LostFocus += (_, _) => { _s.SetSetting("llm.custom", custom.Text.Trim().Length == 0 ? null : custom.Text.Trim()); Apply(); };
        panel.Children.Add(customRow = Row("自定义命令", custom));
        panel.Children.Add(customHint);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(118, 10, 0, 4) };
        buttons.Children.Add(Btn("检测", () => _ = Scan()));
        buttons.Children.Add(Btn("测试", async () =>
        {
            if (Desk.Llm is not { } llm) { status.Text = "✗ 未启用：" + Llm.Problem(_s); return; }
            status.Text = "正在测试（首次启动可能要十几秒）…";
            var sw = Stopwatch.StartNew();
            try { status.Text = $"✓ 可用（{sw.Elapsed.TotalSeconds:0.0} 秒）：{Short((await llm.Test()).Trim(), 40)}"; }
            catch (Exception e) { status.Text = "✗ " + e.Message; }
        }, primary: true));
        panel.Children.Add(buttons);
        status.Margin = new Thickness(118, 2, 0, 4);
        panel.Children.Add(status);
        current.Margin = new Thickness(118, 2, 0, 6);
        current.ToolTip = "实际执行的命令";
        panel.Children.Add(current);
        Refresh();
        return panel;
    }

    static StackPanel Inline(params UIElement[] items)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var i in items)
        {
            if (i is FrameworkElement f && p.Children.Count > 0) { f.Margin = new Thickness(10, 0, 0, 0); f.VerticalAlignment = VerticalAlignment.Center; }
            p.Children.Add(i);
        }
        return p;
    }

    void Set(string key, string value)
    {
        _s.SetSetting(key, value);
        Desk.ApplySettings();
    }

    /// <summary>
    /// Records the next key combination. While the box has focus a low-level hook takes each chord before other
    /// programs' hotkeys do (so a combination QQ already owns can still be recorded, then reported), our own hotkeys
    /// pause, and the IME is off so keys arrive raw. Esc leaves the box (not the window); Backspace unbinds.
    /// </summary>
    FrameworkElement HotkeyRecorder(string key, string def)
    {
        var box = Field(_s.Setting(key, def));
        box.IsReadOnly = true;
        box.Cursor = Cursors.Hand;
        box.Width = 200;
        box.Tag = "hotkey";
        InputMethod.SetIsInputMethodEnabled(box, false);
        var state = Muted("", 12);
        state.VerticalAlignment = VerticalAlignment.Center;
        state.Margin = new Thickness(10, 0, 0, 0);
        IDisposable? capture = null;
        void Show()
        {
            var g = _s.Setting(key, def);
            state.Text = string.IsNullOrWhiteSpace(g) ? "未绑定" : Desk.HotkeyStatus.TryGetValue(key, out var why) && why != null ? "⚠ " + why : "✓ 已注册";
        }
        void Record(string g)
        {
            if (!box.IsKeyboardFocused) return;
            var mods = g.Split('+')[..^1];
            var last = g.Split('+')[^1];
            if (mods.All(m => m == "Shift") && !(last.Length > 1 && last[0] == 'F' && char.IsDigit(last[1]))) { state.Text = "需要配合 Ctrl / Alt / Win"; return; }
            if (Desk.GestureOwner(g, key) is { } other) { state.Text = $"⚠ 与「{other}」重复，请换一个"; return; }
            box.Text = g;
            _s.SetSetting(key, g);
            state.Text = Desk.Hotkeys.Probe(g) is { } why ? "⚠ " + why : "✓ 可以注册";
        }
        void Stop()
        {
            capture?.Dispose();
            capture = null;
        }
        box.GotKeyboardFocus += (_, _) =>
        {
            Desk.Hotkeys.Clear();
            state.Text = "请按下组合键…";
            Stop();
            // Alt+Tab keeps switching windows; every other chord with a modifier is recorded here.
            capture = Hotkeys.CaptureChords(g =>
            {
                if (g is "Alt+Tab" or "Alt+Shift+Tab" or "Alt+Escape") return false;
                Dispatcher.BeginInvoke(() => Record(g));
                return true;
            });
        };
        box.LostKeyboardFocus += (_, _) => { Stop(); Desk.RegisterHotkeys(); Show(); };
        Deactivated += (_, _) => Stop();
        Closed += (_, _) => Stop();
        box.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true;
            var k = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
            if (k is Key.Back or Key.Delete && Keyboard.Modifiers == ModifierKeys.None) { box.Text = ""; _s.SetSetting(key, ""); state.Text = "未绑定"; return; }
            if (k == Key.Escape) { Keyboard.ClearFocus(); FocusManager.SetFocusedElement(this, _tabs); _tabs.Focus(); return; }
            if (Keyboard.Modifiers == ModifierKeys.None && k is >= Key.F13 and <= Key.F24 && Hotkeys.Format(ModifierKeys.None, k) is { } g) Record(g);
        };
        Show();
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(box);
        row.Children.Add(state);
        return row;
    }

    /// <summary>The exe's folder on the user PATH (then broadcast so new shells, and WSL via appendWindowsPath, see it).</summary>
    static bool InPath
    {
        get
        {
            var dir = System.IO.Path.GetDirectoryName(Environment.ProcessPath) ?? "";
            using var k = Registry.CurrentUser.OpenSubKey("Environment");
            return (k?.GetValue("Path") as string ?? "").Split(';').Any(p => string.Equals(p.TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        }
        set
        {
            var dir = System.IO.Path.GetDirectoryName(Environment.ProcessPath) ?? "";
            using var k = Registry.CurrentUser.CreateSubKey("Environment");
            var raw = k.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
            var parts = raw.Split(';', StringSplitOptions.RemoveEmptyEntries).Where(p => !string.Equals(p.TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)).ToList();
            if (value) parts.Add(dir);
            k.SetValue("Path", string.Join(";", parts), raw.Contains('%') ? RegistryValueKind.ExpandString : RegistryValueKind.String);
            SendMessageTimeout(new IntPtr(0xFFFF), 0x001A, IntPtr.Zero, "Environment", 2, 3000, out _); // WM_SETTINGCHANGE to all
        }
    }

    [DllImport("user32", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeout(IntPtr hwnd, int msg, IntPtr wParam, string lParam, int flags, int timeout, out IntPtr result);
}
