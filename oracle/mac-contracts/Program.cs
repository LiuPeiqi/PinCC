using System.Globalization;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PinCC.Core;
using PinCC.UI;

// Reads the production pure functions. No Store, Screens or GUI is instantiated.
// Every clock-sensitive call receives a fixed civil DateTime; JSON uses UTC solely
// as a stable representation of that civil value. Mac tests pass a UTC calendar.
internal static class Program
{
    static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };
    static readonly DateTime Now = new(2026, 10, 10, 16, 27, 31, DateTimeKind.Utc);
    static readonly DateTime Epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    static readonly string[] Bodies =
    [
        "", "  \n\r\n", "标题\n中文内容 😀 #计划", "- [ ] open\n- [x] done\n- [X] DONE",
        "# 标题 **粗体**\r\n\r\n- [ ] 开始\r\n  - 子项\r\n- [x] 完成",
        "```swift\n- [ ] fenced\n```\n- [ ] outside", "```\n\ncode\n\n```", "- [ ]no space\n- [] nope\n- [ ]",
        "https://example.com/a?q=中文\n用户注释", "<https://example.com/路径>\n- [ ] 读文章", "\n \n\"https://example.com\"\r\nnote",
        "HTTPS://example.com/path", "http://x", "https://", "https://example.com/a b", "xhttps://example.com",
        @"C:\Users\me\文档.txt", @"\\server\share\报告.pdf", "file:///C:/Users/me/a%20b.txt", "file:///tmp/a%20b.txt",
        "#Tag_中文-2 body\n#第二", "word#tag\n #tag", "#𐐀 supplementary letter", "#中文 #Plan", "![图](img/hash.png)",
        "**bold *italic* bold** and ~~strike~~ `code`", "[中文](https://en.wikipedia.org/wiki/Foo_(bar))",
        "见https://example.com/a). next", "https://example.com/a(b).", "snake_case _word_ 5 * 3", "\\*literal\\* #tag",
        "> 引用一\r> 引用二\r\r1. 第一\r2) 第二", "- [x] one\n  - detail\n- [X] two\n- [x] three\n- [ ] four",
        "Heading\n- [x] done only", "- [ ] **待办**\n正文", "\t+ [ ] nested\n        - [x] deep", "123. ordered\n1234. paragraph",
        "---\n***\n___\n--- -", "####### seven\n  ## two", "`😀` **中文** [空]()", "note\u0000tail"
    ];

    static int Main(string[] args)
    {
        try
        {
            var root = FindRoot();
            var output = Path.Combine(root, "mac", "contracts", "vectors.jsonl");
            for (int i = 0; i < args.Length; i++)
                if (args[i] == "--output" && ++i < args.Length) output = Path.GetFullPath(args[i]);
                else throw new ArgumentException("Usage: --output <vectors.jsonl>");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var rows = Vectors().Select(v => JsonSerializer.Serialize(v, Json)).ToArray();
            File.WriteAllText(output, string.Join("\n", rows) + "\n", new UTF8Encoding(false));
            var paths = Directory.GetFiles(Path.Combine(root, "src", "Core"), "*.cs", SearchOption.AllDirectories)
                .Concat(new[] { "src/UI/Layout.cs", "src/UI/Native.cs", "src/UI/Palette.cs", "src/PinCC.csproj", "oracle/mac-contracts/Program.cs", "oracle/mac-contracts/PinCC.MacContracts.csproj" }.Select(p => Path.Combine(root, p)))
                .Concat(Directory.GetFiles(Path.Combine(root, "builtin"), "*", SearchOption.AllDirectories).Where(p => Path.GetExtension(p) is ".json" or ".md"))
                .OrderBy(p => Path.GetRelativePath(root, p).Replace('\\', '/'), StringComparer.Ordinal);
            var sources = paths.ToDictionary(p => Path.GetRelativePath(root, p).Replace('\\', '/'), p => SourceHash(p));
            var fingerprint = new { schema = 1, baselineCommit = GitHead(root), generatedBy = "oracle/mac-contracts", normalization = "CRLF-to-LF", schemaVersion = Store.SchemaVersion, vectors = Path.GetFileName(output), vectorsSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(output))).ToLowerInvariant(), fixtureFamilies = rows.Select(line => JsonDocument.Parse(line).RootElement.GetProperty("type").GetString()!).GroupBy(type => type).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()), sources };
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(output)!, "source-fingerprint.json"), JsonSerializer.Serialize(fingerprint, new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
            Console.WriteLine($"Wrote {rows.Length} production C# vectors to {output}; {sources.Count} source fingerprints.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    static string SourceHash(string path)
    {
        var canonical = Encoding.UTF8.GetString(File.ReadAllBytes(path)).Replace("\r\n", "\n");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
    static string GitHead(string root)
    {
        using var git = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD") { WorkingDirectory = root, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
        var head = git.StandardOutput.ReadToEnd().Trim();
        git.WaitForExit();
        if (git.ExitCode != 0) throw new InvalidOperationException("Cannot record the source baseline commit.");
        return head;
    }
    static string FindRoot()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "src", "Core", "Card.cs"))) return dir.FullName;
        throw new DirectoryNotFoundException("Run from the PinCC repository.");
    }
    static object Rect(Px r) => new { x = r.X, y = r.Y, width = r.W, height = r.H };
    static long CivilMs(DateTime d) => (d.Ticks - Epoch.Ticks) / TimeSpan.TicksPerMillisecond;

    static IEnumerable<object> Vectors()
    {
        yield return new { type = "schema", version = Store.SchemaVersion, sql = (string)typeof(Store).GetField("Schema", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!, columns = Card.Columns };
        yield return new { type = "palette", rotation = Card.Rotation, swatches = Palette.All.Select(s => new { name = s.Name, back = Hex(s.Back), strip = Hex(s.Strip), accent = Hex(s.Accent), ink = Hex(s.Ink) }), overdue = Hex(Palette.Overdue), uiAccent = Hex(Palette.UiAccent) };
        foreach (var (left, right) in new[] { ("é", "e\u0301"), ("same", "same"), ("中文", "中文"), ("line\r\n", "line\n"), ("😀", "😀"), ("A", "a") })
            yield return new { type = "storageEquality", left, right, equal = Equals(new Card { Body = left }.Values()[0], new Card { Body = right }.Values()[0]), leftHash = Card.Hash(left), rightHash = Card.Hash(right) };
        for (int i = 0; i < 360; i++)
        {
            var c = new Card { Id = i - 3, Body = Bodies[i % Bodies.Length], CreatedAt = 1_720_000_000_000 + i * 7, UpdatedAt = 1_720_000_000_100 + i * 7, Rev = 1 + i % 5,
                Images = i % 11 == 0 ? ["one.png"] : i % 19 == 0 ? ["one.png", "two.png"] : [], Summary = i % 3 == 0 ? "摘要" : null,
                Color = i % 17 == 0 ? 8 : null, Topmost = i % 2 == 0, Opacity = .3 + i % 8 * .1, Collapsed = i % 7 == 0, Free = i % 5 == 0,
                Mon = i % 4 == 0 ? "screen-id" : null, X = i - 180, Y = 200 - i, W = 180 + i % 9 * 20, H = i % 2 == 0 ? 80 + i % 6 * 30 : null,
                Ord = i % 13 == 0 ? i * .125 : null, RemindAt = i % 4 == 0 ? 1_800_000_000_000 + i : null,
                DoneAt = i % 23 == 0 ? 1_750_000_000_000 : null, DeletedAt = i % 29 == 0 ? 1_760_000_000_000 : null };
            if (i % 3 == 0) c.Meta = new CardMeta { Src = i % 6 == 0 ? c.Src : "https://other.example", Title = i % 9 == 0 ? "Fetched title" : null, TextHash = "EXTRACTEDHASH", SumHash = i % 9 == 0 ? "EXTRACTEDHASH" : Card.Hash("old") }.ToJson();
            yield return new { type = "card", name = $"card-{i:D3}", input = c, expected = new { active = c.Active, firstLine = c.FirstLine, src = c.Src, isLink = c.IsLink, isFile = c.IsFile, imageOnly = c.ImageOnly,
                note = c.Note, noteLine = c.NoteLine, kind = c.Kind, title = c.Title, nextTask = c.NextTask, titleIsTask = c.TitleIsTask, tag = c.Tag,
                done = c.Progress.Done, total = c.Progress.Total, inputHash = c.InputHash, summaryStale = c.SummaryStale, hash = Card.Hash(c.Body), rank = c.Rank,
                colors = new[] { "off", "kind", "tag", "cycle" }.Select(c.ShownColor).ToArray() } };
        }
        foreach (var (text, index) in Bodies.Select((s, i) => (s, i)))
        {
            var blocks = Markdown.Parse(text);
            yield return new { type = "markdown", name = $"markdown-{index:D2}", input = text, expected = new { blocks = blocks.Select(b => new { kind = b.Kind.ToString().ToLowerInvariant(), text = b.Text, line = b.Line, level = b.Level, indent = b.Indent, @checked = b.Checked, marker = b.Marker }),
                inlines = Markdown.Inlines(text).Select(s => new { text = s.Text, fmt = (int)s.Fmt, url = s.Url }), plain = Markdown.Plain(text), taskLines = Markdown.TaskLines(text), foldDone = Markdown.FoldDone(blocks)?.Order().ToArray(),
                toggles = Enumerable.Range(-1, text.Split('\n').Length + 2).Select(line => new { line, toggle = Markdown.ToggleTask(text, line), done = Markdown.SetTask(text, line, true), open = Markdown.SetTask(text, line, false) }),
                added = Markdown.AddTasks(text, "- typed\r\n- [x] already\r\n中文😀\n\n[]"), continued = text.Split('\n').Select(Markdown.ContinueList).ToArray() } };
        }
        string[] times = ["", "   ", "+30m", "2h", "90分钟", "1.5小时", "3天", "2w", "0分钟", "9999999999999999天", "18:30", "16:27", "18点半", "下午3点", "9pm", "12am", "3:30pm", "晚上", "凌晨", "今天", "明天", "今晚", "明早 8点", "后天晚上", "周五", "下周一 10点", "9/25", "9月25日 14:00", "2026-09-25 08:00", "2027/1/2 09:00", "晚上12点", "晚上1点", "夜里2点", "半夜", "24:00", "12:60", "2026/2/30", "明天 nonsense", "星期日 9点", "礼拜一", "９点", "2024-2-29", "2月29日", "2028-2-29", "2026-10-10", "明天 0点", "today", "tomorrow 9am", "tonight", "下周日", "大后天中午"];
        DateTime[] nows = [Now, new(2026, 10, 12, 0, 30, 0, DateTimeKind.Utc), new(2026, 12, 31, 23, 59, 59, DateTimeKind.Utc), new(2024, 2, 29, 4, 59, 59, DateTimeKind.Utc)];
        foreach (var now in nows)
        {
            yield return new { type = "presets", now = CivilMs(now), expected = When.Presets(now).Select(p => new { label = p.Label, at = CivilMs(p.At) }) };
            foreach (var input in times)
            {
                var parsed = When.Parse(input, now);
                yield return new { type = "when", input, now = CivilMs(now), expected = parsed is { } at ? new { at = CivilMs(at), formatted = When.Format(at, now), past = When.IsPast(at, now) } : null };
            }
        }
        var cards = Enumerable.Range(0, 24).Select(i => new Card { Id = i + 1, CreatedAt = i % 7 * 100, Ord = i % 3 == 0 ? i * .25 : null, RemindAt = i % 4 == 0 ? 1000 + i % 3 : null }).ToArray();
        yield return new { type = "order", input = cards, sortedIds = cards.OrderBy(c => c, Comparer<Card>.Create(Card.Compare)).Select(c => c.Id), ranks = Card.Ranks(cards) };
        for (int i = 0; i < 80; i++)
        {
            var area = new Px(i % 2 == 0 ? 0 : -1000, i % 3 == 0 ? -150 : 0, 420 + i % 5 * 160, 300 + i % 7 * 80);
            var hub = new Px(area.Right - 70, area.Y + 12, 44, 44);
            var items = Enumerable.Range(0, 1 + i % 18).Select(n => (W: 180 + n % 4 * 30, H: 90 + n % 5 * 55, PillH: 36)).ToArray();
            var corner = (Corner)(i % 4);
            yield return new { type = "layout", name = $"layout-{i:D2}", area = Rect(area), hub = Rect(hub), corner = corner.ToString(), gap = 12, inset = 16, items = items.Select((s, n) => new { id = n + 1, width = s.W, height = s.H, pillHeight = s.PillH }),
                expected = Layout.Tile(area, items, [hub], 12, 16, corner).Select((s, n) => new { id = n + 1, rect = Rect(s.Rect), collapsed = s.Pill, hidden = s.Hidden }) };
            var moving = new Px(area.X - 90 + i * 13, area.Y + i * 9 - 100, 170 + i % 5 * 100, 110 + i % 7 * 90);
            var obstacles = new[] { hub, new Px(area.X + 120, area.Y + 80, 160, 180) };
            yield return new { type = "geometry", name = $"geometry-{i:D2}", area = Rect(area), rect = Rect(moving), obstacles = obstacles.Select(Rect), gap = 12,
                clamped = Rect(moving.ClampInto(area)), pushed = Rect(Layout.PushOut(moving, obstacles, area, 12)) };
        }
    }
    static string Hex(System.Windows.Media.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}


