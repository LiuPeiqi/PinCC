using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Buffers.Binary;

namespace PinCC.Core;

/// <summary>A fetched web page reduced to what a card shows and what the LLM reads.</summary>
public sealed record Page(string Url, string? Title, string? Desc, string? Site, string? ImageUrl, string Text)
{
    public string? Date { get; init; }

    /// <summary>Media type the server declared ("text/html", "application/zip"…); null for an Edge-rendered DOM.</summary>
    public string? Type { get; init; }

    /// <summary>The URL itself serves an image (checked by its magic bytes); <see cref="ImageExt"/> is ".png", ".jpg", ".gif", ".bmp" or ".webp".</summary>
    public byte[]? Image { get; init; }
    public string? ImageExt { get; init; }

    /// <summary>Local copy of a downloaded PDF / Word / PowerPoint / Excel file (Text holds the Office text; PDFs need the LLM's file mode).</summary>
    public string? Download { get; init; }

    /// <summary>Too little readable text (JS-rendered, login wall, bot challenge, not a web page): try a renderer or a browsing LLM.</summary>
    public bool Thin => Text.Length < 300;
}

/// <summary>An automatic read was redirected to an address that is not public (intranet, localhost, a secret in the query).</summary>
public sealed class UnsafeRedirectException(string target) : Exception("跳转到了非公网地址，未自动读取")
{
    public string Target { get; } = target;
}

/// <summary>Dependency-free web page and document text extraction ("readability-lite").</summary>
public static partial class Fetch
{
    public const int MaxText = 22_000;
    const int MaxPage = 2_000_000, MaxImage = 15_000_000, MaxDownload = 25_000_000;
    internal const int MaxOfficeBytes = 60_000_000, MaxOfficeParts = 256, MaxArchiveEntries = 4096;
    const int RegexMilliseconds = 2000;
    static readonly TimeSpan PageDeadline = TimeSpan.FromSeconds(15), DownloadDeadline = TimeSpan.FromSeconds(60);

    static readonly HttpClient Http = CreateClient(redirect: true), Manual = CreateClient(redirect: false);
    static readonly SemaphoreSlim FileGate = new(2); // cancelled callers never release the slot of a still blocked reader
    static readonly SemaphoreSlim EdgeGate = new(1); // one headless Edge at a time: they share one profile folder

    // Explicit static ctor: guarantees the code-page provider (GBK/Big5/Shift-JIS) is registered before any member runs.
    static Fetch() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    static HttpClient CreateClient(bool redirect)
    {
        var http = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = redirect,
            MaxAutomaticRedirections = 8,
        }) { Timeout = TimeSpan.FromSeconds(90) }; // backstop only: every call carries its own overall deadline
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36 Edg/140.0.0.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.8");
        return http;
    }

    /// <summary>
    /// Reads a URL within one deadline (headers and body). Routed by Content-Type before the body is read: HTML → page
    /// text; text/JSON/XML → the text; image → <see cref="Page.Image"/>; PDF/Office → <see cref="Page.Download"/>;
    /// anything else → a thin page named after the file. An <paramref name="auto"/> read (nobody asked for it) checks
    /// every redirect hop with <see cref="SafeToAutoAsync"/> and throws <see cref="UnsafeRedirectException"/> rather
    /// than follow a public link into a private address.
    /// </summary>
    public static async Task<Page> Web(string url, CancellationToken ct = default, bool auto = false, RunContext? context = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(PageDeadline);
        try { return await WebCore(url, cts, auto, context).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("读取超时"); }
    }

    /// <summary>GET following redirects by hand, so that each hop of an automatic read is checked before it is requested.</summary>
    static async Task<HttpResponseMessage> GetChecked(string url, CancellationToken ct)
    {
        for (int hop = 0; ; hop++)
        {
            var resp = await Manual.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)resp.StatusCode is not (301 or 302 or 303 or 307 or 308) || resp.Headers.Location is not { } location) return resp;
            resp.Dispose();
            if (hop >= 8) throw new HttpRequestException("跳转次数过多");
            var next = new Uri(new Uri(url), location).ToString();
            if (!await SafeToAutoAsync(next, ct).ConfigureAwait(false)) throw new UnsafeRedirectException(next);
            url = next;
        }
    }

    static async Task<Page> WebCore(string url, CancellationTokenSource cts, bool auto, RunContext? context)
    {
        using var httpPhase = context?.Phase("http");
        using var resp = auto ? await GetChecked(url, cts.Token).ConfigureAwait(false)
            : await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        var finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? url;
        var site = Host(finalUrl);
        if ((int)resp.StatusCode is 401 or 403 or 429 or 503) return new Page(finalUrl, null, null, site, null, "");
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"网页返回 HTTP {(int)resp.StatusCode}");
        var headers = resp.Content.Headers;
        var type = headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
        var name = FileName(headers.ContentDisposition, finalUrl);
        var other = new Page(finalUrl, name, type, site, null, "") { Type = type };

        var ext = DocExt(type, name);
        if (ext != null || type.StartsWith("image/"))
        {
            var max = ext == null ? MaxImage : MaxDownload;
            if (headers.ContentLength > max) return other;
            cts.CancelAfter(DownloadDeadline); // files are bigger than pages: a longer deadline, still bounded
            var (bytes, cut) = await ReadCapped(resp, max, cts.Token).ConfigureAwait(false);
            if (cut) return other; // a truncated image or document is useless
            if (ext == null) return ImageExt(bytes) is { } ie ? other with { Image = bytes, ImageExt = ie } : other;
            if (!(ext == ".pdf" ? IsPdf(bytes) : bytes.AsSpan().StartsWith("PK\x03\x04"u8))) return other; // an error page, not the file
            var path = SaveDownload(bytes, ext);
            httpPhase?.Dispose();
            using var parsePhase = context?.Phase("document_parse");
            string text;
            try { text = ext == ".pdf" ? "" : FileText(path, cts.Token); }
            catch (Exception e) when (e is SourceLimitException or TimeoutException && !cts.IsCancellationRequested)
            { return other with { Download = path, Desc = e.Message }; }
            return other with { Download = path, Text = text, Desc = Snippet(text) ?? type };
        }

        bool html = type.Length == 0 || type.Contains("html");
        bool plain = !html && (type.StartsWith("text/") || type.Contains("json") || type.Contains("xml") || type.Contains("javascript"));
        if (!html && !plain) return other;
        var (body, truncated) = await ReadCapped(resp, MaxPage, cts.Token).ConfigureAwait(false);
        var decoded = Decode(body, headers.ContentType?.CharSet, html, truncated);
        httpPhase?.Dispose();
        using var htmlPhase = context?.Phase(html ? "html_parse" : "text_decode");
        if (html) return FromHtml(finalUrl, decoded, cts.Token) with { Type = type };
        var t = Cap(decoded);
        return new Page(finalUrl, name, Snippet(t), site, null, t) { Type = type };
    }

    /// <summary>Metadata + main text from an HTML document (also used for the Edge-rendered DOM).</summary>
    public static Page FromHtml(string url, string html, CancellationToken ct = default)
    {
        try { return FromHtmlCore(url, html, ct); }
        catch (RegexMatchTimeoutException) { ct.ThrowIfCancellationRequested(); throw new TimeoutException("HTML 解析超过匹配时间上限"); }
    }

    static Page FromHtmlCore(string url, string html, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (html.Length > MaxPage) throw new SourceLimitException("HTML 超过 2 MB 解析上限");
        var metadata = MetaRx().Matches(html).Cast<Match>().Take(256).ToArray();
        string? M(string key) => metadata
            .Where(m => string.Equals(Attr(m.Value, "property") ?? Attr(m.Value, "name") ?? Attr(m.Value, "itemprop"), key, StringComparison.OrdinalIgnoreCase))
            .Select(m => Attr(m.Value, "content")).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) is { } v ? Clean(v) : null;

        var ld = JsonLd(html, ct);
        var site = M("og:site_name") ?? ld.Site ?? Host(url);
        var title = M("og:title") ?? M("twitter:title") ?? ld.Title ?? (TitleRx().Match(html) is { Success: true } t ? Clean(t.Groups[1].Value) : null);
        if (title != null && site.Length > 1)
            foreach (var sep in new[] { " - ", " | ", "_", " – ", " — " })
                if (title.EndsWith(sep + site, StringComparison.OrdinalIgnoreCase)) { title = title[..^(sep.Length + site.Length)].Trim(); break; }
        var desc = M("og:description") ?? M("twitter:description") ?? M("description") ?? ld.Desc;
        var date = M("article:published_time") ?? M("og:updated_time") ?? M("datePublished") ?? M("pubdate");
        var image = M("og:image") ?? M("og:image:url") ?? M("twitter:image") ?? ld.Image;
        if (image != null && Uri.TryCreate(new Uri(url), image, out var abs)) image = abs.ToString();

        ct.ThrowIfCancellationRequested();
        var text = ld.Body is { Length: >= 300 } body ? body : MainText(html, ct);
        if (text.Length < 300 && ChallengeRx().IsMatch(title + " " + text)) text = "";
        text = Cap(text);
        return new Page(url, title, desc ?? Snippet(text), site, image, text) { Date = date is { Length: >= 10 } d ? d[..10] : null };
    }

    /// <summary>Downloads a small resource (e.g. og:image) or returns null (also when it is larger than <paramref name="max"/>).</summary>
    public static async Task<byte[]?> Bytes(string url, int max = 4_000_000, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(PageDeadline);
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var (bytes, cut) = await ReadCapped(resp, max, cts.Token).ConfigureAwait(false);
            return cut ? null : bytes;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    static async Task<(byte[] Bytes, bool Truncated)> ReadCapped(HttpResponseMessage resp, int max, CancellationToken ct)
    {
        await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var ms = new MemoryStream();
        var buf = new byte[81920];
        for (int n; (n = await s.ReadAsync(buf, ct).ConfigureAwait(false)) > 0;)
        {
            var keep = (int)Math.Min(n, max - ms.Length);
            ms.Write(buf, 0, keep);
            if (keep < n) return (ms.ToArray(), true);
        }
        return (ms.ToArray(), false);
    }

    /// <summary>Charset from the header, then &lt;meta charset&gt;, then UTF-8 (GBK if UTF-8 is invalid).</summary>
    internal static string Decode(byte[] bytes, string? headerCharset, bool sniffMeta, bool truncated = false)
    {
        var name = headerCharset?.Trim('"', '\'', ' ');
        if (string.IsNullOrEmpty(name) && sniffMeta)
        {
            var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
            if (CharsetRx().Match(head) is { Success: true } m) name = m.Groups[1].Value;
        }
        if (!string.IsNullOrEmpty(name))
        {
            if (name.ToLowerInvariant() is "gb2312" or "gbk" or "x-gbk" or "gb18030" or "gb_2312-80") name = "gb18030"; // superset
            try { return Encoding.GetEncoding(name).GetString(bytes); } catch (ArgumentException) { }
        }
        return Text(bytes, truncated);
    }

    /// <summary>Decodes text bytes: BOM, strict UTF-8, else the Chinese ANSI code page (GBK).</summary>
    public static string Text(byte[] bytes) => Text(bytes, false);

    /// <param name="truncated">The bytes were cut at a size cap: a UTF-8 sequence split at the end is dropped instead of
    /// failing strict UTF-8 (which would misread the whole text as GBK).</param>
    internal static string Text(byte[] bytes, bool truncated)
    {
        if (bytes.Length >= 2 && (bytes[0] == 0xFF && bytes[1] == 0xFE || bytes[0] == 0xFE && bytes[1] == 0xFF) ||
            bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new StreamReader(new MemoryStream(bytes), true).ReadToEnd();
        try { return StrictUtf8.GetString(bytes, 0, truncated ? Utf8Complete(bytes) : bytes.Length); }
        catch (DecoderFallbackException) { return Encoding.GetEncoding(54936).GetString(bytes); } // GB18030
    }

    static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Length of <paramref name="b"/> without a multi-byte UTF-8 sequence that was cut off at the end.</summary>
    static int Utf8Complete(ReadOnlySpan<byte> b)
    {
        for (int i = b.Length - 1, back = 0; i >= 0 && back < 4; i--, back++)
        {
            if ((b[i] & 0xC0) == 0x80) continue; // continuation byte: keep looking for the lead byte
            int need = b[i] >= 0xF0 ? 4 : b[i] >= 0xE0 ? 3 : b[i] >= 0xC0 ? 2 : 1;
            return back + 1 < need ? i : b.Length;
        }
        return b.Length;
    }

    /// <summary>Readable main text: prefer the largest &lt;article&gt;, then &lt;main&gt;, then &lt;body&gt;; drop chrome; keep block breaks.</summary>
    public static string MainText(string html, CancellationToken ct = default)
    {
        try { return MainTextCore(html, ct); }
        catch (RegexMatchTimeoutException) { ct.ThrowIfCancellationRequested(); throw new TimeoutException("HTML 解析超过匹配时间上限"); }
    }

    static string MainTextCore(string html, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (html.Length > MaxPage) throw new SourceLimitException("HTML 超过 2 MB 解析上限");
        var s = JunkRx().Replace(html, " ");
        ct.ThrowIfCancellationRequested();
        var known = KnownContainers.Select(sel => { ct.ThrowIfCancellationRequested(); return Element(s, sel, ct); }).FirstOrDefault(x => x is { Length: > 300 });
        s = ChromeRx().Replace(known ?? s, " ");
        ct.ThrowIfCancellationRequested();
        // Search / login boxes go, but not a form that wraps the whole page (ASP.NET WebForms does that).
        s = FormRx().Replace(s, m => { ct.ThrowIfCancellationRequested(); return WhiteRx().Replace(TagRx().Replace(m.Groups[1].Value, ""), "").Length < 400 ? " " : m.Value; });
        ct.ThrowIfCancellationRequested();
        var article = ArticleRx().Matches(s).Cast<Match>().Select(m => { ct.ThrowIfCancellationRequested(); return m.Groups[1].Value; }).MaxBy(x => x.Length);
        var main = MainRx().Match(s);
        var body = BodyRx().Match(s);
        s = known ?? (article is { Length: > 500 } ? article : main.Success ? main.Groups[1].Value : body.Success ? body.Groups[1].Value : s);
        ct.ThrowIfCancellationRequested();
        s = HeadingTagRx().Replace(s, m => { ct.ThrowIfCancellationRequested(); return "\n\n" + new string('#', m.Groups[1].Value[0] - '0') + " "; });
        s = LiTagRx().Replace(s, "\n- ");
        s = BlockTagRx().Replace(s, "\n");
        ct.ThrowIfCancellationRequested();
        s = TagRx().Replace(s, "");
        ct.ThrowIfCancellationRequested();
        s = WebUtility.HtmlDecode(s);
        var lines = s.Split('\n').Select(l => { ct.ThrowIfCancellationRequested(); return SpaceRx().Replace(l, " ").Trim(); }).Where(l => l.Length > 0 && l != "-");
        return BlankRx().Replace(string.Join("\n", lines), "\n\n").Trim();
    }

    /// <summary>About 160 characters of running text (headings and list marks dropped): the description when a page has none.</summary>
    static string? Snippet(string text)
    {
        var prose = string.Join(" ", text.Split('\n').Where(l => !l.StartsWith('#')).Select(l => l.StartsWith("- ") ? l[2..] : l));
        var s = WhiteRx().Replace(prose.Length > 0 ? prose : text.Replace("#", ""), " ").Trim();
        if (s.Length <= 160) return s.Length == 0 ? null : s;
        return s[..(char.IsHighSurrogate(s[159]) ? 159 : 160)].TrimEnd() + "…";
    }

    // ================= which links may be read without asking =================

    /// <summary>
    /// Whether a link may be fetched (and sent to the model) without asking: public http(s) hosts only, and no
    /// one-time-looking query parameter (token, code, signature…) that a GET could consume or leak. No DNS lookup.
    /// </summary>
    public static bool SafeToAuto(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https") || u.UserInfo.Length > 0) return false;
        var host = u.DnsSafeHost.TrimEnd('.').ToLowerInvariant();
        if (IPAddress.TryParse(host, out var ip)) { if (!IsPublic(ip)) return false; }
        else if (!host.Contains('.') || host == "localhost" || LocalSuffixes.Any(host.EndsWith)) return false;
        return !SecretQuery(u.Query);
    }

    /// <summary><see cref="SafeToAuto"/>, and every address the host resolves to is public (a public name can point inside).</summary>
    public static async Task<bool> SafeToAutoAsync(string url, CancellationToken ct = default)
    {
        if (!SafeToAuto(url)) return false;
        var host = new Uri(url).DnsSafeHost.TrimEnd('.');
        if (IPAddress.TryParse(host, out _)) return true;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            var addrs = await Dns.GetHostAddressesAsync(host, cts.Token).ConfigureAwait(false);
            return addrs.Length > 0 && addrs.All(IsPublic);
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException or ArgumentException)
        {
            ct.ThrowIfCancellationRequested();
            return false; // unresolvable or slow: let the user decide
        }
    }

    static readonly string[] LocalSuffixes = { ".localhost", ".local", ".lan", ".internal", ".intranet", ".corp", ".home.arpa", ".localdomain", ".ts.net" };

    /// <summary>
    /// Globally routable unicast only. 198.18.0.0/15 is deliberately allowed: "fake-IP" proxies (Clash and friends,
    /// common in China) resolve every public name into it.
    /// </summary>
    static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var b = ip.GetAddressBytes();
        if (b.Length == 16)
        {
            // ::/96 (::, ::1, IPv4-compatible) and NAT64 64:ff9b::/96 carry an IPv4 address in the last 4 bytes.
            bool v4Inside = b.AsSpan(0, 12).IndexOfAnyExcept((byte)0) < 0 ||
                            b.AsSpan(0, 12).SequenceEqual(new byte[] { 0, 0x64, 0xff, 0x9b, 0, 0, 0, 0, 0, 0, 0, 0 });
            if (v4Inside) return IsPublic(new IPAddress(b.AsSpan(12)));
            return !(ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || (b[0] & 0xFE) == 0xFC); // fc00::/7 ULA
        }
        return !(b[0] is 0 or 10 or 127 || b[0] >= 224 ||             // this network, private, loopback, multicast/reserved
                 b[0] == 100 && (b[1] & 0xC0) == 64 ||                 // 100.64.0.0/10 CGNAT / Tailscale
                 b[0] == 169 && b[1] == 254 ||                         // link-local
                 b[0] == 172 && (b[1] & 0xF0) == 16 ||                 // 172.16.0.0/12
                 b[0] == 192 && (b[1] == 168 || b[1] == 0 && b[2] == 0)); // 192.168.0.0/16, 192.0.0.0/24
    }

    /// <summary>A query parameter whose NAME contains a secret-ish word: access_token, X-Amz-Signature, api_key, sessionid…</summary>
    static bool SecretQuery(string query)
    {
        foreach (var pair in query.TrimStart('?').Split('&', ';'))
        {
            var name = Uri.UnescapeDataString(pair.Split('=', 2)[0].Replace('+', ' '));
            if (SecretNameRx().IsMatch(BenignNameRx().Replace(name, ""))) return true;
        }
        return false;
    }

    // ================= local files =================

    public static Task<(CardMeta Meta, string Text)> FileAsync(string path, CancellationToken ct = default, RunContext? context = null) =>
        FileWorker(token => File(path, token, context), ct, context);

    // WaitAsync bounds the caller even if a synchronous network-file read is blocked inside the OS.
    // The worker retains its slot until it actually exits, so repeated cancelled requests cannot create unbounded threads.
    internal static async Task<T> FileWorker<T>(Func<CancellationToken, T> read, CancellationToken ct = default, RunContext? context = null)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stop.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using (context?.Phase("file_queue"))
                await FileGate.WaitAsync(stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("等待文件读取超过 15 秒"); }
        var token = stop.Token;
        var work = Task.Run(() =>
        {
            try { token.ThrowIfCancellationRequested(); return read(token); }
            finally { FileGate.Release(); }
        });
        _ = work.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try { return await work.WaitAsync(stop.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("文件读取超过 15 秒"); }
    }

    /// <summary>Facts about a local file or folder plus its readable text (empty when the format is not readable here).</summary>
    public static (CardMeta Meta, string Text) File(string path, CancellationToken ct = default, RunContext? context = null)
    {
        ct.ThrowIfCancellationRequested();
        using var phase = context?.Phase("file_parse");
        var meta = new CardMeta { Src = path, Title = Path.GetFileName(path.TrimEnd('\\', '/')) };
        string text = "";
        try
        {
            if (Directory.Exists(path))
            {
                meta.FType = "文件夹";
                var items = Directory.EnumerateFileSystemEntries(path).Take(40).Select(p => { ct.ThrowIfCancellationRequested(); return Path.GetFileName(p); }).ToList();
                text = string.Join("\n", items);
                meta.Desc = $"{items.Count}{(items.Count == 40 ? "+" : "")} 项：" + string.Join("、", items.Take(8));
            }
            else if (System.IO.File.Exists(path))
            {
                var f = new FileInfo(path);
                meta.FType = f.Extension.TrimStart('.').ToUpperInvariant();
                meta.Size = f.Length;
                meta.Mtime = new DateTimeOffset(f.LastWriteTimeUtc).ToUnixTimeMilliseconds();
                text = FileText(path, ct);
                if (text.Length > 0) meta.Desc = SpaceRx().Replace(text.Length > 300 ? text[..300] : text, " ").Trim();
            }
            else meta.State = "missing";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { ct.ThrowIfCancellationRequested(); meta.Error = e.Message; }
        return (meta, text);
    }

    /// <summary>Plain text of a local document we can read without extra libraries; "" when unsupported.</summary>
    public static string FileText(string path, CancellationToken ct = default)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stop.CancelAfter(TimeSpan.FromSeconds(15));
        var token = stop.Token;
        try
        {
            token.ThrowIfCancellationRequested();
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".docx" or ".pptx" or ".xlsx") return Cap(OfficeText(path, ext, token));
            using var fs = OpenShared(path);
            long length = fs.Length;
            if (length > 20_000_000) throw new SourceLimitException("文本文件超过 20 MB 读取上限");
            var bytes = new byte[length];
            int read = 0;
            while (read < bytes.Length)
            {
                token.ThrowIfCancellationRequested();
                int n = fs.Read(bytes, read, Math.Min(65536, bytes.Length - read));
                if (n == 0) break;
                read += n;
            }
            bytes = bytes[..read];
            token.ThrowIfCancellationRequested();
            if (ext is ".html" or ".htm") return Cap(MainText(Decode(bytes, null, true), token));
            if (bytes.AsSpan(0, Math.Min(bytes.Length, 8000)).Contains((byte)0) && !(bytes.Length > 1 && bytes[0] == 0xFF && bytes[1] == 0xFE))
                return ""; // binary (pdf, images, archives…) — left to the LLM tool / the default app
            return Cap(Text(bytes));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("文件解析超过 15 秒"); }
        catch (OperationCanceledException) { throw; }
        catch (SourceLimitException) { throw; }
        catch (RegexMatchTimeoutException) { throw new TimeoutException("HTML 解析超过匹配时间上限"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) { return ""; }
    }

    /// <summary>Read access that still works while Word / Excel / a sync client holds the file open for writing.</summary>
    static FileStream OpenShared(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    static string OfficeText(string path, string ext, CancellationToken ct)
    {
        using var file = OpenShared(path);
        long length = file.Length;
        if (length > MaxDownload) throw new SourceLimitException("Office 文件超过 25 MB 读取上限");
        var snapshot = new byte[length];
        int copied = 0;
        while (copied < snapshot.Length)
        {
            ct.ThrowIfCancellationRequested();
            int n = file.Read(snapshot, copied, Math.Min(65536, snapshot.Length - copied));
            if (n == 0) throw new InvalidDataException("Office 文件在读取时改变");
            copied += n;
        }
        using var archive = new MemoryStream(snapshot, writable: false);
        CheckZipDirectory(archive, ct);
        archive.Position = 0;
        using var zip = new ZipArchive(archive, ZipArchiveMode.Read);
        if (zip.Entries.Count > MaxArchiveEntries) throw new SourceLimitException("Office 压缩包条目过多");
        IEnumerable<ZipArchiveEntry> parts = ext switch
        {
            ".docx" => zip.Entries.Where(e => e.FullName == "word/document.xml"),
            ".pptx" => zip.Entries.Where(e => e.FullName.StartsWith("ppt/slides/slide") && e.FullName.EndsWith(".xml"))
                                  .OrderBy(e => int.TryParse(Regex.Match(e.FullName, @"\d+").Value, out var n) ? n : 0),
            _ => zip.Entries.Where(e => e.FullName == "xl/sharedStrings.xml"),
        };
        var selected = parts.ToArray();
        if (selected.Length > MaxOfficeParts) throw new SourceLimitException("Office 文本条目超过 256 个上限");
        long declared = 0;
        foreach (var part in selected)
        {
            ct.ThrowIfCancellationRequested();
            if (part.Length > MaxOfficeBytes - declared) throw new SourceLimitException("Office XML 超过累计 60 MB 上限");
            declared += part.Length;
        }
        var sb = new StringBuilder();
        long expanded = 0;
        int count = 0;
        foreach (var e in selected)
        {
            ct.ThrowIfCancellationRequested();
            if (++count > MaxOfficeParts || e.Length > MaxOfficeBytes - expanded)
                throw new SourceLimitException("Office XML 超过累计 60 MB 或 256 个文本条目上限");
            using var stream = new LimitedStream(e.Open(), n =>
            {
                expanded += n;
                if (expanded > MaxOfficeBytes) throw new SourceLimitException("Office 解压内容超过累计 60 MB 上限");
            }, ct);
            using var reader = new StreamReader(stream);
            var buffer = new char[8192];
            var tag = new StringBuilder();
            bool inTag = false;
            for (int n; (n = reader.Read(buffer, 0, buffer.Length)) > 0;)
            {
                ct.ThrowIfCancellationRequested();
                var text = new StringBuilder();
                for (int i = 0; i < n; i++)
                {
                    char ch = buffer[i];
                    if (!inTag && ch == '<') { inTag = true; tag.Clear(); }
                    else if (inTag && ch == '>')
                    {
                        var value = tag.ToString();
                        if (value is "/w:p" or "/a:p" or "/si") text.Append('\n');
                        else if (value is "w:tab/" or "w:tab /") text.Append('\t');
                        inTag = false;
                    }
                    else if (inTag)
                    {
                        if (tag.Length >= 4096) throw new SourceLimitException("Office XML 标签过长");
                        tag.Append(ch);
                    }
                    else text.Append(ch);
                }
                sb.Append(text);
                if (sb.Length > MaxText * 2) { sb.Remove(18_000, sb.Length - 22_000); sb.Insert(18_000, "\n…\n"); }
            }
            sb.Append('\n');
        }
        ct.ThrowIfCancellationRequested();
        return BlankRx().Replace(WebUtility.HtmlDecode(sb.ToString()), "\n\n").Trim();
    }

    // Check central directory limits before ZipArchive allocates an object/name for every entry.
    static void CheckZipDirectory(Stream file, CancellationToken ct)
    {
        if (file.Length < 22) throw new InvalidDataException("Office ZIP 不完整");
        var tail = new byte[(int)Math.Min(file.Length, 65557)];
        file.Position = file.Length - tail.Length;
        file.ReadExactly(tail);
        int end = -1;
        for (int i = tail.Length - 22; i >= 0; i--)
        {
            ct.ThrowIfCancellationRequested();
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i, 4)) == 0x06054b50
                && i + 22 + BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 20, 2)) == tail.Length) { end = i; break; }
        }
        if (end < 0) throw new InvalidDataException("Office ZIP 缺少目录结尾");
        var eocd = tail.AsSpan(end);
        ushort entries = BinaryPrimitives.ReadUInt16LittleEndian(eocd[10..]);
        uint bytes = BinaryPrimitives.ReadUInt32LittleEndian(eocd[12..]), offset = BinaryPrimitives.ReadUInt32LittleEndian(eocd[16..]);
        if (entries == ushort.MaxValue || bytes == uint.MaxValue || offset == uint.MaxValue)
            throw new SourceLimitException("Office ZIP64 超出本地解析范围");
        if (entries > MaxArchiveEntries || bytes > 4_000_000) throw new SourceLimitException("Office ZIP 目录超过 4096 条目或 4 MB 上限");
        if (BinaryPrimitives.ReadUInt16LittleEndian(eocd[4..]) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(eocd[6..]) != 0)
            throw new InvalidDataException("不支持分卷 Office ZIP");
        long directoryEnd = (long)offset + bytes;
        if (directoryEnd > file.Length - tail.Length + end) throw new InvalidDataException("Office ZIP 目录越界");
        file.Position = offset;
        var header = new byte[46];
        int actual = 0;
        while (file.Position < directoryEnd)
        {
            ct.ThrowIfCancellationRequested();
            if (++actual > MaxArchiveEntries) throw new SourceLimitException("Office ZIP 目录超过 4096 条目上限");
            if (directoryEnd - file.Position < header.Length) throw new InvalidDataException("Office ZIP 目录不完整");
            file.ReadExactly(header);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x02014b50) throw new InvalidDataException("Office ZIP 目录无效");
            int name = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28, 2));
            int extra = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(30, 2));
            int comment = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32, 2));
            if (name > 4096) throw new SourceLimitException("Office ZIP 文件名过长");
            long next = file.Position + name + extra + comment;
            if (next > directoryEnd) throw new InvalidDataException("Office ZIP 目录条目越界");
            file.Position = next;
        }
        if (actual != entries) throw new InvalidDataException("Office ZIP 目录数量不一致");
    }

    sealed class LimitedStream(Stream source, Action<int> consume, CancellationToken ct) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            ct.ThrowIfCancellationRequested();
            int n = source.Read(buffer, offset, count);
            ct.ThrowIfCancellationRequested();
            consume(n);
            return n;
        }
        protected override void Dispose(bool disposing) { if (disposing) source.Dispose(); base.Dispose(disposing); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ================= downloads =================

    /// <summary>".pdf" / ".docx" / ".pptx" / ".xlsx" for a document response (by type, or by file name when the type is generic).</summary>
    static string? DocExt(string type, string? name)
    {
        var ext = type switch
        {
            "application/pdf" => ".pdf",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
            "application/vnd.openxmlformats-officedocument.presentationml.presentation" => ".pptx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => ".xlsx",
            _ => null,
        };
        if (ext == null && type is "" or "application/octet-stream" or "binary/octet-stream" or "application/x-download" or "application/force-download" or "application/download")
            ext = Path.GetExtension(name ?? "").ToLowerInvariant() is (".pdf" or ".docx" or ".pptx" or ".xlsx") and var e ? e : null;
        return ext;
    }

    static string? FileName(ContentDispositionHeaderValue? cd, string url)
    {
        var n = (cd?.FileNameStar ?? cd?.FileName)?.Trim('"', ' ');
        if (string.IsNullOrEmpty(n) && Uri.TryCreate(url, UriKind.Absolute, out var u)) n = Path.GetFileName(u.LocalPath);
        return string.IsNullOrEmpty(n) ? null : Path.GetFileName(n); // a name, never a path
    }

    static bool IsPdf(byte[] b) => b.AsSpan(0, Math.Min(b.Length, 1024)).IndexOf("%PDF-"u8) >= 0;

    /// <summary>Image type by magic bytes (the formats WPF decodes out of the box, plus WebP via the Windows codec).</summary>
    internal static string? ImageExt(byte[] b)
    {
        var s = b.AsSpan();
        if (s.StartsWith(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A })) return ".png";
        if (s.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF })) return ".jpg";
        if (s.StartsWith("GIF87a"u8) || s.StartsWith("GIF89a"u8)) return ".gif";
        if (s.Length > 26 && s.StartsWith("BM"u8)) return ".bmp";
        if (s.Length > 12 && s.StartsWith("RIFF"u8) && s[8..12].SequenceEqual("WEBP"u8)) return ".webp";
        return null;
    }

    /// <summary>Content-addressed like images: files/&lt;sha1-20&gt;.ext, written atomically (a parallel fetch may race us).</summary>
    static string SaveDownload(byte[] bytes, string ext)
    {
        var dir = Path.Combine(Store.DefaultHome, "files");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant()[..20] + ext);
        if (System.IO.File.Exists(path)) return path;
        var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        System.IO.File.WriteAllBytes(tmp, bytes);
        try { System.IO.File.Move(tmp, path); }
        catch (IOException) { try { System.IO.File.Delete(tmp); } catch { } }
        return path;
    }

    // ================= page structure =================

    /// <summary>Content containers of popular sites where generic heuristics do poorly (id or class attribute values).</summary>
    static readonly string[] KnownContainers = { "js_content", "RichText", "article_content", "markdown-body", "article-content", "post-content", "entry-content", "rich_media_content" };

    /// <summary>Inner HTML of the first element whose id or class contains <paramref name="token"/> (balanced by tag name).</summary>
    static string? Element(string html, string token, CancellationToken ct)
    {
        var open = Regex.Match(html, $@"<(\w+)\b[^>]*\b(?:id|class)\s*=\s*[""'][^""']*\b{Regex.Escape(token)}\b[^""']*[""'][^>]*>", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(RegexMilliseconds));
        if (!open.Success) return null;
        var tag = open.Groups[1].Value;
        var rx = new Regex($@"<(/?){tag}\b[^>]*>", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(RegexMilliseconds));
        int depth = 1, start = open.Index + open.Length;
        for (var m = rx.Match(html, start); m.Success; m = m.NextMatch())
        {
            ct.ThrowIfCancellationRequested();
            depth += m.Groups[1].Value == "/" ? -1 : m.Value.EndsWith("/>") ? 0 : 1;
            if (depth == 0) return html[start..m.Index];
        }
        return html[start..];
    }

    /// <summary>schema.org JSON-LD: headline, description, image, publisher and articleBody (handles @graph and arrays).</summary>
    static (string? Title, string? Desc, string? Image, string? Site, string? Body) JsonLd(string html, CancellationToken ct)
    {
        string? title = null, desc = null, image = null, site = null, body = null;
        int blocks = 0;
        foreach (Match m in JsonLdRx().Matches(html))
        {
            ct.ThrowIfCancellationRequested();
            if (++blocks > 64) throw new SourceLimitException("HTML 的 JSON-LD 块过多");
            try
            {
                using var doc = JsonDocument.Parse(m.Groups[1].Value.Trim());
                var stack = new Stack<JsonElement>();
                stack.Push(doc.RootElement);
                int nodes = 0;
                while (stack.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    if (++nodes > 4096 || stack.Count > 4096) throw new SourceLimitException("JSON-LD 节点过多");
                    var e = stack.Pop();
                    if (e.ValueKind == JsonValueKind.Array) { foreach (var x in e.EnumerateArray().Reverse()) stack.Push(x); continue; } // document order
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    if (e.TryGetProperty("@graph", out var g)) stack.Push(g);
                    title ??= Str(e, "headline");
                    desc ??= Str(e, "description");
                    image ??= Str(e, "image", "url");
                    site ??= Str(e, "publisher", "name");
                    if (Str(e, "articleBody") is { } b && b.Length > (body?.Length ?? 0)) body = b;
                }
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException) { } // one odd block must not hide the others
        }
        return (Clean(title), Clean(desc), image, Clean(site), body == null ? null : WebUtility.HtmlDecode(body).Trim());
    }

    /// <summary>A string property; for an object (or an array's first item) its <paramref name="sub"/> string: publisher → name, image → url.</summary>
    static string? Str(JsonElement e, string key, string? sub = null)
    {
        if (!e.TryGetProperty(key, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Array) v = v.EnumerateArray().FirstOrDefault();
        if (v.ValueKind == JsonValueKind.Object && sub != null && v.TryGetProperty(sub, out var inner)) v = inner;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    /// <summary>
    /// JS-rendered pages: let the Edge that ships with Windows render the page headlessly and dump the DOM
    /// (separate profile so it never hands the URL to the user's running browser). Null when unavailable.
    /// </summary>
    public static async Task<Page?> Rendered(string url, CancellationToken ct = default, RunContext? context = null)
    {
        var edge = EdgePath();
        if (edge == null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        var profile = Path.Combine(Store.DefaultHome, "edge");
        string[] args = ["--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check", "--mute-audio", "--disable-extensions",
                         $"--user-data-dir={profile}", "--virtual-time-budget=8000", "--dump-dom", uri.AbsoluteUri];
        using var queueStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        queueStop.CancelAfter(TimeSpan.FromSeconds(25));
        try { using var phase = context?.Phase("edge_queue"); await EdgeGate.WaitAsync(queueStop.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        Proc.Result r;
        try { using var phase = context?.Phase("edge"); r = await Proc.Run(edge, args, null, TimeSpan.FromSeconds(25), ct, maxOutput: MaxPage).ConfigureAwait(false); }
        catch (Exception e) when (e is TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
        finally { EdgeGate.Release(); }
        if (r.Out.Length < 200) return null;
        using var parsed = context?.Phase("html_parse");
        return await Task.Run(() => FromHtml(uri.AbsoluteUri, r.Out, ct), ct).ConfigureAwait(false); // regex work stays off the UI thread
    }

    static string? EdgePath()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe");
            if (k?.GetValue(null) is string p && System.IO.File.Exists(p)) return p;
        }
        catch { }
        foreach (var root in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
        {
            var p = Path.Combine(Environment.GetFolderPath(root), "Microsoft", "Edge", "Application", "msedge.exe");
            if (System.IO.File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>Long texts keep their beginning and their end (conclusions often sit there); never splits a surrogate pair.</summary>
    public static string Cap(string s)
    {
        if (s.Length <= MaxText) return s;
        int head = char.IsHighSurrogate(s[17_999]) ? 17_999 : 18_000, tail = s.Length - 4_000;
        if (char.IsLowSurrogate(s[tail])) tail++;
        return s[..head] + "\n…\n" + s[tail..];
    }

    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(s))]
    static string? Clean(string? s) => s == null ? null : SpaceRx().Replace(WebUtility.HtmlDecode(s), " ").Trim();
    public static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.StartsWith("www.") ? u.Host[4..] : u.Host : url;

    static string? Attr(string tag, string name)
    {
        var m = Regex.Match(tag, $@"\b{name}\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(RegexMilliseconds));
        return !m.Success ? null : m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
    }

    [GeneratedRegex(@"<meta\b[^>]*>", RegexOptions.IgnoreCase, RegexMilliseconds)] private static partial Regex MetaRx();
    [GeneratedRegex(@"token|sig|credential|secret|key|auth|code|otp|ticket|session|password|pwd", RegexOptions.IgnoreCase, RegexMilliseconds)] private static partial Regex SecretNameRx();
    [GeneratedRegex(@"keywords?|author", RegexOptions.IgnoreCase, RegexMilliseconds)] private static partial Regex BenignNameRx(); // search / blog params, not secrets
    [GeneratedRegex(@"<script[^>]+application/ld\+json[^>]*>([\s\S]*?)</script>", RegexOptions.IgnoreCase, RegexMilliseconds)] private static partial Regex JsonLdRx();
    [GeneratedRegex(@"just a moment|attention required|captcha|verify you are human|请完成安全验证|访问验证|人机验证|enable javascript", RegexOptions.IgnoreCase, RegexMilliseconds)] private static partial Regex ChallengeRx();
    [GeneratedRegex(@"<title[^>]*>([\s\S]*?)</title>", RegexOptions.IgnoreCase, RegexMilliseconds)] private static partial Regex TitleRx();
    [GeneratedRegex(@"<meta[^>]+charset\s*=\s*[""']?\s*([\w-]+)", RegexOptions.IgnoreCase, RegexMilliseconds)] private static partial Regex CharsetRx();
    [GeneratedRegex(@"<!--[\s\S]*?-->|<(script|style|noscript|svg|template|iframe|canvas|head|select|button)\b[\s\S]*?</\1\s*>", RegexOptions.IgnoreCase, RegexMilliseconds)] private static partial Regex JunkRx();
    [GeneratedRegex(@"<(nav|header|footer|aside|menu|dialog)\b[\s\S]*?</\1\s*>", RegexOptions.IgnoreCase, RegexMilliseconds)] private static partial Regex ChromeRx();
    [GeneratedRegex(@"<form\b[^>]*>([\s\S]*?)</form\s*>", RegexOptions.IgnoreCase, RegexMilliseconds)] private static partial Regex FormRx();
    [GeneratedRegex(@"<article\b[^>]*>([\s\S]*?)</article\s*>", RegexOptions.IgnoreCase, RegexMilliseconds)] private static partial Regex ArticleRx();
    [GeneratedRegex(@"<main\b[^>]*>([\s\S]*?)</main\s*>", RegexOptions.IgnoreCase, RegexMilliseconds)] private static partial Regex MainRx();
    [GeneratedRegex(@"<body\b[^>]*>([\s\S]*?)(?:</body\s*>|$)", RegexOptions.IgnoreCase, RegexMilliseconds)] private static partial Regex BodyRx();
    [GeneratedRegex(@"<h([1-6])\b[^>]*>", RegexOptions.IgnoreCase, RegexMilliseconds)] private static partial Regex HeadingTagRx();
    [GeneratedRegex(@"<li\b[^>]*>", RegexOptions.IgnoreCase, RegexMilliseconds)] private static partial Regex LiTagRx();
    [GeneratedRegex(@"<(br|/p|/div|/h[1-6]|/li|/tr|/section|/blockquote|/pre|/table|/ul|/ol|/dd|/dt)\b[^>]*>|<(p|div|tr|pre|blockquote|section)\b[^>]*>", RegexOptions.IgnoreCase, RegexMilliseconds)] private static partial Regex BlockTagRx();
    [GeneratedRegex(@"<[^>]+>", RegexOptions.None, RegexMilliseconds)] private static partial Regex TagRx();
    [GeneratedRegex(@"[ \t\u00A0\u3000]+", RegexOptions.None, RegexMilliseconds)] private static partial Regex SpaceRx();
    [GeneratedRegex(@"\s+", RegexOptions.None, RegexMilliseconds)] private static partial Regex WhiteRx();
    [GeneratedRegex(@"\n{3,}", RegexOptions.None, RegexMilliseconds)] private static partial Regex BlankRx();
}
