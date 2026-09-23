using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PinCC.UI;

/// <summary>Shared look and small builders for the two auxiliary windows (settings, library).</summary>
public class AuxWindow : Window
{
    protected readonly Desk Desk;

    protected AuxWindow(Desk desk, string title, double width, double height)
    {
        Desk = desk;
        Title = title;
        Width = width;
        Height = height;
        MinWidth = 420;
        MinHeight = 360;
        UseLayoutRounding = true;
        ShowInTaskbar = true;
        FontFamily = (FontFamily)desk.Resource("Ui.Font");
        FontSize = 13;
        SetResourceReference(BackgroundProperty, "Ui.Bg");
        SetResourceReference(ForegroundProperty, "Ui.Ink");
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/pincc.ico")); } catch { }
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(h).AddHook(Native.NoActivateOnDpiChange());
        };
    }

    protected static TextBlock Heading(string text) => new()
    {
        Text = text, FontSize = 14.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 8),
    };

    protected static TextBlock Muted(string text, double size = 12)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Muted");
        return t;
    }

    /// <summary>Label on the left (fixed width), control on the right.</summary>
    protected static FrameworkElement Row(string label, UIElement control, double labelWidth = 118)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        return g;
    }

    /// <summary>One-of-N choice bound to a string value (wraps when the options do not fit one line).</summary>
    protected WrapPanel Segments(string group, (string Value, string Label)[] options, string current, Action<string> changed)
    {
        var p = new WrapPanel();
        foreach (var (value, label) in options)
        {
            var rb = new RadioButton { Content = label, GroupName = group + GetHashCode(), IsChecked = value == current };
            rb.SetResourceReference(StyleProperty, "Ui.Segment");
            rb.Checked += (_, _) => changed(value);
            p.Children.Add(rb);
        }
        return p;
    }

    protected CheckBox Switch(string label, bool value, Action<bool> changed)
    {
        var cb = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 5, 0, 5) };
        cb.SetResourceReference(StyleProperty, "Ui.Switch");
        cb.Checked += (_, _) => changed(true);
        cb.Unchecked += (_, _) => changed(false);
        return cb;
    }

    protected Button Btn(string text, Action click, bool primary = false)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0) };
        b.SetResourceReference(StyleProperty, primary ? "Ui.PrimaryButton" : "Ui.Button");
        b.Click += (_, _) => click();
        return b;
    }

    protected TextBox Field(string text = "")
    {
        var t = new TextBox { Text = text };
        t.SetResourceReference(StyleProperty, "Ui.Field");
        return t;
    }

}
