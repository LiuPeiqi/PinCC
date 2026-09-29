using System.Text;
using System.Text.RegularExpressions;

namespace PinCC.Core;

public enum BlockKind { Paragraph, Heading, Bullet, Ordered, Task, Quote, Code, Rule, Image }

/// <summary>One rendered block. Line = 0-based source line of the block start (used to toggle tasks).</summary>
public sealed record MdBlock(BlockKind Kind, string Text, int Line, int Level = 0, int Indent = 0, bool Checked = false, string Marker = "");

[Flags] public enum Fmt { None = 0, Bold = 1, Italic = 2, Strike = 4, Code = 8 }

/// <summary>A run of inline text; Url set for links (including bare URLs) and #tags (Url = "#tag").</summary>
public sealed record Seg(string Text, Fmt Fmt = Fmt.None, string? Url = null);

/// <summary>
/// The small markdown subset sticky notes need, as plain data (the WPF renderer lives in UI/MarkdownView).
/// Sticky-note semantics: a newline is a line break, not a soft wrap.
/// </summary>
public static partial class Markdown
{
    public static List<MdBlock> Parse(string md)
    {
        var blocks = new List<MdBlock>();
        var lines = md.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0) continue;
            Match m;
            if (line.TrimStart().StartsWith("```"))
            {
                int start = i;
                var code = new StringBuilder();
                while (++i < lines.Length && !lines[i].TrimStart().StartsWith("```")) code.Append(code.Length > 0 ? "\n" : "").Append(lines[i]);
                blocks.Add(new MdBlock(BlockKind.Code, code.ToString(), start));
            }
            else if ((m = HeadingRx().Match(line)).Success)
                blocks.Add(new MdBlock(BlockKind.Heading, m.Groups[2].Value.Trim(), i, m.Groups[1].Length));
            else if (RuleRx().IsMatch(line))
                blocks.Add(new MdBlock(BlockKind.Rule, "", i));
            else if ((m = ImageLineRx().Match(line)).Success)
                blocks.Add(new MdBlock(BlockKind.Image, m.Groups[2].Value, i, Marker: m.Groups[1].Value));
            else if ((m = TaskLineRx().Match(line)).Success)
                blocks.Add(new MdBlock(BlockKind.Task, m.Groups[4].Value, i, Indent: Indent(m.Groups[1].Value), Checked: m.Groups[3].Value != " "));
            else if ((m = BulletRx().Match(line)).Success)
                blocks.Add(new MdBlock(BlockKind.Bullet, m.Groups[3].Value, i, Indent: Indent(m.Groups[1].Value)));
            else if ((m = OrderedRx().Match(line)).Success)
                blocks.Add(new MdBlock(BlockKind.Ordered, m.Groups[3].Value, i, Indent: Indent(m.Groups[1].Value), Marker: m.Groups[2].Value));
            else if ((m = QuoteRx().Match(line)).Success)
            {
                int start = i;
                var q = new StringBuilder(m.Groups[1].Value);
                while (i + 1 < lines.Length && QuoteRx().Match(lines[i + 1]) is { Success: true } n) { q.Append('\n').Append(n.Groups[1].Value); i++; }
                blocks.Add(new MdBlock(BlockKind.Quote, q.ToString(), start));
            }
            else
            {
                int start = i;
                var p = new StringBuilder(line.Trim());
                while (i + 1 < lines.Length && IsParagraphLine(lines[i + 1])) p.Append('\n').Append(lines[++i].Trim());
                blocks.Add(new MdBlock(BlockKind.Paragraph, p.ToString(), start));
            }
        }
        return blocks;
    }

    static bool IsParagraphLine(string l) =>
        l.Trim().Length > 0 && !l.TrimStart().StartsWith("```") && !HeadingRx().IsMatch(l) && !RuleRx().IsMatch(l) &&
        !ImageLineRx().IsMatch(l) && !TaskLineRx().IsMatch(l) && !BulletRx().IsMatch(l) && !OrderedRx().IsMatch(l) && !QuoteRx().IsMatch(l);

    static int Indent(string ws) => Math.Min(3, ws.Replace("\t", "  ").Length / 2);

    /// <summary>Inline spans: **bold**, *italic*/_italic_, ~~strike~~, `code`, [text](url), bare URLs, #tags, \escapes.</summary>
    public static List<Seg> Inlines(string text)
    {
        var runs = new List<Seg>();
        var buf = new StringBuilder();
        var style = Fmt.None;
        void Flush() { if (buf.Length > 0) { runs.Add(new Seg(buf.ToString(), style)); buf.Clear(); } }

        for (int i = 0; i < text.Length;)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length && "\\`*_~[]()#!".Contains(text[i + 1])) { buf.Append(text[i + 1]); i += 2; continue; }
            if (c == '`' && text.IndexOf('`', i + 1) is var end && end > i)
            {
                Flush();
                runs.Add(new Seg(text[(i + 1)..end], style | Fmt.Code));
                i = end + 1;
                continue;
            }
            if (c == '[' && LinkRx().Match(text, i) is { Success: true } lm && lm.Index == i)
            {
                Flush();
                runs.Add(new Seg(lm.Groups[1].Value.Length > 0 ? lm.Groups[1].Value : lm.Groups[2].Value, style, lm.Groups[2].Value));
                i += lm.Length;
                continue;
            }
            // Only an ASCII letter/digit before "http" means it is part of a word: "见https://…" is a link.
            if ((c == 'h' || c == 'H') && (i == 0 || !char.IsAsciiLetterOrDigit(text[i - 1])) && UrlRx().Match(text, i) is { Success: true } um && um.Index == i)
            {
                Flush();
                var url = TrimUrl(um.Value);
                runs.Add(new Seg(url, style, url));
                i += url.Length;
                continue;
            }
            if (c == '#' && (i == 0 || char.IsWhiteSpace(text[i - 1])) && TagRx().Match(text, i) is { Success: true } tm && tm.Index == i)
            {
                Flush();
                runs.Add(new Seg(tm.Value, style, tm.Value));
                i += tm.Length;
                continue;
            }
            if (Toggle(text, i, "**", Fmt.Bold, ref style, Flush) || Toggle(text, i, "__", Fmt.Bold, ref style, Flush) ||
                Toggle(text, i, "~~", Fmt.Strike, ref style, Flush) || Toggle(text, i, "*", Fmt.Italic, ref style, Flush) ||
                (IsWordEdge(text, i) && Toggle(text, i, "_", Fmt.Italic, ref style, Flush)))
            {
                i += c == '*' && i + 1 < text.Length && text[i + 1] == '*' || c == '_' && i + 1 < text.Length && text[i + 1] == '_' || c == '~' ? 2 : 1;
                continue;
            }
            buf.Append(c);
            i++;
        }
        Flush();
        return runs;
    }

    /// <summary>
    /// Drops sentence punctuation after a bare URL. A ")" goes only when unbalanced, so "(see https://x.y/a)" loses it but
    /// "https://en.wikipedia.org/wiki/Foo_(bar)" keeps it.
    /// </summary>
    static string TrimUrl(string url)
    {
        while (url.Length > 0)
        {
            char last = url[^1];
            if (".,;:!?\"'。，；）".Contains(last) || last == ')' && url.Count(ch => ch == ')') > url.Count(ch => ch == '(')) url = url[..^1];
            else break;
        }
        return url;
    }

    /// <summary>Opens a style only if a closing delimiter follows; always closes an open one.</summary>
    static bool Toggle(string t, int i, string d, Fmt s, ref Fmt style, Action flush)
    {
        if (string.CompareOrdinal(t, i, d, 0, d.Length) != 0) return false;
        if (d.Length == 1 && i + 1 < t.Length && t[i + 1] == d[0]) return false; // part of a double delimiter
        bool open = (style & s) != 0;
        if (!open)
        {
            if (i + d.Length >= t.Length || char.IsWhiteSpace(t[i + d.Length])) return false; // "5 * 3" stays literal
            int close = t.IndexOf(d, i + d.Length, StringComparison.Ordinal);
            if (close < 0) return false;
        }
        flush();
        style ^= s;
        return true;
    }

    static bool IsWordEdge(string t, int i) =>
        (i == 0 || !char.IsLetterOrDigit(t[i - 1])) || (i + 1 >= t.Length || !char.IsLetterOrDigit(t[i + 1]));

    /// <summary>A line with markdown syntax removed (titles, pills, CLI listings).</summary>
    public static string Plain(string line)
    {
        var s = line.Trim();
        if (s.StartsWith("```") || RuleRx().IsMatch(s)) return "";
        s = HeadingRx().Replace(s, "$2");
        s = TaskLineRx().Replace(s, "$4");
        s = BulletRx().Replace(s, "$3");
        s = OrderedRx().Replace(s, "$3");
        s = QuoteRx().Replace(s, "$1");
        s = ImageLineRx().IsMatch(s) ? "" : s;
        return string.Concat(Inlines(s).Select(r => r.Text)).Trim();
    }

    /// <summary>Flips "- [ ]" ⇄ "- [x]" on source line <paramref name="line"/>.</summary>
    public static string ToggleTask(string md, int line)
    {
        var lines = md.Replace("\r\n", "\n").Split('\n');
        if (line < 0 || line >= lines.Length) return md;
        var m = TaskLineRx().Match(lines[line]);
        if (!m.Success) return md;
        var box = m.Groups[3];
        lines[line] = lines[line][..box.Index] + (box.Value == " " ? "x" : " ") + lines[line][(box.Index + 1)..];
        return string.Join("\n", lines);
    }

    /// <summary>Checks or unchecks the task on source line <paramref name="line"/> (anything else is left alone).</summary>
    public static string SetTask(string md, int line, bool done)
    {
        var lines = md.Replace("\r\n", "\n").Split('\n');
        if (line < 0 || line >= lines.Length || TaskLineRx().Match(lines[line]) is not { Success: true } m || (m.Groups[3].Value != " ") == done) return md;
        return ToggleTask(md, line);
    }

    /// <summary>Source lines of the tasks, in order: the same tasks the progress counts (nothing inside ``` fences).</summary>
    public static List<int> TaskLines(string md) => Parse(md).Where(b => b.Kind == BlockKind.Task).Select(b => b.Line).ToList();

    /// <summary>
    /// Adds one open task per non-empty line of <paramref name="text"/> (a typed "- [ ] " or "- " is not doubled), after
    /// the last task and its sub-items, at the list's outer indent and marker; at the end when there is no task yet.
    /// </summary>
    public static string AddTasks(string md, string text)
    {
        var items = text.Replace("\r\n", "\n").Split('\n').Select(l => ItemPrefixRx().Replace(l.Trim(), "").Trim()).Where(l => l.Length > 0).ToList();
        if (items.Count == 0) return md;
        var lines = md.Replace("\r\n", "\n").Split('\n').ToList();
        var tasks = TaskLines(md);
        if (tasks.Count == 0)
        {
            var body = md.Replace("\r\n", "\n").TrimEnd();
            return (body.Length > 0 ? body + "\n" : "") + string.Join("\n", items.Select(i => "- [ ] " + i));
        }
        var outer = tasks.Select(l => TaskLineRx().Match(lines[l])).MinBy(m => m.Groups[1].Value.Length)!;
        string indent = outer.Groups[1].Value, marker = outer.Groups[2].Value;
        int at = tasks[^1] + 1;
        while (at < lines.Count && lines[at].Trim().Length > 0 && lines[at].Length - lines[at].TrimStart().Length > indent.Length) at++; // its sub-items
        lines.InsertRange(at, items.Select(i => $"{indent}{marker} [ ] {i}"));
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Checked tasks to fold out of view, with their sub-items (indexes into <paramref name="blocks"/>): only once at least
    /// <paramref name="min"/> are checked and one is still open. Null when nothing folds. Display only.
    /// </summary>
    public static HashSet<int>? FoldDone(IReadOnlyList<MdBlock> blocks, int min = 3)
    {
        int done = blocks.Count(b => b.Kind == BlockKind.Task && b.Checked);
        if (done < min || !blocks.Any(b => b.Kind == BlockKind.Task && !b.Checked)) return null;
        var hide = new HashSet<int>();
        for (int i = 0; i < blocks.Count; i++)
        {
            if (blocks[i].Kind != BlockKind.Task || !blocks[i].Checked) continue;
            int indent = blocks[i].Indent;
            hide.Add(i);
            while (i + 1 < blocks.Count && blocks[i + 1].Kind is (BlockKind.Bullet or BlockKind.Ordered or BlockKind.Task) && blocks[i + 1].Indent > indent) hide.Add(++i);
        }
        return hide;
    }

    /// <summary>
    /// Enter inside a list line: returns the prefix to start the next item ("- [ ] ", "  - ", "3. "),
    /// "" when the current item is empty (the caller removes the marker to end the list), or null for non-list lines.
    /// </summary>
    public static string? ContinueList(string line)
    {
        Match m;
        if ((m = TaskLineRx().Match(line)).Success)
            return m.Groups[4].Value.Trim().Length == 0 ? "" : $"{m.Groups[1].Value}{m.Groups[2].Value} [ ] ";
        if ((m = BulletRx().Match(line)).Success)
            return m.Groups[3].Value.Trim().Length == 0 ? "" : $"{m.Groups[1].Value}{m.Groups[2].Value} ";
        if ((m = OrderedRx().Match(line)).Success)
            return m.Groups[3].Value.Trim().Length == 0 ? "" : $"{m.Groups[1].Value}{int.Parse(m.Groups[2].Value) + 1}. ";
        if ((m = QuoteRx().Match(line)).Success)
            return m.Groups[1].Value.Trim().Length == 0 ? "" : "> ";
        return null;
    }

    /// <summary>Converts a "[] " or "[ ] " typed at the start of a line into a task item.</summary>
    public static bool IsTaskShortcut(string lineBeforeCaret) => lineBeforeCaret is "[]" or "[ ]";

    [GeneratedRegex(@"^\s{0,3}(#{1,6})\s+(.*)$")] private static partial Regex HeadingRx();
    [GeneratedRegex(@"^\s{0,3}([-*_])(\s*\1){2,}\s*$")] private static partial Regex RuleRx();
    [GeneratedRegex(@"^\s*!\[([^\]]*)\]\(([^)\s]+)\)\s*$")] private static partial Regex ImageLineRx();
    [GeneratedRegex(@"^(\s*)([-*+])\s+\[([ xX])\](?:\s+(.*))?$")] private static partial Regex TaskLineRx();
    [GeneratedRegex(@"^(\s*)([-*+])\s+(.*)$")] private static partial Regex BulletRx();
    [GeneratedRegex(@"^(?:[-*+]\s+)?(?:\[[ xX]?\](?:\s+|$))?")] private static partial Regex ItemPrefixRx();
    [GeneratedRegex(@"^(\s*)(\d{1,3})[.)]\s+(.*)$")] private static partial Regex OrderedRx();
    [GeneratedRegex(@"^\s{0,3}>\s?(.*)$")] private static partial Regex QuoteRx();
    // The target may hold one level of balanced parentheses: [w](https://en.wikipedia.org/wiki/Foo_(bar)).
    [GeneratedRegex(@"\[([^\]]*)\]\(((?:[^()\s]|\([^()\s]*\))+)\)")] private static partial Regex LinkRx();
    [GeneratedRegex(@"https?://[^\s<>""'`，。；、）]+", RegexOptions.IgnoreCase)] private static partial Regex UrlRx();
    [GeneratedRegex(@"#[\p{L}\p{N}_\-]{1,32}")] private static partial Regex TagRx();
}
