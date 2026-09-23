using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using PinCC.Core;

namespace PinCC.UI;

/// <summary>
/// A small owned window anchored to a card button (reminder picker, colors). A real window rather than a WPF Popup
/// so text boxes get IME input; it stays above its owner card, closes on Esc or when it loses activation.
/// </summary>
public sealed class Flyout : Window
{
    readonly FrameworkElement _anchor;
    bool _closing;

    Flyout(Window owner, FrameworkElement anchor, UIElement content)
    {
        _anchor = anchor;
        Owner = owner;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Topmost = true;
        Opacity = 0; // shown once positioned, so it never flashes at 0,0
        UseLayoutRounding = true;
        Content = new Border { Style = (Style)FindResource("Ui.Surface"), Child = content };
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            Native.AddExStyle(h, Native.WS_EX_TOOLWINDOW);
            HwndSource.FromHwnd(h).AddHook(Native.NoActivateOnDpiChange());
        };
        Loaded += (_, _) => { Position(); Opacity = 1; };
        Closing += (_, _) => _closing = true;
        // Closing moves the activation back to the owner, which deactivates this window once more: Dismiss ignores that.
        Deactivated += (_, _) => Dismiss();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Dismiss(); } };
    }

    /// <summary>Closes the flyout once; later requests (a click that picked a value, then the deactivation) do nothing.</summary>
    public void Dismiss()
    {
        if (!IsLoaded || _closing) return;
        _closing = true;
        Close();
    }

    public static Flyout Open(Window owner, FrameworkElement anchor, UIElement content)
    {
        var f = new Flyout(owner, anchor, content);
        f.Show();
        f.Activate();
        return f;
    }

    /// <summary>Below the anchor (right-aligned), flipped above when the work area is too short; physical px throughout.</summary>
    void Position()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var a1 = _anchor.PointToScreen(new Point(0, 0));
        var a2 = _anchor.PointToScreen(new Point(_anchor.ActualWidth, _anchor.ActualHeight));
        var anchor = new Px((int)a1.X, (int)a1.Y, (int)(a2.X - a1.X), (int)(a2.Y - a1.Y));
        var screen = Screens.Of(anchor);
        var size = Native.WindowRect(hwnd);
        double k = screen.Scale / Native.WindowScale(hwnd);
        int w = (int)(size.W * k), h = (int)(size.H * k);
        int x = anchor.Right - w + screen.Px(10);
        int y = anchor.Bottom - screen.Px(4);
        if (y + h > screen.Work.Bottom) y = anchor.Y - h + screen.Px(4);
        var r = new Px(x, y, w, h).ClampInto(screen.Work);
        Native.Place(hwnd, r);
    }

    // ================= reminder picker =================

    public static void Reminder(Desk desk, CardWindow card, FrameworkElement anchor)
    {
        Flyout? self = null;
        var root = ReminderPanel(desk, card, () => self?.Dismiss(), out var input);
        self = Open(card, anchor, root);
        input.Focus();
    }

    /// <summary>The picker's content (also rendered off-screen by the snapshot diagnostic).</summary>
    public static StackPanel ReminderPanel(Desk desk, CardWindow card, Action close, out TextBox input)
    {
        var now = DateTime.Now;
        var root = new StackPanel { Width = 300 };
        root.Children.Add(Caption(card.Card.Overdue ? "稍后再提醒" : "提醒我"));
        void Set(DateTime? at) { desk.SetReminder(card, at); close(); }

        var chips = new WrapPanel();
        var options = card.Card.Overdue
            ? new List<(string, DateTime)> { ("10 分钟", now.AddMinutes(10)), ("1 小时", now.AddHours(1)) }.Concat(When.Presets(now).Skip(1)).ToList()
            : When.Presets(now);
        foreach (var (label, at) in options) chips.Children.Add(Chip(label, When.Format(at, now), () => Set(at)));
        root.Children.Add(chips);

        var box = input = new TextBox { Style = (Style)desk.Resource("Ui.Field"), Margin = new Thickness(0, 6, 0, 0) };
        var hint = new TextBlock { Text = "输入：18:30 · 明天 9点 · 周五下午3点 · 9/25 · +30m", FontSize = 11.5, Margin = new Thickness(2, 5, 0, 0), TextWrapping = TextWrapping.Wrap };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Muted");
        var ok = new Button { Content = "设定", Style = (Style)desk.Resource("Ui.PrimaryButton"), Margin = new Thickness(8, 6, 0, 0), IsEnabled = false };
        DateTime? parsed = null;
        box.TextChanged += (_, _) =>
        {
            var at = DateTime.Now;
            var p = When.Parse(box.Text, at);
            parsed = p > at ? p : null; // a time already past would ring at once: refuse it here
            ok.IsEnabled = parsed != null;
            hint.Text = box.Text.Trim().Length == 0 ? "输入：18:30 · 明天 9点 · 周五下午3点 · 9/25 · +30m"
                : p == null ? "看不懂这个时间"
                : parsed == null ? $"这个时间已经过了（{p:MM-dd HH:mm}）"
                : $"→ {p:yyyy-MM-dd} {When.Format(p.Value, at)}";
        };
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter && parsed != null) Set(parsed); };
        ok.Click += (_, _) => { if (parsed != null) Set(parsed); };
        var row = new DockPanel();
        DockPanel.SetDock(ok, Dock.Right);
        row.Children.Add(ok);
        row.Children.Add(box);
        root.Children.Add(row);
        root.Children.Add(hint);

        if (card.Card.RemindAt != null)
        {
            var clear = new Button { Content = "清除提醒", Style = (Style)desk.Resource("Ui.Button"), Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            clear.Click += (_, _) => Set(null);
            root.Children.Add(clear);
        }
        return root;
    }

    // ================= colors =================

    public static void Colors(Desk desk, CardWindow card, FrameworkElement anchor)
    {
        Flyout? self = null;
        var row = ColorsPanel(desk, card, () => self?.Dismiss());
        self = Open(card, anchor, row);
    }

    public static WrapPanel ColorsPanel(Desk desk, CardWindow card, Action close)
    {
        var row = new WrapPanel { Width = 10 * 30 };
        int shown = desk.ColorOf(card.Card);
        foreach (int i in Card.Rotation.Concat([7, 8]))
        {
            int index = i;
            var sw = Palette.Get(i);
            bool current = card.Card.Color == i;
            var dot = new Grid { Width = 30, Height = 30, Cursor = Cursors.Hand, Background = Brushes.Transparent, ToolTip = sw.Name };
            dot.Children.Add(new Ellipse { Width = 22, Height = 22, Fill = Palette.Brush(sw.Back), Stroke = Palette.Brush(sw.Accent), StrokeThickness = current ? 2.4 : i == shown ? 1.4 : 0.8 });
            dot.MouseLeftButtonUp += (_, _) => { desk.SetColor(card, index); close(); };
            row.Children.Add(dot);
        }
        var auto = new Grid { Width = 30, Height = 30, Cursor = Cursors.Hand, Background = Brushes.Transparent, ToolTip = "自动配色（跟随“新卡配色”设置）" };
        var ring = new Ellipse { Width = 22, Height = 22, StrokeThickness = card.Card.Color != null ? 1 : 2.2, StrokeDashArray = new DoubleCollection { 2, 2 } };
        ring.SetResourceReference(Shape.StrokeProperty, "Ui.Muted");
        auto.Children.Add(ring);
        auto.Children.Add(new TextBlock { Text = "A", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        auto.MouseLeftButtonUp += (_, _) => { desk.SetColor(card, null); close(); };
        row.Children.Add(auto);
        return row;
    }

    static TextBlock Caption(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12, Margin = new Thickness(2, 0, 0, 8), FontWeight = FontWeights.SemiBold };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Muted");
        return t;
    }

    static Button Chip(string label, string detail, Action click)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = label });
        var d = new TextBlock { Text = "  " + detail, FontSize = 11.5 };
        d.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Muted");
        content.Children.Add(d);
        var b = new Button { Content = content };
        b.SetResourceReference(StyleProperty, "Ui.Chip");
        b.Click += (_, _) => click();
        return b;
    }
}
