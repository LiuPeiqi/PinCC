using System.Text.RegularExpressions;

namespace PinCC.Core;

/// <summary>One journal entry: a card's state before a group changed it, and which columns the group changed.</summary>
public sealed record Op(long Id, long Grp, long At, long CardId, string Name, string? Before, string? Cols);

/// <summary>
/// One user gesture or one CLI command. Every card it touches is journaled (its state before the gesture, plus the
/// columns the gesture changed), so undo restores the whole gesture: an edit session, a drag, "tile all", a CLI `set`
/// with several flags — and nothing else.
/// </summary>
public sealed class Group
{
    internal long Id;
    /// <summary>Per card: the rev this group last wrote, its current journal entry and the columns recorded there.</summary>
    internal Dictionary<long, (long Rev, long Op, string Cols)> Cards = new();
    public long Number => Id;
    public bool Empty => Id == 0;
}

/// <summary>
/// All persistence, shared by the GUI and the CLI (separate processes; WAL + busy timeout). Writes are optimistic:
/// read the row, apply the change, write only changed columns WHERE rev matches, rev+1 — so two writers never clobber
/// each other's fields. Layout-computed positions are never written.
/// </summary>
public sealed partial class Store : IDisposable
{
    public const int SchemaVersion = 1;
    public string Home { get; }
    public string ImgDir { get; }
    public Db Db { get; }
    /// <summary>True when the database was written by a newer PinCC: reading works, writing is refused.</summary>
    public bool ReadOnly { get; }

    const string Schema = """
        CREATE TABLE IF NOT EXISTS cards(
          id INTEGER PRIMARY KEY AUTOINCREMENT, rev INTEGER NOT NULL DEFAULT 0,
          body TEXT NOT NULL DEFAULT '', images TEXT, summary TEXT, meta TEXT,
          color INTEGER, topmost INTEGER NOT NULL DEFAULT 0, opacity REAL NOT NULL DEFAULT 1,
          collapsed INTEGER NOT NULL DEFAULT 0, free INTEGER NOT NULL DEFAULT 0,
          mon TEXT, x REAL NOT NULL DEFAULT 0, y REAL NOT NULL DEFAULT 0, w REAL NOT NULL DEFAULT 260, h REAL,
          remind_at INTEGER, done_at INTEGER, deleted_at INTEGER, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL);
        -- AUTOINCREMENT: a group is numbered by its first op id, which must never come back after a purge.
        -- cols: the columns the group changed on that card (restore writes back only those).
        CREATE TABLE IF NOT EXISTS ops(
          id INTEGER PRIMARY KEY AUTOINCREMENT, grp INTEGER NOT NULL, at INTEGER NOT NULL, card_id INTEGER NOT NULL,
          op TEXT NOT NULL, before TEXT, cols TEXT);
        CREATE INDEX IF NOT EXISTS ops_grp ON ops(grp);
        CREATE INDEX IF NOT EXISTS ops_card ON ops(card_id, id);
        CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT);
        """;

    public Store(string? home = null, int busyMs = 5000)
    {
        Home = home ?? DefaultHome;
        ImgDir = Path.Combine(Home, "img");
        Directory.CreateDirectory(ImgDir);
        Db = new Db(Path.Combine(Home, "pincc.db"), busyMs);
        var version = UserVersion();
        if (version > SchemaVersion) { ReadOnly = true; return; }
        // Opening must not write: read-only CLI commands would otherwise bump every other connection's data_version.
        if (version < SchemaVersion || PreRelease()) Migrate();
    }

    int UserVersion() => Convert.ToInt32(Db.Scalar("PRAGMA user_version"));

    /// <summary>A database from a pre-release build (journal without per-op columns). Remove before release.</summary>
    bool PreRelease() => UserVersion() == SchemaVersion && Convert.ToInt64(Db.Scalar("SELECT count(*) FROM pragma_table_info('ops') WHERE name='cols'")) == 0;

    void Migrate() => Db.Tx(() =>
    {
        bool pre = PreRelease();
        if (UserVersion() >= SchemaVersion && !pre) return; // another process got here first
        if (pre) Db.Script("DROP INDEX IF EXISTS ops_grp; DROP INDEX IF EXISTS ops_card; ALTER TABLE ops RENAME TO ops_old;");
        Db.Script(Schema);
        if (pre) Db.Script("INSERT INTO ops(id,grp,at,card_id,op,before) SELECT id,grp,at,card_id,op,before FROM ops_old; DROP TABLE ops_old;");
        Db.Script($"PRAGMA user_version = {SchemaVersion}");
    });

    /// <summary>%PINCC_HOME% if set, else %LOCALAPPDATA%\PinCC.</summary>
    public static string DefaultHome => Environment.GetEnvironmentVariable("PINCC_HOME") is { Length: > 0 } env ? env
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PinCC");

    /// <summary>Changes whenever another connection (the CLI, or the GUI seen from the CLI) commits.</summary>
    public long DataVersion => Db.DataVersion;

    // ================= reading =================

    public Card? Get(long id) => Db.Query("SELECT * FROM cards WHERE id=?", id).Select(Card.FromRow).FirstOrDefault();

    /// <summary>Every stored card, including done/deleted ones still inside the retention window.</summary>
    public List<Card> All() => Db.Query("SELECT * FROM cards").Select(Card.FromRow).ToList();

    /// <summary>Cards on the desk, in display order.</summary>
    public List<Card> Active()
    {
        var list = Db.Query("SELECT * FROM cards WHERE done_at IS NULL AND deleted_at IS NULL").Select(Card.FromRow).ToList();
        list.Sort(Card.Compare);
        return list;
    }

    // ================= writing =================

    public Group Begin() => new();

    /// <summary>Inserts a new card, journaled as "create" (undo = delete).</summary>
    public Card Insert(Group g, Card c)
    {
        Writable();
        var now = When.NowMs;
        if (c.CreatedAt == 0) c.CreatedAt = now;
        c.UpdatedAt = now;
        c.Rev = 1;
        InTx(g, () =>
        {
            c.Id = Db.Insert($"INSERT INTO cards(rev,updated_at,{string.Join(",", Card.Columns)}) VALUES(1,?,{Marks(Card.Columns.Length)})", [now, .. c.Values()]);
            Journal(g, c.Id, 0, 1, "create", null, []);
            return 0;
        });
        return c;
    }

    /// <summary>
    /// Applies <paramref name="change"/> to the stored row and writes only the columns that changed, guarded by rev.
    /// Journals the card's prior state once per group — again if another writer changed the card since this group last
    /// wrote it, so that writer's version stays in history. Returns the stored result, or null when the card no longer
    /// exists. No change → no write, no journal entry. <paramref name="ifRev"/> makes a stale caller fail instead;
    /// <paramref name="journal"/> = false stores derived data (link metadata) without a history entry.
    /// </summary>
    public Card? Mutate(Group g, long id, Action<Card> change, string op, long? ifRev = null, bool journal = true)
    {
        Writable();
        return InTx(g, () =>
        {
            // Inside BEGIN IMMEDIATE no other writer can interleave, so the rev guard in Write always holds here;
            // ifRev protects callers that computed their change from an older read.
            var cur = Get(id);
            if (cur == null) return null;
            if (ifRev != null && cur.Rev != ifRev) throw new RevConflictException(cur.Rev);
            var next = cur.Clone();
            change(next);
            return Write(g, cur, next, op, journal);
        });
    }

    /// <summary>Writes the columns where <paramref name="next"/> differs from <paramref name="cur"/> (the stored row).</summary>
    Card Write(Group g, Card cur, Card next, string op, bool journal)
    {
        var a = cur.Values();
        var b = next.Values();
        var cols = Enumerable.Range(0, a.Length).Where(i => !Equals(a[i], b[i])).ToList();
        if (cols.Count == 0) return cur;
        var set = string.Join(",", cols.Select(i => Card.Columns[i] + "=?"));
        var now = Math.Max(When.NowMs, cur.UpdatedAt + 1);
        Db.Exec($"UPDATE cards SET {set},rev=rev+1,updated_at=? WHERE id=? AND rev=?", [.. cols.Select(i => b[i]), now, cur.Id, cur.Rev]);
        next.Rev = cur.Rev + 1;
        next.UpdatedAt = now;
        if (journal) Journal(g, cur.Id, cur.Rev, next.Rev, op, cur.ToJson(), cols.Select(i => Card.Columns[i]));
        else if (g.Cards.TryGetValue(cur.Id, out var e) && e.Rev == cur.Rev) g.Cards[cur.Id] = e with { Rev = next.Rev }; // our own write, not a concurrent one
        return next;
    }

    /// <summary>
    /// Records that group <paramref name="g"/> moved a card from <paramref name="rev"/> to <paramref name="newRev"/>.
    /// If the group wrote that very rev last, the columns are added to its existing entry; otherwise (first touch, or
    /// another writer came in between) a new entry keeps the state before this write.
    /// </summary>
    void Journal(Group g, long cardId, long rev, long newRev, string op, string? before, IEnumerable<string> cols)
    {
        if (g.Cards.TryGetValue(cardId, out var e) && e.Rev == rev)
        {
            var merged = string.Join(",", e.Cols.Split(',', StringSplitOptions.RemoveEmptyEntries).Union(cols));
            if (merged != e.Cols) Db.Exec("UPDATE ops SET cols=? WHERE id=?", merged, e.Op);
            g.Cards[cardId] = (newRev, e.Op, merged);
            return;
        }
        var list = string.Join(",", cols);
        var id = Db.Insert("INSERT INTO ops(grp, at, card_id, op, before, cols) VALUES(?,?,?,?,?,?)", g.Id, When.NowMs, cardId, op, before, list);
        if (g.Id == 0)
        {
            g.Id = id; // a group is numbered by its first operation
            Db.Exec("UPDATE ops SET grp=? WHERE id=?", id, id);
        }
        g.Cards[cardId] = (newRev, id, list);
    }

    /// <summary>A transaction that also rolls back the group's bookkeeping, so a failed write leaves no phantom entry.</summary>
    T InTx<T>(Group g, Func<T> body)
    {
        var (id, cards) = (g.Id, new Dictionary<long, (long, long, string)>(g.Cards));
        try { return Db.Tx(body); }
        catch { g.Id = id; g.Cards = cards; throw; }
    }

    void Writable()
    {
        if (ReadOnly) throw new InvalidOperationException("数据库由更新版本的 PinCC 创建，当前版本只能读取。请更新 PinCC。");
    }

    // ================= history =================

    public List<Op> Ops(int limit = 200, long? cardId = null) =>
        (cardId == null
            ? Db.Query("SELECT * FROM ops ORDER BY id DESC LIMIT ?", limit)
            : Db.Query("SELECT * FROM ops WHERE card_id=? ORDER BY id DESC LIMIT ?", cardId.Value, limit))
          .Select(r => new Op((long)r["id"]!, (long)r["grp"]!, (long)r["at"]!, (long)r["card_id"]!, (string)r["op"]!, (string?)r["before"], (string?)r["cols"]))
          .ToList();

    /// <summary>
    /// Undoes a whole group: every card it touched gets back the columns the group changed, as they were before the
    /// group (a "create" is undone by deleting). Other columns — later edits by anyone — are left alone. Entries are
    /// replayed newest first, so a card journaled twice ends at its earliest state. The restore is itself a new group,
    /// so it can be undone too. Returns the ids of the cards the group touched (empty: no such group).
    /// </summary>
    public List<long> Restore(long grp)
    {
        Writable();
        var g = Begin();
        return InTx(g, () =>
        {
            var ids = new List<long>();
            foreach (var r in Db.Query("SELECT * FROM ops WHERE grp=? ORDER BY id DESC", grp))
            {
                var cardId = (long)r["card_id"]!;
                if (!ids.Contains(cardId)) ids.Add(cardId);
                var cur = Get(cardId);
                if (r["before"] is string json)
                {
                    var target = Card.FromJson(json);
                    if (cur != null)
                    {
                        // Entries without cols predate per-op columns: they restore the whole row.
                        var cols = r["cols"] is string list ? list.Split(',', StringSplitOptions.RemoveEmptyEntries) : Card.Columns;
                        Write(g, cur, cur.With(target, cols), "restore", journal: true);
                    }
                    else
                    {
                        // The row itself is gone (forgotten or purged): bring back the whole snapshot.
                        Db.Exec($"INSERT INTO cards(id,rev,updated_at,{string.Join(",", Card.Columns)}) VALUES(?,1,?,{Marks(Card.Columns.Length)})", [cardId, When.NowMs, .. target.Values()]);
                        Journal(g, cardId, 0, 1, "restore", null, []);
                    }
                }
                else if (cur is { DeletedAt: null })
                {
                    var next = cur.Clone();
                    next.DeletedAt = When.NowMs;
                    Write(g, cur, next, "restore", journal: true);
                }
            }
            return ids;
        });
    }

    /// <summary>Removes a card that never held content (an abandoned new card) without leaving history.</summary>
    public void Forget(long id)
    {
        Writable();
        Db.Tx(() =>
        {
            Db.Exec("DELETE FROM ops WHERE card_id=?", id);
            Db.Exec("DELETE FROM cards WHERE id=?", id);
        });
    }

    /// <summary>
    /// Drops history and deleted cards older than <paramref name="days"/> (done cards are kept as the archive), then
    /// images that nothing — no card, no journaled snapshot — references any more.
    /// </summary>
    public void Purge(int days)
    {
        if (ReadOnly) return;
        var cutoff = When.NowMs - (long)days * 86_400_000;
        Db.Tx(() =>
        {
            Db.Exec("DELETE FROM ops WHERE at < ?", cutoff);
            Db.Exec("DELETE FROM cards WHERE deleted_at IS NOT NULL AND deleted_at < ?", cutoff);
        });
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in Db.Query("SELECT ifnull(images,'') || body AS t FROM cards UNION ALL SELECT ifnull(before,'') FROM ops"))
            foreach (Match m in ImgRefRx().Matches((string?)r["t"] ?? "")) used.Add(m.Groups[1].Value);
        var old = DateTime.UtcNow.AddDays(-Math.Max(1, days));
        foreach (var f in Directory.EnumerateFiles(ImgDir))
            if (!used.Contains(Path.GetFileName(f)) && File.GetLastWriteTimeUtc(f) < old)
                try { File.Delete(f); } catch { /* in use: next time */ }
    }

    /// <summary>Once a day: a consistent copy of the database into backup\ (7 kept).</summary>
    public void Backup()
    {
        var today = DateTime.Now.ToString("yyyyMMdd");
        if (ReadOnly || Setting("backup.last") == today) return;
        var dir = Path.Combine(Home, "backup");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"pincc-{today}.db");
        if (File.Exists(file)) File.Delete(file);
        Db.Script($"VACUUM INTO '{file.Replace("'", "''")}'");
        foreach (var old in Directory.GetFiles(dir, "pincc-*.db").OrderByDescending(f => f).Skip(7))
            try { File.Delete(old); } catch { }
        SetSetting("backup.last", today);
    }

    // ================= images =================

    /// <summary>Stores image bytes content-addressed under img/ and returns the reference "img/&lt;hash&gt;.ext".</summary>
    public string SaveImage(byte[] bytes, string ext = ".png")
    {
        var name = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(bytes)).ToLowerInvariant()[..20] + ext;
        var path = Path.Combine(ImgDir, name);
        if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
        return "img/" + name;
    }

    /// <summary>"img/x.png" → absolute path; anything else is returned unchanged.</summary>
    public string ResolveImage(string src) =>
        src.StartsWith("img/", StringComparison.OrdinalIgnoreCase) ? Path.Combine(Home, src.Replace('/', Path.DirectorySeparatorChar)) : src;

    // ================= settings =================

    public string? Setting(string key) => (string?)Db.Scalar("SELECT value FROM settings WHERE key=?", key);
    public string Setting(string key, string fallback) => Setting(key) ?? fallback;
    public int SettingInt(string key, int fallback) => int.TryParse(Setting(key), out var v) ? v : fallback;
    public bool SettingBool(string key, bool fallback) => Setting(key) is { } v ? v == "1" : fallback;

    public void SetSetting(string key, string? value)
    {
        if (value == null) Db.Exec("DELETE FROM settings WHERE key=?", key);
        else Db.Exec("INSERT INTO settings(key,value) VALUES(?,?) ON CONFLICT(key) DO UPDATE SET value=excluded.value", key, value);
    }

    public void SetSetting(string key, bool value) => SetSetting(key, value ? "1" : "0");

    public void Dispose() => Db.Dispose();

    static string Marks(int n) => string.Join(",", Enumerable.Repeat("?", n));

    [GeneratedRegex(@"img/([0-9a-f]{20}\.\w+)")]
    private static partial Regex ImgRefRx();
}

/// <summary>The card changed since the caller read it (CLI --if-rev).</summary>
public sealed class RevConflictException(long current) : Exception($"卡片已被修改（当前 rev={current}）")
{
    public long Current { get; } = current;
}
