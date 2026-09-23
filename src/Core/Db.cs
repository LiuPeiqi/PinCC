using System.Runtime.InteropServices;
using System.Text;

// Every P/Invoke in this app targets a Windows system DLL; never search the app folder or CWD for them.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace PinCC.Core;

/// <summary>
/// Thin wrapper over Windows' built-in winsqlite3.dll (Windows 10+), so the app needs no SQLite package.
/// Rows are plain dictionaries: the same shape is used for mapping, JSON snapshots and restore.
/// </summary>
public sealed class Db : IDisposable
{
    const string Lib = "winsqlite3";
    const int OK = 0, ROW = 100, DONE = 101;
    const int INTEGER = 1, FLOAT = 2, TEXT = 3, BLOB = 4;
    static readonly IntPtr Transient = new(-1);

    readonly object _gate = new();
    IntPtr _h;

    public Db(string path, int busyMs = 5000)
    {
        // open_v2 with a UTF-8 path: sqlite3_open16 would create the database with UTF-16 storage.
        Check(sqlite3_open_v2(Encoding.UTF8.GetBytes(path + "\0"), out _h, 0x2 | 0x4 | 0x10000, IntPtr.Zero), "open"); // READWRITE|CREATE|FULLMUTEX
        sqlite3_busy_timeout(_h, busyMs);
        Script("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
    }

    /// <summary>Runs one statement; returns the number of changed rows.</summary>
    public int Exec(string sql, params object?[] args)
    {
        lock (_gate)
        {
            Run(sql, args, _ => { });
            return sqlite3_changes(_h);
        }
    }

    /// <summary>Runs an INSERT; returns the new rowid.</summary>
    public long Insert(string sql, params object?[] args)
    {
        lock (_gate)
        {
            Run(sql, args, _ => { });
            return sqlite3_last_insert_rowid(_h);
        }
    }

    public List<Dictionary<string, object?>> Query(string sql, params object?[] args)
    {
        var rows = new List<Dictionary<string, object?>>();
        lock (_gate) Run(sql, args, s => rows.Add(ReadRow(s)));
        return rows;
    }

    public object? Scalar(string sql, params object?[] args)
    {
        object? v = null;
        lock (_gate) Run(sql, args, s => v ??= ReadCol(s, 0));
        return v;
    }

    /// <summary>Multi-statement script without parameters (schema, pragmas).</summary>
    public void Script(string sql)
    {
        lock (_gate)
        {
            var rc = sqlite3_exec(_h, Encoding.UTF8.GetBytes(sql + "\0"), IntPtr.Zero, IntPtr.Zero, out var err);
            if (err != IntPtr.Zero) sqlite3_free(err);
            Check(rc, sql);
        }
    }

    /// <summary>BEGIN IMMEDIATE … COMMIT; rolls back on exception. Not re-entrant.</summary>
    public T Tx<T>(Func<T> body)
    {
        lock (_gate)
        {
            Script("BEGIN IMMEDIATE");
            try { var r = body(); Script("COMMIT"); return r; }
            catch { Script("ROLLBACK"); throw; }
        }
    }

    public void Tx(Action body) => Tx(() => { body(); return 0; });

    /// <summary>Changes whenever ANOTHER connection (e.g. the CLI) commits.</summary>
    public long DataVersion => Convert.ToInt64(Scalar("PRAGMA data_version"));

    public void Dispose()
    {
        lock (_gate)
        {
            if (_h != IntPtr.Zero) sqlite3_close_v2(_h);
            _h = IntPtr.Zero;
        }
    }

    void Run(string sql, object?[] args, Action<IntPtr> onRow)
    {
        Check(sqlite3_prepare16_v2(_h, sql, -1, out var st, IntPtr.Zero), sql);
        try
        {
            for (int i = 0; i < args.Length; i++) Bind(st, i + 1, args[i]);
            int rc;
            while ((rc = sqlite3_step(st)) == ROW) onRow(st);
            if (rc != DONE) Check(rc, sql);
        }
        finally { sqlite3_finalize(st); }
    }

    static void Bind(IntPtr st, int i, object? v)
    {
        switch (v)
        {
            case null: sqlite3_bind_null(st, i); break;
            case string s: sqlite3_bind_text16(st, i, s, s.Length * 2, Transient); break;
            case bool b: sqlite3_bind_int64(st, i, b ? 1 : 0); break;
            case int n: sqlite3_bind_int64(st, i, n); break;
            case long n: sqlite3_bind_int64(st, i, n); break;
            case double d: sqlite3_bind_double(st, i, d); break;
            case float f: sqlite3_bind_double(st, i, f); break;
            case byte[] bytes: sqlite3_bind_blob(st, i, bytes, bytes.Length, Transient); break;
            default: throw new ArgumentException($"Unsupported SQL parameter type {v.GetType().Name}");
        }
    }

    static Dictionary<string, object?> ReadRow(IntPtr st)
    {
        int n = sqlite3_column_count(st);
        var row = new Dictionary<string, object?>(n, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < n; i++) row[Marshal.PtrToStringUni(sqlite3_column_name16(st, i))!] = ReadCol(st, i);
        return row;
    }

    static object? ReadCol(IntPtr st, int i) => sqlite3_column_type(st, i) switch
    {
        INTEGER => sqlite3_column_int64(st, i),
        FLOAT => sqlite3_column_double(st, i),
        TEXT => Marshal.PtrToStringUni(sqlite3_column_text16(st, i), sqlite3_column_bytes16(st, i) / 2),
        BLOB => ReadBlob(st, i),
        _ => null,
    };

    static byte[] ReadBlob(IntPtr st, int i)
    {
        var bytes = new byte[sqlite3_column_bytes(st, i)];
        if (bytes.Length > 0) Marshal.Copy(sqlite3_column_blob(st, i), bytes, 0, bytes.Length);
        return bytes;
    }

    void Check(int rc, string what)
    {
        if (rc == OK) return;
        var msg = _h == IntPtr.Zero ? $"sqlite error {rc}" : Marshal.PtrToStringUni(sqlite3_errmsg16(_h));
        throw new InvalidOperationException($"SQLite: {msg} ({rc}) in: {what}");
    }

    // winsqlite3 uses __stdcall on x86; the attribute is ignored on x64/arm64.
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_open_v2(byte[] pathUtf8, out IntPtr db, int flags, IntPtr vfs);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_close_v2(IntPtr db);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_busy_timeout(IntPtr db, int ms);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_exec(IntPtr db, byte[] sqlUtf8, IntPtr cb, IntPtr arg, out IntPtr err);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] static extern void sqlite3_free(IntPtr p);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)] static extern int sqlite3_prepare16_v2(IntPtr db, string sql, int nBytes, out IntPtr stmt, IntPtr tail);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_step(IntPtr st);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_finalize(IntPtr st);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_bind_null(IntPtr st, int i);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_bind_int64(IntPtr st, int i, long v);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_bind_double(IntPtr st, int i, double v);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)] static extern int sqlite3_bind_text16(IntPtr st, int i, string v, int nBytes, IntPtr destructor);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_bind_blob(IntPtr st, int i, byte[] v, int n, IntPtr destructor);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_column_count(IntPtr st);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] static extern IntPtr sqlite3_column_name16(IntPtr st, int i);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_column_type(IntPtr st, int i);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] static extern long sqlite3_column_int64(IntPtr st, int i);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] static extern double sqlite3_column_double(IntPtr st, int i);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] static extern IntPtr sqlite3_column_text16(IntPtr st, int i);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_column_bytes16(IntPtr st, int i);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] static extern IntPtr sqlite3_column_blob(IntPtr st, int i);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_column_bytes(IntPtr st, int i);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] static extern IntPtr sqlite3_errmsg16(IntPtr db);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_changes(IntPtr db);
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)] static extern long sqlite3_last_insert_rowid(IntPtr db);
}
