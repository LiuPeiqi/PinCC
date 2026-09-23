using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace PinCC.UI;

/// <summary>
/// The "+" button, one per monitor, at the corner chosen in settings (computed, never stored per monitor, so it is
/// right on every resolution and scale). Click: new card here. Hover: speed dial with the three one-click actions
/// (平铺 / 折叠 / 最小化). Right-click: the shared menu. Drop files, links, pictures or text on it to make cards.
/// It never takes focus and steps aside while a full-screen app runs on its monitor.
/// </summary>
public sealed class HubWindow : Window
{
    const double D = 40, Dial = 32, Step = 38, Inset = 16;
    readonly Desk _desk;
    readonly Border _dot, _badge;
    readonly TextBlock _plus, _badgeText;
    readonly Ellipse _alarm;
    readonly StackPanel _dial;
    readonly DispatcherTimer _hoverIn = new() { Interval = TimeSpan.FromMilliseconds(300) };
    readonly DispatcherTimer _hoverOut = new() { Interval = TimeSpan.FromMilliseconds(350) };
    IntPtr _hwnd;
    Point _down;
    bool _pressed, _dragging;
    Px _dragStart;
    Native.POINT _cursorStart;

    public Screen Screen { get; }
    bool Bottom => _desk.HubCorner is Corner.BottomLeft or Corner.BottomRight;

    /// <summary>The whole hub (button + dial area) in physical px: an obstacle for tiling.</summary>
    public Px Rect => _hwnd == IntPtr.Zero ? default : Native.WindowRect(_hwnd);

    public HubWindow(Desk desk, Screen screen)
    {
        _desk = desk;
        Screen = screen;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Width = D;
        Height = D + 3 * Step;
        AllowDrop = true;
        UseLayoutRounding = true;

        _plus = new TextBlock { Text = "\uE710", FontFamily = MarkdownView.Icons, FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _dot = new Border { Width = D, Height = D, CornerRadius = new CornerRadius(D / 2), BorderThickness = new Thickness(1), Child = _plus, Cursor = Cursors.Hand, Opacity = 0.6 };
        _alarm = new Ellipse { Width = 9, Height = 9, Fill = Palette.Brush(Palette.Overdue), Stroke = Brushes.White, StrokeThickness = 1.5, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        _badgeText = new TextBlock { FontSize = 10, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
        _badge = new Border { MinWidth = 16, Height = 16, CornerRadius = new CornerRadius(8), Padding = new Thickness(4, 0, 4, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(-4, -4, 0, 0), Child = _badgeText, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        var button = new Grid { Width = D, Height = D };
        button.Children.Add(_dot);
        button.Children.Add(_alarm);
        button.Children.Add(_badge);

        _dial = new StackPanel { Opacity = 0, IsHitTestVisible = false };
        var root = new DockPanel();
        DockPanel.SetDock(button, Bottom ? Dock.Bottom : Dock.Top);
        root.Children.Add(button);
        _dial.VerticalAlignment = Bottom ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        root.Children.Add(_dial);
        Content = root;
        BuildDial();

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Native.AddExStyle(_hwnd, Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
            var src = HwndSource.FromHwnd(_hwnd);
            src.AddHook(Native.NoActivateOnDpiChange());
            src.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (msg == Native.WM_MOUSEACTIVATE) { handled = true; return new IntPtr(Native.MA_NOACTIVATE); }
                return IntPtr.Zero;
            });
            Native.Place(_hwnd, Home());
        };
        _hoverIn.Tick += (_, _) => { _hoverIn.Stop(); ShowDial(true); };
        _hoverOut.Tick += (_, _) => { _hoverOut.Stop(); ShowDial(false); };
        button.MouseEnter += (_, _) => { _hoverOut.Stop(); _dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120))); if (!_dragging) _hoverIn.Start(); };
        MouseEnter += (_, _) => _hoverOut.Stop();
        MouseLeave += (_, _) =>
        {
            _hoverIn.Stop();
            _hoverOut.Start();
            _dot.BeginAnimation(OpacityProperty, new DoubleAnimation(0.6, TimeSpan.FromMilliseconds(200)));
        };

        // Drag by hand (a non-activating window gets no live feedback from DragMove); release snaps to a corner.
        button.MouseLeftButtonDown += (_, e) =>
        {
            _pressed = true;
            _dragging = false;
            _down = e.GetPosition(this);
            _dragStart = Rect;
            Native.GetCursorPos(out _cursorStart);
            button.CaptureMouse();
            e.Handled = true;
        };
        button.MouseMove += (_, e) =>
        {
            if (!_pressed) return;
            var d = e.GetPosition(this) - _down;
            if (!_dragging && Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _dragging = true;
            ShowDial(false);
            Native.GetCursorPos(out var p);
            Native.SetWindowPos(_hwnd, IntPtr.Zero, _dragStart.X + p.X - _cursorStart.X, _dragStart.Y + p.Y - _cursorStart.Y, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        };
        button.MouseLeftButtonUp += (_, _) =>
        {
            button.ReleaseMouseCapture();
            if (!_pressed) return;
            _pressed = false;
            if (!_dragging) { _desk.NewCard(Screen, returnFocus: Native.GetForegroundWindow()); return; }
            Native.GetCursorPos(out var p);
            var s = Screens.At(p.X, p.Y);
            bool left = p.X < s.Work.CX, top = p.Y < s.Work.CY;
            _desk.SetHubCorner(top ? (left ? Corner.TopLeft : Corner.TopRight) : (left ? Corner.BottomLeft : Corner.BottomRight));
        };
        button.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            // A menu of a window that never activates gets no keyboard and may not close on an outside click:
            // let the hub take the foreground while its menu is open, then step back.
            Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64() & ~(long)Native.WS_EX_NOACTIVATE));
            Native.SetForegroundWindow(_hwnd);
            var menu = _desk.HubMenu(Screen);
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.MousePoint;
            menu.Closed += (_, _) => Native.AddExStyle(_hwnd, Native.WS_EX_NOACTIVATE);
            menu.IsOpen = true;
        };
        DragEnter += (_, _) => _dot.RenderTransform = new ScaleTransform(1.12, 1.12, D / 2, D / 2);
        DragLeave += (_, _) => _dot.RenderTransform = null;
        Drop += (_, e) => { _dot.RenderTransform = null; _desk.CreateFrom(e.Data, Screen); };
    }

    Px Home()
    {
        int w = Screen.Px(D), h = Screen.Px(D + 3 * Step), inset = Screen.Px(Inset);
        var c = _desk.HubCorner;
        int x = c is Corner.TopLeft or Corner.BottomLeft ? Screen.Work.X + inset : Screen.Work.Right - inset - w;
        int y = Bottom ? Screen.Work.Bottom - inset - h : Screen.Work.Y + inset;
        return new Px(x, y, w, h);
    }

    void BuildDial()
    {
        _dial.Children.Clear();
        var items = new (string Glyph, string Tip, Action Act)[]
        {
            ("\uE8A9", "平铺显示（全部显示、展开、回到平铺）", _desk.Tile),
            (_desk.Cards.Any(w => w.Ref > 0 && !w.Card.Collapsed) ? "\uE70E" : "\uE70D", "全部折叠 / 展开", _desk.CollapseAll),
            (_desk.Hidden ? "\uE7B3" : "\uE921", _desk.Hidden ? "恢复显示" : "最小化全部", _desk.ToggleHidden),
        };
        var ordered = Bottom ? items.Reverse().ToArray() : items;
        foreach (var (glyph, tip, act) in ordered)
        {
            var b = new Border
            {
                Width = Dial, Height = Dial, CornerRadius = new CornerRadius(Dial / 2), Margin = new Thickness(0, 3, 0, 3), Cursor = Cursors.Hand,
                BorderThickness = new Thickness(1), ToolTip = tip, HorizontalAlignment = HorizontalAlignment.Center,
                Child = new TextBlock { Text = glyph, FontFamily = MarkdownView.Icons, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            b.SetResourceReference(Border.BackgroundProperty, "Ui.Bg");
            b.SetResourceReference(Border.BorderBrushProperty, "Ui.Border");
            TextElement.SetForeground(b, Palette.Brush(Palette.Get(7).Ink));
            b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "Ui.Hover");
            b.MouseLeave += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "Ui.Bg");
            b.MouseLeftButtonUp += (_, e) => { e.Handled = true; act(); BuildDial(); };
            _dial.Children.Add(b);
        }
    }

    void ShowDial(bool show)
    {
        if (show) BuildDial();
        _dial.IsHitTestVisible = show;
        _dial.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(show ? 120 : 180)));
    }

    /// <summary>Colors (the next card's color), badges and full-screen visibility.</summary>
    public void Refresh()
    {
        var sw = Palette.Get(_desk.NextColor);
        _dot.Background = Palette.Brush(sw.Back);
        _dot.BorderBrush = Palette.Brush(Colors.Black, 0.12);
        _plus.Foreground = Palette.Brush(sw.Accent);
        _alarm.Visibility = _desk.OverdueCount(Screen) > 0 ? Visibility.Visible : Visibility.Collapsed;
        int overflow = _desk.OverflowCount(Screen), hidden = _desk.Hidden ? _desk.Cards.Count(w => w.Ref > 0 && !w.IsVisible) : 0;
        (string? text, Color color) = _desk.HotkeyProblem ? ("!", Palette.Overdue) : overflow > 0 ? ($"+{overflow}", Palette.UiAccent) : hidden > 0 ? (hidden.ToString(), Color.FromRgb(0x6B, 0x6F, 0x76)) : (null, default);
        _badge.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
        _badgeText.Text = text ?? "";
        _badge.Background = Palette.Brush(color);
        ToolTip = $"新建卡片（{_desk.Store.Setting("hk.new", Hotkeys.Actions[0].Default)}）\n悬停：平铺 / 折叠 / 最小化 · 右键：更多\n可拖入文件、链接、图片或文字"
            + (overflow > 0 ? $"\n空间不足：{overflow} 张卡片未显示（管理 › 卡片 可查看）" : "")
            + (_desk.HotkeyProblem ? "\n有快捷键被其他程序占用（右键查看）" : "");
        var vis = _desk.Fullscreen(Screen) ? Visibility.Hidden : Visibility.Visible;
        if (Visibility != vis) Visibility = vis;
    }
}
