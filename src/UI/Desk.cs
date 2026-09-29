using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using PinCC.Core;
using Page = PinCC.Core.Page;

namespace PinCC.UI;

/// <summary>
/// Owns every card window and is the only place that changes cards (DRY): each user gesture is one Store group, so
/// Ctrl+Z undoes exactly one gesture. Layout is computed, never stored. One 1 s tick drives sync with the CLI,
/// reminders, full-screen detection and daily maintenance.
/// </summary>
public sealed class Desk
{
    public Store Store { get; }
    readonly Dictionary<long, CardWindow> _cards = new();
    readonly List<HubWindow> _hubs = new();
    readonly MessageWindow _msg = new();
    readonly Dictionary<string, BitmapSource?> _images = new();
    readonly Dictionary<string, (int W, int H)?> _sizes = new();
    readonly List<long> _undo = new();
    readonly HashSet<long> _summarizing = new();
    readonly HashSet<CardWindow> _reenrich = new();
    readonly HashSet<CardWindow> _overflowHidden = new();
    readonly HashSet<(long Id, long At)> _rung = new();
    readonly Dictionary<string, int> _overflowCount = new();
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _layout = new() { Interval = TimeSpan.FromMilliseconds(100) };
    readonly DispatcherTimer _gestureEnd = new() { Interval = TimeSpan.FromMilliseconds(600) };
    Hotkeys _hotkeys = null!;
    ManageWindow? _manage;
    Toast? _toast;
    Group? _gesture;
    CardWindow? _peek;
    long _version, _nextTemp = -1;
    bool _hidden, _deferred, _pendingAlarm, _forceNext;
    (string Device, IntPtr Hwnd)? _fullscreen;
    int _lastMinute = -1, _tickCount;
    string _day = "";

    public Desk(Store store) => Store = store;

    // ================= settings (cached; ApplySettings re-reads) =================

    public double Gap { get; private set; } = 12;
    public double Inset => 16;
    public double FontSize { get; private set; } = 14;
    public Corner Corner { get; private set; } = Corner.TopRight;
    public Corner HubCorner { get; private set; } = Corner.BottomRight;
    public string ColorMode { get; private set; } = "rotate";
    public Llm? Llm { get; private set; }
    public bool AutoSummary { get; private set; } = true;
    public Dictionary<string, string?> HotkeyStatus { get; } = new();
    public Hotkeys Hotkeys => _hotkeys;
    public bool Hidden => _hidden;
    public IReadOnlyCollection<CardWindow> Cards => _cards.Values;

    static Corner ParseCorner(string s, Corner fallback) => s switch
    {
        "tl" => Corner.TopLeft, "tr" => Corner.TopRight, "bl" => Corner.BottomLeft, "br" => Corner.BottomRight, _ => fallback,
    };

    public static string CornerKey(Corner c) => c switch { Corner.TopLeft => "tl", Corner.TopRight => "tr", Corner.BottomLeft => "bl", _ => "br" };

    internal void LoadSettings()
    {
        Gap = Math.Clamp(Store.SettingInt("tile.gap", 12), 4, 40);
        FontSize = double.TryParse(Store.Setting("text.size"), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? Math.Clamp(f, 12, 20) : 14;
        Corner = ParseCorner(Store.Setting("tile.corner", "tr"), Corner.TopRight);
        HubCorner = ParseCorner(Store.Setting("hub.corner", "br"), Corner.BottomRight);
        ColorMode = Store.Setting("color.mode", "rotate");
        Llm = Llm.FromSettings(Store);
        AutoSummary = Store.SettingBool("llm.auto", true);
    }

    public void ApplySettings()
    {
        var before = Llm?.Describe();
        LoadSettings();
        if (Llm != null && Llm.Describe() != before) CatchUpSummaries(); // a tool was just set up or changed
        RegisterHotkeys();
        foreach (var w in _cards.Values) w.Render();
        RebuildHubs();
        Relayout(force: true);
    }

    public object Resource(string key) => Application.Current.FindResource(key);

    // ================= lifecycle =================

    public void Start(bool startup)
    {
        LoadSettings();
        Palette.ApplyChrome(Application.Current.Resources);
        Maintenance();
        _version = Store.DataVersion;

        foreach (var c in Store.Active()) Adopt(c);
        RebuildHubs();
        DoLayout(force: true);

        _hotkeys = new Hotkeys(_msg);
        RegisterHotkeys();
        _msg.Message += OnSystemMessage;
        _tick.Tick += (_, _) => Tick();
        _layout.Tick += (_, _) => { _layout.Stop(); var f = _forceNext; _forceNext = false; DoLayout(f); };
        _gestureEnd.Tick += (_, _) => { _gestureEnd.Stop(); _gesture = null; };
        _tick.Start();

        // Reminders already due when the desk opens ring once, together.
        var due = _cards.Values.Where(w => w.Card.Overdue).ToList();
        foreach (var w in due) _rung.Add((w.Card.Id, w.Card.RemindAt!.Value));
        if (due.Count > 0) Alarm(due);
        foreach (var w in _cards.Values.Where(w => w.Card.Src != null && w.Card.SrcInfo == null).ToList()) _ = Enrich(w); // added by the CLI while closed
        CatchUpSummaries();

        if (Store.Setting("llm.tool") == null && Store.Setting("llm.custom") == null && Store.Setting("llm.detected") == null) _ = DetectLlm();
        if (Store.Setting("autostart") == null && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PINCC_HOME")))
        {
            // First run: start with Windows by default, so cards and reminders come back after a reboot.
            try { SetAutoStart(true); Notify("已设为开机启动，可在“管理 › 设置”中关闭"); } catch { }
        }
        else if (!startup && _cards.Count == 0) Notify("点 + 新建卡片 · 悬停 + 一键平铺/折叠/最小化 · 右键更多");
    }

    /// <summary>Windows is logging off (the user may still cancel): save open edits, keep everything running.</summary>
    public void Flush()
    {
        foreach (var w in _cards.Values.Where(w => w.Editing).ToList()) w.EndEdit(returnFocus: false);
    }

    public void Shutdown()
    {
        Flush();
        _tick.Stop();
        _hotkeys?.Dispose();
        _msg.Dispose();
    }

    void Maintenance()
    {
        _day = DateTime.Now.ToString("yyyyMMdd");
        try { Store.Purge(Store.SettingInt("retention.days", 30)); } catch { /* never block the desk on cleanup */ }
        try { Store.Backup(); } catch { }
    }

    public async Task<Llm?> DetectLlm()
    {
        var found = await Llm.Detect();
        if (found != null) found.Save(Store);
        Store.SetSetting("llm.detected", true); // first-run detection happens once; afterwards the settings page decides
        LoadSettings();
        foreach (var w in _cards.Values) w.Render();
        CatchUpSummaries();
        return found;
    }

    /// <summary>
    /// Readable sources that should have an automatic summary but have none (read before a tool was found, or the
    /// app closed mid-run) get it now. Unreadable ("blocked", "missing") and manual ones wait for the user.
    /// </summary>
    void CatchUpSummaries()
    {
        if (Llm == null) return;
        foreach (var w in _cards.Values.Where(w => w.Ref > 0 && w.Busy == null && w.Card.SrcInfo is { State: null } && string.IsNullOrWhiteSpace(w.Card.Summary) && AutoSummarizes(w.Card)).ToList())
            Summarize(w, userAsked: false);
    }

    public static bool AutoStart
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            return k?.GetValue("PinCC") is string;
        }
    }

    public void SetAutoStart(bool on)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
        {
            if (on) k.SetValue("PinCC", $"\"{Environment.ProcessPath}\" --startup");
            else k.DeleteValue("PinCC", false);
        }
        Store.SetSetting("autostart", on);
    }

    CardWindow Adopt(Card c)
    {
        var w = new CardWindow(this, c) { Ref = c.Id };
        w.EnsureHandle();
        _cards[c.Id] = w;
        return w;
    }

    void Close(CardWindow w)
    {
        if (!_cards.Remove(w.Ref)) return; // already closed (a strip timer and a reload may both get here)
        _overflowHidden.Remove(w);
        _reenrich.Remove(w);
        if (_peek == w) _peek = null;
        w.Close();
    }

    void Rekey(CardWindow w, long id)
    {
        _cards.Remove(w.Ref);
        w.Ref = id;
        _cards[id] = w;
    }

    /// <summary>A finished gesture becomes the newest undo step (moved to the top if it was already there).</summary>
    void Push(Group g)
    {
        if (g.Empty) return;
        _undo.Remove(g.Number);
        _undo.Add(g.Number);
    }

    /// <summary>Continuous gestures (wheel opacity, zoom) share one group until they pause.</summary>
    Group Gesture()
    {
        _gesture ??= Store.Begin();
        _gestureEnd.Stop();
        _gestureEnd.Start();
        return _gesture;
    }

    /// <summary>One journaled change to one card, applied to its window; returns the stored card.</summary>
    Card? Change(CardWindow w, Action<Card> change, string op, Group? g = null, bool push = true)
    {
        if (w.Ref < 0) { change(w.Card); w.Render(); return w.Card; } // not stored yet (a new card being typed)
        g ??= Store.Begin();
        var r = Store.Mutate(g, w.Card.Id, change, op);
        if (push) Push(g);
        if (r == null) { Close(w); return null; }
        w.Update(r);
        return r;
    }

    // ================= derived presentation =================

    /// <summary>The palette index shown. A card that is still being typed previews the color it will get.</summary>
    public int ColorOf(Card c) => c.Id == 0 && c.Color == null && ColorMode == "rotate" ? NextColor : c.ShownColor(ColorMode);

    /// <summary>Effective topmost: pinned or overdue — unless a full-screen app is on that monitor.</summary>
    public bool EffTopmost(CardWindow w) => (w.Card.Topmost || w.Card.Overdue) && !(w.Hwnd != IntPtr.Zero && Covered(ScreenOf(w)));

    bool Covered(Screen s) => _fullscreen is { } f && (f.Device == "*" || f.Device == s.Device);

    public double MaxAutoHeight(CardWindow w)
    {
        var s = w.Hwnd == IntPtr.Zero ? Screens.ById(w.Card.Mon) ?? Screens.Primary : ScreenOf(w);
        return Math.Max(CardWindow.MinH, Math.Min(360, s.Dip(s.Work.H) * 0.4));
    }

    public Screen ScreenOf(CardWindow w) => w.Hwnd == IntPtr.Zero ? Screens.ById(w.Card.Mon) ?? Screens.Primary : Screens.Of(w.Rect);

    static readonly string[] Readable =
        { ".txt", ".md", ".markdown", ".csv", ".tsv", ".json", ".log", ".xml", ".yaml", ".yml", ".ini", ".html", ".htm", ".docx", ".pptx", ".xlsx",
          ".cs", ".py", ".js", ".ts", ".java", ".c", ".cpp", ".h", ".go", ".rs", ".sql", ".sh", ".ps1", ".bat" };

    /// <summary>Automatic summaries: public links, and documents we (or a file-reading CLI) can actually read.</summary>
    public bool AutoSummarizes(Card c) => AutoSummary && Llm is { } llm && c.Src is { } src && (c.IsLink
        ? Fetch.SafeToAuto(src)
        : Directory.Exists(src) || Readable.Contains(Path.GetExtension(src).ToLowerInvariant()) || Path.GetExtension(src).Equals(".pdf", StringComparison.OrdinalIgnoreCase) && llm.Supports(LlmMode.File));

    /// <summary>For the hub: the color the next card gets (from the AUTOINCREMENT sequence), overflow and warnings.</summary>
    public int NextColor
    {
        get
        {
            long next = Convert.ToInt64(Store.Db.Scalar("SELECT ifnull((SELECT seq FROM sqlite_sequence WHERE name='cards'),0)+1"));
            return ColorMode == "rotate" ? Card.Rotation[(int)(next % 7)] : 7;
        }
    }

    public int OverflowCount(Screen s) => _overflowCount.GetValueOrDefault(s.Id);
    public int OverdueCount(Screen s) => _cards.Values.Count(w => w.Card.Overdue && w.Strip == null && ScreenOf(w).Id == s.Id);
    public bool HotkeyProblem => HotkeyStatus.Values.Any(v => v != null && (v.StartsWith("被") || v.StartsWith("与")));
    public bool Fullscreen(Screen s) => Covered(s);

    // ================= creating & ingesting =================

    /// <summary>
    /// A new card on <paramref name="s"/>. An empty card lives only in its window until it gets content (no row, no
    /// history if abandoned). <paramref name="returnFocus"/> gets the focus back after Esc (quick capture).
    /// </summary>
    public CardWindow NewCard(Screen s, string body = "", bool edit = true, List<string>? images = null, Size? size = null,
        IntPtr returnFocus = default, Group? group = null)
    {
        _hidden = false;
        var c = new Card
        {
            Body = body, Images = images ?? new(), Mon = s.Id, W = size?.Width ?? 260, H = size?.Height,
            Topmost = Store.SettingBool("top:" + s.Id, false), CreatedAt = When.NowMs, // newest: it takes the first slot now, not after typing
        };
        CardWindow w;
        if (body.Length > 0 || c.Images.Count > 0)
        {
            var g = group ?? Store.Begin();
            c = Store.Insert(g, c);
            Push(g);
            w = Adopt(c);
        }
        else
        {
            w = new CardWindow(this, c) { Ref = _nextTemp-- };
            w.EnsureHandle();
            _cards[w.Ref] = w;
        }
        w.ReturnFocus = returnFocus;
        DoLayout(force: true);
        if (!w.IsVisible) w.Show();
        Native.Front(w.Hwnd, w.Topmost);
        if (edit) w.BeginEdit();
        if (c.Src != null) _ = Enrich(w);
        return w;
    }

    /// <summary>Pixel size of a picture, read from the file header (no full decode, nothing cached but the size).</summary>
    public (int W, int H)? ImageSize(string src)
    {
        if (_sizes.TryGetValue(src, out var cached)) return cached;
        (int, int)? size = null;
        try
        {
            using var fs = new FileStream(Store.ResolveImage(src), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var f = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
            if (f.PixelWidth > 0 && f.PixelHeight > 0) size = (f.PixelWidth, f.PixelHeight);
        }
        catch { }
        return _sizes[src] = size;
    }

    public double? ImageAspect(string src) => ImageSize(src) is { } s ? (double)s.W / s.H : null;

    /// <summary>A picture's natural size in DIP on <paramref name="s"/>: 1:1 physical pixels, capped at half the work area.</summary>
    Size NaturalSize(Screen s, string img)
    {
        if (ImageSize(img) is not { } px) return new Size(260, 200);
        double w = px.W / s.Scale, h = px.H / s.Scale;
        double k = Math.Min(1, Math.Min(s.Dip(s.Work.W) * 0.5 / w, s.Dip(s.Work.H) * 0.5 / h));
        return new Size(Math.Max(48, Math.Round(w * k)), Math.Max(48, Math.Round(h * k)));
    }

    public CardWindow NewImageCard(Screen s, string img, Group? group = null) =>
        NewCard(s, "", edit: false, images: [img], size: NaturalSize(s, img), group: group);

    /// <summary>
    /// Click on +: a picture copied since PinCC last used the clipboard (a screenshot, "copy image", image files) becomes a
    /// 1:1 picture card at once; otherwise a new empty card to type in. The same copy is pinned once, so the next click on
    /// + opens an empty card again.
    /// </summary>
    public void NewFromHub(Screen s, IntPtr returnFocus)
    {
        if (ClipData.Fresh && ClipData.Current() is { } data && ClipData.Pictures(data, Store) is { Count: > 0 } imgs)
        {
            var g = Store.Begin();
            foreach (var img in imgs) NewImageCard(s, img, g).ShowBadge("剪贴板图片 · 再点 + 新建空白卡片");
            return;
        }
        NewCard(s, returnFocus: returnFocus);
    }

    /// <summary>Clipboard or a drop on the hub (one undo step): one card per file, a link, text (with its browser source), or a picture.</summary>
    public void CreateFrom(IDataObject? data, Screen s)
    {
        if (data == null) return;
        var g = Store.Begin();
        if (ClipData.Files(data) is { Length: > 0 } files)
        {
            foreach (var f in files.Take(12))
                if (ClipData.IsImageFile(f) && ClipData.StoreFile(f, Store) is { } img) NewImageCard(s, img, g);
                else NewCard(s, f, edit: false, group: g);
            return;
        }
        var text = ClipData.Text(data)?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            var from = ClipData.SourceUrl(data);
            NewCard(s, from != null && !text.Contains(from) && !Card.IsUrl(text) ? $"{text}\n\n[来源 · {Fetch.Host(from)}]({from})" : text, edit: false, group: g);
            return;
        }
        if (ClipData.ImagePng(data) is { } png) NewImageCard(s, Store.SaveImage(png), g);
        else Notify("剪贴板里没有可用的内容");
    }

    /// <summary>Drop or Ctrl+V onto a card in view mode: pictures join it, files and text are appended as lines.</summary>
    public void Ingest(CardWindow w, IDataObject data)
    {
        var imgs = new List<string>();
        var lines = new List<string>();
        if (ClipData.Files(data) is { Length: > 0 } files)
        {
            foreach (var f in files)
                if (ClipData.IsImageFile(f) && ClipData.StoreFile(f, Store) is { } img) imgs.Add(img);
                else lines.Add(f);
        }
        else if (ClipData.Text(data)?.Trim() is { Length: > 0 } text) lines.Add(text);
        else if (ClipData.ImagePng(data) is { } png) imgs.Add(Store.SaveImage(png));
        if (imgs.Count == 0 && lines.Count == 0) return;
        Change(w, c =>
        {
            c.Images.AddRange(imgs);
            if (lines.Count > 0) c.Body = (c.Body.TrimEnd() + "\n" + string.Join("\n", lines)).Trim('\n');
        }, "edit");
        MaybeEnrich(w);
        Relayout(force: true);
    }

    /// <summary>Paste while editing: pictures join the card; returns lines to insert for files ("" when none), or null for plain text.</summary>
    public string? IngestImages(CardWindow w, IDataObject data, bool textPreferred)
    {
        if (ClipData.Files(data) is { Length: > 0 } files)
        {
            AddImages(w, files.Where(ClipData.IsImageFile).Select(f => ClipData.StoreFile(f, Store)).OfType<string>().ToList());
            return string.Join("\n", files.Where(f => !ClipData.IsImageFile(f)));
        }
        if (textPreferred && ClipData.Text(data) != null) return null;
        if (ClipData.ImagePng(data) is not { } png) return null;
        AddImages(w, [Store.SaveImage(png)]);
        return "";
    }

    void AddImages(CardWindow w, List<string> imgs)
    {
        if (imgs.Count == 0) return;
        if (w.Ref < 0) { w.Card.Images.AddRange(imgs); return; } // stored with the first save
        var g = Store.Begin();
        var r = Store.Mutate(g, w.Card.Id, c => c.Images.AddRange(imgs), "edit");
        Push(g);
        if (r != null) w.Saved(r);
    }

    // ================= editing =================

    public void SaveBody(CardWindow w, string text, Group g)
    {
        if (w.Ref < 0)
        {
            if (text.Trim().Length == 0 && w.Card.Images.Count == 0) return;
            var c = w.Card.Clone();
            c.Body = text;
            c = Store.Insert(g, c);
            Rekey(w, c.Id);
            w.Saved(c);
            w.Render(); // the stored id decides the rotation color
            Push(g);
            return;
        }
        var r = Store.Mutate(g, w.Card.Id, c => c.Body = text, "edit");
        if (r != null) { w.Saved(r); Push(g); }
    }

    public void CommitEdit(CardWindow w, string text, Group g)
    {
        bool empty = text.Trim().Length == 0 && w.Card.Images.Count == 0;
        if (w.Ref < 0 && empty) { Close(w); Relayout(force: true); return; } // abandoned new card: no row, no history
        if (w.Ref > 0 && empty && string.IsNullOrWhiteSpace(w.Card.Summary))
        {
            // An emptied card goes away; the same undo step brings the text back.
            Store.Mutate(g, w.Card.Id, c => { c.Body = ""; c.DeletedAt = When.NowMs; }, "delete");
            Push(g);
            Close(w);
            Relayout(force: true);
            return;
        }
        SaveBody(w, text, g);
        // "Click +, paste a screenshot, click away": a lone picture floats at its natural size.
        if (w.Card.ImageOnly && w.Card.H == null) Change(w, c => { var n = NaturalSize(ScreenOf(w), c.Images[0]); c.W = n.Width; c.H = n.Height; }, "edit", g);
        w.Render();
        Relayout(force: true);
        MaybeEnrich(w);
    }

    public void ToggleTask(CardWindow w, int line) => Change(w, c => c.Body = Markdown.ToggleTask(c.Body, line), "edit");

    /// <summary>Quick add on a checklist: open items after the last task. One add session (<paramref name="g"/>) is one undo step.</summary>
    public void AddTasks(CardWindow w, string text, Group g) => Change(w, c => c.Body = Markdown.AddTasks(c.Body, text), "edit", g);

    // ================= card actions =================

    public void Complete(CardWindow w) => Finish(w, done: true);
    public void Delete(CardWindow w) => Finish(w, done: false);

    /// <summary>
    /// Complete/delete in place: the card becomes its own "撤销" strip and keeps its whole slot (nothing moves under the
    /// pointer); only when the strip expires does the card close and the desk re-tile.
    /// </summary>
    void Finish(CardWindow w, bool done)
    {
        w.EndAdd();
        if (w.Editing) w.EndEdit(returnFocus: false);
        if (!_cards.ContainsValue(w)) return;
        if (w.Ref < 0) { Close(w); Relayout(force: true); return; }
        var g = Store.Begin();
        var r = Store.Mutate(g, w.Card.Id, c => { if (done) c.DoneAt = When.NowMs; else c.DeletedAt = When.NowMs; }, done ? "done" : "delete");
        if (r == null) { Close(w); return; }
        Push(g);
        var title = w.Card.Title;
        w.Update(r);
        var id = r.Id;
        w.BeginStrip($"{(done ? "已完成" : "已删除")}「{(title.Length > 14 ? title[..14] + "…" : title)}」",
            undo: () => { _undo.Remove(g.Number); Store.Restore(g.Number); Reload(); Relayout(force: true); },
            expire: () =>
            {
                if (Store.Get(id) is { Active: true } back) { w.Update(back); Relayout(force: true); return; } // undone elsewhere meanwhile
                Close(w);
                Relayout(force: true);
            });
        var s = ScreenOf(w);
        var rect = w.StripRect;
        w.Place(new Px(rect.X, rect.Y, Math.Max(rect.W, s.Px(CardWindow.MinW)), s.Px(CardWindow.PillH)).ClampInto(s.Work));
        UpdateHubs();
    }

    public void SetReminder(CardWindow w, DateTime? at)
    {
        Change(w, c => c.RemindAt = at is { } t ? When.ToMs(t) : null, "remind");
        Tick();
        Relayout(force: true);
    }

    public void SetColor(CardWindow w, int? index) => Change(w, c => c.Color = index, "color");

    public void TogglePin(CardWindow w)
    {
        Change(w, c => c.Topmost = !c.Topmost, "view");
        Native.Front(w.Hwnd, w.Topmost);
    }

    public void SetOpacity(CardWindow w, double v) => Change(w, c => c.Opacity = v, "view", Gesture());

    public void ToggleCollapse(CardWindow w)
    {
        w.EndAdd();
        if (w.Editing) w.EndEdit(returnFocus: false);
        if (!_cards.ContainsValue(w)) return;
        w.LayoutPill = false;
        if (_peek == w) _peek = null;
        Change(w, c => c.Collapsed = !c.Collapsed, "view");
        Relayout(force: true);
    }

    /// <summary>Click on a pill: a collapsed card expands; a card squeezed by lack of space opens temporarily in place.</summary>
    public void ExpandPill(CardWindow w)
    {
        if (w.Card.Collapsed) { ToggleCollapse(w); return; }
        Peek(w);
    }

    /// <summary>Shows a card fully for a moment (until the pointer leaves it) without changing anything stored.</summary>
    void Peek(CardWindow w)
    {
        _peek = w;
        _overflowHidden.Remove(w);
        w.LayoutPill = false;
        w.Render();
        var s = ScreenOf(w);
        var r = w.Rect;
        var anchor = w.IsVisible && s.Work.Intersects(r) ? r : new Px(s.Work.Right - s.Px(Inset) - s.Px(w.Expanded.Width), s.Work.Y + s.Px(Inset), 0, 0);
        w.Place(new Px(anchor.X, anchor.Y, s.Px(w.Expanded.Width), s.Px(w.Expanded.Height)).ClampInto(s.Work));
        if (!w.IsVisible) w.Show();
        Native.Front(w.Hwnd, w.Topmost);
    }

    public void Unfree(CardWindow w) { Change(w, c => c.Free = false, "tile"); Relayout(force: true); }

    public void AutoHeight(CardWindow w) { Change(w, c => c.H = null, "resize"); Relayout(force: true); }

    public void RemoveImage(CardWindow w, int index)
    {
        var r = Change(w, c => { if (index < c.Images.Count) c.Images.RemoveAt(index); }, "edit");
        if (r != null && r.Body.Trim().Length == 0 && r.Images.Count == 0) Finish(w, done: false);
        Relayout(force: true);
    }

    /// <summary>Wheel-zoom for picture cards around the cursor (one undo step per zoom gesture).</summary>
    public void Zoom(CardWindow w, double factor)
    {
        var s = ScreenOf(w);
        var ratio = ImageAspect(w.Card.Images[0]) ?? w.Card.W / Math.Max(1, w.Expanded.Height);
        double nw = w.Card.W * factor, nh = nw / ratio;
        double min = Math.Max(48 / nw, 48 / nh); // both sides stay ≥ 48 DIP, keeping the aspect
        if (min > 1) { nw *= min; nh *= min; }
        double max = Math.Min(s.Dip(s.Work.W) / nw, s.Dip(s.Work.H) / nh);
        if (max < 1) { nw *= max; nh *= max; }
        ResizeImage(w, s, nw, nh, anchorCursor: true);
    }

    /// <summary>Middle-click on a picture card: back to 1:1 physical pixels.</summary>
    public void ZoomActual(CardWindow w)
    {
        if (ImageSize(w.Card.Images[0]) is not { } px) return;
        var s = ScreenOf(w);
        double k = Math.Min(1, Math.Min(s.Work.W / (double)px.W, s.Work.H / (double)px.H));
        ResizeImage(w, s, px.W * k / s.Scale, px.H * k / s.Scale, anchorCursor: false);
    }

    void ResizeImage(CardWindow w, Screen s, double nw, double nh, bool anchorCursor)
    {
        var r = w.Rect;
        int pw = s.Px(nw), ph = s.Px(nh);
        Px target;
        if (anchorCursor)
        {
            Native.GetCursorPos(out var p);
            double fx = (p.X - r.X) / (double)Math.Max(1, r.W), fy = (p.Y - r.Y) / (double)Math.Max(1, r.H);
            target = new Px(p.X - (int)(fx * pw), p.Y - (int)(fy * ph), pw, ph);
        }
        else target = new Px(r.X, r.Y, pw, ph);
        target = target.ClampInto(s.Work);
        Change(w, c =>
        {
            c.W = Math.Round(nw);
            c.H = Math.Round(nh);
            if (c.Free) { c.Mon = s.Id; c.X = s.Dip(target.X - s.Work.X); c.Y = s.Dip(target.Y - s.Work.Y); }
        }, "resize", Gesture());
        if (w.Card.Free) w.Place(target);
        Relayout(force: true); // the user resized the card under the pointer: don't wait for the pointer to leave
    }

    /// <summary>Ctrl+drag a picture card: hands the PNG file to other apps.</summary>
    public void DragOut(CardWindow w)
    {
        var path = Store.ResolveImage(w.Card.Images[0]);
        if (File.Exists(path)) DragDrop.DoDragDrop(w, new DataObject(DataFormats.FileDrop, new[] { path }), DragDropEffects.Copy);
    }

    /// <summary>
    /// After a user drag/resize (the only time geometry is saved): a move makes the card free (DIP offset from its
    /// monitor's work area); a resize stores the size (a width-only resize keeps a fit-content height). A dropped card is
    /// pushed clear of other free cards and the hub; a resized free card is trimmed back instead of being moved.
    /// </summary>
    public void OnUserMoved(CardWindow w, Px start, double startScale)
    {
        var r = w.Rect;
        var s = Screens.Of(r);
        double w0 = start.W / startScale, h0 = start.H / startScale, w1 = s.Dip(r.W), h1 = s.Dip(r.H);
        bool widthChanged = Math.Abs(w1 - w0) > 1, heightChanged = Math.Abs(h1 - h0) > 1;
        bool resized = widthChanged || heightChanged;
        bool free = w.Card.Free || !resized;
        w.LayoutPill = false;
        if (free)
        {
            var obstacles = _cards.Values.Where(o => o != w && o.IsVisible && o.Card.Free && ScreenOf(o).Id == s.Id).Select(o => o.Rect)
                .Concat(_hubs.Where(h => h.Screen.Id == s.Id).Select(h => h.Rect)).ToList();
            var fixedRect = resized ? Trim(r, start, obstacles, s) : Layout.PushOut(r, obstacles, s.Work, s.Px(Gap));
            if (fixedRect != r) { w.Place(fixedRect); r = fixedRect; w1 = s.Dip(r.W); h1 = s.Dip(r.H); }
        }
        Change(w, c =>
        {
            if (widthChanged) c.W = Math.Round(w1);
            if ((heightChanged || c.H != null && resized) && !w.Pill) c.H = Math.Round(h1);
            c.Mon = s.Id;
            if (free) { c.Free = true; c.X = s.Dip(r.X - s.Work.X); c.Y = s.Dip(r.Y - s.Work.Y); }
        }, resized ? "resize" : "move");
        Relayout(force: true);
    }

    /// <summary>Pulls back the edges a resize moved until the card no longer overlaps an obstacle (plus the gap).</summary>
    Px Trim(Px r, Px start, List<Px> obstacles, Screen s)
    {
        int gap = s.Px(Gap), minW = s.Px(CardWindow.MinW), minH = s.Px(CardWindow.MinH);
        int left = r.X, top = r.Y, right = r.Right, bottom = r.Bottom;
        foreach (var o in obstacles.Select(o => o.Inflate(gap)))
        {
            if (!new Px(left, top, right - left, bottom - top).Intersects(o)) continue;
            if (right != start.Right && o.X >= start.Right - 1) right = Math.Max(left + minW, o.X);
            else if (bottom != start.Bottom && o.Y >= start.Bottom - 1) bottom = Math.Max(top + minH, o.Y);
            else if (left != start.X && o.Right <= start.X + 1) left = Math.Min(right - minW, o.Right);
            else if (top != start.Y && o.Bottom <= start.Y + 1) top = Math.Min(bottom - minH, o.Bottom);
        }
        var trimmed = new Px(left, top, right - left, bottom - top);
        return obstacles.Any(o => o.Inflate(gap).Intersects(trimmed)) ? Layout.PushOut(trimmed, obstacles, s.Work, gap) : trimmed;
    }

    // ================= bulk (always all monitors; the hub, hotkeys and menus mean the same thing) =================

    /// <summary>平铺显示: every card shown, expanded and back in the tiled flow (one undo step); normal cards raised.</summary>
    public void Tile()
    {
        _hidden = false;
        _peek = null;
        var g = Store.Begin();
        foreach (var w in _cards.Values.Where(w => w.Ref > 0 && w.Strip == null && (w.Card.Free || w.Card.Collapsed)).ToList())
            Change(w, c => { c.Free = false; c.Collapsed = false; }, "tile", g);
        foreach (var w in _cards.Values.Where(w => w.LayoutPill).ToList()) { w.LayoutPill = false; w.Render(); }
        DoLayout(force: true);
        foreach (var w in _cards.Values.Where(w => w.IsVisible)) Native.Front(w.Hwnd, w.Topmost); // also lifts cards above Win+D
    }

    /// <summary>全部折叠: if any card is expanded, collapse all; otherwise expand all (one undo step).</summary>
    public void CollapseAll()
    {
        var list = _cards.Values.Where(w => w.Ref > 0 && !w.Editing && w.Strip == null).ToList();
        bool collapse = list.Any(w => !w.Card.Collapsed);
        var g = Store.Begin();
        foreach (var w in list) { w.LayoutPill = false; Change(w, c => c.Collapsed = collapse, "view", g); }
        _peek = null;
        Relayout(force: true);
    }

    /// <summary>最小化 / 恢复: hide every card (overdue ones stay), or bring them back in front of other apps.</summary>
    public void ToggleHidden()
    {
        _hidden = !_hidden;
        foreach (var w in _cards.Values.Where(w => w.Editing).ToList()) w.EndEdit(returnFocus: false);
        DoLayout(force: true); // shows raise themselves in DoLayout
    }

    public void ShowAll()
    {
        _hidden = false;
        DoLayout(force: true);
        foreach (var w in _cards.Values.Where(w => w.IsVisible)) Native.Front(w.Hwnd, w.Topmost);
    }

    /// <summary>本屏卡片置顶: a per-monitor setting — applied to its cards now and to new cards created there.</summary>
    public void PinScreen(Screen s)
    {
        bool on = !Store.SettingBool("top:" + s.Id, false);
        Store.SetSetting("top:" + s.Id, on);
        var g = Store.Begin();
        foreach (var w in _cards.Values.Where(w => w.Ref > 0 && ScreenOf(w).Id == s.Id).ToList()) Change(w, c => c.Topmost = on, "view", g);
        Notify(on ? "本屏卡片已置顶（新卡片也会置顶）" : "本屏卡片已恢复普通层级");
    }

    public void Undo()
    {
        while (_undo.Count > 0)
        {
            var grp = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
            if (Store.Restore(grp).Count == 0) continue;
            Reload();
            Relayout(force: true);
            Notify("已撤销");
            return;
        }
        Notify("没有可撤销的操作");
    }

    public void Copy(CardWindow w)
    {
        try
        {
            if (w.Card.ImageOnly && LoadImage(w.Card.Images[0], 0) is { } b) Clipboard.SetImage(b);
            else Clipboard.SetText(string.Join("\n\n", new[] { w.Card.Body, w.Card.Summary }.Where(x => !string.IsNullOrWhiteSpace(x))));
            ClipData.MarkUsed(); // our own copy is not a new picture for +
            w.ShowBadge("已复制");
        }
        catch (System.Runtime.InteropServices.COMException) { w.ShowBadge("剪贴板被占用，请重试"); }
    }

    public void Cycle(CardWindow from, int dir)
    {
        var order = _cards.Values.Where(w => w.IsVisible).OrderBy(w => w.Card, Comparer<Card>.Create(Card.Compare)).ToList();
        if (order.Count < 2) return;
        order[(order.IndexOf(from) + dir + order.Count) % order.Count].Activate();
    }

    public void OnCardMouseLeave(CardWindow w)
    {
        if (_peek == w) { _peek = null; Relayout(force: true); }
        else if (_deferred) Relayout();
    }

    public void Resized(CardWindow w) => Relayout(force: w.Editing || w.Adding || w == _peek);

    /// <summary>Opens a source or link: pages and documents with their default apps; programs are only shown in Explorer.</summary>
    public void Open(string target)
    {
        if (target.StartsWith('#')) return;
        try
        {
            if (target.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) target = new Uri(target).LocalPath;
            else if (target.StartsWith("img/", StringComparison.OrdinalIgnoreCase)) target = Store.ResolveImage(target);
            bool web = Uri.TryCreate(target, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" or "mailto";
            if (!web && File.Exists(target) && Path.GetExtension(target).ToLowerInvariant() is ".exe" or ".bat" or ".cmd" or ".ps1" or ".vbs" or ".js" or ".msi" or ".com" or ".scr" or ".lnk")
            {
                Reveal(target);
                return;
            }
            if (!web && !File.Exists(target) && !Directory.Exists(target)) { Notify("找不到：" + target); return; }
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception e) { Notify("无法打开：" + e.Message); }
    }

    public void Reveal(string path)
    {
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { }
    }

    // ================= layout =================

    public void Relayout(bool force = false)
    {
        _forceNext |= force;
        _layout.Stop();
        _layout.Start();
    }

    /// <summary>
    /// Per monitor, in three passes: hubs and free cards (pushed clear of each other, display-only) are obstacles;
    /// busy cards (editing, finishing, peeking, being dragged) keep their place and may only grow into free space;
    /// the rest are tiled in display order. Waits while the pointer rests on a tiled card of that monitor unless forced,
    /// so nothing moves under the mouse. Never writes positions.
    /// </summary>
    void DoLayout(bool force)
    {
        Screens.Invalidate();
        var screens = Screens.All();
        var primary = screens.FirstOrDefault(s => s.Primary) ?? screens[0];
        var autos = screens.ToDictionary(s => s.Id, _ => new List<CardWindow>());
        var obstacles = screens.ToDictionary(s => s.Id, s => _hubs.Where(h => h.Screen.Id == s.Id).Select(h => h.Rect).Where(r => r.W > 0).ToList());
        var moves = new List<(CardWindow W, Px R, Screen S)>();
        _deferred = false;
        var all = _cards.Values.ToList();
        bool Busy(CardWindow w) => w.Editing || w.Adding || w.Strip != null || w == _peek || w.InSizeMove;

        // 1. free cards
        foreach (var w in all.Where(w => !Busy(w) && w.Card.Free))
        {
            var c = w.Card;
            if (Screens.ById(c.Mon) is not { } home) continue; // its monitor is gone: tiled on the primary below
            var r = new Px(home.Work.X + home.Px(c.X), home.Work.Y + home.Px(c.Y), home.Px(w.DipSize.Width), home.Px(w.DipSize.Height));
            r = Layout.PushOut(r, obstacles[home.Id], home.Work, home.Px(Gap)); // display-only: the stored position is untouched
            moves.Add((w, r, home));
            obstacles[home.Id].Add(r);
            _overflowHidden.Remove(w);
        }

        // 2. busy cards stay where they are; their size follows, but growth stops at the next obstacle
        foreach (var w in all.Where(Busy))
        {
            _overflowHidden.Remove(w);
            if (!w.IsVisible && w.Strip == null) continue;
            var s = ScreenOf(w);
            var r = w.Strip != null ? w.StripRect : w.Rect;
            if (w.Strip == null && !w.InSizeMove)
            {
                var want = new Px(r.X, r.Y, s.Px(w.DipSize.Width), s.Px(w.DipSize.Height)).ClampInto(s.Work);
                if (w != _peek)
                {
                    int limit = obstacles[s.Id].Where(o => o.Inflate(s.Px(Gap)).Intersects(want) && o.Y >= r.Y).Select(o => o.Y - s.Px(Gap)).DefaultIfEmpty(want.Bottom).Min();
                    want = new Px(want.X, want.Y, want.W, Math.Max(Math.Min(r.H, want.H), limit - want.Y));
                }
                moves.Add((w, want, s));
                r = want;
            }
            obstacles[s.Id].Add(r);
        }

        // 3. tiled cards
        foreach (var w in all.Where(w => !Busy(w) && !(w.Card.Free && Screens.ById(w.Card.Mon) != null)))
            autos[(Screens.ById(w.Card.Mon) ?? primary).Id].Add(w); // a missing monitor's cards tile on the primary until it returns

        foreach (var s in screens)
        {
            var list = autos[s.Id];
            if (!force && list.Any(w => w.Hovered && w.IsVisible)) { _deferred = true; continue; }
            list.Sort((a, b) => Card.Compare(a.Card, b.Card));
            var items = list.Select(w =>
            {
                bool img = w.Card.ImageOnly, collapsed = w.Card.Collapsed && !w.Card.Overdue;
                int pill = s.Px(img ? CardWindow.Thumb : CardWindow.PillH);
                int width = collapsed && img ? pill : s.Px(w.Expanded.Width);
                return (width, collapsed ? pill : s.Px(w.Expanded.Height), pill);
            }).ToList();
            var slots = Layout.Tile(s.Work, items, obstacles[s.Id], s.Px(Gap), s.Px(Inset), Corner);
            int hidden = 0;
            for (int i = 0; i < list.Count; i++)
            {
                var w = list[i];
                if (slots[i].Hidden) { _overflowHidden.Add(w); hidden++; continue; }
                _overflowHidden.Remove(w);
                if (w.LayoutPill != slots[i].Pill) { w.LayoutPill = slots[i].Pill; w.Render(); }
                var r = slots[i].Rect;
                if (w.LayoutPill && w.Card.ImageOnly) r = new Px(r.Right - s.Px(CardWindow.Thumb), r.Y, s.Px(CardWindow.Thumb), r.H);
                moves.Add((w, r, s));
            }
            _overflowCount[s.Id] = hidden;
        }

        // Same-DPI moves go out as one batch; cross-DPI moves need the two-step placement one by one.
        var batch = new List<(IntPtr, Px)>();
        foreach (var (w, r, s) in moves)
        {
            if (w.Rect == r) continue;
            if (w.IsVisible && Math.Abs(Native.WindowScale(w.Hwnd) - s.Scale) < 0.001) batch.Add((w.Hwnd, r));
            else w.Place(r);
        }
        Native.PlaceMany(batch);

        // A minimized desk shows only overdue (and edited) cards; overflow beyond pills is hidden (+N on the hub).
        foreach (var w in all.Where(w => _cards.ContainsValue(w)))
        {
            bool visible = (!_hidden || w.Card.Overdue || w.Editing || w.Strip != null) && !_overflowHidden.Contains(w) && (w.Ref > 0 || w.Editing);
            if (visible && !w.IsVisible)
            {
                w.Show();
                Native.Front(w.Hwnd, w.Topmost); // cards coming back appear in front, not behind the apps used meanwhile
            }
            else if (!visible && w.IsVisible) w.Hide();
        }
        UpdateHubs();
    }

    void OnSystemMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == Native.WM_DISPLAYCHANGE || msg == Native.WM_SETTINGCHANGE && wParam.ToInt32() == Native.SPI_SETWORKAREA)
        {
            Screens.Invalidate();
            Application.Current.Dispatcher.BeginInvoke(() => { RebuildHubs(); Relayout(force: true); }, DispatcherPriority.Background);
        }
        else if (msg == Native.WM_TIMECHANGE)
        {
            TimeZoneInfo.ClearCachedData();
            Tick();
        }
        else if (msg == Native.WM_POWERBROADCAST && wParam.ToInt32() == 0x12) Tick(); // resumed from sleep
    }

    // ================= hubs =================

    void RebuildHubs()
    {
        foreach (var h in _hubs) h.Close();
        _hubs.Clear();
        foreach (var s in Screens.All())
        {
            var h = new HubWindow(this, s);
            _hubs.Add(h);
            h.Show();
        }
        UpdateHubs();
    }

    void UpdateHubs()
    {
        foreach (var h in _hubs) h.Refresh();
    }

    /// <summary>Dragging a hub to another corner moves every hub there (one setting, correct on any monitor and DPI).</summary>
    public void SetHubCorner(Corner c)
    {
        Store.SetSetting("hub.corner", CornerKey(c));
        HubCorner = c;
        RebuildHubs();
        Relayout(force: true);
    }

    // ================= tick: sync, reminders, full screen, files, daily =================

    void Tick()
    {
        Poll();

        // Every reminder rings once when due, however it got there (picker, snooze, CLI, a time already past).
        var due = _cards.Values.Where(w => w.Card.Overdue && w.Strip == null && !_rung.Contains((w.Card.Id, w.Card.RemindAt!.Value))).ToList();
        if (due.Count > 0)
        {
            foreach (var w in due)
            {
                _rung.Add((w.Card.Id, w.Card.RemindAt!.Value));
                w.LayoutPill = false;
                w.Render();
                w.Pulse();
            }
            DoLayout(force: true);
            Alarm(due);
        }

        var fs = Native.FullScreen();
        if (fs != _fullscreen)
        {
            _fullscreen = fs;
            foreach (var w in _cards.Values)
            {
                bool wasTop = w.Topmost;
                w.Render();
                // Clearing Topmost leaves a card at the top of the normal band — still above a full-screen app. Step behind it.
                if (wasTop && !w.Topmost && fs is { Hwnd: var h } && h != IntPtr.Zero) Native.Behind(w.Hwnd, h);
            }
            Relayout(force: true);
            if (fs == null && _pendingAlarm) Alarm(null);
        }

        if (++_tickCount % 15 == 0) RefreshFiles();

        if (DateTime.Now.Minute != _lastMinute)
        {
            _lastMinute = DateTime.Now.Minute;
            foreach (var w in _cards.Values.Where(w => w.Card.RemindAt != null && !w.Editing)) w.Render(); // "明天" → "今天"
            UpdateHubs();
            if (DateTime.Now.ToString("yyyyMMdd") != _day) Maintenance();
        }
    }

    /// <summary>One sound per due batch — held while every due card sits under a full-screen app, played afterwards.</summary>
    void Alarm(List<CardWindow>? due)
    {
        if (due != null && due.All(w => Covered(ScreenOf(w)))) { _pendingAlarm = true; return; }
        _pendingAlarm = false;
        if (Store.SettingBool("sound", true)) Native.ReminderSound();
        UpdateHubs();
    }

    /// <summary>File cards follow their file: a changed size/date or a deleted file re-reads the facts (and marks the summary stale).</summary>
    void RefreshFiles()
    {
        foreach (var w in _cards.Values.Where(w => w.Card.IsFile && w.Busy == null && w.Card.SrcInfo is { } m && m.State != "manual").ToList())
        {
            var m = w.Card.SrcInfo!;
            var path = w.Card.Src!;
            bool changed;
            try
            {
                if (Directory.Exists(path)) changed = false;
                else if (!File.Exists(path)) changed = m.State != "missing";
                else
                {
                    var f = new FileInfo(path);
                    changed = m.State == "missing" || f.Length != m.Size || new DateTimeOffset(f.LastWriteTimeUtc).ToUnixTimeMilliseconds() != m.Mtime;
                }
            }
            catch { changed = false; }
            if (changed) _ = Enrich(w);
        }
    }

    void Poll()
    {
        long v;
        try { v = Store.DataVersion; } catch { return; }
        if (v == _version) return;
        _version = v;
        Reload();
    }

    /// <summary>Re-reads every row (dozens) and applies the differences to the windows.</summary>
    public void Reload()
    {
        var rows = Store.All().ToDictionary(c => c.Id);
        foreach (var w in _cards.Values.Where(w => w.Ref > 0).ToList())
        {
            if (!rows.TryGetValue(w.Ref, out var row)) { Close(w); continue; }
            if (w.Strip != null)
            {
                if (row.Active) { w.CancelStrip(); w.Update(row); } // undone elsewhere (Ctrl+Z, CLI, manage window)
                continue; // otherwise its own timer closes it
            }
            if (!row.Active) Close(w);
            else if (row.Rev != w.Card.Rev) { w.Update(row); MaybeEnrich(w); }
        }
        var fresh = rows.Values.Where(r => r.Active && !_cards.ContainsKey(r.Id)).ToList();
        foreach (var c in fresh) MaybeEnrich(Adopt(c));
        DoLayout(force: fresh.Count > 0);
        _manage?.Refresh();
    }

    // ================= links, files, summaries =================

    /// <summary>A card whose source has no facts yet (new, or changed by the CLI or a drop) gets them read.</summary>
    void MaybeEnrich(CardWindow w)
    {
        if (w.Card.Src != null && w.Card.SrcInfo == null && _cards.ContainsValue(w)) _ = Enrich(w);
    }

    /// <summary>Reads a card's source (web page or file) into its metadata, then summarizes it when that is automatic.</summary>
    public async Task Enrich(CardWindow w, bool userAsked = false)
    {
        if (w.Busy != null) { _reenrich.Add(w); return; } // busy with the old source: run again when it finishes
        var c = w.Card;
        var src = c.Src;
        if (src == null) return;
        CardMeta meta;
        Page? page = null;
        (byte[] Bytes, string Ext)? picture = null;
        try
        {
            if (c.IsLink)
            {
                // Links are fetched only when automatic reading is on and the address is public (resolved), or when asked.
                if (!userAsked && !(AutoSummary && await Fetch.SafeToAutoAsync(src)))
                {
                    SaveMeta(w, new CardMeta { Src = src, State = "manual" });
                    return;
                }
                w.Busy = "正在读取网页…";
                w.Render();
                string? error = null;
                try { page = await Fetch.Web(src, auto: !userAsked); }
                catch (UnsafeRedirectException)
                {
                    SaveMeta(w, new CardMeta { Src = src, State = "manual" }); // the user can still ask (✨): then it is read as asked
                    return;
                }
                catch (Exception e) { error = e is TaskCanceledException or OperationCanceledException ? "超时" : e.Message; }
                if (page is { Image: { } bytes }) picture = (bytes, page.ImageExt ?? ".png");
                else if (page == null || page.Thin && page.Download == null)
                {
                    try
                    {
                        if (await Fetch.Rendered(src) is { } r && r.Text.Length > (page?.Text.Length ?? 0))
                            page = r with { Title = r.Title ?? page?.Title, Desc = r.Desc ?? page?.Desc };
                    }
                    catch { }
                }
                meta = picture != null ? new CardMeta { Src = src } : new CardMeta
                {
                    Src = src, Title = page?.Title, Site = page?.Site, Desc = page?.Desc, Date = page?.Date,
                    TextHash = page is { Thin: false } ? Card.Hash(page.Text) : page?.Download is { } d ? Card.Hash(d) : null,
                    State = page == null || page.Thin && page.Download == null ? "blocked" : null, Error = page == null ? error : null,
                };
            }
            else
            {
                w.Busy = "正在读取文件…";
                w.Render();
                (meta, var text) = await Task.Run(() => Fetch.File(src));
                meta.TextHash = text.Length > 0 ? Card.Hash(text) : meta.Mtime is long m ? Card.Hash(src + m) : null;
            }
        }
        finally
        {
            w.Busy = null;
        }
        if (!_cards.ContainsValue(w)) return;
        if (w.Card.Src != src) { w.Render(); MaybeEnrich(w); return; } // the source changed meanwhile: read the new one
        if (picture is { } pic)
        {
            await BecomePicture(w, src, pic.Bytes, pic.Ext);
            if (_cards.ContainsValue(w)) { w.Render(); MaybeEnrich(w); }
            return;
        }
        var prev = w.Card.Info;
        // A summary of a different source must show as stale (never as current); same source keeps its bookkeeping.
        meta.SumHash = prev?.Src == src ? prev.SumHash : string.IsNullOrWhiteSpace(w.Card.Summary) ? null : "source-changed";
        SaveMeta(w, meta);
        bool needs = w.Card.SummaryStale || string.IsNullOrWhiteSpace(w.Card.Summary);
        if (needs && Llm != null && (userAsked || AutoSummarizes(w.Card))) Summarize(w, page, userAsked);
        else if (_reenrich.Remove(w)) MaybeEnrich(w);
    }

    /// <summary>A link that is itself a picture turns into a floating picture card (the address is kept in its metadata).</summary>
    async Task BecomePicture(CardWindow w, string src, byte[] bytes, string ext)
    {
        var img = await Task.Run(() => Store.SaveImage(bytes, ext));
        if (!_cards.ContainsValue(w) || w.Card.Src != src) return;
        var size = NaturalSize(ScreenOf(w), img);
        Change(w, c =>
        {
            c.Images.Add(img);
            c.Body = c.Note.Trim('\n', '\r');
            if (c.Body.Length == 0) { c.W = size.Width; c.H = size.Height; }
            c.Meta = new CardMeta { Src = src, Title = Path.GetFileName(new Uri(src).LocalPath) }.ToJson();
        }, "edit", push: false);
        Relayout(force: true);
    }

    void SaveMeta(CardWindow w, CardMeta meta)
    {
        if (w.Ref < 0) { w.Card.Meta = meta.ToJson(); w.Render(); return; }
        // Derived facts are not a user action: saved without a history entry.
        var r = Store.Mutate(Store.Begin(), w.Card.Id, c => c.Meta = meta.ToJson(), "meta", journal: false);
        if (r != null) w.Update(r);
    }

    /// <summary>Roughly how many characters fit the card, so the summary does not overflow (requirement 5).</summary>
    int Budget(CardWindow w)
    {
        double header = w.Card.IsLink ? 72 : w.Card.IsFile ? 52 : 0;
        double height = (w.Card.H ?? MaxAutoHeight(w)) - CardWindow.RowH - CardWindow.PadTop - header;
        double lines = Math.Max(3, height / Math.Round(FontSize * 1.5));
        double perLine = Math.Max(8, (w.Card.W - 26) / FontSize); // CJK: one character per em
        return (int)Math.Clamp(lines * perLine * 0.7, 60, 500);    // bullets and wrapping waste about 30%
    }

    /// <summary>
    /// Summarizes the note, page or document with the configured CLI LLM (one run per card at a time). The result is
    /// stored only if the card still has the same source and input when it arrives; otherwise it is dropped as stale.
    /// Only a summary the user asked for becomes an undo step.
    /// </summary>
    public async void Summarize(CardWindow w, Page? page = null, bool userAsked = true)
    {
        if (Llm is not { } llm) { if (userAsked) Notify("没有可用的 CLI LLM：在“管理 › 设置 › AI 摘要”中选择"); return; }
        if (w.Ref < 0) return;
        var c = w.Card;
        if (c.Src != null && (c.SrcInfo == null || c.SrcInfo.State == "manual"))
        {
            await Enrich(w, userAsked: true); // read the source first; Enrich continues with the summary
            return;
        }
        var id = w.Ref;
        if (!_summarizing.Add(id)) return;
        w.Problem = null;
        try
        {
            var src = c.Src;
            var hash = c.InputHash;
            string what = "笔记", content = c.Body, title = c.Title, source = src ?? "";
            var mode = LlmMode.Text;
            string? cwd = null;
            if (c.IsLink)
            {
                what = "网页";
                if (page == null)
                {
                    try { page = await Fetch.Web(src!, auto: !userAsked); } catch { page = null; } // unreachable now: still try the URL mode below
                }
                if (page is { Thin: false }) content = page.Text;
                else if (page?.Download is { } doc && llm.Supports(LlmMode.File))
                {
                    what = "文档";
                    mode = LlmMode.File;
                    cwd = Path.GetDirectoryName(doc);
                    content = "请先读取当前目录中的文件「" + Path.GetFileName(doc) + "」，再按要求总结。";
                }
                else if (llm.Supports(LlmMode.Url)) { mode = LlmMode.Url; content = $"正文无法直接获取，请先访问这个网址再总结：{src}\n{c.SrcInfo?.Desc}"; }
                else content = $"{c.SrcInfo?.Title}\n{c.SrcInfo?.Desc}".Trim();
                if (!string.IsNullOrWhiteSpace(c.Note)) content += "\n\n（卡片上的备注：" + c.Note.Trim() + "）";
            }
            else if (c.IsFile)
            {
                what = "文档";
                var (_, text) = await Task.Run(() => Fetch.File(src!));
                if (text.Length > 0) content = text;
                else if (llm.Supports(LlmMode.File) && File.Exists(src))
                {
                    mode = LlmMode.File;
                    cwd = Path.GetDirectoryName(src);
                    content = "请先读取当前目录中的文件「" + Path.GetFileName(src) + "」，再按要求总结。";
                }
                else if (userAsked) throw new InvalidOperationException("这种文件需要能读文件的 CLI（例如 claude）");
                else return;
            }
            w.Busy = "✨ 摘要中…";
            w.Render();
            var result = await llm.Ask(Llm.Prompt(what, title, source, content, Budget(w), Store.Setting("llm.prompt")), mode, cwd);
            if (!_cards.TryGetValue(id, out var live)) return;
            var g = Store.Begin();
            var r = Store.Mutate(g, id, x =>
            {
                if (x.Src != src || x.InputHash != hash) return; // the card changed meanwhile: this summary is stale, drop it
                x.Summary = result;
                var meta = x.Info ?? new CardMeta { Src = x.Src };
                meta.SumHash = hash;
                x.Meta = meta.ToJson();
            }, "summary");
            if (userAsked) Push(g);
            if (r != null) live.Update(r);
        }
        catch (Exception e)
        {
            w.Problem = "摘要失败：" + (e.Message.Length > 120 ? e.Message[..120] + "…" : e.Message);
        }
        finally
        {
            _summarizing.Remove(id);
            w.Busy = null;
            if (_cards.ContainsValue(w))
            {
                w.Render();
                if (_reenrich.Remove(w)) MaybeEnrich(w);
            }
        }
    }

    public void ClearSummary(CardWindow w) => Change(w, c => c.Summary = null, "summary");

    // ================= images =================

    /// <summary>Loads (and caches, bounded) a picture decoded no wider than needed; the file is not kept open.</summary>
    public BitmapSource? LoadImage(string src, int decodeWidth)
    {
        int bucket = decodeWidth <= 0 ? 0 : Math.Min(4096, (decodeWidth / 256 + 1) * 256);
        var key = src + "|" + bucket;
        if (_images.TryGetValue(key, out var cached)) return cached;
        if (_images.Count > 64) _images.Clear(); // cards re-decode on their next render; keeps memory bounded
        BitmapSource? img = null;
        try
        {
            var path = Store.ResolveImage(src);
            if (path.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) path = new Uri(path).LocalPath;
            var bi = new BitmapImage();
            bi.BeginInit();
            if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase)) bi.UriSource = new Uri(path);
            else
            {
                if (!File.Exists(path)) throw new FileNotFoundException(path);
                bi.UriSource = new Uri(path);
                bi.CacheOption = BitmapCacheOption.OnLoad;
                if (bucket > 0) bi.DecodePixelWidth = bucket;
            }
            bi.EndInit();
            if (bi.CanFreeze) bi.Freeze();
            img = bi;
        }
        catch { img = null; }
        return _images[key] = img;
    }

    // ================= menus (shared by hubs and cards) =================

    public ContextMenu CardMenu(CardWindow w)
    {
        var c = w.Card;
        var m = new ContextMenu();
        if (Llm != null && !c.ImageOnly) Add(m, "", string.IsNullOrWhiteSpace(c.Summary) ? "摘要" : "重新摘要", () => Summarize(w));
        if (!string.IsNullOrWhiteSpace(c.Summary)) Add(m, "", "清除摘要", () => ClearSummary(w));
        if (c.Src != null) Add(m, "", c.IsLink ? "复制链接" : "复制路径", () => { try { Clipboard.SetText(c.Src!); w.ShowBadge("已复制"); } catch { } });
        if (c.IsFile) Add(m, "", "打开所在位置", () => Reveal(c.Src!));
        if (c.ImageOnly) Add(m, "", "添加文字", () => w.BeginEdit());
        if (c.Progress.Total > 0) Add(m, "", "添加一项", () => w.StartAdd());
        Add(m, "", "颜色…", () => Flyout.Colors(this, w, w));
        Add(m, c.Collapsed ? "" : "", c.Collapsed ? "展开" : "折叠", () => ToggleCollapse(w));
        if (c.Topmost) m.Items.Add(OpacityRow(w, m));
        if (c.Free) Add(m, "", "回到平铺", () => Unfree(w));
        if (c.H != null && !c.ImageOnly) Add(m, "", "自动高度", () => AutoHeight(w));
        m.Items.Add(new Separator());
        Add(m, "", "删除", () => Delete(w), "Del");
        return m;
    }

    MenuItem OpacityRow(CardWindow w, ContextMenu menu)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = "透明度", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        foreach (var v in new[] { 1.0, 0.9, 0.8, 0.7, 0.5 })
        {
            var b = new Button { Content = $"{v * 100:0}", Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 0, 4, 0), FontWeight = Math.Abs(w.Card.Opacity - v) < 0.05 ? FontWeights.Bold : FontWeights.Normal };
            b.SetResourceReference(FrameworkElement.StyleProperty, "Ui.Chip");
            b.Click += (_, _) => { menu.IsOpen = false; SetOpacity(w, v); };
            row.Children.Add(b);
        }
        return new MenuItem { Header = row, Icon = "", StaysOpenOnClick = true, ToolTip = "也可以在卡片上 Ctrl+滚轮" };
    }

    public ContextMenu HubMenu(Screen s)
    {
        string G(string key) => Store.Setting(key, Hotkeys.Actions.First(a => a.Key == key).Default);
        var m = new ContextMenu();
        if (HotkeyProblem) Add(m, "", "快捷键冲突 · 去设置", () => OpenManage("settings"));
        Add(m, "", "新建卡片", () => NewCard(s), G("hk.new"));
        Add(m, "", "用剪贴板新建", () => CreateFrom(ClipData.Current(), s), G("hk.paste"));
        m.Items.Add(new Separator());
        Add(m, "", "平铺显示", Tile, G("hk.tile"));
        bool anyOpen = _cards.Values.Any(w => w.Ref > 0 && !w.Card.Collapsed);
        Add(m, anyOpen ? "" : "", anyOpen ? "全部折叠" : "全部展开", CollapseAll, G("hk.collapse"));
        Add(m, _hidden ? "" : "", _hidden ? "恢复显示" : "最小化全部", ToggleHidden, G("hk.min"));
        m.Items.Add(new Separator());
        Add(m, Store.SettingBool("top:" + s.Id, false) ? "" : "", "本屏卡片置顶", () => PinScreen(s));
        var colors = new MenuItem { Header = "新卡配色", Icon = "" };
        foreach (var (key, label) in new[] { ("off", "关（统一颜色）"), ("rotate", "轮换"), ("kind", "按类型"), ("tag", "按 #标签") })
        {
            var item = new MenuItem { Header = label, Icon = ColorMode == key ? "" : "" };
            item.Click += (_, _) => { Store.SetSetting("color.mode", key); ApplySettings(); };
            colors.Items.Add(item);
        }
        m.Items.Add(colors);
        m.Items.Add(new Separator());
        Add(m, "", "撤销", Undo, "Ctrl+Z");
        Add(m, "", "管理…", () => OpenManage());
        Add(m, "", "退出", () => Application.Current.Shutdown());
        return m;
    }

    static void Add(ContextMenu m, string glyph, string text, Action act, string gesture = "")
    {
        var item = new MenuItem { Header = text, Icon = glyph, InputGestureText = gesture };
        item.Click += (_, _) => act();
        m.Items.Add(item);
    }

    // ================= windows & feedback =================

    public void OpenManage(string? tab = null)
    {
        if (_manage is not { IsLoaded: true })
        {
            _manage = new ManageWindow(this);
            _manage.Closed += (_, _) => _manage = null;
            var s = Screens.UnderCursor();
            _manage.SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(_manage).Handle;
                int cw = s.Px(_manage.Width), ch = s.Px(_manage.Height);
                Native.Place(hwnd, new Px(s.Work.CX - cw / 2, s.Work.CY - ch / 2, cw, ch).ClampInto(s.Work));
            };
            _manage.Show();
        }
        if (tab != null) _manage.Select(tab);
        _manage.Activate();
    }

    /// <summary>Shows a card from the library: back on screen and expanded (even if space ran out), raised, highlighted.</summary>
    public void Locate(long id)
    {
        if (!_cards.TryGetValue(id, out var w)) return;
        _hidden = false;
        if (w.Card.Collapsed) ToggleCollapse(w);
        DoLayout(force: true);
        if (_overflowHidden.Contains(w) || w.Pill) Peek(w);
        if (!w.IsVisible) w.Show();
        Native.Front(w.Hwnd, w.Topmost);
        w.Pulse();
    }

    /// <summary>A short notice beside the hub of the monitor under the cursor, at a spot no card covers.</summary>
    public void Notify(string text)
    {
        _toast?.Dismiss();
        var s = Screens.UnderCursor();
        var hub = _hubs.FirstOrDefault(h => h.Screen.Id == s.Id)?.Rect ?? new Px(s.Work.Right, s.Work.Bottom, 0, 0);
        var cards = _cards.Values.Where(w => w.IsVisible).Select(w => w.Rect).Where(r => s.Bounds.Intersects(r)).ToList();
        _toast = new Toast(text, size => Spot(s, hub, size, cards));
        _toast.Closed += (_, _) => _toast = null;
        _toast.Show();
    }

    /// <summary>Beside the hub toward the screen, then above it, then along the work area's edges: the first spot free of cards.</summary>
    Px Spot(Screen s, Px hub, (int W, int H) size, List<Px> cards)
    {
        int gap = s.Px(8), w = size.W, h = size.H;
        bool left = HubCorner is Corner.TopLeft or Corner.BottomLeft, top = HubCorner is Corner.TopLeft or Corner.TopRight;
        var candidates = new List<Px>
        {
            new(left ? hub.Right + gap : hub.X - w - gap, top ? hub.Y : hub.Bottom - h, w, h),
            new(left ? hub.X : hub.Right - w, top ? hub.Bottom + gap : hub.Y - h - gap, w, h),
            new(s.Work.CX - w / 2, top ? s.Work.Y + gap : s.Work.Bottom - h - gap, w, h),
            new(s.Work.CX - w / 2, top ? s.Work.Bottom - h - gap : s.Work.Y + gap, w, h),
        };
        var clamped = candidates.Select(c => c.ClampInto(s.Work)).ToList();
        return clamped.FirstOrDefault(c => !cards.Any(r => r.Intersects(c)), clamped.MinBy(c => cards.Sum(r => r.Overlap(c))));
    }

    /// <summary>
    /// Registers the configured global hotkeys. Two actions on one combination is reported as a duplicate, never as
    /// "another program".
    /// </summary>
    public void RegisterHotkeys()
    {
        _hotkeys.Clear();
        HotkeyStatus.Clear();
        var seen = new Dictionary<string, string>();
        foreach (var (key, label, def) in Hotkeys.Actions)
        {
            var g = Store.Setting(key, def);
            if (string.IsNullOrWhiteSpace(g)) continue; // unbound
            var norm = Hotkeys.Normalize(g);
            if (seen.TryGetValue(norm, out var other)) { HotkeyStatus[key] = $"与「{other}」重复"; continue; }
            seen[norm] = label;
            HotkeyStatus[key] = _hotkeys.Register(g, key switch
            {
                "hk.new" => () => NewCard(Screens.UnderCursor(), returnFocus: Native.GetForegroundWindow()),
                "hk.paste" => () => CreateFrom(ClipData.Current(), Screens.UnderCursor()),
                "hk.min" => ToggleHidden,
                "hk.tile" => Tile,
                _ => CollapseAll,
            });
        }
        UpdateHubs();
    }

    /// <summary>The label of another action already bound to <paramref name="gesture"/>, if any (settings recorder).</summary>
    public string? GestureOwner(string gesture, string exceptKey)
    {
        var norm = Hotkeys.Normalize(gesture);
        foreach (var (key, label, def) in Hotkeys.Actions)
            if (key != exceptKey && Store.Setting(key, def) is { Length: > 0 } g && Hotkeys.Normalize(g) == norm) return label;
        return null;
    }

    /// <summary>
    /// Diagnostics: builds every auxiliary surface — both tabs of the manage window, the reminder and color pickers, the
    /// card and hub menus — and renders it off-screen (never shown, never focused). Failures go to ui-errors.txt.
    /// </summary>
    public void SnapshotUi(string dir)
    {
        Directory.CreateDirectory(dir);
        var errors = new List<string>();
        void Try(string name, Func<FrameworkElement> build, double width, double height = 720)
        {
            try { Render(build(), Path.Combine(dir, $"ui-{name}.png"), width, height); }
            catch (Exception e) { errors.Add($"{name}: {e}"); }
        }
        ManageWindow? m = null;
        try { m = new ManageWindow(this); }
        catch (Exception e) { errors.Add("manage: " + e); }
        if (m != null)
        {
            var tabs = (UIElement)m.Content;
            m.Content = null;
            var host = new Border { Child = tabs }; // what the window adds around its content: background, ink, font
            System.Windows.Documents.TextElement.SetFontFamily(host, m.FontFamily);
            System.Windows.Documents.TextElement.SetFontSize(host, m.FontSize);
            host.SetResourceReference(Border.BackgroundProperty, "Ui.Bg");
            host.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Ui.Ink");
            Try("manage-cards", () => host, m.Width);
            Try("manage-settings", () => { m.Select("settings"); return host; }, m.Width);
            m.Close();
        }
        // A desk without cards (render-only run) uses a sample card that is never shown.
        var sample = _cards.Values.Any(w => w.Ref > 0) ? null : new CardWindow(this, new Card { Body = "示例卡片", W = 260 });
        var card = sample ?? _cards.Values.First(w => w.Ref > 0);
        Try("reminder", () => Surface(Flyout.ReminderPanel(this, card, () => { }, out _)), double.NaN);
        Try("colors", () => Surface(Flyout.ColorsPanel(this, card, () => { })), double.NaN);
        Try("card-menu", () => CardMenu(card), double.NaN);
        sample?.Close();
        Try("hub-menu", () => HubMenu(Screens.Primary), double.NaN);
        // Checklist states on sample cards (never shown): folded checked items, the highlighted ○, quick add, a pill with the next task.
        const string list = "## 周末\n- [x] 洗车\n- [x] 取快递\n- [ ] 买菜\n- [x] 理发\n  - 顺便买洗发水\n- [ ] 给妈妈打电话";
        SampleCard("checklist-folded", list);
        SampleCard("checklist-unfolded", list, unfold: true);
        SampleCard("checklist-all-done", "## 周末\n- [x] 洗车\n- [x] 买菜");
        SampleCard("checklist-adding", "## 周末\n- [ ] 洗车", w => w.StartAdd());
        SampleCard("checklist-pill", list, w => { w.Card.Collapsed = true; w.Render(); });
        SampleCard("checklist-pill-bare", "- [x] 洗车\n- [ ] 买菜", w => { w.Card.Collapsed = true; w.Render(); });
        File.WriteAllText(Path.Combine(dir, "ui-errors.txt"), string.Join("\n\n", errors));

        void SampleCard(string name, string body, Action<CardWindow>? setup = null, bool unfold = false)
        {
            var w = new CardWindow(this, new Card { Body = body, W = 260 });
            try
            {
                setup?.Invoke(w);
                if (unfold && FindFold(w.Content as DependencyObject) is { } fold)
                    fold.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                var e = (FrameworkElement)w.Content;
                e.Measure(new Size(260, double.PositiveInfinity));
                Try("card-" + name, () => e, 260, w.Pill ? CardWindow.PillH : Math.Ceiling(e.DesiredSize.Height));
            }
            catch (Exception ex) { errors.Add($"card-{name}: {ex}"); }
            finally { w.Close(); }
        }
        static TextBlock? FindFold(DependencyObject? d)
        {
            if (d is TextBlock { ToolTip: "显示已勾选的任务" } t) return t;
            foreach (var child in LogicalTreeHelper.GetChildren(d ?? new DependencyObject()).OfType<DependencyObject>())
                if (FindFold(child) is { } found) return found;
            return null;
        }

        static FrameworkElement Surface(UIElement content)
        {
            var b = new Border { Child = content };
            b.SetResourceReference(FrameworkElement.StyleProperty, "Ui.Surface");
            return b;
        }
        static void Render(FrameworkElement e, string path, double width, double height)
        {
            e.Measure(new Size(double.IsNaN(width) ? double.PositiveInfinity : width, double.IsNaN(width) ? double.PositiveInfinity : height));
            e.Arrange(new Rect(new Point(), double.IsNaN(width) ? e.DesiredSize : new Size(width, height)));
            e.UpdateLayout();
            var bmp = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(e.ActualWidth)), Math.Max(1, (int)Math.Ceiling(e.ActualHeight)), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bmp.Render(e);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(path);
            enc.Save(fs);
        }
    }

    /// <summary>
    /// Diagnostics (env PINCC_STEPS, with PINCC_SNAPSHOT): one scripted gesture through the same methods the mouse and
    /// keyboard use — "tile", "collapse", "hide", "undo", "new[:text]", "commit", "done:id", "del:id", "fold:id",
    /// "peek:id", "pin:id", "zoom:id", "notify:text".
    /// </summary>
    public void Step(string step)
    {
        var parts = step.Split(':', 2);
        var arg = parts.Length > 1 ? parts[1] : "";
        var w = long.TryParse(arg, out var id) ? _cards.GetValueOrDefault(id) : null;
        switch (parts[0])
        {
            case "tile": Tile(); break;
            case "collapse": CollapseAll(); break;
            case "hide": ToggleHidden(); break;
            case "undo": Undo(); break;
            case "new": NewCard(Screens.Primary, arg, edit: arg.Length == 0); break;
            case "commit": foreach (var e in _cards.Values.Where(x => x.Editing).ToList()) e.EndEdit(returnFocus: false); break;
            case "done" when w != null: Complete(w); break;
            case "del" when w != null: Delete(w); break;
            case "fold" when w != null: ToggleCollapse(w); break;
            case "peek": Locate(id); break;
            case "pin" when w != null: TogglePin(w); break;
            case "zoom" when w != null: Zoom(w, 1.6); break;
            case "notify": Notify(arg); break;
            default: throw new ArgumentException("unknown step " + step);
        }
    }

    /// <summary>
    /// Diagnostics (env PINCC_SNAPSHOT=folder): renders every card and hub to PNG off-screen and writes the monitors and
    /// physical window rects to layout.json — verifiable even when the desktop is locked.
    /// </summary>
    public void Snapshot(string dir)
    {
        Directory.CreateDirectory(dir);
        var windows = new List<object>();
        var toast = _toast is { IsVisible: true } t ? new[] { ("toast", (Window)t, Native.WindowRect(new WindowInteropHelper(t).Handle)) } : [];
        foreach (var (name, win, rect) in _cards.Values.Select(w => ($"card{w.Ref}", (Window)w, w.Rect))
                     .Concat(_hubs.Select((h, i) => ($"hub{i}", (Window)h, h.Rect))).Concat(toast))
        {
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(win);
            if (win.ActualWidth > 0 && win.ActualHeight > 0)
            {
                var bmp = new RenderTargetBitmap((int)Math.Ceiling(win.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(win.ActualHeight * dpi.DpiScaleY),
                    dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
                bmp.Render(win);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                using var fs = File.Create(Path.Combine(dir, name + ".png"));
                enc.Save(fs);
            }
            var cw = win as CardWindow;
            windows.Add(new { name, rect = new[] { rect.X, rect.Y, rect.W, rect.H }, visible = win.IsVisible, topmost = win.Topmost, scale = dpi.DpiScaleX,
                title = cw?.Card.Title, pill = cw?.Pill, editing = cw?.Editing, free = cw?.Card.Free, overdue = cw?.Card.Overdue, strip = cw?.Strip != null,
                stripRect = cw?.Strip != null ? new[] { cw.StripRect.X, cw.StripRect.Y, cw.StripRect.W, cw.StripRect.H } : null });
        }
        SnapshotUi(dir);
        var screens = Screens.All().Select(s => new { s.Device, s.Id, bounds = new[] { s.Bounds.X, s.Bounds.Y, s.Bounds.W, s.Bounds.H }, work = new[] { s.Work.X, s.Work.Y, s.Work.W, s.Work.H }, s.Scale, s.Primary });
        File.WriteAllText(Path.Combine(dir, "layout.json"), System.Text.Json.JsonSerializer.Serialize(new { screens, windows },
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }
}
