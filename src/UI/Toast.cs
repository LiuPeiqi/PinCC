using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace PinCC.UI;

/// <summary>
/// A short non-activating notice; fades after 4 s (kept while hovered). Where it goes is the desk's decision
/// (<see cref="Desk.Notify"/>): beside the hub, or the first nearby spot no card covers.
/// </summary>
public sealed class Toast : Window
{
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(4) };
    bool _closing;

    /// <param name="place">Given the toast's physical size, returns its physical rect.</param>
    public Toast(string text, Func<(int W, int H), Px> place)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Opacity = 0;
        var surface = new Border { Child = new TextBlock { Text = text, MaxWidth = 280, TextWrapping = TextWrapping.Wrap }, Padding = new Thickness(12, 7, 12, 8) };
        surface.SetResourceReference(StyleProperty, "Ui.Surface");
        Content = surface;

        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            Native.AddExStyle(h, Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
            HwndSource.FromHwnd(h).AddHook(Native.NoActivateOnDpiChange());
        };
        Loaded += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            var size = Native.WindowRect(h);
            // Measured at the window's current DPI; the target monitor may scale differently.
            var r = place((size.W, size.H));
            var target = Screens.Of(r);
            double k = target.Scale / Native.WindowScale(h);
            if (Math.Abs(k - 1) > 0.001) r = place(((int)(size.W * k), (int)(size.H * k)));
            Native.Place(h, r);
            Opacity = 1;
        };
        Closing += (_, _) => { _closing = true; _timer.Stop(); };
        _timer.Tick += (_, _) => Dismiss();
        MouseEnter += (_, _) => _timer.Stop();
        MouseLeave += (_, _) => { if (!_closing) _timer.Start(); };
        MouseLeftButtonUp += (_, _) => Dismiss();
        _timer.Start();
    }

    /// <summary>Closes once: a click and the timer (or a newer notice) may both ask.</summary>
    public void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }
}
