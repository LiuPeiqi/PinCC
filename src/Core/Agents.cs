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
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            CheckStructure(doc.RootElement);
            p = JsonSerializer.Deserialize<RunnerProfile>(json, Json)!;
        }
        catch (JsonException e) { throw new FormatException("JSON 格式错误：" + e.Message, e); }
        p.Origin = origin;
        if (p.Name != name) throw new FormatException($"$.name 必须与文件名一致（{name}）");
        if (!NameRx().IsMatch(p.Name)) throw new FormatException("$.name 只能用小写字母、数字、. _ -");
        if (p.Name == "custom") throw new FormatException("custom 是保留名（自定义命令）");
        if (p.Args.Length == 0 && p.Command == null) throw new FormatException("缺少 args");
        if (p.Command != null && !CommandRx().IsMatch(p.Command)) throw new FormatException("command 只能是程序名或路径，不能含空格以外的特殊字符");
        for (var i = 0; i < p.Args.Length; i++) Placeholders(p.Args[i], $"$.args[{i}]", "{model}", "{grants}");
        if (p.Args.Count(a => a == "{model}") > 1 || p.Args.Count(a => a == "{grants}") > 1) throw new FormatException("{model}、{grants} 在 args 里最多各出现一次");
        for (var i = 0; i < (p.Model?.Length ?? 0); i++) Placeholders(p.Model![i], $"$.model[{i}]", "{value}");
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

    // Nullable annotations do not protect properties populated by JSON. Validate the effective
    // (last, case-insensitive) property before inspecting tokens. Omitted fields stay optional.
    static void CheckStructure(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("$ 必须是 JSON 对象");
        var fields = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in root.EnumerateObject()) fields[property.Name] = property.Value;
        if (fields.TryGetValue("name", out var name) && name.ValueKind != JsonValueKind.String)
            throw new FormatException("$.name 必须是字符串，不能为 null");
        foreach (var key in new[] { "args", "model", "listModels" })
            if (fields.TryGetValue(key, out var value)) CheckTokens(value, "$." + key);
        if (fields.TryGetValue("grants", out var grants))
        {
            if (grants.ValueKind != JsonValueKind.Object) throw new FormatException("$.grants 必须是对象，不能为 null");
            foreach (var grant in grants.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal).Select(g => g.Last())) CheckTokens(grant.Value, "$.grants." + grant.Name);
        }
        if (fields.TryGetValue("promptEscapes", out var escapes))
        {
            if (escapes.ValueKind != JsonValueKind.Object) throw new FormatException("$.promptEscapes 必须是对象，不能为 null");
            foreach (var escape in escapes.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal).Select(g => g.Last()))
                if (escape.Value.ValueKind != JsonValueKind.String)
                    throw new FormatException($"$.promptEscapes.{escape.Name} 必须是字符串，不能为 null");
        }
    }

    static void CheckTokens(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.Array) throw new FormatException($"{path} 必须是字符串数组，不能为 null");
        var index = 0;
        foreach (var token in value.EnumerateArray())
        {
            if (token.ValueKind != JsonValueKind.String) throw new FormatException($"{path}[{index}] 必须是字符串，不能为 null");
            index++;
        }
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
    sealed record Cache(string Home, string Signature, IReadOnlyList<RunnerProfile> Profiles, IReadOnlyList<string> Errors);
    static Cache? _cache;
    static readonly object Lock = new();

    static Cache Current
    {
        get
        {
            if (Volatile.Read(ref _cache) is { } cache) return cache;
            Refresh();
            return Volatile.Read(ref _cache)!;
        }
    }

    /// <summary>The loaded set. GUI startup and refresh load it in the background; CLI callers may load synchronously.</summary>
    public static IReadOnlyList<RunnerProfile> All => Current.Profiles;
    public static IReadOnlyList<string> Errors => Current.Errors;

    public static RunnerProfile? Find(string name) => All.FirstOrDefault(p => p.Name == name);

    /// <summary>The user's folder for runner profiles.</summary>
    public static string Dir(string? home = null) => Path.Combine(home ?? Store.DefaultHome, "agents");

    /// <summary>Re-reads when the folder (or the data folder itself) changed; returns true when something did.</summary>
    public static bool Refresh(string? home = null)
    {
        home ??= Volatile.Read(ref _cache)?.Home ?? Store.DefaultHome;
        lock (Lock)
        {
            var sig = Signature(home);
            if (_cache != null && home == _cache.Home && sig == _cache.Signature) return false;
            Load(home, sig);
            return true;
        }
    }

    /// <summary>Loads from <paramref name="home"/> now (tests, the settings page's 重新加载).</summary>
    public static void Reload(string? home = null)
    {
        home ??= Volatile.Read(ref _cache)?.Home ?? Store.DefaultHome;
        lock (Lock) Load(home, Signature(home));
    }

    static string Signature(string home)
    {
        try { return BuiltinFiles.Signature(Dir(home), "*.json"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return "error:" + e.GetType().Name + ":" + e.Message; }
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
        try
        {
            if (Directory.Exists(dir))
                foreach (var file in Directory.EnumerateFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    try { map[name] = RunnerProfile.Parse(File.ReadAllText(file), name, file); }
                    catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException) { errors.Add($"{file}：{e.Message}"); }
                }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { errors.Add($"{dir}：{e.Message}"); }
        // Readers never see a partially loaded set while a background refresh is in progress.
        Volatile.Write(ref _cache, new Cache(home, sig,
            Array.AsReadOnly(map.Values.OrderBy(p => p.Priority).ThenBy(p => p.Name, StringComparer.Ordinal).ToArray()),
            errors.AsReadOnly()));
    }
}

/// <summary>One line per completed model run in runs.log. Existing fields and ms semantics are retained.</summary>
public static class RunLog
{
    public static void Write(string home, object entry) => LogFile.Write(home, "runs.log", entry);
}
