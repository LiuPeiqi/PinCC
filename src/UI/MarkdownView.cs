using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using PinCC.Core;

namespace PinCC.UI;

/// <summary>What a rendered card needs from its owner: colors, image loading and callbacks.</summary>
public sealed record MdContext
{
    public required Brush Ink { get; init; }
    public required Brush Muted { get; init; }
    public required Brush Accent { get; init; }
    public required Brush Soft { get; init; }
    public double Size { get; init; } = 13.5;
    public Func<string, ImageSource?> Image { get; init; } = _ => null;
    public Action<int>? ToggleTask { get; init; }
    public Action<string>? Open { get; init; }
    /// <summary>Checklist folding (null: never fold): checked tasks fold behind one line once there are a few of them.</summary>
    public Action? ToggleDone { get; init; }
    /// <summary>With <see cref="ToggleDone"/>: the folded tasks are shown for now.</summary>
    public bool ShowDone { get; init; }
}

/// <summary>Renders the Core.Markdown blocks into lightweight WPF elements (TextBlocks, no scrolling containers).</summary>
public static class MarkdownView
{
    public static readonly FontFamily Ui = new("Microsoft YaHei UI, Segoe UI");
    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Microsoft YaHei UI");
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    /// <summary>Renders <paramref name="md"/>; <paramref name="lineOffset"/> maps block lines back to the full body (tasks, caret).</summary>
    public static void Render(Panel host, string md, MdContext cx, int lineOffset = 0)
    {
        host.Children.Clear();
        MdBlock? prev = null;
        var blocks = Markdown.Parse(md);
        var fold = cx.ToggleDone != null ? Markdown.FoldDone(blocks) : null;
        for (int i = 0; i < blocks.Count; i++)
        {
            if (fold != null && !cx.ShowDone && fold.Contains(i)) continue;
            var b = blocks[i] with { Line = blocks[i].Line + lineOffset };
            var e = Element(b, cx);
            e.Tag = b.Line; // lets double-click put the caret on this source line
            e.Margin = new Thickness(Indent(b), prev == null ? 0 : Gap(prev, b), 0, 0);
            host.Children.Add(e);
            prev = b;
        }
        if (fold != null) host.Children.Add(FoldLine(fold.Count(i => blocks[i] is { Kind: BlockKind.Task, Checked: true }), cx));
    }

    /// <summary>"▸ 已完成 N 项" under a checklist whose checked items are folded (click: show them / fold them again).</summary>
    static FrameworkElement FoldLine(int done, MdContext cx)
    {
        var t = new TextBlock { FontSize = cx.Size - 1.5, Foreground = cx.Muted, Cursor = Cursors.Hand, Margin = new Thickness(0, 6, 0, 0), Background = Brushes.Transparent };
        t.Inlines.Add(new Run(cx.ShowDone ? "\uE70D" : "\uE76C") { FontFamily = Icons, FontSize = cx.Size - 5, BaselineAlignment = BaselineAlignment.Center });
        t.Inlines.Add(new Run(cx.ShowDone ? $"  收起已完成的 {done} 项" : $"  已完成 {done} 项"));
        t.ToolTip = cx.ShowDone ? "折叠已勾选的任务" : "显示已勾选的任务";
        t.MouseLeftButtonUp += (_, e) => { e.Handled = true; cx.ToggleDone?.Invoke(); };
        return t;
    }

    static double Indent(MdBlock b) => b.Indent * 16;

    static double Gap(MdBlock prev, MdBlock b)
    {
        bool list(MdBlock x) => x.Kind is BlockKind.Bullet or BlockKind.Ordered or BlockKind.Task;
        if (b.Kind == BlockKind.Heading) return 10;
        if (list(prev) && list(b)) return 2;
        return 6;
    }

    static FrameworkElement Element(MdBlock b, MdContext cx) => b.Kind switch
    {
        BlockKind.Heading => Text(b.Text, cx, cx.Size + (b.Level == 1 ? 4 : b.Level == 2 ? 2 : 0.5), FontWeights.SemiBold),
        BlockKind.Bullet => Item(b.Indent == 0 ? "•" : "◦", Text(b.Text, cx, cx.Size), cx),
        BlockKind.Ordered => Item(b.Marker + ".", Text(b.Text, cx, cx.Size), cx),
        BlockKind.Task => Task(b, cx),
        BlockKind.Quote => new Border
        {
            BorderBrush = cx.Accent, BorderThickness = new Thickness(2.5, 0, 0, 0), Padding = new Thickness(8, 0, 0, 0),
            Child = Text(b.Text, cx, cx.Size, FontWeights.Normal, cx.Muted),
        },
        BlockKind.Code => new Border
        {
            Background = cx.Soft, CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 5, 8, 6),
            Child = new TextBlock
            {
                Text = b.Text, FontFamily = Mono, FontSize = cx.Size - 1.5, Foreground = cx.Ink, TextWrapping = TextWrapping.Wrap,
            },
        },
        BlockKind.Rule => new Border { Height = 1, Background = cx.Muted, Opacity = 0.35, Margin = new Thickness(0, 4, 0, 4) },
        BlockKind.Image => Picture(b.Text, cx),
        _ => Text(b.Text, cx, cx.Size),
    };

    /// <summary>A wrapping paragraph; "\n" in <paramref name="text"/> are hard line breaks.</summary>
    public static TextBlock Text(string text, MdContext cx, double size, FontWeight? weight = null, Brush? fg = null)
    {
        var tb = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = size,
            FontWeight = weight ?? FontWeights.Normal,
            Foreground = fg ?? cx.Ink,
            LineHeight = Math.Round(size * 1.5),
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) tb.Inlines.Add(new LineBreak());
            foreach (var s in Markdown.Inlines(lines[i])) tb.Inlines.Add(Inline(s, cx, size));
        }
        return tb;
    }

    static Inline Inline(Seg s, MdContext cx, double size)
    {
        var run = new Run(s.Text);
        if (s.Fmt.HasFlag(Fmt.Bold)) run.FontWeight = FontWeights.SemiBold;
        if (s.Fmt.HasFlag(Fmt.Italic)) run.FontStyle = FontStyles.Italic;
        if (s.Fmt.HasFlag(Fmt.Strike)) run.TextDecorations = TextDecorations.Strikethrough;
        if (s.Fmt.HasFlag(Fmt.Code)) { run.FontFamily = Mono; run.FontSize = size - 1.5; run.Background = cx.Soft; }
        if (s.Url is not { } url) return run;
        if (url.StartsWith('#')) { run.Foreground = cx.Accent; return run; }
        if (s.Text == url) run.Text = System.Text.RegularExpressions.Regex.Replace(url, @"([/?&=\-_.])", "$1\u200B"); // wrap long URLs sensibly
        var link = new Hyperlink(run) { Foreground = cx.Accent, TextDecorations = null, Cursor = Cursors.Hand, ToolTip = url };
        link.Click += (_, e) => { cx.Open?.Invoke(url); e.Handled = true; };
        link.MouseEnter += (_, _) => link.TextDecorations = TextDecorations.Underline;
        link.MouseLeave += (_, _) => link.TextDecorations = null;
        return link;
    }

    static FrameworkElement Item(string marker, TextBlock body, MdContext cx)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        var m = new TextBlock { Text = marker, Foreground = cx.Muted, FontSize = body.FontSize, LineHeight = body.LineHeight, LineStackingStrategy = body.LineStackingStrategy };
        Grid.SetColumn(body, 1);
        g.Children.Add(m);
        g.Children.Add(body);
        return g;
    }

    static FrameworkElement Task(MdBlock b, MdContext cx)
    {
        var body = Text(b.Text, cx, cx.Size, fg: b.Checked ? cx.Muted : null);
        if (b.Checked) body.TextDecorations = TextDecorations.Strikethrough;
        var box = Check(b.Checked, cx, body.LineHeight, cx.ToggleTask != null);
        if (cx.ToggleTask != null) box.MouseLeftButtonDown += (_, e) => { e.Handled = true; cx.ToggleTask(b.Line); };
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(body, 1);
        body.Opacity = b.Checked ? 0.75 : 1;
        g.Children.Add(box);
        g.Children.Add(body);
        return g;
    }

    /// <summary>A rounded-square check (circles are reserved for completing the whole card); 4 DIP of extra hit area.</summary>
    static FrameworkElement Check(bool done, MdContext cx, double lineHeight, bool interactive)
    {
        const double d = 14;
        var box = new Border
        {
            Width = d, Height = d, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1.25),
            BorderBrush = done ? cx.Accent : cx.Muted, Background = done ? cx.Accent : Brushes.Transparent,
        };
        if (done)
            box.Child = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 2.6,6.4 L 5.2,8.9 L 9.6,3.8"), Stroke = Brushes.White, StrokeThickness = 1.6,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            };
        var host = new Grid
        {
            Width = d + 8, Height = d + 8, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(-4, (lineHeight - d) / 2 - 4, 0, 0), Background = Brushes.Transparent,
        };
        host.Children.Add(box);
        if (interactive)
        {
            host.Cursor = Cursors.Hand;
            if (!done)
            {
                host.MouseEnter += (_, _) => box.BorderBrush = cx.Accent;
                host.MouseLeave += (_, _) => box.BorderBrush = cx.Muted;
            }
        }
        return host;
    }

    static FrameworkElement Picture(string src, MdContext cx)
    {
        var source = cx.Image(src);
        if (source == null) return Text("🖼 " + src, cx, cx.Size - 1, fg: cx.Muted);
        var img = new Image { Source = source, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        Round(img, 6);
        return img;
    }

    static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached("Radius", typeof(double), typeof(MarkdownView), new PropertyMetadata(-1.0));

    /// <summary>Clips an element to a rounded rectangle that follows its size (radius 0 removes the clip). Safe to call on every render.</summary>
    public static void Round(FrameworkElement e, double radius)
    {
        bool hooked = (double)e.GetValue(RadiusProperty) >= 0;
        e.SetValue(RadiusProperty, radius);
        Clip(e);
        if (!hooked) e.SizeChanged += (_, _) => Clip(e);
    }

    static void Clip(FrameworkElement e)
    {
        var r = (double)e.GetValue(RadiusProperty);
        e.Clip = r > 0 && e.ActualWidth > 0 ? new RectangleGeometry(new Rect(0, 0, e.ActualWidth, e.ActualHeight), r, r) : null;
    }
}
