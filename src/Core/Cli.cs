using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PinCC.Core;

/// <summary>
/// The text API for people and CLI LLM tools: `PinCC.exe &lt;verb&gt; …`. Same store and journal as the GUI (which picks
/// changes up within a second). One command = one undo group. Exit codes: 0 ok, 1 usage, 2 not found, 3 conflict, 4 busy.
/// </summary>
public static class Cli
{
    public const int Ok = 0, UsageError = 1, NotFound = 2, Conflict = 3, Busy = 4;

    static readonly string[] EditOptions = ["summary", "remind", "done", "undone", "color", "top", "normal", "image", "add-task", "json"];

    /// <summary>The options each verb accepts: anything else is a usage error, so a typo is never silently ignored.</summary>
    static readonly Dictionary<string, string[]> Options = new()
    {
        ["list"] = ["all", "json", "md"],
        ["show"] = ["json"],
        ["add"] = EditOptions,
        ["set"] = [.. EditOptions, "append", "check", "uncheck", "if-rev", "if-input"],
        ["rm"] = ["json"],
        ["log"] = ["json"],
        ["restore"] = ["json"],
        ["skills"] = ["json"],
        ["run"] = ["json"],
    };

    public static int Run(string[] args, TextWriter o, Func<string> readStdin)
    {
        var verb = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
        try
        {
            if (verb is "help" or "--help" or "-h" or "/?") { o.WriteLine(args.Skip(1).Contains("--skill") ? AgentSkill() : Help); return Ok; }
            if (!Options.TryGetValue(verb, out var allowed)) return Fail(o, UsageError, $"未知命令「{args[0]}」。可用：list show add set rm log restore skills run help");
            var (pos, flags) = Split(args.Skip(1));
            if (flags.Keys.FirstOrDefault(k => !allowed.Contains(k)) is { } bad)
                return Fail(o, UsageError, $"{verb} 不支持 --{bad}。可用选项：{string.Join(" ", allowed.Select(x => "--" + x))}");
            bool json = flags.ContainsKey("json");
            using var s = new Store();
            var mode = s.Setting("color.mode", "rotate");
            switch (verb)
            {
                case "list":
                {
                    var cards = flags.ContainsKey("all") ? s.All() : s.Active();
                    cards.Sort(Card.Compare);
                    if (json) o.WriteLine(Json(cards.Select(c => Dto(c, s, mode))));
                    else if (flags.ContainsKey("md")) o.WriteLine(DeskMarkdown(s, cards));
                    else if (cards.Count == 0) o.WriteLine("（没有卡片）");
                    else foreach (var c in cards) o.WriteLine(Line(c));
                    return Ok;
                }
                case "show":
                {
                    var c = Need(s, pos);
                    o.WriteLine(json ? Json(Dto(c, s, mode)) : Md(c, s));
                    return Ok;
                }
                case "add":
                {
                    var body = Text(pos, 0, readStdin, flags) ?? "";
                    if (body.Length == 0 && !flags.ContainsKey("image") && !flags.ContainsKey("add-task")) throw new ArgumentException("add 需要内容：PinCC add \"文字\"、PinCC add -（标准输入）、--add-task 任务或 --image 图片");
                    var change = Changes(s, flags, readStdin, null);
                    var c = new Card { Body = body };
                    change(c);
                    c = s.Insert(Begin(s), c); // fully populated: one row, one journal entry
                    o.WriteLine(json ? Json(Dto(c, s, mode)) : $"已添加 #{c.Id}");
                    return Ok;
                }
                case "set":
                {
                    var c = Need(s, pos);
                    var text = Text(pos, 1, readStdin, flags);
                    long? ifRev = flags.TryGetValue("if-rev", out var r) ? long.Parse(r, CultureInfo.InvariantCulture) : null;
                    var ifInput = flags.GetValueOrDefault("if-input");
                    var change = Changes(s, flags, readStdin, text);
                    var g = Begin(s);
                    c = s.Mutate(g, c.Id, x =>
                    {
                        // Checked inside the write transaction: the input cannot change between this check and the write.
                        if (ifInput != null && !string.Equals(x.InputHash, ifInput, StringComparison.OrdinalIgnoreCase)) throw new InputChangedException(x.InputHash);
                        change(x);
                    }, OpFor(flags, text), ifRev) ?? throw new KeyNotFoundException($"找不到卡片 #{c.Id}");
                    o.WriteLine(json ? Json(Dto(c, s, mode)) : g.Empty ? $"#{c.Id} 没有变化" : $"已更新 #{c.Id}（撤销：PinCC restore {g.Number}）");
                    return Ok;
                }
                case "rm":
                {
                    var c = Need(s, pos);
                    var g = Begin(s);
                    c = s.Mutate(g, c.Id, x => x.DeletedAt ??= When.NowMs, "delete") ?? throw new KeyNotFoundException($"找不到卡片 #{c.Id}");
                    if (json) o.WriteLine(Json(Dto(c, s, mode)));
                    else if (g.Empty) o.WriteLine($"#{c.Id} 已经是删除状态（{When.Format(When.FromMs(c.DeletedAt!.Value), DateTime.Now)} 删除），未改动");
                    else o.WriteLine($"已删除 #{c.Id}（撤销：PinCC restore {g.Number}）");
                    return Ok;
                }
                case "log":
                {
                    var ops = s.Ops(pos.Count > 0 ? int.Parse(pos[0], CultureInfo.InvariantCulture) : 30);
                    if (json) o.WriteLine(Json(ops.Select(p => new { grp = p.Grp, op_id = p.Id, at = Iso(p.At), card = p.CardId, op = p.Name })));
                    else foreach (var p in ops) o.WriteLine($"{p.Grp,6}  {When.FromMs(p.At):MM-dd HH:mm:ss}  #{p.CardId,-5} {OpName(p.Name)}");
                    return Ok;
                }
                case "skills":
                {
                    Skills.Refresh(s.Home);
                    Runners.Refresh(s.Home);
                    if (json)
                    {
                        o.WriteLine(Json(new
                        {
                            skills = Skills.All.Select(k => new
                            {
                                name = k.Name, label = k.Label, description = k.Description, origin = k.Builtin ? "builtin" : k.Origin, scope = k.Scope, on = k.On,
                                src = k.Src, kind = k.Kinds, ext = k.Exts, tag = k.Tag, grants = k.Grants, runner = k.Runner, output = k.Output,
                                context = k.Context.Select(x => x.Run != null ? (object)new { run = x.Run, where = x.Where, timeout = x.Timeout } : new { builtin = x.Builtin }),
                                approved = !k.NeedsApproval || s.Setting("skill.ok." + k.Name) == k.Hash,
                            }),
                            runners = Runners.All.Select(p => new { name = p.Name, label = p.Display, origin = p.Origin, command = p.Program, grants = (p.Grants ?? new()).Keys }),
                            errors = Skills.Errors.Concat(Runners.Errors),
                        }));
                        return Ok;
                    }
                    o.WriteLine($"技能（{Skills.Dir(s.Home)}）：");
                    foreach (var k in Skills.All)
                    {
                        var when = string.Join(" ", new[] { k.Scope == "desk" ? "桌面" : null, k.Src.Count > 0 ? "src=" + string.Join(",", k.Src) : null, k.Kinds.Count > 0 ? "kind=" + string.Join(",", k.Kinds) : null,
                            k.Exts.Count > 0 ? "ext=" + string.Join(",", k.Exts) : null, k.Tag != null ? "#" + k.Tag : null }.OfType<string>());
                        var state = k.NeedsApproval ? s.Setting("skill.ok." + k.Name) == k.Hash ? " · 已确认" : " · 待确认（桌面上第一次运行时）" : "";
                        o.WriteLine($"  {k.Name,-16} {k.Label}（{(k.Builtin ? "内置" : "用户")}）· {string.Join("/", k.On)}{(when.Length > 0 ? " · " + when : "")} · 输出 {k.Output}"
                            + (k.Grants.Count > 0 ? " · 授权 " + string.Join(",", k.Grants) : "") + (k.Runner != null ? " · 执行器 " + k.Runner : "") + state);
                    }
                    o.WriteLine($"执行器（{Runners.Dir(s.Home)}）：");
                    foreach (var p in Runners.All) o.WriteLine($"  {p.Name,-16} {p.Display}（{(p.Origin == "内置" ? "内置" : "用户")}）· 授权 {string.Join(",", new[] { "none" }.Concat((p.Grants ?? new()).Keys).Distinct())}");
                    foreach (var e in Skills.Errors.Concat(Runners.Errors)) o.WriteLine("  ✗ " + e);
                    return Ok;
                }
                case "run":
                {
                    if (pos.Count == 0) throw new ArgumentException("run 需要技能名：PinCC run <技能> [卡片编号]（PinCC skills 列出全部）");
                    Skills.Refresh(s.Home);
                    Runners.Refresh(s.Home);
                    var skill = Skills.Find(pos[0]) ?? throw new KeyNotFoundException($"找不到技能「{pos[0]}」（PinCC skills 列出全部）");
                    var card = skill.Scope == "card" ? Need(s, pos.Skip(1).ToList()) : null;
                    var llm = Llm.FromSettings(s, skill.Runner) ?? throw new InvalidOperationException("没有可用的执行器：" + Llm.Problem(s, skill.Runner));
                    var g = Begin(s);
                    if (skill.Grants.Contains("pincc")) s.Reserve(g);
                    SkillResult res;
                    try
                    {
                        res = SkillRun.Run(new SkillRequest
                        {
                            Skill = skill, Llm = llm, Store = s, Card = card, Desk = skill.Scope == "desk" ? DeskMarkdown(s, s.Active()) : null,
                            Template = skill.Name == "summary" && skill.Builtin ? s.Setting("llm.prompt") : null, Group = skill.Grants.Contains("pincc") ? g.Number : null,
                        }).GetAwaiter().GetResult()!;
                    }
                    catch (Exception e) when (e is InvalidOperationException or TimeoutException or IOException)
                    {
                        s.Release(g);
                        o.WriteLine($"错误：技能 {skill.Name} 失败：" + e.Message); // the run failed, the command was fine: no usage hint
                        return UsageError;
                    }
                    var (_, ok) = SkillRun.Apply(s, g, skill, res, card?.Id);
                    s.Release(g);
                    if (json) { o.WriteLine(Json(new { skill = skill.Name, card = card?.Id, output = skill.Output, written = ok, grp = g.Empty ? (long?)null : g.Number, result = res.Text })); return Ok; }
                    o.WriteLine(res.Text);
                    o.WriteLine();
                    o.WriteLine(skill.Output switch
                    {
                        "summary" or "append" when !ok => "（卡片在运行期间改动了，结果未写入）",
                        "summary" => $"已写入 #{card!.Id} 的摘要（撤销：PinCC restore {g.Number}）",
                        "append" => $"已追加到 #{card!.Id}（撤销：PinCC restore {g.Number}）",
                        "cards" when ok => $"已新建卡片（撤销：PinCC restore {g.Number}）",
                        _ => g.Empty ? "（没有写入）" : $"本次运行的写入可一起撤销：PinCC restore {g.Number}",
                    });
                    return Ok;
                }
                default: // restore
                {
                    if (pos.Count == 0 || !long.TryParse(pos[0], out var grp)) return Fail(o, UsageError, "restore 需要操作组编号（见 PinCC log 第一列）");
                    var ids = s.Restore(grp);
                    if (ids.Count == 0) return Fail(o, NotFound, $"找不到操作组 {grp}");
                    if (json) o.WriteLine(Json(new { restored_grp = grp, cards = ids }));
                    else o.WriteLine($"已恢复 {string.Join("、", ids.Select(i => "#" + i))} 到操作组 {grp} 之前的状态");
                    return Ok;
                }
            }
        }
        catch (RevConflictException e) { return Fail(o, Conflict, e.Message + "，未写入。"); }
        catch (InputChangedException e) { return Fail(o, Conflict, e.Message + "，未写入。"); }
        catch (KeyNotFoundException e) { return Fail(o, NotFound, e.Message.Length > 0 && !e.Message.StartsWith("The given key") ? e.Message : "找不到这张卡片"); }
        catch (Exception e) when (e is ArgumentException or FormatException or OverflowException) { return Fail(o, UsageError, e.Message); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return Fail(o, UsageError, e.Message); }
        catch (InvalidOperationException e) when (e.Message.Contains("(5)") || e.Message.Contains("(6)")) { return Fail(o, Busy, "数据库正忙，请稍后重试"); }
        catch (InvalidOperationException e) { return Fail(o, UsageError, e.Message); }
    }

    /// <summary>
    /// A command's undo group: its own, or — when a skill run started this process (PINCC_GROUP names a reserved run) —
    /// the run's, so everything the model wrote is undone together.
    /// </summary>
    static Group Begin(Store s) =>
        Environment.GetEnvironmentVariable("PINCC_GROUP") is { Length: > 0 } v && long.TryParse(v, out var n) && s.Join(n) is { } g ? g : s.Begin();

    /// <summary>The desk as one markdown document (list --md; what a desk skill reads).</summary>
    public static string DeskMarkdown(Store s, List<Card> cards)
    {
        var sb = new StringBuilder($"# PinCC 卡片（{cards.Count} 张，{DateTime.Now:yyyy-MM-dd HH:mm}）\n");
        foreach (var c in cards) sb.Append('\n').Append(Md(c, s)).Append('\n');
        return sb.ToString().TrimEnd();
    }

    /// <summary>--if-input no longer matches: the note or page changed since the caller read it.</summary>
    sealed class InputChangedException(string? current) : Exception(current == null
        ? "卡片的来源还没有读取，没有 input_hash"
        : $"卡片内容已改变（当前 input_hash={current}）");

    /// <summary>
    /// Resolves the shared add/set flags — stdin, time, color, image — before anything is stored, so a usage error never
    /// leaves a half-made card behind. Returns them as one change (one journal entry).
    /// </summary>
    static Action<Card> Changes(Store s, Dictionary<string, string> f, Func<string> readStdin, string? text)
    {
        if (f.GetValueOrDefault("summary") == "-" && f.GetValueOrDefault("add-task") == "-") throw new ArgumentException("标准输入只能读一次：--summary 和 --add-task 不能都是 -");
        string? summary = f.TryGetValue("summary", out var sv) ? sv == "-" ? Stdin(readStdin, "摘要") : sv : null;
        string? addTask = f.TryGetValue("add-task", out var av) ? av == "-" ? Stdin(readStdin, "任务") : av : null;
        if (addTask != null && addTask.Trim().Length == 0) throw new ArgumentException("--add-task 需要任务文字");
        var check = TaskNumbers(f, "check");
        var uncheck = TaskNumbers(f, "uncheck");
        long? remind = null;
        bool setRemind = f.TryGetValue("remind", out var rv);
        if (setRemind && rv is not ("off" or "clear" or "none" or "清除"))
            remind = When.Parse(rv, DateTime.Now) is { } t ? When.ToMs(t) : throw new ArgumentException($"看不懂时间「{rv}」，例如 \"+30m\"、\"18:30\"、\"明天 9点\"、\"今晚\"、\"9/25 14:00\"");
        int? color = null;
        bool setColor = f.TryGetValue("color", out var cv);
        if (setColor && cv != "auto")
            color = int.TryParse(cv, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n <= 8 ? n : throw new ArgumentException("--color 取 0-8 或 auto");
        string? image = null;
        if (f.TryGetValue("image", out var iv))
        {
            if (!File.Exists(iv)) throw new ArgumentException($"找不到图片文件「{iv}」");
            var bytes = File.ReadAllBytes(iv);
            image = s.SaveImage(bytes, ImageExt(bytes) ?? throw new ArgumentException($"「{iv}」不是图片（支持 PNG、JPEG、GIF、BMP、WebP）"));
        }

        return c =>
        {
            if (text != null) c.Body = f.ContainsKey("append") && c.Body.Length > 0 ? c.Body.TrimEnd() + "\n" + text : text;
            if (image != null) c.Images.Add(image);
            if (check.Count + uncheck.Count > 0)
            {
                // Numbered like the progress counts them; checked in the write transaction, against the body as stored.
                var lines = Markdown.TaskLines(c.Body);
                if (check.Concat(uncheck).FirstOrDefault(n => n > lines.Count) is int bad and > 0)
                    throw new ArgumentException(lines.Count == 0 ? $"#{c.Id} 没有任务" : $"#{c.Id} 只有 {lines.Count} 个任务，没有第 {bad} 个");
                foreach (var n in check) c.Body = Markdown.SetTask(c.Body, lines[n - 1], true);
                foreach (var n in uncheck) c.Body = Markdown.SetTask(c.Body, lines[n - 1], false);
            }
            if (addTask != null) c.Body = Markdown.AddTasks(c.Body, addTask);
            if (summary != null) // after the body: the summary belongs to the text as it is now
            {
                c.Summary = summary.Length == 0 ? null : summary;
                var meta = c.Info ?? new CardMeta();
                meta.SumHash = c.Summary == null ? null : c.InputHash; // any later change of the note/page makes it stale
                c.Meta = meta.ToJson();
            }
            if (setRemind) c.RemindAt = remind;
            if (f.ContainsKey("done")) c.DoneAt ??= When.NowMs;
            if (f.ContainsKey("undone")) { c.DoneAt = null; c.DeletedAt = null; }
            if (setColor) c.Color = color;
            if (f.ContainsKey("top")) c.Topmost = true;
            if (f.ContainsKey("normal")) c.Topmost = false;
        };
    }

    /// <summary>"--check 2" / "--check 1,3": task numbers (1-based, as the progress counts them).</summary>
    static List<int> TaskNumbers(Dictionary<string, string> f, string name)
    {
        if (!f.TryGetValue(name, out var v)) return [];
        var list = new List<int>();
        foreach (var part in v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            list.Add(int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1 ? n : throw new ArgumentException($"--{name} 取任务序号（从 1 起），例如 --{name} 2 或 --{name} 1,3"));
        return list.Count > 0 ? list : throw new ArgumentException($"--{name} 需要任务序号");
    }

    /// <summary>A single-purpose `set` is journaled under its purpose ("摘要", "提醒"…), so `log` reads naturally.</summary>
    static string OpFor(Dictionary<string, string> f, string? text)
    {
        var acts = f.Keys.Where(k => k is not ("json" or "if-rev" or "if-input")).ToList();
        if (text != null || acts.Count != 1) return "edit";
        return acts[0] switch { "summary" => "summary", "remind" => "remind", "done" => "done", "color" => "color", "top" or "normal" => "view", _ => "edit" };
    }

    /// <summary>The format from the file's first bytes (the extension may lie); null when it is not an image.</summary>
    static string? ImageExt(byte[] b)
    {
        bool At(int i, string sig) => b.Length >= i + sig.Length && sig.Select((ch, k) => b[i + k] == ch).All(x => x);
        return At(0, "\u0089PNG") ? ".png" : At(0, "\u00FF\u00D8\u00FF") ? ".jpg" : At(0, "GIF8") ? ".gif" : At(0, "BM") ? ".bmp"
            : At(0, "RIFF") && At(8, "WEBP") ? ".webp" : null;
    }

    static int Fail(TextWriter o, int code, string msg)
    {
        o.WriteLine("错误：" + msg);
        if (code == UsageError) o.WriteLine("用法：PinCC help");
        return code;
    }

    static (List<string> Pos, Dictionary<string, string> Flags) Split(IEnumerable<string> args)
    {
        var pos = new List<string>();
        var flags = new Dictionary<string, string>();
        var list = args.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            var a = list[i];
            if (a.StartsWith("--") && a.Length > 2)
            {
                var name = a[2..].ToLowerInvariant();
                bool takesValue = name is "remind" or "color" or "summary" or "if-rev" or "if-input" or "image" or "add-task" or "check" or "uncheck";
                if (takesValue && i + 1 >= list.Count) throw new ArgumentException($"--{name} 需要一个值");
                flags[name] = takesValue ? list[++i] : "1";
            }
            else pos.Add(a);
        }
        return (pos, flags);
    }

    /// <summary>Positional text, or stdin when it is exactly "-" (stdin is never read implicitly: agents often run with an empty pipe).</summary>
    static string? Text(List<string> pos, int i, Func<string> readStdin, Dictionary<string, string> flags)
    {
        if (pos.Count <= i) return null;
        if (pos[i] != "-") return string.Join(" ", pos.Skip(i));
        if (flags.GetValueOrDefault("summary") == "-" || flags.GetValueOrDefault("add-task") == "-") throw new ArgumentException("标准输入只能读一次：正文、--summary、--add-task 只能有一个是 -");
        return Stdin(readStdin, "正文");
    }

    /// <summary>"-" input. Empty stdin is a mistake (a pipe that produced nothing), never a request to clear.</summary>
    static string Stdin(Func<string> readStdin, string what)
    {
        var t = readStdin().TrimEnd();
        return t.Length > 0 ? t : throw new ArgumentException($"{what}要从标准输入读取，但标准输入是空的，未写入" + (what == "摘要" ? "（清空摘要请用 --summary \"\"）" : ""));
    }

    static Card Need(Store s, List<string> pos)
    {
        if (pos.Count == 0 || !long.TryParse(pos[0].TrimStart('#'), out var id)) throw new ArgumentException("需要卡片编号，例如 PinCC show 12");
        return s.Get(id) ?? throw new KeyNotFoundException($"找不到卡片 #{id}");
    }

    static string Line(Card c)
    {
        var sb = new StringBuilder($"#{c.Id,-4}");
        if (c.DoneAt != null) sb.Append(" [已完成]");
        if (c.DeletedAt != null) sb.Append(" [已删除]");
        if (c.RemindAt is long t) sb.Append(c.Overdue ? " ⏰已到 " : " ⏰").Append(When.Format(When.FromMs(t), DateTime.Now));
        if (c.Progress is { Total: > 0 } k) sb.Append($" [{k.Done}/{k.Total}]");
        if (c.Kind is "link" or "file" or "image") sb.Append($" ({c.Kind})");
        var title = c.Title;
        sb.Append("  ").Append(title.Length > 60 ? title[..60] + "…" : title);
        return sb.ToString();
    }

    /// <summary>A card as a markdown section: what an LLM reads.</summary>
    static string Md(Card c, Store s)
    {
        var sb = new StringBuilder($"## #{c.Id} {c.Title}\n");
        var info = c.SrcInfo;
        if (c.RemindAt is long t) sb.AppendLine($"- 提醒：{When.FromMs(t):yyyy-MM-dd HH:mm}{(c.Overdue ? "（已到期）" : "")}");
        if (c.Src != null) sb.AppendLine($"- 来源：{c.Src}");
        if (info?.Site is { } site) sb.AppendLine($"- 站点：{site}");
        if (c.Progress is { Total: > 0 } k) sb.AppendLine($"- 任务：{k.Done}/{k.Total}");
        if (c.DoneAt is long d) sb.AppendLine($"- 已完成：{When.FromMs(d):yyyy-MM-dd HH:mm}");
        sb.AppendLine($"- 创建：{When.FromMs(c.CreatedAt):yyyy-MM-dd HH:mm} · rev {c.Rev}");
        if (info?.Desc is { Length: > 0 } desc && string.IsNullOrWhiteSpace(c.Summary)) sb.AppendLine($"- 描述：{desc}");
        if (!string.IsNullOrWhiteSpace(c.Note)) sb.AppendLine().AppendLine(c.Note.Trim());
        foreach (var img in c.Images) sb.AppendLine($"![]({s.ResolveImage(img)})");
        if (!string.IsNullOrWhiteSpace(c.Summary))
            sb.AppendLine().AppendLine(c.SummaryStale ? "> 摘要（原文已改动，可能过时）：" : "> 摘要：")
              .AppendLine(string.Join("\n", c.Summary.Trim().Split('\n').Select(l => "> " + l)));
        return sb.ToString().TrimEnd();
    }

    static object Dto(Card c, Store s, string mode) => new
    {
        id = c.Id,
        rev = c.Rev,
        kind = c.Kind,
        title = c.Title,
        src = c.Src,
        body = c.Body,
        images = c.Images.Select(s.ResolveImage).ToList(),
        summary = c.Summary,
        summary_stale = c.SummaryStale,
        input_hash = c.InputHash,
        meta = c.SrcInfo,
        color = new { index = c.ShownColor(mode), auto = c.Color == null },
        topmost = c.Topmost,
        opacity = c.Opacity,
        collapsed = c.Collapsed,
        free = c.Free,
        overdue = c.Overdue,
        progress = new { done = c.Progress.Done, total = c.Progress.Total },
        tasks = Markdown.Parse(c.Body).Where(b => b.Kind == BlockKind.Task).Select((b, i) => new { n = i + 1, done = b.Checked, text = b.Text }).ToList(),
        remind_at = c.RemindAt is long t ? Iso(t) : null,
        done_at = c.DoneAt is long d ? Iso(d) : null,
        deleted_at = c.DeletedAt is long x ? Iso(x) : null,
        created_at = Iso(c.CreatedAt),
        updated_at = Iso(c.UpdatedAt),
    };

    static string Iso(long ms) => When.FromMs(ms).ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);

    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static string Json(object v) => JsonSerializer.Serialize(v, JsonOpts);

    /// <summary>A SKILL.md that teaches any agent to use this CLI (help --skill): drop it into the agent's skills folder.</summary>
    public static string AgentSkill()
    {
        var exe = Environment.ProcessPath ?? "PinCC.exe";
        return $"""
            ---
            name: pincc
            description: 用 PinCC（Pin Carbon Copy，Windows 桌面悬浮卡片）读写用户的待办卡片：钉住文件/网址/笔记、加清单项、写摘要、设提醒。用户说“钉起来 / pin 一下 / 记到卡片 / 桌面待办”时使用。
            ---
            PinCC 的命令行就是它的接口，桌面程序会在 1 秒内反映改动。

            - 程序：`{exe}`（WSL 里：`{Llm.WslPath(exe)}`；加入 PATH 后可直接用 `PinCC.exe`）
            - 先运行 `PinCC.exe help` 看完整用法。常用：
              - `PinCC.exe list --md`：读全部卡片
              - `PinCC.exe add "<网址或文件路径>" `：钉一个网址/文档（摘要由 PinCC 自己生成，不要手写）
              - `PinCC.exe add - --remind "明天 9点" < note.md`：新建带提醒的笔记
              - `PinCC.exe set <id> --add-task "…"`、`--check N`：清单逐项修改
              - `PinCC.exe show <id> --json`，再 `set <id> --summary - --if-input <input_hash>`：写摘要
            - 从 WSL 调用时文件路径给 Windows 路径（`wslpath -w`）。
            - 未经用户要求不要 `rm` 或 `--done`；每次写入都可以 `PinCC.exe log` 找到组号后 `restore` 撤销。
            """;
    }

    public static string OpName(string op) => op switch
    {
        "create" => "新建", "edit" => "编辑", "done" => "完成", "delete" => "删除", "restore" => "恢复", "remind" => "提醒",
        "summary" => "摘要", "meta" => "读取来源", "move" => "移动", "resize" => "调整大小", "view" => "显示设置", "color" => "颜色",
        "tile" => "平铺", "run" => "运行技能", _ => op,
    };

    public const string Help = """
        PinCC（Pin Carbon Copy）— 桌面悬浮卡片（每张卡片 = 一件待办：备忘 / 网址 / 文档 / 图片）。命令行即文本接口，供人和 CLI LLM 工具读写。

        PinCC list [--all] [--json|--md]        列出卡片（提醒最近的在前，其余按新建时间由近及远）；--md 为整篇 Markdown
        PinCC show <id> [--json]                查看一张卡片
        PinCC add <文字|-> [选项]                新建卡片；正文第一行是网址或文件路径时即为链接/文档卡片
        PinCC set <id> [文字|-] [--append] [选项] [--check N] [--uncheck N] [--if-rev N] [--if-input H]
                                                修改卡片。--if-rev：仅当版本仍为 N 时写入；--if-input：仅当摘要的输入
                                                （笔记正文或网页/文档正文，见 input_hash）仍为 H 时写入
                                                --check / --uncheck：勾选 / 取消第 N 个任务（从 1 起，可写 1,3；序号见 show --json 的 tasks）
        PinCC rm <id> [--json]                  删除（可恢复）
        PinCC log [N] [--json]                  最近的操作（第一列为操作组编号）
        PinCC restore <组编号> [--json]          撤销整组操作：该组改过的内容回到之前，其他改动不受影响
        PinCC skills [--json]                   列出技能（数据目录 skills\<名字>\SKILL.md）和执行器（agents\<名字>.json）
        PinCC run <技能> [<id>] [--json]         运行一个技能（桌面范围的技能不需要 id），按技能的 output 写回
        PinCC help [--skill]                    本说明；--skill 输出一份教 agent 使用 PinCC 的 SKILL.md

        选项：--summary <文字|->  --remind <时间|off>  --done | --undone  --color <0-8|auto>  --top | --normal  --image <图片文件>  --json
              --add-task <文字|->  加任务：每行一项，加在最后一个任务之后（没有任务时加在末尾），不用改写整段正文
        时间：+30m  2h  18:30  下午3点  今晚  明早  明天 9点  后天晚上  周五 10:00  下周一  9/25 14:00  2026-10-01 08:00
        "-" 表示从标准输入读取（UTF-8，一次命令只能读一次）；标准输入为空时报错、不写入（清空摘要用 --summary ""）。
        退出码：0 成功 · 1 用法错误（含未知选项）· 2 找不到 · 3 冲突（--if-rev / --if-input 不符，未写入）· 4 数据库忙

        给 LLM 工具的约定：
        - 读全部：PinCC list --md（或 --json，含 rev / kind / src / input_hash / summary_stale / progress 等字段）。
        - 写摘要：先 PinCC show <id> --json 取 input_hash，总结后
            PinCC set <id> --summary - --if-input <input_hash> < summary.md
          期间内容变了则退出码 3、不写入（重新读取再总结）；写入后内容再改动，摘要会标为过时。
          摘要格式：第一行 **一句话结论**，然后 3–6 条 "- " 要点，需要行动的事项用 "- [ ] "。
        - 新建：PinCC add - --remind "明天 9点" < note.md
        - 清单：PinCC set <id> --add-task "买牛奶"；PinCC set <id> --check 2（只改那一行，不会覆盖别处的改动）
        - 未经用户要求不要 rm 或 --done；每次写入都可以用 log / restore 撤销。
        - 由 PinCC 技能启动时（环境变量 PINCC_GROUP 已设置），你的所有写入归入同一个操作组，可一起撤销。
        - 从 WSL 调用 PinCC.exe（经 Windows 路径），不要直接打开 pincc.db。

        在 cmd / PowerShell 里手动运行：PinCC.exe 是窗口程序，控制台不会等它结束（输出会和提示符混在一起），
          请用 start /wait PinCC.exe list（cmd），或在命令后加 | more。
          Windows PowerShell 5.1 读取 UTF-8 输出前先执行 [Console]::OutputEncoding=[Text.UTF8Encoding]::new()，
          向标准输入传 UTF-8 文本前先执行 $OutputEncoding=[Text.UTF8Encoding]::new()。
        数据目录：%LOCALAPPDATA%\PinCC（环境变量 PINCC_HOME 可覆盖）。
        """;
}
