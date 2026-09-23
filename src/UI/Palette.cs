using System.Windows;
using System.Windows.Media;

namespace PinCC.UI;

/// <summary>
/// Card colors: 7 equal-weight pastels (OKLCH L≈0.955) with a darker accent that keeps ≥4.5:1 contrast for links and
/// checks, plus manual-only gray and charcoal. Text #1F2328 reads at ≥13:1 on every pastel. Red is reserved for overdue.
/// </summary>
public static class Palette
{
    public sealed record Swatch(string Name, Color Back, Color Strip, Color Accent, Color Ink);

    static readonly Color Text = C("#1F2328");

    // Index order is the storage order; Card.Rotation defines the automatic sequence.
    public static readonly Swatch[] All =
    {
        new("柠檬", C("#FCF1BC"), C("#F3E49D"), C("#786914"), Text), // 0
        new("蜜桃", C("#FFEBD1"), C("#FFDCBB"), C("#935A11"), Text), // 1
        new("蔷薇", C("#FFE6F4"), C("#FFD5E8"), C("#974C72"), Text), // 2
        new("丁香", C("#F6EAFF"), C("#EBDBFF"), C("#78579B"), Text), // 3
        new("天空", C("#DFF3FF"), C("#CDE6FF"), C("#316CA5"), Text), // 4
        new("湖水", C("#D6F8F7"), C("#C1EDEC"), C("#007C7C"), Text), // 5
        new("薄荷", C("#DEF9DE"), C("#CCEECC"), C("#3B793F"), Text), // 6
        new("云灰", C("#F0F0F0"), C("#E3E3E3"), C("#636363"), Text), // 7 (manual)
        new("墨炭", C("#2F3133"), C("#3A3C3F"), C("#8CC3FC"), C("#ECECEC")), // 8 (manual)
    };

    public static readonly Color Overdue = C("#D13438");
    public static readonly Color UiAccent = C("#316CA5");

    public static Swatch Get(int i) => All[((i % All.Length) + All.Length) % All.Length];

    public static SolidColorBrush Brush(Color c, double alpha = 1)
    {
        var b = new SolidColorBrush(alpha >= 1 ? c : Color.FromArgb((byte)Math.Round(alpha * 255), c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    /// <summary>App-wide brushes for menus, flyouts and the manage window (keys used by Theme.xaml).</summary>
    public static void ApplyChrome(ResourceDictionary r)
    {
        r["Ui.Bg"] = Brush(C("#FFFFFF"));
        r["Ui.Border"] = Brush(C("#E3E3E6"));
        r["Ui.Hover"] = Brush(C("#F0F0F3"));
        r["Ui.Ink"] = Brush(Text);
        r["Ui.Muted"] = Brush(C("#6B6F76"));
        r["Ui.Accent"] = Brush(UiAccent);
        r["Ui.FieldBg"] = Brush(C("#F6F6F8"));
        r["Ui.Danger"] = Brush(Overdue);
    }

    static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
