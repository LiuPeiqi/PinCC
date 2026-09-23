using System.Runtime.InteropServices;
using System.Text;
using PinCC.Core;

namespace PinCC;

/// <summary>One exe, two faces: `PinCC.exe &lt;command&gt;` is the text API, `PinCC.exe` is the desktop app.</summary>
public static class Program
{
    const string InstanceName = @"Local\PinCC.Instance";
    public const string ShowEventName = @"Local\PinCC.Show";

    [STAThread]
    public static int Main(string[] args)
    {
        // Any argument is the text API (unknown verbs print usage and never start the GUI) — except the autostart flag.
        if (args.Length > 0 && args[0] != "--startup") return RunCli(args);

        if (Environment.GetEnvironmentVariable("PINCC_UI_SNAPSHOT") is { Length: > 0 })
        {
            new UI.App().Run(); // render-only diagnostic: shows nothing, so it may run beside the real instance
            return 0;
        }
        using var mutex = new Mutex(true, InstanceName, out bool first);
        if (!first)
        {
            // Already running: ask it to bring every card back.
            try { using var e = EventWaitHandle.OpenExisting(ShowEventName); e.Set(); } catch { }
            return 0;
        }
        var app = new UI.App();
        app.Run(); // e.Args carries --startup
        GC.KeepAlive(mutex);
        return 0;
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
        try { return Cli.Run(args, o, ReadStdin); }
        catch (Exception e)
        {
            // Last resort (e.g. stdin unreadable): a message and exit 1, never an unhandled-exception dialog.
            try { o.WriteLine("错误：" + e.Message); } catch { }
            return Cli.UsageError;
        }
        finally { try { o.Flush(); } catch { } }
    }

    [DllImport("kernel32")] static extern IntPtr GetStdHandle(int n);
    [DllImport("kernel32")] static extern int GetFileType(IntPtr h);
    [DllImport("kernel32")] static extern bool AttachConsole(int pid);
}
