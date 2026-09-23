using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PinCC.Core;

/// <summary>
/// One sticky card = one row. Only what the user decided is stored; kind, source, title, progress, overdue and
/// summary staleness are derived (pure, no IO). Geometry: W/H in DIP (H null = fit content); X/Y are the DIP offset
/// from the work-area origin of monitor <see cref="Mon"/> and only matter while <see cref="Free"/>.
/// </summary>
public sealed partial class Card
{
    public long Id { get; set; }
    public long Rev { get; set; }
    public string Body { get; set; } = "";
    public List<string> Images { get; set; } = new();
    public string? Summary { get; set; }
    public string? Meta { get; set; }
    public int? Color { get; set; }
    public bool Topmost { get; set; }
    public double Opacity { get; set; } = 1;
    public bool Collapsed { get; set; }
    public bool Free { get; set; }
    public string? Mon { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; } = 260;
    public double? H { get; set; }
    public long? RemindAt { get; set; }
    public long? DoneAt { get; set; }
    public long? DeletedAt { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }

    /// <summary>Writable columns, in the same order as <see cref="Values"/> (id, rev, updated_at are managed by Store).</summary>
    public static readonly string[] Columns =
    {
        "body", "images", "summary", "meta", "color", "topmost", "opacity", "collapsed", "free", "mon", "x", "y", "w", "h",
        "remind_at", "done_at", "deleted_at", "created_at",
    };

    public object?[] Values() => new object?[]
    {
        Body, Images.Count == 0 ? null : JsonSerializer.Serialize(Images), Summary, Meta, Color, Topmost, Opacity, Collapsed, Free, Mon,
        X, Y, W, H, RemindAt, DoneAt, DeletedAt, CreatedAt,
    };

    public static Card FromRow(IReadOnlyDictionary<string, object?> r) => new()
    {
        Id = L(r["id"]),
        Rev = L(r["rev"]),
        Body = (string?)r["body"] ?? "",
        Images = r["images"] is string json && json.Length > 2 ? JsonSerializer.Deserialize<List<string>>(json) ?? new() : new(),
        Summary = (string?)r["summary"],
        Meta = (string?)r["meta"],
        Color = r["color"] is null ? null : (int)L(r["color"]),
        Topmost = L(r["topmost"]) != 0,
        Opacity = D(r["opacity"], 1),
        Collapsed = L(r["collapsed"]) != 0,
        Free = L(r["free"]) != 0,
        Mon = (string?)r["mon"],
        X = D(r["x"], 0),
        Y = D(r["y"], 0),
        W = D(r["w"], 260),
        H = r["h"] is null ? null : D(r["h"], 0),
        RemindAt = N(r["remind_at"]),
        DoneAt = N(r["done_at"]),
        DeletedAt = N(r["deleted_at"]),
        CreatedAt = L(r["created_at"]),
        UpdatedAt = L(r["updated_at"]),
    };

    static long L(object? v) => v is null ? 0 : Convert.ToInt64(v);
    static long? N(object? v) => v is null ? null : Convert.ToInt64(v);
    static double D(object? v, double fallback) => v is null ? fallback : Convert.ToDouble(v);

    public Card Clone()
    {
        var c = (Card)MemberwiseClone();
        c.Images = new List<string>(Images);
        return c;
    }

    /// <summary>A copy of this card with the named <paramref name="cols"/> taken from <paramref name="from"/> (restore).</summary>
    public Card With(Card from, IEnumerable<string> cols)
    {
        var take = new HashSet<string>(cols, StringComparer.OrdinalIgnoreCase);
        var mine = Values();
        var theirs = from.Values();
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["id"] = Id, ["rev"] = Rev, ["updated_at"] = UpdatedAt };
        for (int i = 0; i < Columns.Length; i++) row[Columns[i]] = take.Contains(Columns[i]) ? theirs[i] : mine[i];
        return FromRow(row);
    }

    public string ToJson() => JsonSerializer.Serialize(this);
    public static Card FromJson(string json) => JsonSerializer.Deserialize<Card>(json)!;

    // ================= derived (never stored) =================

    [JsonIgnore] public bool Active => DoneAt == null && DeletedAt == null;
    [JsonIgnore] public bool Overdue => RemindAt is long t && t <= When.NowMs && DoneAt == null;

    /// <summary>First non-empty line, trimmed.</summary>
    [JsonIgnore] public string FirstLine => Body.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";

    /// <summary>The card's source: a first line that is a web address or an absolute file path (existence not required).</summary>
    [JsonIgnore]
    public string? Src
    {
        get
        {
            var line = FirstLine.Trim('"', '<', '>');
            if (IsUrl(line)) return line;
            if (line.StartsWith("file:///", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(line, UriKind.Absolute, out var u)) return u.LocalPath;
            return PathRx().IsMatch(line) ? line : null;
        }
    }

    [JsonIgnore] public bool IsLink => Src is { } s && IsUrl(s);
    [JsonIgnore] public bool IsFile => Src != null && !IsLink;
    /// <summary>A lone picture without text: it floats full-bleed at its own aspect. Several pictures show as a gallery.</summary>
    [JsonIgnore] public bool ImageOnly => Images.Count == 1 && string.IsNullOrWhiteSpace(Body);

    /// <summary>
    /// The user's own text: the lines after the source line (the whole body when there is no source). Not trimmed, so
    /// line <c>i</c> of the note is line <see cref="NoteLine"/> + i of the body (task toggles, caret placement).
    /// </summary>
    [JsonIgnore] public string Note => Src == null ? Body : string.Join("\n", Body.Split('\n').Skip(NoteLine));

    /// <summary>Index in Body.Split('\n') of the note's first line: 0 without a source, else the line after it.</summary>
    [JsonIgnore] public int NoteLine => Src == null ? 0 : Array.FindIndex(Body.Split('\n'), l => l.Trim().Length > 0) + 1;

    /// <summary>"note" | "task" | "link" | "file" | "image".</summary>
    [JsonIgnore] public string Kind => Images.Count > 0 && string.IsNullOrWhiteSpace(Body) ? "image" : IsLink ? "link" : IsFile ? "file" : Progress.Total > 0 ? "task" : "note";

    [JsonIgnore] public CardMeta? Info => string.IsNullOrEmpty(Meta) ? null : JsonSerializer.Deserialize<CardMeta>(Meta);

    /// <summary>Metadata that belongs to the current source (a changed first line invalidates it).</summary>
    [JsonIgnore] public CardMeta? SrcInfo => Info is { } m && m.Src == Src ? m : null;

    /// <summary>
    /// Task progress over the user's body only (summary action items never count), exactly as the body renders: task
    /// lines inside ``` fences or without a space after the box are not tasks. Cached per body (Kind asks often).
    /// </summary>
    [JsonIgnore]
    public (int Done, int Total) Progress
    {
        get
        {
            if (_progress is { } p && ReferenceEquals(p.Item1, Body)) return p.Item2;
            int done = 0, total = 0;
            foreach (var b in Markdown.Parse(Body))
                if (b.Kind == BlockKind.Task) { total++; if (b.Checked) done++; }
            _progress = Tuple.Create(Body, (done, total)); // one reference: safe to read from another thread
            return (done, total);
        }
    }
    Tuple<string, (int, int)>? _progress;

    /// <summary>What a summary is computed from: the note text, or the fetched text of the source.</summary>
    [JsonIgnore] public string? InputHash => Src == null ? Hash(Body) : SrcInfo?.TextHash;

    /// <summary>A summary whose input has changed since (shown dimmed, ✨ offered again).</summary>
    [JsonIgnore] public bool SummaryStale => !string.IsNullOrWhiteSpace(Summary) && Info?.SumHash is { } h && h != InputHash;

    /// <summary>First meaningful line (markdown stripped) — for pills, lists, the CLI.</summary>
    [JsonIgnore]
    public string Title
    {
        get
        {
            if (SrcInfo?.Title is { Length: > 0 } t) return t;
            foreach (var raw in Note.Split('\n'))
            {
                var line = Markdown.Plain(raw);
                if (line.Length > 0) return line;
            }
            if (Src is { } src) return IsLink ? src : Path.GetFileName(src.TrimEnd('\\', '/'));
            return Images.Count > 0 ? "图片" : "";
        }
    }

    /// <summary>First #tag in the body (color-by-tag), or null.</summary>
    [JsonIgnore] public string? Tag => TagRx().Match(Body) is { Success: true } m ? m.Groups[1].Value.ToLowerInvariant() : null;

    /// <summary>Palette index shown: the manual pick, or the automatic color for <paramref name="mode"/>.</summary>
    public int ShownColor(string mode) => Color ?? AutoColor(mode);

    /// <summary>Rotation order keeps neighbours ≥ 97° apart in hue: yellow, blue, pink, green, purple, peach, teal.</summary>
    public static readonly int[] Rotation = { 0, 4, 2, 6, 3, 1, 5 };

    public int AutoColor(string mode) => mode switch
    {
        "off" => 0,
        "kind" => Kind switch { "link" => 4, "file" => 6, "image" => 3, "task" => 1, _ => 0 },
        "tag" => Tag is { } tag ? Rotation[(int)(Fnv(tag) % 7)] : 0,
        _ => Rotation[(int)(Math.Max(Id, 0) % 7)],
    };

    /// <summary>Soonest reminder first (overdue ones earliest); then newest first (requirement #10).</summary>
    public static int Compare(Card a, Card b)
    {
        if (a.RemindAt is long ra && b.RemindAt is long rb) return ra != rb ? ra.CompareTo(rb) : b.CreatedAt.CompareTo(a.CreatedAt);
        if (a.RemindAt != null) return -1;
        if (b.RemindAt != null) return 1;
        return b.CreatedAt != a.CreatedAt ? b.CreatedAt.CompareTo(a.CreatedAt) : b.Id.CompareTo(a.Id);
    }

    public static bool IsUrl(string s) =>
        s.Length > 8 && !s.Contains('\n') && !s.Contains(' ') && Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Scheme is "http" or "https";

    public static string Hash(string s) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s)))[..16];

    static uint Fnv(string s)
    {
        uint h = 2166136261;
        foreach (var ch in s) { h ^= ch; h *= 16777619; }
        return h;
    }

    [GeneratedRegex(@"^(?:[A-Za-z]:\\|\\\\[^\\\s]+\\)[^<>|*?""\r\n]*$")] private static partial Regex PathRx();
    [GeneratedRegex(@"(?:^|\s)#([\p{L}\p{N}_\-]{1,32})")] private static partial Regex TagRx();
}

/// <summary>Cached facts about a card's source (and summary bookkeeping), stored as JSON in cards.meta.</summary>
public sealed class CardMeta
{
    /// <summary>The source these facts belong to.</summary>
    public string? Src { get; set; }
    public string? Title { get; set; }
    public string? Site { get; set; }
    public string? Desc { get; set; }
    public string? Date { get; set; }
    /// <summary>File facts: extension, size, last write (UTC ms).</summary>
    public string? FType { get; set; }
    public long? Size { get; set; }
    public long? Mtime { get; set; }
    /// <summary>Hash of the extracted text; the summary input for links and files.</summary>
    public string? TextHash { get; set; }
    /// <summary>InputHash at the time the summary was written.</summary>
    public string? SumHash { get; set; }
    /// <summary>"blocked" when the page text could not be read, "missing" for a file that does not exist.</summary>
    public string? State { get; set; }
    public string? Error { get; set; }

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
}
