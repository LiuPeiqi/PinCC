using System.Runtime.InteropServices;
using System.Text;
using PinCC.Core;

namespace PinCC;

/// <summary>One exe, two faces: the text API and the desktop app.</summary>
public static class Program
{
    public static string? SmokeScenario => Environment.GetEnvironmentVariable("PINCC_UI_SMOKE") is { Length: > 0 } value ? value : null;
    public static bool IsDiagnostic => SmokeScenario != null
        || Environment.GetEnvironmentVariable("PINCC_UI_SNAPSHOT") is { Length: > 0 }
        || Environment.GetEnvironmentVariable("PINCC_SNAPSHOT") is { Length: > 0 };
    static string IdentitySuffix => !IsDiagnostic ? "" : "." + Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(Store.DefaultHome).ToUpperInvariant())))[..16];
    static string InstanceName => @"Local\PinCC.Instance" + IdentitySuffix;
    public static string ShowEventName => @"Local\PinCC.Show" + IdentitySuffix;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] != "--startup") return RunCli(args);
        if (SmokeScenario != null && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PINCC_HOME")))
            return 2; // A fault-injection run must have an explicitly isolated data home.
        using var mutex = new Mutex(true, InstanceName, out bool first);
        if (!first)
        {
            try { using var e = EventWaitHandle.OpenExisting(ShowEventName); e.Set(); } catch { }
            return 0;
        }
        try
        {
            var app = new UI.App();
            var result = app.Run();
            GC.KeepAlive(mutex);
            return result;
        }
        catch (Exception error) { UI.App.Log(error, "main"); return 1; }
    }

    static int RunCli(string[] args)
    {
        // A GUI-subsystem exe gets no console. Pipes (agents, WSL, scripts) arrive as real std handles and get UTF-8;
        // an interactive cmd/PowerShell has none, so attach to the parent console and use its code page.
        TextWriter o;
        var h = GetStdHandle(-11);
        if (h != IntPtr.Zero && h != new IntPtr(-1) && GetFileType(h) is 1 or 3) // FILE_TYPE_DISK / FILE_TYPE_PIPE
            o = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        else
        {
            // Interactive console: write UTF-16 through WriteConsoleW, which neither needs nor changes the user's code page.
            if (AttachConsole(-1)) Console.OutputEncoding = Encoding.Unicode;
            o = Console.Out;
            o.WriteLine();
        }
        string ReadStdin()
        {
            using var ms = new MemoryStream();
            Console.OpenStandardInput().CopyTo(ms);
            return Fetch.Text(ms.ToArray());
        }
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        var attached = false;
        if (args[0] == "run")
        {
            try { Console.CancelKeyPress += cancel; attached = true; } catch { }
        }
        try { return Cli.Run(args, o, ReadStdin, cancellation.Token); }
        catch (Exception e)
        {
            // Last resort (e.g. stdin unreadable): a message and exit 1, never an unhandled-exception dialog.
            try { o.WriteLine("错误：" + e.Message); } catch { }
            return Cli.UsageError;
        }
        finally
        {
            if (attached) { try { Console.CancelKeyPress -= cancel; } catch { } }
            try { o.Flush(); } catch { }
        }
    }

    [DllImport("kernel32")] static extern IntPtr GetStdHandle(int n);
    [DllImport("kernel32")] static extern int GetFileType(IntPtr h);
    [DllImport("kernel32")] static extern bool AttachConsole(int pid);
}
