using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PinCC.Core;

/// <summary>A step that gathers text before the model runs: a program PinCC runs itself, or a built-in reader.</summary>
public sealed record SkillStep(string[]? Run, string? Builtin, string Where, int Timeout);

/// <summary>
/// One skill (skills/&lt;name&gt;/SKILL.md, the Claude skill format): front matter says when it applies, what text to
/// gather (context), what the model may do itself (grants), which runner and where the answer goes (output); the body
/// is the prompt. Values are plain strings or JSON (JSON is valid YAML), so any YAML reader agrees with this one.
/// </summary>
public sealed partial class Skill
{
    public string Name { get; private init; } = "";
    public string Label { get; private init; } = "";
    public string Description { get; private init; } = "";
    public string Body { get; private init; } = "";
    /// <summary>The whole SKILL.md: its hash is what the user approved.</summary>
    public string Text { get; private init; } = "";
    /// <summary>"builtin" or the skill's folder.</summary>
    public string Origin { get; private init; } = "builtin";
    public string? Runner { get; private init; }
    public List<string> Src { get; } = [];
    public List<string> Kinds { get; } = [];
    public List<string> Exts { get; } = [];
    public string? Tag { get; private set; }
    public string Scope { get; private set; } = "card";
    public List<string> On { get; } = [];
    public List<SkillStep> Context { get; } = [];
    public List<string> Grants { get; } = [];
    public string Output { get; private set; } = "summary";

    public bool Builtin => Origin == "builtin";
    public string Hash => Card.Hash(Text);
    /// <summary>A user skill that runs programs or lets the model act: the user confirms it once (again after any edit).</summary>
    public bool NeedsApproval => !Builtin && (Context.Any(s => s.Run != null) || Grants.Count > 0);
    public bool Reads(string builtin) => Context.Any(s => s.Builtin == builtin);
    /// <summary>More specific conditions win the ✨ and the automatic run: source 4, extension 3, tag 2, kind 1.</summary>
    public int Specificity => (Src.Count > 0 ? 4 : 0) + (Exts.Count > 0 ? 3 : 0) + (Tag != null ? 2 : 0) + (Kinds.Count > 0 ? 1 : 0);

    /// <summary>Whether this card-scope skill applies to <paramref name="c"/> (every condition in when holds).</summary>
    public bool Matches(Card c)
    {
        if (Scope != "card") return false;
        if (Src.Count > 0 && !(c.Src is { } src && Src.Any(g => Glob(g).IsMatch(src)))) return false;
        if (Kinds.Count > 0 && !Kinds.Contains(c.Kind)) return false;
        if (Exts.Count > 0 && !(c.Src is { } s2 && Exts.Contains(Ext(c, s2), StringComparer.OrdinalIgnoreCase))) return false;
        if (Tag != null && !Regex.IsMatch(c.Body, @"(?:^|\s)#" + Regex.Escape(Tag) + @"(?![\p{L}\p{N}_\-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return false;
        return true;
    }

    static string Ext(Card c, string src)
    {
        if (!c.IsLink) return Path.GetExtension(src);
        return Uri.TryCreate(src, UriKind.Absolute, out var u) ? Path.GetExtension(u.AbsolutePath) : "";
    }

    static Regex Glob(string g) => new("^" + Regex.Escape(g).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // ================= parsing =================

    /// <summary>Parses a SKILL.md; throws FormatException with a reason a person can act on.</summary>
    public static Skill Parse(string text, string name, string origin)
    {
        text = text.Replace("\r\n", "\n").TrimStart('﻿');
        var (front, body) = FrontMatter(text);
        string? Str(string key) => front.TryGetValue(key, out var v) ? Scalar(v, key) : null;
        var skillName = Str("name") ?? throw new FormatException("缺少 name");
        var skill = new Skill
        {
            Name = skillName,
            Label = Str("label") is { Length: > 0 } label ? label : skillName,
            Description = Str("description") ?? throw new FormatException("缺少 description"),
            Body = body.Trim('\n'),
            Text = text,
            Origin = origin,
            Runner = Str("runner"),
        };
        if (skill.Name != name) throw new FormatException($"name 必须与文件夹名一致（{name}）");
        if (!RunnerProfile.NameRx().IsMatch(skill.Name)) throw new FormatException("name 只能用小写字母、数字、. _ -");
        if (skill.Runner != null && skill.Runner != "custom" && !RunnerProfile.NameRx().IsMatch(skill.Runner)) throw new FormatException("runner 不是有效的执行器名");

        if (front.TryGetValue("when", out var when))
        {
            using var doc = Json(when, "when");
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new FormatException("when 要写成 JSON 对象，例如 {\"src\": \"https://*.example.com/*\"}");
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                switch (p.Name)
                {
                    case "src": skill.Src.AddRange(Strings(p.Value, "when.src")); break;
                    case "kind":
                        skill.Kinds.AddRange(Strings(p.Value, "when.kind"));
                        if (skill.Kinds.FirstOrDefault(k => k is not ("note" or "task" or "link" or "file" or "image")) is { } k) throw new FormatException($"when.kind 不认识 {k}（可用 note task link file image）");
                        break;
                    case "ext": skill.Exts.AddRange(Strings(p.Value, "when.ext").Select(e => e.StartsWith('.') ? e : "." + e)); break;
                    case "tag": skill.Tag = Strings(p.Value, "when.tag").FirstOrDefault()?.TrimStart('#'); break;
                    case "scope":
                        skill.Scope = Strings(p.Value, "when.scope").FirstOrDefault() ?? "card";
                        if (skill.Scope is not ("card" or "desk")) throw new FormatException("when.scope 取 card 或 desk");
                        break;
                    case "on":
                        skill.On.AddRange(Strings(p.Value, "when.on"));
                        if (skill.On.FirstOrDefault(o => o is not ("ask" or "create" or "menu")) is { } o) throw new FormatException($"when.on 不认识 {o}（可用 ask create menu）");
                        break;
                    default: throw new FormatException($"when 里不认识 {p.Name}（可用 src kind ext tag scope on）");
                }
            }
        }
        if (skill.On.Count == 0) skill.On.Add("ask");

        if (front.TryGetValue("context", out var context))
        {
            using var doc = Json(context, "context");
            if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new FormatException("context 要写成 JSON 数组");
            foreach (var step in doc.RootElement.EnumerateArray()) skill.Context.Add(Step(step));
        }
        if (skill.Context.Count == 0) skill.Context.Add(new SkillStep(null, skill.Scope == "desk" ? "desk" : "note", "runner", 60));

        if (front.TryGetValue("grants", out var grants))
            foreach (var g in grants.TrimStart().StartsWith('[') ? Strings(Json(grants, "grants").RootElement, "grants") : [Scalar(grants, "grants")])
            {
                if (g.Length == 0) continue;
                if (!(g is "none" or "web" or "read" or "pincc" || g.StartsWith("shell:") && PrefixRx().IsMatch(g["shell:".Length..].Trim())))
                    throw new FormatException($"grants 不认识 {g}（可用 web read pincc shell:<命令前缀>；前缀不能含括号、引号和 $ ` ; & | < > * ? % !）");
                if (g != "none") skill.Grants.Add(g);
            }

        skill.Output = Str("output") ?? (skill.Scope == "desk" ? "cards" : "summary");
        if (skill.Output is not ("summary" or "append" or "cards" or "none")) throw new FormatException("output 取 summary append cards none");
        if (skill.Scope == "desk" && skill.Output is "summary" or "append") throw new FormatException("桌面范围的技能没有“这张卡片”，output 只能是 cards 或 none");
        if (skill.Body.Trim().Length == 0) throw new FormatException("正文（提示词）是空的");
        return skill;
    }

    static SkillStep Step(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new FormatException("context 的每一项要写成 JSON 对象");
        string[]? run = null;
        string? builtin = null;
        string where = "runner";
        int timeout = 60;
        foreach (var p in e.EnumerateObject())
        {
            switch (p.Name)
            {
                case "run":
                    run = Strings(p.Value, "context.run").ToArray();
                    if (run.Length == 0 || run[0].Trim().Length == 0) throw new FormatException("context.run 至少要有程序名");
                    break;
                case "builtin":
                    builtin = Strings(p.Value, "context.builtin").FirstOrDefault();
                    if (builtin is not ("source" or "note" or "desk")) throw new FormatException("context.builtin 取 source note desk");
                    break;
                case "where":
                    where = Strings(p.Value, "context.where").FirstOrDefault() ?? "runner";
                    if (where is not ("runner" or "windows" or "wsl")) throw new FormatException("context.where 取 runner windows wsl");
                    break;
                case "timeout":
                    timeout = p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var t) && t is > 0 and <= 600 ? t : throw new FormatException("context.timeout 取 1–600 秒");
                    break;
                default: throw new FormatException($"context 里不认识 {p.Name}（可用 run builtin where timeout）");
            }
        }
        if ((run == null) == (builtin == null)) throw new FormatException("context 的每一项要么是 run，要么是 builtin");
        return new SkillStep(run, builtin, where, timeout);
    }

    /// <summary>"---" front matter: `key: value` lines; a value may continue on indented lines (multi-line JSON).</summary>
    static (Dictionary<string, string> Front, string Body) FrontMatter(string text)
    {
        if (!text.StartsWith("---\n")) throw new FormatException("SKILL.md 要以 --- 开头的元数据开始");
        int end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
        while (end >= 0 && !(end + 4 == text.Length || text[end + 4] == '\n')) end = text.IndexOf("\n---", end + 1, StringComparison.Ordinal);
        if (end < 0) throw new FormatException("元数据没有用 --- 结束");
        var front = new Dictionary<string, string>();
        string? key = null;
        foreach (var line in text[4..end].Split('\n'))
        {
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#')) continue;
            if (char.IsWhiteSpace(line[0]) && key != null) { front[key] += "\n" + line; continue; }
            var m = KeyRx().Match(line);
            if (!m.Success) throw new FormatException("看不懂这一行元数据：" + line);
            key = m.Groups[1].Value;
            front[key] = m.Groups[2].Value;
        }
        return (front, end + 4 >= text.Length ? "" : text[(end + 5)..]);
    }

    static JsonDocument Json(string value, string key)
    {
        try { return JsonDocument.Parse(value, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException e) { throw new FormatException($"{key} 的 JSON 写错了：{e.Message}"); }
    }

    static string Scalar(string value, string key)
    {
        var v = value.Trim();
        if (v.Length >= 2 && (v[0] == '"' && v[^1] == '"'))
        {
            using var doc = Json(v, key);
            return doc.RootElement.GetString() ?? "";
        }
        if (v.Length >= 2 && v[0] == '\'' && v[^1] == '\'') return v[1..^1].Replace("''", "'");
        return v;
    }

    static List<string> Strings(JsonElement e, string key) => e.ValueKind switch
    {
        JsonValueKind.String => [e.GetString()!],
        JsonValueKind.Array => e.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : throw new FormatException($"{key} 里要写字符串")).ToList(),
        _ => throw new FormatException($"{key} 要写字符串或字符串数组"),
    };

    [GeneratedRegex(@"^([A-Za-z][\w-]*):\s?(.*)$")] private static partial Regex KeyRx();
    [GeneratedRegex(@"^[\w .+/\\:=@,~-]{1,200}$")] private static partial Regex PrefixRx();
}

/// <summary>
/// The skills in effect: built-in (builtin/skills) then the user's (data folder skills/&lt;name&gt;/SKILL.md, same name
/// wins). Broken files are left out and reported in <see cref="Errors"/>.
/// </summary>
public static class Skills
{
    static List<Skill>? _all;
    static Skill? _summary;
    static List<string> _errors = [];
    static string? _home, _signature;
    static readonly object Lock = new();

    /// <summary>The loaded set (read on first use; <see cref="Refresh"/> picks up file changes — the desk does every few seconds).</summary>
    public static IReadOnlyList<Skill> All { get { if (_all == null) Refresh(); return _all!; } }
    public static IReadOnlyList<string> Errors { get { if (_all == null) Refresh(); return _errors; } }
    public static Skill? Find(string name) => All.FirstOrDefault(s => s.Name == name);

    /// <summary>The shipped summary skill (its prompt is the default template, whatever a user file overrides).</summary>
    public static Skill BuiltinSummary
    {
        get
        {
            if (_summary != null) return _summary;
            var text = BuiltinFiles.Under("skills/summary/").First(kv => kv.Key.EndsWith("SKILL.md", StringComparison.OrdinalIgnoreCase)).Value;
            return _summary = Skill.Parse(text, "summary", "builtin");
        }
    }

    /// <summary>The summary skill in effect (a user file named "summary" replaces the built-in one).</summary>
    public static Skill Summary => Find("summary") ?? BuiltinSummary;

    public static string Dir(string? home = null) => Path.Combine(home ?? Store.DefaultHome, "skills");

    public static bool Refresh(string? home = null)
    {
        home ??= _home ?? Store.DefaultHome;
        var sig = BuiltinFiles.Signature(Dir(home), "SKILL.md");
        lock (Lock)
        {
            if (_all != null && home == _home && sig == _signature) return false;
            Load(home, sig);
            return true;
        }
    }

    public static void Reload(string? home = null)
    {
        home ??= _home ?? Store.DefaultHome;
        lock (Lock) Load(home, BuiltinFiles.Signature(Dir(home), "SKILL.md"));
    }

    static void Load(string home, string sig)
    {
        var map = new Dictionary<string, Skill>();
        var errors = new List<string>();
        foreach (var (path, text) in BuiltinFiles.Under("skills/").Where(kv => kv.Key.EndsWith("/SKILL.md", StringComparison.OrdinalIgnoreCase)))
        {
            var name = path.Split('/')[1];
            try { map[name] = Skill.Parse(text, name, "builtin"); }
            catch (FormatException e) { errors.Add($"内置技能 {name}：{e.Message}"); }
        }
        var dir = Dir(home);
        if (Directory.Exists(dir))
            foreach (var folder in Directory.EnumerateDirectories(dir).OrderBy(f => f, StringComparer.Ordinal))
            {
                var file = Path.Combine(folder, "SKILL.md");
                if (!File.Exists(file)) continue;
                var name = Path.GetFileName(folder);
                try { map[name] = Skill.Parse(File.ReadAllText(file), name, folder); }
                catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException) { errors.Add($"{file}：{e.Message}"); }
            }
        _all = map.Values.OrderBy(s => s.Builtin).ThenBy(s => s.Name, StringComparer.Ordinal).ToList();
        _errors = errors;
        _home = home;
        _signature = sig;
    }

    /// <summary>Card skills that apply to <paramref name="c"/>, most specific first (user before built-in on a tie).</summary>
    public static List<Skill> For(Card c) =>
        All.Where(s => s.Matches(c)).OrderByDescending(s => s.Specificity).ThenBy(s => s.Builtin).ThenBy(s => s.Name, StringComparer.Ordinal).ToList();

    /// <summary>
    /// The skill that owns ✨ and the automatic run on this card: the most specific match that is not menu-only. Its own
    /// `on` then decides: a skill that only allows "ask" also keeps the built-in summary from running automatically.
    /// </summary>
    public static Skill? Best(Card c) => For(c).FirstOrDefault(s => s.On.Any(o => o is "ask" or "create"));

    public static List<Skill> ForDesk() => All.Where(s => s.Scope == "desk").ToList();
}

/// <summary>What one skill run needs; <see cref="Desk"/> is the desk as markdown (desk skills).</summary>
public sealed class SkillRequest
{
    public required Skill Skill { get; init; }
    public required Llm Llm { get; init; }
    public required Store Store { get; init; }
    public Card? Card { get; init; }
    public string? Desk { get; init; }
    public int Budget { get; init; } = 300;
    public Page? Page { get; init; }
    public bool UserAsked { get; init; } = true;
    /// <summary>A prompt that replaces the skill body (the settings' summary template).</summary>
    public string? Template { get; init; }
    /// <summary>The run's operation group, reserved when the model may call PinCC itself (pincc grant).</summary>
    public long? Group { get; init; }
}

/// <summary>A finished run: the answer, and the card's source and input as they were when the run started (stale check).</summary>
public sealed record SkillResult(string Text, string? Src, string? InputHash, string Runner, IReadOnlyList<string> Grants);

/// <summary>
/// The skill pipeline, the same for the desktop and the CLI: context steps → prompt → model (with its grants) →
/// result; <see cref="Apply"/> writes the result back through the store, in the run's group.
/// </summary>
public static partial class SkillRun
{
    /// <summary>Runs the skill. Null only when an automatic run finds nothing it can read (the user did not ask).</summary>
    public static async Task<SkillResult?> Run(SkillRequest r, CancellationToken ct = default)
    {
        var skill = r.Skill;
        var llm = skill.Runner != null && skill.Runner != r.Llm.Tool
            ? Llm.FromSettings(r.Store, skill.Runner) ?? throw new InvalidOperationException($"技能要用执行器 {skill.Runner}：" + Llm.Problem(r.Store, skill.Runner))
            : r.Llm;
        var c = r.Card;
        if (skill.Scope == "card" && c == null) throw new InvalidOperationException($"技能 {skill.Name} 作用于一张卡片，需要卡片编号");
        var grants = skill.Grants.Select(g => g == "pincc" ? "shell:" + ExePath(llm.IsWsl) : g).ToList();
        if (skill.Grants.Contains("pincc") && r.Group == null) throw new InvalidOperationException("pincc 授权需要先预留操作组");
        if (grants.FirstOrDefault(g => !llm.Supports(g)) is { } missing)
            throw new InvalidOperationException($"执行器 {llm.Tool} 不支持授权 {missing.Split(':')[0]}（技能 {skill.Name} 需要；可在 agents\\{llm.Tool}.json 的 grants 里补上）");
        var src = c?.Src;
        var hash = c?.InputHash;
        string what = c == null ? "桌面卡片" : c.IsLink ? "网页" : c.IsFile ? "文档" : "笔记";
        string? cwd = null, file = c?.IsFile == true ? src : null;
        var parts = new List<string>();
        var (distro, wslEnv) = Llm.WslSettings(r.Store);
        foreach (var step in skill.Context)
        {
            if (step.Builtin == "note") parts.Add(c?.Note.Trim() ?? "");
            else if (step.Builtin == "desk") parts.Add(r.Desk ?? "");
            else if (step.Builtin == "source")
            {
                if (c == null) continue;
                var got = await Source(c, llm, r.Page, r.UserAsked, ct).ConfigureAwait(false);
                if (got == null) return null;
                what = got.Value.What;
                parts.Add(got.Value.Content);
                if (got.Value.Grant != null) grants.Add(got.Value.Grant);
                cwd ??= got.Value.Cwd;
                file ??= got.Value.File;
            }
            else
            {
                bool wsl = step.Where == "wsl" || step.Where == "runner" && llm.IsWsl;
                var argv = step.Run!.Select(t => Fill(t, c, file)).ToArray();
                var cmd = Llm.StepCommand(argv, wsl, llm.IsWsl ? llm.Distro : distro, llm.IsWsl ? llm.WslEnv : wslEnv, Env(r, c, file, wsl), null);
                try { parts.Add(await Llm.Execute(cmd, null, TimeSpan.FromSeconds(step.Timeout), ct).ConfigureAwait(false)); }
                catch (Exception e) when (e is InvalidOperationException or TimeoutException or IOException or System.ComponentModel.Win32Exception)
                {
                    throw new InvalidOperationException($"{argv[0]} 失败：{(e is TimeoutException ? "超时" : e.Message)}");
                }
            }
        }
        var content = string.Join("\n\n", parts.Where(p => p.Length > 0));
        var prompt = Llm.Prompt(what, c?.Title ?? "桌面", src ?? "", content, r.Budget, r.Template ?? skill.Body, c?.Id ?? 0, c?.Note.Trim());
        if (skill.Grants.Contains("pincc")) prompt += PinccNote(llm.IsWsl);
        var started = When.NowMs;
        string? error = null;
        try
        {
            var text = await llm.Ask(prompt, grants.Count == 0 ? ["none"] : grants, cwd, Env(r, c, file, llm.IsWsl), ct).ConfigureAwait(false);
            return new SkillResult(text, src, hash, llm.Tool, grants);
        }
        catch (Exception e) { error = e.Message; throw; }
        finally
        {
            RunLog.Write(r.Store.Home, new
            {
                at = When.FromMs(started).ToString("yyyy-MM-ddTHH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture), skill = skill.Name, card = c?.Id,
                runner = llm.Tool, grants, ms = When.NowMs - started, code = error == null ? 0 : 1, error,
            });
        }
    }

    /// <summary>
    /// The built-in source reader: the page or document text PinCC can extract; when it cannot, the model reads it
    /// itself if the runner allows (web for pages, read for documents in their folder). Null: nothing readable and
    /// nobody asked, so an automatic run quietly does nothing.
    /// </summary>
    static async Task<(string What, string Content, string? Grant, string? Cwd, string? File)?> Source(Card c, Llm llm, Page? page, bool userAsked, CancellationToken ct)
    {
        var src = c.Src;
        if (src == null) return ("笔记", c.Body, null, null, null);
        if (c.IsLink)
        {
            if (page == null)
            {
                try { page = await Fetch.Web(src, ct, auto: !userAsked).ConfigureAwait(false); } catch { page = null; } // unreachable now: still try the web grant below
            }
            (string What, string Content, string? Grant, string? Cwd, string? File) got;
            if (page is { Thin: false }) got = ("网页", page.Text, null, null, null);
            else if (page?.Download is { } doc && llm.Supports("read"))
                got = ("文档", "请先读取当前目录中的文件「" + Path.GetFileName(doc) + "」，再按要求处理。", "read", Path.GetDirectoryName(doc), doc);
            else if (llm.Supports("web")) got = ("网页", $"正文无法直接获取，请先访问这个网址再按要求处理：{src}\n{c.SrcInfo?.Desc}", "web", null, null);
            else got = ("网页", $"{c.SrcInfo?.Title}\n{c.SrcInfo?.Desc}".Trim(), null, null, null);
            if (!string.IsNullOrWhiteSpace(c.Note)) got.Content += "\n\n（卡片上的备注：" + c.Note.Trim() + "）";
            return got;
        }
        var (_, text) = await Task.Run(() => Fetch.File(src), ct).ConfigureAwait(false);
        if (text.Length > 0) return ("文档", text, null, null, null);
        if (llm.Supports("read") && File.Exists(src))
            return ("文档", "请先读取当前目录中的文件「" + Path.GetFileName(src) + "」，再按要求处理。", "read", Path.GetDirectoryName(src), src);
        if (userAsked) throw new InvalidOperationException($"这种文件需要能读文件的执行器：{llm.Tool} 没有 read 授权（设置 › 技能与执行器）");
        return null;
    }

    /// <summary>{src} {file} {dir} {card} {title} inside a step's argument; the argument stays one argument (no shell).</summary>
    static string Fill(string token, Card? c, string? file) => StepRx().Replace(token, m => m.Groups[1].Value switch
    {
        "src" => c?.Src ?? "",
        "file" => file ?? "",
        "dir" => file != null ? Path.GetDirectoryName(file) ?? "" : "",
        "card" => c?.Id.ToString() ?? "",
        "title" => c?.Title ?? "",
        _ => m.Value,
    });

    /// <summary>The run's PINCC_* variables, paths as the target environment sees them.</summary>
    static Dictionary<string, string> Env(SkillRequest r, Card? c, string? file, bool wsl)
    {
        string P(string p) => wsl ? Llm.WslPath(p) : p;
        var e = new Dictionary<string, string> { ["PINCC_SKILL"] = r.Skill.Name, ["PINCC_EXE"] = ExePath(wsl) };
        if (c != null)
        {
            e["PINCC_CARD"] = c.Id.ToString();
            if (c.Src is { } src) e["PINCC_SRC"] = c.IsFile ? P(src) : src;
        }
        if (file != null)
        {
            e["PINCC_FILE"] = P(file);
            if (Path.GetDirectoryName(file) is { } dir) e["PINCC_DIR"] = P(dir);
        }
        if (Environment.GetEnvironmentVariable("PINCC_HOME") is { Length: > 0 } home) e["PINCC_HOME"] = home;
        if (r.Group is long g) e["PINCC_GROUP"] = g.ToString();
        return e;
    }

    /// <summary>This PinCC.exe as the runner's environment reaches it.</summary>
    public static string ExePath(bool wsl)
    {
        var exe = Environment.ProcessPath ?? "PinCC.exe";
        if (!exe.EndsWith("PinCC.exe", StringComparison.OrdinalIgnoreCase)) exe = Path.Combine(AppContext.BaseDirectory, "PinCC.exe"); // tests run from another host
        return wsl ? Llm.WslPath(exe) : exe;
    }

    /// <summary>
    /// What the model is told when it may call PinCC: the exact program path (the permission covers only commands that
    /// start with it) and the common verbs, so it never needs a shorter spelling.
    /// </summary>
    static string PinccNote(bool wsl)
    {
        var exe = ExePath(wsl);
        return $"""


            你可以调用 PinCC 命令行读写桌面卡片。每条命令都必须以这个完整路径开头（权限只放行这个前缀）：
            {exe}
            不要 cd 过去、不要用变量或别名、不要写成 PinCC.exe 或 PinCC。常用：
            - {exe} list --md                          读全部卡片
            - {exe} show <id> --json                   读一张（含 tasks 编号、input_hash）
            - {exe} add "文字" [--remind "明天 9点"]    新建
            - {exe} set <id> --add-task "文字"          加任务；--check N / --uncheck N 勾选第 N 个
            - {exe} set <id> --summary "文字"           写摘要
            你的写入会自动归入本次运行的操作组，用户可以一键撤销；未经要求不要删除或完成卡片。
            """;
    }

    /// <summary>
    /// Writes the result where the skill says, in group <paramref name="g"/>: summary (only if the card still has the
    /// source and input it had when the run started — otherwise dropped as stale), append, new cards, or nothing
    /// (the model wrote through the CLI itself). Returns the card and whether anything was written.
    /// </summary>
    public static (Card? Card, bool Written) Apply(Store s, Group g, Skill skill, SkillResult res, long? cardId, Func<string, Group, Card>? newCard = null)
    {
        switch (skill.Output)
        {
            case "summary" when cardId is long id:
            {
                bool written = false;
                var c = s.Mutate(g, id, x =>
                {
                    if (x.Src != res.Src || x.InputHash != res.InputHash) return; // the card changed meanwhile: this result is stale
                    x.Summary = res.Text;
                    var meta = x.Info ?? new CardMeta { Src = x.Src };
                    meta.SumHash = res.InputHash;
                    x.Meta = meta.ToJson();
                    written = true;
                }, "summary");
                return (c, written);
            }
            case "append" when cardId is long id:
            {
                var c = s.Mutate(g, id, x => x.Body = (x.Body.TrimEnd() + "\n\n" + res.Text.Trim()).Trim(), "edit");
                return (c, c != null);
            }
            case "cards":
            {
                Card? last = null;
                foreach (var part in Cards(res.Text)) last = newCard != null ? newCard(part, g) : s.Insert(g, new Card { Body = part });
                return (last, last != null);
            }
            default: return (null, false);
        }
    }

    /// <summary>A "cards" result: one card per part, parts separated by a line that is just "---".</summary>
    public static List<string> Cards(string text) =>
        SplitRx().Split(text.Replace("\r\n", "\n")).Select(p => p.Trim()).Where(p => p.Length > 0).ToList();

    public static string Describe(Skill s)
    {
        var sb = new StringBuilder();
        foreach (var step in s.Context.Where(x => x.Run != null))
            sb.Append("  ").Append(string.Join(" ", step.Run!)).Append(step.Where == "runner" ? "" : $"（{(step.Where == "wsl" ? "WSL" : "Windows")}）").Append('\n');
        return sb.ToString();
    }

    [GeneratedRegex(@"\{(src|file|dir|card|title)\}")] private static partial Regex StepRx();
    [GeneratedRegex(@"\n[ \t]*---[ \t]*\n")] private static partial Regex SplitRx();
}
