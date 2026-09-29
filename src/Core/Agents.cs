using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PinCC.Core;

/// <summary>
/// The files that ship inside the exe (repo folder builtin/, embedded as resources "builtin/…"): runner profiles and
/// skills. The Mac app bundles the same files, so they are a shared format, not code.
/// </summary>
public static class BuiltinFiles
{
    static Dictionary<string, string>? _files;

    /// <summary>Path under builtin/ ("agents/claude.json") → text, for every file whose path starts with <paramref name="prefix"/>.</summary>
    public static IEnumerable<KeyValuePair<string, string>> Under(string prefix)
    {
        if (_files == null)
        {
            var asm = typeof(BuiltinFiles).Assembly;
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in asm.GetManifestResourceNames())
            {
                var path = name.Replace('\\', '/');
                if (!path.StartsWith("builtin/", StringComparison.OrdinalIgnoreCase)) continue;
                using var s = asm.GetManifestResourceStream(name)!;
                using var r = new StreamReader(s);
                files[path["builtin/".Length..]] = r.ReadToEnd();
            }
            _files = files;
        }
        return _files.Where(kv => kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).OrderBy(kv => kv.Key, StringComparer.Ordinal);
    }

    /// <summary>Folder files, re-read only when something in them changed (name, size or time).</summary>
    public static string Signature(string dir, string pattern) =>
        !Directory.Exists(dir) ? "" : string.Join("|", Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories)
            .Select(f => new FileInfo(f)).OrderBy(f => f.FullName, StringComparer.Ordinal).Select(f => $"{f.FullName}:{f.Length}:{f.LastWriteTimeUtc.Ticks}"));
}

/// <summary>
/// How to run one CLI model (agents/&lt;name&gt;.json): its constant argument tokens, where the model goes, and how
/// each abstract grant (none / web / read / shell) is spelled for this CLI. Adding a model CLI = adding a file.
/// </summary>
public sealed partial class RunnerProfile
{
    public string Name { get; set; } = "";
    public string? Label { get; set; }
    /// <summary>The program (looked up on PATH, or in the WSL login shell); the name when absent.</summary>
    public string? Command { get; set; }
    /// <summary>Detection order (lower first).</summary>
    public int Priority { get; set; } = 100;
    public string[] Args { get; set; } = [];
    /// <summary>Tokens that pass the model ("{value}" = the model); empty: the CLI takes no model.</summary>
    public string[]? Model { get; set; }
    public bool RequiresModel { get; set; }
    /// <summary>Arguments that list installed models (a header line, then the model in the first column).</summary>
    public string[]? ListModels { get; set; }
    public Dictionary<string, string[]>? Grants { get; set; }
    /// <summary>Literal replacements applied to the prompt before it is sent (gemini/qwen read "@path" as "include this file").</summary>
    public Dictionary<string, string>? PromptEscapes { get; set; }

    /// <summary>"内置" or the file it came from.</summary>
    [JsonIgnore] public string Origin { get; set; } = "内置";
    [JsonIgnore] public string Program => string.IsNullOrWhiteSpace(Command) ? Name : Command!;
    [JsonIgnore] public string Display => string.IsNullOrWhiteSpace(Label) ? Name : Label!;

    public bool HasGrant(string key) => key == "none" || Grants?.ContainsKey(key) == true;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
    };

    /// <summary>Parses and checks a profile; throws FormatException with the reason (shown in settings and `PinCC skills`).</summary>
    public static RunnerProfile Parse(string json, string name, string origin)
    {
        RunnerProfile p;
        try { p = JsonSerializer.Deserialize<RunnerProfile>(json, Json) ?? throw new FormatException("文件是空的"); }
        catch (JsonException e) { throw new FormatException("JSON 格式错误：" + e.Message); }
        p.Origin = origin;
        if (p.Name != name) throw new FormatException($"name 必须与文件名一致（{name}）");
        if (!NameRx().IsMatch(p.Name)) throw new FormatException("name 只能用小写字母、数字、. _ -");
        if (p.Name == "custom") throw new FormatException("custom 是保留名（自定义命令）");
        if (p.Args.Length == 0 && p.Command == null) throw new FormatException("缺少 args");
        if (p.Command != null && !CommandRx().IsMatch(p.Command)) throw new FormatException("command 只能是程序名或路径，不能含空格以外的特殊字符");
        foreach (var a in p.Args) Placeholders(a, "args", "{model}", "{grants}");
        if (p.Args.Count(a => a == "{model}") > 1 || p.Args.Count(a => a == "{grants}") > 1) throw new FormatException("{model}、{grants} 在 args 里最多各出现一次");
        foreach (var a in p.Model ?? []) Placeholders(a, "model", "{value}");
        if (p.Model is { Length: > 0 } m && !m.Contains("{value}")) throw new FormatException("model 里要有 {value}");
        if (p.RequiresModel && p.Model is not { Length: > 0 }) throw new FormatException("requiresModel 需要同时写 model");
        foreach (var (key, tokens) in p.Grants ?? new())
        {
            if (key is not ("none" or "web" or "read" or "shell")) throw new FormatException($"未知的授权 {key}（可用：none web read shell）");
            foreach (var t in tokens)
            {
                if (t.Contains("{prefix}") && key != "shell") throw new FormatException("{prefix} 只能用在 shell 授权里");
                if (PlaceholderRx().IsMatch(t.Replace("{prefix}", ""))) throw new FormatException($"grants.{key} 里有未知占位符：{t}");
            }
            if (key == "shell" && !tokens.Any(t => t.Contains("{prefix}"))) throw new FormatException("shell 授权要用 {prefix} 放命令前缀");
        }
        if ((p.Grants ?? new()).Any(kv => kv.Value.Length > 0) && !p.Args.Contains("{grants}")) throw new FormatException("写了授权参数，但 args 里没有 {grants}");
        if (p.RequiresModel == false && p.Model is { Length: > 0 } && !p.Args.Contains("{model}")) throw new FormatException("写了 model，但 args 里没有 {model}");
        return p;
    }

    /// <summary>A token may hold only the allowed placeholders, and those only as the whole token.</summary>
    static void Placeholders(string token, string where, params string[] allowed)
    {
        if (!token.Contains('{')) return;
        if (allowed.Contains(token)) return;
        if (PlaceholderRx().IsMatch(token)) throw new FormatException($"{where} 里的占位符必须单独作为一个参数：{token}");
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{0,31}$")] public static partial Regex NameRx();
    [GeneratedRegex(@"^[\w .:\\/+-]{1,260}$")] private static partial Regex CommandRx();
    [GeneratedRegex(@"\{[a-z]+\}")] private static partial Regex PlaceholderRx();
}

/// <summary>
/// The runner profiles in effect: built-in (builtin/agents/*.json) then the user's (data folder agents/*.json, same name
/// wins). A broken file is left out and reported in <see cref="Errors"/>; the others keep working.
/// </summary>
public static class Runners
{
    static List<RunnerProfile>? _all;
    static List<string> _errors = [];
    static string? _home, _signature;
    static readonly object Lock = new();

    /// <summary>The loaded set (read on first use; <see cref="Refresh"/> picks up file changes — the desk does every few seconds).</summary>
    public static IReadOnlyList<RunnerProfile> All { get { if (_all == null) Refresh(); return _all!; } }
    public static IReadOnlyList<string> Errors { get { if (_all == null) Refresh(); return _errors; } }

    public static RunnerProfile? Find(string name) => All.FirstOrDefault(p => p.Name == name);

    /// <summary>The user's folder for runner profiles.</summary>
    public static string Dir(string? home = null) => Path.Combine(home ?? Store.DefaultHome, "agents");

    /// <summary>Re-reads when the folder (or the data folder itself) changed; returns true when something did.</summary>
    public static bool Refresh(string? home = null)
    {
        home ??= _home ?? Store.DefaultHome;
        var sig = BuiltinFiles.Signature(Dir(home), "*.json");
        lock (Lock)
        {
            if (_all != null && home == _home && sig == _signature) return false;
            Load(home, sig);
            return true;
        }
    }

    /// <summary>Loads from <paramref name="home"/> now (tests, the settings page's 重新加载).</summary>
    public static void Reload(string? home = null)
    {
        home ??= _home ?? Store.DefaultHome;
        lock (Lock) Load(home, BuiltinFiles.Signature(Dir(home), "*.json"));
    }

    static void Load(string home, string sig)
    {
        var map = new Dictionary<string, RunnerProfile>();
        var errors = new List<string>();
        foreach (var (path, text) in BuiltinFiles.Under("agents/"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            try { map[name] = RunnerProfile.Parse(text, name, "内置"); }
            catch (FormatException e) { errors.Add($"内置执行器 {name}：{e.Message}"); }
        }
        var dir = Dir(home);
        if (Directory.Exists(dir))
            foreach (var file in Directory.EnumerateFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                try { map[name] = RunnerProfile.Parse(File.ReadAllText(file), name, file); }
                catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException) { errors.Add($"{file}：{e.Message}"); }
            }
        _all = map.Values.OrderBy(p => p.Priority).ThenBy(p => p.Name, StringComparer.Ordinal).ToList();
        _errors = errors;
        _home = home;
        _signature = sig;
    }
}

/// <summary>One line per model run in runs.log (data folder): what ran, for whom, how it ended. Capped at 1 MB.</summary>
public static class RunLog
{
    public static void Write(string home, object entry)
    {
        try
        {
            var path = Path.Combine(home, "runs.log");
            var line = JsonSerializer.Serialize(entry, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";
            // One append per line: the GUI and the CLI may both be writing; a line is never split between them.
            using (var f = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) f.Write(System.Text.Encoding.UTF8.GetBytes(line));
            if (new FileInfo(path).Length <= 1_000_000) return;
            // Trimmed through a temporary file and a rename: a crash never leaves half a log; lines stay LF.
            var keep = string.Concat(File.ReadAllText(path).Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(500).Select(l => l + "\n"));
            File.WriteAllText(path + ".tmp", keep);
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // a log must never break a run
    }
}
