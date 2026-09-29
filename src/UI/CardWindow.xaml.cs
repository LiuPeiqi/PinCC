using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PinCC.Core;

namespace PinCC.UI;

/// <summary>
/// One card = one frameless top-level window (the window rect is the card). Two states only: View (rendered markdown)
/// and Edit (raw source). Enter Edit: double-click text, Enter, or a new card. Leave: click elsewhere or Esc — both
/// save; there is no Done button. Everything else lives in one bottom row: state chips left, hover actions right.
/// </summary>
public partial class CardWindow : Window
{
    public const double PillH = 36, Thumb = 48, MinW = 160, MinH = 64, RowH = 31, PadTop = 10;
    const string CompleteGlyph = "\uEA3A";

    readonly Desk _desk;
    IntPtr _hwnd;
    Group? _session;
    string _editBase = "";
    readonly DispatcherTimer _autosave = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly DispatcherTimer _badgeTimer = new() { Interval = TimeSpan.FromSeconds(1.4) };
    readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    readonly DispatcherTimer _stripTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    readonly DispatcherTimer _leaveTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    Point _down;
    bool _armed, _dragged, _hover, _hoverShown, _overflow, _keepEditing, _inSizeMove;
    DateTime _peekUntil;
    Px _sizeMoveStart;
    Border? _adderHost;
    TextBox? _adder;
    Group? _addSession;
    TextBlock? _addChip; // the progress chip: a "+" joins it on hover
    bool _showDone; // folded checked tasks shown for now (this window only, never stored)
    double _sizeMoveScale = 1;

    public Card Card { get; private set; }
    /// <summary>The desk's registry key: the card id, or a negative number while a new card has no row yet.</summary>
    public long Ref { get; set; }
    public bool Editing { get; private set; }
    /// <summary>Quick add is open: the action row is a one-line input for new checklist items.</summary>
    public bool Adding => _adder != null;
    public bool Hovered => _hover;
    public bool InSizeMove => _inSizeMove;
    /// <summary>Transient status ("摘要中…") and last error; never stored.</summary>
    public string? Busy { get; set; }
    public string? Problem { get; set; }
    /// <summary>Measured height (DIP) when the card fits its content (Card.H == null).</summary>
    public double AutoH { get; private set; } = 96;
    /// <summary>Set by layout when space ran out: shown as a pill without changing the stored state.</summary>
    public bool LayoutPill { get; set; }
    /// <summary>While completing/deleting: the in-place undo strip.</summary>
    public (string Text, Action Undo, Action Expire)? Strip { get; private set; }
    /// <summary>The card's full rect when the strip began: layout keeps it free until the strip expires.</summary>
    public Px StripRect { get; private set; }
    /// <summary>Quick capture: the window that had focus before this card was opened by hotkey or hub.</summary>
    public IntPtr ReturnFocus { get; set; }

    public CardWindow(Desk desk, Card card)
    {
        _desk = desk;
        Card = card;
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Native.AddExStyle(_hwnd, Native.WS_EX_TOOLWINDOW); // out of Alt+Tab (ShowInTaskbar=false alone does not)
            var src = HwndSource.FromHwnd(_hwnd);
            src.AddHook(WndProc);
            src.AddHook(Native.NoActivateOnDpiChange());
        };
        _autosave.Tick += (_, _) => { _autosave.Stop(); Autosave(); };
        _badgeTimer.Tick += (_, _) => { _badgeTimer.Stop(); Badge.Visibility = Visibility.Collapsed; ApplyOpacity(); };
        _hoverTimer.Tick += (_, _) => { _hoverTimer.Stop(); _hoverShown = _hover; ShowActions(); ApplyOpacity(); };
        _stripTimer.Tick += (_, _) => { _stripTimer.Stop(); if (!_hover) Strip?.Expire(); else _stripTimer.Start(); };
        // The resize band is non-client: WPF reports MouseLeave there although the pointer is still on the card.
        _leaveTimer.Tick += (_, _) => { if (!PointerOnCard()) { _leaveTimer.Stop(); PointerLeft(); } };

        MouseEnter += (_, _) =>
        {
            _leaveTimer.Stop();
            if (_hover) return;
            _hover = true;
            _hoverTimer.Start();
        };
        MouseLeave += (_, _) =>
        {
            if (PointerOnCard()) _leaveTimer.Start();
            else PointerLeft();
        };
        Activated += (_, _) => RenderFrame();
        Deactivated += (_, _) =>
        {
            RenderFrame();
            EndAdd();
            if (_keepEditing) { _keepEditing = false; return; } // focus went to our own flyout
            if (Editing) EndEdit(returnFocus: false);
        };
        PreviewMouseLeftButtonDown += OnDown;
        PreviewMouseMove += OnMove;
        PreviewMouseLeftButtonUp += OnUp;
        MouseRightButtonUp += (_, e) => { if (!Editing && Strip == null) { e.Handled = true; OpenMenu(); } };
        MouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Middle && Card.ImageOnly) _desk.ZoomActual(this); };
        PreviewMouseWheel += OnWheel;
        PreviewKeyDown += OnKey;
        Viewport.SizeChanged += (_, _) => CheckOverflow();

        Editor.TextChanged += (_, _) => { if (Editing) { _autosave.Stop(); _autosave.Start(); MeasureAuto(); } };
        Editor.PreviewKeyDown += OnEditorKey;
        Editor.PreviewTextInput += OnEditorText;
        DataObject.AddPastingHandler(Editor, OnPaste);
        AllowDrop = true;
        Drop += (_, e) => { e.Handled = true; _desk.Ingest(this, e.Data); };

        Render();
    }

    bool PointerOnCard() => IsVisible && Native.GetCursorPos(out var p) && Native.WindowFromPoint(p) == _hwnd;

    void PointerLeft()
    {
        if (!_hover) return;
        _hover = false;
        _hoverTimer.Stop();
        _hoverShown = false;
        ShowActions();
        ApplyOpacity();
        if (Strip != null) { _stripTimer.Stop(); _stripTimer.Start(); } // expires 4 s after the pointer leaves
        _desk.OnCardMouseLeave(this);
    }

    // ================= geometry =================

    public IntPtr Hwnd => _hwnd;

    /// <summary>Shown as a pill: collapsed by the user or by layout, but never while overdue, finishing or editing.</summary>
    public bool Pill => (Card.Collapsed || LayoutPill) && !Card.Overdue && Strip == null && !Editing;

    /// <summary>The card's size (DIP) in its current state.</summary>
    public Size DipSize => Strip != null ? new Size(Math.Max(Card.W, MinW), PillH)
        : Pill ? (Card.ImageOnly ? new Size(Thumb, Thumb) : new Size(Card.W, PillH))
        : Expanded;

    /// <summary>The expanded size (DIP): fixed H, the image's aspect, or the measured content height.</summary>
    public Size Expanded => Card.ImageOnly
        ? new Size(Card.W, Card.H ?? Card.W / (_desk.ImageAspect(Card.Images[0]) ?? 1))
        : new Size(Math.Max(Card.W, MinW), Math.Max(Card.H ?? AutoH, MinH));

    /// <summary>Physical rect of the window (= the card).</summary>
    public Px Rect => Native.WindowRect(_hwnd);

    /// <summary>Moves/resizes the card to a physical rect (DPI-aware two-step placement when crossing monitors).</summary>
    public void Place(Px r) => Native.Place(_hwnd, r);

    public void EnsureHandle()
    {
        Width = DipSize.Width;
        Height = DipSize.Height;
        new WindowInteropHelper(this).EnsureHandle();
    }

    // ================= rendering =================

    /// <summary>Takes a newer stored version (from the desk or another process).</summary>
    public void Update(Card c)
    {
        if (Editing && c.Body != _editBase)
        {
            // Someone else changed the text while we edit: keep ours, and make our save a new undo step
            // whose "before" is their version, so both stay recoverable.
            _session = _desk.Store.Begin();
            _editBase = c.Body;
            ShowBadge("外部已修改 · 你的编辑会覆盖（可撤销）");
        }
        if (Editing) c.Body = Editor.Text.Replace("\r\n", "\n");
        Card = c;
        Render();
    }

    public void Render()
    {
        var sw = Palette.Get(_desk.ColorOf(Card));
        Surface.Background = Palette.Brush(sw.Back);
        Foreground = Palette.Brush(sw.Ink);
        FontSize = _desk.FontSize;
        Editor.Foreground = Foreground;
        Editor.CaretBrush = Foreground;
        Editor.SelectionBrush = Palette.Brush(sw.Accent);
        Editor.FontSize = _desk.FontSize;
        Topmost = _desk.EffTopmost(this);
        MinWidth = Card.ImageOnly && !Pill && Strip == null ? 48 : Card.ImageOnly && Pill ? Thumb : MinW;
        MinHeight = Strip != null || Pill ? (Card.ImageOnly && Strip == null ? Thumb : PillH) : Card.ImageOnly ? 48 : MinH;
        RenderFrame();
        ApplyOpacity();
        RenderContent();
        RenderRow();
    }

    /// <summary>Stroke = state: red when overdue, accent when this card has keyboard focus, a faint line otherwise.</summary>
    public void RenderFrame()
    {
        var sw = Palette.Get(_desk.ColorOf(Card));
        bool overdue = Card.Overdue && Strip == null;
        Surface.BorderBrush = overdue ? Palette.Brush(Palette.Overdue) : IsActive && !Editing ? Palette.Brush(sw.Accent, 0.9) : Palette.Brush(sw.Ink, 0.12);
        Surface.BorderThickness = new Thickness(overdue ? 2 : 1);
    }

    /// <summary>Called once when the reminder comes due: the red frame pulses three times, then stays.</summary>
    public void Pulse()
    {
        Ring.BorderBrush = Palette.Brush(Palette.Overdue);
        Ring.Visibility = Visibility.Visible;
        var a = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(400)) { AutoReverse = true, RepeatBehavior = new RepeatBehavior(3) };
        a.Completed += (_, _) => Ring.Visibility = Visibility.Collapsed;
        Ring.BeginAnimation(OpacityProperty, a);
    }

    MdContext Context(bool interactive)
    {
        var sw = Palette.Get(_desk.ColorOf(Card));
        return new MdContext
        {
            Ink = Foreground,
            Muted = Palette.Brush(sw.Ink, 0.6),
            Accent = Palette.Brush(sw.Accent),
            Soft = Palette.Brush(sw.Ink, 0.07),
            Size = _desk.FontSize,
            Image = src => _desk.LoadImage(src, DecodeWidth),
            ToggleTask = interactive ? line => _desk.ToggleTask(this, line) : null,
            Open = _desk.Open,
        };
    }

    int DecodeWidth => (int)Math.Ceiling(Card.W * Native.WindowScale(_hwnd));

    void RenderContent()
    {
        if (Editing) return;
        var c = Card;
        Stack.Children.Clear();
        Stack.VerticalAlignment = VerticalAlignment.Top;
        Viewport.OpacityMask = null;
        MarkdownView.Round(Viewport, 0);
        _overflow = false;

        if (Strip is { } strip)
        {
            Viewport.Margin = new Thickness(14, 0, 10, 0);
            Stack.VerticalAlignment = VerticalAlignment.Center;
            var row = new DockPanel();
            var undo = Link("撤销", strip.Undo);
            DockPanel.SetDock(undo, Dock.Right);
            row.Children.Add(undo);
            row.Children.Add(new TextBlock { Text = strip.Text, FontSize = _desk.FontSize - 1, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75 });
            Stack.Children.Add(row);
            return;
        }

        if (c.ImageOnly && !Pill)
        {
            Viewport.Margin = new Thickness(0);
            Stack.VerticalAlignment = VerticalAlignment.Stretch;
            var img = new Image { Source = _desk.LoadImage(c.Images[0], DecodeWidth), Stretch = Stretch.Uniform, Height = Math.Max(1, DipSize.Height - 2), Tag = "image:" + c.Images[0] };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            Stack.Children.Add(img);
            MarkdownView.Round(Viewport, 7);
            return;
        }

        if (Pill)
        {
            Viewport.Margin = c.ImageOnly ? new Thickness(0) : new Thickness(12, 0, 6, 0);
            Stack.VerticalAlignment = c.ImageOnly ? VerticalAlignment.Stretch : VerticalAlignment.Center;
            if (c.ImageOnly)
            {
                Stack.Children.Add(new Image { Source = _desk.LoadImage(c.Images[0], DecodeWidth), Stretch = Stretch.UniformToFill, Height = Thumb - 2 });
                MarkdownView.Round(Viewport, 7);
                return;
            }
            var row = new DockPanel();
            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            foreach (var chip in StateChips(compact: true)) right.Children.Add(chip);
            var done = CompleteButton();
            if (done.Tag is not "ready") done.Tag = "hover";
            done.Visibility = _hoverShown || done.Tag is "ready" ? Visibility.Visible : Visibility.Collapsed;
            right.Children.Add(done);
            DockPanel.SetDock(right, Dock.Right);
            row.Children.Add(right);
            // A checklist shows what is next: after its heading, or instead of a title that is itself one of the tasks.
            var title = new TextBlock
            {
                FontSize = _desk.FontSize, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            };
            var next = c.NextTask;
            if (next != null && c.TitleIsTask) title.Text = next;
            else
            {
                title.Inlines.Add(new Run(c.Title.Length > 0 ? c.Title : "（空）"));
                if (next != null) title.Inlines.Add(new Run(" · " + next) { FontWeight = FontWeights.Normal, Foreground = Palette.Brush(Palette.Get(_desk.ColorOf(c)).Ink, 0.6) });
            }
            row.Children.Add(title);
            Stack.Children.Add(row);
            return;
        }

        Viewport.Margin = new Thickness(12, PadTop, 12, 0);
        var cx = Context(interactive: true);
        if (c.Images.Count > 0) Stack.Children.Add(Pictures());
        if (c.Src != null) Stack.Children.Add(Spaced(SourceHeader(cx)));
        var info = c.SrcInfo;

        if (Busy != null) Stack.Children.Add(Spaced(Caption(Busy, cx.Muted, null)));
        else if (Problem != null) Stack.Children.Add(Spaced(Caption("⚠ " + Problem + " · 重试", Palette.Brush(Palette.Overdue), () => _desk.Summarize(this))));
        else if (info?.State == "blocked" && string.IsNullOrWhiteSpace(c.Summary)) Stack.Children.Add(Spaced(Caption("正文不可获取 · 仅标题/描述", cx.Muted, null)));

        if (!string.IsNullOrWhiteSpace(c.Summary))
        {
            var sum = new StackPanel { Tag = "summary" };
            MarkdownView.Render(sum, c.Summary, Context(interactive: false));
            Stack.Children.Add(Spaced(new Border
            {
                BorderBrush = cx.Accent, BorderThickness = new Thickness(2, 0, 0, 0), Padding = new Thickness(9, 0, 0, 0), Child = sum,
                Opacity = c.SummaryStale ? 0.55 : 1, ToolTip = c.SummaryStale ? "内容已改动，摘要可能过时（✨ 重新生成）" : "AI 摘要",
            }));
        }
        else if (info?.Desc is { Length: > 0 } desc && Busy == null)
        {
            // Plain text: a document's opening lines are markdown or code, shown here only as a preview.
            var d = MarkdownView.Text(string.Join(" ", desc.Split('\n').Select(Markdown.Plain).Where(l => l.Length > 0)), cx, _desk.FontSize - 1, fg: cx.Muted);
            d.MaxHeight = d.LineHeight * 3;
            d.TextTrimming = TextTrimming.CharacterEllipsis;
            Stack.Children.Add(Spaced(d));
        }

        var note = c.Note;
        if (!string.IsNullOrWhiteSpace(note))
        {
            var body = new StackPanel();
            // Bounded: a huge paste is stored untouched but only its beginning is laid out.
            // Checked tasks fold away once there are a few of them (display only; the fold line opens them again).
            var noteCx = cx with { ShowDone = _showDone, ToggleDone = () => { _showDone = !_showDone; Render(); Dispatcher.BeginInvoke(() => _desk.Relayout(force: true), DispatcherPriority.Loaded); } };
            MarkdownView.Render(body, note.Length > 12_000 ? note[..12_000] : note, noteCx, c.NoteLine);
            Stack.Children.Add(Spaced(body));
        }
        else if (Stack.Children.Count == 0)
            Stack.Children.Add(new TextBlock { Text = "双击输入…", Foreground = cx.Muted, FontSize = _desk.FontSize });

        Dispatcher.BeginInvoke(() => { MeasureAuto(); CheckOverflow(); }, DispatcherPriority.Loaded);
    }

    FrameworkElement Spaced(FrameworkElement e)
    {
        if (Stack.Children.Count > 0) e.Margin = new Thickness(e.Margin.Left, 8, e.Margin.Right, e.Margin.Bottom);
        return e;
    }

    TextBlock Caption(string text, Brush fg, Action? click)
    {
        var t = new TextBlock { Text = text, Foreground = fg, FontSize = _desk.FontSize - 2, TextWrapping = TextWrapping.Wrap };
        if (click != null) { t.Cursor = Cursors.Hand; t.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); }; }
        return t;
    }

    /// <summary>One image fills the width (≤ half the content height); several form one row of thumbnails with "+N".</summary>
    FrameworkElement Pictures()
    {
        var c = Card;
        double width = Math.Max(40, c.W - 26);
        double room = (c.H ?? _desk.MaxAutoHeight(this)) - RowH - PadTop; // not the measured height: that depends on the picture
        if (c.Images.Count == 1)
            return Picture(0, width, Math.Max(40, Math.Min(room * 0.6, width)), Stretch.Uniform);
        double h = Math.Min(96, Math.Max(40, room / 3));
        int fit = Math.Max(1, (int)((width + 6) / (h + 6)));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        for (int i = 0; i < Math.Min(fit, c.Images.Count); i++)
        {
            var p = Picture(i, h, h, Stretch.UniformToFill);
            p.Margin = new Thickness(0, 0, 6, 0);
            if (i == fit - 1 && c.Images.Count > fit)
            {
                var more = new Grid();
                more.Children.Add(p);
                more.Children.Add(new Border
                {
                    Background = Palette.Brush(Colors.Black, 0.45), CornerRadius = new CornerRadius(6), Margin = p.Margin, IsHitTestVisible = false,
                    Child = new TextBlock { Text = $"+{c.Images.Count - fit}", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                });
                row.Children.Add(more);
            }
            else row.Children.Add(p);
        }
        return row;
    }

    /// <summary>A picture with a hover × (remove, undoable); double-click opens it in the system viewer.</summary>
    FrameworkElement Picture(int index, double w, double h, Stretch stretch)
    {
        var src = Card.Images[index];
        var img = new Image { Source = _desk.LoadImage(src, (int)(w * Native.WindowScale(_hwnd))), Stretch = stretch, MaxWidth = w, MaxHeight = h, HorizontalAlignment = HorizontalAlignment.Left };
        if (stretch == Stretch.UniformToFill) { img.Width = w; img.Height = h; }
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        MarkdownView.Round(img, 6);
        var remove = new Border
        {
            Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = Palette.Brush(Colors.Black, 0.55), Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 4, 0), Visibility = Visibility.Collapsed,
            Child = new TextBlock { Text = "\uE711", FontFamily = MarkdownView.Icons, FontSize = 9, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            ToolTip = "移除图片",
        };
        remove.MouseLeftButtonUp += (_, e) => { e.Handled = true; _desk.RemoveImage(this, index); };
        var host = new Grid { HorizontalAlignment = HorizontalAlignment.Left, Tag = "image:" + src, Background = Brushes.Transparent };
        host.Children.Add(img);
        host.Children.Add(remove);
        host.MouseEnter += (_, _) => remove.Visibility = Visibility.Visible;
        host.MouseLeave += (_, _) => remove.Visibility = Visibility.Collapsed;
        return host;
    }

    /// <summary>Link: caption (site · domain · date) + title. File: glyph, name, facts. Click opens it through the shell.</summary>
    FrameworkElement SourceHeader(MdContext cx)
    {
        var c = Card;
        var info = c.SrcInfo;
        var panel = new StackPanel { Cursor = Cursors.Hand, Background = Brushes.Transparent, ToolTip = c.Src, Tag = "src" };
        panel.MouseLeftButtonUp += (_, e) => { if (!_dragged) { e.Handled = true; _desk.Open(c.Src!); } };
        if (c.IsLink)
        {
            var host = Fetch.Host(c.Src!);
            var caption = info?.Site is { } site && !string.Equals(site, host, StringComparison.OrdinalIgnoreCase) ? $"{site} · {host}" : host;
            if (info?.Date is { } date) caption += " · " + date;
            panel.Children.Add(GlyphLine("\uE71B", caption, cx.Muted, _desk.FontSize - 2));
            panel.Children.Add(new TextBlock
            {
                Text = Breakable(info?.Title ?? c.Src!), FontSize = _desk.FontSize, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
                LineHeight = Math.Round(_desk.FontSize * 1.45), LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                MaxHeight = Math.Round(_desk.FontSize * 1.45) * 2, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0),
            });
        }
        else
        {
            var path = c.Src!;
            var name = Path.GetFileName(path.TrimEnd('\\', '/'));
            var missing = info?.State == "missing";
            var facts = missing ? "找不到文件" : string.Join(" · ", new[]
            {
                info?.FType, info?.Size is long n ? Size(n) : null,
                info?.Mtime is long m ? When.Format(When.FromMs(m), DateTime.Now) + " 修改" : null,
                Path.GetFileName(Path.GetDirectoryName(path.TrimEnd('\\', '/')) ?? ""),
            }.Where(x => !string.IsNullOrEmpty(x)));
            var row = new DockPanel();
            var glyph = new TextBlock { Text = info?.FType == "文件夹" ? "\uE8B7" : "\uE8A5", FontFamily = MarkdownView.Icons, FontSize = 22, Foreground = cx.Accent, Margin = new Thickness(0, 2, 10, 0), VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(glyph, Dock.Left);
            row.Children.Add(glyph);
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, FontSize = _desk.FontSize, TextWrapping = TextWrapping.Wrap });
            text.Children.Add(new TextBlock { Text = facts, FontSize = _desk.FontSize - 2, Foreground = missing ? Palette.Brush(Palette.Overdue) : cx.Muted, TextTrimming = TextTrimming.CharacterEllipsis });
            row.Children.Add(text);
            panel.Children.Add(row);
        }
        return panel;
    }

    static TextBlock GlyphLine(string glyph, string text, Brush fg, double size)
    {
        var t = new TextBlock { FontSize = size, Foreground = fg, TextTrimming = TextTrimming.CharacterEllipsis };
        t.Inlines.Add(new Run(glyph) { FontFamily = MarkdownView.Icons, BaselineAlignment = BaselineAlignment.Center });
        t.Inlines.Add(new Run("  " + text));
        return t;
    }

    /// <summary>Lets long URLs wrap at sensible points.</summary>
    static string Breakable(string s) => Card.IsUrl(s) ? System.Text.RegularExpressions.Regex.Replace(s, @"([/?&=\-_.])", "$1​") : s;

    static string Size(long n) => n switch { < 1024 => $"{n} B", < 1 << 20 => $"{n / 1024.0:0.#} KB", _ => $"{n / 1048576.0:0.#} MB" };

    // ================= the action row =================

    /// <summary>State that is set, one chip each (a chip is also the button for that state).</summary>
    List<FrameworkElement> StateChips(bool compact)
    {
        var c = Card;
        var list = new List<FrameworkElement>();
        var sw = Palette.Get(_desk.ColorOf(c));
        if (c.Topmost && !compact)
        {
            var pin = Chip("\uE840", c.Opacity < 0.99 ? $"{c.Opacity:P0}" : null, "取消置顶 · 在此滚动调透明度", () => _desk.TogglePin(this));
            pin.PreviewMouseWheel += (_, e) => { e.Handled = true; AdjustOpacity(e.Delta); };
            list.Add(pin);
        }
        if (c.RemindAt is long t)
        {
            Button? chip = null;
            chip = Chip("\uE823", When.Format(When.FromMs(t), DateTime.Now), c.Overdue ? "已到提醒时间 · 点此改期" : "修改提醒",
                () => Flyout.Reminder(_desk, this, chip!), c.Overdue ? Palette.Brush(Palette.Overdue) : null);
            list.Add(chip);
        }
        if (c.Progress is { Total: > 0 } p)
        {
            if (compact || Ref < 0) list.Add(Chip("\uE73A", $"{p.Done}/{p.Total}", "任务进度", null, p.Done == p.Total ? Palette.Brush(sw.Accent) : null));
            else
            {
                // The progress chip sits right under the list: it is also where a new item is added (a "+" shows on hover).
                var chip = Chip("\uE73A", $"{p.Done}/{p.Total}", "任务进度 · 点此添加一项", StartAdd, p.Done == p.Total ? Palette.Brush(sw.Accent) : null);
                _addChip = (TextBlock)chip.Content;
                list.Add(chip);
            }
        }
        if (!compact && _desk.Llm != null && Busy == null)
        {
            bool none = string.IsNullOrWhiteSpace(c.Summary);
            bool wantsSummary = c.SummaryStale || none && (_overflow || c.Src != null && !_desk.AutoSummarizes(c));
            if (wantsSummary) list.Add(Chip(null, "✨ 摘要", c.SummaryStale ? "内容已改动：重新生成摘要" : "用 CLI LLM 生成摘要", () => _desk.Summarize(this)));
        }
        return list;
    }

    void RenderRow()
    {
        Chips.Children.Clear();
        Actions.Children.Clear();
        _addChip = null;
        bool show = !Editing && !Pill && Strip == null;
        Row.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        var c = Card;
        Chips.Visibility = Actions.Visibility = Adding ? Visibility.Collapsed : Visibility.Visible;
        if (Adding)
        {
            if (_adderHost!.Parent == null) Row.Children.Add(_adderHost);
            return;
        }
        if (c.ImageOnly)
        {
            // Pictures stay full-bleed: the row floats over the image bottom on a scrim, on hover only.
            Grid.SetRow(Row, 0);
            Row.VerticalAlignment = VerticalAlignment.Bottom;
            Row.Margin = new Thickness(0);
            Row.Background = new LinearGradientBrush(Colors.Transparent, Color.FromArgb(0x90, 0, 0, 0), 90);
            TextElement.SetForeground(Row, Brushes.White);
        }
        else
        {
            Grid.SetRow(Row, 1);
            Row.VerticalAlignment = VerticalAlignment.Stretch;
            Row.Margin = new Thickness(5, 0, 5, 3);
            Row.Background = null;
            Row.ClearValue(TextElement.ForegroundProperty);
        }
        foreach (var chip in StateChips(compact: false)) Chips.Children.Add(chip);

        if (c.Overdue)
        {
            // Overdue: snooze is one click away, no hover needed.
            Actions.Children.Add(Chip(null, "10分钟", "10 分钟后再提醒", () => _desk.SetReminder(this, DateTime.Now.AddMinutes(10))));
            Actions.Children.Add(Chip(null, "1小时", "1 小时后再提醒", () => _desk.SetReminder(this, DateTime.Now.AddHours(1))));
            Actions.Children.Add(Action(CompleteGlyph, "完成", () => _desk.Complete(this)));
        }
        else
        {
            if (c.RemindAt == null) Actions.Children.Add(Action("\uE823", "提醒", () => Flyout.Reminder(_desk, this, Actions)));
            if (!c.Topmost) Actions.Children.Add(Action("\uE718", "置顶", () => _desk.TogglePin(this)));
            if (c.W >= 200)
            {
                Actions.Children.Add(Action("\uE790", "颜色", () => Flyout.Colors(_desk, this, Actions)));
                Actions.Children.Add(Action("\uE70E", "折叠", () => _desk.ToggleCollapse(this)));
            }
            else Actions.Children.Add(Action("\uE712", "更多", OpenMenu));
            Actions.Children.Add(CompleteButton());
        }
        ShowActions(animate: false);
    }

    /// <summary>○ completes the card. When every task is checked it is highlighted and stays visible without hover.</summary>
    Button CompleteButton()
    {
        var b = Action(CompleteGlyph, "完成", () => _desk.Complete(this));
        if (Card.Progress is { Total: > 0 } p && p.Done == p.Total && !Card.Overdue)
        {
            b.Tag = "ready";
            b.Foreground = Palette.Brush(Palette.Get(_desk.ColorOf(Card)).Accent);
            b.ToolTip = "清单已全部勾完 · 点此完成这张卡片";
        }
        return b;
    }

    /// <summary>Hover actions fade in and out; a highlighted ○ ("ready") stays. Rebuilt rows take their state at once.</summary>
    void ShowActions(bool animate = true)
    {
        bool show = _hoverShown || Card.Overdue;
        foreach (var a in Actions.Children.OfType<FrameworkElement>())
        {
            bool on = show || a.Tag is "ready";
            if (animate) a.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 1 : 0, TimeSpan.FromMilliseconds(on ? 120 : 200)));
            else { a.BeginAnimation(OpacityProperty, null); a.Opacity = on ? 1 : 0; }
            a.IsHitTestVisible = on;
        }
        // Added and removed, not emptied: an empty icon-font run still draws missing-glyph boxes.
        if (_addChip is { } tb && (tb.Inlines.LastInline is Run { Tag: "add" }) != _hoverShown)
        {
            if (_hoverShown) tb.Inlines.Add(new Run(" \uE710") { Tag = "add", FontFamily = MarkdownView.Icons, FontSize = _desk.FontSize - 4, BaselineAlignment = BaselineAlignment.Center });
            else tb.Inlines.Remove(tb.Inlines.LastInline);
        }
        if (Card.ImageOnly && Strip == null && !Pill && !Editing) Row.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        foreach (var e in Stack.Children.OfType<DockPanel>().SelectMany(d => d.Children.OfType<StackPanel>()).SelectMany(p => p.Children.OfType<FrameworkElement>()))
            if (e.Tag is "hover") e.Visibility = _hoverShown ? Visibility.Visible : Visibility.Collapsed;
    }

    Button Chip(string? glyph, string? text, string? tip, Action? click, Brush? fg = null)
    {
        var tb = new TextBlock { FontSize = _desk.FontSize - 2, VerticalAlignment = VerticalAlignment.Center };
        if (glyph != null) tb.Inlines.Add(new Run(glyph) { FontFamily = MarkdownView.Icons, BaselineAlignment = BaselineAlignment.Center });
        if (text != null) tb.Inlines.Add(new Run((glyph != null ? " " : "") + text));
        var b = new Button { Content = tb, ToolTip = tip };
        b.SetResourceReference(StyleProperty, "Card.Chip");
        if (fg != null) b.Foreground = fg;
        if (click != null) b.Click += (_, _) => click();
        else b.Cursor = Cursors.Arrow;
        return b;
    }

    Button Action(string glyph, string tip, Action click)
    {
        var b = new Button { Content = glyph, ToolTip = tip };
        b.SetResourceReference(StyleProperty, "Card.Action");
        b.Click += (_, _) => click();
        if (glyph == CompleteGlyph)
        {
            b.MouseEnter += (_, _) => b.Content = "\uE930";
            b.MouseLeave += (_, _) => b.Content = CompleteGlyph;
        }
        return b;
    }

    TextBlock Link(string text, Action click)
    {
        var t = new TextBlock
        {
            Text = text, Cursor = Cursors.Hand, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 4, 0),
            Foreground = Palette.Brush(Palette.Get(_desk.ColorOf(Card)).Accent), FontSize = _desk.FontSize - 1,
        };
        t.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        return t;
    }

    // ================= size, overflow, opacity =================

    /// <summary>Fit-content height: measured from the rendered (or edited) content; grows only while editing.</summary>
    void MeasureAuto()
    {
        if (Card.H != null || Card.ImageOnly || Pill || Strip != null) return;
        double contentW = Math.Max(10, Card.W - 26);
        double h;
        if (Editing) { Editor.Measure(new Size(contentW, double.PositiveInfinity)); h = Editor.DesiredSize.Height; }
        else { Stack.Measure(new Size(contentW, double.PositiveInfinity)); h = Stack.DesiredSize.Height + RowH; }
        var nh = Math.Clamp(Math.Ceiling(PadTop + h + 3), MinH, _desk.MaxAutoHeight(this)); // +3: rounding at 125%/150% must not clip the last line
        if (Editing) nh = Math.Max(nh, AutoH);
        if (Math.Abs(nh - AutoH) < 1) return;
        AutoH = nh;
        _desk.Resized(this);
    }

    /// <summary>No scrollbars: taller content fades out at the bottom; ✨ offers a summary.</summary>
    void CheckOverflow()
    {
        if (Editing || Pill || Card.ImageOnly || Strip != null || Viewport.ActualHeight <= 0) return;
        Stack.Measure(new Size(Math.Max(0, Viewport.ActualWidth), double.PositiveInfinity));
        bool over = Stack.DesiredSize.Height > Viewport.ActualHeight + 2;
        if (over)
        {
            double fade = Math.Min(36, Viewport.ActualHeight / 3) / Viewport.ActualHeight;
            Viewport.OpacityMask = new LinearGradientBrush(new GradientStopCollection { new(Colors.Black, 0), new(Colors.Black, 1 - fade), new(Colors.Transparent, 1) }, 90);
        }
        else Viewport.OpacityMask = null;
        if (over != _overflow)
        {
            _overflow = over;
            RenderRow();
        }
    }

    /// <summary>Opacity applies only while topmost; hover (after a beat), editing and alarms show the card fully.</summary>
    public void ApplyOpacity()
    {
        bool full = !Topmost || Editing || Card.Overdue || (_hoverShown && DateTime.Now > _peekUntil);
        BeginAnimation(OpacityProperty, new DoubleAnimation(full ? 1 : Math.Clamp(Card.Opacity, 0.2, 1), TimeSpan.FromMilliseconds(140)));
    }

    public void AdjustOpacity(int delta)
    {
        if (!Card.Topmost) { ShowBadge("置顶后可调透明度"); return; }
        var v = Math.Round(Math.Clamp(Card.Opacity + (delta > 0 ? 0.1 : -0.1), 0.2, 1), 1);
        _peekUntil = DateTime.Now.AddSeconds(1.2);
        ShowBadge($"透明度 {v:P0}");
        _desk.SetOpacity(this, v);
    }

    public void ShowBadge(string text)
    {
        BadgeText.Text = text;
        Badge.Visibility = Visibility.Visible;
        _badgeTimer.Stop();
        _badgeTimer.Start();
        ApplyOpacity();
    }

    // ================= finishing (in-place undo) =================

    /// <summary>Complete/delete: the card shrinks in place to "已完成 · 撤销" and keeps its slot until 4 s after the mouse leaves.</summary>
    public void BeginStrip(string text, Action undo, Action expire)
    {
        StripRect = Rect;
        Strip = (text, () => { CancelStrip(); undo(); }, () => { CancelStrip(); expire(); });
        _stripTimer.Start();
        Render();
    }

    /// <summary>Ends the strip without running its actions (the card came back some other way).</summary>
    public void CancelStrip()
    {
        _stripTimer.Stop();
        Strip = null;
    }

    // ================= editing =================

    public void BeginEdit(int? line = null, int? column = null)
    {
        if (Editing || Strip != null) return;
        EndAdd();
        if (Card.Collapsed) _desk.ToggleCollapse(this);
        LayoutPill = false;
        Editing = true;
        _session = _desk.Store.Begin();
        _editBase = Card.Body;
        Editor.Text = Card.Body;
        Stack.Visibility = Visibility.Collapsed;
        Editor.Visibility = Visibility.Visible;
        Viewport.OpacityMask = null;
        MarkdownView.Round(Viewport, 0);
        Viewport.Margin = new Thickness(12, PadTop, 12, 0);
        Render();
        Activate();
        Editor.Focus();
        int caret = Editor.Text.Length;
        if (line is int l && l >= 0)
        {
            var lines = Editor.Text.Split('\n');
            if (l < lines.Length)
            {
                int start = lines.Take(l).Sum(x => x.Length + 1);
                caret = start + Math.Clamp(column ?? lines[l].Length, 0, lines[l].Length);
            }
        }
        Editor.CaretIndex = caret;
        MeasureAuto();
    }

    public void EndEdit(bool returnFocus)
    {
        if (!Editing) return;
        Editing = false;
        _autosave.Stop();
        var text = Editor.Text.Replace("\r\n", "\n").TrimEnd();
        Editor.Visibility = Visibility.Collapsed;
        Stack.Visibility = Visibility.Visible;
        var session = _session!;
        _session = null;
        try { _desk.CommitEdit(this, text, session); }
        catch (Exception e)
        {
            // Not saved (database busy, disk full…): stay in edit mode so the text is not lost; autosave retries.
            Editing = true;
            _session = session;
            Stack.Visibility = Visibility.Collapsed;
            Editor.Visibility = Visibility.Visible;
            ShowBadge("保存失败，稍后自动重试：" + e.Message);
            _autosave.Start();
            return;
        }
        if (returnFocus && ReturnFocus != IntPtr.Zero && Native.IsWindow(ReturnFocus)) Native.SetForegroundWindow(ReturnFocus);
        ReturnFocus = IntPtr.Zero;
    }

    void Autosave()
    {
        if (!Editing || _session == null) return;
        try { _desk.SaveBody(this, Editor.Text.Replace("\r\n", "\n"), _session); }
        catch (Exception e) { ShowBadge("保存失败，稍后自动重试：" + e.Message); _autosave.Start(); }
    }

    /// <summary>The desk stored our text: remember it (external-change detection) and take the stored row.</summary>
    public void Saved(Card c)
    {
        _editBase = c.Body;
        if (Editing) c.Body = Editor.Text.Replace("\r\n", "\n");
        Card = c;
    }

    void OnEditorKey(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        if (e.Key == Key.Escape) { e.Handled = true; EndEdit(returnFocus: true); return; }
        if (e.Key == Key.Enter && mods == ModifierKeys.None && ContinueList()) { e.Handled = true; return; }
        // WPF's TextBox pastes text only: with just a picture (a screenshot) or copied files, Ctrl+V would do nothing.
        if ((e.Key == Key.V && mods == ModifierKeys.Control || e.Key == Key.Insert && mods == ModifierKeys.Shift)
            && ClipData.Current() is { } clip && ClipData.Text(clip) == null && PasteData(clip)) { e.Handled = true; return; }
        if (e.Key == Key.Tab && mods is ModifierKeys.None or ModifierKeys.Shift) { e.Handled = true; Indent(mods == ModifierKeys.Shift); return; }
        if (mods == ModifierKeys.Control)
        {
            string? wrap = e.Key switch { Key.B => "**", Key.I => "*", Key.T => "~~", Key.E => "`", _ => null };
            if (wrap != null) { e.Handled = true; Wrap(wrap); return; }
            if (e.Key == Key.L) { e.Handled = true; TogglePrefix("- [ ] "); }
        }
        if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.L) { e.Handled = true; TogglePrefix("- "); }
    }

    (int Start, string Text) CurrentLine()
    {
        var t = Editor.Text;
        int caret = Editor.CaretIndex;
        int start = caret == 0 ? 0 : t.LastIndexOf('\n', caret - 1) + 1;
        int end = t.IndexOf('\n', caret);
        return (start, t[start..(end < 0 ? t.Length : end)]);
    }

    /// <summary>Enter inside a list continues it ("- [ ] ", "- ", "2. "); Enter on an empty item ends the list.</summary>
    bool ContinueList()
    {
        var (start, line) = CurrentLine();
        if (Editor.SelectionLength > 0 || Editor.CaretIndex < start + line.Length) return false;
        var next = Markdown.ContinueList(line);
        if (next == null) return false;
        if (next.Length == 0)
        {
            Editor.Select(start, line.Length);
            Editor.SelectedText = "";
            Editor.CaretIndex = start;
            return true;
        }
        Insert("\n" + next);
        return true;
    }

    void Indent(bool outdent)
    {
        var (start, line) = CurrentLine();
        int caret = Editor.CaretIndex;
        if (Markdown.ContinueList(line) == null) { if (!outdent) Insert("  "); return; }
        if (outdent)
        {
            int n = line.StartsWith("  ") ? 2 : line.StartsWith(' ') ? 1 : 0;
            if (n == 0) return;
            Editor.Select(start, n);
            Editor.SelectedText = "";
            Editor.CaretIndex = Math.Max(start, caret - n);
        }
        else
        {
            Editor.Select(start, 0);
            Editor.SelectedText = "  ";
            Editor.CaretIndex = caret + 2;
        }
    }

    void Wrap(string mark)
    {
        int s = Editor.SelectionStart, n = Editor.SelectionLength;
        Editor.SelectedText = mark + Editor.SelectedText + mark;
        if (n == 0) Editor.CaretIndex = s + mark.Length;
        else Editor.Select(s, n + 2 * mark.Length);
    }

    /// <summary>Ctrl+L: "- [ ] " ⇄ plain; Ctrl+Shift+L: "- " ⇄ plain (also switches between the two list kinds).</summary>
    void TogglePrefix(string prefix)
    {
        var (start, line) = CurrentLine();
        int caret = Editor.CaretIndex;
        var trimmed = line.TrimStart();
        int indent = line.Length - trimmed.Length;
        var task = System.Text.RegularExpressions.Regex.Match(trimmed, @"^[-*+]\s+\[[ xX]\]\s?");
        var bullet = System.Text.RegularExpressions.Regex.Match(trimmed, @"^[-*+]\s+");
        string rest = task.Success ? trimmed[task.Length..] : bullet.Success ? trimmed[bullet.Length..] : trimmed;
        bool had = prefix.Contains('[') ? task.Success : bullet.Success && !task.Success;
        string replacement = had ? rest : prefix + rest;
        Editor.Select(start + indent, trimmed.Length);
        Editor.SelectedText = replacement;
        Editor.CaretIndex = Math.Clamp(caret + replacement.Length - trimmed.Length, start, Editor.Text.Length);
    }

    /// <summary>"[] " or "[ ] " typed at the start of a line becomes a task item.</summary>
    void OnEditorText(object sender, TextCompositionEventArgs e)
    {
        if (e.Text != " ") return;
        var (start, line) = CurrentLine();
        var before = line[..Math.Clamp(Editor.CaretIndex - start, 0, line.Length)];
        var trimmed = before.TrimStart();
        if (!Markdown.IsTaskShortcut(trimmed)) return;
        int indent = before.Length - trimmed.Length;
        Editor.Select(start + indent, trimmed.Length);
        Editor.SelectedText = "- [ ] ";
        Editor.CaretIndex = start + indent + 6;
        e.Handled = true;
    }

    void Insert(string text)
    {
        int at = Editor.SelectionStart;
        Editor.SelectedText = text;
        Editor.CaretIndex = at + text.Length;
    }

    /// <summary>Images and files pasted while editing: images join the card's pictures, files become path lines.</summary>
    void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (PasteData(e.DataObject)) e.CancelCommand(); // otherwise plain text: the TextBox pastes it
    }

    /// <summary>Takes pictures and files from <paramref name="data"/>; false when it is plain text for the TextBox.</summary>
    bool PasteData(IDataObject data)
    {
        var lines = _desk.IngestImages(this, data, textPreferred: true);
        if (lines == null) return false;
        if (lines.Length > 0) Insert(lines);
        else if (Ref < 0 && Editor.Text.Trim().Length == 0) Dispatcher.BeginInvoke(() => EndEdit(returnFocus: false)); // a new card that is just a picture floats at its size
        else ShowBadge("已添加图片");
        return true;
    }

    // ================= quick add (checklists) =================

    /// <summary>
    /// Adds checklist items without edit mode: the action row becomes a one-line input. Enter adds "- [ ] …" after the last
    /// task and stays open for the next one; Esc, Enter on an empty line, or leaving the card closes it (typed text is
    /// kept, as leaving edit mode keeps it). The whole session is one undo step.
    /// </summary>
    public void StartAdd()
    {
        if (Adding || Editing || Strip != null || Ref < 0) return;
        if (Card.Collapsed) _desk.ToggleCollapse(this);
        LayoutPill = false;
        var sw = Palette.Get(_desk.ColorOf(Card));
        var box = new TextBox
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), FontSize = _desk.FontSize,
            Foreground = Foreground, CaretBrush = Foreground, SelectionBrush = Palette.Brush(sw.Accent), VerticalAlignment = VerticalAlignment.Center,
            AcceptsReturn = false, TextWrapping = TextWrapping.NoWrap, SpellCheck = { IsEnabled = false },
        };
        box.SetResourceReference(FontFamilyProperty, "Ui.Font");
        var hint = new TextBlock
        {
            Text = "添加一项 · Enter 确认，Esc 结束", FontSize = _desk.FontSize - 1, Foreground = Palette.Brush(sw.Ink, 0.45),
            VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        box.TextChanged += (_, _) => hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; EndAdd(); }
            else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
            {
                e.Handled = true;
                if (box.Text.Trim().Length == 0) { EndAdd(); return; }
                var text = box.Text;
                box.Text = "";
                _desk.AddTasks(this, text, _addSession!);
            }
        };
        // A pasted list becomes one item per line (a one-line TextBox would keep only its first line).
        DataObject.AddPastingHandler(box, (_, e) =>
        {
            if (!e.SourceDataObject.GetDataPresent(DataFormats.UnicodeText) || e.SourceDataObject.GetData(DataFormats.UnicodeText) is not string t || !t.Contains('\n')) return;
            e.CancelCommand();
            _desk.AddTasks(this, box.Text + t, _addSession!);
            box.Text = "";
        });
        var input = new Grid();
        input.Children.Add(hint);
        input.Children.Add(box);
        var line = new DockPanel();
        var mark = new Border
        {
            Width = 14, Height = 14, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1.25), BorderBrush = Palette.Brush(sw.Ink, 0.45),
            Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(mark, Dock.Left);
        line.Children.Add(mark);
        line.Children.Add(input);
        _adderHost = new Border { Child = line, CornerRadius = new CornerRadius(6), Background = Palette.Brush(sw.Ink, 0.06), Padding = new Thickness(7, 0, 7, 0), Margin = new Thickness(2, 1, 2, 1) };
        _adder = box;
        _addSession = _desk.Store.Begin();
        Render();
        Activate();
        Dispatcher.BeginInvoke(() => _adder?.Focus(), DispatcherPriority.Input);
    }

    /// <summary>Closes quick add, keeping what was typed. Safe to call when it is not open.</summary>
    public void EndAdd()
    {
        if (_adder is not { } box) return;
        var text = box.Text;
        var g = _addSession!;
        Row.Children.Remove(_adderHost);
        _adder = null;
        _adderHost = null;
        _addSession = null;
        if (text.Trim().Length > 0) _desk.AddTasks(this, text, g); // re-renders the row
        else RenderRow();
        _desk.Relayout(); // no longer held in place: back into the tiling
    }

    // ================= mouse & keyboard =================

    void OnDown(object sender, MouseButtonEventArgs e)
    {
        _dragged = false;
        if (Strip != null) return;
        if (e.ClickCount == 2 && !Editing)
        {
            if (Tagged(e.OriginalSource, "image:") is string img) { e.Handled = true; _desk.Open(img["image:".Length..]); return; }
            if (Tagged(e.OriginalSource, "src") != null) { e.Handled = true; return; } // the header opens on click; a double click is not an edit
            if (!IsInteractive(e.OriginalSource) && !Pill && !Card.ImageOnly)
            {
                e.Handled = true;
                var (line, column) = HitLine(e);
                BeginEdit(line, column);
                return;
            }
        }
        if (IsInteractive(e.OriginalSource)) return;
        if (Editing && IsWithin(e.OriginalSource, Editor)) return;
        if (_adderHost != null && IsWithin(e.OriginalSource, _adderHost)) return;
        if (Card.ImageOnly && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            _desk.DragOut(this);
            return;
        }
        _down = e.GetPosition(this);
        _armed = true;
    }

    void OnMove(object sender, MouseEventArgs e)
    {
        if (!_armed || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(this) - _down;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _armed = false;
        _dragged = true;
        try { DragMove(); } catch (InvalidOperationException) { }
    }

    void OnUp(object sender, MouseButtonEventArgs e)
    {
        bool click = _armed && !_dragged;
        _armed = false;
        if (click && Pill) _desk.ExpandPill(this);
    }

    bool IsInteractive(object src)
    {
        for (var d = src as DependencyObject; d != null && d != this; d = Up(d))
        {
            if (d is ButtonBase or Hyperlink) return true;
            if (d is FrameworkElement { Tag: not "src" } fe && fe.Cursor == Cursors.Hand) return true;
        }
        return false;
    }

    static DependencyObject? Up(DependencyObject d) =>
        d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

    static bool IsWithin(object src, DependencyObject target)
    {
        for (var d = src as DependencyObject; d != null; d = Up(d)) if (d == target) return true;
        return false;
    }

    static string? Tagged(object src, string prefix)
    {
        for (var d = src as DependencyObject; d != null; d = Up(d))
            if (d is FrameworkElement { Tag: string s } && s.StartsWith(prefix)) return s;
        return null;
    }

    /// <summary>Source line (and, for plain one-run text, the column) under the mouse, so the caret lands where you clicked.</summary>
    (int? Line, int? Column) HitLine(MouseEventArgs e)
    {
        if (Tagged(e.OriginalSource, "summary") != null) return (null, null); // summary blocks carry their own line numbers
        for (var d = e.OriginalSource as DependencyObject; d != null; d = Up(d))
        {
            if (d is FrameworkElement { Tag: int line } fe)
            {
                int? column = null;
                var lines = Card.Body.Split('\n');
                if (fe is TextBlock tb && tb.Inlines.Count == 1 && line < lines.Length && tb.GetPositionFromPoint(e.GetPosition(tb), true) is { } pos)
                {
                    var raw = lines[line];
                    var plain = raw.TrimStart();
                    if (plain == tb.Text) column = raw.Length - plain.Length + Math.Max(0, tb.ContentStart.GetOffsetToPosition(pos) - 1);
                }
                return (line, column);
            }
        }
        return (null, null);
    }

    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; AdjustOpacity(e.Delta); }
        else if (Card.ImageOnly && !Pill && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; _desk.Zoom(this, e.Delta > 0 ? 1.1 : 1 / 1.1); }
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Strip is { } strip && key == Key.Z && mods == ModifierKeys.Control) { e.Handled = true; strip.Undo(); return; }
        if (Editing || Adding || Strip != null) return;
        bool handled = true;
        switch (key)
        {
            case Key.Enter when mods == ModifierKeys.None: BeginEdit(); break;
            case Key.Delete when mods == ModifierKeys.None: _desk.Delete(this); break;
            case Key.D when mods == ModifierKeys.Control: _desk.Delete(this); break;
            case Key.Z when mods == ModifierKeys.Control: _desk.Undo(); break;
            case Key.C when mods == ModifierKeys.Control: _desk.Copy(this); break;
            case Key.V when mods == ModifierKeys.Control: if (ClipData.Current() is { } data) _desk.Ingest(this, data); break;
            case Key.N when mods == ModifierKeys.Control: _desk.NewCard(_desk.ScreenOf(this)); break;
            case Key.F when mods == ModifierKeys.Control: _desk.OpenManage(); break;
            case Key.Tab when mods.HasFlag(ModifierKeys.Control): _desk.Cycle(this, mods.HasFlag(ModifierKeys.Shift) ? -1 : 1); break;
            default: handled = false; break;
        }
        e.Handled = handled;
    }

    public void OpenMenu()
    {
        var menu = _desk.CardMenu(this);
        menu.PlacementTarget = this;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    // ================= native: resize band, aspect lock, activation =================

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Native.WM_NCHITTEST:
                int hit = HitTest(lParam);
                if (hit != Native.HTCLIENT) { handled = true; return new IntPtr(hit); }
                break;
            case Native.WM_NCLBUTTONDBLCLK when wParam.ToInt32() == Native.HTBOTTOM && !Card.ImageOnly:
                handled = true; // no vertical maximize: double-clicking the bottom edge returns to fit-content height
                _desk.AutoHeight(this);
                break;
            case Native.WM_SYSCOMMAND when (wParam.ToInt32() & 0xFFF0) is Native.SC_MAXIMIZE or Native.SC_MINIMIZE:
                handled = true;
                break;
            case Native.WM_ACTIVATE when (wParam.ToInt32() & 0xFFFF) == Native.WA_INACTIVE:
                _keepEditing = Native.IsOwnedBy(lParam, _hwnd); // our own flyout took focus: stay in edit mode
                break;
            case Native.WM_ENTERSIZEMOVE:
                _inSizeMove = true;
                _sizeMoveStart = Rect;
                _sizeMoveScale = Native.WindowScale(_hwnd);
                break;
            case Native.WM_SIZING when Card.ImageOnly && !Pill:
                KeepAspect(wParam.ToInt32(), lParam);
                break;
            case Native.WM_EXITSIZEMOVE:
                _inSizeMove = false;
                if (Rect != _sizeMoveStart) _desk.OnUserMoved(this, _sizeMoveStart, _sizeMoveScale);
                break;
            case Native.WM_DPICHANGED when !_inSizeMove:
                Dispatcher.BeginInvoke(() => _desk.Relayout(), DispatcherPriority.Loaded); // the monitor's scale changed under us
                break;
        }
        return IntPtr.Zero;
    }

    /// <summary>Resize band: the card's inner 6 DIP (12 at corners). Pills and strips only resize horizontally.</summary>
    int HitTest(IntPtr lParam)
    {
        long lp = lParam.ToInt64();
        int x = (short)(lp & 0xFFFF), y = (short)((lp >> 16) & 0xFFFF);
        double s = Native.WindowScale(_hwnd);
        var r = Rect;
        if (!r.Contains(x, y)) return Native.HTCLIENT;
        int band = (int)Math.Round(6 * s), corner = (int)Math.Round(12 * s);
        bool l = x < r.X + band, rt = x >= r.Right - band, t = y < r.Y + band, b = y >= r.Bottom - band;
        if (Strip != null || (Pill && Card.ImageOnly)) return Native.HTCLIENT;
        if (Pill) return l ? Native.HTLEFT : rt ? Native.HTRIGHT : Native.HTCLIENT;
        bool lc = x < r.X + corner, rc = x >= r.Right - corner, tc = y < r.Y + corner, bc = y >= r.Bottom - corner;
        if ((t && lc) || (l && tc)) return Native.HTTOPLEFT;
        if ((t && rc) || (rt && tc)) return Native.HTTOPRIGHT;
        if ((b && lc) || (l && bc)) return Native.HTBOTTOMLEFT;
        if ((b && rc) || (rt && bc)) return Native.HTBOTTOMRIGHT;
        if (l) return Native.HTLEFT;
        if (rt) return Native.HTRIGHT;
        if (t) return Native.HTTOP;
        if (b) return Native.HTBOTTOM;
        return Native.HTCLIENT;
    }

    void KeepAspect(int edge, IntPtr lParam)
    {
        if (_desk.ImageAspect(Card.Images[0]) is not double ratio || ratio <= 0) return;
        var r = Marshal.PtrToStructure<Native.RECT>(lParam);
        double w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (edge is Native.WMSZ_TOP or Native.WMSZ_BOTTOM) r.Right = r.Left + (int)Math.Round(h * ratio);
        else
        {
            int nh = (int)Math.Round(w / ratio);
            if (edge is Native.WMSZ_TOPLEFT or Native.WMSZ_TOPRIGHT) r.Top = r.Bottom - nh;
            else r.Bottom = r.Top + nh;
        }
        Marshal.StructureToPtr(r, lParam, false);
    }
}
