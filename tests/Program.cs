using System.Globalization;
using PinCC.Core;
using PinCC.UI;

int failed = 0, passed = 0;
void Eq<T>(T actual, T expected, string what)
{
    if (EqualityComparer<T>.Default.Equals(actual, expected)) passed++;
    else { failed++; Console.WriteLine($"FAIL {what}: expected <{expected}> got <{actual}>"); }
}
void True(bool cond, string what) => Eq(cond, true, what);

// ---------------- When ----------------
var now = new DateTime(2026, 9, 23, 10, 15, 0); // Wednesday
Eq(When.Parse("+30m", now), now.AddMinutes(30), "rel +30m");
Eq(When.Parse("2h", now), now.AddHours(2), "rel 2h");
Eq(When.Parse("90分钟", now), now.AddMinutes(90), "rel 90分钟");
Eq(When.Parse("1.5小时后", now), now.AddMinutes(90), "rel 1.5小时后");
Eq(When.Parse("3天", now), now.AddDays(3), "rel 3天");
Eq(When.Parse("18:30", now), new DateTime(2026, 9, 23, 18, 30, 0), "time later today");
Eq(When.Parse("9:00", now), new DateTime(2026, 9, 24, 9, 0, 0), "time passed → tomorrow");
Eq(When.Parse("18点半", now), new DateTime(2026, 9, 23, 18, 30, 0), "18点半");
Eq(When.Parse("下午3点", now), new DateTime(2026, 9, 23, 15, 0, 0), "下午3点");
Eq(When.Parse("晚上8点15", now), new DateTime(2026, 9, 23, 20, 15, 0), "晚上8点15");
Eq(When.Parse("9pm", now), new DateTime(2026, 9, 23, 21, 0, 0), "9pm");
Eq(When.Parse("明天", now), new DateTime(2026, 9, 24, 9, 0, 0), "明天 default 9:00");
Eq(When.Parse("后天 14:00", now), new DateTime(2026, 9, 25, 14, 0, 0), "后天 14:00");
Eq(When.Parse("明天下午2点", now), new DateTime(2026, 9, 24, 14, 0, 0), "明天下午2点");
Eq(When.Parse("周五", now), new DateTime(2026, 9, 25, 9, 0, 0), "周五");
Eq(When.Parse("周三", now), new DateTime(2026, 9, 30, 9, 0, 0), "周三 (today 9:00 passed → next week)");
Eq(When.Parse("周三 18:00", now), new DateTime(2026, 9, 23, 18, 0, 0), "周三 18:00 today");
Eq(When.Parse("下周一 10点", now), new DateTime(2026, 9, 28, 10, 0, 0), "下周一 10点");
Eq(When.Parse("下周五", now), new DateTime(2026, 10, 2, 9, 0, 0), "下周五");
Eq(When.Parse("星期日", now), new DateTime(2026, 9, 27, 9, 0, 0), "星期日");
Eq(When.Parse("9/25", now), new DateTime(2026, 9, 25, 9, 0, 0), "9/25");
Eq(When.Parse("9月25日 14:00", now), new DateTime(2026, 9, 25, 14, 0, 0), "9月25日 14:00");
Eq(When.Parse("2027-01-02 08:00", now), new DateTime(2027, 1, 2, 8, 0, 0), "iso");
Eq(When.Parse("1/5", now), new DateTime(2027, 1, 5, 9, 0, 0), "past date → next year");
Eq(When.Parse("tomorrow 3pm", now), new DateTime(2026, 9, 24, 15, 0, 0), "tomorrow 3pm");
Eq(When.Parse("hello", now), null, "garbage");
Eq(When.Parse("13/40", now), null, "invalid date");
Eq(When.Parse("25:00", now), null, "invalid time");
Eq(When.Format(new DateTime(2026, 9, 23, 14, 0, 0), now), "14:00", "fmt today");
Eq(When.Format(new DateTime(2026, 9, 24, 9, 0, 0), now), "明天 09:00", "fmt tomorrow");
Eq(When.Format(new DateTime(2026, 9, 26, 9, 0, 0), now), "周六 09:00", "fmt weekday");
Eq(When.Format(new DateTime(2026, 11, 3, 9, 5, 0), now), "11月3日 09:05", "fmt date");
var presets = When.Presets(now);
Eq(presets[0].At, new DateTime(2026, 9, 23, 11, 15, 0), "preset +1h");
Eq(presets[1].Label, "今天下午", "preset afternoon label");
True(presets.Any(p => p.Label == "明天上午" && p.At == new DateTime(2026, 9, 24, 9, 0, 0)), "preset tomorrow morning");
Eq(presets.Last().At, new DateTime(2026, 9, 28, 9, 0, 0), "preset next monday");
var night = new DateTime(2026, 9, 24, 0, 30, 0); // Thursday, just after midnight
var nightPresets = When.Presets(night);
True(nightPresets.Any(p => p.Label == "今天上午" && p.At == new DateTime(2026, 9, 24, 9, 0, 0)), "before dawn: 今天上午 = today 09:00");
True(nightPresets.All(p => p.Label != "明天上午"), "before dawn: no 明天上午 (it would skip a day)");
Eq(nightPresets.Last().At, new DateTime(2026, 9, 28, 9, 0, 0), "night preset next monday");
// date alone: 09:00, or the next full hour once that has passed today — never a past time
Eq(When.Parse("今天", now), new DateTime(2026, 9, 23, 11, 0, 0), "今天 after 9:00 → next full hour");
Eq(When.Parse("9/23", now), new DateTime(2026, 9, 23, 11, 0, 0), "today's date after 9:00 → next full hour");
Eq(When.Parse("今天", new DateTime(2026, 9, 23, 8, 10, 0)), new DateTime(2026, 9, 23, 9, 0, 0), "今天 before 9:00");
Eq(When.Parse("今天", new DateTime(2026, 9, 23, 23, 30, 0)), new DateTime(2026, 9, 24, 0, 0, 0), "今天 late → next full hour");
Eq(When.Parse("今早", now), new DateTime(2026, 9, 23, 11, 0, 0), "今早 passed → next full hour");
Eq(When.Parse("今天 9点", now), new DateTime(2026, 9, 23, 9, 0, 0), "explicit time today is kept (caller sees IsPast)");
True(When.IsPast(When.Parse("今天 9点", now)!.Value, now), "IsPast");
True(!When.IsPast(now.AddMinutes(1), now), "IsPast future");
// periods: default hours, implied by day words
Eq(When.Parse("今天下午", now), new DateTime(2026, 9, 23, 15, 0, 0), "今天下午 15:00");
Eq(When.Parse("今晚", now), new DateTime(2026, 9, 23, 20, 0, 0), "今晚 20:00");
Eq(When.Parse("今晚8点", now), new DateTime(2026, 9, 23, 20, 0, 0), "今晚8点");
Eq(When.Parse("明早", now), new DateTime(2026, 9, 24, 8, 0, 0), "明早 08:00");
Eq(When.Parse("明早 7点半", now), new DateTime(2026, 9, 24, 7, 30, 0), "明早 7点半");
Eq(When.Parse("明晚", now), new DateTime(2026, 9, 24, 20, 0, 0), "明晚 20:00");
Eq(When.Parse("明天上午", now), new DateTime(2026, 9, 24, 9, 0, 0), "明天上午 09:00");
Eq(When.Parse("明天中午", now), new DateTime(2026, 9, 24, 12, 0, 0), "明天中午 12:00");
Eq(When.Parse("明天早上", now), new DateTime(2026, 9, 24, 8, 0, 0), "明天早上 08:00");
Eq(When.Parse("后天晚上", now), new DateTime(2026, 9, 25, 20, 0, 0), "后天晚上 20:00");
Eq(When.Parse("周五晚上", now), new DateTime(2026, 9, 25, 20, 0, 0), "周五晚上");
Eq(When.Parse("tonight", now), new DateTime(2026, 9, 23, 20, 0, 0), "tonight");
Eq(When.Parse("晚上", now), new DateTime(2026, 9, 23, 20, 0, 0), "bare 晚上");
Eq(When.Parse("凌晨", now), null, "凌晨 needs an hour");
// night hours belong to the night
Eq(When.Parse("晚上12点", now), new DateTime(2026, 9, 24, 0, 0, 0), "晚上12点 = 00:00 next day");
Eq(When.Parse("晚上1点", now), new DateTime(2026, 9, 24, 1, 0, 0), "晚上1点 = 01:00 next day");
Eq(When.Parse("夜里2点半", now), new DateTime(2026, 9, 24, 2, 30, 0), "夜里2点半");
Eq(When.Parse("晚上11点", now), new DateTime(2026, 9, 23, 23, 0, 0), "晚上11点");
Eq(When.Parse("今晚12点", now), new DateTime(2026, 9, 24, 0, 0, 0), "今晚12点");
Eq(When.Parse("明晚1点", now), new DateTime(2026, 9, 25, 1, 0, 0), "明晚1点 = night after tomorrow");
Eq(When.Parse("凌晨3点", now), new DateTime(2026, 9, 24, 3, 0, 0), "凌晨3点 next");
Eq(When.Parse("晚上1点", night), new DateTime(2026, 9, 24, 1, 0, 0), "at 00:30 晚上1点 is still tonight");
Eq(When.Parse("晚上12点", night), new DateTime(2026, 9, 25, 0, 0, 0), "at 00:30 晚上12点 is the coming midnight");
Eq(When.Parse("中午1点", now), new DateTime(2026, 9, 23, 13, 0, 0), "中午1点");
// never throws
Eq(When.Parse("0000-01-01", now), null, "year 0");
Eq(When.Parse("0000/1/1 9:00", now), null, "year 0 with time");
Eq(When.Parse("99999999999999d", now), null, "huge relative span");
Eq(When.Parse("1e400天", now), null, "not a number");
Eq(When.Parse("9999-12-31 晚上12点", now), null, "DateTime overflow");
Eq(When.Parse("１８:３０", now), null, "fullwidth digits");
Eq(When.Parse("2/29", now), null, "no Feb 29 next year");

// ---------------- Markdown ----------------
var md = "# Title\nline one\nline two\n\n- [ ] task a\n- [x] task b\n  - sub\n1. first\n> quote a\n> quote b\n```\ncode\n```\n---\n![](img/abc.png)";
var blocks = Markdown.Parse(md);
Eq(string.Join(",", blocks.Select(b => b.Kind)), "Heading,Paragraph,Task,Task,Bullet,Ordered,Quote,Code,Rule,Image", "block kinds");
Eq(blocks[1].Text, "line one\nline two", "paragraph keeps hard breaks");
Eq(blocks[2].Line, 4, "task line index");
Eq(blocks[3].Checked, true, "task checked");
Eq(blocks[4].Indent, 1, "sub indent");
Eq(blocks[6].Text, "quote a\nquote b", "quote merged");
Eq(blocks[9].Text, "img/abc.png", "image src");
var runs = Markdown.Inlines("a **b** *c* ~~d~~ `e*f` [g](http://x.y) https://z.com/p. #tag 5 * 3 snake_case_name");
True(runs.Any(r => r.Text == "b" && r.Fmt == Fmt.Bold), "bold");
True(runs.Any(r => r.Text == "c" && r.Fmt == Fmt.Italic), "italic");
True(runs.Any(r => r.Text == "d" && r.Fmt == Fmt.Strike), "strike");
True(runs.Any(r => r.Text == "e*f" && r.Fmt == Fmt.Code), "code literal");
True(runs.Any(r => r.Text == "g" && r.Url == "http://x.y"), "link");
True(runs.Any(r => r.Url == "https://z.com/p"), "bare url trims trailing dot");
True(runs.Any(r => r.Url == "#tag"), "tag");
True(string.Concat(runs.Select(r => r.Text)).Contains("5 * 3 snake_case_name"), "literals kept");
Eq(Markdown.ToggleTask("- [ ] a\n- [x] b", 0), "- [x] a\n- [x] b", "toggle on");
Eq(Markdown.ToggleTask("- [ ] a\n- [x] b", 1), "- [ ] a\n- [ ] b", "toggle off");
Eq(Markdown.ContinueList("- [x] done"), "- [ ] ", "continue task");
Eq(Markdown.ContinueList("  * item"), "  * ", "continue bullet");
Eq(Markdown.ContinueList("3. x"), "4. ", "continue ordered");
Eq(Markdown.ContinueList("- "), "", "end list on empty item");
Eq(Markdown.ContinueList("plain"), null, "not a list");
Eq(Markdown.Plain("## **Hello** [w](u)"), "Hello w", "plain");
string? Url(string text) => Markdown.Inlines(text).FirstOrDefault(r => r.Url != null && !r.Url.StartsWith('#'))?.Url;
Eq(Url("见https://x.com/a 继续"), "https://x.com/a", "bare url right after CJK text");
Eq(Url("abchttps://x.com/a"), null, "url glued to an ASCII word is not linked");
Eq(Url("2https://x.com/a"), null, "url glued to a digit is not linked");
Eq(Url("https://en.wikipedia.org/wiki/Foo_(bar)"), "https://en.wikipedia.org/wiki/Foo_(bar)", "balanced parens kept");
Eq(Url("(see https://x.com/a)"), "https://x.com/a", "unbalanced trailing paren trimmed");
Eq(Url("(https://en.wikipedia.org/wiki/Foo_(bar))."), "https://en.wikipedia.org/wiki/Foo_(bar)", "nested: outer paren and dot trimmed");
Eq(Url("看 https://x.com/a。"), "https://x.com/a", "CJK full stop trimmed");
Eq(Url("[w](https://en.wikipedia.org/wiki/Foo_(bar))"), "https://en.wikipedia.org/wiki/Foo_(bar)", "link target with balanced parens");
True(Markdown.Inlines("[w](https://en.wikipedia.org/wiki/Foo_(bar)) x").Any(r => r.Text == "w"), "link text with paren target");
Eq(Url("[w](https://x.y/a) (note)"), "https://x.y/a", "link followed by a parenthesis");
Eq(Markdown.Plain("- [ ] buy milk"), "buy milk", "plain task");

// ---------------- Card (derived state) ----------------
var c1 = new Card { Id = 1, CreatedAt = 100 };
var c2 = new Card { Id = 2, CreatedAt = 200 };
var c3 = new Card { Id = 3, CreatedAt = 50, RemindAt = 5000 };
var c4 = new Card { Id = 4, CreatedAt = 60, RemindAt = 1000 };
var sorted = new List<Card> { c1, c2, c3, c4 };
sorted.Sort(Card.Compare);
Eq(string.Join(",", sorted.Select(c => c.Id)), "4,3,2,1", "sort: reminders soonest first, then newest");
var link = new Card { Body = "https://example.com/a?b=1\n为什么重要：读一下" };
Eq(link.Src, "https://example.com/a?b=1", "src from first line");
Eq(link.Kind, "link", "kind link");
Eq(link.Note, "为什么重要：读一下", "note excludes source line");
Eq(new Card { Body = "\n  C:\\docs\\报告.pdf  \n备注" }.Src, "C:\\docs\\报告.pdf", "file src (existence not required)");
Eq(new Card { Body = "C:\\docs\\a.pdf" }.Kind, "file", "kind file");
Eq(new Card { Body = @"\\server\share\x.docx" }.Kind, "file", "unc path");
Eq(new Card { Body = "file:///C:/x/y.txt" }.Src, "C:\\x\\y.txt", "file uri");
Eq(new Card { Body = "see https://a.b" }.Src, null, "url mid-line is not a source");
Eq(new Card { Images = ["img/a.png"] }.Kind, "image", "image-only");
Eq(new Card { Images = ["img/a.png", "img/b.png"] }.Kind, "image", "pictures without text are an image card");
Eq(new Card { Images = ["img/a.png", "img/b.png"] }.ImageOnly, false, "several pictures render as a gallery, not full-bleed");
Eq(new Card { Images = ["img/a.png"], Body = "caption" }.Kind, "note", "image + text is a note");
Eq(new Card { Body = "- [x] a\n- [ ] b\n* [X] c" }.Progress, (2, 3), "progress");
Eq(new Card { Body = "```\n- [ ] in fence\n- [x] also\n```\n- [x] real\n- [x]nospace" }.Progress, (1, 1), "progress ignores fences and '[x]text'");
Eq(new Card { Body = "```\n- [ ] only in a fence\n```" }.Kind, "note", "fenced tasks don't make a task card");
var prog = new Card { Body = "- [ ] a" };
Eq(prog.Progress, (0, 1), "progress before edit");
prog.Body = "- [x] a";
Eq(prog.Progress, (1, 1), "progress recomputed after body change");
var spaced = new Card { Body = "https://x.com/p\n\n- [ ] a\n- [x] b" };
Eq(spaced.Note, "\n- [ ] a\n- [x] b", "note keeps the blank line after the source");
Eq(spaced.NoteLine, 1, "note line after source");
var noteBlocks = Markdown.Parse(spaced.Note).Where(b => b.Kind == BlockKind.Task).Select(b => b.Line + spaced.NoteLine).ToList();
Eq(Markdown.ToggleTask(spaced.Body, noteBlocks[0]), "https://x.com/p\n\n- [x] a\n- [x] b", "note line + block line = body line");
var lead = new Card { Body = "\n\n  C:\\x\\y.pdf\nnote" };
Eq(lead.NoteLine, 3, "note line skips leading blanks and the source");
Eq(lead.Note, "note", "note after a file source");
Eq(new Card { Body = "plain\ntext" }.NoteLine, 0, "no source: note line 0");
Eq(new Card { Body = "plain\ntext" }.Note, "plain\ntext", "no source: note = body");
Eq(new Card { Body = "https://x.com/p" }.Note, "", "source only: empty note");
Eq(new Card { Body = "https://x.com/p\n\n## 标题\n" }.Title, "标题", "title skips blank note lines");
Eq(new Card { Body = "- [x] a", Summary = "- [ ] ignored" }.Progress, (1, 1), "summary tasks don't count");
Eq(new Card { Body = "\n\n## Hi there\nmore" }.Title, "Hi there", "title");
Eq(new Card { Body = "notes #Work stuff" }.Tag, "work", "tag");
var withMeta = new Card { Body = "https://x.org/p", Meta = new CardMeta { Src = "https://x.org/p", Title = "页面标题", TextHash = "abc", SumHash = "abc" }.ToJson(), Summary = "**s**" };
Eq(withMeta.Title, "页面标题", "title from meta");
Eq(withMeta.SummaryStale, false, "summary fresh");
withMeta.Body = "https://x.org/other";
Eq(withMeta.SrcInfo, null, "meta ignored when source changed");
var note = new Card { Body = "hello", Summary = "s", Meta = new CardMeta { SumHash = Card.Hash("hello") }.ToJson() };
Eq(note.SummaryStale, false, "note summary fresh");
note.Body = "hello!";
Eq(note.SummaryStale, true, "note summary stale after edit");
Eq(new Card { Id = 1 }.AutoColor("rotate") != new Card { Id = 2 }.AutoColor("rotate"), true, "rotation differs for neighbours");
Eq(new Card { Id = 9, Color = 5 }.ShownColor("rotate"), 5, "manual color wins");
Eq(new Card { Body = "https://a.bc/x" }.AutoColor("kind"), 4, "kind color link");
Eq(new Card { Id = 3 }.AutoColor("off"), 0, "off = one color");

// ---------------- Layout ----------------
var work = new Px(0, 0, 1000, 600);
List<Slot> T(IEnumerable<(int, int)> sizes, IReadOnlyList<Px> obs, Corner corner = Corner.TopRight) =>
    Layout.Tile(work, sizes.Select(s => (s.Item1, s.Item2, 36)).ToList(), obs, 10, 20, corner);
var tiles = T([(200, 250), (200, 250), (200, 250)], []);
Eq(tiles[0].Rect, new Px(780, 20, 200, 250), "tile TR first");
Eq(tiles[1].Rect, new Px(780, 280, 200, 250), "tile TR second below");
Eq(tiles[2].Rect, new Px(570, 20, 200, 250), "tile TR third next column");
Eq(T([(200, 250)], [], Corner.TopLeft)[0].Rect, new Px(20, 20, 200, 250), "tile TL");
Eq(T([(200, 250)], [], Corner.BottomLeft)[0].Rect, new Px(20, 330, 200, 250), "tile BL");
var mixed = T([(300, 100), (200, 100)], []);
Eq(mixed[1].Rect, new Px(780, 130, 200, 100), "narrower card aligned to the outer (right) edge");
var hub = new Px(930, 530, 50, 50);
var freeCard = new Px(700, 0, 300, 100);
var withObs = T([(200, 250), (200, 250), (100, 100)], [hub, freeCard]);
foreach (var t in withObs.Select(s => s.Rect))
{
    True(!t.Intersects(hub) && !t.Intersects(freeCard), $"no overlap with obstacles {t}");
    True(t.X >= work.X && t.Right <= work.Right && t.Y >= work.Y && t.Bottom <= work.Bottom, $"inside work {t}");
}
for (int i = 0; i < withObs.Count; i++)
    for (int j = i + 1; j < withObs.Count; j++)
        True(!withObs[i].Rect.Intersects(withObs[j].Rect), $"tiles don't overlap {i},{j}");
var many = T(Enumerable.Repeat((300, 280), 20), []);
Eq(many.Count, 20, "overflow returns a slot per card");
True(many.Take(2).All(s => !s.Pill && !s.Hidden), "most urgent cards stay expanded");
True(many.Any(s => s.Pill), "overflow turns trailing cards into pills");
var shown = many.Where(s => !s.Hidden).Select(s => s.Rect).ToList();
for (int i = 0; i < shown.Count; i++)
    for (int j = i + 1; j < shown.Count; j++)
        True(!shown[i].Intersects(shown[j]), $"overflow never overlaps {i},{j}");
var tooMany = T(Enumerable.Repeat((300, 280), 200), []);
True(tooMany.Any(s => s.Hidden), "beyond pills: hidden (+N)");
Eq(tooMany.Take(20).Count(s => s.Hidden), 0, "earliest (most urgent) never hidden first");
var neg = Layout.Tile(new Px(-2560, -698, 2560, 2840), [(300, 200, 36)], [], 12, 16, Corner.TopRight);
Eq(neg[0].Rect, new Px(-316, -682, 300, 200), "negative-coordinate monitor");
var pushed = Layout.PushOut(new Px(290, 100, 200, 100), [new Px(300, 100, 200, 100)], work, 10);
True(!pushed.Intersects(new Px(300, 100, 200, 100).Inflate(10)), $"push out clears obstacle {pushed}");
Eq(Layout.PushOut(new Px(10, 10, 100, 100), [], work, 10), new Px(10, 10, 100, 100), "no obstacle: unchanged");
True(Layout.PushOut(new Px(950, 580, 100, 100), [], work, 10) is { Right: <= 1000, Bottom: <= 600 }, "clamped into work area");

// ---------------- Store ----------------
var home = Path.Combine(Path.GetTempPath(), "pincc-test-" + Guid.NewGuid().ToString("N")[..8]);
using (var s = new Store(home))
using (var other = new Store(home))
{
    var g1 = s.Begin();
    var a = s.Insert(g1, new Card { Body = "hello" });
    True(a.Id > 0 && a.Rev == 1, "insert assigns id/rev");
    var g2 = s.Begin();
    var a2 = s.Mutate(g2, a.Id, c => c.Body = "hello world", "edit")!;
    Eq(a2.Rev, 2L, "rev increments");
    s.Mutate(g2, a.Id, c => c.Body = "hello world!", "edit"); // same group: journaled once
    Eq(s.Ops(10, a.Id).Count, 2, "one journal entry per card per group");
    var unchanged = s.Mutate(s.Begin(), a.Id, c => c.Body = "hello world!", "edit")!;
    Eq(unchanged.Rev, 3L, "no-op writes nothing");
    Eq(s.Ops(10, a.Id).Count, 2, "no-op not journaled");
    // concurrent writers touch different columns: neither is lost
    other.Mutate(other.Begin(), a.Id, c => c.RemindAt = 12345, "remind");
    s.Mutate(s.Begin(), a.Id, c => c.Opacity = 0.5, "view");
    var both = s.Get(a.Id)!;
    True(both.RemindAt == 12345 && both.Opacity == 0.5, "column-diffed writes keep both changes");
    var threw = false;
    try { s.Mutate(s.Begin(), a.Id, c => c.Body = "x", "edit", ifRev: 1); } catch (RevConflictException) { threw = true; }
    True(threw, "ifRev conflict detected");
    var g3 = s.Begin();
    s.Mutate(g3, a.Id, c => c.DoneAt = When.NowMs, "done");
    Eq(s.Active().Count, 0, "done leaves desk");
    s.Restore(g3.Number);
    Eq(s.Get(a.Id)!.DoneAt, null, "restore group undoes done");
    s.Restore(g2.Number);
    Eq(s.Get(a.Id)!.Body, "hello", "restore group back to before the edit session");
    s.Restore(g1.Number);
    True(s.Get(a.Id)!.DeletedAt != null, "undo create = delete");
    // multi-card group
    var gb = s.Begin();
    var b = s.Insert(s.Begin(), new Card { Body = "b" });
    var c = s.Insert(s.Begin(), new Card { Body = "c" });
    s.Mutate(gb, b.Id, x => x.Free = true, "tile");
    s.Mutate(gb, c.Id, x => x.Free = true, "tile");
    var ids = s.Restore(gb.Number);
    True(ids.Contains(b.Id) && ids.Contains(c.Id) && !s.Get(b.Id)!.Free && !s.Get(c.Id)!.Free, "group restore covers every card");
    var ov = other.DataVersion;
    s.Insert(s.Begin(), new Card { Body = "x" });
    True(other.DataVersion != ov, "other connection sees data_version change");
    s.SetSetting("k", "v"); Eq(other.Setting("k"), "v", "settings shared");
    var img = s.SaveImage([1, 2, 3]);
    True(File.Exists(s.ResolveImage(img)), "image saved");
    var withImg = s.Insert(s.Begin(), new Card { Images = [img] });
    Eq(s.Get(withImg.Id)!.Images.Single(), img, "images roundtrip");
    s.Purge(30);
    True(File.Exists(s.ResolveImage(img)), "referenced image survives purge");
    var text = "引号 \" and 'quote' 和 emoji 😀";
    var u = s.Insert(s.Begin(), new Card { Body = text, H = null });
    Eq(other.Get(u.Id)!.Body, text, "unicode roundtrip");
    Eq(other.Get(u.Id)!.H, null, "auto height null roundtrip");
    s.Forget(u.Id);
    Eq(s.Get(u.Id), null, "forget removes row");
    Eq(s.Ops(100, u.Id).Count, 0, "forget removes history");
    s.Backup();
    True(Directory.GetFiles(Path.Combine(home, "backup")).Length == 1, "daily backup written");
}
try { Directory.Delete(home, true); } catch { }

// ---------------- Store: column-scoped undo, concurrent writers, group bookkeeping ----------------
var home2 = Path.Combine(Path.GetTempPath(), "pincc-test-" + Guid.NewGuid().ToString("N")[..8]);
using (var s = new Store(home2))
using (var cli = new Store(home2))
{
    var card = s.Insert(s.Begin(), new Card { Body = "note" });
    // undoing a reminder must not take a later, unrelated CLI summary with it
    var gRemind = s.Begin();
    s.Mutate(gRemind, card.Id, c => c.RemindAt = 1000, "remind");
    cli.Mutate(cli.Begin(), card.Id, c => c.Summary = "cli summary", "summary");
    Eq(s.Ops(10, card.Id).Single(o => o.Grp == gRemind.Number).Cols, "remind_at", "op records the columns it changed");
    s.Restore(gRemind.Number);
    var after = s.Get(card.Id)!;
    True(after.RemindAt == null && after.Summary == "cli summary", "undo reminder keeps later summary");
    Eq(s.Ops(1, card.Id)[0].Cols, "remind_at", "the restore itself is column-scoped");

    // an edit session that outlives a concurrent CLI change journals again: the CLI text stays in history
    var gEdit = s.Begin();
    s.Mutate(gEdit, card.Id, c => c.Body = "note v1", "edit");
    s.Mutate(gEdit, card.Id, c => { c.Body = "note v2"; c.Opacity = 0.9; }, "edit");
    Eq(s.Ops(100, card.Id).Count(o => o.Grp == gEdit.Number), 1, "uninterrupted session: one entry");
    Eq(s.Ops(100, card.Id).Single(o => o.Grp == gEdit.Number).Cols, "body,opacity", "entry columns are the union of the session's writes");
    cli.Mutate(cli.Begin(), card.Id, c => c.Body = "cli text", "edit");
    s.Mutate(gEdit, card.Id, c => c.Body = "note v3", "edit"); // the autosave overwrites the CLI text…
    var editOps = s.Ops(100, card.Id).Where(o => o.Grp == gEdit.Number).ToList();
    Eq(editOps.Count, 2, "rejournaled after a concurrent change");
    Eq(Card.FromJson(editOps[0].Before!).Body, "cli text", "…but the CLI text is kept in history");
    s.Restore(gEdit.Number);
    var undone = s.Get(card.Id)!;
    True(undone.Body == "note" && undone.Opacity == 1 && undone.Summary == "cli summary", "restore ends at the earliest snapshot, only its columns");

    // derived data: stored, no history
    var gMeta = s.Begin();
    s.Mutate(gMeta, card.Id, c => c.Meta = "{}", "meta", journal: false);
    True(gMeta.Empty && s.Get(card.Id)!.Meta == "{}", "journal: false writes without an undo entry");

    // a transaction that fails after journaling leaves the group as it was
    var gFail = s.Begin();
    s.Db.Script("CREATE TEMP TRIGGER boom BEFORE UPDATE OF grp ON ops BEGIN SELECT RAISE(ABORT, 'boom'); END;");
    var failed1 = false;
    try { s.Mutate(gFail, card.Id, c => c.Body = "never", "edit"); } catch (InvalidOperationException) { failed1 = true; }
    s.Db.Script("DROP TRIGGER boom;");
    True(failed1 && gFail.Empty && gFail.Cards.Count == 0 && s.Get(card.Id)!.Body == "note", "failed write rolls back the group");
    s.Mutate(gFail, card.Id, c => c.Body = "after", "edit");
    True(!gFail.Empty && s.Ops(100, card.Id).Count(o => o.Grp == gFail.Number) == 1, "group usable after a rollback");

    // group numbers are never reused, even after history is purged
    var gLast = s.Begin();
    s.Mutate(gLast, card.Id, c => c.Opacity = 0.7, "view");
    s.Db.Exec("DELETE FROM ops");
    var gNew = s.Begin();
    s.Mutate(gNew, card.Id, c => c.Opacity = 0.8, "view");
    True(gNew.Number > gLast.Number, "AUTOINCREMENT group ids after purge");
    Eq(s.Restore(gLast.Number).Count, 0, "a purged group restores nothing");

    // opening an existing database writes nothing (read-only CLI commands)
    var dv = cli.DataVersion;
    using (var reader = new Store(home2)) { reader.Active(); reader.Setting("color.mode"); }
    Eq(cli.DataVersion, dv, "no write on read-only open");
}
// a database from a pre-release build (journal without per-op columns) is upgraded in place
using (var s = new Store(home2))
{
    s.Db.Script("DROP TABLE ops; CREATE TABLE ops(id INTEGER PRIMARY KEY, grp INTEGER NOT NULL, at INTEGER NOT NULL, card_id INTEGER NOT NULL, op TEXT NOT NULL, before TEXT);");
    var old = s.All().First();
    old.Body = "legacy";
    s.Db.Exec("INSERT INTO ops(id,grp,at,card_id,op,before) VALUES(900,900,1,?,'edit',?)", old.Id, old.ToJson());
}
using (var s = new Store(home2))
{
    Eq(s.Ops(1)[0].Cols, null, "pre-release op kept");
    var id = s.Ops(1)[0].CardId;
    s.Restore(900);
    Eq(s.Get(id)!.Body, "legacy", "entries without cols restore the whole row");
    True(s.Ops(1)[0].Grp > 900, "upgraded journal continues numbering");
}
try { Directory.Delete(home2, true); } catch { }

// ---------------- CLI ----------------
var cliHome = Path.Combine(Path.GetTempPath(), "pincc-test-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(cliHome);
Environment.SetEnvironmentVariable("PINCC_HOME", cliHome);
(int Code, string Out) Cmd(string stdin, params string[] a)
{
    var w = new StringWriter();
    var code = Cli.Run(a, w, () => stdin);
    return (code, w.ToString());
}
var unknown = Cmd("", "add", "hello", "--bogus");
True(unknown.Code == 1 && unknown.Out.Contains("--summary") && unknown.Out.Contains("--image"), "unknown flag → exit 1 listing valid options");
Eq(Cmd("", "list", "--remind", "x").Code, 1, "flag not valid for this verb");
Eq(Cmd("", "set", "1", "--summary").Code, 1, "flag without its value is a usage error, not a crash");
Eq(Cmd("", "frobnicate").Code, 1, "unknown verb");
Eq(Cmd("", "add", "x", "--remind", "whenever").Code, 1, "bad time");
Eq(Cmd("", "add", "x", "--color", "12").Code, 1, "bad color");
Eq(Cmd("", "add", "x", "--image", Path.Combine(cliHome, "missing.png")).Code, 1, "missing image");
var notImage = Path.Combine(cliHome, "a.png");
File.WriteAllText(notImage, "just text");
Eq(Cmd("", "add", "x", "--image", notImage).Code, 1, "not an image");
Eq(Cmd("", "add", "-").Code, 1, "add - with empty stdin");
using (var s = new Store()) Eq(s.All().Count, 0, "usage errors leave no card behind");
var png = Path.Combine(cliHome, "p.bin");
File.WriteAllBytes(png, [0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10]);
Eq(Cmd("", "add", "note body", "--summary", "S", "--remind", "明天", "--image", png).Code, 0, "add with every flag");
long cid;
using (var s = new Store())
{
    var c = s.All().Single();
    cid = c.Id;
    True(c.Summary == "S" && c.RemindAt != null && c.Images.Single().EndsWith(".png"), "add stores a fully populated card");
    Eq(s.Ops(10, c.Id).Select(o => o.Name).Single(), "create", "add = one journal entry");
    Eq(c.Info!.SumHash, c.InputHash, "CLI summary records its input hash");
}
var cidText = cid.ToString(CultureInfo.InvariantCulture);
Eq(Cmd("", "set", cidText, "-").Code, 1, "set - with empty stdin");
Eq(Cmd(" \n", "set", cidText, "--summary", "-").Code, 1, "--summary - with blank stdin");
Eq(Cmd("", "set", cidText, "--summary", "x", "--if-input", "ABC").Code, 3, "--if-input mismatch → conflict");
using (var s = new Store()) Eq(s.Get(cid)!.Summary, "S", "conflict writes nothing");
var shownJson = Cmd("", "show", cidText, "--json");
string hash;
using (var s = new Store()) hash = s.Get(cid)!.InputHash!;
True(shownJson.Out.Contains($"\"input_hash\": \"{hash}\""), "json exposes input_hash");
Eq(Cmd("fresh summary", "set", cidText, "--summary", "-", "--if-input", hash.ToLowerInvariant()).Code, 0, "--if-input match");
Eq(Cmd("", "set", cidText, "edited body").Code, 0, "edit body");
using (var s = new Store())
{
    var c = s.Get(cid)!;
    True(c.Summary == "fresh summary" && c.SummaryStale, "a later edit makes the CLI summary stale");
    Eq(string.Join(",", s.Ops(10, cid).Select(o => o.Name)), "edit,summary,create", "single-purpose set is journaled under its purpose");
}
Eq(Cmd("", "set", cidText, "--summary", "").Code, 0, "--summary \"\" clears");
using (var s = new Store()) Eq(s.Get(cid)!.Summary, null, "summary cleared");
Eq(Cmd("", "rm", cidText).Code, 0, "rm");
var rmAgain = Cmd("", "rm", cidText);
True(rmAgain.Code == 0 && rmAgain.Out.Contains("已经是删除状态") && !rmAgain.Out.Contains("restore"), "rm of a deleted card: says so, no restore hint");
Eq(Cmd("", "restore", "999999").Code, 2, "restore unknown group");
Eq(Cmd("", "show", "424242").Code, 2, "show missing card");
using (var watch = new Store())
{
    var v = watch.DataVersion;
    Cmd("", "list", "--all", "--json");
    Cmd("", "show", cidText, "--json");
    Cmd("", "log", "5");
    Eq(watch.DataVersion, v, "list/show/log write nothing");
}
Environment.SetEnvironmentVariable("PINCC_HOME", null);
try { Directory.Delete(cliHome, true); } catch { }

// ---------------- Fetch ----------------
Eq(Fetch.SafeToAuto("https://github.com/dotnet/wpf"), true, "public url auto ok");
Eq(Fetch.SafeToAuto("http://localhost:8080/x"), false, "localhost not auto");
Eq(Fetch.SafeToAuto("http://192.168.1.5/admin"), false, "private ip not auto");
Eq(Fetch.SafeToAuto("https://intranet/x"), false, "single-label not auto");
Eq(Fetch.SafeToAuto("https://example.com/reset?token=abc"), false, "token link not auto");
var html = "<html><head><title>标题 - 站点</title><meta property=\"og:site_name\" content=\"站点\"><meta name=\"description\" content=\"描述\"></head>" +
           "<body><nav>菜单</nav><article><h1>大标题</h1><p>" + string.Concat(Enumerable.Repeat("正文内容。", 120)) + "</p></article><footer>版权</footer></body></html>";
var page = Fetch.FromHtml("https://example.com/a", html);
Eq(page.Title, "标题", "title strips site suffix");
Eq(page.Desc, "描述", "meta description");
True(page.Text.Contains("# 大标题") && !page.Text.Contains("菜单") && !page.Text.Contains("版权"), "main text without chrome");
var wx = "<html><body><div id=\"js_content\" style=\"visibility:hidden\"><p>" + string.Concat(Enumerable.Repeat("微信正文。", 100)) + "</p></div><div>推荐阅读</div></body></html>";
True(Fetch.FromHtml("https://mp.weixin.qq.com/s/x", wx).Text is var wt && wt.Contains("微信正文") && !wt.Contains("推荐阅读"), "wechat container");
var ld = "<html><head><script type=\"application/ld+json\">{\"@graph\":[{\"@type\":\"Article\",\"headline\":\"LD标题\",\"articleBody\":\"" + string.Concat(Enumerable.Repeat("结构化正文。", 80)) + "\"}]}</script></head><body>x</body></html>";
var ldp = Fetch.FromHtml("https://e.com/", ld);
True(ldp.Title == "LD标题" && ldp.Text.StartsWith("结构化正文"), "json-ld headline/articleBody");
Eq(Fetch.Text(System.Text.Encoding.UTF8.GetBytes("中文")), "中文", "utf8 decode");
System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
Eq(Fetch.Text(System.Text.Encoding.GetEncoding(936).GetBytes("中文GBK")), "中文GBK", "gbk fallback");

failed += FetchTests.Run();

Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
